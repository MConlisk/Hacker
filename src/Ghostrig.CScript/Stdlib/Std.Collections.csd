/// Data structures (Document 7 §13.2). Every collection is deterministic: hashed collections iterate in insertion order,
/// sorted ones in comparer order. Keys and elements compare structurally (scalars, strings, bytes, tuples, structs) or by
/// their HashCode / Equatable / Comparable implementations (classes); other classes compare by identity.
/// Costs are in VM steps on top of the 1-step call: n is the collection size, k the size of the argument.
/// Mutating a collection while iterating it raises Error; a faulted event rolls collections back with everything else.
module Std.Collections;

import Std.Seq;

// ---------------------------------------------------------------- sequential collections

/// A growable array.
class List<T> : Iterable<T> {
    List();
    /// Cost: k.
    List(Iterable<T> items);
    int Count { get; }
    bool IsEmpty { get; }
    /// Raises IndexError outside 0..Count-1.
    T this[int index] { get; set; }
    void Add(T item);
    /// Cost: k.
    void AddAll(Iterable<T> items);
    /// Cost: n/64.
    void Insert(int index, T item);
    /// Cost: n.
    bool Remove(T item);
    /// Cost: n/64.
    T RemoveAt(int index);
    void RemoveRange(int start, int count);
    /// Removes every item matching the predicate and returns how many. Cost: n.
    int RemoveAll(fn(T) -> bool predicate);
    void Clear();
    /// Cost: n.
    bool Contains(T item);
    int IndexOf(T item);
    int LastIndexOf(T item);
    int FindIndex(fn(T) -> bool predicate);
    T? Find(fn(T) -> bool predicate);
    T First();
    T Last();
    /// Stable sort in natural order. Cost: n log n.
    void Sort();
    /// Stable sort with a comparer returning <0, 0 or >0.
    void Sort(fn(T, T) -> int comparer);
    /// Stable sort by a key.
    void SortBy<K>(fn(T) -> K key);
    void Reverse();
    void Swap(int i, int j);
    /// Binary search of a sorted list: the index, or ~insertionPoint when absent. Cost: log n.
    int BinarySearch(T item);
    List<T> Slice(int start, int count);
    List<T> Copy();
    T[] ToArray();
    Sequence<T> AsSeq();
    ReadOnlyList<T> AsReadOnly();
    Iterator<T> GetIterator();
    string ToString();
}

/// A read-only view of a List; it reflects later changes to the list.
class ReadOnlyList<T> : Iterable<T> {
    int Count { get; }
    T this[int index] { get; }
    bool Contains(T item);
    int IndexOf(T item);
    T[] ToArray();
    Sequence<T> AsSeq();
    Iterator<T> GetIterator();
    string ToString();
}

/// First-in, first-out queue.
class Queue<T> : Iterable<T> {
    Queue();
    int Count { get; }
    bool IsEmpty { get; }
    void Enqueue(T item);
    /// Raises Error when empty.
    T Dequeue();
    T? TryDequeue();
    T Peek();
    void Clear();
    T[] ToArray();
    Iterator<T> GetIterator();
    string ToString();
}

/// Last-in, first-out stack.
class Stack<T> : Iterable<T> {
    Stack();
    int Count { get; }
    bool IsEmpty { get; }
    void Push(T item);
    T Pop();
    T? TryPop();
    T Peek();
    void Clear();
    /// Top first.
    T[] ToArray();
    Iterator<T> GetIterator();
    string ToString();
}

/// Double-ended queue (ring buffer that grows).
class Deque<T> : Iterable<T> {
    Deque();
    int Count { get; }
    bool IsEmpty { get; }
    T this[int index] { get; set; }
    void PushFront(T item);
    void PushBack(T item);
    T PopFront();
    T PopBack();
    T PeekFront();
    T PeekBack();
    void Clear();
    T[] ToArray();
    Iterator<T> GetIterator();
    string ToString();
}

/// A min-priority queue (smallest priority first; equal priorities leave in insertion order).
class PriorityQueue<T, P> : Iterable<(T, P)> {
    PriorityQueue();
    /// Orders priorities with a comparer instead of their natural order.
    PriorityQueue(fn(P, P) -> int comparer);
    int Count { get; }
    bool IsEmpty { get; }
    /// Cost: log n.
    void Enqueue(T item, P priority);
    /// Cost: log n.
    T Dequeue();
    T? TryDequeue();
    T Peek();
    P PeekPriority();
    void Clear();
    /// Items in priority order (does not modify the queue). Cost: n log n.
    T[] ToArray();
    Iterator<(T, P)> GetIterator();
    string ToString();
}

