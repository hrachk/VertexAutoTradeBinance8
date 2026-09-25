using VertexAutoTrade.Core.Market;
using VertexAutoTrade.Core.Pipeline;
using VertexAutoTrade.Execution;
using VertexAutoTrade.MLEngine;
using VertexAutoTrade.NewsMacro;
using VertexAutoTrade.Regime;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Sizing;

namespace VertexAutoTrade.RiskEngine.Pipeline;

public sealed class InstitutionalEntryContext
{
    public required string Symbol { get; init; }
    public required bool IsLong { get; init; }
    public required decimal EquityUsd { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal StopLossPrice { get; init; }
    public required decimal ConfiguredRiskFraction { get; init; }
    public required int OpenPositionCount { get; init; }
    public decimal RealizedRToday { get; init; }
    public decimal RealizedPnlTodayUsd { get; init; }
    public decimal SignalConfidence { get; init; } = 0.55m;
    public IReadOnlyList<decimal>? Closes { get; init; }
    public IReadOnlyList<decimal>? Highs { get; init; }
    public IReadOnlyList<decimal>? Lows { get; init; }
    public decimal? BtcPriceNow { get; init; }
    public decimal? BtcPrice15mAgo { get; init; }
    public NewsSentimentSnapshot? ActiveNews { get; init; }
    public bool NewsHardMode { get; init; }
    public decimal? MinQty { get; init; }
    public decimal? StepSize { get; init; }
    public decimal? MinNotional { get; init; }
    public DateTime UtcNow { get; init; } = DateTime.UtcNow;
}

public sealed class InstitutionalEntryResult
{
    public bool Allowed { get; init; }
    public string RejectLayer { get; init; } = "";
    public string RejectCode { get; init; } = "";
    public string Message { get; init; } = "";
    public decimal SizeMult { get; init; } = 1m;
    public decimal EffectiveRiskFraction { get; init; }
    public decimal Quantity { get; init; }
    public MarketRegimeKind Regime { get; init; }
    public decimal? MlPWin { get; init; }
}

/// <summary>
/// Phases 1–5 gate chain before order send.
/// HardRisk → AntiTilt → Macro → News → Regime → BTC → OI/Funding → ML → Sizer.
/// </summary>
public sealed class InstitutionalEntryPipeline
{
    private readonly IHardRiskGuard _hard;
    private readonly IAntiTiltCircuitBreaker _tilt;
    private readonly IPositionSizer _sizer;
    private readonly MarketRegimeAnalyzer _regime;
    private readonly BtcCorrelationGuard _btc;
    private readonly CalendarMacroGuard _macro;
    private readonly NewsSentimentGate _news;
    private readonly OiFundingTracker _oi;
    private readonly MlSetupClassifier _ml;

    public InstitutionalEntryPipeline(
        IHardRiskGuard hard,
        IAntiTiltCircuitBreaker tilt,
        IPositionSizer sizer,
        MarketRegimeAnalyzer? regime = null,
        BtcCorrelationGuard? btc = null,
        CalendarMacroGuard? macro = null,
        NewsSentimentGate? news = null,
        OiFundingTracker? oi = null,
        MlSetupClassifier? ml = null)
    {
        _hard = hard;
        _tilt = tilt;
        _sizer = sizer;
        _regime = regime ?? new MarketRegimeAnalyzer();
        _btc = btc ?? new BtcCorrelationGuard();
        _macro = macro ?? new CalendarMacroGuard();
        _news = news ?? new NewsSentimentGate();
        _oi = oi ?? new OiFundingTracker();
        _ml = ml ?? new MlSetupClassifier(hardReject: false);
    }

