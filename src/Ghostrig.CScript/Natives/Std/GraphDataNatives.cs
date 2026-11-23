using System.Globalization;
using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

#region Graph state

/// <summary>Adjacency in insertion order: node → (successors, predecessors), each an ordered table of neighbour → weight.</summary>
internal sealed class GraphState(bool directed, IEqualityComparer<Value> comparer) : NativeState
{
    private readonly IEqualityComparer<Value> _comparer = comparer;

    public bool Directed { get; } = directed;
    public OrderedTable Nodes { get; } = new(comparer);
    public long EdgeCount { get; set; }

    public sealed class Adjacency(IEqualityComparer<Value> comparer)
    {
        public OrderedTable Out { get; } = new(comparer);
        public OrderedTable In { get; } = new(comparer);
    }

    public Adjacency Of(Value node) => (Adjacency)Nodes.ValueAt(SlotOf(node)).Object!;

    public int SlotOf(Value node) => Nodes.TryGetSlot(node, out var slot) ? slot : -1;

    public bool AddNode(Value node) => Nodes.TryAdd(node, Value.FromObject(new Adjacency(_comparer)));

    public void AddEdge(Value from, Value to, double weight)
    {
        AddNode(from);
        AddNode(to);
        var (a, b) = (Of(from), Of(to));
        if (a.Out.Set(to, Value.FromFloat(weight)))
        {
            EdgeCount++;
        }

        b.In.Set(from, Value.FromFloat(weight));
        if (!Directed)
        {
            b.Out.Set(from, Value.FromFloat(weight));
            a.In.Set(to, Value.FromFloat(weight));
        }
    }

    public bool RemoveEdge(Value from, Value to)
    {
        if (SlotOf(from) < 0 || SlotOf(to) < 0 || !Of(from).Out.Remove(to, out _))
        {
            return false;
        }

        Of(to).In.Remove(from, out _);
        if (!Directed)
        {
            Of(to).Out.Remove(from, out _);
            Of(from).In.Remove(to, out _);
        }

        EdgeCount--;
        return true;
    }

    protected internal override object CaptureSnapshot()
    {
        var adjacency = Nodes.Slots().Select(s => (s, ((Adjacency)Nodes.ValueAt(s).Object!).Out.Capture(), ((Adjacency)Nodes.ValueAt(s).Object!).In.Capture())).ToArray();
        return (Nodes.Capture(), adjacency, EdgeCount);
    }

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (nodes, adjacency, edges) = ((object, (int, object, object)[], long))snapshot;
        Nodes.Restore(nodes);
        foreach (var (slot, outEdges, inEdges) in adjacency)
        {
            var restored = new Adjacency(_comparer);
            restored.Out.Restore(outEdges);
            restored.In.Restore(inEdges);
            Nodes.SetValueAt(slot, Value.FromObject(restored));
        }

        EdgeCount = edges;
    }
}

/// <summary>An integer-indexed snapshot of a graph used by the algorithms.</summary>
internal sealed class GraphView
{
    public GraphView(NativeContext c, GraphState g)
    {
        Directed = g.Directed;
        Nodes = [.. g.Nodes.Keys()];
        Index = new Dictionary<Value, int>(c.Comparer);
        for (var i = 0; i < Nodes.Length; i++)
        {
            Index[Nodes[i]] = i;
        }

        Out = new List<(int, double)>[Nodes.Length];
        In = new List<(int, double)>[Nodes.Length];
        for (var i = 0; i < Nodes.Length; i++)
        {
            var adjacency = g.Of(Nodes[i]);
            Out[i] = [.. adjacency.Out.Slots().Select(s => (Index[adjacency.Out.KeyAt(s)], adjacency.Out.ValueAt(s).AsFloat))];
            In[i] = [.. adjacency.In.Slots().Select(s => (Index[adjacency.In.KeyAt(s)], adjacency.In.ValueAt(s).AsFloat))];
        }

        c.Charge(Nodes.Length + Out.Sum(o => o.Count));
    }

    public bool Directed { get; }
    public Value[] Nodes { get; }
    public Dictionary<Value, int> Index { get; }
    public List<(int To, double Weight)>[] Out { get; }
    public List<(int From, double Weight)>[] In { get; }
    public int Count => Nodes.Length;
    public bool Unweighted => Out.All(edges => edges.All(e => e.Weight == 1));

    public int Require(NativeContext c, Value node) =>
        Index.TryGetValue(node, out var i) ? i : throw c.Throw("IndexError", $"Node '{Display(c, node)}' is not in the graph.");

    /// <summary>Neighbours ignoring direction (for weak connectivity).</summary>
    public IEnumerable<int> Undirected(int node) => Out[node].Select(e => e.To).Concat(In[node].Select(e => e.From)).Distinct();
}

#endregion

internal sealed record TableState(string[] Columns, Value[][] Rows)
{
    public int Column(NativeContext c, string name)
    {
        var i = System.Array.IndexOf(Columns, name);
        return i >= 0 ? i : throw c.Throw("IndexError", $"No column named '{name}' (columns: {string.Join(", ", Columns)}).");
    }
}

internal sealed record RowState(TableState Table, int Index);

/// <summary>Host implementations of <c>Std.Graph</c> and <c>Std.Data</c>.</summary>
internal static class GraphDataNatives
{
    public static void Register(NativeRegistry r)
    {
        RegisterGraph(r);
        RegisterTable(r);
    }

    #region Graph

    private static GraphState G(NativeContext c, Value v) => c.State<GraphState>(v);

    private static Value Nodes(NativeContext c, GraphView view, IEnumerable<int> indices) => c.NewArray([.. indices.Select(i => view.Nodes[i])]);

