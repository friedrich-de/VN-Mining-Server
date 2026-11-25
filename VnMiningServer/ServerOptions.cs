using System;
using VnMiningServer.Access;

namespace VnMiningServer;

/// <summary>
/// What the server is started with. Everything has a default, and everything can be set by an
/// environment variable.
/// </summary>
internal sealed record ServerOptions
{
    /// <summary>The environment variable for <see cref="DataFolder"/>.</summary>
    internal const string DataVariable = "VN_MINING_DATA";

    /// <summary>The environment variable for <see cref="Listen"/>.</summary>
    internal const string ListenVariable = "VN_MINING_LISTEN";

    /// <summary>The environment variable for <see cref="Ffmpeg"/>.</summary>
    internal const string FfmpegVariable = "VN_MINING_FFMPEG";

    /// <summary>Gets the folder for everything the server keeps: the stream, the dictionaries, the settings, the key.</summary>
    public string DataFolder { get; init; } = "data";

    /// <summary>Gets the address the server listens on. Only this machine reaches the default; a tunnel or a reverse proxy brings HTTPS.</summary>
    public string Listen { get; init; } = "http://127.0.0.1:8733";

    /// <summary>Gets the API key. Without one the server makes a key and keeps it in the data folder.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Gets the ffmpeg to run: a path, or a name the system finds.</summary>
    public string Ffmpeg { get; init; } = "ffmpeg";

    /// <summary>Gets a value indicating whether the server writes no log. Tests start many servers.</summary>
    public bool Quiet { get; init; }

    /// <summary>
    /// Reads the options from the environment.
    /// </summary>
    /// <param name="read">Reads one variable.</param>
    /// <returns>The options.</returns>
    public static ServerOptions FromEnvironment(Func<string, string?> read)
    {
        var defaults = new ServerOptions();
        return new ServerOptions
        {
            DataFolder = Value(read(DataVariable)) ?? defaults.DataFolder,
            Listen = Value(read(ListenVariable)) ?? defaults.Listen,
            ApiKey = Value(read(ApiAccess.KeyVariable)),
            Ffmpeg = Value(read(FfmpegVariable)) ?? defaults.Ffmpeg,
        };
    }

    private static string? Value(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
