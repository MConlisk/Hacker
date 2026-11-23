using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

/// <summary>A lazy sequence: a factory producing a fresh host enumeration every time it is iterated.</summary>
internal class SequenceState(Func<NativeContext, IEnumerable<Value>> source) : ImmutableNativeState, IValueSource
{
    public Func<NativeContext, IEnumerable<Value>> Source { get; } = source;

    public IEnumerable<Value> Enumerate(NativeContext context) => Source(context);
}

/// <summary>A sequence sorted by a chain of keys (OrderBy … ThenBy …), stable.</summary>
internal sealed class OrderedSequenceState : SequenceState
{
    public OrderedSequenceState(Func<NativeContext, IEnumerable<Value>> baseSource, IReadOnlyList<(FunctionObject Key, bool Descending)> keys)
        : base(c => Sort(c, baseSource, keys))
    {
        BaseSource = baseSource;
        Keys = keys;
    }

    public Func<NativeContext, IEnumerable<Value>> BaseSource { get; }

    public IReadOnlyList<(FunctionObject Key, bool Descending)> Keys { get; }

    private static IEnumerable<Value> Sort(NativeContext c, Func<NativeContext, IEnumerable<Value>> source, IReadOnlyList<(FunctionObject Key, bool Descending)> keys)
    {
        var items = source(c).ToArray();
        var computed = items.Select(item => keys.Select(k => c.Invoke(k.Key, item)).ToArray()).ToArray();
        c.Charge((long)(items.Length * (keys.Count + Math.Log2(Math.Max(2, items.Length)))));
        var order = Enumerable.Range(0, items.Length).ToArray();
        System.Array.Sort(order, (x, y) =>
        {
            for (var level = 0; level < keys.Count; level++)
            {
                var cmp = c.Comparer.Compare(computed[x][level], computed[y][level]);
                if (cmp != 0)
                {
                    return keys[level].Descending ? -cmp : cmp;
                }
            }

            return x.CompareTo(y);
        });
        return order.Select(i => items[i]);
    }
}

/// <summary>Host implementations of <c>Std.Seq</c>: every operator is lazy and re-iterable; terminal operations are metered.</summary>
internal static class SeqNatives
{
    private const string M = "Std.Seq";

    public static Value Create(NativeContext c, Func<NativeContext, IEnumerable<Value>> source) =>
        c.NewInstance(c.Class(M, "Sequence"), new SequenceState(source));

    /// <summary>The source of a Sequence receiver, or an enumeration of any other Iterable.</summary>
    private static Func<NativeContext, IEnumerable<Value>> Src(Value value) => value.Object switch
    {
        ObjectInstance { NativeState: SequenceState s } => s.Source,
        null => throw new ScriptErrorRequest("NullError", "Sequence source is null."),
        _ => ctx => Iterate(ctx, value),
    };

    /// <summary>Wraps a stage so every element it yields costs one step.</summary>
    private static Value Lazy(NativeContext c, Func<NativeContext, IEnumerable<Value>> stage) => Create(c, ctx => Charge(ctx, stage(ctx)));

    private static IEnumerable<Value> Charge(NativeContext c, IEnumerable<Value> items)
    {
        foreach (var item in items)
        {
            c.Charge(1);
            yield return item;
        }
    }

    private static IEnumerable<Value> Run(NativeContext c, Value receiver) => Charge(c, Src(receiver)(c));

    public static void Register(NativeRegistry r)
    {
        var F = r.Scope($"{M}.Sequence");
        var G = r.Scope(M);

        // Both the fluent method (receiver = a[0]) and the functional form (items = a[0]) share an implementation.
        void Both(string name, int arity, StdFunction f)
        {
            F($"{name}/{arity}", f);
            G($"{name}/{arity + 1}", f);
        }

        RegisterOperators(F, Both);
        RegisterTerminals(F, Both);
        RegisterSources(G);

        var o = r.Scope($"{M}.OrderedSequence");
        o("ThenBy/1", (c, a) => ThenBy(c, a, descending: false));
        o("ThenByDescending/1", (c, a) => ThenBy(c, a, descending: true));
    }