/// A fixed-capacity circular buffer: adding to a full buffer overwrites the oldest item.
class RingBuffer<T> : Iterable<T> {
    RingBuffer(int capacity);
    int Count { get; }
    int Capacity { get; }
    bool IsFull { get; }
    /// Index 0 is the oldest item.
    T this[int index] { get; }
    /// Returns true when an old item was overwritten.
    bool Add(T item);
    T RemoveOldest();
    T Oldest();
    T Newest();
    void Clear();
    T[] ToArray();
    Iterator<T> GetIterator();
    string ToString();
}

// ---------------------------------------------------------------- maps and sets

/// A hash map that iterates in insertion order. Re-assigning an existing key keeps its position.
class Map<K, V> : Iterable<(K, V)> {
    Map();
    int Count { get; }
    bool IsEmpty { get; }
    /// Raises IndexError for a missing key.
    V this[K key] { get; set; }
    bool ContainsKey(K key);
    /// Cost: n.
    bool ContainsValue(V value);
    V? Get(K key);
    V GetOr(K key, V fallback);
    V GetOrAdd(K key, fn(K) -> V factory);
    /// Sets key to initial if absent, otherwise to update(current); returns the stored value.
    V Upsert(K key, V initial, fn(V) -> V update);
    bool TryAdd(K key, V value);
    bool Remove(K key);
    void Clear();
    /// Live views in insertion order.
    Sequence<K> Keys { get; }
    Sequence<V> Values { get; }
    Sequence<(K, V)> Entries { get; }
    ReadOnlyMap<K, V> AsReadOnly();
    Iterator<(K, V)> GetIterator();
    string ToString();
}

/// A read-only view of a Map.
class ReadOnlyMap<K, V> : Iterable<(K, V)> {
    int Count { get; }
    V this[K key] { get; }
    bool ContainsKey(K key);
    V? Get(K key);
    Sequence<K> Keys { get; }
    Sequence<V> Values { get; }
    Iterator<(K, V)> GetIterator();
    string ToString();
}

/// A map ordered by key. Lookups cost log n; inserts and removals log n + n/64.
class SortedMap<K, V> : Iterable<(K, V)> {
    SortedMap();
    SortedMap(fn(K, K) -> int comparer);
    int Count { get; }
    bool IsEmpty { get; }
    V this[K key] { get; set; }
    bool ContainsKey(K key);
    V? Get(K key);
    V GetOr(K key, V fallback);
    bool Remove(K key);
    void Clear();
    K FirstKey();
    K LastKey();
    /// Greatest key <= key, or null.
    K? Floor(K key);
    /// Least key >= key, or null.
    K? Ceiling(K key);
    /// Entries with from <= key < to.
    Sequence<(K, V)> Range(K from, K to);
    Sequence<K> Keys { get; }
    Sequence<V> Values { get; }
    Iterator<(K, V)> GetIterator();
    string ToString();
}

/// A hash set that iterates in insertion order.
class Set<T> : Iterable<T> {
    Set();
    Set(Iterable<T> items);
    int Count { get; }
    bool IsEmpty { get; }
    /// Returns false when already present.
    bool Add(T item);
    bool Remove(T item);
    bool Contains(T item);
    void Clear();
    void UnionWith(Iterable<T> other);
    void IntersectWith(Iterable<T> other);
    void ExceptWith(Iterable<T> other);
    Set<T> Union(Set<T> other);
    Set<T> Intersect(Set<T> other);
    Set<T> Except(Set<T> other);
    bool IsSubsetOf(Set<T> other);
    bool Overlaps(Set<T> other);
    T[] ToArray();
    Sequence<T> AsSeq();
    Iterator<T> GetIterator();
    string ToString();
}

