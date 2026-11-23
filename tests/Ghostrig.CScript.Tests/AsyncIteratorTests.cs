using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Tests;

public class AsyncIteratorTests
{
    private static (VirtualMachine Vm, ProgramImage Image, VirtualClockHost Host) Load(string source)
    {
        var compilation = Compilation.Create(SyntaxTree.Parse("module Test;\n" + source));
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        var image = ProgramImage.Link(compilation);
        Assert.Empty(image.LinkErrors);
        var host = new VirtualClockHost();
        var vm = new VirtualMachine(image, host);
        vm.Initialize();
        return (vm, image, host);
    }

    private const string Blocks = """
        async Iterable<int> ReadBlocks(int count) {
            for (var i = 0; i < count; i++) {
                await Task.Delay(Duration.FromMilliseconds(10));   // each block arrives later in simulated time
                yield return i * 100;
            }
        }
        """;

    [Fact]
    public void Await_foreach_streams_values_across_simulated_time()
    {
        var (vm, image, host) = Load(Blocks + """
            async Task<string> Main() {
                var seen = "";
                await foreach (var block in ReadBlocks(4)) { seen += $"{block};"; }
                return seen;
            }
            """);
        var result = vm.Invoke(image.FindFunction("Test", "Main")!);
        Assert.Equal("0;100;200;300;", result.AsString);
        Assert.True(host.Now >= 40_000_000, $"Simulated time should cover four 10 ms waits, was {host.Now} ns.");
    }

    [Fact]
    public void Yield_break_early_exit_and_reuse()
    {
        var (vm, image, _) = Load(Blocks + """
            async AsyncIterable<string> Banner(bool empty) {
                if (empty) yield break;
                yield return "220";
                await Task.Delay(Duration.FromMilliseconds(1));
                yield return "FTP ready";
            }
            async Task<string> Main() {
                var parts = "";
                await foreach (var b in ReadBlocks(10)) { if (b > 100) break; parts += $"{b},"; }
                await foreach (var line in Banner(true)) { parts += "never"; }
                await foreach (var line in Banner(false)) { parts += $"[{line}]"; }
                var it = ReadBlocks(1).GetAsyncIterator();
                var first = await it.MoveNextAsync();
                var second = await it.MoveNextAsync();
                var third = await it.MoveNextAsync();
                return $"{parts}|{first}|{it.Current}|{second}|{third}";
            }
            """);
        Assert.Equal("0,100,[220][FTP ready]|true|0|false|false", vm.Invoke(image.FindFunction("Test", "Main")!).AsString);
    }

    [Fact]
    public void Errors_in_the_body_surface_at_the_consumer_and_can_be_caught()
    {
        var (vm, image, _) = Load("""
            async AsyncIterable<int> Flaky() {
                yield return 1;
                await Task.Delay(Duration.FromMilliseconds(5));
                throw new ProtocolError("connection reset");
            }
            async Task<string> Main() {
                var got = "";
                try {
                    await foreach (var x in Flaky()) { got += $"{x}"; }
                } catch (ProtocolError e) {
                    got += $"|{e.Code}:{e.Message}";
                }
                return got;
            }
            """);
        Assert.Equal("1|EPROTO:connection reset", vm.Invoke(image.FindFunction("Test", "Main")!).AsString);
        Assert.Empty(vm.Faults);
    }

    [Fact]
    public void Concurrent_MoveNextAsync_calls_are_rejected()
    {
        var (vm, image, _) = Load(Blocks + """
            async Task<string> Main() {
                var it = ReadBlocks(3).GetAsyncIterator();
                var pending = it.MoveNextAsync();
                try { it.MoveNextAsync(); return "accepted"; } catch (Error e) { return e.Message; }
            }
            """);
        Assert.Contains("before the previous call completed", vm.Invoke(image.FindFunction("Test", "Main")!).AsString);
    }

    [Fact]
    public void Async_iteration_is_deterministic()
    {
        string RunOnce()
        {
            var (vm, image, host) = Load(Blocks + """
                async Task<int> Producer(int id) { var sum = 0; await foreach (var b in ReadBlocks(3)) { sum += b + id; } return sum; }
                async Task<string> Main() {
                    var a = Producer(1); var b = Producer(2);
                    await Task.WhenAll(a, b);
                    return $"{a.Result}|{b.Result}";
                }
                """);
            return vm.Invoke(image.FindFunction("Test", "Main")!).AsString + "|" + host.Now + "|" + vm.Steps;
        }

        Assert.Equal(RunOnce(), RunOnce());
        Assert.StartsWith("303|306|", RunOnce());
    }
}
