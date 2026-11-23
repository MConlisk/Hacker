using Ghostrig.CScript.Syntax;

namespace Ghostrig.CScript.Semantics.Binding;

/// <summary>A resolved binary operator: the operator kind plus the operand and result types.</summary>
internal readonly record struct BinarySignature(BinaryOperatorKind Kind, TypeSymbol Left, TypeSymbol Right, TypeSymbol Result);

/// <summary>Operator resolution with numeric promotion (Doc 7 §6): byte/char promote to int; int with float promotes to float.</summary>
internal static class Operators
{
    public static BinarySignature? ResolveBinary(BinaryOperator op, TypeSymbol left, TypeSymbol right)
    {
        var l = left;
        var r = right;

        if (op is BinaryOperator.Equal or BinaryOperator.NotEqual)
        {
            return ResolveEquality(op, l, r);
        }

        // String concatenation accepts any non-void operand on either side.
        if (op == BinaryOperator.Add && (l == PrimitiveType.String || r == PrimitiveType.String) && l != PrimitiveType.Void && r != PrimitiveType.Void)
        {
            return new BinarySignature(BinaryOperatorKind.StringConcat, l, r, PrimitiveType.String);
        }

        if (op == BinaryOperator.Add && l == PrimitiveType.Bytes && r == PrimitiveType.Bytes)
        {
            return new BinarySignature(BinaryOperatorKind.BytesConcat, l, r, PrimitiveType.Bytes);
        }

        if (l == PrimitiveType.Bool && r == PrimitiveType.Bool)
        {
            return op switch
            {
                BinaryOperator.BitwiseAnd => new(BinaryOperatorKind.BoolAnd, l, r, l),
                BinaryOperator.BitwiseOr => new(BinaryOperatorKind.BoolOr, l, r, l),
                BinaryOperator.BitwiseXor => new(BinaryOperatorKind.BoolXor, l, r, l),
                _ => null,
            };
        }

        if (TimeOperator(op, l, r) is { } time)
        {
            return time;
        }

        if (l is NamedTypeSymbol { TypeKind: TypeKind.Enum } && l == r)
        {
            return op switch
            {
                BinaryOperator.BitwiseAnd => new(BinaryOperatorKind.IntAnd, l, r, l),
                BinaryOperator.BitwiseOr => new(BinaryOperatorKind.IntOr, l, r, l),
                BinaryOperator.BitwiseXor => new(BinaryOperatorKind.IntXor, l, r, l),
                BinaryOperator.Less => new(BinaryOperatorKind.IntLess, l, r, PrimitiveType.Bool),
                BinaryOperator.LessOrEqual => new(BinaryOperatorKind.IntLessOrEqual, l, r, PrimitiveType.Bool),
                BinaryOperator.Greater => new(BinaryOperatorKind.IntGreater, l, r, PrimitiveType.Bool),
                BinaryOperator.GreaterOrEqual => new(BinaryOperatorKind.IntGreaterOrEqual, l, r, PrimitiveType.Bool),
                _ => null,
            };
        }

        if (l is not PrimitiveType pl || r is not PrimitiveType pr || !pl.IsNumeric || !pr.IsNumeric)
        {
            return null;
        }

        var isFloat = pl.Primitive == PrimitiveKind.Float || pr.Primitive == PrimitiveKind.Float;
        var operand = isFloat ? PrimitiveType.Float : PrimitiveType.Int;
        var kind = (op, isFloat) switch
        {
            (BinaryOperator.Add, false) => BinaryOperatorKind.IntAdd,
            (BinaryOperator.Subtract, false) => BinaryOperatorKind.IntSubtract,
            (BinaryOperator.Multiply, false) => BinaryOperatorKind.IntMultiply,
            (BinaryOperator.Divide, false) => BinaryOperatorKind.IntDivide,
            (BinaryOperator.Modulo, false) => BinaryOperatorKind.IntModulo,
            (BinaryOperator.ShiftLeft, false) => BinaryOperatorKind.IntShiftLeft,
            (BinaryOperator.ShiftRight, false) => BinaryOperatorKind.IntShiftRight,
            (BinaryOperator.ShiftRightUnsigned, false) => BinaryOperatorKind.IntShiftRightUnsigned,
            (BinaryOperator.BitwiseAnd, false) => BinaryOperatorKind.IntAnd,
            (BinaryOperator.BitwiseOr, false) => BinaryOperatorKind.IntOr,
            (BinaryOperator.BitwiseXor, false) => BinaryOperatorKind.IntXor,
            (BinaryOperator.Less, false) => BinaryOperatorKind.IntLess,
            (BinaryOperator.LessOrEqual, false) => BinaryOperatorKind.IntLessOrEqual,
            (BinaryOperator.Greater, false) => BinaryOperatorKind.IntGreater,
            (BinaryOperator.GreaterOrEqual, false) => BinaryOperatorKind.IntGreaterOrEqual,
            (BinaryOperator.Add, true) => BinaryOperatorKind.FloatAdd,
            (BinaryOperator.Subtract, true) => BinaryOperatorKind.FloatSubtract,
            (BinaryOperator.Multiply, true) => BinaryOperatorKind.FloatMultiply,
            (BinaryOperator.Divide, true) => BinaryOperatorKind.FloatDivide,
            (BinaryOperator.Modulo, true) => BinaryOperatorKind.FloatModulo,
            (BinaryOperator.Less, true) => BinaryOperatorKind.FloatLess,
            (BinaryOperator.LessOrEqual, true) => BinaryOperatorKind.FloatLessOrEqual,
            (BinaryOperator.Greater, true) => BinaryOperatorKind.FloatGreater,
            (BinaryOperator.GreaterOrEqual, true) => BinaryOperatorKind.FloatGreaterOrEqual,
            _ => (BinaryOperatorKind?)null,
        };

        if (kind is null)
        {
            return null;
        }

        var result = kind is BinaryOperatorKind.IntLess or BinaryOperatorKind.IntLessOrEqual or BinaryOperatorKind.IntGreater
            or BinaryOperatorKind.IntGreaterOrEqual or BinaryOperatorKind.FloatLess or BinaryOperatorKind.FloatLessOrEqual
            or BinaryOperatorKind.FloatGreater or BinaryOperatorKind.FloatGreaterOrEqual
            ? PrimitiveType.Bool
            : operand;
        return new BinarySignature(kind.Value, operand, operand, result);
    }

