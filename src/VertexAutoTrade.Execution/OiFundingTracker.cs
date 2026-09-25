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
}

/// <summary>
/// Tracks funding + OI. Blocks longs when funding is extreme positive and OI falling
/// (overheated longs). Blocks shorts when funding extreme negative and OI falling.
/// </summary>
public sealed class OiFundingTracker
{
    private readonly ConcurrentDictionary<string, OiFundingSnapshot> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly decimal _fundingExtreme;
    public OiFundingTracker(decimal fundingExtreme = 0.0005m) => _fundingExtreme = fundingExtreme;

    public void Update(OiFundingSnapshot snap)
    {
        _last.AddOrUpdate(snap.Symbol, snap, (_, prev) => snap with
        {
            PrevOpenInterest = prev.OpenInterest
        });
    }

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

    /// <summary>Pull premiumIndex funding from Binance public REST (no keys).</summary>
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
                && decimal.TryParse(f.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : 0m;
            return new OiFundingSnapshot { Symbol = symbol, FundingRate = fr, Utc = DateTime.UtcNow };
        }
        catch { return null; }
    }
}
