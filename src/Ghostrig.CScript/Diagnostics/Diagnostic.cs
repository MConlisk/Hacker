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
}