    private static Value ThenBy(NativeContext c, Value[] a, bool descending)
    {
        var state = c.State<OrderedSequenceState>(a[0]);
        return c.NewInstance(c.Class(M, "OrderedSequence"), new OrderedSequenceState(state.BaseSource, [.. state.Keys, (Fn(a[1]), descending)]));
    }

    private static void RegisterOperators(Action<string, StdFunction> F, Action<string, int, StdFunction> Both)
    {
        Both("Map", 1, (c, a) =>
        {
            var (src, f) = (Src(a[0]), Fn(a[1]));
            return Lazy(c, ctx => src(ctx).Select(x => ctx.Invoke(f, x)));
        });
        F("MapIndexed/1", (c, a) =>
        {
            var (src, f) = (Src(a[0]), Fn(a[1]));
            return Lazy(c, ctx => src(ctx).Select((x, i) => ctx.Invoke(f, x, Value.FromInt(i))));
        });
        Both("Filter", 1, (c, a) =>
        {
            var (src, f) = (Src(a[0]), Fn(a[1]));
            return Lazy(c, ctx => src(ctx).Where(x => ctx.Invoke(f, x).AsBool));
        });
        F("FlatMap/1", (c, a) =>
        {
            var (src, f) = (Src(a[0]), Fn(a[1]));
            return Lazy(c, ctx => src(ctx).SelectMany(x => Iterate(ctx, ctx.Invoke(f, x))));
        });
        Both("Take", 1, (c, a) =>
        {
            var (src, n) = (Src(a[0]), (int)Math.Clamp(a[1].Bits, 0, int.MaxValue));
            return Lazy(c, ctx => src(ctx).Take(n));
        });
        Both("Skip", 1, (c, a) =>
        {
            var (src, n) = (Src(a[0]), (int)Math.Clamp(a[1].Bits, 0, int.MaxValue));
            return Lazy(c, ctx => src(ctx).Skip(n));
        });
        F("TakeWhile/1", (c, a) =>
        {
            var (src, f) = (Src(a[0]), Fn(a[1]));
            return Lazy(c, ctx => src(ctx).TakeWhile(x => ctx.Invoke(f, x).AsBool));
        });
        F("SkipWhile/1", (c, a) =>
        {
            var (src, f) = (Src(a[0]), Fn(a[1]));
            return Lazy(c, ctx => src(ctx).SkipWhile(x => ctx.Invoke(f, x).AsBool));
        });
        F("TakeLast/1", (c, a) =>
        {
            var (src, n) = (Src(a[0]), (int)Math.Clamp(a[1].Bits, 0, int.MaxValue));
            return Lazy(c, ctx => src(ctx).TakeLast(n));
        });
        Both("Zip", 1, (c, a) =>
        {
            var (src, other) = (Src(a[0]), Src(a[1]));
            return Lazy(c, ctx => src(ctx).Zip(other(ctx), (x, y) => Tuple(x, y)));
        });
        Both("Concat", 1, (c, a) =>
        {
            var (src, other) = (Src(a[0]), Src(a[1]));
            return Lazy(c, ctx => src(ctx).Concat(other(ctx)));
        });
        F("Append/1", (c, a) =>
        {
            var (src, item) = (Src(a[0]), a[1]);
            return Lazy(c, ctx => src(ctx).Append(item));
        });
        F("Prepend/1", (c, a) =>
        {
            var (src, item) = (Src(a[0]), a[1]);
            return Lazy(c, ctx => src(ctx).Prepend(item));
        });
        Both("Distinct", 0, (c, a) =>
        {
            var src = Src(a[0]);
            return Lazy(c, ctx => src(ctx).Distinct(ctx.Comparer));
        });
        F("DistinctBy/1", (c, a) =>
        {
            var (src, f) = (Src(a[0]), Fn(a[1]));
            return Lazy(c, ctx => src(ctx).DistinctBy(x => ctx.Invoke(f, x), ctx.Comparer));
        });
        F("OrderBy/1", (c, a) => c.NewInstance(c.Class(M, "OrderedSequence"), new OrderedSequenceState(Src(a[0]), [(Fn(a[1]), false)])));
        F("OrderByDescending/1", (c, a) => c.NewInstance(c.Class(M, "OrderedSequence"), new OrderedSequenceState(Src(a[0]), [(Fn(a[1]), true)])));
        F("Sorted/0", (c, a) =>
        {
            var src = Src(a[0]);
            return Create(c, ctx => CollectionNatives.StableSort(ctx, src(ctx).ToArray(), ctx.Comparer));
        });
        F("Sorted/1", (c, a) =>
        {
            var (src, f) = (Src(a[0]), a[1]);
            return Create(c, ctx => CollectionNatives.StableSort(ctx, src(ctx).ToArray(), ctx.Comparer.From(f)));
        });
        F("Reverse/0", (c, a) =>
        {
            var src = Src(a[0]);
            return Lazy(c, ctx => src(ctx).Reverse());
        });
        F("Join/4", (c, a) =>
        {
            var (outer, inner, outerKey, innerKey, result) = (Src(a[0]), Src(a[1]), Fn(a[2]), Fn(a[3]), Fn(a[4]));
            return Lazy(c, ctx =>
            {
                var lookup = new OrderedTable(ctx.Comparer);
                foreach (var item in Charge(ctx, inner(ctx)))
                {
                    var key = ctx.Invoke(innerKey, item);
                    if (!lookup.TryGetValue(key, out var bucket))
                    {
                        bucket = Value.FromObject(new List<Value>());
                        lookup.Set(key, bucket);
                    }

                    ((List<Value>)bucket.Object!).Add(item);
                }

                return outer(ctx).SelectMany(o => lookup.TryGetValue(ctx.Invoke(outerKey, o), out var matches)
                    ? ((List<Value>)matches.Object!).Select(i => ctx.Invoke(result, o, i))
                    : []);
            });
        });
        F("Chunk/1", (c, a) =>
        {
            var (src, size) = (Src(a[0]), Int32(c, a[1].Bits, "Chunk size", 1));
            return Lazy(c, ctx => src(ctx).Chunk(size).Select(chunk => ctx.NewArray(chunk)));
        });
        F("Window/1", (c, a) =>
        {
            var (src, size) = (Src(a[0]), Int32(c, a[1].Bits, "Window size", 1));
            return Lazy(c, ctx => Windows(ctx, src(ctx), size));
        });
        F("Indexed/0", (c, a) =>
        {
            var src = Src(a[0]);
            return Lazy(c, ctx => src(ctx).Select((x, i) => Tuple(Value.FromInt(i), x)));
        });
        F("Scan/2", (c, a) =>
        {
            var (src, seed, f) = (Src(a[0]), a[1], Fn(a[2]));
            return Lazy(c, ctx => Scan(ctx, src(ctx), seed, f));
        });
        F("Union/1", (c, a) =>
        {
            var (src, other) = (Src(a[0]), Src(a[1]));
            return Lazy(c, ctx => src(ctx).Union(other(ctx), ctx.Comparer));
        });
        F("Intersect/1", (c, a) =>
        {
            var (src, other) = (Src(a[0]), Src(a[1]));
            return Lazy(c, ctx => src(ctx).Intersect(other(ctx), ctx.Comparer));
        });
        F("Except/1", (c, a) =>
        {
            var (src, other) = (Src(a[0]), Src(a[1]));
            return Lazy(c, ctx => src(ctx).Except(other(ctx), ctx.Comparer));
        });
    }

