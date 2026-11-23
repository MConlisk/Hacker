using System.Collections;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Diagnostics;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Location of a diagnostic in a source file.</summary>
public sealed record SourceLocation(string Path, TextSpan Span, LinePosition Position)
{
    public static SourceLocation From(SourceText source, TextSpan span) => new(source.Path, span, source.GetLinePosition(span.Start));

    public override string ToString() => $"{Path}:{Position.Line}:{Position.Column}";
}

/// <summary>
/// A compiler message in the stable Doc 7 §14.1 format: <c>CSC1234 error path:line:col message</c>.
/// </summary>
public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, SourceLocation Location, string Message)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString() => $"{Code} {Severity.ToString().ToLowerInvariant()} {Location} {Message}";
}

/// <summary>Accumulates diagnostics in report order.</summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _items = [];

    public int Count => _items.Count;

    public bool HasErrors => _items.Exists(d => d.IsError);

    public void Report(DiagnosticDescriptor descriptor, SourceText source, TextSpan span, params object[] args) =>
        _items.Add(new Diagnostic(descriptor.Code, descriptor.Severity, SourceLocation.From(source, span),
            string.Format(System.Globalization.CultureInfo.InvariantCulture, descriptor.Format, args)));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A diagnostic kind with a stable code and message template.</summary>
public sealed record DiagnosticDescriptor(string Code, DiagnosticSeverity Severity, string Format);

/// <summary>Catalog of compiler diagnostics. Codes are stable once published (Doc 7 §14.1).</summary>
public static class DiagnosticCodes
{
    // 1xxx — lexical
    public static readonly DiagnosticDescriptor BadCharacter = new("CSC1001", DiagnosticSeverity.Error, "Unexpected character '{0}'.");
    public static readonly DiagnosticDescriptor UnterminatedString = new("CSC1002", DiagnosticSeverity.Error, "Unterminated string literal.");
    public static readonly DiagnosticDescriptor UnterminatedChar = new("CSC1003", DiagnosticSeverity.Error, "Unterminated or empty character literal.");
    public static readonly DiagnosticDescriptor UnterminatedComment = new("CSC1004", DiagnosticSeverity.Error, "Unterminated block comment.");
    public static readonly DiagnosticDescriptor InvalidNumber = new("CSC1005", DiagnosticSeverity.Error, "Invalid numeric literal '{0}'.");
    public static readonly DiagnosticDescriptor InvalidEscape = new("CSC1006", DiagnosticSeverity.Error, "Invalid escape sequence '\\{0}'.");
    public static readonly DiagnosticDescriptor UnbalancedInterpolation = new("CSC1007", DiagnosticSeverity.Error, "Unbalanced braces in interpolated string.");

    // 2xxx — syntax
    public static readonly DiagnosticDescriptor Expected = new("CSC2001", DiagnosticSeverity.Error, "Expected {0} but found '{1}'.");
    public static readonly DiagnosticDescriptor ExpectedExpression = new("CSC2002", DiagnosticSeverity.Error, "Expected an expression but found '{0}'.");
    public static readonly DiagnosticDescriptor ExpectedType = new("CSC2003", DiagnosticSeverity.Error, "Expected a type but found '{0}'.");
    public static readonly DiagnosticDescriptor ExpectedMember = new("CSC2004", DiagnosticSeverity.Error, "Expected a declaration but found '{0}'.");
    public static readonly DiagnosticDescriptor ModuleNameMismatch = new("CSC2005", DiagnosticSeverity.Error, "Module '{0}' must be declared in a file named '{0}' (found '{1}').");
    public static readonly DiagnosticDescriptor InvalidAssignmentTarget = new("CSC2006", DiagnosticSeverity.Error, "The left-hand side of an assignment must be a variable, field, property or indexer.");
    public static readonly DiagnosticDescriptor InvalidStatement = new("CSC2007", DiagnosticSeverity.Error, "Only assignment, call, increment, decrement, await and new expressions can be used as statements.");
    public static readonly DiagnosticDescriptor DuplicateModifier = new("CSC2008", DiagnosticSeverity.Error, "Duplicate modifier '{0}'.");