/// A set ordered by its comparer.
class SortedSet<T> : Iterable<T> {
    SortedSet();
    SortedSet(fn(T, T) -> int comparer);
    int Count { get; }
    bool IsEmpty { get; }
    bool Add(T item);
    bool Remove(T item);
    bool Contains(T item);
    void Clear();
    T Min();
    T Max();
    T? Floor(T item);
    T? Ceiling(T item);
    /// Items with from <= item < to.
    Sequence<T> Range(T from, T to);
    T[] ToArray();
    Sequence<T> AsSeq();
    Iterator<T> GetIterator();
    string ToString();
}

/// A map from each key to a list of values (insertion-ordered).
class Multimap<K, V> : Iterable<(K, V)> {
    Multimap();
    /// Total number of key/value pairs.
    int Count { get; }
    int KeyCount { get; }
    void Add(K key, V value);
    /// The values for a key (empty when absent).
    V[] Get(K key);
    bool ContainsKey(K key);
    bool Remove(K key, V value);
    int RemoveAll(K key);
    void Clear();
    Sequence<K> Keys { get; }
    Iterator<(K, V)> GetIterator();
    string ToString();
}

// ---------------------------------------------------------------- grids and bits

/// A fixed-size set of bits.
class BitSet : Iterable<int> {
    BitSet(int length);
    int Length { get; }
    /// Number of set bits. Cost: n/64.
    int Count { get; }
    bool Get(int index);
    void Set(int index);
    void Set(int index, bool value);
    void Clear(int index);
    void Flip(int index);
    void ClearAll();
    void SetAll();
    /// In-place bitwise operations with a BitSet of the same length. Cost: n/64.
    void And(BitSet other);
    void Or(BitSet other);
    void Xor(BitSet other);
    void Not();
    /// Index of the next set bit at or after start, or -1.
    int NextSet(int start);
    BitSet Copy();
    /// Indices of set bits.
    Iterator<int> GetIterator();
    /// "0101…", bit 0 first.
    string ToString();
}

/// A rectangular grid addressed by (x, y), row-major.
class Grid2D<T> : Iterable<T> {
    Grid2D(int width, int height, T fill);
    int Width { get; }
    int Height { get; }
    T Get(int x, int y);
    void Set(int x, int y, T value);
    bool InBounds(int x, int y);
    void Fill(T value);
    /// In-bounds orthogonal neighbours (N, E, S, W order).
    (int, int)[] Neighbors4(int x, int y);
    /// In-bounds neighbours including diagonals (clockwise from N).
    (int, int)[] Neighbors8(int x, int y);
    T[] Row(int y);
    T[] Column(int x);
    Grid2D<T> Copy();
    Iterator<T> GetIterator();
    string ToString();
}

/// A rows × columns matrix of any element type (numeric linear algebra is in Std.Math).
class Matrix<T> : Iterable<T> {
    Matrix(int rows, int columns, T fill);
    int Rows { get; }
    int Columns { get; }
    T Get(int row, int column);
    void Set(int row, int column, T value);
    T[] Row(int row);
    T[] Column(int column);
    Matrix<T> Transpose();
    Matrix<U> Map<U>(fn(T) -> U f);
    Iterator<T> GetIterator();
    string ToString();
}

// ---------------------------------------------------------------- caches (Document 7 D4)

/// Least-recently-used cache. Get and Put cost 1 step; Get refreshes recency.
class LruCache<K, V> {
    LruCache(int capacity);
    int Count { get; }
    int Capacity { get; }
    V? Get(K key);
    /// Inserts or replaces; evicts the least recently used entry when full.
    void Put(K key, V value);
    V GetOrAdd(K key, fn(K) -> V factory);
    /// Does not change recency or counters.
    bool Contains(K key);
    bool Remove(K key);
    void Clear();
    int Hits { get; }
    int Misses { get; }
    int Evictions { get; }
    /// Hits / (Hits + Misses), 0 before the first lookup.
    float HitRate { get; }
    void ResetStats();
    /// Keys from least to most recently used.
    Sequence<K> Keys { get; }
    string ToString();
}

/// Least-frequently-used cache; ties evict the least recently used. Get and Put cost log n.
class LfuCache<K, V> {
    LfuCache(int capacity);
    int Count { get; }
    int Capacity { get; }
    V? Get(K key);
    void Put(K key, V value);
    bool Contains(K key);
    /// Uses recorded for a key (0 when absent).
    int Frequency(K key);
    bool Remove(K key);
    void Clear();
    int Hits { get; }
    int Misses { get; }
    int Evictions { get; }
    float HitRate { get; }
    void ResetStats();
    string ToString();
}

