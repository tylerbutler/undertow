namespace Undertow.Runtime;

/// <summary>
/// Plain token bucket refilled from TimeProvider deltas on access; no timer.
/// A rate of 0 disables the limit (beryl's convention).
/// </summary>
public sealed class TokenBucket
{
    private readonly double _ratePerSecond;
    private readonly double _burst;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private double _tokens;
    private long _lastRefill;

    public TokenBucket(double ratePerSecond, double burst, TimeProvider time)
    {
        _ratePerSecond = ratePerSecond;
        _burst = burst;
        _time = time;
        _tokens = burst;
        _lastRefill = time.GetTimestamp();
    }

    public bool TryTake()
    {
        if (_ratePerSecond <= 0)
            return true;

        lock (_lock)
        {
            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
            _lastRefill = now;
            _tokens = Math.Min(_burst, _tokens + elapsed * _ratePerSecond);
            if (_tokens < 1)
                return false;
            _tokens -= 1;
            return true;
        }
    }
}

/// <summary>
/// Concurrent-socket ceilings, per peer address and node-wide. Slots are
/// acquired before the upgrade (so rejection is an HTTP 429, not a close
/// frame) and released in the connection's finally. 0 = unlimited.
/// </summary>
public sealed class ConnectionLimiter(int maxPerIp, int maxTotal)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _perIp = [];
    private int _total;

    public bool TryAcquire(string ip)
    {
        lock (_lock)
        {
            var current = _perIp.GetValueOrDefault(ip);
            if (maxPerIp > 0 && current >= maxPerIp)
                return false;
            if (maxTotal > 0 && _total >= maxTotal)
                return false;
            _perIp[ip] = current + 1;
            _total++;
            return true;
        }
    }

    public void Release(string ip)
    {
        lock (_lock)
        {
            if (_perIp.TryGetValue(ip, out var current))
            {
                if (current <= 1)
                    _perIp.Remove(ip);
                else
                    _perIp[ip] = current - 1;
            }

            if (_total > 0)
                _total--;
        }
    }
}
