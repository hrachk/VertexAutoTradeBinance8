using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.Learning;

namespace VertexAutoTradeBinance8.Services.News;

/// <summary>
/// News Filter Alpha Engine: severity matrix, dynamic windows, microstructure overlay.
/// ShadowMode (default true): never blocks live orders; logs + shadow blocks for ΔPF/ΔDD.
/// </summary>
public sealed class NewsCatalystService : INewsCatalystService
{
    private readonly ILogger<NewsCatalystService> _log;
    private readonly IConfiguration _cfg;
    private readonly ShadowKpiEvaluator? _shadowKpi;
    private readonly NewsMicrostructureGate? _micro;
    private readonly object _gate = new();
    private readonly List<NewsEvent> _recent = new();
    private readonly List<NewsDirective> _directives = new();
    private decimal _atrRatio = 1m; // ATR_1h / ATR_24h avg
    private string? _sharedRoot;

    private const decimal MinCredibility = 0.55m;
    private const decimal MinImpact = 0.40m;

    public NewsCatalystService(
        ILogger<NewsCatalystService> log,
        IConfiguration cfg,
        ShadowKpiEvaluator? shadowKpi = null,
        NewsMicrostructureGate? micro = null)
    {
        _log = log;
        _cfg = cfg;
        _shadowKpi = shadowKpi;
        _micro = micro;
        _sharedRoot = cfg["SharedData:Root"];
    }

    public bool ShadowMode => _cfg.GetValue("News:ShadowMode", true);

    public void SetAtrRatio(decimal atr1hOverAvg24h)
    {
        if (atr1hOverAvg24h <= 0) return;
        _atrRatio = Math.Clamp(atr1hOverAvg24h, 0.25m, 4m);
    }

