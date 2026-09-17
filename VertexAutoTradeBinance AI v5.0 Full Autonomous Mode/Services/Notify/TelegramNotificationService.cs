using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.Infra;
using VertexAutoTradeBinance8.Services.Risk;

namespace VertexAutoTradeBinance8.Services.Notify;

public sealed class TelegramMessage
{
    public string Text { get; init; } = "";
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Async Telegram outbox + long-poll commands /status /kill /pause /resume.
/// Config: Telegram:BotToken, Telegram:ChatId, Telegram:AdminChatIds (optional comma list).
/// </summary>
public sealed class TelegramNotificationService : BackgroundService
{
    private readonly ILogger<TelegramNotificationService> _log;
    private readonly IConfiguration _cfg;
    private readonly EmergencyControlService? _kill;
    private readonly FlattenAllService? _flatten;
    private readonly BtcVolatilityFilterService? _btcVol;
    private readonly DailyDrawdownGuard? _dailyDd;
    private readonly Channel<TelegramMessage> _q =
        Channel.CreateBounded<TelegramMessage>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private long _updateOffset;
    private DateTime _lastOkUtc = DateTime.UtcNow;
    private readonly SqliteJournalStore? _sqlite;
    private readonly BinanceTimeSyncService? _timeSync;

    public TelegramNotificationService(
        ILogger<TelegramNotificationService> log,
        IConfiguration cfg,
        EmergencyControlService? kill = null,
        BtcVolatilityFilterService? btcVol = null,
        DailyDrawdownGuard? dailyDd = null,
        FlattenAllService? flatten = null,
        SqliteJournalStore? sqlite = null,
        BinanceTimeSyncService? timeSync = null)
    {
        _log = log;
        _cfg = cfg;
        _kill = kill;
        _btcVol = btcVol;
        _dailyDd = dailyDd;
        _flatten = flatten;
        _sqlite = sqlite;
        _timeSync = timeSync;
    }

    public void Enqueue(string text) => _q.Writer.TryWrite(new TelegramMessage { Text = text });

    public void Entry(string symbol, string side, decimal entry, decimal sl, decimal tp, decimal riskUsd)
        => Enqueue($"🟢 ENTRY {symbol} {side}\nEntry={entry} SL={sl} TP={tp}\nRisk≈${riskUsd:F2}");

    public void Exit(string symbol, decimal r, decimal pnl, string reason)
        => Enqueue($"🔴 EXIT {symbol}\nR={r:F2} PnL=${pnl:F2}\n{reason}");

    public void RiskAlert(string msg)
        => Enqueue($"⚠️ RISK ALERT\n{msg}");

    private string? Token => _cfg["Telegram:BotToken"];
    private string? ChatId => _cfg["Telegram:ChatId"];

