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
            Both,
            None
        }

        public DominantTF SelectTF(
            MarketSnapshot oneM,
            MarketSnapshot fiveM)
        {
            if (oneM == null || fiveM == null)
                return DominantTF.None;

            // --- 1. Сильный выброс волатильности на 1m → шум, отключаем
            if (oneM.VolatilityPercent > fiveM.VolatilityPercent * 2.2m)
                return DominantTF.FiveMinutes;

            // --- 2. Если тренд на 5m сильнее → dominate 5m
            if (Math.Abs(fiveM.TrendSlopePercent) > Math.Abs(oneM.TrendSlopePercent) * 1.5m)
                return DominantTF.FiveMinutes;

            // --- 3. Если 1m и 5m совпадают по направлению — оба сильные
            if (Math.Sign(oneM.TrendSlopePercent) == Math.Sign(fiveM.TrendSlopePercent))
            {
                // но только если шум низкий
                if (oneM.VolatilityPercent < fiveM.VolatilityPercent * 1.3m)
                    return DominantTF.Both;
            }

            // --- 4. Импульс 1m > 5m → 1m доминирует (рынок быстрый)
            if (Math.Abs(oneM.TrendSlopePercent) > Math.Abs(fiveM.TrendSlopePercent) * 1.4m
                && oneM.VolatilityPercent < 0.015m)
            {
                return DominantTF.OneMinute;
            }

            // --- 5. Флет и шум → отключить оба
            if (Math.Abs(oneM.TrendSlopePercent) < 0.005m &&
                Math.Abs(fiveM.TrendSlopePercent) < 0.005m)
                return DominantTF.None;

            // Контрольный fallback
            return DominantTF.FiveMinutes;
        }
    }
}
