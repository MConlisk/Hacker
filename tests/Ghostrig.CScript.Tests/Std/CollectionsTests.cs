using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Tests.Std.StdProgram;

namespace Ghostrig.CScript.Tests.Std;

public class CollectionsTests
{
    private const string Imports = "import Std.Collections;\nimport Std.Seq;";

    [Fact]
    public void List_supports_indexing_insertion_removal_and_stable_sorting()
    {
        var result = Run(Imports, """
            string Main() {
                var xs = new List<int>([5, 3, 9]);
                xs.Add(1);
                xs.Insert(0, 7);
                xs[1] = 4;
                xs.Remove(9);
                var removed = xs.RemoveAt(0);
                xs.Sort();
                var pairs = new List<(int, string)>();
                pairs.Add((2, "b")); pairs.Add((1, "x")); pairs.Add((2, "a")); pairs.Add((1, "y"));
                pairs.SortBy(p => p.Item1);
                return $"{xs}|{removed}|{xs.Count}|{xs.IndexOf(4)}|{xs.BinarySearch(4)}|{xs.BinarySearch(2)}|{pairs}|{xs.RemoveAll(x => x > 3)}|{xs}";
            }
            """);
        Assert.Equal("[1, 3, 4]|7|3|2|2|-2|[(1, x), (1, y), (2, b), (2, a)]|1|[1, 3]", result);
    }

    [Fact]
    public void Map_preserves_insertion_order_and_supports_upsert_and_views()
    {
        var result = Run(Imports, """
            string Main() {
                var m = new Map<string, int>();
                m["b"] = 1; m["a"] = 2; m["c"] = 3;
                m["b"] = 10;                       // re-assignment keeps position
                m.Remove("a");
                m["a"] = 4;                        // re-insertion goes to the end
                m.Upsert("c", 0, v => v + 1);
                m.Upsert("d", 100, v => v + 1);
                var total = 0;
                foreach (var entry in m) { total += entry.Item2; }
                var got = m.Get("zzz");
                return $"{m}|{m.Keys.Join(",")}|{total}|{got == null}|{m.GetOr("q", -1)}|{m.ContainsValue(4)}";
            }
            """);
        Assert.Equal("{b: 10, c: 4, a: 4, d: 100}|b,c,a,d|118|true|-1|true", result);
    }

    [Fact]
    public void Missing_map_keys_raise_IndexError()
    {
        var error = ErrorOf(Imports, """string Main() { var m = new Map<string, int>(); return $"{m["nope"]}"; }""");
        Assert.Contains("IndexError", error);
        Assert.Contains("nope", error);
    }

    [Fact]
    public void Doc7_mini_lru_sample_works_against_Std_Map()
    {
        var program = Load("""
            module Test;
            import Std.Collections;
            import Std.Seq;

            class MiniLru<K, V> where K : Hashable {
                private Map<K, V> items = new Map<K, V>();
                private int capacity;
                MiniLru(int capacity) { this.capacity = capacity; }

                V? Get(K key) {
                    if (!items.ContainsKey(key)) return null;
                    V value = items[key];
                    items.Remove(key); items[key] = value;
                    return value;
                }

                void Put(K key, V value) {
                    items.Remove(key);
                    if (items.Count >= capacity) items.Remove(items.Keys.First());
                    items[key] = value;
                }

                string Dump() => items.Keys.Join(",");
            }

            string Main() {
                var lru = new MiniLru<string, int>(2);
                lru.Put("a", 1); lru.Put("b", 2);
                lru.Get("a");
                lru.Put("c", 3);              // evicts b
                return lru.Dump();
            }
            """);
        Assert.Equal("a,c", program.Text());
    }

