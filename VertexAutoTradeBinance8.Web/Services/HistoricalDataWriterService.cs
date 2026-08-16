using System.Globalization;
using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Web-side write access to the shared candle archive:
///   {SharedData:Root}/datadb/{SYMBOL}/{tf}.json
/// Atomic tmp→rename so a crash mid-write never leaves a corrupt file.
/// </summary>
public sealed class HistoricalDataWriterService
{
    private readonly string _root;
    private readonly ILogger<HistoricalDataWriterService> _log;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public string Root => _root;

    public HistoricalDataWriterService(IConfiguration cfg, ILogger<HistoricalDataWriterService> log)
    {
        _log = log;
        var shared = cfg["SharedData:Root"];
        _root = !string.IsNullOrWhiteSpace(shared)
            ? Path.Combine(shared, "datadb")
            : Path.Combine(AppContext.BaseDirectory, "datadb");
        try { Directory.CreateDirectory(_root); } catch { }
    }

    private static string Safe(string s) =>
        string.Concat((s ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));

    public string FilePath(string symbol, string timeframeLabel) =>
        Path.Combine(_root, Safe(symbol.ToUpperInvariant()), $"{Safe(timeframeLabel.ToLowerInvariant())}.json");

    public bool HasEnough(string symbol, string timeframeLabel, int minBars)
    {
        var path = FilePath(symbol, timeframeLabel);
        if (!File.Exists(path)) return false;
        try
        {
            var text = File.ReadAllText(path);
            int n = 0;
            foreach (var ch in text)
                if (ch == '{') n++;
            return n >= minBars;
        }
        catch { return false; }
    }

    public async Task SaveAsync(string symbol, string timeframeLabel, IReadOnlyList<KlineDto> klines, CancellationToken ct = default)
    {
        if (klines == null || klines.Count == 0) return;
        var path = FilePath(symbol, timeframeLabel);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        List<KlineDto> merged = klines.ToList();
        try
        {
            if (File.Exists(path))
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var existing = await JsonSerializer.DeserializeAsync<List<KlineDto>>(fs, cancellationToken: ct);
                if (existing != null && existing.Count > 0)
                {
                    var map = existing.ToDictionary(k => k.OpenTime);
                    foreach (var k in klines)
                        map[k.OpenTime] = k;
                    merged = map.Values.OrderBy(k => k.OpenTime).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[HIST-WRITE] merge read failed {sym}/{tf}", symbol, timeframeLabel);
        }

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(merged, JsonOpts);
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
        _log.LogInformation("[HIST-WRITE] {sym}/{tf} bars={n} → {path}",
            symbol, timeframeLabel, merged.Count, path);
    }
}

