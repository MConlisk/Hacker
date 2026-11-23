using System.Numerics;

namespace Ghostrig.CScript.Natives.Std;

/// <summary>
/// Deterministic software implementations of the transcendental functions (Doc 7 D7). They use only IEEE-754 operations
/// that are correctly rounded on every platform (+, −, ×, ÷, √, scaling), following the fdlibm algorithms, so results are
/// bit-identical across machines. Accuracy is within about 1 ulp (pow: a few ulp).
/// </summary>
public static class SoftMath
{
    public const double Pi = 3.14159265358979311600e+00;
    public const double E = 2.71828182845904509080e+00;
    private const double PiLo = 1.2246467991473531772e-16;
    private const double Ln2Hi = 6.93147180369123816490e-01;
    private const double Ln2Lo = 1.90821492927058770002e-10;
    private const double InvLn2 = 1.44269504088896338700e+00;

    private static int High(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);

    private static uint Low(double x) => (uint)BitConverter.DoubleToInt64Bits(x);

    private static double WithHigh(double x, int high) =>
        BitConverter.Int64BitsToDouble(((long)high << 32) | Low(x));

    private static double FromWords(int high, uint low) => BitConverter.Int64BitsToDouble(((long)high << 32) | low);

    #region exp, log

    public static double Exp(double x)
    {
        const double oThreshold = 7.09782712893383973096e+02;
        const double uThreshold = -7.45133219101941108420e+02;
        const double p1 = 1.66666666666666019037e-01, p2 = -2.77777777770155933842e-03, p3 = 6.61375632143793436117e-05,
            p4 = -1.65339022054652515390e-06, p5 = 4.13813679705723846039e-08;

        if (double.IsNaN(x))
        {
            return x;
        }

        if (x > oThreshold)
        {
            return double.PositiveInfinity;
        }

        if (x < uThreshold)
        {
            return 0;
        }

        var hx = High(x) & 0x7fffffff;
        var negative = x < 0;
        double hi = 0, lo = 0;
        int k;
        if (hx > 0x3fd62e42)
        {
            if (hx < 0x3FF0A2B2)
            {
                hi = x - (negative ? -Ln2Hi : Ln2Hi);
                lo = negative ? -Ln2Lo : Ln2Lo;
                k = negative ? -1 : 1;
            }
            else
            {
                k = (int)((InvLn2 * x) + (negative ? -0.5 : 0.5));
                double t = k;
                hi = x - (t * Ln2Hi);
                lo = t * Ln2Lo;
            }

            x = hi - lo;
        }
        else if (hx < 0x3e300000)
        {
            return 1 + x;
        }
        else
        {
            k = 0;
        }

        var tt = x * x;
        var c = x - (tt * (p1 + (tt * (p2 + (tt * (p3 + (tt * (p4 + (tt * p5)))))))));
        if (k == 0)
        {
            return 1 - (((x * c) / (c - 2.0)) - x);
        }

        var y = 1 - ((lo - ((x * c) / (2.0 - c))) - hi);
        return Math.ScaleB(y, k);
    }

