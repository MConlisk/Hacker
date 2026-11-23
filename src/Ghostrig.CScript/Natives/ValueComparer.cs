using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Natives;

/// <summary>
/// The equality, hashing and ordering used by every standard-library collection (Doc 7 §9: deterministic). Scalars, strings,
/// bytes, tuples and structs compare structurally; class instances use their <c>HashCode</c>, <c>Equatable&lt;T&gt;.Equals</c>
/// and <c>Comparable&lt;T&gt;.CompareTo</c> implementations when they declare them, otherwise identity (hashes are
/// allocation-order ids, never host addresses).
/// </summary>
public sealed class ValueComparer : IEqualityComparer<Value>, IComparer<Value>
{
    private readonly NativeContext _context;
    private ProgramImage? _image;
    private int _hashSlot;
    private int _equalsSlot;
    private int _compareSlot;
    private NamedTypeSymbol? _equatable;
    private NamedTypeSymbol? _comparable;

    internal ValueComparer(NativeContext context) => _context = context;

    private void EnsureSlots()
    {
        var image = _context.Image;
        if (ReferenceEquals(image, _image))
        {
            return;
        }

        var linker = image.Linker;
        var core = linker.Compilation.GetModule("Core")!;
        NamedTypeSymbol Type(string name) => core.Lookup(name).OfType<NamedTypeSymbol>().First();
        _equatable = Type("Equatable");
        _comparable = Type("Comparable");
        _hashSlot = linker.SlotOf(PrimitiveType.Object.MemberSource!.GetDeclaredMembers("HashCode").OfType<MethodSymbol>().Single());
        _equalsSlot = linker.SlotOf(_equatable.GetDeclaredMembers("Equals").OfType<MethodSymbol>().Single());
        _compareSlot = linker.SlotOf(_comparable.GetDeclaredMembers("CompareTo").OfType<MethodSymbol>().Single());
        _image = image;
    }

    private static bool IsUserObject(Value v, out ObjectInstance instance)
    {
        if (v.Object is ObjectInstance { Class.IsStruct: false, NativeState: null } o)
        {
            instance = o;
            return true;
        }

        instance = null!;
        return false;
    }

    public bool Equals(Value x, Value y)
    {
        if (IsUserObject(x, out var o) && !ReferenceEquals(x.Object, y.Object) && !y.IsNull)
        {
            EnsureSlots();
            if (o.Class.Implements(_equatable!))
            {
                return _context.InvokeVirtual(_equalsSlot, x, y).AsBool;
            }
        }

        return VirtualMachine.ValuesEqual(x, y);
    }

    public int GetHashCode(Value value) => (int)Hash(value) ^ (int)(Hash(value) >> 32);

    /// <summary>A 64-bit deterministic hash (also exposed to scripts through <c>HashCode()</c>).</summary>
    public long Hash(Value value)
    {
        if (IsUserObject(value, out _))
        {
            EnsureSlots();
            if (_context.ResolveVirtual(_hashSlot, value) is CodeUnit)
            {
                return _context.InvokeVirtual(_hashSlot, value).AsInt;
            }
        }

        // Floats that equal integers must hash alike because ValuesEqual(1, 1.0) is true.
        if (value.Tag == ValueTag.Float && value.AsFloat is var d && d == Math.Floor(d) && Math.Abs(d) < 9.2e18)
        {
            return _context.Hash(Value.FromInt((long)d));
        }

        return value.Tag == ValueTag.Float && value.AsFloat == 0 ? _context.Hash(Value.FromInt(0)) : _context.Hash(value);
    }

    public int Compare(Value x, Value y)
    {
        if (x.IsNull || y.IsNull)
        {
            return x.IsNull ? (y.IsNull ? 0 : -1) : 1;
        }

        if (x.Tag == ValueTag.Float || y.Tag == ValueTag.Float)
        {
            var dx = x.Tag == ValueTag.Float ? x.AsFloat : x.Bits;
            var dy = y.Tag == ValueTag.Float ? y.AsFloat : y.Bits;
            return dx.CompareTo(dy);
        }

        if (x.Tag != ValueTag.Ref)
        {
            return x.Bits.CompareTo(y.Bits);
        }

        switch (x.Object, y.Object)
        {
            case (string a, string b):
                return Math.Sign(string.CompareOrdinal(a, b));
            case (BytesObject a, BytesObject b):
                return Math.Sign(a.Data.AsSpan().SequenceCompareTo(b.Data));
            case (TupleObject a, TupleObject b):
                for (var i = 0; i < Math.Min(a.Items.Length, b.Items.Length); i++)
                {
                    var c = Compare(a.Items[i], b.Items[i]);
                    if (c != 0)
                    {
                        return c;
                    }
                }

                return a.Items.Length.CompareTo(b.Items.Length);
            case (ObjectInstance { NativeState: IStructuralState a }, ObjectInstance { NativeState: IStructuralState b })
                when a.StructuralCompare(b) is { } order:
                return Math.Sign(order);
            case (ObjectInstance a, _):
                EnsureSlots();
                if (a.Class.Implements(_comparable!))
                {
                    return Math.Sign(_context.InvokeVirtual(_compareSlot, x, y).AsInt);
                }

                throw _context.Throw("ArgumentError", $"{a.Class.Name} does not implement Comparable; pass a comparer.");
            default:
                throw _context.Throw("ArgumentError", $"Values of type {_context.Image.ClassOf(x)?.Name ?? "?"} cannot be ordered; pass a comparer.");
        }
    }

    /// <summary>An ordering from a CScript <c>fn(T, T) -&gt; int</c>, or the natural order when <paramref name="function"/> is null.</summary>
    public IComparer<Value> From(Value function) => function.Object is FunctionObject f
        ? Comparer<Value>.Create((a, b) => Math.Sign(_context.Invoke(f, a, b).AsInt))
        : this;

    /// <summary>An ordering by a key selector <c>fn(T) -&gt; K</c>.</summary>
    public IComparer<Value> ByKey(FunctionObject selector, bool descending = false) =>
        Comparer<Value>.Create((a, b) =>
        {
            var c = Compare(_context.Invoke(selector, a), _context.Invoke(selector, b));
            return descending ? -c : c;
        });
}
