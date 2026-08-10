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

       private static readonly string MissedTradesPath =  Path.Combine(AppContext.BaseDirectory, "missed_trades.json");
       // private static readonly string  MissedTradesPath = @"C:\Users\karap\source\repos\VertexAutoTradeBinance8\VertexAutoTradeBinance AI v5.0 Full Autonomous Mode\bin\Debug\net8.0\missed_trades.json";
    

        public decimal LastBalanceUsdt { get; private set; }

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo,
            TradingOptions options,
            BinanceClientFactory factory,
            MarketDataService marketData,
            AiLeverageService aiLeverage, AiMarketRegimeService marketRegimeService, SmartRegimeService smartRegime, SimulatedTradeService simulator)
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
        // SAFE QTY v7.7 (QUANT-REALTIME FINAL)
        // ====================================================================
        public async Task<decimal> CalculateSafeQty(
       TradeSignal signal,          // добавлен параметр signal
       string symbol,
       decimal entryPrice,
       decimal stopLoss,
       decimal riskMultiplier,
       decimal safetyRiskMultiplier,
       decimal leverage,
       SignalSide side,
       List<decimal> takeProfits,
       CancellationToken ct)
        {
            if (entryPrice <= 0 || stopLoss <= 0)
                return 0;

            decimal slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0)
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

            // for UI
            LastBalanceUsdt = free;

            var klines = await _marketData.GetKlines(symbol, KlineInterval.FiveMinutes, 200);
            var baseReg = _marketRegimeService.DetectRegime(symbol, KlineInterval.FiveMinutes, klines);
            var smart = _smartRegime.Evaluate(symbol, KlineInterval.FiveMinutes, klines);

            decimal atr = baseReg.VolatilityPercent * klines.Last().ClosePrice;

            // ===============================
            //  AI Opportunity Score v2.0
            // ===============================

            int scoreUi = 50; // базовая середина — нейтральный рынок

            // === 1) Трендовый бонус (Slope) ===
            decimal slope = Math.Abs(baseReg.TrendSlopePercent);

            if (slope > 0.004m) scoreUi += 22;       // 0.40% — сильнейший тренд
            else if (slope > 0.003m) scoreUi += 15;  // 0.30%
            else if (slope > 0.002m) scoreUi += 8;   // 0.20%
            else if (slope > 0.001m) scoreUi += 3;   // 0.10%

            // === 2) Волатильность (VolatilityPercent) ===
            // Высокая волатильность = шум, хаос → минус score
            decimal vol = baseReg.VolatilityPercent;

            if (vol > 0.025m) scoreUi -= 22;        // 2.5% — ад
            else if (vol > 0.015m) scoreUi -= 15;   // 1.5% — дикий рынок
            else if (vol > 0.010m) scoreUi -= 8;    // шумный
            else if (vol > 0.006m) scoreUi -= 3;    // легкий шум

            // === 3) ATR нагрузка (atr / price) ===
            // ATR% показывает “нервность” рынка
            decimal atrPct = atr / entryPrice;

            if (atrPct > 0.025m) scoreUi -= 15;  // ATR > 2.5%
            else if (atrPct > 0.015m) scoreUi -= 8;
            else if (atrPct > 0.010m) scoreUi -= 4;
            else scoreUi += 4;                   // маленький ATR = стабильность рынка

            // === 4) Chop-zone — опасный рынок, но НЕ смертельно ===
            if (smart.IsDangerChopZone)
                scoreUi -= 18;

            // === 5) Smart regime bonuses ===
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

            // === 6) Take Profit structure ===
            if (takeProfits.Count >= 3) scoreUi += 8;
            else if (takeProfits.Count == 2) scoreUi += 4;

            // === 7) AI Confidence ===
            scoreUi += (int)(smart.Confidence * 20); // 0..20

            // === 8) Окончательная нормализация ===
            scoreUi = Math.Clamp(scoreUi, 1, 100);

            if (free <= 0)
            {
                LogMissedTrade(
                    symbol,
                    entryPrice,
                    stopLoss,
                    "NoBalance",
                    free,
                    0,                          // attemptNotional
                    binanceMinNotional,         // requiredMinNotional
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

            // BASE RISK
            decimal baseRiskPercent = _options.BaseRiskPercent > 0
                ? _options.BaseRiskPercent
                : 0.03m;

            // AI LEVERAGE FACTOR
            decimal aiLevMult = await GetAiLeverageMultiplierAsync(symbol, ct);

            // FINAL MULTIPLIER
            decimal finalRisk = riskMultiplier * safetyRiskMultiplier * aiLevMult;
            finalRisk = Math.Clamp(finalRisk, 0.3m, 2.7m);

            decimal maxRisk = free * baseRiskPercent * finalRisk;
            if (maxRisk < 1m) maxRisk = 1m;
            if (maxRisk > free * 0.20m) maxRisk = free * 0.20m;

            decimal qty = maxRisk / slDist;
            if (leverage > 0) qty *= leverage;

            qty = Math.Floor(qty / step) * step;
            if (qty < minQty) qty = minQty;

            decimal notional = qty * entryPrice;

            // =====================
            // SIGNAL STRENGTH LOGIC
            // =====================

            decimal score = riskMultiplier * safetyRiskMultiplier;
            bool strong = score >= 1.30m;
            bool weak = score < 0.80m;

            if (weak)
            {
                LogMissedTrade(
                    symbol,
                    entryPrice,
                    stopLoss,
                    "WeakSignalRejected",
                    free,
                    0, // attemptNotional
                    binanceMinNotional, // requiredMinNotional
                    side,
                    takeProfits,
                    baseReg,
                    smart,
                    atr,
                    scoreUi
                );

                _logger.LogInformation($"[RISK][{symbol}] Weak signal detected — lowering position size.");

                // уменьшаем риск, но НЕ отбрасываем сделку (фактически maxRisk уже использован,
                // строка ниже не влияет на qty/notional, оставляем как комментарий намерения)
                maxRisk *= 0.35m;
            }

            // =============================================================
            //  BOOST + ADAPTIVE REDUCE (v7.5)
            // =============================================================
            if (notional < binanceMinNotional)
            {
                _logger.LogInformation($"[RISK][{symbol}] Below minNotional → boosting or reducing.");

                decimal targetNotional = weak
                    ? binanceMinNotional          // слабый сигнал → минимум
                    : binanceMinNotional * 1.4m;  // сильный сигнал → усилить

                // ограничение реальным балансом
                decimal maxAllowed = free * leverage;
                if (targetNotional > maxAllowed)
                    targetNotional = maxAllowed;

                // первая попытка
                qty = Math.Floor((targetNotional / entryPrice) / step) * step;
                if (qty < minQty)
                    qty = minQty;

                notional = qty * entryPrice;

                // если всё нормально — продолжаем
                if (notional >= binanceMinNotional)
                    goto BOOST_OK;

                // иначе → уменьшаем позицию до допустимых значений
                for (int i = 0; i < 12; i++)   // до 12 попыток
                {
                    targetNotional *= 0.85m;  // уменьшаем на 15%

                    qty = Math.Floor((targetNotional / entryPrice) / step) * step;
                    if (qty < minQty)
                        qty = minQty;

                    notional = qty * entryPrice;

                    if (notional >= binanceMinNotional)
                        goto BOOST_OK;
                }

                // если даже после 12 уменьшений не прошли → пропускаем
                LogMissedTrade(
                    symbol, entryPrice, stopLoss, "MinNotionalAfterAdaptiveReduce",
                    free, notional, binanceMinNotional,
                    side, takeProfits, baseReg, smart, atr, scoreUi
                );

                await _simulator.SimulateMissedTradeAsync(signal, "MinNotionalAfterAdaptiveReduce");
                return 0;
            }

        BOOST_OK:
            _logger.LogInformation($"[RISK][{symbol}] Boost/Reduce OK → qty={qty}, notional={notional:F4}");

            if (qty <= 0 || notional <= 0)
            {
                LogMissedTrade(
                    symbol,
                    entryPrice,
                    stopLoss,
                    "QtyZeroAfterAdjust",
                    free,
                    0,                          // attemptNotional
                    binanceMinNotional,         // requiredMinNotional
                    side,
                    takeProfits,
                    baseReg,
                    smart,
                    atr,
                    scoreUi
                );

                await _simulator.SimulateMissedTradeAsync(signal, "QtyZeroAfterAdjust");
                return 0;
            }

            // 🔥 ДОБАВЛЕНО: ФИНАЛЬНАЯ ПРОВЕРКА МАРЖИ
            if (leverage <= 0)
                leverage = 1m;

            decimal requiredMargin = notional / leverage;

            // =============================================================
            //  SMART MARGIN ADJUSTMENT v7.5 — уменьшаем позицию, но НЕ отбрасываем
            // =============================================================
            if (requiredMargin > free)
            {
                _logger.LogInformation($"[RISK][{symbol}] Margin too high → reducing position.");

                // 1) максимальный возможный notional
                decimal maxNotional = free * leverage * 0.97m;
                if (maxNotional <= 0)
                {
                    LogMissedTrade(
                        symbol, entryPrice, stopLoss, "NoMargin",
                        free, 0,
                        binanceMinNotional,
                        side, takeProfits, baseReg, smart, atr, scoreUi
                    );

                    await _simulator.SimulateMissedTradeAsync(signal, "NoMargin");
                    return 0;
                }

                // 2) Уменьшаем notional пошагово, пока не пройдём фильтры
                for (int i = 0; i < 12; i++)  // до 12 уменьшений (достаточно)
                {
                    qty = Math.Floor((maxNotional / entryPrice) / step) * step;

                    if (qty >= minQty)
                    {
                        notional = qty * entryPrice;
                        requiredMargin = notional / leverage;

                        if (notional >= binanceMinNotional && requiredMargin <= free)
                        {
                            _logger.LogInformation(
                                $"[RISK][{symbol}] Reduced position OK → qty={qty}, ntn={notional:F2}, margin={requiredMargin:F2}");
                            break;
                        }
                    }

                    // уменьшаем позицию на 15%
                    maxNotional *= 0.85m;
                }

                // 3) После цикла, если всё равно не проходит — вернуть 0
                if (notional < binanceMinNotional || requiredMargin > free || qty < minQty)
                {
                    LogMissedTrade(
                        symbol, entryPrice, stopLoss, "InsufficientBalanceAfterReduce",
                        free, 0,
                        binanceMinNotional,
                        side, takeProfits, baseReg, smart, atr, scoreUi
                    );

                    await _simulator.SimulateMissedTradeAsync(signal, "InsufficientBalanceAfterReduce");
                    return 0;
                }
            }

            // Для недостаточного баланса ПОСЛЕ всех корректировок — только предупреждение
            if (notional < binanceMinNotional)
            {
                _logger.LogWarning(
                    $"[RISK][{symbol}] Warning: notional < minNotional AFTER full reduce. " +
                    $"notional={notional:F4}, required={binanceMinNotional:F4}");

                // НЕ return!!!
                // Просто позволяем торговать минимально допустимой позицией.
            }
            // ===== FINAL HARD SAFETY =====
            if (qty <= 0)
            {
                qty = minQty;
                notional = qty * entryPrice;
            }

            // если даже minQty не проходит — честно выходим
            if (notional < binanceMinNotional && free < (binanceMinNotional / Math.Max(leverage, 1)))
            {
                LogMissedTrade(
                    symbol, entryPrice, stopLoss, "FinalSafetyQtyZero",
                    free, notional, binanceMinNotional,
                    side, takeProfits, baseReg, smart, atr, scoreUi
                );

                await _simulator.SimulateMissedTradeAsync(signal, "FinalSafetyQtyZero");
                return 0;
            }

            return qty;
        }



        // ====================================================================
        // AI LEVERAGE MULTIPLIER
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
    decimal atr, int scoreUi)


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

                    // === Market regime ===
                    atr = atr,
                    slope = baseReg.TrendSlopePercent,
                    vol = baseReg.VolatilityPercent,
                    deviation = baseReg.DeviationScore,
                    regime = baseReg.Regime.ToString(),

                    // === Smart regime ===
                    confidence = (int)(smart.Confidence * 100),
                    smartType = smart.SmartType.ToString(),
                    // === NEW ===
                    score = scoreUi
                };

                List<object> list;

                if (File.Exists(MissedTradesPath))
                {
                    var json = File.ReadAllText(MissedTradesPath);
                    list = JsonSerializer.Deserialize<List<object>>(json) ?? new();
                }
                else list = new();

                list.Add(record);

                File.WriteAllText(MissedTradesPath,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }


    }
}
