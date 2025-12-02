using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Console UI v3.0 – умный форматтер:
    /// - авто-детект Unicode/Emoji
    /// - fallback на ASCII, если эмодзи не поддерживаются
    /// - те же публичные методы, что и раньше
    /// </summary>
    public static class ConsoleReportFormatter
    {
        // ============================================================
        // CAPABILITIES DETECTION
        // ============================================================

        static ConsoleReportFormatter()
        {
            SupportsUnicode = CheckUnicode();
            SupportsEmoji = CheckEmoji();
        }

        /// <summary>Поддерживает ли консоль UTF-8 / Unicode.</summary>
        public static bool SupportsUnicode { get; }

        /// <summary>Поддерживает ли консоль Emoji (примерно).</summary>
        public static bool SupportsEmoji { get; }

        private static bool CheckUnicode()
        {
            try
            {
                // 65001 – UTF-8
                return Console.OutputEncoding.CodePage == 65001;
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckEmoji()
        {
            if (!SupportsUnicode)
                return false;

            try
            {
                // Лёгкий тест – выводим и сразу затираем один символ.
                var left = Console.CursorLeft;
                var top = Console.CursorTop;

                Console.Write("📈");
                Console.SetCursorPosition(left, top);
                Console.Write(' ');
                Console.SetCursorPosition(left, top);

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ============================================================
        // COLORS / STYLES
        // ============================================================
        private const string Reset = "\u001b[0m";
        private const string Bold = "\u001b[1m";

        private const string Green = "\u001b[32m";
        private const string Red = "\u001b[31m";
        private const string Yellow = "\u001b[33m";
        private const string Cyan = "\u001b[36m";
        private const string Magenta = "\u001b[35m";
        private const string Gray = "\u001b[90m";
        private const string White = "\u001b[97m";

        private const string BgGreen = "\u001b[42m";
        private const string BgRed = "\u001b[41m";
        private const string BgBlue = "\u001b[44m";

        // ============================================================
        // LINE / TITLES HELPERS
        // ============================================================

        private static string Line =>
            SupportsUnicode
                ? "═══════════════════════════════════════════════════════════"
                : "-----------------------------------------------------------";

        private static string Icon(string emoji, string ascii)
            => SupportsEmoji ? emoji : ascii;

        private static string SectionTitle(string emojiTitle, string asciiTitle)
            => SupportsEmoji ? emojiTitle : asciiTitle;

        private static string Section(string title) =>
            $"\n{Cyan}{Line}\n{White}{Bold}  {title}{Reset}\n{Cyan}{Line}{Reset}\n";

        // ============================================================
        // SMALL HELPERS: BAR / SPARKLINE
        // ============================================================

        /// <summary>
        /// Прогресс-бар вида [██████░░░░] или [######....] (fallback)
        /// по значению 0..1
        /// </summary>
        private static string Bar(decimal value, int width = 20)
        {
            if (value < 0) value = 0;
            if (value > 1) value = 1;

            if (!SupportsUnicode)
            {
                int filled = (int)Math.Round((double)value * width);
                int empty = width - filled;
                return "[" + new string('#', filled) + new string('.', empty) + "]";
            }

            int filledBlocks = (int)Math.Round((double)value * width);
            int emptyBlocks = width - filledBlocks;
            return new string('█', filledBlocks) + new string('░', emptyBlocks);
        }

        /// <summary>
        /// Мини-спарклайн для массива значений.
        /// Если Unicode недоступен — просто текст-заглушка.
        /// </summary>
        public static string Sparkline(IReadOnlyList<decimal> values)
        {
            if (values == null || values.Count == 0)
                return string.Empty;

            if (!SupportsUnicode)
                return "[spark disabled]";

            decimal min = decimal.MaxValue;
            decimal max = decimal.MinValue;

            foreach (var v in values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            if (max - min == 0)
                return new string('─', values.Count);

            // Юникод блоки разной высоты
            char[] blocks = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };
            var chars = new char[values.Count];

            for (int i = 0; i < values.Count; i++)
            {
                var norm = (double)((values[i] - min) / (max - min)); // 0..1
                int idx = (int)Math.Round(norm * (blocks.Length - 1));
                if (idx < 0) idx = 0;
                if (idx >= blocks.Length) idx = blocks.Length - 1;
                chars[i] = blocks[idx];
            }

            return new string(chars);
        }

        // ============================================================
        // SIGNAL REPORT (тот же метод, что был – только UI умнее)
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
            var sideUpper = side?.ToUpperInvariant() ?? string.Empty;
            var sideColor = sideUpper == "LONG" ? Green : Red;

            var title = SectionTitle(
                "🚀 СИГНАЛ ТОРГОВОЙ СИСТЕМЫ",
                "СИГНАЛ ТОРГОВОЙ СИСТЕМЫ");

            var msg =
                $"{Section(title)}" +
                $"{White}{Bold}{symbol}{Reset} | TF {timeframe} | {sideColor}{Bold}{sideUpper}{Reset}\n\n" +

                $"{Gray}ENTRY:{Reset} {White}{entry:F4}{Reset}\n" +
                $"{Gray}STOP LOSS:{Reset} {Red}{sl:F4}{Reset}\n" +
                $"{Gray}ATR:{Reset} {White}{atr:F6}{Reset}\n\n" +

                $"{Gray}TARGETS:{Reset}\n" +
                $"  • TP1 → {Green}{tp1:F4}{Reset}\n" +
                $"  • TP2 → {Green}{tp2:F4}{Reset}\n" +
                $"  • TP3 → {Green}{tp3:F4}{Reset}\n";

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
            var status = d.Allow
                ? $"{BgGreen}{White}{Bold}   {Icon("ВХОД РАЗРЕШЁН", "ENTRY ALLOWED")}   {Reset}"
                : $"{BgRed}{White}{Bold}   {Icon("ВХОД ЗАПРЕЩЁН", "ENTRY BLOCKED")}   {Reset}";

            decimal scoreNorm = d.Score;
            if (scoreNorm < 0) scoreNorm = 0;
            if (scoreNorm > 1) scoreNorm = 1;

            var title = SectionTitle(
                "🤖 AI АНАЛИЗ РЕШЕНИЯ",
                "AI АНАЛИЗ РЕШЕНИЯ");

            var msg =
                $"{Section(title)}" +
                $"{White}{symbol}{Reset} [{timeframe}] → {status}\n\n" +

                $"{Gray}Grade:{Reset}   {White}{d.Grade}{Reset}\n" +
                $"{Gray}Score:{Reset}   {White}{d.Score:F2}{Reset}   {Bar(scoreNorm)}\n" +
                $"{Gray}ATR%:{Reset}    {White}{d.AtrPct:P2}{Reset}\n" +
                $"{Gray}Trend:{Reset}   {White}{d.Trend}{Reset}\n" +
                $"{Gray}Body/ATR:{Reset} {White}{d.BodyAtr:F2}{Reset}\n" +
                $"{Gray}R/R:{Reset}      {White}{d.Rr:F2}{Reset}\n\n" +

                $"{Gray}Манипуляция:{Reset} {Yellow}{d.Manipulation}{Reset}\n" +
                $"{Gray}Super-signal:{Reset} {Cyan}{d.SuperSignal}{Reset}\n" +
                $"{Gray}Комментарий:{Reset} {White}{d.Reason}{Reset}\n";

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
                MarketRegime.StrongUpTrend => $"{Green}{Icon("📈 Сильный восходящий тренд", "[UP] Сильный восходящий тренд")}{Reset}",
                MarketRegime.StrongDownTrend => $"{Red}{Icon("📉 Сильный нисходящий тренд", "[DOWN] Сильный нисходящий тренд")}{Reset}",
                MarketRegime.Range => $"{Cyan}{Icon("🔹 Диапазон (флэт)", "[RANGE] Диапазон (флэт)")}{Reset}",
                MarketRegime.VolatileChop => $"{Yellow}{Icon("⚡ Пилообразный рынок", "[CHOP] Пилообразный рынок")}{Reset}",
                _ => $"{Gray}{Icon("❓ Не определён", "[?] Не определён")}{Reset}"
            };

            var title = SectionTitle(
                "📊 РЫНОЧНЫЙ РЕЖИМ",
                "РЫНОЧНЫЙ РЕЖИМ");

            var msg =
                $"{Section(title)}" +
                $"{White}{symbol}{Reset} [{timeframe}]\n" +
                $"{icon}\n\n" +
                $"{Gray}Наклон тренда:{Reset} {White}{r.TrendSlopePercent:P2}{Reset}\n" +
                $"{Gray}Волатильность:{Reset} {White}{r.VolatilityPercent:P2}{Reset}\n" +
                $"{Gray}Девиация:{Reset} {White}{r.DeviationScore:F2}{Reset}\n";

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
            var ratio = volume / Math.Max(1, avgVolume);

            var title = SectionTitle(
                "⚠ ЛИКВИДНОСТЬ",
                "ЛИКВИДНОСТЬ");

            var msg =
                $"{Section(title)}" +
                $"{Yellow}{Bold}{Icon("Сделка заблокирована из-за низкой ликвидности",
                                      "Сделка заблокирована: низкая ликвидность")}{Reset}\n\n" +
                $"{Gray}Символ:{Reset} {White}{symbol}{Reset}  [{timeframe}]\n" +
                $"{Gray}Объём:{Reset}     {White}{volume:N0}{Reset}\n" +
                $"{Gray}Средний:{Reset}   {White}{avgVolume:N0}{Reset}\n" +
                $"{Gray}Отношение:{Reset} {White}{ratio:P1}{Reset}\n";

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
            decimal riskPct = depo > 0 ? riskUsd / depo : 0;
            if (riskPct < 0) riskPct = 0;
            if (riskPct > 1) riskPct = 1;

            var title = SectionTitle(
                "💰 РИСК-МЕНЕДЖМЕНТ (v5.0 SMART)",
                "РИСК-МЕНЕДЖМЕНТ (v5.0 SMART)");

            var msg =
                $"{Section(title)}" +
                $"{White}{symbol}{Reset}\n\n" +
                $"{Gray}Размер позиции:{Reset} {White}{qty:F6}{Reset}\n" +
                $"{Gray}Нотионал:{Reset}      {White}{notional:F2} USDT{Reset}\n" +
                $"{Gray}Риск по SL:{Reset}    {White}{riskUsd:F2} USDT{Reset} (dist {slDist:F4})\n" +
                $"{Gray}Риск % от депо:{Reset} {White}{riskPct:P2}{Reset}  {Bar(riskPct)}\n\n" +
                $"{Gray}Плечо:{Reset}         {White}x{lev}{Reset}\n" +
                $"{Gray}Депозит:{Reset}       {White}{depo:F2}{Reset}\n" +
                $"{Gray}Max Notional cfg:{Reset} {White}{maxNotionalCfg:F2}{Reset}\n" +
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
            var title = SectionTitle(
                "🎯 ПОДГОТОВКА ВХОДА",
                "ПОДГОТОВКА ВХОДА");

            var msg =
                $"{Section(title)}" +
                $"{White}{symbol}{Reset} [{side}]\n\n" +
                $"{Gray}Entry:{Reset} {White}{entry:F4}{Reset}\n" +
                $"{Gray}SL:{Reset}    {Red}{slTrig:F4}{Gray} / {Red}{slLim:F4}{Reset}\n" +
                $"{Gray}Qty:{Reset}   {White}{qty:F6}{Reset}\n" +
                $"{Gray}Step:{Reset}  {White}{step:F6}{Reset}    " +
                $"{Gray}Tick:{Reset} {White}{tick:F6}{Reset}\n";

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
            var title = SectionTitle(
                "🟢 ВХОД УСПЕШНО ВЫПОЛНЕН",
                "ВХОД УСПЕШНО ВЫПОЛНЕН");

            var banner = Icon("ПОЗИЦИЯ ОТКРЫТА", "POSITION OPENED");

            var msg =
                $"{Section(title)}" +
                $"{BgBlue}{White}{Bold}   {banner}   {Reset}\n\n" +
                $"{White}{symbol}{Reset}\n\n" +
                $"{Gray}Цена входа:{Reset} {Green}{price:F4}{Reset}\n" +
                $"{Gray}Объём:{Reset}      {White}{qty:F6}{Reset}\n" +
                $"{Gray}Попытка:{Reset}    {White}{attempt}{Reset}\n";

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
                $"{Yellow}{Icon("🔁 Binance отклонил ордер по", "[RETRY] Binance отклонил ордер по")} {symbol}{Reset}\n" +
                $"{Gray}Код:{Reset} {White}{code}{Reset}\n" +
                $"{Gray}Причина:{Reset} {White}{message}{Reset}\n" +
                $"{Gray}Новый QTY:{Reset} {White}{qty:F6}{Reset}\n" +
                $"{Gray}Попытка:{Reset} {White}{attempt}/{maxAttempts}{Reset}\n");
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
                $"{Green}{Icon("🟩 ПОЗИЦИЯ ОТКРЫТА", "[OPEN] ПОЗИЦИЯ ОТКРЫТА")} {White}{symbol}{Reset}\n" +
                $"{Gray}Side:{Reset} {White}{side}{Reset}\n" +
                $"{Gray}Qty:{Reset}  {White}{qty:F6}{Reset}\n");
        }

        // ============================================================
        // SL / TP
        // ============================================================
        public static void TPPlaced(ILogger logger, int index, decimal price, decimal qty)
        {
            logger.LogInformation(
                $"{Green}{Icon("🎯", "TP")} TP{index} установлен → {White}{price:F4}{Reset}, qty={White}{qty:F6}{Reset}");
        }

        public static void SLPlaced(ILogger logger, decimal trigger, decimal limit, decimal qty)
        {
            logger.LogInformation(
                $"{Magenta}{Icon("🛡", "SL")} STOP-LOSS → trg={White}{trigger:F4}{Reset}, lim={White}{limit:F4}{Reset}, qty={White}{qty:F6}{Reset}");
        }

        // ============================================================
        // ENTRY FAIL (жёсткий фейл)
        // ============================================================
        public static void EntryFailedHard(ILogger logger, string symbol, string error)
        {
            logger.LogError(
                $"{Red}{Bold}{Icon("🔴", "[ERROR]")} Ошибка входа {symbol}{Reset}\n" +
                $"{Gray}Причина:{Reset} {White}{error}{Reset}");
        }
    }
}
