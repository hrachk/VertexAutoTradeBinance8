using System.Text.Json;

namespace VertexAutoTradeBinance8.Models.DTO;

public sealed class DecisionMarkersFileService
{ // ================= DTO WRAPPER =================
    private sealed class Snapshot
    {
        public int Version { get; set; }
        public Dictionary<string, List<DecisionMarkerDto>> Data { get; set; } = new();
    }


    private readonly DecisionMarkerSink _sink;
    private readonly string _path;
    private readonly ILogger<DecisionMarkersFileService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DecisionMarkersFileService(
        DecisionMarkerSink sink,
        ILogger<DecisionMarkersFileService> logger)
    {
        _sink = sink;
        _logger = logger;
         
        var dir = Path.Combine(AppContext.BaseDirectory, "market");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "decision_markers.json");
        _logger.LogError("FULL PATH: {path}", _path);
    } 

    // ================= RESTORE =================
    public async Task RestoreAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogInformation("[DEBUG] No decision markers snapshot found");
                return;
            }

            await using var fs = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            Snapshot? snapshot;

            try
            {
                snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(fs, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[DEBUG] Corrupted decision markers file, deleting");
                fs.Close();
                File.Delete(_path);
                return;
            }

            if (snapshot?.Data == null || snapshot.Data.Count == 0)
            {
                _logger.LogWarning("[DEBUG] Decision markers snapshot empty");
                return;
            }

            int restored = 0;

            foreach (var (key, markers) in snapshot.Data)
            {
                if (markers == null || markers.Count == 0)
                    continue;

                _sink.Restore(key, markers);
                restored += markers.Count;
            }

            _logger.LogInformation("[DEBUG] Decision markers restored: {count}", restored);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DEBUG] Failed to restore decision markers");
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================= SAVE =================
    public async Task SaveAsync(CancellationToken ct)
    {
        _logger.LogWarning("SAVE CALLED at {time}", DateTime.UtcNow);
        await _lock.WaitAsync(ct);
        try
        {
            var snapshot = new Snapshot
            {
                Version = CurrentVersion,
                Data = _sink
                    .DumpAll()
                    .ToDictionary(kv => kv.Key, kv => kv.Value.ToList())
            };

            var temp = _path + ".tmp";

            // --- write temp ---
            await using (var fs = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, snapshot, JsonOpts, ct);
                await fs.FlushAsync(ct);
            }

            // --- atomic replace with retry ---
            await ReplaceWithRetryAsync(temp, _path, ct);

            var totalMarkers = snapshot.Data.Sum(x => x.Value.Count);

            _logger.LogInformation(
                "[DEBUG] Decision markers saved: streams={streams}, markers={markers}",
                snapshot.Data.Count,
                totalMarkers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DEBUG] Failed to save decision markers");
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================= REPLACE RETRY =================
    private async Task ReplaceWithRetryAsync(string temp, string target, CancellationToken ct)
    {
        const int maxAttempts = 5;

        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                File.Move(temp, target, true);
                return;
            }
            catch (IOException) when (i < maxAttempts - 1)
            {
                await Task.Delay(100 * (i + 1), ct);
            }
        }

        // last attempt — let it throw
        File.Move(temp, target, true);
    }
}