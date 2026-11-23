using System.Globalization;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

internal sealed record SummaryState(int Count, double Min, double Max, double Mean, double Median, double StdDev, double Q1, double Q3);

internal sealed record HistogramState(double[] Edges, long[] Counts);

internal sealed record RegressionState(double Slope, double Intercept, double RSquared);

/// <summary>Welford's online algorithm.</summary>
internal sealed class RunningState : NativeState
{
    public long Count { get; private set; }
    public double Mean { get; private set; }
    public double M2 { get; private set; }
    public double Min { get; private set; } = double.PositiveInfinity;
    public double Max { get; private set; } = double.NegativeInfinity;

    public void Add(double x)
    {
        Count++;
        var delta = x - Mean;
        Mean += delta / Count;
        M2 += delta * (x - Mean);
        Min = Math.Min(Min, x);
        Max = Math.Max(Max, x);
    }

    public void Reset() => (Count, Mean, M2, Min, Max) = (0, 0, 0, double.PositiveInfinity, double.NegativeInfinity);

    public double Variance => Count == 0 ? 0 : M2 / Count;

    protected internal override object CaptureSnapshot() => (Count, Mean, M2, Min, Max);

    protected internal override void RestoreSnapshot(object snapshot) => (Count, Mean, M2, Min, Max) = ((long, double, double, double, double))snapshot;
}

/// <summary>Host implementations of <c>Std.Stats</c>.</summary>
internal static class StatsNatives
{
    private const string M = "Std.Stats";

    private static string N(double d) => ValueFormatting.FormatFloat(d);

    private static double[] Series(NativeContext c, Value values)
    {
        var data = Iterate(c, values).Select(MathNatives.D).ToArray();
        c.Charge(data.Length);
        return data.Length > 0 ? data : throw c.Throw("ArgumentError", "The series is empty.");
    }

    private static double[] Sorted(NativeContext c, double[] data)
    {
        var sorted = (double[])data.Clone();
        System.Array.Sort(sorted);
        c.Charge((long)(sorted.Length * Math.Log2(sorted.Length + 1)));
        return sorted;
    }

    /// <summary>Linear interpolation between closest ranks (the "R-7" / spreadsheet definition).</summary>
    internal static double Percentile(double[] sorted, double p)
    {
        var rank = p / 100 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
    }

    private static double Mean(double[] data)
    {
        // Pairwise-free compensated (Kahan–Neumaier) summation: deterministic and accurate.
        double sum = 0, compensation = 0;
        foreach (var x in data)
        {
            var t = sum + x;
            compensation += Math.Abs(sum) >= Math.Abs(x) ? (sum - t) + x : (x - t) + sum;
            sum = t;
        }

        return (sum + compensation) / data.Length;
    }

    private static double Variance(double[] data, bool sample)
    {
        if (sample && data.Length < 2)
        {
            throw new ScriptErrorRequest("ArgumentError", "Sample variance needs at least two values.");
        }

        var mean = Mean(data);
        var squares = data.Select(x => (x - mean) * (x - mean)).ToArray();
        return Mean(squares) * data.Length / (sample ? data.Length - 1 : data.Length);
    }

