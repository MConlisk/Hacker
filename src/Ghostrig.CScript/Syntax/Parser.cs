using System.Collections.Immutable;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Syntax;

/// <summary>A parsed source file and the diagnostics produced while lexing and parsing it.</summary>
public sealed record SyntaxTree(SourceText Source, CompilationUnit Root, ImmutableArray<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    public static SyntaxTree Parse(string text, string path = "<memory>") => Parse(new SourceText(text, path));

    public static SyntaxTree Parse(SourceText source)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = Lexer.Tokenize(source, diagnostics);
        var root = new Parser(source, tokens, diagnostics).ParseCompilationUnit();
        return new SyntaxTree(source, root, [.. diagnostics]);
    }
}

/// <summary>
/// Recursive-descent parser for CScript (Doc 7 §15) with precedence climbing for expressions (Doc 7 §6) and bounded
/// speculative parsing to resolve the C#-family ambiguities (casts, generic arguments, lambdas, local declarations).
/// </summary>
public sealed class Parser
{
    private readonly SourceText _source;
    private readonly ImmutableArray<Token> _tokens;
    private DiagnosticBag _diagnostics;
    private int _pos;

    public Parser(SourceText source, ImmutableArray<Token> tokens, DiagnosticBag diagnostics)
    {
        _source = source;
        _tokens = tokens;
        _diagnostics = diagnostics;
        if (tokens.IsDefaultOrEmpty || tokens[^1].Kind != TokenKind.EndOfFile)
        {
            throw new ArgumentException("The token stream must end with EndOfFile.", nameof(tokens));
        }
    }

    #region Token helpers

    private Token Current => Peek(0);