    // 3xxx — declarations, names and types
    public static readonly DiagnosticDescriptor DuplicateModule = new("CSC3001", DiagnosticSeverity.Error, "Module '{0}' is declared more than once.");
    public static readonly DiagnosticDescriptor ModuleNotFound = new("CSC3002", DiagnosticSeverity.Error, "Module '{0}' could not be found.");
    public static readonly DiagnosticDescriptor TypeNotFound = new("CSC3003", DiagnosticSeverity.Error, "The type '{0}' could not be found.");
    public static readonly DiagnosticDescriptor AmbiguousName = new("CSC3004", DiagnosticSeverity.Error, "'{0}' is ambiguous between {1}.");
    public static readonly DiagnosticDescriptor WrongTypeArgumentCount = new("CSC3005", DiagnosticSeverity.Error, "'{0}' requires {1} type argument(s).");
    public static readonly DiagnosticDescriptor ConstraintViolation = new("CSC3006", DiagnosticSeverity.Error, "The type '{0}' does not satisfy the constraint '{1}' of '{2}'.");
    public static readonly DiagnosticDescriptor VoidNotAllowed = new("CSC3007", DiagnosticSeverity.Error, "'void' can only be used as a return type.");
    public static readonly DiagnosticDescriptor DuplicateMember = new("CSC3008", DiagnosticSeverity.Error, "'{0}' is already declared in {1}.");
    public static readonly DiagnosticDescriptor MissingBody = new("CSC3009", DiagnosticSeverity.Error, "'{0}' must declare a body.");
    public static readonly DiagnosticDescriptor CapabilityDenied = new("CSC3010", DiagnosticSeverity.Error, "A {0} module may not use '{1}' (imported via {2}).");
    public static readonly DiagnosticDescriptor InvalidBaseType = new("CSC3011", DiagnosticSeverity.Error, "'{0}' cannot be used as a base type of {1}: {2}.");
    public static readonly DiagnosticDescriptor CyclicInheritance = new("CSC3012", DiagnosticSeverity.Error, "Circular base type dependency involving '{0}'.");
    public static readonly DiagnosticDescriptor InvalidAsyncReturn = new("CSC3013", DiagnosticSeverity.Error, "An async function must return Task, Task<T> or void.");
    public static readonly DiagnosticDescriptor BodyNotAllowed = new("CSC3014", DiagnosticSeverity.Error, "Interface member '{0}' cannot have a body.");
    public static readonly DiagnosticDescriptor NotConstant = new("CSC3015", DiagnosticSeverity.Error, "The value of '{0}' must be a compile-time constant.");
    public static readonly DiagnosticDescriptor VarNotAllowedHere = new("CSC3016", DiagnosticSeverity.Error, "'var' cannot be used for fields; declare an explicit type.");
    public static readonly DiagnosticDescriptor NestedTypeNotSupported = new("CSC3017", DiagnosticSeverity.Error, "Nested type declarations are not supported; declare '{0}' at module level.");
    public static readonly DiagnosticDescriptor MissingInterfaceMember = new("CSC3018", DiagnosticSeverity.Error, "'{0}' does not implement interface member '{1}'.");
    public static readonly DiagnosticDescriptor InvalidMemberInInterface = new("CSC3019", DiagnosticSeverity.Error, "Interfaces cannot declare {0}.");
    public static readonly DiagnosticDescriptor InvalidModifier = new("CSC3020", DiagnosticSeverity.Error, "The modifier '{0}' is not valid here.");

