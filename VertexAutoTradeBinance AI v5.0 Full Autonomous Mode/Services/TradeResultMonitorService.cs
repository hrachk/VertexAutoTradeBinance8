using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VertexAutoTradeBinance8.Helpers;   // ✅ ОБЯЗАТЕЛЬНО: здесь твой ToKlineInterval()
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class TradeResultMonitorService
    {
        private readonly ILogger<TradeResultMonitorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly AiSelfLearningService _aiLearning;
        private readonly AiMarketRegimeService _regimeService;

        public TradeResultMonitorService(
            ILogger<TradeResultMonitorService> logger,
            BinanceClientFactory factory,
            AiSelfLearningService aiLearning,
            AiMarketRegimeService regimeService)
        {
            _logger = logger;
            _factory = factory;
            _aiLearning = aiLearning;
            _regimeService = regimeService;
        }

        /// <summary>
        /// Проверяет факт закрытия позиции и обучает AI.
        /// </summary>
        public async Task CheckClosedPositionAsync(string symbol, TradeSignal signal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            var posResult = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
            if (!posResult.Success || posResult.Data == null)
                return;

            var longPos = posResult.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = posResult.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            decimal qtyLong = longPos != null ? Math.Abs(longPos.Quantity) : 0m;
            decimal qtyShort = shortPos != null ? Math.Abs(shortPos.Quantity) : 0m;

            // если позиция полностью закрыта
            if (qtyLong == 0 && qtyShort == 0)
            {
                decimal exitPrice = longPos?.MarkPrice ?? shortPos?.MarkPrice ?? 0m;

                if (exitPrice <= 0 || signal == null) return;

                decimal entry = signal.EntryPrice;
                decimal sl = signal.StopLoss;

                // R/R
                decimal risk = Math.Abs(entry - sl);
                decimal reward = Math.Abs(exitPrice - entry);

                decimal rr = reward / Math.Max(risk, 0.00001m);
                bool isWin = signal.Side == SignalSide.Buy
                    ? exitPrice > entry
                    : exitPrice < entry;

                // === РЕЖИМ РЫНКА (BINANCE.NET v11.11.0 правильный вызов) ===
                var klines = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    signal.Timeframe!.ToKlineInterval(),  // KlineInterval
                    startTime: null,                     // DateTime?
                    endTime: null,                       // DateTime?
                    limit: 150,                           // int?
                    ct                                    // CancellationToken
                );

                var regime = MarketRegime.Range;

                if (klines.Success)
                {
                    var det = _regimeService.DetectRegime(
     symbol,
     signal.Timeframe!.ToKlineInterval(),
     klines.Data.Cast<BinanceFuturesUsdtKline>().ToList()
 );

                    regime = det.Regime;
                }

                // === обучение ===
                _aiLearning.RegisterTradeResult(
                    symbol,
                    regime,
                    rr,
                    isWin);

                _logger.LogInformation(
                    "AI-LEARN CLOSED TRADE {symbol}: entry={entry}, exit={exit}, rr={rr:F2}, win={win}, regime={regime}",
                    symbol, entry, exitPrice, rr, isWin, regime);
            }
        }
    }
}
