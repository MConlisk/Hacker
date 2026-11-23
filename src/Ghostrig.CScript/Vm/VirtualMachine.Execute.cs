using System.Globalization;
using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;

namespace Ghostrig.CScript.Vm;

internal enum ExecStatus
{
    Completed,
    Faulted,
    Suspended,
    QuantumExpired,
    BarrierReturned,
}

public sealed partial class VirtualMachine
{
    /// <summary>
    /// The interpreter loop. Runs <paramref name="fiber"/> until it completes, faults, suspends on an incomplete task, uses
    /// up <paramref name="quantum"/> steps, or (for host callbacks) returns from <paramref name="barrier"/>.
    /// </summary>
    private ExecStatus Execute(Fiber fiber, long quantum, Frame? barrier, out Value barrierResult)
    {
        barrierResult = Value.Null;
        _current = fiber;
        fiber.State = FiberState.Running;

        if (fiber.HasResume)
        {
            fiber.HasResume = false;
            if (fiber.ResumeThrows)
            {
                if (!Throw(fiber, fiber.ResumeValue, barrier, out var thrownStatus))
                {
                    return thrownStatus;
                }
            }
            else
            {
                fiber.Top.Push(fiber.ResumeValue);
            }
        }

        var startSteps = Steps;
        while (true)
        {
            if (Steps - startSteps >= quantum)
            {
                return ExecStatus.QuantumExpired;
            }

            var frame = fiber.Top;
            var instruction = frame.Unit.Code[frame.Ip++];
            Steps++;
            if (++_uncharged >= 4096)
            {
                FlushSteps();
            }

            if (fiber.Budget is not null && --fiber.BudgetRemaining < 0)
            {
                fiber.BudgetRemaining = long.MaxValue;
                if (!Throw(fiber, Image.CreateError("StepBudgetExceeded", $"Step budget of {fiber.Budget:N0} exceeded in {frame.Unit.Name}."), barrier, out var budgetStatus))
                {
                    return budgetStatus;
                }

                continue;
            }

            try
            {
                var outcome = Step(fiber, frame, instruction, barrier, out barrierResult);
                if (outcome is { } status)
                {
                    return status;
                }
            }
            catch (Exception ex) when (ex is ScriptException or ScriptErrorRequest or InvalidOperationException or ArgumentException
                                           or IndexOutOfRangeException or InvalidCastException or NullReferenceException or FormatException
                                           or OverflowException or KeyNotFoundException)
            {
                if (!Throw(fiber, ToScriptError(ex), barrier, out var thrownStatus))
                {
                    return thrownStatus;
                }
            }
        }
    }

