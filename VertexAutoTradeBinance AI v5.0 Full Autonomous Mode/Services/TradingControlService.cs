using System.Text.Json;
using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Live control shared with Web UI (no appsettings reload / restart).
    /// File: C:\VertexShared\trading_control.json
    /// </summary>
    public class TradingControlService
    {
        private readonly ILogger<TradingControlService> _logger;
        private readonly string _path;
        private readonly object _lock = new();
        private DateTime _lastReadUtc = DateTime.MinValue;
        private ControlState _cache = new();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public class ControlState
        {
            public bool TradingEnabled { get; set; } = true;
            /// <summary>true = no new entries Sat/Sun UTC.</summary>
            public bool BlockWeekends { get; set; } = true;
            public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
            public string? Source { get; set; }
        }

        public TradingControlService(ILogger<TradingControlService> logger)
        {
            _logger = logger;
            _path = ResolvePath();
            EnsureFile();
            _logger.LogInformation("[TRADE-CTRL] control file: {path}", _path);
        }

        public string PathUsed => _path;

        private static string ResolvePath()
        {
            var env = Environment.GetEnvironmentVariable("VERTEX_SHARED");
            if (!string.IsNullOrWhiteSpace(env))
            {
                try { Directory.CreateDirectory(env); } catch { }
                return Path.Combine(env, "trading_control.json");
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Directory.CreateDirectory(@"C:\VertexShared");
                    return @"C:\VertexShared\trading_control.json";
                }
                catch { }
            }

            var userShared = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "VertexShared");
            try { Directory.CreateDirectory(userShared); } catch { }
            if (Directory.Exists(userShared))
                return Path.Combine(userShared, "trading_control.json");

            return Path.Combine(AppContext.BaseDirectory, "trading_control.json");
        }

        private void EnsureFile()
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    if (!File.Exists(_path))
                        WriteStateUnlocked(new ControlState { Source = "init" });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TRADE-CTRL] EnsureFile failed");
                }
            }
        }

        private void WriteStateUnlocked(ControlState state)
        {
            state.UpdatedUtc = DateTime.UtcNow;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
            _cache = state;
            _lastReadUtc = DateTime.UtcNow;
        }

        private ControlState ReadState()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastReadUtc < CacheTtl)
                    return _cache;

                try
                {
                    if (!File.Exists(_path))
                    {
                        _cache = new ControlState();
                        _lastReadUtc = DateTime.UtcNow;
                        return _cache;
                    }

                    var json = File.ReadAllText(_path);
                    _cache = JsonSerializer.Deserialize<ControlState>(json, JsonOpts) ?? new ControlState();
                    _lastReadUtc = DateTime.UtcNow;
                    return _cache;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[TRADE-CTRL] read failed — keep cache");
                    _lastReadUtc = DateTime.UtcNow;
                    return _cache;
                }
            }
        }

        public bool IsTradingEnabled() => ReadState().TradingEnabled;

        public bool IsBlockWeekends() => ReadState().BlockWeekends;

        public void SetTradingEnabled(bool enabled, string source = "api")
        {
            lock (_lock)
            {
                try
                {
                    EnsureFile();
                    var state = ReadStateUnlockedFresh();
                    state.TradingEnabled = enabled;
                    state.Source = source;
                    WriteStateUnlocked(state);
                    _logger.LogInformation("[TRADE-CTRL] TradingEnabled={en} src={src}", enabled, source);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TRADE-CTRL] SetTradingEnabled failed");
                }
            }
        }

        public void SetBlockWeekends(bool block, string source = "api")
        {
            lock (_lock)
            {
                try
                {
                    EnsureFile();
                    var state = ReadStateUnlockedFresh();
                    state.BlockWeekends = block;
                    state.Source = source;
                    WriteStateUnlocked(state);
                    _logger.LogInformation("[TRADE-CTRL] BlockWeekends={b} src={src}", block, source);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TRADE-CTRL] SetBlockWeekends failed");
                }
            }
        }

        public void SetAll(bool tradingEnabled, bool blockWeekends, string source = "api")
        {
            lock (_lock)
            {
                try
                {
                    EnsureFile();
                    WriteStateUnlocked(new ControlState
                    {
                        TradingEnabled = tradingEnabled,
                        BlockWeekends = blockWeekends,
                        Source = source
                    });
                    _logger.LogInformation(
                        "[TRADE-CTRL] TradingEnabled={en} BlockWeekends={bw} src={src} path={path}",
                        tradingEnabled, blockWeekends, source, _path);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TRADE-CTRL] SetAll failed");
                }
            }
        }

        private ControlState ReadStateUnlockedFresh()
        {
            try
            {
                if (!File.Exists(_path))
                    return new ControlState();
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<ControlState>(json, JsonOpts) ?? new ControlState();
            }
            catch
            {
                return new ControlState
                {
                    TradingEnabled = _cache.TradingEnabled,
                    BlockWeekends = _cache.BlockWeekends
                };
            }
        }
    }
}
