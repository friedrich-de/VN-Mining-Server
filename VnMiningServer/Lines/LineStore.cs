using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace VnMiningServer.Lines;

/// <summary>
/// The one stream of the game's lines: a small database for the text and a folder for each line's voice
/// file and screenshot. Only the newest lines are kept, so the stream never grows without end.
/// </summary>
internal sealed class LineStore : IDisposable
{
    /// <summary>How many lines are kept unless the settings say otherwise.</summary>
    internal const int DefaultMaxLines = 1000;

    /// <summary>The fewest lines the settings may keep.</summary>
    internal const int MinMaxLines = 50;

    /// <summary>The most lines the settings may keep.</summary>
    internal const int MaxMaxLines = 20000;

    /// <summary>The longest text of a line, in characters.</summary>
    internal const int MaxTextLength = 4000;

    /// <summary>The longest name, voice name or title, in characters.</summary>
    internal const int MaxNameLength = 200;

    /// <summary>How many pages may follow the stream at once.</summary>
    internal const int MaxListeners = 32;

    /// <summary>How much news may wait for a page that reads slowly before the page is cut off to catch up anew.</summary>
    internal const int ListenerBacklog = 512;

    private readonly string _folder;
    private readonly string _mediaFolder;
    private readonly TimeProvider _time;
    private readonly Func<int> _maxLines;
    private readonly ILogger<LineStore> _logger;
    private readonly System.Threading.Lock _lock = new();
    private readonly List<Listener> _listeners = [];
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LineStore"/> class.
    /// </summary>
    /// <param name="folder">The folder the stream is kept in.</param>
    /// <param name="time">The clock.</param>
    /// <param name="maxLines">How many lines to keep, read whenever a line comes.</param>
    /// <param name="logger">The logger.</param>
    public LineStore(string folder, TimeProvider time, Func<int> maxLines, ILogger<LineStore> logger)
    {
        _folder = folder;
        _mediaFolder = Path.Combine(folder, "media");
        _time = time;
        _maxLines = maxLines;
        _logger = logger;
    }

    /// <summary>
    /// Gets how many pages follow the stream.
    /// </summary>
    public int ListenerCount
    {
        get
        {
            lock (_lock)
            {
                return _listeners.Count;
            }
        }
    }

