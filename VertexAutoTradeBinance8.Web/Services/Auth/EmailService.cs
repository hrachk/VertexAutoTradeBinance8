using System.Net;
using System.Net.Mail;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// Sends transactional emails via SMTP.
/// Configure in appsettings.web.json under "Email" section.
/// Supports Gmail, Outlook, custom SMTP.
/// Falls back to console log if SMTP not configured (dev mode).
/// </summary>
public sealed class EmailService
{
    private readonly EmailOptions _opt;
    private readonly ILogger<EmailService> _log;

    public EmailService(IConfiguration cfg, ILogger<EmailService> log)
    {
        _log = log;
        _opt = cfg.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();
    }

    public async Task<bool> SendVerificationCodeAsync(string toEmail, string displayName, string code)
    {
        var subject = "Vertex AI — Подтверждение email";
        var body    = BuildVerificationEmail(displayName, code);
        return await SendAsync(toEmail, subject, body);
    }

    public async Task<bool> SendWelcomeAsync(string toEmail, string displayName)
    {
        var subject = "Добро пожаловать в Vertex AI!";
        var body    = BuildWelcomeEmail(displayName);
        return await SendAsync(toEmail, subject, body);
    }

    private async Task<bool> SendAsync(string to, string subject, string htmlBody)
    {
        // Dev mode: no SMTP configured → just log the email
        if (string.IsNullOrWhiteSpace(_opt.SmtpHost) || string.IsNullOrWhiteSpace(_opt.From))
        {
            _log.LogInformation("[EMAIL] DEV MODE — would send to {to}: {subject}", to, subject);
            _log.LogInformation("[EMAIL] Body: {body}", htmlBody);
            return true; // pretend it was sent in dev
        }

        try
        {
            using var client = new SmtpClient(_opt.SmtpHost, _opt.SmtpPort)
            {
                EnableSsl            = _opt.UseSsl,
                Credentials          = new NetworkCredential(_opt.Username, _opt.Password),
                DeliveryMethod       = SmtpDeliveryMethod.Network,
                Timeout              = 10_000,
            };

            using var msg = new MailMessage
            {
                From       = new MailAddress(_opt.From, _opt.FromName ?? "Vertex AI"),
                Subject    = subject,
                Body       = htmlBody,
                IsBodyHtml = true,
            };
            msg.To.Add(to);

            await client.SendMailAsync(msg);
            _log.LogInformation("[EMAIL] Sent to {to}: {subject}", to, subject);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[EMAIL] Failed to send to {to}", to);
            return false;
        }
    }

    // ── Email templates ───────────────────────────────────
    private static string BuildVerificationEmail(string name, string code) => $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"></head>
        <body style="background:#050a12;margin:0;padding:40px 20px;font-family:'Inter',sans-serif">
          <div style="max-width:480px;margin:0 auto;background:#0a1220;border:1px solid #1a2e44;
                      border-radius:12px;overflow:hidden">
            <div style="background:linear-gradient(135deg,#0f2040,#0a1220);padding:28px 32px;
                        border-bottom:1px solid #1a2e44;text-align:center">
              <div style="font-size:24px;font-weight:900;color:#f0f6ff;letter-spacing:-0.5px">
                VERTEX <span style="color:#38bdf8">AI</span>
              </div>
              <div style="font-size:12px;color:#334e68;margin-top:4px">Autonomous Trading Intelligence</div>
            </div>
            <div style="padding:32px">
              <p style="font-size:15px;font-weight:700;color:#f0f6ff;margin:0 0 8px">
                Привет, {name}!
              </p>
              <p style="font-size:13px;color:#6e90b2;margin:0 0 24px;line-height:1.6">
                Для завершения регистрации введи код подтверждения:
              </p>
              <div style="background:#050a12;border:1px solid #1e3050;border-radius:8px;
                          padding:20px;text-align:center;margin-bottom:24px">
                <div style="font-size:40px;font-weight:900;color:#38bdf8;letter-spacing:12px;
                            font-family:'JetBrains Mono',monospace">
                  {code}
                </div>
                <div style="font-size:11px;color:#334e68;margin-top:8px">
                  Код действителен 15 минут
                </div>
              </div>
              <p style="font-size:12px;color:#334e68;margin:0;line-height:1.6">
                Если ты не регистрировался в Vertex AI — просто проигнорируй это письмо.
              </p>
            </div>
            <div style="background:#050a12;padding:16px 32px;border-top:1px solid #1a2e44;
                        text-align:center;font-size:11px;color:#1e3050">
              Vertex AI · Autonomous Crypto Trading · vertexai.trade
            </div>
          </div>
        </body>
        </html>
        """;

    private static string BuildWelcomeEmail(string name) => $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"></head>
        <body style="background:#050a12;margin:0;padding:40px 20px;font-family:'Inter',sans-serif">
          <div style="max-width:480px;margin:0 auto;background:#0a1220;border:1px solid #1a2e44;
                      border-radius:12px;overflow:hidden">
            <div style="background:linear-gradient(135deg,#0f2040,#0a1220);padding:28px 32px;
                        border-bottom:1px solid #1a2e44;text-align:center">
              <div style="font-size:24px;font-weight:900;color:#f0f6ff">
                VERTEX <span style="color:#38bdf8">AI</span>
              </div>
            </div>
            <div style="padding:32px">
              <p style="font-size:20px;font-weight:900;color:#f0f6ff;margin:0 0 8px">
                🎉 Добро пожаловать, {name}!
              </p>
              <p style="font-size:13px;color:#6e90b2;margin:0 0 16px;line-height:1.6">
                Твой аккаунт подтверждён. Теперь доступно:
              </p>
              <ul style="color:#94a3b8;font-size:13px;line-height:2;padding-left:18px;margin:0 0 20px">
                <li>🎮 <strong style="color:#fb923c">Demo-режим</strong> — $10,000 виртуальных USDT на живых ценах</li>
                <li>🤖 AI-сигналы в реальном времени</li>
                <li>📊 Multi-Chart с управлением позициями</li>
                <li>📈 Статистика и история сделок</li>
              </ul>
              <a href="http://127.0.0.1:5101/market"
                 style="display:inline-block;background:#3b82f6;color:#fff;font-weight:700;
                        font-size:14px;padding:12px 28px;border-radius:7px;text-decoration:none">
                Перейти на Market →
              </a>
            </div>
          </div>
        </body>
        </html>
        """;
}

public sealed class EmailOptions
{
    public string SmtpHost  { get; set; } = "";
    public int    SmtpPort  { get; set; } = 587;
    public bool   UseSsl    { get; set; } = true;
    public string Username  { get; set; } = "";
    public string Password  { get; set; } = "";
    public string From      { get; set; } = "";
    public string? FromName { get; set; } = "Vertex AI";
}
