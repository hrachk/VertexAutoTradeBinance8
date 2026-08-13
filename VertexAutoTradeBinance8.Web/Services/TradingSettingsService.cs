using System.Text.Json;
using System.Text.Json.Nodes;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Reads/writes bot appsettings.json (Trading / SymbolSelection / Strategy / TakeProfit / TestMode).
/// Path can be overridden via config key BotSettings:Path.
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
        _path = config["BotSettings:Path"]
            ?? Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "VertexAutoTradeBinance AI v5.0 Full Autonomous Mode",
                "appsettings.json"));
    }

    public string PathUsed => _path;

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
            {
                dto.TestModeEnabled = test["Enabled"]?.GetValue<bool>() ?? false;
            }

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
            if (!File.Exists(_path))
                return (false, $"File not found: {_path}");

            var json = File.ReadAllText(_path);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;
            if (root == null) return (false, "Invalid JSON");

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

            // backup
            var bak = _path + $".bak.{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_path, bak, overwrite: true);

            File.WriteAllText(_path, root.ToJsonString(Opts));
            return (true, $"Saved. Backup: {Path.GetFileName(bak)}. Restart bot to apply.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save settings failed");
            return (false, ex.Message);
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
}
