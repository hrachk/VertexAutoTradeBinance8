using System;
using System.Collections.Generic;
using System.Linq;

namespace VertexAutoTrade.Core.Risk;

/// <summary>Pearson R on log-returns (institutional directional exposure).</summary>
public static class CorrelationMath
{
    public static double? PearsonLogReturns(
        IReadOnlyList<decimal> closesA,
        IReadOnlyList<decimal> closesB,
        int maxBars = 48)
    {
        if (closesA == null || closesB == null) return null;
        int n = Math.Min(Math.Min(closesA.Count, closesB.Count), maxBars + 1);
        if (n < 12) return null;

        // align last n closes
        var a = closesA.Skip(closesA.Count - n).ToList();
        var b = closesB.Skip(closesB.Count - n).ToList();

        var ra = new List<double>(n - 1);
        var rb = new List<double>(n - 1);
        for (int i = 1; i < n; i++)
        {
            if (a[i - 1] <= 0 || b[i - 1] <= 0 || a[i] <= 0 || b[i] <= 0) continue;
            ra.Add(Math.Log((double)(a[i] / a[i - 1])));
            rb.Add(Math.Log((double)(b[i] / b[i - 1])));
        }
        if (ra.Count < 10) return null;
        return Pearson(ra, rb);
    }

    public static double? Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = Math.Min(x.Count, y.Count);
        if (n < 3) return null;
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;
        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < n; i++)
        {
            double vx = x[i] - mx, vy = y[i] - my;
            num += vx * vy;
            dx += vx * vx;
            dy += vy * vy;
        }
        if (dx <= 1e-18 || dy <= 1e-18) return null;
        return num / Math.Sqrt(dx * dy);
    }
}