    public void Ingest(NewsEvent ev)
    {
        if (ev == null) return;
        ClassifyIfNeeded(ev);
        AppendNewsTape(ev); // SharedData/news_tape.jsonl — UI лента на /ai-dashboard

        lock (_gate)
        {
            _recent.Add(ev);
            if (_recent.Count > 300) _recent.RemoveRange(0, _recent.Count - 300);
            if (ev.Credibility < MinCredibility || ev.Impact < MinImpact) return;

            var grade = ev.Grade;
            if (ev.Category == NewsEventCategory.MacroHigh)
                grade = NewsImpactGrade.High;
            if (ev.Vector == NewsVector.Neutral && grade < NewsImpactGrade.High
                && ev.Category != NewsEventCategory.MacroHigh)
                return;

            // Dynamic window: T_start = T_news - k1 * ATR_ratio (minutes), T_end = duration * f(ATR)
            decimal k1 = _cfg.GetValue("News:DynamicWindowK1", 12m);
            int leadMin = (int)Math.Clamp((double)(k1 * _atrRatio), 5, 90);
            int duration = ev.EventDurationMinutes > 0 ? ev.EventDurationMinutes : DefaultDuration(ev.Category, grade);
            duration = (int)Math.Clamp(duration * (double)Math.Max(1m, _atrRatio * 0.85m), 10, 240);

            var tNews = ev.Utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(ev.Utc, DateTimeKind.Utc) : ev.Utc.ToUniversalTime();
            var windowStart = tNews.AddMinutes(-leadMin);
            var windowEnd = tNews.AddMinutes(duration);

            // Severity matrix → size mult
            decimal sizeMult = 1m;
            string reasonCode = ev.ReasonCode ?? NewsReasonCodes.DynamicWindow;
            switch (ev.Category)
            {
                case NewsEventCategory.MacroHigh:
                    sizeMult = 0m;
                    reasonCode = NewsReasonCodes.MacroHighImpact;
                    grade = NewsImpactGrade.High;
                    break;
                case NewsEventCategory.TokenSpecific:
                    sizeMult = grade >= NewsImpactGrade.High ? 0.25m : 0.50m;
                    reasonCode = NewsReasonCodes.TokenEvent;
                    break;
                case NewsEventCategory.Infrastructure:
                    sizeMult = 0.50m;
                    reasonCode = NewsReasonCodes.LowDepth;
                    break;
            }

            if (grade == NewsImpactGrade.High && ev.Category != NewsEventCategory.TokenSpecific)
                sizeMult = 0m;

            var dir = new NewsDirective
            {
                Utc = DateTime.UtcNow,
                WindowStartUtc = windowStart,
                WindowEndUtc = windowEnd,
                Vector = ev.Vector,
                Strength = Math.Clamp(ev.Impact * ev.Credibility, 0m, 1m),
                Grade = grade,
                Category = ev.Category,
                Symbols = ev.RelatedSymbols?.ToList() ?? new List<string>(),
                Reason = $"[{ev.Category}] {ev.Headline}",
                ReasonCode = reasonCode,
                PauseNewEntriesSeconds = (int)Math.Max(60, (windowEnd - DateTime.UtcNow).TotalSeconds),
                SizeMult = sizeMult
            };

            // Keep strongest overlapping directive
            _directives.RemoveAll(d => d.WindowEndUtc < DateTime.UtcNow.AddMinutes(-5));
            _directives.Add(dir);
            if (_directives.Count > 40) _directives.RemoveRange(0, _directives.Count - 30);

            // Shadow KPI block window (so Filtered curve can diverge)
            try
            {
                _shadowKpi?.RegisterShadowBlock(windowStart, windowEnd, dir.ReasonCode + "|" + dir.Reason);
            }
            catch { }

            int ttlSec = Math.Max(0, (int)(windowEnd - DateTime.UtcNow).TotalSeconds);
            if (ShadowMode)
            {
                _log.LogInformation(
                    "[NEWS-SHADOW] cat={cat} grade={g} size×{sm:F2} code={code} win={ws:HH:mm}-{we:HH:mm}Z atrR={atr:F2} | {h}",
                    ev.Category, grade, sizeMult, reasonCode, windowStart, windowEnd, _atrRatio, Trunc(ev.Headline, 120));
            }
            else
            {
                _log.LogWarning(
                    "[NEWS] ACTIVE cat={cat} size×{sm:F2} code={code} ttl={sec}s | {h}",
                    ev.Category, sizeMult, reasonCode, ttlSec, Trunc(ev.Headline, 120));
            }

            AppendDecisionLog(new
            {
                utc = DateTime.UtcNow,
                shadow = ShadowMode,
                category = ev.Category.ToString(),
                grade = grade.ToString(),
                sizeMult,
                reasonCode,
                windowStart,
                windowEnd,
                symbols = dir.Symbols,
                headline = Trunc(ev.Headline, 160)
            });
        }
    }

