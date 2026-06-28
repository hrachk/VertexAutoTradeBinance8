namespace VertexAutoTradeBinance8.Configuration
{
    /// <summary>
    /// Strongly-typed mirror of the "Dca" appsettings.json section.
    /// DCA is intentionally a separate, schedule-driven accumulation
    /// strategy — not tied to the signal-reactive StrategyEngine at
    /// all. It buys on a fixed schedule regardless of market
    /// conditions (the actual definition of Dollar-Cost Averaging;
    /// research consistently shows this outperforms attempts to time
    /// entries via indicators, and resists curve-fitting far better).
    /// The only market-awareness this strategy has is the optional,
    /// explicit DipBonus below — never hidden "smart" entry logic.
    /// </summary>
    public sealed class DcaOptions
    {
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// How the total per-cycle budget is split across symbols when
        /// more than one is configured. "Weighted" uses each entry's
        /// own Weight (e.g. BTC gets the largest share, alts smaller -
        /// a standard, professional allocation pattern); "Equal" splits
        /// the budget evenly regardless of Weight.
        /// </summary>
        public string AllocationMode { get; set; } = "Weighted";

        public List<DcaSymbolEntry> Symbols { get; set; } = new();

        public DcaScheduleOptions Schedule { get; set; } = new();

        public DcaDipBonusOptions DipBonus { get; set; } = new();

        public sealed class DcaSymbolEntry
        {
            public string Symbol { get; set; } = "";

            /// <summary>
            /// Relative weight for allocation when AllocationMode is
            /// "Weighted" — e.g. BTC=60, ETH=25, SOL=15 means roughly
            /// 60%/25%/15% of each cycle's budget. Ignored entirely
            /// when AllocationMode is "Equal".
            /// </summary>
            public decimal Weight { get; set; } = 1m;
        }

        public sealed class DcaScheduleOptions
        {
            /// <summary>"Daily", "Weekly", or "Monthly".</summary>
            public string Frequency { get; set; } = "Weekly";

            /// <summary>
            /// IANA time zone identifier (e.g. "UTC", "America/New_York",
            /// "Europe/Moscow") the schedule below is interpreted in.
            /// Defaults to "UTC" for backward compatibility with
            /// existing configs. Using .NET's own TimeZoneInfo means
            /// DST transitions (e.g. EDT ↔ EST) are handled
            /// automatically — a configured "9 AM America/New_York"
            /// buy stays at 9 AM New York time year-round, with no
            /// manual hour adjustment needed twice a year.
            /// </summary>
            public string TimeZoneId { get; set; } = "UTC";

            /// <summary>Hour of day (0-23) the scheduled buy fires at, in the TimeZoneId above.</summary>
            public int HourUtc { get; set; } = 12;

            /// <summary>
            /// Day of week for Weekly frequency (Monday=1..Sunday=7,
            /// ISO 8601 convention) — confirmed via research that
            /// buying on Mondays specifically has a measurable edge
            /// for BTC DCA backtests, so this defaults there, but is
            /// fully configurable.
            /// </summary>
            public int DayOfWeek { get; set; } = 1;

            /// <summary>Day of month (1-28) for Monthly frequency.</summary>
            public int DayOfMonth { get; set; } = 1;

            /// <summary>Total USDT budget spent across all configured symbols on each scheduled buy.</summary>
            public decimal BudgetPerCycle { get; set; } = 100m;
        }

        /// <summary>
        /// Optional, explicit, auditable override — NOT hidden "smart"
        /// logic. If enabled, and the price dropped by at least
        /// DropThresholdPct over the lookback window at the moment a
        /// scheduled buy fires, that single buy's amount is multiplied
        /// by Multiplier. This is the full extent of the market-
        /// awareness this strategy has — deliberately simple and
        /// transparent, not a value-averaging or indicator-driven
        /// system (research shows those don't reliably beat plain DCA,
        /// and are much easier to accidentally curve-fit).
        /// </summary>
        public sealed class DcaDipBonusOptions
        {
            public bool Enabled { get; set; } = false;
            public int LookbackHours { get; set; } = 24;
            public decimal DropThresholdPct { get; set; } = 10m;
            public decimal Multiplier { get; set; } = 2m;
        }
    }
}
