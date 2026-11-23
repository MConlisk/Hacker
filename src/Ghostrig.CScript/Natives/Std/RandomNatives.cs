using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

/// <summary>xoshiro256** (Blackman &amp; Vigna), seeded by SplitMix64. Bit-identical on every platform.</summary>
internal sealed class RandomState : NativeState
{
    private ulong _s0, _s1, _s2, _s3;

    public RandomState(long seed)
    {
        Seed = seed;
        var x = (ulong)seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    private RandomState(long seed, ulong s0, ulong s1, ulong s2, ulong s3) => (Seed, _s0, _s1, _s2, _s3) = (seed, s0, s1, s2, s3);

    public long Seed { get; }

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        return Mix(x);
    }

    public ulong Next()
    {
        var result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, bound) by Lemire's multiply-and-reject (no modulo bias).</summary>
    public ulong Below(ulong bound)
    {
        if (bound == 0)
        {
            return 0;
        }

        var threshold = (0UL - bound) % bound;
        while (true)
        {
            var product = Math.BigMul(Next(), bound, out var low);
            if (low >= threshold)
            {
                return product;
            }
        }
    }

    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Standard normal via the Marsaglia polar method (only +, ×, √ and a software logarithm: deterministic).</summary>
    public double Gaussian()
    {
        while (true)
        {
            var u = (2 * NextDouble()) - 1;
            var v = (2 * NextDouble()) - 1;
            var s = (u * u) + (v * v);
            if (s is > 0 and < 1)
            {
                return u * Math.Sqrt(-2 * SoftMath.Log(s) / s);
            }
        }
    }

    /// <summary>The xoshiro256 jump: returns a generator 2^128 steps ahead and advances this one past it.</summary>
    public RandomState Fork()
    {
        ulong[] jump = [0x180ec6d33cfd0abaUL, 0xd5a61266f0c9392cUL, 0xa9582618e03fc9aaUL, 0x39abdc4529b1661cUL];
        ulong s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        foreach (var word in jump)
        {
            for (var b = 0; b < 64; b++)
            {
                if ((word & (1UL << b)) != 0)
                {
                    s0 ^= _s0;
                    s1 ^= _s1;
                    s2 ^= _s2;
                    s3 ^= _s3;
                }

                Next();
            }
        }

        var fork = new RandomState(Seed, _s0, _s1, _s2, _s3);
        (_s0, _s1, _s2, _s3) = (s0, s1, s2, s3);
        return fork;
    }

    protected internal override object CaptureSnapshot() => (_s0, _s1, _s2, _s3);

    protected internal override void RestoreSnapshot(object snapshot) => (_s0, _s1, _s2, _s3) = ((ulong, ulong, ulong, ulong))snapshot;
}

