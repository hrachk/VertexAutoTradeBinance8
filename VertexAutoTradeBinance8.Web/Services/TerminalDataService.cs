using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class PositionRow
{
    public string Symbol { get; init; } = "";
    public string Side { get; init; } = "";
    public bool IsLong { get; init; }
    public decimal Qty { get; init; }
    public decimal Entry { get; init; }
    public decimal Mark { get; init; }
    public decimal UnrealizedPnl { get; init; }
    public decimal Notional { get; init; }
    public decimal RoePercent { get; init; }
    public decimal Liquidation { get; init; }
    public int Leverage { get; init; }
    public bool Managed { get; init; }
    public decimal? StopLoss { get; init; }
    public IReadOnlyList<decimal> TakeProfits { get; init; } = Array.Empty<decimal>();
    public int Stage { get; init; }
    public decimal FilledFraction { get; init; }
    public decimal? RMultiple { get; init; }
    public DateTime? OpenedAt { get; init; }
    /// <summary>0..1 положение mark между SL (0) и TP1 (1).</summary>
    public decimal? Progress { get; init; }
}

public sealed class JournalStats
{
    public int Trades { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public decimal WinRate => Trades > 0 ? (decimal)Wins / Trades : 0m;
    public decimal NetPnl { get; init; }
    public decimal TodayPnl { get; init; }
    public decimal WeekPnl { get; init; }
    public decimal GrossProfit { get; init; }
    public decimal GrossLoss { get; init; }
    public decimal ProfitFactor => GrossLoss < 0 ? GrossProfit / Math.Abs(GrossLoss) : (GrossProfit > 0 ? 99m : 0m);
    public decimal AvgWin { get; init; }
    public decimal AvgLoss { get; init; }
    public decimal MaxDrawdown { get; init; }
    public decimal Fees { get; init; }
    public int TodayTrades { get; init; }
    public IReadOnlyList<(DateTime t, decimal equity)> Curve { get; init; } = Array.Empty<(DateTime, decimal)>();
}

public sealed class TerminalSnapshot
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
    public string Environment { get; init; } = "Live";
    public bool HasCredentials { get; init; }
    public bool ApiOk { get; init; }
    public string? ApiError { get; init; }

    public decimal Wallet { get; init; }
    public decimal Available { get; init; }
    public decimal Unrealized { get; init; }
    public decimal Equity => Wallet + Unrealized;

    public bool TradingEnabled { get; init; }
    public bool BlockWeekends { get; init; }
    public bool SessionOpen { get; init; }
    public string SessionReason { get; init; } = "";

    public DateTime? EngineHeartbeat { get; init; }
    public string EngineStatus { get; init; } = "";
    public string EngineSymbol { get; init; } = "";
    public string EngineRegime { get; init; } = "";

    public IReadOnlyList<PositionRow> Positions { get; init; } = Array.Empty<PositionRow>();
    public JournalStats Stats { get; init; } = new();
    public IReadOnlyList<ExecutedSignalRecord> RecentClosed { get; init; } = Array.Empty<ExecutedSignalRecord>();
    public IReadOnlyList<ExecutedSignalRecord> RecentActivity { get; init; } = Array.Empty<ExecutedSignalRecord>();

    public bool EngineAlive => EngineHeartbeat.HasValue && DateTime.UtcNow - EngineHeartbeat.Value < TimeSpan.FromMinutes(2);
}

/// <summary>
/// Агрегатор данных для UI: биржа (позиции/баланс) + журнал бота + управляющие файлы.
/// Кэш 3 секунды, чтобы несколько открытых вкладок не долбили Binance.
/// </summary>
public sealed class TerminalDataService
{
    private readonly AccountStateCache _account;
    private readonly ManagedPositionRegistry _managed;
    private readonly ExecutedSignalService _journal;
    private readonly TradingControlService _control;
    private readonly TradingSessionGate _session;
    private readonly BinanceClientFactory _factory;
    private readonly EngineStateService _engine;
    private readonly ILogger<TerminalDataService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TerminalSnapshot? _last;

