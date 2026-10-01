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
    /// <summary>
    /// Capital is present only when volume expands and/or OI+book agree with the side.
    /// Single weak OI tick alone is NOT enough (that was blind local-trend pass-through).
    /// </summary>
    public bool HasCapitalConfirmation(decimal minVolRatio, decimal minOiAbs, decimal minBook)
    {
        bool volOk = HasVolume && VolumeRatio >= minVolRatio;

        // OI: rising OI with price direction = real money; falling OI on long = distribution → not OK
        bool oiOk = false;
        if (HasOi && Math.Abs(OiDeltaPct) >= minOiAbs)
        {
            if (IsLong && OiDeltaPct >= minOiAbs) oiOk = true;
            if (!IsLong && OiDeltaPct >= minOiAbs) oiOk = true; // shorts often open with OI up
            // OI collapsing against a long is anti-confirmation
            if (IsLong && OiDeltaPct <= -minOiAbs) oiOk = false;
        }

        bool bookOk = HasBook && BookAlign >= minBook;

        // Strict: volume expansion alone OR (OI + book) together — never OI-only
        if (volOk) return true;
        if (oiOk && bookOk) return true;
        // If we have no volume data at all, demand both OI and book
        if (!HasVolume && oiOk && bookOk) return true;
        return false;
    }

    /// <summary>Volume leg only — used when RequireVolumeOnTrend is on.</summary>
    public bool HasVolumeExpansion(decimal minVolRatio) =>
        HasVolume && VolumeRatio >= minVolRatio;

    public string Summarize() =>
        $"volR={VolumeRatio:F2} oiΔ={OiDeltaPct:P2} book={BookAlign:F2} spr={SpreadPct:P3} fund={FundingRate:P4}";
}
