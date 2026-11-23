using System.Collections.Frozen;
using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Natives;

/// <summary>
/// Maps native declaration keys (<c>Module.Type.Member/arity</c>, see <see cref="Semantics.MethodSymbol.NativeKey"/>) to host
/// implementations. Libraries register themselves; hosts (the kernel) add system libraries on top.
/// </summary>
public sealed class NativeRegistry
{
    private readonly Dictionary<string, NativeFunction> _functions = new(StringComparer.Ordinal);

    private static readonly Lazy<NativeRegistry> LazyDefault = new(() =>
    {
        var registry = new NativeRegistry();
        CoreNatives.Register(registry);
        Std.StdNatives.Register(registry);
        return registry;
    });

    /// <summary>Core and standard-library (<c>Std.*</c>) natives; hosts add system libraries with <see cref="With"/>.</summary>
    public static NativeRegistry Default => LazyDefault.Value;

    public IEnumerable<string> Keys => _functions.Keys.Order(StringComparer.Ordinal);

    public NativeRegistry Add(string key, NativeFunction function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_functions.TryAdd(key, function))
        {
            throw new InvalidOperationException($"Native '{key}' is registered twice.");
        }

        return this;
    }

    /// <summary>Creates a registry containing this registry's natives plus more.</summary>
    public NativeRegistry With(Action<NativeRegistry> register)
    {
        var copy = new NativeRegistry();
        foreach (var (key, function) in _functions)
        {
            copy._functions[key] = function;
        }

        register(copy);
        return copy;
    }

    public bool TryGet(string key, out NativeFunction function) => _functions.TryGetValue(key, out function!);
}

/// <summary>Host-side iterator exposed to CScript through the Iterator&lt;T&gt; interface.</summary>
public abstract class NativeIterator
{
    public abstract Value Current { get; }
    public abstract bool MoveNext(NativeContext context);
}

public sealed class EnumeratorIterator(IEnumerator<Value> enumerator) : NativeIterator
{
    public override Value Current => enumerator.Current;

    public override bool MoveNext(NativeContext context)
    {
        context.Charge(1);
        return enumerator.MoveNext();
    }
}

/// <summary>Stable error codes for Core error classes (Doc 7 §7).</summary>
public static class ErrorCodes
{
    private static readonly FrozenDictionary<string, string> Codes = new Dictionary<string, string>
    {
        ["AccessDenied"] = "403",
        ["NotFound"] = "ENOENT",
        ["AlreadyExists"] = "EEXIST",
        ["NotEmpty"] = "ENOTEMPTY",
        ["IsDirectory"] = "EISDIR",
        ["NotDirectory"] = "ENOTDIR",
        ["InvalidPath"] = "EINVAL",
        ["DnsError"] = "NXDOMAIN",
        ["HostUnreachable"] = "EHOSTUNREACH",
        ["ConnectionRefused"] = "ECONNREFUSED",
        ["AuthFailed"] = "401",
        ["ProtocolError"] = "EPROTO",
        ["HardwareError"] = "HW_CONFIG",
        ["OutOfMemory"] = "ENOMEM",
        ["ArithmeticError"] = "EARITH",
        ["IndexError"] = "EINDEX",
        ["NullError"] = "ENULL",
        ["CastError"] = "ECAST",
        ["FormatError"] = "EFORMAT",
        ["ArgumentError"] = "EARG",
        ["CancelledError"] = "ECANCELED",
        ["CompileError"] = "ECOMPILE",
        ["StepBudgetExceeded"] = "STEP_BUDGET",
        ["KernelPanic"] = "KERNEL_PANIC",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string For(string className) => Codes.TryGetValue(className, out var code) ? code : "ERROR";

    public static IEnumerable<string> ErrorClasses => Codes.Keys;
}

/// <summary>Deterministic hashing (Doc 7 §9): never the host's randomized string hash.</summary>
public static class StableHash
{
    private const long GoldenRatio = unchecked((long)0x9E3779B97F4A7C15UL);

    public static long Of(string text)
    {
        unchecked
        {
            var hash = (long)0xcbf29ce484222325;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 0x100000001b3;
            }

            return hash;
        }
    }

    public static long Of(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            var hash = (long)0xcbf29ce484222325;
            foreach (var b in data)
            {
                hash ^= b;
                hash *= 0x100000001b3;
            }

            return hash;
        }
    }

    /// <summary>Content hash for values; reference objects use <paramref name="identity"/> (allocation-order ids, never addresses).</summary>
    public static long Of(Value value, Func<object, long> identity) => value.Object switch
    {
        string s => Of(s),
        BytesObject b => Of(b.Data),
        TupleObject t => t.Items.Aggregate(17L, (h, v) => unchecked((h * 31) + Of(v, identity))),
        ObjectInstance { Class.IsStruct: true } s => s.Fields.Aggregate(19L, (h, v) => unchecked((h * 31) + Of(v, identity))),
        RuntimeClass when value.Tag == ValueTag.Enum => unchecked(value.Bits * GoldenRatio),
        null => value.Tag == ValueTag.Null ? 0 : unchecked((value.Bits * GoldenRatio) ^ (long)value.Tag),
        _ => identity(value.Object),
    };

    public static string Utf8(byte[] data) => Encoding.UTF8.GetString(data);
}
