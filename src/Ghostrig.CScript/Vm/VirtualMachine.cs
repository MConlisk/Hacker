using System.Globalization;
using System.Text;
using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;

namespace Ghostrig.CScript.Vm;

/// <summary>Supplies simulated time and receives step charges (the kernel in the game, a virtual clock in tests).</summary>
public interface IVmHost
{
    long Now { get; }

    /// <summary>Moves the clock forward to <paramref name="nanoseconds"/> (never backwards).</summary>
    void AdvanceTo(long nanoseconds);

    /// <summary>Charges executed steps; the host converts them to CPU cycles, heat and simulated time.</summary>
    void OnSteps(long steps);
}

/// <summary>Standalone host: each step costs a fixed number of simulated nanoseconds.</summary>
public sealed class VirtualClockHost(long nanosecondsPerStep = 1) : IVmHost
{
    public long Now { get; private set; }

    public long StepsCharged { get; private set; }

    public void AdvanceTo(long nanoseconds) => Now = Math.Max(Now, nanoseconds);

    public void OnSteps(long steps)
    {
        StepsCharged += steps;
        Now += steps * nanosecondsPerStep;
    }
}

public sealed record VmOptions
{
    /// <summary>Steps a fiber runs before yielding to others (preemption at instruction boundaries).</summary>
    public long Quantum { get; init; } = 100_000;

    /// <summary>Default per-event budget for <see cref="VirtualMachine.StartEvent"/> (Doc 7 §8.5).</summary>
    public long DefaultEventBudget { get; init; } = 2_000_000;

    public int MaxFrames { get; init; } = 2_048;

    /// <summary>Maximum nesting of host callbacks and synchronously started async functions.</summary>
    public int MaxNesting { get; init; } = 128;
}

/// <summary>
/// The step-metered CScript interpreter (Doc 7 §8). Deterministic: scheduling depends only on simulated time, enqueue order
/// and step counts — never on the host.
/// </summary>
public sealed partial class VirtualMachine
{
    private Value[] _globals;
    private readonly Queue<Fiber> _ready = new();
    private readonly SortedDictionary<(long Time, long Sequence), Action> _timers = new();
    private readonly NativeContext _context;
    private readonly List<VmFault> _faults = [];
    private long _timerSequence;
    private long _fiberSequence;
    private long _uncharged;
    private int _nesting;
    private Fiber? _current;