    /// <summary>Executes one instruction. Returns a status when the loop must stop, otherwise null.</summary>
    private ExecStatus? Step(Fiber fiber, Frame frame, Instruction instruction, Frame? barrier, out Value barrierResult)
    {
        barrierResult = Value.Null;
        var a = instruction.A;
        switch (instruction.Op)
        {
            case OpCode.Nop:
                break;
            case OpCode.PushNull:
                frame.Push(Value.Null);
                break;
            case OpCode.PushTrue:
                frame.Push(Value.True);
                break;
            case OpCode.PushFalse:
                frame.Push(Value.False);
                break;
            case OpCode.PushConst:
                frame.Push(frame.Unit.Constants[a]);
                break;
            case OpCode.Pop:
                frame.Sp--;
                break;
            case OpCode.Dup:
                frame.Push(frame.Peek());
                break;

            case OpCode.LoadLocal:
                frame.Push(frame.Locals[a]);
                break;
            case OpCode.StoreLocal:
                frame.Locals[a] = frame.Pop();
                break;
            case OpCode.MakeCell:
                frame.Locals[a] = Value.FromObject(new Cell(frame.Locals[a]));
                break;
            case OpCode.LoadCell:
                frame.Push(frame.Locals[a].As<Cell>().Value);
                break;
            case OpCode.StoreCell:
                StoreCell(fiber, frame.Locals[a].As<Cell>(), frame.Pop());
                break;
            case OpCode.LoadCellRef:
                frame.Push(frame.Locals[a]);
                break;
            case OpCode.LoadCaptured:
                frame.Push(frame.Captures[a].Value);
                break;
            case OpCode.StoreCaptured:
                StoreCell(fiber, frame.Captures[a], frame.Pop());
                break;
            case OpCode.LoadCapturedRef:
                frame.Push(Value.FromObject(frame.Captures[a]));
                break;
            case OpCode.LoadThis:
                frame.Push(frame.This);
                break;
            case OpCode.LoadGlobal:
                frame.Push(_globals[a]);
                break;
            case OpCode.StoreGlobal:
            {
                var value = frame.Pop();
                if (fiber.Journal is { } journal)
                {
                    var old = _globals[a];
                    journal.Record(() => _globals[a] = old);
                }

                _globals[a] = value;
                break;
            }

            case OpCode.LoadField:
                frame.Push(Instance(frame.Pop()).Fields[a]);
                break;
            case OpCode.StoreField:
            {
                var value = frame.Pop();
                var fields = Instance(frame.Pop()).Fields;
                if (fiber.Journal is { } journal)
                {
                    var old = fields[a];
                    journal.Record(() => fields[a] = old);
                }

                fields[a] = value;
                break;
            }

            case OpCode.NewArray:
            {
                var length = frame.Pop().AsInt;
                if (length < 0 || length > 16_000_000)
                {
                    throw new ScriptErrorRequest("ArgumentError", $"Invalid array length {length}.");
                }

                ChargeNative(1 + (length / 8));
                var items = new Value[length];
                var fill = frame.Unit.Constants[a];
                for (var i = 0; i < items.Length; i++)
                {
                    items[i] = fill.Object is ObjectInstance { Class.IsStruct: true } s ? Value.FromObject(s.CloneStruct()) : fill;
                }

                frame.Push(Value.FromObject(new ArrayObject(items)));
                break;
            }

            case OpCode.NewArrayInit:
            {
                var items = new Value[a];
                for (var i = a - 1; i >= 0; i--)
                {
                    items[i] = frame.Pop();
                }

                ChargeNative(a / 8);
                frame.Push(Value.FromObject(new ArrayObject(items)));
                break;
            }

            case OpCode.LoadElement:
            {
                var index = frame.Pop().AsInt;
                var array = NotNull<ArrayObject>(frame.Pop(), "array");
                frame.Push(array.Items[CheckIndex(index, array.Length)]);
                break;
            }

            case OpCode.StoreElement:
            {
                var value = frame.Pop();
                var index = frame.Pop().AsInt;
                var array = NotNull<ArrayObject>(frame.Pop(), "array");
                var i = CheckIndex(index, array.Length);
                if (fiber.Journal is { } journal)
                {
                    var old = array.Items[i];
                    journal.Record(() => array.Items[i] = old);
                }

                array.Items[i] = value;
                break;
            }

            case OpCode.ArrayLength:
                frame.Push(Value.FromInt(NotNull<ArrayObject>(frame.Pop(), "array").Length));
                break;
            case OpCode.NewTuple:
            {
                var items = new Value[a];
                for (var i = a - 1; i >= 0; i--)
                {
                    items[i] = frame.Pop();
                }

                frame.Push(Value.FromObject(new TupleObject(items)));
                break;
            }

            case OpCode.LoadTupleItem:
                frame.Push(NotNull<TupleObject>(frame.Pop(), "tuple").Items[a]);
                break;
            case OpCode.StringLength:
                frame.Push(Value.FromInt(NotNull<string>(frame.Pop(), "string").Length));
                break;
            case OpCode.StringCharAt:
            {
                var index = frame.Pop().AsInt;
                var text = NotNull<string>(frame.Pop(), "string");
                frame.Push(Value.FromChar(text[CheckIndex(index, text.Length)]));
                break;
            }

            case OpCode.BytesLength:
                frame.Push(Value.FromInt(NotNull<BytesObject>(frame.Pop(), "bytes").Data.Length));
                break;
            case OpCode.BytesAt:
            {
                var index = frame.Pop().AsInt;
                var data = NotNull<BytesObject>(frame.Pop(), "bytes").Data;
                frame.Push(Value.FromByte(data[CheckIndex(index, data.Length)]));
                break;
            }

            case OpCode.NewObject:
            {
                var runtimeClass = Image.Classes[a];
                ChargeNative(1 + (runtimeClass.FieldCount / 4));
                frame.Push(Value.FromObject(runtimeClass.CreateInstance()));
                break;
            }

            case OpCode.CopyStruct:
            case OpCode.CopyIfStruct:
                if (frame.Peek().Object is ObjectInstance { Class.IsStruct: true } structValue)
                {
                    frame.Sp--;
                    frame.Push(Value.FromObject(structValue.CloneStruct()));
                }

                break;

            case OpCode.ToInt:
            {
                var v = frame.Pop();
                frame.Push(v.Tag == ValueTag.Float ? Value.FromInt(TruncateToInt(v.AsFloat)) : Value.FromInt(v.Bits));
                break;
            }

            case OpCode.ToFloat:
            {
                var v = frame.Pop();
                frame.Push(v.Tag == ValueTag.Float ? v : Value.FromFloat(v.Bits));
                break;
            }

            case OpCode.ToByte:
            {
                var v = frame.Pop();
                frame.Push(Value.FromByte(v.Tag == ValueTag.Float ? TruncateToInt(v.AsFloat) : v.Bits));
                break;
            }

            case OpCode.ToChar:
            {
                var v = frame.Pop();
                var code = v.Tag == ValueTag.Float ? TruncateToInt(v.AsFloat) : v.Bits;
                frame.Push(Value.FromChar(((code % 0x110000) + 0x110000) % 0x110000));
                break;
            }

            case OpCode.ToEnum:
                frame.Push(Value.FromEnum(frame.Pop().Bits, Image.Classes[a]));
                break;
            case OpCode.UnwrapNullable:
                if (frame.Peek().IsNull)
                {
                    throw new ScriptErrorRequest("NullError", "A null value was used where a value is required.");
                }

                break;
            case OpCode.CastCheck:
            {
                var test = Image.TypeTests[a];
                if (!test.Matches(frame.Peek(), Image))
                {
                    var actual = frame.Peek().IsNull ? "null" : Image.ClassOf(frame.Peek())?.Name ?? "value";
                    throw new ScriptErrorRequest(frame.Peek().IsNull ? "NullError" : "CastError", $"Cannot cast {actual} to {test.Display}.");
                }

                break;
            }

            case OpCode.IsType:
                frame.Push(Value.FromBool(Image.TypeTests[a].Matches(frame.Pop(), Image)));
                break;
            case OpCode.IsNull:
                frame.Push(Value.FromBool(frame.Pop().IsNull));
                break;

            // ---- integer arithmetic (wrapping) ----
            case OpCode.IAdd: IntOp(frame, static (x, y) => unchecked(x + y)); break;
            case OpCode.ISub: IntOp(frame, static (x, y) => unchecked(x - y)); break;
            case OpCode.IMul: IntOp(frame, static (x, y) => unchecked(x * y)); break;
            case OpCode.IDiv:
            {
                var y = frame.Pop().Bits;
                var x = frame.Pop().Bits;
                if (y == 0)
                {
                    throw new ScriptErrorRequest("ArithmeticError", "Division by zero.");
                }

                frame.Push(Value.FromInt(x == long.MinValue && y == -1 ? x : x / y));
                break;
            }

            case OpCode.IRem:
            {
                var y = frame.Pop().Bits;
                var x = frame.Pop().Bits;
                if (y == 0)
                {
                    throw new ScriptErrorRequest("ArithmeticError", "Division by zero.");
                }

                frame.Push(Value.FromInt(y == -1 ? 0 : x % y));
                break;
            }

            case OpCode.IShl: IntOp(frame, static (x, y) => x << (int)(y & 63)); break;
            case OpCode.IShr: IntOp(frame, static (x, y) => x >> (int)(y & 63)); break;
            case OpCode.IUshr: IntOp(frame, static (x, y) => (long)((ulong)x >> (int)(y & 63))); break;
            case OpCode.IAnd: ScalarOp(frame, static (x, y) => x & y); break;
            case OpCode.IOr: ScalarOp(frame, static (x, y) => x | y); break;
            case OpCode.IXor: ScalarOp(frame, static (x, y) => x ^ y); break;
            case OpCode.INeg: frame.Push(Value.FromInt(unchecked(-frame.Pop().Bits))); break;
            case OpCode.INot: frame.Push(Value.FromInt(~frame.Pop().Bits)); break;
            case OpCode.ILt: CompareOp(frame, static (x, y) => x < y); break;
            case OpCode.ILe: CompareOp(frame, static (x, y) => x <= y); break;
            case OpCode.IGt: CompareOp(frame, static (x, y) => x > y); break;
            case OpCode.IGe: CompareOp(frame, static (x, y) => x >= y); break;

            // ---- float arithmetic ----
            case OpCode.FAdd: FloatOp(frame, static (x, y) => x + y); break;
            case OpCode.FSub: FloatOp(frame, static (x, y) => x - y); break;
            case OpCode.FMul: FloatOp(frame, static (x, y) => x * y); break;
            case OpCode.FDiv: FloatOp(frame, static (x, y) => x / y); break;
            case OpCode.FRem: FloatOp(frame, static (x, y) => x % y); break;
            case OpCode.FNeg: frame.Push(Value.FromFloat(-frame.Pop().AsFloat)); break;
            case OpCode.FLt: FloatCompare(frame, static (x, y) => x < y); break;
            case OpCode.FLe: FloatCompare(frame, static (x, y) => x <= y); break;
            case OpCode.FGt: FloatCompare(frame, static (x, y) => x > y); break;
            case OpCode.FGe: FloatCompare(frame, static (x, y) => x >= y); break;

            // ---- bool ----
            case OpCode.BNot: frame.Push(Value.FromBool(!frame.Pop().AsBool)); break;
            case OpCode.BAnd: { var y = frame.Pop().AsBool; var x = frame.Pop().AsBool; frame.Push(Value.FromBool(x & y)); break; }
            case OpCode.BOr: { var y = frame.Pop().AsBool; var x = frame.Pop().AsBool; frame.Push(Value.FromBool(x | y)); break; }
            case OpCode.BXor: { var y = frame.Pop().AsBool; var x = frame.Pop().AsBool; frame.Push(Value.FromBool(x ^ y)); break; }

            // ---- simulated time ----
            case OpCode.DAdd: { var y = frame.Pop().Bits; var x = frame.Pop().Bits; frame.Push(Value.FromDuration(unchecked(x + y))); break; }
            case OpCode.DSub: { var y = frame.Pop().Bits; var x = frame.Pop().Bits; frame.Push(Value.FromDuration(unchecked(x - y))); break; }
            case OpCode.DMulInt: { var y = frame.Pop().Bits; var x = frame.Pop().Bits; frame.Push(Value.FromDuration(unchecked(x * y))); break; }
            case OpCode.DMulFloat: { var y = frame.Pop().AsFloat; var x = frame.Pop().Bits; frame.Push(Value.FromDuration(TruncateToInt(x * y))); break; }
            case OpCode.DDivInt:
            {
                var y = frame.Pop().Bits;
                var x = frame.Pop().Bits;
                if (y == 0)
                {
                    throw new ScriptErrorRequest("ArithmeticError", "Division by zero.");
                }

                frame.Push(Value.FromDuration(x / y));
                break;
            }

            case OpCode.DDivFloat: { var y = frame.Pop().AsFloat; var x = frame.Pop().Bits; frame.Push(Value.FromDuration(TruncateToInt(x / y))); break; }
            case OpCode.DNeg: frame.Push(Value.FromDuration(unchecked(-frame.Pop().Bits))); break;
            case OpCode.TAddD: { var y = frame.Pop().Bits; var x = frame.Pop().Bits; frame.Push(Value.FromInstant(unchecked(x + y))); break; }
            case OpCode.TSubD: { var y = frame.Pop().Bits; var x = frame.Pop().Bits; frame.Push(Value.FromInstant(unchecked(x - y))); break; }
            case OpCode.TDiff: { var y = frame.Pop().Bits; var x = frame.Pop().Bits; frame.Push(Value.FromDuration(unchecked(x - y))); break; }

            // ---- strings and bytes ----
            case OpCode.StrConcat:
            {
                var y = frame.Pop();
                var x = frame.Pop();
                var text = (x.Object as string ?? string.Empty) + (y.Object as string ?? string.Empty);
                ChargeNative(text.Length / 64);
                frame.Push(Value.FromString(text));
                break;
            }

            case OpCode.BytesConcat:
            {
                var y = NotNull<BytesObject>(frame.Pop(), "bytes").Data;
                var x = NotNull<BytesObject>(frame.Pop(), "bytes").Data;
                ChargeNative((x.Length + y.Length) / 64);
                frame.Push(Value.FromObject(new BytesObject([.. x, .. y])));
                break;
            }

            case OpCode.FormatValue:
            {
                var format = a >= 0 ? frame.Unit.Constants[a].AsString : null;
                frame.Push(Value.FromString(Format(frame.Pop(), format)));
                break;
            }

            case OpCode.Equal:
            {
                var y = frame.Pop();
                var x = frame.Pop();
                frame.Push(Value.FromBool(ValuesEqual(x, y)));
                break;
            }

            case OpCode.NotEqual:
            {
                var y = frame.Pop();
                var x = frame.Pop();
                frame.Push(Value.FromBool(!ValuesEqual(x, y)));
                break;
            }

            // ---- control flow ----
            case OpCode.Jump:
                frame.Ip = a;
                break;
            case OpCode.JumpIfFalse:
                if (!frame.Pop().AsBool)
                {
                    frame.Ip = a;
                }

                break;
            case OpCode.JumpIfTrue:
                if (frame.Pop().AsBool)
                {
                    frame.Ip = a;
                }

                break;
            case OpCode.JumpIfNull:
                if (frame.Pop().IsNull)
                {
                    frame.Ip = a;
                }

                break;
            case OpCode.JumpIfNotNull:
                if (!frame.Pop().IsNull)
                {
                    frame.Ip = a;
                }

                break;

            // ---- calls ----
            case OpCode.Call:
            {
                var callable = Image.Callables[a];
                var args = PopArguments(frame, instruction.B);
                var receiver = callable.HasReceiver && args.Length > 0 ? args[0] : Value.Null;
                var parameters = callable.HasReceiver && args.Length > 0 ? args.AsSpan(1) : args;
                if (!Enter(fiber, callable, receiver, parameters, [], out var result) && callable.ReturnsValue)
                {
                    frame.Push(result);
                }

                break;
            }

            case OpCode.CallVirtual:
            {
                var args = PopArguments(frame, instruction.B);
                var receiver = args[0];
                var callable = Resolve(a, receiver);
                if (!Enter(fiber, callable, receiver, args.AsSpan(1), [], out var result) && callable.ReturnsValue)
                {
                    frame.Push(result);
                }

                break;
            }

            case OpCode.CallFunction:
            {
                var args = PopArguments(frame, instruction.B);
                var function = NotNull<FunctionObject>(frame.Pop(), "function");
                if (function.IsMulticast)
                {
                    var last = InvokeSynchronously(function, args);
                    if (function.Target.ReturnsValue)
                    {
                        frame.Push(last);
                    }

                    break;
                }

                var receiver = function.HasReceiver ? function.Receiver : Value.Null;
                if (!Enter(fiber, function.Target, receiver, args, function.Captures, out var result) && function.Target.ReturnsValue)
                {
                    frame.Push(result);
                }

                break;
            }

            case OpCode.Return:
                return Return(fiber, frame, a == 1 ? frame.Pop() : Value.Null, a == 1, barrier, out barrierResult);

            // ---- closures ----
            case OpCode.MakeClosure:
            {
                var receiver = frame.Pop();
                var captures = new Cell[instruction.B];
                for (var i = instruction.B - 1; i >= 0; i--)
                {
                    captures[i] = frame.Pop().As<Cell>();
                }

                var callable = Image.Callables[a];
                frame.Push(Value.FromObject(new FunctionObject(callable, captures, receiver, callable.HasReceiver)));
                break;
            }

            case OpCode.MakeMethodRef:
            {
                var receiver = frame.Pop();
                var callable = instruction.B == 1 ? Resolve(a, receiver) : Image.Callables[a];
                frame.Push(Value.FromObject(new FunctionObject(callable, [], receiver, instruction.B == 1)));
                break;
            }

            case OpCode.FunctionCombine:
            {
                var y = frame.Pop().Object as FunctionObject;
                var x = frame.Pop().Object as FunctionObject;
                frame.Push(Value.FromObject(FunctionObject.Combine(x, y)));
                break;
            }

            case OpCode.FunctionRemove:
            {
                var y = frame.Pop().Object as FunctionObject;
                var x = frame.Pop().Object as FunctionObject;
                frame.Push(Value.FromObject(FunctionObject.Remove(x, y)));
                break;
            }

            // ---- errors ----
            case OpCode.Throw:
            {
                var error = frame.Pop();
                if (error.IsNull)
                {
                    error = Image.CreateError("NullError", "Cannot throw null.");
                }

                return Throw(fiber, error, barrier, out var status) ? null : status;
            }

            case OpCode.Rethrow:
                return Throw(fiber, frame.Locals[a], barrier, out var rethrowStatus) ? null : rethrowStatus;

            case OpCode.ThrowRuntime:
                return Throw(fiber, Image.CreateError(frame.Unit.Constants[a].AsString, frame.Unit.Constants[instruction.B].AsString), barrier,
                    out var runtimeStatus) ? null : runtimeStatus;

            // ---- async and iterators ----
            case OpCode.Await:
            {
                var task = NotNull<TaskObject>(frame.Pop(), "task");
                if (task.IsCompleted)
                {
                    if (task.Status == Runtime.TaskStatus.Faulted)
                    {
                        task.FaultObserved = true;
                        return Throw(fiber, task.Fault, barrier, out var faultStatus) ? null : faultStatus;
                    }

                    frame.Push(task.Result);
                    break;
                }

                if (barrier is not null)
                {
                    throw new ScriptErrorRequest("Error", "A synchronous callback cannot await an incomplete task.");
                }

                fiber.State = FiberState.Waiting;
                task.Continuations.Add(t =>
                {
                    fiber.HasResume = true;
                    fiber.ResumeThrows = t.Status == Runtime.TaskStatus.Faulted;
                    if (fiber.ResumeThrows)
                    {
                        t.FaultObserved = true;
                    }

                    fiber.ResumeValue = fiber.ResumeThrows ? t.Fault : t.Result;
                    fiber.State = FiberState.Ready;
                    _ready.Enqueue(fiber);
                });
                return ExecStatus.Suspended;
            }

            case OpCode.Yield:
            {
                var value = frame.Pop();
                var generator = frame.Generator ?? throw new InvalidOperationException("yield outside an iterator.");
                generator.Current = value;
                fiber.Frames.RemoveAt(fiber.Frames.Count - 1);
                fiber.Top.Push(Value.True);
                break;
            }

            default:
                throw new InvalidOperationException($"Unknown opcode {instruction.Op}.");
        }

        return null;
    }

