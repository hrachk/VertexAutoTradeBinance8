using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Universe selection: широкий candidate pool → компактный Active set.
/// Один запрос GetTickers (без стаканов/свечей) — без тормозов цикла.
/// Ротация: недавно закрытые символы получают штраф, open managed — всегда в active.
/// </summary>
public class SymbolRegistryService
{
    private readonly IConfiguration _cfg;
    private readonly BinanceClientFactory _factory;
    private readonly ILogger<SymbolRegistryService> _logger;
    private readonly ExecutedSignalService? _executed;
    private readonly ManagedPositionRegistry? _managed;

    private readonly object _lock = new();
    private List<string> _active = new();

    /// <summary>Текущий рабочий набор (компактный, для TradingWorker).</summary>
    public IReadOnlyList<string> ActiveSymbols
    {
        get { lock (_lock) return _active.ToList(); }
        private set { lock (_lock) _active = value.ToList(); }
    }

    private readonly TimeSpan _refreshInterval;

    // лёгкий учёт «сколько раз подряд в active» — мягкая ротация без диска
    private readonly ConcurrentDictionary<string, int> _streakInActive =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DefaultBan = new(StringComparer.OrdinalIgnoreCase)
    {
        "AIAUSDT"
    };

    public SymbolRegistryService(
        IConfiguration cfg,
        BinanceClientFactory factory,
        ILogger<SymbolRegistryService> logger,
        ExecutedSignalService? executed = null,
        ManagedPositionRegistry? managed = null)
    {
        _cfg = cfg;
        _factory = factory;
        _logger = logger;
        _executed = executed;
        _managed = managed;

        var min = _cfg.GetValue("SymbolSelection:Auto:RefreshInterval", 5);
        if (min < 3) min = 3;
        _refreshInterval = TimeSpan.FromMinutes(min);
    }

