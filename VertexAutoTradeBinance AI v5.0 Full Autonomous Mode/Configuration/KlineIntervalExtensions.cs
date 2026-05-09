using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Helpers
{
    public static class KlineIntervalExtensions
    {
        public static KlineInterval ToKlineInterval(this string timeframe)
        {
            return timeframe?.ToLower() switch
            {
                "1m" or "oneminute" => KlineInterval.OneMinute,
                "3m" => KlineInterval.ThreeMinutes,
                "5m" or "fiveminute" => KlineInterval.FiveMinutes,
                "15m" => KlineInterval.FifteenMinutes,
                "30m" => KlineInterval.ThirtyMinutes,
                "1h" or "onehour" => KlineInterval.OneHour,
                "2h" => KlineInterval.TwoHour,
                "4h" => KlineInterval.FourHour,
                "1d" or "oneday" => KlineInterval.OneDay,
                _ => KlineInterval.OneMinute
            };
        }

        public static string ToTimeframeString(this int minutes)
        {
            return minutes switch
            {
                1 => "1m",
                3 => "3m",
                5 => "5m",
                15 => "15m",
                30 => "30m",
                60 => "1h",
                120 => "2h",
                240 => "4h",
                1440 => "1d",
                _ => "1m"
            };
        }

    }
}