    private ExecStatus? Return(Fiber fiber, Frame frame, Value value, bool hasValue, Frame? barrier, out Value barrierResult)
    {
        barrierResult = Value.Null;
        fiber.Frames.RemoveAt(fiber.Frames.Count - 1);

        if (frame.Generator is { } generator)
        {
            generator.Finished = true;
            generator.Frame = null;
            fiber.Top.Push(Value.False);
            return null;
        }

        if (ReferenceEquals(frame, barrier))
        {
            barrierResult = value;
            return ExecStatus.BarrierReturned;
        }

        if (fiber.Frames.Count == 0)
        {
            fiber.Result = value;
            fiber.State = FiberState.Completed;
            fiber.Task?.TryComplete(value);
            return ExecStatus.Completed;
        }

        if (hasValue)
        {
            fiber.Top.Push(value);
        }

        return null;
    }

    /// <summary>
    /// Unwinds to the nearest matching handler. Returns true when execution can continue in a handler; otherwise sets
    /// <paramref name="status"/> (fault of the fiber) — or, inside a host callback, throws to the native caller.
    /// </summary>
    private bool Throw(Fiber fiber, Value error, Frame? barrier, out ExecStatus status)
    {
        status = ExecStatus.Faulted;
        CaptureStack(error, fiber);
        var uncatchable = IsUncatchable(error);
        while (fiber.Frames.Count > 0)
        {
            var frame = fiber.Top;
            if (!uncatchable)
            {
                var ip = frame.Ip - 1;
                foreach (var region in frame.Unit.Handlers)
                {
                    if (ip >= region.Start && ip < region.End && (region.Test is null || region.Test.Matches(error, Image)))
                    {
                        frame.Sp = 0;
                        frame.Locals[region.ErrorLocal] = error;
                        frame.Ip = region.Handler;
                        return true;
                    }
                }
            }

            fiber.Frames.RemoveAt(fiber.Frames.Count - 1);
            if (frame.Generator is { } generator)
            {
                generator.Finished = true;
                generator.Frame = null;
            }

            if (ReferenceEquals(frame, barrier))
            {
                throw new ScriptException(error, DescribeError(error));
            }
        }

        fiber.Error = error;
        fiber.State = FiberState.Faulted;
        return false;
    }