/// A cache whose entries expire after a time-to-live on the simulated clock.
class TtlCache<K, V> {
    TtlCache(Duration ttl);
    /// Live (unexpired) entries. Cost: n.
    int Count { get; }
    Duration Ttl { get; }
    V? Get(K key);
    void Put(K key, V value);
    void Put(K key, V value, Duration ttl);
    bool Contains(K key);
    bool Remove(K key);
    /// Drops expired entries and returns how many. Cost: n.
    int Purge();
    void Clear();
    int Hits { get; }
    int Misses { get; }
    int Expirations { get; }
    float HitRate { get; }
    string ToString();
}

/// A recyclable object pool.
class ObjectPool<T> {
    ObjectPool(fn() -> T factory, int maxSize);
    /// Reuses a returned object or creates a new one.
    T Rent();
    /// Keeps the object for reuse unless the pool is full.
    void Return(T item);
    int Available { get; }
    int Created { get; }
    int Reuses { get; }
    string ToString();
}

// ---------------------------------------------------------------- probabilistic and specialised structures

/// A Bloom filter: no false negatives, tunable false positives. Add and MightContain cost k (hash count).
class BloomFilter<T> {
    BloomFilter(int expectedItems, float falsePositiveRate);
    void Add(T item);
    bool MightContain(T item);
    /// Items added (including duplicates).
    int Count { get; }
    int BitCount { get; }
    int HashCount { get; }
    /// Current false-positive probability from the fill ratio.
    float EstimatedFalsePositiveRate { get; }
    void Clear();
    string ToString();
}

/// Count-min sketch: approximate frequencies that never under-count.
class CountMinSketch<T> {
    CountMinSketch(int width, int depth);
    void Add(T item);
    void Add(T item, int count);
    int Estimate(T item);
    int Total { get; }
    void Clear();
    string ToString();
}

/// HyperLogLog distinct-count estimator (precision 4–16; standard error ≈ 1.04 / sqrt(2^precision)).
class HyperLogLog<T> {
    HyperLogLog(int precision);
    void Add(T item);
    int Estimate();
    /// Merges another sketch of the same precision into this one.
    void Merge(HyperLogLog<T> other);
    void Clear();
    string ToString();
}

/// A prefix tree of strings.
class Trie : Iterable<string> {
    Trie();
    int Count { get; }
    /// Returns false when already present. Cost: length.
    bool Add(string word);
    bool Remove(string word);
    bool Contains(string word);
    bool HasPrefix(string prefix);
    /// Words starting with prefix, in ordinal order.
    Sequence<string> WithPrefix(string prefix);
    /// The longest stored word that is a prefix of text, or null.
    string? LongestPrefixOf(string text);
    void Clear();
    Iterator<string> GetIterator();
    string ToString();
}

/// Half-open integer intervals [start, end) with attached values. Queries cost log n + matches.
class IntervalTree<T> : Iterable<(int, int, T)> {
    IntervalTree();
    int Count { get; }
    void Add(int start, int end, T value);
    bool Remove(int start, int end, T value);
    /// Values whose interval overlaps [start, end), ordered by start.
    T[] Overlapping(int start, int end);
    /// Values whose interval contains point.
    T[] At(int point);
    void Clear();
    Iterator<(int, int, T)> GetIterator();
    string ToString();
}

/// A sorted set implemented as a skip list (deterministic levels). Operations cost log n.
class SkipList<T> : Iterable<T> {
    SkipList();
    int Count { get; }
    bool Add(T item);
    bool Remove(T item);
    bool Contains(T item);
    T Min();
    T Max();
    Sequence<T> Range(T from, T to);
    void Clear();
    /// Number of levels currently in use.
    int Levels { get; }
    Iterator<T> GetIterator();
    string ToString();
}

/// Consistent hashing with virtual nodes: adding or removing a node moves only ~1/n of the keys.
class ConsistentHashRing<T> {
    ConsistentHashRing(int virtualNodes);
    void Add(T node);
    bool Remove(T node);
    /// The node responsible for key. Cost: log(n × virtualNodes).
    T NodeFor(string key);
    int Count { get; }
    T[] Nodes { get; }
    string ToString();
}
