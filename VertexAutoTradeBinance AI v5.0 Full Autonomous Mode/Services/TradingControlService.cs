using System.Text.Json;
using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Live ON/OFF for new entries — shared file read by bot + written by Web UI.
    /// Does NOT require appsettings reload or process restart.
    /// Path priority: VERTEX_SHARED / C:\VertexShared / BaseDirectory.
    /// </summary>
    public class TradingControlService
    {
        private readonly ILogger<TradingControlService> _logger;
        private readonly string _path;
        private readonly object _lock = new();
        private DateTime _lastReadUtc = DateTime.MinValue;
        private bool _cachedEnabled = true;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public class ControlState
        {
            public bool TradingEnabled { get; set; } = true;
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

            // Windows shared folder (same as engine_state)
            var winShared = @"C:\VertexShared";
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Directory.CreateDirectory(winShared);
                    return Path.Combine(winShared, "trading_control.json");
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
                    {
                        var state = new ControlState { TradingEnabled = true, Source = "init" };
                        File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOpts));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TRADE-CTRL] EnsureFile failed");
                }
            }
        }

        public bool IsTradingEnabled()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastReadUtc < CacheTtl)
                    return _cachedEnabled;

                try
                {
                    if (!File.Exists(_path))
                    {
                        _cachedEnabled = true;
                        _lastReadUtc = DateTime.UtcNow;
                        return true;
                    }

                    var json = File.ReadAllText(_path);
                    var state = JsonSerializer.Deserialize<ControlState>(json, JsonOpts);
                    _cachedEnabled = state?.TradingEnabled ?? true;
                    _lastReadUtc = DateTime.UtcNow;
                    return _cachedEnabled;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[TRADE-CTRL] read failed — keep last={last}", _cachedEnabled);
                    _lastReadUtc = DateTime.UtcNow;
                    return _cachedEnabled;
                }
            }
        }

        public void SetTradingEnabled(bool enabled, string source = "api")
        {
            lock (_lock)
            {
                try
                {
                    EnsureFile();
                    var state = new ControlState
                    {
                        TradingEnabled = enabled,
                        UpdatedUtc = DateTime.UtcNow,
                        Source = source
                    };
                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(tmp, _path);
                    _cachedEnabled = enabled;
                    _lastReadUtc = DateTime.UtcNow;
                    _logger.LogInformation(
                        "[TRADE-CTRL] TradingEnabled={en} source={src} path={path}",
                        enabled, source, _path);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TRADE-CTRL] SetTradingEnabled failed");
                }
            }
        }
    }
}
