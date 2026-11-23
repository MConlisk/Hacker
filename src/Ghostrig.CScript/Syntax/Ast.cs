using System.Collections.Immutable;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Syntax;

/// <summary>Base of every syntax node. Nodes are immutable; <see cref="Span"/> covers the node's source text.</summary>
public abstract record Node(TextSpan Span);

#region Compilation unit and declarations

public sealed record CompilationUnit(
    TextSpan Span,
    ImmutableArray<AttributeSyntax> Attributes,
    QualifiedName ModuleName,
    ImmutableArray<ImportDecl> Imports,
    ImmutableArray<MemberDecl> Members,
    string? DocComment) : Node(Span);

public sealed record QualifiedName(TextSpan Span, ImmutableArray<string> Parts) : Node(Span)
{
    public override string ToString() => string.Join('.', Parts);
}

public sealed record AttributeSyntax(TextSpan Span, string Name, ImmutableArray<Expr> Arguments) : Node(Span);

public sealed record ImportDecl(TextSpan Span, QualifiedName Name, string? Alias) : Node(Span);

/// <summary>Declaration modifiers. Top-level and member declarations are public unless <see cref="IsPrivate"/> (Doc 7 D2).</summary>
public sealed record Modifiers(bool IsPrivate, bool IsAsync, bool IsConst)
{
    public static readonly Modifiers None = new(false, false, false);
}

public abstract record MemberDecl(TextSpan Span, ImmutableArray<AttributeSyntax> Attributes, Modifiers Modifiers, string Name, string? DocComment)
    : Node(Span);

public sealed record TypeParameter(TextSpan Span, string Name) : Node(Span);

public sealed record Constraint(TextSpan Span, string TypeParameter, ImmutableArray<TypeSyntax> Bounds) : Node(Span);

public sealed record Parameter(TextSpan Span, TypeSyntax? Type, string Name, Expr? Default, bool IsParams) : Node(Span);

public sealed record FunctionDecl(
    TextSpan Span, ImmutableArray<AttributeSyntax> Attributes, Modifiers Modifiers, string? DocComment,
    TypeSyntax ReturnType, string Name, ImmutableArray<TypeParameter> TypeParameters, ImmutableArray<Parameter> Parameters,
    ImmutableArray<Constraint> Constraints, BlockStmt? Body, Expr? ExpressionBody)
    : MemberDecl(Span, Attributes, Modifiers, Name, DocComment);

public sealed record ConstructorDecl(
    TextSpan Span, ImmutableArray<AttributeSyntax> Attributes, Modifiers Modifiers, string? DocComment,
    string Name, ImmutableArray<Parameter> Parameters, ConstructorInitializer? Initializer, BlockStmt? Body)
    : MemberDecl(Span, Attributes, Modifiers, Name, DocComment);

public sealed record ConstructorInitializer(TextSpan Span, bool IsBase, ImmutableArray<Argument> Arguments) : Node(Span);

/// <summary>A field (inside a type) or module global / constant (top level).</summary>
public sealed record FieldDecl(
    TextSpan Span, ImmutableArray<AttributeSyntax> Attributes, Modifiers Modifiers, string? DocComment,
    TypeSyntax? Type, string Name, Expr? Initializer)
    : MemberDecl(Span, Attributes, Modifiers, Name, DocComment);

public sealed record Accessor(TextSpan Span, bool IsGetter, BlockStmt? Body, Expr? ExpressionBody) : Node(Span);

public sealed record PropertyDecl(
    TextSpan Span, ImmutableArray<AttributeSyntax> Attributes, Modifiers Modifiers, string? DocComment,
    TypeSyntax Type, string Name, ImmutableArray<Accessor> Accessors, Expr? ExpressionBody)
    : MemberDecl(Span, Attributes, Modifiers, Name, DocComment);