    private static IEnumerable<Value> Windows(NativeContext c, IEnumerable<Value> items, int size)
    {
        var window = new Queue<Value>(size);
        foreach (var item in items)
        {
            window.Enqueue(item);
            if (window.Count > size)
            {
                window.Dequeue();
            }

            if (window.Count == size)
            {
                yield return c.NewArray([.. window]);
            }
        }
    }

    private static IEnumerable<Value> Scan(NativeContext c, IEnumerable<Value> items, Value seed, FunctionObject f)
    {
        var acc = seed;
        foreach (var item in items)
        {
            acc = c.Invoke(f, acc, item);
            yield return acc;
        }
    }

    private static void RegisterTerminals(Action<string, StdFunction> F, Action<string, int, StdFunction> Both)
    {
        Both("Fold", 2, (c, a) =>
        {
            var (acc, f) = (a[1], Fn(a[2]));
            foreach (var item in Run(c, a[0]))
            {
                acc = c.Invoke(f, acc, item);
            }

            return acc;
        });
        Both("Reduce", 1, (c, a) =>
        {
            var f = Fn(a[1]);
            var any = false;
            var acc = Value.Null;
            foreach (var item in Run(c, a[0]))
            {
                acc = any ? c.Invoke(f, acc, item) : item;
                any = true;
            }

            return any ? acc : throw Empty(c, "sequence");
        });
        F("Any/0", (c, a) => Value.FromBool(Run(c, a[0]).Any()));
        Both("Any", 1, (c, a) =>
        {
            var f = Fn(a[1]);
            return Value.FromBool(Run(c, a[0]).Any(x => c.Invoke(f, x).AsBool));
        });
        Both("All", 1, (c, a) =>
        {
            var f = Fn(a[1]);
            return Value.FromBool(Run(c, a[0]).All(x => c.Invoke(f, x).AsBool));
        });
        Both("Count", 0, (c, a) => Value.FromInt(Run(c, a[0]).LongCount()));
        F("Count/1", (c, a) =>
        {
            var f = Fn(a[1]);
            return Value.FromInt(Run(c, a[0]).LongCount(x => c.Invoke(f, x).AsBool));
        });
        F("Contains/1", (c, a) => Value.FromBool(Run(c, a[0]).Any(x => c.Comparer.Equals(x, a[1]))));
        Both("First", 0, (c, a) =>
        {
            foreach (var item in Run(c, a[0]))
            {
                return item;
            }

            throw Empty(c, "sequence");
        });
        F("First/1", (c, a) =>
        {
            var f = Fn(a[1]);
            foreach (var item in Run(c, a[0]))
            {
                if (c.Invoke(f, item).AsBool)
                {
                    return item;
                }
            }

            throw c.Throw("Error", "No element matches the predicate.");
        });
        F("FirstOrNull/0", (c, a) => Run(c, a[0]).FirstOrDefault());
        F("FirstOrNull/1", (c, a) =>
        {
            var f = Fn(a[1]);
            return Run(c, a[0]).FirstOrDefault(x => c.Invoke(f, x).AsBool);
        });
        Both("Last", 0, (c, a) =>
        {
            var any = false;
            var last = Value.Null;
            foreach (var item in Run(c, a[0]))
            {
                (any, last) = (true, item);
            }

            return any ? last : throw Empty(c, "sequence");
        });
        F("LastOrNull/0", (c, a) => Run(c, a[0]).LastOrDefault());
        F("ElementAt/1", (c, a) =>
        {
            if (a[1].Bits >= 0)
            {
                var i = 0L;
                foreach (var item in Run(c, a[0]))
                {
                    if (i++ == a[1].Bits)
                    {
                        return item;
                    }
                }
            }

            throw c.Throw("IndexError", $"Index {a[1].Bits} is beyond the end of the sequence.");
        });
        F("IndexOf/1", (c, a) =>
        {
            var i = 0;
            foreach (var item in Run(c, a[0]))
            {
                if (c.Comparer.Equals(item, a[1]))
                {
                    return Value.FromInt(i);
                }

                i++;
            }

            return Value.FromInt(-1);
        });
        F("Min/0", (c, a) => Extreme(c, Run(c, a[0]), x => x, -1));
        F("Max/0", (c, a) => Extreme(c, Run(c, a[0]), x => x, 1));
        F("MinBy/1", (c, a) =>
        {
            var f = Fn(a[1]);
            return Extreme(c, Run(c, a[0]), x => c.Invoke(f, x), -1);
        });
        F("MaxBy/1", (c, a) =>
        {
            var f = Fn(a[1]);
            return Extreme(c, Run(c, a[0]), x => c.Invoke(f, x), 1);
        });
        F("Sum/1", (c, a) =>
        {
            var f = Fn(a[1]);
            return Value.FromInt(Run(c, a[0]).Aggregate(0L, (s, x) => unchecked(s + c.Invoke(f, x).AsInt)));
        });
        F("SumFloat/1", (c, a) =>
        {
            var f = Fn(a[1]);
            return Value.FromFloat(Run(c, a[0]).Aggregate(0.0, (s, x) => s + AsFloat(c.Invoke(f, x))));
        });
        F("Average/1", (c, a) =>
        {
            var f = Fn(a[1]);
            var (sum, n) = (0.0, 0L);
            foreach (var item in Run(c, a[0]))
            {
                sum += AsFloat(c.Invoke(f, item));
                n++;
            }

            return n > 0 ? Value.FromFloat(sum / n) : throw Empty(c, "sequence");
        });
        F("GroupBy/1", (c, a) =>
        {
            var f = Fn(a[1]);
            var map = new MapState(c.Comparer);
            foreach (var item in Run(c, a[0]))
            {
                var key = c.Invoke(f, item);
                if (!map.Table.TryGetValue(key, out var list))
                {
                    list = CollectionNatives.NewList(c, []);
                    map.Table.Set(key, list);
                }

                c.State<ListState>(list).Items.Add(item);
            }

            return CollectionNatives.NewMap(c, map);
        });
        F("CountBy/1", (c, a) =>
        {
            var f = Fn(a[1]);
            var map = new MapState(c.Comparer);
            foreach (var item in Run(c, a[0]))
            {
                var key = c.Invoke(f, item);
                map.Table.Set(key, Value.FromInt(map.Table.TryGetValue(key, out var n) ? n.Bits + 1 : 1));
            }

            return CollectionNatives.NewMap(c, map);
        });
        F("Partition/1", (c, a) =>
        {
            var f = Fn(a[1]);
            List<Value> yes = [], no = [];
            foreach (var item in Run(c, a[0]))
            {
                (c.Invoke(f, item).AsBool ? yes : no).Add(item);
            }

            return Tuple(CollectionNatives.NewList(c, yes), CollectionNatives.NewList(c, no));
        });
        Both("ToArray", 0, (c, a) => c.NewArray([.. Run(c, a[0])]));
        Both("ToList", 0, (c, a) => CollectionNatives.NewList(c, [.. Run(c, a[0])]));
        Both("ToSet", 0, (c, a) => CollectionNatives.NewSet(c, [.. Run(c, a[0])]));
        F("ToMap/2", (c, a) =>
        {
            var (key, value) = (Fn(a[1]), Fn(a[2]));
            var map = new MapState(c.Comparer);
            foreach (var item in Run(c, a[0]))
            {
                map.Table.Set(c.Invoke(key, item), c.Invoke(value, item));
            }

            return CollectionNatives.NewMap(c, map);
        });
        F("Join/1", (c, a) =>
        {
            var sb = new StringBuilder();
            var first = true;
            foreach (var item in Run(c, a[0]))
            {
                if (!first)
                {
                    sb.Append(a[1].AsString);
                }

                sb.Append(Display(c, item));
                first = false;
            }

            return Value.FromString(sb.ToString());
        });
        F("ForEach/1", (c, a) =>
        {
            var f = Fn(a[1]);
            foreach (var item in Run(c, a[0]))
            {
                c.Invoke(f, item);
            }

            return Value.Null;
        });
        F("SequenceEqual/1", (c, a) => Value.FromBool(Run(c, a[0]).SequenceEqual(Src(a[1])(c), c.Comparer)));
        F("GetIterator/0", (c, a) => c.NewIterator(Src(a[0])(c)));
        F("ToString/0", (c, a) => Value.FromString(Join(c, Src(a[0])(c), "Seq[", "]", 16)));
    }