    public NewsDirective? TryGetActiveDirective()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return _directives
                .Where(d => now >= d.WindowStartUtc && now <= d.WindowEndUtc)
                .OrderBy(d => d.SizeMult)
                .ThenByDescending(d => d.Strength)
                .FirstOrDefault();
        }
    }

    public NewsEntryDecision EvaluateEntry(string symbol, string? side = null)
    {
        var decision = new NewsEntryDecision { SizeMult = 1m };

        // Microstructure always can tighten during/near news
        if (_micro != null && _micro.TryGetRisk(symbol, out var mCode, out var mDetail))
        {
            decision.SizeMult = Math.Min(decision.SizeMult, 0.35m);
            decision.WouldBlock = decision.SizeMult <= 0.01m;
            decision.ReasonCode = mCode;
            decision.Reason = mDetail;
        }

        var d = TryGetActiveDirective();
        if (d == null)
        {
            if (!string.IsNullOrEmpty(decision.ReasonCode))
                LogShadowEval(symbol, side, decision);
            return decision;
        }

        if (!AppliesTo(d, symbol))
        {
            if (!string.IsNullOrEmpty(decision.ReasonCode))
                LogShadowEval(symbol, side, decision);
            return decision;
        }

        decision.Category = d.Category;
        decimal sm = d.SizeMult;
        string code = d.ReasonCode;
        string reason = d.Reason;

        // Opposing vector → extra scale-down
        if (!string.IsNullOrEmpty(side) && d.Grade >= NewsImpactGrade.Medium)
        {
            bool isLong = side.Contains("BUY", StringComparison.OrdinalIgnoreCase)
                       || side.Contains("LONG", StringComparison.OrdinalIgnoreCase);
            if ((isLong && d.Vector == NewsVector.Bearish) || (!isLong && d.Vector == NewsVector.Bullish))
            {
                sm = Math.Min(sm <= 0 ? 0m : sm, 0.35m);
                code = NewsReasonCodes.OpposingVector;
                reason = d.Reason + " | opposes side";
            }
        }

        // Category C / active news + micro
        if (_micro != null && _micro.TryGetRisk(symbol, out var mc, out var md))
        {
            sm = Math.Min(sm <= 0 ? 0m : sm, 0.25m);
            code = mc;
            reason = md;
        }

        decision.SizeMult = sm;
        decision.WouldBlock = sm <= 0.01m;
        decision.ReasonCode = code;
        decision.Reason = reason;
        LogShadowEval(symbol, side, decision);
        return decision;
    }

    public bool IsEntryPaused(string symbol)
    {
        if (ShadowMode) return false;
        var d = EvaluateEntry(symbol);
        return d.WouldBlock;
    }

    public decimal GetEntrySizeMult(string symbol, string? side = null)
    {
        var d = EvaluateEntry(symbol, side);
        if (ShadowMode)
        {
            // live size unchanged; intended scale only in logs/shadow
            return 1m;
        }
        if (d.SizeMult <= 0.01m) return 0m;
        return Math.Clamp(d.SizeMult, 0.25m, 1m);
    }

    private void LogShadowEval(string symbol, string? side, NewsEntryDecision d)
    {
        if (d.SizeMult >= 0.999m && string.IsNullOrEmpty(d.ReasonCode)) return;
        if (ShadowMode)
        {
            _log.LogInformation(
                "[NEWS-SHADOW] eval {sym} side={side} size×{sm:F2} block={b} code={code} | {r}",
                symbol, side ?? "-", d.SizeMult, d.WouldBlock, d.ReasonCode, d.Reason);
        }
        AppendDecisionLog(new
        {
            utc = DateTime.UtcNow,
            type = "entry_eval",
            symbol,
            side,
            d.SizeMult,
            d.WouldBlock,
            d.ReasonCode,
            d.Reason,
            shadow = ShadowMode
        });
    }

    private static bool AppliesTo(NewsDirective d, string symbol)
    {
        if (d.Category == NewsEventCategory.MacroHigh) return true;
        if (d.Symbols == null || d.Symbols.Count == 0)
            return d.Category != NewsEventCategory.TokenSpecific; // token events need symbols
        return d.Symbols.Any(s =>
            string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase)
            || symbol.StartsWith(s.Replace("USDT", ""), StringComparison.OrdinalIgnoreCase));
    }

    private static int DefaultDuration(NewsEventCategory cat, NewsImpactGrade grade) => cat switch
    {
        NewsEventCategory.MacroHigh => grade >= NewsImpactGrade.High ? 45 : 25,
        NewsEventCategory.Infrastructure => 40,
        _ => 25
    };

    private static void ClassifyIfNeeded(NewsEvent ev)
    {
        var h = (ev.Headline ?? "") + " " + (ev.Body ?? "");
        var u = h.ToUpperInvariant();
        // Fear&Greed / regime → market-wide, never "TOKEN listing"
        if (ContainsAny(u, "FEAR&GREED", "FEAR AND GREED", "FEAR GREED", "FNG"))
        {
            ev.Category = NewsEventCategory.MacroHigh;
            if (ev.Impact < 0.35m) ev.Impact = 0.35m;
            ev.ReasonCode ??= "REASON_MARKET_REGIME_FNG";
            return;
        }
        if (ContainsAny(u, "FOMC", "CPI", "NFP", "NON-FARM", "INTEREST RATE", "FED ", "ECB", "MACRO:"))
        {
            ev.Category = NewsEventCategory.MacroHigh;
            if (ev.Grade < NewsImpactGrade.High) ev.Grade = NewsImpactGrade.High;
            if (ev.EventDurationMinutes < 30) ev.EventDurationMinutes = 45;
            ev.ReasonCode ??= NewsReasonCodes.MacroHighImpact;
        }
        else if (ContainsAny(u, "MAINTENANCE", "API ", "OUTAGE", "DOWNGRADE", "WEBSOCKET", "LATENCY"))
        {
            ev.Category = NewsEventCategory.Infrastructure;
            ev.ReasonCode ??= NewsReasonCodes.LowDepth;
        }
        else if (ContainsAny(u, "UNLOCK", "LISTING", "HALVING", "HARDFORK", "AIRDROP", "MAINNET"))
        {
            ev.Category = NewsEventCategory.TokenSpecific;
            ev.ReasonCode ??= NewsReasonCodes.TokenEvent;
        }
    }

    private static bool ContainsAny(string hay, params string[] needles)
        => needles.Any(n => hay.Contains(n, StringComparison.Ordinal));

    private static string Trunc(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s[..n] + "…";
    }


    /// <summary>Compact tape for Web UI (last events with headline context).</summary>
    private void AppendNewsTape(NewsEvent ev)
    {
        try
        {
            if (string.IsNullOrEmpty(_sharedRoot)) return;
            Directory.CreateDirectory(_sharedRoot);
            var path = Path.Combine(_sharedRoot, "news_tape.jsonl");
            // Sentiment proxy from vector × impact for UI (-1..+1)
            decimal sentiment = ev.Vector switch
            {
                NewsVector.Bullish => Math.Min(1m, ev.Impact),
                NewsVector.Bearish => -Math.Min(1m, ev.Impact),
                _ => 0m
            };
            int impactStars = ev.Impact >= 0.85m ? 5
                : ev.Impact >= 0.7m ? 4
                : ev.Impact >= 0.5m ? 3
                : ev.Impact >= 0.35m ? 2 : 1;

            var row = new
            {
                utc = ev.Utc == default ? DateTime.UtcNow : ev.Utc,
                ingestedUtc = DateTime.UtcNow,
                source = Trunc(ev.Source, 48),
                headline = Trunc(ev.Headline, 180),
                vector = ev.Vector.ToString(),
                grade = ev.Grade.ToString(),
                category = ev.Category.ToString(),
                impact = Math.Round(ev.Impact, 2),
                impactStars,
                sentiment = Math.Round(sentiment, 2),
                symbols = (ev.RelatedSymbols ?? new List<string>()).Take(6).ToArray(),
                reasonCode = ev.ReasonCode ?? "",
                url = Trunc(ev.OfficialSpeaker, 120),
                shadow = ShadowMode
            };
            File.AppendAllText(path, JsonSerializer.Serialize(row) + "\n");
            // Keep file bounded (~400 lines)
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length > 400)
                    File.WriteAllLines(path, lines.AsSpan(lines.Length - 300).ToArray());
            }
            catch { }
        }
        catch { }
    }

    private void AppendDecisionLog(object row)
    {
        try
        {
            if (string.IsNullOrEmpty(_sharedRoot)) return;
            Directory.CreateDirectory(_sharedRoot);
            var path = Path.Combine(_sharedRoot, "news_shadow_decisions.jsonl");
            var line = JsonSerializer.Serialize(row) + "\n";
            File.AppendAllText(path, line);
        }
        catch { }
    }
}
