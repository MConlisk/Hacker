using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;

namespace Ghostrig.CScript.Vm;

/// <summary>One activation of a <see cref="CodeUnit"/>. Frames are heap objects so fibers can suspend without host recursion.</summary>
public sealed class Frame
{
    internal Frame(CodeUnit unit, Value receiver, Cell[] captures)
    {
        Unit = unit;
        Locals = new Value[Math.Max(unit.LocalCount, unit.ArgumentCount)];
        Stack = new Value[16];
        This = receiver;
        Captures = captures;
    }

    public CodeUnit Unit { get; }
    public int Ip { get; internal set; }
    internal Value[] Locals { get; }
    internal Value[] Stack { get; private set; }
    internal int Sp { get; set; }
    internal Value This { get; }
    internal Cell[] Captures { get; }

    /// <summary>Set for frames that run an iterator body.</summary>
    internal GeneratorObject? Generator { get; set; }

    /// <summary>Marks the frame entered by a synchronous host callback; returning from it ends the nested run.</summary>
    internal bool IsBarrier { get; set; }

    public int Line => Unit.LineAt(Math.Max(0, Ip - 1));

    internal void Push(Value value)
    {
        if (Sp == Stack.Length)
        {
            var bigger = new Value[Stack.Length * 2];
            Array.Copy(Stack, bigger, Stack.Length);
            Stack = bigger;
        }

        Stack[Sp++] = value;
    }

    internal Value Pop() => Stack[--Sp];

    internal Value Peek() => Stack[Sp - 1];
}

public enum FiberState
{
    Ready,
    Running,
    Waiting,
    Completed,
    Faulted,
}

/// <summary>
/// A lightweight thread of CScript execution: a stack of frames plus scheduling state. Async functions each run on their own
/// fiber; awaiting an incomplete task suspends the fiber until the task completes in simulated time (Doc 7 §8.3).
/// </summary>
public sealed class Fiber
{
    internal Fiber(string name, long id)
    {
        Name = name;
        Id = id;
    }

    public long Id { get; }
    public string Name { get; }
    public FiberState State { get; internal set; } = FiberState.Ready;

    internal List<Frame> Frames { get; } = [];

    /// <summary>The task this fiber completes (async functions); null for event and synchronous fibers.</summary>
    public TaskObject? Task { get; internal set; }

    /// <summary>Step budget per run segment (Doc 7 §8.5); null means unlimited (workers, async continuations of workers).</summary>
    public long? Budget { get; internal set; }

    internal long BudgetRemaining { get; set; }

    /// <summary>Undo log for the current segment when rollback is enabled (event fibers).</summary>
    internal Journal? Journal { get; set; }

    internal bool HasResume { get; set; }
    internal bool ResumeThrows { get; set; }
    internal Value ResumeValue { get; set; }

    /// <summary>Final result of a non-async fiber.</summary>
    public Value Result { get; internal set; }

    /// <summary>The error that terminated the fiber, if any.</summary>
    public Value Error { get; internal set; }

    internal Frame Top => Frames[^1];

    public override string ToString() => $"fiber#{Id} {Name} ({State})";
}

/// <summary>Records overwritten values so a faulted event can be rolled back (Doc 7 §8.6).</summary>
internal sealed class Journal
{
    private static long s_epochs;
    private readonly List<Action> _undo = [];

    public int Count => _undo.Count;

    /// <summary>Identifies the current segment; native state snapshots at most once per epoch (see <see cref="Natives.NativeState"/>).</summary>
    public long Epoch { get; private set; } = Interlocked.Increment(ref s_epochs);

    public void Record(Action undo) => _undo.Add(undo);

    public void Rollback()
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            _undo[i]();
        }

        _undo.Clear();
        Epoch = Interlocked.Increment(ref s_epochs);
    }

    public void Commit()
    {
        _undo.Clear();
        Epoch = Interlocked.Increment(ref s_epochs);
    }
}

public enum FaultKind
{
    UnhandledError,
    StepBudgetExceeded,
    UnobservedTaskFault,
}

/// <summary>A runtime fault reported to the host (System Profiler, Doc 3 §2.2).</summary>
public sealed record VmFault(FaultKind Kind, string FiberName, string Message, string StackTrace, Value Error, long AtNanoseconds, bool RolledBack);
