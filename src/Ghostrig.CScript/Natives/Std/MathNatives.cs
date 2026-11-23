using System.Globalization;
using System.Numerics;
using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

#region Value states

internal static class ValueHash
{
    public static long Of(params double[] parts)
    {
        unchecked
        {
            var h = 17L;
            foreach (var d in parts)
            {
                h = (h * 31) + BitConverter.DoubleToInt64Bits(d == 0 ? 0.0 : d);
            }

            return (long)Mix((ulong)h);
        }
    }

    public static long Of(BigInteger value) => StableHash.Of(value.ToByteArray());
}

internal sealed class BigState(BigInteger value) : ImmutableNativeState, IStructuralState
{
    public BigInteger Value { get; } = value;
    public bool StructurallyEquals(IStructuralState other) => other is BigState b && b.Value == Value;
    public long StructuralHash() => ValueHash.Of(Value);
    public int? StructuralCompare(IStructuralState other) => other is BigState b ? Value.CompareTo(b.Value) : null;
}

internal sealed class RationalState : ImmutableNativeState, IStructuralState
{
    public RationalState(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new ScriptErrorRequest("ArithmeticError", "A rational number cannot have a zero denominator.");
        }

        if (denominator.Sign < 0)
        {
            (numerator, denominator) = (-numerator, -denominator);
        }

        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        (Numerator, Denominator) = gcd.IsOne || gcd.IsZero ? (numerator, denominator) : (numerator / gcd, denominator / gcd);
    }

    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public bool StructurallyEquals(IStructuralState other) => other is RationalState r && r.Numerator == Numerator && r.Denominator == Denominator;
    public long StructuralHash() => ValueHash.Of(Numerator) * 31 + ValueHash.Of(Denominator);
    public int? StructuralCompare(IStructuralState other) => other is RationalState r ? (Numerator * r.Denominator).CompareTo(r.Numerator * Denominator) : null;
}

internal sealed class ComplexState(double re, double im) : ImmutableNativeState, IStructuralState
{
    public double Re { get; } = re;
    public double Im { get; } = im;
    public bool StructurallyEquals(IStructuralState other) => other is ComplexState c && c.Re == Re && c.Im == Im;
    public long StructuralHash() => ValueHash.Of(Re, Im);
}

internal sealed class FixedState(long raw) : ImmutableNativeState, IStructuralState
{
    public long Raw { get; } = raw;
    public bool StructurallyEquals(IStructuralState other) => other is FixedState f && f.Raw == Raw;
    public long StructuralHash() => (long)Mix((ulong)Raw);
    public int? StructuralCompare(IStructuralState other) => other is FixedState f ? Raw.CompareTo(f.Raw) : null;
}

internal sealed class Vec2State(double x, double y) : ImmutableNativeState, IStructuralState
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public bool StructurallyEquals(IStructuralState other) => other is Vec2State v && v.X == X && v.Y == Y;
    public long StructuralHash() => ValueHash.Of(X, Y);
}

internal sealed class Vec3State(double x, double y, double z) : ImmutableNativeState, IStructuralState
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public double Z { get; } = z;
    public bool StructurallyEquals(IStructuralState other) => other is Vec3State v && v.X == X && v.Y == Y && v.Z == Z;
    public long StructuralHash() => ValueHash.Of(X, Y, Z);
}

internal sealed class MatrixState(double[] m) : ImmutableNativeState, IStructuralState
{
    public double[] M { get; } = m;
    public bool StructurallyEquals(IStructuralState other) => other is MatrixState o && o.M.AsSpan().SequenceEqual(M);
    public long StructuralHash() => ValueHash.Of(M);
}

internal sealed class RectState(double x, double y, double w, double h) : ImmutableNativeState, IStructuralState
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public double W { get; } = w;
    public double H { get; } = h;
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool StructurallyEquals(IStructuralState other) => other is RectState r && r.X == X && r.Y == Y && r.W == W && r.H == H;
    public long StructuralHash() => ValueHash.Of(X, Y, W, H);
}

internal sealed class CircleState(double x, double y, double r) : ImmutableNativeState, IStructuralState
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public double R { get; } = r;
    public bool StructurallyEquals(IStructuralState other) => other is CircleState c && c.X == X && c.Y == Y && c.R == R;
    public long StructuralHash() => ValueHash.Of(X, Y, R);
}

internal sealed class PolygonState((double X, double Y)[] points) : ImmutableNativeState
{
    public (double X, double Y)[] Points { get; } = points;

    public double SignedArea
    {
        get
        {
            var sum = 0.0;
            for (var i = 0; i < Points.Length; i++)
            {
                var (a, b) = (Points[i], Points[(i + 1) % Points.Length]);
                sum += (a.X * b.Y) - (b.X * a.Y);
            }

            return sum / 2;
        }
    }
}

#endregion

/// <summary>Host implementations of <c>Std.Math</c> functions and number types.</summary>
internal static class MathNatives
{
    private const string M = "Std.Math";

    public static void Register(NativeRegistry r)
    {
        RegisterFunctions(r.Scope(M));
        RegisterIntegers(r.Scope(M));
        RegisterBigInt(r.Scope($"{M}.BigInt"));
        RegisterRational(r.Scope($"{M}.Rational"));
        RegisterComplex(r.Scope($"{M}.Complex"));
        RegisterFixed(r.Scope($"{M}.Fixed"));
    }

