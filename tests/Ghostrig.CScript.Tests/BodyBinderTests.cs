using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Syntax;

namespace Ghostrig.CScript.Tests;

public class BodyBinderTests
{
    private static Compilation Compile(string source, string path = "<memory>") => Compilation.Create(SyntaxTree.Parse(source, path));

    private static Compilation AssertClean(string source)
    {
        var compilation = Compile(source);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        return compilation;
    }

    private static Diagnostic AssertDiagnostic(string source, string code)
    {
        var compilation = Compile(source);
        var diagnostic = compilation.Diagnostics.FirstOrDefault(d => d.Code == code);
        Assert.True(diagnostic is not null, $"expected {code}, got:\n{string.Join("\n", compilation.Diagnostics)}");
        return diagnostic!;
    }

    private static BoundFunction Function(Compilation compilation, string name) =>
        compilation.Program.Functions.Values.Single(f => f.Method.Name == name);

    [Fact]
    public void A_realistic_program_binds_without_errors()
    {
        AssertClean("""
            module Toolkit;

            enum Mode { CPU, DMA }

            class Packet {
                bytes Payload;
                int Port;
                Packet(bytes payload, int port) { Payload = payload; Port = port; }
                int Size => Payload.Length;
            }

            class Probe : Packet {
                string Banner { get; set; }
                Probe(int port) : base(bytes.Empty, port) { Banner = ""; }
                string Describe() => $"{Port}/{Banner} ({Size} bytes)";
            }

            interface Shape { float Area(); }
            struct Square : Shape {
                float Side;
                float Area() => Side * Side;
            }

            T Pick<T>(T a, T b) where T : Comparable<T> => a.CompareTo(b) >= 0 ? a : b;

            R[] MapAll<T, R>(T[] items, fn(T) -> R transform) {
                var result = new R?[items.Length];
                for (int i = 0; i < items.Length; i++) {
                    result[i] = transform(items[i]);
                }
                return [];
            }

            int Square(int x) => x * x;

            Iterable<int> Countdown(int from) {
                for (var i = from; i > 0; i--) {
                    yield return i;
                }
            }

            async Task<int> ScanAsync(int[] ports) {
                var open = 0;
                foreach (var port in ports) {
                    await Task.Delay(Duration.FromMilliseconds(5));
                    if (port == 2121 || port == 8443) open++;
                }
                return open;
            }

            string Classify(object value) => value switch {
                Packet p when p.Port == 0 => "empty",
                Packet p => $"packet:{p.Port}",
                string s => s,
                _ => "?",
            };

            int Run(Args args) {
                int total = 0;
                total += Pick(3, 7);
                float scaled = total * 1.5;
                string label = "x" + total + scaled;
                var words = "a,b,c".Split(",");
                foreach (var w in words) { label = label + w.ToUpper(); }
                foreach (var c in label) { if (char.IsDigit(c)) total++; }
                foreach (var n in Countdown(3)) { total += n; }

                var doubled = MapAll([1, 2, 3], x => x * 2);
                fn(int) -> int sq = Square;
                total = total + sq(4) + doubled.Length;

                var (code, name) = (200, "ok");
                total += code;
                string? maybe = args.Option("name");
                if (maybe != null) { total += maybe.Length; }
                total += maybe?.Length ?? 0;

                Mode mode = Mode.DMA;
                var described = mode switch { Mode.CPU => "polled", Mode.DMA => "dma", _ => "?" };

                Duration timeout = Duration.FromSeconds(2) * 3 + Duration.FromMilliseconds(250);
                var parsed = int.TryParse("42") ?? -1;

                try {
                    if (total > 1000) throw new ArgumentError("too big");
                } catch (ArgumentError e) {
                    label = e.Message;
                } finally {
                    total--;
                }

                switch (total % 3) {
                    case 0: total += 1; break;
                    case 1:
                    case 2: total += 2; break;
                    default: break;
                }

                var probe = new Probe(2121) { Banner = "ftpd" };
                Packet asPacket = probe;
                if (asPacket is Probe again) { label = again.Describe(); }

                var shapes = new Shape?[2];
                shapes[0] = new Square { Side = 2.0 };
                var tasks = Task.WhenAll(ScanAsync([21, 2121]), ScanAsync([8443]));
                return total + parsed + (int)timeout.TotalSeconds + described.Length + name.Length;
            }
            """);
    }

