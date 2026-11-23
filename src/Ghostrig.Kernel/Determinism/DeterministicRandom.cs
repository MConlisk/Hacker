namespace Ghostrig.Kernel.Determinism;

/// <summary>
/// Seeded xoshiro256** generator. The only permitted source of variation in the simulation
/// (Doc 2 §2, Doc 3 §2.3); its full state is serialized with the save so replays are bit-identical.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public DeterministicRandom(ulong seed)
    {
        var sm = seed;
        _s0 = SplitMix64(ref sm);
        _s1 = SplitMix64(ref sm);
        _s2 = SplitMix64(ref sm);
        _s3 = SplitMix64(ref sm);
    }

    private DeterministicRandom(RandomState state)
    {
        if ((state.S0 | state.S1 | state.S2 | state.S3) == 0)
        {
            throw new ArgumentException("An all-zero xoshiro state is invalid.", nameof(state));
        }

        (_s0, _s1, _s2, _s3) = (state.S0, state.S1, state.S2, state.S3);
    }

    public static DeterministicRandom FromState(RandomState state) => new(state);

    public RandomState State => new(_s0, _s1, _s2, _s3);

    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Unbiased integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>) (Lemire's method).</summary>
    public long NextInt64(long minInclusive, long maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "The range must be non-empty.");
        }

        var range = (ulong)(maxExclusive - minInclusive);
        var x = NextUInt64();
        var m = (UInt128)x * range;
        var low = (ulong)m;
        if (low < range)
        {
            var threshold = (0 - range) % range;
            while (low < threshold)
            {
                x = NextUInt64();
                m = (UInt128)x * range;
                low = (ulong)m;
            }
        }

        return minInclusive + (long)(ulong)(m >> 64);
    }

    public void NextBytes(Span<byte> destination)
    {
        Span<byte> block = stackalloc byte[8];
        while (!destination.IsEmpty)
        {
            BitConverter.TryWriteBytes(block, NextUInt64());
            var n = Math.Min(8, destination.Length);
            block[..n].CopyTo(destination);
            destination = destination[n..];
        }
    }

    /// <summary>Stateless SplitMix64 step, also used for random-access synthetic file content.</summary>
    public static ulong SplitMix64(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));
}

/// <summary>Serializable xoshiro256** state.</summary>
public readonly record struct RandomState(ulong S0, ulong S1, ulong S2, ulong S3);
