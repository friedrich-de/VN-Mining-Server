using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VnMiningServer.Lines;

namespace VnMiningServer.Settings;

/// <summary>
/// The settings of the server itself, which are not about cards.
/// </summary>
internal interface ISharedSettings
{
    /// <summary>
    /// Gets the address the server fetches how a word sounds from, as stored: encrypted, or empty for none.
    /// </summary>
    string WordAudioAddress { get; }

    /// <summary>
    /// Changes the address the server fetches how a word sounds from, and saves it.
    /// </summary>
    /// <param name="stored">The address, already encrypted, or empty for none.</param>
    void SetWordAudioAddress(string stored);

    /// <summary>
    /// Gets how many lines the stream keeps.
    /// </summary>
    int MaxLines { get; }

    /// <summary>
    /// Changes how many lines the stream keeps, and saves it.
    /// </summary>
    /// <param name="lines">The new number. It is brought into the range the server allows.</param>
    void SetMaxLines(int lines);
}

/// <summary>
/// The server's settings in a file in the data folder, readable only by the user the server runs as.
/// </summary>
internal sealed class FileSharedSettings : ISharedSettings
{
    /// <summary>The file's name in the data folder.</summary>
    internal const string FileName = "server-settings.json";

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<FileSharedSettings> _logger;
    private readonly System.Threading.Lock _lock = new();
    private Stored? _stored;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSharedSettings"/> class.
    /// </summary>
    /// <param name="dataFolder">The data folder. It is made on the first save.</param>
    /// <param name="logger">The logger.</param>
    public FileSharedSettings(string dataFolder, ILogger<FileSharedSettings> logger)
    {
        _path = Path.Combine(dataFolder, FileName);
        _logger = logger;
    }

    /// <inheritdoc />
    public string WordAudioAddress => Read().WordAudioAddress;

    /// <inheritdoc />
    public int MaxLines => LineStore.ClampMaxLines(Read().MaxLines);

    /// <inheritdoc />
    public void SetWordAudioAddress(string stored) => Change(settings => settings.WordAudioAddress = stored);

    /// <inheritdoc />
    public void SetMaxLines(int lines) => Change(settings => settings.MaxLines = LineStore.ClampMaxLines(lines));

    private Stored Read()
    {
        lock (_lock)
        {
            if (_stored is not null)
            {
                return _stored;
            }

            try
            {
                _stored = File.Exists(_path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(_path), _json) : null;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "The server's settings in {Path} could not be read. The defaults apply.", _path);
            }

            return _stored ??= new Stored();
        }
    }

    private void Change(Action<Stored> change)
    {
        lock (_lock)
        {
            var settings = Read();
            change(settings);
            var folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // The file is replaced whole, so a crash leaves the old one or the new one.
            var temporary = _path + ".new";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(settings, _json));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
    }

    private sealed class Stored
    {
        public string WordAudioAddress { get; set; } = string.Empty;

        public int MaxLines { get; set; } = LineStore.DefaultMaxLines;
    }
}
