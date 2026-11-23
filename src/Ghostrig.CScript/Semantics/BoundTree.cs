using System.Collections.Immutable;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Semantics;

// The bound tree is the fully resolved, type-checked form of a function body. Every expression carries its type,
// every name is a symbol, every implicit conversion is explicit, and every operator is resolved to a typed kind.
// Code generation consumes it without consulting syntax.

public abstract record BoundNode(TextSpan Span);

#region Expressions

public abstract record BoundExpression(TextSpan Span, TypeSymbol Type) : BoundNode(Span)
{
    /// <summary>Compile-time value, when the expression is a constant.</summary>
    public virtual object? ConstantValue => null;
}

public sealed record BoundLiteral(TextSpan Span, TypeSymbol Type, object? Value) : BoundExpression(Span, Type)
{
    public override object? ConstantValue => Value;
}

public sealed record BoundLocal(TextSpan Span, LocalSymbol Local) : BoundExpression(Span, Local.Type)
{
    public override object? ConstantValue => Local.IsConst ? Local.ConstantValue : null;
}

public sealed record BoundParameter(TextSpan Span, ParameterSymbol Parameter) : BoundExpression(Span, Parameter.Type);

/// <summary>Instance field (with receiver) or static/global field (receiver null).</summary>
public sealed record BoundField(TextSpan Span, BoundExpression? Receiver, FieldSymbol Field) : BoundExpression(Span, Field.Type)
{
    public override object? ConstantValue => Field.IsConst ? Field.ConstantValue : null;
}

/// <summary>Property or indexer access; lowered to accessor calls by code generation.</summary>
public sealed record BoundProperty(TextSpan Span, BoundExpression? Receiver, PropertySymbol Property, ImmutableArray<BoundExpression> Arguments)
    : BoundExpression(Span, Property.Type);

public sealed record BoundArrayElement(TextSpan Span, BoundExpression Array, BoundExpression Index, TypeSymbol ElementType)
    : BoundExpression(Span, ElementType);

public sealed record BoundTupleElement(TextSpan Span, BoundExpression Tuple, int Index, TypeSymbol ElementType)
    : BoundExpression(Span, ElementType);

public sealed record BoundCall(TextSpan Span, BoundExpression? Receiver, MethodSymbol Method, ImmutableArray<BoundExpression> Arguments,
    bool IsBaseCall = false) : BoundExpression(Span, Method.ReturnType);

/// <summary>Invocation of a function-typed value (lambda, method reference, event handler list).</summary>
public sealed record BoundFunctionInvocation(TextSpan Span, BoundExpression Target, ImmutableArray<BoundExpression> Arguments, TypeSymbol ReturnType)
    : BoundExpression(Span, ReturnType);

/// <summary>A method used as a value: <c>fn(int) -> int f = Square;</c></summary>
public sealed record BoundMethodReference(TextSpan Span, BoundExpression? Receiver, MethodSymbol Method, FunctionTypeSymbol FunctionType)
    : BoundExpression(Span, FunctionType);

public sealed record BoundObjectCreation(TextSpan Span, NamedTypeSymbol CreatedType, MethodSymbol? Constructor,
    ImmutableArray<BoundExpression> Arguments, ImmutableArray<BoundExpression> Initializers) : BoundExpression(Span, CreatedType);

public sealed record BoundArrayCreation(TextSpan Span, ArrayTypeSymbol ArrayType, BoundExpression Length) : BoundExpression(Span, ArrayType);

public sealed record BoundArrayLiteral(TextSpan Span, ArrayTypeSymbol ArrayType, ImmutableArray<BoundExpression> Elements) : BoundExpression(Span, ArrayType);

public sealed record BoundTupleLiteral(TextSpan Span, TupleTypeSymbol TupleType, ImmutableArray<BoundExpression> Elements) : BoundExpression(Span, TupleType);

public enum UnaryOperatorKind
{
    IntNegate,
    FloatNegate,
    DurationNegate,
    IntBitwiseNot,
    BoolNot,
}

public sealed record BoundUnary(TextSpan Span, UnaryOperatorKind Operator, BoundExpression Operand, TypeSymbol ResultType)
    : BoundExpression(Span, ResultType)
{
    public override object? ConstantValue => (Operator, Operand.ConstantValue) switch
    {
        (UnaryOperatorKind.IntNegate, long l) => unchecked(-l),
        (UnaryOperatorKind.FloatNegate, double d) => -d,
        (UnaryOperatorKind.IntBitwiseNot, long l) => ~l,
        (UnaryOperatorKind.BoolNot, bool b) => !b,
        _ => null,
    };
}

