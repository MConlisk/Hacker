using System.Text;
using Ghostrig.CScript.Syntax;

namespace Ghostrig.CScript.Semantics;

/// <summary>
/// Evaluates compile-time constant expressions (const fields, enum values, default parameters) with the same
/// wrapping 64-bit integer semantics as the VM (Doc 7 D6).
/// </summary>
internal static class ConstantFolder
{
    public static bool TryEvaluate(Expr expr, out object? value, IReadOnlyDictionary<string, long>? names = null)
    {
        try
        {
            value = Evaluate(expr, names);
            return value is not Unknown;
        }
        catch (DivideByZeroException)
        {
            value = null;
            return false;
        }
    }

    private sealed class Unknown
    {
        public static readonly Unknown Instance = new();
    }

    private static object? Evaluate(Expr expr, IReadOnlyDictionary<string, long>? names) => expr switch
    {
        LiteralExpr { Kind: LiteralKind.Char, Value: Rune r } => r,
        LiteralExpr literal => literal.Value,
        NameExpr name when names is not null && names.TryGetValue(name.Name, out var v) => v,
        UnaryExpr unary => EvaluateUnary(unary.Operator, Evaluate(unary.Operand, names)),
        BinaryExpr binary => EvaluateBinary(binary.Operator, Evaluate(binary.Left, names), Evaluate(binary.Right, names)),
        ConditionalExpr c => Evaluate(c.Condition, names) is bool b ? Evaluate(b ? c.WhenTrue : c.WhenFalse, names) : Unknown.Instance,
        _ => Unknown.Instance,
    };

    private static object EvaluateUnary(UnaryOperator op, object? operand) => (op, operand) switch
    {
        (UnaryOperator.Plus, long l) => l,
        (UnaryOperator.Plus, double d) => d,
        (UnaryOperator.Negate, long l) => unchecked(-l),
        (UnaryOperator.Negate, double d) => -d,
        (UnaryOperator.BitwiseNot, long l) => ~l,
        (UnaryOperator.LogicalNot, bool b) => !b,
        _ => Unknown.Instance,
    };

    private static object EvaluateBinary(BinaryOperator op, object? left, object? right)
    {
        if (left is Unknown || right is Unknown)
        {
            return Unknown.Instance;
        }

        if (left is long a && right is long b)
        {
            return op switch
            {
                BinaryOperator.Add => unchecked(a + b),
                BinaryOperator.Subtract => unchecked(a - b),
                BinaryOperator.Multiply => unchecked(a * b),
                BinaryOperator.Divide => b == 0 ? throw new DivideByZeroException() : a == long.MinValue && b == -1 ? a : a / b,
                BinaryOperator.Modulo => b == 0 ? throw new DivideByZeroException() : b == -1 ? 0L : a % b,
                BinaryOperator.ShiftLeft => a << (int)(b & 63),
                BinaryOperator.ShiftRight => a >> (int)(b & 63),
                BinaryOperator.ShiftRightUnsigned => (long)((ulong)a >> (int)(b & 63)),
                BinaryOperator.BitwiseAnd => a & b,
                BinaryOperator.BitwiseOr => a | b,
                BinaryOperator.BitwiseXor => a ^ b,
                BinaryOperator.Less => a < b,
                BinaryOperator.LessOrEqual => a <= b,
                BinaryOperator.Greater => a > b,
                BinaryOperator.GreaterOrEqual => a >= b,
                BinaryOperator.Equal => a == b,
                BinaryOperator.NotEqual => a != b,
                _ => Unknown.Instance,
            };
        }

        if (left is double or long && right is double or long && (left is double || right is double))
        {
            var x = Convert.ToDouble(left, System.Globalization.CultureInfo.InvariantCulture);
            var y = Convert.ToDouble(right, System.Globalization.CultureInfo.InvariantCulture);
            return op switch
            {
                BinaryOperator.Add => x + y,
                BinaryOperator.Subtract => x - y,
                BinaryOperator.Multiply => x * y,
                BinaryOperator.Divide => x / y,
                BinaryOperator.Less => x < y,
                BinaryOperator.Greater => x > y,
                _ => Unknown.Instance,
            };
        }

        if (left is bool p && right is bool q)
        {
            return op switch
            {
                BinaryOperator.LogicalAnd => p && q,
                BinaryOperator.LogicalOr => p || q,
                BinaryOperator.Equal => p == q,
                BinaryOperator.NotEqual => p != q,
                _ => Unknown.Instance,
            };
        }

        if (left is string s && right is string t && op == BinaryOperator.Add)
        {
            return s + t;
        }

        return Unknown.Instance;
    }
}
