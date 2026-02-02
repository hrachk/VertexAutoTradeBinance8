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

            // --- 1. ПРИОРЕТЕТ 15M (Контекстный локомотив)
            // Если на 15м идет "вертикальный" тренд с ER > 0.8, 
            // мелкие ТФ становятся лишь инструментами уточнения входа.
            bool extreme15m = fifteenM.EfficiencyRatio > 0.8m && Math.Abs(fifteenM.TrendSlopePercent) > 0.01m;

            // --- 2. ФИЛЬТР ХАОСА (Global Choppiness)
            // Если на старшем ТФ (15м) полная "пила", то торговля на 1м/5м — это казино.
            if (fifteenM.EfficiencyRatio < 0.25m && fiveM.EfficiencyRatio < 0.3m)
                return DominantTF.None;

            // --- 3. АНАЛИЗ КОНФЛИКТА ТАЙМФРЕЙМОВ
            bool isM1CounterTrend = Math.Sign(oneM.TrendSlopePercent) != Math.Sign(fifteenM.TrendSlopePercent);
            bool isM5CounterTrend = Math.Sign(fiveM.TrendSlopePercent) != Math.Sign(fifteenM.TrendSlopePercent);

            // --- 4. ЛОГИКА ВЫБОРА (Decision Tree)

            // Сценарий А: Сильный тренд на 15м
            if (extreme15m)
            {
                // Если 1м и 5м подтверждают 15м — работаем по обоим (агрессивно)
                if (!isM5CounterTrend && oneM.EfficiencyRatio > 0.5m) return DominantTF.Both;
                // Если 1м шумит или корректируется, доверяем 5м как фильтру
                return DominantTF.FiveMinutes;
            }

            // Сценарий Б: Выброс волатильности на 1м (Шум)
            if (oneM.VolatilityPercent > fiveM.VolatilityPercent * 2.2m)
            {
                // Если 1м летит против 15м на высокой волатильности — это ложный вынос (Squeeze)
                if (isM1CounterTrend) return DominantTF.FiveMinutes;
                return DominantTF.FiveMinutes;
            }

            // Сценарий В: Идеальная сонаправленность (Стэк)
            if (!isM1CounterTrend && !isM5CounterTrend)
            {
                // Если все три ТФ смотрят в одну сторону и эффективны
                if (oneM.EfficiencyRatio > 0.6m && fiveM.EfficiencyRatio > 0.6m)
                    return DominantTF.Both;
            }

            // Сценарий Г: Быстрый скальпинг (M1 доминирует)
            // Только если M1 очень эффективен и НЕ противоречит 15м
            if (oneM.EfficiencyRatio > 0.75m && !isM1CounterTrend)
            {
                if (Math.Abs(oneM.TrendSlopePercent) > Math.Abs(fiveM.TrendSlopePercent) * 1.4m)
                    return DominantTF.OneMinute;
            }

            // --- 5. ФЛЕТ (Порог 0.05%)
            if (Math.Abs(oneM.TrendSlopePercent) < 0.005m &&
                Math.Abs(fiveM.TrendSlopePercent) < 0.005m &&
                Math.Abs(fifteenM.TrendSlopePercent) < 0.005m)
                return DominantTF.None;

            return DominantTF.FiveMinutes;
        }

        public class ProfessionalTFSelector
        {
            // Пороги эффективности по Кауфману
            private const decimal ER_TrendThreshold = 0.6m;  // Выше 0.6 — направленное движение
            private const decimal ER_NoiseThreshold = 0.3m;  // Ниже 0.3 — "пила" (не торговать)

            public DominantTF SelectTF(
                MarketSnapshot m1,
                MarketSnapshot m5,
                MarketSnapshot m15)
            {
                if (m1 == null || m5 == null || m15 == null) return DominantTF.None;

                // 1. Фильтр "Рыночного шума" через ER
                // Если на 5м и 1м низкая эффективность — рынок в фазе накопления/распределения
                if (m5.EfficiencyRatio < ER_NoiseThreshold && m1.EfficiencyRatio < ER_NoiseThreshold)
                    return DominantTF.None;

                // 2. Определение "Локомотива" (M15)
                // Если старший ТФ супер-эффективен, мы ищем вход только в его сторону
                bool isM15Strong = m15.EfficiencyRatio > ER_TrendThreshold;
                int globalTrend = Math.Sign(m15.TrendSlopePercent);

                // 3. Анализ M1 (Скальпинг-импульс)
                // M1 доминирует только если он экстремально эффективен (чистый импульс)
                bool m1IsClean = m1.EfficiencyRatio > 0.8m && m1.VolatilityPercent < m5.VolatilityPercent * 1.5m;

                if (m1IsClean)
                {
                    // Если M1 идет против сильного M15 — игнорируем M1, ждем отката
                    if (isM15Strong && Math.Sign(m1.TrendSlopePercent) != globalTrend)
                        return DominantTF.FiveMinutes;

                    return DominantTF.OneMinute;
                }

                // 4. Основная рабочая логика (M5 + M15 Alignment)
                if (Math.Sign(m5.TrendSlopePercent) == globalTrend)
                {
                    // Если и M1 подтверждает M5, и оба эффективны
                    if (Math.Sign(m1.TrendSlopePercent) == globalTrend && m1.EfficiencyRatio > ER_NoiseThreshold)
                        return DominantTF.Both;

                    return DominantTF.FiveMinutes;
                }

                // 5. Конфликт направлений (M5 против M15)
                // В про-трейдинге в такой ситуации лучше остаться в стороне
                return DominantTF.None;
            }
        }
    }
}