public enum BinaryOperatorKind
{
    IntAdd, IntSubtract, IntMultiply, IntDivide, IntModulo,
    IntShiftLeft, IntShiftRight, IntShiftRightUnsigned, IntAnd, IntOr, IntXor,
    IntLess, IntLessOrEqual, IntGreater, IntGreaterOrEqual,
    FloatAdd, FloatSubtract, FloatMultiply, FloatDivide, FloatModulo,
    FloatLess, FloatLessOrEqual, FloatGreater, FloatGreaterOrEqual,
    BoolAnd, BoolOr, BoolXor,
    StringConcat,
    BytesConcat,
    DurationAdd, DurationSubtract, DurationMultiply, DurationDivide,
    InstantAddDuration, InstantSubtractDuration, InstantDifference,

    /// <summary>Structural for primitives, strings, bytes, structs, tuples; reference equality for classes.</summary>
    Equal,
    NotEqual,

    /// <summary>Ordering via <c>CompareTo</c> for Duration, Instant, char and enums.</summary>
    CompareLess, CompareLessOrEqual, CompareGreater, CompareGreaterOrEqual,
}

public sealed record BoundBinary(TextSpan Span, BinaryOperatorKind Operator, BoundExpression Left, BoundExpression Right, TypeSymbol ResultType)
    : BoundExpression(Span, ResultType)
{
    public override object? ConstantValue => ConstantEvaluation.Binary(Operator, Left.ConstantValue, Right.ConstantValue);
}

/// <summary>Short-circuit <c>&amp;&amp;</c> / <c>||</c>.</summary>
public sealed record BoundLogical(TextSpan Span, bool IsAnd, BoundExpression Left, BoundExpression Right) : BoundExpression(Span, PrimitiveType.Bool);

/// <summary><c>left ?? right</c>.</summary>
public sealed record BoundCoalesce(TextSpan Span, BoundExpression Left, BoundExpression Right, TypeSymbol ResultType) : BoundExpression(Span, ResultType);

/// <summary>Null-conditional member access <c>a?.b</c>; <see cref="Access"/> uses <see cref="Placeholder"/> for the receiver.</summary>
public sealed record BoundConditionalAccess(TextSpan Span, BoundExpression Receiver, BoundConditionalReceiver Placeholder, BoundExpression Access, TypeSymbol ResultType)
    : BoundExpression(Span, ResultType);

public sealed record BoundConditionalReceiver(TextSpan Span, TypeSymbol ReceiverType) : BoundExpression(Span, ReceiverType);

public sealed record BoundAssignment(TextSpan Span, BoundExpression Target, BoundExpression Value) : BoundExpression(Span, Target.Type);

/// <summary><c>target op= value</c>. For function types, <see cref="Operator"/> is null and the operation is combine/remove.</summary>
public sealed record BoundCompoundAssignment(TextSpan Span, BoundExpression Target, BinaryOperatorKind? Operator, bool IsFunctionRemove,
    BoundExpression Value, bool IsCoalesce = false) : BoundExpression(Span, Target.Type);

public sealed record BoundIncrement(TextSpan Span, BoundExpression Target, bool IsIncrement, bool IsPostfix) : BoundExpression(Span, Target.Type);

public sealed record BoundConversion(TextSpan Span, BoundExpression Operand, ConversionKind Kind, TypeSymbol TargetType) : BoundExpression(Span, TargetType)
{
    public override object? ConstantValue => ConstantEvaluation.Convert(Operand.ConstantValue, TargetType, Kind);
}

public sealed record BoundConditional(TextSpan Span, BoundExpression Condition, BoundExpression WhenTrue, BoundExpression WhenFalse, TypeSymbol ResultType)
    : BoundExpression(Span, ResultType);

public sealed record BoundIsPattern(TextSpan Span, BoundExpression Operand, BoundPattern Pattern) : BoundExpression(Span, PrimitiveType.Bool);

public sealed record BoundSwitchArm(TextSpan Span, BoundPattern Pattern, BoundExpression? Guard, BoundExpression Result, ImmutableArray<LocalSymbol> Locals)
    : BoundNode(Span);

public sealed record BoundSwitchExpression(TextSpan Span, BoundExpression Subject, ImmutableArray<BoundSwitchArm> Arms, TypeSymbol ResultType)
    : BoundExpression(Span, ResultType);

