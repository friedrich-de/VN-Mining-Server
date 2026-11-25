using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using VnMiningServer.Access;
using VnMiningServer.Tests.Support;

namespace VnMiningServer.Tests;

/// <summary>
/// The whole server, started on a free port and called over HTTP as the game and the page call it.
/// </summary>
public sealed class ServerTests : IAsyncLifetime
{
    private const string Key = "the-key-of-the-test-server-0123456789";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vn-mining-server-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;
    private WebApplication? _app;
    private HttpClient _client = new();

    public async ValueTask InitializeAsync()
    {
        _app = ServerHost.Build(new ServerOptions { DataFolder = _folder, Listen = "http://127.0.0.1:0", ApiKey = Key, Quiet = true });
        await _app.StartAsync(_none);
        _client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? key = Key, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    private static MultipartFormDataContent Line(string text, string? name = null, byte[]? audio = null, byte[]? screenshot = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(text, Encoding.UTF8), "text" } };
        if (name is not null)
        {
            form.Add(new StringContent(name, Encoding.UTF8), "name");
        }

        if (audio is not null)
        {
            form.Add(new ByteArrayContent(audio), "audio", "voice.ogg");
        }

        if (screenshot is not null)
        {
            form.Add(new ByteArrayContent(screenshot), "screenshot", "screenshot.jpg");
        }

