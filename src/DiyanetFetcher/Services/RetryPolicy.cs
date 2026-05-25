using System.Net;

namespace DiyanetFetcher.Services;

public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        int maxAttempts = 5,
        int initialDelayMs = 500,
        double backoffMultiplier = 2.0,
        int maxDelayMs = 30_000,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        var delay = initialDelayMs;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastException = ex;
                if (attempt == maxAttempts) break;

                var jitter = Random.Shared.Next(0, 250);
                var waitMs = Math.Min(delay + jitter, maxDelayMs);

                Console.WriteLine($"  [RETRY] {label ?? "operation"}: deneme {attempt}/{maxAttempts} basarisiz ({ex.Message}). {waitMs}ms sonra tekrar...");
                await Task.Delay(waitMs, cancellationToken);

                delay = (int)Math.Min(delay * backoffMultiplier, maxDelayMs);
            }
        }

        throw new InvalidOperationException(
            $"[{label}] {maxAttempts} denemeden sonra basarisiz oldu.",
            lastException);
    }

    private static bool IsTransient(Exception ex)
    {
        return ex switch
        {
            HttpRequestException => true,
            TaskCanceledException => true,
            TimeoutException => true,
            IOException => true,
            DiyanetTransientException => true,
            _ => false,
        };
    }
}

public class DiyanetTransientException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public DiyanetTransientException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

public class DiyanetUnauthorizedException : Exception
{
    public DiyanetUnauthorizedException(string message) : base(message) { }
}
