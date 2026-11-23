using System.Text.Json;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Xunit;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Tests.Support;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Tests;

public class ParserTests
{
    private static (Parser Parser, DiagnosticBag Diagnostics) ParserFor(string text)
    {
        var source = new SourceText(text);
        var diagnostics = new DiagnosticBag();
        return (new Parser(source, Lexer.Tokenize(source, diagnostics), diagnostics), diagnostics);
    }

    private static string Expr(string text)
    {
        var (parser, diagnostics) = ParserFor(text);
        var expr = parser.ParseExpression();
        Assert.Empty(diagnostics);
        return AstPrinter.Print(expr);
    }

    private static string Stmt(string text)
    {
        var (parser, diagnostics) = ParserFor(text);
        var stmt = parser.ParseStatement();
        Assert.Empty(diagnostics);
        return AstPrinter.Print(stmt);
    }

    private static SyntaxTree Module(string body, string header = "module Test;") => SyntaxTree.Parse(header + "\n" + body);

    #region Expressions

    [Theory]
    [InlineData("1 + 2 * 3", "(Binary Add (Literal Int 1) (Binary Multiply (Literal Int 2) (Literal Int 3)))")]
    [InlineData("a - b - c", "(Binary Subtract (Binary Subtract (Name \"a\") (Name \"b\")) (Name \"c\"))")]
    [InlineData("a ?? b ?? c", "(Binary Coalesce (Name \"a\") (Binary Coalesce (Name \"b\") (Name \"c\")))")]
    [InlineData("a || b && c", "(Binary LogicalOr (Name \"a\") (Binary LogicalAnd (Name \"b\") (Name \"c\")))")]
    [InlineData("x & 0xFF == y", "(Binary BitwiseAnd (Name \"x\") (Binary Equal (Literal Int 255) (Name \"y\")))")]
    [InlineData("x >> 2 >>> 1", "(Binary ShiftRightUnsigned (Binary ShiftRight (Name \"x\") (Literal Int 2)) (Literal Int 1))")]
    [InlineData("a >= b", "(Binary GreaterOrEqual (Name \"a\") (Name \"b\"))")]
    [InlineData("-x * !y", "(Binary Multiply (Unary Negate (Name \"x\")) (Unary LogicalNot (Name \"y\")))")]
    [InlineData("c ? a : b", "(Conditional (Name \"c\") (Name \"a\") (Name \"b\"))")]
    public void Operator_precedence_and_associativity(string source, string expected) => Assert.Equal(expected, Expr(source));

    [Theory]
    [InlineData("x = y = 3", "(Assignment (Name \"x\") (Assignment (Name \"y\") (Literal Int 3)))")]
    [InlineData("i += Hardware.MaxBatch", "(Assignment Add (Name \"i\") (MemberAccess (Name \"Hardware\") \"MaxBatch\"))")]
    [InlineData("x >>= 1", "(Assignment ShiftRight (Name \"x\") (Literal Int 1))")]
    [InlineData("cache ??= new Map<string, int>()", "(Assignment Coalesce (Name \"cache\") (New <Map<string, int>>))")]
    public void Assignments_are_right_associative(string source, string expected) => Assert.Equal(expected, Expr(source));

    [Fact]
    public void Generic_invocation_versus_comparison()
    {
        Assert.Equal("(Invocation (Name \"Max\" [<int>]) [(Argument (Literal Int 1)) (Argument (Literal Int 2))])", Expr("Max<int>(1, 2)"));
        Assert.Equal(
            "(Invocation (Name \"f\") [(Argument (Binary Less (Name \"a\") (Name \"b\"))) (Argument (Binary Greater (Name \"c\") (Name \"d\")))])",
            Expr("f(a < b, c > d)"));
        Assert.Equal("(New <Map<string, List<int>>>)", Expr("new Map<string, List<int>>()"));
    }

    [Fact]
    public void Casts_versus_parenthesized_expressions()
    {
        Assert.Equal("(Cast <int> (Unary Negate (Literal Int 1)))", Expr("(int)-1"));
        Assert.Equal("(Cast <Packet> (Name \"obj\"))", Expr("(Packet)obj"));
        Assert.Equal("(Binary Subtract (Name \"a\") (Name \"b\"))", Expr("(a) - b"));
        Assert.Equal("(Tuple [(Name \"a\") (Literal String \"b\")])", Expr("(a, \"b\")"));
    }

    [Theory]
    [InlineData("x => x * 2", "(Lambda [(LambdaParameter \"x\")] (Binary Multiply (Name \"x\") (Literal Int 2)))")]
    [InlineData("(a, b) => a + b", "(Lambda [(LambdaParameter \"a\") (LambdaParameter \"b\")] (Binary Add (Name \"a\") (Name \"b\")))")]
    [InlineData("(int x) => x", "(Lambda [(LambdaParameter <int> \"x\")] (Name \"x\"))")]
    [InlineData("() => 0", "(Lambda (Literal Int 0))")]
    public void Lambdas(string source, string expected) => Assert.Equal(expected, Expr(source));