    internal static double D(Value v) => v.Tag == ValueTag.Float ? v.AsFloat : v.Bits;

    private static Value Fl(double d) => Value.FromFloat(d);

    #region functions

    private static void RegisterFunctions(Action<string, StdFunction> G)
    {
        G("AbsInt", (c, a) => Value.FromInt(a[0].Bits == long.MinValue ? a[0].Bits : Math.Abs(a[0].Bits)));
        G("AbsFloat", (c, a) => Fl(Math.Abs(D(a[0]))));
        G("MinInt", (c, a) => Value.FromInt(Math.Min(a[0].Bits, a[1].Bits)));
        G("MinFloat", (c, a) => Fl(double.IsNaN(D(a[0])) || double.IsNaN(D(a[1])) ? double.NaN : Math.Min(D(a[0]), D(a[1]))));
        G("MaxInt", (c, a) => Value.FromInt(Math.Max(a[0].Bits, a[1].Bits)));
        G("MaxFloat", (c, a) => Fl(double.IsNaN(D(a[0])) || double.IsNaN(D(a[1])) ? double.NaN : Math.Max(D(a[0]), D(a[1]))));
        G("ClampInt", (c, a) => a[1].Bits <= a[2].Bits
            ? Value.FromInt(Math.Clamp(a[0].Bits, a[1].Bits, a[2].Bits))
            : throw c.Throw("ArgumentError", "Clamp: min is greater than max."));
        G("ClampFloat", (c, a) => D(a[1]) <= D(a[2])
            ? Fl(Math.Clamp(D(a[0]), D(a[1]), D(a[2])))
            : throw c.Throw("ArgumentError", "Clamp: min is greater than max."));
        G("SignInt", (c, a) => Value.FromInt(Math.Sign(a[0].Bits)));
        G("SignFloat", (c, a) => double.IsNaN(D(a[0])) ? throw c.Throw("ArithmeticError", "Sign of NaN.") : Value.FromInt(Math.Sign(D(a[0]))));

        void Unary(string name, Func<double, double> f, long cost = 4) => G($"{name}/1", (c, a) =>
        {
            c.Charge(cost);
            return Fl(f(D(a[0])));
        });

        G("Pow/2", (c, a) =>
        {
            c.Charge(8);
            return Fl(SoftMath.Pow(D(a[0]), D(a[1])));
        });
        Unary("Sqrt", Math.Sqrt, 1);
        Unary("Cbrt", SoftMath.Cbrt);
        Unary("Exp", SoftMath.Exp);
        Unary("Log", SoftMath.Log);
        G("Log/2", (c, a) =>
        {
            c.Charge(8);
            return Fl(SoftMath.Log(D(a[0])) / SoftMath.Log(D(a[1])));
        });
        Unary("Log2", SoftMath.Log2);
        Unary("Log10", SoftMath.Log10);
        Unary("Floor", Math.Floor, 0);
        Unary("Ceil", Math.Ceiling, 0);
        Unary("Round", x => Math.Round(x, MidpointRounding.AwayFromZero), 0);
        G("Round/2", (c, a) =>
        {
            var digits = Int32(c, a[1].Bits, "Digits", 0, 15);
            var scale = SoftMath.Pow(10, digits);
            return Fl(Math.Round(D(a[0]) * scale, MidpointRounding.AwayFromZero) / scale);
        });
        Unary("Trunc", Math.Truncate, 0);
        G("FloorToInt/1", (c, a) => ToInt(c, Math.Floor(D(a[0]))));
        G("RoundToInt/1", (c, a) => ToInt(c, Math.Round(D(a[0]), MidpointRounding.AwayFromZero)));
        Unary("Sin", SoftMath.Sin);
        Unary("Cos", SoftMath.Cos);
        Unary("Tan", SoftMath.Tan);
        Unary("Asin", SoftMath.Asin);
        Unary("Acos", SoftMath.Acos);
        Unary("Atan", SoftMath.Atan);
        G("Atan2/2", (c, a) =>
        {
            c.Charge(4);
            return Fl(SoftMath.Atan2(D(a[0]), D(a[1])));
        });
        Unary("Sinh", SoftMath.Sinh);
        Unary("Cosh", SoftMath.Cosh);
        Unary("Tanh", SoftMath.Tanh);
        G("Hypot/2", (c, a) => Fl(SoftMath.Hypot(D(a[0]), D(a[1]))));
        Unary("ToRadians", d => d * (SoftMath.Pi / 180), 0);
        Unary("ToDegrees", r => r * (180 / SoftMath.Pi), 0);
        G("Lerp/3", (c, a) => Fl(D(a[0]) + ((D(a[1]) - D(a[0])) * D(a[2]))));
        G("InverseLerp/3", (c, a) => D(a[1]) == D(a[0]) ? Fl(0) : Fl((D(a[2]) - D(a[0])) / (D(a[1]) - D(a[0]))));
        G("Remap/5", (c, a) =>
        {
            var t = D(a[2]) == D(a[1]) ? 0 : (D(a[0]) - D(a[1])) / (D(a[2]) - D(a[1]));
            return Fl(D(a[3]) + ((D(a[4]) - D(a[3])) * t));
        });
        G("ApproxEqual/3", (c, a) => Value.FromBool(Math.Abs(D(a[0]) - D(a[1])) <= D(a[2])));
    }

