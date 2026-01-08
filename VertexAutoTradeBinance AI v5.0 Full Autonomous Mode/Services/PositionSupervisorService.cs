using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Diagnostics;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// PositionSupervisorService v8.2 PRO (Production)
    ///
    /// v8.2 FIXES (раз и навсегда):
    /// 0) EARLY TP (Partial close) 35% на +0.9 ATR → чтобы прибыль фиксировалась ДО откатов
    /// 1) SL -> BE (безубыток + буфер) на +1.2 ATR → чтобы после ранней прибыли не ловить минус
    /// 2) Анти-спам: partial/BE выполняются один раз на "позицию" (entry+qty+side)
    /// 3) UpdateSL: без reduceOnly (и без зависания на -1106), WorkingType.Mark используем осторожно
    /// 4) Если Binance вернёт -4120 → ставим/обновляем через RAW /fapi/v1/algoOrder (CONDITIONAL)
    /// 5) NEW 15-15-12-2025 -КОНЦЕПЦИЯ: PROTECT → PROBE → CONFIRM → SCALE PROBE — умный тест обратного движения(ключ)

    /// </summary>
    public class PositionSupervisorService
    {
        private readonly ILogger<PositionSupervisorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly AiStopLossOptimizer _slOptimizer;
        private readonly AiSelfLearningService _aiLearning;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _regime;
        private readonly ManualPositionHandler _manualHandler;
        private readonly BinanceAlgoOrderRaw _algoRaw;
        private readonly LiquidityGuardService _liquidityGuard;
        private readonly IOrderDispatcher _dispatcher;
        private MarketRegime _regimeNow;

        // === Anti-spam guards for EarlyTP / BE-move ===
        private readonly ConcurrentDictionary<string, long> _earlyTpDone = new();   // key -> unixMs
        private readonly ConcurrentDictionary<string, long> _beMoved = new();      // key -> unixMs
        private readonly ConcurrentDictionary<string, decimal> _restoredEntries = new();
        // === Harvest block after partial close ===
        private readonly ConcurrentDictionary<string, long> _recentPartialClose = new();

        private readonly EngineStateSnapshotService _stateSvc;

        private readonly SmartRegimeService _smartRegime;
        private readonly ReverseProbeEngine _reverseProbe;

        // === Attach idempotency (existing position attach) ===
        private readonly ConcurrentDictionary<string, bool> _attached = new();

        // === Funding risk guard ===
        private readonly ConcurrentDictionary<string, decimal> _fundingCost = new();

        // somewhere in class (field). If you already have it - DO NOT duplicate.
        private readonly ConcurrentDictionary<string, long> _fundingLastIncomeTs = new();

        // symbol -> cumulative funding (signed)
        private readonly ConcurrentDictionary<string, long> _fundingLastSync = new();   // symbol -> unixMs

        // === HEDGE COOLDOWN (GLOBAL) ===
        private static readonly ConcurrentDictionary<string, DateTime> _hedgeCooldown
            = new();
        private static readonly TimeSpan HedgeCooldownPeriod = TimeSpan.FromMinutes(10);

        // === Position fingerprint for anti-spam (stable qty) ===
        private readonly ConcurrentDictionary<string, decimal> _posBaseQty = new();   // key: symbol|side|entry -> baseQty
        private readonly ConcurrentDictionary<string, decimal> _posBaseEntry = new(); // key: symbol|side -> entry (latest stable)

        private readonly OpenPositionSymbolTracker _openPos;


        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiStopLossOptimizer slOptimizer,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ManualPositionHandler manualHandler,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            LiquidityGuardService liquidityGuard,
            IOrderDispatcher dispatcher, EngineStateSnapshotService stateSvc,
            SmartRegimeService smartRegime,
            ReverseProbeEngine reverseProbe, OpenPositionSymbolTracker openPos)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _slOptimizer = slOptimizer;
            _aiLearning = aiLearning;
            _marketData = marketData;
            _regime = regime;
            _manualHandler = manualHandler;

            _regimeNow = MarketRegime.Range;

            _algoRaw = new BinanceAlgoOrderRaw(cfg, httpFactory, _logger);
            _liquidityGuard = liquidityGuard;
            _dispatcher = dispatcher;
            _stateSvc = stateSvc;
            _smartRegime = smartRegime;
            _reverseProbe = reverseProbe;
            _openPos = openPos;


        }

        private bool IsHedgeOnCooldown(string symbol)
        {
            if (_hedgeCooldown.TryGetValue(symbol, out var until))
                return DateTime.UtcNow < until;

            return false;
        }

        private string BuildPosEntryKey(string symbol, PositionSide side, decimal entry)
        {
            string E(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            return $"{symbol}|{side}|e={E(entry)}";
        }

        private decimal GetOrSetBaseQty(string symbol, PositionSide side, decimal entry, decimal qtyAbs)
        {
            var ek = BuildPosEntryKey(symbol, side, entry);

            // baseQty is set once per entry; if position is scaled up, allow increasing base
            return _posBaseQty.AddOrUpdate(
                ek,
                qtyAbs,
                (_, prev) => qtyAbs > prev ? qtyAbs : prev
            );
        }

        private void ClearBaseQty(string symbol, PositionSide side)
        {
            // remove all entry-based keys for this symbol+side
            foreach (var k in _posBaseQty.Keys.Where(k => k.StartsWith($"{symbol}|{side}|", StringComparison.OrdinalIgnoreCase)))
                _posBaseQty.TryRemove(k, out _);

            _posBaseEntry.TryRemove($"{symbol}|{side}", out _);
        }


        private void MarkHedgeCooldown(string symbol)
        {
            _hedgeCooldown[symbol] = DateTime.UtcNow.Add(HedgeCooldownPeriod);
        }

        private EngineState _engineState => _stateSvc.State;
        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // 0) MANUAL → виртуальный сигнал
            if (lastSignal == null)
            {
                var manualSignal = await _manualHandler.DetectManualAsync(client, symbol, ct);
                if (manualSignal != null)
                {
                    lastSignal = manualSignal;
                    _logger.LogWarning("[MANUAL][{symbol}] Virtual signal injected", symbol);
                }
            }

            // 1) Позиции (с ретраями)
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (!posInfo.Success || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR] No positions {symbol}", symbol);
                return;
            }

                    var positions = posInfo.Data
            .Where(p => p.Symbol == symbol && p.Quantity != 0m)
            .ToList();

                    var positionSnapshot = positions.ToDictionary(
            p => p.PositionSide,
            p => p);


            var longPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Short);


            var hasLong = longPos != null && longPos.Quantity != 0m;
            var hasShort = shortPos != null && shortPos.Quantity != 0m;

            if (!hasLong && !hasShort)
            {
                // 🔥 FUNDING RESET ONLY WHEN SYMBOL HAS NO POSITIONS AT ALL
                _fundingCost.TryRemove(symbol, out _);
                _fundingLastIncomeTs.TryRemove(symbol, out _);

                _logger.LogInformation("[SUPERVISOR] {symbol}: no positions (funding reset)", symbol);
                return;
            }
            // ✅ N4: keep tracker synced (idempotent)
            if (hasLong) _openPos.MarkOpen(symbol);
            if (hasShort) _openPos.MarkOpen(symbol);

            // ===============================
            // FUNDING COST REFRESH (real, not fiction)
            // ===============================
            await RefreshFundingCostAsync(client, symbol, ct);

           

            // 2) Ордера
            var openOrders = await LoadOrdersAsync(client, symbol);

            // 3) Режим + klines (1m)
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;
            try
            {
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 160);
                var rr = _regime.DetectRegime(symbol, KlineInterval.OneMinute, klines1m);
                if (rr != null) _regimeNow = rr.Regime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] Klines load error {symbol}", symbol);
            }


            // =======================================
            // CONFIRM OR KILL HEDGE (v8.2 PRO)
            // =======================================
            if (hasLong && hasShort && klines1m != null && klines1m.Count >= 30)
            {
                await ConfirmOrKillHedgeAsync(
                    client,
                    symbol,
                    longPos!,
                    shortPos!,
                    klines1m,
                    ct);
            }


            SmartRegimeInfo? smart1m = null;
            decimal atr14_1m = 0m;

            try
            {
                if (klines1m != null && klines1m.Count >= 50)
                {
                    smart1m = _smartRegime.Evaluate(symbol, KlineInterval.OneMinute, klines1m);
                    atr14_1m = _marketData.CalculateAtr(klines1m, 14);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] SmartRegime/Evaluate failed {symbol}", symbol);
            }

            // 🔁 PROBE должен быть ОДИН раз на тик supervise, до HandleSideAsync
            if (smart1m != null && atr14_1m > 0)
            {
                await TryReverseProbeAsync(client, symbol, longPos, shortPos, smart1m, atr14_1m, ct);

            }



            // 4) Обработка сторон
            if (hasLong)
                await HandleSideAsync(client, symbol, PositionSide.Long, longPos!, openOrders, lastSignal, klines1m, ct);

            if (hasShort)
                await HandleSideAsync(client, symbol, PositionSide.Short, shortPos!, openOrders, lastSignal, klines1m, ct);
        }


        private async Task ConfirmOrKillHedgeAsync(
        BinanceRestClient client,
        string symbol,
        BinancePositionDetailsUsdt longPos,
        BinancePositionDetailsUsdt shortPos,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        CancellationToken ct)
        {
            // ⛔ HARD BLOCK — NO HEDGE IN SQUEEZE
            if (_regimeNow == MarketRegime.Squeeze)
            {
                _logger.LogWarning(
                    "[HEDGE][{symbol}] SKIP hedge decision → SQUEEZE regime",
                    symbol);

                // 🧠 AI LEARNING — WHY HEDGE WAS BLOCKED
                _aiLearning.RecordMarketStateTriggered(
                    reason: "HEDGE_BLOCK_SQUEEZE",
                    symbol: symbol,
                    timeframe: "HEDGE",
                    regime: MarketRegime.Squeeze,
                    slope: 0m,
                    volatility: 0m,
                    atr: 0m,
                    confidence: 0.90m
                );

                return;
            }

            // ⛔ HARD NO-HEDGE DURING LIQUIDITY DANGER
            if (_liquidityGuard.LastDanger?.Block == true)
            {
                _logger.LogWarning(
                    "[HEDGE][{symbol}] SKIP hedge decision → liquidity danger {reason}",
                    symbol, _liquidityGuard.LastDanger?.Reason);
                return;
            }

            // ⛔ HEDGE COOLDOWN GUARD
            if (IsHedgeOnCooldown(symbol))
            {
                _logger.LogDebug(
                    "[HEDGE][{symbol}] skipped — cooldown active",
                    symbol);
                return;
            }

            // === Safety ===
            if (longPos.Quantity == 0 || shortPos.Quantity == 0)
                return;

            // === Реальный PnL ===
            decimal longPnl = longPos.UnrealizedPnl;
            decimal shortPnl = shortPos.UnrealizedPnl;

            // === Кто хуже ===
            var loser =
                longPnl < shortPnl ? PositionSide.Long : PositionSide.Short;

            var winner =
                loser == PositionSide.Long ? PositionSide.Short : PositionSide.Long;

            var loserPos = loser == PositionSide.Long ? longPos : shortPos;
            var winnerPos = winner == PositionSide.Long ? longPos : shortPos;

            // === Минимальная разница, чтобы не дёргаться ===
            if (Math.Abs(longPnl - shortPnl) < 3m)
                return;

            // === ATR ===
            var atr = _marketData.CalculateAtr(klines, 14);
            if (atr <= 0)
                return;

            var last = klines[^1].ClosePrice;

            bool winnerConfirmed =
                winner == PositionSide.Long
                    ? last > winnerPos.EntryPrice + atr * 0.4m
                    : last < winnerPos.EntryPrice - atr * 0.4m;

            if (!winnerConfirmed)
                return;

            //===============3) Production-патч: “Smart Hedge Kill Gate” (точечно, без ломки архитектуры)======================================================
            /*
             * Что это даёт:

            Если общий hedge ещё “живой” (netPnL нормальный) — не фиксируем большой минус

            Если фиксировать минус придётся, то он ограничен “бюджетом отдачи” от уже заработанного bucket

            Если реально опасно (hardLoss / fundingPressure) — режем без разговоров
             * */
            // ===============================
            // SMART HEDGE-KILL GATES (PRO)
            // цель: не отдавать весь общий профит и не резать loser, если netPnL еще терпимый
            // ===============================

            // 1) Net uPnL on symbol (hedge as a whole)
            decimal netPnl = longPnl + shortPnl; // sum uPnL of both legs

            // 2) Pull realized bucket (profit reserve from harvests)
            var sKey = EngineState.Key(symbol);
            var st = _engineState.Symbols.GetOrAdd(sKey, _ => new SymbolState());
            decimal bucket = st.RealizedPnlBucketUsd;

            // 3) Hard loss threshold for emergency (always allow kill if too deep)
            const decimal hardLoserUsd = 18m;      // absolute pain for 1 symbol (tune)
            const decimal hardNetUsd = -10m;       // if net hedge is already negative -> allow cut faster

            // 4) Giveback budget: allow to "pay" only part of earned bucket
            // If bucket == 0 => allow small giveback only (avoid "10 earned -> 10 lost")
            decimal givebackBudget = bucket > 0m ? bucket * 0.35m : 4m; // allow max 35% of bucket, else $4 cap

            // loserPnl is negative usually
            decimal loserPnl = loser == PositionSide.Long ? longPnl : shortPnl;
            decimal loserLossAbs = Math.Abs(Math.Min(0m, loserPnl)); // only negative part

            // === Gate A: if hedge netPnL is still positive enough, don't finalize loser ===
            // Example: net still >= +3$ => don't lock loss; let system work (trail/harvest/BE)
            if (netPnl >= 3m && !IsFundingRiskExceeded(symbol))
            {
                _logger.LogInformation(
                    "[HEDGE-KILL][{symbol}] SKIP → netPnL still ok net={net:F2} loser={loser} loserPnl={lp:F2} bucket={bucket:F2}",
                    symbol, netPnl, loser, loserPnl, bucket);
                return;
            }

            // === Gate B: giveback limiter ===
            // if cutting loser would consume too much of earned bucket -> skip, unless hard loss or funding pressure
            bool hardLoss = loserLossAbs >= hardLoserUsd || netPnl <= hardNetUsd;
            bool fundingPressure = IsFundingRiskExceeded(symbol);

            if (!hardLoss && !fundingPressure && loserLossAbs > givebackBudget)
            {
                _logger.LogWarning(
                    "[HEDGE-KILL][{symbol}] SKIP → giveback limit. loserLoss={loss:F2} budget={budget:F2} net={net:F2} bucket={bucket:F2}",
                    symbol, loserLossAbs, givebackBudget, netPnl, bucket);
                return;
            }



            //==================================================================================================================================




            // === FUNDING GUARD ===
            if (IsFundingRiskExceeded(symbol))
            {
                _logger.LogWarning(
                    "[HEDGE-KILL][{symbol}] funding pressure → force close loser {side}",
                    symbol, loser);
            }

            _logger.LogWarning(
                "[HEDGE-KILL][{symbol}] CLOSE LOSER {loser} pnl={pnl:F2} | KEEP {winner}",
                symbol, loser, loser == PositionSide.Long ? longPnl : shortPnl, winner);


            // 🧠 AI LEARNING — WHY HEDGE WAS KILLED
            _aiLearning.RecordMarketStateTriggered(
                reason: "HEDGE_KILL",
                symbol: symbol,
                timeframe: "HEDGE",
                regime: MarketRegime.Unknown,
                slope: 0m,
                volatility: 0m,
                atr: atr,
                confidence: Math.Min(0.95m, Math.Abs(longPnl - shortPnl) / 10m)
            );

            // ===================================
            // ❌ CLOSE LOSER (MARKET)
            // ===================================
            var closeSide = loser == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            // soften kill: reduce loser first, not always full close
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            decimal loserQtyAbs = Math.Abs(loserPos.Quantity);

            // default: close 60% of loser, keep 40% if price may mean-revert
            decimal closeQty = loserQtyAbs * 0.60m;
            closeQty = Math.Floor(closeQty / step) * step;

            // if funding pressure OR hard loss -> full close
            if (IsFundingRiskExceeded(symbol) || Math.Abs(Math.Min(0m, loserPnl)) >= 25m)
                closeQty = loserQtyAbs;

            if (closeQty < filters.minQty)
                closeQty = loserQtyAbs; // fallback: if too small -> close all

            await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: closeSide,
                type: FuturesOrderType.Market,
                quantity: closeQty,
                positionSide: loser,
                ct: ct);

            await Task.Delay(400, ct);

            // ===================================
            // ✔ PROTECT WINNER → SL → BE + buffer
            // ===================================
            decimal buffer = atr * 0.25m;

            decimal newSl =
                winner == PositionSide.Long
                    ? winnerPos.EntryPrice + buffer
                    : winnerPos.EntryPrice - buffer;

            var orders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
            if (!orders.Success || orders.Data == null)
                return;

            var slOrder = orders.Data.FirstOrDefault(o =>
                o.Type == FuturesOrderType.StopMarket &&
                o.PositionSide == winner);

            if (slOrder != null)
            {
                await UpdateSL_ProAsync(
                    client,
                    symbol,
                    winner,
                    Math.Abs(winnerPos.Quantity),
                    slOrder,
                    winnerPos.EntryPrice,
                    newSl,
                    signal: null,
                    ct);
            }
            else
            {
                // ===============================
                // 🔒 ABSOLUTE PROTECTION (NO GAP)
                // ===============================
                _logger.LogWarning(
                    "[HEDGE-KILL][{symbol}] SL missing on winner → CREATE EMERGENCY SL",
                    symbol);

                await CreateEmergencySLAsync(
                    client,
                    symbol,
                    winner,
                    Math.Abs(winnerPos.Quantity),
                    winnerPos.EntryPrice,
                    signal: null,
                    ct);
            }

            MarkProtection(symbol);

            _logger.LogWarning(
                "[HEDGE-KILL][{symbol}] WINNER {side} protected → SL enforced",
                symbol, winner);

            // 🔒 HEDGE COOLDOWN — запрет повторного hedge
            MarkHedgeCooldown(symbol);

        }


        private bool IsFundingRiskExceeded(string symbol)
        {
            if (!_fundingCost.TryGetValue(symbol, out var cost))
                return false;

            var equity = _engineState.EquityUsd;
            if (equity <= 0)
            {
                _logger.LogWarning("[FUNDING-GUARD] equity unavailable, skip funding check");
                return false; // allow but log
            }

            const decimal maxFundingPct = 0.03m; // 3% equity
            return Math.Abs(cost) / equity >= maxFundingPct;
        }

        private bool CanIncreasePosition(string symbol)
        {
            return !IsFundingRiskExceeded(symbol);
        }


        private async Task RefreshFundingCostAsync(BinanceRestClient client, string symbol, CancellationToken ct)
        {
            try
            {
                // throttle: не чаще 60 сек на symbol
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_fundingLastSync.TryGetValue(symbol, out var last) && (now - last) < 60_000)
                    return;

                _fundingLastSync[symbol] = now;

                // Pull delta from income history (Funding Fee)
                var delta = await GetFundingIncomeDeltaAsync(client, symbol, ct);

                if (delta != 0m)
                {
                    _fundingCost.AddOrUpdate(symbol, delta, (_, prev) => prev + delta);

                    _logger.LogInformation(
                        "[FUNDING][{symbol}] +delta={delta:F6} cum={cum:F6}",
                        symbol, delta, _fundingCost[symbol]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[FUNDING][{symbol}] refresh failed", symbol);
            }
        }

     
        private async Task<decimal> GetFundingIncomeDeltaAsync(
            BinanceRestClient client,
            string symbol,
            CancellationToken ct)
        {
            // Берём funding за последние 2 дня (больше не нужно)
            var startTime = DateTime.UtcNow.AddDays(-2);

            // В ТВОЕЙ версии Binance.Net incomeType = string, startTime = DateTime
            // Поэтому передаём Binance API code:
            const string fundingType = "FUNDING_FEE";



            var res = await client.UsdFuturesApi.Account.GetIncomeHistoryAsync(
    symbol: symbol,
    incomeType: fundingType,
    startTime: DateTime.UtcNow.AddDays(-2),
    limit: 100,
    ct: ct);


            if (!res.Success || res.Data == null)
                return 0m;

            // anti-dup: суммируем только новые элементы после lastTs
            long lastTs = _fundingLastIncomeTs.TryGetValue(symbol, out var v) ? v : 0L;

            decimal delta = 0m;
            long newLastTs = lastTs;

            foreach (var item in res.Data)
            {
                // item может быть разного типа в разных версиях → берём безопасно через dynamic
                dynamic x = item;

                long ts = 0;
                decimal income = 0m;

                // 1) timestamp
                try
                {
                    // часто: x.Time (long ms) или x.Timestamp (DateTime/DateTimeOffset) или x.Date
                    try { ts = (long)x.Time; } catch { }

                    if (ts == 0)
                    {
                        try
                        {
                            DateTime dt = (DateTime)x.Timestamp;
                            ts = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                        }
                        catch { }
                    }

                    if (ts == 0)
                    {
                        try
                        {
                            DateTimeOffset dto = (DateTimeOffset)x.Timestamp;
                            ts = dto.ToUnixTimeMilliseconds();
                        }
                        catch { }
                    }

                    if (ts == 0)
                    {
                        try
                        {
                            DateTime dt = (DateTime)x.Date;
                            ts = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                        }
                        catch { }
                    }

                    if (ts == 0)
                    {
                        // крайний fallback: если нет времени — пропускаем, чтобы не наделать дублей
                        continue;
                    }

                    // 2) income
                    try { income = (decimal)x.Income; } catch { }
                    if (income == 0m)
                    {
                        try { income = (decimal)x.Amount; } catch { }
                    }
                }
                catch
                {
                    continue;
                }

                // только НОВЫЕ записи
                if (ts <= lastTs)
                    continue;

                delta += income;
                if (ts > newLastTs) newLastTs = ts;
            }

            if (newLastTs > lastTs)
                _fundingLastIncomeTs[symbol] = newLastTs;

            return delta;
        }




        // =====================================================================
        // RETRY POSITIONS
        // =====================================================================
        private async Task<WebCallResult<BinancePositionDetailsUsdt[]>> GetPositionsWithRetryAsync(
        BinanceRestClient client,
        string symbol,
        CancellationToken ct)
        {
            const int maxAttempts = 5;
            WebCallResult<BinancePositionDetailsUsdt[]> last = null!;

            for (int i = 0; i < maxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                // 🔥 БЕЗ symbol-фильтра — Binance bug-safe
                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                last = res;

                if (res.Success && res.Data != null)
                {
                    // ⏳ ждём, пока позиция реально появится
                    if (res.Data.Any(p => p.Symbol == symbol && p.Quantity != 0m))
                        return res;
                }

                await Task.Delay(300, ct);
            }

            return last;
        }


        public async Task AttachExistingPositionAsync(
         string symbol,
         PositionSide side,
         decimal qty,
         decimal entryPrice,
         CancellationToken ct)
        {
            qty = Math.Abs(qty);

            if (qty <= 0 || entryPrice <= 0) return;

            var key = $"{symbol}:{side}:{entryPrice:F8}:{qty:F8}";

            if (_attached.TryAdd(key, true) == false)
            {
                _logger.LogInformation("[SUPERVISOR][ATTACH] already attached {key}", key);
                return;
            }

            // ✅ N4: track open position symbol
            _openPos.MarkOpen(symbol);

            _logger.LogWarning(
                "[SUPERVISOR][ATTACH] attaching existing position {symbol} {side} qty={qty} entry={entry}",
                symbol, side, qty, entryPrice);

            await EnsureEmergencyProtectionAsync(symbol, side, qty, entryPrice, ct);
        }


        private async Task EnsureEmergencyProtectionAsync(
        string symbol,
        PositionSide side,
        decimal qty,
        decimal entryPrice,
        CancellationToken ct)
        {
            if (qty <= 0 || entryPrice <= 0)
                return;

            // Важно: qty у позиции в Binance может быть со знаком
            qty = Math.Abs(qty);

            using var client = _factory.CreateRestClient();

            // 1) Проверяем существующие ордера
            var openOrders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(
                symbol: symbol,
                ct: ct);

            if (!openOrders.Success || openOrders.Data == null)
            {
                _logger.LogWarning(
                    "[SUPERVISOR][EMERGENCY] cannot fetch open orders {symbol}: {err}",
                    symbol, openOrders.Error?.Message);
                return;
            }

            bool hasSL = openOrders.Data.Any(o =>
                o.Type == FuturesOrderType.StopMarket &&
                o.PositionSide == side);

            if (hasSL)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][EMERGENCY] SL already exists {symbol} {side}",
                    symbol, side);
                return;
            }

            // 2) Emergency SL без klines (bootstrap-safe)
            // Консервативно: 1.2% от entry (под твой v8.2 смысл "быстро защитить")
            const decimal emergencyPct = 0.012m;

            decimal slPrice = side == PositionSide.Long
                ? entryPrice * (1m - emergencyPct)
                : entryPrice * (1m + emergencyPct);

            _logger.LogWarning(
                "[SUPERVISOR][EMERGENCY] placing SL {symbol} {side} qty={qty} sl={sl}",
                symbol, side, qty, slPrice);

            // 3) Пробуем обычный endpoint (без reduceOnly параметра -> меньше риска -1106)
            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var sl = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: orderSide,
                type: FuturesOrderType.StopMarket,
                quantity: qty,
                stopPrice: slPrice,
                positionSide: side,
                workingType: WorkingType.Mark,
                reduceOnly: null,
                ct: ct);

            if (sl.Success)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][EMERGENCY] SL placed OK (NORMAL) {symbol} {side} sl={sl}",
                    symbol, side, slPrice);
                return;
            }

            // 4) Fallback: ALGO RAW CONDITIONAL (если -4120)
            if (IsAlgoRequired(sl.Error))
            {
                _logger.LogWarning(
                    "[SUPERVISOR][EMERGENCY] requires ALGO (-4120) -> RAW /fapi/v1/algoOrder {symbol} {side}",
                    symbol, side);

                // workingType: "MARK_PRICE" чтобы совпасть с WorkingType.Mark
                var (safeQty, safeTrig) = await NormalizeForAlgoAsync(symbol, qty, slPrice, ct);

                var ok = await _algoRaw.PlaceConditionalAsync(
                    symbol: symbol,
                    side: orderSide,
                    positionSide: side,
                    type: "STOP_MARKET",
                    quantity: safeQty,
                    triggerPrice: safeTrig,
                    workingType: "MARK_PRICE",
                    reduceOnly: null,
                    ct: ct);


                if (ok)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][EMERGENCY] SL placed OK (ALGO-RAW) {symbol} {side} sl={sl}",
                        symbol, side, slPrice);
                    return;
                }

                _logger.LogCritical(
                    "[SUPERVISOR][EMERGENCY] ALGO-RAW FAILED {symbol} {side}",
                    symbol, side);
                return;
            }

            _logger.LogError(
                "[SUPERVISOR][EMERGENCY] SL FAILED (NORMAL) {symbol}: {err}",
                symbol, sl.Error?.Message);
        }



        private async Task TryReverseProbeAsync(
    BinanceRestClient client,
    string symbol,
    BinancePositionDetailsUsdt? longPos,
    BinancePositionDetailsUsdt? shortPos,
    SmartRegimeInfo smart,
    decimal atr,
    CancellationToken ct)
        {


            // 0) protection must exist (PROTECT stage already done by EarlyTP/BE)
            var sKey = EngineState.Key(symbol);
            if (!_engineState.Symbols.TryGetValue(sKey, out var st))
                return;

            bool protectedRecently = st.LastProtectionUtc > DateTime.UtcNow.AddMinutes(-15);
            if (!protectedRecently)
                return;

            // 🔒 FUNDING RISK GATE (before any exposure logic)
            if (IsFundingRiskExceeded(symbol))
            {
                _logger.LogWarning(
                    "[FUNDING-GUARD][{symbol}] funding > limit → probe blocked",
                    symbol);
                return;
            }

            // 1) do not probe if both sides already exist (already hedged)
            bool hasLong = longPos != null && longPos.Quantity != 0m;
            bool hasShort = shortPos != null && shortPos.Quantity != 0m;

            if (hasLong && hasShort)
                return;

            decimal refPrice =
     hasLong ? longPos!.MarkPrice : shortPos!.MarkPrice;

            if (refPrice <= 0)
                return;
            // ===============================
            // VOLATILITY GATE (block probe in extreme vol)
            // ===============================
            // smart.VolatilityPercent у тебя уже есть в SmartRegimeInfo? Если нет — используй atr relative.
            try
            {
                // 1) ATR relative gate (универсально)
                // Берём текущий "price" из имеющейся позиции, без лишних REST вызовов


                if (refPrice > 0 && atr > 0)
                {
                    var atrPct = atr / refPrice; // 0.01 = 1%
                                                 // Жёстко: >1.8% ATR на 1m = часто уже “шум/взрыв”
                    if (atrPct >= 0.018m)
                    {
                        _logger.LogWarning(
                            "[PROBE][{symbol}] BLOCK → extreme ATR vol atrPct={atrPct:P2}",
                            symbol, atrPct);
                        return;
                    }
                }

                // 2) Если у тебя есть smart.VolatilityPercent — можно добавить ещё одно условие
                // if (smart.VolatilityPercent >= 2.5m) return;
            }
            catch { }


            // 2) base side = existing position side
            PositionSide baseSide =
                hasLong ? PositionSide.Long :
                hasShort ? PositionSide.Short :
                PositionSide.Both;

            if (baseSide == PositionSide.Both)
                return;

            // if liquidity recent → wait a bit, but allow early TP later           
            // 🚫 no probe right after liquidity event
            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(5)))
            {
                if (DateTime.UtcNow - _liquidityGuard.LastDanger!.UtcTime < TimeSpan.FromMinutes(2))
                {
                    _logger.LogInformation(
                        "[PROBE][{symbol}] SKIP → recent liquidity danger {reason}",
                        symbol, _liquidityGuard.LastDanger?.Reason);
                    return;
                }
            }

            if (_liquidityGuard.LastDanger?.Reason == LiquidityGuardReason.LowVolume)
                return;


            // 3) flip condition (strict)
            bool flipToShort =
                baseSide == PositionSide.Long &&
                smart.BaseRegime == MarketRegime.StrongDownTrend &&
                smart.TrendSlopePercent < -0.01m;

            bool flipToLong =
                baseSide == PositionSide.Short &&
                smart.BaseRegime == MarketRegime.StrongUpTrend &&
                smart.TrendSlopePercent > 0.01m;

            if (!flipToShort && !flipToLong)
                return;

            var probeSide = flipToShort ? PositionSide.Short : PositionSide.Long;

            // 4) anti-spam (single probe per 5 minutes per symbol)
            if (!_reverseProbe.CanProbeNow(symbol))
                return;

            // 5) size = 7% of existing position qty (micro-hedge, no RiskManager needed)
            decimal baseQtyAbs = Math.Abs((baseSide == PositionSide.Long ? longPos!.Quantity : shortPos!.Quantity));
            if (baseQtyAbs <= 0m)
                return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            decimal probeQty = baseQtyAbs * 0.07m;
            probeQty = Math.Floor(probeQty / step) * step;



            if (probeQty < filters.minQty)
                return;

            // ===============================
            // PORTFOLIO-CAP (SIDE EXPOSURE)
            // ===============================
            decimal equity = _engineState.EquityUsd;
            if (equity <= 0)
                return;

            // текущая экспозиция по стороне (ТОЛЬКО из реальных позиций)
            decimal currentNotional = 0m;

            if (probeSide == PositionSide.Long && longPos != null)
                currentNotional = Math.Abs(longPos.Quantity) * longPos.MarkPrice;

            if (probeSide == PositionSide.Short && shortPos != null)
                currentNotional = Math.Abs(shortPos.Quantity) * shortPos.MarkPrice;

            // notional нового probe
            //decimal probePrice =
            //    probeSide == PositionSide.Long
            //        ? (longPos?.MarkPrice ?? shortPos!.MarkPrice)
            //        : (shortPos?.MarkPrice ?? longPos!.MarkPrice);

            decimal probeNotional = probeQty * refPrice;

            // лимит — 35% equity
            const decimal maxSidePct = 0.35m;

            if ((currentNotional + probeNotional) / equity > maxSidePct)
            {
                _logger.LogWarning(
                    "[PORTFOLIO-CAP][{symbol}] {side} exposure limit hit → probe blocked",
                    symbol, probeSide);
                return;
            }



            // 6) place MARKET entry (Hedge side-aware)
            var orderSide = probeSide == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;

            _logger.LogWarning(
                "[PROBE][{symbol}] START micro-hedge: base={baseSide} -> probe={probeSide} qty={qty} smart={reg} slope={slope:P2}",
                symbol, baseSide, probeSide, probeQty, smart.BaseRegime, smart.TrendSlopePercent);

            _dispatcher.Enqueue(async token =>
            {
                using var c = _factory.CreateRestClient();

                var entryRes = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: orderSide,
                    type: FuturesOrderType.Market,
                    quantity: probeQty,
                    positionSide: probeSide,
                    ct: token);

                if (!entryRes.Success)
                {
                    _logger.LogWarning("[PROBE][{symbol}] Entry failed: {err}", symbol, entryRes.Error);
                    return;
                }

                // small sync lag
                await Task.Delay(350, token);

                // resolve entry from actual position

                var posInfo = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: token);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var p = posInfo.Data.FirstOrDefault(x =>
                    x.Symbol == symbol &&
                    x.PositionSide == probeSide &&
                    Math.Abs(x.Quantity) > 0);

                if (p == null || p.EntryPrice <= 0)
                {
                    _logger.LogWarning("[PROBE][{symbol}] Entry resolve failed after open", symbol);
                    return;
                }

                var entry = p.EntryPrice;

                // set emergency SL/TP for probe immediately
                await CreateEmergencySLAsync(c, symbol, probeSide, probeQty, entry, signal: null, token);
                await CreateEmergencyTPAsync(c, symbol, probeSide, probeQty, entry, signal: null, token);

                _logger.LogWarning(
                    "[PROBE][{symbol}] OPENED {side} qty={qty} entry={entry} -> SL/TP restored",
                    symbol, probeSide, probeQty, entry);
            });

            _reverseProbe.MarkProbe(symbol);
        }


        private async Task<List<BinanceUsdFuturesOrder>> LoadOrdersAsync(BinanceRestClient client, string symbol)
        {
            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
            return res.Success && res.Data != null ? res.Data.ToList() : new List<BinanceUsdFuturesOrder>();
        }


        // =====================================================================
        // HANDLE SIDE  (v8.2 PRO)
        // =====================================================================
        private async Task HandleSideAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            List<BinanceUsdFuturesOrder> allOrders,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines,
            CancellationToken ct)
        {

            decimal qtyAbs = Math.Abs(pos.Quantity);

            // ---------- CLOSE DETECTOR ----------
            var key = $"{symbol}_{side}";
            var prevQty = _manualHandler.GetPrevQty(key);
            var prevEntry = _manualHandler.GetPrevEntry(key);
            _manualHandler.SetPrevState(key, pos.Quantity, pos.EntryPrice);

            if (prevQty != 0 && pos.Quantity == 0)
            {
                decimal exitPrice = pos.MarkPrice > 0 ? pos.MarkPrice : (pos.EntryPrice > 0 ? pos.EntryPrice : prevEntry);
                var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                // AI trade record (only if we had a valid prevEntry)
                if (prevEntry > 0 && exitPrice > 0)
                {
                    _aiLearning.RecordTrade(symbol, sigSide, entry: prevEntry, exit: exitPrice, regime: _regimeNow);

                    _logger.LogWarning(
                        "[AI][{symbol}] POSITION CLOSED → saved to ai_learning.json | entry={entry} exit={exit}",
                        symbol, prevEntry, exitPrice);
                }

                // STOP LOSS DETECT → STRATEGY COOLDOWN
                bool isStopLoss = prevEntry > 0 && exitPrice > 0 &&
                    (side == PositionSide.Long ? exitPrice < prevEntry : exitPrice > prevEntry);

                if (isStopLoss)
                {
                    _manualHandler.RegisterStop(symbol);
                    // 🔑 ВАЖНО: уведомляем StrategyEngine
                    StrategyEngine.RegisterStop(
                        symbol,
                        side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell
                    );
                  
                    _logger.LogWarning("[STOP][{symbol}] StopLoss detected → strategy cooldown registered", symbol);
                }

                // === CLEANUP: wipe all anti-spam keys for this symbol+side (qty/entry may vary) ===
                foreach (var k in _earlyTpDone.Keys.Where(k => k.StartsWith($"{symbol}|{side}|", StringComparison.OrdinalIgnoreCase)))
                    _earlyTpDone.TryRemove(k, out _);

                foreach (var k in _beMoved.Keys.Where(k => k.StartsWith($"{symbol}|{side}|", StringComparison.OrdinalIgnoreCase)))
                    _beMoved.TryRemove(k, out _);

                ClearBaseQty(symbol, side);

                // funding reset on full close (side closed)
                // (если хочешь строго "только когда нет позиций вообще" — оставь reset в SuperviseAsync как сейчас)
                var cleanKey = $"{symbol}_{side}";
                _restoredEntries.TryRemove(cleanKey, out _);

                foreach (var k in _attached.Keys.Where(k => k.StartsWith($"{symbol}:{side}", StringComparison.OrdinalIgnoreCase)))
                    _attached.TryRemove(k, out _);


                // ✅ N4: Instant drop from registry pinned-by-positions
                // We close ONE leg (side). In hedge, the other side may still be open.
                //_openPos.MarkClosed(symbol);
                // ✅ N4: close tracking only if no positions left on symbol
                try
                {
                    // if we just detected this side is 0, other side might still be open.
                    // Use engine snapshot: if symbol state exists -> keep open; otherwise fallback to REST.
                    

                    var info = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                    if (info.Success && info.Data != null)
                    {
                        bool anyLeft = info.Data.Any(p => p.Symbol == symbol && p.Quantity != 0m);
                        if (!anyLeft) _openPos.MarkClosed(symbol);
                       
                    }
                }
                catch { /* ignore */ }
                return;
            }


            bool fundingBlocked = IsFundingRiskExceeded(symbol);

            if (fundingBlocked)
            {
                _logger.LogWarning(
                    "[FUNDING-GUARD][{symbol}] funding limit → block ADD / SCALE only",
                    symbol);
            }

            if (qtyAbs <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no qty", symbol, side);
                return;
            }

            decimal entry = pos.EntryPrice;

            if (entry > 0)
                _posBaseEntry[$"{symbol}|{side}"] = entry;

            if (entry <= 0)
            {
                key = $"{symbol}_{side}";

                if (!_restoredEntries.TryGetValue(key, out entry))
                {
                    var restored = await ResolveEntryFromExchangeAsync(client, symbol, side, ct);
                    if (!restored.HasValue)
                    {
                        _logger.LogError("[SUPERVISOR][{symbol}] Entry unresolved → skip SL/TP", symbol);
                        return;
                    }

                    entry = restored.Value;
                    _restoredEntries[key] = entry;

                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}] Entry restored from exchange = {entry}",
                        symbol, entry);
                }
            }

            // In case signal missing ATR in supervisor context, try compute
            decimal atr14 = 0m;
            if (signal?.Atr != null && signal.Atr.Value > 0)
                atr14 = signal.Atr.Value;
            else if (klines != null && klines.Count >= 30)
                atr14 = _marketData.CalculateAtr(klines, 14);

            // === Side-specific orders (Hedge) ===
            var orders = allOrders.Where(o => o.PositionSide == side).ToList();

            // === Find SL/TP ===
            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            bool hasMultipleSL = orders.Count(o => o.Type == FuturesOrderType.StopMarket) > 1;
            bool hasMultipleTP = orders.Count(o => o.Type == FuturesOrderType.TakeProfitMarket) > 1;

            if (hasMultipleSL)
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple SL detected → skip SL create", symbol, side);

            if (hasMultipleTP)
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple TP detected → skip TP create", symbol, side);

            var sl = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.StopMarket);
            var tp = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.TakeProfitMarket);

            var baseQtyForGuards = GetOrSetBaseQty(symbol, side, entry, qtyAbs);

            // =================================================================
            // v8.2: EARLY PROFIT + BE MOVE (До restore TP/SL)
            // =================================================================
            if (klines != null && klines.Count >= 50 && atr14 > 0 && entry > 0)
            {
                // 1) EARLY TP (partial 35% at +0.9 ATR)
                // await TryEarlyPartialTakeAsync(client, symbol, side, qtyAbs, entry, atr14, signal, klines, ct);
                await TryEarlyPartialTakeAsync(client, symbol, side, baseQtyForGuards, entry, atr14, signal, klines, ct);

                // 2) SL -> BE when +1.2 ATR (only if SL exists)
                if (sl != null)
                    //await TryMoveSlToBeAsync(client, symbol, side, qtyAbs, entry, atr14, sl, signal, klines, ct);
                    await TryMoveSlToBeAsync(client, symbol, side, baseQtyForGuards, entry, atr14, sl, signal, klines, ct);

            }


            // PROFIT HARVEST (ПОСЛЕ early/BE, ДО restore SL/TP)
            // =================================================================
            if (klines != null && klines.Count >= 50)
            {
                decimal aiEdgeScore =
                    _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend
                        ? 0.82m
                        : 0.62m;

                await TryHarvestProfitAsync(
                    client,
                    _engineState,
                    symbol,
                    side,
                    pos,
                    klines,
                    aiEdgeScore,
                    minUsd: 6m,
                    ct);
            }



            // 1) SL отсутствует → аварийный SL (если нет дублей)
            if (sl == null && !hasMultipleSL)
            {
                await CreateEmergencySLAsync(client, symbol, side, qtyAbs, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] SL restored", symbol, side);
                //return;
            }

            // 2) TP отсутствует → аварийный TP (если нет дублей)
            if (tp == null && !hasMultipleTP)
            {
                await CreateEmergencyTPAsync(client, symbol, side, qtyAbs, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] TP restored", symbol, side);
                // return;
            }

            // 3) Трейлинг + раннер
            // 3) Трейлинг + раннер (ТОЛЬКО если funding не превышен)
            if (!fundingBlocked && klines != null && klines.Count >= 50)
            {
                await ManageRunnerTpAsync(client, symbol, side, qtyAbs, entry, orders, signal, klines, ct);
                await ManageRunnerTpExtensionAsync(client, symbol, side, qtyAbs, entry, signal, orders, klines, ct);
                await MultiLayerTrailingAsync(client, symbol, side, qtyAbs, entry, signal, orders, klines, ct);
            }

        }


        private async Task<decimal?> ResolveEntryFromExchangeAsync(
    BinanceRestClient client,
    string symbol,
    PositionSide side,
    CancellationToken ct)
        {
            var trades = await client.UsdFuturesApi.Trading.GetUserTradesAsync(
                symbol: symbol,
                limit: 50,
                ct: ct);

            if (!trades.Success || trades.Data == null)
                return null;

            var lastOpen = trades.Data
                .Where(t => t.PositionSide == side)
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefault();

            return lastOpen?.Price;
        }

        private void MarkProtection(string symbol)
        {
            var key = EngineState.Key(symbol);
            var st = _engineState.Symbols.GetOrAdd(key, _ => new SymbolState());
            st.LastProtectionUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "[PROTECTION][{symbol}] protection marked @ {time}",
                symbol, st.LastProtectionUtc);
        }

        // =====================================================================
        // EARLY TP (Partial close) — ключевой фикс v8.2
        // =====================================================================
        private async Task TryEarlyPartialTakeAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entry,
            decimal atr,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            var last = klines[^1].ClosePrice;
            // Блокируем, если LiquidityGuard сигналит опасность (не лезем в рынок лишний раз)
            if (_liquidityGuard.LastDanger?.Block == true)
            {
                EarlyTpTrace.Skip(_logger, symbol, side, entry, last, atr, "LIQUIDITY_BLOCK");
                return;
            }

            // ⚠️ skip early TP if liquidity was recent (soft protection)
            // if liquidity recent → wait a bit, but allow early TP later
            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(5)))
            {
                var age = DateTime.UtcNow - _liquidityGuard.LastDanger!.UtcTime;
                if (age < TimeSpan.FromMinutes(2))
                {
                    EarlyTpTrace.Skip(
                        _logger, symbol, side, entry, last, atr,
                        "LIQUIDITY_RECENT",
                        $"age={age.TotalSeconds:F0}s");
                    return;
                }
            }

            var lastCandle = klines[^1];
            var body = Math.Abs(lastCandle.ClosePrice - lastCandle.OpenPrice);
            var wickAgainst =
                side == PositionSide.Long
                    ? lastCandle.HighPrice - lastCandle.ClosePrice
                    : lastCandle.ClosePrice - lastCandle.LowPrice;

            // если свеча с хвостом против — это не импульс
            if (wickAgainst > body * 0.8m)
            {
                EarlyTpTrace.Skip(
                    _logger, symbol, side, entry, last, atr,
                    "WICK_REJECTION",
                    $"wick={wickAgainst:F4} body={body:F4}");
                return;
            }



            bool reached =
                side == PositionSide.Long
                    ? last >= entry + atr * 0.90m
                    : last <= entry - atr * 0.90m;

            if (!reached)
            {
                var need = atr * 0.90m;
                EarlyTpTrace.Skip(
                    _logger, symbol, side, entry, last, atr,
                    "ATR_NOT_REACHED",
                    $"need={need:F6}");
                return;
            }

            var guardKey = BuildPosGuardKey(symbol, side, entry);
            if (_earlyTpDone.ContainsKey(guardKey))
            {
                EarlyTpTrace.Skip(
                    _logger, symbol, side, entry, last, atr,
                    "ALREADY_DONE");
                return;
            }

            var closeQty = Math.Round(qty * 0.35m, 8);
            if (closeQty <= 0)
            {
                EarlyTpTrace.Skip(
                    _logger, symbol, side, entry, last, atr,
                    "QTY_ZERO",
                    $"baseQty={qty}");
                return;
            }

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();
                var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: closeQty,
                    positionSide: side,
                    ct: ct);


                if (!res.Success)
                {
                    _logger.LogWarning("[EARLY-TP][{symbol}][{side}] Market partial close failed: {err}", symbol, side, res.Error);
                    return;
                }

                // ✅ SUCCESS (HIT) — СТАВИТЬ ИМЕННО ЗДЕСЬ
                EarlyTpTrace.Hit(
                    _logger,
                    symbol,
                    side,
                    entry,
                    last,
                    atr,
                    closeQty,
                    qty);

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                _earlyTpDone[guardKey] = now;
                _recentPartialClose[$"{symbol}|{side}"] = now;

                MarkProtection(symbol);

                //_logger.LogWarning(
                //    "[EARLY-TP][{symbol}][{side}] Partial fixed {closed}/{total}",
                //    symbol, side, closeQty, qty);
            });



            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

         //   _earlyTpDone[guardKey] = now;
            // 🔒 BLOCK HARVEST for 8 seconds after EARLY-TP
          //  _recentPartialClose[$"{symbol}|{side}"] = now;

            _logger.LogWarning(
                "[EARLY-TP][{symbol}][{side}] Partial profit fixed {closed}/{total} @price={price} (+0.9ATR)",
                symbol, side, closeQty, qty, last);

            // Optional learning hook
            try
            {
                if (signal != null && !signal.IsManual)
                {
                    var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;
                    _aiLearning.RecordTrade(symbol, sigSide, entry, last, _regimeNow);
                }
            }
            catch { }
        }

        // =====================================================================
        // SL -> BE (безубыток + буфер) — ключевой фикс v8.2
        // =====================================================================
        private async Task TryMoveSlToBeAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entry,
            decimal atr,
            BinanceUsdFuturesOrder slOrder,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            var last = klines[^1].ClosePrice;

            bool reached =
                side == PositionSide.Long
                    ? last >= entry + atr * 1.20m
                    : last <= entry - atr * 1.20m;

            if (!reached) return;

            var guardKey = BuildPosGuardKey(symbol, side, entry);
            if (_beMoved.ContainsKey(guardKey)) return;

            decimal buffer = atr * 0.15m;

            // если была ликвидность недавно — НЕ ставим SL близко
            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(6)))
                buffer *= 0.5m;

            // structural swing (последние 5 свечей)
            decimal structural =
                side == PositionSide.Long
                    ? klines.TakeLast(5).Min(k => k.LowPrice)
                    : klines.TakeLast(5).Max(k => k.HighPrice);

            decimal beBase =
                side == PositionSide.Long
                    ? entry + buffer
                    : entry - buffer;

            // берём более «дальний» уровень
            decimal newSl =
                side == PositionSide.Long
                    ? Math.Max(beBase, structural)
                    : Math.Min(beBase, structural);

            // только если реально улучшает SL
            decimal oldSl = slOrder.StopPrice ?? slOrder.Price;
            if (oldSl <= 0) return;

            if (side == PositionSide.Long && newSl <= oldSl) return;
            if (side == PositionSide.Short && newSl >= oldSl) return;

            var ok = await UpdateSL_ProAsync(client, symbol, side, qty, slOrder, entry, newSl, signal, ct);
       

            if (!ok)
                return;

            _beMoved[guardKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            MarkProtection(symbol);

            _logger.LogWarning(
                "[BE][{symbol}][{side}] SL moved to BE+buffer newSL={sl}",
                symbol, side, newSl);
        }

        private static string BuildPosGuardKey(string symbol, PositionSide side, decimal entry)
        {
            // грубый, но рабочий ключ: символ+side+entry+qty (округлим)
            string E(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            return $"{symbol}|{side}|e={E(entry)}";
        }
        // =====================================================================
        // EMERGENCY SL  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
        // =====================================================================
        private async Task CreateEmergencySLAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            CancellationToken ct)
        {
            try
            {
                // ==========================================================
                // 1) ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ
                // ==========================================================
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var pos = posInfo.Data.FirstOrDefault(p =>
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0);

                if (pos == null)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}][{side}] SKIP SL → no open position",
                        symbol, side);
                    return;
                }

                // ==========================================================
                // 2) КОЛИЧЕСТВО ЗАКРЫТИЯ
                // ==========================================================
                var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                if (closeQty <= 0)
                    return;

                // ==========================================================
                // 3) РАСЧЁТ SL
                // ==========================================================
                decimal rawSl;

                if (signal?.StopLoss > 0)
                {
                    rawSl = signal.StopLoss;
                }
                else
                {
                    //var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    //if (kl.Count < 30) return;

                    //var atr = _marketData.CalculateAtr(kl, 14);
                    //if (atr <= 0) return;

                    //var atrMult = _regimeNow switch
                    //{
                    //    MarketRegime.Range => 1.2m,
                    //    MarketRegime.Squeeze => 1.5m,
                    //    MarketRegime.UpTrend or MarketRegime.DownTrend => 1.8m,
                    //    MarketRegime.VolatileChop => 2.0m,
                    //    _ => 2.2m
                    //};

                    //rawSl = side == PositionSide.Long
                    //    ? entryPrice - atr * atrMult
                    //    : entryPrice + atr * atrMult;
                    // BOOTSTRAP SAFE: без klines
                    // База: процент от entry + лёгкая адаптация по режиму
                    decimal pct = _regimeNow switch
                    {
                        MarketRegime.Range => 0.010m,        // 1.0%
                        MarketRegime.Squeeze => 0.012m,      // 1.2%
                        MarketRegime.UpTrend or MarketRegime.DownTrend => 0.014m, // 1.4%
                        MarketRegime.VolatileChop => 0.016m, // 1.6%
                        _ => 0.018m                          // 1.8%
                    };

                    rawSl = side == PositionSide.Long
                        ? entryPrice * (1m - pct)
                        : entryPrice * (1m + pct);
                }

                // ==========================================================
                // 4) TICK + MARK PRICE SAFETY
                // ==========================================================
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;

                decimal sl = Math.Round(rawSl / tick) * tick;
                decimal mark = await GetMarkPriceSafeAsync(client, symbol, entryPrice, ct);

                if (mark > 0)
                {
                    if (side == PositionSide.Long && sl >= mark)
                        sl = mark - tick;

                    if (side == PositionSide.Short && sl <= mark)
                        sl = mark + tick;
                }

                var orderSide = side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                // ==========================================================
                // 5) ОТПРАВКА (NORMAL → ALGO RAW)
                // ==========================================================
                _dispatcher.Enqueue(async ct =>
                {
                    using var c = _factory.CreateRestClient();

                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: closeQty,
                        positionSide: side,
                        stopPrice: sl,
                        reduceOnly: null,
                        ct: ct);

                    if (res.Success)
                    {
                        _logger.LogInformation(
                            "[SUPERVISOR] SL CREATED (NORMAL) {symbol} {side} sl={sl}",
                            symbol, side, sl);
                        return;
                    }

                    // ======================================================
                    // 6) FALLBACK → ALGO RAW (-4120)
                    // ======================================================
                    if (IsAlgoRequired(res.Error))
                    {
                        // === SAFE precision for ALGO-RAW ===

                        var (safeQty, safeTrig) = await NormalizeForAlgoAsync(symbol, closeQty, sl, ct);

                        // minQty gate
                        var filters2 = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                        if (safeQty < filters2.minQty)
                        {
                            _logger.LogError("[ALGO-RAW][SL] SKIP {symbol} qty {q} < minQty {min}",
                                symbol, safeQty, filters2.minQty);
                            return;
                        }
                       
                        _logger.LogWarning(
                            "[SUPERVISOR] SL requires ALGO endpoint (-4120) → RAW ALGO {symbol} {side}",
                            symbol, side);

                        var ok = await _algoRaw.PlaceConditionalAsync(
    symbol: symbol,
    side: orderSide,
    positionSide: side,
    type: "STOP_MARKET",
    quantity: safeQty,        // ✅ ИСПОЛЬЗУЕМ SAFE
    triggerPrice: safeTrig,   // ✅ ИСПОЛЬЗУЕМ SAFE
    workingType: "CONTRACT_PRICE",
    reduceOnly: true,
    ct: ct);

                        if (ok)
                        {
                            _logger.LogInformation(
                                "[SUPERVISOR] SL CREATED (ALGO-RAW) {symbol} {side} sl={sl}",
                                symbol, side, sl);
                            return;
                        }

                        _logger.LogWarning(
                            "[SUPERVISOR] SL skipped: ALGO-RAW failed {symbol} {side}",
                            symbol, side);
                        return;
                    }

                    _logger.LogError(
                        "[SUPERVISOR] ERROR SL create (NORMAL) {symbol}: {err}",
                        symbol, res.Error);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX SL create {symbol}", symbol);
            }
        }

        // =====================================================================
        // EMERGENCY TP  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
        // =====================================================================
        private async Task CreateEmergencyTPAsync(
     BinanceRestClient client,
     string symbol,
     PositionSide side,
     decimal qty,
     decimal entryPrice,
     TradeSignal? signal,
     CancellationToken ct)
        {
            try
            {
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
                if (!posInfo.Success || posInfo.Data == null) return;

                var pos = posInfo.Data.FirstOrDefault(p =>
                    p.PositionSide == side && Math.Abs(p.Quantity) > 0);

                if (pos == null) return;

                var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                if (closeQty <= 0) return;

                decimal trigger;
                if (signal?.TakeProfits?.Count > 0)
                    trigger = signal.TakeProfits[0];
                else
                    trigger = side == PositionSide.Long
                        ? entryPrice * 1.016m
                        : entryPrice * 0.984m;

                var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

                trigger = NormalizeToStep(trigger, f.tickSize > 0 ? f.tickSize : 0.0001m);

                if (side == PositionSide.Long && trigger <= entryPrice)
                    trigger = entryPrice + f.tickSize * 3;
                if (side == PositionSide.Short && trigger >= entryPrice)
                    trigger = entryPrice - f.tickSize * 3;

                var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                _dispatcher.Enqueue(async token =>
                {
                    using var c = _factory.CreateRestClient();

                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.TakeProfitMarket,
                        quantity: closeQty,
                        positionSide: side,
                        stopPrice: trigger,
                        reduceOnly: null,
                        ct: token);

                    if (res.Success) return;

                    if (!IsAlgoRequired(res.Error)) return;

                    var safeQty = NormalizeToStep(closeQty, f.step > 0 ? f.step : 1m);
                    var safeTrig = NormalizeToStep(trigger, f.tickSize > 0 ? f.tickSize : 0.0001m);

                    if (safeQty < f.minQty) return;

                    _logger.LogWarning(
                        "[ALGO-RAW][PRE][TP] {symbol} qty={qty} trig={trig}",
                        symbol, safeQty, safeTrig);

                    await _algoRaw.PlaceConditionalAsync(
                        symbol: symbol,
                        side: orderSide,
                        positionSide: side,
                        type: "TAKE_PROFIT_MARKET",
                        quantity: safeQty,
                        triggerPrice: safeTrig,
                        workingType: "CONTRACT_PRICE",
                        reduceOnly: true,
                        ct: token);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX CreateEmergencyTP {symbol}", symbol);
            }
        }


        private static int GetPrecision(decimal value)
        {
            value = Math.Abs(value);
            int precision = 0;

            while (value < 1m && value != Math.Round(value))
            {
                value *= 10;
                precision++;
                if (precision > 18) break;
            }

            return precision;
        }

        private static decimal Truncate(decimal value, int precision)
        {
            if (precision <= 0) return Math.Truncate(value);
            var p = (decimal)Math.Pow(10, precision);
            return Math.Truncate(value * p) / p;
        }


        private static int GetPrecisionFromStep(decimal step)
        {
            var s = step.ToString(CultureInfo.InvariantCulture);

            if (!s.Contains('.'))
                return 0;

            return s.TrimEnd('0').Split('.')[1].Length;
        }

        private static decimal Clamp(decimal value, int precision)
        {
            if (precision <= 0)
                return Math.Floor(value);

            var factor = (decimal)Math.Pow(10, precision);
            return Math.Floor(value * factor) / factor;
        }
        private static bool IsAlgoRequired(CryptoExchange.Net.Objects.Error? err)
        {
            if (err == null) return false;

            if (err.Code == -4120) return true;

            var msg = (err.Message ?? string.Empty).ToLowerInvariant();
            return msg.Contains("algo order api") || msg.Contains("algoorder") || msg.Contains("not supported for this endpoint");
        }

        // =====================================================================
        // PLACEHOLDERS: keep your existing methods below
        // =====================================================================
        private async Task ManageRunnerTpExtensionAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            List<BinanceUsdFuturesOrder> orders,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            await Task.CompletedTask;
        }
        // =====================================================================
        // RUNNER (твоя логика) + SL update (v8.2 PRO)
        // =====================================================================
        private async Task ManageRunnerTpAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            List<BinanceUsdFuturesOrder> orders,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            // ===== STRONG TREND RUNNER FIX =====
            bool strongTrend =
                _regimeNow == MarketRegime.StrongUpTrend ||
                _regimeNow == MarketRegime.StrongDownTrend;

            if (!strongTrend)
                return;


            if (signal?.TakeProfit is null || signal.TakeProfit <= 0)
                return;

            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
            if (!priceRes.Success || priceRes.Data == null)
                return;

            var price = priceRes.Data.Price;

            bool tpHit =
                side == PositionSide.Long ? price >= signal.TakeProfit : price <= signal.TakeProfit;

            if (!tpHit) return;

            bool trendOk =
                side == PositionSide.Long
                    ? _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.UpTrend
                    : _regimeNow is MarketRegime.StrongDownTrend or MarketRegime.DownTrend;

            if (!trendOk) return;

            var atr = signal.Atr ?? 0m;
            if (atr <= 0 || klines.Count < 2) return;

            var last = klines[^1];
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            if (body < atr * 0.7m) return;

            if (_liquidityGuard.LastDanger?.Block == true) return;

            // 1) частичное закрытие (70%)
            var closeQty = Math.Round(qty * 0.7m, 8);
            var runnerQty = qty - closeQty;
            if (closeQty <= 0 || runnerQty <= 0) return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();

                await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: closeQty,
                    positionSide: side,
                    ct: ct);

                _logger.LogInformation("[TP-EXT][{symbol}] Partial TP executed {closed}/{total}, runner={runner}",
                    symbol, closeQty, qty, runnerQty);

                decimal newSl =
                    side == PositionSide.Long ? entryPrice + atr * 0.25m : entryPrice - atr * 0.25m;

                var slOrder = orders.FirstOrDefault(o =>
                    o.Type == FuturesOrderType.StopMarket &&
                    o.PositionSide == side);

                if (slOrder != null)
                    await UpdateSL_ProAsync(c, symbol, side, runnerQty, slOrder, entryPrice, newSl, signal, ct);

                _logger.LogWarning("[TP-EXT][{symbol}] Runner activated | new SL={sl}", symbol, newSl);
            });

        }

        /// <summary>
        /// v8.2 PRO SL update:
        /// - Cancel old SL
        /// - Place new SL via NORMAL endpoint
        /// - If -4120 -> ALGO-RAW
        /// - NO reduceOnly (важно для Hedge/ошибок -1106)
        /// - WorkingType.Mark используем осторожно: сначала пробуем, если Binance ругается — повтор без него
        /// </summary>
        //   private Task<bool> UpdateSL_ProAsync(
        //BinanceRestClient client,
        //string symbol,
        //PositionSide side,
        //decimal qty,
        //BinanceUsdFuturesOrder slOrder,
        //decimal entry,
        //decimal newSl,
        //TradeSignal? signal,
        //CancellationToken ct)
        //   {
        //       if (qty <= 0 || newSl <= 0) return Task.FromResult(false);

        //       _dispatcher.Enqueue(async token =>
        //       {
        //           using var c = _factory.CreateRestClient();

        //           var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

        //           var safeTrig = NormalizeToStep(newSl, f.tickSize > 0 ? f.tickSize : 0.0001m);
        //           var safeQty = NormalizeToStep(qty, f.step > 0 ? f.step : 1m);

        //           if (safeQty < f.minQty) return;

        //           var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

        //           var r1 = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
        //               symbol: symbol,
        //               side: orderSide,
        //               type: FuturesOrderType.StopMarket,
        //               quantity: safeQty,
        //               stopPrice: safeTrig,
        //               positionSide: side,
        //               workingType: WorkingType.Mark,
        //               ct: token);

        //           if (r1.Success) return;

        //           if (!IsAlgoRequired(r1.Error)) return;

        //           _logger.LogWarning(
        //               "[ALGO-RAW][PRE][SL] {symbol} qty={qty} trig={trig}",
        //               symbol, safeQty, safeTrig);

        //           await _algoRaw.PlaceConditionalAsync(
        //               symbol: symbol,
        //               side: orderSide,
        //               positionSide: side,
        //               type: "STOP_MARKET",
        //               quantity: safeQty,
        //               triggerPrice: safeTrig,
        //               workingType: "CONTRACT_PRICE",
        //               reduceOnly: null,
        //               ct: token);
        //       });

        //       return Task.FromResult(true);
        //   }

        private async Task<bool> UpdateSL_ProAsync(
       BinanceRestClient client,
       string symbol,
       PositionSide side,
       decimal qty,
       BinanceUsdFuturesOrder oldSl,
       decimal entry,
       decimal newSl,
       TradeSignal? signal,
       CancellationToken ct)
        {
            if (qty <= 0 || newSl <= 0) return false;

            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            var safeQty = NormalizeToStep(qty, f.step > 0 ? f.step : 1m);
            var safeTrig = NormalizeToStep(newSl, f.tickSize > 0 ? f.tickSize : 0.0001m);

            if (safeQty < f.minQty) return false;

            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            try
            {
                // 0) Safety: ensure position exists (avoid orphan SL spam)
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!posInfo.Success || posInfo.Data == null) return false;

                var realPos = posInfo.Data.FirstOrDefault(p =>
                    p.Symbol == symbol &&
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0);

                if (realPos == null) return false;

                // 1) PLACE NEW SL FIRST (NORMAL)
                var place = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: orderSide,
                    type: FuturesOrderType.StopMarket,
                    quantity: safeQty,
                    stopPrice: safeTrig,
                    positionSide: side,
                    reduceOnly: null,
                    workingType: WorkingType.Mark,
                    ct: ct);

                bool placedOk = place.Success;

                // 2) FALLBACK: ALGO RAW (-4120)
                if (!placedOk && IsAlgoRequired(place.Error))
                {
                    placedOk = await _algoRaw.PlaceConditionalAsync(
                        symbol: symbol,
                        side: orderSide,
                        positionSide: side,
                        type: "STOP_MARKET",
                        quantity: safeQty,
                        triggerPrice: safeTrig,
                        workingType: "CONTRACT_PRICE",
                        reduceOnly: null,
                        ct: ct);
                }

                if (!placedOk)
                {
                    _logger.LogWarning(
                        "[SL][{symbol}][{side}] New SL place FAILED -> keep old SL (NO GAP)",
                        symbol, side);
                    return false;
                }

                // 3) Cancel OLD SL after new one is confirmed placed
                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, oldSl.Id, ct: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SL][{symbol}][{side}] Cancel old SL failed {id}", symbol, side, oldSl.Id);
                    // Not fatal: at worst we have 2 SL temporarily; OrderCleaner can resolve later.
                }

                _logger.LogInformation("[SL][{symbol}][{side}] SL updated -> {sl}", symbol, side, safeTrig);

                if (signal != null && !signal.IsManual)
                {
                    HookAiLearningOnSlMove(signal, symbol, side, entry, safeTrig);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SL][{symbol}][{side}] EX UpdateSL", symbol, side);
                return false;
            }
        }



        private void HookAiLearningOnSlMove(TradeSignal? signal, string symbol, PositionSide side, decimal entry, decimal newSl)
        {
            try
            {
                if (signal != null && !signal.IsManual)
                {
                    var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                    _manualHandler.RegisterStop(symbol);
                    _aiLearning.RecordTrade(symbol, sigSide, entry, newSl, _regimeNow);
                }
            }
            catch { }
        }

        private static decimal NormalizeToStep(decimal value, decimal step)
        {
            if (step <= 0) return value;
            return Math.Floor(value / step) * step;
        }


        private async Task MultiLayerTrailingAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            List<BinanceUsdFuturesOrder> orders,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            await Task.CompletedTask;
        }
        // =====================================================================
        // MARK PRICE SAFE
        // =====================================================================
        /* private static async Task<decimal> GetMarkPriceSafeAsync(
             BinanceRestClient client,
             string symbol,
             decimal fallback,
             CancellationToken ct)
         {
             try
             {
                 var r = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
                 if (r.Success && r.Data != null && r.Data.Price > 0)
                     return r.Data.Price;
             }
             catch { }

             return fallback > 0 ? fallback : 0m;
         }*/

        private static async Task<decimal> GetMarkPriceSafeAsync(
    BinanceRestClient client,
    string symbol,
    decimal fallback,
    CancellationToken ct)
        {
            try
            {
                // 1) Prefer MARK PRICE if available in your Binance.Net version
                try
                {
                    var mp = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol, ct: ct);
                    if (mp.Success && mp.Data != null && mp.Data.MarkPrice > 0)
                        return mp.Data.MarkPrice;
                }
                catch
                {
                    // ignore: method not available in some versions
                }

                // 2) Fallback: last price
                var r = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
                if (r.Success && r.Data != null && r.Data.Price > 0)
                    return r.Data.Price;
            }
            catch { }

            return fallback > 0 ? fallback : 0m;
        }

        // =====================================================================
        // RAW BINANCE ALGO ORDER (POST /fapi/v1/algoOrder)
        // =====================================================================
        private sealed class BinanceAlgoOrderRaw
        {
            private readonly HttpClient _http;
            private readonly ILogger _logger;
            private readonly string _apiKey;
            private readonly string _apiSecret;
            private readonly string _baseUrl;
            public BinanceAlgoOrderRaw(IConfiguration cfg, IHttpClientFactory httpFactory, ILogger logger)
            {
                _logger = logger;

                _apiKey = cfg["Binance:ApiKey"] ?? string.Empty;
                _apiSecret = cfg["Binance:SecretKey"] ?? string.Empty;
                _baseUrl = (cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com").TrimEnd('/');

                _http = httpFactory.CreateClient("BinanceAlgoRaw");
                _http.Timeout = TimeSpan.FromSeconds(8);
            }
            public async Task<bool> PlaceConditionalAsync(
                string symbol,
                OrderSide side,
                PositionSide positionSide,
                string type,
                decimal quantity,
                decimal triggerPrice,
                string workingType,
                bool? reduceOnly,
                CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
                {
                    _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:ApiSecret in config");
                    return false;
                }

                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                static string D(decimal v)
                {
                    // fixed invariant, then trim trailing zeros
                    var s = v.ToString("0.############################", CultureInfo.InvariantCulture);
                    if (s.Contains('.'))
                        s = s.TrimEnd('0').TrimEnd('.');
                    return s;
                }

                var q = new List<KeyValuePair<string, string>>
                {
                    new("algoType", "CONDITIONAL"),
                    new("symbol", symbol),
                    new("side", side == OrderSide.Buy ? "BUY" : "SELL"),
                    new("type", type),
                    new("timestamp", ts.ToString(CultureInfo.InvariantCulture)),
                    new("workingType", workingType),
                    new("triggerPrice", D(triggerPrice)),
                    new("positionSide", positionSide.ToString().ToUpperInvariant()),
                    new("quantity", D(quantity))
                };

                // reduceOnly — только если positionSide == BOTH (в Hedge не шлём)
                if (reduceOnly.HasValue && positionSide == PositionSide.Both)
                    q.Add(new("reduceOnly", reduceOnly.Value ? "true" : "false"));

                var query = BuildQuery(q);
                var sig = Sign(query, _apiSecret);

                var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                try
                {
                    using var resp = await _http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogError("[ALGO-RAW] HTTP {code} body={body}", (int)resp.StatusCode, body);
                        return false;
                    }

                    _logger.LogInformation("[ALGO-RAW] OK {symbol} {type} posSide={ps} trig={tp} body={body}",
                        symbol, type, positionSide, triggerPrice, body);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALGO-RAW] EX PlaceConditionalAsync {symbol}", symbol);
                    return false;
                }
            }

            private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> q)
            {
                var sb = new StringBuilder();
                foreach (var kv in q)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(Uri.EscapeDataString(kv.Key));
                    sb.Append('=');
                    sb.Append(Uri.EscapeDataString(kv.Value));
                }
                return sb.ToString();
            }

            private static string Sign(string queryString, string secret)
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }



        }





        private async Task TryHarvestProfitAsync(
            BinanceRestClient client,
            EngineState state,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            decimal aiEdgeScore,
            decimal minUsd,
            CancellationToken ct)
        {

            // ==========================================================
            // 🔒 BLOCK HARVEST right after EARLY-TP (Binance sync lag)
            // ==========================================================
            var harvestKey = $"{symbol}|{side}";

            if (_recentPartialClose.TryGetValue(harvestKey, out var ts))
            {
                var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts;
                if (ageMs < 8000) // 8 seconds hard block
                {
                    _logger.LogInformation(
                        "[HARVEST][{symbol}][{side}] SKIP → recent EARLY-TP ({ms}ms)",
                        symbol, side, ageMs);
                    return;
                }

                _recentPartialClose.TryRemove(harvestKey, out _);
            }




            var sKey = EngineState.Key(symbol);
            var st = state.Symbols.GetOrAdd(sKey, _ => new SymbolState());

            // throttle
            if ((DateTime.UtcNow - st.LastHarvestUtc) < TimeSpan.FromMinutes(6))
                return;

            // ==========================================================
            // 🔒 ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ
            // ==========================================================
            var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!posInfo.Success || posInfo.Data == null)
                return;

            var realPos = posInfo.Data.FirstOrDefault(p =>
                p.Symbol == symbol &&
                p.PositionSide == side &&
                Math.Abs(p.Quantity) > 0);

            if (realPos == null)
            {
                _logger.LogWarning("[HARVEST][{symbol}][{side}] SKIP → no open position", symbol, side);
                return;
            }

            decimal qty = Math.Abs(realPos.Quantity);
            if (qty <= 0) return;

            // ==========================================================
            // uPnL
            // ==========================================================
            decimal uPnl;
            try { uPnl = realPos.UnrealizedPnl; }
            catch { return; }

            if (uPnl <= 0m || uPnl < minUsd)
                return;

            decimal atr = _marketData.CalculateAtr(klines);
            if (atr <= 0) atr = 0.00000001m;

            var last = klines[^1];
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);

            // сильный импульс → не режем
            if (
                (_regimeNow == MarketRegime.StrongUpTrend ||
                 _regimeNow == MarketRegime.StrongDownTrend)
                && body > atr * 1.1m
            )
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}] SKIP → trend expansion",
                    symbol);
                return;
            }



            decimal rr = Math.Abs(realPos.MarkPrice - realPos.EntryPrice) / atr;

            decimal harvestPct =
                aiEdgeScore >= 0.80m && rr >= 1.4m ? 0.18m :
                aiEdgeScore >= 0.70m ? 0.28m :
                0.45m;

            //  decimal closeQty = Math.Round(qty * harvestPct, 8);
            // if (closeQty <= 0) return;

            // ==========================================================
            // 🔥 FULL vs PARTIAL CLOSE LOGIC (КЛЮЧЕВО)
            // ==========================================================

            decimal closeQty = qty * harvestPct;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            // ❗ КЛЮЧЕВО: всегда вниз
            closeQty = Math.Floor(closeQty / step) * step;

            if (closeQty < filters.minQty)
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}][{side}] SKIP → rounded closeQty {q} < minQty {min}",
                    symbol, side, closeQty, filters.minQty);
                return;
            }


            bool isFullClose = closeQty >= qty;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
     symbol: symbol,
     side: closeSide,
     type: FuturesOrderType.Market,
     quantity: closeQty,
     positionSide: side,
     ct: ct);


            if (!res.Success)
            {
                _logger.LogWarning("[HARVEST][{symbol}][{side}] FAIL: {err}", symbol, side, res.Error);
                return;
            }

            decimal addToBucket = uPnl * harvestPct;
            st.RealizedPnlBucketUsd += Math.Max(0m, addToBucket);
            st.LastHarvestUtc = DateTime.UtcNow;
            st.HarvestsToday++;

            _logger.LogInformation(
                "[HARVEST][{symbol}][{side}] OK closeQty={q} uPnl={pnl:F2} addBucket={b:F2} edge={e:F2} rr={rr:F2}",
                symbol, side, closeQty, uPnl, addToBucket, aiEdgeScore, rr);

            _recentPartialClose[$"{symbol}|{side}"] =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        }
        public async Task HandleUiActionAsync(PositionActionRequest req)
        {
            var pos = GetTrackedPosition(req.Symbol, req.Side);
            if (pos == null) return;// NOTE: dummy object, real position resolved later via REST

            switch (req.Action)
            {
                case PositionActionType.CloseMarket:
                    await ClosePositionAsync(pos, CloseReason.ManualUi);
                    break;

                case PositionActionType.UpdateStopLoss:
                    await UpdateStopLossAsync(pos, req.Price!.Value, reason: "UI");
                    break;

                case PositionActionType.UpdateTakeProfit:
                    await UpdateTakeProfitAsync(pos, req.Price!.Value, reason: "UI");
                    break;
            }
        }

        private Task UpdateTakeProfitAsync(BinancePositionDetailsUsdt pos, decimal price, string reason)
        {
            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();

                var info = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!info.Success || info.Data == null) return;

                var real = info.Data.FirstOrDefault(p =>
                    p.Symbol == pos.Symbol &&
                    p.PositionSide == pos.PositionSide &&
                    Math.Abs(p.Quantity) > 0);

                if (real == null) return;

                var filters = await _symbolInfo.GetFuturesFiltersAsync(real.Symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;
                var tp = Math.Round(price / tick) * tick;

                // cancel existing TP for side
                var open = await c.UsdFuturesApi.Trading.GetOpenOrdersAsync(real.Symbol, ct: ct);
                if (open.Success && open.Data != null)
                {
                    foreach (var o in open.Data.Where(o => o.Type == FuturesOrderType.TakeProfitMarket && o.PositionSide == real.PositionSide))
                    {
                        try { await c.UsdFuturesApi.Trading.CancelOrderAsync(real.Symbol, o.Id, ct: ct); } catch { }
                    }
                }

                var orderSide = real.PositionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: real.Symbol,
                    side: orderSide,
                    type: FuturesOrderType.TakeProfitMarket,
                    quantity: Math.Abs(real.Quantity),
                    positionSide: real.PositionSide,
                    stopPrice: tp,
                    reduceOnly: null, // ✅ no reduceOnly
                    ct: ct);

                _logger.LogWarning("[UI][TP] {symbol} {side} -> {tp} ({reason})", real.Symbol, real.PositionSide, tp, reason);
                MarkProtection(real.Symbol);
            });

            return Task.CompletedTask;
        }


        private Task UpdateStopLossAsync(BinancePositionDetailsUsdt pos, decimal price, string reason)
        {
            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();

                var info = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!info.Success || info.Data == null) return;

                var real = info.Data.FirstOrDefault(p =>
                    p.Symbol == pos.Symbol &&
                    p.PositionSide == pos.PositionSide &&
                    Math.Abs(p.Quantity) > 0);

                if (real == null) return;

                var filters = await _symbolInfo.GetFuturesFiltersAsync(real.Symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;
                var sl = Math.Round(price / tick) * tick;

                // cancel existing SL for side
                var open = await c.UsdFuturesApi.Trading.GetOpenOrdersAsync(real.Symbol, ct: ct);
                if (open.Success && open.Data != null)
                {
                    foreach (var o in open.Data.Where(o => o.Type == FuturesOrderType.StopMarket && o.PositionSide == real.PositionSide))
                    {
                        try { await c.UsdFuturesApi.Trading.CancelOrderAsync(real.Symbol, o.Id, ct: ct); } catch { }
                    }
                }

                var orderSide = real.PositionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: real.Symbol,
                    side: orderSide,
                    type: FuturesOrderType.StopMarket,
                    quantity: Math.Abs(real.Quantity),
                    positionSide: real.PositionSide,
                    stopPrice: sl,
                    reduceOnly: null, // ✅ no reduceOnly
                    ct: ct);

                _logger.LogWarning("[UI][SL] {symbol} {side} -> {sl} ({reason})", real.Symbol, real.PositionSide, sl, reason);
                MarkProtection(real.Symbol);
            });

            return Task.CompletedTask;
        }


        private Task ClosePositionAsync(
        BinancePositionDetailsUsdt pos,
        CloseReason reason)
        {
            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();

                var info = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!info.Success || info.Data == null)
                    return;

                var real = info.Data.FirstOrDefault(p =>
                    p.Symbol == pos.Symbol &&
                    p.PositionSide == pos.PositionSide &&
                    Math.Abs(p.Quantity) > 0);

                if (real == null)
                    return;

                var closeSide = real.PositionSide == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: real.Symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: Math.Abs(real.Quantity),
                    positionSide: real.PositionSide,
                    ct: ct);

                _logger.LogWarning(
                    "[UI][CLOSE] {symbol} {side} closed by {reason}",
                    real.Symbol, real.PositionSide, reason);
            });

            return Task.CompletedTask;
        }

        private static decimal TruncateByPrecision(decimal value, int precision)
        {
            if (precision < 0) return value;
            var factor = (decimal)Math.Pow(10, precision);
            return Math.Floor(value * factor) / factor;
        }

        private async Task<(decimal qty, decimal trig)> NormalizeForAlgoAsync(
        string symbol,
        decimal qty,
        decimal trigger,
        CancellationToken ct)
        {
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            var step = f.step > 0 ? f.step : 1m;
            var tick = f.tickSize > 0 ? f.tickSize : 0.0001m;

            // step/tick clamp
            var q1 = Math.Floor(qty / step) * step;
            var t1 = Math.Round(trigger / tick) * tick;

          
            // HARD precision clamp (🔥 ключ к -1111)           
            var qtyPrecision = GetPrecisionFromStep(f.step);
            var pricePrecision = GetPrecisionFromStep(f.tickSize);

            var safeQty = Clamp(q1, qtyPrecision);
            var safeTrig = Clamp(t1, pricePrecision);

            return (safeQty, safeTrig);
        }


        private BinancePositionDetailsUsdt? GetTrackedPosition(string symbol, PositionSide side)
        {
            var sKey = EngineState.Key(symbol);

            if (!_engineState.Symbols.TryGetValue(sKey, out _))
                return null;

            // Supervisor уже работает с фактической позицией через REST/WS,
            // UI-action не должен сам решать qty/entry
            // Поэтому просто возвращаем null как "gate"
            // Реальные действия делаются через существующие методы ниже
            return new BinancePositionDetailsUsdt
            {
                Symbol = symbol,
                PositionSide = side
            };
        }
    }
}