    public VirtualMachine(ProgramImage image, IVmHost? host = null, VmOptions? options = null)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Host = host ?? new VirtualClockHost();
        Options = options ?? new VmOptions();
        _globals = new Value[image.GlobalCount];
        _context = new NativeContext(this);
    }

    public ProgramImage Image { get; private set; }
    public IVmHost Host { get; }
    public VmOptions Options { get; }

    /// <summary>Total steps executed (instructions plus native charges).</summary>
    public long Steps { get; private set; }

    public IReadOnlyList<VmFault> Faults => _faults;

    /// <summary>Lines written by <c>Debug.Trace</c> (and breakpoint markers), oldest first; the IDE console drains it.</summary>
    public List<string> TraceLog { get; } = [];

    /// <summary>Raised by <c>Debug.Breakpoint()</c>; the IDE debugger pauses the simulation here (Doc 7 §14.3).</summary>
    public event Action<VirtualMachine>? BreakpointHit;

    internal void OnBreakpoint()
    {
        TraceLog.Add($"[breakpoint at T+{Host.Now}ns, step {Steps}]");
        BreakpointHit?.Invoke(this);
    }

    public bool IsIdle => _ready.Count == 0 && _timers.Count == 0;

    public Value GetGlobal(int slot) => _globals[slot];

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<long>> _identities = new();
    private long _identitySequence;

    internal long IdentityHash(object obj) =>
        _identities.GetValue(obj, _ => new System.Runtime.CompilerServices.StrongBox<long>(++_identitySequence)).Value;

    #region Host API

    /// <summary>Runs every module initializer (imports first).</summary>
    public void Initialize()
    {
        foreach (var initializer in Image.Initializers)
        {
            Invoke(initializer);
        }
    }

    /// <summary>
    /// Calls a function and runs the scheduler (advancing simulated time for awaits) until it finishes. Throws
    /// <see cref="ScriptException"/> if the function fails.
    /// </summary>
    public Value Invoke(Callable callable, params Value[] arguments)
    {
        ArgumentNullException.ThrowIfNull(callable);
        switch (callable)
        {
            case NativeCallable native:
                return CallNative(native, arguments);
            case CodeUnit { Flavor: Semantics.FunctionFlavor.Async } asyncUnit:
            {
                var task = StartAsync(asyncUnit, Value.Null, arguments, [], budget: null);
                RunUntil(() => task is null || task.IsCompleted);
                if (task is null)
                {
                    return Value.Null;
                }

                if (task.Status == Runtime.TaskStatus.Faulted)
                {
                    task.FaultObserved = true;
                    throw new ScriptException(task.Fault, DescribeError(task.Fault));
                }

                if (task.Status == Runtime.TaskStatus.Pending)
                {
                    throw new InvalidOperationException($"{callable.Name} never completed (deadlock: nothing left to run).");
                }

                return task.Result;
            }

            case CodeUnit unit:
            {
                var fiber = NewFiber(unit.Name, budget: null, journal: false);
                if (!Enter(fiber, unit, unit.HasReceiver && arguments.Length > 0 ? arguments[0] : Value.Null,
                        unit.HasReceiver && arguments.Length > 0 ? arguments.AsSpan(1) : arguments, [], out var immediate))
                {
                    return immediate;
                }

                _ready.Enqueue(fiber);
                RunUntil(() => fiber.State is FiberState.Completed or FiberState.Faulted);
                if (fiber.State == FiberState.Faulted)
                {
                    throw new ScriptException(fiber.Error, DescribeError(fiber.Error));
                }

                if (fiber.State != FiberState.Completed)
                {
                    throw new InvalidOperationException($"{callable.Name} never completed (deadlock: nothing left to run).");
                }

                return fiber.Result;
            }

            default:
                throw new ArgumentException($"Cannot invoke {callable.GetType().Name} directly.", nameof(callable));
        }
    }

    /// <summary>
    /// Starts an event handler on its own fiber with a step budget and rollback journal (Doc 7 §8.5–8.6). It runs when the
    /// scheduler next runs.
    /// </summary>
    public Fiber StartEvent(Callable callable, Value[] arguments, long? budget = -1, string? name = null)
    {
        var effectiveBudget = budget == -1 ? Options.DefaultEventBudget : budget;
        var fiber = NewFiber(name ?? callable.Name, effectiveBudget, journal: true);
        if (callable is CodeUnit unit)
        {
            if (unit.Flavor == Semantics.FunctionFlavor.Async)
            {
                fiber = CreateAsyncFiber(unit, Value.Null, arguments, [], effectiveBudget, out _);
                fiber.Journal = new Journal();
                _ready.Enqueue(fiber);
            }
            else if (Enter(fiber, unit, Value.Null, arguments, [], out _))
            {
                _ready.Enqueue(fiber);
            }
        }
        else if (callable is NativeCallable native)
        {
            CallNative(native, arguments);
            fiber.State = FiberState.Completed;
        }

        return fiber;
    }

    /// <summary>Runs ready fibers and fires timers until nothing is left, or until simulated time would pass <paramref name="deadline"/>.</summary>
    public void RunUntilIdle(long? deadline = null) => RunUntil(() => false, deadline);

    private void RunUntil(Func<bool> done, long? deadline = null)
    {
        while (!done())
        {
            FireDueTimers();
            if (_ready.TryDequeue(out var fiber))
            {
                RunFiber(fiber, Options.Quantum);
                continue;
            }

            if (_timers.Count == 0)
            {
                return;
            }

            var (time, _) = _timers.Keys.First();
            if (deadline is not null && time > deadline)
            {
                Host.AdvanceTo(deadline.Value);
                return;
            }

            Host.AdvanceTo(time);
            FireDueTimers();
        }
    }

    private void FireDueTimers()
    {
        while (_timers.Count > 0)
        {
            var key = _timers.Keys.First();
            if (key.Time > Host.Now)
            {
                return;
            }

            var action = _timers[key];
            _timers.Remove(key);
            action();
        }
    }

    internal void ScheduleTimer(long at, Action action) => _timers[(at, _timerSequence++)] = action;

    #endregion

    #region Fibers

    private Fiber NewFiber(string name, long? budget, bool journal) =>
        new(name, ++_fiberSequence) { Budget = budget, Journal = journal ? new Journal() : null };

    /// <summary>Runs one fiber segment from the ready queue (or a nested start) and handles its outcome.</summary>
    private void RunFiber(Fiber fiber, long quantum)
    {
        fiber.BudgetRemaining = fiber.Budget ?? long.MaxValue;
        var status = Execute(fiber, quantum, barrier: null, out _);
        FlushSteps();
        switch (status)
        {
            case ExecStatus.QuantumExpired:
                fiber.State = FiberState.Ready;
                fiber.Journal?.Commit();
                _ready.Enqueue(fiber);
                break;
            case ExecStatus.Suspended:
            case ExecStatus.Completed:
                fiber.Journal?.Commit();
                break;
            case ExecStatus.Faulted:
                OnFiberFaulted(fiber);
                break;
        }
    }

    private void OnFiberFaulted(Fiber fiber)
    {
        var isBudget = IsErrorClass(fiber.Error, "StepBudgetExceeded");
        var rolledBack = false;
        if (fiber.Journal is { } journal)
        {
            journal.Rollback();
            rolledBack = true;
        }

        if (fiber.Task is { } task)
        {
            task.TryFault(fiber.Error);
            if (!isBudget)
            {
                return; // observed by awaiters (or reported as unobserved below if nobody awaits)
            }
        }

        _faults.Add(new VmFault(isBudget ? FaultKind.StepBudgetExceeded : FaultKind.UnhandledError, fiber.Name, DescribeError(fiber.Error),
            ErrorStack(fiber.Error), fiber.Error, Host.Now, rolledBack));
    }

    /// <summary>Starts an async function: a new fiber runs synchronously until its first incomplete await (C# semantics).</summary>
    private Fiber CreateAsyncFiber(CodeUnit unit, Value receiver, ReadOnlySpan<Value> parameters, Cell[] captures, long? budget, out TaskObject? task)
    {
        var isGeneric = unit.Symbol?.ReturnType is Semantics.NamedTypeSymbol { TypeArguments.Length: 1 };
        task = unit.ReturnsValue ? new TaskObject(isGeneric ? Image.TaskOfTClass : Image.TaskClass) : null;
        var child = NewFiber(unit.Name, budget, journal: false);
        child.Task = task;
        child.Frames.Add(CreateFrame(unit, receiver, parameters, captures));
        return child;
    }

    private TaskObject? StartAsync(CodeUnit unit, Value receiver, ReadOnlySpan<Value> parameters, Cell[] captures, long? budget)
    {
        var child = CreateAsyncFiber(unit, receiver, parameters, captures, budget, out var task);

        if (++_nesting > Options.MaxNesting)
        {
            _nesting--;
            throw new ScriptErrorRequest("Error", "Async calls are nested too deeply.");
        }

        try
        {
            child.BudgetRemaining = child.Budget ?? long.MaxValue;
            var saved = _current;
            var status = Execute(child, long.MaxValue, barrier: null, out _);
            _current = saved;
            if (status == ExecStatus.Faulted)
            {
                OnAsyncFault(child);
            }
        }
        finally
        {
            _nesting--;
        }

        return task;
    }

    private void OnAsyncFault(Fiber child)
    {
        if (child.Task is { } task)
        {
            task.TryFault(child.Error);
        }
        else
        {
            _faults.Add(new VmFault(FaultKind.UnhandledError, child.Name, DescribeError(child.Error), ErrorStack(child.Error), child.Error, Host.Now, false));
        }
    }

    private Frame CreateFrame(CodeUnit unit, Value receiver, ReadOnlySpan<Value> parameters, Cell[] captures)
    {
        var frame = new Frame(unit, receiver, captures);
        parameters.CopyTo(frame.Locals);
        foreach (var slot in unit.CellParameters)
        {
            frame.Locals[slot] = Value.FromObject(new Cell(frame.Locals[slot]));
        }

        return frame;
    }

    /// <summary>
    /// Begins a call on <paramref name="fiber"/>. Returns true when a frame was pushed (execution continues in the callee);
    /// false when the call completed immediately (native, iterator creation, async start) with <paramref name="result"/>.
    /// </summary>
    private bool Enter(Fiber fiber, Callable callable, Value receiver, ReadOnlySpan<Value> parameters, Cell[] captures, out Value result)
    {
        result = Value.Null;
        switch (callable)
        {
            case CodeUnit unit:
                switch (unit.Flavor)
                {
                    case Semantics.FunctionFlavor.Async:
                        result = StartAsync(unit, receiver, parameters, captures, fiber.Budget) is { } task ? Value.FromObject(task) : Value.Null;
                        return false;
                    case Semantics.FunctionFlavor.Iterator:
                    {
                        var generator = new GeneratorObject(unit);
                        var frame = CreateFrame(unit, receiver, parameters, captures);
                        frame.Generator = generator;
                        generator.Frame = frame;
                        result = Value.FromObject(generator);
                        return false;
                    }

                    case Semantics.FunctionFlavor.AsyncIterator:
                        throw new ScriptErrorRequest("Error", "Async iterators are not supported yet.");
                }

                if (fiber.Frames.Count >= Options.MaxFrames)
                {
                    throw new ScriptErrorRequest("Error", "Call stack overflow.");
                }

                fiber.Frames.Add(CreateFrame(unit, receiver, parameters, captures));
                return true;

            case NativeCallable native:
            {
                if (native.HasReceiver)
                {
                    var args = new Value[parameters.Length + 1];
                    args[0] = receiver;
                    parameters.CopyTo(args.AsSpan(1));
                    result = CallNative(native, args);
                }
                else
                {
                    result = CallNative(native, parameters.ToArray());
                }

                return false;
            }

            case IntrinsicCallable intrinsic:
                return EnterIntrinsic(fiber, intrinsic, receiver, out result);

            default:
                throw new InvalidOperationException($"Unknown callable {callable.GetType().Name}.");
        }
    }

    private bool EnterIntrinsic(Fiber fiber, IntrinsicCallable intrinsic, Value receiver, out Value result)
    {
        var generator = receiver.Object as GeneratorObject ?? throw new ScriptErrorRequest("NullError", "Iterator is null.");
        result = Value.Null;
        switch (intrinsic.Kind)
        {
            case IntrinsicKind.GeneratorGetIterator:
                result = receiver;
                return false;
            case IntrinsicKind.GeneratorCurrent:
                result = generator.Current;
                return false;
            default:
                if (generator.Finished || generator.Frame is null)
                {
                    result = Value.False;
                    return false;
                }

                generator.Started = true;
                fiber.Frames.Add(generator.Frame);
                return true;
        }
    }

    private Value CallNative(NativeCallable native, Value[] args)
    {
        Steps++;
        _uncharged++;
        return native.Function(_context, args);
    }

    /// <summary>Snapshots mutable native state once per journal segment so a faulted event restores it (Doc 7 §8.6).</summary>
    internal void JournalNativeState(Natives.NativeState state)
    {
        if (_current?.Journal is not { } journal || state.SnapshotEpoch == journal.Epoch)
        {
            return;
        }

        var snapshot = state.CaptureSnapshot();
        state.SnapshotEpoch = journal.Epoch;
        journal.Record(() => state.RestoreSnapshot(snapshot));
    }

    /// <summary>Journals a whole script array before a native rewrites it in place (sort, shuffle, fill).</summary>
    internal void JournalArray(Value[] items)
    {
        if (_current?.Journal is { } journal && items.Length > 0)
        {
            var copy = (Value[])items.Clone();
            journal.Record(() => copy.CopyTo(items, 0));
        }
    }

    internal Callable ResolveVirtual(int slot, Value receiver) => Resolve(slot, receiver);

    internal void ChargeNative(long steps)
    {
        Steps += steps;
        _uncharged += steps;
        if (_current is { Budget: not null } fiber)
        {
            fiber.BudgetRemaining -= steps;
        }
    }

    private void FlushSteps()
    {
        if (_uncharged > 0)
        {
            Host.OnSteps(_uncharged);
            _uncharged = 0;
        }
    }

    #endregion

    #region Synchronous callbacks

    internal Value InvokeSynchronously(FunctionObject function, Value[] arguments)
    {
        if (function.IsMulticast)
        {
            var last = Value.Null;
            foreach (var handler in function.Handlers)
            {
                last = InvokeSynchronously(handler, arguments);
            }

            return last;
        }

        return InvokeTarget(function.Target, function.HasReceiver ? function.Receiver : Value.Null, arguments, function.Captures);
    }

    internal Value InvokeVirtualSynchronously(int slot, Value receiver, Value[] arguments)
    {
        var target = Resolve(slot, receiver);
        return InvokeTarget(target, receiver, arguments, []);
    }

    private Value InvokeTarget(Callable target, Value receiver, Value[] arguments, Cell[] captures)
    {
        var fiber = _current ?? NewFiber("<callback>", null, journal: false);
        if (++_nesting > Options.MaxNesting)
        {
            _nesting--;
            throw new ScriptErrorRequest("Error", "Callbacks are nested too deeply.");
        }

        try
        {
            if (!Enter(fiber, target, receiver, arguments, captures, out var immediate))
            {
                return immediate;
            }

            var barrier = fiber.Top;
            barrier.IsBarrier = true;
            var saved = _current;
            var status = Execute(fiber, long.MaxValue, barrier, out var value);
            _current = saved;
            return status switch
            {
                ExecStatus.BarrierReturned => value,
                _ => throw new ScriptErrorRequest("Error", "A synchronous callback cannot await."),
            };
        }
        finally
        {
            _nesting--;
        }
    }

    #endregion

    #region Errors

    private bool IsErrorClass(Value error, string className) =>
        error.Object is ObjectInstance o && o.Class.IsSubclassOf(Image.ErrorClass(className));

    private bool IsUncatchable(Value error) => IsErrorClass(error, "StepBudgetExceeded") || IsErrorClass(error, "KernelPanic");

    public static string DescribeError(Value error) => error.Object switch
    {
        ObjectInstance { NativeState: ErrorState state } o => $"{o.Class.Name} [{state.Code}]: {state.Message}",
        _ => error.ToString(),
    };

    private static string ErrorStack(Value error) => error.Object is ObjectInstance { NativeState: ErrorState state } ? state.StackTrace : string.Empty;

    private static void CaptureStack(Value error, Fiber fiber)
    {
        if (error.Object is not ObjectInstance { NativeState: ErrorState state } || state.StackTrace.Length > 0)
        {
            return;
        }

        var sb = new StringBuilder();
        for (var i = fiber.Frames.Count - 1; i >= 0; i--)
        {
            var frame = fiber.Frames[i];
            sb.Append("  at ").Append(frame.Unit.Name).Append(" (").Append(frame.Unit.SourcePath).Append(':')
                .Append(frame.Line.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        }

        state.StackTrace = sb.ToString();
    }

    private Value ToScriptError(Exception exception) => exception switch
    {
        ScriptException script => script.Error,
        ScriptErrorRequest request => Image.CreateError(request.ClassName, request.Message),
        _ => Image.CreateError("Error", $"Host error: {exception.Message}"),
    };

    #endregion
}
