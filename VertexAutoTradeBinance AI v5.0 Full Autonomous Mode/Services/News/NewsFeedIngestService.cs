using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.News;

/// <summary>
/// Pulls public crypto news (CryptoCompare) + optional calendar-ish keywords
/// and feeds NewsCatalystService.Ingest so TradingWorker can pause entries.
/// </summary>
public sealed class NewsFeedIngestService : BackgroundService
{
    private readonly ILogger<NewsFeedIngestService> _log;
    private readonly INewsCatalystService _news;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] BullishKw =
    {
        "etf approved", "etf approval", "rate cut", "interest rate cut", "stimulus",
        "partnership", "listing on", "all-time high", "adoption", "inflow", "buys bitcoin"
    };
    private static readonly string[] BearishKw =
    {
        "hack", "exploit", "sec charges", "lawsuit", "ban", "crackdown", "outflow",
        "bankruptcy", "collapse", "fraud", "delist", "investigation", "rate hike"
    };
    private static readonly (string token, string symbol)[] SymMap =
    {
        ("bitcoin", "BTCUSDT"), ("btc", "BTCUSDT"),
        ("ethereum", "ETHUSDT"), ("eth", "ETHUSDT"),
        ("solana", "SOLUSDT"), ("sol ", "SOLUSDT"),
        ("xrp", "XRPUSDT"), ("ripple", "XRPUSDT"),
        ("bnb", "BNBUSDT"), ("dogecoin", "DOGEUSDT"), ("doge", "DOGEUSDT"),
        ("cardano", "ADAUSDT"), ("ada", "ADAUSDT"),
    };

    public NewsFeedIngestService(ILogger<NewsFeedIngestService> log, INewsCatalystService news)
    {
        _log = log;
        _news = news;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VertexAutoTrade/1.0");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[NEWS-FEED] started — CryptoCompare ingest every 8 min");
        // first pull shortly after start
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PullCryptoCompareAsync(stoppingToken); }
            catch (Exception ex) { _log.LogDebug(ex, "[NEWS-FEED] tick failed"); }

            try { await Task.Delay(TimeSpan.FromMinutes(8), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task PullCryptoCompareAsync(CancellationToken ct)
    {
        // Public endpoint — no key required for basic news list
        var url = "https://min-api.cryptocompare.com/data/v2/news/?lang=EN&categories=BTC,ETH,Trading,Regulation,Market";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogDebug("[NEWS-FEED] HTTP {code}", (int)resp.StatusCode);
            return;
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array)
            return;

        int ingested = 0;
        foreach (var item in data.EnumerateArray().Take(25))
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            var title = item.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            var body = item.TryGetProperty("body", out var bEl) ? bEl.GetString() : null;
            var source = item.TryGetProperty("source", out var sEl) ? sEl.GetString() : "CryptoCompare";
            if (string.IsNullOrWhiteSpace(title)) continue;
            var key = id ?? title;
            if (!_seen.Add(key!)) continue;
            if (_seen.Count > 800)
            {
                _seen.Clear();
                _seen.Add(key!);
            }

            var text = (title + " " + (body ?? "")).ToLowerInvariant();
            var vector = Classify(text);
            if (vector == NewsVector.Neutral) continue;

            var symbols = ResolveSymbols(text);
            decimal impact = symbols.Count > 0 ? 0.55m : 0.40m;
            if (text.Contains("sec ") || text.Contains("federal reserve") || text.Contains("etf"))
                impact = Math.Min(0.85m, impact + 0.20m);
            if (text.Contains("hack") || text.Contains("exploit"))
                impact = Math.Min(0.90m, impact + 0.25m);

            _news.Ingest(new NewsEvent
            {
                Source = source ?? "CryptoCompare",
                Headline = title!,
                Body = body != null && body.Length > 280 ? body[..280] : body,
                Credibility = 0.65m,
                Impact = impact,
                Vector = vector,
                RelatedSymbols = symbols,
                Utc = DateTime.UtcNow
            });
            ingested++;
        }
        if (ingested > 0)
            _log.LogInformation("[NEWS-FEED] ingested {n} directional headlines", ingested);
    }

    private static NewsVector Classify(string text)
    {
        int bull = BullishKw.Count(k => text.Contains(k));
        int bear = BearishKw.Count(k => text.Contains(k));
        if (bull == 0 && bear == 0) return NewsVector.Neutral;
        if (bear > bull) return NewsVector.Bearish;
        if (bull > bear) return NewsVector.Bullish;
        return NewsVector.Neutral;
    }

    private static List<string> ResolveSymbols(string text)
    {
        var list = new List<string>();
        foreach (var (token, sym) in SymMap)
        {
            if (text.Contains(token) && !list.Contains(sym))
                list.Add(sym);
        }
        return list;
    }
}