    private static Value ToInt(NativeContext c, double d) =>
        d >= -9.223372036854775808e18 && d < 9.223372036854775808e18 ? Value.FromInt((long)d) : throw c.Throw("ArithmeticError", $"{d} does not fit in an int.");

    #endregion

    #region integers

    private static long Gcd(long a, long b)
    {
        var (x, y) = ((ulong)Int128.Abs(a), (ulong)Int128.Abs(b));
        while (y != 0)
        {
            (x, y) = (y, x % y);
        }

        return (long)x;
    }

    private static long FloorMod(long a, long m)
    {
        var r = (long)((Int128)a % m);
        return r < 0 ? r + Math.Abs(m) : r;
    }

    private static ulong MulMod(ulong a, ulong b, ulong m) => (ulong)((UInt128)a * b % m);

    private static ulong PowMod(ulong b, ulong e, ulong m)
    {
        ulong result = 1 % m;
        b %= m;
        while (e > 0)
        {
            if ((e & 1) != 0)
            {
                result = MulMod(result, b, m);
            }

            b = MulMod(b, b, m);
            e >>= 1;
        }

        return result;
    }

    internal static bool IsPrime(ulong n)
    {
        if (n < 2)
        {
            return false;
        }

        ReadOnlySpan<ulong> bases = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37];
        foreach (var p in bases)
        {
            if (n % p == 0)
            {
                return n == p;
            }
        }

        var d = n - 1;
        var s = 0;
        while ((d & 1) == 0)
        {
            d >>= 1;
            s++;
        }