    /// <summary>
    /// Brings the number of lines to keep into the range the server allows.
    /// </summary>
    /// <param name="value">What the settings say.</param>
    /// <returns>A number the store keeps to.</returns>
    public static int ClampMaxLines(int value) => value <= 0 ? DefaultMaxLines : Math.Clamp(value, MinMaxLines, MaxMaxLines);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var listener in _listeners)
            {
                listener.End();
            }

            _listeners.Clear();
            _connection?.Dispose();
            _connection = null;
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Takes a line into the stream. The same line sent twice in a row is kept once.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="audio">Its voice file, or <c>null</c>.</param>
    /// <param name="picture">Its screenshot, or <c>null</c>.</param>
    /// <returns>The line as it is kept.</returns>
    /// <exception cref="ArgumentException">The text is empty, or a file is of no kind the server knows.</exception>
    public StreamLine Add(NewLine line, byte[]? audio, byte[]? picture)
    {
        var text = Tidy(line.Text, MaxTextLength);
        if (text.Length == 0)
        {
            throw new ArgumentException("A line needs text.", nameof(line));
        }

        var audioExtension = audio is { Length: > 0 } ? MediaKind.Audio(audio) ?? throw new ArgumentException("The sound is of no kind the server knows (Ogg, MP3, WAV, FLAC, M4A).", nameof(audio)) : null;
        var pictureExtension = picture is { Length: > 0 } ? MediaKind.Picture(picture) ?? throw new ArgumentException("The picture is of no kind the server knows (JPEG, PNG, WebP).", nameof(picture)) : null;
        var name = TidyOrNull(line.Name);
        var voice = TidyOrNull(line.Voice);
        var game = TidyOrNull(line.Game);

        lock (_lock)
        {
            var connection = Open();
            var newest = Newest(connection);
            if (newest is not null && newest.Text == text && newest.Name == name && newest.Voice == voice)
            {
                // The game shows a line again after its menu or history. What the first copy lacks is added.
                var gains = (newest.AudioExtension is null && audioExtension is not null) || (newest.PictureExtension is null && pictureExtension is not null);
                if (!gains)
                {
                    return newest;
                }

                var withAudio = newest.AudioExtension ?? audioExtension;
                var withPicture = newest.PictureExtension ?? pictureExtension;
                if (newest.AudioExtension is null && audioExtension is not null)
                {
                    WriteMedia(newest.Id, audioExtension, audio!);
                }

                if (newest.PictureExtension is null && pictureExtension is not null)
                {
                    WriteMedia(newest.Id, pictureExtension, picture!);
                }

                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE lines SET audio = $audio, picture = $picture WHERE id = $id";
                update.Parameters.AddWithValue("$audio", (object?)withAudio ?? DBNull.Value);
                update.Parameters.AddWithValue("$picture", (object?)withPicture ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", newest.Id);
                update.ExecuteNonQuery();
                var changed = newest with { AudioExtension = withAudio, PictureExtension = withPicture };
                Tell(new StreamEvent(StreamEventKind.Added, changed, 0));
                return changed;
            }

            var at = _time.GetUtcNow();
            long id;
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO lines (at, text, name, voice, game, audio, picture) VALUES ($at, $text, $name, $voice, $game, $audio, $picture); SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$text", text);
                insert.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
                insert.Parameters.AddWithValue("$voice", (object?)voice ?? DBNull.Value);
                insert.Parameters.AddWithValue("$game", (object?)game ?? DBNull.Value);
                insert.Parameters.AddWithValue("$audio", (object?)audioExtension ?? DBNull.Value);
                insert.Parameters.AddWithValue("$picture", (object?)pictureExtension ?? DBNull.Value);
                id = (long)insert.ExecuteScalar()!;
            }

            if (audioExtension is not null)
            {
                WriteMedia(id, audioExtension, audio!);
            }

            if (pictureExtension is not null)
            {
                WriteMedia(id, pictureExtension, picture!);
            }

            var added = new StreamLine(id, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), text, name, voice, game, audioExtension, pictureExtension);
            Tell(new StreamEvent(StreamEventKind.Added, added, 0));
            Prune(connection, ClampMaxLines(_maxLines()));
            return added;
        }
    }

    /// <summary>
    /// The lines after a number, oldest first.
    /// </summary>
    /// <param name="after">The number to start after. 0 for the oldest kept.</param>
    /// <param name="limit">The most lines to return.</param>
    /// <returns>The lines.</returns>
    public IReadOnlyList<StreamLine> After(long after, int limit)
    {
        lock (_lock)
        {
            using var command = Open().CreateCommand();
            command.CommandText = "SELECT id, at, text, name, voice, game, audio, picture FROM lines WHERE id > $after ORDER BY id LIMIT $limit";
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxMaxLines));
            return Read(command);
        }
    }

    /// <summary>
    /// The newest lines, oldest first.
    /// </summary>
    /// <param name="limit">The most lines to return.</param>
    /// <returns>The lines.</returns>
    public IReadOnlyList<StreamLine> Latest(int limit)
    {
        lock (_lock)
        {
            using var command = Open().CreateCommand();
            command.CommandText = "SELECT id, at, text, name, voice, game, audio, picture FROM (SELECT * FROM lines ORDER BY id DESC LIMIT $limit) ORDER BY id";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxMaxLines));
            return Read(command);
        }
    }

    /// <summary>
    /// The lines just before a number, oldest first.
    /// </summary>
    /// <param name="before">The number to end before.</param>
    /// <param name="limit">The most lines to return.</param>
    /// <returns>The lines.</returns>
    public IReadOnlyList<StreamLine> Before(long before, int limit)
    {
        lock (_lock)
        {
            using var command = Open().CreateCommand();
            command.CommandText = "SELECT id, at, text, name, voice, game, audio, picture FROM (SELECT * FROM lines WHERE id < $before ORDER BY id DESC LIMIT $limit) ORDER BY id";
            command.Parameters.AddWithValue("$before", before);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxMaxLines));
            return Read(command);
        }
    }

    /// <summary>
    /// One line by its number.
    /// </summary>
    /// <param name="id">The number.</param>
    /// <returns>The line, or <c>null</c> when it is gone.</returns>
    public StreamLine? Find(long id)
    {
        lock (_lock)
        {
            using var command = Open().CreateCommand();
            command.CommandText = "SELECT id, at, text, name, voice, game, audio, picture FROM lines WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            var found = Read(command);
            return found.Count == 0 ? null : found[0];
        }
    }

    /// <summary>
    /// How many lines the stream holds.
    /// </summary>
    /// <returns>The count.</returns>
    public int Count()
    {
        lock (_lock)
        {
            using var command = Open().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM lines";
            return (int)(long)command.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// The oldest number the stream still holds.
    /// </summary>
    /// <returns>The number, or 0 for an empty stream.</returns>
    public long Oldest()
    {
        lock (_lock)
        {
            using var command = Open().CreateCommand();
            command.CommandText = "SELECT COALESCE(MIN(id), 0) FROM lines";
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Where a line's voice file is.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The path, or <c>null</c> when the line has none.</returns>
    public string? AudioPath(StreamLine line) => line.AudioExtension is null ? null : MediaPath(line.Id, line.AudioExtension);

    /// <summary>
    /// Where a line's screenshot is.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The path, or <c>null</c> when the line has none.</returns>
    public string? PicturePath(StreamLine line) => line.PictureExtension is null ? null : MediaPath(line.Id, line.PictureExtension);

    /// <summary>
    /// Removes every line and its files.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            var connection = Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM lines";
                command.ExecuteNonQuery();
            }

            try
            {
                if (Directory.Exists(_mediaFolder))
                {
                    Directory.Delete(_mediaFolder, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "The stream's files in {Folder} could not all be removed", _mediaFolder);
            }

            Tell(new StreamEvent(StreamEventKind.Cleared, null, 0));
        }
    }

    /// <summary>
    /// Brings the stream down to the number of lines the settings keep, after the setting changed.
    /// </summary>
    public void Trim()
    {
        lock (_lock)
        {
            Prune(Open(), ClampMaxLines(_maxLines()));
        }
    }

    /// <summary>
    /// Starts following the stream.
    /// </summary>
    /// <returns>What the news arrives on. Disposing it stops following.</returns>
    public Listener Listen()
    {
        var listener = new Listener(this);
        lock (_lock)
        {
            if (_disposed)
            {
                listener.End();
            }
            else
            {
                _listeners.Add(listener);
            }
        }

        return listener;
    }

    private static string Tidy(string? value, int max)
    {
        var text = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (text.Length <= max)
        {
            return text;
        }

        // Not in the middle of a character that takes two places.
        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut];
    }

    private static string? TidyOrNull(string? value)
    {
        var text = Tidy(value, MaxNameLength).Replace('\n', ' ');
        return text.Length == 0 ? null : text;
    }

    private static List<StreamLine> Read(SqliteCommand command)
    {
        var lines = new List<StreamLine>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(new StreamLine(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return lines;
    }

    private static StreamLine? Newest(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, at, text, name, voice, game, audio, picture FROM lines ORDER BY id DESC LIMIT 1";
        var found = Read(command);
        return found.Count == 0 ? null : found[0];
    }

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null)
        {
            return _connection;
        }

        Directory.CreateDirectory(_folder);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_folder, "stream.db") }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            // AUTOINCREMENT: a removed line's number is never given to a later line, so a page that asks for
            // "the lines after number N" is never handed an old number again.
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS lines (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    at INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    name TEXT,
                    voice TEXT,
                    game TEXT,
                    audio TEXT,
                    picture TEXT
                );
                """;
            command.ExecuteNonQuery();
        }

        _connection = connection;
        return connection;
    }

    private void Prune(SqliteConnection connection, int max)
    {
        var gone = new List<StreamLine>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, at, text, name, voice, game, audio, picture FROM lines WHERE id <= (SELECT id FROM lines ORDER BY id DESC LIMIT 1 OFFSET $max) ORDER BY id";
            command.Parameters.AddWithValue("$max", max);
            gone = Read(command);
        }

        if (gone.Count == 0)
        {
            return;
        }

        var upTo = gone[^1].Id;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM lines WHERE id <= $upTo";
            command.Parameters.AddWithValue("$upTo", upTo);
            command.ExecuteNonQuery();
        }

        foreach (var line in gone)
        {
            DeleteMedia(AudioPath(line));
            DeleteMedia(PicturePath(line));
        }

        Tell(new StreamEvent(StreamEventKind.Removed, null, upTo));
    }

    private string MediaPath(long id, string extension) =>
        Path.Combine(_mediaFolder, id.ToString(CultureInfo.InvariantCulture) + "." + extension);

    private void WriteMedia(long id, string extension, byte[] content)
    {
        Directory.CreateDirectory(_mediaFolder);
        var path = MediaPath(id, extension);
        var temporary = path + ".new";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private void DeleteMedia(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "The file {Path} of a line that left the stream could not be removed", path);
        }
    }

    private void Tell(StreamEvent news)
    {
        for (var index = _listeners.Count - 1; index >= 0; index--)
        {
            if (!_listeners[index].Offer(news))
            {
                // It reads too slowly. Its stream ends, and the page asks anew for what it missed.
                _listeners[index].End();
                _listeners.RemoveAt(index);
            }
        }
    }

    private void Remove(Listener listener)
    {
        lock (_lock)
        {
            _listeners.Remove(listener);
        }
    }

    /// <summary>
    /// One page that follows the stream.
    /// </summary>
    internal sealed class Listener : IDisposable
    {
        private readonly LineStore _store;
        private readonly Channel<StreamEvent> _channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(ListenerBacklog)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        /// <summary>
        /// Initializes a new instance of the <see cref="Listener"/> class.
        /// </summary>
        /// <param name="store">The store it listens to.</param>
        public Listener(LineStore store)
        {
            _store = store;
        }

        /// <summary>Gets the news, in the order it happened. It completes when the store cuts the page off.</summary>
        public ChannelReader<StreamEvent> News => _channel.Reader;

        /// <inheritdoc />
        public void Dispose()
        {
            _store.Remove(this);
            End();
        }

        /// <summary>
        /// Hands the listener news.
        /// </summary>
        /// <param name="news">The news.</param>
        /// <returns><c>false</c> when too much waits already.</returns>
        internal bool Offer(StreamEvent news) => _channel.Writer.TryWrite(news);

        /// <summary>
        /// Ends the listener's stream.
        /// </summary>
        internal void End() => _channel.Writer.TryComplete();
    }
}
