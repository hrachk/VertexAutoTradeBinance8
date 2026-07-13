using Binance.Net.Enums;
using VertexAutoTradeBinance8.Services.HistoricalData;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Independent historical-data loader — deliberately has NO
    /// dependency on SymbolRegistryService, TradingWorker, or anything
    /// else related to what the strategy is currently trading. Its job is
    /// singular: keep datadb/ populated with history for whatever symbols
    /// it's told to track, on whatever timeframes it's told to track,
    /// regardless of whether the trading strategy ever looks at them.
    ///
    /// This is step one of the "full charting history for every symbol"
    /// goal: a single timeframe, proven working end-to-end (REST fetch ->
    /// HistoricalDataStore -> file on disk), before expanding to every
    /// timeframe and the full exchange symbol list. Configured via the
    /// "HistoricalData" section in appsettings.json — see the Symbols/
    /// Timeframes/IntervalSeconds keys read below.
    /// </summary>
    public sealed class HistoricalDataLoaderService : BackgroundService
    {
        private readonly HistoricalDataStore _store;
        private readonly BinanceClientFactory _factory;
        private readonly IConfiguration _cfg;
        private readonly ILogger<HistoricalDataLoaderService> _logger;

        // One REST call at a time — this service has no urgency (it's
        // backfilling history, not reacting to live signals), so there's
        // no reason to compete with the trading engine's own REST budget.
        private static readonly SemaphoreSlim _restGate = new(1, 1);

        private readonly DataDbSymbolFeed? _symbolFeed;

        public HistoricalDataLoaderService(
            HistoricalDataStore store,
            BinanceClientFactory factory,
            IConfiguration cfg,
            ILogger<HistoricalDataLoaderService> logger,
            DataDbSymbolFeed? symbolFeed = null)
        {
            _store      = store;
            _factory    = factory;
            _cfg        = cfg;
            _logger     = logger;
            _symbolFeed = symbolFeed;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Give the rest of the Engine a head start (its own startup
            // bootstrap, WS subscriptions, etc) before this background
            // job starts making its own REST calls.
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOneCycleAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DATADB-LOADER] Unhandled error in loader cycle");
                }

                var intervalSeconds = _cfg.GetValue("HistoricalData:IntervalSeconds", 300);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, intervalSeconds)), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task RunOneCycleAsync(CancellationToken ct)
        {
            var enabled = _cfg.GetValue("HistoricalData:Enabled", false);
            if (!enabled)
            {
                _logger.LogDebug("[DATADB-LOADER] HistoricalData:Enabled is false — skipping cycle");
                return;
            }

            // ── Symbol resolution via DataDbSymbolFeed ───────────────────
            // DataDbSymbolFeed aggregates ALL sources without calling Binance:
            //   • HistoricalData:Symbols (appsettings explicit list)
            //   • SymbolSelection:Pinned
            //   • Live trading universe (SymbolRegistryService.ActiveSymbols)
            //   • Currently open positions (picks up manual trades on any symbol)
            //   • Symbols seen since last restart (via NotifySignal/NotifyExecution)
            // Falls back to old appsettings-only logic if feed not registered.
            string[] symbols;
            if (_symbolFeed != null)
            {
                symbols = await _symbolFeed.GetSymbolsAsync(ct);
            }
            else
            {
                // Legacy fallback: appsettings + pinned only
                var explicitSymbols = _cfg.GetSection("HistoricalData:Symbols").Get<string[]>() ?? Array.Empty<string>();
                var includePinned   = _cfg.GetValue("HistoricalData:IncludePinnedSymbols", true);
                var pinnedSymbols   = includePinned
                    ? (_cfg.GetSection("SymbolSelection:Pinned").Get<string[]>() ?? Array.Empty<string>())
                    : Array.Empty<string>();
                symbols = explicitSymbols
                    .Concat(pinnedSymbols)
                    .Select(s => s.Trim().ToUpperInvariant())
                    .Where(s => s.Length > 0)
                    .Distinct().OrderBy(s => s).ToArray();
            }

            var timeframes = _cfg.GetSection("HistoricalData:Timeframes").Get<string[]>() ?? new[] { "15m" };
            int barsPerFetch = _cfg.GetValue("HistoricalData:BarsPerFetch", 500);

            if (symbols.Length == 0)
            {
                _logger.LogInformation("[DATADB-LOADER] No symbols configured (HistoricalData:Symbols empty and SymbolSelection:Pinned empty/disabled) — nothing to do this cycle");
                return;
            }

            _logger.LogInformation(
                "[DATADB-LOADER] Cycle starting: {symCount} symbol(s) x {tfCount} timeframe(s)",
                symbols.Length, timeframes.Length);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int ok = 0, failed = 0;

            foreach (var symbol in symbols)
            {
                if (ct.IsCancellationRequested) break;

                foreach (var tfLabel in timeframes)
                {
                    if (ct.IsCancellationRequested) break;

                    if (!TryParseTimeframe(tfLabel, out var tf))
                    {
                        _logger.LogWarning("[DATADB-LOADER] Unknown timeframe label '{tf}' — skipped", tfLabel);
                        continue;
                    }

                    try
                    {
                        await FetchAndStoreAsync(symbol, tfLabel, tf, barsPerFetch, ct);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "[DATADB-LOADER] Failed {symbol} {tf}", symbol, tfLabel);
                    }

                    // Gentle pacing between calls — this is a slow background
                    // job, not a latency-sensitive one.
                    try { await Task.Delay(250, ct); } catch (OperationCanceledException) { return; }
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "[DATADB-LOADER] Cycle done in {elapsed:F1}s: {ok} ok, {failed} failed ({symCount} symbols x {tfCount} timeframes)",
                sw.Elapsed.TotalSeconds, ok, failed, symbols.Length, timeframes.Length);

            // ── Synthesise 1W and 1M candles from 1D data ─────────────────
            // Binance Futures perpetuals don't serve these TFs natively.
            // We aggregate stored 1D bars into weekly/monthly candles and
            // save them as synthetic entries in the same datadb store.
            // This gives the AI full macro context (weekly/monthly levels)
            // without any additional REST calls to Binance.
            if (timeframes.Any(t => t.Equals("1D", StringComparison.OrdinalIgnoreCase)))
            {
                try { await SynthesiseHigherTfAsync(symbols, ct); }
                catch (Exception ex)
                    { _logger.LogWarning(ex, "[DATADB-LOADER] SynthesiseHigherTf failed (non-critical)"); }
            }

            // Deep backfill (extending history BACKWARD in time, toward
            // each symbol's actual listing date) runs far less often than
            // the forward-sync pass above — it's a one-time-ish job per
            // symbol+timeframe (stops permanently once IsBackfillExhausted
            // is set), not something that needs checking every cycle.
            // Gated by DeepBackfillEveryNCycles (default 6 — e.g. once per
            // hour at the default 5-minute IntervalSeconds) so it doesn't
            // compete with the time-sensitive forward sync for REST budget.
            _cycleCount++;
            var everyN = Math.Max(1, _cfg.GetValue("HistoricalData:DeepBackfillEveryNCycles", 6));
            if (_cycleCount % everyN == 0)
            {
                await RunDeepBackfillPassAsync(symbols, timeframes, barsPerFetch, ct);
            }
        }

        private int _cycleCount;

        private async Task RunDeepBackfillPassAsync(string[] symbols, string[] timeframes, int barsPerFetch, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int extended = 0, exhausted = 0;

            foreach (var symbol in symbols)
            {
                if (ct.IsCancellationRequested) break;
                foreach (var tfLabel in timeframes)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!TryParseTimeframe(tfLabel, out var tf)) continue;

                    try
                    {
                        var result = await DeepBackfillOneAsync(symbol, tfLabel, tf, barsPerFetch, ct);
                        if (result == DeepBackfillResult.Extended) extended++;
                        else if (result == DeepBackfillResult.Exhausted) exhausted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[DATADB-LOADER][DEEP] Failed {symbol} {tf}", symbol, tfLabel);
                    }

                    try { await Task.Delay(250, ct); } catch (OperationCanceledException) { return; }
                }
            }

            sw.Stop();
            if (extended > 0 || exhausted > 0)
            {
                _logger.LogInformation(
                    "[DATADB-LOADER][DEEP] Pass done in {elapsed:F1}s: {extended} extended further back, {exhausted} newly reached listing date",
                    sw.Elapsed.TotalSeconds, extended, exhausted);
            }
        }

        private enum DeepBackfillResult { Extended, Exhausted, Skipped }

        private async Task<DeepBackfillResult> DeepBackfillOneAsync(string symbol, string tfLabel, KlineInterval tf, int barsPerFetch, CancellationToken ct)
        {
            if (_knownInvalidSymbols.Contains(symbol)) return DeepBackfillResult.Skipped;
            if (_store.IsBackfillExhausted(symbol, tfLabel)) return DeepBackfillResult.Skipped;

            var oldest = await _store.GetOldestOpenTimeAsync(symbol, tfLabel, ct);
            if (oldest == null) return DeepBackfillResult.Skipped; // nothing fetched forward yet — let the regular pass establish a baseline first

            await _restGate.WaitAsync(ct);
            try
            {
                using var client = _factory.CreateRestClient();

                // endTime just before our oldest known bar — asks Binance
                // for the bars that come immediately BEFORE what we already
                // have, extending the archive backward one page at a time.
                var endTime = DateTimeOffset.FromUnixTimeMilliseconds(oldest.Value).UtcDateTime.AddSeconds(-1);

                var result = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, tf,
                    endTime: endTime,
                    limit: barsPerFetch,
                    ct: ct);

                if (!result.Success || result.Data == null)
                {
                    if (result.Error?.Code == -1121) _knownInvalidSymbols.Add(symbol);
                    return DeepBackfillResult.Skipped;
                }

                var bars = result.Data
                    .Select(k => new HistoricalKline(
                        new DateTimeOffset(k.OpenTime).ToUnixTimeMilliseconds(),
                        k.OpenPrice, k.HighPrice, k.LowPrice, k.ClosePrice, k.Volume))
                    .ToList();

                if (bars.Count == 0)
                {
                    // Binance returned nothing further back — we've reached
                    // this symbol's actual listing date on this timeframe.
                    await _store.MarkBackfillExhaustedAsync(symbol, tfLabel, ct);
                    _logger.LogInformation("[DATADB-LOADER][DEEP] {symbol} {tf} reached listing date — backfill complete", symbol, tfLabel);
                    return DeepBackfillResult.Exhausted;
                }

                await _store.AppendAsync(symbol, tfLabel, bars, ct);

                // If Binance gave back fewer bars than asked for, that
                // also means we hit the start of available history.
                if (bars.Count < barsPerFetch)
                {
                    await _store.MarkBackfillExhaustedAsync(symbol, tfLabel, ct);
                    _logger.LogInformation("[DATADB-LOADER][DEEP] {symbol} {tf} reached listing date — backfill complete", symbol, tfLabel);
                    return DeepBackfillResult.Exhausted;
                }

                return DeepBackfillResult.Extended;
            }
            finally
            {
                _restGate.Release();
            }
        }

        // Symbols confirmed invalid on the exchange (typo, delisted, wrong
        // quote asset, etc) — Binance error code -1121 ("Invalid symbol").
        // Tracked so a bad entry in config doesn't waste a REST call on
        // every single cycle forever; cleared only by a process restart
        // (which re-reads config anyway, so a fixed symbol gets retried).
        private readonly HashSet<string> _knownInvalidSymbols = new();

        private async Task FetchAndStoreAsync(string symbol, string tfLabel, KlineInterval tf, int barsPerFetch, CancellationToken ct)
        {
            if (_knownInvalidSymbols.Contains(symbol))
                return;

            await _restGate.WaitAsync(ct);
            try
            {
                using var client = _factory.CreateRestClient();

                // If we already have history, only ask for bars since the
                // last stored OpenTime — keeps every cycle after the first
                // cheap (a handful of new bars), instead of re-downloading
                // hundreds of bars we already have every single time.
                var existing = await _store.LoadLastAsync(symbol, tfLabel, 1, ct);
                DateTime? startTime = existing.Count > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(existing[0].OpenTime).UtcDateTime
                    : null;

                var result = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, tf,
                    startTime: startTime,
                    limit: barsPerFetch,
                    ct: ct);

                if (!result.Success || result.Data == null)
                {
                    if (result.Error?.Code == -1121)
                    {
                        _knownInvalidSymbols.Add(symbol);
                        _logger.LogWarning("[DATADB-LOADER] {symbol} does not exist on the exchange — skipping permanently this run", symbol);
                    }
                    else
                    {
                        _logger.LogWarning("[DATADB-LOADER] REST failed {symbol} {tf}: {err}", symbol, tfLabel, result.Error?.Message);
                    }
                    return;
                }

                var bars = result.Data
                    .Select(k => new HistoricalKline(
                        new DateTimeOffset(k.OpenTime).ToUnixTimeMilliseconds(),
                        k.OpenPrice, k.HighPrice, k.LowPrice, k.ClosePrice, k.Volume))
                    .ToList();

                if (bars.Count == 0) return;

                await _store.AppendAsync(symbol, tfLabel, bars, ct);
            }
            finally
            {
                _restGate.Release();
            }
        }

        private static bool TryParseTimeframe(string label, out KlineInterval tf)
        {
            // Binance USDT-M Futures perpetuals do NOT support 3D, 1W, 1M, 1Y klines.
            // These will fail with "Invalid interval" — skip them silently.
            if (label.Equals("3D", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("1W", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("1M", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("1Y", StringComparison.OrdinalIgnoreCase))
            { tf = default; return false; }
            switch (label.Trim().ToLowerInvariant())
            {
                case "1m": tf = KlineInterval.OneMinute; return true;
                case "3m": tf = KlineInterval.ThreeMinutes; return true;
                case "5m": tf = KlineInterval.FiveMinutes; return true;
                case "15m": tf = KlineInterval.FifteenMinutes; return true;
                case "30m": tf = KlineInterval.ThirtyMinutes; return true;
                case "1h": tf = KlineInterval.OneHour; return true;
                case "2h": tf = KlineInterval.TwoHour; return true;
                case "4h": tf = KlineInterval.FourHour; return true;
                case "6h": tf = KlineInterval.SixHour; return true;
                case "8h": tf = KlineInterval.EightHour; return true;
                case "12h": tf = KlineInterval.TwelveHour; return true;
                case "1d": tf = KlineInterval.OneDay; return true;
                case "3d": tf = KlineInterval.ThreeDay; return true;
                case "1w": tf = KlineInterval.OneWeek; return true;
                default: tf = default; return false;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // SYNTHETIC HIGHER TIMEFRAME CANDLE AGGREGATION
    // Builds 3D / 1W / 1M candles from stored 1D data.
    // No REST calls — pure in-memory aggregation → save to store.
    // ══════════════════════════════════════════════════════════════════════
    private async Task SynthesiseHigherTfAsync(string[] symbols, CancellationToken ct)
    {
        // (groupSize, tfLabel) — built from 1D bars stored in datadb
        var synths = new[] { (3, "3D"), (7, "1W"), (30, "1M") };

        foreach (var symbol in symbols)
        {
            if (ct.IsCancellationRequested) return;

            // Load stored 1D candles (use LoadAsync which is the correct store API)
            var daily = await _store.LoadAsync(symbol, "1D", ct);
            if (daily == null || daily.Count < 7) continue;

            foreach (var (groupSize, tfLabel) in synths)
            {
                try
                {
                    var aggregated = AggregateCandles(daily, groupSize);
                    if (aggregated.Count > 0)
                        await _store.AppendAsync(symbol, tfLabel, aggregated, ct);
                    _logger.LogDebug(
                        "[SYNTH] {sym}/{tf}: {n} synthetic candles written",
                        symbol, tfLabel, aggregated.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[SYNTH] {sym}/{tf} failed (non-critical)", symbol, tfLabel);
                }
            }
        }
    }

    /// <summary>
    /// Aggregates consecutive 1D candles into higher-TF bars.
    /// HistoricalKline is a positional record: (OpenTime, Open, High, Low, Close, Volume)
    /// </summary>
    private static List<HistoricalKline> AggregateCandles(
        IReadOnlyList<HistoricalKline> source, int groupSize)
    {
        var result  = new List<HistoricalKline>();
        var ordered = source.OrderBy(k => k.OpenTime).ToList();

        for (int i = 0; i + groupSize <= ordered.Count; i += groupSize)
        {
            var group = ordered.GetRange(i, groupSize);
            result.Add(new HistoricalKline(
                OpenTime: group[0].OpenTime,
                Open:     group[0].Open,
                High:     group.Max(k => k.High),
                Low:      group.Min(k => k.Low),
                Close:    group[^1].Close,
                Volume:   group.Sum(k => k.Volume)
            ));
        }
        return result;
    }
}
}