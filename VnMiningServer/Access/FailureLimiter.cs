using System;
using System.Collections.Generic;

namespace VnMiningServer.Access;

/// <summary>
/// Counts wrong keys by the address they come from. An address with too many in a minute is turned away
/// until the minute is over, also with the right key, so that guessing is slow.
/// </summary>
internal sealed class FailureLimiter
{
    /// <summary>How many wrong keys an address may send in a minute.</summary>
    internal const int MaxFailures = 10;

    /// <summary>How many addresses are remembered. The oldest go first.</summary>
    internal const int MaxAddresses = 4096;

    private static readonly TimeSpan _window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly System.Threading.Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Since, int Count)> _failures = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="FailureLimiter"/> class.
    /// </summary>
    /// <param name="time">The clock.</param>
    public FailureLimiter(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Whether an address is turned away at the moment.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="retryAfter">How long it has to wait.</param>
    /// <returns><c>true</c> when it sent too many wrong keys.</returns>
    public bool IsLocked(string address, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        lock (_lock)
        {
            if (!_failures.TryGetValue(address, out var entry))
            {
                return false;
            }

            var left = entry.Since + _window - _time.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                _failures.Remove(address);
                return false;
            }

            retryAfter = left;
            return entry.Count >= MaxFailures;
        }
    }

    /// <summary>
    /// Notes a wrong key.
    /// </summary>
    /// <param name="address">The address it came from.</param>
    public void Failed(string address)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_failures.TryGetValue(address, out var entry) && entry.Since + _window > now)
            {
                _failures[address] = (entry.Since, entry.Count + 1);
                return;
            }

            if (_failures.Count >= MaxAddresses)
            {
                Sweep(now);
            }

            _failures[address] = (now, 1);
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        var over = new List<string>();
        foreach (var (address, entry) in _failures)
        {
            if (entry.Since + _window <= now)
            {
                over.Add(address);
            }
        }

        foreach (var address in over)
        {
            _failures.Remove(address);
        }

        if (_failures.Count >= MaxAddresses)
        {
            _failures.Clear();
        }
    }
}