    [Fact]
    public void Class_keys_use_their_HashCode_and_Equatable_implementations()
    {
        var result = Run(Imports, """
            class Endpoint : Equatable<Endpoint>, Hashable {
                string Host; int Port;
                Endpoint(string host, int port) { Host = host; Port = port; }
                bool Equals(Endpoint other) => Host == other.Host && Port == other.Port;
                int HashCode() => Host.HashCode() * 31 + Port;
            }
            class Opaque { }
            string Main() {
                var seen = new Set<Endpoint>();
                seen.Add(new Endpoint("10.0.0.1", 80));
                var dup = seen.Add(new Endpoint("10.0.0.1", 80));
                seen.Add(new Endpoint("10.0.0.1", 8443));
                var o = new Opaque();
                var ids = new Set<Opaque>();
                ids.Add(o); ids.Add(new Opaque()); ids.Add(o);
                return $"{seen.Count}|{dup}|{seen.Contains(new Endpoint("10.0.0.1", 8443))}|{ids.Count}";
            }
            """);
        Assert.Equal("2|false|true|2", result);
    }

    [Fact]
    public void Sets_sorted_collections_and_structural_keys()
    {
        var result = Run(Imports, """
            string Main() {
                var a = new Set<int>([1, 2, 3, 4]);
                var b = new Set<int>([3, 4, 5]);
                var sm = new SortedMap<string, int>();
                sm["delta"] = 4; sm["alpha"] = 1; sm["charlie"] = 3; sm["bravo"] = 2;
                var ss = new SortedSet<int>((x, y) => y - x);   // descending comparer
                foreach (var x in [5, 1, 9, 1, 7]) { ss.Add(x); }
                var byTuple = new Map<(string, int), string>();
                byTuple[("ftp", 2121)] = "open";
                return $"{a.Union(b)}|{a.Intersect(b)}|{a.Except(b)}|{sm}|{sm.Floor("bz")}|{sm.Ceiling("bz")}|{sm.Range("b", "d").Count()}|{ss}|{byTuple[("ftp", 2121)]}";
            }
            """);
        Assert.Equal("{1, 2, 3, 4, 5}|{3, 4}|{1, 2}|{alpha: 1, bravo: 2, charlie: 3, delta: 4}|bravo|charlie|2|{9, 7, 5, 1}|open", result);
    }

    [Fact]
    public void Queues_stacks_deques_ring_buffers_and_priority_queues()
    {
        var result = Run(Imports, """
            string Main() {
                var q = new Queue<int>(); q.Enqueue(1); q.Enqueue(2); q.Enqueue(3);
                var s = new Stack<string>(); s.Push("a"); s.Push("b");
                var d = new Deque<int>(); d.PushBack(2); d.PushFront(1); d.PushBack(3);
                var ring = new RingBuffer<int>(3);
                for (var i = 1; i <= 5; i++) { ring.Add(i); }
                var pq = new PriorityQueue<string, int>();
                pq.Enqueue("low", 5); pq.Enqueue("urgent", 1); pq.Enqueue("first-tie", 3); pq.Enqueue("second-tie", 3);
                var order = new List<string>();
                while (!pq.IsEmpty) { order.Add(pq.Dequeue()); }
                return $"{q.Dequeue()}{q.Peek()}|{s.Pop()}{s}|{d.PopBack()}{d}|{ring}|{ring.Oldest()}|{order}|{q.TryDequeue()}{q.TryDequeue()}{q.TryDequeue() == null}";
            }
            """);
        Assert.Equal("12|b[a]|3[1, 2]|[3, 4, 5]|3|[urgent, first-tie, second-tie, low]|23true", result);
    }

    [Fact]
    public void Bitsets_grids_matrices_and_multimaps()
    {
        var result = Run(Imports, """
            string Main() {
                var bits = new BitSet(10);
                bits.Set(1); bits.Set(3); bits.Set(9); bits.Flip(3);
                var other = new BitSet(10); other.Set(9); other.Set(2);
                var both = bits.Copy(); both.Or(other);
                var grid = new Grid2D<char>(3, 2, '.');
                grid.Set(1, 1, '#');
                var corner = grid.Neighbors4(0, 0);
                var m = new Matrix<int>(2, 3, 0);
                m.Set(0, 2, 7);
                var mm = new Multimap<string, int>();
                mm.Add("x", 1); mm.Add("y", 2); mm.Add("x", 3);
                return $"{bits}|{bits.Count}|{both.NextSet(2)}|{grid}|{corner.Length}|{m.Transpose().Get(2, 0)}|{mm.Get("x").Length}|{mm.Count}|{mm.KeyCount}";
            }
            """);
        Assert.Equal("0100000001|2|2|...\n.#.|2|7|2|3|2", result);
    }

