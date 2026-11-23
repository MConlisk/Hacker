/// Seeded pseudo-random numbers (Document 7 §13.6): xoshiro256** seeded through SplitMix64. The same seed produces the
/// same stream on every machine; there is no unseeded constructor (Doc 7 §9).
module Std.Random;

class Random {
    Random(int seed);
    int Seed { get; }
    /// A uniformly distributed 64-bit integer.
    int NextInt();
    /// Uniform in [0, maxExclusive), without modulo bias.
    int NextInt(int maxExclusive);
    /// Uniform in [min, maxExclusive).
    int NextInt(int min, int maxExclusive);
    /// Uniform in [0, 1) with 53 random bits.
    float NextFloat();
    float NextFloat(float min, float max);
    bool NextBool();
    /// True with probability p.
    bool Chance(float p);
    bytes NextBytes(int count);
    T Choice<T>(T[] items);
    /// k distinct items in random order.
    T[] Sample<T>(T[] items, int k);
    void Shuffle<T>(T[] items);
    /// An item chosen with probability proportional to its weight.
    T Weighted<T>(T[] items, float[] weights);
    /// Standard normal sample (mean 0, deviation 1).
    float Gaussian();
    float Gaussian(float mean, float standardDeviation);
    /// A version-4 UUID string drawn from this generator.
    string Uuid();
    /// An independent generator (the xoshiro jump), leaving this one advanced.
    Random Fork();
    string ToString();
}
