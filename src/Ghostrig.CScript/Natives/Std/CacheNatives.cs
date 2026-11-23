using System.Globalization;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

#region States

/// <summary>Hit/miss counters reported by every cache (Doc 7 §13.2: shown by Debug.Inspect and the Profiler).</summary>
internal abstract class CacheState(int capacity) : NativeState
{
    public int Capacity { get; } = capacity;
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long Evictions { get; set; }

    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);

    public string Stats => $"hits {Hits}, misses {Misses}, evictions {Evictions}, hit rate {HitRate.ToString("0.###", CultureInfo.InvariantCulture)}";

    protected (long, long, long) CaptureStats() => (Hits, Misses, Evictions);

    protected void RestoreStats((long, long, long) stats) => (Hits, Misses, Evictions) = stats;
}

/// <summary>LRU: an insertion-ordered table where a hit moves the key to the end; the first entry is the eviction victim.</summary>
internal sealed class LruState(int capacity, IEqualityComparer<Value> comparer) : CacheState(capacity)
{
    public OrderedTable Table { get; } = new(comparer);

    protected internal override object CaptureSnapshot() => (Table.Capture(), CaptureStats());

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (table, stats) = ((object, (long, long, long)))snapshot;
        Table.Restore(table);
        RestoreStats(stats);
    }
}

/// <summary>
/// LFU: each key carries (value, frequency, tick); a sorted set of (frequency, tick) pairs finds the victim in O(log n),
/// and ticks are unique so they also identify the key. Snapshots copy without re-hashing.
/// </summary>
internal sealed class LfuState(int capacity, IEqualityComparer<Value> comparer) : CacheState(capacity)
{
    private readonly IEqualityComparer<Value> _comparer = comparer;

    public Dictionary<Value, (Value Value, long Frequency, long Tick)> Entries { get; private set; } = new(comparer);

    public SortedSet<(long Frequency, long Tick)> Order { get; private set; } = [];

    public Dictionary<long, Value> KeyByTick { get; private set; } = [];

    public long Clock { get; set; }

    public void Store(Value key, Value value, long frequency)
    {
        if (Entries.TryGetValue(key, out var old))
        {
            Order.Remove((old.Frequency, old.Tick));
            KeyByTick.Remove(old.Tick);
        }

        var tick = ++Clock;
        Entries[key] = (value, frequency, tick);
        Order.Add((frequency, tick));
        KeyByTick[tick] = key;
    }

    public void Delete(Value key)
    {
        if (Entries.Remove(key, out var old))
        {
            Order.Remove((old.Frequency, old.Tick));
            KeyByTick.Remove(old.Tick);
        }
    }

    public void EvictOne()
    {
        var victim = Order.Min;
        Delete(KeyByTick[victim.Tick]);
        Evictions++;
    }

    public void Clear()
    {
        Entries.Clear();
        Order.Clear();
        KeyByTick.Clear();
    }

    protected internal override object CaptureSnapshot() =>
        (new Dictionary<Value, (Value, long, long)>(Entries, _comparer), new SortedSet<(long, long)>(Order), new Dictionary<long, Value>(KeyByTick), Clock, CaptureStats());

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (entries, order, keys, clock, stats) = ((Dictionary<Value, (Value, long, long)>, SortedSet<(long, long)>, Dictionary<long, Value>, long, (long, long, long)))snapshot;
        Entries = new Dictionary<Value, (Value Value, long Frequency, long Tick)>(entries, _comparer);
        Order = new SortedSet<(long Frequency, long Tick)>(order);
        KeyByTick = new Dictionary<long, Value>(keys);
        Clock = clock;
        RestoreStats(stats);
    }
}

/// <summary>TTL: values are stored with their expiry instant (simulated nanoseconds).</summary>
internal sealed class TtlState(long ttl, IEqualityComparer<Value> comparer) : CacheState(int.MaxValue)
{
    public long Ttl { get; } = ttl;
    public OrderedTable Table { get; } = new(comparer);

    public static Value Entry(Value value, long expiresAt) => Tuple(value, Value.FromInt(expiresAt));

    protected internal override object CaptureSnapshot() => (Table.Capture(), CaptureStats());

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (table, stats) = ((object, (long, long, long)))snapshot;
        Table.Restore(table);
        RestoreStats(stats);
    }
}

internal sealed class PoolState(FunctionObject factory, int maxSize) : NativeState
{
    public FunctionObject Factory { get; } = factory;
    public int MaxSize { get; } = maxSize;
    public List<Value> Free { get; private set; } = [];
    public long Created { get; set; }
    public long Reuses { get; set; }

    protected internal override object CaptureSnapshot() => (Free.ToArray(), Created, Reuses);

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (free, created, reuses) = ((Value[], long, long))snapshot;
        Free = [.. free];
        (Created, Reuses) = (created, reuses);
    }
}

/// <summary>Bloom filter with double hashing (Kirsch–Mitzenmacher) over the deterministic collection hash.</summary>
internal sealed class BloomState(int bits, int hashes) : NativeState
{
    public int BitCount { get; } = bits;
    public int HashCount { get; } = hashes;
    public ulong[] Words { get; private set; } = new ulong[(bits + 63) / 64];
    public long Count { get; set; }

