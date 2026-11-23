/// Core types, available to every module without an import (Document 7 §13.1).
/// Declarations without bodies are implemented natively by the runtime and charged by published step costs.
module Core;

// ---------------------------------------------------------------- iteration and comparison

/// Anything that can be walked with foreach.
interface Iterable<T> { Iterator<T> GetIterator(); }

interface Iterator<T> {
    bool MoveNext();
    T Current { get; }
}

/// Anything that can be walked with await foreach.
interface AsyncIterable<T> { AsyncIterator<T> GetAsyncIterator(); }

interface AsyncIterator<T> {
    Task<bool> MoveNextAsync();
    T Current { get; }
}

interface Comparable<T> { int CompareTo(T other); }

interface Equatable<T> { bool Equals(T other); }

interface Hashable { int HashCode(); }

/// Marker for types that Std.Text.Json can serialize.
interface Serializable { }

// ---------------------------------------------------------------- primitives

@Primitive("object")
class object {
    string ToString();
    int HashCode();
    bool Equals(object? other);
}

@Primitive("bool")
struct bool : Equatable<bool>, Hashable {
    static bool Parse(string text);
    string ToString();
    bool Equals(bool other);
    int HashCode();
}

@Primitive("int")
struct int : Comparable<int>, Equatable<int>, Hashable {
    const int MaxValue = 9223372036854775807;
    const int MinValue = -9223372036854775807 - 1;
    static int Parse(string text);
    static int? TryParse(string text);
    static int ParseHex(string text);
    string ToString();
    /// Formats with a .NET-style format string: "D8", "X2", "N0".
    string ToString(string format);
    string ToHex();
    int CompareTo(int other);
    bool Equals(int other);
    int HashCode();
}

@Primitive("float")
struct float : Comparable<float>, Equatable<float>, Hashable {
    const float Epsilon = 4.9e-324;
    static float Parse(string text);
    static float? TryParse(string text);
    static float NaN { get; }
    static float PositiveInfinity { get; }
    static float NegativeInfinity { get; }
    bool IsNaN();
    bool IsFinite();
    string ToString();
    string ToString(string format);
    int CompareTo(float other);
    bool Equals(float other);
    int HashCode();
}

@Primitive("byte")
struct byte : Comparable<byte>, Equatable<byte>, Hashable {
    const byte MaxValue = 255;
    static byte Parse(string text);
    string ToString();
    string ToHex();
    int CompareTo(byte other);
    bool Equals(byte other);
    int HashCode();
}

@Primitive("char")
struct char : Comparable<char>, Equatable<char>, Hashable {
    static bool IsDigit(char c);
    static bool IsLetter(char c);
    static bool IsLetterOrDigit(char c);
    static bool IsWhiteSpace(char c);
    static bool IsUpper(char c);
    static bool IsLower(char c);
    static char ToUpper(char c);
    static char ToLower(char c);
    static char FromCode(int code);
    int Code { get; }
    string ToString();
    int CompareTo(char other);
    bool Equals(char other);
    int HashCode();
}

@Primitive("string")
class string : Comparable<string>, Equatable<string>, Hashable, Iterable<char> {
    static string Empty { get; }
    static bool IsNullOrEmpty(string? value);
    static bool IsNullOrWhiteSpace(string? value);
    static string Join(string separator, Iterable<string> parts);
    static string Repeat(string value, int count);
    static string FromChars(char[] chars);

    int Length { get; }
    char this[int index] { get; }
    bool IsEmpty { get; }

    string Substring(int start);
    string Substring(int start, int length);
    int IndexOf(string value);
    int IndexOf(string value, int start);
    int LastIndexOf(string value);
    bool Contains(string value);
    bool StartsWith(string value);
    bool EndsWith(string value);
    string[] Split(string separator);
    string[] Split(string separator, int maxParts);
    string[] Lines();
    string Trim();
    string TrimStart();
    string TrimEnd();
    string ToUpper();
    string ToLower();
    string Replace(string oldValue, string newValue);
    string PadLeft(int width);
    string PadLeft(int width, char fill);
    string PadRight(int width);
    string PadRight(int width, char fill);
    string Reverse();
    char[] ToChars();
    /// UTF-8 encoding of the text.
    bytes ToBytes();

    Iterator<char> GetIterator();
    string ToString();
    int CompareTo(string other);
    bool Equals(string other);
    int HashCode();
}

@Primitive("bytes")
class bytes : Equatable<bytes>, Hashable, Iterable<byte> {
    static bytes Empty { get; }
    static bytes FromHex(string hex);
    /// UTF-8 encoding of text.
    static bytes FromText(string text);
    static bytes FromArray(byte[] data);
    static bytes Concat(params bytes[] parts);

    int Length { get; }
    byte this[int index] { get; }
    bytes Slice(int start, int count);
    bytes Slice(int start);
    int IndexOf(bytes needle);
    bool StartsWith(bytes prefix);
    string ToHex();
    /// Decodes as UTF-8.
    string ToText();
    byte[] ToArray();

