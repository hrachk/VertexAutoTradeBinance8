using Binance.Net.Enums;
using System;
using System.Collections.Generic;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// ReverseProbeEngine (PRO)
    /// Micro-probe против предыдущего тренда:
    /// - ТОЛЬКО после защиты (BE или close)
    /// - 5–10% обычного риска
    /// - Без market-flip
    /// </summary>
    public class ReverseProbeEngine
    {
        private static readonly Dictionary<string, DateTime> _lastProbeUtc = new();

        public TradeSignal? TryCreateProbe(
            string symbol,
            TradeSignal baseSignal,
            SmartRegimeInfo smart,
            bool positionIsProtected,
            decimal atr)
        {
            if (!positionIsProtected)
                return null;

            // Только сильный противоположный режим
            bool flipDown =
                baseSignal.Side == SignalSide.Buy &&
                smart.BaseRegime == MarketRegime.StrongDownTrend &&
                smart.TrendSlopePercent < -0.01m;

            bool flipUp =
                baseSignal.Side == SignalSide.Sell &&
                smart.BaseRegime == MarketRegime.StrongUpTrend &&
                smart.TrendSlopePercent > 0.01m;

            if (!flipDown && !flipUp)
                return null;

            // Anti-spam: 1 probe на символ раз в 5 минут
            if (_lastProbeUtc.TryGetValue(symbol, out var last)
                && (DateTime.UtcNow - last) < TimeSpan.FromMinutes(5))
                return null;

            // Micro size = 7% стандартного риска
            decimal riskScale = 0.07m;

            var side = flipDown ? SignalSide.Sell : SignalSide.Buy;

            var entry = baseSignal.EntryPrice;
            var sl = side == SignalSide.Sell
                ? entry + atr * 0.6m
                : entry - atr * 0.6m;

            var tp = side == SignalSide.Sell
                ? entry - atr * 1.2m
                : entry + atr * 1.2m;

            var probe = new TradeSignal
            {
                Symbol = symbol,
                Side = side,
                EntryPrice = entry,
                StopLoss = sl,
                Atr = atr,
                TakeProfits = new List<decimal> { tp },
                Time = DateTime.UtcNow,
                Timeframe = baseSignal.Timeframe,
                Reason = "REVERSE_PROBE",
                IsSuperSignal = false 
            };

            _lastProbeUtc[symbol] = DateTime.UtcNow;
            return probe;
        }
    }
}
