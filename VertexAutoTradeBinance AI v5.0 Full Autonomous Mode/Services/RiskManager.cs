using Binance.Net.Enums;
using System.Text.Json;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// RiskManager v8 — размер позиции от риска.
    ///
    /// Исправленные дефекты v7.7:
    ///  1) BaseRiskPercent=1.0 трактовался как ДОЛЯ (100% баланса) → риск ограничивался
    ///     только аварийным cap'ом 20% от free. Теперь 1.0 = 1%.
    ///  2) qty умножался на плечо: qty = risk/slDist * leverage → риск ×плечо. Убрано.
    ///     Плечо влияет только на требуемую маржу, не на риск.
    ///  3) «Weak signal» уменьшал maxRisk ПОСЛЕ расчёта qty — не работало. Теперь до.
    ///  4) Буст до minNotional×1.4 без учёта риска. Теперь: если minNotional требует
    ///     риск больше целевого × MaxRiskOvershootForMinNotional — сделка пропускается.
    ///  5) «notional &lt; minNotional после reduce — всё равно торгуем» → биржа отклоняла ордер.
    ///  6) missed_trades.json рос бесконечно и переписывался целиком на каждую запись.
    /// </summary>
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;
        private readonly SymbolInfoService _symbolInfo;
        private readonly TradingOptions _options;
        private readonly MarketDataService _marketData;
        private readonly AiLeverageService _aiLeverage;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly SmartRegimeService _smartRegime;
        private readonly SimulatedTradeService _simulator;
        private readonly AccountStateCache _account;

        private static readonly object MissedLock = new();
        private const int MaxMissedRecords = 3000;

        public decimal LastBalanceUsdt { get; private set; }

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo,
            TradingOptions options,
            MarketDataService marketData,
            AiLeverageService aiLeverage,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            SimulatedTradeService simulator,
            AccountStateCache account)
        {
            _logger = logger;
            _symbolInfo = symbolInfo;
            _options = options;
            _marketData = marketData;
            _aiLeverage = aiLeverage;
            _marketRegimeService = marketRegimeService;
            _smartRegime = smartRegime;
            _simulator = simulator;
            _account = account;
        }

        public async Task<decimal> CalculateSafeQty(
            TradeSignal signal,
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
            if (entryPrice <= 0 || stopLoss <= 0) return 0;

            decimal slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0) return 0;

            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal step = f.step > 0 ? f.step : 0.001m;
            decimal minQty = f.minQty > 0 ? f.minQty : step;
            decimal minNotional = f.minNotional > 0 ? f.minNotional : Math.Max(5m, _options.MinNotional);

            if (leverage <= 0) leverage = 1m;

            // ---------- BALANCE ----------
            var (wallet, available) = await _account.GetBalancesAsync(ct, forceRefresh: true);
            LastBalanceUsdt = available;
            decimal equityBase = wallet > 0 ? wallet : available;

            if (equityBase <= 0 || available <= 0)
                return await Missed(signal, "NoBalance", entryPrice, stopLoss, available, 0, minNotional, side, takeProfits);

            // ---------- RISK % ----------
            decimal basePct = Math.Clamp(_options.BaseRiskPercent, 0.05m, 5m) / 100m;
            decimal maxPct = Math.Clamp(_options.MaxRiskPercent > 0 ? _options.MaxRiskPercent : 1.5m, 0.05m, 5m) / 100m;

            decimal aiLevMult = await GetAiLeverageMultiplierAsync(symbol, ct);
            decimal mult = Math.Clamp(riskMultiplier * safetyRiskMultiplier * aiLevMult, 0.30m, 1.60m);

            // Слабый сигнал — уменьшаем риск ДО расчёта объёма
            decimal strength = riskMultiplier * safetyRiskMultiplier;
            if (strength < 0.80m)
            {
                mult *= 0.5m;
                _logger.LogInformation("[RISK][{symbol}] weak signal ({s:F2}) → risk ×0.5", symbol, strength);
            }

            decimal riskPct = Math.Min(basePct * mult, maxPct);
            decimal riskUsd = equityBase * riskPct;

            // ---------- QTY FROM RISK ----------
            decimal qty = SymbolInfoService.FloorToStep(riskUsd / slDist, step);

            // ---------- MARGIN CAP ----------
            // 90% доступной маржи × плечо — оставляем запас на комиссии/движение
            decimal maxNotionalByMargin = available * leverage * 0.90m;
            decimal maxQtyByMargin = SymbolInfoService.FloorToStep(maxNotionalByMargin / entryPrice, step);
            if (qty > maxQtyByMargin)
            {
                _logger.LogInformation("[RISK][{symbol}] margin cap: qty {q} → {m}", symbol, qty, maxQtyByMargin);
                qty = maxQtyByMargin;
            }

            // ---------- EXCHANGE MINIMUMS ----------
            decimal notional = qty * entryPrice;
            if (qty < minQty || notional < minNotional)
            {
                decimal needQty = Math.Max(minQty, Math.Ceiling(minNotional * 1.02m / entryPrice / step) * step);
                decimal riskNeeded = needQty * slDist;
                decimal allowed = riskUsd * Math.Max(1m, _options.MaxRiskOvershootForMinNotional);

                if (riskNeeded > allowed || needQty > maxQtyByMargin)
                {
                    _logger.LogInformation(
                        "[RISK][{symbol}] SKIP: min order needs risk ${need:F2} > allowed ${allowed:F2} (or margin)",
                        symbol, riskNeeded, allowed);
                    return await Missed(signal, "MinNotionalExceedsRisk", entryPrice, stopLoss, available, notional, minNotional, side, takeProfits);
                }

                qty = needQty;
                notional = qty * entryPrice;
            }

            if (qty <= 0)
                return await Missed(signal, "QtyZero", entryPrice, stopLoss, available, notional, minNotional, side, takeProfits);

            _logger.LogInformation(
                "[RISK][{symbol}] qty={qty} notional={ntn:F2} risk=${risk:F2} ({pct:P2} of {bal:F2}) lev={lev} slDist={sl:P2}",
                symbol, qty, notional, qty * slDist, (qty * slDist) / equityBase, equityBase, leverage, slDist / entryPrice);

            return qty;
        }

        private async Task<decimal> GetAiLeverageMultiplierAsync(string symbol, CancellationToken ct)
        {
            try
            {
                var klines = await _marketData.GetKlines(symbol, KlineInterval.FifteenMinutes, 200);
                if (klines == null || klines.Count < 30) return 1.0m;
                decimal m = _aiLeverage.Calculate(symbol, KlineInterval.FifteenMinutes, klines);
                return m > 0 ? Math.Clamp(m, 0.5m, 1.3m) : 1.0m;
            }
            catch
            {
                return 1.0m;
            }
        }

        private async Task<decimal> Missed(
            TradeSignal signal, string reason, decimal entry, decimal sl, decimal free,
            decimal attemptNotional, decimal minNotional, SignalSide side, List<decimal> tps)
        {
            try
            {
                var klines = await _marketData.GetKlines(signal.Symbol, KlineInterval.FiveMinutes, 200);
                if (klines.Count > 30)
                {
                    var baseReg = _marketRegimeService.DetectRegime(signal.Symbol, KlineInterval.FiveMinutes, klines);
                    var smart = _smartRegime.Evaluate(signal.Symbol, KlineInterval.FiveMinutes, klines);
                    LogMissedTrade(signal.Symbol, entry, sl, reason, free, attemptNotional, minNotional, side, tps,
                        baseReg, smart, baseReg.VolatilityPercent * klines[^1].ClosePrice,
                        (int)Math.Clamp(smart.Confidence * 100m, 1, 100));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[RISK] missed-trade log failed");
            }

            await _simulator.SimulateMissedTradeAsync(signal, reason);
            return 0m;
        }

        private void LogMissedTrade(
            string symbol, decimal entry, decimal sl, string reason, decimal freeBalance,
            decimal attemptNotional, decimal requiredMinNotional, SignalSide side, List<decimal> takeProfits,
            MarketRegimeResult baseReg, SmartRegimeInfo smart, decimal atr, int scoreUi)
        {
            var record = new
            {
                symbol,
                time = DateTime.UtcNow,
                entry,
                stopLoss = sl,
                side = side.ToString(),
                takeProfits,
                reason,
                freeBalance,
                attemptNotional,
                requiredMinNotional,
                atr,
                slope = baseReg.TrendSlopePercent,
                vol = baseReg.VolatilityPercent,
                deviation = baseReg.DeviationScore,
                regime = baseReg.Regime.ToString(),
                confidence = (int)(smart.Confidence * 100),
                smartType = smart.SmartType.ToString(),
                score = scoreUi
            };

            lock (MissedLock)
            {
                var path = VertexPaths.MissedTrades;
                List<JsonElement> list;
                try
                {
                    var json = VertexPaths.ReadAllTextShared(path);
                    list = string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new();
                }
                catch { list = new(); }

                list.Add(JsonSerializer.SerializeToElement(record));
                if (list.Count > MaxMissedRecords)
                    list = list.Skip(list.Count - MaxMissedRecords).ToList();

                VertexPaths.WriteAllTextAtomic(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
}
