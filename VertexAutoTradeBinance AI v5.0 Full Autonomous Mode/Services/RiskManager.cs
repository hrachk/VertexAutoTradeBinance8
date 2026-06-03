using Binance.Net.Enums;
using System.Text.Json;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;
        private readonly SymbolInfoService _symbolInfo;
        private readonly TradingOptions _options;
        private readonly BinanceClientFactory _factory;
        private readonly MarketDataService _marketData;
        private readonly AiLeverageService _aiLeverage;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly SmartRegimeService _smartRegime;
        private readonly SimulatedTradeService _simulator;

        private static readonly string MissedTradesPath =
            Path.Combine(AppContext.BaseDirectory, "missed_trades.json");

        public decimal LastBalanceUsdt { get; private set; }

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo,
            TradingOptions options,
            BinanceClientFactory factory,
            MarketDataService marketData,
            AiLeverageService aiLeverage,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            SimulatedTradeService simulator)
        {
            _logger = logger;
            _symbolInfo = symbolInfo;
            _options = options;
            _factory = factory;
            _marketData = marketData;
            _aiLeverage = aiLeverage;
            _marketRegimeService = marketRegimeService;
            _smartRegime = smartRegime;
            _simulator = simulator;
        }

        // ====================================================================
        // SAFE QTY — Fixed 35% margin sizing with symbol-specific leverage
        // ====================================================================
        public async Task<decimal> CalculateSafeQty(
            TradeSignal signal,
            string symbol,
            decimal entryPrice,
            decimal stopLoss,             // для логов
            decimal riskMultiplier,       // оставлен для совместимости
            decimal safetyRiskMultiplier, // оставлен для совместимости
            decimal leverage,             // будет переопределён под символ
            SignalSide side,
            List<decimal> takeProfits,
            CancellationToken ct)
        {
            if (entryPrice <= 0 || stopLoss <= 0)
                return 0;

            // LOAD FILTERS
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal step = f.step > 0 ? f.step : 0.001m;
            decimal minQty = f.minQty > 0 ? f.minQty : step;

            decimal minNotional = f.minNotional > 0
                ? f.minNotional
                : (_options.MinNotionalGuard > 0 ? _options.MinNotionalGuard : 5m);

            decimal binanceMinNotional = minNotional;

            // ACCOUNT BALANCE
            using var client = _factory.CreateRestClient();
            var acc = await client.UsdFuturesApi.Account.GetBalancesAsync(null, ct);
            decimal free = acc?.Data?.FirstOrDefault(x => x.Asset == "USDT")?.AvailableBalance ?? 0;

            LastBalanceUsdt = free;

            // Market regime and smart info (для логов/аналитики)
            var klines = await _marketData.GetKlines(symbol, KlineInterval.FiveMinutes, 200);
            var baseReg = _marketRegimeService.DetectRegime(symbol, KlineInterval.FiveMinutes, klines);
            var smart = _smartRegime.Evaluate(symbol, KlineInterval.FiveMinutes, klines);
            decimal atr = baseReg.VolatilityPercent * klines.Last().ClosePrice;

            // ===============================
            //  AI Opportunity Score (для логов)
            // ===============================
            int scoreUi = 50;

            // 1) Тренд
            decimal slope = Math.Abs(baseReg.TrendSlopePercent);
            if (slope > 0.004m) scoreUi += 22;
            else if (slope > 0.003m) scoreUi += 15;
            else if (slope > 0.002m) scoreUi += 8;
            else if (slope > 0.001m) scoreUi += 3;

            // 2) Волатильность
            decimal vol = baseReg.VolatilityPercent;
            if (vol > 0.025m) scoreUi -= 22;
            else if (vol > 0.015m) scoreUi -= 15;
            else if (vol > 0.010m) scoreUi -= 8;
            else if (vol > 0.006m) scoreUi -= 3;

            // 3) ATR нагрузка
            decimal atrPct = atr / entryPrice;
            if (atrPct > 0.025m) scoreUi -= 15;
            else if (atrPct > 0.015m) scoreUi -= 8;
            else if (atrPct > 0.010m) scoreUi -= 4;
            else scoreUi += 4;

            // 4) Chop zone
            if (smart.IsDangerChopZone) scoreUi -= 18;

            // 5) Smart regime
            switch (smart.SmartType)
            {
                case SmartRegimeType.SmartStrongTrend:
                    scoreUi += 18;
                    break;
                case SmartRegimeType.SmartTrend:
                    scoreUi += 8;
                    break;
                case SmartRegimeType.SmartSqueeze:
                    scoreUi += 10;
                    break;
                case SmartRegimeType.SmartRange:
                    scoreUi += 0;
                    break;
                case SmartRegimeType.SmartChop:
                    scoreUi -= 10;
                    break;
            }

            // 6) TP структура
            if (takeProfits.Count >= 3) scoreUi += 8;
            else if (takeProfits.Count == 2) scoreUi += 4;

            // 7) Confidence
            scoreUi += (int)(smart.Confidence * 20);

            // 8) Clamp
            scoreUi = Math.Clamp(scoreUi, 1, 100);

            if (free <= 0)
            {
                LogMissedTrade(
                    symbol,
                    entryPrice,
                    stopLoss,
                    "NoBalance",
                    free,
                    0,
                    binanceMinNotional,
                    side,
                    takeProfits,
                    baseReg,
                    smart,
                    atr,
                    scoreUi
                );

                await _simulator.SimulateMissedTradeAsync(signal, "NoBalance");
            }

            // =============================================================
            //  FIXED-MARGIN SIZING: берем 35% free как маржу и плечо по правилу
            // =============================================================
            decimal preferredLev = await GetPreferredLeverageAsync(symbol, ct);
            if (preferredLev < 1m) preferredLev = 1m;
            leverage = preferredLev;

            decimal targetMargin = free * 0.35m;
            if (targetMargin <= 0)
            {
                LogMissedTrade(
                    symbol,
                    entryPrice,
                    stopLoss,
                    "NoBalance",
                    free,
                    0,
                    binanceMinNotional,
                    side,
                    takeProfits,
                    baseReg,
                    smart,
                    atr,
                    scoreUi
                );

                await _simulator.SimulateMissedTradeAsync(signal, "NoBalance");
                return 0;
            }

            decimal targetNotional = targetMargin * leverage;

            decimal qty = 0;
            decimal notional = 0;
            decimal requiredMargin = 0;

            for (int i = 0; i < 12; i++)
            {
                qty = Math.Floor((targetNotional / entryPrice) / step) * step;
                if (qty < minQty) qty = minQty;

                notional = qty * entryPrice;
                requiredMargin = notional / leverage;

                if (notional >= binanceMinNotional && requiredMargin <= free)
                    break;

                targetNotional *= 0.85m; // адаптивное уменьшение
            }

            if (qty <= 0 || notional <= 0 || notional < binanceMinNotional || requiredMargin > free)
            {
                LogMissedTrade(
                    symbol,
                    entryPrice,
                    stopLoss,
                    "InsufficientBalanceAfterReduce",
                    free,
                    notional,
                    binanceMinNotional,
                    side,
                    takeProfits,
                    baseReg,
                    smart,
                    atr,
                    scoreUi
                );

                await _simulator.SimulateMissedTradeAsync(signal, "InsufficientBalanceAfterReduce");
                return 0;
            }

            _logger.LogInformation(
                $"[RISK][{symbol}] Fixed-margin sizing → lev={leverage}, qty={qty}, " +
                $"ntn={notional:F4}, margin={requiredMargin:F4}");

            return qty;
        }

        // ====================================================================
        // Preferred leverage per symbol:
        // if maxLev >= 50 → 50; else if maxLev >= 10 → 10; else → maxLev
        // ====================================================================
        private async Task<decimal> GetPreferredLeverageAsync(string symbol, CancellationToken ct)
        {
            try
            {
                using var client = _factory.CreateRestClient();
                var res = await client.UsdFuturesApi.Account.GetBracketsAsync(symbol, null, ct);
                if (!res.Success || res.Data == null || res.Data.Length == 0)
                    return 10m;

                var brackets = res.Data[0].Brackets;
                if (brackets == null || brackets.Length == 0)
                    return 10m;

                decimal maxLev = (decimal)brackets.Max(b => b.InitialLeverage);

                if (maxLev >= 50m) return 50m;
                if (maxLev >= 10m) return 10m;
                return Math.Max(1m, maxLev);
            }
            catch
            {
                return 10m;
            }
        }

        // ====================================================================
        // AI LEVERAGE MULTIPLIER (оставлен для совместимости, сейчас не влияет)
        // ====================================================================
        private async Task<decimal> GetAiLeverageMultiplierAsync(string symbol, CancellationToken ct)
        {
            try
            {
                var klines = await _marketData.GetKlines(symbol, KlineInterval.FifteenMinutes, 200);
                if (klines == null || klines.Count < 30)
                    return 1.0m;

                decimal m = _aiLeverage.Calculate(symbol, KlineInterval.FifteenMinutes, klines);
                return m > 0 ? m : 1.0m;
            }
            catch
            {
                return 1.0m;
            }
        }

        // ====================================================================
        // LOG MISSED TRADES (FULL VERSION WITH ATR / VOL / SLOPE / CONF)
        // ====================================================================
        private void LogMissedTrade(
            string symbol,
            decimal entry,
            decimal sl,
            string reason,
            decimal freeBalance,
            decimal attemptNotional,
            decimal requiredMinNotional,
            SignalSide side,
            List<decimal> takeProfits,
            MarketRegimeResult baseReg,
            SmartRegimeInfo smart,
            decimal atr,
            int scoreUi)
        {
            try
            {
                var record = new
                {
                    symbol = symbol,
                    time = DateTime.UtcNow,

                    entry = entry,
                    stopLoss = sl,
                    side = side.ToString(),
                    takeProfits = takeProfits,

                    reason,
                    freeBalance,
                    attemptNotional,
                    requiredMinNotional,

                    // Market regime
                    atr = atr,
                    slope = baseReg.TrendSlopePercent,
                    vol = baseReg.VolatilityPercent,
                    deviation = baseReg.DeviationScore,
                    regime = baseReg.Regime.ToString(),

                    // Smart regime
                    confidence = (int)(smart.Confidence * 100),
                    smartType = smart.SmartType.ToString(),
                    score = scoreUi
                };

                List<object> list;

                if (File.Exists(MissedTradesPath))
                {
                    var json = File.ReadAllText(MissedTradesPath);
                    list = JsonSerializer.Deserialize<List<object>>(json) ?? new();
                }
                else
                {
                    list = new();
                }

                list.Add(record);

                File.WriteAllText(
                    MissedTradesPath,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })
                );
            }
            catch
            {
                // ignore logging errors
            }
        }
    }
}
