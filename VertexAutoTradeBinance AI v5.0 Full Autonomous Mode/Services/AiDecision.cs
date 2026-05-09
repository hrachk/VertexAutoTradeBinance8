using System;

namespace VertexAutoTradeBinance8.Models
{
    public enum AiGrade
    {
        Block = 0,
        Weak = 1,
        Border = 2,
        Ok = 3,
        Strong = 4
    }
    public record AiDecision(
        bool Allow,
        string Grade,          // BLOCK / BORDER / OK / GOOD / STRONG
        decimal Score,         // 0..1
        decimal AtrPct,        // ATR% в долях (0.0017 = 0.17 %)
        string Trend,          // UP / DOWN / FLAT
        decimal BodyAtr,       // тело свечи / ATR
        decimal Rr,            // risk-reward
        bool Manipulation,     // замечены манипуляции
        bool SuperSignal,      // TradeSignal.IsSuperSignal
        string Reason          // текстовая причина
    );
}
