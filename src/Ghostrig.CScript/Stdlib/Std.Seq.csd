/// Lazy functional sequences over any Iterable (Document 7 §13.3). Operators are lazy: nothing runs until the sequence is
/// iterated or a terminal operation (Count, First, ToList, Fold…) is called, and a sequence can be iterated again.
/// Each element passing through an operator costs 1 step plus whatever its callback costs.
module Std.Seq;

import Std.Collections;

/// A lazy, re-iterable sequence. Create one with Seq.From(items), a collection's AsSeq(), or Seq.Range.
class Sequence<T> : Iterable<T> {
    // ---- lazy operators
    Sequence<U> Map<U>(fn(T) -> U f);
    Sequence<U> MapIndexed<U>(fn(T, int) -> U f);
    Sequence<T> Filter(fn(T) -> bool predicate);
    Sequence<U> FlatMap<U>(fn(T) -> Iterable<U> f);
    Sequence<T> Take(int count);
    Sequence<T> Skip(int count);
    Sequence<T> TakeWhile(fn(T) -> bool predicate);
    Sequence<T> SkipWhile(fn(T) -> bool predicate);
    Sequence<T> TakeLast(int count);
    Sequence<(T, U)> Zip<U>(Iterable<U> other);
    Sequence<T> Concat(Iterable<T> other);
    Sequence<T> Append(T item);
    Sequence<T> Prepend(T item);
    /// First occurrence wins.
    Sequence<T> Distinct();
    Sequence<T> DistinctBy<K>(fn(T) -> K key);
    /// Stable ordering by key; refine with ThenBy.
    OrderedSequence<T> OrderBy<K>(fn(T) -> K key);
    OrderedSequence<T> OrderByDescending<K>(fn(T) -> K key);
    /// Stable sort in natural order, or with a comparer.
    Sequence<T> Sorted();
    Sequence<T> Sorted(fn(T, T) -> int comparer);
    Sequence<T> Reverse();
    /// Inner join on equal keys, in outer order.
    Sequence<R> Join<U, K, R>(Iterable<U> inner, fn(T) -> K outerKey, fn(U) -> K innerKey, fn(T, U) -> R result);
    /// Consecutive groups of size (the last may be shorter).
    Sequence<T[]> Chunk(int size);
    /// Sliding windows of exactly size items.
    Sequence<T[]> Window(int size);
    Sequence<(int, T)> Indexed();
    /// Running fold: emits every intermediate accumulator.
    Sequence<A> Scan<A>(A seed, fn(A, T) -> A f);
    Sequence<T> Union(Iterable<T> other);
    Sequence<T> Intersect(Iterable<T> other);
    Sequence<T> Except(Iterable<T> other);

    // ---- terminal operations
    A Fold<A>(A seed, fn(A, T) -> A f);
    /// Raises Error on an empty sequence.
    T Reduce(fn(T, T) -> T f);
    bool Any();
    bool Any(fn(T) -> bool predicate);
    bool All(fn(T) -> bool predicate);
    int Count();
    int Count(fn(T) -> bool predicate);
    bool Contains(T item);
    /// Raises Error on an empty sequence (or when nothing matches).
    T First();
    T First(fn(T) -> bool predicate);
    T? FirstOrNull();
    T? FirstOrNull(fn(T) -> bool predicate);
    T Last();
    T? LastOrNull();
    T ElementAt(int index);
    int IndexOf(T item);
    /// Natural order; raises Error on an empty sequence.
    T Min();
    T Max();
    T MinBy<K>(fn(T) -> K key);
    T MaxBy<K>(fn(T) -> K key);
    int Sum(fn(T) -> int selector);
    float SumFloat(fn(T) -> float selector);
    /// Raises Error on an empty sequence.
    float Average(fn(T) -> float selector);
    Map<K, List<T>> GroupBy<K>(fn(T) -> K key);
    Map<K, int> CountBy<K>(fn(T) -> K key);
    /// Splits into (matching, non-matching).
    (List<T>, List<T>) Partition(fn(T) -> bool predicate);
    T[] ToArray();
    List<T> ToList();
    Set<T> ToSet();
    /// Later duplicates overwrite earlier keys.
    Map<K, V> ToMap<K, V>(fn(T) -> K key, fn(T) -> V value);
    string Join(string separator);
    void ForEach(fn(T) -> void action);
    bool SequenceEqual(Iterable<T> other);
    Iterator<T> GetIterator();
    string ToString();
}

/// A sequence sorted by one or more keys.
class OrderedSequence<T> : Sequence<T> {
    OrderedSequence<T> ThenBy<K>(fn(T) -> K key);
    OrderedSequence<T> ThenByDescending<K>(fn(T) -> K key);
}

// ---------------------------------------------------------------- sources

Sequence<T> From<T>(Iterable<T> items);
/// count integers from start.
Sequence<int> Range(int start, int count);
/// count integers from start, stepping by step.
Sequence<int> Range(int start, int count, int step);
Sequence<T> Repeat<T>(T value, int count);
Sequence<T> Empty<T>();
/// seed, next(seed), next(next(seed)), … (infinite: combine with Take or TakeWhile).
Sequence<T> Iterate<T>(T seed, fn(T) -> T next);
/// Concatenates nested arrays.
Sequence<T> Flatten<T>(Iterable<T[]> items);

// ---------------------------------------------------------------- functional forms (Seq.Map(items, f) ≡ Seq.From(items).Map(f))

Sequence<U> Map<T, U>(Iterable<T> items, fn(T) -> U f);
Sequence<T> Filter<T>(Iterable<T> items, fn(T) -> bool predicate);
A Fold<T, A>(Iterable<T> items, A seed, fn(A, T) -> A f);
T Reduce<T>(Iterable<T> items, fn(T, T) -> T f);
bool Any<T>(Iterable<T> items, fn(T) -> bool predicate);
bool All<T>(Iterable<T> items, fn(T) -> bool predicate);
int Count<T>(Iterable<T> items);
T First<T>(Iterable<T> items);
T Last<T>(Iterable<T> items);
Sequence<T> Take<T>(Iterable<T> items, int count);
Sequence<T> Skip<T>(Iterable<T> items, int count);
Sequence<(T, U)> Zip<T, U>(Iterable<T> first, Iterable<U> second);
Sequence<T> Concat<T>(Iterable<T> first, Iterable<T> second);
Sequence<T> Distinct<T>(Iterable<T> items);
T[] ToArray<T>(Iterable<T> items);
List<T> ToList<T>(Iterable<T> items);
Set<T> ToSet<T>(Iterable<T> items);
@Native("Std.Seq.SumInt") int Sum(Iterable<int> items);
@Native("Std.Seq.SumFloat") float Sum(Iterable<float> items);
@Native("Std.Seq.MinOf") T Min<T>(Iterable<T> items);
@Native("Std.Seq.MaxOf") T Max<T>(Iterable<T> items);
@Native("Std.Seq.AverageInt") float Average(Iterable<int> items);
@Native("Std.Seq.AverageFloat") float Average(Iterable<float> items);
