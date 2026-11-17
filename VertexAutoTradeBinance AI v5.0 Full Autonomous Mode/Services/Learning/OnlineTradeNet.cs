using System.Globalization;
using System.Text.Json;

namespace VertexAutoTradeBinance8.Services.Learning;

/// <summary>
/// Meta-labeler (Lopez de Prado): the rules pick the side, this net only
/// estimates P(the trade reaches profit before the stop).
/// One hidden layer, 8 inputs. Online SGD on each close, weights on disk.
/// It does not predict price and it stays silent until it has seen enough closes.
/// </summary>
public sealed class OnlineTradeNet
{
    public const int Inputs = 8;
    private const int Hidden = 8;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly double[] _w1 = new double[Hidden * Inputs];
    private readonly double[] _b1 = new double[Hidden];
    private readonly double[] _w2 = new double[Hidden];
    private double _b2;
    private int _updates;
    private double _lr = 0.05;

    public int Updates { get { lock (_gate) return _updates; } }

    public OnlineTradeNet(string path)
    {
        _path = path;
        InitSmall();
        Load();
    }

    public static double[] Features(
        decimal confidence, int sideSign, int hourUtc,
        decimal avgR, int lossStreak, decimal riskPct, bool trendLeg)
    {
        var conf = (double)confidence;
        if (conf > 1.5) conf /= 100.0;
        conf = Math.Clamp(conf, 0, 1);
        var hour = ((hourUtc % 24) + 24) % 24;
        var ang = 2.0 * Math.PI * hour / 24.0;
        return new[]
        {
            conf,
            sideSign >= 0 ? 1.0 : -1.0,
            Math.Sin(ang),
            Math.Cos(ang),
            (double)Math.Clamp(avgR, -1.5m, 1.5m),
            Math.Clamp(lossStreak / 5.0, 0, 2),
            (double)Math.Clamp(riskPct, 0m, 0.2m) / 0.05,
            trendLeg ? 1.0 : 0.0
        };
    }

    public MetaNetScore Score(double[] x)
    {
        double p;
        int n;
        lock (_gate)
        {
            p = Forward(x, null, null);
            n = _updates;
        }
        // Untrained net sits near 0.5 and must not veto live signals.
        if (n < 8)
            return new MetaNetScore((decimal)p, n, false, 1m, $"shadow n={n} p={p:F2}");
        if (p < 0.36)
            return new MetaNetScore((decimal)p, n, true, 0m, $"skip n={n} p={p:F2}");
        if (p < 0.48)
        {
            var size = (decimal)Math.Clamp(p / 0.50, 0.40, 0.95);
            return new MetaNetScore((decimal)p, n, false, size, $"cut n={n} p={p:F2}");
        }
        return new MetaNetScore((decimal)p, n, false, 1m, $"ok n={n} p={p:F2}");
    }

    public void Learn(double[] x, bool win, double sampleWeight = 1)
    {
        if (x == null || x.Length != Inputs) return;
        lock (_gate)
        {
            Step(x, win ? 1.0 : 0.0, sampleWeight);
            _updates++;
            Save();
        }
    }

    /// <summary>One ordered pass over history. Replaces weights only when the file is still empty.</summary>
    public void Bootstrap(IReadOnlyList<(double[] X, bool Win, double Weight)> rows)
    {
        if (rows == null || rows.Count == 0) return;
        lock (_gate)
        {
            if (_updates > 0) return;
            var prev = _lr;
            _lr = 0.08;
            foreach (var row in rows)
            {
                if (row.X == null || row.X.Length != Inputs) continue;
                Step(row.X, row.Win ? 1.0 : 0.0, row.Weight <= 0 ? 1 : row.Weight);
                _updates++;
            }
            _lr = prev;
            Save();
        }
    }

    private void Step(double[] x, double y, double weight)
    {
        var h = new double[Hidden];
        var z1 = new double[Hidden];
        var p = Forward(x, h, z1);
        var err = (p - y) * Math.Clamp(weight, 0.25, 3);
        for (var j = 0; j < Hidden; j++)
        {
            var dh = err * _w2[j] * (1.0 - h[j] * h[j]);
            _w2[j] -= _lr * (err * h[j] + 1e-4 * _w2[j]);
            _b1[j] -= _lr * dh;
            for (var i = 0; i < Inputs; i++)
                _w1[j * Inputs + i] -= _lr * (dh * x[i] + 1e-4 * _w1[j * Inputs + i]);
        }
        _b2 -= _lr * err;
    }

    private double Forward(double[] x, double[]? hOut, double[]? zOut)
    {
        var h = hOut ?? new double[Hidden];
        for (var j = 0; j < Hidden; j++)
        {
            double z = _b1[j];
            var row = j * Inputs;
            for (var i = 0; i < Inputs; i++) z += _w1[row + i] * x[i];
            z = Math.Clamp(z, -20, 20);
            if (zOut != null) zOut[j] = z;
            h[j] = Math.Tanh(z);
        }
        double z2 = _b2;
        for (var j = 0; j < Hidden; j++) z2 += _w2[j] * h[j];
        z2 = Math.Clamp(z2, -20, 20);
        return 1.0 / (1.0 + Math.Exp(-z2));
    }

    private void InitSmall()
    {
        for (var i = 0; i < _w1.Length; i++)
            _w1[i] = 0.02 * Math.Sin(i * 12.9898);
        for (var j = 0; j < Hidden; j++)
        {
            _b1[j] = 0;
            _w2[j] = 0.02 * Math.Cos(j * 4.123);
        }
        _b2 = 0;
        _updates = 0;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            if (!TryArray(root, "w1", _w1) || !TryArray(root, "b1", _b1) || !TryArray(root, "w2", _w2))
                return;
            if (root.TryGetProperty("b2", out var b2)) _b2 = b2.GetDouble();
            if (root.TryGetProperty("updates", out var u)) _updates = u.GetInt32();
            if (root.TryGetProperty("lr", out var lr)) _lr = lr.GetDouble();
        }
        catch
        {
            InitSmall();
        }
    }

    private static bool TryArray(JsonElement root, string name, double[] dest)
    {
        if (!root.TryGetProperty(name, out var el) || el.GetArrayLength() != dest.Length) return false;
        var i = 0;
        foreach (var v in el.EnumerateArray()) dest[i++] = v.GetDouble();
        return true;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var payload = new
            {
                updates = _updates,
                lr = _lr,
                w1 = _w1,
                b1 = _b1,
                w2 = _w2,
                b2 = _b2,
                savedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            /* a missed save must not block the close */
        }
    }
}

public readonly record struct MetaNetScore(decimal PWin, int Updates, bool Skip, decimal SizeMult, string Note);