    private static double AsFloat(Value v) => v.Tag == ValueTag.Float ? v.AsFloat : v.Bits;

    private static Value Extreme(NativeContext c, IEnumerable<Value> items, Func<Value, Value> key, int sign)
    {
        var any = false;
        Value best = Value.Null, bestKey = Value.Null;
        foreach (var item in items)
        {
            var k = key(item);
            if (!any || (c.Comparer.Compare(k, bestKey) * sign) > 0)
            {
                (best, bestKey, any) = (item, k, true);
            }
        }

        return any ? best : throw Empty(c, "sequence");
    }

    private static void RegisterSources(Action<string, StdFunction> G)
    {
        G("From/1", (c, a) => a[0].Object is ObjectInstance { NativeState: SequenceState } ? a[0] : Create(c, Src(a[0])));
        G("Range/2", (c, a) =>
        {
            var (start, count) = (a[0].Bits, Math.Max(0, a[1].Bits));
            return Lazy(c, _ => Range(start, count, 1));
        });
        G("Range/3", (c, a) =>
        {
            var (start, count, step) = (a[0].Bits, Math.Max(0, a[1].Bits), a[2].Bits);
            return Lazy(c, _ => Range(start, count, step));
        });
        G("Repeat/2", (c, a) =>
        {
            var (value, count) = (a[0], (int)Math.Clamp(a[1].Bits, 0, int.MaxValue));
            return Lazy(c, _ => Enumerable.Repeat(value, count));
        });
        G("Empty/0", (c, _) => Create(c, _ => []));
        G("Iterate/2", (c, a) =>
        {
            var (seed, f) = (a[0], Fn(a[1]));
            return Lazy(c, ctx => Unfold(ctx, seed, f));
        });
        G("Flatten/1", (c, a) =>
        {
            var src = Src(a[0]);
            return Lazy(c, ctx => src(ctx).SelectMany(x => Items(x)));
        });
        G("SumInt", (c, a) => Value.FromInt(Run(c, a[0]).Aggregate(0L, (s, x) => unchecked(s + x.Bits))));
        G("SumFloat", (c, a) => Value.FromFloat(Run(c, a[0]).Aggregate(0.0, (s, x) => s + AsFloat(x))));
        G("MinOf", (c, a) => Extreme(c, Run(c, a[0]), x => x, -1));
        G("MaxOf", (c, a) => Extreme(c, Run(c, a[0]), x => x, 1));
        StdFunction average = (c, a) =>
        {
            var (sum, n) = (0.0, 0L);
            foreach (var item in Run(c, a[0]))
            {
                sum += AsFloat(item);
                n++;
            }

            return n > 0 ? Value.FromFloat(sum / n) : throw Empty(c, "sequence");
        };
        G("AverageInt", average);
        G("AverageFloat", average);
    }

    private static IEnumerable<Value> Range(long start, long count, long step)
    {
        for (var i = 0L; i < count; i++)
        {
            yield return Value.FromInt(unchecked(start + (i * step)));
        }
    }

    private static IEnumerable<Value> Unfold(NativeContext c, Value seed, FunctionObject next)
    {
        var current = seed;
        while (true)
        {
            yield return current;
            current = c.Invoke(next, current);
        }
    }
}