    Iterator<byte> GetIterator();
    string ToString();
    bool Equals(bytes other);
    int HashCode();
}

@Primitive("Duration")
struct Duration : Comparable<Duration>, Equatable<Duration>, Hashable {
    static Duration Zero { get; }
    static Duration FromNanoseconds(int value);
    static Duration FromMicroseconds(int value);
    static Duration FromMilliseconds(int value);
    static Duration FromSeconds(int value);
    static Duration FromMinutes(int value);
    static Duration FromHours(int value);
    int TotalNanoseconds { get; }
    float TotalMilliseconds { get; }
    float TotalSeconds { get; }
    string ToString();
    int CompareTo(Duration other);
    bool Equals(Duration other);
    int HashCode();
}

@Primitive("Instant")
struct Instant : Comparable<Instant>, Equatable<Instant>, Hashable {
    /// Nanoseconds since the campaign epoch.
    int Nanoseconds { get; }
    string ToString();
    int CompareTo(Instant other);
    bool Equals(Instant other);
    int HashCode();
}

/// Members of every array type T[].
@Primitive("array")
class Array<T> : Iterable<T> {
    int Length { get; }
    T this[int index] { get; set; }
    T[] Slice(int start, int count);
    T[] Slice(int start);
    T[] Copy();
    int IndexOf(T value);
    bool Contains(T value);
    void Fill(T value);
    Iterator<T> GetIterator();
}

// ---------------------------------------------------------------- tasks (Document 7 §5.6)

/// A pending operation that completes in simulated time.
class Task {
    static Task CompletedTask { get; }
    /// Completes after the given simulated duration (not available to drivers).
    static Task Delay(Duration duration);
    static Task WhenAll(params Task[] tasks);
    /// Completes when any task completes; the result is that task's index.
    static Task<int> WhenAny(params Task[] tasks);
    bool IsCompleted { get; }
    bool IsFaulted { get; }
    Error? Fault { get; }
}

class Task<T> : Task {
    static Task<T> FromResult(T value);
    T Result { get; }
}

class CancellationSource {
    CancellationSource();
    CancellationToken Token { get; }
    void Cancel();
    void CancelAfter(Duration delay);
}

struct CancellationToken {
    static CancellationToken None { get; }
    bool IsCancellationRequested { get; }
    void ThrowIfCancelled();
}

// ---------------------------------------------------------------- program arguments

/// Command-line arguments passed to an application's Main.
class Args : Iterable<string> {
    int Count { get; }
    string this[int index] { get; }
    string[] Positional { get; }
    bool Flag(string name);
    string? Option(string name);
    Iterator<string> GetIterator();
}

// ---------------------------------------------------------------- optional values and results

struct Option<T> {
    static Option<T> Some(T value);
    static Option<T> None { get; }
    bool HasValue { get; }
    T Unwrap();
    T UnwrapOr(T fallback);
}

struct Result<T, E> {
    static Result<T, E> Ok(T value);
    static Result<T, E> Fail(E error);
    bool IsOk { get; }
    T Unwrap();
    T UnwrapOr(T fallback);
    E ErrorValue { get; }
}

// ---------------------------------------------------------------- errors (Document 7 §7)

/// Base of every error. Kernel faults surface with their authentic codes.
class Error {
    Error(string message);
    Error(string message, string code);
    string Code { get; }
    string Message { get; }
    string StackTrace { get; }
    string ToString();
}

class AccessDenied : Error { AccessDenied(string message); }
class NotFound : Error { NotFound(string message); }
class AlreadyExists : Error { AlreadyExists(string message); }
class NotEmpty : Error { NotEmpty(string message); }
class IsDirectory : Error { IsDirectory(string message); }
class NotDirectory : Error { NotDirectory(string message); }
class InvalidPath : Error { InvalidPath(string message); }
class DnsError : Error { DnsError(string message); }
class HostUnreachable : Error { HostUnreachable(string message); }
class ConnectionRefused : Error { ConnectionRefused(string message); }
class AuthFailed : Error { AuthFailed(string message); }
class ProtocolError : Error { ProtocolError(string message); }
class HardwareError : Error { HardwareError(string message); }
class OutOfMemory : Error { OutOfMemory(string message); }
class ArithmeticError : Error { ArithmeticError(string message); }
class IndexError : Error { IndexError(string message); }
class NullError : Error { NullError(string message); }
class CastError : Error { CastError(string message); }
class FormatError : Error { FormatError(string message); }
class ArgumentError : Error { ArgumentError(string message); }
class CancelledError : Error { CancelledError(string message); }
class CompileError : Error { CompileError(string message); }

/// Uncatchable: aborts the event and triggers rollback (Document 7 §8.6).
@Uncatchable class StepBudgetExceeded : Error { }

/// Uncatchable: the rig halted after a hardware protection fault.
@Uncatchable class KernelPanic : Error { }
