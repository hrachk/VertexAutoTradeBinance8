using System.Text.Json;
using System.Text.Json.Nodes;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Reads/writes bot appsettings.json (Trading / SymbolSelection / Strategy / TakeProfit / TestMode).
/// Path: BotSettings:Path, else auto-discover common locations.
/// </summary>
public class TradingSettingsService
{
    private readonly ILogger<TradingSettingsService> _logger;
    private readonly string _path;
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public TradingSettingsService(ILogger<TradingSettingsService> logger, IConfiguration config)
    {
        _logger = logger;
        var configured = config["BotSettings:Path"];
        _path = ResolvePath(configured);
        _logger.LogInformation("[SETTINGS] Using bot config path: {path} (exists={exists})", _path, File.Exists(_path));
    }

    public string PathUsed => _path;

    private static string ResolvePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured!;

        var candidates = new List<string>();

        // Explicit config even if missing (user intent)
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured!);

        // Shared folder (same as engine_state)
        candidates.Add(@"C:\VertexShared\appsettings.json");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "VertexShared", "appsettings.json"));

        // Next to published web / bot
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "appsettings.json"));
        candidates.Add(Path.Combine(baseDir, "bot-appsettings.json"));

        // Walk up from publish dir looking for AI v5.0 project
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName,
                "VertexAutoTradeBinance AI v5.0 Full Autonomous Mode", "appsettings.json"));
            candidates.Add(Path.Combine(dir.FullName, "appsettings.json"));
        }

        // Common repo path for this user
        candidates.Add(@"C:\Users\karap\source\repos\VertexAutoTradeBinance8\VertexAutoTradeBinance AI v5.0 Full Autonomous Mode\appsettings.json");

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(c))
                    return Path.GetFullPath(c);
            }
            catch { /* ignore */ }
        }

        // Default writable shared path (will show "not found" until user creates/copies)
        var fallback = @"C:\VertexShared\appsettings.json";
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured!;
    }

    public TradingSettingsDto Load()
    {
        var dto = new TradingSettingsDto();
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogWarning("Bot settings file not found: {path}", _path);
                return dto;
            }

            var json = File.ReadAllText(_path);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;
            if (root == null) return dto;

            var trading = root["Trading"] as JsonObject;
            if (trading != null)
            {
                dto.RiskPerTrade = GetDec(trading, "RiskPerTrade", 0.025m);
                dto.Deposit = GetDec(trading, "Deposit", 15m);
                dto.Leverage = GetInt(trading, "Leverage", 1);
                dto.CooldownSeconds = GetInt(trading, "CooldownSeconds", 360);
                dto.TimeframeMinutes = GetInt(trading, "TimeframeMinutes", 5);
                dto.MaxRiskPercent = GetDec(trading, "MaxRiskPercent", 1.2m);
                dto.BaseRiskPercent = GetDec(trading, "BaseRiskPercent", 1.0m);
                dto.MinNotional = GetDec(trading, "MinNotional", 10m);
                dto.MinNotionalGuard = GetDec(trading, "MinNotionalGuard", 30m);
                dto.TradingEnabled = trading["TradingEnabled"]?.GetValue<bool>() ?? true;
                dto.MaxOpenPositions = GetInt(trading, "MaxOpenPositions", 4);
                dto.PostCloseCooldownMinutes = GetInt(trading, "PostCloseCooldownMinutes", 45);
                dto.SameSideCooldownMinutes = GetInt(trading, "SameSideCooldownMinutes", 240);

                var sessions = trading["TradingSessions"] as JsonObject;
                if (sessions != null)
                {
                    dto.SessionsEnabled = sessions["Enabled"]?.GetValue<bool>() ?? true;
                    dto.EarlyStartMinutes = GetInt(sessions, "EarlyStartMinutes", 60);
                    dto.BlockWeekends = sessions["BlockWeekends"]?.GetValue<bool>() ?? true;
                    var windows = sessions["Windows"] as JsonArray;
                    if (windows != null)
                    {
                        foreach (var w in windows)
                        {
                            var wo = w as JsonObject;
                            if (wo == null) continue;
                            var name = wo["Name"]?.ToString() ?? "";
                            if (name.Equals("London", StringComparison.OrdinalIgnoreCase))
                            {
                                dto.LondonStartUtc = wo["StartUtc"]?.ToString() ?? "07:00";
                                dto.LondonEndUtc = wo["EndUtc"]?.ToString() ?? "16:00";
                            }
                            else if (name.Equals("NewYork", StringComparison.OrdinalIgnoreCase))
                            {
                                dto.NewYorkStartUtc = wo["StartUtc"]?.ToString() ?? "12:00";
                                dto.NewYorkEndUtc = wo["EndUtc"]?.ToString() ?? "21:00";
                            }
                        }
                    }
                }
            }

            var auto = root["SymbolSelection"]?["Auto"] as JsonObject;
            if (auto != null)
            {
                dto.TopVolumeCount = GetInt(auto, "TopVolumeCount", 25);
                dto.Min24hVolume = GetDec(auto, "Min24hVolume", 2_000_000m);
                dto.MinPrice = GetDec(auto, "MinPrice", 0.005m);
                dto.RefreshIntervalMin = GetInt(auto, "RefreshInterval", 5);
            }

            var strategy = root["Strategy"] as JsonObject;
            if (strategy != null)
            {
                dto.StrategyMode = strategy["Mode"]?.ToString() ?? "Pullback";
                dto.PullbackType = strategy["PullbackType"]?.ToString() ?? "CandleNearEma";
                dto.EmaPeriod = GetInt(strategy, "EmaPeriod", 21);
                dto.EmaZonePercent = GetDec(strategy, "EmaZonePercent", 0.002m);
            }

            var tp = root["TakeProfit"] as JsonObject;
            if (tp != null)
            {
                dto.TpMode = tp["Mode"]?.ToString() ?? "MultiAtr";
                dto.AtrPeriod = GetInt(tp, "AtrPeriod", 14);
                dto.Tp1Multiplier = GetDec(tp, "Tp1Multiplier", 1.0m);
                dto.Tp2Multiplier = GetDec(tp, "Tp2Multiplier", 1.5m);
                dto.Tp3Multiplier = GetDec(tp, "Tp3Multiplier", 2.0m);
            }

            var test = root["TestMode"] as JsonObject;
            if (test != null)
                dto.TestModeEnabled = test["Enabled"]?.GetValue<bool>() ?? false;

            var binance = root["Binance"] as JsonObject;
            if (binance != null)
            {
                var key = binance["ApiKey"]?.ToString() ?? "";
                dto.ApiKeyMasked = Mask(key);
                dto.IsTestNet = binance["IsTestNet"]?.GetValue<bool>() ?? false;
                dto.UseFutures = binance["UseFutures"]?.GetValue<bool>() ?? true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load bot settings");
        }
        return dto;
    }

    public (bool Ok, string Message) Save(TradingSettingsDto dto)
    {
        try
        {
            // Ensure directory exists for shared path
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            JsonObject root;
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) as JsonObject ?? new JsonObject();
            }
            else
            {
                // Create minimal structure if missing
                root = new JsonObject();
            }

            var trading = root["Trading"] as JsonObject ?? new JsonObject();
            trading["RiskPerTrade"] = dto.RiskPerTrade;
            trading["Deposit"] = dto.Deposit;
            trading["Leverage"] = dto.Leverage;
            trading["CooldownSeconds"] = dto.CooldownSeconds;
            trading["TimeframeMinutes"] = dto.TimeframeMinutes;
            trading["MaxRiskPercent"] = dto.MaxRiskPercent;
            trading["BaseRiskPercent"] = dto.BaseRiskPercent;
            trading["MinNotional"] = dto.MinNotional;
            trading["MinNotionalGuard"] = dto.MinNotionalGuard;
            trading["TradingEnabled"] = dto.TradingEnabled;
            trading["MaxOpenPositions"] = dto.MaxOpenPositions;
            trading["PostCloseCooldownMinutes"] = dto.PostCloseCooldownMinutes;
            trading["SameSideCooldownMinutes"] = dto.SameSideCooldownMinutes;

            var sessions = new JsonObject
            {
                ["Enabled"] = dto.SessionsEnabled,
                ["EarlyStartMinutes"] = dto.EarlyStartMinutes,
                ["BlockWeekends"] = dto.BlockWeekends,
                ["Windows"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Name"] = "London",
                        ["StartUtc"] = dto.LondonStartUtc,
                        ["EndUtc"] = dto.LondonEndUtc
                    },
                    new JsonObject
                    {
                        ["Name"] = "NewYork",
                        ["StartUtc"] = dto.NewYorkStartUtc,
                        ["EndUtc"] = dto.NewYorkEndUtc
                    }
                }
            };
            trading["TradingSessions"] = sessions;
            root["Trading"] = trading;

            var symSel = root["SymbolSelection"] as JsonObject ?? new JsonObject();
            var auto = symSel["Auto"] as JsonObject ?? new JsonObject();
            auto["TopVolumeCount"] = dto.TopVolumeCount;
            auto["Min24hVolume"] = dto.Min24hVolume;
            auto["MinPrice"] = dto.MinPrice;
            auto["RefreshInterval"] = dto.RefreshIntervalMin;
            symSel["Auto"] = auto;
            root["SymbolSelection"] = symSel;

            var strategy = root["Strategy"] as JsonObject ?? new JsonObject();
            strategy["Mode"] = dto.StrategyMode;
            strategy["PullbackType"] = dto.PullbackType;
            strategy["EmaPeriod"] = dto.EmaPeriod;
            strategy["EmaZonePercent"] = dto.EmaZonePercent;
            root["Strategy"] = strategy;

            var tp = root["TakeProfit"] as JsonObject ?? new JsonObject();
            tp["Mode"] = dto.TpMode;
            tp["AtrPeriod"] = dto.AtrPeriod;
            tp["Tp1Multiplier"] = dto.Tp1Multiplier;
            tp["Tp2Multiplier"] = dto.Tp2Multiplier;
            tp["Tp3Multiplier"] = dto.Tp3Multiplier;
            root["TakeProfit"] = tp;

            var test = root["TestMode"] as JsonObject ?? new JsonObject();
            test["Enabled"] = dto.TestModeEnabled;
            root["TestMode"] = test;

            if (File.Exists(_path))
            {
                var bak = _path + $".bak.{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(_path, bak, overwrite: true);
            }

            File.WriteAllText(_path, root.ToJsonString(Opts));

            // Live switch — bot polls this file every few seconds (no restart)
            var ctrlMsg = WriteTradingControl(dto.TradingEnabled);
            return (true, $"Saved → {_path}. Trade switch: {(dto.TradingEnabled ? "ON" : "OFF")}. {ctrlMsg}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save settings failed");
            return (false, $"Save failed: {ex.Message} (path={_path})");
        }
    }

    private static decimal GetDec(JsonObject o, string k, decimal d)
    {
        try { return o[k]?.GetValue<decimal>() ?? d; } catch { return d; }
    }
    private static int GetInt(JsonObject o, string k, int d)
    {
        try { return o[k]?.GetValue<int>() ?? d; } catch
        {
            try { return (int)(o[k]?.GetValue<decimal>() ?? d); } catch { return d; }
        }
    }

    /// <summary>
    /// Same path the bot TradingControlService reads — C:\VertexShared\trading_control.json
    /// </summary>
    private string WriteTradingControl(bool enabled)
    {
        var paths = new List<string>();
        try
        {
            Directory.CreateDirectory(@"C:\VertexShared");
            paths.Add(@"C:\VertexShared\trading_control.json");
        }
        catch { }

        try
        {
            var user = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "VertexShared");
            Directory.CreateDirectory(user);
            paths.Add(Path.Combine(user, "trading_control.json"));
        }
        catch { }

        paths.Add(Path.Combine(AppContext.BaseDirectory, "trading_control.json"));

        // Also next to bot appsettings if we know it
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                paths.Add(Path.Combine(dir, "trading_control.json"));
        }
        catch { }

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            tradingEnabled = enabled,
            updatedUtc = DateTime.UtcNow,
            source = "web-ui"
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var written = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var d = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                File.WriteAllText(path, payload);
                written.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WriteTradingControl failed for {path}", path);
            }
        }

        if (written.Count == 0)
            return "WARN: trading_control.json not written — OFF may not apply until restart.";

        _logger.LogInformation("[SETTINGS] trading_control written enabled={en} → {paths}",
            enabled, string.Join(" | ", written));
        return $"Live control → {written[0]}";
    }

    private static string Mask(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length < 8) return "••••";
        return key[..4] + "••••••••" + key[^4..];
    }
}