    private Token Peek(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Length - 1)];

    private Token Previous => _tokens[Math.Max(0, _pos - 1)];

    private Token Next()
    {
        var token = Current;
        if (token.Kind != TokenKind.EndOfFile)
        {
            _pos++;
        }

        return token;
    }

    private bool At(TokenKind kind) => Current.Kind == kind;

    private bool AtContextual(string word) => Current.Kind == TokenKind.Identifier && Current.Text == word;

    private bool Match(TokenKind kind)
    {
        if (!At(kind))
        {
            return false;
        }

        Next();
        return true;
    }

    private Token Expect(TokenKind kind, string description)
    {
        if (At(kind))
        {
            return Next();
        }

        _diagnostics.Report(DiagnosticCodes.Expected, _source, Current.Span, description, Current);
        return new Token(kind, new TextSpan(Current.Span.Start, 0), string.Empty, kind == TokenKind.Identifier ? "<missing>" : null);
    }

    private string ExpectIdentifier(string description = "an identifier") => Expect(TokenKind.Identifier, description).Text is { Length: > 0 } t ? t : "<missing>";

    private static bool Adjacent(Token a, Token b) => a.Span.End == b.Span.Start;

    private TextSpan From(int startPos) =>
        TextSpan.FromBounds(_tokens[startPos].Span.Start, Math.Max(_tokens[startPos].Span.Start, Previous.Span.End));

    /// <summary>Runs <paramref name="parse"/> speculatively; on failure the position and diagnostics are restored.</summary>
    private bool Try<T>(Func<T?> parse, out T result) where T : class
    {
        var saved = _pos;
        var savedDiagnostics = _diagnostics;
        var scratch = new DiagnosticBag();
        _diagnostics = scratch;
        T? value;
        try
        {
            value = parse();
        }
        finally
        {
            _diagnostics = savedDiagnostics;
        }

        if (value is not null && !scratch.HasErrors)
        {
            result = value;
            return true;
        }

        _pos = saved;
        result = null!;
        return false;
    }

    #endregion

    #region Compilation unit

    public CompilationUnit ParseCompilationUnit()
    {
        var start = _pos;
        var doc = Current.DocComment;
        var attributes = ParseAttributes();
        Expect(TokenKind.ModuleKeyword, "'module'");
        var name = ParseQualifiedName();
        Expect(TokenKind.Semicolon, "';'");
        CheckModuleName(name);

        var imports = ImmutableArray.CreateBuilder<ImportDecl>();
        while (At(TokenKind.ImportKeyword))
        {
            var importStart = _pos;
            Next();
            var importName = ParseQualifiedName();
            string? alias = null;
            if (Match(TokenKind.AsKeyword))
            {
                alias = ExpectIdentifier();
            }

            Expect(TokenKind.Semicolon, "';'");
            imports.Add(new ImportDecl(From(importStart), importName, alias));
        }

        var members = ImmutableArray.CreateBuilder<MemberDecl>();
        while (!At(TokenKind.EndOfFile))
        {
            var before = _pos;
            var member = ParseMember(enclosingType: null);
            if (member is not null)
            {
                members.Add(member);
            }

            if (_pos == before)
            {
                Next();
            }
        }

        return new CompilationUnit(From(start), attributes, name, imports.ToImmutable(), members.ToImmutable(), doc);
    }

    private void CheckModuleName(QualifiedName name)
    {
        if (_source.Path.StartsWith('<'))
        {
            return;
        }

        var file = Path.GetFileNameWithoutExtension(_source.Path);
        if (name.Parts.Length > 0 && !string.Equals(name.Parts[^1], file, StringComparison.Ordinal))
        {
            _diagnostics.Report(DiagnosticCodes.ModuleNameMismatch, _source, name.Span, name.ToString(), file);
        }
    }

    private QualifiedName ParseQualifiedName()
    {
        var start = _pos;
        var parts = ImmutableArray.CreateBuilder<string>();
        parts.Add(ExpectIdentifier());
        while (At(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
        {
            Next();
            parts.Add(Next().Text);
        }

        return new QualifiedName(From(start), parts.ToImmutable());
    }

    private ImmutableArray<AttributeSyntax> ParseAttributes()
    {
        var list = ImmutableArray.CreateBuilder<AttributeSyntax>();
        while (At(TokenKind.At))
        {
            var start = _pos;
            Next();
            var name = ParseQualifiedName().ToString();
            var args = ImmutableArray<Expr>.Empty;
            if (Match(TokenKind.OpenParen))
            {
                args = ParseSeparated(TokenKind.CloseParen, ParseExpression);
                Expect(TokenKind.CloseParen, "')'");
            }

            list.Add(new AttributeSyntax(From(start), name, args));
        }

        return list.ToImmutable();
    }

    #endregion

    #region Members

    private MemberDecl? ParseMember(string? enclosingType)
    {
        var start = _pos;
        var doc = Current.DocComment;
        var attributes = ParseAttributes();
        var modifiers = ParseModifiers();

        switch (Current.Kind)
        {
            case TokenKind.ClassKeyword:
            case TokenKind.StructKeyword:
            case TokenKind.InterfaceKeyword:
                return ParseTypeDecl(start, attributes, modifiers, doc);
            case TokenKind.EnumKeyword:
                return ParseEnum(start, attributes, modifiers, doc);
            case TokenKind.VarKeyword:
                Next();
                return ParseFieldRest(start, attributes, modifiers, doc, type: null, ExpectIdentifier());
        }

        if (enclosingType is not null && AtContextual(enclosingType) && Peek(1).Kind == TokenKind.OpenParen)
        {
            return ParseConstructor(start, attributes, modifiers, doc);
        }

        if (!StartsType())
        {
            _diagnostics.Report(DiagnosticCodes.ExpectedMember, _source, Current.Span, Current);
            return null;
        }

        var type = ParseType();
        if (enclosingType is not null && At(TokenKind.ThisKeyword))
        {
            return ParseIndexer(start, attributes, modifiers, doc, type);
        }

        var name = ExpectIdentifier();
        if (At(TokenKind.Less) || At(TokenKind.OpenParen))
        {
            return ParseFunctionRest(start, attributes, modifiers, doc, type, name);
        }

        if (At(TokenKind.OpenBrace))
        {
            var accessors = ParseAccessors();
            return new PropertyDecl(From(start), attributes, modifiers, doc, type, name, accessors, null);
        }

        if (At(TokenKind.FatArrow))
        {
            Next();
            var body = ParseExpression();
            Expect(TokenKind.Semicolon, "';'");
            return new PropertyDecl(From(start), attributes, modifiers, doc, type, name, [], body);
        }

        return ParseFieldRest(start, attributes, modifiers, doc, type, name);
    }

    private Modifiers ParseModifiers()
    {
        var seen = new HashSet<TokenKind>();
        while (Current.Kind is TokenKind.PrivateKeyword or TokenKind.AsyncKeyword or TokenKind.ConstKeyword)
        {
            if (!seen.Add(Current.Kind))
            {
                _diagnostics.Report(DiagnosticCodes.DuplicateModifier, _source, Current.Span, Current.Text);
            }

            Next();
        }

        return new Modifiers(
            seen.Contains(TokenKind.PrivateKeyword), seen.Contains(TokenKind.AsyncKeyword), seen.Contains(TokenKind.ConstKeyword));
    }

    private FieldDecl ParseFieldRest(int start, ImmutableArray<AttributeSyntax> attributes, Modifiers modifiers, string? doc,
        TypeSyntax? type, string name)
    {
        Expr? initializer = null;
        if (Match(TokenKind.Equal))
        {
            initializer = ParseExpression();
        }

        Expect(TokenKind.Semicolon, "';'");
        return new FieldDecl(From(start), attributes, modifiers, doc, type, name, initializer);
    }

    private FunctionDecl ParseFunctionRest(int start, ImmutableArray<AttributeSyntax> attributes, Modifiers modifiers, string? doc,
        TypeSyntax returnType, string name)
    {
        var typeParameters = ParseTypeParameters();
        var parameters = ParseParameterList();
        var constraints = ParseConstraints();
        var (body, exprBody) = ParseBody();
        return new FunctionDecl(From(start), attributes, modifiers, doc, returnType, name, typeParameters, parameters, constraints, body, exprBody);
    }

    /// <summary>Parses <c>{ … }</c>, <c>=&gt; expr;</c> or <c>;</c> (an interface signature).</summary>
    private (BlockStmt? Body, Expr? ExpressionBody) ParseBody()
    {
        if (At(TokenKind.OpenBrace))
        {
            return (ParseBlock(), null);
        }

        if (Match(TokenKind.FatArrow))
        {
            var expr = ParseExpression();
            Expect(TokenKind.Semicolon, "';'");
            return (null, expr);
        }

        Expect(TokenKind.Semicolon, "a body, '=>' or ';'");
        return (null, null);
    }

    private ConstructorDecl ParseConstructor(int start, ImmutableArray<AttributeSyntax> attributes, Modifiers modifiers, string? doc)
    {
        var name = Next().Text;
        var parameters = ParseParameterList();
        ConstructorInitializer? initializer = null;
        if (Match(TokenKind.Colon))
        {
            var initStart = _pos;
            var isBase = At(TokenKind.BaseKeyword);
            if (!Match(TokenKind.BaseKeyword))
            {
                Expect(TokenKind.ThisKeyword, "'base' or 'this'");
            }

            var args = ParseArgumentList();
            initializer = new ConstructorInitializer(From(initStart), isBase, args);
        }

        var body = ParseBlock();
        return new ConstructorDecl(From(start), attributes, modifiers, doc, name, parameters, initializer, body);
    }

    private IndexerDecl ParseIndexer(int start, ImmutableArray<AttributeSyntax> attributes, Modifiers modifiers, string? doc, TypeSyntax type)
    {
        Next(); // this
        Expect(TokenKind.OpenBracket, "'['");
        var parameters = ParseSeparated(TokenKind.CloseBracket, ParseParameter);
        Expect(TokenKind.CloseBracket, "']'");
        var accessors = ParseAccessors();
        return new IndexerDecl(From(start), attributes, modifiers, doc, type, parameters, accessors);
    }

    private ImmutableArray<Accessor> ParseAccessors()
    {
        Expect(TokenKind.OpenBrace, "'{'");
        var list = ImmutableArray.CreateBuilder<Accessor>();
        while (AtContextual("get") || AtContextual("set"))
        {
            var start = _pos;
            var isGetter = Next().Text == "get";
            BlockStmt? body = null;
            Expr? exprBody = null;
            if (At(TokenKind.OpenBrace))
            {
                body = ParseBlock();
            }
            else if (Match(TokenKind.FatArrow))
            {
                exprBody = ParseExpression();
                Expect(TokenKind.Semicolon, "';'");
            }
            else
            {
                Expect(TokenKind.Semicolon, "';'");
            }

            list.Add(new Accessor(From(start), isGetter, body, exprBody));
        }

        Expect(TokenKind.CloseBrace, "'get', 'set' or '}'");
        return list.ToImmutable();
    }

    private TypeDecl ParseTypeDecl(int start, ImmutableArray<AttributeSyntax> attributes, Modifiers modifiers, string? doc)
    {
        var kind = Next().Kind switch
        {
            TokenKind.ClassKeyword => TypeDeclKind.Class,
            TokenKind.StructKeyword => TypeDeclKind.Struct,
            _ => TypeDeclKind.Interface,
        };
        var name = ExpectIdentifier("a type name");
        var typeParameters = ParseTypeParameters();
        var bases = ImmutableArray<TypeSyntax>.Empty;
        if (Match(TokenKind.Colon))
        {
            bases = ParseTypeList();
        }

        var constraints = ParseConstraints();
        Expect(TokenKind.OpenBrace, "'{'");
        var members = ImmutableArray.CreateBuilder<MemberDecl>();
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            var before = _pos;
            var member = ParseMember(name);
            if (member is not null)
            {
                members.Add(member);
            }

            if (_pos == before)
            {
                Next();
            }
        }

        Expect(TokenKind.CloseBrace, "'}'");
        return new TypeDecl(From(start), attributes, modifiers, doc, kind, name, typeParameters, bases, constraints, members.ToImmutable());
    }

    private EnumDecl ParseEnum(int start, ImmutableArray<AttributeSyntax> attributes, Modifiers modifiers, string? doc)
    {
        Next(); // enum
        var name = ExpectIdentifier("an enum name");
        Expect(TokenKind.OpenBrace, "'{'");
        var members = ImmutableArray.CreateBuilder<EnumMember>();
        while (At(TokenKind.Identifier))
        {
            var memberStart = _pos;
            var memberName = Next().Text;
            Expr? value = null;
            if (Match(TokenKind.Equal))
            {
                value = ParseExpression();
            }

            members.Add(new EnumMember(From(memberStart), memberName, value));
            if (!Match(TokenKind.Comma))
            {
                break;
            }
        }

        Expect(TokenKind.CloseBrace, "'}'");
        return new EnumDecl(From(start), attributes, modifiers, doc, name, members.ToImmutable());
    }

    private ImmutableArray<TypeParameter> ParseTypeParameters()
    {
        if (!Match(TokenKind.Less))
        {
            return [];
        }

        var list = ParseSeparated(TokenKind.Greater, () =>
        {
            var start = _pos;
            var name = ExpectIdentifier("a type parameter");
            return new TypeParameter(From(start), name);
        });
        Expect(TokenKind.Greater, "'>'");
        return list;
    }

    private ImmutableArray<Constraint> ParseConstraints()
    {
        var list = ImmutableArray.CreateBuilder<Constraint>();
        while (At(TokenKind.WhereKeyword))
        {
            var start = _pos;
            Next();
            var parameter = ExpectIdentifier("a type parameter");
            Expect(TokenKind.Colon, "':'");
            list.Add(new Constraint(From(start), parameter, ParseTypeList()));
        }

        return list.ToImmutable();
    }

    private ImmutableArray<TypeSyntax> ParseTypeList()
    {
        var list = ImmutableArray.CreateBuilder<TypeSyntax>();
        do
        {
            list.Add(ParseType());
        }
        while (Match(TokenKind.Comma));
        return list.ToImmutable();
    }

    private ImmutableArray<Parameter> ParseParameterList()
    {
        Expect(TokenKind.OpenParen, "'('");
        var list = ParseSeparated(TokenKind.CloseParen, ParseParameter);
        Expect(TokenKind.CloseParen, "')'");
        return list;
    }

    private Parameter ParseParameter()
    {
        var start = _pos;
        var isParams = Match(TokenKind.ParamsKeyword);
        var type = ParseType();
        var name = ExpectIdentifier("a parameter name");
        Expr? defaultValue = null;
        if (Match(TokenKind.Equal))
        {
            defaultValue = ParseExpression();
        }

        return new Parameter(From(start), type, name, defaultValue, isParams);
    }

    #endregion

    #region Types

    private bool StartsType() => Current.Kind is TokenKind.Identifier or TokenKind.OpenParen or TokenKind.FnKeyword;

    public TypeSyntax ParseType()
    {
        var start = _pos;
        TypeSyntax type;
        if (At(TokenKind.FnKeyword))
        {
            Next();
            Expect(TokenKind.OpenParen, "'('");
            var parameters = ParseSeparated(TokenKind.CloseParen, ParseType);
            Expect(TokenKind.CloseParen, "')'");
            Expect(TokenKind.ThinArrow, "'->'");
            var returns = ParseType();
            type = new FunctionType(From(start), parameters, returns);
        }
        else if (At(TokenKind.OpenParen))
        {
            Next();
            var elements = ParseSeparated(TokenKind.CloseParen, ParseType);
            Expect(TokenKind.CloseParen, "')'");
            if (elements.Length < 2)
            {
                _diagnostics.Report(DiagnosticCodes.ExpectedType, _source, From(start), "tuple with fewer than two elements");
            }

            type = new TupleType(From(start), elements);
        }
        else if (At(TokenKind.Identifier))
        {
            var name = ParseQualifiedName();
            var typeArgs = ImmutableArray<TypeSyntax>.Empty;
            if (At(TokenKind.Less))
            {
                Next();
                typeArgs = ParseSeparated(TokenKind.Greater, ParseType);
                Expect(TokenKind.Greater, "'>'");
            }

            type = new NamedType(From(start), name, typeArgs);
        }
        else
        {
            _diagnostics.Report(DiagnosticCodes.ExpectedType, _source, Current.Span, Current);
            return new NamedType(new TextSpan(Current.Span.Start, 0), new QualifiedName(new TextSpan(Current.Span.Start, 0), ["<missing>"]), []);
        }

        while (true)
        {
            if (At(TokenKind.OpenBracket) && Peek(1).Kind == TokenKind.CloseBracket)
            {
                Next();
                Next();
                type = new ArrayType(From(start), type);
            }
            else if (At(TokenKind.Question))
            {
                Next();
                type = new NullableType(From(start), type);
            }
            else
            {
                return type;
            }
        }
    }

    /// <summary>In expression context: parses <c>&lt;T, U&gt;</c> only if what follows makes it unambiguously a type-argument list.</summary>
    private ImmutableArray<TypeSyntax> TryParseTypeArgumentsInExpression()
    {
        if (!At(TokenKind.Less))
        {
            return [];
        }

        return Try(() =>
        {
            Next();
            var args = ParseSeparated(TokenKind.Greater, ParseType);
            if (!Match(TokenKind.Greater))
            {
                return null;
            }

            return Current.Kind is TokenKind.OpenParen or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace
                or TokenKind.Colon or TokenKind.Semicolon or TokenKind.Comma or TokenKind.Dot or TokenKind.Question
                or TokenKind.QuestionDot or TokenKind.EqualEqual or TokenKind.BangEqual or TokenKind.Pipe or TokenKind.Caret
                or TokenKind.AmpAmp or TokenKind.PipePipe or TokenKind.Amp or TokenKind.OpenBracket or TokenKind.EndOfFile
                ? new TypeArgumentList(args)
                : null;
        }, out var list) ? list.Arguments : [];
    }

    private sealed record TypeArgumentList(ImmutableArray<TypeSyntax> Arguments);

    #endregion

    #region Statements

    public BlockStmt ParseBlock()
    {
        var start = _pos;
        Expect(TokenKind.OpenBrace, "'{'");
        var statements = ImmutableArray.CreateBuilder<Stmt>();
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            var before = _pos;
            statements.Add(ParseStatement());
            if (_pos == before)
            {
                Next();
            }
        }

        Expect(TokenKind.CloseBrace, "'}'");
        return new BlockStmt(From(start), statements.ToImmutable());
    }

    public Stmt ParseStatement()
    {
        var start = _pos;
        switch (Current.Kind)
        {
            case TokenKind.OpenBrace:
                return ParseBlock();
            case TokenKind.Semicolon:
                Next();
                return new EmptyStmt(From(start));
            case TokenKind.IfKeyword:
            {
                Next();
                Expect(TokenKind.OpenParen, "'('");
                var condition = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                var then = ParseStatement();
                var otherwise = Match(TokenKind.ElseKeyword) ? ParseStatement() : null;
                return new IfStmt(From(start), condition, then, otherwise);
            }

            case TokenKind.WhileKeyword:
            {
                Next();
                Expect(TokenKind.OpenParen, "'('");
                var condition = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                return new WhileStmt(From(start), condition, ParseStatement());
            }

            case TokenKind.DoKeyword:
            {
                Next();
                var body = ParseStatement();
                Expect(TokenKind.WhileKeyword, "'while'");
                Expect(TokenKind.OpenParen, "'('");
                var condition = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                Expect(TokenKind.Semicolon, "';'");
                return new DoWhileStmt(From(start), body, condition);
            }

            case TokenKind.ForKeyword:
                return ParseFor();
            case TokenKind.ForeachKeyword:
                return ParseForeach(start, isAwait: false);
            case TokenKind.AwaitKeyword when Peek(1).Kind == TokenKind.ForeachKeyword:
                Next();
                return ParseForeach(start, isAwait: true);
            case TokenKind.SwitchKeyword:
                return ParseSwitchStatement();
            case TokenKind.BreakKeyword:
                Next();
                Expect(TokenKind.Semicolon, "';'");
                return new BreakStmt(From(start));
            case TokenKind.ContinueKeyword:
                Next();
                Expect(TokenKind.Semicolon, "';'");
                return new ContinueStmt(From(start));
            case TokenKind.ReturnKeyword:
            {
                Next();
                var value = At(TokenKind.Semicolon) ? null : ParseExpression();
                Expect(TokenKind.Semicolon, "';'");
                return new ReturnStmt(From(start), value);
            }

            case TokenKind.ThrowKeyword:
            {
                Next();
                var value = At(TokenKind.Semicolon) ? null : ParseExpression();
                Expect(TokenKind.Semicolon, "';'");
                return new ThrowStmt(From(start), value);
            }

            case TokenKind.TryKeyword:
                return ParseTry();
            case TokenKind.YieldKeyword:
            {
                Next();
                if (Match(TokenKind.BreakKeyword))
                {
                    Expect(TokenKind.Semicolon, "';'");
                    return new YieldBreakStmt(From(start));
                }

                Expect(TokenKind.ReturnKeyword, "'return' or 'break'");
                var value = ParseExpression();
                Expect(TokenKind.Semicolon, "';'");
                return new YieldReturnStmt(From(start), value);
            }

            case TokenKind.VarKeyword when Peek(1).Kind == TokenKind.OpenParen:
                return ParseDeconstruction();
            case TokenKind.VarKeyword:
            case TokenKind.ConstKeyword:
            {
                var declaration = ParseLocalDeclaration();
                Expect(TokenKind.Semicolon, "';'");
                return declaration with { Span = From(start) };
            }
        }

        if (IsLocalDeclarationStart())
        {
            var declaration = ParseLocalDeclaration();
            Expect(TokenKind.Semicolon, "';'");
            return declaration with { Span = From(start) };
        }

        var expression = ParseExpression();
        Expect(TokenKind.Semicolon, "';'");
        if (expression is not (AssignmentExpr or InvocationExpr or AwaitExpr or NewExpr or ErrorExpr
            or UnaryExpr { Operator: UnaryOperator.PreIncrement or UnaryOperator.PreDecrement or UnaryOperator.PostIncrement or UnaryOperator.PostDecrement }))
        {
            _diagnostics.Report(DiagnosticCodes.InvalidStatement, _source, expression.Span);
        }

        return new ExprStmt(From(start), expression);
    }

    /// <summary><c>Type name [= …]</c> — distinguished from an expression statement by speculative parsing.</summary>
    private bool IsLocalDeclarationStart()
    {
        if (!StartsType())
        {
            return false;
        }

        var saved = _pos;
        var ok = Try<Parser>(() =>
        {
            ParseType();
            return At(TokenKind.Identifier) && Peek(1).Kind is TokenKind.Equal or TokenKind.Semicolon or TokenKind.Comma or TokenKind.InKeyword
                ? this
                : null;
        }, out _);
        _pos = saved;
        return ok;
    }

    private LocalDeclStmt ParseLocalDeclaration()
    {
        var start = _pos;
        var isConst = Match(TokenKind.ConstKeyword);
        TypeSyntax? type = null;
        if (!Match(TokenKind.VarKeyword))
        {
            type = ParseType();
        }

        var declarators = ImmutableArray.CreateBuilder<VariableDeclarator>();
        do
        {
            var declStart = _pos;
            var name = ExpectIdentifier("a variable name");
            var initializer = Match(TokenKind.Equal) ? ParseExpression() : null;
            declarators.Add(new VariableDeclarator(From(declStart), name, initializer));
        }
        while (Match(TokenKind.Comma));

        return new LocalDeclStmt(From(start), isConst, type, declarators.ToImmutable());
    }

    private DeconstructionStmt ParseDeconstruction()
    {
        var start = _pos;
        Next(); // var
        Expect(TokenKind.OpenParen, "'('");
        var names = ParseSeparated(TokenKind.CloseParen, () => ExpectIdentifier());
        Expect(TokenKind.CloseParen, "')'");
        Expect(TokenKind.Equal, "'='");
        var value = ParseExpression();
        Expect(TokenKind.Semicolon, "';'");
        return new DeconstructionStmt(From(start), names, value);
    }

    private ForStmt ParseFor()
    {
        var start = _pos;
        Next();
        Expect(TokenKind.OpenParen, "'('");
        Stmt? initializer = null;
        if (!At(TokenKind.Semicolon))
        {
            var initStart = _pos;
            if (At(TokenKind.VarKeyword) || IsLocalDeclarationStart())
            {
                initializer = ParseLocalDeclaration();
            }
            else
            {
                var expression = ParseExpression();
                initializer = new ExprStmt(From(initStart), expression);
            }
        }

        Expect(TokenKind.Semicolon, "';'");
        var condition = At(TokenKind.Semicolon) ? null : ParseExpression();
        Expect(TokenKind.Semicolon, "';'");
        var iterators = ParseSeparated(TokenKind.CloseParen, ParseExpression);
        Expect(TokenKind.CloseParen, "')'");
        var body = ParseStatement();
        return new ForStmt(From(start), initializer, condition, iterators, body);
    }

    private ForeachStmt ParseForeach(int start, bool isAwait)
    {
        Next(); // foreach
        Expect(TokenKind.OpenParen, "'('");
        TypeSyntax? type = null;
        if (!Match(TokenKind.VarKeyword))
        {
            type = ParseType();
        }

        var variable = ExpectIdentifier("a loop variable");
        Expect(TokenKind.InKeyword, "'in'");
        var collection = ParseExpression();
        Expect(TokenKind.CloseParen, "')'");
        var body = ParseStatement();
        return new ForeachStmt(From(start), isAwait, type, variable, collection, body);
    }

    private SwitchStmt ParseSwitchStatement()
    {
        var start = _pos;
        Next();
        Expect(TokenKind.OpenParen, "'('");
        var subject = ParseExpression();
        Expect(TokenKind.CloseParen, "')'");
        Expect(TokenKind.OpenBrace, "'{'");
        var sections = ImmutableArray.CreateBuilder<SwitchSection>();
        while (At(TokenKind.CaseKeyword) || At(TokenKind.DefaultKeyword))
        {
            var sectionStart = _pos;
            var labels = ImmutableArray.CreateBuilder<SwitchLabel>();
            while (At(TokenKind.CaseKeyword) || At(TokenKind.DefaultKeyword))
            {
                var labelStart = _pos;
                if (Match(TokenKind.DefaultKeyword))
                {
                    Expect(TokenKind.Colon, "':'");
                    labels.Add(new DefaultLabel(From(labelStart)));
                    continue;
                }

                Next(); // case
                var pattern = ParsePattern();
                Expr? guard = null;
                if (AtContextual("when"))
                {
                    Next();
                    guard = ParseExpression();
                }

                Expect(TokenKind.Colon, "':'");
                labels.Add(new CaseLabel(From(labelStart), pattern, guard));
            }

            var statements = ImmutableArray.CreateBuilder<Stmt>();
            while (!At(TokenKind.CaseKeyword) && !At(TokenKind.DefaultKeyword) && !At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
            {
                var before = _pos;
                statements.Add(ParseStatement());
                if (_pos == before)
                {
                    Next();
                }
            }

            sections.Add(new SwitchSection(From(sectionStart), labels.ToImmutable(), statements.ToImmutable()));
        }

        Expect(TokenKind.CloseBrace, "'case', 'default' or '}'");
        return new SwitchStmt(From(start), subject, sections.ToImmutable());
    }

    private TryStmt ParseTry()
    {
        var start = _pos;
        Next();
        var body = ParseBlock();
        var catches = ImmutableArray.CreateBuilder<CatchClause>();
        while (At(TokenKind.CatchKeyword))
        {
            var catchStart = _pos;
            Next();
            TypeSyntax? type = null;
            string? variable = null;
            if (Match(TokenKind.OpenParen))
            {
                type = ParseType();
                if (At(TokenKind.Identifier))
                {
                    variable = Next().Text;
                }

                Expect(TokenKind.CloseParen, "')'");
            }

            catches.Add(new CatchClause(From(catchStart), type, variable, ParseBlock()));
        }

        BlockStmt? finallyBlock = Match(TokenKind.FinallyKeyword) ? ParseBlock() : null;
        if (catches.Count == 0 && finallyBlock is null)
        {
            _diagnostics.Report(DiagnosticCodes.Expected, _source, Current.Span, "'catch' or 'finally'", Current);
        }

        return new TryStmt(From(start), body, catches.ToImmutable(), finallyBlock);
    }

    #endregion

    #region Patterns

    private Pattern ParsePattern()
    {
        var start = _pos;
        if (AtContextual("_") && Peek(1).Kind != TokenKind.Dot)
        {
            Next();
            return new DiscardPattern(From(start));
        }

        if (At(TokenKind.VarKeyword))
        {
            Next();
            return new VarPattern(From(start), ExpectIdentifier("a variable name"));
        }

        if (RelationalOperatorAhead(out var relational, out var tokenCount))
        {
            for (var i = 0; i < tokenCount; i++)
            {
                Next();
            }

            var value = ParseBinary(Precedence.Shift);
            return new RelationalPattern(From(start), relational, value);
        }

        if (StartsType() && Try(() =>
            {
                var type = ParseType();
                return At(TokenKind.Identifier) && Current.Text != "when" ? new TypePattern(From(start), type, Next().Text) : null;
            }, out var typePattern))
        {
            return typePattern;
        }

        var constant = ParseBinary(Precedence.Shift);
        return new ConstantPattern(From(start), constant);
    }

    private bool RelationalOperatorAhead(out BinaryOperator op, out int tokens)
    {
        tokens = 1;
        switch (Current.Kind)
        {
            case TokenKind.Less:
                op = BinaryOperator.Less;
                return true;
            case TokenKind.LessEqual:
                op = BinaryOperator.LessOrEqual;
                return true;
            case TokenKind.Greater when Peek(1).Kind == TokenKind.Equal && Adjacent(Current, Peek(1)):
                op = BinaryOperator.GreaterOrEqual;
                tokens = 2;
                return true;
            case TokenKind.Greater:
                op = BinaryOperator.Greater;
                return true;
            default:
                op = default;
                return false;
        }
    }

    #endregion

    #region Expressions

    private enum Precedence
    {
        Coalesce = 1,
        LogicalOr,
        LogicalAnd,
        BitwiseOr,
        BitwiseXor,
        BitwiseAnd,
        Equality,
        Relational,
        Shift,
        Additive,
        Multiplicative,
    }

    public Expr ParseExpression()
    {
        if (IsLambdaStart())
        {
            return ParseLambda();
        }

        var start = _pos;
        if (At(TokenKind.ThrowKeyword))
        {
            Next();
            return new ThrowExpr(From(start), ParseExpression());
        }

        var left = ParseConditional();
        if (AssignmentOperatorAhead(out var op, out var tokens))
        {
            for (var i = 0; i < tokens; i++)
            {
                Next();
            }

            var right = ParseExpression();
            if (left is not (NameExpr or MemberAccessExpr or ElementAccessExpr or TupleExpr or ErrorExpr))
            {
                _diagnostics.Report(DiagnosticCodes.InvalidAssignmentTarget, _source, left.Span);
            }

            return new AssignmentExpr(From(start), op, left, right);
        }

        return left;
    }

    private bool AssignmentOperatorAhead(out BinaryOperator? op, out int tokens)
    {
        tokens = 1;
        var isAssignment = true;
        op = Current.Kind switch
        {
            TokenKind.Equal => null,
            TokenKind.PlusEqual => BinaryOperator.Add,
            TokenKind.MinusEqual => BinaryOperator.Subtract,
            TokenKind.StarEqual => BinaryOperator.Multiply,
            TokenKind.SlashEqual => BinaryOperator.Divide,
            TokenKind.PercentEqual => BinaryOperator.Modulo,
            TokenKind.AmpEqual => BinaryOperator.BitwiseAnd,
            TokenKind.PipeEqual => BinaryOperator.BitwiseOr,
            TokenKind.CaretEqual => BinaryOperator.BitwiseXor,
            TokenKind.LessLessEqual => BinaryOperator.ShiftLeft,
            TokenKind.QuestionQuestionEqual => BinaryOperator.Coalesce,
            _ => NotAssignment(out isAssignment),
        };

        if (isAssignment)
        {
            return true;
        }

        // '>>=' and '>>>=' arrive as adjacent '>' tokens followed by '='.
        if (At(TokenKind.Greater) && Peek(1).Kind == TokenKind.Greater && Adjacent(Current, Peek(1)))
        {
            if (Peek(2).Kind == TokenKind.Equal && Adjacent(Peek(1), Peek(2)))
            {
                (op, tokens) = (BinaryOperator.ShiftRight, 3);
                return true;
            }

            if (Peek(2).Kind == TokenKind.Greater && Adjacent(Peek(1), Peek(2)) && Peek(3).Kind == TokenKind.Equal && Adjacent(Peek(2), Peek(3)))
            {
                (op, tokens) = (BinaryOperator.ShiftRightUnsigned, 4);
                return true;
            }
        }

        op = null;
        return false;
    }

    private static BinaryOperator? NotAssignment(out bool isAssignment)
    {
        isAssignment = false;
        return null;
    }

    private Expr ParseConditional()
    {
        var start = _pos;
        var condition = ParseBinary(Precedence.Coalesce);
        if (!At(TokenKind.Question))
        {
            return condition;
        }

        Next();
        var whenTrue = ParseExpression();
        Expect(TokenKind.Colon, "':'");
        var whenFalse = ParseExpression();
        return new ConditionalExpr(From(start), condition, whenTrue, whenFalse);
    }

    private Expr ParseBinary(Precedence minimum)
    {
        var start = _pos;
        var left = ParseUnary();
        while (true)
        {
            if (At(TokenKind.IsKeyword) && minimum <= Precedence.Relational)
            {
                Next();
                left = new IsPatternExpr(From(start), left, ParsePattern());
                continue;
            }

            if (!BinaryOperatorAhead(out var op, out var precedence, out var tokens) || precedence < minimum)
            {
                return left;
            }

            for (var i = 0; i < tokens; i++)
            {
                Next();
            }

            // '??' is right-associative; everything else is left-associative.
            var right = op == BinaryOperator.Coalesce ? ParseBinary(precedence) : ParseBinary(precedence + 1);
            left = new BinaryExpr(From(start), op, left, right);
        }
    }

    private bool BinaryOperatorAhead(out BinaryOperator op, out Precedence precedence, out int tokens)
    {
        tokens = 1;
        (op, precedence) = Current.Kind switch
        {
            TokenKind.Star => (BinaryOperator.Multiply, Precedence.Multiplicative),
            TokenKind.Slash => (BinaryOperator.Divide, Precedence.Multiplicative),
            TokenKind.Percent => (BinaryOperator.Modulo, Precedence.Multiplicative),
            TokenKind.Plus => (BinaryOperator.Add, Precedence.Additive),
            TokenKind.Minus => (BinaryOperator.Subtract, Precedence.Additive),
            TokenKind.LessLess => (BinaryOperator.ShiftLeft, Precedence.Shift),
            TokenKind.Less => (BinaryOperator.Less, Precedence.Relational),
            TokenKind.LessEqual => (BinaryOperator.LessOrEqual, Precedence.Relational),
            TokenKind.EqualEqual => (BinaryOperator.Equal, Precedence.Equality),
            TokenKind.BangEqual => (BinaryOperator.NotEqual, Precedence.Equality),
            TokenKind.Amp => (BinaryOperator.BitwiseAnd, Precedence.BitwiseAnd),
            TokenKind.Caret => (BinaryOperator.BitwiseXor, Precedence.BitwiseXor),
            TokenKind.Pipe => (BinaryOperator.BitwiseOr, Precedence.BitwiseOr),
            TokenKind.AmpAmp => (BinaryOperator.LogicalAnd, Precedence.LogicalAnd),
            TokenKind.PipePipe => (BinaryOperator.LogicalOr, Precedence.LogicalOr),
            TokenKind.QuestionQuestion => (BinaryOperator.Coalesce, Precedence.Coalesce),
            _ => (default(BinaryOperator), (Precedence)0),
        };

        if (precedence != 0)
        {
            return true;
        }

        if (!At(TokenKind.Greater))
        {
            return false;
        }

        // Combine adjacent '>' tokens: '>>>', '>>', '>=' — but never swallow a compound assignment.
        var g1 = Peek(1);
        if (g1.Kind == TokenKind.Greater && Adjacent(Current, g1))
        {
            var g2 = Peek(2);
            if (g2.Kind == TokenKind.Greater && Adjacent(g1, g2))
            {
                if (Peek(3).Kind == TokenKind.Equal && Adjacent(g2, Peek(3)))
                {
                    return false;
                }

                (op, precedence, tokens) = (BinaryOperator.ShiftRightUnsigned, Precedence.Shift, 3);
                return true;
            }

            if (g2.Kind == TokenKind.Equal && Adjacent(g1, g2))
            {
                return false;
            }

            (op, precedence, tokens) = (BinaryOperator.ShiftRight, Precedence.Shift, 2);
            return true;
        }

        if (g1.Kind == TokenKind.Equal && Adjacent(Current, g1))
        {
            (op, precedence, tokens) = (BinaryOperator.GreaterOrEqual, Precedence.Relational, 2);
            return true;
        }

        (op, precedence) = (BinaryOperator.Greater, Precedence.Relational);
        return true;
    }

    private Expr ParseUnary()
    {
        var start = _pos;
        UnaryOperator? op = Current.Kind switch
        {
            TokenKind.Plus => UnaryOperator.Plus,
            TokenKind.Minus => UnaryOperator.Negate,
            TokenKind.Bang => UnaryOperator.LogicalNot,
            TokenKind.Tilde => UnaryOperator.BitwiseNot,
            TokenKind.PlusPlus => UnaryOperator.PreIncrement,
            TokenKind.MinusMinus => UnaryOperator.PreDecrement,
            _ => null,
        };

        Expr result;
        if (op is { } unary)
        {
            Next();
            result = new UnaryExpr(From(start), unary, ParseUnary());
        }
        else if (At(TokenKind.AwaitKeyword))
        {
            Next();
            result = new AwaitExpr(From(start), ParseUnary());
        }
        else if (At(TokenKind.OpenParen) && Try(ParseCast, out var cast))
        {
            result = cast;
        }
        else
        {
            result = ParsePostfix(start, ParsePrimary());
        }

        while (At(TokenKind.SwitchKeyword))
        {
            result = ParseSwitchExpression(start, result);
        }

        return result;
    }

    private CastExpr? ParseCast()
    {
        var start = _pos;
        Next(); // (
        var type = ParseType();
        if (!Match(TokenKind.CloseParen))
        {
            return null;
        }

        var isPredefined = type is NamedType { TypeArguments.IsEmpty: true } named && named.Name.Parts.Length == 1
                           && SyntaxFacts.PredefinedTypes.Contains(named.Name.Parts[0]);
        var castable = Current.Kind is TokenKind.Identifier or TokenKind.IntLiteral or TokenKind.FloatLiteral or TokenKind.CharLiteral
                or TokenKind.StringLiteral or TokenKind.BytesLiteral or TokenKind.InterpolatedString or TokenKind.OpenParen
                or TokenKind.Bang or TokenKind.Tilde or TokenKind.ThisKeyword or TokenKind.BaseKeyword or TokenKind.NewKeyword
                or TokenKind.TypeofKeyword or TokenKind.NullKeyword or TokenKind.TrueKeyword or TokenKind.FalseKeyword
                or TokenKind.AwaitKeyword or TokenKind.OpenBracket
            || (isPredefined && Current.Kind is TokenKind.Minus or TokenKind.Plus or TokenKind.PlusPlus or TokenKind.MinusMinus);
        if (!castable)
        {
            return null;
        }

        var operand = ParseUnary();
        return new CastExpr(From(start), type, operand);
    }

    private Expr ParseSwitchExpression(int start, Expr subject)
    {
        Next(); // switch
        Expect(TokenKind.OpenBrace, "'{'");
        var arms = ImmutableArray.CreateBuilder<SwitchArm>();
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            var armStart = _pos;
            var pattern = ParsePattern();
            Expr? guard = null;
            if (AtContextual("when"))
            {
                Next();
                guard = ParseExpression();
            }

            Expect(TokenKind.FatArrow, "'=>'");
            var result = ParseExpression();
            arms.Add(new SwitchArm(From(armStart), pattern, guard, result));
            if (!Match(TokenKind.Comma))
            {
                break;
            }
        }

        Expect(TokenKind.CloseBrace, "'}'");
        return new SwitchExpr(From(start), subject, arms.ToImmutable());
    }

    private Expr ParsePrimary()
    {
        var start = _pos;
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.IntLiteral:
                Next();
                return new LiteralExpr(token.Span, LiteralKind.Int, token.Value);
            case TokenKind.FloatLiteral:
                Next();
                return new LiteralExpr(token.Span, LiteralKind.Float, token.Value);
            case TokenKind.CharLiteral:
                Next();
                return new LiteralExpr(token.Span, LiteralKind.Char, token.Value);
            case TokenKind.StringLiteral:
                Next();
                return new LiteralExpr(token.Span, LiteralKind.String, token.Value);
            case TokenKind.BytesLiteral:
                Next();
                return new LiteralExpr(token.Span, LiteralKind.Bytes, token.Value);
            case TokenKind.TrueKeyword:
            case TokenKind.FalseKeyword:
                Next();
                return new LiteralExpr(token.Span, LiteralKind.Bool, token.Kind == TokenKind.TrueKeyword);
            case TokenKind.NullKeyword:
                Next();
                return new LiteralExpr(token.Span, LiteralKind.Null, null);
            case TokenKind.InterpolatedString:
                Next();
                return ParseInterpolation(token);
            case TokenKind.ThisKeyword:
                Next();
                return new ThisExpr(token.Span);
            case TokenKind.BaseKeyword:
                Next();
                return new BaseExpr(token.Span);
            case TokenKind.Identifier:
            {
                Next();
                var typeArgs = TryParseTypeArgumentsInExpression();
                return new NameExpr(From(start), token.Text, typeArgs);
            }

            case TokenKind.TypeofKeyword:
            {
                Next();
                Expect(TokenKind.OpenParen, "'('");
                var type = ParseType();
                Expect(TokenKind.CloseParen, "')'");
                return new TypeofExpr(From(start), type);
            }

            case TokenKind.NewKeyword:
                return ParseNew();
            case TokenKind.OpenBracket:
            {
                Next();
                var elements = ParseSeparated(TokenKind.CloseBracket, ParseExpression);
                Expect(TokenKind.CloseBracket, "']'");
                return new ArrayLiteralExpr(From(start), elements);
            }

            case TokenKind.OpenParen:
            {
                Next();
                var first = ParseExpression();
                if (!At(TokenKind.Comma))
                {
                    Expect(TokenKind.CloseParen, "')'");
                    return first;
                }

                var elements = ImmutableArray.CreateBuilder<Expr>();
                elements.Add(first);
                while (Match(TokenKind.Comma))
                {
                    elements.Add(ParseExpression());
                }

                Expect(TokenKind.CloseParen, "')'");
                return new TupleExpr(From(start), elements.ToImmutable());
            }
        }

        _diagnostics.Report(DiagnosticCodes.ExpectedExpression, _source, token.Span, token);
        if (token.Kind is not (TokenKind.Semicolon or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace
            or TokenKind.Comma or TokenKind.EndOfFile))
        {
            Next();
        }

        return new ErrorExpr(From(start));
    }

    private Expr ParseNew()
    {
        var start = _pos;
        Next(); // new
        var type = ParseType();
        if (At(TokenKind.OpenBracket))
        {
            Next();
            var length = ParseExpression();
            Expect(TokenKind.CloseBracket, "']'");
            return new NewArrayExpr(From(start), type, length);
        }

        var hasArguments = At(TokenKind.OpenParen);
        var args = hasArguments ? ParseArgumentList() : [];
        var initializers = ImmutableArray<AssignmentExpr>.Empty;
        if (At(TokenKind.OpenBrace))
        {
            Next();
            initializers = ParseSeparated(TokenKind.CloseBrace, () =>
            {
                var initStart = _pos;
                var nameStart = _pos;
                var name = ExpectIdentifier("a member name");
                var target = new NameExpr(From(nameStart), name, []);
                Expect(TokenKind.Equal, "'='");
                return new AssignmentExpr(From(initStart), null, target, ParseExpression());
            });
            Expect(TokenKind.CloseBrace, "'}'");
        }
        else if (!hasArguments)
        {
            Expect(TokenKind.OpenParen, "'(', '[' or '{'");
        }

        return new NewExpr(From(start), type, args, initializers);
    }

    private Expr ParsePostfix(int start, Expr expr)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.Dot:
                case TokenKind.QuestionDot:
                {
                    var conditional = Next().Kind == TokenKind.QuestionDot;
                    var name = ExpectIdentifier("a member name");
                    var typeArgs = TryParseTypeArgumentsInExpression();
                    expr = new MemberAccessExpr(From(start), expr, name, typeArgs, conditional);
                    break;
                }

                case TokenKind.OpenParen:
                {
                    var args = ParseArgumentList();
                    expr = new InvocationExpr(From(start), expr, args);
                    break;
                }

                case TokenKind.OpenBracket:
                {
                    Next();
                    var indices = ParseSeparated(TokenKind.CloseBracket, ParseExpression);
                    Expect(TokenKind.CloseBracket, "']'");
                    expr = new ElementAccessExpr(From(start), expr, indices, false);
                    break;
                }

                case TokenKind.PlusPlus:
                    Next();
                    expr = new UnaryExpr(From(start), UnaryOperator.PostIncrement, expr);
                    break;
                case TokenKind.MinusMinus:
                    Next();
                    expr = new UnaryExpr(From(start), UnaryOperator.PostDecrement, expr);
                    break;
                case TokenKind.Bang:
                    Next();
                    expr = new UnaryExpr(From(start), UnaryOperator.NullForgiving, expr);
                    break;
                default:
                    return expr;
            }
        }
    }

    private ImmutableArray<Argument> ParseArgumentList()
    {
        Expect(TokenKind.OpenParen, "'('");
        var args = ParseSeparated(TokenKind.CloseParen, () =>
        {
            var start = _pos;
            string? name = null;
            if (At(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
            {
                name = Next().Text;
                Next();
            }

            var value = ParseExpression();
            return new Argument(From(start), name, value);
        });
        Expect(TokenKind.CloseParen, "')'");
        return args;
    }

    private bool IsLambdaStart()
    {
        var offset = At(TokenKind.AsyncKeyword) ? 1 : 0;
        var first = Peek(offset);
        if (first.Kind == TokenKind.Identifier && Peek(offset + 1).Kind == TokenKind.FatArrow)
        {
            return true;
        }

        if (first.Kind != TokenKind.OpenParen)
        {
            return false;
        }

        var depth = 0;
        for (var i = offset; ; i++)
        {
            var t = Peek(i);
            switch (t.Kind)
            {
                case TokenKind.OpenParen:
                    depth++;
                    break;
                case TokenKind.CloseParen when --depth == 0:
                    return Peek(i + 1).Kind == TokenKind.FatArrow;
                case TokenKind.EndOfFile:
                case TokenKind.Semicolon:
                case TokenKind.OpenBrace:
                case TokenKind.CloseBrace:
                    return false;
            }
        }
    }

    private LambdaExpr ParseLambda()
    {
        var start = _pos;
        var isAsync = Match(TokenKind.AsyncKeyword);
        var parameters = ImmutableArray.CreateBuilder<LambdaParameter>();
        if (At(TokenKind.Identifier))
        {
            var token = Next();
            parameters.Add(new LambdaParameter(token.Span, null, token.Text));
        }
        else
        {
            Expect(TokenKind.OpenParen, "'('");
            parameters.AddRange(ParseSeparated(TokenKind.CloseParen, () =>
            {
                var paramStart = _pos;
                if (At(TokenKind.Identifier) && Peek(1).Kind is TokenKind.Comma or TokenKind.CloseParen)
                {
                    return new LambdaParameter(Next().Span, null, Previous.Text);
                }

                var type = ParseType();
                var name = ExpectIdentifier("a parameter name");
                return new LambdaParameter(From(paramStart), type, name);
            }));
            Expect(TokenKind.CloseParen, "')'");
        }

        Expect(TokenKind.FatArrow, "'=>'");
        if (At(TokenKind.OpenBrace))
        {
            var block = ParseBlock();
            return new LambdaExpr(From(start), isAsync, parameters.ToImmutable(), null, block);
        }

        var body = ParseExpression();
        return new LambdaExpr(From(start), isAsync, parameters.ToImmutable(), body, null);
    }

    private InterpolatedStringExpr ParseInterpolation(Token token)
    {
        var segments = ImmutableArray.CreateBuilder<InterpolationSegment>();
        foreach (var part in token.Parts)
        {
            if (!part.IsHole)
            {
                segments.Add(new TextSegment(part.Span, part.Text));
                continue;
            }

            var holeTokens = new Lexer(_source, _diagnostics, part.Span.Start, part.Span.End).LexAll();
            var holeParser = new Parser(_source, holeTokens, _diagnostics);
            var expression = holeParser.ParseExpression();
            if (!holeParser.At(TokenKind.EndOfFile))
            {
                _diagnostics.Report(DiagnosticCodes.Expected, _source, holeParser.Current.Span, "'}'", holeParser.Current);
            }

            segments.Add(new HoleSegment(part.Span, expression, part.Format));
        }

        return new InterpolatedStringExpr(token.Span, segments.ToImmutable());
    }

    #endregion

    /// <summary>Parses a comma-separated list until <paramref name="terminator"/> (not consumed).</summary>
    private ImmutableArray<T> ParseSeparated<T>(TokenKind terminator, Func<T> parseItem)
    {
        var list = ImmutableArray.CreateBuilder<T>();
        if (At(terminator))
        {
            return list.ToImmutable();
        }

        while (true)
        {
            var before = _pos;
            list.Add(parseItem());
            if (!Match(TokenKind.Comma) || At(terminator) || _pos == before)
            {
                return list.ToImmutable();
            }
        }
    }
}