    private bool IsAdmin(string chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId)) return false;
        if (string.Equals(chatId, ChatId, StringComparison.Ordinal)) return true;
        var admins = _cfg["Telegram:AdminChatIds"] ?? "";
        return admins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(a => string.Equals(a, chatId, StringComparison.Ordinal));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ChatId))
            _log.LogInformation("[TG] BotToken/ChatId not set — alerts log-only, commands disabled");

        var sendTask = Task.Run(() => SendLoopAsync(stoppingToken), stoppingToken);
        var cmdTask = Task.Run(() => CommandLoopAsync(stoppingToken), stoppingToken);
        var healthTask = Task.Run(() => TgHealthLoopAsync(stoppingToken), stoppingToken);
        await Task.WhenAny(sendTask, cmdTask, healthTask);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var msg in _q.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ChatId))
                {
                    _log.LogInformation("[TG-OUT] {text}", msg.Text);
                    continue;
                }
                await SendAsync(ChatId!, msg.Text, ct);
            }
            catch (Exception ex) { _log.LogDebug(ex, "[TG] send failed"); }
        }
    }

    private async Task CommandLoopAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Token)) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{Token}/getUpdates?timeout=25&offset={_updateOffset}";
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    await Task.Delay(3000, ct);
                    continue;
                }
                MarkTgOk();
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out var arr)) continue;
                foreach (var upd in arr.EnumerateArray())
                {
                    if (upd.TryGetProperty("update_id", out var uid))
                        _updateOffset = uid.GetInt64() + 1;
                    if (!upd.TryGetProperty("message", out var msg)) continue;
                    var chat = msg.GetProperty("chat").GetProperty("id").ToString();
                    var text = msg.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
                    if (!IsAdmin(chat)) continue;
                    await HandleCommandAsync(chat, text.Trim(), ct);
                }
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[TG] getUpdates failed");
                try { await Task.Delay(5000, ct); } catch { break; }
            }
        }
    }


    private void MarkTgOk() => _lastOkUtc = DateTime.UtcNow;

    private async Task TgHealthLoopAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            _log.LogInformation("[TG-HEALTH] no BotToken — health monitor idle");
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var silence = DateTime.UtcNow - _lastOkUtc;
                if (silence.TotalSeconds > 60)
                {
                    _log.LogWarning(
                        "[TG-HEALTH] no successful Telegram API contact for {sec:F0}s (lastOk={last:u})",
                        silence.TotalSeconds, _lastOkUtc);
                    // probe getMe
                    try
                    {
                        using var resp = await _http.GetAsync($"https://api.telegram.org/bot{Token}/getMe", ct);
                        if (resp.IsSuccessStatusCode)
                            MarkTgOk();
                        else
                            _log.LogWarning("[TG-HEALTH] getMe HTTP {code}", (int)resp.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[TG-HEALTH] getMe failed");
                    }
                }
            }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task HandleCommandAsync(string chat, string text, CancellationToken ct)
    {
        var cmd = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        switch (cmd)
        {
            case "/status":
            {
                var vol = _btcVol != null && _btcVol.IsAltEntryLocked(out var vr) ? vr : "BTC vol: OK";
                var (em, dayPnl, stops) = _dailyDd?.Snapshot() ?? (false, 0m, 0);
                var kill = _kill?.IsKillActive == true ? "KILL ACTIVE" : "running";
                var dbMb = _sqlite?.DbSizeMb ?? 0;
                var dbOk = _sqlite?.IntegrityStatus ?? "n/a";
                var offset = _timeSync?.OffsetMs ?? 0;
                var lastSync = _timeSync?.LastSyncUtc;
                var syncStr = lastSync.HasValue && lastSync.Value > DateTime.MinValue
                    ? lastSync.Value.ToString("HH:mm:ss") + "Z"
                    : "never";
                var tgAge = (DateTime.UtcNow - _lastOkUtc).TotalSeconds;
                await SendAsync(chat,
                    $"📊 STATUS\n" +
                    $"Engine: {kill}\n" +
                    $"Day PnL≈{dayPnl:F2} stopsInRow={stops}\n" +
                    $"EmergencyDD={(em ? "YES" : "no")}\n" +
                    $"{vol}\n" +
                    $"SQLite: {dbMb:F2} MB · integrity={dbOk}\n" +
                    $"TimeOffset: {offset} ms · lastSync={syncStr}\n" +
                    $"TG lastOk: {tgAge:F0}s ago\n" +
                    $"UTC {DateTime.UtcNow:HH:mm:ss}",
                    ct);
                break;
            }

            case "/kill":
                _kill?.ActivateKill("telegram /kill");
                await SendAsync(chat, "🛑 KILL — flattening all positions...", ct);
                try
                {
                    if (_flatten != null)
                    {
                        var report = await _flatten.ExecuteAsync("telegram /kill", ct);
                        await SendAsync(chat, report.Length > 3500 ? report[..3500] : report, ct);
                    }
                    else
                        await SendAsync(chat, "Flatten service unavailable — only entry block active. /resume to clear.", ct);
                }
                catch (Exception ex)
                {
                    await SendAsync(chat, "Flatten error: " + ex.Message, ct);
                }
                break;
            case "/resume":
                _kill?.ClearKill();
                await SendAsync(chat, "✅ Kill cleared. AutoTrade entries allowed (subject to other filters).", ct);
                break;
            case "/pause":
            {
                int mins = 30;
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && int.TryParse(parts[1], out var m) && m > 0 && m < 24 * 60)
                    mins = m;
                _kill?.ActivateKill($"telegram /pause {mins}m");
                // schedule clear via fire-and-forget
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(mins), CancellationToken.None);
                        _kill?.ClearKill();
                        Enqueue($"▶️ Pause ended ({mins}m) — entries re-enabled");
                    }
                    catch { }
                });
                await SendAsync(chat, $"⏸ Paused {mins} minutes.", ct);
                break;
            }
            default:
                if (cmd.StartsWith("/"))
                    await SendAsync(chat, "Commands: /status /kill /resume /pause [minutes]", ct);
                break;
        }
    }

    private async Task SendAsync(string chatId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Token)) return;
        var url = $"https://api.telegram.org/bot{Token}/sendMessage";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = text.Length > 3500 ? text[..3500] : text,
            ["disable_web_page_preview"] = "true"
        });
        using var resp = await _http.PostAsync(url, content, ct);
        if (!resp.IsSuccessStatusCode)
            _log.LogWarning("[TG] HTTP {code}", (int)resp.StatusCode);
        else
            MarkTgOk();
    }
}
