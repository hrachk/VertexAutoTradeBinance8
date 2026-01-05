using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Text.Json;
using VertexAutoTradeBinance8.MarketData;

public sealed class KlineBufferPersistence
{
    private readonly MarketDataKlineBuffer _buffer;
    private readonly string _path;
    private readonly ILogger<KlineBufferPersistence> _logger;

    public KlineBufferPersistence(
        MarketDataKlineBuffer buffer,
        IConfiguration cfg,
        ILogger<KlineBufferPersistence> logger)
    {
        _buffer = buffer;
        _logger = logger;

        var root = cfg["SharedData:Root"]
            ?? throw new InvalidOperationException("SharedData:Root not configured");

        var dir = Path.Combine(root, "market");
        Directory.CreateDirectory(dir);

        _path = Path.Combine(dir, "klines_bootstrap.json");
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("[BOOT] No kline snapshot → cold start");
            return;
        }

        var json = await File.ReadAllTextAsync(_path, ct);

        var data = JsonSerializer.Deserialize<
            Dictionary<string, List<BinanceFuturesUsdtKline>>>(json);

        if (data == null) return;

        foreach (var (key, klines) in data)
        {
            var parts = key.Split(':');
            var symbol = parts[0];
            var tf = Enum.Parse<KlineInterval>(parts[1]);

            foreach (var k in klines)
                _buffer.Upsert(symbol, tf, k);
        }

        _logger.LogInformation(
            "[BOOT] Kline buffer restored: {cnt} streams",
            data.Count);
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        var dump = _buffer.DumpAll();
        var json = JsonSerializer.Serialize(dump);

        var tmp = _path + ".tmp";

        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, _path, overwrite: true);

        _logger.LogInformation(
            "[BOOT] Kline buffer saved: {cnt} streams",
            dump.Count);
    }

}