    [Fact]
    public void Generic_inference_and_lambda_return_types()
    {
        var compilation = AssertClean("""
            module Infer;
            R Apply<T, R>(T value, fn(T) -> R f) => f(value);
            void Use() {
                var text = Apply(21, x => $"{x * 2}");
                string check = text;
                var widened = Apply(1, x => x * 0.5);
                float f = widened;
            }
            """);
        var use = Function(compilation, "Use");
        var first = (BoundLocalDeclaration)use.Body.Statements[0];
        Assert.Equal(PrimitiveType.String, first.Local.Type);
        var call = (BoundCall)first.Initializer!;
        Assert.Equal("string Apply<int, string>(int value, fn(int) -> string f)", call.Method.ToString());
    }

    [Fact]
    public void Async_functions_and_lambdas_have_the_right_flavor()
    {
        var compilation = AssertClean("""
            module Asyncs;
            async Task<string> FetchAsync() {
                await Task.Delay(Duration.FromSeconds(1));
                return "page";
            }
            async Task Main(Args args) {
                var page = await FetchAsync();
                fn() -> Task<int> later = async () => { await Task.Delay(Duration.Zero); return page.Length; };
                var n = await later();
            }
            Iterable<string> Lines() { yield return "a"; yield break; }
            """);
        Assert.Equal(FunctionFlavor.Async, Function(compilation, "FetchAsync").Flavor);
        Assert.Equal(FunctionFlavor.Iterator, Function(compilation, "Lines").Flavor);
        Assert.Equal(PrimitiveType.String, Function(compilation, "Lines").ElementType);
        Assert.Single(compilation.Program.Lambdas);
        Assert.Equal(FunctionFlavor.Async, compilation.Program.Lambdas[0].Flavor);
    }

    [Fact]
    public void Lambdas_record_captured_variables()
    {
        var compilation = AssertClean("""
            module Captures;
            class Counter {
                int Count;
                fn() -> int Make(int step) {
                    int local = 10;
                    fn() -> int f = () => local + step + Count;
                    return f;
                }
            }
            """);
        var make = Function(compilation, "Make");
        Assert.Equal(["local"], make.CapturedLocals.Select(l => l.Name));
        Assert.Equal(["step"], make.CapturedParameters.Select(p => p.Name));
        Assert.True(compilation.Program.Lambdas.Single().CapturesThis);
    }

    [Fact]
    public void Null_narrowing_removes_dereference_warnings()
    {
        var compilation = AssertClean("""
            module Nulls;
            int Len(string? s) {
                if (s == null) return 0;
                return s.Length;
            }
            int Len2(string? s) => s != null && s.Length > 2 ? s.Length : 0;
            int Unsafe(string? s) => s.Length;
            """);
        var warnings = compilation.Diagnostics.Where(d => d.Code == "CSC4029").ToList();
        Assert.Single(warnings);
        Assert.Equal(7, warnings[0].Location.Position.Line);
    }

    [Fact]
    public void Implicit_conversions_are_made_explicit_in_the_bound_tree()
    {
        var compilation = AssertClean("""
            module Conv;
            float F(byte b) { float x = b + 1; return x; }
            """);
        var declaration = (BoundLocalDeclaration)Function(compilation, "F").Body.Statements[0];
        var conversion = Assert.IsType<BoundConversion>(declaration.Initializer);
        Assert.Equal(ConversionKind.ImplicitNumeric, conversion.Kind);
        var add = Assert.IsType<BoundBinary>(conversion.Operand);
        Assert.Equal(BinaryOperatorKind.IntAdd, add.Operator);
        Assert.IsType<BoundConversion>(add.Left);
    }