    private Callable Resolve(int slot, Value receiver)
    {
        if (receiver.IsNull)
        {
            throw new ScriptErrorRequest("NullError", "Method called on a null value.");
        }

        var runtimeClass = Image.ClassOf(receiver) ?? Image.ObjectClass;
        if (runtimeClass.VTable.TryGetValue(slot, out var callable) || Image.ObjectClass.VTable.TryGetValue(slot, out callable))
        {
            return callable;
        }

        throw new ScriptErrorRequest("Error", $"{runtimeClass.Name} does not implement the called member.");
    }

    private static Value[] PopArguments(Frame frame, int count)
    {
        var args = new Value[count];
        for (var i = count - 1; i >= 0; i--)
        {
            args[i] = frame.Pop();
        }

        return args;
    }

    private static void StoreCell(Fiber fiber, Cell cell, Value value)
    {
        if (fiber.Journal is { } journal)
        {
            var old = cell.Value;
            journal.Record(() => cell.Value = old);
        }

        cell.Value = value;
    }

    private static ObjectInstance Instance(Value value) =>
        value.Object as ObjectInstance ?? throw new ScriptErrorRequest("NullError", value.IsNull ? "Field access on a null object." : "Not an object.");

    private static T NotNull<T>(Value value, string what) where T : class =>
        value.Object as T ?? throw new ScriptErrorRequest("NullError", value.IsNull ? $"The {what} is null." : $"Expected a {what}.");

