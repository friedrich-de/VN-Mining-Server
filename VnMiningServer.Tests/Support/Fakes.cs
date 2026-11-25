using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VnMiningServer.Access;
using VnMiningServer.Anki;
using VnMiningServer.Api;
using VnMiningServer.Dictionary;
using VnMiningServer.Lines;
using VnMiningServer.Media;
using VnMiningServer.Settings;

namespace VnMiningServer.Tests.Support;

internal sealed class FakeCallers : ICallerResolver
{
    public Caller? Caller { get; set; }

    public Task<Caller?> ResolveAsync(HttpContext context) => Task.FromResult(Caller);
}

/// <summary>Page files held in memory, by name.</summary>
internal sealed class FakePageFileSource : IPageFileSource
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public FakePageFileSource(params string[] names)
    {
        foreach (var name in names)
        {
            _files[name] = "content of " + name;
        }
    }

    public IReadOnlyCollection<string> Names => _files.Keys;

    public Stream? Open(string name) => _files.TryGetValue(name, out var content) ? new MemoryStream(Encoding.UTF8.GetBytes(content)) : null;
}

/// <summary>A clock that moves when a test says so.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 20, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;

    public void AdvanceMs(long milliseconds) => Now += TimeSpan.FromMilliseconds(milliseconds);
}

/// <summary>An ffmpeg that does what a test tells it and remembers how it was called.</summary>
internal sealed class FakeFfmpeg : IFfmpeg
{
    private readonly object _lock = new();

    /// <summary>Gets the calls so far, oldest first.</summary>
    public List<IReadOnlyList<string>> Runs { get; } = [];

    /// <summary>Gets or sets what a run does. By default it writes a few bytes into the file named last.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<bool>> OnRun { get; set; } = (arguments, _) =>
    {
        File.WriteAllBytes(arguments[^1], [1, 2, 3]);
        return Task.FromResult(true);
    };

    /// <summary>Gets or sets what ffmpeg says about itself.</summary>
    public string? Encoders { get; set; } = " V....D libsvtav1            SVT-AV1 \n V....D libwebp              WebP \n";

    public int Asked { get; private set; }

    public async Task<bool> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Runs.Add(arguments);
        }

        return await OnRun(arguments, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> AskAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Asked++;
        return Task.FromResult(Encoders);
    }
}

/// <summary>The server's settings, held in memory.</summary>
internal sealed class FakeSharedSettings : ISharedSettings
{
    public string WordAudioAddress { get; set; } = string.Empty;

    public int MaxLines { get; set; } = LineStore.DefaultMaxLines;

    public void SetWordAudioAddress(string stored) => WordAudioAddress = stored;

    public void SetMaxLines(int lines) => MaxLines = LineStore.ClampMaxLines(lines);
}

/// <summary>A request an AnkiConnect got: where it went and its body.</summary>
internal sealed record AnkiRequest(Uri Address, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string Action => Json.GetProperty("action").GetString()!;
}

/// <summary>
/// The network as an AnkiConnect sees it: every request is remembered, and the test says what is answered. By
/// default it answers as AnkiConnect does, with a null result.
/// </summary>
internal sealed class FakeAnkiHandler : HttpMessageHandler
{
    private readonly object _lock = new();

    public List<AnkiRequest> Requests { get; } = [];

    /// <summary>Gets or sets what is answered to a request.</summary>
    public Func<AnkiRequest, CancellationToken, Task<HttpResponseMessage>> OnRequest { get; set; } = (_, _) => Task.FromResult(Answer("""{"result":null,"error":null}"""));

