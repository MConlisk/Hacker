using static Ghostrig.CScript.Tests.Std.StdProgram;

namespace Ghostrig.CScript.Tests.Std;

public class MathTests
{
    private const string Imports = "import Std.Math;\nimport Std.Collections;";

    [Fact]
    public void Functions_dispatch_on_int_and_float_overloads()
    {
        var result = Run(Imports, """
            string Main() => $"{Math.Abs(-5)}|{Math.Abs(-2.5)}|{Math.Min(3, 7)}|{Math.Max(1.5, 0.5)}|{Math.Clamp(15, 0, 10)}|{Math.Sign(-0.1)}|{Math.Sqrt(16.0)}|{Math.Pow(2.0, 10.0)}|{Math.Log2(1024.0)}|{Math.Log10(1000.0)}|{Math.Round(2.5)}|{Math.Round(3.14159, 2)}|{Math.FloorToInt(-1.5)}|{Math.Hypot(3.0, 4.0)}|{Math.Log(8.0, 2.0)}|{Math.Remap(5.0, 0.0, 10.0, 100.0, 200.0)}";
            """);
        Assert.Equal("5|2.5|3|1.5|10|-1|4.0|1024.0|10.0|3.0|3.0|3.14|-2|5.0|3.0|150.0", result);
    }

    [Fact]
    public void Trigonometry_is_deterministic_and_accurate()
    {
        var result = Run(Imports, """
            string Main() {
                var s = Math.Sin(Math.Pi / 6);
                var c = Math.Cos(Math.Pi / 3);
                var a = Math.Atan2(1.0, 1.0) * 4;
                return $"{Math.ApproxEqual(s, 0.5, 1e-15)}|{Math.ApproxEqual(c, 0.5, 1e-15)}|{Math.ApproxEqual(a, Math.Pi, 1e-15)}|{Math.ToDegrees(Math.Pi)}";
            }
            """);
        Assert.Equal("true|true|true|180.0", result);
    }

    [Fact]
    public void Integer_number_theory()
    {
        var result = Run(Imports, """
            string Main() {
                var factors = Math.Factorize(600851475143);
                var bigPrime = Math.IsPrime(9223372036854775783);
                return $"{Math.Gcd(84, 36)}|{Math.Lcm(4, 6)}|{Math.ModPow(4, 13, 497)}|{Math.ModInverse(3, 11)}|{Math.IsPrime(97)}|{Math.IsPrime(561)}|{bigPrime}|{Math.NextPrime(100)}|{factors.Length}:{factors[3]}|{Math.ISqrt(99)}|{Math.FloorDiv(-7, 2)}|{Math.FloorMod(-7, 3)}|{Math.PopCount(255)}|{Math.RotateLeft(1, 65)}|{Math.NextPowerOfTwo(1000)}|{Math.Factorial(20)}|{Math.Binomial(52, 5)}";
            }
            """);
        Assert.Equal("12|12|445|4|true|false|true|101|4:6857|9|-4|2|8|2|1024|2432902008176640000|2598960", result);
    }

    [Fact]
    public void Checked_arithmetic_raises_ArithmeticError_while_plain_ints_wrap()
    {
        Assert.Equal("-9223372036854775808", Run(Imports, """string Main() => $"{9223372036854775807 + 1}";"""));
        Assert.Contains("ArithmeticError", ErrorOf(Imports, """string Main() => $"{Math.CheckedAdd(9223372036854775807, 1)}";"""));
        Assert.Contains("ArithmeticError", ErrorOf(Imports, """string Main() => $"{Math.ModInverse(4, 8)}";"""));
    }

    [Fact]
    public void BigInt_handles_rsa_sized_arithmetic()
    {
        var result = Run(Imports, """
            string Main() {
                var p = BigInt.Parse("61"); var q = BigInt.Parse("53");
                var n = p.Multiply(q);                                    // 3233
                var phi = p.Subtract(BigInt.One).Multiply(q.Subtract(BigInt.One));
                var e = new BigInt(17);
                var d = e.ModInverse(phi);                                // 2753
                var cipher = new BigInt(65).ModPow(e, n);                 // 2790
                var plain = cipher.ModPow(d, n);
                var big = new BigInt(2).Pow(256).Subtract(BigInt.One);
                var keys = new Map<BigInt, string>();
                keys[BigInt.Parse("3233")] = "toy-rsa";
                return $"{n}|{d}|{cipher}|{plain}|{big.BitLength}|{big.ToHex().Length}|{BigInt.Parse("0xff")}|{big.Sqrt().BitLength}|{keys[n]}|{n == BigInt.Parse("3233")}|{BigInt.Parse("170141183460469231731687303715884105727").IsProbablePrime()}";
            }
            """);
        Assert.Equal("3233|2753|2790|65|256|64|255|128|toy-rsa|true|true", result);
    }

    [Fact]
    public void Rational_complex_and_fixed_numbers()
    {
        var result = Run(Imports, """
            string Main() {
                var third = new Rational(1, 3);
                var sum = third.Add(new Rational(1, 6));                  // 1/2
                var parsed = Rational.Parse("0.125");
                var i = new Complex(0.0, 1.0);
                var minusOne = i.Multiply(i);
                var euler = Complex.FromPolar(1.0, Math.Pi).Real;
                var half = Fixed.Parse("0.5");
                var fx = new Fixed(3).Multiply(half).Add(Fixed.FromFloat(0.25));
                return $"{sum}|{parsed}|{sum.CompareTo(third)}|{new Rational(6, -4)}|{minusOne}|{euler}|{fx}|{fx.Raw}|{new Fixed(2).Sqrt().ToFloat() > 1.4142}|{Fixed.Parse("-1.75")}";
            }
            """);
        Assert.Equal("1/2|1/8|1|-3/2|-1.0 + 0.0i|-1.0|1.75|7516192768|true|-1.75", result);
    }