    private static (double[] Distance, int[] Previous) Dijkstra(NativeContext c, GraphView g, int start, Func<int, double>? heuristic = null, int goal = -1)
    {
        if (g.Out.Any(edges => edges.Any(e => e.Weight < 0)))
        {
            throw c.Throw("ArgumentError", "Shortest paths need non-negative edge weights.");
        }

        var distance = Enumerable.Repeat(double.PositiveInfinity, g.Count).ToArray();
        var previous = Enumerable.Repeat(-1, g.Count).ToArray();
        var done = new bool[g.Count];
        var queue = new PriorityQueue<int, (double, int)>();
        distance[start] = 0;
        queue.Enqueue(start, (heuristic?.Invoke(start) ?? 0, start));
        while (queue.TryDequeue(out var u, out _))
        {
            if (done[u])
            {
                continue;
            }

            done[u] = true;
            c.Charge(1 + (long)Math.Log2(g.Count + 1));
            if (u == goal)
            {
                break;
            }

            foreach (var (v, w) in g.Out[u])
            {
                var candidate = distance[u] + w;
                if (candidate < distance[v])
                {
                    distance[v] = candidate;
                    previous[v] = u;
                    queue.Enqueue(v, (candidate + (heuristic?.Invoke(v) ?? 0), v));
                }
            }
        }

        return (distance, previous);
    }

    private static List<int>? Path(int[] previous, int start, int goal, double[] distance)
    {
        if (double.IsPositiveInfinity(distance[goal]))
        {
            return null;
        }

        var path = new List<int>();
        for (var at = goal; at != -1; at = previous[at])
        {
            path.Add(at);
            if (at == start)
            {
                break;
            }
        }

        path.Reverse();
        return path;
    }

    private static Value FloatMap(NativeContext c, GraphView g, IEnumerable<(int Node, double Value)> entries)
    {
        var map = new MapState(c.Comparer);
        foreach (var (node, value) in entries)
        {
            map.Table.Set(g.Nodes[node], Value.FromFloat(value));
        }

        return CollectionNatives.NewMap(c, map);
    }

    private static List<List<int>> Components(GraphView g, Func<int, IEnumerable<int>> neighbours)
    {
        var seen = new bool[g.Count];
        var components = new List<List<int>>();
        for (var s = 0; s < g.Count; s++)
        {
            if (seen[s])
            {
                continue;
            }

            var component = new List<int>();
            var stack = new Stack<int>([s]);
            seen[s] = true;
            while (stack.Count > 0)
            {
                var u = stack.Pop();
                component.Add(u);
                foreach (var v in neighbours(u).Where(v => !seen[v]))
                {
                    seen[v] = true;
                    stack.Push(v);
                }
            }

            component.Sort();
            components.Add(component);
        }

        return [.. components.OrderByDescending(x => x.Count).ThenBy(x => x[0])];
    }

    /// <summary>Kosaraju's algorithm with iterative depth-first searches.</summary>
    private static List<List<int>> StrongComponents(GraphView g)
    {
        var order = new List<int>();
        var visited = new bool[g.Count];
        for (var s = 0; s < g.Count; s++)
        {
            if (visited[s])
            {
                continue;
            }

            var stack = new Stack<(int Node, int Next)>([(s, 0)]);
            visited[s] = true;
            while (stack.Count > 0)
            {
                var (u, next) = stack.Pop();
                if (next < g.Out[u].Count)
                {
                    stack.Push((u, next + 1));
                    var v = g.Out[u][next].To;
                    if (!visited[v])
                    {
                        visited[v] = true;
                        stack.Push((v, 0));
                    }
                }
                else
                {
                    order.Add(u);
                }
            }
        }

        var assigned = new bool[g.Count];
        var result = new List<List<int>>();
        for (var k = order.Count - 1; k >= 0; k--)
        {
            var s = order[k];
            if (assigned[s])
            {
                continue;
            }

            var component = new List<int>();
            var stack = new Stack<int>([s]);
            assigned[s] = true;
            while (stack.Count > 0)
            {
                var u = stack.Pop();
                component.Add(u);
                foreach (var (v, _) in g.In[u].Where(e => !assigned[e.From]))
                {
                    assigned[v] = true;
                    stack.Push(v);
                }
            }

            component.Sort();
            result.Add(component);
        }

        return [.. result.OrderByDescending(x => x.Count).ThenBy(x => x[0])];
    }

    private static List<List<int>> Cycles(NativeContext c, GraphView g, int limit)
    {
        var cycles = new List<List<int>>();
        if (g.Directed)
        {
            // Simple cycles whose smallest node is the start: enumerate paths through larger nodes only.
            var budget = 200_000L + (limit * 1_000L);
            for (var s = 0; s < g.Count && cycles.Count < limit; s++)
            {
                var path = new List<int> { s };
                var onPath = new bool[g.Count];
                onPath[s] = true;
                var stack = new Stack<int>([0]);
                while (stack.Count > 0 && cycles.Count < limit && budget-- > 0)
                {
                    c.Charge(1);
                    var u = path[^1];
                    var next = stack.Pop();
                    if (next >= g.Out[u].Count)
                    {
                        onPath[u] = false;
                        path.RemoveAt(path.Count - 1);
                        continue;
                    }

                    stack.Push(next + 1);
                    var v = g.Out[u][next].To;
                    if (v == s)
                    {
                        cycles.Add([.. path]);
                    }
                    else if (v > s && !onPath[v])
                    {
                        onPath[v] = true;
                        path.Add(v);
                        stack.Push(0);
                    }
                }

                onPath[s] = false;
            }

            return cycles;
        }

        // Undirected: a fundamental cycle basis from a BFS spanning forest.
        var parent = Enumerable.Repeat(-1, g.Count).ToArray();
        var depth = new int[g.Count];
        var seen = new bool[g.Count];
        var treeEdges = new HashSet<(int, int)>();
        for (var s = 0; s < g.Count; s++)
        {
            if (seen[s])
            {
                continue;
            }

            seen[s] = true;
            var queue = new Queue<int>([s]);
            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                foreach (var (v, _) in g.Out[u].Where(e => !seen[e.To]))
                {
                    seen[v] = true;
                    parent[v] = u;
                    depth[v] = depth[u] + 1;
                    treeEdges.Add((Math.Min(u, v), Math.Max(u, v)));
                    queue.Enqueue(v);
                }
            }
        }

