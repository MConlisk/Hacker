using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Testing;
using static Ghostrig.CScript.Tests.Std.StdProgram;

namespace Ghostrig.CScript.Tests.Std;

public class GraphTests
{
    private const string Imports = "import Std.Graph;\nimport Std.Collections;";

    [Fact]
    public void Traversal_and_shortest_paths_on_the_dark_subnet()
    {
        var result = Run(Imports, """
            string Main() {
                var net = new WeightedGraph<string>(false);
                net.AddEdge("gateway", "relay-a", 4.0);
                net.AddEdge("gateway", "relay-b", 1.0);
                net.AddEdge("relay-b", "relay-a", 1.0);
                net.AddEdge("relay-a", "vault", 1.0);
                net.AddEdge("relay-b", "decoy", 5.0);
                var path = net.ShortestPath("gateway", "vault")!;
                var dist = net.Dijkstra("gateway");
                var hops = net.Hops("gateway");
                var astar = net.AStar("gateway", "vault", n => 0.0)!;
                return $"{string.Join(">", path)}|{net.PathCost(path)}|{dist["decoy"]}|{hops["vault"]}|{string.Join(",", net.Bfs("gateway"))}|{string.Join(",", net.Dfs("gateway"))}|{astar.Length}|{net.ShortestPaths()[("decoy", "vault")]}";
            }
            """);
        Assert.Equal("gateway>relay-b>relay-a>vault|3.0|6.0|2|gateway,relay-a,relay-b,vault,decoy|gateway,relay-a,relay-b,decoy,vault|4|7.0", result);
    }

    [Fact]
    public void Structure_ordering_components_and_cycles()
    {
        var result = Run(Imports, """
            string Main() {
                var build = new Graph<string>(true);
                build.AddEdge("lexer", "parser"); build.AddEdge("parser", "binder"); build.AddEdge("binder", "vm"); build.AddEdge("lexer", "vm");
                var order = build.TopologicalSort();
                var loop = new Graph<int>(true);
                loop.AddEdge(1, 2); loop.AddEdge(2, 3); loop.AddEdge(3, 1); loop.AddEdge(3, 4); loop.AddNode(9);
                var islands = new Graph<string>(false);
                islands.AddEdge("a", "b"); islands.AddEdge("b", "c"); islands.AddEdge("x", "y"); islands.AddEdge("c", "a");
                var mst = islands.MinimumSpanningTree();
                return $"{string.Join(",", order)}|{build.HasCycle()}|{loop.HasCycle()}|{loop.StronglyConnectedComponents()[0].Length}|{loop.Cycles(10).Length}|{islands.ConnectedComponents().Length}|{islands.Cycles(10)[0].Length}|{mst.EdgeCount}|{loop.InDegree(1)}|{loop.Degree(3)}";
            }
            """);
        Assert.Equal("lexer,parser,binder,vm|false|true|3|1|2|3|3|1|3", result);
        Assert.Contains("cycle", ErrorOf(Imports, """string Main() { var g = new Graph<int>(true); g.AddEdge(1, 2); g.AddEdge(2, 1); return $"{g.TopologicalSort().Length}"; }"""));
    }

    [Fact]
    public void Centrality_and_dot_export()
    {
        var result = Run(Imports, """
            string Main() {
                var star = new Graph<string>(false);
                foreach (var leaf in ["a", "b", "c", "d"]) { star.AddEdge("hub", leaf); }
                var between = star.BetweennessCentrality();
                var degree = star.DegreeCentrality();
                return $"{between["hub"]}|{between["a"]}|{degree["hub"]}|{star.ToDot().StartsWith("graph G {")}|{star.ToDot().Contains("\"hub\" -- \"a\"")}";
            }
            """);
        Assert.Equal("1.0|0.0|1.0|true|true", result);
    }

    [Fact]
    public void Graph_mutations_roll_back_with_a_faulted_event()
    {
        var program = Load("""
            module Test;
            import Std.Graph;
            Graph<string> g = new Graph<string>(true);
            void Seed() { g.AddEdge("a", "b"); }
            void Break() { g.AddEdge("b", "c"); g.RemoveNode("a"); throw new Error("x"); }
            string Dump() => $"{g.NodeCount}|{g.EdgeCount}|{g.HasEdge("a", "b")}";
            """);
        program.Call("Seed");
        program.Vm.StartEvent(program.Image.FindFunction("Test", "Break")!, []);
        program.Vm.RunUntilIdle();
        Assert.Equal("2|1|true", program.Text("Dump"));
    }
}

