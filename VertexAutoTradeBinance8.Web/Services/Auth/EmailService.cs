using System.Net;
using System.Net.Mail;
using System.Text;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// Universal email sender.
///
/// In appsettings.web.json specify only:
///   "Email": {
///     "Provider": "gmail",          // gmail | mailru | yandex | outlook | yahoo | custom
///     "Username": "you@gmail.com",  // your login
///     "Password":  "app-password",  // app password (not account password!)
///     "FromName":  "Vertex AI"      // display name (optional)
///   }
///
/// For custom SMTP also add:
///   "SmtpHost": "smtp.example.com"
///   "SmtpPort": 587
///   "UseSsl": true
///
/// If Provider is empty or not configured → dev mode (logs to console).
/// </summary>
public sealed class EmailService
{
    // ── Known providers — SMTP settings auto-resolved by name ──────
    private static readonly Dictionary<string, SmtpPreset> Presets = new(
        StringComparer.OrdinalIgnoreCase)
    {
        // ── Google Gmail ────────────────────────────────────────────
        // Requires: Google Account → Security → 2-Step → App Passwords
        // Generate 16-char app password for "Mail" app
        ["gmail"]   = new("smtp.gmail.com",        587, true),
        ["google"]  = new("smtp.gmail.com",        587, true),

        // ── Mail.ru ─────────────────────────────────────────────────
        // Requires: mail.ru Settings → Security → App Passwords
        ["mailru"]  = new("smtp.mail.ru",          465, true),
        ["mail.ru"] = new("smtp.mail.ru",          465, true),
        ["bk"]      = new("smtp.mail.ru",          465, true), // bk.ru, inbox.ru, list.ru

        // ── Yandex ──────────────────────────────────────────────────
        // Requires: id.yandex.ru → Security → App Passwords
        ["yandex"]  = new("smtp.yandex.ru",        465, true),
        ["ya"]      = new("smtp.yandex.ru",        465, true),

        // ── Microsoft Outlook / Hotmail / Live ──────────────────────
        // Works with Microsoft personal accounts
        ["outlook"] = new("smtp-mail.outlook.com", 587, true),
        ["hotmail"] = new("smtp-mail.outlook.com", 587, true),
        ["live"]    = new("smtp-mail.outlook.com", 587, true),

        // ── Microsoft Office 365 (work/school accounts) ─────────────
        ["office365"] = new("smtp.office365.com",  587, true),
        ["o365"]      = new("smtp.office365.com",  587, true),

        // ── Yahoo Mail ──────────────────────────────────────────────
        // Requires: Account Security → Generate App Password
        ["yahoo"]   = new("smtp.mail.yahoo.com",   587, true),

        // ── iCloud Mail ─────────────────────────────────────────────
        // Requires: appleid.apple.com → App-Specific Passwords
        ["icloud"]  = new("smtp.mail.me.com",      587, true),
        ["apple"]   = new("smtp.mail.me.com",      587, true),

        // ── Zoho Mail ───────────────────────────────────────────────
        ["zoho"]    = new("smtp.zoho.com",         587, true),

        // ── ProtonMail Bridge ────────────────────────────────────────
        // Requires: ProtonMail Bridge app running locally
        ["proton"]  = new("127.0.0.1",             1025, false),

        // ── Resend.com (HTTP API but supports SMTP relay) ───────────
        // Username: "resend", Password: your API key
        ["resend"]  = new("smtp.resend.com",       465, true),

        // ── SendGrid SMTP relay ──────────────────────────────────────
        // Username: "apikey", Password: your SendGrid API key
        ["sendgrid"] = new("smtp.sendgrid.net",    587, true),
        ["sg"]       = new("smtp.sendgrid.net",    587, true),

        // ── Mailgun SMTP ─────────────────────────────────────────────
        ["mailgun"] = new("smtp.mailgun.org",      587, true),

        // ── Mailtrap (for testing — never sends real emails) ─────────
        ["mailtrap"] = new("smtp.mailtrap.io",     2525, false),
        ["test"]     = new("smtp.mailtrap.io",     2525, false),
    };

    private readonly ResolvedSmtp  _smtp;
    private readonly ILogger<EmailService> _log;

