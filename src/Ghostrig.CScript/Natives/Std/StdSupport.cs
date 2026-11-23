using System.Runtime.CompilerServices;
using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Natives.Std;

/// <summary>Host state that can enumerate its elements directly (fast path for <see cref="StdSupport.Iterate"/>).</summary>
public interface IValueSource
{
    IEnumerable<Value> Enumerate(NativeContext context);
}

/// <summary>A standard-library native: arguments arrive as an array so implementations may capture them in lazy closures.</summary>
public delegate Value StdFunction(NativeContext context, Value[] args);

/// <summary>Registration and conversion helpers shared by the <c>Std.*</c> natives.</summary>
internal static class StdSupport
{
    /// <summary>Returns a registrar that prefixes keys with <paramref name="owner"/> (e.g. <c>Std.Collections.List</c>).</summary>
    public static Action<string, StdFunction> Scope(this NativeRegistry registry, string owner) =>
        (member, function) => registry.Add($"{owner}.{member}", (c, a) => function(c, a.ToArray()));

    /// <summary>Enumerates any Iterable, short-circuiting host collections and sequences.</summary>
    public static IEnumerable<Value> Iterate(NativeContext c, Value iterable) =>
        iterable.Object is ObjectInstance { NativeState: IValueSource source } ? source.Enumerate(c) : CoreNatives.Iterate(c, iterable);

    /// <summary>Iterates and charges one step per element.</summary>
    public static IEnumerable<Value> Metered(NativeContext c, Value iterable)
    {
        foreach (var item in Iterate(c, iterable))
        {
            c.Charge(1);
            yield return item;
        }
    }

    public static List<Value> Materialize(NativeContext c, Value iterable)
    {
        var list = new List<Value>();
        foreach (var item in Iterate(c, iterable))
        {
            list.Add(item);
        }

        c.Charge(list.Count);
        return list;
    }

    public static Value[] Items(Value array) => array.Object is ArrayObject a ? a.Items : throw new ScriptErrorRequest("NullError", "Array is null.");

    public static FunctionObject Fn(Value function) =>
        function.Object as FunctionObject ?? throw new ScriptErrorRequest("NullError", "Function value is null.");

    public static Value Tuple(params Value[] items) => Value.FromObject(new TupleObject(items));

    public static Value Array(NativeContext c, IEnumerable<Value> items) => c.NewArray(items as Value[] ?? [.. items]);

    public static int Int32(NativeContext c, long value, string what, long min = 0, long max = int.MaxValue) =>
        value >= min && value <= max ? (int)value : throw c.Throw("ArgumentError", $"{what} {value} is out of range.");

    public static int Index(NativeContext c, long index, int count) =>
        index >= 0 && index < count ? (int)index : throw c.Throw("IndexError", $"Index {index} is out of range for length {count}.");

    public static Exception Empty(NativeContext c, string what) => c.Throw("Error", $"The {what} is empty.");

    /// <summary>Script-visible text of a value: user ToString overrides are honoured; strings are not quoted.</summary>
    public static string Display(NativeContext c, Value value) => value.Object switch
    {
        string s => s,
        ObjectInstance or NativeObject => c.InvokeVirtual(c.Image.ToStringSlot, value).Object as string ?? string.Empty,
        TupleObject t => "(" + string.Join(", ", t.Items.Select(v => Display(c, v))) + ")",
        _ => value.IsNull ? "null" : value.ToString(),
    };

    public static string Join(NativeContext c, IEnumerable<Value> items, string open = "[", string close = "]", int limit = 64)
    {
        var sb = new StringBuilder(open);
        var n = 0;
        foreach (var item in items)
        {
            if (n > 0)
            {
                sb.Append(", ");
            }

            if (n++ == limit)
            {
                sb.Append('…');
                break;
            }

            sb.Append(Display(c, item));
        }

        c.Charge(n);
        return sb.Append(close).ToString();
    }

    /// <summary>SplitMix64 finaliser: a fast, well-distributed 64-bit mix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Applies an operation to a collection under the journal and iterator-version rules.</summary>
    public static Value Mutate<TState>(NativeContext c, Value receiver, Func<TState, Value> action) where TState : NativeState
    {
        var state = c.State<TState>(receiver);
        c.Mutating(state);
        return action(state);
    }

    public static Value Void(Action action)
    {
        action();
        return Value.Null;
    }
}

/// <summary>
/// An insertion-ordered hash table (the engine of Map, Set, LruCache and TtlCache). Removal leaves a tombstone that is
/// compacted when tombstones outnumber live entries, so ordered iteration, append and move-to-end are amortised O(1).
/// Snapshots copy the index without re-hashing, so rollback never calls back into script code.
/// </summary>
internal sealed class OrderedTable
{
    private readonly IEqualityComparer<Value> _comparer;
    private Dictionary<Value, int> _index;
    private Value[] _keys = new Value[4];
    private Value[] _values = new Value[4];
    private bool[] _alive = new bool[4];
    private int _used;
    private int _head;

    public OrderedTable(IEqualityComparer<Value> comparer)
    {
        _comparer = comparer;
        _index = new Dictionary<Value, int>(comparer);
    }

    public int Count { get; private set; }

    public bool ContainsKey(Value key) => _index.ContainsKey(key);

    public bool TryGetSlot(Value key, out int slot) => _index.TryGetValue(key, out slot);

    public bool TryGetValue(Value key, out Value value)
    {
        if (_index.TryGetValue(key, out var slot))
        {
            value = _values[slot];
            return true;
        }

        value = Value.Null;
        return false;
    }

