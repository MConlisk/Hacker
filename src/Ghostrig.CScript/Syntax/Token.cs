using System.Collections.Frozen;
using System.Collections.Immutable;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Syntax;

public enum TokenKind
{
    EndOfFile,
    Bad,
    Identifier,
    IntLiteral,
    FloatLiteral,
    CharLiteral,
    StringLiteral,
    BytesLiteral,
    InterpolatedString,

    // Punctuation
    OpenParen, CloseParen, OpenBrace, CloseBrace, OpenBracket, CloseBracket,
    Semicolon, Comma, Dot, Colon, At,
    Question, QuestionDot, QuestionQuestion, QuestionQuestionEqual,
    FatArrow, ThinArrow,
    Plus, Minus, Star, Slash, Percent, Amp, Pipe, Caret, Tilde, Bang,
    Less, Greater, LessEqual, EqualEqual, BangEqual, AmpAmp, PipePipe, LessLess,
    Equal, PlusEqual, MinusEqual, StarEqual, SlashEqual, PercentEqual, AmpEqual, PipeEqual, CaretEqual, LessLessEqual,
    PlusPlus, MinusMinus,

    // Keywords
    ModuleKeyword, ImportKeyword, AsKeyword, PrivateKeyword, ConstKeyword, VarKeyword,
    IfKeyword, ElseKeyword, WhileKeyword, DoKeyword, ForKeyword, ForeachKeyword, InKeyword,
    BreakKeyword, ContinueKeyword, ReturnKeyword, SwitchKeyword, CaseKeyword, DefaultKeyword,
    TryKeyword, CatchKeyword, FinallyKeyword, ThrowKeyword, NewKeyword, NullKeyword, TrueKeyword, FalseKeyword,
    ThisKeyword, BaseKeyword, ClassKeyword, StructKeyword, InterfaceKeyword, EnumKeyword, WhereKeyword,
    IsKeyword, FnKeyword, YieldKeyword, TypeofKeyword, ParamsKeyword, AsyncKeyword, AwaitKeyword, StaticKeyword,
}

/// <summary>A segment of an interpolated string: literal text, or a hole holding an expression.</summary>
/// <param name="IsHole">True for <c>{expression[:format]}</c> segments.</param>
/// <param name="Text">Literal text (escapes resolved) or the raw hole expression source.</param>
/// <param name="Span">Location of the segment's text in the source file.</param>
/// <param name="Format">Optional format specifier after ':' in a hole.</param>
public sealed record InterpolationPart(bool IsHole, string Text, TextSpan Span, string? Format);

/// <summary>
/// A lexical token. '>' is always lexed as a single <see cref="TokenKind.Greater"/>; the parser combines adjacent
/// greater-than tokens into shift / comparison operators so that nested generics (<c>List&lt;List&lt;int&gt;&gt;</c>) parse.
/// </summary>
public sealed record Token(TokenKind Kind, TextSpan Span, string Text, object? Value = null)
{
    /// <summary>Text of a <c>///</c> doc comment immediately preceding this token, if any.</summary>
    public string? DocComment { get; init; }

    public ImmutableArray<InterpolationPart> Parts { get; init; } = [];

    public bool IsKeyword => Kind >= TokenKind.ModuleKeyword;

    public override string ToString() => Kind == TokenKind.EndOfFile ? "end of file" : Text;
}

public static class SyntaxFacts
{
    public static readonly FrozenDictionary<string, TokenKind> Keywords = new Dictionary<string, TokenKind>
    {
        ["module"] = TokenKind.ModuleKeyword, ["import"] = TokenKind.ImportKeyword, ["as"] = TokenKind.AsKeyword,
        ["private"] = TokenKind.PrivateKeyword, ["const"] = TokenKind.ConstKeyword, ["var"] = TokenKind.VarKeyword,
        ["if"] = TokenKind.IfKeyword, ["else"] = TokenKind.ElseKeyword, ["while"] = TokenKind.WhileKeyword,
        ["do"] = TokenKind.DoKeyword, ["for"] = TokenKind.ForKeyword, ["foreach"] = TokenKind.ForeachKeyword,
        ["in"] = TokenKind.InKeyword, ["break"] = TokenKind.BreakKeyword, ["continue"] = TokenKind.ContinueKeyword,
        ["return"] = TokenKind.ReturnKeyword, ["switch"] = TokenKind.SwitchKeyword, ["case"] = TokenKind.CaseKeyword,
        ["default"] = TokenKind.DefaultKeyword, ["try"] = TokenKind.TryKeyword, ["catch"] = TokenKind.CatchKeyword,
        ["finally"] = TokenKind.FinallyKeyword, ["throw"] = TokenKind.ThrowKeyword, ["new"] = TokenKind.NewKeyword,
        ["null"] = TokenKind.NullKeyword, ["true"] = TokenKind.TrueKeyword, ["false"] = TokenKind.FalseKeyword,
        ["this"] = TokenKind.ThisKeyword, ["base"] = TokenKind.BaseKeyword, ["class"] = TokenKind.ClassKeyword,
        ["struct"] = TokenKind.StructKeyword, ["interface"] = TokenKind.InterfaceKeyword, ["enum"] = TokenKind.EnumKeyword,
        ["where"] = TokenKind.WhereKeyword, ["is"] = TokenKind.IsKeyword, ["fn"] = TokenKind.FnKeyword,
        ["yield"] = TokenKind.YieldKeyword, ["typeof"] = TokenKind.TypeofKeyword, ["params"] = TokenKind.ParamsKeyword,
        ["async"] = TokenKind.AsyncKeyword, ["await"] = TokenKind.AwaitKeyword, ["static"] = TokenKind.StaticKeyword,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Built-in type names; they are ordinary identifiers to the lexer and resolved by the binder.</summary>
    public static readonly FrozenSet<string> PredefinedTypes =
        new[] { "bool", "byte", "char", "int", "float", "string", "bytes", "void", "object" }.ToFrozenSet(StringComparer.Ordinal);
}
