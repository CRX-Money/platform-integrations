namespace CrxMoney.Integrations.Providers.Br.Inter.Api;

/// <summary>
/// A sliding-log limiter: at most <c>limit</c> calls start in any <c>window</c>. Callers over the limit
/// wait (cancellably) until the oldest call leaves the window instead of being rejected, so a sync
/// paces itself to Inter's per-minute limits rather than collecting 429s.
/// </summary>
internal sealed class InterCallThrottle
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<DateTimeOffset> _starts = new();
    private readonly Lock _gate = new();

    public InterCallThrottle(int limit, TimeSpan window, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
        _window = window;
        _timeProvider = timeProvider;
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan wait;
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                while (_starts.Count > 0 && _starts.Peek() + _window <= now)
                    _starts.Dequeue();

                if (_starts.Count < _limit)
                {
                    _starts.Enqueue(now);
                    return;
                }

                wait = _starts.Peek() + _window - now;
            }

            await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