    public Value KeyAt(int slot) => _keys[slot];

    public Value ValueAt(int slot) => _values[slot];

    public void SetValueAt(int slot, Value value) => _values[slot] = value;

    /// <summary>Inserts at the end or replaces in place. Returns true when the key was new.</summary>
    public bool Set(Value key, Value value)
    {
        if (_index.TryGetValue(key, out var slot))
        {
            _values[slot] = value;
            return false;
        }

        Append(key, value);
        return true;
    }

    public bool TryAdd(Value key, Value value)
    {
        if (_index.ContainsKey(key))
        {
            return false;
        }

        Append(key, value);
        return true;
    }

    public bool Remove(Value key, out Value value)
    {
        if (!_index.Remove(key, out var slot))
        {
            value = Value.Null;
            return false;
        }

        value = _values[slot];
        _alive[slot] = false;
        _keys[slot] = Value.Null;
        _values[slot] = Value.Null;
        Count--;
        return true;
    }

    /// <summary>Moves an existing key to the end (most recent) keeping its value.</summary>
    public void MoveToEnd(Value key)
    {
        if (Remove(key, out var value))
        {
            Append(key, value);
        }
    }

    /// <summary>The first live slot, or -1.</summary>
    public int FirstSlot()
    {
        while (_head < _used && !_alive[_head])
        {
            _head++;
        }

        return _head < _used ? _head : -1;
    }

    public void Clear()
    {
        _index.Clear();
        System.Array.Clear(_keys, 0, _used);
        System.Array.Clear(_values, 0, _used);
        System.Array.Clear(_alive, 0, _used);
        _used = 0;
        _head = 0;
        Count = 0;
    }

    public IEnumerable<int> Slots()
    {
        for (var i = FirstSlot(); i >= 0 && i < _used; i++)
        {
            if (_alive[i])
            {
                yield return i;
            }
        }
    }

    public IEnumerable<Value> Keys() => Slots().Select(s => _keys[s]);

    public IEnumerable<Value> Values() => Slots().Select(s => _values[s]);

    private void Append(Value key, Value value)
    {
        if (_used == _keys.Length)
        {
            if (_used - Count > Count)
            {
                Compact();
            }

            if (_used == _keys.Length)
            {
                var size = _keys.Length * 2;
                System.Array.Resize(ref _keys, size);
                System.Array.Resize(ref _values, size);
                System.Array.Resize(ref _alive, size);
            }
        }

        _keys[_used] = key;
        _values[_used] = value;
        _alive[_used] = true;
        _index[key] = _used;
        _used++;
        Count++;
    }

    private void Compact()
    {
        var write = 0;
        for (var read = 0; read < _used; read++)
        {
            if (!_alive[read])
            {
                continue;
            }

            if (write != read)
            {
                _keys[write] = _keys[read];
                _values[write] = _values[read];
                _alive[write] = true;
                _index[_keys[write]] = write;
            }

            write++;
        }

        System.Array.Clear(_keys, write, _used - write);
        System.Array.Clear(_values, write, _used - write);
        System.Array.Clear(_alive, write, _used - write);
        _used = write;
        _head = 0;
    }

    private sealed record Snapshot(Dictionary<Value, int> Index, Value[] Keys, Value[] Values, bool[] Alive, int Used, int Head, int Count);

    public object Capture() => new Snapshot(new Dictionary<Value, int>(_index, _comparer), (Value[])_keys.Clone(), (Value[])_values.Clone(),
        (bool[])_alive.Clone(), _used, _head, Count);

    public void Restore(object snapshot)
    {
        var s = (Snapshot)snapshot;
        _index = new Dictionary<Value, int>(s.Index, _comparer);
        _keys = (Value[])s.Keys.Clone();
        _values = (Value[])s.Values.Clone();
        _alive = (bool[])s.Alive.Clone();
        _used = s.Used;
        _head = s.Head;
        Count = s.Count;
    }
}

/// <summary>A sorted array with binary search (SortedMap, SortedSet). Snapshots are plain array copies.</summary>
internal sealed class SortedArray(IComparer<Value> comparer)
{
    private List<Value> _keys = [];
    private List<Value> _values = [];

    public IComparer<Value> Comparer { get; } = comparer;

    public int Count => _keys.Count;

    public List<Value> KeyList => _keys;

    public List<Value> ValueList => _values;

    public int Find(Value key)
    {
        int lo = 0, hi = _keys.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var c = Comparer.Compare(_keys[mid], key);
            if (c == 0)
            {
                return mid;
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }

    public bool Set(Value key, Value value)
    {
        var i = Find(key);
        if (i >= 0)
        {
            _values[i] = value;
            return false;
        }

        _keys.Insert(~i, key);
        _values.Insert(~i, value);
        return true;
    }

    public bool Remove(Value key)
    {
        var i = Find(key);
        if (i < 0)
        {
            return false;
        }

        _keys.RemoveAt(i);
        _values.RemoveAt(i);
        return true;
    }

    public void Clear()
    {
        _keys.Clear();
        _values.Clear();
    }

    /// <summary>Index of the first key &gt;= key.</summary>
    public int LowerBound(Value key)
    {
        var i = Find(key);
        return i >= 0 ? i : ~i;
    }

    public object Capture() => (_keys.ToArray(), _values.ToArray());

    public void Restore(object snapshot)
    {
        var (keys, values) = ((Value[], Value[]))snapshot;
        _keys = [.. keys];
        _values = [.. values];
    }
}
