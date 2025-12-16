

using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Models
{
    public class SymbolState
    {
        public DateTime? LastStopTimeUtc { get; set; }
        public int LosingStreak { get; set; }
        public DateTime? LastHtfOpenTimeUtc { get; set; }
        public bool HadStopInsideHtf { get; set; }


        // === PROFIT BUCKET ===
        public decimal RealizedPnlBucketUsd { get; set; } = 0m;
        public DateTime BucketDayUtc { get; set; } = DateTime.UtcNow.Date;

        // === HARVEST CONTROL ===
        public DateTime LastHarvestUtc { get; set; } = DateTime.MinValue;
        public int HarvestsToday { get; set; } = 0;

        // === DYNAMIC CAPS ===
        public decimal DefaultSymbolCapPct { get; set; } = 0.18m; // default 18%
        public decimal CurrentSymbolCapPct { get; set; } = 0.18m; // AI может расширять временно
        public DateTime CapBoostUntilUtc { get; set; } = DateTime.MinValue;

        // === MODE ===
        public HedgeMode HedgeMode { get; set; } = HedgeMode.Defensive;

        // === ADD CONTROL ===
        public DateTime LastAddUtc { get; set; } = DateTime.MinValue;
        public int AddsToday { get; set; } = 0;

        // === SAFETY ===
        public DateTime LastSafeKillUtc { get; set; } = DateTime.MinValue;
        public DateTime LastProtectionUtc { get; set; } = DateTime.MinValue;
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HedgeMode
    {
        Defensive,
        BiasLong,
        BiasShort,
        TrendRide
    }
}
