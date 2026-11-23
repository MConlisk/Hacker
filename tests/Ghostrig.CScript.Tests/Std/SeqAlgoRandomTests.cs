using FsCheck;
using FsCheck.Xunit;
using Ghostrig.CScript.Natives.Std;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Syntax;
using static Ghostrig.CScript.Tests.Std.StdProgram;

namespace Ghostrig.CScript.Tests.Std;

public class SeqTests
{
    private const string Imports = "import Std.Collections;\nimport Std.Seq;";

    [Fact]
    public void Lazy_operators_infer_types_from_lambdas_and_can_be_reiterated()
    {
        var result = Run(Imports, """
            string Main() {
                var calls = 0;
                var squares = Seq.Range(1, 10).Map(x => { calls++; return x * x; }).Filter(x => x % 2 == 0);
                var before = calls;                       // nothing has run yet
                var first = squares.Take(2).ToArray();
                var all = squares.ToList();               // iterating again re-runs the pipeline
                var labels = squares.Map(x => $"#{x}").Join(" ");
                return $"{before}|{first.Length}:{first[0]},{first[1]}|{all}|{labels}|{calls > 10}";
            }
            """);
        Assert.Equal("0|2:4,16|[4, 16, 36, 64, 100]|#4 #16 #36 #64 #100|true", result);
    }

    [Fact]
    public void Ordering_grouping_joining_and_windows()
    {
        var result = Run(Imports, """
            string Main() {
                var hosts = new List<(string, int)>();
                hosts.Add(("dns", 53)); hosts.Add(("web", 80)); hosts.Add(("ftp", 2121)); hosts.Add(("web", 443)); hosts.Add(("dns", 5353));
                var ordered = Seq.From(hosts).OrderBy(h => h.Item1).ThenByDescending(h => h.Item2).Map(h => $"{h.Item1}:{h.Item2}").Join(",");
                var groups = Seq.From(hosts).GroupBy(h => h.Item1);
                var counts = Seq.From(hosts).CountBy(h => h.Item1);
                var owners = new Map<string, string>(); owners["dns"] = "isp"; owners["web"] = "verbena";
                var joined = Seq.From(hosts).Join(owners.Entries, h => h.Item1, o => o.Item1, (h, o) => $"{h.Item2}@{o.Item2}").Join(",");
                var windows = Seq.Range(1, 5).Window(3).Map(w => w[0] + w[1] + w[2]).Join(",");
                var chunks = Seq.Range(1, 5).Chunk(2).Map(c => c.Length).Join(",");
                var (big, small) = Seq.Range(1, 6).Partition(x => x > 3);
                return $"{ordered}|{groups["web"].Count}|{counts}|{joined}|{windows}|{chunks}|{big}{small}";
            }
            """);
        Assert.Equal("dns:5353,dns:53,ftp:2121,web:443,web:80|2|{dns: 2, web: 2, ftp: 1}|53@isp,80@verbena,443@verbena,5353@isp|6,9,12|2,2,1|[4, 5, 6][1, 2, 3]", result);
    }

    [Fact]
    public void Terminal_operations_and_functional_forms()
    {
        var result = Run(Imports, """
            string Main() {
                int[] xs = [4, 8, 15, 16, 23, 42];
                var fold = Seq.Fold(xs, 0, (acc, x) => acc + x);
                var reduce = Seq.From(xs).Reduce((a, b) => a > b ? a : b);
                var avg = Seq.Average(xs);
                float[] fs = [0.5, 0.25];
                var minBy = Seq.From(["ftp", "ssh", "telnet"]).MinBy(s => s.Length);
                var maxBy = Seq.From(["ftp", "ssh", "telnet"]).MaxBy(s => s.Length);
                var scan = Seq.From(xs).Scan(0, (a, x) => a + x).Last();
                var distinct = Seq.Distinct([3, 1, 3, 2, 1]).ToArray();
                var gen = Seq.Iterate(1, x => x * 2).TakeWhile(x => x < 100).Count();
                return $"{fold}|{reduce}|{avg}|{Seq.Sum(xs)}|{Seq.Sum(fs)}|{Seq.Min(xs)}|{Seq.Max(xs)}|{minBy}|{maxBy}|{scan}|{distinct.Length}|{gen}|{Seq.Any(xs, x => x > 40)}|{Seq.All(xs, x => x > 4)}|{Seq.From(xs).SequenceEqual([4, 8, 15, 16, 23, 42])}";
            }
            """);
        Assert.Equal("108|42|18.0|108|0.75|4|42|ftp|telnet|108|3|7|true|false|true", result);
    }

