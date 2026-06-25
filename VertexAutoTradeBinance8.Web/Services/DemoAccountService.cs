using System.Text.Json;
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

    public event Action? Updated;

    public DemoAccountService(MarketDataLiveState liveState, ILogger<DemoAccountService> logger, IConfiguration cfg)
    {
        _liveState = liveState;
        _logger = logger;

        // Same simple file-based persistence pattern already used
        // elsewhere in this project (klines_bootstrap.json) — a single
        // JSON file, not a database, matching the project's existing
        // scale and conventions.
        var root = cfg["SharedData:Root"] ?? AppContext.BaseDirectory;
        _filePath = Path.Combine(root, "demo-account.json");

        Load();

        _liveState.PriceTicked += OnPriceTicked;
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

                _state.Positions.Add(new DemoPosition
                {
                    Symbol = order.Symbol, Side = order.Side, Qty = order.Qty, Leverage = order.Leverage,
                    EntryPrice = price, Margin = margin, StopLoss = order.StopLoss, TakeProfits = order.TakeProfits,
                });
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
