/// Graphs (Document 7 §13.10): directed or undirected, optionally weighted (default weight 1). Node and neighbour order
/// is insertion order, so every traversal is deterministic. Algorithms are methods; costs are their usual complexity
/// in steps (BFS/DFS V + E, Dijkstra (V + E) log V, betweenness V·E).
module Std.Graph;

import Std.Collections;

class Graph<T> {
    Graph(bool directed);
    bool IsDirected { get; }
    int NodeCount { get; }
    int EdgeCount { get; }
    T[] Nodes { get; }
    /// Returns false when already present.
    bool AddNode(T node);
    /// Adds missing nodes; replaces the weight of an existing edge.
    void AddEdge(T from, T to);
    bool RemoveEdge(T from, T to);
    bool RemoveNode(T node);
    bool HasNode(T node);
    bool HasEdge(T from, T to);
    /// Successors (directed) or neighbours (undirected), in insertion order.
    T[] Neighbors(T node);
    T[] Predecessors(T node);
    int Degree(T node);
    int InDegree(T node);

    // ---- traversal
    T[] Bfs(T start);
    T[] Dfs(T start);
    /// Breadth-first distances in hops from start (unreachable nodes are absent).
    Map<T, int> Hops(T start);

    // ---- paths
    /// Cheapest path from → to (Dijkstra; BFS when unweighted), or null when unreachable.
    T[]? ShortestPath(T from, T to);
    float PathCost(T[] path);
    /// Cheapest distance from start to every reachable node.
    Map<T, float> Dijkstra(T start);
    /// A* with an admissible heuristic estimating the remaining cost to goal.
    T[]? AStar(T start, T goal, fn(T) -> float heuristic);
    /// Every pair's cheapest distance (Floyd–Warshall; V³ steps).
    Map<(T, T), float> ShortestPaths();

    // ---- structure
    /// Raises Error when the graph has a cycle (directed graphs only).
    T[] TopologicalSort();
    bool HasCycle();
    /// Weakly connected components, largest first.
    T[][] ConnectedComponents();
    /// Strongly connected components (Tarjan), for directed graphs.
    T[][] StronglyConnectedComponents();
    /// Elementary cycles (directed: Johnson-style enumeration; undirected: a cycle basis), at most limit of them.
    T[][] Cycles(int limit);
    /// Minimum spanning forest (Kruskal) as a new undirected graph.
    Graph<T> MinimumSpanningTree();

    // ---- centrality
    /// Degree divided by (n − 1).
    Map<T, float> DegreeCentrality();
    /// Brandes' betweenness, normalised to [0, 1].
    Map<T, float> BetweennessCentrality();

    /// Graphviz DOT text for the visualiser.
    string ToDot();
    string ToString();
}

/// A graph whose edges carry weights.
class WeightedGraph<T> : Graph<T> {
    WeightedGraph(bool directed);
    void AddEdge(T from, T to, float weight);
    /// The weight of an edge; raises IndexError when absent.
    float Weight(T from, T to);
}