    public static double Log(double x)
    {
        const double two54 = 1.80143985094819840000e+16;
        const double lg1 = 6.666666666666735130e-01, lg2 = 3.999999999940941908e-01, lg3 = 2.857142874366239149e-01,
            lg4 = 2.222219843214978396e-01, lg5 = 1.818357216161805012e-01, lg6 = 1.531383769920937332e-01, lg7 = 1.479819860511658591e-01;

        var hx = High(x);
        var lx = Low(x);
        var k = 0;
        if (hx < 0x00100000)
        {
            if (((hx & 0x7fffffff) | (int)lx) == 0)
            {
                return double.NegativeInfinity;
            }

            if (hx < 0)
            {
                return double.NaN;
            }

            k -= 54;
            x *= two54;
            hx = High(x);
        }

        if (hx >= 0x7ff00000)
        {
            return x + x;
        }

        k += (hx >> 20) - 1023;
        hx &= 0x000fffff;
        var i = (hx + 0x95f64) & 0x100000;
        x = WithHigh(x, hx | (i ^ 0x3ff00000));
        k += i >> 20;
        var f = x - 1.0;
        double dk;
        if ((0x000fffff & (2 + hx)) < 3)
        {
            if (f == 0)
            {
                if (k == 0)
                {
                    return 0;
                }

                dk = k;
                return (dk * Ln2Hi) + (dk * Ln2Lo);
            }

            var rr = f * f * (0.5 - (0.33333333333333333 * f));
            if (k == 0)
            {
                return f - rr;
            }

            dk = k;
            return (dk * Ln2Hi) - ((rr - (dk * Ln2Lo)) - f);
        }

        var s = f / (2.0 + f);
        dk = k;
        var z = s * s;
        i = hx - 0x6147a;
        var w = z * z;
        var j = 0x6b851 - hx;
        var t1 = w * (lg2 + (w * (lg4 + (w * lg6))));
        var t2 = z * (lg1 + (w * (lg3 + (w * (lg5 + (w * lg7))))));
        i |= j;
        var r = t2 + t1;
        if (i > 0)
        {
            var hfsq = 0.5 * f * f;
            return k == 0 ? f - (hfsq - (s * (hfsq + r))) : (dk * Ln2Hi) - ((hfsq - ((s * (hfsq + r)) + (dk * Ln2Lo))) - f);
        }

        return k == 0 ? f - (s * (f - r)) : (dk * Ln2Hi) - (((s * (f - r)) - (dk * Ln2Lo)) - f);
    }

    /// <summary>Splits a finite positive x into m·2^k with m in [1, 2).</summary>
    private static (double Mantissa, int Exponent) Frexp(double x)
    {
        var k = 0;
        if (x < 2.2250738585072014e-308)
        {
            x *= 1.8014398509481984e16;
            k = -54;
        }

        var bits = BitConverter.DoubleToInt64Bits(x);
        k += (int)((bits >> 52) & 0x7ff) - 1023;
        return (BitConverter.Int64BitsToDouble((bits & unchecked((long)0x800FFFFFFFFFFFFFUL)) | 0x3FF0000000000000L), k);
    }

    /// <summary>x = m·2^k with m in [√½, √2), so log m never cancels against k (fdlibm's normalisation).</summary>
    private static (double Mantissa, int Exponent) Balanced(double x)
    {
        var (m, k) = Frexp(x);
        return m > 1.4142135623730951 ? (m / 2, k + 1) : (m, k);
    }

    public static double Log2(double x)
    {
        if (!(x > 0) || double.IsInfinity(x))
        {
            return Log(x);
        }

        var (m, k) = Balanced(x);
        return k + (Log(m) * InvLn2);
    }

    private static readonly double[] PowersOfTen = [.. Enumerable.Range(0, 23).Select(i => double.Parse($"1e{i}", System.Globalization.CultureInfo.InvariantCulture))];

    public static double Log10(double x)
    {
        const double invLn10 = 4.34294481903251816668e-01;
        const double log10Of2Hi = 3.01029995663611771306e-01;
        const double log10Of2Lo = 3.69423907715893078616e-13;
        if (!(x > 0) || double.IsInfinity(x))
        {
            return Log(x);
        }

        var (m, k) = Balanced(x);
        var result = (k * log10Of2Hi) + ((k * log10Of2Lo) + (invLn10 * Log(m)));
        var n = Math.Round(result);
        return n is >= 0 and <= 22 && PowersOfTen[(int)n] == x ? n : result;
    }

    #endregion

    #region trigonometry

    private const double InvPio2 = 6.36619772367581382433e-01;
    private const double Pio2_1 = 1.57079632673412561417e+00, Pio2_1t = 6.07710050650619224932e-11;
    private const double Pio2_2 = 6.07710050630396597660e-11, Pio2_2t = 2.02226624879595063154e-21;
    private const double Pio2_3 = 2.02226624871116645580e-21, Pio2_3t = 8.47842766036889956997e-32;

