using Binance.Net.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services
{
    public class BackgroundMarketScannerService : BackgroundService
    {
        private readonly ILogger<BackgroundMarketScannerService> _logger;
        private readonly MarketDataService _market;
        private readonly AiMarketRegimeService _regime;
        private readonly AiSelfLearningService _ai;
        private readonly SymbolLiquidityScanner _liquidity;

        // интервал сканирования
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(20);

        public BackgroundMarketScannerService(
            ILogger<BackgroundMarketScannerService> logger,
            MarketDataService market,
            AiMarketRegimeService regime,
            AiSelfLearningService ai,
            SymbolLiquidityScanner liquidity)
        {
            _logger = logger;
            _market = market;
            _regime = regime;
            _ai = ai;
            _liquidity = liquidity;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[BG-SCANNER] Started");

            // основной бесконечный цикл
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 1. Берём авто-список ликвидных символов
                    var snapshots = await _liquidity.LoadSnapshotsAsync();
                    if (snapshots.Count == 0)
                    {
                        _logger.LogWarning("[BG-SCANNER] No symbols from liquidity scanner");
                    }

                    

                    foreach (var snap in snapshots)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        var symbol = snap.Symbol;
                        try
                        {

                            // --- 1. Загружаем клайны ---
                            var kl = await _market.GetKlines(symbol, KlineInterval.OneMinute, 160);
                            if (kl.Count < 50)
                                continue;

                            // --- 2. Определяем режим ---
                            var r = _regime.DetectRegime(symbol, KlineInterval.OneMinute, kl);
                            if (r == null)
                                continue;

                            // --- 3. ATR / slope / vol ---
                            decimal slope = r.TrendSlopePercent;
                            decimal vol = r.VolatilityPercent;
                            decimal atr = _market.CalculateAtr(kl, 14);
                            decimal conf = r.Confidence;      // <-- если свойство другое, просто поменяй

                            // --- 4. Запись в AI (канал №3) ---
                            _ai.RecordMarketStateTriggered(
                                reason: "BACKGROUND",
                                symbol: symbol,
                                timeframe: "OneMinute",
                                regime: r.Regime,
                                slope: slope,
                                volatility: vol,
                                atr: atr,
                                confidence: conf);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[BG-SCANNER] Error symbol={symbol}", symbol);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // нормальное завершение
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[BG-SCANNER] Fatal loop error");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("[BG-SCANNER] Stopped");
        }
    }
}
