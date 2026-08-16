using System.Text.Json;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Web.Demo;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class DemoAccountService
{
    private readonly MarketDataLiveState _liveState;
    private readonly ILogger<DemoAccountService> _logger;
    private readonly string _accountsDir;
    private readonly object _lock = new();

    private string _clientId = "";
    private string _filePath = "";
    private string _dcaStatePath = "";

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

    /// <summary>Currently bound client id (empty if none).</summary>
    public string BoundClientId => _clientId;

    /// <summary>
    /// Bind demo ledger to a registered user. Loads their isolated
    /// demo-account.json. Call on login / session restore; Unbind on logout.
    /// </summary>
    public void BindClient(string clientId, decimal? preferredInitialBalance = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            UnbindClient();
            return;
        }

        lock (_lock)
        {
            if (_clientId == clientId && !string.IsNullOrEmpty(_filePath))
                return; // already bound

            // Persist previous user before switching
            if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_filePath))
            {
                Save();
                SaveDcaState();
            }

            _clientId = clientId;
            var clientDir = Path.Combine(_accountsDir, $"client_{clientId}");
            try { Directory.CreateDirectory(clientDir); } catch { }

            _filePath = Path.Combine(clientDir, "demo-account.json");
            _dcaStatePath = Path.Combine(clientDir, "demo-dca-state.json");

            _state = new DemoAccountState();
            _dcaState = new DemoDcaState();
            Load();
            LoadDcaState();

            // Seed initial balance for brand-new demo ledger
            if (!File.Exists(_filePath))
            {
                var seed = preferredInitialBalance is > 0 ? preferredInitialBalance.Value : 10_000m;
                _state.InitialBalance = seed;
                _state.Balance = seed;
                Save();
            }
        }

        _logger.LogInformation("[DEMO] Bound client {id} balance={bal:F2}", clientId, _state.Balance);
        Updated?.Invoke();
    }

    public void UnbindClient()
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_filePath))
            {
                Save();
                SaveDcaState();
            }
            _clientId = "";
            _filePath = "";
            _dcaStatePath = "";
            _state = new DemoAccountState();
            _dcaState = new DemoDcaState();
        }
        Updated?.Invoke();
    }

    private bool EnsureBound()
    {
        if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_filePath))
        {
            _logger.LogWarning("[DEMO] Operation ignored — no client bound (login required)");
            return false;
        }
        return true;
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

        // Per-user demo state under engines root:
        //   {EnginesRoot}/client_{id}/demo-account.json
        //   {EnginesRoot}/client_{id}/demo-dca-state.json
        // Shared demo-account.json is NO LONGER used — each registered
        // user has an isolated virtual balance and positions.
        var enginesRoot = cfg["SharedData:EnginesRoot"];
        if (string.IsNullOrWhiteSpace(enginesRoot))
        {
            var legacy = cfg["SharedData:Root"] ?? AppContext.BaseDirectory;
            enginesRoot = Path.GetDirectoryName(legacy.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                          ?? AppContext.BaseDirectory;
        }
        _accountsDir = enginesRoot;
        try { Directory.CreateDirectory(_accountsDir); } catch { }

        // Start unbound — BindClient(userId) loads that user's demo ledger.
        _clientId = "";
        _filePath = "";
        _dcaStatePath = "";
        _state = new DemoAccountState();
        _dcaState = new DemoDcaState();

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

    /// <summary>Margin locked in open positions.</summary>
    public decimal GetUsedMargin()
    {
        lock (_lock) return _state.Positions.Sum(p => p.Margin);
    }

    /// <summary>Available to open = wallet − used margin.</summary>
    public decimal GetAvailableBalance()
    {
        lock (_lock)
        {
            var used = _state.Positions.Sum(p => p.Margin);
            return Math.Max(0m, _state.Balance - used);
        }
    }

    /// <summary>Equity = wallet + unrealized PnL at last known marks.</summary>
    public decimal GetEquity()
    {
        lock (_lock)
        {
            decimal uPnL = 0m;
            foreach (var p in _state.Positions)
            {
                var mark = _lastPrices.TryGetValue(p.Symbol, out var px) && px > 0 ? px : p.EntryPrice;
                var dir = p.Side == "LONG" ? 1m : -1m;
                uPnL += (mark - p.EntryPrice) * dir * p.Qty;
            }
            return _state.Balance + uPnL;
        }
    }

    /// <summary>
    /// Equity for any client id (reads that client's demo-account.json without rebinding session).
    /// Used by parallel DEMO auto-trade sizing.
    /// </summary>
    public decimal GetEquityForClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return 10_000m;
        if (string.Equals(_clientId, clientId, StringComparison.OrdinalIgnoreCase))
            return GetEquity();

        try
        {
            var path = Path.Combine(_accountsDir, $"client_{clientId}", "demo-account.json");
            if (!File.Exists(path)) return 10_000m;
            var state = System.Text.Json.JsonSerializer.Deserialize<DemoAccountState>(File.ReadAllText(path));
            if (state == null) return 10_000m;
            // Without live marks, equity ≈ wallet (unrealized ~0)
            return state.Balance > 0 ? state.Balance : 10_000m;
        }
        catch
        {
            return 10_000m;
        }
    }

    // ===================== Account-level actions =====================

    public void ResetAccount(decimal newInitialBalance)
    {
        if (!EnsureBound()) return;
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
        if (!EnsureBound()) return (false, "Войдите в аккаунт для Demo-торговли.");
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


    /// <summary>
    /// Open demo position for a specific user without switching the session bind.
    /// Enables parallel DEMO auto-trade while the user stays in LIVE mode.
    /// </summary>
    public (bool ok, string error) OpenMarketPositionForClient(
        string clientId,
        string symbol, string side, decimal qty, int leverage,
        decimal currentPrice, decimal? stopLoss, List<DemoTpLevel>? takeProfits)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return (false, "clientId required");
        if (qty <= 0 || currentPrice <= 0)
            return (false, "Invalid quantity or price");

        lock (_lock)
        {
            var clientDir = Path.Combine(_accountsDir, $"client_{clientId}");
            try { Directory.CreateDirectory(clientDir); } catch { }
            var path = Path.Combine(clientDir, "demo-account.json");

            DemoAccountState state;
            try
            {
                if (File.Exists(path))
                {
                    state = JsonSerializer.Deserialize<DemoAccountState>(File.ReadAllText(path))
                            ?? new DemoAccountState { InitialBalance = 10_000m, Balance = 10_000m, AccountingVersion = 1 };
                }
                else
                {
                    state = new DemoAccountState { InitialBalance = 10_000m, Balance = 10_000m, AccountingVersion = 1 };
                }
            }
            catch
            {
                state = new DemoAccountState { InitialBalance = 10_000m, Balance = 10_000m, AccountingVersion = 1 };
            }

            // Migrate legacy v0 accounting (margin was burned out of wallet)
            if (state.AccountingVersion < 1)
            {
                decimal locked = state.Positions.Sum(p => p.Margin);
                if (locked > 0) state.Balance += locked;
                state.AccountingVersion = 1;
            }

            decimal margin = (qty * currentPrice) / Math.Max(1, leverage);
            decimal available = state.Balance - state.Positions.Sum(p => p.Margin);
            if (margin > available)
                return (false, $"Insufficient available balance: need ${margin:F2}, available ${available:F2} (wallet ${state.Balance:F2})");

            var existing = state.Positions.FirstOrDefault(p => p.Symbol == symbol && p.Side == side);
            if (existing != null)
            {
                decimal totalQty = existing.Qty + qty;
                existing.EntryPrice = ((existing.EntryPrice * existing.Qty) + (currentPrice * qty)) / totalQty;
                existing.Qty = totalQty;
                existing.Margin += margin;
                existing.Leverage = leverage;
                if (stopLoss.HasValue && stopLoss.Value > 0) existing.StopLoss = stopLoss;
                if (takeProfits != null && takeProfits.Count > 0) existing.TakeProfits = takeProfits;
            }
            else
            {
                // Wallet model: margin is locked inside the position, not deducted from Balance.
                state.Positions.Add(new DemoPosition
                {
                    Symbol = symbol,
                    Side = side,
                    Qty = qty,
                    Leverage = leverage,
                    EntryPrice = currentPrice,
                    Margin = margin,
                    StopLoss = stopLoss,
                    TakeProfits = takeProfits ?? new(),
                });
            }

            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                File.Copy(tmp, path, overwrite: true);
                try { File.Delete(tmp); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DEMO] OpenForClient save failed {id}", clientId);
                return (false, "save failed");
            }

            // Keep session state in sync if this is the bound user
            if (_clientId == clientId)
                _state = state;

            _logger.LogInformation("[DEMO-PARALLEL] {id} {side} {symbol} qty={qty} @ {price}",
                clientId, side, symbol, qty, currentPrice);
            Updated?.Invoke();
            return (true, "");
        }
    }

    public (bool ok, string error) PlacePendingOrder(
        string symbol, string side, DemoOrderType type, decimal triggerPrice, decimal qty, int leverage,
        decimal? stopLoss, List<DemoTpLevel>? takeProfits)
    {
        if (!EnsureBound()) return (false, "Войдите в аккаунт для Demo-торговли.");
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
    public bool UpdatePositionProtectiveLevel(string positionId, bool isStopLoss, decimal price, int? tpIndex, decimal? newTpQty = null)
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
                // Adding a genuinely NEW level (no specific index given)
                // - per direct confirmation, dragging from the Entry
                // line always creates a new TP rather than replacing
                // one. newTpQty lets the caller specify how much of the
                // position this new level covers (e.g. from the
                // requested-percentage prompt) instead of always
                // defaulting to the full position.
                decimal pctOfPosition = pos.Qty > 0 && newTpQty.HasValue
                    ? Math.Clamp(newTpQty.Value / pos.Qty * 100m, 1m, 100m)
                    : 100m;
                pos.TakeProfits.Add(new DemoTpLevel { Price = price, Pct = pctOfPosition });
            }

            Save();
        }
        Updated?.Invoke();
        return true;
    }

    // Per direct request for a per-level cancel button on the chart's
    // SL/TP pills - removes just one specific level.
    public bool RemoveStopLoss(string positionId)
    {
        lock (_lock)
        {
            var pos = _state.Positions.FirstOrDefault(p => p.Id == positionId);
            if (pos == null) return false;
            pos.StopLoss = 0m;
            Save();
        }
        Updated?.Invoke();
        return true;
    }

    public bool RemoveTakeProfitLevel(string positionId, int tpIndex)
    {
        lock (_lock)
        {
            var pos = _state.Positions.FirstOrDefault(p => p.Id == positionId);
            if (pos == null) return false;
            if (tpIndex < 0 || tpIndex >= pos.TakeProfits.Count) return false;
            pos.TakeProfits.RemoveAt(tpIndex);
            Save();
        }
        Updated?.Invoke();
        return true;
    }

    // ===================== Closing positions =====================

    public (bool ok, string error) ClosePosition(string id, decimal pctToClose = 100m, string reason = "Manual")
    {
        if (!EnsureBound()) return (false, "Войдите в аккаунт для Demo-торговли.");
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
                decimal available = _state.Balance - _state.Positions.Sum(p => p.Margin);
                if (margin > available)
                {
                    // Can't afford it anymore — drop the order rather
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

                // Use the configured DCA leverage (default 3x), same
                // as the real Engine-side DcaService — previously this
                // was hardcoded to 1, meaning demo always ran at 1x
                // while live would run at the configured leverage.
                int dcaLeverage = Math.Clamp(opts.Leverage > 0 ? opts.Leverage : 3, 1, 20);
                var (ok, error) = OpenMarketPosition(entry.Symbol, "LONG", qty, dcaLeverage, price, null, null);
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
            if (string.IsNullOrEmpty(_dcaStatePath) || !File.Exists(_dcaStatePath))
                return;
            var json = File.ReadAllText(_dcaStatePath);
            var loaded = JsonSerializer.Deserialize<DemoDcaState>(json);
            if (loaded != null) _dcaState = loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEMO-DCA] Failed to load {path}", _dcaStatePath);
        }
    }

    private void SaveDcaState()
    {
        try
        {
            if (string.IsNullOrEmpty(_dcaStatePath)) return;
            var dir = Path.GetDirectoryName(_dcaStatePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_dcaState, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _dcaStatePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _dcaStatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEMO-DCA] Failed to save {path}", _dcaStatePath);
        }
    }

    private void Load()
    {
        try
        {
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
                return;
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<DemoAccountState>(json);
            if (loaded != null) _state = loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEMO] Failed to load {path} — starting fresh", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(_filePath)) return;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEMO] Failed to save {path}", _filePath);
        }
    }
}