    private static double KernelSin(double x, double y, bool hasTail)
    {
        const double s1 = -1.66666666666666324348e-01, s2 = 8.33333333332248946124e-03, s3 = -1.98412698298579493134e-04,
            s4 = 2.75573137070700676789e-06, s5 = -2.50507602534068634195e-08, s6 = 1.58969099521155010221e-10;
        var z = x * x;
        var v = z * x;
        var r = s2 + (z * (s3 + (z * (s4 + (z * (s5 + (z * s6)))))));
        return !hasTail ? x + (v * (s1 + (z * r))) : x - (((z * ((0.5 * y) - (v * r))) - y) - (v * s1));
    }

    private static double KernelCos(double x, double y)
    {
        const double c1 = 4.16666666666666019037e-02, c2 = -1.38888888888741095749e-03, c3 = 2.48015872894767294178e-05,
            c4 = -2.75573143513906633035e-07, c5 = 2.08757232129817482790e-09, c6 = -1.13596475577881948265e-11;
        var ix = High(x) & 0x7fffffff;
        if (ix < 0x3e400000 && (int)x == 0)
        {
            return 1;
        }

        var z = x * x;
        var r = z * (c1 + (z * (c2 + (z * (c3 + (z * (c4 + (z * (c5 + (z * c6))))))))));
        if (ix < 0x3FD33333)
        {
            return 1 - ((0.5 * z) - ((z * r) - (x * y)));
        }

        var qx = ix > 0x3fe90000 ? 0.28125 : FromWords(ix - 0x00200000, 0);
        var hz = (0.5 * z) - qx;
        var a = 1 - qx;
        return a - (hz - ((z * r) - (x * y)));
    }

    /// <summary>Reduces x to y0 + y1 in [−π/4, π/4] and returns the quadrant count n (x ≈ nπ/2 + y).</summary>
    private static int ReducePio2(double x, out double y0, out double y1)
    {
        var hx = High(x);
        var ix = hx & 0x7fffffff;
        if (ix <= 0x413921fb)
        {
            // |x| ≤ 2^19·π/2: Cody–Waite reduction with a three-part π/2.
            var t = Math.Abs(x);
            var n = (int)((t * InvPio2) + 0.5);
            double fn = n;
            var r = t - (fn * Pio2_1);
            var w = fn * Pio2_1t;
            var j = ix >> 20;
            y0 = r - w;
            var i = j - ((High(y0) >> 20) & 0x7ff);
            if (i > 16)
            {
                t = r;
                w = fn * Pio2_2;
                r = t - w;
                w = (fn * Pio2_2t) - ((t - r) - w);
                y0 = r - w;
                i = j - ((High(y0) >> 20) & 0x7ff);
                if (i > 49)
                {
                    t = r;
                    w = fn * Pio2_3;
                    r = t - w;
                    w = (fn * Pio2_3t) - ((t - r) - w);
                    y0 = r - w;
                }
            }

            y1 = (r - y0) - w;
            if (hx < 0)
            {
                y0 = -y0;
                y1 = -y1;
                return -n;
            }

            return n;
        }

        return ReduceLarge(x, out y0, out y1);
    }

    private const int TwoOverPiBits = 1400;

    /// <summary>⌊(2/π)·2^1400⌋, computed exactly once with Machin's formula (no hand-copied constant tables).</summary>
    private static readonly Lazy<BigInteger> TwoOverPiFixed = new(() =>
    {
        var precision = TwoOverPiBits + 64;
        var one = BigInteger.One << precision;
        BigInteger ArcTanInverse(int k)
        {
            var sum = BigInteger.Zero;
            var term = one / k;
            var k2 = k * k;
            for (var n = 1; !term.IsZero; n += 2)
            {
                sum += ((n / 2) % 2 == 0 ? term : -term) / n;
                term /= k2;
            }

            return sum;
        }

        var pi = (16 * ArcTanInverse(5)) - (4 * ArcTanInverse(239)); // π · 2^precision
        return (BigInteger.One << (precision + TwoOverPiBits + 1)) / pi;
    });

