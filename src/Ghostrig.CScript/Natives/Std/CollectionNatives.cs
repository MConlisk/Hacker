using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

#region States

internal sealed class ListState(List<Value> items) : NativeState, IValueSource
{
    public List<Value> Items { get; private set; } = items;

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Indexed());

    private IEnumerable<Value> Indexed()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            yield return Items[i];
        }
    }

    protected internal override object CaptureSnapshot() => Items.ToArray();

    protected internal override void RestoreSnapshot(object snapshot) => Items = [.. (Value[])snapshot];
}

/// <summary>A growable ring buffer: Queue, Deque and (fixed-capacity) RingBuffer.</summary>
internal sealed class RingState(int capacity, bool fixedCapacity) : NativeState, IValueSource
{
    private Value[] _buffer = new Value[Math.Max(capacity, 4)];
    private int _head;

    public int Count { get; private set; }

    public int Capacity => fixedCapacity ? capacity : _buffer.Length;

    public bool IsFixed => fixedCapacity;

    public Value this[int index]
    {
        get => _buffer[(_head + index) % _buffer.Length];
        set => _buffer[(_head + index) % _buffer.Length] = value;
    }

    public void PushBack(Value item)
    {
        Grow();
        _buffer[(_head + Count) % _buffer.Length] = item;
        Count++;
    }

    public void PushFront(Value item)
    {
        Grow();
        _head = (_head - 1 + _buffer.Length) % _buffer.Length;
        _buffer[_head] = item;
        Count++;
    }

    public Value PopFront()
    {
        var item = _buffer[_head];
        _buffer[_head] = Value.Null;
        _head = (_head + 1) % _buffer.Length;
        Count--;
        return item;
    }

    public Value PopBack()
    {
        var index = (_head + Count - 1) % _buffer.Length;
        var item = _buffer[index];
        _buffer[index] = Value.Null;
        Count--;
        return item;
    }

    public void Clear()
    {
        System.Array.Clear(_buffer);
        _head = 0;
        Count = 0;
    }

    private void Grow()
    {
        if (Count < _buffer.Length || fixedCapacity)
        {
            return;
        }

        var bigger = new Value[_buffer.Length * 2];
        for (var i = 0; i < Count; i++)
        {
            bigger[i] = this[i];
        }

        _buffer = bigger;
        _head = 0;
    }

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Indexed());

    private IEnumerable<Value> Indexed()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    protected internal override object CaptureSnapshot() => ((Value[])_buffer.Clone(), _head, Count);

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (buffer, head, count) = ((Value[], int, int))snapshot;
        _buffer = (Value[])buffer.Clone();
        _head = head;
        Count = count;
    }
}

internal sealed class StackState : NativeState, IValueSource
{
    public List<Value> Items { get; private set; } = [];

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, TopFirst());

    private IEnumerable<Value> TopFirst()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            yield return Items[i];
        }
    }

    protected internal override object CaptureSnapshot() => Items.ToArray();

    protected internal override void RestoreSnapshot(object snapshot) => Items = [.. (Value[])snapshot];
}

/// <summary>Binary min-heap; ties are broken by insertion sequence so dequeue order is deterministic and stable.</summary>
internal sealed class PriorityState(IComparer<Value> comparer) : NativeState, IValueSource
{
    private List<(Value Item, Value Priority, long Sequence)> _heap = [];
    private long _sequence;

    public int Count => _heap.Count;

    public (Value Item, Value Priority) Peek => (_heap[0].Item, _heap[0].Priority);

    private int Compare((Value Item, Value Priority, long Sequence) a, (Value Item, Value Priority, long Sequence) b)
    {
        var c = comparer.Compare(a.Priority, b.Priority);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    }

    public void Enqueue(Value item, Value priority)
    {
        _heap.Add((item, priority, _sequence++));
        var i = _heap.Count - 1;
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (Compare(_heap[i], _heap[parent]) >= 0)
            {
                break;
            }

            (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
            i = parent;
        }
    }

    public Value Dequeue()
    {
        var top = _heap[0].Item;
        var last = _heap[^1];
        _heap.RemoveAt(_heap.Count - 1);
        if (_heap.Count > 0)
        {
            _heap[0] = last;
            var i = 0;
            while (true)
            {
                int l = (2 * i) + 1, r = l + 1, smallest = i;
                if (l < _heap.Count && Compare(_heap[l], _heap[smallest]) < 0)
                {
                    smallest = l;
                }

                if (r < _heap.Count && Compare(_heap[r], _heap[smallest]) < 0)
                {
                    smallest = r;
                }

                if (smallest == i)
                {
                    break;
                }

                (_heap[i], _heap[smallest]) = (_heap[smallest], _heap[i]);
                i = smallest;
            }
        }

        return top;
    }

    public void Clear() => _heap.Clear();

    public List<(Value Item, Value Priority, long Sequence)> Ordered()
    {
        var copy = _heap.ToList();
        copy.Sort(Compare);
        return copy;
    }

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Ordered().Select(e => Tuple(e.Item, e.Priority)));

    protected internal override object CaptureSnapshot() => (_heap.ToArray(), _sequence);

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (heap, sequence) = (((Value, Value, long)[], long))snapshot;
        _heap = [.. heap];
        _sequence = sequence;
    }
}

internal sealed class MapState(IEqualityComparer<Value> comparer) : NativeState, IValueSource
{
    public OrderedTable Table { get; } = new(comparer);

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Table.Slots().Select(s => Tuple(Table.KeyAt(s), Table.ValueAt(s))));

    public IEnumerable<Value> KeysOf(NativeContext context) => Guard(context, Table.Keys());

    public IEnumerable<Value> ValuesOf(NativeContext context) => Guard(context, Table.Values());

    protected internal override object CaptureSnapshot() => Table.Capture();

    protected internal override void RestoreSnapshot(object snapshot) => Table.Restore(snapshot);
}

internal sealed class SetState(IEqualityComparer<Value> comparer) : NativeState, IValueSource
{
    public OrderedTable Table { get; } = new(comparer);

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Table.Keys());

    protected internal override object CaptureSnapshot() => Table.Capture();

    protected internal override void RestoreSnapshot(object snapshot) => Table.Restore(snapshot);
}

internal sealed class SortedState(IComparer<Value> comparer, bool isMap) : NativeState, IValueSource
{
    public SortedArray Data { get; } = new(comparer);

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Indexes(0, int.MaxValue).Select(Item));

    public Value Item(int i) => isMap ? Tuple(Data.KeyList[i], Data.ValueList[i]) : Data.KeyList[i];

    public IEnumerable<int> Indexes(int from, int to)
    {
        for (var i = from; i < Math.Min(to, Data.Count); i++)
        {
            yield return i;
        }
    }

    protected internal override object CaptureSnapshot() => Data.Capture();

    protected internal override void RestoreSnapshot(object snapshot) => Data.Restore(snapshot);
}

internal sealed class MultimapState(IEqualityComparer<Value> comparer) : NativeState, IValueSource
{
    public OrderedTable Table { get; } = new(comparer);

    public int Pairs { get; set; }

    public List<Value> ListFor(Value key) => Table.TryGetValue(key, out var list) ? (List<Value>)list.Object! : [];

