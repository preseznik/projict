namespace ProJict.Core.Api;

/// <summary>
/// The API's door: locks out an address that keeps sending wrong tokens, and limits how fast one token may call.
/// Thread-safe; the clock is passed in so it can be tested.
/// </summary>
public sealed class ApiGate(int maxFailures = 10, TimeSpan? lockout = null, int perMinute = 600)
{
    private readonly TimeSpan _lockout = lockout ?? TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, (int Count, DateTime Since, DateTime? Until)> _failures = [];
    private readonly Dictionary<Guid, (double Left, DateTime At)> _buckets = [];
    private readonly Lock _lock = new();

    public int PerMinute => perMinute;

    /// <summary>How long the address must still wait, or null when it may try.</summary>
    public TimeSpan? LockedOut(string address, DateTime now)
    {
        lock (_lock)
        {
            if (!_failures.TryGetValue(address, out var f) || f.Until is not { } until) return null;
            if (now < until) return until - now;
            _failures.Remove(address);
            return null;
        }
    }

    /// <summary>A wrong token from the address; the last allowed one starts the lockout.</summary>
    public void Failed(string address, DateTime now)
    {
        lock (_lock)
        {
            // Failures older than the lockout period are forgotten.
            var f = _failures.TryGetValue(address, out var known) && now - known.Since < _lockout ? known : (0, now, null);
            var count = f.Count + 1;
            _failures[address] = (count, f.Since, count >= maxFailures ? now + _lockout : null);
        }
    }

    public void Succeeded(string address)
    {
        lock (_lock) _failures.Remove(address);
    }

    /// <summary>Takes one call from the token's allowance (a bucket of <see cref="PerMinute"/> that refills steadily).</summary>
    public bool Allow(Guid token, DateTime now, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var perSecond = perMinute / 60.0;
            var (left, at) = _buckets.TryGetValue(token, out var b) ? b : (perMinute, now);
            left = Math.Min(perMinute, left + Math.Max(0, (now - at).TotalSeconds) * perSecond);
            if (left < 1)
            {
                _buckets[token] = (left, now);
                retryAfter = TimeSpan.FromSeconds(Math.Ceiling((1 - left) / perSecond));
                return false;
            }
            _buckets[token] = (left - 1, now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
