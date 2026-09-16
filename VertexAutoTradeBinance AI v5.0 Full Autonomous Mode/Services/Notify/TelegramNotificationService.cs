using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Notify;

public sealed class TelegramMessage
{
    public string Text { get; init; } = "";
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Async Telegram outbox. Configure Telegram:BotToken + Telegram:ChatId.
/// If missing, messages are logged only.
/// </summary>
public sealed class TelegramNotificationService : BackgroundService
{
    private readonly ILogger<TelegramNotificationService> _log;
    private readonly IConfiguration _cfg;
    private readonly Channel<TelegramMessage> _q =
        Channel.CreateBounded<TelegramMessage>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public TelegramNotificationService(ILogger<TelegramNotificationService> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public void Enqueue(string text)
    {
        _q.Writer.TryWrite(new TelegramMessage { Text = text });
    }

    public void Entry(string symbol, string side, decimal entry, decimal sl, decimal tp, decimal riskUsd)
        => Enqueue($"🟢 ENTRY {symbol} {side}\nEntry={entry} SL={sl} TP={tp}\nRisk≈${riskUsd:F2}");

    public void Exit(string symbol, decimal r, decimal pnl, string reason)
        => Enqueue($"🔴 EXIT {symbol}\nR={r:F2} PnL=${pnl:F2}\n{reason}");

    public void RiskAlert(string msg)
        => Enqueue($"⚠️ RISK ALERT\n{msg}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _cfg["Telegram:BotToken"];
        var chat = _cfg["Telegram:ChatId"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat))
            _log.LogInformation("[TG] BotToken/ChatId not set — alerts log-only");

        await foreach (var msg in _q.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat))
                {
                    _log.LogInformation("[TG-OUT] {text}", msg.Text);
                    continue;
                }
                var url = $"https://api.telegram.org/bot{token}/sendMessage";
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = chat!,
                    ["text"] = msg.Text,
                    ["disable_web_page_preview"] = "true"
                });
                using var resp = await _http.PostAsync(url, content, stoppingToken);
                if (!resp.IsSuccessStatusCode)
                    _log.LogWarning("[TG] HTTP {code}", (int)resp.StatusCode);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[TG] send failed");
            }
        }
    }
}
