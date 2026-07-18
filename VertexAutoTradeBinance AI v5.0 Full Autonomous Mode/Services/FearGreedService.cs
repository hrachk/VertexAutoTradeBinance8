// ═══════════════════════════════════════════════════════════════════════════
// FearGreedService.cs
// Fetches Crypto Fear & Greed Index from alternative.me API.
// Updates every 4 hours. Used as macro market bias filter.
//
// Integration with strategy:
//   Index < 20 (Extreme Fear)  → strongly prefer LONG signals
//   Index 20-40 (Fear)         → slight LONG bias
//   Index 40-60 (Neutral)      → no bias
//   Index 60-80 (Greed)        → slight SHORT bias
//   Index > 80 (Extreme Greed) → strongly prefer SHORT signals
// ═══════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VertexAutoTradeBinance8.Services
{
    public enum FearGreedClassification
    {
        Unknown,
        ExtremeFear,   // 0-24
        Fear,          // 25-44
        Neutral,       // 45-55
        Greed,         // 56-75
        ExtremeGreed   // 76-100
    }

    public sealed class FearGreedSnapshot
    {
        public int    Index           { get; init; } = 50;
        public string Label           { get; init; } = "Neutral";
        public FearGreedClassification Classification { get; init; } = FearGreedClassification.Neutral;
        public DateTime FetchedAtUtc  { get; init; } = DateTime.UtcNow;
        public bool   IsStale         => (DateTime.UtcNow - FetchedAtUtc).TotalHours > 8;

        /// <summary>
        /// Side bias: +1 = prefer LONG, -1 = prefer SHORT, 0 = neutral.
        /// Used to boost/penalise signal confidence.
        /// </summary>
        public int SideBias => Classification switch
        {
            FearGreedClassification.ExtremeFear  =>  1,
            FearGreedClassification.Fear         =>  1,
            FearGreedClassification.Greed        => -1,
            FearGreedClassification.ExtremeGreed => -1,
            _                                    =>  0,
        };

        /// <summary>
        /// Confidence multiplier for signals that AGREE with the macro bias.
        /// Signals AGAINST the bias get 1/mult.
        /// </summary>
        public decimal ConfidenceBoost => Classification switch
        {
            FearGreedClassification.ExtremeFear  => 1.15m,
            FearGreedClassification.Fear         => 1.08m,
            FearGreedClassification.ExtremeGreed => 1.15m,
            FearGreedClassification.Greed        => 1.08m,
            _                                    => 1.00m,
        };
    }

    public sealed class FearGreedService : BackgroundService
    {
        private readonly ILogger<FearGreedService> _logger;
        private readonly HttpClient _http;
        private FearGreedSnapshot _current = new();
        private readonly SemaphoreSlim _lock = new(1, 1);

        private const string ApiUrl = "https://api.alternative.me/fng/?limit=1&format=json";
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(4);

        public FearGreedService(ILogger<FearGreedService> logger)
        {
            _logger = logger;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public FearGreedSnapshot Current => _current;

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Initial fetch
            await FetchAsync();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RefreshInterval, ct);
                    await FetchAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FearGreed] Refresh failed, using cached value {idx}", _current.Index);
                }
            }
        }

        private async Task FetchAsync()
        {
            try
            {
                var json = await _http.GetStringAsync(ApiUrl);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data")[0];
                int value = int.Parse(data.GetProperty("value").GetString() ?? "50");
                string label = data.GetProperty("value_classification").GetString() ?? "Neutral";

                var classification = value switch
                {
                    <= 24 => FearGreedClassification.ExtremeFear,
                    <= 44 => FearGreedClassification.Fear,
                    <= 55 => FearGreedClassification.Neutral,
                    <= 75 => FearGreedClassification.Greed,
                    _     => FearGreedClassification.ExtremeGreed,
                };

                await _lock.WaitAsync();
                try { _current = new FearGreedSnapshot { Index = value, Label = label, Classification = classification, FetchedAtUtc = DateTime.UtcNow }; }
                finally { _lock.Release(); }

                _logger.LogInformation("[FearGreed] Index={idx} ({label}) Bias={bias}",
                    value, label, _current.SideBias > 0 ? "LONG" : _current.SideBias < 0 ? "SHORT" : "NEUTRAL");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FearGreed] Fetch failed");
            }
        }

        public override void Dispose() { _http.Dispose(); base.Dispose(); }
    }
}