public class DataTests
{
    private const string Imports = "import Std.Data;\nimport Std.Text;";

    [Fact]
    public void Tables_filter_join_group_and_pivot()
    {
        var result = Run(Imports, """
            string Main() {
                var manifests = Table.FromCsv("crate,dock,kg,day\nBIO-01,P44,120,Mon\nBIO-09,P44,80,Tue\nORC-03,P12,15,Mon\nBIO-02,P12,60,Tue\n");
                var docks = Table.FromCsv("dock,owner\nP44,Lotus\nP12,Iron\n");
                var heavy = manifests.Where(r => r.GetInt("kg") >= 60).OrderByDescending("kg");
                var joined = manifests.Join(docks, "dock", "dock");
                var perOwner = joined.GroupBy("owner", "kg", Aggregate.Sum);
                var pivot = manifests.Pivot("dock", "day", "kg", Aggregate.Sum);
                var total = manifests.Aggregate("kg", Aggregate.Sum);
                var withTons = manifests.WithColumn("tons", r => r.GetFloat("kg") / 1000.0).Select("crate", "tons");
                return $"{heavy.RowCount}|{heavy.Row(0).GetString("crate")}|{joined.Columns.Length}|{perOwner.Row(0).Get("owner")}:{perOwner.Row(0).Get("Sum(kg)")}|{string.Join(",", pivot.Columns)}|{pivot.Get(1, "Mon")}|{total}|{withTons.Get(2, "tons")}|{manifests.Aggregate("dock", Aggregate.CountDistinct)}";
            }
            """);
        Assert.Equal("3|BIO-01|6|Lotus:200|dock,Mon,Tue|15|275|0.015|2", result);
    }

    [Fact]
    public void Tables_round_trip_csv_json_and_print()
    {
        var result = Run(Imports, """
            string Main() {
                var t = new Table(["host", "port", "open"]).AddRow("verbena", 2121, true).AddRow("aegis", 8443, null);
                var json = t.ToJson();
                var back = Table.FromJson(JsonValue.Parse(json));
                return $"{json}|{back.RowCount}|{back.Row(1).IsNull("open")}|{t.ToCsv().Replace("\r\n", ";")}\n{t}";
            }
            """);
        Assert.Equal("""
            [{"host":"verbena","port":2121,"open":true},{"host":"aegis","port":8443,"open":null}]|2|true|host,port,open;verbena,2121,true;aegis,8443,;
            host     port  open
            -------  ----  ----
            verbena  2121  true
            aegis    8443  null
            """.Replace("\r\n", "\n"), result);
    }
}

public class GameTests
{
    private const string Imports = "import Std.Game;\nimport Std.Math;\nimport Std.Collections;";

    [Fact]
    public void Fixed_step_loop_easing_tweens_and_colours()
    {
        var result = Run(Imports, """
            string Main() {
                var loop = new GameLoop(Duration.FromMilliseconds(10));
                var first = loop.Advance(Duration.FromMilliseconds(25), 5);
                var second = loop.Advance(Duration.FromMilliseconds(5), 5);
                var flood = loop.Advance(Duration.FromSeconds(10), 4);
                var tween = new Tween(0.0, 100.0, Duration.FromSeconds(2), "outQuad");
                var amber = Color.Parse("#ffb000");
                var mid = Color.Black.Lerp(Color.White, 0.5);
                return $"{first}|{second}|{flood}|{loop.TotalSteps}|{Easing.InQuad(0.5)}|{Easing.OutBounce(1.0)}|{tween.ValueAt(Duration.FromSeconds(1))}|{tween.IsDone(Duration.FromSeconds(3))}|{amber.R},{amber.G},{amber.B}|{mid}|{Color.FromHsv(120.0, 1.0, 1.0)}|{Palette.Nearest(Palette.Cga, Color.Parse("#a0a0a8"))}|{Palette.Accessible.Length}";
            }
            """);
        Assert.Equal("2|1|4|7|0.25|1.0|75.0|true|255,176,0|#808080|#00ff00|#aaaaaa|8", result);
    }

