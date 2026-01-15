using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;
using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// AI-анализ стакана: кластеры ликвидности, дисбаланс Bid/Ask, зоны стоп-ханта.
    /// Работает поверх MarketDataService (OrderBookSnapshot).
    /// </summary>
    public class AiLiquidityClusterService
    {
        private readonly ILogger<AiLiquidityClusterService> _logger;
        private readonly MarketDataService _marketData;

        // Порог по объёму кластера (в USDT)
        private const decimal ClusterNotionalThreshold = 50_000m;

        // Порог дисбаланса
        private const decimal ImbalanceDangerThreshold = 0.75m;

        // Макс. расстояние до стены, чтобы блокировать вход
        private const decimal MaxDistancePctToBlockEntry = 0.0015m; // 0.15%

        // Насколько дальше за кластер уводить стоп
        private const decimal StopBeyondClusterPct = 0.0007m; // 0.07%

        private const int DefaultDepth = 50;

        public AiLiquidityClusterService(
            ILogger<AiLiquidityClusterService> logger,
            MarketDataService marketData)
        {
            _logger = logger;
            _marketData = marketData;
        }

        /// <summary>
        /// Backward-compatible sync wrapper.
        /// ВАЖНО: в production лучше вызывать async-версию из StrategyEngine,
        /// но этот метод оставляем чтобы не ломать текущие вызовы.
        /// </summary>
        public TradeSignal? FilterAndAdjust(TradeSignal signal)
        {
            // Do NOT use .Result. This still blocks, but avoids AggregateException wrapping.
            // Prefer async path in new code.
            return FilterAndAdjustAsync(signal, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        public async Task<TradeSignal?> FilterAndAdjustAsync(TradeSignal signal, CancellationToken ct)
        {
            OrderBookSnapshot? snapshot = null;

            try
            {
                snapshot = await _marketData.GetOrderBookAsync(signal.Symbol, DefaultDepth)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LiquidityCluster: depth fetch failed for {symbol} → soft-pass", signal.Symbol);
                return signal; // fail-safe: do not block trade due to depth errors
            }

            if (snapshot == null)
            {
                _logger.LogDebug("LiquidityCluster: no depth for {symbol} → soft-pass", signal.Symbol);
                return signal;
            }

            // Validate book
            if (snapshot.Bids == null || snapshot.Asks == null || snapshot.Bids.Count == 0 || snapshot.Asks.Count == 0)
            {
                _logger.LogDebug("LiquidityCluster: empty depth for {symbol} → soft-pass", signal.Symbol);
                return signal;
            }

            // Ensure bestBid/bestAsk sane
            var bestBid = snapshot.Bids[0].price;
            var bestAsk = snapshot.Asks[0].price;

            if (bestBid <= 0m || bestAsk <= 0m || bestAsk < bestBid)
            {
                // book may be unsorted or stale; try fallback by sorting shallowly
                var bids = snapshot.Bids.OrderByDescending(x => x.price).ToList();
                var asks = snapshot.Asks.OrderBy(x => x.price).ToList();

                if (bids.Count == 0 || asks.Count == 0)
                    return signal;

                bestBid = bids[0].price;
                bestAsk = asks[0].price;

                if (bestBid <= 0m || bestAsk <= 0m || bestAsk < bestBid)
                    return signal;

                snapshot = new OrderBookSnapshot(snapshot.Symbol, bids, asks, snapshot.Timestamp);
            }

            var analysis = AnalyzeInternal(signal, snapshot);

            if (analysis.IsDangerZone)
            {
                _logger.LogInformation(
                    "LiquidityCluster: DANGER for {symbol} side={side}, reason={reason}, imbalance={imb:F2}",
                    signal.Symbol, signal.Side, analysis.Reason ?? "n/a", analysis.Imbalance);

                return null;
            }

            // Apply adjustments (only if meaningful)
            if (analysis.SuggestedStopLoss is decimal newSl)
            {
                if (newSl > 0m && newSl != signal.StopLoss)
                {
                    _logger.LogInformation("LiquidityCluster: adjust SL for {symbol} {old} → {new}",
                        signal.Symbol, signal.StopLoss, newSl);
                    signal.StopLoss = newSl;
                }
            }

            if (analysis.SuggestedEntry is decimal newEntry)
            {
                if (newEntry > 0m && newEntry != signal.EntryPrice)
                {
                    _logger.LogInformation("LiquidityCluster: adjust Entry for {symbol} {old} → {new}",
                        signal.Symbol, signal.EntryPrice, newEntry);
                    signal.EntryPrice = newEntry;
                }
            }

            return signal;
        }

        // -------------------- CORE --------------------

        private LiquidityAnalysisResult AnalyzeInternal(
            TradeSignal signal,
            OrderBookSnapshot depth)
        {
            var result = new LiquidityAnalysisResult
            {
                Symbol = signal.Symbol
            };

            if (depth.Bids.Count == 0 || depth.Asks.Count == 0)
                return result;

            var bestBid = depth.Bids[0].price;
            var bestAsk = depth.Asks[0].price;
            if (bestBid <= 0m || bestAsk <= 0m)
                return result;

            var mid = (bestBid + bestAsk) / 2m;
            if (mid <= 0m)
                return result;

            // 1) Total notionals + imbalance
            decimal bidNotional = 0m;
            for (int i = 0; i < depth.Bids.Count; i++)
            {
                var (price, qty) = depth.Bids[i];
                if (price > 0m && qty > 0m) bidNotional += price * qty;
            }

            decimal askNotional = 0m;
            for (int i = 0; i < depth.Asks.Count; i++)
            {
                var (price, qty) = depth.Asks[i];
                if (price > 0m && qty > 0m) askNotional += price * qty;
            }

            result.BidNotional = bidNotional;
            result.AskNotional = askNotional;

            var total = bidNotional + askNotional;
            if (total > 0m)
                result.Imbalance = (bidNotional - askNotional) / total;

            // 2) Find clusters
            var clusters = new List<LiquidityCluster>(capacity: 16);

            for (int i = 0; i < depth.Bids.Count; i++)
            {
                var (price, qty) = depth.Bids[i];
                var notional = price * qty;
                if (notional < ClusterNotionalThreshold)
                    continue;

                var distPct = (price - mid) / mid;

                clusters.Add(new LiquidityCluster
                {
                    Symbol = signal.Symbol,
                    Side = LiquidityClusterSide.Bid,
                    Price = price,
                    Quantity = qty,
                    Notional = notional,
                    DistanceFromMidPercent = distPct,
                    IsMajor = true
                });
            }

            for (int i = 0; i < depth.Asks.Count; i++)
            {
                var (price, qty) = depth.Asks[i];
                var notional = price * qty;
                if (notional < ClusterNotionalThreshold)
                    continue;

                var distPct = (price - mid) / mid;

                clusters.Add(new LiquidityCluster
                {
                    Symbol = signal.Symbol,
                    Side = LiquidityClusterSide.Ask,
                    Price = price,
                    Quantity = qty,
                    Notional = notional,
                    DistanceFromMidPercent = distPct,
                    IsMajor = true
                });
            }

            result.Clusters = clusters;

            // 3) Wall near entry (against the signal)
            if (clusters.Count > 0 && signal.EntryPrice > 0m)
            {
                if (signal.Side == SignalSide.Buy)
                {
                    var nearestAsk = clusters
                        .Where(c => c.Side == LiquidityClusterSide.Ask && c.Price >= signal.EntryPrice)
                        .OrderBy(c => c.Price)
                        .FirstOrDefault();

                    if (nearestAsk != null)
                    {
                        var distPct = (nearestAsk.Price - signal.EntryPrice) / signal.EntryPrice;
                        if (distPct < MaxDistancePctToBlockEntry)
                        {
                            result.IsDangerZone = true;
                            result.Reason = $"Strong ASK wall near entry ({distPct:P2}) at {nearestAsk.Price}";
                            return result;
                        }
                    }
                }
                else if (signal.Side == SignalSide.Sell)
                {
                    var nearestBid = clusters
                        .Where(c => c.Side == LiquidityClusterSide.Bid && c.Price <= signal.EntryPrice)
                        .OrderByDescending(c => c.Price)
                        .FirstOrDefault();

                    if (nearestBid != null)
                    {
                        var distPct = (signal.EntryPrice - nearestBid.Price) / signal.EntryPrice;
                        if (distPct < MaxDistancePctToBlockEntry)
                        {
                            result.IsDangerZone = true;
                            result.Reason = $"Strong BID wall near entry ({distPct:P2}) at {nearestBid.Price}";
                            return result;
                        }
                    }
                }
            }

            // 4) Imbalance danger
            if (Math.Abs(result.Imbalance) > ImbalanceDangerThreshold)
            {
                result.IsDangerZone = true;
                result.Reason = $"Orderbook imbalance={result.Imbalance:F2} > {ImbalanceDangerThreshold:F2}";
                return result;
            }

            // 5) Soft SL adjustment
            if (clusters.Count > 0 && signal.EntryPrice > 0m && signal.StopLoss > 0m)
            {
                if (signal.Side == SignalSide.Buy)
                {
                    var support = clusters
                        .Where(c => c.Side == LiquidityClusterSide.Bid &&
                                    c.Price < signal.EntryPrice &&
                                    c.Price > signal.StopLoss)
                        .OrderByDescending(c => c.Notional)
                        .FirstOrDefault();

                    if (support != null)
                    {
                        var newSl = support.Price * (1m - StopBeyondClusterPct);
                        if (newSl > 0m && newSl < signal.StopLoss)
                            result.SuggestedStopLoss = newSl;
                    }
                }
                else if (signal.Side == SignalSide.Sell)
                {
                    var resistance = clusters
                        .Where(c => c.Side == LiquidityClusterSide.Ask &&
                                    c.Price > signal.EntryPrice &&
                                    c.Price < signal.StopLoss)
                        .OrderByDescending(c => c.Notional)
                        .FirstOrDefault();

                    if (resistance != null)
                    {
                        var newSl = resistance.Price * (1m + StopBeyondClusterPct);
                        if (newSl > 0m && newSl > signal.StopLoss)
                            result.SuggestedStopLoss = newSl;
                    }
                }
            }

            return result;
        }
    }
}
