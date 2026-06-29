using System.Text.Json;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Web.Demo;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class DemoAccountService
{
    private readonly MarketDataLiveState _liveState;
    private readonly ILogger<DemoAccountService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();

    private DemoAccountState _state = new();

    // Tracks the most recent price for every symbol we've seen a tick
    // for — needed because checking SL/TP on a tick for symbol X
    // shouldn't require also having live data for every OTHER open
    // demo position's symbol at that exact same moment.
    private readonly Dictionary<string, decimal> _lastPrices = new();

    // ===================== DCA (Dollar-Cost Averaging) =====================
    // Reuses the exact same DcaOptions config and IsCycleDueNow schedule
    // logic as the real Engine-side DcaService, so demo and real DCA
    // can never drift apart in behavior — only the execution side
    // differs (virtual buys here vs a real Binance order there).
    private readonly IOptionsMonitor<VertexAutoTradeBinance8.Configuration.DcaOptions> _dcaOptions;
    private readonly HistoricalDataReaderService _historicalData;
    private readonly string _dcaStatePath;
    private DemoDcaState _dcaState = new();
    private Timer? _dcaTimer;

    public event Action? Updated;

    // Demo mode is shared, global state (not local to any one page's
    // component) so the always-visible sticky header in MainLayout can
    // show and control the toggle, per direct request to move it
    // there - this Singleton service is the natural place for it,
    // reachable from any page.
    public bool DemoMode { get; private set; }
    public event Action? DemoModeChanged;
    public void SetDemoMode(bool enabled)
    {
        if (DemoMode == enabled) return;
        DemoMode = enabled;
        DemoModeChanged?.Invoke();
    }

    public DemoAccountService(
        MarketDataLiveState liveState, ILogger<DemoAccountService> logger, IConfiguration cfg,
        IOptionsMonitor<VertexAutoTradeBinance8.Configuration.DcaOptions> dcaOptions,
        HistoricalDataReaderService historicalData)
    {
        _liveState = liveState;
        _logger = logger;
        _dcaOptions = dcaOptions;
        _historicalData = historicalData;

        // Same simple file-based persistence pattern already used
        // elsewhere in this project (klines_bootstrap.json) — a single
        // JSON file, not a database, matching the project's existing
        // scale and conventions.
        var root = cfg["SharedData:Root"] ?? AppContext.BaseDirectory;
        _filePath = Path.Combine(root, "demo-account.json");
        _dcaStatePath = Path.Combine(root, "demo-dca-state.json");

        Load();
        LoadDcaState();

        _liveState.PriceTicked += OnPriceTicked;

        // Checking once per hour is plenty — DCA schedules are
        // measured in days/weeks, matching the real DcaService's own
        // check interval.
        _dcaTimer = new Timer(_ => _ = CheckDcaCycleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
    }

    public DemoAccountState GetSnapshot()
    {
        lock (_lock)
        {
            // Return a deep-enough copy for read-only display purposes
            // — callers mutate via the explicit methods below, not by
            // poking the returned object directly.
            return new DemoAccountState
            {
                InitialBalance = _state.InitialBalance,
                Balance = _state.Balance,
                Positions = _state.Positions.ToList(),
                PendingOrders = _state.PendingOrders.ToList(),
                History = _state.History.ToList(),
            };
        }
    }

    public decimal GetLastPrice(string symbol)
    {
        lock (_lock) { return _lastPrices.TryGetValue(symbol, out var p) ? p : 0m; }
    }

    // ===================== Account-level actions =====================

    public void ResetAccount(decimal newInitialBalance)
    {
        lock (_lock)
        {
            _state = new DemoAccountState { InitialBalance = newInitialBalance, Balance = newInitialBalance };
            Save();
        }
        _logger.LogInformation("[DEMO] Account reset, initial balance = {bal}", newInitialBalance);
        Updated?.Invoke();
    }

    public void SetInitialBalance(decimal newInitialBalance)
    {
        // Changes the configured starting balance for the NEXT reset,
        // without touching the current running balance — matches the
        // direct request that the current account's running P&L stays
        // real/permanent, while still letting the user configure what
        // a fresh reset starts from.
        lock (_lock)
        {
            _state.InitialBalance = newInitialBalance;
            Save();
        }
        Updated?.Invoke();
    }

    // ===================== Opening positions/orders =====================

    public (bool ok, string error) OpenMarketPosition(
        string symbol, string side, decimal qty, int leverage,
        decimal currentPrice, decimal? stopLoss, List<DemoTpLevel>? takeProfits)
    {
        if (qty <= 0 || currentPrice <= 0) return (false, "Invalid quantity or price");

        lock (_lock)
        {
            decimal margin = (qty * currentPrice) / Math.Max(1, leverage);
            if (margin > _state.Balance)
                return (false, $"Insufficient demo balance: need ${margin:F2}, have ${_state.Balance:F2}");

            // CRITICAL: both Binance and Bybit always track a single
            // position per symbol+side — adding to an existing
            // position averages the entry price (weighted by quantity),
            // it never creates a second record. Replicating that exact
            // behavior here, rather than always creating a brand new
            // DemoPosition, which previously meant buying the same
            // symbol+side twice incorrectly showed up as two separate
            // rows instead of one merged position.
            var existing = _state.Positions.FirstOrDefault(p => p.Symbol == symbol && p.Side == side);
            if (existing != null)
            {
                decimal totalQty = existing.Qty + qty;
                existing.EntryPrice = ((existing.EntryPrice * existing.Qty) + (currentPrice * qty)) / totalQty;
                existing.Qty = totalQty;
                existing.Margin += margin;
                existing.Leverage = leverage; // last-set leverage wins, matching how the real exchange treats leverage as a symbol-level setting, not per-fill
                // SL/TP on an add-to-position stay as whatever the
                // position already had — a fresh add usually doesn't
                // come with new protective levels, and overwriting
                // existing ones here would be a surprising side effect.
                if (stopLoss.HasValue && stopLoss.Value > 0) existing.StopLoss = stopLoss;
                if (takeProfits != null && takeProfits.Count > 0) existing.TakeProfits = takeProfits;
                Save();
                _logger.LogInformation("[DEMO] Added {side} {symbol} qty={qty} @ {price} — merged into existing position, new avg entry {entry}",
                    side, symbol, qty, currentPrice, existing.EntryPrice);
                Updated?.Invoke();
                return (true, "");
            }

            var pos = new DemoPosition
            {
                Symbol = symbol, Side = side, Qty = qty, Leverage = leverage,
                EntryPrice = currentPrice, Margin = margin,
                StopLoss = stopLoss, TakeProfits = takeProfits ?? new(),
            };
            _state.Positions.Add(pos);
            Save();
        }

        _logger.LogInformation("[DEMO] Opened {side} {symbol} qty={qty} @ {price}", side, symbol, qty, currentPrice);
        Updated?.Invoke();
        return (true, "");
    }

    public (bool ok, string error) PlacePendingOrder(
        string symbol, string side, DemoOrderType type, decimal triggerPrice, decimal qty, int leverage,
        decimal? stopLoss, List<DemoTpLevel>? takeProfits)
    {
        if (qty <= 0 || triggerPrice <= 0) return (false, "Invalid quantity or trigger price");

        lock (_lock)
        {
            _state.PendingOrders.Add(new DemoPendingOrder
            {
                Symbol = symbol, Side = side, Type = type, TriggerPrice = triggerPrice,
                Qty = qty, Leverage = leverage, StopLoss = stopLoss, TakeProfits = takeProfits ?? new(),
            });
            Save();
        }

        Updated?.Invoke();
        return (true, "");
    }

    public bool CancelPendingOrder(string id)
    {
        lock (_lock)
        {
            var removed = _state.PendingOrders.RemoveAll(o => o.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    // Updates a position's SL or one of its TP levels (or adds a new
    // TP level when tpIndex is null/out of range) — used by the
    // chart's drag-to-set-TP/SL flow. The proper, explicit way to
    // mutate a position's protective levels, rather than relying on
    // GetSnapshot's returned objects happening to share references
    // with the internal state.
    public bool UpdatePositionProtectiveLevel(string positionId, bool isStopLoss, decimal price, int? tpIndex)
    {
        lock (_lock)
        {
            var pos = _state.Positions.FirstOrDefault(p => p.Id == positionId);
            if (pos == null) return false;

            if (isStopLoss)
            {
                pos.StopLoss = price;
            }
            else if (tpIndex.HasValue && tpIndex.Value >= 0 && tpIndex.Value < pos.TakeProfits.Count)
            {
                pos.TakeProfits[tpIndex.Value].Price = price;
            }
            else
            {
                pos.TakeProfits.Add(new DemoTpLevel { Price = price, Pct = 100m });
            }

            Save();
        }
        Updated?.Invoke();
        return true;
    }

    // ===================== Closing positions =====================

    public (bool ok, string error) ClosePosition(string id, decimal pctToClose = 100m, string reason = "Manual")
    {
        decimal? exitPrice = null;
        lock (_lock)
        {
            var pos = _state.Positions.FirstOrDefault(p => p.Id == id);
            if (pos == null) return (false, "Position not found");
            if (!_lastPrices.TryGetValue(pos.Symbol, out var price) || price <= 0)
                return (false, "No live price available for this symbol yet");
            exitPrice = price;

            ClosePositionInternal(pos, pctToClose, price, reason);
            Save();
        }
        Updated?.Invoke();
        return (true, "");
    }

    // Closes (fully or partially) and books the realized PnL — caller
    // must already hold _lock. Splitting the qty for a partial close
    // creates no new position; it just reduces this one's qty in place
    // and books the proportional PnL, mirroring how a real exchange's
    // partial close behaves.
    private void ClosePositionInternal(DemoPosition pos, decimal pctToClose, decimal exitPrice, string reason)
    {
        decimal closeQty = pctToClose >= 100m ? pos.Qty : pos.Qty * (pctToClose / 100m);
        decimal dir = pos.Side == "LONG" ? 1m : -1m;
        decimal realizedPnl = (exitPrice - pos.EntryPrice) * dir * closeQty;

        _state.Balance += realizedPnl;
        _state.History.Add(new DemoClosedTrade
        {
            Symbol = pos.Symbol, Side = pos.Side, EntryPrice = pos.EntryPrice, ExitPrice = exitPrice,
            Qty = closeQty, RealizedPnl = realizedPnl, CloseReason = reason, OpenedAtUtc = pos.OpenedAtUtc,
        });

        if (pctToClose >= 100m || closeQty >= pos.Qty)
        {
            _state.Positions.Remove(pos);
        }
        else
        {
            // Partial close: shrink the position and its margin
            // proportionally, keep the same entry/SL/TP levels.
            decimal remainingFraction = (pos.Qty - closeQty) / pos.Qty;
            pos.Margin *= remainingFraction;
            pos.Qty -= closeQty;
        }

        _logger.LogInformation(
            "[DEMO] Closed {pct}% of {symbol} {side} @ {exit} ({reason}) — realized PnL {pnl}",
            pctToClose, pos.Symbol, pos.Side, exitPrice, reason, realizedPnl);
    }

    // ===================== Live price monitoring =====================

    private void OnPriceTicked(string symbol, decimal price)
    {
        if (price <= 0) return;

        List<(DemoPosition pos, decimal pct, string reason)>? toClose = null;
        List<DemoPendingOrder>? toFill = null;
        bool changed = false;

        lock (_lock)
        {
            _lastPrices[symbol] = price;

            // 1) Check pending orders for this symbol — did price cross
            // the trigger?
            foreach (var order in _state.PendingOrders.Where(o => o.Symbol == symbol).ToList())
            {
                bool triggered = order.Type == DemoOrderType.Limit
                    ? (order.Side == "LONG" ? price <= order.TriggerPrice : price >= order.TriggerPrice)
                    : (order.Side == "LONG" ? price >= order.TriggerPrice : price <= order.TriggerPrice);

                if (triggered)
                {
                    (toFill ??= new()).Add(order);
                }
            }

            foreach (var order in toFill ?? Enumerable.Empty<DemoPendingOrder>())
            {
                decimal margin = (order.Qty * price) / Math.Max(1, order.Leverage);
                if (margin > _state.Balance)
                {
                    // Can't afford it anymore (balance dropped since
                    // the order was placed) — drop the order rather
                    // than opening a position the demo account can't
                    // actually afford.
                    _state.PendingOrders.Remove(order);
                    continue;
                }

                // CRITICAL: same merge logic as OpenMarketPosition -
                // a filled pending order for a symbol+side that
                // already has an open position must merge into it
                // (weighted-average entry price), not create a
                // second separate row. This exact gap (this path
                // never checked for an existing position at all) is
                // confirmed to be the real cause of a reported case:
                // a market-opened position, then a pending limit
                // order on the same symbol+side filling later through
                // this path, showing up as two separate rows with
                // different leverage instead of one merged position.
                var existingPos = _state.Positions.FirstOrDefault(p => p.Symbol == order.Symbol && p.Side == order.Side);
                if (existingPos != null)
                {
                    decimal totalQty = existingPos.Qty + order.Qty;
                    existingPos.EntryPrice = ((existingPos.EntryPrice * existingPos.Qty) + (price * order.Qty)) / totalQty;
                    existingPos.Qty = totalQty;
                    existingPos.Margin += margin;
                    existingPos.Leverage = order.Leverage;
                    if (order.StopLoss.HasValue && order.StopLoss.Value > 0) existingPos.StopLoss = order.StopLoss;
                    if (order.TakeProfits != null && order.TakeProfits.Count > 0) existingPos.TakeProfits = order.TakeProfits;
                }
                else
                {
                    _state.Positions.Add(new DemoPosition
                    {
                        Symbol = order.Symbol, Side = order.Side, Qty = order.Qty, Leverage = order.Leverage,
                        EntryPrice = price, Margin = margin, StopLoss = order.StopLoss, TakeProfits = order.TakeProfits,
                    });
                }
                _state.PendingOrders.Remove(order);
                changed = true;
            }

            // 2) Check open positions for this symbol — did price hit
            // SL or any TP level?
            foreach (var pos in _state.Positions.Where(p => p.Symbol == symbol).ToList())
            {
                bool isLong = pos.Side == "LONG";

                if (pos.StopLoss.HasValue && pos.StopLoss.Value > 0)
                {
                    bool slHit = isLong ? price <= pos.StopLoss.Value : price >= pos.StopLoss.Value;
                    if (slHit)
                    {
                        (toClose ??= new()).Add((pos, 100m, "SL"));
                        continue; // SL closes the whole remaining position — no need to also check TPs
                    }
                }

                foreach (var tp in pos.TakeProfits.Where(t => t.Price > 0).OrderBy(t => isLong ? t.Price : -t.Price).ToList())
                {
                    bool tpHit = isLong ? price >= tp.Price : price <= tp.Price;
                    if (tpHit)
                    {
                        (toClose ??= new()).Add((pos, tp.Pct, $"TP {fmtPrice(tp.Price)}"));
                        pos.TakeProfits.Remove(tp); // this level is consumed — don't trigger it again
                    }
                }
            }

            foreach (var (pos, pct, reason) in toClose ?? Enumerable.Empty<(DemoPosition, decimal, string)>())
            {
                if (_state.Positions.Contains(pos)) // might already be gone if a prior iteration fully closed it
                {
                    ClosePositionInternal(pos, pct, price, reason);
                    changed = true;
                }
            }

            if (changed) Save();
        }

        if (changed) Updated?.Invoke();

        static string fmtPrice(decimal p) => p.ToString("G8");
    }

    // ===================== Persistence =====================

    public DemoDcaState GetDcaSnapshot()
    {
        lock (_lock) { return new DemoDcaState { LastCycleUtc = _dcaState.LastCycleUtc, History = _dcaState.History.ToList() }; }
    }

    private async Task CheckDcaCycleAsync()
    {
        try
        {
            var opts = _dcaOptions.CurrentValue;
            if (!opts.Enabled || opts.Symbols.Count == 0) return;

            DateTime lastCycle;
            lock (_lock) { lastCycle = _dcaState.LastCycleUtc; }
            if (!VertexAutoTradeBinance8.Services.DcaService.IsCycleDueNow(opts.Schedule, lastCycle, DateTime.UtcNow)) return;

            _logger.LogInformation("[DEMO-DCA] Scheduled cycle starting — {count} symbols, budget {budget} USDT",
                opts.Symbols.Count, opts.Schedule.BudgetPerCycle);

            bool weighted = string.Equals(opts.AllocationMode, "Weighted", StringComparison.OrdinalIgnoreCase);
            decimal totalWeight = weighted ? opts.Symbols.Sum(s => Math.Max(0.0001m, s.Weight)) : opts.Symbols.Count;

            foreach (var entry in opts.Symbols)
            {
                decimal share = weighted ? Math.Max(0.0001m, entry.Weight) / totalWeight : 1m / opts.Symbols.Count;
                decimal usdtAmount = opts.Schedule.BudgetPerCycle * share;

                decimal price = GetLastPrice(entry.Symbol);
                if (price <= 0)
                {
                    _logger.LogWarning("[DEMO-DCA] No live price yet for {symbol} — skipping this symbol this cycle", entry.Symbol);
                    continue;
                }

                bool dipBonusApplied = false;
                if (opts.DipBonus.Enabled)
                {
                    decimal? oldPrice = await TryGetDemoPriceHoursAgoAsync(entry.Symbol, opts.DipBonus.LookbackHours);
                    if (oldPrice.HasValue && oldPrice.Value > 0)
                    {
                        decimal dropPct = (oldPrice.Value - price) / oldPrice.Value * 100m;
                        if (dropPct >= opts.DipBonus.DropThresholdPct)
                        {
                            usdtAmount *= opts.DipBonus.Multiplier;
                            dipBonusApplied = true;
                        }
                    }
                }

                decimal qty = price > 0 ? usdtAmount / price : 0m;
                if (qty <= 0) continue;

                // DCA is spot-style accumulation, not a leveraged
                // directional bet — leverage 1, matching the real
                // Engine-side DcaService's own approach.
                var (ok, error) = OpenMarketPosition(entry.Symbol, "LONG", qty, 1, price, null, null);
                if (!ok)
                {
                    _logger.LogWarning("[DEMO-DCA] Buy failed for {symbol}: {error}", entry.Symbol, error);
                    continue;
                }

                lock (_lock)
                {
                    _dcaState.History.Insert(0, new DemoDcaPurchaseRecord
                    {
                        Symbol = entry.Symbol, TimeUtc = DateTime.UtcNow, Price = price,
                        Qty = qty, UsdtSpent = usdtAmount, DipBonusApplied = dipBonusApplied,
                    });
                    if (_dcaState.History.Count > 500) _dcaState.History = _dcaState.History.Take(500).ToList();
                }

                _logger.LogInformation("[DEMO-DCA] Bought {qty} {symbol} @ {price} ({amount} USDT){dip}",
                    qty, entry.Symbol, price, usdtAmount, dipBonusApplied ? " [DIP BONUS]" : "");
            }

            lock (_lock) { _dcaState.LastCycleUtc = DateTime.UtcNow; }
            SaveDcaState();
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DEMO-DCA] Cycle check failed");
        }
    }

    private async Task<decimal?> TryGetDemoPriceHoursAgoAsync(string symbol, int hoursAgo)
    {
        try
        {
            // Reads the same historical archive already powering the
            // chart (via HistoricalDataReaderService) — demo mode has
            // no real Binance client of its own for a fresh kline
            // fetch, but doesn't need one since this data already
            // exists locally.
            var klines = await _historicalData.LoadAsync(symbol, "1h");
            if (klines == null || klines.Count == 0) return null;
            int idx = Math.Max(0, klines.Count - 1 - hoursAgo);
            return klines[idx].Close;
        }
        catch
        {
            return null;
        }
    }

    private void LoadDcaState()
    {
        try
        {
            if (File.Exists(_dcaStatePath))
            {
                var json = File.ReadAllText(_dcaStatePath);
                var loaded = JsonSerializer.Deserialize<DemoDcaState>(json);
                if (loaded != null) _dcaState = loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEMO-DCA] Failed to load demo-dca-state.json — starting fresh");
        }
    }

    private void SaveDcaState()
    {
        try
        {
            var json = JsonSerializer.Serialize(_dcaState, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _dcaStatePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _dcaStatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEMO-DCA] Failed to save demo-dca-state.json");
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<DemoAccountState>(json);
                if (loaded != null) _state = loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEMO] Failed to load demo-account.json — starting fresh");
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEMO] Failed to save demo-account.json");
        }
    }
}