    private static BinarySignature? TimeOperator(BinaryOperator op, TypeSymbol l, TypeSymbol r)
    {
        var duration = PrimitiveType.Duration;
        var instant = PrimitiveType.Instant;
        var integerOrFloat = r == PrimitiveType.Int || r == PrimitiveType.Float || r == PrimitiveType.Byte;
        return (op, l, r) switch
        {
            (BinaryOperator.Add, _, _) when l == duration && r == duration => new(BinaryOperatorKind.DurationAdd, l, r, duration),
            (BinaryOperator.Subtract, _, _) when l == duration && r == duration => new(BinaryOperatorKind.DurationSubtract, l, r, duration),
            (BinaryOperator.Multiply, _, _) when l == duration && integerOrFloat => new(BinaryOperatorKind.DurationMultiply, l, r == PrimitiveType.Byte ? PrimitiveType.Int : r, duration),
            (BinaryOperator.Divide, _, _) when l == duration && integerOrFloat => new(BinaryOperatorKind.DurationDivide, l, r == PrimitiveType.Byte ? PrimitiveType.Int : r, duration),
            (BinaryOperator.Add, _, _) when l == instant && r == duration => new(BinaryOperatorKind.InstantAddDuration, l, r, instant),
            (BinaryOperator.Subtract, _, _) when l == instant && r == duration => new(BinaryOperatorKind.InstantSubtractDuration, l, r, instant),
            (BinaryOperator.Subtract, _, _) when l == instant && r == instant => new(BinaryOperatorKind.InstantDifference, l, r, duration),
            _ when (l == duration && r == duration) || (l == instant && r == instant) =>
                CompareKind(op) is { } k ? new(k, l, r, PrimitiveType.Bool) : null,
            _ => null,
        };
    }