    public static void Register(NativeRegistry r)
    {
        var G = r.Scope(M);
        void Pair(string name, StdFunction f)
        {
            G($"{name}/{(name is "Percentile" or "HistogramOf" ? 2 : 1)}", f);
            G($"{name}Int", f);
        }

        Pair("Mean", (c, a) => Value.FromFloat(Mean(Series(c, a[0]))));
        Pair("Median", (c, a) => Value.FromFloat(Percentile(Sorted(c, Series(c, a[0])), 50)));
        Pair("Mode", (c, a) =>
        {
            var groups = Series(c, a[0]).GroupBy(x => x).Select(g => (Value: g.Key, Count: g.Count()));
            return Value.FromFloat(groups.OrderByDescending(g => g.Count).ThenBy(g => g.Value).First().Value);
        });
        Pair("Variance", (c, a) => Value.FromFloat(Variance(Series(c, a[0]), sample: false)));
        Pair("SampleVariance", (c, a) => Value.FromFloat(Variance(Series(c, a[0]), sample: true)));
        Pair("StdDev", (c, a) => Value.FromFloat(Math.Sqrt(Variance(Series(c, a[0]), sample: false))));
        Pair("SampleStdDev", (c, a) => Value.FromFloat(Math.Sqrt(Variance(Series(c, a[0]), sample: true))));
        Pair("Percentile", (c, a) =>
        {
            var p = MathNatives.D(a[1]);
            return p is >= 0 and <= 100 ? Value.FromFloat(Percentile(Sorted(c, Series(c, a[0])), p)) : throw c.Throw("ArgumentError", "p must be in [0, 100].");
        });
        Pair("Describe", (c, a) =>
        {
            var data = Series(c, a[0]);
            var sorted = Sorted(c, data);
            var summary = new SummaryState(data.Length, sorted[0], sorted[^1], Mean(data), Percentile(sorted, 50),
                Math.Sqrt(Variance(data, sample: false)), Percentile(sorted, 25), Percentile(sorted, 75));
            return c.NewInstance(c.Class(M, "Summary"), summary);
        });
        Pair("HistogramOf", (c, a) =>
        {
            var data = Series(c, a[0]);
            var bins = Int32(c, a[1].Bits, "Bin count", 1, 1_000_000);
            var (min, max) = (data.Min(), data.Max());
            var width = max > min ? (max - min) / bins : 1;
            var edges = Enumerable.Range(0, bins + 1).Select(i => i == bins ? (max > min ? max : min + 1) : min + (i * width)).ToArray();
            var counts = new long[bins];
            foreach (var x in data)
            {
                counts[BinOf(edges, x)]++;
            }

            return c.NewInstance(c.Class(M, "Histogram"), new HistogramState(edges, counts));
        });

        G("Correlation/2", (c, a) =>
        {
            var (xs, ys) = Pairwise(c, a);
            var (mx, my) = (Mean(xs), Mean(ys));
            double sxy = 0, sxx = 0, syy = 0;
            for (var i = 0; i < xs.Length; i++)
            {
                sxy += (xs[i] - mx) * (ys[i] - my);
                sxx += (xs[i] - mx) * (xs[i] - mx);
                syy += (ys[i] - my) * (ys[i] - my);
            }

            return sxx == 0 || syy == 0 ? throw c.Throw("ArithmeticError", "Correlation is undefined for a constant series.") : Value.FromFloat(sxy / Math.Sqrt(sxx * syy));
        });
        G("LinearRegression/2", (c, a) =>
        {
            var (xs, ys) = Pairwise(c, a);
            var (mx, my) = (Mean(xs), Mean(ys));
            double sxy = 0, sxx = 0, syy = 0;
            for (var i = 0; i < xs.Length; i++)
            {
                sxy += (xs[i] - mx) * (ys[i] - my);
                sxx += (xs[i] - mx) * (xs[i] - mx);
                syy += (ys[i] - my) * (ys[i] - my);
            }

            if (sxx == 0)
            {
                throw c.Throw("ArithmeticError", "Regression is undefined when every x is equal.");
            }

            var slope = sxy / sxx;
            var r2 = syy == 0 ? 1 : sxy * sxy / (sxx * syy);
            return c.NewInstance(c.Class(M, "Regression"), new RegressionState(slope, my - (slope * mx), r2));
        });
        G("MovingAverage/2", (c, a) =>
        {
            var data = Series(c, a[0]);
            var window = Int32(c, a[1].Bits, "Window", 1, data.Length);
            var result = new Value[data.Length - window + 1];
            var sum = data.Take(window).Sum();
            for (var i = 0; ; i++)
            {
                result[i] = Value.FromFloat(sum / window);
                if (i + window >= data.Length)
                {
                    break;
                }

                sum += data[i + window] - data[i];
            }

            return c.NewArray(result);
        });
        G("ExponentialMovingAverage/2", (c, a) =>
        {
            var data = Series(c, a[0]);
            var alpha = MathNatives.D(a[1]);
            if (!(alpha > 0 && alpha <= 1))
            {
                throw c.Throw("ArgumentError", "alpha must be in (0, 1].");
            }

            var result = new Value[data.Length];
            var ema = data[0];
            for (var i = 0; i < data.Length; i++)
            {
                ema = i == 0 ? data[0] : (alpha * data[i]) + ((1 - alpha) * ema);
                result[i] = Value.FromFloat(ema);
            }

            return c.NewArray(result);
        });
        G("ZScores/1", (c, a) =>
        {
            var data = Series(c, a[0]);
            var (mean, sd) = (Mean(data), Math.Sqrt(Variance(data, sample: false)));
            return c.NewArray([.. data.Select(x => Value.FromFloat(sd == 0 ? 0 : (x - mean) / sd))]);
        });
        G("Outliers/2", (c, a) =>
        {
            var data = Series(c, a[0]);
            var (mean, sd, threshold) = (Mean(data), Math.Sqrt(Variance(data, sample: false)), MathNatives.D(a[1]));
            return c.NewArray([.. data.Select((x, i) => (x, i)).Where(p => sd > 0 && Math.Abs(p.x - mean) / sd > threshold).Select(p => Value.FromInt(p.i))]);
        });
        G("IqrOutliers/1", (c, a) =>
        {
            var data = Series(c, a[0]);
            var sorted = Sorted(c, data);
            var (q1, q3) = (Percentile(sorted, 25), Percentile(sorted, 75));
            var (low, high) = (q1 - (1.5 * (q3 - q1)), q3 + (1.5 * (q3 - q1)));
            return c.NewArray([.. data.Select((x, i) => (x, i)).Where(p => p.x < low || p.x > high).Select(p => Value.FromInt(p.i))]);
        });

        var s = r.Scope($"{M}.Summary");
        static SummaryState S(NativeContext c, Value v) => c.State<SummaryState>(v);
        s("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Count));
        s("get_Min/0", (c, a) => Value.FromFloat(S(c, a[0]).Min));
        s("get_Max/0", (c, a) => Value.FromFloat(S(c, a[0]).Max));
        s("get_Mean/0", (c, a) => Value.FromFloat(S(c, a[0]).Mean));
        s("get_Median/0", (c, a) => Value.FromFloat(S(c, a[0]).Median));
        s("get_StdDev/0", (c, a) => Value.FromFloat(S(c, a[0]).StdDev));
        s("get_Q1/0", (c, a) => Value.FromFloat(S(c, a[0]).Q1));
        s("get_Q3/0", (c, a) => Value.FromFloat(S(c, a[0]).Q3));
        s("ToString/0", (c, a) =>
        {
            var x = S(c, a[0]);
            return Value.FromString($"n={x.Count} min={N(x.Min)} q1={N(x.Q1)} median={N(x.Median)} q3={N(x.Q3)} max={N(x.Max)} mean={Round(x.Mean)} sd={Round(x.StdDev)}");
        });

        var h = r.Scope($"{M}.Histogram");
        static HistogramState H(NativeContext c, Value v) => c.State<HistogramState>(v);
        h("get_Edges/0", (c, a) => c.NewArray([.. H(c, a[0]).Edges.Select(Value.FromFloat)]));
        h("get_Counts/0", (c, a) => c.NewArray([.. H(c, a[0]).Counts.Select(Value.FromInt)]));
        h("BinOf/1", (c, a) =>
        {
            var edges = H(c, a[0]).Edges;
            var x = MathNatives.D(a[1]);
            return Value.FromInt(x < edges[0] || x > edges[^1] ? -1 : BinOf(edges, x));
        });
        h("ToString/0", (c, a) =>
        {
            var x = H(c, a[0]);
            var peak = Math.Max(1, x.Counts.Max());
            var rows = x.Counts.Select((n, i) => $"[{Round(x.Edges[i])}, {Round(x.Edges[i + 1])}) {new string('#', (int)(n * 40 / peak))} {n}");
            return Value.FromString(string.Join("\n", rows));
        });

        var g = r.Scope($"{M}.Regression");
        static RegressionState L(NativeContext c, Value v) => c.State<RegressionState>(v);
        g("get_Slope/0", (c, a) => Value.FromFloat(L(c, a[0]).Slope));
        g("get_Intercept/0", (c, a) => Value.FromFloat(L(c, a[0]).Intercept));
        g("get_RSquared/0", (c, a) => Value.FromFloat(L(c, a[0]).RSquared));
        g("Predict/1", (c, a) => Value.FromFloat((L(c, a[0]).Slope * MathNatives.D(a[1])) + L(c, a[0]).Intercept));
        g("ToString/0", (c, a) => Value.FromString($"y = {Round(L(c, a[0]).Slope)}x + {Round(L(c, a[0]).Intercept)} (R² {Round(L(c, a[0]).RSquared)})"));

        var w = r.Scope($"{M}.RunningStats");
        static RunningState W(NativeContext c, Value v) => c.State<RunningState>(v);
        w("RunningStats/0", (c, a) => CollectionNatives.Init(a[0], new RunningState()));
        w("Add/1", (c, a) => Mutate<RunningState>(c, a[0], s => Void(() => s.Add(MathNatives.D(a[1])))));
        w("get_Count/0", (c, a) => Value.FromInt(W(c, a[0]).Count));
        w("get_Mean/0", (c, a) => Value.FromFloat(W(c, a[0]).Mean));
        w("get_Variance/0", (c, a) => Value.FromFloat(W(c, a[0]).Variance));
        w("get_StdDev/0", (c, a) => Value.FromFloat(Math.Sqrt(W(c, a[0]).Variance)));
        w("get_Min/0", (c, a) => W(c, a[0]).Count > 0 ? Value.FromFloat(W(c, a[0]).Min) : throw Empty(c, "running statistic"));
        w("get_Max/0", (c, a) => W(c, a[0]).Count > 0 ? Value.FromFloat(W(c, a[0]).Max) : throw Empty(c, "running statistic"));
        w("Reset/0", (c, a) => Mutate<RunningState>(c, a[0], s => Void(s.Reset)));
        w("ToString/0", (c, a) =>
        {
            var x = W(c, a[0]);
            return Value.FromString($"n={x.Count} mean={Round(x.Mean)} sd={Round(Math.Sqrt(x.Variance))}");
        });
    }

    private static string Round(double d) => Math.Round(d, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);

    private static int BinOf(double[] edges, double x)
    {
        var bins = edges.Length - 1;
        var index = System.Array.BinarySearch(edges, x);
        index = index >= 0 ? index : ~index - 1;
        return Math.Clamp(index, 0, bins - 1);
    }

    private static (double[] Xs, double[] Ys) Pairwise(NativeContext c, Value[] a)
    {
        var (xs, ys) = (Series(c, a[0]), Series(c, a[1]));
        return xs.Length == ys.Length && xs.Length >= 2 ? (xs, ys) : throw c.Throw("ArgumentError", "Both series need the same length (at least two).");
    }
}
