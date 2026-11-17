using Microsoft.AspNetCore.Components.Server.Circuits;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Each browser circuit sees only its own user. Demo books and live keys
/// are chosen from this id, so two logged-in clients cannot trade each other.
/// The handler is scoped, so the injected session is this circuit's session.
/// </summary>
public sealed class UserCircuitHandler : CircuitHandler
{
    private readonly AuthSessionService _auth;

    public UserCircuitHandler(AuthSessionService auth)
    {
        _auth = auth;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
        => async _ =>
        {
            var id = _auth.CurrentClient?.Id;
            DemoAccountService.SetAmbientClient(id);
            TradingCredentialStore.SetAmbientClient(id);
            try
            {
                await next(_);
            }
            finally
            {
                DemoAccountService.ClearAmbientClient();
                TradingCredentialStore.ClearAmbientClient();
            }
        };
}
