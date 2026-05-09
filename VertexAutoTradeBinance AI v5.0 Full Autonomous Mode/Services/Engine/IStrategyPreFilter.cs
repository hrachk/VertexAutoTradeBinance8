using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Services.Engine
{
    public sealed record PreFilterResult(
        bool Allow,
        string Code,
        string Reason,
        int? SleepMs = null)
    {
        public static PreFilterResult Ok(string reason = "OK") =>
            new(true, "OK", reason);

        public static PreFilterResult Skip(string code, string reason, int? sleepMs = null) =>
            new(false, code, reason, sleepMs);
    }

    public interface IStrategyPreFilter
    {
        /// <summary>
        /// Быстрый pre-gate ДО загрузки klines и ДО StrategyEngine.GenerateSignal.
        /// Должен быть дешёвым: без REST, без heavy-аналитики.
        /// </summary>
        Task<PreFilterResult> EvaluateAsync(
            string symbol,
            KlineInterval tf,
            CancellationToken ct);
    }
}