    [Fact]
    public void Async_lambda_with_block_and_await()
    {
        Assert.Equal(
            "(Lambda True [(LambdaParameter \"s\") (LambdaParameter \"e\")] (Block [(Expr (Await (Invocation (MemberAccess (Name \"Net\") \"ProbeAsync\") [(Argument (Name \"ip\"))])))]))",
            Expr("async (s, e) => { await Net.ProbeAsync(ip); }"));
    }

    [Fact]
    public void Postfix_chains_conditional_access_and_null_forgiving()
    {
        Assert.Equal(
            "(Binary Coalesce (MemberAccess (ElementAccess (Name \"items\") [(Literal Int 0)]) \"Banner\" True) (Literal String \"\"))",
            Expr("items[0]?.Banner ?? \"\""));
        Assert.Equal("(MemberAccess (Unary NullForgiving (Name \"maybe\")) \"Length\")", Expr("maybe!.Length"));
        Assert.Equal("(Unary PostIncrement (Name \"sent\"))", Expr("sent++"));
    }

    [Fact]
    public void Switch_expression_with_patterns()
    {
        Assert.Equal(
            "(Switch (Name \"mode\") [(SwitchArm (Constant (MemberAccess (Name \"TransferMode\") \"CPU\")) (Literal String \"polled\")) (SwitchArm (Relational Less (Literal Int 5)) (Literal String \"small\")) (SwitchArm (Type <Packet> \"p\") (MemberAccess (Name \"p\") \"Port\")) (SwitchArm (Discard) (Literal String \"?\"))])",
            Expr("mode switch { TransferMode.CPU => \"polled\", < 5 => \"small\", Packet p => p.Port, _ => \"?\" }"));
    }

    [Fact]
    public void Is_patterns()
    {
        Assert.Equal("(Binary LogicalAnd (IsPattern (Name \"x\") (Type <Packet> \"p\")) (Binary Greater (MemberAccess (Name \"p\") \"Port\") (Literal Int 0)))",
            Expr("x is Packet p && p.Port > 0"));
        Assert.Equal("(IsPattern (Name \"x\") (Constant (Literal Null)))", Expr("x is null"));
        Assert.Equal("(IsPattern (Name \"b\") (Relational GreaterOrEqual (Literal Int 128)))", Expr("b is >= 0x80"));
    }

    [Fact]
    public void Creation_expressions()
    {
        Assert.Equal("(NewArray <int> (Literal Int 5))", Expr("new int[5]"));
        Assert.Equal("(ArrayLiteral [(Literal Int 21) (Literal Int 80)])", Expr("[21, 80]"));
        Assert.Equal("(New <Packet> [(Assignment (Name \"Port\") (Literal Int 2121))])", Expr("new Packet { Port = 2121 }"));
        Assert.Equal("(New <Form> [(Argument (Literal String \"x\"))])", Expr("new Form(\"x\")"));
        Assert.Equal("(Invocation (Name \"Send\") [(Argument \"timeout\" (Literal Int 3))])", Expr("Send(timeout: 3)"));
    }

    [Fact]
    public void Interpolated_string_holes_are_parsed_as_expressions()
    {
        Assert.Equal(
            "(InterpolatedString [(Hole (Name \"port\")) (Text \" \") (Hole (MemberAccess (Name \"state\") \"Status\") \"G\")])",
            Expr("$\"{port} {state.Status:G}\""));
    }

    #endregion

    #region Statements

    [Theory]
    [InlineData("int sent = 0;", "(LocalDecl <int> [(VariableDeclarator \"sent\" (Literal Int 0))])")]
    [InlineData("List<List<int>> grid = null;", "(LocalDecl <List<List<int>>> [(VariableDeclarator \"grid\" (Literal Null))])")]
    [InlineData("Packet[]? batch;", "(LocalDecl <Packet[]?> [(VariableDeclarator \"batch\")])")]
    [InlineData("var x = 1, y = 2;", "(LocalDecl [(VariableDeclarator \"x\" (Literal Int 1)) (VariableDeclarator \"y\" (Literal Int 2))])")]
    [InlineData("const int Max = 32;", "(LocalDecl True <int> [(VariableDeclarator \"Max\" (Literal Int 32))])")]
    [InlineData("var (code, msg) = Probe();", "(Deconstruction [\"code\" \"msg\"] (Invocation (Name \"Probe\")))")]
    [InlineData("fn(int) -> int square = null;", "(LocalDecl <fn(int) -> int> [(VariableDeclarator \"square\" (Literal Null))])")]
    [InlineData("(int, string) pair = (1, \"a\");", "(LocalDecl <(int, string)> [(VariableDeclarator \"pair\" (Tuple [(Literal Int 1) (Literal String \"a\")]))])")]
    public void Local_declarations(string source, string expected) => Assert.Equal(expected, Stmt(source));

