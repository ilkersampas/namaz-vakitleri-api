namespace DiyanetFetcher.Services;

/// <summary>
/// Diyanet API'sinin endpoint basina kota sinirini koruyan basit token-bucket benzeri throttler.
/// Endpoint pattern'i basina ayri pencere tutulur (ornek: "/api/PrayerTime/Monthly").
/// </summary>
public class RateLimiter
{
    private readonly int _maxRequestsPerWindow;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, Queue<DateTime>> _hits = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RateLimiter(int maxRequestsPerWindow = 5, TimeSpan? window = null)
    {
        _maxRequestsPerWindow = maxRequestsPerWindow;
        _window = window ?? TimeSpan.FromSeconds(2);
    }

    public async Task WaitAsync(string bucket, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _gate.WaitAsync(cancellationToken);
            TimeSpan? waitFor = null;
            try
            {
                if (!_hits.TryGetValue(bucket, out var queue))
                {
                    queue = new Queue<DateTime>();
                    _hits[bucket] = queue;
                }

                var now = DateTime.UtcNow;
                while (queue.Count > 0 && now - queue.Peek() > _window)
                    queue.Dequeue();

                if (queue.Count < _maxRequestsPerWindow)
                {
                    queue.Enqueue(now);
                    return;
                }

                var oldest = queue.Peek();
                waitFor = _window - (now - oldest) + TimeSpan.FromMilliseconds(50);
            }
            finally
            {
                _gate.Release();
            }

            if (waitFor is { } w && w > TimeSpan.Zero)
                await Task.Delay(w, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
