using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VnMiningServer.Media;

/// <summary>
/// How to make a card's picture from a line's screenshot.
/// </summary>
/// <param name="Format"><c>jpeg</c>, <c>webp</c> or <c>avif</c>.</param>
/// <param name="Width">The widest the picture may be, in pixels. A smaller screenshot is not enlarged.</param>
/// <param name="Quality">From 1, smallest file, to 100, best picture.</param>
internal sealed record PictureRequest(string Format, int Width, int Quality);

/// <summary>
/// How to make a card's sound from the voice files of one or several lines.
/// </summary>
/// <param name="Format"><c>mp3</c> or <c>opus</c>.</param>
/// <param name="Bitrate">Kilobits per second.</param>
/// <param name="Normalize">Whether to bring the loudness to a common level.</param>
internal sealed record AudioRequest(string Format, int Bitrate, bool Normalize);

/// <summary>
/// How making a file ended.
/// </summary>
internal enum CutStatus
{
    /// <summary>The file is there.</summary>
    Done,

    /// <summary>What was asked for makes no sense or this server cannot make it.</summary>
    Refused,

    /// <summary>Too many files are being made already.</summary>
    Busy,

    /// <summary>ffmpeg failed or made nothing.</summary>
    Failed,
}

/// <summary>
/// The outcome of making a file.
/// </summary>
/// <param name="Status">How it ended.</param>
/// <param name="Path">The file, when it is done. Whoever takes it deletes it.</param>
/// <param name="ContentType">The file's media type.</param>
/// <param name="Extension">The file's extension, without a dot.</param>
/// <param name="Message">A sentence for the user when there is no file.</param>
internal sealed record CutResult(CutStatus Status, string? Path, string? ContentType, string? Extension, string? Message)
{
    public static CutResult Refuse(string message) => new(CutStatus.Refused, null, null, null, message);
}

/// <summary>
/// Turns what the game sent into what goes on a card, with ffmpeg: the screenshot in the format, size and
/// quality the user chose, and the voice files of the chosen lines as one sound. Two run at a time and the
/// rest wait in line; each has a time limit and is stopped when the page that asked for it is gone.
/// </summary>
internal sealed class LineMedia : IDisposable
{
    /// <summary>The longest sound, in milliseconds.</summary>
    internal const long MaxAudioMs = 120_000;

    /// <summary>How many lines' voices one sound may hold.</summary>
    internal const int MaxAudioLines = 30;

    /// <summary>The pause between two lines' voices in one sound, in milliseconds.</summary>
    internal const int GapMs = 250;

    /// <summary>How many run at the same time.</summary>
    internal const int Running = 2;

    /// <summary>How many may wait or run before more are turned away.</summary>
    internal const int MaxWaiting = 8;

    private static readonly string[] _avifEncoders = ["libsvtav1", "libaom-av1"];