    public IEnumerable<Value> Enumerate(NativeContext context) =>
        Guard(context, Table.Slots().SelectMany(s => ((List<Value>)Table.ValueAt(s).Object!).Select(v => Tuple(Table.KeyAt(s), v))));

    protected internal override object CaptureSnapshot()
    {
        var lists = Table.Slots().Select(s => (s, ((List<Value>)Table.ValueAt(s).Object!).ToArray())).ToArray();
        return (Table.Capture(), lists, Pairs);
    }

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (table, lists, pairs) = ((object, (int, Value[])[], int))snapshot;
        Table.Restore(table);
        foreach (var (slot, items) in lists)
        {
            Table.SetValueAt(slot, Value.FromObject(new List<Value>(items)));
        }

        Pairs = pairs;
    }
}

internal sealed class BitSetState(int length) : NativeState, IValueSource
{
    public int Length { get; } = length;

    public ulong[] Words { get; private set; } = new ulong[(length + 63) / 64];

    public bool Get(int i) => (Words[i >> 6] & (1UL << (i & 63))) != 0;

    public void Set(int i, bool on)
    {
        if (on)
        {
            Words[i >> 6] |= 1UL << (i & 63);
        }
        else
        {
            Words[i >> 6] &= ~(1UL << (i & 63));
        }
    }

    /// <summary>Clears the unused high bits of the last word after whole-word operations.</summary>
    public void Trim()
    {
        if (Length % 64 != 0 && Words.Length > 0)
        {
            Words[^1] &= (1UL << (Length % 64)) - 1;
        }
    }

    public int NextSet(int start)
    {
        for (var i = Math.Max(0, start); i < Length; i++)
        {
            var word = Words[i >> 6] >> (i & 63);
            if (word == 0)
            {
                i = (i | 63);
                continue;
            }

            return i + System.Numerics.BitOperations.TrailingZeroCount(word);
        }

        return -1;
    }

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, SetBits());

    private IEnumerable<Value> SetBits()
    {
        for (var i = NextSet(0); i >= 0; i = NextSet(i + 1))
        {
            yield return Value.FromInt(i);
        }
    }

    protected internal override object CaptureSnapshot() => Words.Clone();

    protected internal override void RestoreSnapshot(object snapshot) => Words = (ulong[])((ulong[])snapshot).Clone();
}

internal sealed class GridState(int width, int height, Value fill) : NativeState, IValueSource
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public Value[] Cells { get; private set; } = Enumerable.Repeat(fill, width * height).ToArray();

    public IEnumerable<Value> Enumerate(NativeContext context) => Guard(context, Cells.AsEnumerable());

    protected internal override object CaptureSnapshot() => Cells.Clone();

    protected internal override void RestoreSnapshot(object snapshot) => Cells = (Value[])((Value[])snapshot).Clone();
}

#endregion

/// <summary>Host implementations of the sequential, hashed, sorted and grid collections in <c>Std.Collections</c>.</summary>
internal static class CollectionNatives
{
    private const string M = "Std.Collections";

    public static void Register(NativeRegistry r)
    {
        RegisterList(r);
        RegisterQueues(r);
        RegisterMaps(r);
        RegisterSets(r);
        RegisterMultimap(r);
        RegisterBitSet(r);
        RegisterGrids(r);
    }

    internal static Value NewList(NativeContext c, List<Value> items) => c.NewInstance(c.Class(M, "List"), new ListState(items));

    internal static Value NewSet(NativeContext c, IEnumerable<Value> items)
    {
        var state = new SetState(c.Comparer);
        foreach (var item in items)
        {
            state.Table.TryAdd(item, Value.Null);
        }

        return c.NewInstance(c.Class(M, "Set"), state);
    }

    internal static Value NewMap(NativeContext c, MapState state) => c.NewInstance(c.Class(M, "Map"), state);

    private static Value Seq(NativeContext c, Func<NativeContext, IEnumerable<Value>> source) => SeqNatives.Create(c, source);

    #region List

