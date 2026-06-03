using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// AiTimeframeSelectorService v1.0
    /// ML-like фильтр доминирования таймфрейма.
    /// Оценивает структуру тренда, волатильность,
    /// качество импульса и шум — и выбирает лучший TF.
    /// </summary>
    public class AiTimeframeSelectorService
    {
        public enum DominantTF
        {
            OneMinute,
            FiveMinutes,
            FifteenMinutes,
            Both,
            None
        }

        public DominantTF SelectTF(
    MarketSnapshot oneM,
    MarketSnapshot fiveM,
    MarketSnapshot fifteenM)
        {
            if (oneM == null || fiveM == null || fifteenM == null)
                return DominantTF.None;

            // --- 0. Если 15m в сильном тренде → он главный (макро-направление)
            if (Math.Abs(fifteenM.TrendSlopePercent) > 0.01m
                && fifteenM.VolatilityPercent < 0.02m)
            {
                // Если 5m подтверждает 15m → используем 5m для входов
                if (Math.Sign(fiveM.TrendSlopePercent) == Math.Sign(fifteenM.TrendSlopePercent))
                    return DominantTF.FiveMinutes;

                // Если 5m против 15m → ждём (коррекция или разворот)
                return DominantTF.None;
            }

            // --- 1. Сильный выброс волатильности на 1m → шум, игнорируем
            if (oneM.VolatilityPercent > fiveM.VolatilityPercent * 2.2m)
                return DominantTF.FiveMinutes;

            // --- 2. Если тренд на 5m сильнее 1m → dominate 5m
            if (Math.Abs(fiveM.TrendSlopePercent) > Math.Abs(oneM.TrendSlopePercent) * 1.5m)
                return DominantTF.FiveMinutes;

            // --- 3. Все 3 TF совпадают по направлению → сильный тренд (используем 1m+5m)
            bool allAligned = Math.Sign(oneM.TrendSlopePercent) == Math.Sign(fiveM.TrendSlopePercent)
                           && Math.Sign(fiveM.TrendSlopePercent) == Math.Sign(fifteenM.TrendSlopePercent);

            if (allAligned && oneM.VolatilityPercent < fiveM.VolatilityPercent * 1.3m)
                return DominantTF.Both; // или можно добавить DominantTF.AllTimeframes

            // --- 4. Импульс 1m > 5m, но 15m не против → быстрый рынок (scalping zone)
            if (Math.Abs(oneM.TrendSlopePercent) > Math.Abs(fiveM.TrendSlopePercent) * 1.4m
                && oneM.VolatilityPercent < 0.015m
                && Math.Sign(oneM.TrendSlopePercent) == Math.Sign(fifteenM.TrendSlopePercent))
            {
                return DominantTF.OneMinute;
            }

            // --- 5. Флет на всех TF → выключаем торговлю
            // --- 5. Флет и шум → отключить оба
            if (Math.Abs(oneM.TrendSlopePercent) < 0.005m &&
                Math.Abs(fiveM.TrendSlopePercent) < 0.005m)
                return DominantTF.None;

            // Контрольный fallback
            return DominantTF.FiveMinutes;

        }
    }
}
