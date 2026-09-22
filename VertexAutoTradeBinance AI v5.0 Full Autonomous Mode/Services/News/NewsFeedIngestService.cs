using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.News;

/// <summary>
/// Low-latency news ingest (CryptoCompare + Fear&amp;Greed).
/// Default poll 15s (was 8 min). Fast-path keyword filter → NewsCatalyst.Ingest.
/// </summary>
public sealed class NewsFeedIngestService : BackgroundService
{
    private readonly ILogger<NewsFeedIngestService> _log;
    private readonly INewsCatalystService _news;
    private readonly IConfiguration _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFearGreedPull = DateTime.MinValue;

    private static readonly string[] BullishKw =
    {
        "etf approved", "etf approval", "rate cut", "interest rate cut", "stimulus",
        "partnership", "listing on", "all-time high", "adoption", "inflow", "buys bitcoin",
        "spot etf", "approval", "greenlight", "bullish", "surge", "rally", "breakout"
    };
    private static readonly string[] BearishKw =
    {
        "hack", "exploit", "sec charges", "lawsuit", "ban", "crackdown", "outflow",
        "bankruptcy", "collapse", "fraud", "delist", "investigation", "rate hike",
        "liquidation", "crash", "plunge", "bearish", "sell-off", "sec sues"
    };
    private static readonly (string token, string symbol)[] SymMap =
    {
        ("bitcoin", "BTCUSDT"), ("btc", "BTCUSDT"),
        ("ethereum", "ETHUSDT"), ("eth", "ETHUSDT"),
        ("solana", "SOLUSDT"), ("sol ", "SOLUSDT"),
        ("xrp", "XRPUSDT"), ("ripple", "XRPUSDT"),
        ("bnb", "BNBUSDT"), ("dogecoin", "DOGEUSDT"), ("doge", "DOGEUSDT"),
        ("cardano", "ADAUSDT"), ("ada", "ADAUSDT"),
        ("near", "NEARUSDT"), ("avax", "AVAXUSDT"), ("link", "LINKUSDT"),
    };

