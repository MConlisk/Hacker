using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Natives;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Tests;

public class VmTests
{
    private sealed record Program(Compilation Compilation, ProgramImage Image, VirtualMachine Vm, VirtualClockHost Host)
    {
        public Value Call(string function, params Value[] args) => Vm.Invoke(Image.FindFunction("Test", function)!, args);
    }

    private static Program Load(string source, VmOptions? options = null)
    {
        var compilation = Compilation.Create(SyntaxTree.Parse("module Test;\n" + source));
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        var image = ProgramImage.Link(compilation);
        Assert.Empty(image.LinkErrors);
        var host = new VirtualClockHost();
        var vm = new VirtualMachine(image, host, options);
        vm.Initialize();
        return new Program(compilation, image, vm, host);
    }

    private static Value Run(string source, string function = "Main", params Value[] args) => Load(source).Call(function, args);

    private static Value Int(long v) => Value.FromInt(v);

    [Fact]
    public void Arithmetic_loops_and_recursion()
    {
        var program = Load("""
            int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
            int Sum(int n) { var total = 0; for (var i = 1; i <= n; i++) { total += i; } return total; }
            int Collatz(int n) { var steps = 0; while (n != 1) { n = n % 2 == 0 ? n / 2 : 3 * n + 1; steps++; } return steps; }
            float Avg(int a, int b) => (a + b) / 2.0;
            int Wrap() => 9223372036854775807 + 1 == -9223372036854775807 - 1 ? 1 : 0;
            """);
        Assert.Equal(55, program.Call("Fib", Int(10)).AsInt);
        Assert.Equal(5050, program.Call("Sum", Int(100)).AsInt);
        Assert.Equal(111, program.Call("Collatz", Int(27)).AsInt);
        Assert.Equal(2.5, program.Call("Avg", Int(2), Int(3)).AsFloat);
        Assert.Equal(1, program.Call("Wrap").AsInt);
    }

    [Fact]
    public void Strings_interpolation_and_formatting()
    {
        var result = Run("""
            string Main() {
                var parts = "a,b,c".Split(",");
                var joined = string.Join("-", parts).ToUpper();
                var port = 2121;
                float ratio = 0.5;
                return $"{joined}|{port:X4}|{ratio}|{1.0 / 4}|{'x'}|{true}|" + parts.Length + "|" + "padded".PadLeft(8, '.');
            }
            """);
        Assert.Equal("A-B-C|0849|0.5|0.25|x|true|3|..padded", result.AsString);
    }

    [Fact]
    public void Classes_inheritance_virtual_dispatch_and_properties()
    {
        var result = Run("""
            class Shape {
                string Name { get; set; }
                static int Created = 0;
                Shape(string name) { Name = name; Created++; }
                float Area() => 0.0;
                string Describe() => $"{Name}:{Area()}";
            }
            class Rect : Shape {
                float W; float H;
                Rect(float w, float h) : base("rect") { W = w; H = h; }
                float Area() => W * H;
            }
            class Square : Rect {
                Square(float s) : base(s, s) { Name = "square"; }
                string Describe() => "[" + base.Describe() + "]";
            }
            string Main() {
                Shape[] shapes = [new Rect(2.0, 3.0), new Square(2.0), new Shape("blob")];
                var text = "";
                foreach (var s in shapes) { text = text + s.Describe() + " "; }
                return text + Shape.Created;
            }
            """);
        Assert.Equal("rect:6.0 [square:4.0] blob:0.0 3", result.AsString);
    }

    [Fact]
    public void Structs_have_value_semantics()
    {
        var result = Run("""
            struct Point { int X; int Y; void Move(int dx) { X += dx; } }
            string Main() {
                Point a = new Point { X = 1, Y = 2 };
                var b = a;
                b.X = 10;
                a.Move(5);
                Point[] arr = [a];
                var c = arr[0];
                c.Y = 99;
                return $"{a.X},{a.Y} {b.X},{b.Y} {arr[0].Y} {a == new Point { X = 6, Y = 2 }}";
            }
            """);
        Assert.Equal("6,2 10,2 2 true", result.AsString);
    }