        foreach (var a in bases)
        {
            var x = PowMod(a, d, n);
            if (x == 1 || x == n - 1)
            {
                continue;
            }

            var composite = true;
            for (var i = 1; i < s && composite; i++)
            {
                x = MulMod(x, x, n);
                composite = x != n - 1;
            }

            if (composite)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Pollard–Brent rho with deterministic parameters.</summary>
    private static ulong Rho(ulong n)
    {
        if (n % 2 == 0)
        {
            return 2;
        }

        for (ulong constant = 1; ; constant++)
        {
            ulong x = 2, y = 2, d = 1;
            ulong F(ulong v) => (MulMod(v, v, n) + constant) % n;
            while (d == 1)
            {
                x = F(x);
                y = F(F(y));
                d = (ulong)Gcd((long)(x > y ? x - y : y - x), (long)n);
            }

            if (d != n)
            {
                return d;
            }
        }
    }

    private static void Factor(ulong n, List<ulong> factors)
    {
        if (n == 1)
        {
            return;
        }

        foreach (var p in new ulong[] { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 })
        {
            while (n % p == 0)
            {
                factors.Add(p);
                n /= p;
            }
        }

        if (n == 1)
        {
            return;
        }

        if (IsPrime(n))
        {
            factors.Add(n);
            return;
        }

        var d = Rho(n);
        Factor(d, factors);
        Factor(n / d, factors);
    }

    private static void RegisterIntegers(Action<string, StdFunction> G)
    {
        G("Gcd/2", (c, a) => Value.FromInt(Gcd(a[0].Bits, a[1].Bits)));
        G("Lcm/2", (c, a) =>
        {
            var g = Gcd(a[0].Bits, a[1].Bits);
            return Value.FromInt(g == 0 ? 0 : Math.Abs(a[0].Bits / g * a[1].Bits));
        });
        G("ModPow/3", (c, a) =>
        {
            if (a[1].Bits < 0 || a[2].Bits <= 0)
            {
                throw c.Throw("ArgumentError", "ModPow needs a non-negative exponent and a positive modulus.");
            }

            c.Charge(64 - BitOperations.LeadingZeroCount((ulong)a[1].Bits));
            return Value.FromInt((long)PowMod((ulong)FloorMod(a[0].Bits, a[2].Bits), (ulong)a[1].Bits, (ulong)a[2].Bits));
        });
        G("ModInverse/2", (c, a) =>
        {
            var m = a[1].Bits;
            if (m <= 0)
            {
                throw c.Throw("ArgumentError", "The modulus must be positive.");
            }

            (Int128 oldR, Int128 r) = (FloorMod(a[0].Bits, m), m);
            (Int128 oldS, Int128 s) = (1, 0);
            while (r != 0)
            {
                var q = oldR / r;
                (oldR, r) = (r, oldR - (q * r));
                (oldS, s) = (s, oldS - (q * s));
            }

            return oldR != 1 ? throw c.Throw("ArithmeticError", $"{a[0].Bits} has no inverse modulo {m}.") : Value.FromInt((long)(((oldS % m) + m) % m));
        });
        G("IsPrime/1", (c, a) =>
        {
            c.Charge(64);
            return Value.FromBool(a[0].Bits > 1 && IsPrime((ulong)a[0].Bits));
        });
        G("NextPrime/1", (c, a) =>
        {
            var n = Math.Max(2, a[0].Bits + 1);
            while (!IsPrime((ulong)n))
            {
                c.Charge(64);
                n++;
            }

            return Value.FromInt(n);
        });
        G("Factorize/1", (c, a) =>
        {
            if (a[0].Bits < 1)
            {
                throw c.Throw("ArgumentError", "Factorize needs a positive integer.");
            }

            var factors = new List<ulong>();
            Factor((ulong)a[0].Bits, factors);
            factors.Sort();
            c.Charge(64 * (factors.Count + 1));
            return c.NewArray([.. factors.Select(f => Value.FromInt((long)f))]);
        });
        G("ISqrt/1", (c, a) =>
        {
            if (a[0].Bits < 0)
            {
                throw c.Throw("ArithmeticError", "ISqrt of a negative number.");
            }

            var x = (long)Math.Sqrt(a[0].Bits);
            while ((Int128)x * x > a[0].Bits)
            {
                x--;
            }

            while ((Int128)(x + 1) * (x + 1) <= a[0].Bits)
            {
                x++;
            }

            return Value.FromInt(x);
        });
        G("FloorDiv/2", (c, a) =>
        {
            var (x, y) = (a[0].Bits, a[1].Bits);
            if (y == 0)
            {
                throw c.Throw("ArithmeticError", "Division by zero.");
            }

            var q = (long)((Int128)x / y);
            return Value.FromInt((x % y != 0) && ((x < 0) != (y < 0)) ? q - 1 : q);
        });
        G("FloorMod/2", (c, a) =>
        {
            var (x, y) = (a[0].Bits, a[1].Bits);
            if (y == 0)
            {
                throw c.Throw("ArithmeticError", "Division by zero.");
            }

            var r = (long)((Int128)x % y);
            return Value.FromInt(r != 0 && ((r < 0) != (y < 0)) ? r + y : r);
        });
        G("PopCount/1", (c, a) => Value.FromInt(BitOperations.PopCount((ulong)a[0].Bits)));
        G("LeadingZeros/1", (c, a) => Value.FromInt(BitOperations.LeadingZeroCount((ulong)a[0].Bits)));
        G("TrailingZeros/1", (c, a) => Value.FromInt(BitOperations.TrailingZeroCount(a[0].Bits)));
        G("RotateLeft/2", (c, a) => Value.FromInt((long)BitOperations.RotateLeft((ulong)a[0].Bits, (int)(a[1].Bits & 63))));
        G("RotateRight/2", (c, a) => Value.FromInt((long)BitOperations.RotateRight((ulong)a[0].Bits, (int)(a[1].Bits & 63))));
        G("IsPowerOfTwo/1", (c, a) => Value.FromBool(a[0].Bits > 0 && BitOperations.IsPow2(a[0].Bits)));
        G("NextPowerOfTwo/1", (c, a) => a[0].Bits <= 1L << 62
            ? Value.FromInt(a[0].Bits <= 1 ? 1 : (long)BitOperations.RoundUpToPowerOf2((ulong)a[0].Bits))
            : throw c.Throw("ArithmeticError", "No power of two fits."));
        Value Checked(NativeContext c, Func<long> op, string what)
        {
            try
            {
                return Value.FromInt(op());
            }
            catch (OverflowException)
            {
                throw c.Throw("ArithmeticError", $"{what} overflowed.");
            }
        }

        G("CheckedAdd/2", (c, a) => Checked(c, () => checked(a[0].Bits + a[1].Bits), "Addition"));
        G("CheckedSub/2", (c, a) => Checked(c, () => checked(a[0].Bits - a[1].Bits), "Subtraction"));
        G("CheckedMul/2", (c, a) => Checked(c, () => checked(a[0].Bits * a[1].Bits), "Multiplication"));
        G("CheckedNeg/1", (c, a) => Checked(c, () => checked(-a[0].Bits), "Negation"));
        G("Factorial/1", (c, a) =>
        {
            if (a[0].Bits is < 0 or > 20)
            {
                throw c.Throw("ArithmeticError", "Factorial is defined here for 0..20 (use BigInt beyond).");
            }

            var result = 1L;
            for (var i = 2L; i <= a[0].Bits; i++)
            {
                result *= i;
            }

            return Value.FromInt(result);
        });
        G("Binomial/2", (c, a) =>
        {
            var (n, k) = (a[0].Bits, a[1].Bits);
            if (k < 0 || n < 0 || k > n)
            {
                return Value.FromInt(0);
            }

            k = Math.Min(k, n - k);
            Int128 result = 1;
            for (var i = 1L; i <= k; i++)
            {
                result = result * (n - k + i) / i;
                if (result > long.MaxValue)
                {
                    throw c.Throw("ArithmeticError", "Binomial coefficient overflowed.");
                }
            }

            c.Charge(k);
            return Value.FromInt((long)result);
        });
    }

    #endregion

    #region BigInt, Rational, Complex, Fixed

    internal static Value Big(NativeContext c, BigInteger v) => c.NewInstance(c.Class(M, "BigInt"), new BigState(v));

    internal static BigInteger B(NativeContext c, Value v) => c.State<BigState>(v).Value;

    private static long Words(BigInteger v) => 1 + (long)(v.GetBitLength() / 64);

    private static void RegisterBigInt(Action<string, StdFunction> F)
    {
        Value Binary(NativeContext c, Value[] a, Func<BigInteger, BigInteger, BigInteger> op, bool quadratic)
        {
            var (x, y) = (B(c, a[0]), B(c, a[1]));
            c.Charge(quadratic ? Words(x) * Words(y) : Math.Max(Words(x), Words(y)));
            return Big(c, op(x, y));
        }

        BigInteger NonZero(NativeContext c, BigInteger d) => d.IsZero ? throw c.Throw("ArithmeticError", "Division by zero.") : d;

        F("BigInt/1", (c, a) => CollectionNatives.Init(a[0], new BigState(a[1].Bits)));
        F("get_Zero/0", (c, _) => Big(c, BigInteger.Zero));
        F("get_One/0", (c, _) => Big(c, BigInteger.One));
        F("Parse/1", (c, a) =>
        {
            var text = a[0].AsString.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
            var negative = text.StartsWith('-');
            var body = negative || text.StartsWith('+') ? text[1..] : text;
            c.Charge(1 + (body.Length / 16));
            var value = BigInteger.Zero;
            var ok = body.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? BigInteger.TryParse("0" + body[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
                : body.Length > 0 && body.All(char.IsAsciiDigit) && BigInteger.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            return ok ? Big(c, negative ? -value : value) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not an integer.");
        });
        F("FromBytes/1", (c, a) => Big(c, new BigInteger(a[0].As<BytesObject>().Data, isUnsigned: true, isBigEndian: true)));
        F("get_Sign/0", (c, a) => Value.FromInt(B(c, a[0]).Sign));
        F("get_IsZero/0", (c, a) => Value.FromBool(B(c, a[0]).IsZero));
        F("get_IsEven/0", (c, a) => Value.FromBool(B(c, a[0]).IsEven));
        F("get_BitLength/0", (c, a) => Value.FromInt((long)B(c, a[0]).GetBitLength()));
        F("Add/1", (c, a) => Binary(c, a, BigInteger.Add, false));
        F("Subtract/1", (c, a) => Binary(c, a, BigInteger.Subtract, false));
        F("Multiply/1", (c, a) => Binary(c, a, BigInteger.Multiply, true));
        F("Divide/1", (c, a) => Binary(c, a, (x, y) => BigInteger.Divide(x, NonZero(c, y)), true));
        F("Remainder/1", (c, a) => Binary(c, a, (x, y) => BigInteger.Remainder(x, NonZero(c, y)), true));
        F("Mod/1", (c, a) => Binary(c, a, (x, y) =>
        {
            var r = BigInteger.Remainder(x, NonZero(c, y));
            return r.Sign < 0 ? r + BigInteger.Abs(y) : r;
        }, true));
        F("Negate/0", (c, a) => Big(c, -B(c, a[0])));
        F("Abs/0", (c, a) => Big(c, BigInteger.Abs(B(c, a[0]))));
        F("Pow/1", (c, a) =>
        {
            var x = B(c, a[0]);
            var e = Int32(c, a[1].Bits, "Exponent", 0, 1_000_000);
            if ((double)x.GetBitLength() * e > 4_000_000)
            {
                throw c.Throw("ArgumentError", "BigInt.Pow result would exceed 4,000,000 bits.");
            }

            var result = BigInteger.Pow(x, e);
            c.Charge(Words(result) * Words(result) / 4);
            return Big(c, result);
        });
        F("ModPow/2", (c, a) =>
        {
            var (x, e, m) = (B(c, a[0]), B(c, a[1]), B(c, a[2]));
            if (e.Sign < 0 || m.Sign <= 0)
            {
                throw c.Throw("ArgumentError", "ModPow needs a non-negative exponent and a positive modulus.");
            }

            c.Charge((long)e.GetBitLength() * Words(m) * Words(m));
            var r = BigInteger.ModPow(x, e, m);
            return Big(c, r.Sign < 0 ? r + m : r);
        });
        F("ModInverse/1", (c, a) =>
        {
            var (x, m) = (B(c, a[0]), B(c, a[1]));
            if (m.Sign <= 0)
            {
                throw c.Throw("ArgumentError", "The modulus must be positive.");
            }

            c.Charge(Words(m) * Words(m) * 8);
            var (oldR, r) = (((x % m) + m) % m, m);
            var (oldS, s) = (BigInteger.One, BigInteger.Zero);
            while (!r.IsZero)
            {
                var q = oldR / r;
                (oldR, r) = (r, oldR - (q * r));
                (oldS, s) = (s, oldS - (q * s));
            }

            return oldR.IsOne ? Big(c, ((oldS % m) + m) % m) : throw c.Throw("ArithmeticError", "No modular inverse exists.");
        });
        F("Gcd/1", (c, a) => Binary(c, a, BigInteger.GreatestCommonDivisor, true));
        F("Sqrt/0", (c, a) =>
        {
            var n = B(c, a[0]);
            if (n.Sign < 0)
            {
                throw c.Throw("ArithmeticError", "Square root of a negative BigInt.");
            }

            c.Charge(Words(n) * Words(n));
            if (n < 2)
            {
                return Big(c, n);
            }

            var x = BigInteger.One << (int)((n.GetBitLength() + 1) / 2);
            while (true)
            {
                var y = (x + (n / x)) >> 1;
                if (y >= x)
                {
                    return Big(c, x);
                }

                x = y;
            }
        });
        F("ShiftLeft/1", (c, a) => Big(c, B(c, a[0]) << Int32(c, a[1].Bits, "Shift", 0, 4_000_000)));
        F("ShiftRight/1", (c, a) => Big(c, B(c, a[0]) >> Int32(c, a[1].Bits, "Shift", 0, int.MaxValue)));
        F("IsProbablePrime/0", (c, a) =>
        {
            var n = B(c, a[0]);
            c.Charge(Words(n) * Words(n) * 64);
            if (n < 2)
            {
                return Value.False;
            }

            if (n <= ulong.MaxValue)
            {
                return Value.FromBool(IsPrime((ulong)n));
            }

            // Miller–Rabin with the first 24 prime bases: deterministic, error probability below 4^-24.
            int[] bases = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89];
            if (bases.Any(p => n % p == 0))
            {
                return Value.False;
            }

            var d = n - 1;
            var s = 0;
            while (d.IsEven)
            {
                d >>= 1;
                s++;
            }

            foreach (var b in bases)
            {
                var x = BigInteger.ModPow(b, d, n);
                if (x.IsOne || x == n - 1)
                {
                    continue;
                }

                var composite = true;
                for (var i = 1; i < s && composite; i++)
                {
                    x = BigInteger.ModPow(x, 2, n);
                    composite = x != n - 1;
                }

                if (composite)
                {
                    return Value.False;
                }
            }

            return Value.True;
        });
        F("ToInt/0", (c, a) => B(c, a[0]) is var v && v >= long.MinValue && v <= long.MaxValue
            ? Value.FromInt((long)v)
            : throw c.Throw("ArithmeticError", "The BigInt does not fit in an int."));
        F("ToFloat/0", (c, a) => Fl((double)B(c, a[0])));
        F("ToBytes/0", (c, a) => B(c, a[0]).Sign >= 0
            ? Value.FromObject(new BytesObject(B(c, a[0]).IsZero ? [0] : B(c, a[0]).ToByteArray(isUnsigned: true, isBigEndian: true)))
            : throw c.Throw("ArgumentError", "ToBytes needs a non-negative value."));
        F("ToHex/0", (c, a) =>
        {
            var v = B(c, a[0]);
            var hex = BigInteger.Abs(v).ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
            return Value.FromString((v.Sign < 0 ? "-" : string.Empty) + (hex.Length == 0 ? "0" : hex));
        });
        F("CompareTo/1", (c, a) => Value.FromInt(B(c, a[0]).CompareTo(B(c, a[1]))));
        F("Equals/1", (c, a) => Value.FromBool(B(c, a[0]) == B(c, a[1])));
        F("HashCode/0", (c, a) => Value.FromInt(ValueHash.Of(B(c, a[0]))));
        F("ToString/0", (c, a) =>
        {
            var v = B(c, a[0]);
            c.Charge(Words(v) * Words(v) / 4);
            return Value.FromString(v.ToString(CultureInfo.InvariantCulture));
        });
    }

    private static Value Rat(NativeContext c, BigInteger n, BigInteger d) => c.NewInstance(c.Class(M, "Rational"), new RationalState(n, d));

    private static RationalState R(NativeContext c, Value v) => c.State<RationalState>(v);

    /// <summary>Correctly scaled BigInteger division to double (no overflow for huge operands).</summary>
    internal static double Divide(BigInteger n, BigInteger d)
    {
        if (n.IsZero)
        {
            return 0;
        }

        var shift = (int)(64 - (n.GetBitLength() - d.GetBitLength()));
        var q = shift >= 0 ? (n << shift) / d : n / (d << -shift);
        return Math.ScaleB((double)q, -shift);
    }

    private static void RegisterRational(Action<string, StdFunction> F)
    {
        Value Op(NativeContext c, Value[] a, Func<RationalState, RationalState, (BigInteger, BigInteger)> op)
        {
            var (x, y) = (R(c, a[0]), R(c, a[1]));
            c.Charge(Words(x.Denominator) * Words(y.Denominator));
            var (n, d) = op(x, y);
            return Rat(c, n, d);
        }

        F("Rational/2", (c, a) => CollectionNatives.Init(a[0], new RationalState(a[1].Bits, a[2].Bits)));
        F("Parse/1", (c, a) =>
        {
            var text = a[0].AsString.Trim();
            static BigInteger? Integer(string s) =>
                BigInteger.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
            var slash = text.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0 && Integer(text[..slash].Trim()) is { } n && Integer(text[(slash + 1)..].Trim()) is { } d)
            {
                return Rat(c, n, d);
            }

            var dot = text.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0 && Integer(text.Remove(dot, 1)) is { } digits && text[(dot + 1)..].All(char.IsAsciiDigit))
            {
                return Rat(c, digits, BigInteger.Pow(10, text.Length - dot - 1));
            }

            return Integer(text) is { } whole ? Rat(c, whole, 1) : throw c.Throw("FormatError", $"'{text}' is not a rational number.");
        });
        F("FromBig/2", (c, a) => Rat(c, B(c, a[0]), B(c, a[1])));
        F("get_Numerator/0", (c, a) => Big(c, R(c, a[0]).Numerator));
        F("get_Denominator/0", (c, a) => Big(c, R(c, a[0]).Denominator));
        F("Add/1", (c, a) => Op(c, a, (x, y) => ((x.Numerator * y.Denominator) + (y.Numerator * x.Denominator), x.Denominator * y.Denominator)));
        F("Subtract/1", (c, a) => Op(c, a, (x, y) => ((x.Numerator * y.Denominator) - (y.Numerator * x.Denominator), x.Denominator * y.Denominator)));
        F("Multiply/1", (c, a) => Op(c, a, (x, y) => (x.Numerator * y.Numerator, x.Denominator * y.Denominator)));
        F("Divide/1", (c, a) => Op(c, a, (x, y) => y.Numerator.IsZero
            ? throw c.Throw("ArithmeticError", "Division by zero.")
            : (x.Numerator * y.Denominator, x.Denominator * y.Numerator)));
        F("Negate/0", (c, a) => Rat(c, -R(c, a[0]).Numerator, R(c, a[0]).Denominator));
        F("Reciprocal/0", (c, a) => R(c, a[0]).Numerator.IsZero
            ? throw c.Throw("ArithmeticError", "Zero has no reciprocal.")
            : Rat(c, R(c, a[0]).Denominator, R(c, a[0]).Numerator));
        F("Pow/1", (c, a) =>
        {
            var x = R(c, a[0]);
            var e = (int)Math.Clamp(a[1].Bits, -100_000, 100_000);
            var (n, d) = (BigInteger.Pow(x.Numerator, Math.Abs(e)), BigInteger.Pow(x.Denominator, Math.Abs(e)));
            c.Charge(Words(n) + Words(d));
            return e >= 0 ? Rat(c, n, d) : n.IsZero ? throw c.Throw("ArithmeticError", "Division by zero.") : Rat(c, d, n);
        });
        F("Floor/0", (c, a) =>
        {
            var x = R(c, a[0]);
            var q = BigInteger.DivRem(x.Numerator, x.Denominator, out var rem);
            return Big(c, rem.Sign < 0 ? q - 1 : q);
        });
        F("ToFloat/0", (c, a) => Fl(Divide(R(c, a[0]).Numerator, R(c, a[0]).Denominator)));
        F("CompareTo/1", (c, a) => Value.FromInt(Math.Sign(R(c, a[0]).StructuralCompare(R(c, a[1]))!.Value)));
        F("Equals/1", (c, a) => Value.FromBool(R(c, a[0]).StructurallyEquals(R(c, a[1]))));
        F("HashCode/0", (c, a) => Value.FromInt(R(c, a[0]).StructuralHash()));
        F("ToString/0", (c, a) =>
        {
            var x = R(c, a[0]);
            return Value.FromString(x.Denominator.IsOne
                ? x.Numerator.ToString(CultureInfo.InvariantCulture)
                : $"{x.Numerator.ToString(CultureInfo.InvariantCulture)}/{x.Denominator.ToString(CultureInfo.InvariantCulture)}");
        });
    }

