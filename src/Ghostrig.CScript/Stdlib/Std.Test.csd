/// Testing and diagnostics (Document 7 §13.14, §14.4). Failed assertions raise AssertionFailed (code EASSERT).
/// @Test functions (and @TestCase(args…) variants) are discovered and run by the IDE's test runner; functions marked
/// @Setup / @Teardown run around every test in their module.
module Std.Test;

class AssertionFailed : Error { AssertionFailed(string message); }

class Assert {
    static void True(bool condition);
    static void True(bool condition, string message);
    static void False(bool condition);
    static void Equal<T>(T expected, T actual);
    static void NotEqual<T>(T unexpected, T actual);
    static void Null<T>(T? value);
    static void NotNull<T>(T? value);
    /// |expected − actual| ≤ tolerance.
    static void Near(float expected, float actual, float tolerance);
    static void InRange(int value, int min, int max);
    static void Contains(string text, string fragment);
    static void SequenceEqual<T>(Iterable<T> expected, Iterable<T> actual);
    /// Runs action, expects it to raise, and returns the error (check it with `is`). Generic arguments are erased at
    /// run time, so the type is checked by name: Throws(action, "IndexError").
    static Error Throws(fn() -> void action);
    static Error Throws(fn() -> void action, string errorClass);
    static void Fail(string message);
}

/// A generator of random test inputs that knows how to shrink a failing input.
class Gen<T> {
    /// Values produced by mapping another generator's (shrinking maps too).
    Gen<U> Map<U>(fn(T) -> U f);
    Gen<T> Filter(fn(T) -> bool predicate);
    T Sample(int seed);
}

class Gens {
    static Gen<int> Int(int min, int max);
    static Gen<float> Float(float min, float max);
    static Gen<bool> Bool();
    /// Printable ASCII strings up to maxLength.
    static Gen<string> Text(int maxLength);
    static Gen<T> OneOf<T>(T[] values);
    static Gen<T[]> ArrayOf<T>(Gen<T> element, int maxLength);
    static Gen<(A, B)> Pair<A, B>(Gen<A> first, Gen<B> second);
}

/// Property-based testing with shrinking (deterministic: the seed fixes every input).
class Property {
    /// Checks property on 100 inputs from seed 0; raises AssertionFailed with the smallest counterexample found.
    static void ForAll<T>(Gen<T> gen, fn(T) -> bool property);
    static void ForAll<T>(Gen<T> gen, fn(T) -> bool property, int runs, int seed);
}

class BenchResult {
    string Name { get; }
    int Iterations { get; }
    int TotalSteps { get; }
    float StepsPerIteration { get; }
    string ToString();
}

/// Step-count micro-benchmarks: steps are the cost model, so results are exact and repeatable.
class Bench {
    static BenchResult Run(string name, fn() -> void action, int iterations);
    /// Steps used by one call.
    static int Steps(fn() -> void action);
}

/// Diagnostics. Trace output and breakpoints are collected by the host (IDE console, debugger).
class Debug {
    static void Assert(bool condition, string message);
    static void Trace(string message);
    /// A pretty-printed view of an object graph: fields, collection contents, cache counters.
    static string Inspect<T>(T value);
    /// Pauses in the IDE debugger; a no-op elsewhere (recorded in the trace).
    static void Breakpoint();
    /// Steps executed by this program so far.
    static int StepCount();
    /// Runs action and reports its steps and simulated time.
    static string Profile(fn() -> void action);
}
