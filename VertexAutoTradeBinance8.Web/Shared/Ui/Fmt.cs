using System.Globalization;

namespace VertexAutoTradeBinance8.Web.Shared.Ui;

public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Money(decimal v, int dp = 2) => v.ToString("N" + dp, Inv);
    public static string Signed(decimal v, int dp = 2) => (v > 0 ? "+" : v < 0 ? "−" : "") + Math.Abs(v).ToString("N" + dp, Inv);
    public static string Pct(decimal fraction, int dp = 1) => (fraction * 100m).ToString("0." + new string('0', dp), Inv) + "%";
    public static string SignedPct(decimal percent, int dp = 2) => (percent > 0 ? "+" : percent < 0 ? "−" : "") + Math.Abs(percent).ToString("0." + new string('0', dp), Inv) + "%";
    public static string Cls(decimal v) => v > 0 ? "up" : v < 0 ? "down" : "";

    /// <summary>Цена с разумной точностью по величине.</summary>
    public static string Price(decimal v)
    {
        if (v == 0) return "—";
        var a = Math.Abs(v);
        string f = a >= 1000 ? "N2" : a >= 10 ? "0.000" : a >= 1 ? "0.0000" : a >= 0.01m ? "0.00000" : "0.0000000";
        return v.ToString(f, Inv);
    }

    public static string Qty(decimal v) => v.ToString(v >= 100 ? "N0" : "0.####", Inv);

    public static string Ago(DateTime? utc)
    {
        if (utc == null) return "—";
        var d = DateTime.UtcNow - utc.Value;
        if (d.TotalSeconds < 0) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 48) return $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{(int)d.TotalDays}d";
    }

    public static string Local(DateTime utc, string f = "dd.MM HH:mm") => utc.ToLocalTime().ToString(f, Inv);
}