    /// <summary>Exact Payne–Hanek style reduction for huge arguments using arbitrary-precision 2/π.</summary>
    private static int ReduceLarge(double x, out double y0, out double y1)
    {
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(x));
        var exponent = (int)((bits >> 52) & 0x7ff) - 1075;
        var mantissa = (bits & 0xFFFFFFFFFFFFFL) | 0x10000000000000L;
        var product = mantissa * TwoOverPiFixed.Value; // x·(2/π) scaled by 2^(1400 − exponent)
        var shift = TwoOverPiBits - exponent;
        var quadrant = (int)((product >> shift) & 3);
        const int fractionBits = 120;
        var fraction = (product >> (shift - fractionBits)) & ((BigInteger.One << fractionBits) - 1);
        if (fraction >= BigInteger.One << (fractionBits - 1))
        {
            fraction -= BigInteger.One << fractionBits;
            quadrant = (quadrant + 1) & 3;
        }

        // f = fraction / 2^120 in [−0.5, 0.5); r = f·π/2 evaluated in double-double.
        var negative = fraction.Sign < 0;
        var magnitude = BigInteger.Abs(fraction);
        var fh = Math.ScaleB((double)(magnitude >> (fractionBits - 60)), -60);
        var fl = Math.ScaleB((double)(magnitude - ((magnitude >> (fractionBits - 60)) << (fractionBits - 60))), -fractionBits);
        const double pio2Hi = 1.5707963267948966, pio2Lo = 6.123233995736766e-17;
        var (ph, pl) = TwoProduct(fh, pio2Hi);
        pl += (fh * pio2Lo) + (fl * pio2Hi);
        y0 = ph + pl;
        y1 = pl - (y0 - ph);
        if (negative)
        {
            y0 = -y0;
            y1 = -y1;
        }

        if (x < 0)
        {
            y0 = -y0;
            y1 = -y1;
            quadrant = (4 - quadrant) & 3;
        }

