using Microsoft.AspNetCore.Components.Server.Circuits;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Each browser circuit sees only its own user. Demo books and live keys
/// are chosen from this id, so two logged-in clients cannot trade each other.
/// </summary>
public sealed class UserCircuitHandler : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
        => async context =>
        {
            var auth = context.Services.GetRequiredService<AuthSessionService>();
            var id = auth.CurrentClient?.Id;
            DemoAccountService.SetAmbientClient(id);
            TradingCredentialStore.SetAmbientClient(id);
            try
            {
                await next(context);
            }
            finally
            {
                DemoAccountService.ClearAmbientClient();
                TradingCredentialStore.ClearAmbientClient();
            }
        };
}
