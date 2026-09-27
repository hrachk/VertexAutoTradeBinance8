using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using VertexAutoTrade.Core.Pipeline;

namespace VertexAutoTrade.Execution;

public sealed class OiFundingSnapshot
{
    public string Symbol { get; init; } = "";
    public decimal FundingRate { get; init; }
    public decimal OpenInterest { get; init; }
    public decimal? PrevOpenInterest { get; init; }
    public DateTime Utc { get; init; } = DateTime.UtcNow;

    /// <summary>OI change fraction vs previous sample (e.g. +0.01 = +1%).</summary>
    public decimal OiDeltaPct =>
        PrevOpenInterest is decimal p && p > 0
            ? (OpenInterest - p) / p
            : 0m;
}

/// <summary>
/// Tracks funding + OI from Binance public REST. Used by SignalQuality Derivatives factor
/// and institutional OI/funding gate.
/// </summary>
public sealed class OiFundingTracker
{
    private readonly ConcurrentDictionary<string, OiFundingSnapshot> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly decimal _fundingExtreme;
    public OiFundingTracker(decimal fundingExtreme = 0.0005m) => _fundingExtreme = fundingExtreme;

    public void Update(OiFundingSnapshot snap)
    {
        _last.AddOrUpdate(snap.Symbol, snap, (_, prev) => new OiFundingSnapshot
        {
            Symbol = snap.Symbol,
            FundingRate = snap.FundingRate,
            OpenInterest = snap.OpenInterest > 0 ? snap.OpenInterest : prev.OpenInterest,
            PrevOpenInterest = prev.OpenInterest > 0 ? prev.OpenInterest : snap.PrevOpenInterest,
            Utc = snap.Utc
        });
    }

    public bool TryGet(string symbol, out OiFundingSnapshot snap) =>
        _last.TryGetValue(symbol, out snap!);

    public GateDecision Evaluate(string symbol, bool isLong)
    {
        if (!_last.TryGetValue(symbol, out var s))
            return GateDecision.Ok(GateLayer.Sizing, "OI/Funding unknown — fail-open");

        var oiFalling = s.PrevOpenInterest is decimal p && p > 0 && s.OpenInterest < p * 0.995m;

        if (isLong && s.FundingRate >= _fundingExtreme && oiFalling)
            return GateDecision.Reject(GateLayer.Sizing, "FUNDING_OI_LONG_BLOCK",
                $"Funding {s.FundingRate:P4} high + OI falling — skip long");

        if (!isLong && s.FundingRate <= -_fundingExtreme && oiFalling)
            return GateDecision.Reject(GateLayer.Sizing, "FUNDING_OI_SHORT_BLOCK",
                $"Funding {s.FundingRate:P4} low + OI falling — skip short");

        return GateDecision.Ok(GateLayer.Sizing, $"funding={s.FundingRate:P4}");
    }

    /// <summary>
    /// Derivatives factor 0..1 for Signal Quality.
    /// Price-up + OI up (or price-down + OI up) → high; extreme funding against side → cut.
    /// </summary>
    public decimal ScoreDerivatives(string symbol, bool isLong, decimal? priceChangePct = null)
    {
        if (!_last.TryGetValue(symbol, out var s))
            return 0.55m; // neutral fail-open

        decimal score = 0.55m;
        decimal oiDelta = s.OiDeltaPct;

        // OI rising with direction of price (or unknown price → reward OI up)
        bool priceUp = priceChangePct is null or >= 0;
        bool priceDown = priceChangePct is null or <= 0;
        if (oiDelta > 0.002m)
        {
            if ((isLong && priceUp) || (!isLong && priceDown) || priceChangePct is null)
                score = 0.92m; // leveraged flow confirms
            else
                score = 0.70m; // OI up against price — mixed
        }
        else if (oiDelta < -0.002m)
        {
            score = 0.35m; // OI leaving — weaker continuation
        }

        // Funding extreme against the crowd direction of our trade
        // Long into very positive funding → squeeze risk
        if (isLong && s.FundingRate >= _fundingExtreme)
            score = Math.Max(0.15m, score - 0.35m);
        if (!isLong && s.FundingRate <= -_fundingExtreme)
            score = Math.Max(0.15m, score - 0.35m);

        // Mild reward when funding is mild and supports (not extreme)
        if (isLong && s.FundingRate > 0 && s.FundingRate < _fundingExtreme * 0.5m && oiDelta >= 0)
            score = Math.Min(1m, score + 0.05m);
        if (!isLong && s.FundingRate < 0 && s.FundingRate > -_fundingExtreme * 0.5m && oiDelta >= 0)
            score = Math.Min(1m, score + 0.05m);

        return Math.Clamp(score, 0.10m, 1.0m);
    }

    public static async Task<OiFundingSnapshot?> FetchFundingAsync(HttpClient http, string symbol, CancellationToken ct)
    {
        try
        {
            var url = $"https://fapi.binance.com/fapi/v1/premiumIndex?symbol={symbol}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var fr = doc.RootElement.TryGetProperty("lastFundingRate", out var f)
                && decimal.TryParse(f.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var fv)
                ? fv : 0m;
            return new OiFundingSnapshot { Symbol = symbol, FundingRate = fr, Utc = DateTime.UtcNow };
        }
        catch { return null; }
    }

    public static async Task<decimal?> FetchOpenInterestAsync(HttpClient http, string symbol, CancellationToken ct)
    {
        try
        {
            var url = $"https://fapi.binance.com/fapi/v1/openInterest?symbol={symbol}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("openInterest", out var oi)
                && decimal.TryParse(oi.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return v;
            return null;
        }
        catch { return null; }
    }

    /// <summary>Refresh funding + OI for symbol and store delta vs previous OI.</summary>
    public async Task<OiFundingSnapshot?> RefreshAsync(HttpClient http, string symbol, CancellationToken ct)
    {
        var fund = await FetchFundingAsync(http, symbol, ct).ConfigureAwait(false);
        var oi = await FetchOpenInterestAsync(http, symbol, ct).ConfigureAwait(false);
        if (fund is null && oi is null) return null;

        var snap = new OiFundingSnapshot
        {
            Symbol = symbol,
            FundingRate = fund?.FundingRate ?? 0m,
            OpenInterest = oi ?? 0m,
            Utc = DateTime.UtcNow
        };
        Update(snap);
        return _last.TryGetValue(symbol, out var s) ? s : snap;
    }
}