    public NewsFeedIngestService(
        ILogger<NewsFeedIngestService> log,
        INewsCatalystService news,
        IConfiguration cfg)
    {
        _log = log;
        _news = news;
        _cfg = cfg;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VertexAutoTrade/1.0");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int pollSec = Math.Clamp(_cfg.GetValue("News:PollSeconds", 15), 5, 120);
        _log.LogInformation("[NEWS-FEED] started — poll every {s}s (target E2E <15s)", pollSec);

        try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PullCryptoCompareAsync(stoppingToken);
                await PullFearGreedAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[NEWS-FEED] tick failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(pollSec), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task PullCryptoCompareAsync(CancellationToken ct)
    {
        // categories=ALL, latest first
        var url = "https://min-api.cryptocompare.com/data/v2/news/?lang=EN&extraParams=VertexAutoTrade";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogDebug("[NEWS-FEED] CryptoCompare HTTP {code}", (int)resp.StatusCode);
            return;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("Data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return;

        int ingested = 0;
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            var title = item.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(title)) continue;
            var key = !string.IsNullOrEmpty(id) ? id! : title!;
            if (!_seen.Add(key)) continue;
            if (_seen.Count > 2000)
            {
                _seen.Clear();
                _seen.Add(key);
            }

            string? body = item.TryGetProperty("body", out var bEl) ? bEl.GetString() : null;
            string? source = item.TryGetProperty("source", out var sEl) ? sEl.GetString() : "CryptoCompare";
            string? urlArt = item.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;

            // published_on is Unix seconds — real news time (not ingest lag)
            DateTime published = DateTime.UtcNow;
            if (item.TryGetProperty("published_on", out var pEl) && pEl.TryGetInt64(out var unix))
            {
                published = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

            var text = (title + " " + (body ?? "")).ToLowerInvariant();
            var vector = Classify(text);
            // Still ingest neutral high-impact macro keywords for tape transparency
            bool macroHint = text.Contains("fomc") || text.Contains("cpi") || text.Contains("nfp")
                             || text.Contains("federal reserve") || text.Contains("interest rate");
            if (vector == NewsVector.Neutral && !macroHint) continue;

            var symbols = ResolveSymbols(text);
            decimal impact = symbols.Count > 0 ? 0.55m : 0.42m;
            if (macroHint) impact = Math.Max(impact, 0.80m);
            if (text.Contains("sec ") || text.Contains("etf")) impact = Math.Min(0.85m, impact + 0.15m);
            if (text.Contains("hack") || text.Contains("exploit")) impact = Math.Min(0.92m, impact + 0.25m);

            decimal sentiment = vector switch
            {
                NewsVector.Bullish => Math.Min(1m, 0.35m + impact * 0.5m),
                NewsVector.Bearish => -Math.Min(1m, 0.35m + impact * 0.5m),
                _ => 0m
            };

            var grade = impact >= 0.75m ? NewsImpactGrade.High
                : impact >= 0.5m ? NewsImpactGrade.Medium
                : NewsImpactGrade.Low;

            var cat = macroHint ? NewsEventCategory.MacroHigh
                : (text.Contains("maintenance") || text.Contains("outage") ? NewsEventCategory.Infrastructure
                : NewsEventCategory.TokenSpecific);

            _news.Ingest(new NewsEvent
            {
                Source = source ?? "CryptoCompare",
                Headline = title!,
                Body = body != null && body.Length > 280 ? body[..280] : body,
                Credibility = 0.70m,
                Impact = impact,
                Vector = macroHint && vector == NewsVector.Neutral ? NewsVector.Neutral : vector,
                Grade = grade,
                Category = cat,
                RelatedSymbols = symbols,
                Utc = published,
                ReasonCode = macroHint ? NewsReasonCodes.MacroHighImpact : null,
                OfficialSpeaker = urlArt // reuse field for source URL (audit link)
            });
            ingested++;
            if (ingested >= 12) break; // cap per tick
        }

        if (ingested > 0)
            _log.LogInformation("[NEWS-FEED] ingested {n} headlines (poll)", ingested);
    }

    private async Task PullFearGreedAsync(CancellationToken ct)
    {
        // At most once per 5 minutes — index itself updates slowly
        if ((DateTime.UtcNow - _lastFearGreedPull).TotalMinutes < 5) return;
        _lastFearGreedPull = DateTime.UtcNow;

        try
        {
            var url = "https://api.alternative.me/fng/?limit=1";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return;
            var row = data[0];
            var valueStr = row.TryGetProperty("value", out var v) ? v.GetString() : null;
            var classStr = row.TryGetProperty("value_classification", out var c) ? c.GetString() : "";
            if (!int.TryParse(valueStr, out var fg)) return;

            DateTime ts = DateTime.UtcNow;
            if (row.TryGetProperty("timestamp", out var tEl) && long.TryParse(tEl.GetString(), out var unix))
                ts = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

            // Regime indicator — NOT a token listing event
            var vector = fg >= 75 ? NewsVector.Bullish : fg <= 25 ? NewsVector.Bearish : NewsVector.Neutral;
            decimal impact = fg >= 80 || fg <= 20 ? 0.55m : 0.35m;
            decimal sentiment = (fg - 50) / 50m; // -1..+1

            var key = $"FNG:{fg}:{ts:yyyyMMddHH}";
            if (!_seen.Add(key)) return;

            _news.Ingest(new NewsEvent
            {
                Source = "alternative.me/fng",
                Headline = $"Crypto Fear&Greed {fg} ({classStr})",
                Body = "Market regime indicator (not a single-token news item).",
                Credibility = 0.75m,
                Impact = impact,
                Vector = vector,
                Grade = impact >= 0.5m ? NewsImpactGrade.Medium : NewsImpactGrade.Low,
                Category = NewsEventCategory.MacroHigh, // regime / market-wide
                RelatedSymbols = new List<string> { "BTCUSDT", "ETHUSDT" },
                Utc = ts,
                ReasonCode = "REASON_MARKET_REGIME_FNG",
                OfficialSpeaker = "https://alternative.me/crypto/fear-and-greed-index/"
            });
            _log.LogInformation("[NEWS-FEED] Fear&Greed {v} ({c})", fg, classStr);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[NEWS-FEED] Fear&Greed failed");
        }
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
        foreach (var (token, symbol) in SymMap)
        {
            if (text.Contains(token) && !list.Contains(symbol))
                list.Add(symbol);
        }
        return list;
    }
}
