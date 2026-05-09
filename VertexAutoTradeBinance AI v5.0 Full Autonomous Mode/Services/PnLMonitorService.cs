using System.Text;
//TODO:
namespace VertexAutoTradeBinance8.Services
{
    public class PnLMonitorService
    {
        private readonly ILogger<PnLMonitorService> _logger;
        private readonly BinanceClientFactory _factory;

        private readonly List<decimal> _pnlHistory = new();   // история PnL (delta от старта)
        private decimal? _startEquity;                        // equity в момент старта
        private DateTime _lastUpdate = DateTime.MinValue;

        // как часто обновлять график
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

        // ANSI цвета (как в ConsoleReportFormatter)
        private const string Reset = "\u001b[0m";
        private const string Green = "\u001b[32m";
        private const string Red = "\u001b[31m";
        private const string Cyan = "\u001b[36m";
        private const string Gray = "\u001b[90m";

        public PnLMonitorService(
            ILogger<PnLMonitorService> logger,
            BinanceClientFactory factory)
        {
            _logger = logger;
            _factory = factory;
        }

        /// <summary>
        /// Вызывается периодически из TradingWorker.
        /// Тянет фьючерсный баланс и рисует ASCII-график PnL.
        /// </summary>
        public async Task TickAsync(CancellationToken ct = default)
        {
            if (DateTime.UtcNow - _lastUpdate < _interval)
                return;

            _lastUpdate = DateTime.UtcNow;

            decimal equity;

            try
            {
                using var client = _factory.CreateRestClient();

                var acc = await client.UsdFuturesApi.Account.GetAccountInfoV3Async();
                if (!acc.Success || acc.Data == null)
                {
                    _logger.LogWarning("PnLMonitor: cannot load account info: {Err}", acc.Error);
                    return;
                }

                var usdt = acc.Data.Assets.FirstOrDefault(a => a.Asset == "USDT");
                if (usdt == null)
                {
                    _logger.LogWarning("PnLMonitor: USDT asset not found");
                    return;
                }

                // Вариант: используем MarginBalance (equity) или WalletBalance + UnrealizedPnl
                // Подправишь под реальные поля модели, если названия чуть отличаются
                equity = usdt.MarginBalance;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PnLMonitor: exception while loading equity");
                return;
            }

            if (_startEquity == null)
                _startEquity = equity;

            var pnl = equity - _startEquity.Value;

            _pnlHistory.Add(pnl);
            if (_pnlHistory.Count > 80) // ограничиваем длину
                _pnlHistory.RemoveAt(0);

            string chart = BuildSparkline(_pnlHistory);

            var color = pnl >= 0 ? Green : Red;
            var sign = pnl >= 0 ? "+" : "-";

            _logger.LogInformation(
                $"\n{Cyan}========= REAL-TIME PnL ========={Reset}\n" +
                $"{Gray}Equity:{Reset} {equity:F2} USDT\n" +
                $"{Gray}PnL от старта:{Reset} {color}{sign}{Math.Abs(pnl):F2} USDT{Reset}\n" +
                $"{Gray}История ({_pnlHistory.Count} точек):{Reset}\n" +
                $"{chart}\n" +
                $"{Cyan}================================={Reset}\n");
        }

        /// <summary>
        /// Строим горизонтальный ASCII/Unicode "спарклайн" по истории PnL.
        /// </summary>
        private static string BuildSparkline(IReadOnlyList<decimal> values)
        {
            if (values.Count == 0)
                return string.Empty;

            var min = values.Min();
            var max = values.Max();

            // все значения одинаковые → просто линия
            if (min == max)
                return new string('-', values.Count);

            // Юникод-спарклайн (формально не чистый ASCII, но консоль ест и выглядит топово)
            const string levels = "▁▂▃▄▅▆▇█";
            int n = levels.Length;

            var sb = new StringBuilder(values.Count + 10);

            foreach (var v in values)
            {
                var norm = (double)((v - min) / (max - min)); // 0..1
                var idx = (int)Math.Round(norm * (n - 1));
                if (idx < 0) idx = 0;
                if (idx >= n) idx = n - 1;

                sb.Append(levels[idx]);
            }

            return sb.ToString();
        }
    }
}