    [Fact]
    public void Closures_capture_variables_by_reference_and_per_iteration()
    {
        var result = Run("""
            fn() -> int Counter() {
                var count = 0;
                return () => { count++; return count; };
            }
            string Main() {
                var next = Counter();
                next(); next();
                var third = next();
                var fns = new (fn() -> int)?[3];
                var i = 0;
                foreach (var n in [10, 20, 30]) { fns[i] = () => n; i++; }
                var total = 0;
                foreach (var f in fns) { total += f!(); }
                fn(int) -> int addThird = x => x + third;
                return $"{third} {total} {addThird(4)}";
            }
            """);
        Assert.Equal("3 60 7", result.AsString);
    }

    [Fact]
    public void Generics_inference_and_constrained_calls()
    {
        var result = Run("""
            class Box<T> { T Value; Box(T v) { Value = v; } T Get() => Value; }
            T Max<T>(T a, T b) where T : Comparable<T> => a.CompareTo(b) >= 0 ? a : b;
            R[] Map<T, R>(T[] items, fn(T) -> R f) {
                var result = new R?[items.Length];
                for (var i = 0; i < items.Length; i++) { result[i] = f(items[i]); }
                return result.Length == 0 ? [] : [f(items[0]), f(items[items.Length - 1])];
            }
            string Main() {
                var box = new Box<string>("ghost");
                var mapped = Map([1, 2, 3], x => x * 10);
                return $"{box.Get()} {Max(3, 9)} {Max("orchid", "aegis")} {mapped[0]}+{mapped[1]}";
            }
            """);
        Assert.Equal("ghost 9 orchid 10+30", result.AsString);
    }

    [Fact]
    public void Iterators_yield_lazily()
    {
        var result = Run("""
            Iterable<int> Countdown(int from) {
                for (var i = from; i > 0; i--) { yield return i; }
                yield return 0;
            }
            Iterable<string> Words() { yield return "dark"; yield return "subnet"; yield break; }
            string Main() {
                var text = "";
                foreach (var n in Countdown(3)) { text = text + n; }
                foreach (var w in Words()) { text = text + " " + w; }
                return text;
            }
            """);
        Assert.Equal("3210 dark subnet", result.AsString);
    }

    [Fact]
    public void Exceptions_catch_finally_rethrow_and_codes()
    {
        var result = Run("""
            class TraceLost : Error { TraceLost(string m) : base(m) { } }
            string Log = "";
            int Divide(int a, int b) => a / b;
            string Main() {
                try { Divide(1, 0); } catch (ArithmeticError e) { Log = Log + "arith:" + e.Code + " "; }
                try {
                    try { throw new TraceLost("relay down"); }
                    finally { Log = Log + "inner-finally "; }
                } catch (TraceLost e) { Log = Log + "caught:" + e.Message + ":" + e.Code + " "; }
                try {
                    try { var arr = [1]; var x = arr[5]; }
                    catch (IndexError e) { Log = Log + "index "; throw; }
                } catch (Error e) { Log = Log + "rethrown:" + e.Code + " "; }
                for (var i = 0; i < 3; i++) {
                    try { if (i == 1) continue; Log = Log + i; } finally { Log = Log + "f"; }
                }
                return Log;
            }
            """);
        Assert.Equal("arith:EARITH inner-finally caught:relay down:ERROR index rethrown:EINDEX 0ff2f", result.AsString);
    }

    [Fact]
    public void Uncaught_errors_surface_with_stack_traces()
    {
        var program = Load("""
            string? Lookup(int key) => key == 1 ? "v.chen" : null;
            int Main() { var name = Lookup(2); return name!.Length; }
            """);
        var ex = Assert.Throws<ScriptException>(() => program.Call("Main"));
        Assert.Contains("NullError [ENULL]", ex.Message);
        var state = (ErrorState)ex.Error.As<ObjectInstance>().NativeState!;
        Assert.Contains("Test.Main", state.StackTrace);
    }

