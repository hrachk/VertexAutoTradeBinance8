using System.Text.Json;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class AiLearningFileService
{
    private readonly string _filePath;
    private readonly string _backupPath;
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
        var baseDir = Path.Combine(AppContext.BaseDirectory, "ai-models");

        _filePath = Path.Combine(baseDir, "ai_learning.json");
        _backupPath = Path.Combine(baseDir, "ai_learning_backup.json");

        _logger = logger;
    }

    // ============================================================
    // CORE SAFE READER (atomic-safe + fallback)
    // ============================================================
    private async Task<T?> ReadSafeAsync<T>()
    {
        var result = await TryReadFileAsync<T>(_filePath);

        if (result != null)
            return result;

        _logger.LogWarning(
            "[AI-LEARN-WEB] Primary snapshot failed → trying backup");

        result = await TryReadFileAsync<T>(_backupPath);

        if (result != null)
        {
            _logger.LogInformation(
                "[AI-LEARN-WEB] Backup snapshot loaded successfully");

            return result;
        }

        _logger.LogError(
            "[AI-LEARN-WEB] Both primary and backup snapshot failed");

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
            // ====================================================
            // ATR-ADAPTIVE NORMALIZATION
            // ====================================================

            var atrNorm = NormalizeAtr(ms.Atr, ms.Price);

            var volatilityNorm =
                NormalizeVolatility(ms.VolatilityPercent, atrNorm);

            var slopeNorm =
                NormalizeSlope(ms.TrendSlopePercent, atrNorm);

            var score =
                ComputeAdaptiveScore(
                    ms.Confidence,
                    volatilityNorm,
                    slopeNorm,
                    ms.Time,
                    now);

            var model = new AiLearningPointModel
            {
                Time = ms.Time,
                Symbol = ms.Symbol,

                Score = score,
                Confidence = ms.Confidence,

                Slope = slopeNorm,
                Volatility = volatilityNorm,

                LiquidityDanger =
                    volatilityNorm > 0.75m &&
                    Math.Abs(slopeNorm) < 0.15m
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

    private static int ComputeAdaptiveScore(
        decimal confidence,
        decimal volatilityNorm,
        decimal slopeNorm,
        DateTime stateTime,
        DateTime now)
    {
        var ageMinutes =
            (decimal)(now - stateTime).TotalMinutes;

        var recencyFactor =
            Math.Exp(-(double)ageMinutes / 90.0);

        var score =
            confidence * 100m *
            (1m + Math.Abs(slopeNorm) * 0.35m) *
            (decimal)recencyFactor;

        return (int)Math.Clamp(score, 1m, 100m);
    }
}