    [Fact]
    public void Modifying_a_collection_while_iterating_it_raises_Error()
    {
        var error = ErrorOf(Imports, """
            string Main() {
                var xs = new List<int>([1, 2, 3]);
                foreach (var x in xs) { xs.Add(x); }
                return "unreachable";
            }
            """);
        Assert.Contains("modified during iteration", error);
    }

    [Fact]
    public void Faulted_events_roll_back_native_collections()
    {
        var program = Load("""
            module Test;
            import Std.Collections;
            List<int> log = new List<int>();
            Map<string, int> counts = new Map<string, int>();
            LruCache<int, string> cache = new LruCache<int, string>(2);
            void Ok() { log.Add(1); counts["ok"] = 1; cache.Put(1, "one"); }
            void Fail() {
                log.Add(2); log.Sort(); log.Clear();
                counts["fail"] = 2; counts.Remove("ok");
                cache.Put(2, "two"); cache.Put(3, "three"); cache.Get(1);
                throw new Error("boom");
            }
            string Dump() => $"{log}|{counts}|{cache.Count}|{cache.Contains(1)}|{cache.Evictions}|{cache.Misses}";
            """);
        program.Vm.StartEvent(program.Image.FindFunction("Test", "Ok")!, []);
        program.Vm.RunUntilIdle();
        program.Vm.StartEvent(program.Image.FindFunction("Test", "Fail")!, []);
        program.Vm.RunUntilIdle();
        var fault = Assert.Single(program.Vm.Faults);
        Assert.True(fault.RolledBack);
        Assert.Equal("[1]|{ok: 1}|1|true|0|0", program.Text("Dump"));
    }

    [Fact]
    public void Caches_report_hits_misses_and_evict_by_policy()
    {
        var result = Run(Imports, """
            string Main() {
                var lru = new LruCache<string, int>(2);
                lru.Put("a", 1); lru.Put("b", 2);
                lru.Get("a");            // a is now most recent
                lru.Put("c", 3);         // evicts b
                var missB = lru.Get("b") == null;
                var lfu = new LfuCache<string, int>(2);
                lfu.Put("x", 1); lfu.Put("y", 2);
                lfu.Get("x"); lfu.Get("x");
                lfu.Put("z", 3);         // evicts y (least frequently used)
                return $"{lru.Keys.Join(",")}|{missB}|{lru.Hits}/{lru.Misses}/{lru.Evictions}|{lru.HitRate}|{lfu.Contains("y")}|{lfu.Frequency("x")}|{lfu.Evictions}";
            }
            """);
        Assert.Equal("a,c|true|1/1/1|0.5|false|3|1", result);
    }

    [Fact]
    public void Ttl_cache_expires_on_the_simulated_clock()
    {
        var program = Load("""
            module Test;
            import Std.Collections;
            TtlCache<string, string> dns = new TtlCache<string, string>(Duration.FromSeconds(30));
            void Prime() { dns.Put("verbena-and-rust.ny56", "10.56.4.20"); dns.Put("short", "x", Duration.FromSeconds(1)); }
            async Task<string> Check() {
                var fresh = dns.Get("verbena-and-rust.ny56") ?? "miss";
                await Task.Delay(Duration.FromSeconds(5));
                var shortGone = dns.Contains("short");
                await Task.Delay(Duration.FromSeconds(30));
                var stale = dns.Get("verbena-and-rust.ny56") ?? "miss";
                return $"{fresh}|{shortGone}|{stale}|{dns.Hits}/{dns.Misses}/{dns.Expirations}";
            }
            """);
        program.Call("Prime");
        Assert.Equal("10.56.4.20|false|miss|1/1/1", program.Call("Check").AsString);
    }

