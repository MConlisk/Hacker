using System.Collections.Immutable;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Runtime;

/// <summary>
/// The CScript instruction set: a compact stack machine. Every instruction costs one step (Doc 7 §8.4); allocations and
/// native calls add their published costs on top.
/// </summary>
public enum OpCode : byte
{
    Nop,

    // Constants and stack
    PushNull, PushTrue, PushFalse,
    /// <summary>A = constant index.</summary>
    PushConst,
    Pop, Dup,

    // Variables. A = slot.
    LoadLocal, StoreLocal,
    /// <summary>Wraps the current value of local A in a fresh <see cref="Cell"/> (captured variables).</summary>
    MakeCell,
    LoadCell, StoreCell,
    /// <summary>Pushes the <see cref="Cell"/> object stored in local A (to capture it in a closure).</summary>
    LoadCellRef,
    LoadCaptured, StoreCaptured,
    /// <summary>Pushes captured cell A itself (re-capturing in a nested closure).</summary>
    LoadCapturedRef,
    LoadThis,
    LoadGlobal, StoreGlobal,
    /// <summary>[obj] → [obj.Fields[A]].</summary>
    LoadField,
    /// <summary>[obj, value] → [].</summary>
    StoreField,

    // Arrays, tuples, strings, bytes
    /// <summary>[length] → [array]; A = default value constant.</summary>
    NewArray,
    /// <summary>[e0..eN-1] → [array]; A = N.</summary>
    NewArrayInit,
    LoadElement, StoreElement, ArrayLength,
    /// <summary>A = N.</summary>
    NewTuple,
    /// <summary>A = index.</summary>
    LoadTupleItem,
    StringLength, StringCharAt, BytesLength, BytesAt,

    // Objects
    /// <summary>A = class index; pushes a fresh instance with default fields.</summary>
    NewObject,
    CopyStruct,
    /// <summary>Copies the value if (at runtime) it is a struct instance; used for type-parameter-typed stores.</summary>
    CopyIfStruct,

    // Conversions and checks
    ToInt, ToFloat, ToByte, ToChar,
    /// <summary>A = enum class index.</summary>
    ToEnum,
    /// <summary>Throws NullError when null.</summary>
    UnwrapNullable,
    /// <summary>A = type test index; throws CastError on mismatch.</summary>
    CastCheck,
    /// <summary>A = type test index; pushes bool.</summary>
    IsType,
    IsNull,

    // Integer arithmetic (64-bit wrapping)
    IAdd, ISub, IMul, IDiv, IRem, IShl, IShr, IUshr, IAnd, IOr, IXor, INeg, INot,
    ILt, ILe, IGt, IGe,

    // Float arithmetic
    FAdd, FSub, FMul, FDiv, FRem, FNeg, FLt, FLe, FGt, FGe,

    // Bool
    BNot, BAnd, BOr, BXor,

    // Simulated time
    DAdd, DSub, DMulInt, DMulFloat, DDivInt, DDivFloat, DNeg,
    TAddD, TSubD, TDiff,

    // Strings and bytes
    StrConcat, BytesConcat,

    // Structural equality (Doc 7 §6)
    Equal, NotEqual,

    // Control flow. A = target instruction.
    Jump, JumpIfFalse, JumpIfTrue, JumpIfNull, JumpIfNotNull,

    // Calls. A = callable index or method slot, B = argument count including receiver.
    Call,
    CallVirtual,
    /// <summary>[fn, args…] → [result]; B = argument count.</summary>
    CallFunction,

    /// <summary>A = 1 when a value is returned.</summary>
    Return,

    // Closures and function values
    /// <summary>[cells…, this] → [fn]; A = callable index, B = capture count.</summary>
    MakeClosure,
    /// <summary>[receiver] → [fn]; A = callable index, or a virtual method slot when B = 1 (resolved on the receiver).</summary>
    MakeMethodRef,
    /// <summary>[fn, fn] → [fn].</summary>
    FunctionCombine,
    FunctionRemove,

    // Errors
    Throw,
    /// <summary>Rethrows the error held in local A.</summary>
    Rethrow,

    /// <summary>Throws a new Core error: A = constant holding the class name, B = constant holding the message.</summary>
    ThrowRuntime,

    // Async and iterators
    /// <summary>[task] → [result].</summary>
    Await,
    /// <summary>[value] → yields from an iterator.</summary>
    Yield,

    /// <summary>[value] → [string]; A = format constant or -1. Built-in formatting for primitives.</summary>
    FormatValue,
}

public readonly record struct Instruction(OpCode Op, int A = 0, int B = 0)
{
    public override string ToString() => $"{Op} {A} {B}";
}

/// <summary>An exception handler region: errors thrown in [Start, End) that match <see cref="Test"/> jump to <see cref="Handler"/>.</summary>
public readonly record struct ExceptionRegion(int Start, int End, int Handler, int ErrorLocal, TypeTest? Test);

/// <summary>Something the VM can invoke: compiled CScript code or a host implementation.</summary>
public abstract class Callable(string name, int argumentCount, bool hasReceiver)
{
    public string Name { get; } = name;

    /// <summary>Arguments including the receiver for instance methods.</summary>
    public int ArgumentCount { get; } = argumentCount;
    public bool HasReceiver { get; } = hasReceiver;

    /// <summary>Whether a call leaves a value on the caller's stack (false for void methods and async void).</summary>
    public bool ReturnsValue { get; internal set; } = true;

    public override string ToString() => Name;
}

/// <summary>Host implementation signature. <paramref name="args"/> starts with the receiver for instance methods.</summary>
public delegate Value NativeFunction(NativeContext context, ReadOnlySpan<Value> args);

public sealed class NativeCallable(string name, int argumentCount, bool hasReceiver, NativeFunction function, string key)
    : Callable(name, argumentCount, hasReceiver)
{
    public NativeFunction Function { get; } = function;
    public string Key { get; } = key;
}

/// <summary>Operations implemented directly by the interpreter (iterator protocol on generator objects).</summary>
public enum IntrinsicKind
{
    GeneratorGetIterator,
    GeneratorMoveNext,
    GeneratorCurrent,
}

public sealed class IntrinsicCallable(string name, int argumentCount, IntrinsicKind kind) : Callable(name, argumentCount, hasReceiver: true)
{
    public IntrinsicKind Kind { get; } = kind;
}

/// <summary>A compiled function body.</summary>
public sealed class CodeUnit : Callable
{
    public CodeUnit(string name, int argumentCount, bool hasReceiver, MethodSymbol? symbol) : base(name, argumentCount, hasReceiver) => Symbol = symbol;

    public MethodSymbol? Symbol { get; }
    public ImmutableArray<Instruction> Code { get; internal set; } = [];
    public ImmutableArray<Value> Constants { get; internal set; } = [];
    public ImmutableArray<ExceptionRegion> Handlers { get; internal set; } = [];

    /// <summary>Source line of each instruction (for stack traces and the profiler).</summary>
    public ImmutableArray<int> Lines { get; internal set; } = [];

    public string SourcePath { get; internal set; } = "<unknown>";
    public int LocalCount { get; internal set; }
    public FunctionFlavor Flavor { get; internal set; }

    /// <summary>Parameter slots to wrap in cells on entry because a closure captures them.</summary>
    public ImmutableArray<int> CellParameters { get; internal set; } = [];

    public int LineAt(int ip) => ip >= 0 && ip < Lines.Length ? Lines[ip] : 0;
}
