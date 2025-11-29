using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

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

        // Порог по объёму кластера (в USDT), чтобы считать его "значимым".
        private const decimal ClusterNotionalThreshold = 50_000m;

        // Порог дисбаланса, при котором считаем ситуацию опасной.
        private const decimal ImbalanceDangerThreshold = 0.75m;

        // Максимальное расстояние (в процентах) от entry до "стены", чтобы заблокировать вход.
        private const decimal MaxDistancePctToBlockEntry = 0.0015m; // 0.15%

        // Насколько дальше за кластер уводить стоп (в процентах).
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
        /// Высокоуровневая точка входа: фильтруем и при необходимости корректируем сигнал.
        /// Если ситуация опасная → возвращаем null.
        /// Иначе можем немного поправить StopLoss (за кластер) и Entry.
        /// </summary>
        public TradeSignal? FilterAndAdjust(TradeSignal signal)
        {
            var snapshot = _marketData
                .GetOrderBookAsync(signal.Symbol, DefaultDepth)
                .GetAwaiter()
                .GetResult();

            if (snapshot == null)
            {
                _logger.LogWarning("LiquidityCluster: no depth for {symbol} → skip filter", signal.Symbol);
                return signal;
            }

            var analysis = AnalyzeInternal(signal, snapshot);

            if (analysis.IsDangerZone)
            {
                _logger.LogInformation(
                    "LiquidityCluster: DANGER for {symbol} side={side}, reason={reason}, imbalance={imbalance:F2}",
                    signal.Symbol, signal.Side, analysis.Reason ?? "n/a", analysis.Imbalance);

                // Блокируем вход — считаем, что стакан с высокой вероятностью ведёт к стоп-ханту.
                return null;
            }

            // Мягкая корректировка SL/Entry, если найдены значимые кластеры.
            if (analysis.SuggestedStopLoss is decimal newSl)
            {
                _logger.LogInformation(
                    "LiquidityCluster: adjust SL for {symbol} {old} → {new}",
                    signal.Symbol, signal.StopLoss, newSl);

                signal.StopLoss = newSl;
            }

            if (analysis.SuggestedEntry is decimal newEntry)
            {
                _logger.LogInformation(
                    "LiquidityCluster: adjust Entry for {symbol} {old} → {new}",
                    signal.Symbol, signal.EntryPrice, newEntry);

                signal.EntryPrice = newEntry;
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
            var mid = (bestBid + bestAsk) / 2m;

            // 1) Суммарный нотионал и дисбаланс
            decimal bidNotional = 0;
            foreach (var (price, qty) in depth.Bids)
                bidNotional += price * qty;

            decimal askNotional = 0;
            foreach (var (price, qty) in depth.Asks)
                askNotional += price * qty;

            result.BidNotional = bidNotional;
            result.AskNotional = askNotional;

            var total = bidNotional + askNotional;
            if (total > 0)
                result.Imbalance = (bidNotional - askNotional) / total;

            // 2) Поиск кластеров
            var clusters = new List<LiquidityCluster>();

            foreach (var (price, qty) in depth.Bids)
            {
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

            foreach (var (price, qty) in depth.Asks)
            {
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

            // 3) Проверка "стены" рядом с entry в сторону против сигнала
            if (clusters.Count > 0)
            {
                if (signal.Side == SignalSide.Buy)
                {
                    // ищем ближайший крупный ASK выше entry (стена сверху)
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
                    // ищем ближайший крупный BID ниже entry
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

            // 4) Дисбаланс стакана как общий риск-фактор
            if (Math.Abs(result.Imbalance) > ImbalanceDangerThreshold)
            {
                // Сильный перекос → часто признак агрессивного выноса/сброса
                result.IsDangerZone = true;
                result.Reason = $"Orderbook imbalance={result.Imbalance:F2} > {ImbalanceDangerThreshold:F2}";
                return result;
            }

            // 5) Мягкая коррекция стопа относительно кластеров
            if (clusters.Count > 0)
            {
                if (signal.Side == SignalSide.Buy)
                {
                    // ищем крупный BID между SL и entry → ставим стоп ЧУТЬ ниже кластера
                    var support = clusters
                        .Where(c =>
                            c.Side == LiquidityClusterSide.Bid &&
                            c.Price < signal.EntryPrice &&
                            c.Price > signal.StopLoss)
                        .OrderByDescending(c => c.Notional)
                        .FirstOrDefault();

                    if (support != null)
                    {
                        var newSl = support.Price * (1m - StopBeyondClusterPct);
                        if (newSl < signal.StopLoss)
                            result.SuggestedStopLoss = newSl;
                    }
                }
                else if (signal.Side == SignalSide.Sell)
                {
                    // ищем крупный ASK между entry и SL → ставим стоп чуть выше стенки
                    var resistance = clusters
                        .Where(c =>
                            c.Side == LiquidityClusterSide.Ask &&
                            c.Price > signal.EntryPrice &&
                            c.Price < signal.StopLoss)
                        .OrderByDescending(c => c.Notional)
                        .FirstOrDefault();

                    if (resistance != null)
                    {
                        var newSl = resistance.Price * (1m + StopBeyondClusterPct);
                        if (newSl > signal.StopLoss)
                            result.SuggestedStopLoss = newSl;
                    }
                }
            }

            return result;
        }
    }
}
