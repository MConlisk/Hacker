using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;

namespace Ghostrig.CScript.Vm;

/// <summary>
/// Services available to host-implemented (native) functions: metering, simulated time, tasks, errors, and synchronous
/// callbacks into CScript (e.g. a comparer passed to a sort).
/// </summary>
public sealed class NativeContext
{
    private readonly VirtualMachine _vm;
    private Natives.ValueComparer? _comparer;

    internal NativeContext(VirtualMachine vm) => _vm = vm;

    /// <summary>Equality, hashing and ordering shared by every standard-library collection.</summary>
    public Natives.ValueComparer Comparer => _comparer ??= new Natives.ValueComparer(this);

    /// <summary>A class declared in any module (Core, Std.*, system libraries).</summary>
    public RuntimeClass Class(string module, string name) =>
        Image.Linker.GetClass(Image.Linker.Compilation.GetModule(module)?.Lookup(name).OfType<Semantics.NamedTypeSymbol>().FirstOrDefault()
                              ?? throw new InvalidOperationException($"Type '{module}.{name}' not found."));

    /// <summary>Creates an instance of a native class backed by host state.</summary>
    public Value NewInstance(RuntimeClass runtimeClass, object state)
    {
        var instance = runtimeClass.CreateInstance();
        instance.NativeState = state;
        return Value.FromObject(instance);
    }

    /// <summary>Host state behind a native instance (null receivers throw NullError).</summary>
    public T State<T>(Value value) where T : class => value.Object switch
    {
        ObjectInstance { NativeState: T state } => state,
        NativeObject { State: T state } => state,
        null => throw Throw("NullError", "Member accessed on a null value."),
        _ => throw Throw("CastError", $"Expected a native {typeof(T).Name.Replace("State", string.Empty, StringComparison.Ordinal)}."),
    };

    /// <summary>Call before mutating native state: journals it for rollback and invalidates live iterators.</summary>
    public void Mutating(Natives.NativeState state)
    {
        _vm.JournalNativeState(state);
        state.Touch();
    }

    /// <summary>Call before a native rewrites a script array in place, so a faulted event restores it.</summary>
    public void MutatingArray(Value[] items) => _vm.JournalArray(items);

    /// <summary>Iterator over a native collection that fails fast on concurrent modification.</summary>
    public Value NewIterator(Natives.NativeState owner, IEnumerable<Value> items, long stepsPerItem = 1) =>
        Value.FromObject(new NativeObject(Image.NativeIteratorClass, new Natives.VersionedIterator(owner, items.GetEnumerator(), stepsPerItem)));

    internal TaskObject MoveNextAsync(AsyncGeneratorObject generator) => _vm.ResumeAsyncGenerator(generator);

    internal Callable ResolveVirtual(int slot, Value receiver) => _vm.ResolveVirtual(slot, receiver);

    /// <summary>Invokes a function value with as many arguments as it declares (lambdas may ignore trailing ones).</summary>
    public Value Call(Value function, params Value[] arguments) =>
        function.Object is FunctionObject f ? Invoke(f, arguments) : throw Throw("NullError", "Function value is null.");

    public ProgramImage Image => _vm.Image;

    /// <summary>Current simulated time in nanoseconds.</summary>
    public long Now => _vm.Host.Now;

    /// <summary>Steps executed by this VM so far (instructions plus native charges).</summary>
    public long StepsExecuted => _vm.Steps;

    /// <summary>Appends a line to the VM's trace log (Debug.Trace).</summary>
    public void Trace(string line) => _vm.TraceLog.Add(line);

    /// <summary>Signals a script breakpoint to the host debugger.</summary>
    public void Breakpoint() => _vm.OnBreakpoint();

    /// <summary>Charges additional steps for work proportional to input size (Doc 7 §8.4 published costs).</summary>
    public void Charge(long steps) => _vm.ChargeNative(steps);

    /// <summary>Creates a pending task of the given class (Task or Task&lt;T&gt;).</summary>
    public TaskObject CreateTask(bool generic = true) => new(generic ? Image.TaskOfTClass : Image.TaskClass);

    public TaskObject CompletedTask(Value result, bool generic = true)
    {
        var task = CreateTask(generic);
        task.TryComplete(result);
        return task;
    }

    /// <summary>Runs <paramref name="action"/> when the simulated clock reaches <paramref name="atNanoseconds"/>.</summary>
    public void ScheduleAt(long atNanoseconds, Action action) => _vm.ScheduleTimer(atNanoseconds, action);

    public void CompleteTask(TaskObject task, Value result) => task.TryComplete(result);

    public void FaultTask(TaskObject task, Value error) => task.TryFault(error);

    /// <summary>Creates a Core error instance (e.g. <c>Error("IndexError", "…")</c>).</summary>
    public Value Error(string className, string message) => Image.CreateError(className, message);

    /// <summary>Throws a Core error into the calling CScript code.</summary>
    public Exception Throw(string className, string message) => new ScriptErrorRequest(className, message);

    /// <summary>Invokes a CScript function value synchronously and returns its result. The callee may not await.</summary>
    public Value Invoke(FunctionObject function, params Value[] arguments) => _vm.InvokeSynchronously(function, arguments);

    /// <summary>Invokes a CScript method on a receiver synchronously (virtual dispatch by method slot).</summary>
    public Value InvokeVirtual(int slot, Value receiver, params Value[] arguments) => _vm.InvokeVirtualSynchronously(slot, receiver, arguments);

    public Value NewIterator(IEnumerable<Value> items) =>
        Value.FromObject(new NativeObject(Image.NativeIteratorClass, new Natives.EnumeratorIterator(items.GetEnumerator())));

    public Value NewArray(Value[] items) => Value.FromObject(new ArrayObject(items));

    public Value String(string text) => Value.FromString(text);

    /// <summary>Deterministic identity hash: assigned in first-request order, never derived from host addresses.</summary>
    public long IdentityHash(object obj) => _vm.IdentityHash(obj);

    public long Hash(Value value) => Natives.StableHash.Of(value, _vm.IdentityHash);
}
