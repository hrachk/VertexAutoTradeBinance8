using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace VertexAutoTradeBinance8.API.Security;

/// <summary>
/// Защита торговых эндпоинтов: заголовок X-Vertex-Token должен совпадать с Api:Token
/// (env: Api__Token). Если токен не задан — эндпоинт полностью отключён (403).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireApiTokenAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var cfg = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = cfg["Api:Token"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 24)
        {
            context.Result = new ObjectResult(new { error = "Trading API disabled: set Api__Token (>=24 chars)" }) { StatusCode = 403 };
            return;
        }

        var got = context.HttpContext.Request.Headers["X-Vertex-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(expected)))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "invalid token" });
            return;
        }

        await next();
    }
}