/// <summary>Host implementations of <c>Std.Random</c>.</summary>
internal static class RandomNatives
{
    public static void Register(NativeRegistry r)
    {
        var F = r.Scope("Std.Random.Random");
        static RandomState R(NativeContext c, Value v)
        {
            var state = c.State<RandomState>(v);
            c.Mutating(state);
            return state;
        }

        F("Random/1", (c, a) => CollectionNatives.Init(a[0], new RandomState(a[1].Bits)));
        F("get_Seed/0", (c, a) => Value.FromInt(c.State<RandomState>(a[0]).Seed));
        F("NextInt/0", (c, a) => Value.FromInt((long)R(c, a[0]).Next()));
        F("NextInt/1", (c, a) => a[1].Bits > 0
            ? Value.FromInt((long)R(c, a[0]).Below((ulong)a[1].Bits))
            : throw c.Throw("ArgumentError", "maxExclusive must be positive."));
        F("NextInt/2", (c, a) => a[2].Bits > a[1].Bits
            ? Value.FromInt(unchecked(a[1].Bits + (long)R(c, a[0]).Below(unchecked((ulong)(a[2].Bits - a[1].Bits)))))
            : throw c.Throw("ArgumentError", "maxExclusive must be greater than min."));
        F("NextFloat/0", (c, a) => Value.FromFloat(R(c, a[0]).NextDouble()));
        F("NextFloat/2", (c, a) => Value.FromFloat(a[1].AsFloat + ((a[2].AsFloat - a[1].AsFloat) * R(c, a[0]).NextDouble())));
        F("NextBool/0", (c, a) => Value.FromBool((R(c, a[0]).Next() >> 63) != 0));
        F("Chance/1", (c, a) => Value.FromBool(R(c, a[0]).NextDouble() < a[1].AsFloat));
        F("NextBytes/1", (c, a) =>
        {
            var count = Int32(c, a[1].Bits, "Byte count", 0, 16_000_000);
            var rng = R(c, a[0]);
            var data = new byte[count];
            for (var i = 0; i < count; i += 8)
            {
                var word = rng.Next();
                for (var j = 0; j < 8 && i + j < count; j++)
                {
                    data[i + j] = (byte)(word >> (8 * j));
                }
            }

            c.Charge(count / 8);
            return Value.FromObject(new BytesObject(data));
        });
        F("Choice/1", (c, a) =>
        {
            var items = Items(a[1]);
            return items.Length > 0 ? items[(int)R(c, a[0]).Below((ulong)items.Length)] : throw Empty(c, "array");
        });
        F("Sample/2", (c, a) =>
        {
            var items = Items(a[1]);
            var k = Int32(c, a[2].Bits, "Sample size", 0, items.Length);
            var rng = R(c, a[0]);
            var copy = (Value[])items.Clone();
            for (var i = 0; i < k; i++)
            {
                var j = i + (int)rng.Below((ulong)(copy.Length - i));
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }

            c.Charge(k);
            return c.NewArray(copy[..k]);
        });
        F("Shuffle/1", (c, a) => Shuffle(c, Items(a[1]), R(c, a[0])));
        F("Weighted/2", (c, a) =>
        {
            var (items, weights) = (Items(a[1]), Items(a[2]));
            if (items.Length == 0 || items.Length != weights.Length)
            {
                throw c.Throw("ArgumentError", "Weighted needs one non-negative weight per item.");
            }

            var total = weights.Sum(w => w.AsFloat);
            if (!(total > 0) || weights.Any(w => w.AsFloat < 0))
            {
                throw c.Throw("ArgumentError", "Weights must be non-negative with a positive total.");
            }

            var target = R(c, a[0]).NextDouble() * total;
            c.Charge(items.Length);
            for (var i = 0; i < items.Length; i++)
            {
                target -= weights[i].AsFloat;
                if (target < 0)
                {
                    return items[i];
                }
            }

            return items[^1];
        });
        F("Gaussian/0", (c, a) => Value.FromFloat(R(c, a[0]).Gaussian()));
        F("Gaussian/2", (c, a) => Value.FromFloat(a[1].AsFloat + (a[2].AsFloat * R(c, a[0]).Gaussian())));
        F("Uuid/0", (c, a) =>
        {
            var rng = R(c, a[0]);
            var (hi, lo) = (rng.Next(), rng.Next());
            hi = (hi & ~0xF000UL) | 0x4000UL;
            lo = (lo & 0x3FFFFFFFFFFFFFFFUL) | 0x8000000000000000UL;
            var hex = $"{hi:x16}{lo:x16}";
            return Value.FromString($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}");
        });
        F("Fork/0", (c, a) => c.NewInstance(c.Class("Std.Random", "Random"), R(c, a[0]).Fork()));
        F("ToString/0", (c, a) => Value.FromString($"Random(seed {c.State<RandomState>(a[0]).Seed})"));
    }

    internal static Value Shuffle(NativeContext c, Value[] items, RandomState rng)
    {
        c.Charge(items.Length);
        c.MutatingArray(items);
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = (int)rng.Below((ulong)(i + 1));
            (items[i], items[j]) = (items[j], items[i]);
        }

        return Value.Null;
    }
}