/// <summary>A lambda; <see cref="Function"/> is the synthesized method holding parameters and body.</summary>
public sealed record BoundLambda(TextSpan Span, MethodSymbol Function, BoundBlock Body, FunctionTypeSymbol FunctionType)
    : BoundExpression(Span, FunctionType);

public sealed record BoundAwait(TextSpan Span, BoundExpression Operand, TypeSymbol ResultType) : BoundExpression(Span, ResultType);

public sealed record BoundTypeOf(TextSpan Span, TypeSymbol Operand) : BoundExpression(Span, PrimitiveType.String);

public sealed record BoundThis(TextSpan Span, TypeSymbol ThisType) : BoundExpression(Span, ThisType);

public sealed record BoundInterpolatedString(TextSpan Span, ImmutableArray<BoundInterpolationPart> Parts) : BoundExpression(Span, PrimitiveType.String);

public sealed record BoundInterpolationPart(string? Text, BoundExpression? Value, string? Format);

public sealed record BoundThrowExpression(TextSpan Span, BoundExpression Error, TypeSymbol ContextType) : BoundExpression(Span, ContextType);

/// <summary>A type or module used as the receiver of a static member access; never evaluated.</summary>
public sealed record BoundTypeExpression(TextSpan Span, TypeSymbol ReferencedType) : BoundExpression(Span, ReferencedType);

public sealed record BoundModuleExpression(TextSpan Span, ModuleSymbol Module) : BoundExpression(Span, ErrorTypeSymbol.Instance);

/// <summary>A method group awaiting overload resolution or conversion to a function type.</summary>
public sealed record BoundMethodGroup(TextSpan Span, BoundExpression? Receiver, ImmutableArray<MethodSymbol> Methods,
    ImmutableArray<TypeSymbol> TypeArguments) : BoundExpression(Span, ErrorTypeSymbol.Instance);

public sealed record BoundErrorExpression(TextSpan Span) : BoundExpression(Span, ErrorTypeSymbol.Instance);

#endregion

#region Patterns

public abstract record BoundPattern(TextSpan Span) : BoundNode(Span);

public sealed record BoundDiscardPattern(TextSpan Span) : BoundPattern(Span);

public sealed record BoundConstantPattern(TextSpan Span, BoundExpression Value) : BoundPattern(Span);

/// <summary>Matches when the value is non-null and of <see cref="MatchedType"/>; optionally binds it.</summary>
public sealed record BoundTypePattern(TextSpan Span, TypeSymbol MatchedType, LocalSymbol? Designation) : BoundPattern(Span);

public sealed record BoundRelationalPattern(TextSpan Span, BinaryOperatorKind Operator, BoundExpression Value) : BoundPattern(Span);

#endregion

#region Statements

public abstract record BoundStatement(TextSpan Span) : BoundNode(Span);

public sealed record BoundBlock(TextSpan Span, ImmutableArray<BoundStatement> Statements) : BoundStatement(Span);

public sealed record BoundLocalDeclaration(TextSpan Span, LocalSymbol Local, BoundExpression? Initializer) : BoundStatement(Span);

public sealed record BoundDeconstruction(TextSpan Span, ImmutableArray<LocalSymbol> Locals, BoundExpression Value) : BoundStatement(Span);

public sealed record BoundExpressionStatement(TextSpan Span, BoundExpression Expression) : BoundStatement(Span);

public sealed record BoundIf(TextSpan Span, BoundExpression Condition, BoundStatement Then, BoundStatement? Else) : BoundStatement(Span);

public sealed record BoundWhile(TextSpan Span, BoundExpression Condition, BoundStatement Body, bool IsDoWhile) : BoundStatement(Span);

public sealed record BoundFor(TextSpan Span, BoundStatement? Initializer, BoundExpression? Condition, ImmutableArray<BoundExpression> Increments,
    BoundStatement Body) : BoundStatement(Span);

public enum ForeachKind
{
    Array,
    String,
    Bytes,
    Iterable,
    AsyncIterable,
}

public sealed record BoundForeach(TextSpan Span, ForeachKind Kind, LocalSymbol Variable, BoundExpression Collection, TypeSymbol ElementType,
    BoundStatement Body) : BoundStatement(Span);

public sealed record BoundSwitchSection(TextSpan Span, ImmutableArray<(BoundPattern? Pattern, BoundExpression? Guard)> Labels,
    ImmutableArray<BoundStatement> Statements) : BoundNode(Span);