        return quadrant;
    }

    /// <summary>Dekker's exact product: a·b = p + e.</summary>
    private static (double P, double E) TwoProduct(double a, double b)
    {
        const double split = 134217729.0;
        var p = a * b;
        var ca = split * a;
        var ah = ca - (ca - a);
        var al = a - ah;
        var cb = split * b;
        var bh = cb - (cb - b);
        var bl = b - bh;
        var e = (((ah * bh) - p) + (ah * bl) + (al * bh)) + (al * bl);
        return (p, e);
    }

    public static double Sin(double x)
    {
        var ix = High(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb)
        {
            return KernelSin(x, 0, false);
        }

        if (ix >= 0x7ff00000)
        {
            return double.NaN;
        }

        var n = ReducePio2(x, out var y0, out var y1);
        return (n & 3) switch
        {
            0 => KernelSin(y0, y1, true),
            1 => KernelCos(y0, y1),
            2 => -KernelSin(y0, y1, true),
            _ => -KernelCos(y0, y1),
        };
    }

    public static double Cos(double x)
    {
        var ix = High(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb)
        {
            return KernelCos(x, 0);
        }

        if (ix >= 0x7ff00000)
        {
            return double.NaN;
        }

        var n = ReducePio2(x, out var y0, out var y1);
        return (n & 3) switch
        {
            0 => KernelCos(y0, y1),
            1 => -KernelSin(y0, y1, true),
            2 => -KernelCos(y0, y1),
            _ => KernelSin(y0, y1, true),
        };
    }

    public static double Tan(double x)
    {
        var ix = High(x) & 0x7fffffff;
        if (ix >= 0x7ff00000)
        {
            return double.NaN;
        }

        double y0 = x, y1 = 0;
        var n = ix <= 0x3fe921fb ? 0 : ReducePio2(x, out y0, out y1);
        var s = KernelSin(y0, y1, true);
        var c = KernelCos(y0, y1);
        return (n & 1) == 0 ? s / c : -c / s;
    }

    public static double Atan(double x)
    {
        ReadOnlySpan<double> atanHi = [4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00];
        ReadOnlySpan<double> atanLo = [2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17];
        ReadOnlySpan<double> aT =
        [
            3.33333333333329318027e-01, -1.99999999998764832476e-01, 1.42857142725034663711e-01, -1.11111104054623557880e-01,
            9.09088713343650656196e-02, -7.69187620504482999495e-02, 6.66107313738753120669e-02, -5.83357013379057348645e-02,
            4.97687799461593236017e-02, -3.65315727442169155270e-02, 1.62858201153657823623e-02,
        ];

        var hx = High(x);
        var ix = hx & 0x7fffffff;
        int id;
        if (ix >= 0x44100000)
        {
            if (double.IsNaN(x))
            {
                return x;
            }

            return hx > 0 ? atanHi[3] + atanLo[3] : -atanHi[3] - atanLo[3];
        }

        if (ix < 0x3fdc0000)
        {
            if (ix < 0x3e200000)
            {
                return x;
            }

            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)
            {
                if (ix < 0x3fe60000)
                {
                    id = 0;
                    x = ((2.0 * x) - 1) / (2.0 + x);
                }
                else
                {
                    id = 1;
                    x = (x - 1) / (x + 1);
                }
            }
            else if (ix < 0x40038000)
            {
                id = 2;
                x = (x - 1.5) / (1 + (1.5 * x));
            }
            else
            {
                id = 3;
                x = -1.0 / x;
            }
        }

        var z = x * x;
        var w = z * z;
        var s1 = z * (aT[0] + (w * (aT[2] + (w * (aT[4] + (w * (aT[6] + (w * (aT[8] + (w * aT[10]))))))))));
        var s2 = w * (aT[1] + (w * (aT[3] + (w * (aT[5] + (w * (aT[7] + (w * aT[9]))))))));
        if (id < 0)
        {
            return x - (x * (s1 + s2));
        }

        z = atanHi[id] - (((x * (s1 + s2)) - atanLo[id]) - x);
        return hx < 0 ? -z : z;
    }

    public static double Atan2(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return double.NaN;
        }

        if (y == 0)
        {
            return x > 0 || (x == 0 && !double.IsNegative(x)) ? y : Math.CopySign(Pi, y);
        }

        if (x == 0)
        {
            return Math.CopySign(Pi / 2, y);
        }

        if (double.IsInfinity(x))
        {
            if (double.IsInfinity(y))
            {
                return Math.CopySign(x > 0 ? Pi / 4 : 3 * Pi / 4, y);
            }

            return x > 0 ? Math.CopySign(0.0, y) : Math.CopySign(Pi, y);
        }

        if (double.IsInfinity(y))
        {
            return Math.CopySign(Pi / 2, y);
        }

        var z = Atan(Math.Abs(y / x));
        return x > 0 ? Math.CopySign(z, y) : Math.CopySign(Pi - (z - PiLo), y);
    }

    public static double Asin(double x) => Math.Abs(x) > 1 ? double.NaN : Atan2(x, Math.Sqrt((1 - x) * (1 + x)));

    public static double Acos(double x) => Math.Abs(x) > 1 ? double.NaN : Atan2(Math.Sqrt((1 - x) * (1 + x)), x);

    #endregion

    #region hyperbolic, power, roots

    public static double Sinh(double x)
    {
        var ax = Math.Abs(x);
        if (ax < 0.5)
        {
            // Taylor series avoids cancellation near zero.
            var x2 = x * x;
            return x * (1 + (x2 / 6 * (1 + (x2 / 20 * (1 + (x2 / 42 * (1 + (x2 / 72 * (1 + (x2 / 110 * (1 + (x2 / 156))))))))))));
        }

        if (ax > 710)
        {
            return Math.CopySign(double.PositiveInfinity, x);
        }

        var e = Exp(ax > 709 ? ax - Ln2Hi : ax);
        var result = ax > 709 ? e : (e - (1 / e)) / 2;
        return Math.CopySign(result, x);
    }

    public static double Cosh(double x)
    {
        var ax = Math.Abs(x);
        if (ax > 710)
        {
            return double.PositiveInfinity;
        }

        if (ax > 709)
        {
            return Exp(ax - Ln2Hi);
        }

        var e = Exp(ax);
        return (e + (1 / e)) / 2;
    }

    public static double Tanh(double x)
    {
        var ax = Math.Abs(x);
        if (double.IsNaN(x))
        {
            return x;
        }

        if (ax > 22)
        {
            return Math.CopySign(1, x);
        }

        if (ax < 0.5)
        {
            return Sinh(x) / Cosh(x);
        }

        var e = Exp(2 * ax);
        return Math.CopySign((e - 1) / (e + 1), x);
    }

    public static double Pow(double x, double y)
    {
        if (y == 0 || x == 1)
        {
            return 1;
        }

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return double.NaN;
        }

        var yIsInteger = Math.Floor(y) == y && !double.IsInfinity(y);
        var yIsOdd = yIsInteger && Math.Abs(y) < 9007199254740992.0 && ((long)y & 1) == 1;

        if (double.IsInfinity(y))
        {
            var ax = Math.Abs(x);
            if (ax == 1)
            {
                return 1;
            }

            return (ax > 1) == (y > 0) ? double.PositiveInfinity : 0;
        }

        if (x == 0)
        {
            if (y < 0)
            {
                return yIsOdd ? Math.CopySign(double.PositiveInfinity, x) : double.PositiveInfinity;
            }

            return yIsOdd ? x : 0;
        }

        if (double.IsInfinity(x))
        {
            if (x > 0)
            {
                return y > 0 ? double.PositiveInfinity : 0;
            }

            var magnitude = y > 0 ? double.PositiveInfinity : 0;
            return yIsOdd ? -magnitude : magnitude;
        }

        if (x < 0 && !yIsInteger)
        {
            return double.NaN;
        }

        if (yIsInteger && Math.Abs(y) <= 64)
        {
            // Exact-ish repeated squaring for small integer powers (2^10 == 1024 exactly).
            var n = (long)Math.Abs(y);
            double result = 1, b = x;
            while (n > 0)
            {
                if ((n & 1) != 0)
                {
                    result *= b;
                }

                b *= b;
                n >>= 1;
            }

            return y < 0 ? 1 / result : result;
        }

        var magnitudeResult = Exp(y * Log(Math.Abs(x)));
        return x < 0 && yIsOdd ? -magnitudeResult : magnitudeResult;
    }

    public static double Cbrt(double x)
    {
        if (x == 0 || double.IsNaN(x) || double.IsInfinity(x))
        {
            return x;
        }

        var ax = Math.Abs(x);
        var (m, k) = Frexp(ax);
        var q = k / 3;
        var rem = k - (3 * q);
        if (rem < 0)
        {
            rem += 3;
            q--;
        }

        m = Math.ScaleB(m, rem); // m in [1, 8)
        var y = 1 + (m / 6);
        for (var i = 0; i < 6; i++)
        {
            y -= ((y * y * y) - m) / (3 * y * y);
        }

        return Math.CopySign(Math.ScaleB(y, q), x);
    }

    public static double Hypot(double x, double y)
    {
        if (double.IsInfinity(x) || double.IsInfinity(y))
        {
            return double.PositiveInfinity;
        }

        (x, y) = (Math.Abs(x), Math.Abs(y));
        if (x < y)
        {
            (x, y) = (y, x);
        }

        if (x == 0)
        {
            return 0;
        }

        var r = y / x;
        return x * Math.Sqrt(1 + (r * r));
    }

    #endregion
}