public sealed record IndexerDecl(
    TextSpan Span, ImmutableArray<AttributeSyntax> Attributes, Modifiers Modifiers, string? DocComment,
    TypeSyntax Type, ImmutableArray<Parameter> Parameters, ImmutableArray<Accessor> Accessors)
    : MemberDecl(Span, Attributes, Modifiers, "this[]", DocComment);

public enum TypeDeclKind
{
    Class,
    Struct,
    Interface,
}

public sealed record TypeDecl(
    TextSpan Span, ImmutableArray<AttributeSyntax> Attributes, Modifiers Modifiers, string? DocComment,
    TypeDeclKind Kind, string Name, ImmutableArray<TypeParameter> TypeParameters, ImmutableArray<TypeSyntax> BaseTypes,
    ImmutableArray<Constraint> Constraints, ImmutableArray<MemberDecl> Members)
    : MemberDecl(Span, Attributes, Modifiers, Name, DocComment);

public sealed record EnumMember(TextSpan Span, string Name, Expr? Value) : Node(Span);

public sealed record EnumDecl(
    TextSpan Span, ImmutableArray<AttributeSyntax> Attributes, Modifiers Modifiers, string? DocComment,
    string Name, ImmutableArray<EnumMember> Members)
    : MemberDecl(Span, Attributes, Modifiers, Name, DocComment);

#endregion

#region Types

public abstract record TypeSyntax(TextSpan Span) : Node(Span);

/// <summary>A (possibly qualified, possibly generic) named type such as <c>int</c>, <c>Std.Net.IpAddress</c> or <c>Map&lt;K, V&gt;</c>.</summary>
public sealed record NamedType(TextSpan Span, QualifiedName Name, ImmutableArray<TypeSyntax> TypeArguments) : TypeSyntax(Span)
{
    public override string ToString() =>
        TypeArguments.IsEmpty ? Name.ToString() : $"{Name}<{string.Join(", ", TypeArguments)}>";
}

public sealed record ArrayType(TextSpan Span, TypeSyntax Element) : TypeSyntax(Span)
{
    public override string ToString() => $"{Element}[]";
}

public sealed record NullableType(TextSpan Span, TypeSyntax Underlying) : TypeSyntax(Span)
{
    public override string ToString() => $"{Underlying}?";
}

public sealed record TupleType(TextSpan Span, ImmutableArray<TypeSyntax> Elements) : TypeSyntax(Span)
{
    public override string ToString() => $"({string.Join(", ", Elements)})";
}

public sealed record FunctionType(TextSpan Span, ImmutableArray<TypeSyntax> Parameters, TypeSyntax Return) : TypeSyntax(Span)
{
    public override string ToString() => $"fn({string.Join(", ", Parameters)}) -> {Return}";
}

#endregion

#region Statements

public abstract record Stmt(TextSpan Span) : Node(Span);

public sealed record BlockStmt(TextSpan Span, ImmutableArray<Stmt> Statements) : Stmt(Span);

/// <summary>A local declaration. <see cref="Type"/> is null for <c>var</c>.</summary>
public sealed record LocalDeclStmt(TextSpan Span, bool IsConst, TypeSyntax? Type, ImmutableArray<VariableDeclarator> Declarators) : Stmt(Span);

public sealed record VariableDeclarator(TextSpan Span, string Name, Expr? Initializer) : Node(Span);

/// <summary><c>var (a, b) = expr;</c></summary>
public sealed record DeconstructionStmt(TextSpan Span, ImmutableArray<string> Names, Expr Value) : Stmt(Span);

public sealed record ExprStmt(TextSpan Span, Expr Expression) : Stmt(Span);

public sealed record IfStmt(TextSpan Span, Expr Condition, Stmt Then, Stmt? Else) : Stmt(Span);

public sealed record WhileStmt(TextSpan Span, Expr Condition, Stmt Body) : Stmt(Span);

public sealed record DoWhileStmt(TextSpan Span, Stmt Body, Expr Condition) : Stmt(Span);

