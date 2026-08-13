using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace VertexAutoTradeBinance8.Web.Controllers;

[ApiController]
[Route("api/market")]
public class MarketDataController : ControllerBase
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<MarketDataController> _logger;
    private static readonly string[] DefaultSymbols =
    {
        "BTCUSDT","ETHUSDT","BNBUSDT","SOLUSDT","XRPUSDT","DOGEUSDT","ADAUSDT",
        "SKHYUSDT","SKHYNIXUSDT","SNXXUSDT","SNDKUSDT","SOXLUSDT","SAMSUNGUSDT",
        "AKEUSDT","APRUSDT","CLUSDT","NBISUSDT"
    };

    public MarketDataController(IHttpClientFactory http, ILogger<MarketDataController> logger)
    {
        _http = http;
        _logger = logger;
    }

    [HttpGet("symbols")]
    public async Task<IActionResult> Symbols(CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient("binance");
            var tickers = await client.GetFromJsonAsync<List<JsonElement>>("/fapi/v1/ticker/24hr", ct);
            if (tickers == null) return Ok(Array.Empty<object>());

            var set = new HashSet<string>(DefaultSymbols, StringComparer.OrdinalIgnoreCase);
            var list = tickers
                .Where(t =>
                {
                    var s = t.GetProperty("symbol").GetString() ?? "";
                    return set.Contains(s) || (s.EndsWith("USDT") && set.Count < 40);
                })
                .Select(t =>
                {
                    var sym = t.GetProperty("symbol").GetString() ?? "";
                    var last = decimal.Parse(t.GetProperty("lastPrice").GetString() ?? "0", CultureInfo.InvariantCulture);
                    var chg = decimal.Parse(t.GetProperty("priceChangePercent").GetString() ?? "0", CultureInfo.InvariantCulture);
                    return new { symbol = sym, price = last, changePct = chg };
                })
                .OrderByDescending(x => Math.Abs(x.changePct))
                .Take(40)
                .ToList();

            // ensure defaults present
            var have = list.Select(x => x.symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DefaultSymbols)
            {
                if (have.Contains(d)) continue;
                var t = tickers.FirstOrDefault(x =>
                    string.Equals(x.GetProperty("symbol").GetString(), d, StringComparison.OrdinalIgnoreCase));
                if (t.ValueKind == JsonValueKind.Undefined) continue;
                var last = decimal.Parse(t.GetProperty("lastPrice").GetString() ?? "0", CultureInfo.InvariantCulture);
                var chg = decimal.Parse(t.GetProperty("priceChangePercent").GetString() ?? "0", CultureInfo.InvariantCulture);
                list.Add(new { symbol = d, price = last, changePct = chg });
            }

            return Ok(list.OrderByDescending(x => Math.Abs(x.changePct)).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "symbols fetch failed");
            return Ok(DefaultSymbols.Select(s => new { symbol = s, price = 0m, changePct = 0m }));
        }
    }

    [HttpGet("klines")]
    public async Task<IActionResult> Klines(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string interval = "15m",
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient("binance");
            var url = $"/fapi/v1/klines?symbol={symbol.ToUpperInvariant()}&interval={interval}&limit={Math.Clamp(limit, 50, 500)}";
            var raw = await client.GetFromJsonAsync<List<JsonElement>>(url, ct);
            if (raw == null) return Ok(Array.Empty<object>());

            var candles = raw.Select(k =>
            {
                var openTime = k[0].GetInt64() / 1000; // unix sec for lightweight-charts
                return new
                {
                    time = openTime,
                    open = decimal.Parse(k[1].GetString()!, CultureInfo.InvariantCulture),
                    high = decimal.Parse(k[2].GetString()!, CultureInfo.InvariantCulture),
                    low = decimal.Parse(k[3].GetString()!, CultureInfo.InvariantCulture),
                    close = decimal.Parse(k[4].GetString()!, CultureInfo.InvariantCulture),
                    volume = decimal.Parse(k[5].GetString()!, CultureInfo.InvariantCulture)
                };
            }).ToList();

            return Ok(candles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "klines failed {symbol}", symbol);
            return Ok(Array.Empty<object>());
        }
    }

    [HttpGet("ticker")]
    public async Task<IActionResult> Ticker([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient("binance");
            var t = await client.GetFromJsonAsync<JsonElement>($"/fapi/v1/ticker/24hr?symbol={symbol.ToUpperInvariant()}", ct);
            var last = decimal.Parse(t.GetProperty("lastPrice").GetString() ?? "0", CultureInfo.InvariantCulture);
            var chg = decimal.Parse(t.GetProperty("priceChangePercent").GetString() ?? "0", CultureInfo.InvariantCulture);
            return Ok(new { symbol, price = last, changePct = chg });
        }
        catch
        {
            return Ok(new { symbol, price = 0m, changePct = 0m });
        }
    }
}
