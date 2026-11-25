using System;
using VnMiningServer.Access;
using VnMiningServer.Anki;
using VnMiningServer.Api;
using VnMiningServer.Dictionary;
using VnMiningServer.Lines;
using VnMiningServer.Media;
using VnMiningServer.Settings;

namespace VnMiningServer;

/// <summary>
/// The server's own services in one object, so the controllers ASP.NET Core creates can ask for them.
/// </summary>
public sealed class AppServices
{
    internal AppServices(
        PageFiles page,
        ICallerResolver callers,
        ApiAccess access,
        FailureLimiter limiter,
        LineStore lines,
        LineMedia media,
        UserSettingsStore settings,
        ISharedSettings shared,
        AnkiClient ankiClient,
        AnkiGate anki,
        DictionaryServices dictionaries,
        TimeProvider time)
    {
        Page = page;
        Callers = callers;
        Access = access;
        Limiter = limiter;
        Lines = lines;
        Media = media;
        Settings = settings;
        Shared = shared;
        AnkiClient = ankiClient;
        Anki = anki;
        Dictionaries = dictionaries.Library;
        DictionaryDownloads = dictionaries.Downloads;
        DictionaryScratch = dictionaries.Scratch;
        WordAudio = dictionaries.WordAudio;
        Time = time;
    }

    /// <summary>Gets the server's version, as the page and the log show it.</summary>
    internal static string Version { get; } = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? string.Empty;

    internal PageFiles Page { get; }

    internal ICallerResolver Callers { get; }

    /// <summary>Gets the API key and the sessions it signs.</summary>
    internal ApiAccess Access { get; }

    /// <summary>Gets what counts wrong keys.</summary>
    internal FailureLimiter Limiter { get; }

    /// <summary>Gets the stream of the game's lines.</summary>
    internal LineStore Lines { get; }

    /// <summary>Gets what makes a card's picture and sound from a line's files.</summary>
    internal LineMedia Media { get; }

    internal UserSettingsStore Settings { get; }

    internal ISharedSettings Shared { get; }

    internal AnkiClient AnkiClient { get; }

    internal AnkiGate Anki { get; }

    internal DictionaryLibrary Dictionaries { get; }

    internal DictionaryDownloader DictionaryDownloads { get; }

    /// <summary>Gets the folder where uploaded and downloaded dictionaries wait for their import.</summary>
    internal string DictionaryScratch { get; }

    /// <summary>Gets where the sound of a word comes from.</summary>
    internal WordAudioSource WordAudio { get; }

    internal TimeProvider Time { get; }
}

/// <summary>
/// The dictionaries, what downloads them, where their zips wait for their import, and where words' sounds come from.
/// </summary>
/// <param name="Library">The dictionaries.</param>
/// <param name="Downloads">What downloads them.</param>
/// <param name="Scratch">Where zips wait.</param>
/// <param name="WordAudio">Where words' sounds come from.</param>
internal sealed record DictionaryServices(DictionaryLibrary Library, DictionaryDownloader Downloads, string Scratch, WordAudioSource WordAudio);