    private static void RegisterList(NativeRegistry r)
    {
        var F = r.Scope($"{M}.List");
        static ListState L(NativeContext c, Value v) => c.State<ListState>(v);
        static List<Value> I(NativeContext c, Value v) => c.State<ListState>(v).Items;

        F("List/0", (c, a) => Init(a[0], new ListState([])));
        F("List/1", (c, a) => Init(a[0], new ListState(Materialize(c, a[1]))));
        F("get_Count/0", (c, a) => Value.FromInt(I(c, a[0]).Count));
        F("get_IsEmpty/0", (c, a) => Value.FromBool(I(c, a[0]).Count == 0));
        F("get_Item/1", (c, a) => I(c, a[0])[Index(c, a[1].Bits, I(c, a[0]).Count)]);
        F("set_Item/2", (c, a) =>
        {
            var s = L(c, a[0]);
            var i = Index(c, a[1].Bits, s.Items.Count);
            c.Mutating(s);
            s.Items[i] = a[2];
            return Value.Null;
        });
        F("Add/1", (c, a) => Mutate<ListState>(c, a[0], s => Void(() => s.Items.Add(a[1]))));
        F("AddAll/1", (c, a) =>
        {
            var items = Materialize(c, a[1]);
            return Mutate<ListState>(c, a[0], s => Void(() => s.Items.AddRange(items)));
        });
        F("Insert/2", (c, a) =>
        {
            var s = L(c, a[0]);
            var i = Index(c, a[1].Bits, s.Items.Count + 1);
            c.Mutating(s);
            c.Charge(s.Items.Count / 64);
            s.Items.Insert(i, a[2]);
            return Value.Null;
        });
        F("Remove/1", (c, a) =>
        {
            var s = L(c, a[0]);
            var i = IndexOf(c, s.Items, a[1]);
            if (i < 0)
            {
                return Value.False;
            }

            c.Mutating(s);
            s.Items.RemoveAt(i);
            return Value.True;
        });
        F("RemoveAt/1", (c, a) =>
        {
            var s = L(c, a[0]);
            var i = Index(c, a[1].Bits, s.Items.Count);
            c.Mutating(s);
            c.Charge(s.Items.Count / 64);
            var item = s.Items[i];
            s.Items.RemoveAt(i);
            return item;
        });
        F("RemoveRange/2", (c, a) =>
        {
            var s = L(c, a[0]);
            CheckRange(c, a[1].Bits, a[2].Bits, s.Items.Count);
            c.Mutating(s);
            c.Charge(s.Items.Count / 64);
            s.Items.RemoveRange((int)a[1].Bits, (int)a[2].Bits);
            return Value.Null;
        });
        F("RemoveAll/1", (c, a) =>
        {
            var s = L(c, a[0]);
            var predicate = Fn(a[1]);
            var keep = new List<Value>(s.Items.Count);
            foreach (var item in s.Items.ToArray())
            {
                c.Charge(1);
                if (!c.Invoke(predicate, item).AsBool)
                {
                    keep.Add(item);
                }
            }

            var removed = s.Items.Count - keep.Count;
            if (removed > 0)
            {
                c.Mutating(s);
                s.Items.Clear();
                s.Items.AddRange(keep);
            }

            return Value.FromInt(removed);
        });
        F("Clear/0", (c, a) => Mutate<ListState>(c, a[0], s => Void(s.Items.Clear)));
        F("Contains/1", (c, a) => Value.FromBool(IndexOf(c, I(c, a[0]), a[1]) >= 0));
        F("IndexOf/1", (c, a) => Value.FromInt(IndexOf(c, I(c, a[0]), a[1])));
        F("LastIndexOf/1", (c, a) =>
        {
            var items = I(c, a[0]);
            for (var i = items.Count - 1; i >= 0; i--)
            {
                c.Charge(1);
                if (c.Comparer.Equals(items[i], a[1]))
                {
                    return Value.FromInt(i);
                }
            }

            return Value.FromInt(-1);
        });
        F("FindIndex/1", (c, a) => Value.FromInt(FindIndex(c, I(c, a[0]), Fn(a[1]))));
        F("Find/1", (c, a) =>
        {
            var items = I(c, a[0]);
            var i = FindIndex(c, items, Fn(a[1]));
            return i >= 0 ? items[i] : Value.Null;
        });
        F("First/0", (c, a) => I(c, a[0]) is { Count: > 0 } items ? items[0] : throw Empty(c, "list"));
        F("Last/0", (c, a) => I(c, a[0]) is { Count: > 0 } items ? items[^1] : throw Empty(c, "list"));
        F("Sort/0", (c, a) => SortList(c, L(c, a[0]), c.Comparer));
        F("Sort/1", (c, a) => SortList(c, L(c, a[0]), c.Comparer.From(a[1])));
        F("SortBy/1", (c, a) => SortList(c, L(c, a[0]), c.Comparer.ByKey(Fn(a[1]))));
        F("Reverse/0", (c, a) => Mutate<ListState>(c, a[0], s =>
        {
            c.Charge(s.Items.Count / 8);
            s.Items.Reverse();
            return Value.Null;
        }));
        F("Swap/2", (c, a) =>
        {
            var s = L(c, a[0]);
            int i = Index(c, a[1].Bits, s.Items.Count), j = Index(c, a[2].Bits, s.Items.Count);
            c.Mutating(s);
            (s.Items[i], s.Items[j]) = (s.Items[j], s.Items[i]);
            return Value.Null;
        });
        F("BinarySearch/1", (c, a) =>
        {
            var items = I(c, a[0]);
            int lo = 0, hi = items.Count - 1;
            while (lo <= hi)
            {
                c.Charge(1);
                var mid = lo + ((hi - lo) >> 1);
                var cmp = c.Comparer.Compare(items[mid], a[1]);
                if (cmp == 0)
                {
                    return Value.FromInt(mid);
                }

                (lo, hi) = cmp < 0 ? (mid + 1, hi) : (lo, mid - 1);
            }

            return Value.FromInt(~lo);
        });
        F("Slice/2", (c, a) =>
        {
            var items = I(c, a[0]);
            CheckRange(c, a[1].Bits, a[2].Bits, items.Count);
            c.Charge(a[2].Bits / 8);
            return NewList(c, items.GetRange((int)a[1].Bits, (int)a[2].Bits));
        });
        F("Copy/0", (c, a) =>
        {
            c.Charge(I(c, a[0]).Count / 8);
            return NewList(c, [.. I(c, a[0])]);
        });
        F("ToArray/0", (c, a) =>
        {
            c.Charge(I(c, a[0]).Count / 8);
            return c.NewArray([.. I(c, a[0])]);
        });
        F("AsSeq/0", (c, a) =>
        {
            var s = L(c, a[0]);
            return Seq(c, s.Enumerate);
        });
        F("AsReadOnly/0", (c, a) => c.NewInstance(c.Class(M, "ReadOnlyList"), L(c, a[0])));
        F("GetIterator/0", (c, a) => c.NewIterator(L(c, a[0]).Enumerate(c)));
        F("ToString/0", (c, a) => Value.FromString(Join(c, I(c, a[0]))));

        var ro = r.Scope($"{M}.ReadOnlyList");
        ro("get_Count/0", (c, a) => Value.FromInt(I(c, a[0]).Count));
        ro("get_Item/1", (c, a) => I(c, a[0])[Index(c, a[1].Bits, I(c, a[0]).Count)]);
        ro("Contains/1", (c, a) => Value.FromBool(IndexOf(c, I(c, a[0]), a[1]) >= 0));
        ro("IndexOf/1", (c, a) => Value.FromInt(IndexOf(c, I(c, a[0]), a[1])));
        ro("ToArray/0", (c, a) => c.NewArray([.. I(c, a[0])]));
        ro("AsSeq/0", (c, a) =>
        {
            var s = L(c, a[0]);
            return Seq(c, s.Enumerate);
        });
        ro("GetIterator/0", (c, a) => c.NewIterator(L(c, a[0]).Enumerate(c)));
        ro("ToString/0", (c, a) => Value.FromString(Join(c, I(c, a[0]))));
    }

    internal static Value Init(Value receiver, object state)
    {
        receiver.As<ObjectInstance>().NativeState = state;
        return Value.Null;
    }

    internal static void CheckRange(NativeContext c, long start, long count, int length)
    {
        if (start < 0 || count < 0 || start + count > length)
        {
            throw c.Throw("IndexError", $"Range ({start}, {count}) is out of range for length {length}.");
        }
    }