    [Fact]
    public void Probabilistic_structures_behave_within_their_guarantees()
    {
        var result = Run(Imports, """
            string Main() {
                var bloom = new BloomFilter<string>(1000, 0.01);
                for (var i = 0; i < 1000; i++) { bloom.Add($"host-{i}"); }
                var falseNegatives = 0;
                for (var i = 0; i < 1000; i++) { if (!bloom.MightContain($"host-{i}")) falseNegatives++; }
                var falsePositives = 0;
                for (var i = 0; i < 10000; i++) { if (bloom.MightContain($"other-{i}")) falsePositives++; }
                var sketch = new CountMinSketch<string>(256, 4);
                for (var i = 0; i < 50; i++) { sketch.Add("hot"); }
                sketch.Add("cold", 3);
                var hll = new HyperLogLog<int>(12);
                for (var i = 0; i < 20000; i++) { hll.Add(i % 5000); }
                var estimate = hll.Estimate();
                return $"{falseNegatives}|{falsePositives < 250}|{sketch.Estimate("hot") >= 50}|{sketch.Estimate("cold") >= 3}|{sketch.Total}|{estimate > 4700 && estimate < 5300}";
            }
            """);
        Assert.Equal("0|true|true|true|53|true", result);
    }

    [Fact]
    public void Tries_interval_trees_skip_lists_pools_and_hash_rings()
    {
        var result = Run(Imports, """
            class Buffer { }
            string Main() {
                var trie = new Trie();
                foreach (var w in ["net", "netcard", "network", "nexus", "dns"]) { trie.Add(w); }
                var tree = new IntervalTree<string>();
                tree.Add(0, 10, "boot"); tree.Add(5, 15, "scan"); tree.Add(20, 30, "idle");
                var skip = new SkipList<int>();
                foreach (var x in [50, 10, 40, 20, 30, 20]) { skip.Add(x); }
                skip.Remove(40);
                var created = 0;
                var pool = new ObjectPool<Buffer>(() => { created++; return new Buffer(); }, 4);
                var b1 = pool.Rent(); pool.Return(b1); var b2 = pool.Rent();
                var ring = new ConsistentHashRing<string>(64);
                ring.Add("node-a"); ring.Add("node-b"); ring.Add("node-c");
                var before = new List<string>();
                for (var i = 0; i < 200; i++) { before.Add(ring.NodeFor($"key-{i}")); }
                ring.Remove("node-c");
                var moved = 0;
                for (var i = 0; i < 200; i++) { if (before[i] != "node-c" && ring.NodeFor($"key-{i}") != before[i]) moved++; }
                return $"{trie.WithPrefix("net").Join(",")}|{trie.LongestPrefixOf("networking")}|{trie.HasPrefix("dn")}|{tree.Overlapping(8, 12).Length}|{tree.At(25)[0]}|{skip}|{skip.Range(15, 45).Join(",")}|{b1 == b2}|{created}|{pool.Reuses}|{moved}";
            }
            """);
        Assert.Equal("net,netcard,network|network|true|2|idle|[10, 20, 30, 50]|20,30|true|1|1|0", result);
    }

    [Fact]
    public void Read_only_views_reflect_changes_but_expose_no_mutators()
    {
        var program = Load("""
            module Test;
            import Std.Collections;
            string Main() {
                var xs = new List<int>([1, 2]);
                var view = xs.AsReadOnly();
                xs.Add(3);
                return $"{view.Count}|{view[2]}|{view}";
            }
            """);
        Assert.Equal("3|3|[1, 2, 3]", program.Text());

        var compilation = Ghostrig.CScript.Semantics.Compilation.Create(Ghostrig.CScript.Syntax.SyntaxTree.Parse("""
            module Test;
            import Std.Collections;
            void Main() { var view = new List<int>().AsReadOnly(); view.Add(1); }
            """));
        Assert.True(compilation.HasErrors);
    }
}
