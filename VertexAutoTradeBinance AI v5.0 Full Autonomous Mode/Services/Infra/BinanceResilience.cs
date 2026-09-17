using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace VertexAutoTradeBinance8.Services.Infra;

/// <summary>Shared retry policy for transient Binance REST failures (-1021 handled via time resync hook).</summary>
public static class BinanceResilience
{
    public static AsyncRetryPolicy CreateRetryPolicy(ILogger log, Func<Task>? onTimestampError = null)
    {
        return Policy
            .Handle<HttpRequestException>()
            .Or<SocketException>()
            .Or<TaskCanceledException>()
            .Or<IOException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)),
                onRetry: (ex, delay, attempt, _) =>
                {
                    log.LogWarning(ex, "[BINANCE-RETRY] attempt={a} wait={ms}ms", attempt, delay.TotalMilliseconds);
                    var msg = ex.Message ?? "";
                    if (msg.Contains("-1021") || msg.Contains("recvWindow", StringComparison.OrdinalIgnoreCase))
                    {
                        try { onTimestampError?.Invoke()?.GetAwaiter().GetResult(); } catch { }
                    }
                });
    }

    public static async Task<T> ExecuteAsync<T>(AsyncRetryPolicy policy, Func<Task<T>> action)
        => await policy.ExecuteAsync(action);
}
