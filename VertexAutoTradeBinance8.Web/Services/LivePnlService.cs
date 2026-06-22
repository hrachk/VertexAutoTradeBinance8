using Binance.Net.Enums;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services
{
    /// <summary>
    /// LivePnlService
    /// ─ читает Central Account State (/api/state/positions)
    /// ─ НЕ ходит в Binance
    /// ─ thread-safe
    /// ─ UI-safe
    /// </summary>
    public sealed class LivePnlService : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly ILogger<LivePnlService> _logger;

        private readonly ConcurrentDictionary<string, LivePositionState> _map = new();
        private AccountBalanceState _balance = new();
        private decimal _realizedPnlToday;

        private CancellationTokenSource? _cts;
        private Task? _loop;

        public event Action? Updated;

        public LivePnlService(HttpClient http, ILogger<LivePnlService> logger)
        {
            _http = http;
            _logger = logger;
        }

        private static string Key(string symbol, PositionSide side)
            => LivePositionState.Key(symbol, side);

        // =============================
        // Single position lookup (cards)
        // =============================
        public LivePositionState? Get(string symbol, string posSide)
        {
            if (!Enum.TryParse<PositionSide>(posSide, true, out var side))
                return null;

            _map.TryGetValue(Key(symbol, side), out var v);
            return v;
        }

        public AccountBalanceState Balance => _balance;
        public decimal RealizedPnlToday => _realizedPnlToday;

        // =============================
        // Polling loop (UI-safe)
        // =============================
        public void Start(string? symbolsCsv, int intervalMs = 900)
        {
            Stop();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _loop = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var url = "/api/state/positions";
                        if (!string.IsNullOrWhiteSpace(symbolsCsv))
                            url += "?symbols=" + Uri.EscapeDataString(symbolsCsv);

                        var data = await _http
                            .GetFromJsonAsync<List<LivePositionState>>(url, ct)
                            ?? new();

                        _map.Clear();

                        foreach (var p in data)
                        {
                            if (string.IsNullOrWhiteSpace(p.Symbol)) continue;
                            if (p.Qty == 0) continue;

                            _map[Key(p.Symbol, p.Side)] = p;
                        }

                        // Balance + today's realized PnL — same Central
                        // Account State source, polled in the same cycle
                        // rather than a separate timer, to keep this one
                        // simple loop as the single source of truth for
                        // everything this service exposes.
                        try
                        {
                            var bal = await _http.GetFromJsonAsync<AccountBalanceState>("/api/state/balance", ct);
                            if (bal != null) _balance = bal;
                        }
                        catch { /* balance is a nice-to-have here; positions matter more, don't let this break the loop */ }

                        try
                        {
                            _realizedPnlToday = await _http.GetFromJsonAsync<decimal>("/api/state/realized-pnl-today", ct);
                        }
                        catch { }

                        Updated?.Invoke();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[LIVE-PNL] poll failed");
                    }

                    try { await Task.Delay(intervalMs, ct); }
                    catch { }
                }
            }, ct);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _loop = null;
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }

        // =============================
        // Table / page consumption
        // =============================
        public IReadOnlyList<LivePositionModel> GetPositions()
        {
            return _map.Values
                .Select(p => new LivePositionModel
                {
                    Symbol = p.Symbol,
                    PositionSide = p.Side.ToString().ToUpperInvariant(),
                    PositionAmt = p.Qty,
                    EntryPrice = p.EntryPrice,
                    MarkPrice = p.MarkPrice,
                    UnrealizedPnl = p.UnrealizedPnl,
                    Notional = p.Notional,
                    LiquidationPrice = p.LiquidationPrice,
                    IsolatedMargin = p.IsolatedMargin,
                    Leverage = p.Leverage
                })
                .OrderBy(x => x.Symbol)
                .ToList();
        }
    }
}