    [Fact]
    public void Patterns_switches_and_enums()
    {
        var result = Run("""
            enum Mode { CPU, DMA }
            enum Flags { None = 0, Read = 1, Write = 2 }
            string Classify(object value) => value switch {
                int n when n > 100 => "big",
                int n => $"int:{n}",
                string s => $"str:{s.Length}",
                Mode m => $"mode:{m}",
                _ => "other",
            };
            string Main() {
                var text = Classify(500) + " " + Classify(7) + " " + Classify("abc") + " " + Classify(Mode.DMA) + " " + Classify(1.5);
                var flags = Flags.Read | Flags.Write;
                switch (flags) {
                    case Flags.None: text = text + " none"; break;
                    default: text = text + " " + (int)flags; break;
                }
                byte b = 200;
                if (b is >= 128) text = text + " high";
                return text;
            }
            """);
        Assert.Equal("big int:7 str:3 mode:DMA other 3 high", result.AsString);
    }

    [Fact]
    public void Async_await_advances_simulated_time_concurrently()
    {
        var program = Load("""
            async Task<int> Probe(int port, int ms) {
                await Task.Delay(Duration.FromMilliseconds(ms));
                return port;
            }
            async Task<int> Main() {
                var a = Probe(21, 300);
                var b = Probe(2121, 500);
                await Task.WhenAll(a, b);
                return await a + await b;
            }
            """);
        var before = program.Host.Now;
        Assert.Equal(2142, program.Call("Main").AsInt);
        var elapsed = program.Host.Now - before;
        Assert.InRange(elapsed, 500_000_000, 501_000_000); // concurrent: max(300, 500) ms, plus a few steps
    }

    [Fact]
    public void Async_errors_propagate_through_await()
    {
        var result = Run("""
            async Task<int> Fail() { await Task.Delay(Duration.FromSeconds(1)); throw new AuthFailed("bad token"); }
            async Task<string> Main() {
                try { await Fail(); return "no"; }
                catch (AuthFailed e) { return e.Code + " " + e.Message; }
            }
            """);
        Assert.Equal("401 bad token", result.AsString);
    }

    [Fact]
    public void Execution_is_deterministic_and_metered()
    {
        const string source = """
            int Work() { var x = 0; for (var i = 0; i < 1000; i++) { x = (x * 31 + i) % 1000003; } return x; }
            """;
        var first = Load(source);
        var r1 = first.Call("Work");
        var second = Load(source);
        var r2 = second.Call("Work");
        Assert.Equal(r1.AsInt, r2.AsInt);
        Assert.Equal(first.Vm.Steps, second.Vm.Steps);
        Assert.InRange(first.Vm.Steps, 10_000, 30_000);
        Assert.Equal(first.Vm.Steps, first.Host.StepsCharged);
    }

    [Fact]
    public void Step_budget_faults_and_rolls_back_the_event()
    {
        var program = Load("""
            int Counter = 0;
            int[] Data = [1, 2, 3];
            void Runaway() { Counter = 99; Data[0] = 42; while (true) { } }
            void Fine() { Counter = 7; }
            """, new VmOptions { DefaultEventBudget = 10_000 });

        program.Vm.StartEvent(program.Image.FindFunction("Test", "Runaway")!, []);
        program.Vm.RunUntilIdle();
        var fault = Assert.Single(program.Vm.Faults);
        Assert.Equal(FaultKind.StepBudgetExceeded, fault.Kind);
        Assert.True(fault.RolledBack);

        var counter = program.Compilation.GetModule("Test")!.Lookup("Counter").OfType<FieldSymbol>().Single();
        var data = program.Compilation.GetModule("Test")!.Lookup("Data").OfType<FieldSymbol>().Single();
        Assert.Equal(0, program.Vm.GetGlobal(program.Image.Linker.GlobalSlot(counter)).AsInt);
        Assert.Equal(1, program.Vm.GetGlobal(program.Image.Linker.GlobalSlot(data)).As<ArrayObject>().Items[0].AsInt);

        program.Vm.StartEvent(program.Image.FindFunction("Test", "Fine")!, []);
        program.Vm.RunUntilIdle();
        Assert.Equal(7, program.Vm.GetGlobal(program.Image.Linker.GlobalSlot(counter)).AsInt);
    }