        return form;
    }

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(_none)).RootElement.Clone();
        }
    }

    private async Task<string> SignInAsync()
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Post, "Api/Session", key: null, JsonContent(new { Key })), _none);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Without_the_key_nothing_of_the_api_answers()
    {
        foreach (var path in new[] { "Api/Status", "Api/Lines", "Api/Events", "Api/Settings", "Api/Dictionaries", "Api/GameSettings", "Api/Lines/1/Screenshot", "Api/Lines/Audio?lines=1", "Api/WordAudio?term=a" })
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Get, path, key: null), _none);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var post = await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", key: null, Line("text")), _none);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        var problem = await JsonAsync(await _client.SendAsync(Request(HttpMethod.Delete, "Api/Lines", key: null), _none));
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Contains("API key", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_opens_the_api_and_the_names_are_written_as_the_page_expects_them()
    {
        var status = await JsonAsync(await _client.SendAsync(Request(HttpMethod.Get, "Api/Status"), _none));

        Assert.Equal(AppServices.Version, status.GetProperty("Version").GetString());
        Assert.Equal(0, status.GetProperty("Lines").GetInt32());
        Assert.Equal(1000, status.GetProperty("MaxLines").GetInt32());
    }

    [Fact]
    public async Task Ten_wrong_keys_lock_their_address_and_no_other()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var wrong = Request(HttpMethod.Get, "Api/Status", key: "wrong-key-" + attempt);
            wrong.Headers.Add("CF-Connecting-IP", "203.0.113.9");
            using var refused = await _client.SendAsync(wrong, _none);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var right = Request(HttpMethod.Get, "Api/Status");
        right.Headers.Add("CF-Connecting-IP", "203.0.113.9");
        using var locked = await _client.SendAsync(right, _none);
        using var signIn = Request(HttpMethod.Post, "Api/Session", key: null, JsonContent(new { Key }));
        signIn.Headers.Add("CF-Connecting-IP", "203.0.113.9");
        using var lockedSignIn = await _client.SendAsync(signIn, _none);
        using var elsewhere = await _client.SendAsync(Request(HttpMethod.Get, "Api/Status"), _none);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.InRange(locked.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedSignIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
    }

    [Fact]
    public async Task The_page_signs_in_once_and_gets_a_cookie_script_cannot_read()
    {
        using var wrong = await _client.SendAsync(Request(HttpMethod.Post, "Api/Session", key: null, JsonContent(new { Key = "not the key" })), _none);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(wrong.Headers.Contains("Set-Cookie"));

        using var response = await _client.SendAsync(Request(HttpMethod.Post, "Api/Session", key: null, JsonContent(new { Key })), _none);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();

        Assert.StartsWith(ApiAccess.CookieName + "=v1.", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, cookie, StringComparison.Ordinal);

        using var withCookie = Request(HttpMethod.Get, "Api/Status", key: null);
        withCookie.Headers.Add("Cookie", cookie.Split(';')[0]);
        using var status = await _client.SendAsync(withCookie, _none);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    [Fact]
    public async Task Behind_https_the_cookie_is_for_https_only()
    {
        using var request = Request(HttpMethod.Post, "Api/Session", key: null, JsonContent(new { Key }));
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await _client.SendAsync(request, _none);

        Assert.Contains("; secure", response.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Another_site_cannot_use_the_cookie_to_change_anything()
    {
        var cookie = await SignInAsync();

        using var foreign = Request(HttpMethod.Delete, "Api/Lines", key: null);
        foreign.Headers.Add("Cookie", cookie);
        foreign.Headers.Add("Origin", "https://elsewhere.example");
        using var refused = await _client.SendAsync(foreign, _none);

        using var own = Request(HttpMethod.Delete, "Api/Lines", key: null);
        own.Headers.Add("Cookie", cookie);
        own.Headers.Add("Origin", _client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var done = await _client.SendAsync(own, _none);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
    }

    [Fact]
    public async Task Signing_out_takes_the_cookie_away()
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Delete, "Api/Session", key: null), _none);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(ApiAccess.CookieName + "=;", cookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_game_sends_lines_and_the_page_reads_them()
    {
        var first = await JsonAsync(await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: Line("「おはよう、タカシくん」", "明日香", Sample.Ogg, Sample.Jpeg)), _none));
        using var narration = await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: new FormUrlEncodedContent([new("text", "空は青かった。")])), _none);
        using var empty = await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: Line(" ")), _none);
        using var notSound = await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: Line("text", audio: Sample.Jpeg)), _none);

        Assert.Equal(1, first.GetProperty("Id").GetInt64());
        Assert.True(first.GetProperty("HasAudio").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, narration.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notSound.StatusCode);

        var lines = await JsonAsync(await _client.SendAsync(Request(HttpMethod.Get, "Api/Lines"), _none));
        Assert.Equal(2, lines.GetProperty("Total").GetInt32());
        Assert.Equal(["「おはよう、タカシくん」", "空は青かった。"], lines.GetProperty("Lines").EnumerateArray().Select(line => line.GetProperty("Text").GetString()));
        Assert.Equal("明日香", lines.GetProperty("Lines")[0].GetProperty("Name").GetString());
        Assert.False(lines.GetProperty("Lines")[1].TryGetProperty("Name", out _));

        using var screenshot = await _client.SendAsync(Request(HttpMethod.Get, "Api/Lines/1/Screenshot"), _none);
        Assert.Equal("image/jpeg", screenshot.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Sample.Jpeg, await screenshot.Content.ReadAsByteArrayAsync(_none));
        using var none = await _client.SendAsync(Request(HttpMethod.Get, "Api/Lines/2/Screenshot"), _none);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task A_line_too_large_is_turned_away()
    {
        // The game's HTTP library asks before it sends something large, so the refusal comes before the upload.
        using var request = Request(HttpMethod.Post, "Api/Lines", content: Line("text", audio: [.. Sample.Ogg, .. new byte[25 * 1024 * 1024]]));
        request.Headers.ExpectContinue = true;

        using var response = await _client.SendAsync(request, _none);
        var status = await JsonAsync(await _client.SendAsync(Request(HttpMethod.Get, "Api/Status"), _none));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, status.GetProperty("Lines").GetInt32());
    }

    [Fact]
    public async Task The_page_follows_the_stream_as_the_game_sends()
    {
        using (await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: Line("before")), _none))
        {
        }

        using var response = await _client.SendAsync(Request(HttpMethod.Get, "Api/Events?after=0"), HttpCompletionOption.ResponseHeadersRead, _none);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(_none), Encoding.UTF8);

        Assert.Equal("event: hello", await reader.ReadLineAsync(_none));
        Assert.Equal("""data: {"Oldest":1,"Total":1,"MaxLines":1000}""", await reader.ReadLineAsync(_none));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(_none));
        Assert.Equal("event: line", await reader.ReadLineAsync(_none));
        Assert.Contains("\"Text\":\"before\"", await reader.ReadLineAsync(_none), StringComparison.Ordinal);
        Assert.Equal(string.Empty, await reader.ReadLineAsync(_none));

        using (await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: Line("「いま来たよ」", "明日香")), _none))
        {
        }

        Assert.Equal("event: line", await reader.ReadLineAsync(_none));
        var line = JsonDocument.Parse((await reader.ReadLineAsync(_none))![6..]).RootElement;
        Assert.Equal((2, "「いま来たよ」", "明日香"), (line.GetProperty("Id").GetInt64(), line.GetProperty("Text").GetString(), line.GetProperty("Name").GetString()));
    }

    [Fact]
    public async Task The_server_stops_while_a_page_still_follows_the_stream()
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, "Api/Events"), HttpCompletionOption.ResponseHeadersRead, _none);
        var watch = Stopwatch.StartNew();

        await _app!.StopAsync(_none);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The open stream held the server up.");
    }

    [Fact]
    public async Task Real_voices_and_a_real_screenshot_become_what_goes_on_a_card()
    {
        var work = Path.Combine(_folder, "made");
        Directory.CreateDirectory(work);
        Assert.SkipUnless(
            await FfmpegAsync("-f", "lavfi", "-i", "sine=frequency=440:duration=1.2", "-c:a", "libvorbis", Path.Combine(work, "a.ogg"))
            && await FfmpegAsync("-f", "lavfi", "-i", "sine=frequency=660:duration=0.8", "-ac", "1", "-ar", "22050", "-c:a", "libvorbis", Path.Combine(work, "b.ogg"))
            && await FfmpegAsync("-f", "lavfi", "-i", "testsrc=size=800x600:rate=1", "-frames:v", "1", Path.Combine(work, "shot.jpg")),
            "ffmpeg with Vorbis is not installed here.");
        var shot = await File.ReadAllBytesAsync(Path.Combine(work, "shot.jpg"), _none);
        using (await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: Line("one", "A", await File.ReadAllBytesAsync(Path.Combine(work, "a.ogg"), _none), shot)), _none))
        using (await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: Line("two", "B", await File.ReadAllBytesAsync(Path.Combine(work, "b.ogg"), _none), shot)), _none))
        {
        }

        using var mp3 = await _client.SendAsync(Request(HttpMethod.Get, "Api/Lines/Audio?lines=1,2&format=mp3&bitrate=64"), _none);
        using var opus = await _client.SendAsync(Request(HttpMethod.Get, "Api/Lines/Audio?lines=2&format=opus&normalize=true"), _none);
        using var webp = await _client.SendAsync(Request(HttpMethod.Get, "Api/Lines/1/Picture?format=webp&width=400&quality=70"), _none);
        using var jpeg = await _client.SendAsync(Request(HttpMethod.Get, "Api/Lines/2/Picture"), _none);

        Assert.Equal((HttpStatusCode.OK, "audio/mpeg"), (mp3.StatusCode, mp3.Content.Headers.ContentType!.MediaType));
        var sound = Path.Combine(work, "card.mp3");
        await File.WriteAllBytesAsync(sound, await mp3.Content.ReadAsByteArrayAsync(_none), _none);

        // 1.2 s, a pause of a quarter second, 0.8 s.
        Assert.InRange(await SecondsAsync(sound), 2.2, 2.4);
        Assert.Equal((HttpStatusCode.OK, "audio/ogg"), (opus.StatusCode, opus.Content.Headers.ContentType!.MediaType));
        Assert.StartsWith("OggS", Encoding.Latin1.GetString(await opus.Content.ReadAsByteArrayAsync(_none)), StringComparison.Ordinal);
        Assert.Equal("image/webp", webp.Content.Headers.ContentType!.MediaType);
        var picture = await webp.Content.ReadAsByteArrayAsync(_none);
        Assert.Equal("WEBP", Encoding.Latin1.GetString(picture, 8, 4));
        Assert.Equal("image/jpeg", jpeg.Content.Headers.ContentType!.MediaType);
        Assert.Equal([0xFF, 0xD8, 0xFF], (await jpeg.Content.ReadAsByteArrayAsync(_none))[..3]);
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "scratch", "media")));
    }

    [Fact]
    public async Task The_games_settings_file_names_the_address_the_page_was_opened_at_and_the_key()
    {
        using var direct = await _client.SendAsync(Request(HttpMethod.Get, "Api/GameSettings"), _none);
        using var request = Request(HttpMethod.Get, "Api/GameSettings");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Host = "vn.example.org";
        using var tunnelled = await _client.SendAsync(request, _none);

        var text = await direct.Content.ReadAsStringAsync(_none);
        Assert.Equal("mining.txt", direct.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("no-store", direct.Headers.CacheControl!.ToString());
        Assert.Contains("\nserver = " + _client.BaseAddress!.GetLeftPart(UriPartial.Authority) + "\n", text, StringComparison.Ordinal);
        Assert.Contains("\nkey = " + Key + "\n", text, StringComparison.Ordinal);
        Assert.Contains("\nserver = https://vn.example.org\n", await tunnelled.Content.ReadAsStringAsync(_none), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_are_kept_and_the_stream_follows_them()
    {
        var change = JsonContent(new { anki = new { where = "server", serverAddress = "https://anki.example.org", serverKey = "anki-secret" }, stream = new { maxLines = 50 } });
        var saved = await JsonAsync(await _client.SendAsync(Request(HttpMethod.Put, "Api/Settings", content: change), _none));
        for (var number = 1; number <= 52; number++)
        {
            using (await _client.SendAsync(Request(HttpMethod.Post, "Api/Lines", content: Line("line " + number)), _none))
            {
            }
        }

        var status = await JsonAsync(await _client.SendAsync(Request(HttpMethod.Get, "Api/Status"), _none));

        Assert.Equal("server", saved.GetProperty("Anki").GetProperty("Where").GetString());
        Assert.True(saved.GetProperty("Anki").GetProperty("ServerKeySet").GetBoolean());
        Assert.Equal(50, saved.GetProperty("Stream").GetProperty("MaxLines").GetInt32());
        Assert.DoesNotContain("anki-secret", saved.GetRawText(), StringComparison.Ordinal);
        Assert.Equal((50, 50), (status.GetProperty("Lines").GetInt32(), status.GetProperty("MaxLines").GetInt32()));
        Assert.DoesNotContain("anki-secret", string.Concat(Directory.GetFiles(_folder, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_server_does_not_have_is_not_found_and_says_nothing_about_itself()
    {
        using var unknown = await _client.SendAsync(Request(HttpMethod.Get, "Api/Nothing"), _none);
        using var outside = await _client.SendAsync(Request(HttpMethod.Get, "..%2Fdata%2Fapi-key.txt", key: null), _none);
        using var notANumber = await _client.SendAsync(Request(HttpMethod.Get, "Api/Lines/abc/Picture"), _none);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notANumber.StatusCode);
        Assert.False(unknown.Headers.Contains("Server"));
    }

    private static async Task<bool> FfmpegAsync(params string[] arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", ["-nostdin", "-loglevel", "error", "-y", .. arguments]) { RedirectStandardError = true })!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<double> SecondsAsync(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]) { RedirectStandardOutput = true })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return double.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
