using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VnMiningServer.Lines;
using VnMiningServer.Media;

namespace VnMiningServer.Api;

/// <summary>
/// The stream of the game's lines: the game adds to it, the page reads it and follows it, and a line's
/// screenshot and voice are made into what goes on a card, for the page or straight into the Anki on a server.
/// </summary>
[ApiController]
[Route("Api/Lines")]
[Authorize]
[CallerGone]
public class LinesController : ControllerBase
{
    /// <summary>The largest request the game may send for one line.</summary>
    internal const long MaxRequestBytes = 24 * 1024 * 1024;

    /// <summary>The largest voice file or screenshot of a line.</summary>
    internal const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>How many lines the page gets when it asks for no number.</summary>
    internal const int DefaultLimit = 300;

    /// <summary>How long the stream stays quiet before it sends a sign of life.</summary>
    internal static readonly TimeSpan Beat = TimeSpan.FromSeconds(15);

    private const string Gone = "This line is no longer in the stream.";

    // Japanese goes out as it is, not as escapes four times its size. Line breaks are escaped either way,
    // so an event's data stays on one line.
    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AppServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinesController"/> class.
    /// </summary>
    /// <param name="services">The server's services.</param>
    public LinesController(AppServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Gets how long this stream stays quiet before it sends a sign of life. Tests shorten it.
    /// </summary>
    internal TimeSpan BeatEvery { get; init; } = Beat;

    /// <summary>
    /// Takes a line from the game: its text, and with it who speaks, the voice file and a screenshot.
    /// The same line sent twice in a row is kept once.
    /// </summary>
    /// <param name="form">The line.</param>
    /// <param name="cancellationToken">Ends with the request.</param>
    /// <returns>The line as it is kept.</returns>
    [HttpPost]
    [Consumes("multipart/form-data", "application/x-www-form-urlencoded")]
    [Produces(MediaTypeNames.Application.Json)]
    [MaxBody(MaxRequestBytes)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<LineView>> PostLine([FromForm] LineForm form, CancellationToken cancellationToken)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(StatusController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(form.Text))
        {
            return Problem("A line needs text.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (form.Audio?.Length > MaxFileBytes || form.Screenshot?.Length > MaxFileBytes)
        {
            return Problem("A line's voice and screenshot may each have 10 MB at most.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var audio = await ReadAsync(form.Audio, cancellationToken).ConfigureAwait(false);
        var picture = await ReadAsync(form.Screenshot, cancellationToken).ConfigureAwait(false);
        if (audio is not null && MediaKind.Audio(audio) is null)
        {
            return Problem("The voice is of no kind the server knows (Ogg, MP3, WAV, FLAC, M4A).", statusCode: StatusCodes.Status400BadRequest);
        }

        if (picture is not null && MediaKind.Picture(picture) is null)
        {
            return Problem("The screenshot is of no kind the server knows (JPEG, PNG, WebP).", statusCode: StatusCodes.Status400BadRequest);
        }

        var line = _services.Lines.Add(new NewLine(form.Text, form.Name, form.Voice, form.Game), audio, picture);
        Response.Headers.CacheControl = "no-store";
        return LineView.Of(line);
    }

    /// <summary>
    /// Reads a part of the stream: the newest lines, the lines after a number, or the lines before one.
    /// </summary>
    /// <param name="after">Only lines after this number.</param>
    /// <param name="before">Only lines before this number. Not together with <paramref name="after"/>.</param>
    /// <param name="limit">The most lines to return.</param>
    /// <returns>The lines, oldest first.</returns>
    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<LinesView>> GetLines([FromQuery] long? after = null, [FromQuery] long? before = null, [FromQuery] int limit = DefaultLimit)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(StatusController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        var lines = after is { } from
            ? _services.Lines.After(from, limit)
            : before is { } until ? _services.Lines.Before(until, limit) : _services.Lines.Latest(limit);
        Response.Headers.CacheControl = "no-store";
        return new LinesView
        {
            Lines = lines.Select(LineView.Of).ToList(),
            Total = _services.Lines.Count(),
            MaxLines = _services.Shared.MaxLines,
        };
    }

    /// <summary>
    /// Empties the stream: every line goes, with its voice and screenshot.
    /// </summary>
    /// <returns>Nothing.</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> DeleteLines()
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(StatusController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        _services.Lines.Clear();
        return NoContent();
    }

    /// <summary>
    /// Keeps the connection open and sends each line as it comes. The answer is a stream of server-sent
    /// events: <c>hello</c> first, with the oldest number the stream still holds, then <c>line</c> for
    /// every line after the number asked for and every new one, <c>removed</c> when the oldest lines go,
    /// and <c>cleared</c> when all go.
    /// </summary>
    /// <param name="after">The newest number the page has. The lines after it are sent first.</param>
    /// <param name="cancellationToken">Ends with the connection.</param>
    /// <returns>Nothing: the answer is written as it happens.</returns>
    [HttpGet("/Api/Events")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task GetEvents([FromQuery] long? after, CancellationToken cancellationToken)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Listening starts before the lines the page lacks are read, so none falls between the two.
        // A line may then come twice, and the page keeps one line for a number.
        using var listener = _services.Lines.Listen();
        if (_services.Lines.ListenerCount > LineStore.MaxListeners)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        // The stream also ends when the server goes down, or the server would wait for it.
        var stopping = HttpContext.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-store";

        // Tells a reverse proxy that would collect the answer first to pass it on as it comes.
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            var hello = new HelloView { Oldest = _services.Lines.Oldest(), Total = _services.Lines.Count(), MaxLines = _services.Shared.MaxLines };
            await SendAsync("hello", hello, ended.Token).ConfigureAwait(false);
            if (after is { } from)
            {
                while (true)
                {
                    var missed = _services.Lines.After(from, 500);
                    foreach (var line in missed)
                    {
                        await SendAsync("line", LineView.Of(line), ended.Token).ConfigureAwait(false);
                    }

                    if (missed.Count < 500)
                    {
                        break;
                    }

                    from = missed[^1].Id;
                }
            }

            while (!ended.IsCancellationRequested)
            {
                StreamEvent news;
                using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(ended.Token))
                {
                    quiet.CancelAfter(BeatEvery);
                    try
                    {
                        news = await listener.News.ReadAsync(quiet.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ended.IsCancellationRequested)
                    {
                        // Nothing happened for a while. A comment keeps the connection alive and shows the page it is.
                        await WriteAsync(": beat\n\n", ended.Token).ConfigureAwait(false);
                        continue;
                    }
                }

                switch (news.Kind)
                {
                    case StreamEventKind.Added:
                        await SendAsync("line", LineView.Of(news.Line!), ended.Token).ConfigureAwait(false);
                        break;
                    case StreamEventKind.Removed:
                        await SendAsync("removed", new RemovedView { UpTo = news.UpTo }, ended.Token).ConfigureAwait(false);
                        break;
                    default:
                        await SendAsync("cleared", new { }, ended.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page closed the connection, or the server is going down.
        }
        catch (ChannelClosedException)
        {
            // The page read too slowly and was cut off. It connects again and asks for what it missed.
        }
    }

    /// <summary>
    /// Serves a line's screenshot as the game sent it, for the page to show.
    /// </summary>
    /// <param name="id">The line.</param>
    /// <returns>The screenshot.</returns>
    [HttpGet("{id:long}/Screenshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetScreenshot([FromRoute] long id)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(StatusController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        var line = _services.Lines.Find(id);
        var path = line is null ? null : _services.Lines.PicturePath(line);
        if (path is null || !System.IO.File.Exists(path))
        {
            return Problem(line is null ? Gone : "This line has no screenshot.", statusCode: StatusCodes.Status404NotFound);
        }

        // A line's screenshot never changes, and no later line gets its number.
        Response.Headers.CacheControl = "private, max-age=604800, immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        return PhysicalFile(path, MediaKind.ContentType(line!.PictureExtension!));
    }

    /// <summary>
    /// Makes a card's picture from a line's screenshot.
    /// </summary>
    /// <param name="id">The line.</param>
    /// <param name="format"><c>jpeg</c>, <c>webp</c> or <c>avif</c>.</param>
    /// <param name="width">The widest the picture may be, in pixels.</param>
    /// <param name="quality">From 1, smallest file, to 100, best picture.</param>
    /// <param name="cancellationToken">Ends with the request, and stops the making.</param>
    /// <returns>The picture.</returns>
    [HttpGet("{id:long}/Picture")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> GetPicture(
        [FromRoute] long id,
        [FromQuery] string format = "jpeg",
        [FromQuery] int width = 960,
        [FromQuery] int quality = 85,
        CancellationToken cancellationToken = default)
    {
        var (cut, refusal) = await MakePictureAsync(id, new PictureRequest(format, width, quality), anki: false, cancellationToken).ConfigureAwait(false);
        return cut is null ? refusal! : Answer(cut);
    }

    /// <summary>
    /// Makes a picture as <see cref="GetPicture"/> does and puts it into the Anki on a server, for a card.
    /// </summary>
    /// <param name="id">The line.</param>
    /// <param name="format"><c>jpeg</c>, <c>webp</c> or <c>avif</c>.</param>
    /// <param name="width">The widest the picture may be, in pixels.</param>
    /// <param name="quality">From 1, smallest file, to 100, best picture.</param>
    /// <param name="cancellationToken">Ends with the request, and stops the making.</param>
    /// <returns>The name Anki stored the picture under.</returns>
    [HttpPost("{id:long}/Picture/Anki")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StoredMediaView>> PostPictureToAnki(
        [FromRoute] long id,
        [FromQuery] string format = "jpeg",
        [FromQuery] int width = 960,
        [FromQuery] int quality = 85,
        CancellationToken cancellationToken = default)
    {
        var (cut, refusal) = await MakePictureAsync(id, new PictureRequest(format, width, quality), anki: true, cancellationToken).ConfigureAwait(false);
        return cut is null ? refusal! : await StoreAsync(cut, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a card's sound from the voices of one or several lines, in the order of the stream, with a
    /// short pause between two. Lines without a voice are left out.
    /// </summary>
    /// <param name="lines">The lines' numbers, separated by commas.</param>
    /// <param name="format"><c>mp3</c> or <c>opus</c>.</param>
    /// <param name="bitrate">Kilobits per second.</param>
    /// <param name="normalize">Whether to bring the loudness to a common level.</param>
    /// <param name="cancellationToken">Ends with the request, and stops the making.</param>
    /// <returns>The sound.</returns>
    [HttpGet("Audio")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> GetAudio(
        [FromQuery, Required] string lines,
        [FromQuery] string format = "mp3",
        [FromQuery] int bitrate = 96,
        [FromQuery] bool normalize = false,
        CancellationToken cancellationToken = default)
    {
        var (cut, _, refusal) = await MakeAudioAsync(lines, new AudioRequest(format, bitrate, normalize), anki: false, cancellationToken).ConfigureAwait(false);
        return cut is null ? refusal! : Answer(cut);
    }

    /// <summary>
    /// Makes a sound as <see cref="GetAudio"/> does and puts it into the Anki on a server, for a card.
    /// </summary>
    /// <param name="lines">The lines' numbers, separated by commas.</param>
    /// <param name="format"><c>mp3</c> or <c>opus</c>.</param>
    /// <param name="bitrate">Kilobits per second.</param>
    /// <param name="normalize">Whether to bring the loudness to a common level.</param>
    /// <param name="cancellationToken">Ends with the request, and stops the making.</param>
    /// <returns>The name Anki stored the sound under.</returns>
    [HttpPost("Audio/Anki")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StoredMediaView>> PostAudioToAnki(
        [FromQuery, Required] string lines,
        [FromQuery] string format = "mp3",
        [FromQuery] int bitrate = 96,
        [FromQuery] bool normalize = false,
        CancellationToken cancellationToken = default)
    {
        var (cut, first, refusal) = await MakeAudioAsync(lines, new AudioRequest(format, bitrate, normalize), anki: true, cancellationToken).ConfigureAwait(false);
        return cut is null ? refusal! : await StoreAsync(cut, first, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The name a file goes into Anki under: the line it is from and a mark of its content, so the same
    /// file gets the same name and another one another, also from a server whose numbers started anew.
    /// </summary>
    internal static string MediaName(long line, byte[] content) =>
        string.Create(CultureInfo.InvariantCulture, $"vn-miner_{line}_{Convert.ToHexStringLower(SHA256.HashData(content), 0, 5)}");

    /// <summary>
    /// Reads the numbers of a list such as <c>12,13,15</c>: each once, in rising order.
    /// </summary>
    internal static List<long>? ReadNumbers(string? text)
    {
        var numbers = new SortedSet<long>();
        foreach (var part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers.Count == 0 ? null : [.. numbers];
    }

    private static async Task<byte[]?> ReadAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return null;
        }

        var content = new byte[file.Length];
        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
        {
            await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        }

        return content;
    }

    private async Task<(CutResult? Cut, ActionResult? Refusal)> MakePictureAsync(long id, PictureRequest request, bool anki, CancellationToken cancellationToken)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return (null, Problem(StatusController.NoUser, statusCode: StatusCodes.Status403Forbidden));
        }

        var line = _services.Lines.Find(id);
        var path = line is null ? null : _services.Lines.PicturePath(line);
        if (path is null || !System.IO.File.Exists(path))
        {
            return (null, Problem(line is null ? Gone : "This line has no screenshot.", statusCode: StatusCodes.Status404NotFound));
        }

        // Whether the file can go anywhere is settled before anything is made.
        if (anki && !_services.Anki.TryFind(caller, out _, out var status, out var message))
        {
            return (null, Problem(message, statusCode: status));
        }

        return Refused(await _services.Media.PictureAsync(path, request, cancellationToken).ConfigureAwait(false));
    }

    private async Task<(CutResult? Cut, long First, ActionResult? Refusal)> MakeAudioAsync(string lines, AudioRequest request, bool anki, CancellationToken cancellationToken)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return (null, 0, Problem(StatusController.NoUser, statusCode: StatusCodes.Status403Forbidden));
        }

        var numbers = ReadNumbers(lines);
        if (numbers is null)
        {
            return (null, 0, Problem("Name the lines by their numbers, separated by commas.", statusCode: StatusCodes.Status400BadRequest));
        }

        if (numbers.Count > LineMedia.MaxAudioLines)
        {
            return (null, 0, Problem(string.Create(CultureInfo.InvariantCulture, $"One sound holds the voices of at most {LineMedia.MaxAudioLines} lines."), statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        var voices = new List<string>();
        long first = 0;
        foreach (var number in numbers)
        {
            var line = _services.Lines.Find(number);
            if (line is null)
            {
                return (null, 0, Problem(numbers.Count == 1 ? Gone : "Some of these lines are no longer in the stream.", statusCode: StatusCodes.Status404NotFound));
            }

            var path = _services.Lines.AudioPath(line);
            if (path is not null && System.IO.File.Exists(path))
            {
                voices.Add(path);
                first = first == 0 ? number : first;
            }
        }

        if (voices.Count == 0)
        {
            return (null, 0, Problem(numbers.Count == 1 ? "This line has no voice." : "None of these lines has a voice.", statusCode: StatusCodes.Status404NotFound));
        }

        if (anki && !_services.Anki.TryFind(caller, out _, out var status, out var message))
        {
            return (null, 0, Problem(message, statusCode: status));
        }

        var (cut, refusal) = Refused(await _services.Media.AudioAsync(voices, request, cancellationToken).ConfigureAwait(false));
        return (cut, first, refusal);
    }

    /// <summary>
    /// Keeps a file that is done, and turns any other outcome into its answer.
    /// </summary>
    private (CutResult? Cut, ActionResult? Refusal) Refused(CutResult result) =>
        result.Status == CutStatus.Done ? (result, null) : (null, Answer(result));

    private async Task<ActionResult<StoredMediaView>> StoreAsync(CutResult cut, long line, CancellationToken cancellationToken)
    {
        var caller = (await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false))!;
        byte[] content;
        try
        {
            content = await System.IO.File.ReadAllBytesAsync(cut.Path!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.IO.File.Delete(cut.Path!);
        }

        return await AnkiMedia.StoreAsync(this, _services, caller, MediaName(line, content) + "." + cut.Extension, content, cancellationToken).ConfigureAwait(false);
    }

    private ActionResult Answer(CutResult result)
    {
        switch (result.Status)
        {
            case CutStatus.Done:
                Response.Headers.CacheControl = "no-store";
                Response.Headers.XContentTypeOptions = "nosniff";

                // The file is removed as soon as it is sent.
                var stream = new FileStream(result.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                return File(stream, result.ContentType!);
            case CutStatus.Busy:
                Response.Headers.RetryAfter = "2";
                return Problem(result.Message, statusCode: StatusCodes.Status429TooManyRequests);
            case CutStatus.Refused:
                return Problem(result.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            default:
                return Problem(result.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private Task SendAsync<T>(string name, T data, CancellationToken cancellationToken) =>
        WriteAsync("event: " + name + "\ndata: " + JsonSerializer.Serialize(data, _json) + "\n\n", cancellationToken);

    private async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The first news of the stream: what it holds when the page starts to follow it.
/// </summary>
public sealed record HelloView
{
    /// <summary>Gets the oldest number the stream still holds, or 0 for an empty stream. The page drops what is older.</summary>
    public required long Oldest { get; init; }

    /// <summary>Gets how many lines the stream holds in all.</summary>
    public required int Total { get; init; }

    /// <summary>Gets how many lines the stream keeps before the oldest go.</summary>
    public required int MaxLines { get; init; }
}

/// <summary>
/// The news that the oldest lines left the stream.
/// </summary>
public sealed record RemovedView
{
    /// <summary>Gets the highest number that went.</summary>
    public required long UpTo { get; init; }
}
