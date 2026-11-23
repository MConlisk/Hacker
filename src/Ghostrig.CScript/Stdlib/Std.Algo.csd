/// Algorithms on arrays and sequences (Document 7 §13.3). Sorting is stable everywhere.
module Std.Algo;

import Std.Seq;
import Std.Random;

// ---------------------------------------------------------------- sorting and searching

/// Stable in-place sort in natural order. Cost: n log n.
void Sort<T>(T[] items);
void Sort<T>(T[] items, fn(T, T) -> int comparer);
void SortBy<T, K>(T[] items, fn(T) -> K key);
bool IsSorted<T>(T[] items);
/// Index of value in a sorted array, or ~insertionPoint when absent. Cost: log n.
int BinarySearch<T>(T[] items, T value);
int BinarySearch<T>(T[] items, T value, fn(T, T) -> int comparer);
/// First index whose item is >= value (sorted input).
int LowerBound<T>(T[] items, T value);
/// First index whose item is > value (sorted input).
int UpperBound<T>(T[] items, T value);
/// Stable partition in place: matching items first. Returns the number of matching items.
int Partition<T>(T[] items, fn(T) -> bool predicate);
/// Fisher–Yates shuffle driven by a seeded generator.
void Shuffle<T>(T[] items, Random rng);
void Reverse<T>(T[] items);
/// Rotates left by count positions.
void Rotate<T>(T[] items, int count);
/// The k largest items, largest first. Cost: n log k.
T[] TopK<T>(Iterable<T> items, int k);
T[] TopK<T>(Iterable<T> items, int k, fn(T, T) -> int comparer);
/// The k-th smallest item (0-based). Cost: n on average.
T NthElement<T>(T[] items, int n);

// ---------------------------------------------------------------- combinatorics (lazy)

/// Every ordering, in lexicographic order of positions.
Sequence<T[]> Permutations<T>(T[] items);
/// Every k-subset, in lexicographic order of positions.
Sequence<T[]> Combinations<T>(T[] items, int k);
/// Every tuple picking one item from each set.
Sequence<T[]> CartesianProduct<T>(T[][] sets);
/// Every subset (2^n of them).
Sequence<T[]> PowerSet<T>(T[] items);

// ---------------------------------------------------------------- comparison

/// Edit distance (insert, delete, substitute). Cost: |a| × |b| / 8.
int LevenshteinDistance(string a, string b);
/// Edit distance that also counts adjacent transpositions.
int DamerauLevenshteinDistance(string a, string b);
string LongestCommonSubsequence(string a, string b);
string LongestCommonSubstring(string a, string b);
int HammingDistance(string a, string b);
/// Jaro–Winkler similarity in [0, 1].
float JaroWinkler(string a, string b);

// ---------------------------------------------------------------- hashing helpers (non-cryptographic, deterministic)

/// FNV-1a over UTF-8 text.
@Native("Std.Algo.Fnv1aText") int Fnv1a(string text);
@Native("Std.Algo.Fnv1aBytes") int Fnv1a(bytes data);
/// MurmurHash3 (x86, 32-bit) of data with a seed.
int Murmur3(bytes data, int seed);
/// The 64-bit finaliser of SplitMix64.
int Mix64(int value);
int HashCombine(int a, int b);
/// The collection hash of any value (as used by Map and Set).
int HashOf<T>(T value);
