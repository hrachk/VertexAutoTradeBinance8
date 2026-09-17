using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace VertexAutoTradeBinance8.Services.Infra;

/// <summary>
/// Shared retry policy for transient Binance REST failures.
/// Covers network, timeouts, -1021 recvWindow (optional resync), and HTTP 429 rate limits.
/// </summary>
public static class BinanceResilience
{
    public static AsyncRetryPolicy CreateRetryPolicy(ILogger log, Func<Task>? onTimestampError = null)
    {
        return Policy
            .Handle<HttpRequestException>(IsTransientHttp)
            .Or<SocketException>()
            .Or<IOException>()
            .Or<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
            .Or<Exception>(ex => IsRateLimitOrTimestamp(ex))
            .WaitAndRetryAsync(
                retryCount: 4,
                sleepDurationProvider: (attempt, ex, _) =>
                {
                    // 429 → longer backoff (1s, 2s, 4s, 8s)
                    if (IsRateLimitOrTimestamp(ex) && (ex.Message?.Contains("429") == true
                        || ex.Message?.Contains("Too Many", StringComparison.OrdinalIgnoreCase) == true))
                        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    return TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));
                },
                onRetryAsync: async (ex, delay, attempt, _) =>
                {
                    log.LogWarning(ex,
                        "[BINANCE-RETRY] attempt={a} wait={ms}ms reason={r}",
                        attempt, delay.TotalMilliseconds, Classify(ex));
                    var msg = ex.Message ?? "";
                    if (msg.Contains("-1021") || msg.Contains("recvWindow", StringComparison.OrdinalIgnoreCase))
                    {
                        try { if (onTimestampError != null) await onTimestampError(); } catch { }
                    }
                    await Task.CompletedTask;
                });
    }

    private static bool IsTransientHttp(HttpRequestException ex)
    {
        if (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout)
            return true;
        return true; // network-level HttpRequestException without status
    }

    private static bool IsRateLimitOrTimestamp(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Contains("429")
            || m.Contains("-1003")
            || m.Contains("Too Many", StringComparison.OrdinalIgnoreCase)
            || m.Contains("-1021")
            || m.Contains("recvWindow", StringComparison.OrdinalIgnoreCase);
    }

    private static string Classify(Exception ex)
    {
        var m = ex.Message ?? ex.GetType().Name;
        if (m.Contains("429") || m.Contains("-1003")) return "RATE_LIMIT";
        if (m.Contains("-1021") || m.Contains("recvWindow", StringComparison.OrdinalIgnoreCase)) return "TIMESTAMP";
        if (ex is SocketException or HttpRequestException) return "NETWORK";
        return "TRANSIENT";
    }

    public static async Task<T> ExecuteAsync<T>(AsyncRetryPolicy policy, Func<Task<T>> action)
        => await policy.ExecuteAsync(action);
}
