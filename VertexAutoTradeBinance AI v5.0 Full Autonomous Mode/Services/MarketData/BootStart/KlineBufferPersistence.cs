using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Text.Json;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Services.MarketData.MDTO;

public sealed class KlineBufferPersistence
{
    private readonly MarketDataKlineBuffer _buffer;
    private readonly string _path;
    private readonly ILogger<KlineBufferPersistence> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public KlineBufferPersistence(
        MarketDataKlineBuffer buffer,
        IConfiguration cfg,
        ILogger<KlineBufferPersistence> logger)
    {
        _buffer = buffer;
        _logger = logger;

        //var dir = Path.Combine(AppContext.BaseDirectory, "market");
        //Directory.CreateDirectory(dir);
        _path = Path.Combine(AppContext.BaseDirectory, "market", "klines_bootstrap.json");
    }

    // =====================================================================
    // RESTORE
    // =====================================================================
    public async Task RestoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("[BOOT] No kline snapshot → cold start");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_path, ct);
            if (string.IsNullOrWhiteSpace(json))
                return;

            using var doc = JsonDocument.Parse(json);

            int restoredStreams = 0;
            int skippedItems = 0;

            foreach (var stream in doc.RootElement.EnumerateObject())
            {
                var key = stream.Name;
                var parts = key.Split(':');
                if (parts.Length != 2)
                    continue;

                var symbol = parts[0];
                if (!Enum.TryParse<KlineInterval>(parts[1], ignoreCase: true, out var tf))
                {
                    _logger.LogWarning("[BOOT] Unknown timeframe in snapshot: {tf}", parts[1]);
                    continue;
                }

                if (stream.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var el in stream.Value.EnumerateArray())
                {
                    try
                    {
                        // 1️⃣ Пытаемся НОВЫЙ формат
                        if (el.TryGetProperty("openTime", out _))
                        {
                            var dto = el.Deserialize<KlineSnapshotDto>(JsonOpts);
                            if (dto == null) continue;

                            _buffer.Upsert(symbol, tf, new BinanceFuturesUsdtKline
                            {
                                OpenTime = dto.OpenTime,
                                OpenPrice = dto.Open,
                                HighPrice = dto.High,
                                LowPrice = dto.Low,
                                ClosePrice = dto.Close,
                                Volume = dto.Volume
                            });
                        }
                        // 2️⃣ Пытаемся СТАРЫЙ формат (Binance)
                        else
                        {
                            var k = el.Deserialize<BinanceFuturesUsdtKline>(JsonOpts);
                            if (k == null) continue;

                            _buffer.Upsert(symbol, tf, k);
                        }
                    }
                    catch (Exception ex)
                    {
                        skippedItems++;
                        _logger.LogWarning(ex,
                            "[BOOT] Failed to deserialize kline item in {stream}",
                            key);
                    }
                }

                restoredStreams++;
            }

            _logger.LogInformation(
                "[BOOT] Kline buffer restored: {streams} streams, skipped={skipped}",
                restoredStreams,
                skippedItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[BOOT] Failed to restore kline buffer → starting empty");
        }
    }

    // =====================================================================
    // SAVE
    // =====================================================================
    public async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            var dump = _buffer.DumpAll()
    .ToDictionary(
        kv => kv.Key,
        kv => kv.Value.ToList());

            var dtoDump = new Dictionary<string, List<KlineSnapshotDto>>(dump.Count);

            foreach (var (key, klines) in dump)
            {
                var list = new List<KlineSnapshotDto>(klines.Count);

                foreach (var k in klines)
                {
                    list.Add(new KlineSnapshotDto
                    {
                        OpenTime = k.OpenTime,
                        Open = k.OpenPrice,
                        High = k.HighPrice,
                        Low = k.LowPrice,
                        Close = k.ClosePrice,
                        Volume = k.Volume
                    });
                }

                dtoDump[key] = list;
            }

            var json = JsonSerializer.Serialize(dtoDump, JsonOpts);

            var tmp = _path + ".tmp";

            await File.WriteAllTextAsync(tmp, json, ct);

            // атомарная замена
            File.Replace(tmp, _path, null);

            _logger.LogInformation(
                "[BOOT] Kline buffer saved: {cnt} streams",
                dtoDump.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BOOT] Failed to save kline buffer");
        }
    }
}
