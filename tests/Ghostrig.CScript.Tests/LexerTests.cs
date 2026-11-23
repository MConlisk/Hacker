using System.Collections.Immutable;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Tests;

public class LexerTests
{
    private static (ImmutableArray<Token> Tokens, DiagnosticBag Diagnostics) Lex(string text)
    {
        var diagnostics = new DiagnosticBag();
        return (Lexer.Tokenize(new SourceText(text), diagnostics), diagnostics);
    }

    private static TokenKind[] Kinds(string text) => Lex(text).Tokens.Select(t => t.Kind).ToArray();

    [Fact]
    public void Keywords_identifiers_and_contextual_words()
    {
        Assert.Equal(
            [TokenKind.AsyncKeyword, TokenKind.Identifier, TokenKind.Identifier, TokenKind.AwaitKeyword, TokenKind.Identifier, TokenKind.EndOfFile],
            Kinds("async get when await _x1"));
    }

    [Fact]
    public void Greater_than_is_never_combined_by_the_lexer()
    {
        Assert.Equal([TokenKind.Identifier, TokenKind.Greater, TokenKind.Greater, TokenKind.Equal, TokenKind.IntLiteral, TokenKind.EndOfFile],
            Kinds("x >>= 1"));
        Assert.Equal([TokenKind.FatArrow, TokenKind.ThinArrow, TokenKind.QuestionQuestionEqual, TokenKind.LessLessEqual, TokenKind.EndOfFile],
            Kinds("=> -> ??= <<="));
    }

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("0x2A", 42L)]
    [InlineData("0b101010", 42L)]
    [InlineData("1_000_000", 1_000_000L)]
    [InlineData("0xFFFFFFFFFFFFFFFF", -1L)]
    public void Integer_literals(string text, long expected)
    {
        var (tokens, diagnostics) = Lex(text);
        Assert.Empty(diagnostics);
        Assert.Equal(expected, tokens[0].Value);
    }

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("1e-9", 1e-9)]
    [InlineData("2.5E+3", 2500.0)]
    public void Float_literals(string text, double expected)
    {
        var token = Lex(text).Tokens[0];
        Assert.Equal(TokenKind.FloatLiteral, token.Kind);
        Assert.Equal(expected, token.Value);
    }

    [Theory]
    [InlineData("99999999999999999999")]
    [InlineData("0x")]
    [InlineData("0b102")]
    public void Invalid_numbers_are_diagnosed(string text) =>
        Assert.Contains(Lex(text).Diagnostics, d => d.Code == "CSC1005");

    [Fact]
    public void String_char_and_bytes_literals_resolve_escapes()
    {
        var tokens = Lex("""
            "a\tb\u0041" @"C:\no\escape""q" 'x' '\n' b"\x89PNG"
            """).Tokens;
        Assert.Equal("a\tbA", tokens[0].Value);
        Assert.Equal("C:\\no\\escape\"q", tokens[1].Value);
        Assert.Equal(new Rune('x'), tokens[2].Value);
        Assert.Equal(new Rune('\n'), tokens[3].Value);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, ((ImmutableArray<byte>)tokens[4].Value!).ToArray());
    }

    [Fact]
    public void Interpolated_strings_split_into_text_and_holes()
    {
        var token = Lex("""$"{user}@{ip} {{x}} {bytes:N0}!" """).Tokens[0];
        Assert.Equal(TokenKind.InterpolatedString, token.Kind);
        Assert.Equal(
            ["hole:user", "text:@", "hole:ip", "text: {x} ", "hole:bytes:N0", "text:!"],
            token.Parts.Select(p => p.IsHole ? $"hole:{p.Text}{(p.Format is null ? "" : ":" + p.Format)}" : $"text:{p.Text}"));
    }

    [Fact]
    public void Doc_comments_attach_to_the_next_token()
    {
        var tokens = Lex("/// Least-recently-used cache.\n/// Second line.\n// ignored\nclass X {}").Tokens;
        Assert.Equal("Least-recently-used cache.\nSecond line.", tokens[0].DocComment);
        Assert.Null(tokens[1].DocComment);
    }

    [Theory]
    [InlineData("\"open", "CSC1002")]
    [InlineData("'", "CSC1003")]
    [InlineData("/* forever", "CSC1004")]
    [InlineData("#", "CSC1001")]
    [InlineData("\"bad \\q escape\"", "CSC1006")]
    [InlineData("$\"{oops\"", "CSC1007")]
    [InlineData("\"\\", "CSC1006")]
    public void Lexical_errors_are_reported_with_stable_codes(string text, string code) =>
        Assert.Contains(Lex(text).Diagnostics, d => d.Code == code);

    [Property(MaxTest = 300)]
    public Property Lexing_terminates_with_ordered_in_bounds_tokens(string? text)
    {
        text ??= string.Empty;
        var tokens = Lex(text).Tokens;
        var ordered = tokens.Zip(tokens.Skip(1)).All(p => p.First.Span.End <= p.Second.Span.Start);
        var inBounds = tokens.All(t => t.Span.Start >= 0 && t.Span.End <= text.Length);
        return (tokens[^1].Kind == TokenKind.EndOfFile && ordered && inBounds).ToProperty();
    }
}