    public IEnumerable<int> Positions(long hash)
    {
        var h1 = Mix((ulong)hash);
        var h2 = Mix(h1 ^ 0x9E3779B97F4A7C15UL) | 1;
        for (var i = 0; i < HashCount; i++)
        {
            yield return (int)((h1 + ((ulong)i * h2)) % (ulong)BitCount);
        }
    }

    public int SetBits => Words.Sum(System.Numerics.BitOperations.PopCount);

    protected internal override object CaptureSnapshot() => (Words.Clone(), Count);

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (words, count) = ((object, long))snapshot;
        Words = (ulong[])((ulong[])words).Clone();
        Count = count;
    }
}

internal sealed class SketchState(int width, int depth) : NativeState
{
    public int Width { get; } = width;
    public int Depth { get; } = depth;
    public long[] Counts { get; private set; } = new long[width * depth];
    public long Total { get; set; }

    public int Cell(int row, long hash) => (row * Width) + (int)(Mix((ulong)hash + (0x9E3779B97F4A7C15UL * (ulong)(row + 1))) % (ulong)Width);

    protected internal override object CaptureSnapshot() => (Counts.Clone(), Total);

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (counts, total) = ((object, long))snapshot;
        Counts = (long[])((long[])counts).Clone();
        Total = total;
    }
}

internal sealed class HyperLogLogState(int precision) : NativeState
{
    public int Precision { get; } = precision;
    public byte[] Registers { get; private set; } = new byte[1 << precision];

    public void Add(long hash)
    {
        var x = Mix((ulong)hash);
        var index = (int)(x >> (64 - Precision));
        var rest = (x << Precision) | (1UL << (Precision - 1));
        var rank = (byte)(System.Numerics.BitOperations.LeadingZeroCount(rest) + 1);
        if (rank > Registers[index])
        {
            Registers[index] = rank;
        }
    }

    public long Estimate()
    {
        var m = Registers.Length;
        var alpha = m switch
        {
            16 => 0.673,
            32 => 0.697,
            64 => 0.709,
            _ => 0.7213 / (1 + (1.079 / m)),
        };
        var sum = 0.0;
        var zeros = 0;
        foreach (var r in Registers)
        {
            sum += Math.ScaleB(1.0, -r);
            if (r == 0)
            {
                zeros++;
            }
        }

        var estimate = alpha * m * m / sum;
        if (estimate <= 2.5 * m && zeros > 0)
        {
            estimate = m * SoftMath.Log((double)m / zeros);
        }

        return (long)Math.Round(estimate);
    }

    protected internal override object CaptureSnapshot() => Registers.Clone();

    protected internal override void RestoreSnapshot(object snapshot) => Registers = (byte[])((byte[])snapshot).Clone();
}

internal sealed class TrieState : NativeState, IValueSource
{
    public SortedSet<string> Words { get; private set; } = new(StringComparer.Ordinal);

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Words.Select(Value.FromString).ToArray());

    public IEnumerable<string> WithPrefix(string prefix) =>
        prefix.Length == 0 ? Words : Words.GetViewBetween(prefix, prefix + '￿').Where(w => w.StartsWith(prefix, StringComparison.Ordinal));

    protected internal override object CaptureSnapshot() => Words.ToArray();

    protected internal override void RestoreSnapshot(object snapshot) => Words = new SortedSet<string>((string[])snapshot, StringComparer.Ordinal);
}

/// <summary>Intervals sorted by start with a lazily rebuilt max-end prefix array: queries are log n + matches.</summary>
internal sealed class IntervalState : NativeState, IValueSource
{
    public List<(long Start, long End, Value Value, long Sequence)> Items { get; private set; } = [];
    private long[]? _maxEnd;
    public long Sequence { get; set; }

    public void Invalidate() => _maxEnd = null;

    public IEnumerable<Value> Query(long start, long end)
    {
        if (_maxEnd is null)
        {
            _maxEnd = new long[Items.Count];
            var max = long.MinValue;
            for (var i = 0; i < Items.Count; i++)
            {
                max = Math.Max(max, Items[i].End);
                _maxEnd[i] = max;
            }
        }

        // Candidates have Start < end: a binary search bounds them; scanning stops early via the prefix max of ends.
        int lo = 0, hi = Items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Items[mid].Start < end)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var results = new List<Value>();
        for (var i = lo - 1; i >= 0 && _maxEnd[i] > start; i--)
        {
            if (Items[i].End > start)
            {
                results.Add(Items[i].Value);
            }
        }

        results.Reverse();
        return results;
    }

    public IEnumerable<Value> Enumerate(NativeContext context) =>
        Guard(context, Items.Select(i => Tuple(Value.FromInt(i.Start), Value.FromInt(i.End), i.Value)).ToArray());

    protected internal override object CaptureSnapshot() => (Items.ToArray(), Sequence);

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (items, sequence) = (((long, long, Value, long)[], long))snapshot;
        Items = [.. items];
        Sequence = sequence;
        _maxEnd = null;
    }
}

/// <summary>A skip list with levels drawn from a SplitMix64 stream seeded by the insertion count (deterministic).</summary>
internal sealed class SkipListState(IComparer<Value> comparer) : NativeState, IValueSource
{
    private const int MaxLevel = 32;