public sealed record ForStmt(TextSpan Span, Stmt? Initializer, Expr? Condition, ImmutableArray<Expr> Iterators, Stmt Body) : Stmt(Span);

public sealed record ForeachStmt(TextSpan Span, bool IsAwait, TypeSyntax? Type, string Variable, Expr Collection, Stmt Body) : Stmt(Span);

public sealed record SwitchSection(TextSpan Span, ImmutableArray<SwitchLabel> Labels, ImmutableArray<Stmt> Statements) : Node(Span);

public abstract record SwitchLabel(TextSpan Span) : Node(Span);

public sealed record CaseLabel(TextSpan Span, Pattern Pattern, Expr? Guard) : SwitchLabel(Span);

public sealed record DefaultLabel(TextSpan Span) : SwitchLabel(Span);

public sealed record SwitchStmt(TextSpan Span, Expr Subject, ImmutableArray<SwitchSection> Sections) : Stmt(Span);

public sealed record BreakStmt(TextSpan Span) : Stmt(Span);

public sealed record ContinueStmt(TextSpan Span) : Stmt(Span);

public sealed record ReturnStmt(TextSpan Span, Expr? Value) : Stmt(Span);

public sealed record ThrowStmt(TextSpan Span, Expr? Value) : Stmt(Span);

public sealed record CatchClause(TextSpan Span, TypeSyntax? Type, string? Variable, BlockStmt Body) : Node(Span);

public sealed record TryStmt(TextSpan Span, BlockStmt Body, ImmutableArray<CatchClause> Catches, BlockStmt? Finally) : Stmt(Span);

public sealed record YieldReturnStmt(TextSpan Span, Expr Value) : Stmt(Span);

public sealed record YieldBreakStmt(TextSpan Span) : Stmt(Span);

public sealed record EmptyStmt(TextSpan Span) : Stmt(Span);

#endregion

#region Patterns

public abstract record Pattern(TextSpan Span) : Node(Span);

/// <summary><c>_</c></summary>
public sealed record DiscardPattern(TextSpan Span) : Pattern(Span);

/// <summary>A constant: literal, enum member or <c>null</c>.</summary>
public sealed record ConstantPattern(TextSpan Span, Expr Value) : Pattern(Span);

/// <summary><c>Packet p</c> or <c>Packet</c>; <see cref="Designation"/> may be null.</summary>
public sealed record TypePattern(TextSpan Span, TypeSyntax Type, string? Designation) : Pattern(Span);

/// <summary><c>var x</c></summary>
public sealed record VarPattern(TextSpan Span, string Designation) : Pattern(Span);

/// <summary><c>&lt; 5</c>, <c>&gt;= 0x80</c></summary>
public sealed record RelationalPattern(TextSpan Span, BinaryOperator Operator, Expr Value) : Pattern(Span);

#endregion

#region Expressions

public abstract record Expr(TextSpan Span) : Node(Span);

public enum LiteralKind
{
    Int,
    Float,
    Char,
    String,
    Bytes,
    Bool,
    Null,
}

public sealed record LiteralExpr(TextSpan Span, LiteralKind Kind, object? Value) : Expr(Span);

public sealed record InterpolatedStringExpr(TextSpan Span, ImmutableArray<InterpolationSegment> Segments) : Expr(Span);

public abstract record InterpolationSegment(TextSpan Span) : Node(Span);

public sealed record TextSegment(TextSpan Span, string Text) : InterpolationSegment(Span);

public sealed record HoleSegment(TextSpan Span, Expr Expression, string? Format) : InterpolationSegment(Span);

public sealed record NameExpr(TextSpan Span, string Name, ImmutableArray<TypeSyntax> TypeArguments) : Expr(Span);

public sealed record MemberAccessExpr(TextSpan Span, Expr Target, string Member, ImmutableArray<TypeSyntax> TypeArguments, bool IsConditional)
    : Expr(Span);