    [Fact]
    public void Empty_sequences_fail_loudly_for_first_and_min()
    {
        Assert.Contains("empty", ErrorOf(Imports, """string Main() => $"{Seq.Empty<int>().First()}";"""));
        Assert.Contains("empty", ErrorOf(Imports, """string Main() => $"{Seq.Empty<int>().Min()}";"""));
        Assert.Equal("true", Run(Imports, """string Main() => $"{Seq.Empty<int>().FirstOrNull() == null}";"""));
    }

    [Fact]
    public void Sequence_steps_are_metered()
    {
        var cheap = Load($"module Test;\n{Imports}\nint Main() => Seq.Range(0, 10).Map(x => x + 1).Count();");
        cheap.Call("Main");
        var expensive = Load($"module Test;\n{Imports}\nint Main() => Seq.Range(0, 10000).Map(x => x + 1).Count();");
        expensive.Call("Main");
        Assert.True(expensive.Vm.Steps > cheap.Vm.Steps * 100, $"{expensive.Vm.Steps} vs {cheap.Vm.Steps}");
    }
}

public class AlgoTests
{
    private const string Imports = "import Std.Algo;\nimport Std.Seq;\nimport Std.Random;";

    [Fact]
    public void Sorting_searching_and_partitioning_arrays()
    {
        var result = Run(Imports, """
            string Main() {
                int[] xs = [5, 2, 9, 1, 5, 6];
                Algo.Sort(xs);
                var found = Algo.BinarySearch(xs, 6);
                var missing = Algo.BinarySearch(xs, 3);
                string[] words = ["ccc", "a", "bb", "dd"];
                Algo.SortBy(words, w => w.Length);
                int[] ys = [1, 2, 3, 4, 5, 6];
                var evens = Algo.Partition(ys, y => y % 2 == 0);
                Algo.Rotate(ys, 2);
                var top = Algo.TopK([7, 3, 9, 1, 8], 3);
                return $"{string.Join(",", Seq.From(xs).Map(x => $"{x}"))}|{found}|{~missing}|{string.Join(",", words)}|{evens}|{Seq.From(ys).Join(",")}|{Seq.From(top).Join(",")}|{Algo.LowerBound(xs, 5)}|{Algo.UpperBound(xs, 5)}|{Algo.NthElement([9, 4, 7], 1)}|{Algo.IsSorted(xs)}";
            }
            """);
        Assert.Equal("1,2,5,5,6,9|4|2|a,bb,dd,ccc|3|6,1,3,5,2,4|9,8,7|2|4|7|true", result);
    }

    [Fact]
    public void Combinatorics_are_lazy_and_ordered()
    {
        var result = Run(Imports, """
            string Main() {
                var perms = Algo.Permutations(["a", "b", "c"]).Map(p => string.Join("", p)).Join(",");
                var combos = Algo.Combinations([1, 2, 3, 4], 2).Count();
                var product = Algo.CartesianProduct([[0, 1], [0, 1], [0, 1]]).Count();
                var power = Algo.PowerSet([1, 2, 3]).Count();
                var firstPin = Algo.Permutations([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]).First().Length;
                return $"{perms}|{combos}|{product}|{power}|{firstPin}";
            }
            """);
        Assert.Equal("abc,acb,bac,bca,cab,cba|6|8|8|10", result);
    }

