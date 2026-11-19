using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// PositionSupervisorService v8 — сопровождение и фиксация прибыли bot-managed позиций.
    ///
    /// Что было сломано в v7.3 (2121 строк, ~60% мёртвого кода):
    ///  1) Детектор закрытия НИКОГДА не срабатывал: условие qty==0 проверялось только
    ///     внутри ветки «позиция открыта». → закрытия не попадали в журнал, не работал
    ///     post-close cooldown, AI не учился, PnL в UI пустой.
    ///  2) Для каждого «пустого» символа: 10 ретраев × 200 мс (2 с простоя) + отмена ВСЕХ
    ///     ордеров символа каждые ~секунды (включая ручные ордера пользователя).
    ///  3) EnsureThreeTakeProfits: после TP1+TP2 оставался 1 TP → он отменялся и ставилась
    ///     НОВАЯ лестница от исходных TP1/TP2 (уже пройденных) → runner закрывался сразу.
    ///  4) HYBRID harvest: каждые 6 минут рынком срезалось 18–40% позиции при «1R»,
    ///     где R считался по ATR(1m), а не по реальному SL. Прибыль резалась копейками,
    ///     убыток шёл полным стопом.
    ///  5) SL на фиксированный qty → после частичных фиксаций qty SL > позиции.
    ///
    /// v8 логика:
    ///  • Один снимок позиций на все символы (AccountStateCache).
    ///  • Сопровождаются ТОЛЬКО позиции из ManagedPositionRegistry (ручные не трогаем).
    ///  • SL всегда есть (closePosition). Нет SL → восстановить; уровень уже пройден → выйти.
    ///  • TP лестница ставится один раз; существующие TP не пересобираются.
    ///  • После TP1 → SL в безубыток (+буфер комиссий). После TP2 → SL на уровень TP1.
    ///  • Закрытие: реальный PnL из userTrades (realizedPnl − комиссии) → журнал, AI, cooldown.
    /// </summary>
    public class PositionSupervisorService
    {
        private readonly ILogger<PositionSupervisorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly AiSelfLearningService _aiLearning;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _regime;
        private readonly ExecutedSignalService _journal;
        private readonly ManagedPositionRegistry _managed;
        private readonly TradeSignalMemoryService _signalMemory;
        private readonly AccountStateCache _account;
        private readonly OrderExecutor _executor;
        private readonly PositionGuardService _guard;
        private readonly TradingOptions _options;

        private readonly Dictionary<string, DateTime> _lastSupervised = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _missingConfirm = new(StringComparer.OrdinalIgnoreCase);

        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ExecutedSignalService executed,
            ManagedPositionRegistry managed,
            TradeSignalMemoryService signalMemory,
            AccountStateCache account,
            OrderExecutor executor,
            PositionGuardService guard,
            TradingOptions options)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _aiLearning = aiLearning;
            _marketData = marketData;
            _regime = regime;
            _journal = executed;
            _managed = managed;
            _signalMemory = signalMemory;
            _account = account;
            _executor = executor;
            _guard = guard;
            _options = options;
        }

        /// <summary>Совместимость: сопровождение managed-позиций символа.</summary>
        public Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
            => SuperviseSymbolAsync(symbol, ct, force: lastSignal != null);

        /// <summary>
        /// Сопровождение ВСЕХ managed-позиций (в т.ч. символов, выпавших из ротации ActiveSymbols —
        /// раньше такие позиции навсегда оставались без присмотра).
        /// </summary>
        public async Task SuperviseAllAsync(CancellationToken ct)
        {
            var symbols = _managed.Snapshot().Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var s in symbols)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await SuperviseSymbolAsync(s, ct, force: false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SUPERVISOR][{symbol}] supervise failed", s);
                }
            }
        }

        private async Task SuperviseSymbolAsync(string symbol, CancellationToken ct, bool force)
        {
            // троттлинг: не чаще раза в 3 секунды на символ
            if (!force && _lastSupervised.TryGetValue(symbol, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(3))
                return;
            _lastSupervised[symbol] = DateTime.UtcNow;

            foreach (var side in new[] { PositionSide.Long, PositionSide.Short })
            {
                var info = _managed.Get(symbol, side);
                if (info == null) continue;
                if (info.AllowManualOverride || info.UserClearedProtection) continue;

                var positions = await _account.GetOpenPositionsAsync(ct, forceRefresh: force);
                if (!_account.IsFresh)
                {
                    _logger.LogWarning("[SUPERVISOR] account snapshot stale — skip cycle (fail-safe)");
                    return;
                }

                var pos = positions.FirstOrDefault(p =>
                    string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && p.PositionSide == side);

                var key = symbol + "|" + side;
                if (pos == null || pos.Quantity == 0m)
                {
                    // подтверждаем закрытие двумя снимками (защита от «пустого» ответа биржи)
                    _missingConfirm[key] = _missingConfirm.GetValueOrDefault(key) + 1;
                    if (_missingConfirm[key] < 2)
                    {
                        _account.Invalidate();
                        continue;
                    }
                    _missingConfirm.Remove(key);
                    await HandleClosedAsync(info, ct);
                    continue;
                }

                _missingConfirm.Remove(key);
                await ManageOpenAsync(info, pos, ct);
            }
        }

        // =====================================================================
        // OPEN POSITION MANAGEMENT
        // =====================================================================
        private async Task ManageOpenAsync(ManagedPositionRegistry.ManagedInfo info, BinancePositionDetailsUsdt pos, CancellationToken ct)
        {
            var client = _factory.CreateRestClient();
            string symbol = info.Symbol;
            var side = info.Side;
            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            int dir = side == PositionSide.Long ? 1 : -1;

            decimal qty = Math.Abs(pos.Quantity);
            decimal entry = pos.EntryPrice > 0 ? pos.EntryPrice : info.EntryPrice;
            decimal mark = pos.MarkPrice > 0 ? pos.MarkPrice : entry;

            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = f.tickSize > 0 ? f.tickSize : 0.0001m;
            decimal step = f.step > 0 ? f.step : 0.001m;
            decimal minQty = f.minQty > 0 ? f.minQty : step;

            bool dirty = false;
            if (info.InitialQty <= 0) { info.InitialQty = qty; dirty = true; }
            if (qty > info.PeakQty) { info.PeakQty = qty; dirty = true; }
            if (info.InitialSL <= 0 && info.CalculatedSL > 0) { info.InitialSL = info.CalculatedSL; dirty = true; }

            // ---------- текущие защитные ордера ----------
            var (slOrders, tpOrders) = await LoadProtectiveAsync(client, symbol, side, closeSide, ct);
            decimal currentSl = slOrders.Count > 0 ? slOrders.Max(o => o.Trigger * dir) * dir : 0m; // самый «защитный»

            // ---------- 1) SL должен существовать ----------
            if (slOrders.Count == 0)
            {
                if (!_managed.CanAttemptRestore(symbol, side))
                {
                    _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] SL restore paused (too many fails)", symbol, side);
                }
                else
                {
                    decimal sl = info.CalculatedSL;
                    if (sl <= 0)
                        sl = await FallbackSlAsync(symbol, side, entry);
                    sl = SymbolInfoService.RoundToTick(sl, tick);

                    bool crossed = side == PositionSide.Long ? mark <= sl : mark >= sl;
                    if (crossed)
                    {
                        _logger.LogError("[SUPERVISOR][{symbol}][{side}] no SL and price {m} beyond SL {sl} → EMERGENCY CLOSE",
                            symbol, side, mark, sl);
                        await _executor.EmergencyCloseAsync(client, symbol, side, qty, ct);
                        return;
                    }

                    bool ok = await _executor.PlaceStopLossAsync(client, symbol, side, sl, ct);
                    _managed.MarkRestoreAttempt(symbol, side, ok);
                    if (ok)
                    {
                        currentSl = sl;
                        _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] SL RESTORED @ {sl}", symbol, side, sl);
                    }
                }
            }
            else if (slOrders.Count > 1)
            {
                // дубликаты SL (наследие старых версий) — оставляем самый защитный
                foreach (var dup in slOrders.OrderByDescending(o => o.Trigger * dir).Skip(1))
                    await CancelAsync(client, symbol, dup, ct);
            }

            // ---------- 2) Стадии сопровождения ----------
            var split = _options.EffectiveTpSplit;
            decimal peak = info.PeakQty > 0 ? info.PeakQty : qty;
            decimal filledFrac = peak > 0 ? 1m - qty / peak : 0m;

            decimal? targetSl = null;
            int targetStage = info.Stage;

            if (info.Stage < 2 && _options.LockProfitAfterTp2 && info.CalculatedTPs.Count >= 1 &&
                filledFrac >= (split[0] + split[1]) * 0.90m)
            {
                targetSl = info.CalculatedTPs[0];
                targetStage = 2;
            }
            else if (info.Stage < 1 && _options.BreakEvenAfterTp1 && filledFrac >= split[0] * 0.90m)
            {
                decimal buf = entry * Math.Max(0m, _options.BreakEvenBufferPercent) / 100m;
                targetSl = entry + dir * buf;
                targetStage = 1;
            }

            if (targetSl.HasValue)
            {
                decimal newSl = SymbolInfoService.RoundToTick(targetSl.Value, tick);
                bool improves = currentSl <= 0 || (dir * (newSl - currentSl) > 0);
                bool valid = dir * (mark - newSl) > tick * 2; // SL должен быть с «правильной» стороны цены

                if (improves && valid)
                {
                    bool moved = await MoveStopAsync(client, symbol, side, slOrders, newSl, currentSl, ct);
                    if (moved)
                    {
                        _logger.LogInformation("[SUPERVISOR][{symbol}][{side}] STAGE {from}→{to}: SL {old} → {new} (filled {f:P0})",
                            symbol, side, info.Stage, targetStage, currentSl, newSl, filledFrac);
                        info.Stage = targetStage;
                        info.CalculatedSL = newSl;
                        currentSl = newSl;
                        dirty = true;
                    }
                }
                else if (!improves)
                {
                    info.Stage = targetStage; // SL уже лучше — просто фиксируем стадию
                    dirty = true;
                }
            }

            // ---------- 3) TP: ставим только если их нет совсем ----------
            if (tpOrders.Count == 0 && info.CalculatedTPs.Count > 0)
            {
                var remainingTps = info.CalculatedTPs
                    .Select(tp => SymbolInfoService.RoundToTick(tp, tick))
                    .Where(tp => dir * (tp - mark) > tick)
                    .ToList();

                int alreadyHit = info.CalculatedTPs.Count - remainingTps.Count;
                if (remainingTps.Count > 0 && _managed.CanAttemptRestore(symbol, side))
                {
                    var tail = split.Skip(Math.Max(0, alreadyHit)).Take(remainingTps.Count).ToArray();
                    int ok = await OrderExecutor.PlaceTpLadderAsync(client, symbol, side, qty, remainingTps, step, minQty,
                        PadSplit(tail), _logger, ct);
                    _managed.MarkRestoreAttempt(symbol, side, ok > 0);
                    _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] TP restored: {n} level(s)", symbol, side, ok);
                }
            }

            if (dirty) _managed.Save(info);
        }

        private static decimal[] PadSplit(decimal[] s)
            => s.Length >= 3 ? s : s.Concat(Enumerable.Repeat(0m, 3 - s.Length)).ToArray();

        // =====================================================================
        // CLOSE DETECTION
        // =====================================================================
        private async Task HandleClosedAsync(ManagedPositionRegistry.ManagedInfo info, CancellationToken ct)
        {
            var client = _factory.CreateRestClient();
            var symbol = info.Symbol;
            var side = info.Side;
            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

            decimal pnl = 0m, fees = 0m, exitQty = 0m, exitNotional = 0m;
            try
            {
                var since = info.OpenedAt.AddMinutes(-2);
                var trades = await client.UsdFuturesApi.Trading.GetUserTradesAsync(symbol, startTime: since, ct: ct);
                if (trades.Success && trades.Data != null)
                {
                    foreach (var t in trades.Data.Where(t => t.PositionSide == side))
                    {
                        pnl += t.RealizedPnl;
                        fees += t.Fee;
                        if (t.Side == closeSide)
                        {
                            exitQty += t.Quantity;
                            exitNotional += t.Quantity * t.Price;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR][{symbol}] userTrades fetch failed — PnL approximated", symbol);
            }

            decimal exitPrice = exitQty > 0 ? exitNotional / exitQty : info.EntryPrice;
            decimal netPnl = pnl - fees;
            decimal baseNotional = info.EntryPrice * Math.Max(info.PeakQty, info.InitialQty);
            decimal roi = baseNotional > 0 ? netPnl / baseNotional * 100m : 0m;

            var status = netPnl >= 0 ? TradeExecutionStatus.PositionClosedTp : TradeExecutionStatus.PositionClosedSl;
            string reason = info.Stage switch
            {
                2 => "Runner/LockedTP1",
                1 => netPnl >= 0 ? "TP/BreakEven" : "BreakEvenStop",
                _ => netPnl >= 0 ? "TakeProfit" : "StopLoss"
            };

            bool journaled = !string.IsNullOrWhiteSpace(info.ExecRecordId) &&
                             _journal.UpdateById(info.ExecRecordId!, status, exitPrice: exitPrice, pnl: netPnl, roi: roi, fees: fees, closeReason: reason);
            if (!journaled)
                _journal.CloseLatestOpen(symbol, sigSide, status, exitPrice, netPnl, roi, fees, reason);

            try
            {
                var kl = await _marketData.GetKlines(symbol, KlineInterval.FiveMinutes, 200);
                var regime = kl.Count > 30 ? _regime.DetectRegime(symbol, KlineInterval.FiveMinutes, kl).Regime : MarketRegime.Range;
                _aiLearning.RecordTrade(symbol, sigSide, info.EntryPrice, exitPrice, regime);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[SUPERVISOR] AI record failed");
            }

            // остатки защитных ордеров этой стороны (TP, которые не исполнились)
            await _executor.CancelProtectiveOrdersAsync(client, symbol, side, closeSide, ct);

            _managed.Unregister(symbol, side);
            if (!_managed.IsManagedAny(symbol))
            {
                _signalMemory.Clear(symbol);
                _guard.ResetSymbol(symbol);
            }

            _logger.LogWarning(
                "[SUPERVISOR][{symbol}][{side}] CLOSED → {st} net={pnl:F4} USDT (fees {fees:F4}) roi={roi:F2}% exit≈{exit} reason={reason}",
                symbol, side, status, netPnl, fees, roi, exitPrice, reason);
        }

        // =====================================================================
        // HELPERS
        // =====================================================================
        private sealed record ProtOrder(long Id, bool IsAlgo, decimal Trigger, decimal Qty);

        private async Task<(List<ProtOrder> sl, List<ProtOrder> tp)> LoadProtectiveAsync(
            BinanceRestClient client, string symbol, PositionSide side, OrderSide closeSide, CancellationToken ct)
        {
            var sl = new List<ProtOrder>();
            var tp = new List<ProtOrder>();

            var algo = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
            if (algo.Success && algo.Data != null)
            {
                foreach (var a in algo.Data.Where(a => a.Side == closeSide && a.PositionSide == side))
                {
                    var trig = a.TriggerPrice ?? 0m;
                    if (a.Type is FuturesOrderType.StopMarket or FuturesOrderType.Stop)
                        sl.Add(new ProtOrder(a.Id, true, trig, a.Quantity));
                    else if (a.Type is FuturesOrderType.TakeProfitMarket or FuturesOrderType.TakeProfit)
                        tp.Add(new ProtOrder(a.Id, true, trig, a.Quantity));
                }
            }

            var open = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
            if (open.Success && open.Data != null)
            {
                foreach (var o in open.Data.Where(o => o.Side == closeSide && o.PositionSide == side))
                {
                    if (o.Type is FuturesOrderType.StopMarket or FuturesOrderType.Stop)
                        sl.Add(new ProtOrder(o.Id, false, o.StopPrice ?? 0m, o.Quantity));
                    else if (o.Type is FuturesOrderType.TakeProfitMarket or FuturesOrderType.TakeProfit ||
                             (o.Type == FuturesOrderType.Limit && o.ReduceOnly))
                        tp.Add(new ProtOrder(o.Id, false, o.StopPrice is > 0 ? o.StopPrice.Value : o.Price, o.Quantity));
                }
            }

            return (sl, tp);
        }

        private async Task CancelAsync(BinanceRestClient client, string symbol, ProtOrder o, CancellationToken ct)
        {
            try
            {
                if (o.IsAlgo) await client.UsdFuturesApi.Trading.CancelConditionalOrderAsync(orderId: o.Id, ct: ct);
                else await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR][{symbol}] cancel {id} failed", symbol, o.Id);
            }
        }

        /// <summary>
        /// Перенос SL: отмена старого → новый; если новый не встал — вернуть старый.
        /// (closePosition-стоп на сторону может быть только один.)
        /// </summary>
        private async Task<bool> MoveStopAsync(
            BinanceRestClient client, string symbol, PositionSide side,
            List<ProtOrder> oldStops, decimal newSl, decimal oldSl, CancellationToken ct)
        {
            foreach (var o in oldStops)
                await CancelAsync(client, symbol, o, ct);

            if (await _executor.PlaceStopLossAsync(client, symbol, side, newSl, ct))
                return true;

            _logger.LogError("[SUPERVISOR][{symbol}][{side}] new SL {sl} failed — restoring old {old}", symbol, side, newSl, oldSl);
            if (oldSl > 0 && await _executor.PlaceStopLossAsync(client, symbol, side, oldSl, ct))
                return false;

            _logger.LogCritical("[SUPERVISOR][{symbol}][{side}] could not restore SL → EMERGENCY CLOSE", symbol, side);
            await _executor.EmergencyCloseAsync(client, symbol, side, 0m, ct);
            return false;
        }

        private async Task<decimal> FallbackSlAsync(string symbol, PositionSide side, decimal entry)
        {
            try
            {
                var kl = await _marketData.GetKlines(symbol, KlineInterval.FiveMinutes, 100);
                var atr = _marketData.CalculateAtr(kl, 14);
                if (atr > 0)
                    return side == PositionSide.Long ? entry - atr * 2.0m : entry + atr * 2.0m;
            }
            catch { }
            return side == PositionSide.Long ? entry * 0.985m : entry * 1.015m;
        }
    }
}