    [Fact]
    public void Vectors_matrices_and_shapes()
    {
        var result = Run(Imports, """
            string Main() {
                var v = new Vector2(3, 4);
                var rotated = new Vector2(1, 0).Rotate(Math.Pi / 2);
                var m = Matrix3x2.Translation(10, 0).Multiply(Matrix3x2.Scaling(2, 2));
                var p = m.Transform(new Vector2(1, 1));                  // (1+10)*2, (1+0)*2
                var back = m.Invert().Transform(p);
                var cube = Matrix4x4.Translation(1, 2, 3).Multiply(Matrix4x4.Scaling(2, 2, 2));   // translate, then scale
                var moved = cube.Transform(new Vector3(1, 1, 1));
                var r = new Rect(0, 0, 10, 10);
                var overlap = r.Intersection(new Rect(5, 5, 10, 10));
                var square = new Polygon([new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 4), new Vector2(0, 4)]);
                var hull = Math.ConvexHull([new Vector2(0, 0), new Vector2(2, 2), new Vector2(4, 0), new Vector2(2, 1), new Vector2(4, 4), new Vector2(0, 4)]);
                var cross = Math.SegmentIntersection(new Vector2(0, 0), new Vector2(4, 4), new Vector2(0, 4), new Vector2(4, 0));
                var points = new Set<Vector2>(); points.Add(new Vector2(1, 2)); points.Add(new Vector2(1, 2));
                return $"{v.Length}|{Math.ApproxEqual(rotated.Y, 1.0, 1e-15)}|{p}|{back}|{moved}|{cube.Invert().Multiply(cube).Equals(Matrix4x4.Identity)}|{overlap}|{r.Contains(new Vector2(10, 5))}|{square.Area}|{square.Contains(new Vector2(2, 2))}|{square.Centroid}|{square.IsConvex}|{hull.Length}|{cross}|{points.Count}|{new Circle(Vector2.Zero, 1).IntersectsRect(new Rect(0.5, 0.5, 2, 2))}";
            }
            """);
        Assert.Equal("5.0|true|(22.0, 2.0)|(1.0, 1.0)|(4.0, 6.0, 8.0)|true|Rect(5.0, 5.0, 5.0×5.0)|false|16.0|true|(2.0, 2.0)|true|4|(2.0, 2.0)|1|true", result);
    }
}

public class StatsTests
{
    private const string Imports = "import Std.Stats;";

    [Fact]
    public void Descriptive_statistics_over_int_and_float_series()
    {
        var result = Run(Imports, """
            string Main() {
                int[] pings = [12, 15, 11, 14, 13, 90, 12];
                float[] xs = [2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0];
                return $"{Stats.Mean(xs)}|{Stats.StdDev(xs)}|{Stats.Median(pings)}|{Stats.Mode(pings)}|{Stats.Percentile(xs, 25.0)}|{Stats.Variance(xs)}|{Stats.SampleVariance([1.0, 2.0, 3.0, 4.0])}";
            }
            """);
        Assert.Equal("5.0|2.0|13.0|12.0|4.0|4.0|1.6666666666666667", result);
    }

    [Fact]
    public void Outlier_detection_finds_the_jittery_relay()
    {
        var result = Run(Imports, """
            string Main() {
                float[] latency = [21.0, 20.5, 22.1, 19.8, 20.9, 87.4, 21.3, 20.2, 21.7, 20.6];
                var z = Stats.Outliers(latency, 2.5);
                var iqr = Stats.IqrOutliers(latency);
                var summary = Stats.Describe(latency);
                return $"{z.Length}:{z[0]}|{iqr.Length}:{iqr[0]}|{summary.Count}|{summary.Max}";
            }
            """);
        Assert.Equal("1:5|1:5|10|87.4", result);
    }

    [Fact]
    public void Regression_correlation_histograms_and_running_stats()
    {
        var result = Run(Imports, """
            string Main() {
                float[] xs = [1.0, 2.0, 3.0, 4.0, 5.0];
                float[] ys = [3.0, 5.0, 7.0, 9.0, 11.0];
                var fit = Stats.LinearRegression(xs, ys);
                var hist = Stats.HistogramOf([1, 2, 2, 3, 3, 3, 4, 10], 3);
                var running = new RunningStats();
                foreach (var x in xs) { running.Add(x); }
                var ma = Stats.MovingAverage(xs, 2);
                return $"{fit.Slope}|{fit.Intercept}|{fit.RSquared}|{fit.Predict(10.0)}|{Stats.Correlation(xs, ys)}|{hist.Counts[0]},{hist.Counts[1]},{hist.Counts[2]}|{running.Mean}|{running.Variance}|{ma.Length}:{ma[0]}";
            }
            """);
        Assert.Equal("2.0|1.0|1.0|21.0|1.0|6,1,1|3.0|2.0|4:1.5", result);
    }

    [Fact]
    public void Empty_series_raise_ArgumentError() =>
        Assert.Contains("ArgumentError", ErrorOf(Imports, """string Main() { float[] none = []; return $"{Stats.Mean(none)}"; }"""));
}