    public TerminalDataService(
        AccountStateCache account,
        ManagedPositionRegistry managed,
        ExecutedSignalService journal,
        TradingControlService control,
        TradingSessionGate session,
        BinanceClientFactory factory,
        EngineStateService engine,
        ILogger<TerminalDataService> logger)
    {
        _account = account;
        _managed = managed;
        _journal = journal;
        _control = control;
        _session = session;
        _factory = factory;
        _engine = engine;
        _logger = logger;
    }

    public TradingControlService Control => _control;

    public async Task<TerminalSnapshot> GetAsync(CancellationToken ct = default, bool force = false)
    {
        if (!force && _last != null && DateTime.UtcNow - _last.AtUtc < TimeSpan.FromSeconds(3))
            return _last;

        await _gate.WaitAsync(ct);
        try
        {
            if (!force && _last != null && DateTime.UtcNow - _last.AtUtc < TimeSpan.FromSeconds(3))
                return _last;
            _last = await BuildAsync(ct);
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TerminalSnapshot> BuildAsync(CancellationToken ct)
    {
        string? apiError = null;
        decimal wallet = 0, available = 0;
        IReadOnlyList<Binance.Net.Objects.Models.Futures.BinancePositionDetailsUsdt> positions =
            Array.Empty<Binance.Net.Objects.Models.Futures.BinancePositionDetailsUsdt>();

        if (_factory.HasCredentials)
        {
            try
            {
                (wallet, available) = await _account.GetBalancesAsync(ct);
                positions = await _account.GetOpenPositionsAsync(ct);
                if (!_account.IsFresh) apiError = "Binance API: нет свежих данных по позициям";
            }
            catch (Exception ex)
            {
                apiError = ex.Message;
                _logger.LogWarning(ex, "[UI] exchange snapshot failed");
            }
        }
        else
        {
            apiError = "API-ключи не настроены для Web (Binance__ApiKey / Binance__SecretKey)";
        }

        _managed.Reload();
        var rows = positions.Select(p => BuildRow(p)).OrderByDescending(r => Math.Abs(r.UnrealizedPnl)).ToList();

        var all = _journal.GetAll();
        var stats = BuildStats(all);

        var engine = _engine.Load();
        var sessionOpen = _session.IsTradingAllowed(out var sessionReason, out _);

        return new TerminalSnapshot
        {
            Environment = _factory.EnvironmentName,
            HasCredentials = _factory.HasCredentials,
            ApiOk = apiError == null,
            ApiError = apiError,
            Wallet = wallet,
            Available = available,
            Unrealized = rows.Sum(r => r.UnrealizedPnl),
            TradingEnabled = _control.IsTradingEnabled(),
            BlockWeekends = _control.IsBlockWeekends(),
            SessionOpen = sessionOpen,
            SessionReason = sessionReason,
            EngineHeartbeat = engine?.LastUpdate.ToUniversalTime(),
            EngineStatus = engine?.Status ?? "",
            EngineSymbol = engine?.Symbol ?? "",
            EngineRegime = string.IsNullOrWhiteSpace(engine?.SmartRegime) ? engine?.MarketRegime ?? "" : engine!.SmartRegime,
            Positions = rows,
            Stats = stats,
            RecentClosed = all.Where(x => x.IsClosed).OrderByDescending(x => x.EffectiveCloseTime).Take(12).ToList(),
            RecentActivity = all.Take(12).ToList()
        };
    }

    private PositionRow BuildRow(Binance.Net.Objects.Models.Futures.BinancePositionDetailsUsdt p)
    {
        var info = _managed.Get(p.Symbol, p.PositionSide);
        bool isLong = p.PositionSide == PositionSide.Long || (p.PositionSide == PositionSide.Both && p.Quantity > 0);
        decimal qty = Math.Abs(p.Quantity);
        decimal notional = qty * (p.MarkPrice > 0 ? p.MarkPrice : p.EntryPrice);
        decimal margin = p.Leverage > 0 ? notional / p.Leverage : notional;
        decimal roe = margin > 0 ? p.UnrealizedPnl / margin * 100m : 0m;

        decimal? r = null, progress = null;
        decimal? sl = info?.CalculatedSL > 0 ? info.CalculatedSL : null;
        decimal initSl = info?.InitialSL > 0 ? info.InitialSL : sl ?? 0m;
        if (initSl > 0 && p.EntryPrice > 0)
        {
            decimal risk = Math.Abs(p.EntryPrice - initSl);
            if (risk > 0)
                r = (isLong ? p.MarkPrice - p.EntryPrice : p.EntryPrice - p.MarkPrice) / risk;

            var tp1 = info?.CalculatedTPs.FirstOrDefault() ?? 0m;
            if (tp1 > 0 && tp1 != initSl)
                progress = Math.Clamp((p.MarkPrice - initSl) / (tp1 - initSl), 0m, 1m);
        }

        decimal peak = info?.PeakQty > 0 ? info.PeakQty : qty;
        return new PositionRow
        {
            Symbol = p.Symbol,
            Side = isLong ? "LONG" : "SHORT",
            IsLong = isLong,
            Qty = qty,
            Entry = p.EntryPrice,
            Mark = p.MarkPrice,
            UnrealizedPnl = p.UnrealizedPnl,
            Notional = notional,
            RoePercent = roe,
            Liquidation = p.LiquidationPrice,
            Leverage = p.Leverage,
            Managed = info != null,
            StopLoss = sl,
            TakeProfits = info?.CalculatedTPs ?? new List<decimal>(),
            Stage = info?.Stage ?? 0,
            FilledFraction = peak > 0 ? 1m - qty / peak : 0m,
            RMultiple = r,
            OpenedAt = info?.OpenedAt,
            Progress = progress
        };
    }

    public static JournalStats BuildStats(IReadOnlyList<ExecutedSignalRecord> all)
    {
        var closed = all.Where(x => x.IsClosed && x.PnL.HasValue).OrderBy(x => x.EffectiveCloseTime).ToList();
        var today = DateTime.UtcNow.Date;
        var week = today.AddDays(-6);

        decimal eq = 0, peak = 0, maxDd = 0;
        var curve = new List<(DateTime, decimal)>();
        foreach (var c in closed)
        {
            eq += c.PnL!.Value;
            peak = Math.Max(peak, eq);
            maxDd = Math.Max(maxDd, peak - eq);
            curve.Add((c.EffectiveCloseTime, eq));
        }

        var wins = closed.Where(x => x.PnL > 0).ToList();
        var losses = closed.Where(x => x.PnL <= 0).ToList();

        return new JournalStats
        {
            Trades = closed.Count,
            Wins = wins.Count,
            Losses = losses.Count,
            NetPnl = eq,
            TodayPnl = closed.Where(x => x.EffectiveCloseTime >= today).Sum(x => x.PnL!.Value),
            TodayTrades = closed.Count(x => x.EffectiveCloseTime >= today),
            WeekPnl = closed.Where(x => x.EffectiveCloseTime >= week).Sum(x => x.PnL!.Value),
            GrossProfit = wins.Sum(x => x.PnL!.Value),
            GrossLoss = losses.Sum(x => x.PnL!.Value),
            AvgWin = wins.Count > 0 ? wins.Average(x => x.PnL!.Value) : 0,
            AvgLoss = losses.Count > 0 ? losses.Average(x => x.PnL!.Value) : 0,
            MaxDrawdown = maxDd,
            Fees = closed.Sum(x => x.Fees ?? 0m),
            Curve = curve
        };
    }
}
