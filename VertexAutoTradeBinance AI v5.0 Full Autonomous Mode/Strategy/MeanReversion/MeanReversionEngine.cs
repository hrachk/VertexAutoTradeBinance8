using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Strategy.MeanReversion
{
    /// <summary>
    /// Statistical mean-reversion strategy, completely independent from
    /// the trend-following StrategyEngine (no shared mutable state, no
    /// modifications to that file). It produces the same TradeSignal
    /// contract so the rest of the pipeline (TradingWorker, order
    /// placement, PositionSupervisor) doesn't need to know which engine
    /// originated a signal — only the Reason prefix ("MEANREV_") tags it
    /// for the supervisor's exit-logic branch and for the StrategyRouter.
    ///
    /// Core logic (per the spec):
    ///   1) Compute rolling mean/std (Bollinger-style, 20-bar default).
    ///   2) Entry trigger: |Z-Score| >= EntrySigma (default 2.0).
    ///   3) Volume confirmation: current bar volume >= avg(50) * 1.5x —
    ///      filters out low-liquidity noise spikes.
    ///   4) Reversal confirmation: last candle must actually be turning
    ///      back toward the mean (not a falling knife mid-collapse).
    ///   5) Regime gate: only fires in Range/Squeeze regimes — trending
    ///      markets are exactly where mean-reversion gets run over.
    ///   6) BTC correlation guard: skip if BTC itself is in a strong
    ///      trend and this symbol is highly correlated to it (a market-
    ///      wide move isn't a "statistical anomaly" to fade).
    ///   7) Target (TP) = the mean itself (0σ) by default; StopLoss sits
    ///      just beyond the entry band so a continued move invalidates
    ///      the mean-reversion thesis quickly rather than averaging down.
    /// </summary>
    public sealed class MeanReversionEngine
    {
        private readonly ILogger<MeanReversionEngine> _logger;
        private readonly SmartRegimeService _smartRegimeService;
        private readonly AiCorrelationService _correlationService;
        private readonly MeanReversionOptions _opt;

        public event Action<TradeSignal>? OnSignalGenerated;

        // Per-(symbol,side) cooldown so a single oscillation around -2σ
        // doesn't fire ten signals in a row on every closed candle.
        private readonly ConcurrentDictionary<string, DateTime> _lastSignalTime = new(StringComparer.OrdinalIgnoreCase);

        private MarketDataFacade? _boundMarketData;
        private Action<string, KlineInterval, BinanceFuturesUsdtKline>? _onKlineHandler;

        public MeanReversionEngine(
            ILogger<MeanReversionEngine> logger,
            SmartRegimeService smartRegimeService,
            AiCorrelationService correlationService,
            MeanReversionOptions opt)
        {
            _logger = logger;
            _smartRegimeService = smartRegimeService;
            _correlationService = correlationService;
            _opt = opt;
        }

        /// <summary>
        /// Wires this engine to closed-candle events on the given timeframes.
        /// Mirrors StrategyEngine.BindReactive's pattern but only listens to
        /// WsClosedKline (mean-reversion is evaluated on confirmed bars only —
        /// no realtime/tick-based triggering, since acting mid-candle on a
        /// statistical extreme is exactly the "falling knife" risk we guard
        /// against with HasReversalConfirmation).
        /// </summary>
        public void BindReactive(MarketDataFacade marketData)
        {
            if (marketData == null) throw new ArgumentNullException(nameof(marketData));

            UnbindReactive();
            _boundMarketData = marketData;

            _onKlineHandler = (symbol, tf, _) =>
            {
                if (tf != _opt.Timeframe) return;
                SafeFireAndForget(() => EvaluateAsync(symbol, tf, marketData));
            };

            marketData.WsClosedKline += _onKlineHandler;

            _logger.LogInformation(
                "[MEANREV] Reactive entry-point bound (tf={tf}, window={window}, entrySigma={sigma})",
                _opt.Timeframe, _opt.Window, _opt.EntrySigma);
        }

        public void UnbindReactive()
        {
            if (_boundMarketData == null) return;
            if (_onKlineHandler != null)
                _boundMarketData.WsClosedKline -= _onKlineHandler;
            _onKlineHandler = null;
            _boundMarketData = null;
        }

        private void SafeFireAndForget(Func<Task> action)
        {
            _ = action().ContinueWith(t =>
            {
                if (t.Exception != null)
                    _logger.LogError(t.Exception, "[MEANREV] async error");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task EvaluateAsync(string symbol, KlineInterval tf, MarketDataFacade marketData)
        {
            var klines = await marketData.GetKlinesAsync(symbol, tf, need: Math.Max(_opt.Window, 60) + 10);
            if (klines == null || klines.Count < Math.Max(_opt.Window, 60))
                return;

            var signal = await TryBuildSignalAsync(symbol, tf, klines);
            if (signal == null) return;

            // Cooldown: don't re-signal the same symbol within the window
            var key = $"{symbol}:{signal.Side}";
            var now = DateTime.UtcNow;
            if (_lastSignalTime.TryGetValue(key, out var last) &&
                now - last < _opt.SignalCooldown)
                return;

            _lastSignalTime[key] = now;

            var bandsForLog = ZScoreCalculator.ComputeBands(klines, _opt.Window, _opt.EntrySigma);
            _logger.LogInformation(
                "[MEANREV][{symbol}] {reason} — Z={z:F2} mean={mean:F4} entry={entry:F4} sl={sl:F4} tp={tp:F4}",
                symbol, signal.Reason, bandsForLog.ZScore, bandsForLog.Mean,
                signal.EntryPrice, signal.StopLoss, signal.TakeProfits.FirstOrDefault());

            OnSignalGenerated?.Invoke(signal);
        }

        /// <summary>
        /// Pure decision function (aside from the optional BTC-correlation
        /// fetch) — returns null if any gate fails. No log spam for the
        /// common "no setup right now" case; only successful builds are
        /// logged by the caller.
        /// </summary>
        public async Task<TradeSignal?> TryBuildSignalAsync(
            string symbol,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < Math.Max(_opt.Window, 60))
                return null;

            // ── GATE 1: Regime — only trade mean-reversion in Range/Squeeze ──
            var smart = _smartRegimeService.Evaluate(symbol, tf, klines);
            bool rangeLike =
                smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange ||
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            if (_opt.RequireRangeRegime && !rangeLike)
                return null;

            // ── GATE 2: Z-Score extreme ──
            var bands = ZScoreCalculator.ComputeBands(klines, _opt.Window, _opt.EntrySigma);
            if (!bands.IsValid) return null;

            bool longSetup  = bands.ZScore <= -_opt.EntrySigma;
            bool shortSetup = bands.ZScore >= _opt.EntrySigma;
            if (!longSetup && !shortSetup) return null;

            // ── GATE 3: Volume confirmation ──
            if (!ZScoreCalculator.IsVolumeSurge(klines, _opt.VolumeAvgPeriod, _opt.VolumeSurgeMultiplier))
                return null;

            // ── GATE 4: Reversal confirmation (not a falling knife) ──
            if (_opt.RequireReversalConfirmation &&
                !ZScoreCalculator.HasReversalConfirmation(klines, longSetup, _opt.ReversalLookback))
                return null;

            // ── GATE 5: BTC correlation guard ──
            // Skip fading a move that's really just BTC dragging the whole
            // market with it — that's not a symbol-specific "anomaly" to
            // bet against, it's the whole market moving together.
            if (_opt.UseBtcCorrelationGuard &&
                !string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase) &&
                _boundMarketData != null)
            {
                var corr = _correlationService.GetCorrelation("BTCUSDT", symbol);
                if (corr.HasValue && Math.Abs(corr.Value) >= _opt.BtcCorrelationThreshold)
                {
                    var btcKlines = await _boundMarketData.GetKlinesAsync(
                        "BTCUSDT", tf, need: Math.Max(_opt.Window, 30));

                    if (btcKlines != null && btcKlines.Count >= _opt.Window)
                    {
                        var btcBands = ZScoreCalculator.ComputeBands(btcKlines, _opt.Window, _opt.EntrySigma);
                        // If BTC itself is at a statistical extreme in the
                        // SAME direction as our setup, this isn't a symbol-
                        // specific anomaly — it's correlated market-wide
                        // movement. Skip it; let the trend engine (or a
                        // dedicated BTC mean-reversion read) handle that case.
                        bool btcSameDirection =
                            (longSetup && btcBands.ZScore <= -1.0m) ||
                            (shortSetup && btcBands.ZScore >= 1.0m);
                        if (btcBands.IsValid && btcSameDirection)
                            return null;
                    }
                }
            }

            decimal atr = ZScoreCalculator.Atr(klines, 14, klines.Count - 1);
            if (atr <= 0m) return null;

            decimal entry = bands.LastClose;
            decimal stopLoss;
            decimal takeProfit = bands.Mean; // 0σ target — the core thesis

            if (longSetup)
            {
                // Stop sits beyond the entry band (further than where we
                // got in), padded by a fraction of ATR — if price keeps
                // falling past our statistical "extreme", the thesis is
                // wrong and we get out fast rather than average down.
                stopLoss = Math.Min(bands.LowerEntryBand, entry) - atr * _opt.StopAtrPad;
            }
            else
            {
                stopLoss = Math.Max(bands.UpperEntryBand, entry) + atr * _opt.StopAtrPad;
            }

            decimal risk = Math.Abs(entry - stopLoss);
            decimal reward = Math.Abs(takeProfit - entry);
            if (risk <= 0m) return null;

            // Reject setups with poor risk/reward — if the mean is too close
            // to the entry relative to the stop distance, it's not worth it.
            if (reward / risk < _opt.MinRewardRiskRatio)
                return null;

            var signal = new TradeSignal
            {
                Symbol = symbol,
                Side = longSetup ? SignalSide.Buy : SignalSide.Sell,
                Reason = longSetup ? "MEANREV_ZSCORE_LONG" : "MEANREV_ZSCORE_SHORT",
                Atr = atr,
                EntryPrice = entry,
                StopLoss = stopLoss,
                Confidence = ClampConfidence(bands.ZScore, smart.Confidence),
                TakeProfits = new List<decimal> { takeProfit },
                Timeframe = tf.ToString(),
                Time = DateTime.UtcNow,
                // Mean-reversion holds are inherently short — if the thesis
                // (return to mean) hasn't played out within N bars, it's
                // wrong, and PositionSupervisor's time-stop should close it
                // regardless of current PnL.
                TimeStopBars = _opt.TimeStopBars
            };

            return signal;
        }

        private static decimal ClampConfidence(decimal zScore, decimal regimeConfidence)
        {
            // Stronger statistical extremes (higher |Z|) and a more confident
            // regime read both push confidence up, capped at 0.95 so it never
            // claims full certainty (this is a statistical bet, not a lock).
            decimal zComponent = Math.Min(Math.Abs(zScore) / 4m, 1m); // |Z|=4 -> maxed out
            decimal blended = zComponent * 0.6m + regimeConfidence * 0.4m;
            return Math.Clamp(blended, 0.1m, 0.95m);
        }
    }

    /// <summary>
    /// Tunables for MeanReversionEngine, bound from appsettings.json under
    /// "MeanReversion". Kept as a plain options class (not IOptionsMonitor)
    /// for the engine itself — live on/off switching is handled one layer up
    /// by StrategyRouter/StrategyModeState, not by hot-reloading these
    /// numeric thresholds mid-flight.
    /// </summary>
    public sealed class MeanReversionOptions
    {
        public KlineInterval Timeframe { get; set; } = KlineInterval.FifteenMinutes;

        /// <summary>Rolling window for mean/std, in bars (classic Bollinger = 20).</summary>
        public int Window { get; set; } = 20;

        /// <summary>Entry trigger: |Z-Score| must reach this many standard deviations.</summary>
        public decimal EntrySigma { get; set; } = 2.0m;

        public int VolumeAvgPeriod { get; set; } = 50;
        public decimal VolumeSurgeMultiplier { get; set; } = 1.5m;

        public bool RequireReversalConfirmation { get; set; } = true;
        public int ReversalLookback { get; set; } = 5;

        public bool RequireRangeRegime { get; set; } = true;

        public bool UseBtcCorrelationGuard { get; set; } = true;
        public decimal BtcCorrelationThreshold { get; set; } = 0.6m;

        /// <summary>Extra ATR padding beyond the entry band for the stop-loss.</summary>
        public decimal StopAtrPad { get; set; } = 0.3m;

        public decimal MinRewardRiskRatio { get; set; } = 1.2m;

        /// <summary>
        /// If the position hasn't reverted toward the mean within this many
        /// bars, PositionSupervisor's mean-reversion branch force-closes it —
        /// the thesis didn't play out, no point holding indefinitely.
        /// </summary>
        public decimal TimeStopBars { get; set; } = 12;

        public TimeSpan SignalCooldown { get; set; } = TimeSpan.FromMinutes(10);
    }
}
