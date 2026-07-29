using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class TradePermissionFileService
{
    private readonly string _traceDir;

    public TradePermissionFileService(IConfiguration cfg)
    {
        // Engine writes decision_trace_*.jsonl under SharedData:Root —
        // AppContext.BaseDirectory here points at the Web app's OWN bin
        // folder, which is a different physical path from where the
        // Engine process actually runs and writes these files.
        var root = cfg["SharedData:Root"];
        _traceDir = !string.IsNullOrWhiteSpace(root)
            ? Path.Combine(root, "ai-models", "decision-trace")
            : Path.Combine(AppContext.BaseDirectory, "ai-models", "decision-trace");
    }

    public async Task<IReadOnlyList<TradePermissionEvent>> LoadLatestAsync()
    {
        var file = Directory
            .GetFiles(_traceDir, "decision_trace_*.jsonl")
            .OrderByDescending(f => f)
            .FirstOrDefault();

        if (file == null)
            return [];

        var all = new List<TradePermissionEvent>();

        using var fs = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        using var sr = new StreamReader(fs);

        while (!sr.EndOfStream)
        {
            var line = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var e = JsonSerializer.Deserialize<TradePermissionEvent>(line);
                if (e != null)
                    all.Add(e);
            }
            catch
            {
                // пропускаем битую строку
            }
        }

        // ⚠️ ВАЖНО: берём ПОСЛЕДНЕЕ решение
        return all
            .GroupBy(x => (x.Symbol, x.Timeframe))
            .Select(g => g.OrderByDescending(x => x.Time).First())
            .OrderByDescending(x => x.Time)
            .ToList();
    }

    /// <summary>Returns the decision-trace folder for a specific client.</summary>
    public static string GetTraceDirForRoot(string root)
        => Path.Combine(root, "ai-models", "decision-trace");

}
