using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Web-owned candle history bootstrap (does NOT wait for Engine).
///
/// Priority each cycle:
///   1) Open position symbols missing/thin history in datadb
///   2) Other monitored symbols (bootstrap snapshot / live positions feed)
///
/// Fetches public Binance Futures klines and persists to:
///   {SharedData:Root}/datadb/{SYMBOL}/{tf}.json
/// Charts read the same folder via HistoricalDataReaderService.
/// </summary>
public sealed class WebHistoryBootstrapService : BackgroundService
{
    private readonly HistoricalDataWriterService _writer;
    private readonly HistoricalDataReaderService _reader;
    private readonly MarketSnapshotFileService _snapshot;
    private readonly PositionsLiveService _positions;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebHistoryBootstrapService> _log;

    private static readonly string[] PriorityTfs = { "1m", "5m", "15m", "1h", "4h" };
    private const int MinBars = 80;
    private const int FetchLimit = 500;

    // Binance interval labels
    private static readonly Dictionary<string, string> TfToBinance = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1m"] = "1m", ["5m"] = "5m", ["15m"] = "15m",
        ["30m"] = "30m", ["1h"] = "1h", ["4h"] = "4h", ["1d"] = "1d"
    };

    public WebHistoryBootstrapService(
        HistoricalDataWriterService writer,
        HistoricalDataReaderService reader,
        MarketSnapshotFileService snapshot,
        PositionsLiveService positions,
        IHttpClientFactory httpFactory,
        ILogger<WebHistoryBootstrapService> log)
    {
        _writer = writer;
        _reader = reader;
        _snapshot = snapshot;
        _positions = positions;
        _httpFactory = httpFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "[WEB-HIST] Bootstrap started. Archive root={root}", _writer.Root);

        // Short delay so positions WS / snapshot can initialise
        try { await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WEB-HIST] tick failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // ── Priority 1: open positions ────────────────────────────────
        var positionSyms = SafePositionSymbols();
        var posMissing = positionSyms
            .Where(s => NeedsHistory(s))
            .Take(6)
            .ToList();

        // ── Priority 2: monitored / snapshot universe ─────────────────
        var monitored = await SafeMonitoredSymbolsAsync(ct);
        var monMissing = monitored
            .Except(positionSyms, StringComparer.OrdinalIgnoreCase)
            .Where(s => NeedsHistory(s))
            .Take(3)
            .ToList();

        var queue = posMissing.Select(s => (s, pri: 1))
            .Concat(monMissing.Select(s => (s, pri: 2)))
            .ToList();

        if (queue.Count == 0) return;

        _log.LogInformation(
            "[WEB-HIST] queue positions={p} monitored={m} → {syms}",
            posMissing.Count, monMissing.Count,
            string.Join(",", queue.Select(x => x.s)));

        foreach (var (sym, pri) in queue)
        {
            ct.ThrowIfCancellationRequested();
            await EnsureSymbolHistoryAsync(sym, pri, ct);
            // gentle rate limit
            await Task.Delay(250, ct);
        }
    }

    private bool NeedsHistory(string symbol)
    {
        // Need at least one primary TF with enough bars
        foreach (var tf in PriorityTfs)
        {
            if (!_writer.HasEnough(symbol, tf, MinBars) && !_reader.Has(symbol, tf))
                return true;
            if (_reader.Has(symbol, tf) && _reader.ApproxBarCount(symbol, tf) < MinBars)
                return true;
        }
        // If 15m is solid, consider OK for chart
        if (_writer.HasEnough(symbol, "15m", MinBars) || _reader.ApproxBarCount(symbol, "15m") >= MinBars)
            return false;
        return true;
    }

    private async Task EnsureSymbolHistoryAsync(string symbol, int priority, CancellationToken ct)
    {
        foreach (var tf in PriorityTfs)
        {
            if (_writer.HasEnough(symbol, tf, MinBars) || _reader.ApproxBarCount(symbol, tf) >= MinBars)
                continue;

            try
            {
                var bars = await FetchBinanceKlinesAsync(symbol, tf, FetchLimit, ct);
                if (bars.Count == 0)
                {
                    _log.LogDebug("[WEB-HIST] empty response {sym}/{tf}", symbol, tf);
                    continue;
                }
                await _writer.SaveAsync(symbol, tf, bars, ct);
                _log.LogInformation(
                    "[WEB-HIST] {pri} filled {sym}/{tf} bars={n}",
                    priority == 1 ? "POSITION" : "MONITOR", symbol, tf, bars.Count);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[WEB-HIST] fetch failed {sym}/{tf}", symbol, tf);
            }

            await Task.Delay(150, ct);
        }
    }

    private async Task<List<KlineDto>> FetchBinanceKlinesAsync(
        string symbol, string tfLabel, int limit, CancellationToken ct)
    {
        if (!TfToBinance.TryGetValue(tfLabel, out var interval))
            interval = tfLabel;

        var url =
            $"https://fapi.binance.com/fapi/v1/klines?symbol={symbol.ToUpperInvariant()}&interval={interval}&limit={limit}";

        var http = _httpFactory.CreateClient("WebHistoryBootstrap");
        http.Timeout = TimeSpan.FromSeconds(12);

        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogDebug("[WEB-HIST] HTTP {code} for {sym}/{tf}", (int)resp.StatusCode, symbol, tfLabel);
            return new();
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new();

        var list = new List<KlineDto>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            // Binance kline array: [ openTime, o, h, l, c, vol, ... ]
            if (el.GetArrayLength() < 6) continue;
            long ot = el[0].GetInt64();
            decimal o = ParseDec(el[1]);
            decimal h = ParseDec(el[2]);
            decimal l = ParseDec(el[3]);
            decimal c = ParseDec(el[4]);
            decimal v = ParseDec(el[5]);
            list.Add(new KlineDto(ot, o, h, l, c, v));
        }
        return list;
    }

    private static decimal ParseDec(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return 0m;
    }

    private List<string> SafePositionSymbols()
    {
        try
        {
            return _positions.GetActiveSymbols()
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task<List<string>> SafeMonitoredSymbolsAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var series = await _snapshot.LoadAsync();
            foreach (var s in series)
            {
                if (!string.IsNullOrWhiteSpace(s.Symbol))
                    set.Add(s.Symbol.Trim().ToUpperInvariant());
            }
        }
        catch { /* ignore */ }

        // Also any symbols already partially archived
        try
        {
            foreach (var s in _reader.ListArchivedSymbols())
                set.Add(s);
        }
        catch { }

        return set.ToList();
    }
}