    [Fact]
    public void String_comparison_metrics()
    {
        var result = Run(Imports, """
            string Main() => $"{Algo.LevenshteinDistance("kitten", "sitting")}|{Algo.DamerauLevenshteinDistance("ca", "ac")}|{Algo.LongestCommonSubsequence("ABCBDAB", "BDCABA").Length}|{Algo.LongestCommonSubstring("verbena-rust", "rusty-verbena")}|{Algo.HammingDistance("karolin", "kathrin")}|{Algo.JaroWinkler("martha", "marhta") > 0.96}";
            """);
        Assert.Equal("3|1|4|verbena|3|true", result);
    }

    [Fact]
    public void Hash_helpers_match_reference_values()
    {
        var result = Run(Imports, """
            string Main() => $"{Algo.Fnv1a("")}|{Algo.Fnv1a("a").ToHex()}|{Algo.Murmur3(bytes.FromText("hello"), 0)}|{Algo.HashOf("x") == Algo.HashOf("x")}";
            """);
        // FNV-1a 64 offset basis; FNV-1a("a") = af63dc4c8601ec8c; Murmur3_x86_32("hello", 0) = 0x248bfa47.
        Assert.Equal("-3750763034362895579|af63dc4c8601ec8c|613153351|true", result);
    }
}

public class RandomTests
{
    private const string Imports = "import Std.Random;\nimport Std.Seq;";

    [Fact]
    public void Same_seed_gives_the_same_stream_and_different_seeds_differ()
    {
        const string body = """
            string Main() {
                var a = new Random(2056); var b = new Random(2056); var c = new Random(2057);
                var same = Seq.Range(0, 100).All(i => a.NextInt() == b.NextInt());
                return $"{same}|{a.NextInt() != c.NextInt()}";
            }
            """;
        Assert.Equal("true|true", Run(Imports, body));
    }

    [Fact]
    public void Xoshiro_output_matches_the_reference_stream_for_a_known_seed()
    {
        // Reference: SplitMix64(0) seeds xoshiro256**; values cross-checked against the C reference implementation.
        var result = Run(Imports, """string Main() { var r = new Random(0); return $"{r.NextInt().ToHex()}|{r.NextInt().ToHex()}"; }""");
        Assert.Equal("99ec5f36cb75f2b4|bf6e1f784956452a", result);
    }

    [Fact]
    public void Bounded_draws_choices_samples_and_distributions()
    {
        var result = Run(Imports, """
            string Main() {
                var r = new Random(7);
                var inRange = Seq.Range(0, 1000).All(i => { var x = r.NextInt(-5, 5); return x >= -5 && x < 5; });
                var floats = Seq.Range(0, 1000).All(i => { var f = r.NextFloat(); return f >= 0.0 && f < 1.0; });
                int[] deck = [1, 2, 3, 4, 5, 6, 7, 8];
                r.Shuffle(deck);
                var sample = r.Sample(deck, 3);
                var distinct = Seq.Distinct(sample).Count();
                var heavy = Seq.Range(0, 1000).Count(i => r.Weighted(["rare", "common"], [0.1, 0.9]) == "common");
                var mean = Seq.Range(0, 4000).Map(i => r.Gaussian(10.0, 2.0)).Average(x => x);
                var uuid = r.Uuid();
                return $"{inRange}|{floats}|{Seq.Sum(deck)}|{distinct}|{heavy > 850 && heavy < 950}|{mean > 9.8 && mean < 10.2}|{uuid.Length}|{uuid[14]}";
            }
            """);
        Assert.Equal("true|true|36|3|true|true|36|4", result);
    }

    [Fact]
    public void Forked_generators_are_independent_and_deterministic()
    {
        var result = Run(Imports, """
            string Main() {
                var a = new Random(99); var b = new Random(99);
                var fa = a.Fork(); var fb = b.Fork();
                return $"{fa.NextInt() == fb.NextInt()}|{a.NextInt() == b.NextInt()}|{fa.NextInt() != a.NextInt()}";
            }
            """);
        Assert.Equal("true|true|true", result);
    }
}