    [Fact]
    public void Expression_statements_are_not_mistaken_for_declarations()
    {
        Assert.StartsWith("(Expr (Invocation (MemberAccess (Name \"Hardware\") \"SendPacket\")", Stmt("Hardware.SendPacket(messages[i]);"));
        Assert.StartsWith("(Expr (Assignment", Stmt("disk.Mode = TransferMode.CPU;"));
    }

    [Fact]
    public void Control_flow_statements()
    {
        Assert.Equal(
            "(For (LocalDecl <int> [(VariableDeclarator \"i\" (Literal Int 0))]) (Binary Less (Name \"i\") (MemberAccess (Name \"messages\") \"Length\")) [(Assignment (Name \"i\") (Binary Add (Name \"i\") (Literal Int 1)))] (Block [(Expr (Unary PostIncrement (Name \"sent\")))]))",
            Stmt("for (int i = 0; i < messages.Length; i = i + 1) { sent++; }"));
        Assert.Equal("(Foreach True \"chunk\" (Name \"stream\") (Block))", Stmt("await foreach (var chunk in stream) { }"));
        Assert.StartsWith("(Try (Block) [(CatchClause <AccessDenied> \"e\" (Block))] (Block))", Stmt("try { } catch (AccessDenied e) { } finally { }"));
        Assert.Equal("(DoWhile (Block) (Name \"busy\"))", Stmt("do { } while (busy);"));
        Assert.Equal("(YieldReturn (Name \"node\"))", Stmt("yield return node;"));
    }

    [Fact]
    public void Switch_statement_sections()
    {
        Assert.Equal(
            "(Switch (Name \"port\") [(SwitchSection [(CaseLabel (Constant (Literal Int 21))) (CaseLabel (Constant (Literal Int 2121)))] [(Return (Literal String \"ftp\"))]) (SwitchSection [(CaseLabel (Type <Packet> \"p\") (Binary Equal (MemberAccess (Name \"p\") \"Port\") (Literal Int 0)))] [(Break)]) (SwitchSection [(DefaultLabel)] [(Return (Literal Null))])])",
            Stmt("switch (port) { case 21: case 2121: return \"ftp\"; case Packet p when p.Port == 0: break; default: return null; }"));
    }

    #endregion

    #region Declarations

    [Fact]
    public void Class_with_every_member_kind()
    {
        var tree = Module("""
            class LruCache<K, V> : Collection where K : Hashable {
                private Map<K, V> items = new Map<K, V>();
                int Count { get => items.Count; }
                V this[K key] { get => items[key]; set { items[key] = value; } }
                LruCache(int capacity) : base(capacity) { }
                async Task<V?> GetAsync(K key) => await Load(key);
            }
            interface Comparable<T> { int CompareTo(T other); }
            enum Mode { CPU, DMA = 4 }
            private const int Limit = 1 << 8;
            """);

        Assert.False(tree.HasErrors, string.Join("\n", tree.Diagnostics));
        var cls = Assert.IsType<TypeDecl>(tree.Root.Members[0]);
        Assert.Equal(["K", "V"], cls.TypeParameters.Select(t => t.Name));
        Assert.Equal(
            ["FieldDecl", "PropertyDecl", "IndexerDecl", "ConstructorDecl", "FunctionDecl"],
            cls.Members.Select(m => m.GetType().Name));
        Assert.True(cls.Members[0].Modifiers.IsPrivate);
        Assert.True(cls.Members[4].Modifiers.IsAsync);
        Assert.Equal(TypeDeclKind.Interface, Assert.IsType<TypeDecl>(tree.Root.Members[1]).Kind);
        Assert.Equal(["CPU", "DMA"], Assert.IsType<EnumDecl>(tree.Root.Members[2]).Members.Select(m => m.Name));
        var constant = Assert.IsType<FieldDecl>(tree.Root.Members[3]);
        Assert.True(constant.Modifiers.IsConst && constant.Modifiers.IsPrivate);
    }

    [Fact]
    public void Module_header_attributes_imports_and_doc_comment()
    {
        var tree = SyntaxTree.Parse("""
            /// Stock modem driver.
            @Driver("Kestrel OM-1 Optical Modem")
            @Version("1.0.4-stock")
            module NetCardDriver;
            import Sys.Hardware;
            import Std.Collections as C;
            """);
        Assert.False(tree.HasErrors);
        Assert.Equal("Stock modem driver.", tree.Root.DocComment);
        Assert.Equal(["Driver", "Version"], tree.Root.Attributes.Select(a => a.Name));
        Assert.Equal("Sys.Hardware", tree.Root.Imports[0].Name.ToString());
        Assert.Equal("C", tree.Root.Imports[1].Alias);
    }