    private static Value Cx(NativeContext c, double re, double im) => c.NewInstance(c.Class(M, "Complex"), new ComplexState(re, im));

    private static ComplexState X(NativeContext c, Value v) => c.State<ComplexState>(v);

    private static (double, double) Mul((double Re, double Im) a, (double Re, double Im) b) => ((a.Re * b.Re) - (a.Im * b.Im), (a.Re * b.Im) + (a.Im * b.Re));

    private static void RegisterComplex(Action<string, StdFunction> F)
    {
        F("Complex/2", (c, a) => CollectionNatives.Init(a[0], new ComplexState(D(a[1]), D(a[2]))));
        F("FromPolar/2", (c, a) => Cx(c, D(a[0]) * SoftMath.Cos(D(a[1])), D(a[0]) * SoftMath.Sin(D(a[1]))));
        F("get_Real/0", (c, a) => Fl(X(c, a[0]).Re));
        F("get_Imaginary/0", (c, a) => Fl(X(c, a[0]).Im));
        F("get_Magnitude/0", (c, a) => Fl(SoftMath.Hypot(X(c, a[0]).Re, X(c, a[0]).Im)));
        F("get_Phase/0", (c, a) => Fl(SoftMath.Atan2(X(c, a[0]).Im, X(c, a[0]).Re)));
        F("Add/1", (c, a) => Cx(c, X(c, a[0]).Re + X(c, a[1]).Re, X(c, a[0]).Im + X(c, a[1]).Im));
        F("Subtract/1", (c, a) => Cx(c, X(c, a[0]).Re - X(c, a[1]).Re, X(c, a[0]).Im - X(c, a[1]).Im));
        F("Multiply/1", (c, a) =>
        {
            var (re, im) = Mul((X(c, a[0]).Re, X(c, a[0]).Im), (X(c, a[1]).Re, X(c, a[1]).Im));
            return Cx(c, re, im);
        });
        F("Divide/1", (c, a) =>
        {
            var (x, y) = (X(c, a[0]), X(c, a[1]));
            var d = (y.Re * y.Re) + (y.Im * y.Im);
            return Cx(c, ((x.Re * y.Re) + (x.Im * y.Im)) / d, ((x.Im * y.Re) - (x.Re * y.Im)) / d);
        });
        F("Conjugate/0", (c, a) => Cx(c, X(c, a[0]).Re, -X(c, a[0]).Im));
        F("Exp/0", (c, a) =>
        {
            var x = X(c, a[0]);
            var m = SoftMath.Exp(x.Re);
            return Cx(c, m * SoftMath.Cos(x.Im), m * SoftMath.Sin(x.Im));
        });
        F("Pow/1", (c, a) =>
        {
            var x = X(c, a[0]);
            var n = Math.Abs(a[1].Bits);
            (double, double) result = (1, 0), b = (x.Re, x.Im);
            while (n > 0)
            {
                if ((n & 1) != 0)
                {
                    result = Mul(result, b);
                }

                b = Mul(b, b);
                n >>= 1;
            }

            if (a[1].Bits < 0)
            {
                var d = (result.Item1 * result.Item1) + (result.Item2 * result.Item2);
                result = (result.Item1 / d, -result.Item2 / d);
            }

            return Cx(c, result.Item1, result.Item2);
        });
        F("Equals/1", (c, a) => Value.FromBool(X(c, a[0]).StructurallyEquals(X(c, a[1]))));
        F("HashCode/0", (c, a) => Value.FromInt(X(c, a[0]).StructuralHash()));
        F("ToString/0", (c, a) =>
        {
            var x = X(c, a[0]);
            var sign = x.Im < 0 || (x.Im == 0 && double.IsNegative(x.Im)) ? "-" : "+";
            return Value.FromString($"{ValueFormatting.FormatFloat(x.Re)} {sign} {ValueFormatting.FormatFloat(Math.Abs(x.Im))}i");
        });
    }

