using System.Text;
using System.Text.Json;
using VertexAutoTradeBinance8.Models.DTO.Debugg;

namespace VertexAutoTradeBinance8.Models.DTO;

/// <summary>
/// Persist decision markers with crash-safe atomic writes.
/// Null-byte / truncated files (0x00 at start) are common after hard kill mid-write —
/// Restore must recover without Error spam or blocking the engine.
/// </summary>
public sealed class DecisionMarkersFileService
{
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
    private const int MaxStreamsInSnapshot = 200;
    private const int MaxMarkersPerStream = 80;

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
        _logger.LogInformation("[DEBUG] Decision markers path: {path}", _path);
    }

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

            var fi = new FileInfo(_path);
            if (fi.Length == 0)
            {
                SafeDelete(_path, "empty");
                return;
            }

            // Fast reject: leading NUL / non-JSON → Windows crash mid-write pattern
            await using (var probe = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var b0 = probe.ReadByte();
                if (b0 < 0 || b0 == 0x00 || (b0 != '{' && b0 != '['))
                {
                    SafeDelete(_path, $"invalid header 0x{Math.Max(b0, 0):X2}");
                    return;
                }
            }

            Snapshot? snapshot = null;
            try
            {
                await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(fs, JsonOpts, ct);
            }
            catch (JsonException)
            {
                SafeDelete(_path, "json parse");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DEBUG] Decision markers restore read failed — starting clean");
                SafeDelete(_path, "read error");
                return;
            }

            if (snapshot?.Data == null || snapshot.Data.Count == 0)
            {
                _logger.LogDebug("[DEBUG] Decision markers snapshot empty");
                return;
            }

            int restored = 0;
            foreach (var (key, markers) in snapshot.Data)
            {
                if (string.IsNullOrWhiteSpace(key) || markers == null || markers.Count == 0)
                    continue;
                _sink.Restore(key, markers);
                restored += markers.Count;
            }

            _logger.LogInformation(
                "[DEBUG] Decision markers restored: streams={streams}, markers={markers}",
                snapshot.Data.Count, restored);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var dump = _sink.DumpAll();
            var data = new Dictionary<string, List<DecisionMarkerDto>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in dump.Take(MaxStreamsInSnapshot))
            {
                var list = kv.Value?
                    .Where(m => m != null)
                    .TakeLast(MaxMarkersPerStream)
                    .ToList() ?? new List<DecisionMarkerDto>();
                if (list.Count == 0) continue;
                data[kv.Key] = list;
            }

            var snapshot = new Snapshot { Version = CurrentVersion, Data = data };
            var temp = _path + ".tmp";
            var bak = _path + ".bak";

            // Serialize to memory first — never leave a half-written target
            await using (var ms = new MemoryStream())
            {
                await JsonSerializer.SerializeAsync(ms, snapshot, JsonOpts, ct);
                if (ms.Length == 0)
                    return;

                var bytes = ms.ToArray();
                // Refuse to write if somehow empty/null
                if (bytes.Length == 0 || bytes[0] == 0x00)
                {
                    _logger.LogWarning("[DEBUG] Decision markers serialize produced invalid payload — skip save");
                    return;
                }

                await File.WriteAllBytesAsync(temp, bytes, ct);
            }

            // Prefer File.Replace (atomic on NTFS); fallback Move
            try
            {
                if (File.Exists(_path))
                    File.Replace(temp, _path, bak, ignoreMetadataErrors: true);
                else
                    File.Move(temp, _path);
            }
            catch (IOException)
            {
                await ReplaceWithRetryAsync(temp, _path, ct);
            }

            try { if (File.Exists(bak)) File.Delete(bak); } catch { /* ignore */ }
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }

            _logger.LogDebug(
                "[DEBUG] Decision markers saved: streams={streams}, markers={markers}",
                data.Count, data.Sum(x => x.Value.Count));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEBUG] Failed to save decision markers (non-fatal)");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void SafeDelete(string path, string reason)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            _logger.LogWarning("[DEBUG] Removed corrupted decision markers ({reason}) — engine continues", reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEBUG] Could not delete corrupted decision markers");
        }
    }

    private static async Task ReplaceWithRetryAsync(string temp, string target, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (IOException) when (i < maxAttempts - 1)
            {
                await Task.Delay(80 * (i + 1), ct);
            }
        }
        File.Move(temp, target, overwrite: true);
    }
}