    #endregion

    #region Real sources

    public static IEnumerable<object[]> DocumentSamples()
    {
        var documents = Path.Combine(AppContext.BaseDirectory, "documents");
        foreach (var file in new[] { "05-content-architecture.md", "07-cscript-language.md" })
        {
            var markdown = File.ReadAllText(Path.Combine(documents, file));
            var index = 0;
            foreach (Match block in Regex.Matches(markdown, "```c\\n(.*?)```", RegexOptions.Singleline))
            {
                var code = block.Groups[1].Value;
                index++;
                if (Regex.IsMatch(code, @"^\s*module \w+;", RegexOptions.Multiline) && !code.Contains("@Attribute(args)*"))
                {
                    yield return [$"{file}#{index}", code];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(DocumentSamples))]
    public void Every_module_sample_in_the_design_documents_parses_cleanly(string name, string code)
    {
        var tree = SyntaxTree.Parse(code);
        Assert.True(!tree.HasErrors, $"{name}:\n{string.Join("\n", tree.Diagnostics)}");
    }

    [Fact]
    public void Document_samples_were_found() => Assert.True(DocumentSamples().Count() >= 5);

    [Fact]
    public void Shipped_world_cform_pages_parse()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "content", "world", "act1-verbena-and-rust.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var pages = json.RootElement.GetProperty("hosts").EnumerateArray()
            .SelectMany(h => h.GetProperty("files").EnumerateArray())
            .Where(f => f.GetProperty("path").GetString()!.EndsWith(".cform", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(pages);
        foreach (var page in pages)
        {
            var tree = SyntaxTree.Parse(page.GetProperty("text").GetString()!);
            Assert.True(!tree.HasErrors, string.Join("\n", tree.Diagnostics));
        }
    }

    #endregion

    #region Errors and recovery

    [Fact]
    public void Missing_semicolon_is_reported_and_parsing_continues()
    {
        var tree = Module("int a = 1\nint b = 2;\nvoid F() { }");
        Assert.Contains(tree.Diagnostics, d => d.Code == "CSC2001" && d.Message.Contains("';'"));
        Assert.Equal(["a", "b", "F"], tree.Root.Members.Select(m => m.Name));
        var diagnostic = tree.Diagnostics.First();
        Assert.Equal(3, diagnostic.Location.Position.Line);
        Assert.StartsWith("CSC2001 error <memory>:3:1", diagnostic.ToString());
    }

    [Fact]
    public void Non_statement_expressions_are_rejected() =>
        Assert.Contains(Module("void F() { a + b; }").Diagnostics, d => d.Code == "CSC2007");

    [Fact]
    public void Invalid_assignment_targets_are_rejected() =>
        Assert.Contains(Module("void F() { 1 = 2; }").Diagnostics, d => d.Code == "CSC2006");

    [Fact]
    public void Module_name_must_match_the_file_name()
    {
        var tree = SyntaxTree.Parse("module Wrong;", "/sys/drivers/net/NetCardDriver.drv");
        Assert.Contains(tree.Diagnostics, d => d.Code == "CSC2005");
        Assert.False(SyntaxTree.Parse("module NetCardDriver;", "/sys/drivers/net/NetCardDriver.drv").HasErrors);
    }

    [Fact]
    public void Duplicate_modifiers_are_reported() =>
        Assert.Contains(Module("private private int x;").Diagnostics, d => d.Code == "CSC2008");

    [Property(MaxTest = 300)]
    public Property Parser_terminates_on_arbitrary_input(string? text)
    {
        var tree = SyntaxTree.Parse("module Fuzz;\n" + (text ?? string.Empty));
        return (tree.Root.Span.End <= tree.Source.Length).ToProperty();
    }

    [Property(MaxTest = 200)]
    public Property Parser_terminates_on_token_soup(PositiveInt seed)
    {
        string[] vocabulary =
        [
            "(", ")", "{", "}", "[", "]", "<", ">", "=", "=>", ";", ",", ".", "?", ":", "x", "int", "new", "if", "else",
            "class", "async", "await", "switch", "case", "1", "\"s\"", "+", "-", "!", "is", "var", "fn", "->", "@A", "yield",
        ];
        var random = new System.Random(seed.Get);
        var text = string.Join(' ', Enumerable.Range(0, 60).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
        var tree = SyntaxTree.Parse("module Fuzz;\nvoid F() { " + text + " }");
        return (tree.Root is not null).ToProperty();
    }

    #endregion
}