        var done = new HashSet<(int, int)>();
        for (var u = 0; u < g.Count && cycles.Count < limit; u++)
        {
            foreach (var (v, _) in g.Out[u])
            {
                var key = (Math.Min(u, v), Math.Max(u, v));
                if (u == v || treeEdges.Contains(key) || !done.Add(key) || cycles.Count >= limit)
                {
                    continue;
                }

                var (a, b) = (u, v);
                List<int> left = [a], right = [b];
                while (a != b)
                {
                    if (depth[a] >= depth[b])
                    {
                        a = parent[a];
                        left.Add(a);
                    }
                    else
                    {
                        b = parent[b];
                        right.Add(b);
                    }
                }

                right.RemoveAt(right.Count - 1);
                right.Reverse();
                cycles.Add([.. left, .. right]);
            }
        }

        return cycles;
    }

    private static void RegisterGraph(NativeRegistry r)
    {
        var F = r.Scope("Std.Graph.Graph");
        static GraphView View(NativeContext c, Value v) => new(c, G(c, v));

        F("Graph/1", (c, a) => CollectionNatives.Init(a[0], new GraphState(a[1].AsBool, c.Comparer)));
        F("get_IsDirected/0", (c, a) => Value.FromBool(G(c, a[0]).Directed));
        F("get_NodeCount/0", (c, a) => Value.FromInt(G(c, a[0]).Nodes.Count));
        F("get_EdgeCount/0", (c, a) => Value.FromInt(G(c, a[0]).EdgeCount));
        F("get_Nodes/0", (c, a) => c.NewArray([.. G(c, a[0]).Nodes.Keys()]));
        F("AddNode/1", (c, a) => G(c, a[0]).SlotOf(a[1]) >= 0 ? Value.False : Mutate<GraphState>(c, a[0], g => Value.FromBool(g.AddNode(a[1]))));
        F("AddEdge/2", (c, a) => Mutate<GraphState>(c, a[0], g => Void(() => g.AddEdge(a[1], a[2], 1))));
        F("RemoveEdge/2", (c, a) => !(G(c, a[0]) is var g && g.SlotOf(a[1]) >= 0 && g.Of(a[1]).Out.ContainsKey(a[2]))
            ? Value.False
            : Mutate<GraphState>(c, a[0], s => Value.FromBool(s.RemoveEdge(a[1], a[2]))));
        F("RemoveNode/1", (c, a) => G(c, a[0]).SlotOf(a[1]) < 0 ? Value.False : Mutate<GraphState>(c, a[0], g =>
        {
            var adjacency = g.Of(a[1]);
            foreach (var to in adjacency.Out.Keys().ToList())
            {
                g.RemoveEdge(a[1], to);
            }

            foreach (var from in adjacency.In.Keys().ToList())
            {
                g.RemoveEdge(from, a[1]);
            }

            g.Nodes.Remove(a[1], out _);
            return Value.True;
        }));
        F("HasNode/1", (c, a) => Value.FromBool(G(c, a[0]).SlotOf(a[1]) >= 0));
        F("HasEdge/2", (c, a) => Value.FromBool(G(c, a[0]) is var g && g.SlotOf(a[1]) >= 0 && g.Of(a[1]).Out.ContainsKey(a[2])));
        F("Neighbors/1", (c, a) => G(c, a[0]).SlotOf(a[1]) >= 0 ? c.NewArray([.. G(c, a[0]).Of(a[1]).Out.Keys()]) : throw c.Throw("IndexError", "Unknown node."));
        F("Predecessors/1", (c, a) => G(c, a[0]).SlotOf(a[1]) >= 0 ? c.NewArray([.. G(c, a[0]).Of(a[1]).In.Keys()]) : throw c.Throw("IndexError", "Unknown node."));
        F("Degree/1", (c, a) => G(c, a[0]).SlotOf(a[1]) >= 0
            ? Value.FromInt(G(c, a[0]).Directed ? G(c, a[0]).Of(a[1]).Out.Count + G(c, a[0]).Of(a[1]).In.Count : G(c, a[0]).Of(a[1]).Out.Count)
            : throw c.Throw("IndexError", "Unknown node."));
        F("InDegree/1", (c, a) => G(c, a[0]).SlotOf(a[1]) >= 0 ? Value.FromInt(G(c, a[0]).Of(a[1]).In.Count) : throw c.Throw("IndexError", "Unknown node."));

        F("Bfs/1", (c, a) =>
        {
            var g = View(c, a[0]);
            var start = g.Require(c, a[1]);
            var order = new List<int>();
            var seen = new bool[g.Count];
            var queue = new Queue<int>([start]);
            seen[start] = true;
            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                order.Add(u);
                foreach (var (v, _) in g.Out[u].Where(e => !seen[e.To]))
                {
                    seen[v] = true;
                    queue.Enqueue(v);
                }
            }

            return Nodes(c, g, order);
        });
        F("Dfs/1", (c, a) =>
        {
            var g = View(c, a[0]);
            var order = new List<int>();
            var seen = new bool[g.Count];
            var stack = new Stack<int>([g.Require(c, a[1])]);
            while (stack.Count > 0)
            {
                var u = stack.Pop();
                if (seen[u])
                {
                    continue;
                }

                seen[u] = true;
                order.Add(u);
                for (var k = g.Out[u].Count - 1; k >= 0; k--)
                {
                    if (!seen[g.Out[u][k].To])
                    {
                        stack.Push(g.Out[u][k].To);
                    }
                }
            }

            return Nodes(c, g, order);
        });
        F("Hops/1", (c, a) =>
        {
            var g = View(c, a[0]);
            var start = g.Require(c, a[1]);
            var hops = Enumerable.Repeat(-1, g.Count).ToArray();
            hops[start] = 0;
            var queue = new Queue<int>([start]);
            var order = new List<int>();
            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                order.Add(u);
                foreach (var (v, _) in g.Out[u].Where(e => hops[e.To] < 0))
                {
                    hops[v] = hops[u] + 1;
                    queue.Enqueue(v);
                }
            }

            var map = new MapState(c.Comparer);
            foreach (var u in order)
            {
                map.Table.Set(g.Nodes[u], Value.FromInt(hops[u]));
            }

            return CollectionNatives.NewMap(c, map);
        });
        F("ShortestPath/2", (c, a) =>
        {
            var g = View(c, a[0]);
            var (start, goal) = (g.Require(c, a[1]), g.Require(c, a[2]));
            var (distance, previous) = Dijkstra(c, g, start, goal: goal);
            return Path(previous, start, goal, distance) is { } path ? Nodes(c, g, path) : Value.Null;
        });
        F("PathCost/1", (c, a) =>
        {
            var g = View(c, a[0]);
            var path = Items(a[1]).Select(n => g.Require(c, n)).ToArray();
            var cost = 0.0;
            for (var i = 1; i < path.Length; i++)
            {
                var edge = g.Out[path[i - 1]].FirstOrDefault(e => e.To == path[i], (-1, 0));
                cost += edge.To >= 0 ? edge.Weight : throw c.Throw("ArgumentError", "The path uses an edge that does not exist.");
            }

            return Value.FromFloat(cost);
        });
        F("Dijkstra/1", (c, a) =>
        {
            var g = View(c, a[0]);
            var (distance, _) = Dijkstra(c, g, g.Require(c, a[1]));
            return FloatMap(c, g, Enumerable.Range(0, g.Count).Where(i => !double.IsPositiveInfinity(distance[i])).OrderBy(i => distance[i]).ThenBy(i => i).Select(i => (i, distance[i])));
        });
        F("AStar/3", (c, a) =>
        {
            var g = View(c, a[0]);
            var (start, goal) = (g.Require(c, a[1]), g.Require(c, a[2]));
            var h = Fn(a[3]);
            var cache = new Dictionary<int, double>();
            double Heuristic(int n) => cache.TryGetValue(n, out var v) ? v : cache[n] = MathNatives.D(c.Invoke(h, g.Nodes[n]));
            var (distance, previous) = Dijkstra(c, g, start, Heuristic, goal);
            return Path(previous, start, goal, distance) is { } path ? Nodes(c, g, path) : Value.Null;
        });
        F("ShortestPaths/0", (c, a) =>
        {
            var g = View(c, a[0]);
            var n = g.Count;
            c.Charge((long)n * n * n / 4);
            var d = new double[n, n];
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    d[i, j] = i == j ? 0 : double.PositiveInfinity;
                }

                foreach (var (j, w) in g.Out[i])
                {
                    d[i, j] = Math.Min(d[i, j], w);
                }
            }

            for (var k = 0; k < n; k++)
            {
                for (var i = 0; i < n; i++)
                {
                    for (var j = 0; j < n; j++)
                    {
                        if (d[i, k] + d[k, j] < d[i, j])
                        {
                            d[i, j] = d[i, k] + d[k, j];
                        }
                    }
                }
            }

            var map = new MapState(c.Comparer);
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    if (!double.IsPositiveInfinity(d[i, j]))
                    {
                        map.Table.Set(Tuple(g.Nodes[i], g.Nodes[j]), Value.FromFloat(d[i, j]));
                    }
                }
            }

            return CollectionNatives.NewMap(c, map);
        });
        F("TopologicalSort/0", (c, a) =>
        {
            var g = View(c, a[0]);
            if (!g.Directed)
            {
                throw c.Throw("Error", "Topological order needs a directed graph.");
            }

            var indegree = g.In.Select(edges => edges.Count).ToArray();
            var ready = new PriorityQueue<int, int>(Enumerable.Range(0, g.Count).Where(i => indegree[i] == 0).Select(i => (i, i)));
            var order = new List<int>();
            while (ready.TryDequeue(out var u, out _))
            {
                order.Add(u);
                foreach (var (v, _) in g.Out[u])
                {
                    if (--indegree[v] == 0)
                    {
                        ready.Enqueue(v, v);
                    }
                }
            }

            return order.Count == g.Count ? Nodes(c, g, order) : throw c.Throw("Error", "The graph has a cycle, so it has no topological order.");
        });
        F("HasCycle/0", (c, a) =>
        {
            var g = View(c, a[0]);
            return Value.FromBool(g.Directed
                ? StrongComponents(g).Any(comp => comp.Count > 1) || Enumerable.Range(0, g.Count).Any(i => g.Out[i].Any(e => e.To == i))
                : g.Out.Sum(e => e.Count) / 2 > g.Count - Components(g, g.Undirected).Count || Enumerable.Range(0, g.Count).Any(i => g.Out[i].Any(e => e.To == i)));
        });
        F("ConnectedComponents/0", (c, a) =>
        {
            var g = View(c, a[0]);
            return c.NewArray([.. Components(g, g.Undirected).Select(comp => Nodes(c, g, comp))]);
        });
        F("StronglyConnectedComponents/0", (c, a) =>
        {
            var g = View(c, a[0]);
            return c.NewArray([.. StrongComponents(g).Select(comp => Nodes(c, g, comp))]);
        });
        F("Cycles/1", (c, a) =>
        {
            var g = View(c, a[0]);
            return c.NewArray([.. Cycles(c, g, Int32(c, a[1].Bits, "Limit", 0, 100_000)).Select(cycle => Nodes(c, g, cycle))]);
        });
        F("MinimumSpanningTree/0", (c, a) =>
        {
            var g = View(c, a[0]);
            var edges = Enumerable.Range(0, g.Count).SelectMany(u => g.Out[u].Select(e => (U: Math.Min(u, e.To), V: Math.Max(u, e.To), e.Weight)))
                .Distinct().OrderBy(e => e.Weight).ThenBy(e => e.U).ThenBy(e => e.V).ToList();
            var parent = Enumerable.Range(0, g.Count).ToArray();
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            var tree = new GraphState(false, c.Comparer);
            foreach (var node in g.Nodes)
            {
                tree.AddNode(node);
            }

            foreach (var (u, v, w) in edges)
            {
                var (ru, rv) = (Find(u), Find(v));
                if (ru != rv && u != v)
                {
                    parent[ru] = rv;
                    tree.AddEdge(g.Nodes[u], g.Nodes[v], w);
                }
            }

            c.Charge(edges.Count * (1 + (long)Math.Log2(edges.Count + 1)));
            return c.NewInstance(c.Class("Std.Graph", "WeightedGraph"), tree);
        });
        F("DegreeCentrality/0", (c, a) =>
        {
            var g = View(c, a[0]);
            var scale = g.Count > 1 ? 1.0 / (g.Count - 1) : 0;
            return FloatMap(c, g, Enumerable.Range(0, g.Count).Select(i => (i, (g.Directed ? g.Out[i].Count + g.In[i].Count : g.Out[i].Count) * scale)));
        });
        F("BetweennessCentrality/0", (c, a) =>
        {
            var g = View(c, a[0]);
            var n = g.Count;
            var cb = new double[n];
            c.Charge((long)n * (n + g.Out.Sum(e => e.Count)));
            for (var s = 0; s < n; s++)
            {
                var stack = new Stack<int>();
                var predecessors = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
                var sigma = new double[n];
                var dist = Enumerable.Repeat(-1, n).ToArray();
                (sigma[s], dist[s]) = (1, 0);
                var queue = new Queue<int>([s]);
                while (queue.Count > 0)
                {
                    var v = queue.Dequeue();
                    stack.Push(v);
                    foreach (var (w, _) in g.Out[v])
                    {
                        if (dist[w] < 0)
                        {
                            dist[w] = dist[v] + 1;
                            queue.Enqueue(w);
                        }

                        if (dist[w] == dist[v] + 1)
                        {
                            sigma[w] += sigma[v];
                            predecessors[w].Add(v);
                        }
                    }
                }

                var delta = new double[n];
                while (stack.Count > 0)
                {
                    var w = stack.Pop();
                    foreach (var v in predecessors[w])
                    {
                        delta[v] += sigma[v] / sigma[w] * (1 + delta[w]);
                    }

                    if (w != s)
                    {
                        cb[w] += delta[w];
                    }
                }
            }

            var norm = n > 2 ? 1.0 / ((n - 1) * (n - 2)) : 0;
            return FloatMap(c, g, Enumerable.Range(0, n).Select(i => (i, cb[i] * norm)));
        });
        F("ToDot/0", (c, a) =>
        {
            var g = View(c, a[0]);
            var weighted = !g.Unweighted;
            var arrow = g.Directed ? "->" : "--";
            var sb = new StringBuilder(g.Directed ? "digraph G {\n" : "graph G {\n");
            string Q(Value v) => "\"" + Display(c, v).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            foreach (var node in g.Nodes)
            {
                sb.Append("  ").Append(Q(node)).Append(";\n");
            }

            for (var u = 0; u < g.Count; u++)
            {
                foreach (var (v, w) in g.Out[u].Where(e => g.Directed || e.To >= u))
                {
                    sb.Append("  ").Append(Q(g.Nodes[u])).Append(' ').Append(arrow).Append(' ').Append(Q(g.Nodes[v]));
                    sb.Append(weighted ? $" [weight={ValueFormatting.FormatFloat(w)}]" : string.Empty).Append(";\n");
                }
            }

            return Value.FromString(sb.Append("}\n").ToString());
        });
        F("ToString/0", (c, a) => Value.FromString($"{(G(c, a[0]).Directed ? "Directed" : "Undirected")}Graph({G(c, a[0]).Nodes.Count} nodes, {G(c, a[0]).EdgeCount} edges)"));

        var W = r.Scope("Std.Graph.WeightedGraph");
        W("WeightedGraph/1", (c, a) => CollectionNatives.Init(a[0], new GraphState(a[1].AsBool, c.Comparer)));
        W("AddEdge/3", (c, a) =>
        {
            var weight = MathNatives.D(a[3]);
            return double.IsNaN(weight) ? throw c.Throw("ArgumentError", "Edge weights cannot be NaN.") : Mutate<GraphState>(c, a[0], g => Void(() => g.AddEdge(a[1], a[2], weight)));
        });
        W("Weight/2", (c, a) => G(c, a[0]) is var g && g.SlotOf(a[1]) >= 0 && g.Of(a[1]).Out.TryGetValue(a[2], out var w)
            ? w
            : throw c.Throw("IndexError", "No such edge."));
    }

    #endregion

    #region Table

    private const string D = "Std.Data";

    private static Value Table(NativeContext c, TableState state)
    {
        c.Charge(1 + ((long)state.Rows.Length * Math.Max(1, state.Columns.Length) / 8));
        return c.NewInstance(c.Class(D, "Table"), state);
    }

    private static TableState T(NativeContext c, Value v) => c.State<TableState>(v);

    private static Value RowValue(NativeContext c, TableState table, int index) => c.NewInstance(c.Class(D, "Row"), new RowState(table, index));

    internal static Value Infer(string cell)
    {
        if (cell.Length == 0)
        {
            return Value.Null;
        }

        if (long.TryParse(cell, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
        {
            return Value.FromInt(i);
        }

        return double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && cell.Any(char.IsAsciiDigit)
            ? Value.FromFloat(d)
            : Value.FromString(cell);
    }

    private static Value FromJson(JsonNode node) => node.Kind switch
    {
        JsonNode.NullKind => Value.Null,
        JsonNode.BoolKind => Value.FromBool(node.Bool),
        JsonNode.NumberKind => node.IsInt ? Value.FromInt(node.Int) : Value.FromFloat(node.Float),
        JsonNode.StringKind => Value.FromString(node.Str),
        _ => Value.FromString(JsonText.Write(node, pretty: false)),
    };

    private static string Cell(NativeContext c, Value v) => v.IsNull ? string.Empty : Display(c, v);

    private static string Op(Value op) => op.Bits switch
    {
        0 => "Count",
        1 => "Sum",
        2 => "Average",
        3 => "Min",
        4 => "Max",
        5 => "First",
        6 => "Last",
        _ => "CountDistinct",
    };

    private static Value Aggregate(NativeContext c, IReadOnlyList<Value> values, long op)
    {
        var present = values.Where(v => !v.IsNull).ToList();
        switch (op)
        {
            case 0: return Value.FromInt(present.Count);
            case 7: return Value.FromInt(present.Distinct(c.Comparer).Count());
            case 5: return values.Count > 0 ? values[0] : Value.Null;
            case 6: return values.Count > 0 ? values[^1] : Value.Null;
            case 3 or 4:
                if (present.Count == 0)
                {
                    return Value.Null;
                }

                var best = present[0];
                foreach (var v in present.Skip(1))
                {
                    var cmp = c.Comparer.Compare(v, best);
                    if (op == 3 ? cmp < 0 : cmp > 0)
                    {
                        best = v;
                    }
                }

                return best;
        }

        if (present.Any(v => v.Tag is not (ValueTag.Int or ValueTag.Float)))
        {
            throw c.Throw("CastError", $"{(op == 1 ? "Sum" : "Average")} needs numeric values.");
        }

        if (op == 1 && present.All(v => v.Tag == ValueTag.Int))
        {
            return Value.FromInt(present.Aggregate(0L, (s, v) => unchecked(s + v.Bits)));
        }

        var sum = present.Sum(MathNatives.D);
        return op == 1 ? Value.FromFloat(sum) : present.Count == 0 ? Value.Null : Value.FromFloat(sum / present.Count);
    }

    private static TableState JoinTables(NativeContext c, TableState left, TableState right, string leftColumn, string rightColumn, bool outer)
    {
        var (li, ri) = (left.Column(c, leftColumn), right.Column(c, rightColumn));
        var lookup = new OrderedTable(c.Comparer);
        foreach (var row in right.Rows)
        {
            if (!lookup.TryGetValue(row[ri], out var bucket))
            {
                bucket = Value.FromObject(new List<Value[]>());
                lookup.Set(row[ri], bucket);
            }

            ((List<Value[]>)bucket.Object!).Add(row);
        }

        var columns = left.Columns.Concat(right.Columns.Select(name => left.Columns.Contains(name) ? "right." + name : name)).ToArray();
        var rows = new List<Value[]>();
        foreach (var row in left.Rows)
        {
            c.Charge(1);
            if (!row[li].IsNull && lookup.TryGetValue(row[li], out var matches))
            {
                rows.AddRange(((List<Value[]>)matches.Object!).Select(m => row.Concat(m).ToArray()));
            }
            else if (outer)
            {
                rows.Add([.. row, .. new Value[right.Columns.Length]]);
            }
        }

        return new TableState(columns, [.. rows]);
    }

    private static void RegisterTable(NativeRegistry r)
    {
        var F = r.Scope($"{D}.Table");
        F("Table/1", (c, a) => CollectionNatives.Init(a[0], new TableState([.. Items(a[1]).Select(v => v.AsString)], [])));
        F("FromCsv/1", (c, a) =>
        {
            var rows = JsonNatives.ParseCsv(c, a[0].AsString, ',');
            if (rows.Length == 0)
            {
                throw c.Throw("FormatError", "The CSV text has no header row.");
            }

            var width = rows[0].Length;
            return Table(c, new TableState(rows[0], [.. rows[1..].Select(row => Enumerable.Range(0, width).Select(i => i < row.Length ? Infer(row[i]) : Value.Null).ToArray())]));
        });
        F("FromJson/1", (c, a) =>
        {
            var root = c.State<JsonNode>(a[0]);
            if (root.Kind != JsonNode.ArrayKind || root.Items.Any(i => i.Kind != JsonNode.ObjectKind))
            {
                throw c.Throw("FormatError", "Table.FromJson needs an array of objects.");
            }

            var columns = root.Items.SelectMany(o => o.Members.Select(m => m.Key)).Distinct().ToArray();
            return Table(c, new TableState(columns, [.. root.Items.Select(o => columns.Select(col => o.Member(col) is { } v ? FromJson(v) : Value.Null).ToArray())]));
        });
        F("get_Columns/0", (c, a) => c.NewArray([.. T(c, a[0]).Columns.Select(Value.FromString)]));
        F("get_RowCount/0", (c, a) => Value.FromInt(T(c, a[0]).Rows.Length));
        F("Row/1", (c, a) => RowValue(c, T(c, a[0]), Index(c, a[1].Bits, T(c, a[0]).Rows.Length)));
        F("get_Rows/0", (c, a) => c.NewArray([.. Enumerable.Range(0, T(c, a[0]).Rows.Length).Select(i => RowValue(c, T(c, a[0]), i))]));
        F("AddRow/1", (c, a) =>
        {
            var t = T(c, a[0]);
            var values = Items(a[1]);
            if (values.Length != t.Columns.Length)
            {
                throw c.Throw("ArgumentError", $"AddRow needs {t.Columns.Length} values, got {values.Length}.");
            }

            return Table(c, t with { Rows = [.. t.Rows, (Value[])values.Clone()] });
        });
        F("Get/2", (c, a) => T(c, a[0]).Rows[Index(c, a[1].Bits, T(c, a[0]).Rows.Length)][T(c, a[0]).Column(c, a[2].AsString)]);
        F("Column/1", (c, a) =>
        {
            var i = T(c, a[0]).Column(c, a[1].AsString);
            return c.NewArray([.. T(c, a[0]).Rows.Select(row => row[i])]);
        });
        F("Where/1", (c, a) =>
        {
            var t = T(c, a[0]);
            var f = Fn(a[1]);
            return Table(c, t with { Rows = [.. t.Rows.Where((_, i) => c.Invoke(f, RowValue(c, t, i)).AsBool)] });
        });
        F("Select/1", (c, a) =>
        {
            var t = T(c, a[0]);
            var names = Items(a[1]).Select(v => v.AsString).ToArray();
            var indices = names.Select(n => t.Column(c, n)).ToArray();
            return Table(c, new TableState(names, [.. t.Rows.Select(row => indices.Select(i => row[i]).ToArray())]));
        });
        F("WithColumn/2", (c, a) =>
        {
            var t = T(c, a[0]);
            var f = Fn(a[2]);
            var name = a[1].AsString;
            var existing = System.Array.IndexOf(t.Columns, name);
            var rows = t.Rows.Select((row, i) =>
            {
                var value = c.Invoke(f, RowValue(c, t, i));
                if (existing >= 0)
                {
                    var copy = (Value[])row.Clone();
                    copy[existing] = value;
                    return copy;
                }

                return [.. row, value];
            }).ToArray();
            return Table(c, new TableState(existing >= 0 ? t.Columns : [.. t.Columns, name], rows));
        });
        F("Rename/2", (c, a) =>
        {
            var t = T(c, a[0]);
            var i = t.Column(c, a[1].AsString);
            var columns = (string[])t.Columns.Clone();
            columns[i] = a[2].AsString;
            return Table(c, t with { Columns = columns });
        });
        StdFunction order(bool descending) => (c, a) =>
        {
            var t = T(c, a[0]);
            var i = t.Column(c, a[1].AsString);
            var comparer = Comparer<Value[]>.Create((x, y) => descending ? c.Comparer.Compare(y[i], x[i]) : c.Comparer.Compare(x[i], y[i]));
            var keyed = t.Rows.Select((row, n) => (row, n)).ToArray();
            System.Array.Sort(keyed, (x, y) => comparer.Compare(x.row, y.row) is var cmp && cmp != 0 ? cmp : x.n.CompareTo(y.n));
            c.Charge((long)(t.Rows.Length * Math.Log2(t.Rows.Length + 2)));
            return Table(c, t with { Rows = [.. keyed.Select(k => k.row)] });
        };
        F("OrderBy/1", order(false));
        F("OrderByDescending/1", order(true));
        F("Limit/1", (c, a) => Table(c, T(c, a[0]) with { Rows = [.. T(c, a[0]).Rows.Take((int)Math.Clamp(a[1].Bits, 0, int.MaxValue))] }));
        F("Distinct/0", (c, a) =>
        {
            var t = T(c, a[0]);
            return Table(c, t with { Rows = [.. t.Rows.DistinctBy(row => Tuple(row), c.Comparer)] });
        });
        F("Join/3", (c, a) => Table(c, JoinTables(c, T(c, a[0]), T(c, a[1]), a[2].AsString, a[3].AsString, outer: false)));
        F("LeftJoin/3", (c, a) => Table(c, JoinTables(c, T(c, a[0]), T(c, a[1]), a[2].AsString, a[3].AsString, outer: true)));
        F("GroupBy/3", (c, a) =>
        {
            var t = T(c, a[0]);
            var (k, v) = (t.Column(c, a[1].AsString), t.Column(c, a[2].AsString));
            var groups = new OrderedTable(c.Comparer);
            foreach (var row in t.Rows)
            {
                if (!groups.TryGetValue(row[k], out var bucket))
                {
                    bucket = Value.FromObject(new List<Value>());
                    groups.Set(row[k], bucket);
                }

                ((List<Value>)bucket.Object!).Add(row[v]);
            }

            var rows = groups.Slots().Select(s => new[] { groups.KeyAt(s), Aggregate(c, (List<Value>)groups.ValueAt(s).Object!, a[3].Bits) }).ToArray();
            return Table(c, new TableState([a[1].AsString, $"{Op(a[3])}({a[2].AsString})"], rows));
        });
        F("Aggregate/2", (c, a) =>
        {
            var t = T(c, a[0]);
            var i = t.Column(c, a[1].AsString);
            c.Charge(t.Rows.Length);
            return Aggregate(c, [.. t.Rows.Select(row => row[i])], a[2].Bits);
        });
        F("Pivot/4", (c, a) =>
        {
            var t = T(c, a[0]);
            var (ri, ci, vi) = (t.Column(c, a[1].AsString), t.Column(c, a[2].AsString), t.Column(c, a[3].AsString));
            var rowKeys = t.Rows.Select(row => row[ri]).Distinct(c.Comparer).ToList();
            var colKeys = t.Rows.Select(row => row[ci]).Distinct(c.Comparer).ToList();
            var rows = rowKeys.Select(rk => new[] { rk }.Concat(colKeys.Select(ck =>
                Aggregate(c, [.. t.Rows.Where(row => c.Comparer.Equals(row[ri], rk) && c.Comparer.Equals(row[ci], ck)).Select(row => row[vi])], a[4].Bits))).ToArray()).ToArray();
            c.Charge((long)rowKeys.Count * colKeys.Count * t.Rows.Length / 8);
            return Table(c, new TableState([a[1].AsString, .. colKeys.Select(k => Cell(c, k))], rows));
        });
        F("Concat/1", (c, a) =>
        {
            var (x, y) = (T(c, a[0]), T(c, a[1]));
            return x.Columns.SequenceEqual(y.Columns)
                ? Table(c, x with { Rows = [.. x.Rows, .. y.Rows] })
                : throw c.Throw("ArgumentError", "Concat needs tables with the same columns.");
        });
        F("ToCsv/0", (c, a) =>
        {
            var t = T(c, a[0]);
            string Quote(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
            var lines = new[] { string.Join(",", t.Columns.Select(Quote)) }.Concat(t.Rows.Select(row => string.Join(",", row.Select(v => Quote(Cell(c, v))))));
            return Value.FromString(string.Join("\r\n", lines) + "\r\n");
        });
        F("ToJson/0", (c, a) =>
        {
            var t = T(c, a[0]);
            var array = JsonNode.Array([.. t.Rows.Select(row => JsonNode.Object([.. t.Columns.Select((col, i) => (col, JsonNatives.From(c, row[i])))]))]);
            return Value.FromString(JsonText.Write(array, pretty: false));
        });
        F("ToString/0", (c, a) =>
        {
            var t = T(c, a[0]);
            var rows = new List<string[]> { t.Columns };
            rows.AddRange(t.Rows.Take(50).Select(row => row.Select(v => v.IsNull ? "null" : Display(c, v)).ToArray()));
            var widths = Enumerable.Range(0, t.Columns.Length).Select(i => rows.Max(r => r[i].Length)).ToArray();
            string Line(string[] r) => string.Join("  ", r.Select((s, i) => s.PadRight(widths[i]))).TrimEnd();
            var lines = new List<string> { Line(rows[0]), string.Join("  ", widths.Select(w => new string('-', w))) };
            lines.AddRange(rows.Skip(1).Select(Line));
            if (t.Rows.Length > 50)
            {
                lines.Add($"… {t.Rows.Length - 50} more rows");
            }

            return Value.FromString(string.Join("\n", lines));
        });

        var R = r.Scope($"{D}.Row");
        static RowState S(NativeContext c, Value v) => c.State<RowState>(v);
        static Value RowCell(NativeContext c, Value[] a) => S(c, a[0]).Table.Rows[S(c, a[0]).Index][S(c, a[0]).Table.Column(c, a[1].AsString)];
        R("get_Index/0", (c, a) => Value.FromInt(S(c, a[0]).Index));
        R("Get/1", (c, a) => RowCell(c, a));
        R("GetString/1", (c, a) => Value.FromString(RowCell(c, a) is var v && v.IsNull ? string.Empty : Display(c, v)));
        R("GetInt/1", (c, a) => RowCell(c, a) switch
        {
            { Tag: ValueTag.Int } v => v,
            { Tag: ValueTag.Float } v when v.AsFloat == Math.Floor(v.AsFloat) => Value.FromInt((long)v.AsFloat),
            { Object: string s } when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) => Value.FromInt(n),
            var v => throw c.Throw("CastError", $"Column '{a[1].AsString}' holds {(v.IsNull ? "null" : Display(c, v))}, not an int."),
        });
        R("GetFloat/1", (c, a) => RowCell(c, a) switch
        {
            { Tag: ValueTag.Int or ValueTag.Float } v => Value.FromFloat(MathNatives.D(v)),
            { Object: string s } when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => Value.FromFloat(n),
            var v => throw c.Throw("CastError", $"Column '{a[1].AsString}' holds {(v.IsNull ? "null" : Display(c, v))}, not a number."),
        });
        R("IsNull/1", (c, a) => Value.FromBool(RowCell(c, a).IsNull));
        R("ToString/0", (c, a) =>
        {
            var s = S(c, a[0]);
            return Value.FromString("{" + string.Join(", ", s.Table.Columns.Select((col, i) => $"{col}: {(s.Table.Rows[s.Index][i] is var v && v.IsNull ? "null" : Display(c, v))}")) + "}");
        });
    }

    #endregion
}