    private const double FixedOne = 4294967296.0;

    private static Value Fx(NativeContext c, long raw) => c.NewInstance(c.Class(M, "Fixed"), new FixedState(raw));

    private static long Raw(NativeContext c, Value v) => c.State<FixedState>(v).Raw;

    internal static string FixedText(long raw)
    {
        var magnitude = BigInteger.Abs(raw);
        var whole = magnitude >> 32;
        var fraction = magnitude & uint.MaxValue;
        var digits = ((fraction * BigInteger.Pow(10, 10)) + (BigInteger.One << 31)) >> 32;
        if (digits >= BigInteger.Pow(10, 10))
        {
            whole += 1;
            digits -= BigInteger.Pow(10, 10);
        }

        var text = whole.ToString(CultureInfo.InvariantCulture);
        var frac = digits.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0').TrimEnd('0');
        return (raw < 0 ? "-" : string.Empty) + text + (frac.Length > 0 ? "." + frac : ".0");
    }

    private static void RegisterFixed(Action<string, StdFunction> F)
    {
        F("Fixed/1", (c, a) => CollectionNatives.Init(a[0], new FixedState(unchecked(a[1].Bits << 32))));
        F("FromFloat/1", (c, a) =>
        {
            var scaled = Math.Round(D(a[0]) * FixedOne, MidpointRounding.ToEven);
            return scaled is >= -9.223372036854775808e18 and < 9.223372036854775808e18 ? Fx(c, (long)scaled) : throw c.Throw("ArithmeticError", "Value is outside the Fixed range.");
        });
        F("FromRaw/1", (c, a) => Fx(c, a[0].Bits));
        F("Parse/1", (c, a) =>
        {
            var text = a[0].AsString.Trim();
            var negative = text.StartsWith('-');
            var body = negative ? text[1..] : text;
            var dot = body.IndexOf('.', StringComparison.Ordinal);
            var (wholeText, fracText) = dot < 0 ? (body, string.Empty) : (body[..dot], body[(dot + 1)..]);
            if (wholeText.Length == 0 || !wholeText.All(char.IsAsciiDigit) || !fracText.All(char.IsAsciiDigit))
            {
                throw c.Throw("FormatError", $"'{text}' is not a fixed-point number.");
            }

            var scale = BigInteger.Pow(10, fracText.Length);
            var numerator = (BigInteger.Parse(wholeText + fracText, CultureInfo.InvariantCulture) << 32) + (scale / 2);
            var raw = numerator / scale;
            return Fx(c, (long)(negative ? -raw : raw));
        });
        F("get_Raw/0", (c, a) => Value.FromInt(Raw(c, a[0])));
        F("Add/1", (c, a) => Fx(c, unchecked(Raw(c, a[0]) + Raw(c, a[1]))));
        F("Subtract/1", (c, a) => Fx(c, unchecked(Raw(c, a[0]) - Raw(c, a[1]))));
        F("Multiply/1", (c, a) => Fx(c, (long)(((Int128)Raw(c, a[0]) * Raw(c, a[1])) >> 32)));
        F("Divide/1", (c, a) => Raw(c, a[1]) == 0
            ? throw c.Throw("ArithmeticError", "Division by zero.")
            : Fx(c, (long)(((Int128)Raw(c, a[0]) << 32) / Raw(c, a[1]))));
        F("Negate/0", (c, a) => Fx(c, unchecked(-Raw(c, a[0]))));
        F("Abs/0", (c, a) => Fx(c, Math.Abs(Raw(c, a[0]))));
        F("Floor/0", (c, a) => Fx(c, Raw(c, a[0]) & ~0xFFFFFFFFL));
        F("Sqrt/0", (c, a) =>
        {
            var raw = Raw(c, a[0]);
            if (raw < 0)
            {
                throw c.Throw("ArithmeticError", "Square root of a negative number.");
            }

            var n = (UInt128)(ulong)raw << 32;
            UInt128 x = (UInt128)Math.Sqrt((double)n);
            while (x * x > n)
            {
                x--;
            }

            while ((x + 1) * (x + 1) <= n)
            {
                x++;
            }

            return Fx(c, (long)x);
        });
        F("ToInt/0", (c, a) => Value.FromInt(Raw(c, a[0]) >> 32));
        F("ToFloat/0", (c, a) => Fl(Raw(c, a[0]) / FixedOne));
        F("CompareTo/1", (c, a) => Value.FromInt(Raw(c, a[0]).CompareTo(Raw(c, a[1]))));
        F("Equals/1", (c, a) => Value.FromBool(Raw(c, a[0]) == Raw(c, a[1])));
        F("HashCode/0", (c, a) => Value.FromInt((long)Mix((ulong)Raw(c, a[0]))));
        F("ToString/0", (c, a) => Value.FromString(FixedText(Raw(c, a[0]))));
    }

    #endregion
}