public sealed record BoundSwitch(TextSpan Span, BoundExpression Subject, ImmutableArray<BoundSwitchSection> Sections) : BoundStatement(Span);

public sealed record BoundBreak(TextSpan Span) : BoundStatement(Span);

public sealed record BoundContinue(TextSpan Span) : BoundStatement(Span);

public sealed record BoundReturn(TextSpan Span, BoundExpression? Value) : BoundStatement(Span);

/// <summary><c>throw e;</c> or, with a null error inside a catch block, a rethrow.</summary>
public sealed record BoundThrow(TextSpan Span, BoundExpression? Error) : BoundStatement(Span);

public sealed record BoundCatch(TextSpan Span, NamedTypeSymbol ErrorType, LocalSymbol? Variable, BoundBlock Body) : BoundNode(Span);

public sealed record BoundTry(TextSpan Span, BoundBlock Body, ImmutableArray<BoundCatch> Catches, BoundBlock? Finally) : BoundStatement(Span);

public sealed record BoundYieldReturn(TextSpan Span, BoundExpression Value) : BoundStatement(Span);

public sealed record BoundYieldBreak(TextSpan Span) : BoundStatement(Span);

public sealed record BoundEmpty(TextSpan Span) : BoundStatement(Span);

#endregion

/// <summary>How a function body executes (Doc 7 §5.5–5.6).</summary>
public enum FunctionFlavor
{
    Normal,
    Async,
    Iterator,
    AsyncIterator,
}

/// <summary>The bound body of one method, constructor, accessor or lambda.</summary>
public sealed record BoundFunction(MethodSymbol Method, BoundBlock Body, FunctionFlavor Flavor, TypeSymbol? ElementType,
    ImmutableArray<LocalSymbol> CapturedLocals, ImmutableArray<ParameterSymbol> CapturedParameters, bool CapturesThis);

/// <summary>Everything code generation needs for one compilation.</summary>
public sealed record BoundProgram(
    ImmutableDictionary<MethodSymbol, BoundFunction> Functions,
    ImmutableDictionary<ModuleSymbol, ImmutableArray<(FieldSymbol Field, BoundExpression Value)>> GlobalInitializers,
    ImmutableDictionary<NamedTypeSymbol, ImmutableArray<(Symbol Member, BoundExpression Value)>> FieldInitializers,
    ImmutableArray<BoundFunction> Lambdas);

/// <summary>Constant folding over bound operators, shared with <see cref="ConstantFolder"/> semantics.</summary>
internal static class ConstantEvaluation
{
    public static object? Binary(BinaryOperatorKind op, object? left, object? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        return (op, left, right) switch
        {
            (BinaryOperatorKind.IntAdd, long a, long b) => unchecked(a + b),
            (BinaryOperatorKind.IntSubtract, long a, long b) => unchecked(a - b),
            (BinaryOperatorKind.IntMultiply, long a, long b) => unchecked(a * b),
            (BinaryOperatorKind.IntShiftLeft, long a, long b) => a << (int)(b & 63),
            (BinaryOperatorKind.IntShiftRight, long a, long b) => a >> (int)(b & 63),
            (BinaryOperatorKind.IntAnd, long a, long b) => a & b,
            (BinaryOperatorKind.IntOr, long a, long b) => a | b,
            (BinaryOperatorKind.IntXor, long a, long b) => a ^ b,
            (BinaryOperatorKind.FloatAdd, double a, double b) => a + b,
            (BinaryOperatorKind.FloatSubtract, double a, double b) => a - b,
            (BinaryOperatorKind.FloatMultiply, double a, double b) => a * b,
            (BinaryOperatorKind.StringConcat, string a, string b) => a + b,
            (BinaryOperatorKind.BoolAnd, bool a, bool b) => a & b,
            (BinaryOperatorKind.BoolOr, bool a, bool b) => a | b,
            _ => null,
        };
    }

    public static object? Convert(object? value, TypeSymbol target, ConversionKind kind) => (value, target, kind) switch
    {
        (long l, PrimitiveType { Primitive: PrimitiveKind.Float }, ConversionKind.ImplicitNumeric or ConversionKind.ExplicitNumeric) => (double)l,
        (long l, PrimitiveType { Primitive: PrimitiveKind.Byte }, _) => (long)(byte)l,
        (_, _, ConversionKind.Identity) => value,
        _ => null,
    };
}
