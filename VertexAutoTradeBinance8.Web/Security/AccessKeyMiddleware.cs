using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VertexAutoTradeBinance8.Web.Security;

/// <summary>
/// Простая защита терминала: если задан Web:AccessKey (env Web__AccessKey),
/// все запросы требуют cookie, выдаваемую после ввода ключа на /login.
/// Терминал управляет реальной торговлей (ON/OFF, риск) — открытым его оставлять нельзя.
/// </summary>
public sealed class AccessKeyMiddleware
{
    private const string Cookie = "vx_auth";
    private readonly RequestDelegate _next;
    private readonly string? _key;
    private readonly string? _token;

    public AccessKeyMiddleware(RequestDelegate next, IConfiguration cfg)
    {
        _next = next;
        _key = cfg["Web:AccessKey"];
        if (!string.IsNullOrWhiteSpace(_key))
            _token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("vertex::" + _key)));
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (_token == null) { await _next(ctx); return; }

        var path = ctx.Request.Path.Value ?? "/";
        if (path.StartsWith("/css/") || path.StartsWith("/favicon") || path.StartsWith("/img/"))
        {
            await _next(ctx); return;
        }

        if (path.Equals("/login", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                var form = await ctx.Request.ReadFormAsync();
                var given = form["key"].ToString();
                if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(_key!)))
                {
                    ctx.Response.Cookies.Append(Cookie, _token, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Secure = ctx.Request.IsHttps,
                        MaxAge = TimeSpan.FromDays(14)
                    });
                    ctx.Response.Redirect("/");
                    return;
                }
                await WriteLogin(ctx, "Неверный ключ доступа");
                return;
            }
            await WriteLogin(ctx, null);
            return;
        }

        if (ctx.Request.Cookies.TryGetValue(Cookie, out var c) && c == _token)
        {
            await _next(ctx); return;
        }

        ctx.Response.Redirect("/login");
    }

    private static Task WriteLogin(HttpContext ctx, string? error)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var err = error == null ? "" : $"<div class=\"vt-toast err\">{WebUtility.HtmlEncode(error)}</div>";
        return ctx.Response.WriteAsync($$"""
<!DOCTYPE html><html lang="ru"><head><meta charset="utf-8"/><meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Vertex · Login</title><link rel="stylesheet" href="/css/vertex-ui.css"/>
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;600;700&family=JetBrains+Mono:wght@500;600&display=swap" rel="stylesheet">
<style>.wrap{min-height:100vh;display:grid;place-items:center}.box{width:360px}</style></head>
<body><div class="wrap"><form method="post" class="vt-card box">
<div class="vt-row" style="margin-bottom:18px"><div class="vt-brand-mark">V</div><div><b style="letter-spacing:.14em">VERTEX</b><div class="dim" style="font-size:10px;letter-spacing:.2em">TERMINAL ACCESS</div></div></div>
{{err}}<label class="vt-label">Access key</label><input class="vt-input" type="password" name="key" autofocus autocomplete="current-password"/>
<button class="vt-btn primary" style="width:100%;margin-top:16px" type="submit">Войти</button>
<div class="vt-hint">Ключ задаётся переменной окружения <span class="mono">Web__AccessKey</span>.</div></form></div></body></html>
""");
    }
}