    public InstitutionalEntryResult Evaluate(InstitutionalEntryContext ctx)
    {
        decimal sizeMult = 1m;
        var snap = new RiskSnapshot(
            ctx.UtcNow, ctx.EquityUsd, ctx.RealizedPnlTodayUsd, ctx.RealizedRToday, ctx.OpenPositionCount);

        var hard = _hard.EvaluateEntry(snap);
        if (!hard.Allowed)
            return Fail("HardRisk", hard.Reason.ToString(), hard.Message);

        var tilt = _tilt.EvaluateEntry(ctx.UtcNow,
            ctx.ConfiguredRiskFraction > 0 ? ctx.ConfiguredRiskFraction : hard.EffectiveRiskFraction);
        if (!tilt.Allowed)
            return Fail("AntiTilt", tilt.Reason.ToString(), tilt.Message);
        var riskFrac = tilt.EffectiveRiskFraction > 0 ? tilt.EffectiveRiskFraction : ctx.ConfiguredRiskFraction;

        var macro = _macro.Evaluate(ctx.UtcNow);
        if (!macro.Allowed)
            return Fail("NewsMacro", macro.Code, macro.Message);
        sizeMult *= _macro.SizeMultNearWindow(ctx.UtcNow);

        var news = _news.Evaluate(ctx.ActiveNews, ctx.NewsHardMode);
        if (!news.Allowed)
            return Fail("NewsMacro", news.Code, news.Message);
        sizeMult *= news.SizeMult;

        var regimeKind = MarketRegimeKind.Unknown;
        if (ctx.Closes is { Count: >= 30 } && ctx.Highs is not null && ctx.Lows is not null)
        {
            regimeKind = _regime.Classify(ctx.Closes, ctx.Highs, ctx.Lows);
            var reg = _regime.EvaluateEntry(regimeKind);
            if (!reg.Allowed)
                return Fail("Regime", reg.Code, reg.Message, regimeKind);
        }

        if (ctx.BtcPriceNow is decimal bn && ctx.BtcPrice15mAgo is decimal b15)
            _btc.ObserveBtcMove(bn, b15, ctx.UtcNow);
        var btc = _btc.EvaluateAltEntry(ctx.Symbol, ctx.UtcNow);
        if (!btc.Allowed)
            return Fail("BtcCorrelation", btc.Code, btc.Message, regimeKind);

        var oi = _oi.Evaluate(ctx.Symbol, ctx.IsLong);
        if (!oi.Allowed)
            return Fail("OiFunding", oi.Code, oi.Message, regimeKind);

        var atrPct = ctx.EntryPrice > 0
            ? Math.Abs(ctx.EntryPrice - ctx.StopLossPrice) / ctx.EntryPrice
            : 0.01m;
        var features = new TradeFeatureVector
        {
            Confidence = ctx.SignalConfidence,
            AtrPct = atrPct,
            HourUtc = ctx.UtcNow.Hour,
            RegimeCode = (int)regimeKind,
            SideSign = ctx.IsLong ? 1 : -1,
            NewsImpact = ctx.ActiveNews is null ? 0m : (decimal)(int)ctx.ActiveNews.Impact / 4m
        };
        var pWin = _ml.PredictPWin(features);
        var ml = _ml.Evaluate(features);
        if (!ml.Allowed)
            return Fail("MlClassifier", ml.Code, ml.Message, regimeKind, pWin);
        sizeMult *= ml.SizeMult;

        sizeMult = Math.Clamp(sizeMult, 0.15m, 1m);
        riskFrac = Math.Max(0.001m, riskFrac * sizeMult);

        var sized = _sizer.Calculate(new PositionSizeRequest(
            ctx.EquityUsd, ctx.EntryPrice, ctx.StopLossPrice, riskFrac,
            ctx.MinQty, ctx.StepSize, ctx.MinNotional));
        if (!sized.Ok)
            return Fail("Sizing", "QTY_REJECT", sized.RejectReason ?? "size failed", regimeKind, pWin);

        return new InstitutionalEntryResult
        {
            Allowed = true,
            Message = "OK",
            SizeMult = sizeMult,
            EffectiveRiskFraction = riskFrac,
            Quantity = sized.Quantity,
            Regime = regimeKind,
            MlPWin = pWin
        };
    }

    public void OnTradeClosed(ClosedTradeRiskEvent evt)
    {
        _hard.RegisterClosedR(evt.RealizedR, evt.ClosedAtUtc);
        _tilt.OnTradeClosed(evt);
    }

    public OiFundingTracker OiFunding => _oi;
    public MlSetupClassifier Ml => _ml;

    private static InstitutionalEntryResult Fail(
        string layer, string code, string msg,
        MarketRegimeKind regime = MarketRegimeKind.Unknown,
        decimal? pWin = null) => new()
    {
        Allowed = false,
        RejectLayer = layer,
        RejectCode = code,
        Message = msg,
        Regime = regime,
        MlPWin = pWin,
        SizeMult = 0m
    };
}
