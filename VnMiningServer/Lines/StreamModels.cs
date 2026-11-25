using System;

namespace VnMiningServer.Lines;

/// <summary>
/// One line of the game's text, as the server keeps it.
/// </summary>
/// <param name="Id">Its number. Numbers only grow, and a removed line's number is not given again.</param>
/// <param name="At">When the server got it.</param>
/// <param name="Text">What is said.</param>
/// <param name="Name">Who says it, where the game names a speaker.</param>
/// <param name="Voice">The game's name for the line's voice file, if it has one.</param>
/// <param name="Game">The game's title, if the game sent one.</param>
/// <param name="AudioExtension">The kind of the voice file kept for the line (<c>ogg</c>, ...), or <c>null</c> for none.</param>
/// <param name="PictureExtension">The kind of the screenshot kept for the line (<c>jpg</c>, ...), or <c>null</c> for none.</param>
internal sealed record StreamLine(long Id, DateTimeOffset At, string Text, string? Name, string? Voice, string? Game, string? AudioExtension, string? PictureExtension);

/// <summary>
/// A line the game sends.
/// </summary>
/// <param name="Text">What is said.</param>
/// <param name="Name">Who says it.</param>
/// <param name="Voice">The game's name for the voice file.</param>
/// <param name="Game">The game's title.</param>
internal sealed record NewLine(string Text, string? Name, string? Voice, string? Game);

/// <summary>
/// What happened to the stream.
/// </summary>
internal enum StreamEventKind
{
    /// <summary>A line came.</summary>
    Added,

    /// <summary>The oldest lines went, up to a number.</summary>
    Removed,

    /// <summary>Every line went.</summary>
    Cleared,
}

/// <summary>
/// News of the stream, for the pages that follow it.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Line">The line that came.</param>
/// <param name="UpTo">The highest number that went.</param>
internal sealed record StreamEvent(StreamEventKind Kind, StreamLine? Line, long UpTo);

/// <summary>
/// Tells what a file is from its first bytes. The server keeps only sound and pictures it knows.
/// </summary>
internal static class MediaKind
{
    /// <summary>
    /// The kind of a sound file.
    /// </summary>
    /// <param name="head">The file, or its first bytes.</param>
    /// <returns>Its extension, or <c>null</c> for a file that is no sound the server knows.</returns>
    public static string? Audio(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("OggS"u8))
        {
            return "ogg";
        }

        if (head.StartsWith("fLaC"u8))
        {
            return "flac";
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WAVE"u8))
        {
            return "wav";
        }

        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            return "m4a";
        }

        if (head.StartsWith("ID3"u8) || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0))
        {
            return "mp3";
        }

        return null;
    }

    /// <summary>
    /// The kind of a picture file.
    /// </summary>
    /// <param name="head">The file, or its first bytes.</param>
    /// <returns>Its extension, or <c>null</c> for a file that is no picture the server knows.</returns>
    public static string? Picture(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return "jpg";
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "png";
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        return null;
    }

    /// <summary>
    /// The media type of a kept file.
    /// </summary>
    /// <param name="extension">Its extension.</param>
    /// <returns>The media type.</returns>
    public static string ContentType(string extension) => extension switch
    {
        "ogg" => "audio/ogg",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        "m4a" => "audio/mp4",
        "mp3" => "audio/mpeg",
        "jpg" => "image/jpeg",
        "png" => "image/png",
        "webp" => "image/webp",
        _ => "application/octet-stream",
    };
}
