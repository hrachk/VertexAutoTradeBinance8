using System.Text.Json;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class AiLearningFileService
{
    private readonly string _filePath;
    private readonly string _backupPath;
    private readonly string _legacyPath = "";
    private readonly ILogger<AiLearningFileService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public AiLearningFileService(
        IWebHostEnvironment env,
        IConfiguration cfg,
        ILogger<AiLearningFileService> logger)
    {
        // Prefer SharedData:Root (same folder Engine writes), then app base
        var root = cfg["SharedData:Root"];
        string baseDir;
        if (!string.IsNullOrWhiteSpace(root))
            baseDir = Path.Combine(root.Trim(), "ai-models");
        else
            baseDir = Path.Combine(AppContext.BaseDirectory, "ai-models");

        Directory.CreateDirectory(baseDir);
        _filePath = Path.Combine(baseDir, "ai_learning.json");
        _backupPath = Path.Combine(baseDir, "ai_learning_backup.json");

        // Secondary fallback under Web bin (legacy)
        _legacyPath = Path.Combine(AppContext.BaseDirectory, "ai-models", "ai_learning.json");

        _logger = logger;
        _logger.LogInformation("[AI-LEARN-WEB] Snapshot path: {Path}", _filePath);
    }

    // ============================================================
    // CORE SAFE READER (atomic-safe + fallback)
    // ============================================================
    private async Task<T?> ReadSafeAsync<T>()
    {
        var result = await TryReadFileAsync<T>(_filePath);
        if (result != null)
            return result;

        _logger.LogWarning("[AI-LEARN-WEB] Primary snapshot failed → backup");
        result = await TryReadFileAsync<T>(_backupPath);
        if (result != null)
            return result;

        if (!string.IsNullOrEmpty(_legacyPath) && !string.Equals(_legacyPath, _filePath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[AI-LEARN-WEB] Trying legacy path {Path}", _legacyPath);
            result = await TryReadFileAsync<T>(_legacyPath);
            if (result != null)
                return result;
        }

        _logger.LogError("[AI-LEARN-WEB] No readable ai_learning snapshot");
        return default;
    }

    private async Task<T?> TryReadFileAsync<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        try
        {
            await using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (fs.Length == 0)
                return default;

            return await JsonSerializer.DeserializeAsync<T>(fs, JsonOptions);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                ex,
                "[AI-LEARN-WEB] File locked or partial write: {Path}",
                path);

            return default;
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "[AI-LEARN-WEB] JSON corrupted: {Path}",
                path);

            return default;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[AI-LEARN-WEB] Unknown read error: {Path}",
                path);

            return default;
        }
    }

    // ============================================================
    // PUBLIC API
    // ============================================================

    public Task<AiLearningSnapshot?> LoadSnapshot()
        => ReadSafeAsync<AiLearningSnapshot>();


    // ============================================================
    // ATR-ADAPTIVE NORMALIZED POINTS EXPORT
    // ============================================================
    public async Task<IReadOnlyList<AiLearningPointModel>> LoadAsync(
        DateTime? fromUtc = null,
        int minScore = 0)
    {
        var snap = await ReadSafeAsync<AiLearningSnapshot>();

        if (snap == null || snap.MarketStates == null)
            return Array.Empty<AiLearningPointModel>();

        var now = DateTime.UtcNow;

        var points = new List<AiLearningPointModel>(snap.MarketStates.Count);

        foreach (var ms in snap.MarketStates)
        {
            var atrNorm = NormalizeAtr(ms.Atr, ms.Price);
            var volatilityNorm = NormalizeVolatility(ms.VolatilityPercent, atrNorm);
            var slopeNorm = NormalizeSlope(ms.TrendSlopePercent, atrNorm);

            // Confidence may be 0–1 or already 0–100 depending on writer
            var conf01 = ms.Confidence;
            if (conf01 > 1.5m)
                conf01 = Math.Clamp(conf01 / 100m, 0m, 1m);
            else
                conf01 = Math.Clamp(conf01, 0m, 1m);

            var score = ComputeDisplayScore(
                conf01,
                volatilityNorm,
                slopeNorm,
                ms.PulseValue,
                ms.Time,
                now);

            var regime = ms.Regime.ToString();
            if (string.IsNullOrWhiteSpace(regime) || regime == "0")
                regime = "Unknown";

            var model = new AiLearningPointModel
            {
                Time = ms.Time,
                Symbol = ms.Symbol,
                Score = score,
                Confidence = conf01,
                Slope = slopeNorm,
                Volatility = Math.Clamp(volatilityNorm, 0m, 1.5m) / 1.5m, // 0–1 for chart
                LiquidityDanger = volatilityNorm > 0.9m && Math.Abs(slopeNorm) < 0.2m,
                Regime = regime,
                PulseValue = (decimal)ms.PulseValue
            };

            points.Add(model);
        }

        if (fromUtc.HasValue)
            points = points
                .Where(p => p.Time >= fromUtc.Value)
                .ToList();

        if (minScore > 0)
            points = points
                .Where(p => p.Score >= minScore)
                .ToList();

        return points
            .OrderBy(p => p.Time)
            .ToList();
    }

    // ============================================================
    // ATR NORMALIZATION CORE
    // ============================================================

    private static decimal NormalizeAtr(decimal atr, decimal price)
    {
        if (price <= 0)
            return 0.01m;

        var pct = atr / price;

        return Math.Clamp(pct, 0.001m, 0.05m);
    }

    private static decimal NormalizeVolatility(
        decimal volatility,
        decimal atrNorm)
    {
        var scaled =
            volatility /
            Math.Max(atrNorm, 0.001m);

        return Math.Clamp(scaled, 0.05m, 2.0m);
    }

    private static decimal NormalizeSlope(
        decimal slope,
        decimal atrNorm)
    {
        var scaled =
            slope /
            Math.Max(atrNorm, 0.001m);

        return Math.Clamp(scaled, -2.0m, 2.0m);
    }

    // ============================================================
    // ADAPTIVE SCORE ENGINE
    // ============================================================

    /// <summary>
    /// UI-only activity score 0–100. Not a trade gate.
    /// Avoids collapsing to 1 when engine Confidence≈0 by blending slope/vol/pulse.
    /// </summary>
    private static int ComputeDisplayScore(
        decimal confidence01,
        decimal volatilityNorm,
        decimal slopeNorm,
        double pulseValue,
        DateTime stateTime,
        DateTime now)
    {
        var ageMinutes = Math.Max(0, (now - stateTime).TotalMinutes);
        // Soft recency: half-life ~3h so dashboard stays readable
        var recency = (decimal)Math.Exp(-ageMinutes / 180.0);
        recency = Math.Clamp(recency, 0.55m, 1m);

        var confPart = confidence01 * 100m;
        var slopePart = Math.Min(30m, Math.Abs(slopeNorm) * 18m);
        var volPart = Math.Min(25m, Math.Abs(volatilityNorm) * 12m);
        var pulsePart = 0m;
        if (pulseValue > 0 && pulseValue <= 1.5)
            pulsePart = (decimal)pulseValue * 40m;
        else if (pulseValue > 1.5 && pulseValue <= 100)
            pulsePart = (decimal)pulseValue * 0.35m;

        decimal raw;
        if (confPart >= 12m)
            raw = confPart * 0.70m + slopePart * 0.15m + volPart * 0.15m;
        else
            // Low conf: still show market activity so UI is not a wall of "1"
            raw = 28m + slopePart + volPart * 0.8m + pulsePart * 0.25m + confPart * 0.5m;

        raw *= recency;
        return (int)Math.Clamp(Math.Round(raw), 1, 100);
    }
}
