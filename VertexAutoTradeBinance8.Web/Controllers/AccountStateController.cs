using Binance.Net.Enums;
using Microsoft.AspNetCore.Mvc;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Interface;

namespace VertexAutoTradeBinance8.API.Controllers
{
    [ApiController]
    [Route("api/state")]
    public sealed class AccountStateController : ControllerBase
    {
        private readonly IAccountStateService _state;

        public AccountStateController(IAccountStateService state)
        {
            _state = state;
        }
        [HttpGet("balance")]


        public ActionResult<AccountBalanceState> GetBalance()
        {
            var b = _state.GetBalance();
            return b != null ? Ok(b) : Ok(new AccountBalanceState());
        }

        [HttpGet("realized-pnl-today")]
        public ActionResult<decimal> GetRealizedPnlToday() => Ok(_state.GetRealizedPnlToday());


        /// <summary>
        /// GET /api/state/positions?symbols=BTCUSDT,ETHUSDT
        /// </summary>
        [HttpGet("positions")]
        public ActionResult<List<LivePositionState>> GetPositions([FromQuery] string? symbols)
            => Ok(_state.GetPositions(symbols).ToList());
    }
}