    public EmailService(IConfiguration cfg, ILogger<EmailService> log)
    {
        _log = log;
        _smtp = Resolve(cfg);

        if (_smtp.IsDevMode)
            _log.LogWarning("[EMAIL] Dev mode — emails will be printed to console, not sent.");
        else
            _log.LogInformation("[EMAIL] Provider={p} host={h}:{port} from={f}",
                _smtp.ProviderName, _smtp.Host, _smtp.Port, _smtp.From);
    }

    // ── Public API ─────────────────────────────────────────────────
    public Task<bool> SendVerificationCodeAsync(
        string toEmail, string displayName, string code)
        => SendAsync(toEmail,
            "Vertex AI — Подтверждение email",
            BuildVerificationEmail(displayName, code));

    public Task<bool> SendWelcomeAsync(string toEmail, string displayName)
        => SendAsync(toEmail,
            "Добро пожаловать в Vertex AI!",
            BuildWelcomeEmail(displayName));

    // ── Send ───────────────────────────────────────────────────────
    private async Task<bool> SendAsync(string to, string subject, string htmlBody)
    {
        if (_smtp.IsDevMode)
        {
            _log.LogInformation(
                "[EMAIL][DEV] To={to} Subject={subj}{nl}Body={body}",
                to, subject, Environment.NewLine, htmlBody);
            return true; // always succeed in dev
        }

        try
        {
            using var client = new SmtpClient(_smtp.Host, _smtp.Port)
            {
                EnableSsl      = _smtp.UseSsl,
                Credentials    = new NetworkCredential(_smtp.Username, _smtp.Password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout        = 15_000,
            };
            // mail.ru и некоторые другие SMTP серверы требуют явный charset
            // через заголовок Content-Type. SmtpClient добавит его автоматически
            // если BodyEncoding = UTF8 и используется AlternateView.

            using var msg = new MailMessage
            {
                From     = new MailAddress(_smtp.From, _smtp.FromName),
                Priority = MailPriority.Normal,
                // FIX: Subject кириллица — явная UTF-8 кодировка
                Subject  = subject,
                SubjectEncoding = Encoding.UTF8,
                // FIX: Body через AlternateView с charset=utf-8
                // SmtpClient.Body по умолчанию latin-1 → кириллица = "??????"
                // AlternateView с явным charset решает проблему для всех SMTP провайдеров
                BodyEncoding = Encoding.UTF8,
            };
            // HTML body с явным UTF-8 charset
            var htmlView = AlternateView.CreateAlternateViewFromString(
                htmlBody,
                Encoding.UTF8,
                "text/html");
            msg.AlternateViews.Add(htmlView);
            msg.To.Add(new MailAddress(to));

            await client.SendMailAsync(msg);
            _log.LogInformation("[EMAIL] ✓ Sent to {to}: {subject}", to, subject);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[EMAIL] ✗ Failed to send to {to}", to);
            return false;
        }
    }

    // ── Config resolution ──────────────────────────────────────────
    private static ResolvedSmtp Resolve(IConfiguration cfg)
    {
        var sec      = cfg.GetSection("Email");
        var provider = sec["Provider"]?.Trim() ?? "";
        var username = sec["Username"]?.Trim() ?? "";
        var password = sec["Password"]?.Trim() ?? "";
        var fromName = sec["FromName"]?.Trim() ?? "Vertex AI";
        var from     = sec["From"]?.Trim()
                       ?? (username.Contains("@") ? username : "");

        // Nothing configured → dev mode
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return ResolvedSmtp.DevMode;

        // Known provider → auto-resolve SMTP settings
        if (!string.IsNullOrEmpty(provider) && Presets.TryGetValue(provider, out var preset))
        {
            return new ResolvedSmtp(
                Host:         preset.Host,
                Port:         preset.Port,
                UseSsl:       preset.UseSsl,
                Username:     username,
                Password:     password,
                From:         string.IsNullOrEmpty(from) ? username : from,
                FromName:     fromName,
                ProviderName: provider,
                IsDevMode:    false);
        }

        // Custom SMTP — all fields must be provided
        var customHost = sec["SmtpHost"]?.Trim() ?? "";
        if (string.IsNullOrEmpty(customHost))
        {
            // No provider name matched, no custom host → dev mode
            return ResolvedSmtp.DevMode;
        }

        return new ResolvedSmtp(
            Host:         customHost,
            Port:         int.TryParse(sec["SmtpPort"], out var p) ? p : 587,
            UseSsl:       !string.Equals(sec["UseSsl"], "false", StringComparison.OrdinalIgnoreCase),
            Username:     username,
            Password:     password,
            From:         string.IsNullOrEmpty(from) ? username : from,
            FromName:     fromName,
            ProviderName: "custom",
            IsDevMode:    false);
    }

    // ── Internal models ────────────────────────────────────────────
    private record SmtpPreset(string Host, int Port, bool UseSsl);

    private record ResolvedSmtp(
        string Host, int Port, bool UseSsl,
        string Username, string Password,
        string From, string FromName,
        string ProviderName, bool IsDevMode)
    {
        public static readonly ResolvedSmtp DevMode = new(
            "", 0, false, "", "", "", "", "dev", IsDevMode: true);
    }

    // ── Email templates ────────────────────────────────────────────
    private static string BuildVerificationEmail(string name, string code) => $"""
        <!DOCTYPE html><html><head><meta charset="utf-8"></head>
        <body style="background:#050a12;margin:0;padding:40px 20px;font-family:'Inter',sans-serif">
          <div style="max-width:480px;margin:0 auto;background:#0a1220;
                      border:1px solid #1a2e44;border-radius:12px;overflow:hidden">
            <div style="background:linear-gradient(135deg,#0f2040,#0a1220);
                        padding:28px 32px;border-bottom:1px solid #1a2e44;text-align:center">
              <div style="font-size:24px;font-weight:900;color:#f0f6ff">
                VERTEX <span style="color:#38bdf8">AI</span>
              </div>
              <div style="font-size:12px;color:#334e68;margin-top:4px">Autonomous Trading Intelligence</div>
            </div>
            <div style="padding:32px">
              <p style="font-size:16px;font-weight:700;color:#f0f6ff;margin:0 0 8px">Привет, {name}!</p>
              <p style="font-size:13px;color:#6e90b2;margin:0 0 24px;line-height:1.6">
                Для завершения регистрации введи код подтверждения:
              </p>
              <div style="background:#050a12;border:1px solid #1e3050;border-radius:8px;
                          padding:20px;text-align:center;margin-bottom:24px">
                <div style="font-size:42px;font-weight:900;color:#38bdf8;
                            letter-spacing:14px;font-family:monospace">{code}</div>
                <div style="font-size:11px;color:#334e68;margin-top:8px">Код действителен 15 минут</div>
              </div>
              <p style="font-size:12px;color:#334e68;margin:0;line-height:1.6">
                Если ты не регистрировался — просто проигнорируй это письмо.
              </p>
            </div>
            <div style="background:#050a12;padding:14px 32px;border-top:1px solid #1a2e44;
                        text-align:center;font-size:11px;color:#1e3050">
              Vertex AI · Autonomous Crypto Trading
            </div>
          </div>
        </body></html>
        """;

    private static string BuildWelcomeEmail(string name) => $"""
        <!DOCTYPE html><html><head><meta charset="utf-8"></head>
        <body style="background:#050a12;margin:0;padding:40px 20px;font-family:'Inter',sans-serif">
          <div style="max-width:480px;margin:0 auto;background:#0a1220;
                      border:1px solid #1a2e44;border-radius:12px;overflow:hidden">
            <div style="background:linear-gradient(135deg,#0f2040,#0a1220);
                        padding:28px 32px;border-bottom:1px solid #1a2e44;text-align:center">
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
              <ul style="color:#94a3b8;font-size:13px;line-height:2.2;padding-left:18px;margin:0 0 20px">
                <li>🎮 <strong style="color:#fb923c">Demo-режим</strong> — $10,000 USDT на живых ценах Binance</li>
                <li>🤖 AI-сигналы в реальном времени на 53 символах</li>
                <li>📊 Multi-Chart с управлением позициями</li>
                <li>🛡️ Риск-менеджмент prop-desk уровня</li>
              </ul>
            </div>
            <div style="background:#050a12;padding:14px 32px;border-top:1px solid #1a2e44;
                        text-align:center;font-size:11px;color:#1e3050">
              Vertex AI · Autonomous Crypto Trading
            </div>
          </div>
        </body></html>
        """;
}