    private static int CheckIndex(long index, int length) =>
        index >= 0 && index < length ? (int)index : throw new ScriptErrorRequest("IndexError", $"Index {index} is out of range for length {length}.");

    private static long TruncateToInt(double d) =>
        double.IsNaN(d) ? 0 : d >= 9.2233720368547758E18 ? long.MaxValue : d <= -9.2233720368547758E18 ? long.MinValue : (long)Math.Truncate(d);

    private static void IntOp(Frame frame, Func<long, long, long> op)
    {
        var y = frame.Pop().Bits;
        var x = frame.Pop().Bits;
        frame.Push(Value.FromInt(op(x, y)));
    }

    /// <summary>Bitwise ops keep the left operand's tag (enum flags stay enums, bools stay bools).</summary>
    private static void ScalarOp(Frame frame, Func<long, long, long> op)
    {
        var y = frame.Pop();
        var x = frame.Pop();
        var bits = op(x.Bits, y.Bits);
        frame.Push(x.Tag == ValueTag.Enum ? Value.FromEnum(bits, (RuntimeClass)x.Object!) : Value.FromInt(bits));
    }

    private static void CompareOp(Frame frame, Func<long, long, bool> op)
    {
        var y = frame.Pop().Bits;
        var x = frame.Pop().Bits;
        frame.Push(Value.FromBool(op(x, y)));
    }

