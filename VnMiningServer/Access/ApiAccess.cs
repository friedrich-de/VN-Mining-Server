using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VnMiningServer.Access;

/// <summary>
/// The server's one API key, and the cookie a browser gets once it has shown the key. The game sends the key
/// with every line. The page asks for it once and keeps the cookie, which script cannot read.
/// </summary>
internal sealed class ApiAccess
{
    /// <summary>The environment variable that sets the key. Without it the server makes one and keeps it in a file.</summary>
    internal const string KeyVariable = "VN_MINING_API_KEY";

    /// <summary>The file a made key is kept in, in the data folder.</summary>
    internal const string KeyFileName = "api-key.txt";

    /// <summary>The cookie's name.</summary>
    internal const string CookieName = "vnm_session";

    /// <summary>The shortest key the server takes from the environment or the file.</summary>
    internal const int MinKeyLength = 16;

    /// <summary>How long a browser stays signed in. Browsers keep no cookie for longer than 400 days.</summary>
    internal static readonly TimeSpan SessionLength = TimeSpan.FromDays(400);

    private readonly byte[] _key;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiAccess"/> class.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="time">The clock.</param>
    public ApiAccess(string key, TimeProvider time)
    {
        if (key.Length < MinKeyLength)
        {
            throw new ArgumentException($"The API key must have at least {MinKeyLength} characters.", nameof(key));
        }

        Key = key;
        _key = Encoding.UTF8.GetBytes(key);
        _time = time;
    }

    /// <summary>Gets the key, for the game's settings file.</summary>
    public string Key { get; }

    /// <summary>
    /// Reads the key from the environment or from the data folder, and makes one where there is none.
    /// </summary>
    /// <param name="fromEnvironment">The value of <see cref="KeyVariable"/>, if set.</param>
    /// <param name="dataFolder">The data folder.</param>
    /// <param name="made">Whether a new key was written to the file.</param>
    /// <returns>The key.</returns>
    public static string ReadOrMakeKey(string? fromEnvironment, string dataFolder, out bool made)
    {
        made = false;
        var given = fromEnvironment?.Trim();
        if (!string.IsNullOrEmpty(given))
        {
            return given;
        }

        var path = Path.Combine(dataFolder, KeyFileName);
        if (File.Exists(path))
        {
            var stored = File.ReadAllText(path).Trim();
            if (stored.Length >= MinKeyLength)
            {
                return stored;
            }
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Directory.CreateDirectory(dataFolder);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            // Only the user the server runs as may read it.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(key + "\n"));
            stream.Flush(flushToDisk: true);
        }

        made = true;
        return key;
    }

    /// <summary>
    /// Whether this is the key. Takes the same time for every wrong key of a length.
    /// </summary>
    /// <param name="candidate">What a caller sent.</param>
    /// <returns><c>true</c> for the key.</returns>
    public bool IsKey(string? candidate) =>
        !string.IsNullOrEmpty(candidate) && CryptographicOperations.FixedTimeEquals(Hash(Encoding.UTF8.GetBytes(candidate)), Hash(_key));

    /// <summary>
    /// Makes the value of a session cookie: when it ends, and a signature only this key makes. A new key ends every session.
    /// </summary>
    /// <returns>The cookie's value and the moment it ends.</returns>
    public (string Value, DateTimeOffset Ends) NewSession()
    {
        var ends = _time.GetUtcNow() + SessionLength;
        var seconds = ends.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return ($"v1.{seconds}.{Sign(seconds)}", ends);
    }

    /// <summary>
    /// Whether a cookie's value is a session this key signed that has not ended.
    /// </summary>
    /// <param name="value">The cookie's value.</param>
    /// <returns><c>true</c> for a session that holds.</returns>
    public bool IsSession(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 3 || parts[0] != "v1" || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var signature = Encoding.ASCII.GetBytes(Sign(parts[1]));
        var given = Encoding.ASCII.GetBytes(parts[2]);
        return CryptographicOperations.FixedTimeEquals(signature, given) && _time.GetUtcNow().ToUnixTimeSeconds() < seconds;
    }

    private static byte[] Hash(byte[] value) => SHA256.HashData(value);

    private string Sign(string seconds)
    {
        var mark = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes("vn-mining-server session|" + seconds));
        return Convert.ToBase64String(mark).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
