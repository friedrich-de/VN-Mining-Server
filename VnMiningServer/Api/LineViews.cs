using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using VnMiningServer.Lines;

namespace VnMiningServer.Api;

/// <summary>
/// One line of the game's text as the page gets it.
/// </summary>
public sealed record LineView
{
    /// <summary>Gets the line's number. Numbers only grow.</summary>
    public required long Id { get; init; }

    /// <summary>Gets when the server got it.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Gets what is said.</summary>
    public required string Text { get; init; }

    /// <summary>Gets who says it, where the game names a speaker.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the game's title, if the game sent one.</summary>
    public string? Game { get; init; }

    /// <summary>Gets a value indicating whether the server keeps the line's voice.</summary>
    public required bool HasAudio { get; init; }

    /// <summary>Gets a value indicating whether the server keeps the line's screenshot.</summary>
    public required bool HasPicture { get; init; }

    internal static LineView Of(StreamLine line) => new()
    {
        Id = line.Id,
        At = line.At,
        Text = line.Text,
        Name = line.Name,
        Game = line.Game,
        HasAudio = line.AudioExtension is not null,
        HasPicture = line.PictureExtension is not null,
    };
}

/// <summary>
/// A part of the stream.
/// </summary>
public sealed record LinesView
{
    /// <summary>Gets the lines, oldest first.</summary>
    public required IReadOnlyList<LineView> Lines { get; init; }

    /// <summary>Gets how many lines the stream holds in all.</summary>
    public required int Total { get; init; }

    /// <summary>Gets how many lines the stream keeps before the oldest go.</summary>
    public required int MaxLines { get; init; }
}

/// <summary>
/// What the game sends for a line, as a form with files.
/// </summary>
public sealed class LineForm
{
    /// <summary>Gets or sets what is said.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets who says it.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the game's name for the voice file.</summary>
    public string? Voice { get; set; }

    /// <summary>Gets or sets the game's title.</summary>
    public string? Game { get; set; }

    /// <summary>Gets or sets the line's voice file.</summary>
    public IFormFile? Audio { get; set; }

    /// <summary>Gets or sets the screenshot.</summary>
    public IFormFile? Screenshot { get; set; }
}