    private sealed class Node(Value value, int level)
    {
        public Value Value { get; } = value;
        public Node?[] Next { get; } = new Node?[level];
    }

    private Node _head = new(Value.Null, MaxLevel);
    private ulong _levelSeed = 0x5DEECE66DUL;

    public int Count { get; private set; }

    public int Levels { get; private set; } = 1;

    private int RandomLevel()
    {
        _levelSeed += 0x9E3779B97F4A7C15UL;
        var bits = Mix(_levelSeed);
        var level = 1 + System.Numerics.BitOperations.TrailingZeroCount(bits | (1UL << (MaxLevel - 1)));
        return Math.Min(level, MaxLevel);
    }

    private Node?[] FindPredecessors(Value value, NativeContext c)
    {
        var update = new Node?[MaxLevel];
        var x = _head;
        for (var i = Levels - 1; i >= 0; i--)
        {
            while (x.Next[i] is { } next && comparer.Compare(next.Value, value) < 0)
            {
                c.Charge(1);
                x = next;
            }

            update[i] = x;
        }

        return update;
    }

    public bool Contains(Value value, NativeContext c)
    {
        var update = FindPredecessors(value, c);
        return update[0]!.Next[0] is { } n && comparer.Compare(n.Value, value) == 0;
    }

    public bool Add(Value value, NativeContext c)
    {
        var update = FindPredecessors(value, c);
        if (update[0]!.Next[0] is { } existing && comparer.Compare(existing.Value, value) == 0)
        {
            return false;
        }

        var level = RandomLevel();
        if (level > Levels)
        {
            for (var i = Levels; i < level; i++)
            {
                update[i] = _head;
            }

            Levels = level;
        }

        var node = new Node(value, level);
        for (var i = 0; i < level; i++)
        {
            node.Next[i] = update[i]!.Next[i];
            update[i]!.Next[i] = node;
        }

        Count++;
        return true;
    }

    public bool Remove(Value value, NativeContext c)
    {
        var update = FindPredecessors(value, c);
        if (update[0]!.Next[0] is not { } target || comparer.Compare(target.Value, value) != 0)
        {
            return false;
        }

        for (var i = 0; i < Levels; i++)
        {
            if (update[i]!.Next[i] != target)
            {
                break;
            }

            update[i]!.Next[i] = target.Next[i];
        }

        while (Levels > 1 && _head.Next[Levels - 1] is null)
        {
            Levels--;
        }

        Count--;
        return true;
    }

    public void Clear()
    {
        _head = new Node(Value.Null, MaxLevel);
        Levels = 1;
        Count = 0;
    }

    public IEnumerable<Value> Values()
    {
        for (var x = _head.Next[0]; x is not null; x = x.Next[0])
        {
            yield return x.Value;
        }
    }

    public IEnumerable<Value> From(Value from, NativeContext c)
    {
        var x = FindPredecessors(from, c)[0]!.Next[0];
        for (; x is not null; x = x.Next[0])
        {
            yield return x.Value;
        }
    }

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Values());

    // Rebuilding from the sorted values needs no comparisons, so rollback never re-enters script code.
    protected internal override object CaptureSnapshot() => (Values().ToArray(), _levelSeed);

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (values, seed) = ((Value[], ulong))snapshot;
        Clear();
        _levelSeed = 0x5DEECE66DUL;
        var tails = new Node[MaxLevel];
        System.Array.Fill(tails, _head);
        foreach (var value in values)
        {
            var level = RandomLevel();
            Levels = Math.Max(Levels, level);
            var node = new Node(value, level);
            for (var i = 0; i < level; i++)
            {
                tails[i].Next[i] = node;
                tails[i] = node;
            }

            Count++;
        }

        _levelSeed = seed;
    }
}

internal sealed class RingHashState(int virtualNodes) : NativeState
{
    public int VirtualNodes { get; } = virtualNodes;
    public List<Value> Nodes { get; private set; } = [];
    public List<(ulong Hash, int Node)> Ring { get; private set; } = [];

    public void Rebuild(NativeContext c)
    {
        var ring = new List<(ulong, int)>(Nodes.Count * VirtualNodes);
        for (var n = 0; n < Nodes.Count; n++)
        {
            var nodeHash = (ulong)c.Comparer.Hash(Nodes[n]);
            for (var v = 0; v < VirtualNodes; v++)
            {
                ring.Add((Mix(nodeHash + (0x9E3779B97F4A7C15UL * (ulong)(v + 1))), n));
            }
        }

        ring.Sort();
        Ring = ring;
        c.Charge(ring.Count);
    }

    protected internal override object CaptureSnapshot() => (Nodes.ToArray(), Ring.ToArray());

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (nodes, ring) = ((Value[], (ulong, int)[]))snapshot;
        Nodes = [.. nodes];
        Ring = [.. ring];
    }
}

#endregion

/// <summary>Host implementations of the caches and probabilistic structures in <c>Std.Collections</c> (Doc 7 D4).</summary>
internal static class CacheNatives
{
    private const string M = "Std.Collections";