    // 4xxx — bodies: names, types, statements
    public static readonly DiagnosticDescriptor NameNotFound = new("CSC4001", DiagnosticSeverity.Error, "The name '{0}' does not exist in the current context.");
    public static readonly DiagnosticDescriptor MemberNotFound = new("CSC4002", DiagnosticSeverity.Error, "'{0}' does not contain a member named '{1}'.");
    public static readonly DiagnosticDescriptor CannotConvert = new("CSC4003", DiagnosticSeverity.Error, "Cannot convert '{0}' to '{1}'.");
    public static readonly DiagnosticDescriptor ExplicitCastRequired = new("CSC4004", DiagnosticSeverity.Error, "Cannot implicitly convert '{0}' to '{1}'; an explicit cast is required.");
    public static readonly DiagnosticDescriptor NoOverload = new("CSC4005", DiagnosticSeverity.Error, "No overload of '{0}' accepts arguments ({1}).");
    public static readonly DiagnosticDescriptor AmbiguousCall = new("CSC4006", DiagnosticSeverity.Error, "The call is ambiguous between {0}.");
    public static readonly DiagnosticDescriptor BinaryOperatorNotDefined = new("CSC4007", DiagnosticSeverity.Error, "Operator '{0}' cannot be applied to operands of type '{1}' and '{2}'.");
    public static readonly DiagnosticDescriptor UnaryOperatorNotDefined = new("CSC4008", DiagnosticSeverity.Error, "Operator '{0}' cannot be applied to an operand of type '{1}'.");
    public static readonly DiagnosticDescriptor NotAssignable = new("CSC4009", DiagnosticSeverity.Error, "'{0}' cannot be assigned to.");
    public static readonly DiagnosticDescriptor DuplicateLocal = new("CSC4010", DiagnosticSeverity.Error, "A local or parameter named '{0}' is already defined in this scope.");
    public static readonly DiagnosticDescriptor NotAllPathsReturn = new("CSC4011", DiagnosticSeverity.Error, "Not all code paths in '{0}' return a value.");
    public static readonly DiagnosticDescriptor ReturnValueInVoid = new("CSC4012", DiagnosticSeverity.Error, "'{0}' does not return a value; remove the return value.");
    public static readonly DiagnosticDescriptor MissingReturnValue = new("CSC4013", DiagnosticSeverity.Error, "'{0}' must return a value of type '{1}'.");
    public static readonly DiagnosticDescriptor JumpOutsideLoop = new("CSC4014", DiagnosticSeverity.Error, "'{0}' is only valid inside a loop{1}.");
    public static readonly DiagnosticDescriptor AwaitOutsideAsync = new("CSC4015", DiagnosticSeverity.Error, "'await' can only be used inside an async function.");
    public static readonly DiagnosticDescriptor NotAwaitable = new("CSC4016", DiagnosticSeverity.Error, "'{0}' cannot be awaited; expected Task or Task<T>.");
    public static readonly DiagnosticDescriptor NotIterable = new("CSC4017", DiagnosticSeverity.Error, "foreach cannot iterate over '{0}'.");
    public static readonly DiagnosticDescriptor YieldNotAllowed = new("CSC4018", DiagnosticSeverity.Error, "'yield' requires a return type of Iterable<T> or AsyncIterable<T>{0}.");
    public static readonly DiagnosticDescriptor NotAnError = new("CSC4019", DiagnosticSeverity.Error, "'{0}' is not an Error type.");
    public static readonly DiagnosticDescriptor UncatchableError = new("CSC4020", DiagnosticSeverity.Error, "'{0}' cannot be caught (Document 7 §7).");
    public static readonly DiagnosticDescriptor CannotInferTypeArguments = new("CSC4021", DiagnosticSeverity.Error, "The type arguments of '{0}' cannot be inferred; specify them explicitly.");
    public static readonly DiagnosticDescriptor NotInvocable = new("CSC4022", DiagnosticSeverity.Error, "'{0}' is not a function.");
    public static readonly DiagnosticDescriptor StaticMismatch = new("CSC4023", DiagnosticSeverity.Error, "'{0}' {1}.");
    public static readonly DiagnosticDescriptor NotAccessible = new("CSC4024", DiagnosticSeverity.Error, "'{0}' is private to {1}.");
    public static readonly DiagnosticDescriptor CannotInstantiate = new("CSC4025", DiagnosticSeverity.Error, "Cannot create an instance of '{0}'{1}.");
    public static readonly DiagnosticDescriptor VarNeedsInitializer = new("CSC4026", DiagnosticSeverity.Error, "Implicitly typed variable '{0}' needs an initializer with a type.");
    public static readonly DiagnosticDescriptor UninitializedLocal = new("CSC4027", DiagnosticSeverity.Error, "Non-nullable variable '{0}' of type '{1}' must be initialized.");
    public static readonly DiagnosticDescriptor LambdaNeedsTarget = new("CSC4028", DiagnosticSeverity.Error, "Cannot infer the type of this lambda; give its parameters types or assign it to an explicit function type.");
    public static readonly DiagnosticDescriptor PossibleNullDereference = new("CSC4029", DiagnosticSeverity.Warning, "'{0}' may be null here; use '?.', '!' or check for null first.");
    public static readonly DiagnosticDescriptor NullNotAllowed = new("CSC4030", DiagnosticSeverity.Error, "Cannot use null as non-nullable type '{0}'; declare it as '{0}?'.");
    public static readonly DiagnosticDescriptor ThisInStaticContext = new("CSC4031", DiagnosticSeverity.Error, "'{0}' is not available in a static context.");
    public static readonly DiagnosticDescriptor AssignToConstant = new("CSC4032", DiagnosticSeverity.Error, "Cannot assign to constant '{0}'.");
    public static readonly DiagnosticDescriptor PatternNeverMatches = new("CSC4034", DiagnosticSeverity.Error, "A pattern of type '{0}' can never match a value of type '{1}'.");
    public static readonly DiagnosticDescriptor ArgumentNameNotFound = new("CSC4036", DiagnosticSeverity.Error, "'{0}' has no parameter named '{1}'.");
    public static readonly DiagnosticDescriptor VoidHasNoValue = new("CSC4037", DiagnosticSeverity.Error, "An expression of type void has no value.");
    public static readonly DiagnosticDescriptor UnawaitedTask = new("CSC4038", DiagnosticSeverity.Warning, "The task returned by '{0}' is not awaited; await it or store it.");
    public static readonly DiagnosticDescriptor DeconstructionArity = new("CSC4039", DiagnosticSeverity.Error, "Cannot deconstruct a value of type '{0}' into {1} variables.");
    public static readonly DiagnosticDescriptor NoBaseType = new("CSC4040", DiagnosticSeverity.Error, "'base' is only valid inside a class that derives from another class.");
    public static readonly DiagnosticDescriptor NoMatchingConstructor = new("CSC4041", DiagnosticSeverity.Error, "'{0}' has no constructor that takes ({1}).");
    public static readonly DiagnosticDescriptor ModuleIsNotAValue = new("CSC4042", DiagnosticSeverity.Error, "'{0}' is a {1} and cannot be used as a value.");
    public static readonly DiagnosticDescriptor MissingEntryPoint = new("CSC4043", DiagnosticSeverity.Error, "App module '{0}' must declare 'void Main(Args args)' or 'async Task Main(Args args)'.");
}
