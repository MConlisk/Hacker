using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

/// <summary>Host implementations of <c>Std.Algo</c>.</summary>
internal static class AlgoNatives
{
    public static void Register(NativeRegistry r)
    {
        var F = r.Scope("Std.Algo");
        RegisterSorting(F);
        RegisterCombinatorics(F);
        RegisterComparison(F);
        RegisterHashing(F);
    }

    private static void SortInPlace(NativeContext c, Value[] items, IComparer<Value> comparer)
    {
        var sorted = CollectionNatives.StableSort(c, items, comparer);
        c.MutatingArray(items);
        sorted.CopyTo(items, 0);
    }

    private static int Search(NativeContext c, Value[] items, Value value, IComparer<Value> comparer, bool upper)
    {
        int lo = 0, hi = items.Length;
        while (lo < hi)
        {
            c.Charge(1);
            var mid = lo + ((hi - lo) >> 1);
            var cmp = comparer.Compare(items[mid], value);
            if (cmp < 0 || (upper && cmp == 0))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static int BinarySearch(NativeContext c, Value[] items, Value value, IComparer<Value> comparer)
    {
        var i = Search(c, items, value, comparer, upper: false);
        return i < items.Length && comparer.Compare(items[i], value) == 0 ? i : ~i;
    }

    private static void RegisterSorting(Action<string, StdFunction> F)
    {
        F("Sort/1", (c, a) => Void(() => SortInPlace(c, Items(a[0]), c.Comparer)));
        F("Sort/2", (c, a) => Void(() => SortInPlace(c, Items(a[0]), c.Comparer.From(a[1]))));
        F("SortBy/2", (c, a) => Void(() => SortInPlace(c, Items(a[0]), c.Comparer.ByKey(Fn(a[1])))));
        F("IsSorted/1", (c, a) =>
        {
            var items = Items(a[0]);
            c.Charge(items.Length);
            for (var i = 1; i < items.Length; i++)
            {
                if (c.Comparer.Compare(items[i - 1], items[i]) > 0)
                {
                    return Value.False;
                }
            }

            return Value.True;
        });
        F("BinarySearch/2", (c, a) => Value.FromInt(BinarySearch(c, Items(a[0]), a[1], c.Comparer)));
        F("BinarySearch/3", (c, a) => Value.FromInt(BinarySearch(c, Items(a[0]), a[1], c.Comparer.From(a[2]))));
        F("LowerBound/2", (c, a) => Value.FromInt(Search(c, Items(a[0]), a[1], c.Comparer, upper: false)));
        F("UpperBound/2", (c, a) => Value.FromInt(Search(c, Items(a[0]), a[1], c.Comparer, upper: true)));
        F("Partition/2", (c, a) =>
        {
            var items = Items(a[0]);
            var f = Fn(a[1]);
            var flags = items.Select(x =>
            {
                c.Charge(1);
                return c.Invoke(f, x).AsBool;
            }).ToArray();
            var reordered = items.Where((_, i) => flags[i]).Concat(items.Where((_, i) => !flags[i])).ToArray();
            c.MutatingArray(items);
            reordered.CopyTo(items, 0);
            return Value.FromInt(flags.Count(x => x));
        });
        F("Shuffle/2", (c, a) =>
        {
            var rng = c.State<RandomState>(a[1]);
            c.Mutating(rng);
            return RandomNatives.Shuffle(c, Items(a[0]), rng);
        });
        F("Reverse/1", (c, a) =>
        {
            var items = Items(a[0]);
            c.Charge(items.Length / 8);
            c.MutatingArray(items);
            System.Array.Reverse(items);
            return Value.Null;
        });
        F("Rotate/2", (c, a) =>
        {
            var items = Items(a[0]);
            if (items.Length == 0)
            {
                return Value.Null;
            }

            var k = (int)(((a[1].Bits % items.Length) + items.Length) % items.Length);
            c.Charge(items.Length / 8);
            c.MutatingArray(items);
            var rotated = items[k..].Concat(items[..k]).ToArray();
            rotated.CopyTo(items, 0);
            return Value.Null;
        });
        StdFunction topK = (c, a) =>
        {
            var k = Int32(c, a[1].Bits, "k");
            var comparer = a.Length > 2 ? c.Comparer.From(a[2]) : c.Comparer;
            // A size-k min-heap keeps the largest items seen so far (ties keep the earlier item).
            var heap = new PriorityQueue<(Value Item, long Index), (Value Item, long Index)>(
                Comparer<(Value Item, long Index)>.Create((x, y) =>
                {
                    var cmp = comparer.Compare(x.Item, y.Item);
                    return cmp != 0 ? cmp : y.Index.CompareTo(x.Index);
                }));
            var index = 0L;
            foreach (var item in Iterate(c, a[0]))
            {
                c.Charge(1 + (long)Math.Log2(k + 1));
                var entry = (item, index++);
                if (heap.Count < k)
                {
                    heap.Enqueue(entry, entry);
                }
                else if (k > 0 && heap.TryPeek(out var smallest, out _) && comparer.Compare(item, smallest.Item) > 0)
                {
                    heap.DequeueEnqueue(entry, entry);
                }
            }

            var result = new List<(Value Item, long Index)>();
            while (heap.Count > 0)
            {
                result.Add(heap.Dequeue());
            }

            result.Reverse();
            return c.NewArray([.. result.Select(e => e.Item)]);
        };
        F("TopK/2", topK);
        F("TopK/3", topK);
        F("NthElement/2", (c, a) =>
        {
            var items = Items(a[0]);
            var n = Index(c, a[1].Bits, items.Length);
            return CollectionNatives.StableSort(c, items, c.Comparer)[n];
        });
    }

    private static void RegisterCombinatorics(Action<string, StdFunction> F)
    {
        F("Permutations/1", (c, a) =>
        {
            var items = (Value[])Items(a[0]).Clone();
            return SeqNatives.Create(c, ctx => Permutations(ctx, items));
        });
        F("Combinations/2", (c, a) =>
        {
            var items = (Value[])Items(a[0]).Clone();
            var k = Int32(c, a[1].Bits, "k", 0, items.Length);
            return SeqNatives.Create(c, ctx => Combinations(ctx, items, k));
        });
        F("CartesianProduct/1", (c, a) =>
        {
            var sets = Items(a[0]).Select(s => (Value[])Items(s).Clone()).ToArray();
            return SeqNatives.Create(c, ctx => Product(ctx, sets));
        });
        F("PowerSet/1", (c, a) =>
        {
            var items = (Value[])Items(a[0]).Clone();
            if (items.Length > 30)
            {
                throw c.Throw("ArgumentError", "PowerSet supports at most 30 items.");
            }

            return SeqNatives.Create(c, ctx => Enumerable.Range(0, 1 << items.Length).Select(mask =>
            {
                ctx.Charge(1);
                return ctx.NewArray([.. items.Where((_, i) => (mask & (1 << i)) != 0)]);
            }));
        });
    }

    private static IEnumerable<Value> Permutations(NativeContext c, Value[] items)
    {
        var index = Enumerable.Range(0, items.Length).ToArray();
        while (true)
        {
            c.Charge(1);
            yield return c.NewArray([.. index.Select(i => items[i])]);
            var k = index.Length - 2;
            while (k >= 0 && index[k] >= index[k + 1])
            {
                k--;
            }

            if (k < 0)
            {
                yield break;
            }

            var l = index.Length - 1;
            while (index[l] <= index[k])
            {
                l--;
            }

            (index[k], index[l]) = (index[l], index[k]);
            System.Array.Reverse(index, k + 1, index.Length - k - 1);
        }
    }

    private static IEnumerable<Value> Combinations(NativeContext c, Value[] items, int k)
    {
        var index = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            c.Charge(1);
            yield return c.NewArray([.. index.Select(i => items[i])]);
            var i = k - 1;
            while (i >= 0 && index[i] == items.Length - k + i)
            {
                i--;
            }

            if (i < 0)
            {
                yield break;
            }

            index[i]++;
            for (var j = i + 1; j < k; j++)
            {
                index[j] = index[j - 1] + 1;
            }
        }
    }

    private static IEnumerable<Value> Product(NativeContext c, Value[][] sets)
    {
        if (sets.Length == 0 || sets.Any(s => s.Length == 0))
        {
            yield break;
        }

        var index = new int[sets.Length];
        while (true)
        {
            c.Charge(1);
            yield return c.NewArray([.. index.Select((i, s) => sets[s][i])]);
            var position = sets.Length - 1;
            while (position >= 0 && ++index[position] == sets[position].Length)
            {
                index[position--] = 0;
            }

            if (position < 0)
            {
                yield break;
            }
        }
    }

    private static void RegisterComparison(Action<string, StdFunction> F)
    {
        F("LevenshteinDistance/2", (c, a) =>
        {
            var (s, t) = (a[0].AsString, a[1].AsString);
            c.Charge((long)s.Length * t.Length / 8);
            var previous = Enumerable.Range(0, t.Length + 1).ToArray();
            var current = new int[t.Length + 1];
            for (var i = 1; i <= s.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= t.Length; j++)
                {
                    var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return Value.FromInt(previous[t.Length]);
        });
        F("DamerauLevenshteinDistance/2", (c, a) =>
        {
            var (s, t) = (a[0].AsString, a[1].AsString);
            c.Charge((long)s.Length * t.Length / 8);
            var d = new int[s.Length + 1, t.Length + 1];
            for (var i = 0; i <= s.Length; i++)
            {
                d[i, 0] = i;
            }

            for (var j = 0; j <= t.Length; j++)
            {
                d[0, j] = j;
            }

            for (var i = 1; i <= s.Length; i++)
            {
                for (var j = 1; j <= t.Length; j++)
                {
                    var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    {
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                    }
                }
            }

            return Value.FromInt(d[s.Length, t.Length]);
        });
        F("LongestCommonSubsequence/2", (c, a) =>
        {
            var (s, t) = (a[0].AsString, a[1].AsString);
            c.Charge((long)s.Length * t.Length / 8);
            var dp = new int[s.Length + 1, t.Length + 1];
            for (var i = s.Length - 1; i >= 0; i--)
            {
                for (var j = t.Length - 1; j >= 0; j--)
                {
                    dp[i, j] = s[i] == t[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            var sb = new StringBuilder();
            int x = 0, y = 0;
            while (x < s.Length && y < t.Length)
            {
                if (s[x] == t[y])
                {
                    sb.Append(s[x]);
                    x++;
                    y++;
                }
                else if (dp[x + 1, y] >= dp[x, y + 1])
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }

            return Value.FromString(sb.ToString());
        });
        F("LongestCommonSubstring/2", (c, a) =>
        {
            var (s, t) = (a[0].AsString, a[1].AsString);
            c.Charge((long)s.Length * t.Length / 8);
            var previous = new int[t.Length + 1];
            int best = 0, end = 0;
            for (var i = 1; i <= s.Length; i++)
            {
                var current = new int[t.Length + 1];
                for (var j = 1; j <= t.Length; j++)
                {
                    if (s[i - 1] == t[j - 1])
                    {
                        current[j] = previous[j - 1] + 1;
                        if (current[j] > best)
                        {
                            (best, end) = (current[j], i);
                        }
                    }
                }

                previous = current;
            }

            return Value.FromString(s.Substring(end - best, best));
        });
        F("HammingDistance/2", (c, a) =>
        {
            var (s, t) = (a[0].AsString, a[1].AsString);
            if (s.Length != t.Length)
            {
                throw c.Throw("ArgumentError", "Hamming distance needs strings of equal length.");
            }

            c.Charge(s.Length / 8);
            return Value.FromInt(s.Zip(t).Count(p => p.First != p.Second));
        });
        F("JaroWinkler/2", (c, a) =>
        {
            var (s, t) = (a[0].AsString, a[1].AsString);
            c.Charge((long)s.Length * t.Length / 8);
            return Value.FromFloat(JaroWinkler(s, t));
        });
    }

    private static double JaroWinkler(string s, string t)
    {
        if (s.Length == 0 && t.Length == 0)
        {
            return 1;
        }

        var range = Math.Max(0, (Math.Max(s.Length, t.Length) / 2) - 1);
        var sMatched = new bool[s.Length];
        var tMatched = new bool[t.Length];
        var matches = 0;
        for (var i = 0; i < s.Length; i++)
        {
            for (var j = Math.Max(0, i - range); j < Math.Min(t.Length, i + range + 1); j++)
            {
                if (!tMatched[j] && s[i] == t[j])
                {
                    sMatched[i] = tMatched[j] = true;
                    matches++;
                    break;
                }
            }
        }

        if (matches == 0)
        {
            return 0;
        }

        var transpositions = 0;
        for (int i = 0, k = 0; i < s.Length; i++)
        {
            if (!sMatched[i])
            {
                continue;
            }

            while (!tMatched[k])
            {
                k++;
            }

            if (s[i] != t[k++])
            {
                transpositions++;
            }
        }

        double m = matches;
        var jaro = ((m / s.Length) + (m / t.Length) + ((m - (transpositions / 2.0)) / m)) / 3;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(s.Length, t.Length)) && s[prefix] == t[prefix])
        {
            prefix++;
        }

        return jaro + (prefix * 0.1 * (1 - jaro));
    }

    private static void RegisterHashing(Action<string, StdFunction> F)
    {
        static long Fnv(ReadOnlySpan<byte> data)
        {
            unchecked
            {
                var hash = 0xcbf29ce484222325UL;
                foreach (var b in data)
                {
                    hash ^= b;
                    hash *= 0x100000001b3UL;
                }

                return (long)hash;
            }
        }

        F("Fnv1aText", (c, a) =>
        {
            var data = Encoding.UTF8.GetBytes(a[0].AsString);
            c.Charge(data.Length / 16);
            return Value.FromInt(Fnv(data));
        });
        F("Fnv1aBytes", (c, a) =>
        {
            var data = a[0].As<BytesObject>().Data;
            c.Charge(data.Length / 16);
            return Value.FromInt(Fnv(data));
        });
        F("Murmur3/2", (c, a) =>
        {
            var data = a[0].As<BytesObject>().Data;
            c.Charge(data.Length / 16);
            return Value.FromInt(Murmur3(data, (uint)a[1].Bits));
        });
        F("Mix64/1", (c, a) => Value.FromInt((long)Mix((ulong)a[0].Bits)));
        F("HashCombine/2", (c, a) => Value.FromInt((long)Mix((ulong)a[0].Bits ^ (((ulong)a[1].Bits) + 0x9E3779B97F4A7C15UL + ((ulong)a[0].Bits << 6) + ((ulong)a[0].Bits >> 2)))));
        F("HashOf/1", (c, a) => Value.FromInt(c.Comparer.Hash(a[0])));
    }

    /// <summary>MurmurHash3 x86_32.</summary>
    internal static uint Murmur3(ReadOnlySpan<byte> data, uint seed)
    {
        const uint c1 = 0xcc9e2d51, c2 = 0x1b873593;
        var h = seed;
        var blocks = data.Length / 4;
        for (var i = 0; i < blocks; i++)
        {
            var k = BitConverter.ToUInt32(data.Slice(i * 4, 4));
            if (!BitConverter.IsLittleEndian)
            {
                k = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(k);
            }

            k *= c1;
            k = uint.RotateLeft(k, 15);
            k *= c2;
            h ^= k;
            h = uint.RotateLeft(h, 13);
            h = (h * 5) + 0xe6546b64;
        }

        uint tail = 0;
        var rest = data[(blocks * 4)..];
        for (var i = rest.Length - 1; i >= 0; i--)
        {
            tail = (tail << 8) | rest[i];
        }

        if (rest.Length > 0)
        {
            tail *= c1;
            tail = uint.RotateLeft(tail, 15);
            tail *= c2;
            h ^= tail;
        }

        h ^= (uint)data.Length;
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}
