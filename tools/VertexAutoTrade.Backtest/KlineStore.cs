using System.Globalization;
using System.Text.Json;
using Binance.Net.Objects.Models.Futures;

namespace VertexAutoTrade.Backtest;

/// <summary>
/// Downloads closed USDⓈ-M futures klines from Binance's public REST endpoint
/// (no API key) and caches them as CSV under ./bt-data so reruns are instant.
/// </summary>
public static class KlineStore
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    public static string Root { get; set; } = Path.Combine(Environment.CurrentDirectory, "bt-data");

    public static TimeSpan Duration(string interval) => interval switch
    {
        "1m" => TimeSpan.FromMinutes(1),
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "1h" => TimeSpan.FromHours(1),
        "4h" => TimeSpan.FromHours(4),
        "1d" => TimeSpan.FromDays(1),
        _ => throw new ArgumentException("interval " + interval)
    };

    public static async Task<List<BinanceFuturesUsdtKline>> LoadAsync(
        string symbol, string interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Root);
        string file = Path.Combine(Root, $"{symbol}_{interval}_{fromUtc:yyyyMMdd}_{toUtc:yyyyMMddHH}.csv");
        if (File.Exists(file)) return Read(file);

        var all = new List<BinanceFuturesUsdtKline>();
        long start = new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds();
        long end = new DateTimeOffset(toUtc).ToUnixTimeMilliseconds();
        var dur = Duration(interval);
        while (start < end)
        {
            string url = $"https://fapi.binance.com/fapi/v1/klines?symbol={symbol}&interval={interval}&startTime={start}&endTime={end}&limit=1500";
            string json = "";
            for (int attempt = 0; ; attempt++)
            {
                try { json = await Http.GetStringAsync(url, ct); break; }
                catch when (attempt < 4) { await Task.Delay(1500 * (attempt + 1), ct); }
            }
            using var doc = JsonDocument.Parse(json);
            int n = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var k = new BinanceFuturesUsdtKline
                {
                    OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()).UtcDateTime,
                    OpenPrice = D(row[1]),
                    HighPrice = D(row[2]),
                    LowPrice = D(row[3]),
                    ClosePrice = D(row[4]),
                    Volume = D(row[5]),
                    CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(row[6].GetInt64()).UtcDateTime,
                    QuoteVolume = D(row[7]),
                };
                // Only fully closed bars.
                if (k.OpenTime + dur <= toUtc) all.Add(k);
                n++;
            }
            if (n == 0) break;
            start = new DateTimeOffset(all.Count > 0 ? all[^1].OpenTime : fromUtc).ToUnixTimeMilliseconds()
                    + (long)dur.TotalMilliseconds;
            await Task.Delay(120, ct); // stay far below Binance weight limits
        }
        all = all.GroupBy(x => x.OpenTime).Select(g => g.First()).OrderBy(x => x.OpenTime).ToList();
        Write(file, all);
        return all;
    }

    private static decimal D(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    private static void Write(string file, List<BinanceFuturesUsdtKline> ks)
    {
        using var w = new StreamWriter(file);
        foreach (var k in ks)
            w.WriteLine(string.Join(',', k.OpenTime.Ticks, k.OpenPrice.ToString(CultureInfo.InvariantCulture),
                k.HighPrice.ToString(CultureInfo.InvariantCulture), k.LowPrice.ToString(CultureInfo.InvariantCulture),
                k.ClosePrice.ToString(CultureInfo.InvariantCulture), k.Volume.ToString(CultureInfo.InvariantCulture),
                k.CloseTime.Ticks, k.QuoteVolume.ToString(CultureInfo.InvariantCulture)));
    }

    private static List<BinanceFuturesUsdtKline> Read(string file)
    {
        var list = new List<BinanceFuturesUsdtKline>();
        foreach (var line in File.ReadLines(file))
        {
            var p = line.Split(',');
            if (p.Length < 8) continue;
            list.Add(new BinanceFuturesUsdtKline
            {
                OpenTime = new DateTime(long.Parse(p[0]), DateTimeKind.Utc),
                OpenPrice = decimal.Parse(p[1], CultureInfo.InvariantCulture),
                HighPrice = decimal.Parse(p[2], CultureInfo.InvariantCulture),
                LowPrice = decimal.Parse(p[3], CultureInfo.InvariantCulture),
                ClosePrice = decimal.Parse(p[4], CultureInfo.InvariantCulture),
                Volume = decimal.Parse(p[5], CultureInfo.InvariantCulture),
                CloseTime = new DateTime(long.Parse(p[6]), DateTimeKind.Utc),
                QuoteVolume = decimal.Parse(p[7], CultureInfo.InvariantCulture),
            });
        }
        return list;
    }
}
