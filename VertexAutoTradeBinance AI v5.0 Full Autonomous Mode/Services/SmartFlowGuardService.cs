using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// SmartFlowGuard — Live microstructure layer on top of structure signals.
///
/// Design principles:
///   1) Additive: CORE / StrategyEngine logic unchanged.
///   2) Fail-open: missing book/klines/funding → Allow (log warning).
///   3) Prefer soft size reduction over hard block; hard only on extremes.
///   4) SL may only WIDEN (never tighten) when EnableSlWiden is on.
///   5) Reuses existing MarketDataService depth + FundingRateService + optional cluster service.
///
/// Scores are diagnostic; decision is Block / SizeMult / optional wider SL.
/// </summary>
public sealed class SmartFlowGuardService
{
    private readonly ILogger<SmartFlowGuardService> _log;
    private readonly IOptionsMonitor<SmartFlowOptions> _opt;
    private readonly MarketDataService _market;
    private readonly FundingRateService _funding;
    private readonly AiLiquidityClusterService? _clusters;

    public SmartFlowGuardService(
        ILogger<SmartFlowGuardService> log,
        IOptionsMonitor<SmartFlowOptions> opt,
        MarketDataService market,
        FundingRateService funding,
        AiLiquidityClusterService? clusters = null)
    {
        _log = log;
        _opt = opt;
        _market = market;
        _funding = funding;
        _clusters = clusters;
    }

    public sealed class Verdict
    {
        public bool Block { get; init; }
        public string Reason { get; init; } = "OK";
        public decimal SizeMult { get; init; } = 1m;
        public decimal? WiderStopLoss { get; init; }
        public decimal Score { get; init; } = 1m; // 1 = friendly, 0 = hostile
        public string Details { get; init; } = "";
    }

    public static Verdict Allow(string reason = "OK") =>
        new() { Block = false, Reason = reason, SizeMult = 1m, Score = 1m };

