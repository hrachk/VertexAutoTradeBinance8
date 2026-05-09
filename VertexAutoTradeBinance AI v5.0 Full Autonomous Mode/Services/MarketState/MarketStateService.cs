using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace VertexAutoTradeBinance8.Services.MarketState
{
    public sealed class MarketStateService
    {
        private readonly ILogger<MarketStateService> _logger;
        private readonly string _path;

        private readonly ConcurrentDictionary<string, MarketStateSnapshot> _states = new();

        public bool IsRestored { get; private set; }

        private readonly SemaphoreSlim _persistGate = new(1, 1);
        private DateTime _lastPersistUtc = DateTime.MinValue;
        private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);

        private readonly object _lock = new();
        private volatile bool _restoreInProgress;

        // hash to detect changes
        private int _lastSnapshotHash;
        public event Action? OnRestored;

        public MarketStateService(
            ILogger<MarketStateService> logger,
            IConfiguration cfg)
        {
            _logger = logger;

            var root = cfg["SharedData:Root"]
                ?? throw new InvalidOperationException("SharedData:Root not configured");

            Directory.CreateDirectory(root);
            _path = Path.Combine(AppContext.BaseDirectory, "market_state_snapshot.json");

            try
            {
                if (!File.Exists(_path))
                {
                    File.WriteAllText(_path, "[]");
                    _logger.LogInformation("[STATE] snapshot created at {path}", _path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STATE] init failed");
            }
        }

        private static string Key(string symbol, string tf)
            => $"{symbol}:{tf}";

        // =====================================================
        // RESTORE (SAFE)
        // =====================================================
        public void Restore()
        {
            try
            {
                _restoreInProgress = true;

                if (!File.Exists(_path))
                {
                    _logger.LogWarning("[STATE] snapshot not found → cold start");
                    return;
                }

                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                var list = JsonSerializer.Deserialize<List<MarketStateSnapshot>>(json, JsonOpts);
                if (list == null || list.Count == 0)
                    return;

                lock (_lock)
                {
                    _states.Clear();

                    foreach (var s in list)
                        _states[Key(s.Symbol, s.Timeframe)] = s;

                    _lastSnapshotHash = ComputeHash(list);
                    IsRestored = true;
                }

                _logger.LogInformation(
                    "[STATE] snapshot restored ({count} entries)",
                    list.Count);

                OnRestored?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STATE] snapshot restore failed");
            }
            finally
            {
                _restoreInProgress = false;
            }
        }


        // =====================================================
        // UPDATE (WS CLOSE SAFE)
        // =====================================================
        private volatile bool _dirty;
        public void Update(MarketStateSnapshot snap)
        {
             
            _states[Key(snap.Symbol, snap.Timeframe)] = snap;
            _dirty = true;
            if (!_dirty)
                return;

            _ = PersistAsync();
            _dirty = false;
        }
      
        // =====================================================
        // ASYNC PERSIST (THROTTLED + CHANGE AWARE)
        // =====================================================
        private async Task PersistAsync()
        {
            if (_restoreInProgress)
                return;

            if (DateTime.UtcNow - _lastPersistUtc < PersistInterval)
                return;

            if (!await _persistGate.WaitAsync(0))
                return;
             

            try
            {
                List<MarketStateSnapshot> snapshot;
                lock (_lock)
                {
                    snapshot = _states.Values.ToList();
                }

                if (snapshot.Count == 0)
                    return;

                var hash = ComputeHash(snapshot);
                if (hash == _lastSnapshotHash)
                    return;

                var json = JsonSerializer.Serialize(snapshot, JsonOpts);

                var tmp = _path + ".tmp";
                await File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
                File.Move(tmp, _path, overwrite: true);

                _lastSnapshotHash = hash;
                _lastPersistUtc = DateTime.UtcNow;

                _logger.LogDebug(
                    "[STATE] snapshot persisted ({count} entries)",
                    snapshot.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STATE] snapshot persist failed");
            }
            finally
            {
                _persistGate.Release();
            }
        }


        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private static int ComputeHash(List<MarketStateSnapshot> list)
        {
            unchecked
            {
                int hash = 17;

                foreach (var s in list
                    .OrderBy(x => x.Symbol, StringComparer.Ordinal)
                    .ThenBy(x => x.Timeframe, StringComparer.Ordinal))
                {
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(s.Symbol);
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(s.Timeframe);

                    hash = hash * 31 + s.Regime.GetHashCode();

                    hash = hash * 31 + s.TrendSlope.GetHashCode();
                    hash = hash * 31 + s.Volatility.GetHashCode();

                    hash = hash * 31 + s.Ema21.GetHashCode();
                    hash = hash * 31 + s.Ema55.GetHashCode();
                    hash = hash * 31 + Decimal.ToInt32(s.Atr14 * 1000);

                    hash = hash * 31 + s.LastPrice.GetHashCode();
                    hash = hash * 31 + s.LastCloseTimeUtc.GetHashCode();
                }

                return hash;
            }
        }


        public bool TryGet(
            string symbol,
            string tf,
            out MarketStateSnapshot snap)
        {
            return _states.TryGetValue(Key(symbol, tf), out snap!);
        }
    }
}