public class TradingSettingsDto
{
    public decimal RiskPerTrade { get; set; } = 0.025m;
    public decimal Deposit { get; set; } = 15m;
    public int Leverage { get; set; } = 1;
    public int CooldownSeconds { get; set; } = 360;
    public int TimeframeMinutes { get; set; } = 5;
    public decimal MaxRiskPercent { get; set; } = 1.2m;
    public decimal BaseRiskPercent { get; set; } = 1.0m;
    public decimal MinNotional { get; set; } = 10m;
    public decimal MinNotionalGuard { get; set; } = 30m;
    public int TopVolumeCount { get; set; } = 25;
    public decimal Min24hVolume { get; set; } = 2_000_000m;
    public decimal MinPrice { get; set; } = 0.005m;
    public int RefreshIntervalMin { get; set; } = 5;
    public string StrategyMode { get; set; } = "Pullback";
    public string PullbackType { get; set; } = "CandleNearEma";
    public int EmaPeriod { get; set; } = 21;
    public decimal EmaZonePercent { get; set; } = 0.002m;
    public string TpMode { get; set; } = "MultiAtr";
    public int AtrPeriod { get; set; } = 14;
    public decimal Tp1Multiplier { get; set; } = 1.0m;
    public decimal Tp2Multiplier { get; set; } = 1.5m;
    public decimal Tp3Multiplier { get; set; } = 2.0m;
    public bool TestModeEnabled { get; set; }
    public string ApiKeyMasked { get; set; } = "••••";
    public bool IsTestNet { get; set; }
    public bool UseFutures { get; set; } = true;

    // Sessions + master switch
    public bool TradingEnabled { get; set; } = true;
    public int MaxOpenPositions { get; set; } = 4;
    public bool SessionsEnabled { get; set; } = true;
    public int EarlyStartMinutes { get; set; } = 60;
    public bool BlockWeekends { get; set; } = true;
    public string LondonStartUtc { get; set; } = "07:00";
    public string LondonEndUtc { get; set; } = "16:00";
    public string NewYorkStartUtc { get; set; } = "12:00";
    public string NewYorkEndUtc { get; set; } = "21:00";

    // Cooldowns
    public int PostCloseCooldownMinutes { get; set; } = 45;
    public int SameSideCooldownMinutes { get; set; } = 240;
}

