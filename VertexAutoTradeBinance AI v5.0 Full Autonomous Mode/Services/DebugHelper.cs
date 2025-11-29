using Binance.Net.Enums;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services;

public static class DebugHelper
{
    public static bool Enabled = true;  // глобальное включение

    public static void Log(ILogger logger, string symbol, KlineInterval tf, string stage, string msg)
    {
        if (!Enabled) return;

        logger.LogInformation(
            $"\n[DEBUG][{symbol}][{tf}] ► {stage}\n    → {msg}\n");
    }

    public static void Section(ILogger logger, string symbol, KlineInterval tf, string title)
    {
        if (!Enabled) return;

        logger.LogInformation(
            $"\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            $"[DEBUG][{symbol}][{tf}] {title}\n" +
            $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    }
}
