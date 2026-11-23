using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Syntax;

/// <summary>
/// Converts CScript source into tokens (Doc 7 §3). Lexical errors are reported and produce <see cref="TokenKind.Bad"/>
/// tokens so parsing can continue.
/// </summary>
public sealed class Lexer
{
    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private readonly int _end;
    private int _pos;
    private string? _pendingDoc;

    public Lexer(SourceText source, DiagnosticBag diagnostics, int start = 0, int? end = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _pos = start;
        _end = end ?? source.Length;
    }

    public static ImmutableArray<Token> Tokenize(SourceText source, DiagnosticBag diagnostics) =>
        new Lexer(source, diagnostics).LexAll();

    public ImmutableArray<Token> LexAll()
    {
        var tokens = ImmutableArray.CreateBuilder<Token>();
        while (true)
        {
            var token = Next();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
            {
                return tokens.ToImmutable();
            }
        }
    }

    private char Current => Peek(0);

    private char Peek(int offset) => _pos + offset < _end ? _source[_pos + offset] : '\0';

    private Token Next()
    {
        SkipTrivia();
        var doc = _pendingDoc;
        _pendingDoc = null;
        var token = LexToken();
        return doc is null ? token : token with { DocComment = doc };
    }

    private void SkipTrivia()
    {
        while (_pos < _end)
        {
            var c = Current;
            if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                var isDoc = Peek(2) == '/' && Peek(3) != '/';
                var start = _pos + (isDoc ? 3 : 2);
                while (_pos < _end && Current != '\n')
                {
                    _pos++;
                }

                if (isDoc)
                {
                    var line = _source.Text[start.._pos].Trim();
                    _pendingDoc = _pendingDoc is null ? line : _pendingDoc + "\n" + line;
                }
            }
            else if (c == '/' && Peek(1) == '*')
            {
                var start = _pos;
                _pos += 2;
                while (_pos < _end && !(Current == '*' && Peek(1) == '/'))
                {
                    _pos++;
                }

                if (_pos >= _end)
                {
                    _diagnostics.Report(DiagnosticCodes.UnterminatedComment, _source, new TextSpan(start, 2));
                    return;
                }

                _pos += 2;
            }
            else
            {
                return;
            }
        }
    }

    private Token LexToken()
    {
        var start = _pos;
        if (_pos >= _end)
        {
            return new Token(TokenKind.EndOfFile, new TextSpan(_end, 0), string.Empty);
        }

        var c = Current;
        if (char.IsAsciiLetter(c) || c == '_')
        {
            if (c == 'b' && Peek(1) == '"')
            {
                _pos++;
                return LexString(start, TokenKind.BytesLiteral, verbatim: false);
            }

            while (char.IsAsciiLetterOrDigit(Current) || Current == '_')
            {
                _pos++;
            }

            var text = _source.Text[start.._pos];
            return SyntaxFacts.Keywords.TryGetValue(text, out var kw)
                ? new Token(kw, Span(start), text)
                : new Token(TokenKind.Identifier, Span(start), text, text);
        }

        if (char.IsAsciiDigit(c))
        {
            return LexNumber(start);
        }

        switch (c)
        {
            case '"':
                return LexString(start, TokenKind.StringLiteral, verbatim: false);
            case '@' when Peek(1) == '"':
                _pos++;
                return LexString(start, TokenKind.StringLiteral, verbatim: true);
            case '$' when Peek(1) == '"':
                return LexInterpolated(start);
            case '\'':
                return LexChar(start);
        }

        var kind = LexPunctuation();
        if (kind is { } k)
        {
            return new Token(k, Span(start), _source.Text[start.._pos]);
        }

        _pos++;
        _diagnostics.Report(DiagnosticCodes.BadCharacter, _source, Span(start), c);
        return new Token(TokenKind.Bad, Span(start), c.ToString());
    }

    private TokenKind? LexPunctuation()
    {
        var (kind, length) = (Current, Peek(1), Peek(2)) switch
        {
            ('?', '?', '=') => (TokenKind.QuestionQuestionEqual, 3),
            ('<', '<', '=') => (TokenKind.LessLessEqual, 3),
            ('?', '?', _) => (TokenKind.QuestionQuestion, 2),
            ('?', '.', _) when !char.IsAsciiDigit(Peek(2)) => (TokenKind.QuestionDot, 2),
            ('=', '>', _) => (TokenKind.FatArrow, 2),
            ('-', '>', _) => (TokenKind.ThinArrow, 2),
            ('=', '=', _) => (TokenKind.EqualEqual, 2),
            ('!', '=', _) => (TokenKind.BangEqual, 2),
            ('<', '=', _) => (TokenKind.LessEqual, 2),
            ('&', '&', _) => (TokenKind.AmpAmp, 2),
            ('|', '|', _) => (TokenKind.PipePipe, 2),
            ('<', '<', _) => (TokenKind.LessLess, 2),
            ('+', '+', _) => (TokenKind.PlusPlus, 2),
            ('-', '-', _) => (TokenKind.MinusMinus, 2),
            ('+', '=', _) => (TokenKind.PlusEqual, 2),
            ('-', '=', _) => (TokenKind.MinusEqual, 2),
            ('*', '=', _) => (TokenKind.StarEqual, 2),
            ('/', '=', _) => (TokenKind.SlashEqual, 2),
            ('%', '=', _) => (TokenKind.PercentEqual, 2),
            ('&', '=', _) => (TokenKind.AmpEqual, 2),
            ('|', '=', _) => (TokenKind.PipeEqual, 2),
            ('^', '=', _) => (TokenKind.CaretEqual, 2),
            ('(', _, _) => (TokenKind.OpenParen, 1),
            (')', _, _) => (TokenKind.CloseParen, 1),
            ('{', _, _) => (TokenKind.OpenBrace, 1),
            ('}', _, _) => (TokenKind.CloseBrace, 1),
            ('[', _, _) => (TokenKind.OpenBracket, 1),
            (']', _, _) => (TokenKind.CloseBracket, 1),
            (';', _, _) => (TokenKind.Semicolon, 1),
            (',', _, _) => (TokenKind.Comma, 1),
            ('.', _, _) => (TokenKind.Dot, 1),
            (':', _, _) => (TokenKind.Colon, 1),
            ('@', _, _) => (TokenKind.At, 1),
            ('?', _, _) => (TokenKind.Question, 1),
            ('+', _, _) => (TokenKind.Plus, 1),
            ('-', _, _) => (TokenKind.Minus, 1),
            ('*', _, _) => (TokenKind.Star, 1),
            ('/', _, _) => (TokenKind.Slash, 1),
            ('%', _, _) => (TokenKind.Percent, 1),
            ('&', _, _) => (TokenKind.Amp, 1),
            ('|', _, _) => (TokenKind.Pipe, 1),
            ('^', _, _) => (TokenKind.Caret, 1),
            ('~', _, _) => (TokenKind.Tilde, 1),
            ('!', _, _) => (TokenKind.Bang, 1),
            ('<', _, _) => (TokenKind.Less, 1),
            ('>', _, _) => (TokenKind.Greater, 1),   // never combined here: see Token remarks
            ('=', _, _) => (TokenKind.Equal, 1),
            _ => ((TokenKind?)null, 0),
        };

        if (kind is null)
        {
            return null;
        }

        _pos += length;
        return kind;
    }

    private Token LexNumber(int start)
    {
        var isHex = Current == '0' && Peek(1) is 'x' or 'X';
        var isBin = Current == '0' && Peek(1) is 'b' or 'B';
        if (isHex || isBin)
        {
            _pos += 2;
            while (Uri.IsHexDigit(Current) || Current == '_')
            {
                _pos++;
            }

            var raw = _source.Text[start.._pos];
            var digits = raw[2..].Replace("_", string.Empty);
            try
            {
                var value = digits.Length == 0
                    ? throw new FormatException()
                    : isHex
                        ? (long)ulong.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                        : (long)Convert.ToUInt64(digits, 2);
                return new Token(TokenKind.IntLiteral, Span(start), raw, value);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                _diagnostics.Report(DiagnosticCodes.InvalidNumber, _source, Span(start), raw);
                return new Token(TokenKind.IntLiteral, Span(start), raw, 0L);
            }
        }

        var isFloat = false;
        ConsumeDigits();
        if (Current == '.' && char.IsAsciiDigit(Peek(1)))
        {
            isFloat = true;
            _pos++;
            ConsumeDigits();
        }

        if (Current is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
        {
            isFloat = true;
            _pos += Peek(1) is '+' or '-' ? 2 : 1;
            ConsumeDigits();
        }

        var text = _source.Text[start.._pos];
        var clean = text.Replace("_", string.Empty);
        if (isFloat)
        {
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            {
                return new Token(TokenKind.FloatLiteral, Span(start), text, d);
            }
        }
        else if (long.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out var l))
        {
            return new Token(TokenKind.IntLiteral, Span(start), text, l);
        }
        else if (clean == "9223372036854775808")
        {
            // Allows -9223372036854775808 (the parser negates the literal).
            return new Token(TokenKind.IntLiteral, Span(start), text, long.MinValue);
        }

        _diagnostics.Report(DiagnosticCodes.InvalidNumber, _source, Span(start), text);
        return new Token(isFloat ? TokenKind.FloatLiteral : TokenKind.IntLiteral, Span(start), text, isFloat ? 0d : 0L);

        void ConsumeDigits()
        {
            while (char.IsAsciiDigit(Current) || (Current == '_' && char.IsAsciiDigit(Peek(1))))
            {
                _pos++;
            }
        }
    }

    private Token LexString(int start, TokenKind kind, bool verbatim)
    {
        _pos++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _end || (!verbatim && Current == '\n'))
            {
                _diagnostics.Report(DiagnosticCodes.UnterminatedString, _source, Span(start));
                break;
            }

            var c = Current;
            if (c == '"')
            {
                if (verbatim && Peek(1) == '"')
                {
                    sb.Append('"');
                    _pos += 2;
                    continue;
                }

                _pos++;
                break;
            }

            if (c == '\\' && !verbatim)
            {
                AppendEscape(sb, allowByteEscapes: kind == TokenKind.BytesLiteral);
                continue;
            }

            sb.Append(c);
            _pos++;
        }

        var text = _source.Text[start.._pos];
        if (kind == TokenKind.BytesLiteral)
        {
            var bytes = new byte[sb.Length];
            for (var i = 0; i < sb.Length; i++)
            {
                if (sb[i] > 0xFF)
                {
                    _diagnostics.Report(DiagnosticCodes.InvalidEscape, _source, Span(start), sb[i]);
                }

                bytes[i] = (byte)sb[i];
            }

            return new Token(kind, Span(start), text, bytes.ToImmutableArray());
        }

        return new Token(kind, Span(start), text, sb.ToString());
    }

    private Token LexChar(int start)
    {
        _pos++;
        var sb = new StringBuilder();
        if (Current == '\\')
        {
            AppendEscape(sb, allowByteEscapes: false);
        }
        else if (Current is not ('\'' or '\n' or '\0'))
        {
            sb.Append(Current);
            _pos++;
            if (char.IsHighSurrogate(sb[0]) && char.IsLowSurrogate(Current))
            {
                sb.Append(Current);
                _pos++;
            }
        }

        if (Current != '\'' || sb.Length == 0)
        {
            _diagnostics.Report(DiagnosticCodes.UnterminatedChar, _source, Span(start));
            while (_pos < _end && Current is not ('\'' or '\n'))
            {
                _pos++;
            }

            if (Current == '\'')
            {
                _pos++;
            }

            return new Token(TokenKind.CharLiteral, Span(start), _source.Text[start.._pos], new Rune('?'));
        }

        _pos++;
        var rune = Rune.GetRuneAt(sb.ToString(), 0);
        return new Token(TokenKind.CharLiteral, Span(start), _source.Text[start.._pos], rune);
    }

    private void AppendEscape(StringBuilder sb, bool allowByteEscapes)
    {
        var escapeStart = _pos;
        _pos++; // backslash
        if (_pos >= _end)
        {
            _diagnostics.Report(DiagnosticCodes.InvalidEscape, _source, new TextSpan(escapeStart, 1), string.Empty);
            return;
        }

        var c = Current;
        _pos++;
        switch (c)
        {
            case 'n': sb.Append('\n'); return;
            case 't': sb.Append('\t'); return;
            case 'r': sb.Append('\r'); return;
            case '0': sb.Append('\0'); return;
            case '\\': sb.Append('\\'); return;
            case '"': sb.Append('"'); return;
            case '\'': sb.Append('\''); return;
            case '{': sb.Append('{'); return;
            case '}': sb.Append('}'); return;
            case 'x':
                AppendHex(2, escapeStart, sb);
                return;
            case 'u' when !allowByteEscapes:
                AppendHex(4, escapeStart, sb);
                return;
            default:
                _diagnostics.Report(DiagnosticCodes.InvalidEscape, _source, TextSpan.FromBounds(escapeStart, _pos), c);
                return;
        }
    }

    private void AppendHex(int digits, int escapeStart, StringBuilder sb)
    {
        var hexStart = _pos;
        while (_pos - hexStart < digits && Uri.IsHexDigit(Current))
        {
            _pos++;
        }

        if (_pos - hexStart != digits)
        {
            _diagnostics.Report(DiagnosticCodes.InvalidEscape, _source, TextSpan.FromBounds(escapeStart, _pos),
                _source.Text[(escapeStart + 1).._pos]);
            return;
        }

        var value = int.Parse(_source.Text.AsSpan(hexStart, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        sb.Append((char)value);
    }

    /// <summary>
    /// Lexes <c>$"text {expr} more {expr:format}"</c> into literal and hole parts; holes are parsed later by the parser.
    /// <c>{{</c> and <c>}}</c> escape braces.
    /// </summary>
    private Token LexInterpolated(int start)
    {
        _pos += 2; // $"
        var parts = ImmutableArray.CreateBuilder<InterpolationPart>();
        var literal = new StringBuilder();
        var literalStart = _pos;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                parts.Add(new InterpolationPart(false, literal.ToString(), TextSpan.FromBounds(literalStart, _pos), null));
                literal.Clear();
            }
        }

        while (true)
        {
            if (_pos >= _end || Current == '\n')
            {
                _diagnostics.Report(DiagnosticCodes.UnterminatedString, _source, Span(start));
                break;
            }

            var c = Current;
            if (c == '"')
            {
                FlushLiteral();
                _pos++;
                break;
            }

            if (c == '{' && Peek(1) == '{')
            {
                literal.Append('{');
                _pos += 2;
                continue;
            }

            if (c == '}' && Peek(1) == '}')
            {
                literal.Append('}');
                _pos += 2;
                continue;
            }

            if (c == '}')
            {
                _diagnostics.Report(DiagnosticCodes.UnbalancedInterpolation, _source, new TextSpan(_pos, 1));
                _pos++;
                continue;
            }

            if (c == '{')
            {
                FlushLiteral();
                _pos++;
                var holeStart = _pos;
                var depth = 0;
                var formatStart = -1;
                while (_pos < _end && Current != '\n' && !(depth == 0 && Current == '}'))
                {
                    switch (Current)
                    {
                        case '(' or '[' or '{': depth++; break;
                        case ')' or ']' or '}': depth--; break;
                        case '"': SkipNestedString(); continue;
                        case ':' when depth == 0 && formatStart < 0: formatStart = _pos; break;
                    }

                    _pos++;
                }

                if (Current != '}')
                {
                    _diagnostics.Report(DiagnosticCodes.UnbalancedInterpolation, _source, TextSpan.FromBounds(holeStart - 1, _pos));
                    break;
                }

                var exprEnd = formatStart >= 0 ? formatStart : _pos;
                var format = formatStart >= 0 ? _source.Text[(formatStart + 1).._pos] : null;
                parts.Add(new InterpolationPart(true, _source.Text[holeStart..exprEnd], TextSpan.FromBounds(holeStart, exprEnd), format));
                _pos++; // }
                literalStart = _pos;
                continue;
            }

            if (c == '\\')
            {
                AppendEscape(literal, allowByteEscapes: false);
                continue;
            }

            literal.Append(c);
            _pos++;
        }

        return new Token(TokenKind.InterpolatedString, Span(start), _source.Text[start.._pos]) { Parts = parts.ToImmutable() };
    }

    private void SkipNestedString()
    {
        _pos++;
        while (_pos < _end && Current is not ('"' or '\n'))
        {
            _pos = Math.Min(_end, _pos + (Current == '\\' ? 2 : 1));
        }

        if (Current == '"')
        {
            _pos++;
        }
    }

    private TextSpan Span(int start) => TextSpan.FromBounds(start, _pos);
}