public sealed record Argument(TextSpan Span, string? Name, Expr Value) : Node(Span);

public sealed record InvocationExpr(TextSpan Span, Expr Target, ImmutableArray<Argument> Arguments) : Expr(Span);

public sealed record ElementAccessExpr(TextSpan Span, Expr Target, ImmutableArray<Expr> Indices, bool IsConditional) : Expr(Span);

public enum UnaryOperator
{
    Plus,
    Negate,
    LogicalNot,
    BitwiseNot,
    PreIncrement,
    PreDecrement,
    PostIncrement,
    PostDecrement,
    NullForgiving,
}

public sealed record UnaryExpr(TextSpan Span, UnaryOperator Operator, Expr Operand) : Expr(Span);

public enum BinaryOperator
{
    Multiply, Divide, Modulo,
    Add, Subtract,
    ShiftLeft, ShiftRight, ShiftRightUnsigned,
    Less, Greater, LessOrEqual, GreaterOrEqual,
    Equal, NotEqual,
    BitwiseAnd, BitwiseXor, BitwiseOr,
    LogicalAnd, LogicalOr,
    Coalesce,
}

public sealed record BinaryExpr(TextSpan Span, BinaryOperator Operator, Expr Left, Expr Right) : Expr(Span);

/// <summary>Assignment; <see cref="Operator"/> is null for plain <c>=</c>, otherwise the compound operator.</summary>
public sealed record AssignmentExpr(TextSpan Span, BinaryOperator? Operator, Expr Target, Expr Value) : Expr(Span);

public sealed record ConditionalExpr(TextSpan Span, Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr(Span);

public sealed record IsPatternExpr(TextSpan Span, Expr Operand, Pattern Pattern) : Expr(Span);

public sealed record CastExpr(TextSpan Span, TypeSyntax Type, Expr Operand) : Expr(Span);

/// <summary><c>new T(args)</c>, optionally with an object initializer <c>{ A = 1 }</c>.</summary>
public sealed record NewExpr(TextSpan Span, TypeSyntax Type, ImmutableArray<Argument> Arguments, ImmutableArray<AssignmentExpr> Initializers) : Expr(Span);

/// <summary><c>new T[n]</c></summary>
public sealed record NewArrayExpr(TextSpan Span, TypeSyntax ElementType, Expr Length) : Expr(Span);

/// <summary><c>[a, b, c]</c></summary>
public sealed record ArrayLiteralExpr(TextSpan Span, ImmutableArray<Expr> Elements) : Expr(Span);

public sealed record TupleExpr(TextSpan Span, ImmutableArray<Expr> Elements) : Expr(Span);

public sealed record LambdaParameter(TextSpan Span, TypeSyntax? Type, string Name) : Node(Span);

public sealed record LambdaExpr(TextSpan Span, bool IsAsync, ImmutableArray<LambdaParameter> Parameters, Expr? ExpressionBody, BlockStmt? BlockBody)
    : Expr(Span);

public sealed record AwaitExpr(TextSpan Span, Expr Operand) : Expr(Span);

public sealed record TypeofExpr(TextSpan Span, TypeSyntax Type) : Expr(Span);

public sealed record ThisExpr(TextSpan Span) : Expr(Span);

public sealed record BaseExpr(TextSpan Span) : Expr(Span);

public sealed record SwitchArm(TextSpan Span, Pattern Pattern, Expr? Guard, Expr Result) : Node(Span);

public sealed record SwitchExpr(TextSpan Span, Expr Subject, ImmutableArray<SwitchArm> Arms) : Expr(Span);

/// <summary><c>throw e</c> used as an expression (e.g. in <c>??</c> or a switch arm).</summary>
public sealed record ThrowExpr(TextSpan Span, Expr Value) : Expr(Span);

/// <summary>Placeholder produced by error recovery; never reaches code generation.</summary>
public sealed record ErrorExpr(TextSpan Span) : Expr(Span);

#endregion
