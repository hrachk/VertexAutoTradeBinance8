using System;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public static class ConsoleReportFormatter
    {
        // Colors
        private const string Reset = "\u001b[0m";
        private const string Green = "\u001b[32m";
        private const string Red = "\u001b[31m";
        private const string Yellow = "\u001b[33m";
        private const string Cyan = "\u001b[36m";
        private const string Magenta = "\u001b[35m";
        private const string Gray = "\u001b[90m";
        private const string White = "\u001b[97m";

        private const string Line =
            "═══════════════════════════════════════════════════════════";

        private static string Section(string title) =>
            $"\n{Cyan}{Line}\n {title}\n{Line}{Reset}\n";


        // ============================================================
        // SIGNAL REPORT
        // ============================================================
        public static void StrategySignal(
            ILogger logger,
            string symbol,
            string timeframe,
            string side,
            decimal entry,
            decimal sl,
            decimal tp1,
            decimal tp2,
            decimal tp3,
            decimal atr)
        {
            var sideColor = side.ToUpper() == "LONG" ? Green : Red;

            var msg =
                $"{Section("📌 СИГНАЛ")}" +
                $"{White}{symbol}{Reset} | {timeframe} | {sideColor}{side}{Reset}\n\n" +
                $"{Gray}Entry:{Reset} {entry:F4}    " +
                $"{Gray}SL:{Reset} {sl:F4}    " +
                $"{Gray}ATR:{Reset} {atr:F6}\n" +
                $"{Gray}TP:{Reset} {tp1:F4} / {tp2:F4} / {tp3:F4}\n";

            logger.LogInformation(msg);
        }


        // ============================================================
        // AI REPORT
        // ============================================================
        public static void AiDecisionReport(
            ILogger logger,
            string symbol,
            string timeframe,
            AiDecision d)
        {
            var status = d.Allow ? $"{Green}✔ РАЗРЕШЕНО" : $"{Yellow}⚠ ОТКЛОНЕНО";

            var msg =
                $"{Section("🤖 AI АНАЛИЗ")}" +
                $"{White}{symbol}{Reset} [{timeframe}] → {status}{Reset}\n\n" +
                $"{Gray}Класс:{Reset} {d.Grade}\n" +
                $"{Gray}Score:{Reset} {d.Score:F2}\n" +
                $"{Gray}ATR%:{Reset} {d.AtrPct:P2}\n" +
                $"{Gray}Тренд:{Reset} {d.Trend}\n" +
                $"{Gray}Body/ATR:{Reset} {d.BodyAtr:F2}\n" +
                $"{Gray}R/R:{Reset} {d.Rr:F2}\n" +
                $"{Gray}Манипуляции:{Reset} {d.Manipulation}\n" +
                $"{Gray}Супер-сигнал:{Reset} {d.SuperSignal}\n" +
                $"{Gray}Комментарий:{Reset} {d.Reason}\n";

            logger.LogInformation(msg);
        }


        // ============================================================
        // MARKET REGIME
        // ============================================================
        public static void MarketRegimeReport(
            ILogger logger,
            string symbol,
            string timeframe,
            MarketRegimeResult r)
        {
            string icon = r.Regime switch
            {
                MarketRegime.StrongUpTrend => "📈 Сильный восходящий тренд",
                MarketRegime.StrongDownTrend => "📉 Сильный нисходящий тренд",
                MarketRegime.Range => "🔹 Диапазон (флэт)",
                MarketRegime.VolatileChop => "⚡ Пилообразный рынок",
                _ => "❓ Не определён"
            };

            var msg =
                $"{Section("📊 РЫНОЧНЫЙ РЕЖИМ")}" +
                $"{White}{symbol}{Reset} [{timeframe}]\n" +
                $"{icon}\n\n" +
                $"{Gray}Наклон:{Reset} {r.TrendSlopePercent:P2}\n" +
                $"{Gray}Волатильность:{Reset} {r.VolatilityPercent:P2}\n" +
                $"{Gray}Девиация:{Reset} {r.DeviationScore:F2}\n";

            logger.LogInformation(msg);
        }


        // ============================================================
        // LIQUIDITY BLOCK
        // ============================================================
        public static void LiquidityBlocked(
            ILogger logger,
            string symbol,
            string timeframe,
            decimal volume,
            decimal avgVolume)
        {
            var msg =
                $"{Section("⚠ ЛИКВИДНОСТЬ")}" +
                $"{Yellow}Сделка заблокирована из-за низкой ликвидности{Reset}\n\n" +
                $"{Gray}Объём:{Reset} {volume:N0}\n" +
                $"{Gray}Средний:{Reset} {avgVolume:N0}\n" +
                $"{Gray}Отношение:{Reset} {(volume / Math.Max(1, avgVolume)):P1}\n";

            logger.LogWarning(msg);
        }


        // ============================================================
        // RISK REPORT
        // ============================================================
        public static void RiskReport(
            ILogger logger,
            string symbol,
            decimal qty,
            decimal notional,
            decimal riskUsd,
            decimal slDist,
            decimal lev,
            decimal depo,
            decimal maxNotionalCfg,
            decimal step,
            decimal minQty,
            decimal minNotional)
        {
            var msg =
                $"{Section("💰 РИСК-МЕНЕДЖМЕНТ")}" +
                $"{White}{symbol}{Reset}\n\n" +
                $"{Gray}Размер позиции:{Reset} {qty:F6}\n" +
                $"{Gray}Нотионал:{Reset} {notional:F2} USDT\n" +
                $"{Gray}Риск по SL:{Reset} {riskUsd:F2} USDT (dist {slDist:F4})\n" +
                $"{Gray}Плечо:{Reset} x{lev}\n" +
                $"{Gray}Депозит:{Reset} {depo:F2}\n" +
                $"{Gray}Max Notional cfg:{Reset} {maxNotionalCfg:F2}\n" +
                $"{Gray}Фильтры:{Reset} step={step:F6}, minQty={minQty:F6}, minNotional={minNotional:F2}\n";

            logger.LogInformation(msg);
        }


        // ============================================================
        // ENTRY PREP
        // ============================================================
        public static void EntryPrep(
            ILogger logger,
            string symbol,
            string side,
            decimal entry,
            decimal slTrig,
            decimal slLim,
            decimal qty,
            decimal step,
            decimal tick)
        {
            var msg =
                $"{Section("📈 ПОДГОТОВКА ВХОДА")}" +
                $"{White}{symbol}{Reset} [{side}]\n\n" +
                $"{Gray}Entry:{Reset} {entry:F4}\n" +
                $"{Gray}SL:{Reset} {slTrig:F4} / {slLim:F4}\n" +
                $"{Gray}Qty:{Reset} {qty:F6}\n" +
                $"{Gray}Step:{Reset} {step:F6}   Tick:{tick:F6}\n";

            logger.LogInformation(msg);
        }


        // ============================================================
        // ENTRY SUCCESS
        // ============================================================
        public static void EntrySuccess(
            ILogger logger,
            string symbol,
            decimal qty,
            decimal price,
            int attempt)
        {
            var msg =
                $"{Section("🟢 ВХОД ВЫПОЛНЕН")}" +
                $"{White}{symbol}{Reset}\n\n" +
                $"{Gray}Цена:{Reset} {price:F4}\n" +
                $"{Gray}Объём:{Reset} {qty:F6}\n" +
                $"{Gray}Попытка:{Reset} {attempt}\n";

            logger.LogInformation(msg);
        }


        // ============================================================
        // FALLBACK ATTEMPT (НЕ УДАЛЁН)
        // ============================================================
        public static void EntryFallbackAttempt(
            ILogger logger,
            string symbol,
            long? code,
            string message,
            decimal qty,
            int attempt,
            int maxAttempts)
        {
            logger.LogWarning(
                $"{Yellow}🔁 Binance отклонил ордер по {symbol}{Reset}\n" +
                $"{Gray}Код:{Reset} {code}\n" +
                $"{Gray}Причина:{Reset} {message}\n" +
                $"{Gray}Новый QTY:{Reset} {qty:F6}\n" +
                $"{Gray}Попытка:{Reset} {attempt}/{maxAttempts}\n");
        }


        // ============================================================
        // POSITION OPENED (НЕ УДАЛЁН)
        // ============================================================
        public static void PositionOpened(
            ILogger logger,
            string symbol,
            string side,
            decimal qty)
        {
            logger.LogInformation(
                $"{Green}🟩 ПОЗИЦИЯ ОТКРЫТА {symbol}{Reset}\n" +
                $"{Gray}Side:{Reset} {side}\n" +
                $"{Gray}Qty:{Reset}  {qty:F6}\n");
        }


        // ============================================================
        // SL / TP
        // ============================================================
        public static void TPPlaced(ILogger logger, int index, decimal price, decimal qty)
        {
            logger.LogInformation($"{Green}📌 TP{index} → {price:F4}, qty={qty:F6}{Reset}");
        }

        public static void SLPlaced(ILogger logger, decimal trigger, decimal limit, decimal qty)
        {
            logger.LogInformation($"{Magenta}🛡 SL → trg={trigger:F4}, lim={limit:F4}, qty={qty:F6}{Reset}");
        }


        // ============================================================
        // ENTRY FAIL
        // ============================================================
        public static void EntryFailedHard(ILogger logger, string symbol, string error)
        {
            logger.LogError($"{Red}🔴 Ошибка входа {symbol}{Reset}\nПричина: {error}");
        }
    }
}
