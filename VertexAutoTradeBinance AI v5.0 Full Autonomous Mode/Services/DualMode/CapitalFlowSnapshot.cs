namespace VertexAutoTradeBinance8.Services.DualMode;

/// <summary>
/// Observable capital / volume context for one symbol at decision time.
/// Built from order book, OI/funding, and recent volume — not from local candle slope alone.
/// </summary>
public sealed class CapitalFlowSnapshot
{
    public string Symbol { get; init; } = "";
    public bool IsLong { get; init; }

    /// <summary>Last bar volume / average volume (e.g. 1.2 = 20% above average).</summary>
    public decimal VolumeRatio { get; init; }

    /// <summary>Open interest change fraction since previous sample.</summary>
    public decimal OiDeltaPct { get; init; }

    /// <summary>Funding rate (perpetual).</summary>
    public decimal FundingRate { get; init; }

    /// <summary>
    /// Book pressure in trade direction: positive = more size on supporting side.
    /// LONG: (bidDepth - askDepth) / (bid+ask); SHORT: inverse.
    /// </summary>
    public decimal BookAlign { get; init; }

    public decimal SpreadPct { get; init; }

    public bool HasBook { get; init; }
    public bool HasOi { get; init; }
    public bool HasVolume { get; init; }

    /// <summary>
    /// True when at least one real capital signal agrees with the trade side
    /// (volume expansion, OI with direction, or book tilt).
    /// </summary>
    public bool HasCapitalConfirmation(decimal minVolRatio, decimal minOiAbs, decimal minBook)
    {
        bool volOk = HasVolume && VolumeRatio >= minVolRatio;
        bool oiOk = HasOi && Math.Abs(OiDeltaPct) >= minOiAbs &&
                    ((IsLong && OiDeltaPct > 0) || (!IsLong && OiDeltaPct > 0) ||
                     (IsLong && OiDeltaPct > 0));
        // OI rising supports continuation for both sides when price direction already chosen;
        // stronger: OI up + side aligned is enough as "money entering".
        if (HasOi && OiDeltaPct >= minOiAbs)
            oiOk = true;
        if (HasOi && OiDeltaPct <= -minOiAbs && !IsLong)
            oiOk = true; // short + OI dump can still be distribution — treat soft

        bool bookOk = HasBook && BookAlign >= minBook;

        // Funding extreme against the crowd can still allow if book/vol agree
        return volOk || oiOk || bookOk;
    }

    public string Summarize() =>
        $"volR={VolumeRatio:F2} oiΔ={OiDeltaPct:P2} book={BookAlign:F2} spr={SpreadPct:P3} fund={FundingRate:P4}";
}
