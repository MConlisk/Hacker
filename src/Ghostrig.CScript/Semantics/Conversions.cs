namespace Ghostrig.CScript.Semantics;

public enum ConversionKind
{
    None,
    Identity,

    /// <summary>Widening numeric: byte→int, char→int, byte/int→float.</summary>
    ImplicitNumeric,

    /// <summary><c>null</c> to any reference or nullable type.</summary>
    NullLiteral,

    /// <summary><c>T</c> → <c>T?</c> (and <c>U</c> → <c>T?</c> when <c>U</c> → <c>T</c>).</summary>
    ImplicitNullable,

    /// <summary>Derived → base class / implemented interface, any reference → <c>object</c>.</summary>
    ImplicitReference,

    /// <summary>Value type → <c>object</c> or an implemented interface.</summary>
    Boxing,

    /// <summary>Element-wise implicit tuple conversion.</summary>
    ImplicitTuple,

    /// <summary>Narrowing numeric: float→int (truncates), int→byte/char (wraps).</summary>
    ExplicitNumeric,

    /// <summary>Base → derived, interface → class, <c>object</c> → reference type (checked; <c>CastError</c>).</summary>
    ExplicitReference,

    /// <summary><c>object</c> / interface → value type (checked).</summary>
    Unboxing,

    /// <summary><c>T?</c> → <c>T</c> (checked; <c>NullError</c>).</summary>
    ExplicitNullable,

    /// <summary>enum ↔ int.</summary>
    ExplicitEnum,

    /// <summary>Involves an error type; always allowed to avoid cascades.</summary>
    Error,
}

public readonly record struct Conversion(ConversionKind Kind)
{
    public bool Exists => Kind != ConversionKind.None;

    public bool IsImplicit => Kind is ConversionKind.Identity or ConversionKind.ImplicitNumeric or ConversionKind.NullLiteral
        or ConversionKind.ImplicitNullable or ConversionKind.ImplicitReference or ConversionKind.Boxing
        or ConversionKind.ImplicitTuple or ConversionKind.Error;

    /// <summary>Conversions that can fail at runtime.</summary>
    public bool IsChecked => Kind is ConversionKind.ExplicitReference or ConversionKind.Unboxing or ConversionKind.ExplicitNullable;

    public static readonly Conversion None = new(ConversionKind.None);
    public static readonly Conversion Identity = new(ConversionKind.Identity);
}

/// <summary>Conversion classification rules (Doc 7 §4).</summary>
public static class Conversions
{
    public static Conversion Classify(TypeSymbol from, TypeSymbol to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.IsError || to.IsError)
        {
            return new(ConversionKind.Error);
        }

        if (from == to)
        {
            return Conversion.Identity;
        }

        var implicitConversion = ClassifyImplicit(from, to);
        return implicitConversion.Exists ? implicitConversion : ClassifyExplicit(from, to);
    }

    public static Conversion ClassifyImplicit(TypeSymbol from, TypeSymbol to)
    {
        if (from.IsError || to.IsError)
        {
            return new(ConversionKind.Error);
        }

        if (from == to)
        {
            return Conversion.Identity;
        }

        if (from is NullTypeSymbol)
        {
            return to.IsReferenceType || to is NullableTypeSymbol ? new(ConversionKind.NullLiteral) : Conversion.None;
        }

        if (to is NullableTypeSymbol nullableTarget)
        {
            if (from is NullableTypeSymbol nullableSource)
            {
                return ClassifyImplicit(nullableSource.Underlying, nullableTarget.Underlying).Exists
                    ? new(ConversionKind.ImplicitNullable)
                    : Conversion.None;
            }

            return ClassifyImplicit(from, nullableTarget.Underlying).Exists ? new(ConversionKind.ImplicitNullable) : Conversion.None;
        }

        if (from is PrimitiveType pf && to is PrimitiveType pt && IsImplicitNumeric(pf.Primitive, pt.Primitive))
        {
            return new(ConversionKind.ImplicitNumeric);
        }

        if (to == PrimitiveType.Object && from is not NullableTypeSymbol)
        {
            return from.IsReferenceType ? new(ConversionKind.ImplicitReference) : new(ConversionKind.Boxing);
        }

        if (from is NamedTypeSymbol fromNamed && to is NamedTypeSymbol toNamed && fromNamed.IsSubtypeOf(toNamed))
        {
            return fromNamed.IsReferenceType ? new(ConversionKind.ImplicitReference) : new(ConversionKind.Boxing);
        }

        if (from is TypeParameterSymbol tp && to is NamedTypeSymbol constraintTarget
            && tp.Constraints.OfType<NamedTypeSymbol>().Any(c => c.IsSubtypeOf(constraintTarget)))
        {
            return new(ConversionKind.Boxing);
        }

        if (from is PrimitiveType { MemberSource: { } source } && to is NamedTypeSymbol iface && source.IsSubtypeOf(iface))
        {
            return new(ConversionKind.Boxing);
        }

        if (from is TupleTypeSymbol ft && to is TupleTypeSymbol tt && ft.Elements.Length == tt.Elements.Length
            && ft.Elements.Zip(tt.Elements).All(p => ClassifyImplicit(p.First, p.Second).Exists))
        {
            return new(ConversionKind.ImplicitTuple);
        }

        return Conversion.None;
    }

    private static Conversion ClassifyExplicit(TypeSymbol from, TypeSymbol to)
    {
        if (from is NullableTypeSymbol n)
        {
            return n.Underlying == to || ClassifyImplicit(n.Underlying, to).Exists || ClassifyExplicit(n.Underlying, to).Exists
                ? new(ConversionKind.ExplicitNullable)
                : Conversion.None;
        }

        if (from is PrimitiveType pf && to is PrimitiveType pt && pf.IsNumeric && pt.IsNumeric)
        {
            return new(ConversionKind.ExplicitNumeric);
        }

        var fromEnum = from is NamedTypeSymbol { TypeKind: TypeKind.Enum };
        var toEnum = to is NamedTypeSymbol { TypeKind: TypeKind.Enum };
        if ((fromEnum && to == PrimitiveType.Int) || (toEnum && from == PrimitiveType.Int))
        {
            return new(ConversionKind.ExplicitEnum);
        }

        if (from == PrimitiveType.Object || from is NamedTypeSymbol { TypeKind: TypeKind.Interface } || from is TypeParameterSymbol)
        {
            return to.IsReferenceType ? new(ConversionKind.ExplicitReference) : new(ConversionKind.Unboxing);
        }

        if (from is NamedTypeSymbol baseType && to is NamedTypeSymbol derived && derived.IsSubtypeOf(baseType))
        {
            return new(ConversionKind.ExplicitReference);
        }

        if (to is TypeParameterSymbol)
        {
            return new(ConversionKind.Unboxing);
        }

        return Conversion.None;
    }

    private static bool IsImplicitNumeric(PrimitiveKind from, PrimitiveKind to) => (from, to) switch
    {
        (PrimitiveKind.Byte, PrimitiveKind.Int) => true,
        (PrimitiveKind.Byte, PrimitiveKind.Float) => true,
        (PrimitiveKind.Char, PrimitiveKind.Int) => true,
        (PrimitiveKind.Int, PrimitiveKind.Float) => true,
        _ => false,
    };
}