    private readonly IFfmpeg _ffmpeg;
    private readonly string _scratchDirectory;
    private readonly ILogger<LineMedia> _logger;
    private readonly SemaphoreSlim _slots = new(Running, Running);
    private readonly Lock _lock = new();
    private int _waiting;
    private Task<string?>? _avifEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="LineMedia"/> class.
    /// </summary>
    /// <param name="ffmpeg">Runs ffmpeg.</param>
    /// <param name="scratchDirectory">A folder for the files that are made.</param>
    /// <param name="logger">The logger.</param>
    public LineMedia(IFfmpeg ffmpeg, string scratchDirectory, ILogger<LineMedia> logger)
    {
        _ffmpeg = ffmpeg;
        _scratchDirectory = scratchDirectory;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// Removes what an earlier run of the server left behind in the folder for made files.
    /// </summary>
    public void Sweep()
    {
        try
        {
            if (Directory.Exists(_scratchDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_scratchDirectory))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Files left in {Folder} could not be removed", _scratchDirectory);
        }
    }

    /// <summary>
    /// Makes a card's picture from a screenshot.
    /// </summary>
    /// <param name="screenshot">The screenshot's file.</param>
    /// <param name="request">How to make it.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The outcome.</returns>
    public async Task<CutResult> PictureAsync(string screenshot, PictureRequest request, CancellationToken cancellationToken)
    {
        var format = request.Format.ToLowerInvariant();
        if (format == "jpg")
        {
            format = "jpeg";
        }

        if (format is not ("jpeg" or "webp" or "avif"))
        {
            return CutResult.Refuse("A picture is made as jpeg, webp or avif.");
        }

        string? encoder = null;
        if (format == "avif")
        {
            encoder = await AvifEncoderAsync(cancellationToken).ConfigureAwait(false);
            if (encoder is null)
            {
                return CutResult.Refuse("The ffmpeg of this server cannot make avif pictures. Choose jpeg or webp.");
            }
        }

        return await RunAsync(
            format == "jpeg" ? "jpg" : format,
            "image/" + format,
            TimeSpan.FromSeconds(30),
            output => PictureArguments(screenshot, output, request with { Format = format }, encoder),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a card's sound from the voice files of the chosen lines, in their order, with a short pause between two.
    /// </summary>
    /// <param name="voices">The voice files.</param>
    /// <param name="request">How to make it.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The outcome.</returns>
    public async Task<CutResult> AudioAsync(IReadOnlyList<string> voices, AudioRequest request, CancellationToken cancellationToken)
    {
        var format = request.Format.ToLowerInvariant();
        if (format is not ("mp3" or "opus"))
        {
            return CutResult.Refuse("Sound is made as mp3 or opus.");
        }

        if (voices.Count == 0)
        {
            return CutResult.Refuse("None of these lines has a voice.");
        }

        if (voices.Count > MaxAudioLines)
        {
            return CutResult.Refuse(string.Create(CultureInfo.InvariantCulture, $"One sound holds the voices of at most {MaxAudioLines} lines."));
        }

        return await RunAsync(
            format == "mp3" ? "mp3" : "ogg",
            format == "mp3" ? "audio/mpeg" : "audio/ogg",
            TimeSpan.FromSeconds(60),
            output => AudioArguments(voices, output, request with { Format = format }),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The ffmpeg call for a picture.
    /// </summary>
    internal static List<string> PictureArguments(string screenshot, string output, PictureRequest request, string? avifEncoder)
    {
        var width = Math.Clamp(request.Width, 16, 3840);
        var quality = Math.Clamp(request.Quality, 1, 100);
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-i", screenshot,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-frames:v", "1",

            // A screenshot narrower than the wanted width keeps its size. The height follows and stays even.
            "-vf", string.Create(CultureInfo.InvariantCulture, $"scale='min({width},iw)':-2"),
        };

        switch (request.Format)
        {
            case "jpeg":
                // ffmpeg counts the other way round: 2 is the best picture, 31 the smallest file.
                arguments.AddRange(["-c:v", "mjpeg", "-pix_fmt", "yuvj420p", "-q:v", Number(Math.Clamp((int)Math.Round(31 - (quality * 0.29)), 2, 31)), "-f", "image2"]);
                break;
            case "webp":
                arguments.AddRange(["-c:v", "libwebp", "-quality", Number(quality), "-compression_level", "3", "-f", "webp"]);
                break;
            default:
                // For AV1 a lower number is a better picture too: 63 is the smallest file.
                arguments.AddRange(["-c:v", avifEncoder ?? _avifEncoders[0], "-crf", Number(Math.Clamp((int)Math.Round(60 - (quality * 0.4)), 18, 60)), "-pix_fmt", "yuv420p"]);
                arguments.AddRange(avifEncoder == "libaom-av1" ? ["-cpu-used", "8"] : ["-preset", "10"]);
                arguments.AddRange(["-still-picture", "1", "-f", "avif"]);
                break;
        }

        arguments.Add(output);
        return arguments;
    }

    /// <summary>
    /// The ffmpeg call for a sound.
    /// </summary>
    internal static List<string> AudioArguments(IReadOnlyList<string> voices, string output, AudioRequest request)
    {
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var voice in voices)
        {
            arguments.AddRange(["-i", voice]);
        }

        // Every voice is brought to the same shape, so that they join whatever the game recorded them as.
        // All but the last get a pause at their end.
        var gap = (GapMs / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
        var graph = new List<string>();
        for (var index = 0; index < voices.Count; index++)
        {
            var pad = index < voices.Count - 1 ? ",apad=pad_dur=" + gap : string.Empty;
            graph.Add(string.Create(CultureInfo.InvariantCulture, $"[{index}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo{pad}[a{index}]"));
        }

        var joined = string.Concat(Enumerable.Range(0, voices.Count).Select(index => string.Create(CultureInfo.InvariantCulture, $"[a{index}]")));
        var last = string.Create(CultureInfo.InvariantCulture, $"{joined}concat=n={voices.Count}:v=0:a=1");
        if (request.Normalize)
        {
            last += ",loudnorm=I=-16:TP=-1.5:LRA=11";
        }

        graph.Add(last + "[out]");
        arguments.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[out]", "-vn", "-sn", "-dn", "-t", (MaxAudioMs / 1000m).ToString("0.###", CultureInfo.InvariantCulture)]);
        if (request.Format == "mp3")
        {
            arguments.AddRange(["-c:a", "libmp3lame", "-b:a", Number(Math.Clamp(request.Bitrate, 32, 320)) + "k", "-ar", "48000", "-f", "mp3"]);
        }
        else
        {
            // loudnorm works at 192 kHz, and Opus only takes its own rates.
            arguments.AddRange(["-c:a", "libopus", "-b:a", Number(Math.Clamp(request.Bitrate, 16, 256)) + "k", "-ar", "48000", "-f", "ogg"]);
        }

        arguments.Add(output);
        return arguments;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<CutResult> RunAsync(
        string extension,
        string contentType,
        TimeSpan limit,
        Func<string, List<string>> arguments,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_waiting >= MaxWaiting)
            {
                return new CutResult(CutStatus.Busy, null, null, null, "Too many pictures and sounds are being made at the moment. Try again in a few seconds.");
            }

            _waiting++;
        }

        var output = Path.Combine(_scratchDirectory, Guid.NewGuid().ToString("N") + "." + extension);
        var done = false;
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_scratchDirectory);
                var ran = await _ffmpeg.RunAsync(arguments(output), limit, cancellationToken).ConfigureAwait(false);
                if (!ran || !File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    return new CutResult(CutStatus.Failed, null, null, null, "ffmpeg could not make this. The server's log says why.");
                }

                done = true;
                return new CutResult(CutStatus.Done, output, contentType, extension, null);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            lock (_lock)
            {
                _waiting--;
            }

            if (!done)
            {
                try
                {
                    File.Delete(output);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "A file that was not finished could not be removed");
                }
            }
        }
    }

    /// <summary>
    /// Finds out, once, which AV1 encoder this server's ffmpeg has.
    /// </summary>
    private Task<string?> AvifEncoderAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _avifEncoder ??= FindAsync();
        }

        async Task<string?> FindAsync()
        {
            try
            {
                var encoders = await _ffmpeg.AskAsync(["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false) ?? string.Empty;
                return _avifEncoders.FirstOrDefault(name => encoders.Contains(" " + name + " ", StringComparison.Ordinal));
            }
            catch (OperationCanceledException)
            {
                // Nobody waited for the answer. The next request asks again.
                lock (_lock)
                {
                    _avifEncoder = null;
                }

                throw;
            }
        }
    }
}