    [Fact]
    public void Constants_fold_through_bound_operators()
    {
        var compilation = AssertClean("""
            module Consts;
            const int Mask = 0xFF << 8;
            int F() { const int Shift = 4; return Mask >> Shift; }
            """);
        var mask = compilation.GetModule("Consts")!.Lookup("Mask").OfType<FieldSymbol>().Single();
        Assert.Equal(0xFF00L, mask.ConstantValue);
    }

    [Theory]
    [InlineData("void F() { x = 1; }", "CSC4001")]
    [InlineData("void F() { var s = \"a\"; s.Nope(); }", "CSC4002")]
    [InlineData("void F() { int x = \"a\"; }", "CSC4003")]
    [InlineData("void F() { int x = 1.5; }", "CSC4004")]
    [InlineData("int G(int a) => a; void F() { G(\"s\"); }", "CSC4005")]
    [InlineData("void F() { var b = true + 1; }", "CSC4007")]
    [InlineData("void F() { var b = -\"s\"; }", "CSC4008")]
    [InlineData("int G() => 1; void F() { G() = 2; }", "CSC4009")]
    [InlineData("void F() { int a = 1; int a = 2; }", "CSC4010")]
    [InlineData("void F(int a) { int a = 2; }", "CSC4010")]
    [InlineData("int F(bool b) { if (b) return 1; }", "CSC4011")]
    [InlineData("void F() { return 1; }", "CSC4012")]
    [InlineData("int F() { return; }", "CSC4013")]
    [InlineData("void F() { break; }", "CSC4014")]
    [InlineData("void F() { await Task.Delay(Duration.Zero); }", "CSC4015")]
    [InlineData("async Task F() { await 5; }", "CSC4016")]
    [InlineData("void F() { foreach (var x in 5) { } }", "CSC4017")]
    [InlineData("int F() { yield return 1; }", "CSC4018")]
    [InlineData("void F() { throw \"boom\"; }", "CSC4019")]
    [InlineData("void F() { try { } catch (StepBudgetExceeded e) { } }", "CSC4020")]
    [InlineData("T Make<T>() => Make<T>(); void F() { var x = Make(); }", "CSC4021")]
    [InlineData("void F() { int x = 1; x(); }", "CSC4022")]
    [InlineData("class C { int n; static int Get() => n; }", "CSC4023")]
    [InlineData("class C { private int secret; } int F(C c) => c.secret;", "CSC4024")]
    [InlineData("interface I { } void F() { var i = new I(); }", "CSC4025")]
    [InlineData("void F() { var x; }", "CSC4026")]
    [InlineData("void F() { string s; }", "CSC4027")]
    [InlineData("void F() { var f = x => x; }", "CSC4028")]
    [InlineData("void F() { string s = null; }", "CSC4030")]
    [InlineData("class C { static void F() { var t = this; } }", "CSC4031")]
    [InlineData("const int K = 1; void F() { K = 2; }", "CSC4032")]
    [InlineData("void F(int x) { var b = x is string; }", "CSC4034")]
    [InlineData("void G() { } void F() { var x = G(); }", "CSC4026")]
    [InlineData("void G() { } int F() => G() + 1;", "CSC4037")]
    [InlineData("async Task G() { } void F() { G(); }", "CSC4038")]
    [InlineData("void F() { var (a, b, c) = (1, 2); }", "CSC4039")]
    [InlineData("class C { void F() { base.ToString(); } }", "CSC4040")]
    [InlineData("class C { C(int x) { } } void F() { var c = new C(); }", "CSC4041")]
    [InlineData("void F() { var x = int; }", "CSC4042")]
    public void Body_errors_have_stable_codes(string body, string code) => AssertDiagnostic("module M;\n" + body, code);

    [Fact]
    public void Apps_require_an_entry_point()
    {
        AssertDiagnostic("@App(\"X\") module X; void Helper() { }", "CSC4043");
        Assert.False(Compile("@App(\"X\") module X; async Task Main(Args args) { await Task.Delay(Duration.Zero); }").HasErrors);
    }

    [Fact]
    public void Possible_null_dereference_is_a_warning_not_an_error()
    {
        var diagnostic = AssertDiagnostic("module M; int F(string? s) => s.Length;", "CSC4029");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }
}