    public static void Register(NativeRegistry r)
    {
        RegisterLru(r);
        RegisterLfu(r);
        RegisterTtl(r);
        RegisterPool(r);
        RegisterProbabilistic(r);
        RegisterTrie(r);
        RegisterIntervals(r);
        RegisterSkipList(r);
        RegisterRing(r);
    }

    private static int Capacity(NativeContext c, long value) => Int32(c, value, "Capacity", 1, 16_000_000);

    private static void Stats(Action<string, StdFunction> F)
    {
        F("get_Hits/0", (c, a) => Value.FromInt(c.State<CacheState>(a[0]).Hits));
        F("get_Misses/0", (c, a) => Value.FromInt(c.State<CacheState>(a[0]).Misses));
        F("get_HitRate/0", (c, a) => Value.FromFloat(c.State<CacheState>(a[0]).HitRate));
    }

    private static void ResetStats(Action<string, StdFunction> F) => F("ResetStats/0", (c, a) => Mutate<CacheState>(c, a[0], s =>
    {
        s.Hits = s.Misses = s.Evictions = 0;
        return Value.Null;
    }));

    private static void RegisterLru(NativeRegistry r)
    {
        var F = r.Scope($"{M}.LruCache");
        static LruState S(NativeContext c, Value v) => c.State<LruState>(v);
        static Value Put(NativeContext c, LruState s, Value key, Value value)
        {
            c.Mutating(s);
            if (s.Table.TryGetSlot(key, out var slot))
            {
                s.Table.SetValueAt(slot, value);
                s.Table.MoveToEnd(key);
                return Value.Null;
            }

            if (s.Table.Count >= s.Capacity)
            {
                s.Table.Remove(s.Table.KeyAt(s.Table.FirstSlot()), out _);
                s.Evictions++;
            }

            s.Table.Set(key, value);
            return Value.Null;
        }

        F("LruCache/1", (c, a) => CollectionNatives.Init(a[0], new LruState(Capacity(c, a[1].Bits), c.Comparer)));
        F("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Table.Count));
        F("get_Capacity/0", (c, a) => Value.FromInt(S(c, a[0]).Capacity));
        F("Get/1", (c, a) =>
        {
            var s = S(c, a[0]);
            c.Mutating(s);
            if (s.Table.TryGetValue(a[1], out var value))
            {
                s.Hits++;
                s.Table.MoveToEnd(a[1]);
                return value;
            }

            s.Misses++;
            return Value.Null;
        });
        F("Put/2", (c, a) => Put(c, S(c, a[0]), a[1], a[2]));
        F("GetOrAdd/2", (c, a) =>
        {
            var s = S(c, a[0]);
            c.Mutating(s);
            if (s.Table.TryGetValue(a[1], out var value))
            {
                s.Hits++;
                s.Table.MoveToEnd(a[1]);
                return value;
            }

            s.Misses++;
            var created = c.Invoke(Fn(a[2]), a[1]);
            Put(c, s, a[1], created);
            return created;
        });
        F("Contains/1", (c, a) => Value.FromBool(S(c, a[0]).Table.ContainsKey(a[1])));
        F("Remove/1", (c, a) => !S(c, a[0]).Table.ContainsKey(a[1]) ? Value.False : Mutate<LruState>(c, a[0], s => Value.FromBool(s.Table.Remove(a[1], out _))));
        F("Clear/0", (c, a) => Mutate<LruState>(c, a[0], s => Void(s.Table.Clear)));
        F("get_Evictions/0", (c, a) => Value.FromInt(S(c, a[0]).Evictions));
        Stats(F);
        ResetStats(F);
        F("get_Keys/0", (c, a) =>
        {
            var s = S(c, a[0]);
            return SeqNatives.Create(c, _ => s.Table.Keys().ToArray());
        });
        F("ToString/0", (c, a) => Value.FromString($"LruCache({S(c, a[0]).Table.Count}/{S(c, a[0]).Capacity}; {S(c, a[0]).Stats})"));
    }

    private static void RegisterLfu(NativeRegistry r)
    {
        var F = r.Scope($"{M}.LfuCache");
        static LfuState S(NativeContext c, Value v) => c.State<LfuState>(v);
        static int Log(int n) => 1 + (int)Math.Log2(Math.Max(1, n));

        F("LfuCache/1", (c, a) => CollectionNatives.Init(a[0], new LfuState(Capacity(c, a[1].Bits), c.Comparer)));
        F("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Entries.Count));
        F("get_Capacity/0", (c, a) => Value.FromInt(S(c, a[0]).Capacity));
        F("Get/1", (c, a) =>
        {
            var s = S(c, a[0]);
            c.Mutating(s);
            c.Charge(Log(s.Entries.Count));
            if (s.Entries.TryGetValue(a[1], out var entry))
            {
                s.Hits++;
                s.Store(a[1], entry.Value, entry.Frequency + 1);
                return entry.Value;
            }

            s.Misses++;
            return Value.Null;
        });
        F("Put/2", (c, a) =>
        {
            var s = S(c, a[0]);
            c.Mutating(s);
            c.Charge(Log(s.Entries.Count));
            if (s.Entries.TryGetValue(a[1], out var entry))
            {
                s.Store(a[1], a[2], entry.Frequency + 1);
                return Value.Null;
            }

            if (s.Entries.Count >= s.Capacity)
            {
                s.EvictOne();
            }

            s.Store(a[1], a[2], 1);
            return Value.Null;
        });
        F("Contains/1", (c, a) => Value.FromBool(S(c, a[0]).Entries.ContainsKey(a[1])));
        F("Frequency/1", (c, a) => Value.FromInt(S(c, a[0]).Entries.TryGetValue(a[1], out var e) ? e.Frequency : 0));
        F("Remove/1", (c, a) => !S(c, a[0]).Entries.ContainsKey(a[1]) ? Value.False : Mutate<LfuState>(c, a[0], s =>
        {
            s.Delete(a[1]);
            return Value.True;
        }));
        F("Clear/0", (c, a) => Mutate<LfuState>(c, a[0], s => Void(s.Clear)));
        F("get_Evictions/0", (c, a) => Value.FromInt(S(c, a[0]).Evictions));
        Stats(F);
        ResetStats(F);
        F("ToString/0", (c, a) => Value.FromString($"LfuCache({S(c, a[0]).Entries.Count}/{S(c, a[0]).Capacity}; {S(c, a[0]).Stats})"));
    }

    private static void RegisterTtl(NativeRegistry r)
    {
        var F = r.Scope($"{M}.TtlCache");
        static TtlState S(NativeContext c, Value v) => c.State<TtlState>(v);
        static bool Live(NativeContext c, Value entry) => entry.As<TupleObject>().Items[1].Bits > c.Now;
        static Value Put(NativeContext c, Value[] a, long ttl)
        {
            if (ttl <= 0)
            {
                throw c.Throw("ArgumentError", "TTL must be positive.");
            }

            return Mutate<TtlState>(c, a[0], s => Void(() =>
            {
                s.Table.Remove(a[1], out _);
                s.Table.Set(a[1], TtlState.Entry(a[2], c.Now + ttl));
            }));
        }

        F("TtlCache/1", (c, a) => a[1].Bits > 0
            ? CollectionNatives.Init(a[0], new TtlState(a[1].Bits, c.Comparer))
            : throw c.Throw("ArgumentError", "TTL must be positive."));
        F("get_Count/0", (c, a) =>
        {
            var s = S(c, a[0]);
            c.Charge(s.Table.Count);
            return Value.FromInt(s.Table.Values().Count(e => Live(c, e)));
        });
        F("get_Ttl/0", (c, a) => Value.FromDuration(S(c, a[0]).Ttl));
        F("Get/1", (c, a) =>
        {
            var s = S(c, a[0]);
            c.Mutating(s);
            if (s.Table.TryGetValue(a[1], out var entry))
            {
                if (Live(c, entry))
                {
                    s.Hits++;
                    return entry.As<TupleObject>().Items[0];
                }

                s.Table.Remove(a[1], out _);
                s.Evictions++;
            }

            s.Misses++;
            return Value.Null;
        });
        F("Put/2", (c, a) => Put(c, a, S(c, a[0]).Ttl));
        F("Put/3", (c, a) => Put(c, a, a[3].Bits));
        F("Contains/1", (c, a) => Value.FromBool(S(c, a[0]).Table.TryGetValue(a[1], out var e) && Live(c, e)));
        F("Remove/1", (c, a) => !S(c, a[0]).Table.ContainsKey(a[1]) ? Value.False : Mutate<TtlState>(c, a[0], s => Value.FromBool(s.Table.Remove(a[1], out _))));
        F("Purge/0", (c, a) =>
        {
            var s = S(c, a[0]);
            c.Charge(s.Table.Count);
            var expired = s.Table.Slots().Where(slot => !Live(c, s.Table.ValueAt(slot))).Select(s.Table.KeyAt).ToList();
            if (expired.Count > 0)
            {
                c.Mutating(s);
                expired.ForEach(k => s.Table.Remove(k, out _));
                s.Evictions += expired.Count;
            }

            return Value.FromInt(expired.Count);
        });
        F("Clear/0", (c, a) => Mutate<TtlState>(c, a[0], s => Void(s.Table.Clear)));
        F("get_Expirations/0", (c, a) => Value.FromInt(S(c, a[0]).Evictions));
        Stats(F);
        F("ToString/0", (c, a) => Value.FromString($"TtlCache({S(c, a[0]).Table.Count} entries, ttl {ValueFormatting.FormatDuration(S(c, a[0]).Ttl)}; {S(c, a[0]).Stats})"));
    }

    private static void RegisterPool(NativeRegistry r)
    {
        var F = r.Scope($"{M}.ObjectPool");
        static PoolState S(NativeContext c, Value v) => c.State<PoolState>(v);
        F("ObjectPool/2", (c, a) => CollectionNatives.Init(a[0], new PoolState(Fn(a[1]), Int32(c, a[2].Bits, "maxSize", 0, 16_000_000))));
        F("Rent/0", (c, a) =>
        {
            var s = S(c, a[0]);
            c.Mutating(s);
            if (s.Free.Count > 0)
            {
                var item = s.Free[^1];
                s.Free.RemoveAt(s.Free.Count - 1);
                s.Reuses++;
                return item;
            }

            s.Created++;
            return c.Invoke(s.Factory);
        });
        F("Return/1", (c, a) => Mutate<PoolState>(c, a[0], s => Void(() =>
        {
            if (s.Free.Count < s.MaxSize)
            {
                s.Free.Add(a[1]);
            }
        })));
        F("get_Available/0", (c, a) => Value.FromInt(S(c, a[0]).Free.Count));
        F("get_Created/0", (c, a) => Value.FromInt(S(c, a[0]).Created));
        F("get_Reuses/0", (c, a) => Value.FromInt(S(c, a[0]).Reuses));
        F("ToString/0", (c, a) => Value.FromString($"ObjectPool(available {S(c, a[0]).Free.Count}, created {S(c, a[0]).Created}, reuses {S(c, a[0]).Reuses})"));
    }

    private static void RegisterProbabilistic(NativeRegistry r)
    {
        var b = r.Scope($"{M}.BloomFilter");
        static BloomState B(NativeContext c, Value v) => c.State<BloomState>(v);
        b("BloomFilter/2", (c, a) =>
        {
            var n = Int32(c, a[1].Bits, "expectedItems", 1, 100_000_000);
            var p = a[2].AsFloat;
            if (!(p > 0 && p < 1))
            {
                throw c.Throw("ArgumentError", "falsePositiveRate must be between 0 and 1.");
            }

            var ln2 = SoftMath.Log(2);
            var bits = (int)Math.Clamp(Math.Ceiling(-n * SoftMath.Log(p) / (ln2 * ln2)), 64, int.MaxValue - 64);
            var hashes = (int)Math.Clamp(Math.Round((double)bits / n * ln2), 1, 32);
            return CollectionNatives.Init(a[0], new BloomState(bits, hashes));
        });
        b("Add/1", (c, a) =>
        {
            var hash = c.Comparer.Hash(a[1]);
            return Mutate<BloomState>(c, a[0], s =>
            {
                c.Charge(s.HashCount);
                foreach (var position in s.Positions(hash))
                {
                    s.Words[position >> 6] |= 1UL << (position & 63);
                }

                s.Count++;
                return Value.Null;
            });
        });
        b("MightContain/1", (c, a) =>
        {
            var s = B(c, a[0]);
            c.Charge(s.HashCount);
            return Value.FromBool(s.Positions(c.Comparer.Hash(a[1])).All(p => (s.Words[p >> 6] & (1UL << (p & 63))) != 0));
        });
        b("get_Count/0", (c, a) => Value.FromInt(B(c, a[0]).Count));
        b("get_BitCount/0", (c, a) => Value.FromInt(B(c, a[0]).BitCount));
        b("get_HashCount/0", (c, a) => Value.FromInt(B(c, a[0]).HashCount));
        b("get_EstimatedFalsePositiveRate/0", (c, a) =>
        {
            var s = B(c, a[0]);
            c.Charge(s.Words.Length / 8);
            return Value.FromFloat(SoftMath.Pow((double)s.SetBits / s.BitCount, s.HashCount));
        });
        b("Clear/0", (c, a) => Mutate<BloomState>(c, a[0], s =>
        {
            System.Array.Clear(s.Words);
            s.Count = 0;
            return Value.Null;
        }));
        b("ToString/0", (c, a) => Value.FromString($"BloomFilter({B(c, a[0]).Count} items, {B(c, a[0]).BitCount} bits, {B(c, a[0]).HashCount} hashes)"));

        var k = r.Scope($"{M}.CountMinSketch");
        static SketchState K(NativeContext c, Value v) => c.State<SketchState>(v);
        static Value Add(NativeContext c, Value[] a, long count)
        {
            if (count < 0)
            {
                throw c.Throw("ArgumentError", "Counts cannot be negative.");
            }

            var hash = c.Comparer.Hash(a[1]);
            return Mutate<SketchState>(c, a[0], s =>
            {
                c.Charge(s.Depth);
                for (var row = 0; row < s.Depth; row++)
                {
                    s.Counts[s.Cell(row, hash)] += count;
                }

                s.Total += count;
                return Value.Null;
            });
        }

        k("CountMinSketch/2", (c, a) =>
        {
            var width = Int32(c, a[1].Bits, "width", 1, 16_000_000);
            var depth = Int32(c, a[2].Bits, "depth", 1, 64);
            if ((long)width * depth > 16_000_000)
            {
                throw c.Throw("ArgumentError", "Sketch is too large.");
            }

            return CollectionNatives.Init(a[0], new SketchState(width, depth));
        });
        k("Add/1", (c, a) => Add(c, a, 1));
        k("Add/2", (c, a) => Add(c, a, a[2].Bits));
        k("Estimate/1", (c, a) =>
        {
            var s = K(c, a[0]);
            var hash = c.Comparer.Hash(a[1]);
            c.Charge(s.Depth);
            return Value.FromInt(Enumerable.Range(0, s.Depth).Min(row => s.Counts[s.Cell(row, hash)]));
        });
        k("get_Total/0", (c, a) => Value.FromInt(K(c, a[0]).Total));
        k("Clear/0", (c, a) => Mutate<SketchState>(c, a[0], s =>
        {
            System.Array.Clear(s.Counts);
            s.Total = 0;
            return Value.Null;
        }));
        k("ToString/0", (c, a) => Value.FromString($"CountMinSketch({K(c, a[0]).Width}×{K(c, a[0]).Depth}, total {K(c, a[0]).Total})"));

        var h = r.Scope($"{M}.HyperLogLog");
        static HyperLogLogState H(NativeContext c, Value v) => c.State<HyperLogLogState>(v);
        h("HyperLogLog/1", (c, a) => CollectionNatives.Init(a[0], new HyperLogLogState(Int32(c, a[1].Bits, "precision", 4, 16))));
        h("Add/1", (c, a) =>
        {
            var hash = c.Comparer.Hash(a[1]);
            return Mutate<HyperLogLogState>(c, a[0], s => Void(() => s.Add(hash)));
        });
        h("Estimate/0", (c, a) =>
        {
            c.Charge(H(c, a[0]).Registers.Length / 16);
            return Value.FromInt(H(c, a[0]).Estimate());
        });
        h("Merge/1", (c, a) =>
        {
            var other = H(c, a[1]);
            return Mutate<HyperLogLogState>(c, a[0], s =>
            {
                if (s.Precision != other.Precision)
                {
                    throw c.Throw("ArgumentError", "HyperLogLog precisions differ.");
                }

                c.Charge(s.Registers.Length / 16);
                for (var i = 0; i < s.Registers.Length; i++)
                {
                    s.Registers[i] = Math.Max(s.Registers[i], other.Registers[i]);
                }

                return Value.Null;
            });
        });
        h("Clear/0", (c, a) => Mutate<HyperLogLogState>(c, a[0], s => Void(() => System.Array.Clear(s.Registers))));
        h("ToString/0", (c, a) => Value.FromString($"HyperLogLog(precision {H(c, a[0]).Precision}, estimate {H(c, a[0]).Estimate()})"));
    }

    private static void RegisterTrie(NativeRegistry r)
    {
        var F = r.Scope($"{M}.Trie");
        static TrieState T(NativeContext c, Value v) => c.State<TrieState>(v);
        F("Trie/0", (c, a) => CollectionNatives.Init(a[0], new TrieState()));
        F("get_Count/0", (c, a) => Value.FromInt(T(c, a[0]).Words.Count));
        F("Add/1", (c, a) =>
        {
            c.Charge(a[1].AsString.Length);
            return T(c, a[0]).Words.Contains(a[1].AsString) ? Value.False : Mutate<TrieState>(c, a[0], s => Value.FromBool(s.Words.Add(a[1].AsString)));
        });
        F("Remove/1", (c, a) =>
        {
            c.Charge(a[1].AsString.Length);
            return !T(c, a[0]).Words.Contains(a[1].AsString) ? Value.False : Mutate<TrieState>(c, a[0], s => Value.FromBool(s.Words.Remove(a[1].AsString)));
        });
        F("Contains/1", (c, a) =>
        {
            c.Charge(a[1].AsString.Length);
            return Value.FromBool(T(c, a[0]).Words.Contains(a[1].AsString));
        });
        F("HasPrefix/1", (c, a) =>
        {
            c.Charge(a[1].AsString.Length);
            return Value.FromBool(T(c, a[0]).WithPrefix(a[1].AsString).Any());
        });
        F("WithPrefix/1", (c, a) =>
        {
            var (s, prefix) = (T(c, a[0]), a[1].AsString);
            return SeqNatives.Create(c, ctx => s.WithPrefix(prefix).Select(Value.FromString).ToArray());
        });
        F("LongestPrefixOf/1", (c, a) =>
        {
            var (s, text) = (T(c, a[0]), a[1].AsString);
            c.Charge(text.Length);
            for (var n = text.Length; n >= 0; n--)
            {
                if (s.Words.Contains(text[..n]))
                {
                    return Value.FromString(text[..n]);
                }
            }

            return Value.Null;
        });
        F("Clear/0", (c, a) => Mutate<TrieState>(c, a[0], s => Void(s.Words.Clear)));
        F("GetIterator/0", (c, a) => c.NewIterator(T(c, a[0]).Enumerate(c)));
        F("ToString/0", (c, a) => Value.FromString($"Trie({T(c, a[0]).Words.Count} words)"));
    }

    private static void RegisterIntervals(NativeRegistry r)
    {
        var F = r.Scope($"{M}.IntervalTree");
        static IntervalState S(NativeContext c, Value v) => c.State<IntervalState>(v);
        F("IntervalTree/0", (c, a) => CollectionNatives.Init(a[0], new IntervalState()));
        F("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Items.Count));
        F("Add/3", (c, a) =>
        {
            if (a[2].Bits < a[1].Bits)
            {
                throw c.Throw("ArgumentError", "An interval's end cannot precede its start.");
            }

            return Mutate<IntervalState>(c, a[0], s =>
            {
                var entry = (a[1].Bits, a[2].Bits, a[3], s.Sequence++);
                var index = s.Items.FindLastIndex(i => i.Start <= a[1].Bits) + 1;
                c.Charge(1 + (s.Items.Count / 64));
                s.Items.Insert(index, entry);
                s.Invalidate();
                return Value.Null;
            });
        });
        F("Remove/3", (c, a) =>
        {
            var s = S(c, a[0]);
            var index = s.Items.FindIndex(i => i.Start == a[1].Bits && i.End == a[2].Bits && c.Comparer.Equals(i.Value, a[3]));
            if (index < 0)
            {
                return Value.False;
            }

            c.Mutating(s);
            s.Items.RemoveAt(index);
            s.Invalidate();
            return Value.True;
        });
        F("Overlapping/2", (c, a) =>
        {
            var found = S(c, a[0]).Query(a[1].Bits, a[2].Bits).ToArray();
            c.Charge(1 + (long)Math.Log2(S(c, a[0]).Items.Count + 1) + found.Length);
            return c.NewArray(found);
        });
        F("At/1", (c, a) =>
        {
            var found = S(c, a[0]).Query(a[1].Bits, a[1].Bits + 1).ToArray();
            c.Charge(1 + (long)Math.Log2(S(c, a[0]).Items.Count + 1) + found.Length);
            return c.NewArray(found);
        });
        F("Clear/0", (c, a) => Mutate<IntervalState>(c, a[0], s =>
        {
            s.Items.Clear();
            s.Invalidate();
            return Value.Null;
        }));
        F("GetIterator/0", (c, a) => c.NewIterator(S(c, a[0]).Enumerate(c)));
        F("ToString/0", (c, a) => Value.FromString($"IntervalTree({S(c, a[0]).Items.Count} intervals)"));
    }

    private static void RegisterSkipList(NativeRegistry r)
    {
        var F = r.Scope($"{M}.SkipList");
        static SkipListState S(NativeContext c, Value v) => c.State<SkipListState>(v);
        F("SkipList/0", (c, a) => CollectionNatives.Init(a[0], new SkipListState(c.Comparer)));
        F("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Count));
        F("get_Levels/0", (c, a) => Value.FromInt(S(c, a[0]).Levels));
        F("Add/1", (c, a) => S(c, a[0]).Contains(a[1], c) ? Value.False : Mutate<SkipListState>(c, a[0], s => Value.FromBool(s.Add(a[1], c))));
        F("Remove/1", (c, a) => !S(c, a[0]).Contains(a[1], c) ? Value.False : Mutate<SkipListState>(c, a[0], s => Value.FromBool(s.Remove(a[1], c))));
        F("Contains/1", (c, a) => Value.FromBool(S(c, a[0]).Contains(a[1], c)));
        F("Min/0", (c, a) => S(c, a[0]).Count > 0 ? S(c, a[0]).Values().First() : throw Empty(c, "skip list"));
        F("Max/0", (c, a) => S(c, a[0]).Count > 0 ? S(c, a[0]).Values().Last() : throw Empty(c, "skip list"));
        F("Range/2", (c, a) =>
        {
            var (s, from, to) = (S(c, a[0]), a[1], a[2]);
            return SeqNatives.Create(c, ctx => s.From(from, ctx).TakeWhile(v => ctx.Comparer.Compare(v, to) < 0).ToArray());
        });
        F("Clear/0", (c, a) => Mutate<SkipListState>(c, a[0], s => Void(s.Clear)));
        F("GetIterator/0", (c, a) => c.NewIterator(S(c, a[0]).Enumerate(c)));
        F("ToString/0", (c, a) => Value.FromString(Join(c, S(c, a[0]).Values())));
    }

    private static void RegisterRing(NativeRegistry r)
    {
        var F = r.Scope($"{M}.ConsistentHashRing");
        static RingHashState S(NativeContext c, Value v) => c.State<RingHashState>(v);
        F("ConsistentHashRing/1", (c, a) => CollectionNatives.Init(a[0], new RingHashState(Int32(c, a[1].Bits, "virtualNodes", 1, 10_000))));
        F("Add/1", (c, a) =>
        {
            var s = S(c, a[0]);
            if (s.Nodes.Any(n => c.Comparer.Equals(n, a[1])))
            {
                return Value.Null;
            }

            c.Mutating(s);
            s.Nodes.Add(a[1]);
            s.Rebuild(c);
            return Value.Null;
        });
        F("Remove/1", (c, a) =>
        {
            var s = S(c, a[0]);
            var index = s.Nodes.FindIndex(n => c.Comparer.Equals(n, a[1]));
            if (index < 0)
            {
                return Value.False;
            }

            c.Mutating(s);
            s.Nodes.RemoveAt(index);
            s.Rebuild(c);
            return Value.True;
        });
        F("NodeFor/1", (c, a) =>
        {
            var s = S(c, a[0]);
            if (s.Ring.Count == 0)
            {
                throw Empty(c, "hash ring");
            }

            var hash = Mix((ulong)StableHash.Of(a[1].AsString));
            int lo = 0, hi = s.Ring.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (s.Ring[mid].Hash < hash)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            c.Charge(1 + (long)Math.Log2(s.Ring.Count));
            return s.Nodes[s.Ring[lo == s.Ring.Count ? 0 : lo].Node];
        });
        F("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Nodes.Count));
        F("get_Nodes/0", (c, a) => c.NewArray([.. S(c, a[0]).Nodes]));
        F("ToString/0", (c, a) => Value.FromString($"ConsistentHashRing({S(c, a[0]).Nodes.Count} nodes × {S(c, a[0]).VirtualNodes})"));
    }
}
