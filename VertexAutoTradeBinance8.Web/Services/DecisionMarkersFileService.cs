using System.Globalization;
using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class DecisionMarkersFileService
{
    private readonly string _file;
    private DateTime _lastWrite;

    // key = SYMBOL:TF
    private Dictionary<string, IReadOnlyList<UiDecisionMarker>>? _cache;

    public DecisionMarkersFileService()
    {
        _file = Path.Combine(
            AppContext.BaseDirectory,
            "market",
            "decision_markers.json");
    }

    // ============================================================
    // PUBLIC API
    // ============================================================
    public async Task<IReadOnlyList<UiDecisionMarker>> LoadAsync(
        string symbol,
        string timeframe)
    {
        if (!File.Exists(_file))
            return Array.Empty<UiDecisionMarker>();

        var wt = File.GetLastWriteTimeUtc(_file);
        if (_cache == null || wt != _lastWrite)
        {
            _lastWrite = wt;
            _cache = await LoadInternalAsync();
        }

        var key = Key(symbol, timeframe);
        return _cache.TryGetValue(key, out var list)
            ? list
            : Array.Empty<UiDecisionMarker>();
    }

    // ============================================================
    // INTERNAL
    // ============================================================
    private async Task<Dictionary<string, IReadOnlyList<UiDecisionMarker>>> LoadInternalAsync()
    {
        using var fs = new FileStream(
            _file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        using var doc = await JsonDocument.ParseAsync(fs);

        var result = new Dictionary<string, IReadOnlyList<UiDecisionMarker>>(128);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;

            var list = new List<UiDecisionMarker>();

            foreach (var m in prop.Value.EnumerateArray())
            {
                if (!m.TryGetProperty("candleTimeUtc", out var tEl))
                    continue;

                var candleMs = ReadTimeToUnixMs(tEl);
                if (candleMs <= 0)
                    continue;

                UiMarkerType type = UiMarkerType.EntryRejected;

                if (m.TryGetProperty("type", out var ty))
                {
                    if (ty.ValueKind == JsonValueKind.Number)
                        type = (UiMarkerType)ty.GetInt32();
                    else if (ty.ValueKind == JsonValueKind.String)
                        type = ParseType(ty.GetString());
                }

                var code = m.TryGetProperty("code", out var cd)
                    ? cd.GetString() ?? ""
                    : "";

                var details = m.TryGetProperty("details", out var dt)
                    ? dt.GetString()
                    : null;

                list.Add(new UiDecisionMarker(
                    CandleOpenTimeMs: candleMs,
                    Type: type,
                    Code: code,
                    Details: details
                ));
            }

            if (list.Count > 0)
                result[prop.Name] = list;
        }

        return result;
    }

    private static string Key(string symbol, string tf)
        => $"{symbol.ToUpperInvariant()}:{tf}";

    private static long ReadTimeToUnixMs(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return el.GetInt64();

        if (el.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                el.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
            return dto.ToUnixTimeMilliseconds();

        return 0;
    }

    private static UiMarkerType ParseType(string? s) => s switch
    {
        "Evaluated" => UiMarkerType.Evaluated,
        "BlockedLate" => UiMarkerType.BlockedLate,
        "LowVolume" => UiMarkerType.LowVolume,
        "ConfidenceBelow" => UiMarkerType.ConfidenceBelow,
        "EntryAllowed" => UiMarkerType.EntryAllowed,
        "EntryRejected" => UiMarkerType.EntryRejected,
        _ => UiMarkerType.EntryRejected
    };
}
