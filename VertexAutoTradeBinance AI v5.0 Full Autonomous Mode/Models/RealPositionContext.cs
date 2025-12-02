using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class RealPositionContext
    {
        //// --- ОРИГИНАЛЬНЫЕ ПОЛЯ ПО ФАКТУ ПОЗИЦИИ ---
        //public string Symbol { get; set; } = string.Empty;

        //public PositionSide Side { get; set; } = PositionSide.Both;   // Long/Short/Both

        //public decimal Entry { get; set; }      // Entry price
        //public decimal Mark { get; set; }       // Mark price
        //public decimal Qty { get; set; }        // Absolute quantity
        //public decimal Leverage { get; set; }   // Position leverage
        //public decimal Liquidation { get; set; } // Liq price (important for SL logic)

        //// --- ПОЛЯ ОКРУЖЕНИЯ ---
        //public MarketRegime Regime { get; set; } = MarketRegime.Unknown;

        //public IReadOnlyList<Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtKline>? Klines { get; set; }

        //public bool ManipulationDetected { get; set; } = false;

        //public List<Binance.Net.Objects.Models.Futures.BinanceUsdFuturesOrder> Orders { get; set; }
        //    = new List<Binance.Net.Objects.Models.Futures.BinanceUsdFuturesOrder>();

        //// --- СЛ / ТП ИЗ НАШЕГО ПОСЛЕДНЕГО СИГНАЛА ---
        //public TradeSignal? Signal { get; set; }

        //// --- ФЛАГ: позиция создана руками ---
        //public bool IsManual { get; set; } = false;

        //// --- HELPERS ---
        //public decimal DistanceToSL =>
        //    Signal == null ? 0 : Math.Abs(Mark - Signal.StopLoss);

        //public decimal DistanceToTP =>
        //    Signal == null || Signal.TakeProfits.Count == 0
        //        ? 0
        //        : Math.Abs(Signal.TakeProfits[0] - Mark);

        //public override string ToString()
        //{
        //    return $"{Symbol} {Side} qty={Qty} entry={Entry} mark={Mark} regime={Regime}";
        //}
    }
}