    /// <summary>
    /// Evaluate flow vs signal side. Safe to call on every candidate entry.
    /// </summary>
    public async Task<Verdict> EvaluateAsync(
        TradeSignal signal,
        IReadOnlyList<BinanceFuturesUsdtKline>? klines,
        CancellationToken ct = default)
    {
        var o = _opt.CurrentValue;
        if (!o.Enabled)
            return Allow("DISABLED");

        if (signal == null || signal.Side is not (SignalSide.Buy or SignalSide.Sell))
            return Allow("NO_SIDE");

        try
        {
            decimal sizeMult = 1m;
            decimal score = 1m;
            var notes = new List<string>(8);
            bool hard = false;
            string hardReason = "";

            // ── 1) Order book: spread, top notional, imbalance ─────────────
            OrderBookSnapshot? book = null;
            try
            {
                book = await _market.GetOrderBookAsync(signal.Symbol, o.Depth).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[SMARTFLOW] depth fetch failed {sym} → soft-pass", signal.Symbol);
            }

            if (book != null && book.Bids.Count > 0 && book.Asks.Count > 0)
            {
                var bestBid = book.Bids[0].price;
                var bestAsk = book.Asks[0].price;
                if (bestBid > 0 && bestAsk >= bestBid)
                {
                    var mid = (bestBid + bestAsk) / 2m;
                    var spreadPct = (bestAsk - bestBid) / mid;

                    if (spreadPct >= o.BlockSpreadPct)
                    {
                        hard = true;
                        hardReason = $"SPREAD_EXTREME:{spreadPct:P3}";
                        notes.Add(hardReason);
                        score = Math.Min(score, 0.15m);
                    }
                    else if (spreadPct >= o.MaxSpreadPct)
                    {
                        sizeMult = Math.Min(sizeMult, o.SoftSizeMult);
                        score = Math.Min(score, 0.55m);
                        notes.Add($"SPREAD_WIDE:{spreadPct:P3}");
                    }

                    decimal topBidN = bestBid * book.Bids[0].qty;
                    decimal topAskN = bestAsk * book.Asks[0].qty;
                    decimal topN = topBidN + topAskN;
                    if (topN > 0 && topN < o.MinTopNotionalUsd)
                    {
                        sizeMult = Math.Min(sizeMult, o.SoftSizeMult);
                        score = Math.Min(score, 0.60m);
                        notes.Add($"THIN_TOP:{topN:F0}");
                    }

                    // Imbalance over depth (same idea as cluster service)
                    decimal bidN = 0m, askN = 0m;
                    int n = Math.Min(book.Bids.Count, book.Asks.Count);
                    n = Math.Min(n, Math.Max(10, o.Depth));
                    for (int i = 0; i < n; i++)
                    {
                        bidN += book.Bids[i].price * book.Bids[i].qty;
                        askN += book.Asks[i].price * book.Asks[i].qty;
                    }
                    var total = bidN + askN;
                    if (total > 0)
                    {
                        // +1 = bid heavy, -1 = ask heavy
                        var imb = (bidN - askN) / total;

                        // Adverse for long = ask-heavy (imb negative); for short = bid-heavy
                        bool adverse =
                            (signal.Side == SignalSide.Buy && imb <= -o.SoftImbalance) ||
                            (signal.Side == SignalSide.Sell && imb >= o.SoftImbalance);

                        bool extreme =
                            (signal.Side == SignalSide.Buy && imb <= -o.HardImbalance) ||
                            (signal.Side == SignalSide.Sell && imb >= o.HardImbalance);

                        if (extreme)
                        {
                            hard = true;
                            hardReason = $"BOOK_ADVERSE:{imb:F2}";
                            notes.Add(hardReason);
                            score = Math.Min(score, 0.20m);
                        }
                        else if (adverse)
                        {
                            sizeMult = Math.Min(sizeMult, o.SoftSizeMult);
                            score = Math.Min(score, 0.45m);
                            notes.Add($"BOOK_SOFT:{imb:F2}");
                        }
                        else
                        {
                            notes.Add($"BOOK_OK:{imb:F2}");
                        }
                    }
                }
            }
            else
            {
                notes.Add("BOOK_NA");
            }

            // ── 2) Tape proxy: taker buy volume share on recent klines ─────
            if (klines != null && klines.Count >= 5)
            {
                int bars = Math.Clamp(o.DeltaBars, 3, 30);
                var slice = klines.Skip(Math.Max(0, klines.Count - bars)).ToList();
                decimal buyVol = 0m, totalVol = 0m;
                foreach (var k in slice)
                {
                    var vol = k.Volume;
                    if (vol <= 0) continue;
                    totalVol += vol;
                    // Binance.Net USDT-M kline exposes taker buy base volume
                    // Binance USDT-M kline: TakerBuyBaseVolume is filled by the API.
                    // If zero on sparse bars, fall back to body-direction proxy.
                    var takerBuy = k.TakerBuyBaseVolume;
                    if (takerBuy > 0m)
                        buyVol += takerBuy;
                    else if (k.ClosePrice >= k.OpenPrice)
                        buyVol += vol * 0.55m;
                    else
                        buyVol += vol * 0.45m;
                }

                if (totalVol > 0)
                {
                    var buyShare = buyVol / totalVol; // 0..1
                    // Adverse for long: low buyShare; for short: high buyShare
                    decimal adverseShare = signal.Side == SignalSide.Buy
                        ? (1m - buyShare)
                        : buyShare;

                    if (adverseShare >= o.HardAdverseDelta)
                    {
                        hard = true;
                        hardReason = $"DELTA_ADVERSE:{buyShare:F2}";
                        notes.Add(hardReason);
                        score = Math.Min(score, 0.25m);
                    }
                    else if (adverseShare >= o.SoftAdverseDelta)
                    {
                        sizeMult = Math.Min(sizeMult, o.SoftSizeMult);
                        score = Math.Min(score, 0.50m);
                        notes.Add($"DELTA_SOFT:{buyShare:F2}");
                    }
                    else
                    {
                        notes.Add($"DELTA_OK:{buyShare:F2}");
                    }
                }
            }
            else
            {
                notes.Add("DELTA_NA");
            }

            // ── 3) Funding crowding ───────────────────────────────────────
            try
            {
                if (o.BlockOnFunding)
                {
                    if (signal.Side == SignalSide.Buy && !_funding.CanEnterLong(signal.Symbol))
                    {
                        hard = true;
                        hardReason = "FUNDING_BLOCK_LONG";
                        notes.Add(hardReason);
                        score = Math.Min(score, 0.30m);
                    }
                    else if (signal.Side == SignalSide.Sell && !_funding.CanEnterShort(signal.Symbol))
                    {
                        hard = true;
                        hardReason = "FUNDING_BLOCK_SHORT";
                        notes.Add(hardReason);
                        score = Math.Min(score, 0.30m);
                    }
                    else
                    {
                        var snap = _funding.Get(signal.Symbol);
                        if (snap != null && snap.Risk >= FundingRateService.FundingRisk.High)
                        {
                            // crowded but not blocked → soft cut
                            bool sameSideCrowd =
                                (signal.Side == SignalSide.Buy && snap.PredictedRate > 0) ||
                                (signal.Side == SignalSide.Sell && snap.PredictedRate < 0);
                            if (sameSideCrowd)
                            {
                                sizeMult = Math.Min(sizeMult, o.FundingSizeMult);
                                score = Math.Min(score, 0.55m);
                                notes.Add($"FUNDING_SOFT:{snap.PredictedRate:P4}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[SMARTFLOW] funding check failed {sym}", signal.Symbol);
                notes.Add("FUNDING_NA");
            }

            // ── 4) Optional cluster soft-adjust (existing service) ────────
            decimal? widerSl = null;
            if (o.UseClusterService && _clusters != null)
            {
                try
                {
                    var adjusted = await _clusters.FilterAndAdjustAsync(signal, ct).ConfigureAwait(false);
                    if (adjusted != null)
                    {
                        // SizeMultiplier / Confidence already touched on signal by cluster service
                        if (adjusted.SizeMultiplier > 0 && adjusted.SizeMultiplier < 1m)
                            sizeMult = Math.Min(sizeMult, adjusted.SizeMultiplier);

                        if (o.EnableSlWiden && adjusted.StopLoss > 0 && signal.EntryPrice > 0)
                        {
                            // Only accept WIDER stops
                            if (signal.Side == SignalSide.Buy && adjusted.StopLoss < signal.StopLoss)
                                widerSl = adjusted.StopLoss;
                            else if (signal.Side == SignalSide.Sell && adjusted.StopLoss > signal.StopLoss)
                                widerSl = adjusted.StopLoss;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "[SMARTFLOW] cluster soft-adjust failed {sym}", signal.Symbol);
                }
            }

            sizeMult = Math.Clamp(sizeMult, 0.40m, 1.0m);

            if (hard && o.AllowHardBlock)
            {
                _log.LogInformation(
                    "[SMARTFLOW][{sym}] BLOCK {reason} score={sc:F2} notes={n}",
                    signal.Symbol, hardReason, score, string.Join("|", notes));

                return new Verdict
                {
                    Block = true,
                    Reason = hardReason,
                    SizeMult = sizeMult,
                    WiderStopLoss = widerSl,
                    Score = score,
                    Details = string.Join("|", notes)
                };
            }

            if (sizeMult < 0.999m || widerSl.HasValue)
            {
                _log.LogInformation(
                    "[SMARTFLOW][{sym}] SOFT size×{sm:F2} score={sc:F2} sl={sl} notes={n}",
                    signal.Symbol, sizeMult, score,
                    widerSl?.ToString("F6") ?? "-",
                    string.Join("|", notes));
            }

            return new Verdict
            {
                Block = false,
                Reason = notes.Count > 0 ? string.Join("|", notes) : "OK",
                SizeMult = sizeMult,
                WiderStopLoss = widerSl,
                Score = score,
                Details = string.Join("|", notes)
            };
        }
        catch (Exception ex)
        {
            // Fail-open: never break the existing pipeline
            _log.LogWarning(ex, "[SMARTFLOW] evaluate failed {sym} → allow", signal.Symbol);
            return Allow("ERROR_FAIL_OPEN");
        }
    }
}