public class SoftMathTests
{
    private static void Close(double expected, double actual, double ulps = 2)
    {
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual));
            return;
        }

        if (double.IsInfinity(expected) || expected == 0)
        {
            Assert.Equal(expected, actual, 1e-300);
            return;
        }

        var tolerance = Math.Abs(expected) * ulps * 2.3e-16;
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected:R}, got {actual:R}");
    }

    [Property(MaxTest = 500)]
    public void Exp_and_log_agree_with_the_platform_within_an_ulp_or_two(NormalFloat seed)
    {
        var x = Math.IEEERemainder(seed.Get, 700);
        Close(Math.Exp(x), SoftMath.Exp(x));
        var y = Math.Abs(seed.Get) + 1e-300;
        Close(Math.Log(y), SoftMath.Log(y));
        Close(Math.Log2(y), SoftMath.Log2(y), 4);
        Close(Math.Log10(y), SoftMath.Log10(y), 4);
    }

    [Property(MaxTest = 500)]
    public void Trigonometry_agrees_with_the_platform(NormalFloat seed)
    {
        var x = Math.IEEERemainder(seed.Get, 1e6);
        Close(Math.Sin(x), SoftMath.Sin(x), 4);
        Close(Math.Cos(x), SoftMath.Cos(x), 4);
        Close(Math.Atan(x), SoftMath.Atan(x));
        var unit = Math.IEEERemainder(seed.Get, 1);
        Close(Math.Asin(unit), SoftMath.Asin(unit), 4);
        Close(Math.Acos(unit), SoftMath.Acos(unit), 4);
        Close(Math.Atan2(x, unit), SoftMath.Atan2(x, unit));
    }

    [Theory]
    [InlineData(1e22)]
    [InlineData(1e300)]
    [InlineData(-123456789.123)]
    [InlineData(1.0e10)]
    public void Huge_arguments_use_exact_reduction(double x)
    {
        Close(Math.Sin(x), SoftMath.Sin(x), 4);
        Close(Math.Cos(x), SoftMath.Cos(x), 4);
    }

    [Fact]
    public void Special_values_and_exact_cases()
    {
        Assert.Equal(1024.0, SoftMath.Pow(2, 10));
        Assert.Equal(3.0, SoftMath.Log10(1000));
        Assert.Equal(10.0, SoftMath.Log2(1024));
        Assert.Equal(0.0, SoftMath.Log(1));
        Assert.Equal(1.0, SoftMath.Exp(0));
        Assert.Equal(double.NegativeInfinity, SoftMath.Log(0));
        Assert.True(double.IsNaN(SoftMath.Log(-1)));
        Assert.True(double.IsNaN(SoftMath.Pow(-8, 1.0 / 3)));
        Assert.Equal(-2.0, SoftMath.Cbrt(-8), 15);
        Assert.Equal(5.0, SoftMath.Hypot(3, 4));
        Close(Math.Pow(1.0001, 12345.5), SoftMath.Pow(1.0001, 12345.5), 64);
        Close(Math.Tanh(0.3), SoftMath.Tanh(0.3), 4);
        Close(Math.Sinh(3), SoftMath.Sinh(3), 4);
    }
}

public class BinderRegressionTests
{
    [Fact]
    public void Generic_methods_of_generic_classes_infer_type_arguments_from_lambdas()
    {
        var compilation = Compilation.Create(SyntaxTree.Parse("""
            module Test;
            class Box<T> {
                T Item;
                Box(T item) { Item = item; }
                Box<U> Map<U>(fn(T) -> U f) => new Box<U>(f(Item));
            }
            string Main() => new Box<int>(41).Map(x => $"{x + 1}").Item;
            """));
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
    }

    [Fact]
    public void Implementations_hide_the_interface_members_they_satisfy()
    {
        var compilation = Compilation.Create(SyntaxTree.Parse("""
            module Test;
            class Key : Hashable { int HashCode() => 7; }
            int Main() => "abc".HashCode() + new Key().HashCode() + 5.HashCode();
            """));
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
    }
}
