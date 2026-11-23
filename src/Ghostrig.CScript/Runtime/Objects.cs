using System.Collections.Immutable;
using System.Text;
using Ghostrig.CScript.Semantics;

namespace Ghostrig.CScript.Runtime;

/// <summary>An instance of a user or native class or struct. Struct instances are copied on assignment.</summary>
public sealed class ObjectInstance(RuntimeClass @class, Value[] fields)
{
    public RuntimeClass Class { get; } = @class;
    public Value[] Fields { get; } = fields;

    /// <summary>State owned by a native base class (e.g. <see cref="ErrorState"/> for errors).</summary>
    public object? NativeState { get; set; }

    public ObjectInstance CloneStruct()
    {
        var copy = new Value[Fields.Length];
        for (var i = 0; i < Fields.Length; i++)
        {
            copy[i] = Fields[i].Object is ObjectInstance { Class.IsStruct: true } nested ? Value.FromObject(nested.CloneStruct()) : Fields[i];
        }

        return new ObjectInstance(Class, copy) { NativeState = NativeState };
    }

    public override string ToString() => Class.Name;
}

public sealed class ArrayObject(Value[] items)
{
    public Value[] Items { get; } = items;
    public int Length => Items.Length;
    public override string ToString() => $"array[{Items.Length}]";
}

/// <summary>The immutable <c>bytes</c> type.</summary>
public sealed class BytesObject(byte[] data) : IEquatable<BytesObject>
{
    public static readonly BytesObject Empty = new([]);

    public byte[] Data { get; } = data;

    public bool Equals(BytesObject? other) => other is not null && Data.AsSpan().SequenceEqual(other.Data);
    public override bool Equals(object? obj) => obj is BytesObject b && Equals(b);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Data);
        return hash.ToHashCode();
    }

    public override string ToString() => $"bytes[{Data.Length}]";
}

public sealed class TupleObject(Value[] items)
{
    public Value[] Items { get; } = items;
    public override string ToString() => "(" + string.Join(", ", Items.Select(i => i.ToString())) + ")";
}

/// <summary>A captured variable shared between a function and the closures that reference it.</summary>
public sealed class Cell(Value value)
{
    public Value Value { get; set; } = value;
}

/// <summary>A function value: closure, bound method reference, native function, or a multicast list of handlers.</summary>
public sealed class FunctionObject
{
    public FunctionObject(Callable target, Cell[] captures, Value receiver, bool hasReceiver)
    {
        Target = target;
        Captures = captures;
        Receiver = receiver;
        HasReceiver = hasReceiver;
    }

    private FunctionObject(ImmutableArray<FunctionObject> handlers)
    {
        Target = handlers[0].Target;
        Captures = [];
        Handlers = handlers;
    }

    public Callable Target { get; }
    public Cell[] Captures { get; }
    public Value Receiver { get; }
    public bool HasReceiver { get; }

    /// <summary>Non-empty for event-handler lists built with <c>+=</c>.</summary>
    public ImmutableArray<FunctionObject> Handlers { get; } = [];

    public bool IsMulticast => !Handlers.IsEmpty;

    public IEnumerable<FunctionObject> Flatten() => IsMulticast ? Handlers : [this];

    public static FunctionObject? Combine(FunctionObject? a, FunctionObject? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return new FunctionObject([.. a.Flatten(), .. b.Flatten()]);
    }

    public static FunctionObject? Remove(FunctionObject? source, FunctionObject? remove)
    {
        if (source is null || remove is null)
        {
            return source;
        }

        var list = source.Flatten().ToList();
        var index = list.FindLastIndex(h => h.SameTarget(remove));
        if (index < 0)
        {
            return source;
        }

        list.RemoveAt(index);
        return list.Count switch
        {
            0 => null,
            1 => list[0],
            _ => new FunctionObject([.. list]),
        };
    }

    private bool SameTarget(FunctionObject other) =>
        ReferenceEquals(Target, other.Target) && Receiver.Equals(other.Receiver)
        && Captures.Length == other.Captures.Length && Captures.Zip(other.Captures).All(p => ReferenceEquals(p.First, p.Second));

    public override string ToString() => IsMulticast ? $"fn[{Handlers.Length} handlers]" : $"fn {Target.Name}";
}

/// <summary>An object implemented by the host (iterators, cancellation sources, Args…).</summary>
public sealed class NativeObject(RuntimeClass @class, object state)
{
    public RuntimeClass Class { get; } = @class;
    public object State { get; } = state;
    public override string ToString() => Class.Name;
}

/// <summary>Native state behind every <c>Error</c> instance.</summary>
public sealed class ErrorState(string code, string message)
{
    public string Code { get; set; } = code;
    public string Message { get; set; } = message;
    public string StackTrace { get; set; } = string.Empty;
}

public enum TaskStatus
{
    Pending,
    Completed,
    Faulted,
}

/// <summary>A pending operation that completes in simulated time (Doc 7 §5.6).</summary>
public sealed class TaskObject(RuntimeClass @class)
{
    public RuntimeClass Class { get; } = @class;
    public TaskStatus Status { get; private set; }
    public Value Result { get; private set; }

    /// <summary>The <c>Error</c> object when <see cref="Status"/> is <see cref="TaskStatus.Faulted"/>.</summary>
    public Value Fault { get; private set; }

    public bool IsCompleted => Status != TaskStatus.Pending;

    internal List<Action<TaskObject>> Continuations { get; } = [];

    /// <summary>Set when an awaiting fiber observed the fault, so it is not reported as unhandled.</summary>
    public bool FaultObserved { get; internal set; }

    internal bool TryComplete(Value result)
    {
        if (Status != TaskStatus.Pending)
        {
            return false;
        }

        Status = TaskStatus.Completed;
        Result = result;
        RunContinuations();
        return true;
    }

    internal bool TryFault(Value error)
    {
        if (Status != TaskStatus.Pending)
        {
            return false;
        }

        Status = TaskStatus.Faulted;
        Fault = error;
        RunContinuations();
        return true;
    }

    private void RunContinuations()
    {
        var continuations = Continuations.ToArray();
        Continuations.Clear();
        foreach (var c in continuations)
        {
            c(this);
        }
    }

    public override string ToString() => $"Task({Status})";
}

/// <summary>Helpers for the built-in string representation of values (object.ToString default).</summary>
public static class ValueText
{
    public static string Describe(Value value) => value.Object switch
    {
        BytesObject b => Convert.ToHexString(b.Data).ToLowerInvariant(),
        ObjectInstance { NativeState: ErrorState e } o => $"{o.Class.Name}: {e.Message}",
        TupleObject t => "(" + string.Join(", ", t.Items.Select(Describe)) + ")",
        _ => value.ToString(),
    };

    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch { '\n' => "\\n", '\t' => "\\t", '"' => "\\\"", _ => c.ToString() });
        }

        return sb.ToString();
    }
}

/// <summary>A CScript error thrown across the host boundary (from natives into the VM).</summary>
public sealed class ScriptException(Value error, string message) : Exception(message)
{
    public Value Error { get; } = error;
}

/// <summary>Thrown by natives that need a VM-created error of a Core class (e.g. IndexError).</summary>
public sealed class ScriptErrorRequest(string className, string message) : Exception(message)
{
    public string ClassName { get; } = className;
}

internal static class SymbolText
{
    public static string Of(Symbol symbol) => symbol.Container is null ? symbol.Name : $"{symbol.Container.Name}.{symbol.Name}";
}
