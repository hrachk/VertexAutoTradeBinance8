using System.Globalization;
using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class MarketSnapshotFileService
{
    private readonly string _file;
    private DateTime _lastWrite;
    private IReadOnlyList<MarketSeries>? _cache;
    public MarketSnapshotFileService(IConfiguration cfg)
    {
        // SharedData:Root = C:\Vertex\Engines\client_001  (engine writes here)
        // AppContext.BaseDirectory = web bin folder (wrong!)
        var root = cfg["SharedData:Root"];
        if (!string.IsNullOrEmpty(root))
            _file = Path.Combine(root, "market", "klines_bootstrap.json");
        else
            _file = Path.Combine(AppContext.BaseDirectory, "market", "klines_bootstrap.json");
    }
 
    private static decimal ReadDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return el.GetDecimal();

        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var v))
            return v;

        return 0m;
    }


    private static long ReadOpenTime(JsonElement el)
    {
        // case 1: unix ms number
        if (el.ValueKind == JsonValueKind.Number)
            return el.GetInt64();

        // case 2: unix ms string
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();

            if (long.TryParse(s, out var unix))
                return unix;

            // case 3: ISO-8601 datetime string
            if (DateTimeOffset.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dto))
                return dto.ToUnixTimeMilliseconds();
        }

        return 0;
    }

    public async Task<IReadOnlyList<MarketSeries>> LoadAsync()
    {
        if (!File.Exists(_file))
        {
            Console.WriteLine($"[MarketSnapshotFileService] FILE NOT FOUND: {_file}");
            return Array.Empty<MarketSeries>();
        }
        Console.WriteLine($"[MarketSnapshotFileService] Loading from: {_file}");

        var wt = File.GetLastWriteTimeUtc(_file);
        if (_cache != null && wt == _lastWrite)
            return _cache;

        _lastWrite = wt;
        _cache = await LoadInternalAsync();
        return _cache;
    }
    //public async Task<IReadOnlyList<MarketSeries>> LoadAsync()
    //{

    //    try
    //    {
    //        if (!File.Exists(_file))
    //            return Array.Empty<MarketSeries>();

    //        using var fs = new FileStream(
    //            _file,
    //            FileMode.Open,
    //            FileAccess.Read,
    //            FileShare.ReadWrite);

    //        using var doc = await JsonDocument.ParseAsync(fs);

    //        var result = new List<MarketSeries>();

    //        // ROOT = object: "BTCUSDT:FiveMinutes": [...]
    //        if (doc.RootElement.ValueKind != JsonValueKind.Object)
    //            return result;

    //        foreach (var prop in doc.RootElement.EnumerateObject())
    //        {
    //            var parts = prop.Name.Split(':', 2);
    //            if (parts.Length != 2)
    //                continue;

    //            var symbol = parts[0];
    //            var timeframe = parts[1];

    //            if (prop.Value.ValueKind != JsonValueKind.Array)
    //                continue;

    //            var klines = new List<KlineDto>();

    //            foreach (var k in prop.Value.EnumerateArray())
    //            {
    //                // =====================================================
    //                // LEGACY FORMAT: [ts, o, h, l, c, v]
    //                // =====================================================
    //                if (k.ValueKind == JsonValueKind.Array && k.GetArrayLength() >= 6)
    //                {
    //                    klines.Add(new KlineDto(
    //    ReadOpenTime(k[0]),
    //    ReadDecimal(k[1]),
    //    ReadDecimal(k[2]),
    //    ReadDecimal(k[3]),
    //    ReadDecimal(k[4]),
    //    ReadDecimal(k[5])
    //));
    //                }
    //                // =====================================================
    //                // NEW FORMAT: { openTime, open, high, low, close, volume }
    //                // =====================================================
    //                else if (k.ValueKind == JsonValueKind.Object)
    //                {
    //                    var ot = ReadOpenTime(k.GetProperty("openTime"));
    //                    if (ot <= 0)
    //                        continue;

    //                    klines.Add(new KlineDto(
    //                        ot,
    //                        ReadDecimal(k.GetProperty("open")),
    //                        ReadDecimal(k.GetProperty("high")),
    //                        ReadDecimal(k.GetProperty("low")),
    //                        ReadDecimal(k.GetProperty("close")),
    //                        ReadDecimal(k.GetProperty("volume"))
    //                    ));
    //                }
    //            }

    //            if (klines.Count > 0)
    //                result.Add(new MarketSeries(symbol, timeframe, klines));
    //        }

    //        return result;
    //    }
    //    catch (Exception ex)
    //    {
    //        // если есть ILogger — логируй
    //        return Array.Empty<MarketSeries>();
    //    }



    //}

    private async Task<IReadOnlyList<MarketSeries>> LoadInternalAsync()
    {
        using var fs = new FileStream(
            _file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        using var doc = await JsonDocument.ParseAsync(fs);

        var result = new List<MarketSeries>(128);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var sep = prop.Name.IndexOf(':');
            if (sep <= 0) continue;

            var symbol = prop.Name[..sep];
            var timeframe = prop.Name[(sep + 1)..];

            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;

            var klines = new List<KlineDto>(prop.Value.GetArrayLength());

            foreach (var k in prop.Value.EnumerateArray())
            {
                if (k.ValueKind == JsonValueKind.Array && k.GetArrayLength() >= 6)
                {
                    klines.Add(new KlineDto(
                        ReadOpenTime(k[0]),
                        ReadDecimal(k[1]),
                        ReadDecimal(k[2]),
                        ReadDecimal(k[3]),
                        ReadDecimal(k[4]),
                        ReadDecimal(k[5])
                    ));
                }
                else if (k.ValueKind == JsonValueKind.Object)
                {
                    var ot = ReadOpenTime(k.GetProperty("openTime"));
                    if (ot <= 0) continue;

                    var dto = new KlineDto(
                        ot,
                        ReadDecimal(k.GetProperty("open")),
                        ReadDecimal(k.GetProperty("high")),
                        ReadDecimal(k.GetProperty("low")),
                        ReadDecimal(k.GetProperty("close")),
                        ReadDecimal(k.GetProperty("volume"))
                    );

                    // Older snapshot files (written before this field
                    // existed on the Engine side) simply won't have this
                    // property — TryGetProperty handles that gracefully.
                    if (k.TryGetProperty("takerBuyBaseVolume", out var tbv) && tbv.ValueKind != JsonValueKind.Null)
                        dto = dto with { TakerBuyBaseVolume = ReadDecimal(tbv) };

                    klines.Add(dto);
                }
            }

            if (klines.Count > 0)
                result.Add(new MarketSeries(symbol, timeframe, klines));
        }

        return result;
    }

}