    internal static int IndexOf(NativeContext c, List<Value> items, Value item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            c.Charge(1);
            if (c.Comparer.Equals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindIndex(NativeContext c, List<Value> items, FunctionObject predicate)
    {
        var snapshot = items.ToArray();
        for (var i = 0; i < snapshot.Length; i++)
        {
            c.Charge(1);
            if (c.Invoke(predicate, snapshot[i]).AsBool)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Stable merge sort (List.Sort in .NET is unstable). Cost: n log n steps.</summary>
    internal static Value[] StableSort(NativeContext c, Value[] items, IComparer<Value> comparer)
    {
        c.Charge((long)(items.Length * Math.Max(1, Math.Log2(Math.Max(2, items.Length)))));
        var keyed = items.Select((v, i) => (v, i)).ToArray();
        System.Array.Sort(keyed, (x, y) =>
        {
            var cmp = comparer.Compare(x.v, y.v);
            return cmp != 0 ? cmp : x.i.CompareTo(y.i);
        });
        return [.. keyed.Select(k => k.v)];
    }

    private static Value SortList(NativeContext c, ListState s, IComparer<Value> comparer)
    {
        var sorted = StableSort(c, [.. s.Items], comparer);
        c.Mutating(s);
        s.Items.Clear();
        s.Items.AddRange(sorted);
        return Value.Null;
    }

    #endregion

    #region Queue, Stack, Deque, PriorityQueue, RingBuffer

    private static void RegisterQueues(NativeRegistry r)
    {
        static RingState R(NativeContext c, Value v) => c.State<RingState>(v);
        static Value Front(NativeContext c, RingState s, string what) => s.Count > 0 ? s[0] : throw Empty(c, what);
        static Value Back(NativeContext c, RingState s, string what) => s.Count > 0 ? s[s.Count - 1] : throw Empty(c, what);
        static Value Pop(NativeContext c, Value v, bool front, string what)
        {
            var s = R(c, v);
            if (s.Count == 0)
            {
                throw Empty(c, what);
            }

            c.Mutating(s);
            return front ? s.PopFront() : s.PopBack();
        }

        void Common(string type, string what)
        {
            var F = r.Scope($"{M}.{type}");
            F("get_Count/0", (c, a) => Value.FromInt(R(c, a[0]).Count));
            F("Clear/0", (c, a) => Mutate<RingState>(c, a[0], s => Void(s.Clear)));
            F("ToArray/0", (c, a) => c.NewArray([.. R(c, a[0]).Enumerate(c)]));
            F("GetIterator/0", (c, a) => c.NewIterator(R(c, a[0]).Enumerate(c)));
            F("ToString/0", (c, a) => Value.FromString(Join(c, R(c, a[0]).Enumerate(c))));
            if (type != "RingBuffer")
            {
                F("get_IsEmpty/0", (c, a) => Value.FromBool(R(c, a[0]).Count == 0));
            }

            _ = what;
        }

        Common("Queue", "queue");
        var q = r.Scope($"{M}.Queue");
        q("Queue/0", (c, a) => Init(a[0], new RingState(4, false)));
        q("Enqueue/1", (c, a) => Mutate<RingState>(c, a[0], s => Void(() => s.PushBack(a[1]))));
        q("Dequeue/0", (c, a) => Pop(c, a[0], true, "queue"));
        q("TryDequeue/0", (c, a) => R(c, a[0]).Count == 0 ? Value.Null : Pop(c, a[0], true, "queue"));
        q("Peek/0", (c, a) => Front(c, R(c, a[0]), "queue"));

        Common("Deque", "deque");
        var d = r.Scope($"{M}.Deque");
        d("Deque/0", (c, a) => Init(a[0], new RingState(4, false)));
        d("get_Item/1", (c, a) => R(c, a[0])[Index(c, a[1].Bits, R(c, a[0]).Count)]);
        d("set_Item/2", (c, a) =>
        {
            var s = R(c, a[0]);
            var i = Index(c, a[1].Bits, s.Count);
            c.Mutating(s);
            s[i] = a[2];
            return Value.Null;
        });
        d("PushFront/1", (c, a) => Mutate<RingState>(c, a[0], s => Void(() => s.PushFront(a[1]))));
        d("PushBack/1", (c, a) => Mutate<RingState>(c, a[0], s => Void(() => s.PushBack(a[1]))));
        d("PopFront/0", (c, a) => Pop(c, a[0], true, "deque"));
        d("PopBack/0", (c, a) => Pop(c, a[0], false, "deque"));
        d("PeekFront/0", (c, a) => Front(c, R(c, a[0]), "deque"));
        d("PeekBack/0", (c, a) => Back(c, R(c, a[0]), "deque"));

        Common("RingBuffer", "ring buffer");
        var rb = r.Scope($"{M}.RingBuffer");
        rb("RingBuffer/1", (c, a) => Init(a[0], new RingState(Int32(c, a[1].Bits, "Capacity", 1, 16_000_000), true)));
        rb("get_Capacity/0", (c, a) => Value.FromInt(R(c, a[0]).Capacity));
        rb("get_IsFull/0", (c, a) => Value.FromBool(R(c, a[0]).Count == R(c, a[0]).Capacity));
        rb("get_Item/1", (c, a) => R(c, a[0])[Index(c, a[1].Bits, R(c, a[0]).Count)]);
        rb("Add/1", (c, a) => Mutate<RingState>(c, a[0], s =>
        {
            var overwrite = s.Count == s.Capacity;
            if (overwrite)
            {
                s.PopFront();
            }

            s.PushBack(a[1]);
            return Value.FromBool(overwrite);
        }));
        rb("RemoveOldest/0", (c, a) => Pop(c, a[0], true, "ring buffer"));
        rb("Oldest/0", (c, a) => Front(c, R(c, a[0]), "ring buffer"));
        rb("Newest/0", (c, a) => Back(c, R(c, a[0]), "ring buffer"));

        var st = r.Scope($"{M}.Stack");
        static StackState S(NativeContext c, Value v) => c.State<StackState>(v);
        st("Stack/0", (c, a) => Init(a[0], new StackState()));
        st("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Items.Count));
        st("get_IsEmpty/0", (c, a) => Value.FromBool(S(c, a[0]).Items.Count == 0));
        st("Push/1", (c, a) => Mutate<StackState>(c, a[0], s => Void(() => s.Items.Add(a[1]))));
        st("Pop/0", (c, a) => S(c, a[0]).Items.Count == 0 ? throw Empty(c, "stack") : Mutate<StackState>(c, a[0], s =>
        {
            var top = s.Items[^1];
            s.Items.RemoveAt(s.Items.Count - 1);
            return top;
        }));
        st("TryPop/0", (c, a) => S(c, a[0]).Items.Count == 0 ? Value.Null : Mutate<StackState>(c, a[0], s =>
        {
            var top = s.Items[^1];
            s.Items.RemoveAt(s.Items.Count - 1);
            return top;
        }));
        st("Peek/0", (c, a) => S(c, a[0]).Items is { Count: > 0 } items ? items[^1] : throw Empty(c, "stack"));
        st("Clear/0", (c, a) => Mutate<StackState>(c, a[0], s => Void(s.Items.Clear)));
        st("ToArray/0", (c, a) => c.NewArray([.. S(c, a[0]).Enumerate(c)]));
        st("GetIterator/0", (c, a) => c.NewIterator(S(c, a[0]).Enumerate(c)));
        st("ToString/0", (c, a) => Value.FromString(Join(c, S(c, a[0]).Enumerate(c))));

        var pq = r.Scope($"{M}.PriorityQueue");
        static PriorityState P(NativeContext c, Value v) => c.State<PriorityState>(v);
        static int Log(int n) => 1 + (int)Math.Log2(Math.Max(1, n));
        pq("PriorityQueue/0", (c, a) => Init(a[0], new PriorityState(c.Comparer)));
        pq("PriorityQueue/1", (c, a) => Init(a[0], new PriorityState(c.Comparer.From(a[1]))));
        pq("get_Count/0", (c, a) => Value.FromInt(P(c, a[0]).Count));
        pq("get_IsEmpty/0", (c, a) => Value.FromBool(P(c, a[0]).Count == 0));
        pq("Enqueue/2", (c, a) => Mutate<PriorityState>(c, a[0], s =>
        {
            c.Charge(Log(s.Count));
            s.Enqueue(a[1], a[2]);
            return Value.Null;
        }));
        pq("Dequeue/0", (c, a) => P(c, a[0]).Count == 0 ? throw Empty(c, "priority queue") : Mutate<PriorityState>(c, a[0], s =>
        {
            c.Charge(Log(s.Count));
            return s.Dequeue();
        }));
        pq("TryDequeue/0", (c, a) => P(c, a[0]).Count == 0 ? Value.Null : Mutate<PriorityState>(c, a[0], s =>
        {
            c.Charge(Log(s.Count));
            return s.Dequeue();
        }));
        pq("Peek/0", (c, a) => P(c, a[0]).Count > 0 ? P(c, a[0]).Peek.Item : throw Empty(c, "priority queue"));
        pq("PeekPriority/0", (c, a) => P(c, a[0]).Count > 0 ? P(c, a[0]).Peek.Priority : throw Empty(c, "priority queue"));
        pq("Clear/0", (c, a) => Mutate<PriorityState>(c, a[0], s => Void(s.Clear)));
        pq("ToArray/0", (c, a) =>
        {
            c.Charge(P(c, a[0]).Count * Log(P(c, a[0]).Count));
            return c.NewArray([.. P(c, a[0]).Ordered().Select(e => e.Item)]);
        });
        pq("GetIterator/0", (c, a) => c.NewIterator(P(c, a[0]).Enumerate(c)));
        pq("ToString/0", (c, a) => Value.FromString(Join(c, P(c, a[0]).Enumerate(c))));
    }

    #endregion

    #region Map, ReadOnlyMap, SortedMap

    private static Exception KeyMissing(NativeContext c, Value key) => c.Throw("IndexError", $"Key '{Display(c, key)}' was not found.");

    private static string MapText(NativeContext c, IEnumerable<(Value Key, Value Value)> entries)
    {
        var parts = entries.Take(65).Select((e, i) => i == 64 ? "…" : $"{Display(c, e.Key)}: {Display(c, e.Value)}").ToList();
        c.Charge(parts.Count);
        return "{" + string.Join(", ", parts) + "}";
    }

    private static void RegisterMaps(NativeRegistry r)
    {
        static MapState S(NativeContext c, Value v) => c.State<MapState>(v);

        void ReadMembers(Action<string, StdFunction> F)
        {
            F("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Table.Count));
            F("get_Item/1", (c, a) => S(c, a[0]).Table.TryGetValue(a[1], out var v) ? v : throw KeyMissing(c, a[1]));
            F("ContainsKey/1", (c, a) => Value.FromBool(S(c, a[0]).Table.ContainsKey(a[1])));
            F("Get/1", (c, a) => S(c, a[0]).Table.TryGetValue(a[1], out var v) ? v : Value.Null);
            F("get_Keys/0", (c, a) =>
            {
                var s = S(c, a[0]);
                return Seq(c, s.KeysOf);
            });
            F("get_Values/0", (c, a) =>
            {
                var s = S(c, a[0]);
                return Seq(c, s.ValuesOf);
            });
            F("GetIterator/0", (c, a) => c.NewIterator(S(c, a[0]).Enumerate(c)));
            F("ToString/0", (c, a) =>
            {
                var t = S(c, a[0]).Table;
                return Value.FromString(MapText(c, t.Slots().Select(i => (t.KeyAt(i), t.ValueAt(i)))));
            });
        }

        var F = r.Scope($"{M}.Map");
        ReadMembers(F);
        F("Map/0", (c, a) => Init(a[0], new MapState(c.Comparer)));
        F("get_IsEmpty/0", (c, a) => Value.FromBool(S(c, a[0]).Table.Count == 0));
        F("set_Item/2", (c, a) => Mutate<MapState>(c, a[0], s => Void(() => s.Table.Set(a[1], a[2]))));
        F("ContainsValue/1", (c, a) =>
        {
            foreach (var v in S(c, a[0]).Table.Values())
            {
                c.Charge(1);
                if (c.Comparer.Equals(v, a[1]))
                {
                    return Value.True;
                }
            }

            return Value.False;
        });
        F("GetOr/2", (c, a) => S(c, a[0]).Table.TryGetValue(a[1], out var v) ? v : a[2]);
        F("GetOrAdd/2", (c, a) =>
        {
            var s = S(c, a[0]);
            if (s.Table.TryGetValue(a[1], out var v))
            {
                return v;
            }

            var created = c.Invoke(Fn(a[2]), a[1]);
            c.Mutating(s);
            s.Table.Set(a[1], created);
            return created;
        });
        F("Upsert/3", (c, a) =>
        {
            var s = S(c, a[0]);
            var next = s.Table.TryGetValue(a[1], out var current) ? c.Invoke(Fn(a[3]), current) : a[2];
            c.Mutating(s);
            s.Table.Set(a[1], next);
            return next;
        });
        F("TryAdd/2", (c, a) => S(c, a[0]).Table.ContainsKey(a[1]) ? Value.False
            : Mutate<MapState>(c, a[0], s => Value.FromBool(s.Table.TryAdd(a[1], a[2]))));
        F("Remove/1", (c, a) => !S(c, a[0]).Table.ContainsKey(a[1]) ? Value.False
            : Mutate<MapState>(c, a[0], s => Value.FromBool(s.Table.Remove(a[1], out _))));
        F("Clear/0", (c, a) => Mutate<MapState>(c, a[0], s => Void(s.Table.Clear)));
        F("get_Entries/0", (c, a) =>
        {
            var s = S(c, a[0]);
            return Seq(c, s.Enumerate);
        });
        F("AsReadOnly/0", (c, a) => c.NewInstance(c.Class(M, "ReadOnlyMap"), S(c, a[0])));

        ReadMembers(r.Scope($"{M}.ReadOnlyMap"));

        var sm = r.Scope($"{M}.SortedMap");
        static SortedState T(NativeContext c, Value v) => c.State<SortedState>(v);
        static int Log(int n) => 1 + (int)Math.Log2(Math.Max(1, n));
        sm("SortedMap/0", (c, a) => Init(a[0], new SortedState(c.Comparer, isMap: true)));
        sm("SortedMap/1", (c, a) => Init(a[0], new SortedState(c.Comparer.From(a[1]), isMap: true)));
        sm("get_Count/0", (c, a) => Value.FromInt(T(c, a[0]).Data.Count));
        sm("get_IsEmpty/0", (c, a) => Value.FromBool(T(c, a[0]).Data.Count == 0));
        sm("get_Item/1", (c, a) =>
        {
            var d = T(c, a[0]).Data;
            c.Charge(Log(d.Count));
            var i = d.Find(a[1]);
            return i >= 0 ? d.ValueList[i] : throw KeyMissing(c, a[1]);
        });
        sm("set_Item/2", (c, a) => Mutate<SortedState>(c, a[0], s =>
        {
            c.Charge(Log(s.Data.Count) + (s.Data.Count / 64));
            s.Data.Set(a[1], a[2]);
            return Value.Null;
        }));
        sm("ContainsKey/1", (c, a) =>
        {
            c.Charge(Log(T(c, a[0]).Data.Count));
            return Value.FromBool(T(c, a[0]).Data.Find(a[1]) >= 0);
        });
        sm("Get/1", (c, a) =>
        {
            var d = T(c, a[0]).Data;
            c.Charge(Log(d.Count));
            var i = d.Find(a[1]);
            return i >= 0 ? d.ValueList[i] : Value.Null;
        });
        sm("GetOr/2", (c, a) =>
        {
            var d = T(c, a[0]).Data;
            c.Charge(Log(d.Count));
            var i = d.Find(a[1]);
            return i >= 0 ? d.ValueList[i] : a[2];
        });
        sm("Remove/1", (c, a) => T(c, a[0]).Data.Find(a[1]) < 0 ? Value.False : Mutate<SortedState>(c, a[0], s =>
        {
            c.Charge(Log(s.Data.Count) + (s.Data.Count / 64));
            return Value.FromBool(s.Data.Remove(a[1]));
        }));
        sm("Clear/0", (c, a) => Mutate<SortedState>(c, a[0], s => Void(s.Data.Clear)));
        RegisterSortedQueries(sm, "map");
        sm("get_Keys/0", (c, a) =>
        {
            var s = T(c, a[0]);
            return Seq(c, ctx => s.Enumerate(ctx).Select(t => t.As<TupleObject>().Items[0]));
        });
        sm("get_Values/0", (c, a) =>
        {
            var s = T(c, a[0]);
            return Seq(c, ctx => s.Enumerate(ctx).Select(t => t.As<TupleObject>().Items[1]));
        });
        sm("GetIterator/0", (c, a) => c.NewIterator(T(c, a[0]).Enumerate(c)));
        sm("ToString/0", (c, a) =>
        {
            var d = T(c, a[0]).Data;
            return Value.FromString(MapText(c, d.KeyList.Zip(d.ValueList)));
        });
    }

    /// <summary>First/Last/Floor/Ceiling/Range shared by SortedMap (keys) and SortedSet (items).</summary>
    private static void RegisterSortedQueries(Action<string, StdFunction> F, string what)
    {
        static SortedState T(NativeContext c, Value v) => c.State<SortedState>(v);
        var (first, last) = what == "map" ? ("FirstKey/0", "LastKey/0") : ("Min/0", "Max/0");
        F(first, (c, a) => T(c, a[0]).Data is { Count: > 0 } d ? d.KeyList[0] : throw Empty(c, what));
        F(last, (c, a) => T(c, a[0]).Data is { Count: > 0 } d ? d.KeyList[^1] : throw Empty(c, what));
        F("Floor/1", (c, a) =>
        {
            var d = T(c, a[0]).Data;
            var i = d.Find(a[1]);
            var index = i >= 0 ? i : ~i - 1;
            return index >= 0 ? d.KeyList[index] : Value.Null;
        });
        F("Ceiling/1", (c, a) =>
        {
            var d = T(c, a[0]).Data;
            var index = d.LowerBound(a[1]);
            return index < d.Count ? d.KeyList[index] : Value.Null;
        });
        F("Range/2", (c, a) =>
        {
            var s = T(c, a[0]);
            Value from = a[1], to = a[2];
            return Seq(c, ctx =>
            {
                var start = s.Data.LowerBound(from);
                var end = s.Data.LowerBound(to);
                return s.Indexes(start, end).Select(s.Item).ToArray();
            });
        });
    }

    #endregion

    #region Set, SortedSet

    private static void RegisterSets(NativeRegistry r)
    {
        static SetState S(NativeContext c, Value v) => c.State<SetState>(v);
        var F = r.Scope($"{M}.Set");
        F("Set/0", (c, a) => Init(a[0], new SetState(c.Comparer)));
        F("Set/1", (c, a) =>
        {
            var s = new SetState(c.Comparer);
            foreach (var item in Metered(c, a[1]))
            {
                s.Table.TryAdd(item, Value.Null);
            }

            return Init(a[0], s);
        });
        F("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Table.Count));
        F("get_IsEmpty/0", (c, a) => Value.FromBool(S(c, a[0]).Table.Count == 0));
        F("Add/1", (c, a) => S(c, a[0]).Table.ContainsKey(a[1]) ? Value.False : Mutate<SetState>(c, a[0], s => Value.FromBool(s.Table.TryAdd(a[1], Value.Null))));
        F("Remove/1", (c, a) => !S(c, a[0]).Table.ContainsKey(a[1]) ? Value.False : Mutate<SetState>(c, a[0], s => Value.FromBool(s.Table.Remove(a[1], out _))));
        F("Contains/1", (c, a) => Value.FromBool(S(c, a[0]).Table.ContainsKey(a[1])));
        F("Clear/0", (c, a) => Mutate<SetState>(c, a[0], s => Void(s.Table.Clear)));
        F("UnionWith/1", (c, a) =>
        {
            var items = Materialize(c, a[1]);
            return Mutate<SetState>(c, a[0], s => Void(() => items.ForEach(i => s.Table.TryAdd(i, Value.Null))));
        });
        F("IntersectWith/1", (c, a) =>
        {
            var other = new HashSet<Value>(Materialize(c, a[1]), c.Comparer);
            return Mutate<SetState>(c, a[0], s => Void(() =>
            {
                foreach (var key in s.Table.Keys().Where(k => !other.Contains(k)).ToList())
                {
                    s.Table.Remove(key, out _);
                }
            }));
        });
        F("ExceptWith/1", (c, a) =>
        {
            var items = Materialize(c, a[1]);
            return Mutate<SetState>(c, a[0], s => Void(() => items.ForEach(i => s.Table.Remove(i, out _))));
        });
        F("Union/1", (c, a) => NewSet(c, Metered(c, a[0]).Concat(Metered(c, a[1])).ToList()));
        F("Intersect/1", (c, a) =>
        {
            var other = S(c, a[1]).Table;
            return NewSet(c, Metered(c, a[0]).Where(other.ContainsKey).ToList());
        });
        F("Except/1", (c, a) =>
        {
            var other = S(c, a[1]).Table;
            return NewSet(c, Metered(c, a[0]).Where(k => !other.ContainsKey(k)).ToList());
        });
        F("IsSubsetOf/1", (c, a) =>
        {
            var other = S(c, a[1]).Table;
            return Value.FromBool(Metered(c, a[0]).All(other.ContainsKey));
        });
        F("Overlaps/1", (c, a) =>
        {
            var other = S(c, a[1]).Table;
            return Value.FromBool(Metered(c, a[0]).Any(other.ContainsKey));
        });
        F("ToArray/0", (c, a) => c.NewArray([.. S(c, a[0]).Table.Keys()]));
        F("AsSeq/0", (c, a) =>
        {
            var s = S(c, a[0]);
            return Seq(c, s.Enumerate);
        });
        F("GetIterator/0", (c, a) => c.NewIterator(S(c, a[0]).Enumerate(c)));
        F("ToString/0", (c, a) => Value.FromString(Join(c, S(c, a[0]).Table.Keys(), "{", "}")));

        static SortedState T(NativeContext c, Value v) => c.State<SortedState>(v);
        static int Log(int n) => 1 + (int)Math.Log2(Math.Max(1, n));
        var ss = r.Scope($"{M}.SortedSet");
        ss("SortedSet/0", (c, a) => Init(a[0], new SortedState(c.Comparer, isMap: false)));
        ss("SortedSet/1", (c, a) => Init(a[0], new SortedState(c.Comparer.From(a[1]), isMap: false)));
        ss("get_Count/0", (c, a) => Value.FromInt(T(c, a[0]).Data.Count));
        ss("get_IsEmpty/0", (c, a) => Value.FromBool(T(c, a[0]).Data.Count == 0));
        ss("Add/1", (c, a) => T(c, a[0]).Data.Find(a[1]) >= 0 ? Value.False : Mutate<SortedState>(c, a[0], s =>
        {
            c.Charge(Log(s.Data.Count) + (s.Data.Count / 64));
            return Value.FromBool(s.Data.Set(a[1], Value.Null));
        }));
        ss("Remove/1", (c, a) => T(c, a[0]).Data.Find(a[1]) < 0 ? Value.False : Mutate<SortedState>(c, a[0], s =>
        {
            c.Charge(Log(s.Data.Count) + (s.Data.Count / 64));
            return Value.FromBool(s.Data.Remove(a[1]));
        }));
        ss("Contains/1", (c, a) =>
        {
            c.Charge(Log(T(c, a[0]).Data.Count));
            return Value.FromBool(T(c, a[0]).Data.Find(a[1]) >= 0);
        });
        ss("Clear/0", (c, a) => Mutate<SortedState>(c, a[0], s => Void(s.Data.Clear)));
        RegisterSortedQueries(ss, "set");
        ss("ToArray/0", (c, a) => c.NewArray([.. T(c, a[0]).Data.KeyList]));
        ss("AsSeq/0", (c, a) =>
        {
            var s = T(c, a[0]);
            return Seq(c, s.Enumerate);
        });
        ss("GetIterator/0", (c, a) => c.NewIterator(T(c, a[0]).Enumerate(c)));
        ss("ToString/0", (c, a) => Value.FromString(Join(c, T(c, a[0]).Data.KeyList, "{", "}")));
    }

    #endregion

    #region Multimap

    private static void RegisterMultimap(NativeRegistry r)
    {
        static MultimapState S(NativeContext c, Value v) => c.State<MultimapState>(v);
        var F = r.Scope($"{M}.Multimap");
        F("Multimap/0", (c, a) => Init(a[0], new MultimapState(c.Comparer)));
        F("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Pairs));
        F("get_KeyCount/0", (c, a) => Value.FromInt(S(c, a[0]).Table.Count));
        F("Add/2", (c, a) => Mutate<MultimapState>(c, a[0], s =>
        {
            if (!s.Table.TryGetValue(a[1], out var list))
            {
                list = Value.FromObject(new List<Value>());
                s.Table.Set(a[1], list);
            }

            ((List<Value>)list.Object!).Add(a[2]);
            s.Pairs++;
            return Value.Null;
        }));
        F("Get/1", (c, a) => c.NewArray([.. S(c, a[0]).ListFor(a[1])]));
        F("ContainsKey/1", (c, a) => Value.FromBool(S(c, a[0]).Table.ContainsKey(a[1])));
        F("Remove/2", (c, a) =>
        {
            var s = S(c, a[0]);
            var list = s.ListFor(a[1]);
            var i = IndexOf(c, list, a[2]);
            if (i < 0)
            {
                return Value.False;
            }

            c.Mutating(s);
            list.RemoveAt(i);
            s.Pairs--;
            if (list.Count == 0)
            {
                s.Table.Remove(a[1], out _);
            }

            return Value.True;
        });
        F("RemoveAll/1", (c, a) =>
        {
            var s = S(c, a[0]);
            var count = s.ListFor(a[1]).Count;
            if (count > 0)
            {
                c.Mutating(s);
                s.Table.Remove(a[1], out _);
                s.Pairs -= count;
            }

            return Value.FromInt(count);
        });
        F("Clear/0", (c, a) => Mutate<MultimapState>(c, a[0], s =>
        {
            s.Table.Clear();
            s.Pairs = 0;
            return Value.Null;
        }));
        F("get_Keys/0", (c, a) =>
        {
            var s = S(c, a[0]);
            return Seq(c, ctx => s.Enumerate(ctx).Select(t => t.As<TupleObject>().Items[0]).Distinct(ctx.Comparer));
        });
        F("GetIterator/0", (c, a) => c.NewIterator(S(c, a[0]).Enumerate(c)));
        F("ToString/0", (c, a) =>
        {
            var t = S(c, a[0]).Table;
            return Value.FromString(MapText(c, t.Slots().Select(i => (t.KeyAt(i), Value.FromString(Join(c, (List<Value>)t.ValueAt(i).Object!))))));
        });
    }

    #endregion

    #region BitSet

    private static void RegisterBitSet(NativeRegistry r)
    {
        static BitSetState B(NativeContext c, Value v) => c.State<BitSetState>(v);
        static int Bit(NativeContext c, BitSetState s, long i) => Index(c, i, s.Length);
        static Value Combine(NativeContext c, Value[] a, Func<ulong, ulong, ulong> op)
        {
            var (s, o) = (B(c, a[0]), B(c, a[1]));
            if (s.Length != o.Length)
            {
                throw c.Throw("ArgumentError", $"BitSet lengths differ ({s.Length} vs {o.Length}).");
            }

            c.Mutating(s);
            c.Charge(s.Words.Length);
            for (var i = 0; i < s.Words.Length; i++)
            {
                s.Words[i] = op(s.Words[i], o.Words[i]);
            }

            s.Trim();
            return Value.Null;
        }

        var F = r.Scope($"{M}.BitSet");
        F("BitSet/1", (c, a) => Init(a[0], new BitSetState(Int32(c, a[1].Bits, "Length", 0, 1L << 30))));
        F("get_Length/0", (c, a) => Value.FromInt(B(c, a[0]).Length));
        F("get_Count/0", (c, a) =>
        {
            var s = B(c, a[0]);
            c.Charge(s.Words.Length);
            return Value.FromInt(s.Words.Sum(w => (long)System.Numerics.BitOperations.PopCount(w)));
        });
        F("Get/1", (c, a) => Value.FromBool(B(c, a[0]).Get(Bit(c, B(c, a[0]), a[1].Bits))));
        F("Set/1", (c, a) => Mutate<BitSetState>(c, a[0], s => Void(() => s.Set(Bit(c, s, a[1].Bits), true))));
        F("Set/2", (c, a) => Mutate<BitSetState>(c, a[0], s => Void(() => s.Set(Bit(c, s, a[1].Bits), a[2].AsBool))));
        F("Clear/1", (c, a) => Mutate<BitSetState>(c, a[0], s => Void(() => s.Set(Bit(c, s, a[1].Bits), false))));
        F("Flip/1", (c, a) => Mutate<BitSetState>(c, a[0], s =>
        {
            var i = Bit(c, s, a[1].Bits);
            s.Set(i, !s.Get(i));
            return Value.Null;
        }));
        F("ClearAll/0", (c, a) => Mutate<BitSetState>(c, a[0], s => Void(() => System.Array.Clear(s.Words))));
        F("SetAll/0", (c, a) => Mutate<BitSetState>(c, a[0], s => Void(() =>
        {
            System.Array.Fill(s.Words, ulong.MaxValue);
            s.Trim();
        })));
        F("And/1", (c, a) => Combine(c, [a[0], a[1]], (x, y) => x & y));
        F("Or/1", (c, a) => Combine(c, [a[0], a[1]], (x, y) => x | y));
        F("Xor/1", (c, a) => Combine(c, [a[0], a[1]], (x, y) => x ^ y));
        F("Not/0", (c, a) => Combine(c, [a[0], a[0]], (x, _) => ~x));
        F("NextSet/1", (c, a) =>
        {
            c.Charge(B(c, a[0]).Words.Length / 8);
            return Value.FromInt(B(c, a[0]).NextSet((int)Math.Clamp(a[1].Bits, 0, int.MaxValue)));
        });
        F("Copy/0", (c, a) =>
        {
            var s = B(c, a[0]);
            var copy = new BitSetState(s.Length);
            System.Array.Copy(s.Words, copy.Words, s.Words.Length);
            return c.NewInstance(c.Class(M, "BitSet"), copy);
        });
        F("GetIterator/0", (c, a) => c.NewIterator(B(c, a[0]).Enumerate(c)));
        F("ToString/0", (c, a) =>
        {
            var s = B(c, a[0]);
            c.Charge(s.Length / 32);
            return Value.FromString(string.Concat(Enumerable.Range(0, s.Length).Select(i => s.Get(i) ? '1' : '0')));
        });
    }

    #endregion

    #region Grid2D, Matrix

    private static void RegisterGrids(NativeRegistry r)
    {
        static GridState G(NativeContext c, Value v) => c.State<GridState>(v);
        static int Cell(NativeContext c, GridState g, long x, long y) =>
            x >= 0 && x < g.Width && y >= 0 && y < g.Height ? (int)((y * g.Width) + x) : throw c.Throw("IndexError", $"({x}, {y}) is outside {g.Width}×{g.Height}.");
        static Value Pairs(NativeContext c, GridState g, long x, long y, (int, int)[] offsets) => c.NewArray([
            .. offsets.Select(o => (x + o.Item1, y + o.Item2))
                .Where(p => p.Item1 >= 0 && p.Item1 < g.Width && p.Item2 >= 0 && p.Item2 < g.Height)
                .Select(p => Tuple(Value.FromInt(p.Item1), Value.FromInt(p.Item2))),
        ]);
        static Value Create(NativeContext c, Value receiver, long w, long h, Value fill)
        {
            var width = Int32(c, w, "Width", 0, 1 << 24);
            var height = Int32(c, h, "Height", 0, 1 << 24);
            if ((long)width * height > 16_000_000)
            {
                throw c.Throw("ArgumentError", "Grid is too large.");
            }

            c.Charge(((long)width * height) / 8);
            return Init(receiver, new GridState(width, height, fill));
        }

        var F = r.Scope($"{M}.Grid2D");
        F("Grid2D/3", (c, a) => Create(c, a[0], a[1].Bits, a[2].Bits, a[3]));
        F("get_Width/0", (c, a) => Value.FromInt(G(c, a[0]).Width));
        F("get_Height/0", (c, a) => Value.FromInt(G(c, a[0]).Height));
        F("Get/2", (c, a) => G(c, a[0]).Cells[Cell(c, G(c, a[0]), a[1].Bits, a[2].Bits)]);
        F("Set/3", (c, a) =>
        {
            var g = G(c, a[0]);
            var i = Cell(c, g, a[1].Bits, a[2].Bits);
            c.Mutating(g);
            g.Cells[i] = a[3];
            return Value.Null;
        });
        F("InBounds/2", (c, a) => Value.FromBool(a[1].Bits >= 0 && a[1].Bits < G(c, a[0]).Width && a[2].Bits >= 0 && a[2].Bits < G(c, a[0]).Height));
        F("Fill/1", (c, a) => Mutate<GridState>(c, a[0], g =>
        {
            c.Charge(g.Cells.Length / 8);
            System.Array.Fill(g.Cells, a[1]);
            return Value.Null;
        }));
        F("Neighbors4/2", (c, a) => Pairs(c, G(c, a[0]), a[1].Bits, a[2].Bits, [(0, -1), (1, 0), (0, 1), (-1, 0)]));
        F("Neighbors8/2", (c, a) => Pairs(c, G(c, a[0]), a[1].Bits, a[2].Bits, [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)]));
        F("Row/1", (c, a) =>
        {
            var g = G(c, a[0]);
            var y = Index(c, a[1].Bits, g.Height);
            return c.NewArray(g.Cells.AsSpan(y * g.Width, g.Width).ToArray());
        });
        F("Column/1", (c, a) =>
        {
            var g = G(c, a[0]);
            var x = Index(c, a[1].Bits, g.Width);
            return c.NewArray([.. Enumerable.Range(0, g.Height).Select(y => g.Cells[(y * g.Width) + x])]);
        });
        F("Copy/0", (c, a) =>
        {
            var g = G(c, a[0]);
            var copy = new GridState(g.Width, g.Height, Value.Null);
            System.Array.Copy(g.Cells, copy.Cells, g.Cells.Length);
            c.Charge(g.Cells.Length / 8);
            return c.NewInstance(c.Class(M, "Grid2D"), copy);
        });
        F("GetIterator/0", (c, a) => c.NewIterator(G(c, a[0]).Enumerate(c)));
        F("ToString/0", (c, a) =>
        {
            // Character grids (maps, ASCII art) print compactly; other grids separate cells with spaces.
            var g = G(c, a[0]);
            var separator = g.Cells.All(v => v.Tag == ValueTag.Char) ? string.Empty : " ";
            c.Charge(g.Cells.Length / 8);
            var rows = Enumerable.Range(0, Math.Min(g.Height, 256))
                .Select(y => string.Join(separator, g.Cells.Skip(y * g.Width).Take(g.Width).Select(v => Display(c, v))));
            return Value.FromString(string.Join("\n", rows));
        });

        // Matrix<T> is a Grid2D addressed (row, column).
        var m = r.Scope($"{M}.Matrix");
        m("Matrix/3", (c, a) => Create(c, a[0], a[2].Bits, a[1].Bits, a[3]));
        m("get_Rows/0", (c, a) => Value.FromInt(G(c, a[0]).Height));
        m("get_Columns/0", (c, a) => Value.FromInt(G(c, a[0]).Width));
        m("Get/2", (c, a) => G(c, a[0]).Cells[Cell(c, G(c, a[0]), a[2].Bits, a[1].Bits)]);
        m("Set/3", (c, a) =>
        {
            var g = G(c, a[0]);
            var i = Cell(c, g, a[2].Bits, a[1].Bits);
            c.Mutating(g);
            g.Cells[i] = a[3];
            return Value.Null;
        });
        m("Row/1", (c, a) =>
        {
            var g = G(c, a[0]);
            var y = Index(c, a[1].Bits, g.Height);
            return c.NewArray(g.Cells.AsSpan(y * g.Width, g.Width).ToArray());
        });
        m("Column/1", (c, a) =>
        {
            var g = G(c, a[0]);
            var x = Index(c, a[1].Bits, g.Width);
            return c.NewArray([.. Enumerable.Range(0, g.Height).Select(y => g.Cells[(y * g.Width) + x])]);
        });
        m("Transpose/0", (c, a) =>
        {
            var g = G(c, a[0]);
            var t = new GridState(g.Height, g.Width, Value.Null);
            for (var y = 0; y < g.Height; y++)
            {
                for (var x = 0; x < g.Width; x++)
                {
                    t.Cells[(x * t.Width) + y] = g.Cells[(y * g.Width) + x];
                }
            }

            c.Charge(g.Cells.Length / 4);
            return c.NewInstance(c.Class(M, "Matrix"), t);
        });
        m("Map/1", (c, a) =>
        {
            var g = G(c, a[0]);
            var f = Fn(a[1]);
            var result = new GridState(g.Width, g.Height, Value.Null);
            for (var i = 0; i < g.Cells.Length; i++)
            {
                c.Charge(1);
                result.Cells[i] = c.Invoke(f, g.Cells[i]);
            }

            return c.NewInstance(c.Class(M, "Matrix"), result);
        });
        m("GetIterator/0", (c, a) => c.NewIterator(G(c, a[0]).Enumerate(c)));
        m("ToString/0", (c, a) =>
        {
            var g = G(c, a[0]);
            var rows = Enumerable.Range(0, Math.Min(g.Height, 64)).Select(y => Join(c, g.Cells.Skip(y * g.Width).Take(g.Width)));
            return Value.FromString(string.Join("\n", rows));
        });
    }

    #endregion
}