    private static BinaryOperatorKind? CompareKind(BinaryOperator op) => op switch
    {
        BinaryOperator.Less => BinaryOperatorKind.CompareLess,
        BinaryOperator.LessOrEqual => BinaryOperatorKind.CompareLessOrEqual,
        BinaryOperator.Greater => BinaryOperatorKind.CompareGreater,
        BinaryOperator.GreaterOrEqual => BinaryOperatorKind.CompareGreaterOrEqual,
        _ => null,
    };

    private static BinarySignature? ResolveEquality(BinaryOperator op, TypeSymbol l, TypeSymbol r)
    {
        var kind = op == BinaryOperator.Equal ? BinaryOperatorKind.Equal : BinaryOperatorKind.NotEqual;
        if (l is PrimitiveType pl && r is PrimitiveType pr && pl.IsNumeric && pr.IsNumeric && l != r)
        {
            var operand = pl.Primitive == PrimitiveKind.Float || pr.Primitive == PrimitiveKind.Float ? PrimitiveType.Float : PrimitiveType.Int;
            return new BinarySignature(kind, operand, operand, PrimitiveType.Bool);
        }

        if (l == r)
        {
            return new BinarySignature(kind, l, r, PrimitiveType.Bool);
        }

        if (l is NullTypeSymbol && (r.IsReferenceType || r.IsNullable))
        {
            return new BinarySignature(kind, r, r, PrimitiveType.Bool);
        }

        if (r is NullTypeSymbol && (l.IsReferenceType || l.IsNullable))
        {
            return new BinarySignature(kind, l, l, PrimitiveType.Bool);
        }

        if (Conversions.ClassifyImplicit(r, l).Exists)
        {
            return new BinarySignature(kind, l, l, PrimitiveType.Bool);
        }

        if (Conversions.ClassifyImplicit(l, r).Exists)
        {
            return new BinarySignature(kind, r, r, PrimitiveType.Bool);
        }

        return null;
    }

    public static (UnaryOperatorKind Kind, TypeSymbol Operand)? ResolveUnary(UnaryOperator op, TypeSymbol operand) => (op, operand) switch
    {
        (UnaryOperator.LogicalNot, _) when operand == PrimitiveType.Bool => (UnaryOperatorKind.BoolNot, PrimitiveType.Bool),
        (UnaryOperator.Negate, _) when operand == PrimitiveType.Float => (UnaryOperatorKind.FloatNegate, PrimitiveType.Float),
        (UnaryOperator.Negate, _) when operand == PrimitiveType.Duration => (UnaryOperatorKind.DurationNegate, PrimitiveType.Duration),
        (UnaryOperator.Negate, PrimitiveType { IsIntegral: true }) => (UnaryOperatorKind.IntNegate, PrimitiveType.Int),
        (UnaryOperator.BitwiseNot, PrimitiveType { IsIntegral: true }) => (UnaryOperatorKind.IntBitwiseNot, PrimitiveType.Int),
        _ => null,
    };

    public static string Symbol(BinaryOperator op) => op switch
    {
        BinaryOperator.Multiply => "*", BinaryOperator.Divide => "/", BinaryOperator.Modulo => "%",
        BinaryOperator.Add => "+", BinaryOperator.Subtract => "-",
        BinaryOperator.ShiftLeft => "<<", BinaryOperator.ShiftRight => ">>", BinaryOperator.ShiftRightUnsigned => ">>>",
        BinaryOperator.Less => "<", BinaryOperator.Greater => ">", BinaryOperator.LessOrEqual => "<=", BinaryOperator.GreaterOrEqual => ">=",
        BinaryOperator.Equal => "==", BinaryOperator.NotEqual => "!=",
        BinaryOperator.BitwiseAnd => "&", BinaryOperator.BitwiseXor => "^", BinaryOperator.BitwiseOr => "|",
        BinaryOperator.LogicalAnd => "&&", BinaryOperator.LogicalOr => "||", BinaryOperator.Coalesce => "??",
        _ => op.ToString(),
    };

    public static string Symbol(UnaryOperator op) => op switch
    {
        UnaryOperator.Plus => "+", UnaryOperator.Negate => "-", UnaryOperator.LogicalNot => "!", UnaryOperator.BitwiseNot => "~",
        UnaryOperator.PreIncrement or UnaryOperator.PostIncrement => "++",
        UnaryOperator.PreDecrement or UnaryOperator.PostDecrement => "--",
        _ => "!",
    };
}