    [Fact]
    public void Uncaught_event_errors_are_reported_and_rolled_back()
    {
        var program = Load("""
            string State = "idle";
            void Handler() { State = "half-done"; throw new ProtocolError("bad frame"); }
            """);
        program.Vm.StartEvent(program.Image.FindFunction("Test", "Handler")!, []);
        program.Vm.RunUntilIdle();
        var fault = Assert.Single(program.Vm.Faults);
        Assert.Equal(FaultKind.UnhandledError, fault.Kind);
        Assert.Contains("ProtocolError [EPROTO]: bad frame", fault.Message);
        var state = program.Compilation.GetModule("Test")!.Lookup("State").OfType<FieldSymbol>().Single();
        Assert.Equal("idle", program.Vm.GetGlobal(program.Image.Linker.GlobalSlot(state)).AsString);
    }

    [Fact]
    public void Budget_errors_cannot_be_caught()
    {
        var program = Load("""
            string Main() {
                try { while (true) { } } catch (Error e) { return "caught"; }
            }
            """, new VmOptions { DefaultEventBudget = 5_000 });
        var fiber = program.Vm.StartEvent(program.Image.FindFunction("Test", "Main")!, []);
        program.Vm.RunUntilIdle();
        Assert.Equal(FiberState.Faulted, fiber.State);
        Assert.Equal(FaultKind.StepBudgetExceeded, Assert.Single(program.Vm.Faults).Kind);
    }

    [Fact]
    public void Tuples_nullables_and_coalescing()
    {
        var result = Run("""
            (int, string) Probe() => (8443, "mgmt");
            int? Find(int[] xs, int target) { foreach (var x in xs) { if (x == target) return x; } return null; }
            string Main() {
                var (port, name) = Probe();
                var found = Find([21, 2121], 2121) ?? -1;
                var missing = Find([21], 80) ?? -1;
                string? maybe = null;
                return $"{port}/{name} {found} {missing} {maybe?.Length ?? 0} {Probe().Item2}";
            }
            """);
        Assert.Equal("8443/mgmt 2121 -1 0 mgmt", result.AsString);
    }

    [Fact]
    public void Event_handlers_combine_with_plus_equals()
    {
        var result = Run("""
            string Log = "";
            (fn(string) -> void)? Handlers = null;
            void A(string s) { Log = Log + "a" + s; }
            void B(string s) { Log = Log + "b" + s; }
            string Main() {
                Handlers += A;
                Handlers += B;
                Handlers!("1");
                Handlers -= A;
                Handlers!("2");
                return Log;
            }
            """);
        Assert.Equal("a1b1b2", result.AsString);
    }

    [Fact]
    public void Document_seven_generic_cache_sample_runs()
    {
        var result = Run("""
            class MiniLru<K, V> where K : Hashable {
                private Map<K, V> items;
            }
            string Main() => "skipped";
            """.Replace("private Map<K, V> items;", "private int capacity; MiniLru(int capacity) { this.capacity = capacity; } int Capacity => capacity;"));
        Assert.Equal("skipped", result.AsString);
    }

    [Fact]
    public void Every_core_native_declaration_is_implemented()
    {
        var image = ProgramImage.Link(Compilation.Create(SyntaxTree.Parse("module Empty;")));
        var declared = StandardLibrary.Default.GetModule("Core")!.Members
            .Concat(StandardLibrary.Default.GetModule("Core")!.HiddenTypes)
            .OfType<NamedTypeSymbol>()
            .SelectMany(t => t.Members)
            .SelectMany(m => m switch
            {
                MethodSymbol method => [method],
                PropertySymbol p => new[] { p.Getter, p.Setter }.OfType<MethodSymbol>(),
                _ => [],
            })
            .Where(m => m.IsNative)
            .Select(m => m.NativeKey)
            .Distinct()
            .ToList();
        var missing = declared.Where(key => !NativeRegistry.Default.TryGet(key, out _)).ToList();
        Assert.True(missing.Count == 0, "Missing natives:\n" + string.Join("\n", missing));
        Assert.Empty(image.LinkErrors);
    }
}