    public async Task LoadAsync(CancellationToken stoppingToken)
    {
        string mode = _cfg["SymbolSelection:Mode"] ?? "Manual";

        if (string.Equals(mode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            var manual = _cfg.GetSection("SymbolSelection:Manual").Get<string[]>() ?? Array.Empty<string>();
            ActiveSymbols = manual.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _logger.LogInformation("[SYMBOLS] Manual mode → {n}: {list}",
                ActiveSymbols.Count, string.Join(", ", ActiveSymbols));
            return;
        }

        // Первый load — синхронно, чтобы worker не стартовал с пустым списком
        await LoadAutoSafe();

        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_refreshInterval, stoppingToken);
                    await LoadAutoSafe();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SYMBOLS] background refresh error");
                }
            }
        }, stoppingToken);
    }

    private async Task LoadAutoSafe()
    {
        try { await LoadAuto(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SYMBOLS] LoadAuto failed");
        }
    }

    private async Task LoadAuto()
    {
        var minVolume = _cfg.GetValue("SymbolSelection:Auto:Min24hVolume", 2_000_000m);
        var minPrice = _cfg.GetValue("SymbolSelection:Auto:MinPrice", 0.005m);

        // широкий пул кандидатов (только из одного ticker snapshot)
        var poolSize = _cfg.GetValue("SymbolSelection:Auto:CandidatePoolSize", 60);
        // компактный active — качество, не перегруз CPU
        var activeCount = _cfg.GetValue("SymbolSelection:Auto:ActiveCount", 18);
        var rotationHours = _cfg.GetValue("SymbolSelection:Auto:RotationHours", 6);
        var maxStreak = _cfg.GetValue("SymbolSelection:Auto:MaxStreakInActive", 4);

        // backward compat: TopVolumeCount → ActiveCount если ActiveCount не задан явно
        var legacyTop = _cfg.GetValue("SymbolSelection:Auto:TopVolumeCount", 0);
        if (legacyTop > 0 && !_cfg.GetSection("SymbolSelection:Auto").GetChildren()
                .Any(c => c.Key.Equals("ActiveCount", StringComparison.OrdinalIgnoreCase)))
            activeCount = Math.Clamp(legacyTop, 10, 30);

        poolSize = Math.Clamp(poolSize, 30, 100);
        activeCount = Math.Clamp(activeCount, 10, 25);

        var ban = new HashSet<string>(DefaultBan, StringComparer.OrdinalIgnoreCase);
        var cfgBan = _cfg.GetSection("SymbolSelection:Auto:BanList").Get<string[]>();
        if (cfgBan != null)
            foreach (var b in cfgBan)
                if (!string.IsNullOrWhiteSpace(b)) ban.Add(b.Trim());

        using var client = _factory.CreateRestClient();
        var tickers = await client.UsdFuturesApi.ExchangeData.GetTickersAsync();

        if (!tickers.Success || tickers.Data == null)
        {
            _logger.LogError("[SYMBOLS] Failed to load futures tickers: {err}", tickers.Error);
            return;
        }

        // --- 1) Candidate pool: top by volume (USDT-M perpetual names end with USDT) ---
        var candidates = tickers.Data
            .Where(t => t.Symbol != null &&
                        t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
            .Where(t => !ban.Contains(t.Symbol))
            .Where(t => t.LastPrice >= minPrice)
            .Where(t => t.QuoteVolume >= minVolume)
            .OrderByDescending(t => t.QuoteVolume)
            .Take(poolSize)
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogWarning("[SYMBOLS] candidate pool empty — keep previous ActiveSymbols");
            return;
        }

        // --- 2) Must-keep: already open bot positions ---
        var mustKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_managed != null)
        {
            foreach (var info in _managed.Snapshot())
                mustKeep.Add(info.Symbol);
        }

        var now = DateTime.UtcNow;
        var rotationWindow = TimeSpan.FromHours(Math.Max(1, rotationHours));

        // --- 3) Score (только поля ticker — без klines/orderbook) ---
        // quality = volume strength + mild activity − rotation penalty − streak penalty
        var maxVol = candidates.Max(c => c.QuoteVolume);
        if (maxVol <= 0) maxVol = 1;

        var scored = new List<(string Symbol, double Score, decimal Vol, decimal Chg)>();

        foreach (var t in candidates)
        {
            var sym = t.Symbol;
            double volScore = Math.Log10((double)(t.QuoteVolume + 1m)) /
                              Math.Log10((double)(maxVol + 1m)); // 0..1

            // активность дня (не «мёртвый» топ)
            double chg = (double)Math.Abs(t.PriceChangePercent);
            double actScore = Math.Min(chg / 8.0, 1.0); // 8%+ → full

            double score = volScore * 0.70 + actScore * 0.30;

            // ротация: недавно закрывали → штраф (не бан навсегда)
            if (_executed != null)
            {
                var lastClose = _executed.GetLastCloseUtc(sym);
                if (lastClose.HasValue)
                {
                    var age = now - lastClose.Value;
                    if (age < rotationWindow)
                    {
                        // чем свежее close — тем сильнее штраф (до -0.55)
                        var frac = 1.0 - age.TotalHours / rotationWindow.TotalHours;
                        score -= 0.55 * Math.Clamp(frac, 0, 1);
                    }
                }
            }

            // soft rotation: слишком долго в active подряд
            if (_streakInActive.TryGetValue(sym, out var streak) && streak >= maxStreak)
                score -= 0.20 + 0.05 * Math.Min(streak - maxStreak, 6);

            // open managed — гарантированно высоко
            if (mustKeep.Contains(sym))
                score += 2.0;

            scored.Add((sym, score, t.QuoteVolume, t.PriceChangePercent));
        }

        // --- 4) Build active set ---
        var ordered = scored.OrderByDescending(x => x.Score).ToList();

        var next = new List<string>();
        // сначала must-keep (если в pool)
        foreach (var mk in mustKeep)
        {
            if (ordered.Any(x => x.Symbol.Equals(mk, StringComparison.OrdinalIgnoreCase)) &&
                !next.Contains(mk, StringComparer.OrdinalIgnoreCase))
                next.Add(mk);
        }

        foreach (var row in ordered)
        {
            if (next.Count >= activeCount)
                break;
            if (next.Contains(row.Symbol, StringComparer.OrdinalIgnoreCase))
                continue;
            next.Add(row.Symbol);
        }

        // --- 5) Update streaks ---
        var nextSet = new HashSet<string>(next, StringComparer.OrdinalIgnoreCase);
        foreach (var s in nextSet)
            _streakInActive.AddOrUpdate(s, 1, (_, old) => old + 1);

        foreach (var key in _streakInActive.Keys.ToList())
        {
            if (!nextSet.Contains(key))
                _streakInActive.TryRemove(key, out _);
        }

        ActiveSymbols = next;

        var preview = ordered.Take(Math.Min(12, ordered.Count))
            .Select(x => $"{x.Symbol}({x.Score:F2})")
            .ToList();

        _logger.LogInformation(
            "[SYMBOLS] Auto pool={pool} → active={act} | top scores: {scores}",
            candidates.Count, next.Count, string.Join(", ", preview));
        _logger.LogInformation("[SYMBOLS] Active: {list}", string.Join(", ", next));
    }
}
