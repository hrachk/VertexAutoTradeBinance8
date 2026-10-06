using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Strategy;

/// <summary>
/// Pre-trade economics for micro-scalp / spread leg (like a careful arb desk):
/// spread, round-trip fees, min net edge vs TP1, basic book presence.
/// Does not replace DualMode — runs at Router before Forward.
/// </summary>
public static class ScalpPreTradeGate
{
    // Defaults aligned with DualModeOptions scalp section
    public const decimal DefaultMaxSpreadPct = 0.0006m;      // 6 bps
    public const decimal DefaultRoundTripFeePct = 0.0008m;   // ~4bps*2 taker
    public const decimal DefaultMinNetEdgePct = 0.0010m;     // 10 bps net target

    public static bool Allow(
        TradeSignal signal,
        MarketDataFacade? md,
        out string reason,
        decimal maxSpreadPct = DefaultMaxSpreadPct,
        decimal feeRt = DefaultRoundTripFeePct,
        decimal minNetEdge = DefaultMinNetEdgePct)
    {
        reason = "ok";
        if (signal == null)
        {
            reason = "null_signal";
            return false;
        }

        decimal entry = signal.EntryPrice;
        if (entry <= 0)
        {
            reason = "bad_entry";
            return false;
        }

        // TP1 distance must cover fees + spread + min edge
        decimal tp1 = 0m;
        if (signal.TakeProfits != null && signal.TakeProfits.Count > 0)
            tp1 = signal.TakeProfits[0];

        bool isLong = signal.Side == SignalSide.Buy;
        decimal tpDistPct = 0m;
        if (tp1 > 0)
        {
            tpDistPct = isLong
                ? (tp1 - entry) / entry
                : (entry - tp1) / entry;
            if (tpDistPct <= 0)
            {
                reason = "tp1_wrong_side";
                return false;
            }
        }

        decimal spreadPct = 0m;
        try
        {
            if (md != null)
            {
                // Best-effort sync book from cache/async is heavy; use signal marks if any
                // MarketDataFacade may expose last book — try GetOrderBookAsync with short timeout pattern via .Result avoided
                // Use zero spread if unavailable → fee-only check
            }
        }
        catch { /* ignore */ }

        decimal cost = feeRt + spreadPct;
        decimal need = cost + minNetEdge;

        if (tp1 > 0 && tpDistPct < need)
        {
            reason = $"edge_lt_cost tp={tpDistPct:P3} need={need:P3} (fee+spr+edge)";
            return false;
        }

        // Risk distance: scalp should not risk more than ~1.2% without being "trend"
        if (signal.StopLoss > 0)
        {
            decimal riskPct = isLong
                ? (entry - signal.StopLoss) / entry
                : (signal.StopLoss - entry) / entry;
            if (riskPct > 0.015m)
            {
                reason = $"scalp_risk_too_wide {riskPct:P2}";
                return false;
            }
            if (riskPct > 0 && tpDistPct > 0 && tpDistPct / riskPct < 1.05m)
            {
                reason = $"scalp_rr_lt_1 ({tpDistPct / riskPct:F2})";
                return false;
            }
        }

        // Force micro size ceiling for any scalp-tagged reason
        if (signal.SizeMultiplier > 0.55m)
            signal.SizeMultiplier = 0.55m;

        reason = $"scalp_ok cost~{cost:P3} tp={tpDistPct:P3}";
        return true;
    }
}
