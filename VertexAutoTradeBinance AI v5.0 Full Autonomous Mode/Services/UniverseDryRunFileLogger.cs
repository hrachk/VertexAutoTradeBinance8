using System.Text.Json;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

public sealed class UniverseDryRunFileLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _dir;
    private readonly object _lock = new();

    public UniverseDryRunFileLogger(IConfiguration cfg)
    {
        _dir = Path.Combine(
            cfg["SharedData:Root"] ?? AppContext.BaseDirectory,
            "logs");

        Directory.CreateDirectory(_dir);
    }

    public void Log(UniverseDryRunEntry entry)
    {
        try
        {
            var file = Path.Combine(
                _dir,
                $"universe_dryrun_{DateTime.UtcNow:yyyyMMdd}.jsonl");

            var json = JsonSerializer.Serialize(entry, JsonOptions);

            lock (_lock)
            {
                File.AppendAllText(file, json + Environment.NewLine);
            }
        }
        catch
        {
            // ❗ intentionally silent: dry-run must NEVER break trading
        }
    }
}