    public static HttpResponseMessage Answer(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Answers every request with a result, as AnkiConnect would.</summary>
    public void AnswerWith(Func<AnkiRequest, string> result) =>
        OnRequest = (request, _) => Task.FromResult(Answer("{\"result\":" + result(request) + ",\"error\":null}"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var seen = new AnkiRequest(request.RequestUri!, body);
        lock (_lock)
        {
            Requests.Add(seen);
        }

        return await OnRequest(seen, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Answers downloads with what a test gave for each address, or 404.</summary>
internal sealed class FakeDownloadHandler : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public List<Uri> Asked { get; } = [];

    /// <summary>Gets the addresses that cannot be reached, as a server that is not there.</summary>
    public HashSet<string> Unreachable { get; } = [];

    /// <summary>Gets the media type each address is answered with; none where it is not named.</summary>
    public Dictionary<string, string> Types { get; } = [];

    /// <summary>Gets the addresses that are answered with an error, and which.</summary>
    public Dictionary<string, System.Net.HttpStatusCode> Errors { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Asked.Add(request.RequestUri!);
        if (Unreachable.Contains(request.RequestUri!.AbsoluteUri))
        {
            throw new HttpRequestException("Connection refused (dictionaries.example:443)");
        }

        var address = request.RequestUri!.AbsoluteUri;
        if (Errors.TryGetValue(address, out var error))
        {
            return Task.FromResult(new HttpResponseMessage(error) { Content = new StringContent("{\"message\":\"no\"}") });
        }

        if (!Files.TryGetValue(address, out var data))
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });
        }

        var content = new ByteArrayContent(data);
        if (Types.TryGetValue(address, out var type))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}

/// <summary>The server's services over fakes, with a scratch folder that goes away with the test.</summary>
internal sealed class TestServices : IDisposable
{
    /// <summary>The API key of the test server.</summary>
    public const string Key = "test-key-0123456789abcdef";

    public TestServices()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "vn-mining-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Scratch);
        Access = new ApiAccess(Key, Time);
        Limiter = new FailureLimiter(Time);
        Lines = new LineStore(Path.Combine(Scratch, "stream"), Time, () => Shared.MaxLines, NullLogger<LineStore>.Instance);
        Media = new LineMedia(Ffmpeg, Path.Combine(Scratch, "work"), NullLogger<LineMedia>.Instance);
        Keys = new KeyProtector(RandomNumberGenerator.GetBytes(32));
        Settings = new UserSettingsStore(Path.Combine(Scratch, "users"), Keys, NullLogger<UserSettingsStore>.Instance);
        AnkiClient = new AnkiClient(AnkiNetwork, NullLogger<AnkiClient>.Instance);
        Anki = new AnkiGate(Settings);
        Dictionaries = new DictionaryLibrary(Path.Combine(Scratch, "dictionaries"), NullLogger<DictionaryLibrary>.Instance);
        DictionaryDownloads = new DictionaryDownloader(DownloadNetwork);
        WordAudio = new WordAudioSource(AudioNetwork, Shared, Keys, NullLogger<WordAudioSource>.Instance);
    }

    /// <summary>Gets the id the owner's settings are kept under.</summary>
    public static Guid UserId => OwnerResolver.OwnerId;

    public string Scratch { get; }

    public FakeCallers Callers { get; } = new() { Caller = OwnerResolver.Owner };

    public ManualTime Time { get; } = new();

    public FakeFfmpeg Ffmpeg { get; } = new();

    public ApiAccess Access { get; }

    public FailureLimiter Limiter { get; }

    public LineStore Lines { get; }

    public LineMedia Media { get; }

    public KeyProtector Keys { get; }

    public UserSettingsStore Settings { get; }

    public FakeSharedSettings Shared { get; } = new();

    public FakeAnkiHandler AnkiNetwork { get; } = new();

    public AnkiClient AnkiClient { get; }

    public AnkiGate Anki { get; }

    public DictionaryLibrary Dictionaries { get; }

    /// <summary>Gets what answers the downloads of dictionaries.</summary>
    public FakeDownloadHandler DownloadNetwork { get; } = new();

    public DictionaryDownloader DictionaryDownloads { get; }

    /// <summary>Gets what answers the audio source for words.</summary>
    public FakeDownloadHandler AudioNetwork { get; } = new();

    public WordAudioSource WordAudio { get; }

    /// <summary>Makes the request come without the key, as far as the controllers can tell.</summary>
    public void SignOut() => Callers.Caller = null;

    /// <summary>Signs the test in as the owner, or as someone who may not change the server's dictionaries and sources.</summary>
    public void SignInAs(bool administrator) => Callers.Caller = OwnerResolver.Owner with { IsAdministrator = administrator };

    /// <summary>Makes the cards go to an AnkiConnect on a server.</summary>
    public void UseServerAnki(string address = "https://anki.example.org/", string key = "server-key")
    {
        var settings = Settings.Load(UserId);
        settings.Where = AnkiWhere.Server;
        settings.ServerAddress = address;
        settings.ServerKey = Keys.Protect(key);
        Settings.Save(UserId, settings);
    }

    public AppServices Build(params string[] pageFiles) => new(
        new PageFiles(new FakePageFileSource(pageFiles)),
        Callers,
        Access,
        Limiter,
        Lines,
        Media,
        Settings,
        Shared,
        AnkiClient,
        Anki,
        new DictionaryServices(Dictionaries, DictionaryDownloads, Path.Combine(Scratch, "dictionary-uploads"), WordAudio),
        Time);

    /// <summary>Makes a file in the scratch folder and returns its path.</summary>
    public string WriteFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(Scratch, name);
        File.WriteAllBytes(path, (encoding ?? Encoding.UTF8).GetBytes(content));
        return path;
    }

    public void Dispose()
    {
        Lines.Dispose();
        Media.Dispose();
        AnkiClient.Dispose();
        Dictionaries.Dispose();
        DictionaryDownloads.Dispose();
        WordAudio.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Scratch, recursive: true);
    }
}

/// <summary>Small files of the kinds a game sends.</summary>
internal static class Sample
{
    /// <summary>Gets the start of an Ogg file.</summary>
    public static byte[] Ogg => [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4];

    /// <summary>Gets the start of a JPEG file.</summary>
    public static byte[] Jpeg => [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. "JFIF"u8, 0, 1, 1];

    /// <summary>Gets the start of a PNG file.</summary>
    public static byte[] Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
}