    [Fact]
    public void Collision_spatial_hash_bodies_and_pathfinding()
    {
        var result = Run(Imports, """
            string Main() {
                var player = new Rect(0, 0, 10, 10);
                var wall = new Rect(8, 0, 10, 10);
                var push = Collision.RectPenetration(player, wall)!;
                var toi = Collision.Sweep(new Rect(0, 0, 2, 2), new Vector2(10, 0), new Rect(5, 0, 2, 2));
                var hash = new SpatialHash<string>(16);
                hash.Insert("ship", new Rect(0, 0, 8, 8)); hash.Insert("rock", new Rect(40, 40, 8, 8)); hash.Insert("bolt", new Rect(4, 4, 2, 2));
                var near = hash.Query(new Rect(0, 0, 10, 10));
                hash.Update("rock", new Rect(2, 2, 4, 4));
                var body = new Body(Vector2.Zero, 2.0);
                body.ApplyForce(new Vector2(4, 0));
                body.Step(1.0);
                var grid = new Grid2D<bool>(5, 3, true);
                grid.Set(1, 0, false); grid.Set(1, 1, false);
                var path = Pathfinding.FindPath(grid, (0, 0), (2, 0), false)!;
                var field = Pathfinding.DistanceField(grid, (0, 0));
                return $"{push}|{toi}|{string.Join(",", near)}|{hash.QueryPoint(new Vector2(3, 3)).Length}|{body.Velocity}|{body.Position}|{path.Length}|{field.Get(2, 0)}|{Collision.CircleRect(new Circle(new Vector2(0, 0), 1), new Rect(0.5, 0.5, 1, 1))}";
            }
            """);
        Assert.Equal("(-2.0, 0.0)|0.3|ship,bolt|2|(2.0, 0.0)|(2.0, 0.0)|7|6|true", result);
    }

    [Fact]
    public void State_machines_cameras_tiles_particles_and_input()
    {
        var result = Run(Imports, """
            string Main() {
                var log = new List<string>();
                var ai = new StateMachine("patrol").Allow("patrol", "spot", "chase").Allow("chase", "lose", "patrol");
                ai.OnTransition((from, t, to) => log.Add($"{from}-{t}->{to}"));
                ai.Fire("spot");
                var refused = ai.TryFire("spot");
                var cam = new Camera2D(320, 200);
                cam.Position = new Vector2(100, 100); cam.Zoom = 2.0;
                var screen = cam.WorldToScreen(new Vector2(110, 100));
                var map = TileMap.Parse("####\n#..#\n####", 8);
                var fx = new ParticleSystem(42, 100);
                fx.Emit(Vector2.Zero, 10, 0.0, 1.0, 1.0, 2.0, 0.5, Color.White);
                fx.Step(0.25);
                var alive = fx.Count;
                fx.Step(0.5);
                var keys = new InputMap().Bind("fire", "Space").Bind("fire", "MouseLeft");
                keys.Press("Space");
                var trig = keys.WasTriggered("fire");
                keys.Update();
                keys.Press("MouseLeft");
                return $"{ai.State}|{refused}|{log[0]}|{screen}|{cam.ScreenToWorld(screen)}|{map.Get(1, 1)}|{map.Get(0, 0)}|{map.WorldToTile(new Vector2(9, 9)).Item1}|{alive}|{fx.Count}|{trig}|{keys.WasTriggered("fire")}|{keys.IsActive("fire")}";
            }
            """);
        Assert.Equal("chase|false|patrol-spot->chase|(180.0, 100.0)|(110.0, 100.0)|0|1|1|10|0|true|false|true", result);
    }

    [Fact]
    public void Particle_simulation_is_deterministic_for_a_seed()
    {
        const string body = """
            string Main() {
                var fx = new ParticleSystem(7, 50);
                fx.Gravity = new Vector2(0, 9.8);
                fx.Emit(new Vector2(10, 10), 5, 1.0, 2.0, 1.0, 3.0, 2.0, Color.White);
                fx.Step(0.1);
                return $"{fx.Particles[3].Position}";
            }
            """;
        Assert.Equal(Run(Imports, body), Run(Imports, body));
    }
}

public class TestLibraryTests
{
    private const string Imports = "import Std.Test;\nimport Std.Collections;";

