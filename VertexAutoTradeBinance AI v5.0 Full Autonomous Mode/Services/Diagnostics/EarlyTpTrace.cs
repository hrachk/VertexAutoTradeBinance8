using System;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.Diagnostics
{
    /// <summary>
    /// EARLY TP decision trace (v8.2 PRO)
    /// Explains WHY EarlyTP was skipped or executed.
    /// Single-line, low-noise, production-safe.
    /// </summary>
    public static class EarlyTpTrace
    {
        public static void Skip(
            ILogger logger,
            string symbol,
            PositionSide side,
            decimal entry,
            decimal last,
            decimal atr,
            string reason,
            object? extra = null)
        {
            logger.LogInformation(
                "[EARLY-TP][SKIP][{symbol}][{side}] entry={entry} last={last} atr={atr} reason={reason}{extra}",
                symbol,
                side,
                F(entry),
                F(last),
                F(atr),
                reason,
                extra != null ? $" | {extra}" : string.Empty);
        }

        public static void Hit(
            ILogger logger,
            string symbol,
            PositionSide side,
            decimal entry,
            decimal last,
            decimal atr,
            decimal closeQty,
            decimal totalQty)
        {
            logger.LogWarning(
                "[EARLY-TP][HIT][{symbol}][{side}] entry={entry} last={last} atr={atr} close={close}/{total}",
                symbol,
                side,
                F(entry),
                F(last),
                F(atr),
                F(closeQty),
                F(totalQty));
        }

        private static string F(decimal v)
            => v.ToString("0.########", CultureInfo.InvariantCulture);
    }
}