    private static void FloatOp(Frame frame, Func<double, double, double> op)
    {
        var y = frame.Pop().AsFloat;
        var x = frame.Pop().AsFloat;
        frame.Push(Value.FromFloat(op(x, y)));
    }

    private static void FloatCompare(Frame frame, Func<double, double, bool> op)
    {
        var y = frame.Pop().AsFloat;
        var x = frame.Pop().AsFloat;
        frame.Push(Value.FromBool(op(x, y)));
    }

    /// <summary>Structural equality (Doc 7 §6): scalars by value, strings/bytes/tuples/structs by content, classes by reference.</summary>
    public static bool ValuesEqual(Value x, Value y)
    {
        if (x.IsNull || y.IsNull)
        {
            return x.IsNull && y.IsNull;
        }

        if (x.Tag == ValueTag.Float || y.Tag == ValueTag.Float)
        {
            var dx = x.Tag == ValueTag.Float ? x.AsFloat : x.Bits;
            var dy = y.Tag == ValueTag.Float ? y.AsFloat : y.Bits;
            return dx == dy;
        }

        if (x.Tag != ValueTag.Ref || y.Tag != ValueTag.Ref)
        {
            return x.Bits == y.Bits && (x.Tag != ValueTag.Enum || ReferenceEquals(x.Object, y.Object));
        }

        return (x.Object, y.Object) switch
        {
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            (BytesObject a, BytesObject b) => a.Equals(b),
            (TupleObject a, TupleObject b) => a.Items.Length == b.Items.Length && a.Items.Zip(b.Items).All(p => ValuesEqual(p.First, p.Second)),
            (ObjectInstance { Class.IsStruct: true } a, ObjectInstance { Class.IsStruct: true } b) =>
                ReferenceEquals(a.Class, b.Class) && a.Fields.Zip(b.Fields).All(p => ValuesEqual(p.First, p.Second)),
            _ => ReferenceEquals(x.Object, y.Object),
        };
    }

    /// <summary>Invariant built-in formatting used by string concatenation and interpolation.</summary>
    public static string Format(Value value, string? format)
    {
        try
        {
            return (value.Tag, format) switch
            {
                (ValueTag.Null, _) => string.Empty,
                (ValueTag.Int, not null) => value.Bits.ToString(format, CultureInfo.InvariantCulture),
                (ValueTag.Byte, not null) => ((byte)value.Bits).ToString(format, CultureInfo.InvariantCulture),
                (ValueTag.Float, not null) => value.AsFloat.ToString(format, CultureInfo.InvariantCulture),
                _ => value.Object is string s ? s : ValueText.Describe(value),
            };
        }
        catch (FormatException)
        {
            throw new ScriptErrorRequest("FormatError", $"Invalid format string '{format}'.");
        }
    }
}