    [Fact]
    public void Assertions_pass_silently_and_fail_with_EASSERT()
    {
        Assert.Equal("ok", Run(Imports, """
            string Main() {
                Assert.True(1 < 2); Assert.Equal("a", "a"); Assert.Near(0.1 + 0.2, 0.3, 1e-9);
                Assert.SequenceEqual([1, 2, 3], new List<int>([1, 2, 3]));
                var e = Assert.Throws(() => { var xs = new int[1]; xs[5] = 1; }, "IndexError");
                Assert.Contains(e.Message, "out of range");
                return "ok";
            }
            """));
        var error = ErrorOf(Imports, """string Main() { Assert.Equal(2056, 2057); return "unreachable"; }""");
        Assert.Contains("AssertionFailed [EASSERT]", error);
        Assert.Contains("Expected: 2056", error);
        Assert.Contains("Expected an error", ErrorOf(Imports, """string Main() { Assert.Throws(() => { }); return ""; }"""));
    }

    [Fact]
    public void Property_testing_shrinks_to_a_minimal_counterexample()
    {
        Assert.Equal("ok", Run(Imports, """
            string Main() {
                Property.ForAll(Gens.Int(-1000, 1000), x => x + 0 == x);
                Property.ForAll(Gens.ArrayOf(Gens.Int(0, 9), 8), xs => xs.Length <= 8, 200, 99);
                return "ok";
            }
            """));
        var error = ErrorOf(Imports, """string Main() { Property.ForAll(Gens.Int(0, 10000), x => x < 500); return ""; }""");
        Assert.Contains("Falsified", error);
        Assert.EndsWith(": 500", error);
        var listError = ErrorOf(Imports, """string Main() { Property.ForAll(Gens.ArrayOf(Gens.Int(0, 100), 10), xs => xs.Length < 3); return ""; }""");
        Assert.Contains("[0, 0, 0]", listError);
    }

    [Fact]
    public void Bench_and_debug_report_steps_and_inspect_objects()
    {
        var program = Load("""
            module Test;
            import Std.Test;
            import Std.Collections;
            class Session { string User; int Port; Session(string u, int p) { User = u; Port = p; } }
            string Main() {
                var cheap = Bench.Steps(() => { var x = 1; });
                var costly = Bench.Run("loop", () => { var s = 0; for (var i = 0; i < 100; i++) { s += i; } }, 3);
                Debug.Trace("probe");
                var cache = new LruCache<int, int>(2); cache.Put(1, 1); cache.Get(1); cache.Get(2);
                return $"{cheap < costly.StepsPerIteration}|{costly.Iterations}|{Debug.Inspect(new Session("kael", 2121)).Replace("\n", " ")}|{Debug.Inspect(cache)}|{Debug.StepCount() > 0}";
            }
            """);
        Assert.Equal("true|3|Session {   User: \"kael\"   Port: 2121 }|LruCache { hits 1, misses 1, evictions 0, hit rate 0.5 }|true", program.Text());
        Assert.Contains("probe", program.Vm.TraceLog);
    }

    [Fact]
    public void Test_runner_discovers_tests_cases_and_fixtures()
    {
        var compilation = Compilation.Create(SyntaxTree.Parse("""
            module CacheTests;
            import Std.Test;
            import Std.Collections;

            LruCache<string, int> cache = new LruCache<string, int>(2);
            int setups = 0;

            @Setup
            void Prepare() { setups++; cache.Put("a", 1); }

            @Test
            void Hit_counts() { cache.Get("a"); Assert.Equal(1, cache.Hits); Assert.Equal(1, setups); }

            @Test
            void Fails_on_purpose() { Assert.Equal(2, cache.Count); }

            @TestCase(1, 2, 3)
            @TestCase(-4, 4, 0)
            @TestCase(2, 2, 5)
            void Adds(int a, int b, int expected) { Assert.Equal(expected, a + b); }

            @Test
            bool Returns_true() => cache.Contains("a");
            """, "CacheTests.csc"));
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        var results = TestRunner.Run(compilation);
        Assert.Equal(6, results.Length);
        Assert.Equal(["Hit_counts", "Fails_on_purpose", "Adds(1, 2, 3)", "Adds(-4, 4, 0)", "Adds(2, 2, 5)", "Returns_true"], results.Select(r => r.Name));
        Assert.Equal([true, false, true, true, false, true], results.Select(r => r.Passed));
        Assert.Contains("Expected: 2", results[1].Message);
        Assert.All(results, r => Assert.True(r.Steps > 0));
    }
}
