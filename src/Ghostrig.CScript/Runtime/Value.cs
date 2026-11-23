using System.Globalization;
using System.Runtime.CompilerServices;

namespace Ghostrig.CScript.Runtime;

/// <summary>Runtime tag of a <see cref="Value"/>. Generic code is type-erased, so values describe themselves.</summary>
public enum ValueTag : byte
{
    Null,
    Bool,
    Int,
    Float,
    Char,
    Byte,
    Duration,
    Instant,

    /// <summary>An enum member: <see cref="Value.Bits"/> holds the integer, <see cref="Value.Object"/> the enum's <see cref="RuntimeClass"/>.</summary>
    Enum,

    /// <summary>Any heap value: string, bytes, array, object, struct, tuple, function, task…</summary>
    Ref,
}

/// <summary>
/// A CScript value: 64 bits of scalar payload plus an optional heap reference, tagged. Scalars never allocate, so
/// integer and float arithmetic is cheap for the host while staying bit-exact (Doc 7 D6–D7).
/// </summary>
public readonly struct Value : IEquatable<Value>
{
    private Value(ValueTag tag, long bits, object? obj)
    {
        Tag = tag;
        Bits = bits;
        Object = obj;
    }

    public ValueTag Tag { get; }
    public long Bits { get; }
    public object? Object { get; }

    public static readonly Value Null = default;
    public static readonly Value True = new(ValueTag.Bool, 1, null);
    public static readonly Value False = new(ValueTag.Bool, 0, null);

    public static Value FromBool(bool b) => b ? True : False;
    public static Value FromInt(long i) => new(ValueTag.Int, i, null);
    public static Value FromFloat(double d) => new(ValueTag.Float, BitConverter.DoubleToInt64Bits(d), null);
    public static Value FromChar(long c) => new(ValueTag.Char, c, null);
    public static Value FromByte(long b) => new(ValueTag.Byte, b & 0xFF, null);
    public static Value FromDuration(long nanoseconds) => new(ValueTag.Duration, nanoseconds, null);
    public static Value FromInstant(long nanoseconds) => new(ValueTag.Instant, nanoseconds, null);
    public static Value FromEnum(long value, RuntimeClass enumClass) => new(ValueTag.Enum, value, enumClass);
    public static Value FromString(string s) => new(ValueTag.Ref, 0, s ?? throw new ArgumentNullException(nameof(s)));

    public static Value FromObject(object? o) => o switch
    {
        null => Null,
        _ => new Value(ValueTag.Ref, 0, o),
    };

    public bool IsNull => Tag == ValueTag.Null;
    public bool AsBool => Bits != 0;
    public long AsInt => Bits;
    public double AsFloat => BitConverter.Int64BitsToDouble(Bits);
    public string AsString => (string)Object!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T As<T>() where T : class => (T)Object!;

    /// <summary>Re-tags the scalar payload (used by conversions such as byte → int).</summary>
    public Value WithTag(ValueTag tag) => new(tag, Bits, tag == ValueTag.Enum ? Object : null);

    public bool Equals(Value other) => Tag == other.Tag && Bits == other.Bits && ReferenceEquals(Object, other.Object);
    public override bool Equals(object? obj) => obj is Value v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Tag, Bits, Object is null ? 0 : RuntimeHelpers.GetHashCode(Object));

    public override string ToString() => Tag switch
    {
        ValueTag.Null => "null",
        ValueTag.Bool => AsBool ? "true" : "false",
        ValueTag.Int => Bits.ToString(CultureInfo.InvariantCulture),
        ValueTag.Float => ValueFormatting.FormatFloat(AsFloat),
        ValueTag.Char => char.ConvertFromUtf32((int)Bits),
        ValueTag.Byte => Bits.ToString(CultureInfo.InvariantCulture),
        ValueTag.Duration => ValueFormatting.FormatDuration(Bits),
        ValueTag.Instant => $"T+{Bits}ns",
        ValueTag.Enum => ((RuntimeClass)Object!).EnumName(Bits),
        _ => Object switch
        {
            string s => s,
            _ => Object!.ToString() ?? "?",
        },
    };
}

/// <summary>Invariant, platform-independent formatting rules shared by the VM and natives.</summary>
public static class ValueFormatting
{
    public static string FormatFloat(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "Infinity" : "-Infinity";
        }

        var text = d.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    public static string FormatDuration(long nanoseconds)
    {
        var abs = Math.Abs((decimal)nanoseconds);
        var sign = nanoseconds < 0 ? "-" : string.Empty;
        return abs switch
        {
            >= 1_000_000_000 => $"{sign}{(abs / 1_000_000_000m).ToString("0.###", CultureInfo.InvariantCulture)}s",
            >= 1_000_000 => $"{sign}{(abs / 1_000_000m).ToString("0.###", CultureInfo.InvariantCulture)}ms",
            >= 1_000 => $"{sign}{(abs / 1_000m).ToString("0.###", CultureInfo.InvariantCulture)}µs",
            _ => $"{sign}{abs}ns",
        };
    }
}
