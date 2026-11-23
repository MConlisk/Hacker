using System.Collections.Immutable;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.CodeGen;

/// <summary>
/// Lowers one bound function to stack bytecode. Locals (including parameters) live in frame slots; captured variables
/// live in <see cref="Cell"/>s; <c>finally</c> blocks are inlined on every normal exit path and also run from a
/// catch-all handler that rethrows.
/// </summary>
internal sealed class FunctionCompiler
{
    private readonly Linker _linker;
    private readonly CodeUnit _unit;
    private readonly MethodSymbol _method;
    private readonly BoundFunction? _function;
    private readonly List<Instruction> _code = [];
    private readonly List<int> _lines = [];
    private readonly List<Value> _constants = [];
    private readonly Dictionary<string, int> _stringConstants = new(StringComparer.Ordinal);
    private readonly List<int> _labels = [];
    private readonly List<(int Instruction, int Label)> _fixups = [];
    private readonly List<(int StartLabel, int EndLabel, int HandlerLabel, int ErrorLocal, TypeTest? Test)> _regions = [];
    private readonly Dictionary<Symbol, int> _slots = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Symbol> _cells = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Symbol, int> _captureIndex = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BoundConditionalReceiver, int> _placeholders = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<(int Break, int Continue, int FinallyDepth)> _loops = new();
    private readonly List<BoundBlock> _finallies = [];
    private readonly Stack<int> _catchErrorLocals = new();
    private int _localCount;
    private int _line;

    public FunctionCompiler(Linker linker, CodeUnit unit, MethodSymbol method, BoundFunction? function)
    {
        _linker = linker;
        _unit = unit;
        _method = method;
        _function = function;
    }

    #region Entry points

    public void Compile()
    {
        var function = _function!;
        foreach (var parameter in _method.Parameters)
        {
            _slots[parameter] = _localCount++;
        }

        foreach (var local in function.CapturedLocals)
        {
            _cells.Add(local);
        }

        foreach (var parameter in function.CapturedParameters)
        {
            _cells.Add(parameter);
        }

        if (_method.MethodKind == MethodKind.Lambda && function.Body is { } && _linker.LambdaBody(_method) is not null)
        {
            // Lambdas read outer variables through their closure's captured cells.
            var lambdaNode = new BoundLambda(default, _method, function.Body, new FunctionTypeSymbol([], PrimitiveType.Void));
            var captures = _linker.CapturesOf(lambdaNode);
            for (var i = 0; i < captures.Length; i++)
            {
                _captureIndex[captures[i]] = i;
            }
        }

        if (_method.MethodKind == MethodKind.Constructor && _method.Container is NamedTypeSymbol type)
        {
            EmitFieldInitializers(type);
        }

        EmitStatement(function.Body!);
        Emit(OpCode.Return, 0);
        Finish(function.Flavor, [.. _method.Parameters.Where(_cells.Contains).Select(p => _slots[p])]);
    }

    public static void CompileAutoAccessor(Linker linker, CodeUnit unit, MethodSymbol accessor, FieldSymbol backing)
    {
        var compiler = new FunctionCompiler(linker, unit, accessor, null);
        compiler._localCount = accessor.Parameters.Length;
        var isGetter = accessor.MethodKind == MethodKind.PropertyGetter;
        if (backing.IsStatic)
        {
            if (isGetter)
            {
                compiler.Emit(OpCode.LoadGlobal, linker.GlobalSlot(backing));
                compiler.Emit(OpCode.Return, 1);
            }
            else
            {
                compiler.Emit(OpCode.LoadLocal, 0);
                compiler.Emit(OpCode.StoreGlobal, linker.GlobalSlot(backing));
                compiler.Emit(OpCode.Return, 0);
            }
        }
        else if (isGetter)
        {
            compiler.Emit(OpCode.LoadThis);
            compiler.Emit(OpCode.LoadField, backing.OriginalDefinition.Slot);
            compiler.Emit(OpCode.Return, 1);
        }
        else
        {
            compiler.Emit(OpCode.LoadThis);
            compiler.Emit(OpCode.LoadLocal, 0);
            compiler.EmitStructCopy(backing.Type);
            compiler.Emit(OpCode.StoreField, backing.OriginalDefinition.Slot);
            compiler.Emit(OpCode.Return, 0);
        }

        compiler.Finish(FunctionFlavor.Normal, []);
    }

    public static void CompileModuleInitializer(Linker linker, CodeUnit unit, MethodSymbol symbol,
        ImmutableArray<(FieldSymbol Field, BoundExpression Value)> initializers)
    {
        var compiler = new FunctionCompiler(linker, unit, symbol, null);
        foreach (var (field, value) in initializers)
        {
            compiler.SetLine(value.Span, field);
            compiler.EmitValue(value);
            compiler.Emit(OpCode.StoreGlobal, linker.GlobalSlot(field));
        }

        compiler.Emit(OpCode.Return, 0);
        compiler.Finish(FunctionFlavor.Normal, []);
    }

    private void Finish(FunctionFlavor flavor, ImmutableArray<int> cellParameters)
    {
        foreach (var (instruction, label) in _fixups)
        {
            var old = _code[instruction];
            _code[instruction] = old with { A = _labels[label] };
        }

        _unit.Code = [.. _code];
        _unit.Lines = [.. _lines];
        _unit.Constants = [.. _constants];
        _unit.LocalCount = _localCount;
        _unit.Flavor = flavor;
        _unit.CellParameters = cellParameters;
        _unit.Handlers = [.. _regions.Select(r => new ExceptionRegion(_labels[r.StartLabel], _labels[r.EndLabel], _labels[r.HandlerLabel], r.ErrorLocal, r.Test))];
    }

    private void EmitFieldInitializers(NamedTypeSymbol type)
    {
        foreach (var (member, value) in _linker.FieldInitializers(type))
        {
            var field = (FieldSymbol)member;
            Emit(OpCode.LoadThis);
            EmitValue(value);
            Emit(OpCode.StoreField, field.OriginalDefinition.Slot);
        }
    }

    #endregion

    #region Emission primitives

    private int Emit(OpCode op, int a = 0, int b = 0)
    {
        _code.Add(new Instruction(op, a, b));
        _lines.Add(_line);
        return _code.Count - 1;
    }

    private int NewLabel()
    {
        _labels.Add(-1);
        return _labels.Count - 1;
    }

    private void Mark(int label) => _labels[label] = _code.Count;

    private void EmitJump(OpCode op, int label) => _fixups.Add((Emit(op), label));

    private int Temp() => _localCount++;

    private int Constant(Value value)
    {
        if (value.Object is string s)
        {
            if (_stringConstants.TryGetValue(s, out var existing))
            {
                return existing;
            }

            _stringConstants[s] = _constants.Count;
        }

        _constants.Add(value);
        return _constants.Count - 1;
    }

    private void PushConstant(Value value)
    {
        switch (value.Tag)
        {
            case ValueTag.Null:
                Emit(OpCode.PushNull);
                return;
            case ValueTag.Bool:
                Emit(value.AsBool ? OpCode.PushTrue : OpCode.PushFalse);
                return;
            default:
                Emit(OpCode.PushConst, Constant(value));
                return;
        }
    }

    private void SetLine(TextSpan span, Semantics.Symbol? owner = null)
    {
        var tree = (owner ?? _method).Tree ?? _method.Tree;
        if (tree is not null && span.Start <= tree.Source.Length)
        {
            _line = tree.Source.GetLinePosition(span.Start).Line;
        }
    }

    private Value ConstantValue(object? constant, TypeSymbol type) => (constant, type) switch
    {
        (null, _) => Value.Null,
        (bool b, _) => Value.FromBool(b),
        (long l, PrimitiveType { Primitive: PrimitiveKind.Byte }) => Value.FromByte(l),
        (long l, PrimitiveType { Primitive: PrimitiveKind.Char }) => Value.FromChar(l),
        (long l, PrimitiveType { Primitive: PrimitiveKind.Float }) => Value.FromFloat(l),
        (long l, NamedTypeSymbol { TypeKind: TypeKind.Enum } e) => Value.FromEnum(l, _linker.GetClass(e.Definition)),
        (long l, _) => Value.FromInt(l),
        (double d, _) => Value.FromFloat(d),
        (string s, _) => Value.FromString(s),
        (ImmutableArray<byte> bytes, _) => Value.FromObject(new BytesObject([.. bytes])),
        _ => Value.Null,
    };

    #endregion

    #region Variables

    private int SlotOf(Symbol symbol)
    {
        if (!_slots.TryGetValue(symbol, out var slot))
        {
            slot = _localCount++;
            _slots[symbol] = slot;
        }

        return slot;
    }

    private void EmitLoadVariable(Symbol symbol)
    {
        if (_captureIndex.TryGetValue(symbol, out var captured))
        {
            Emit(OpCode.LoadCaptured, captured);
        }
        else if (_cells.Contains(symbol))
        {
            Emit(OpCode.LoadCell, SlotOf(symbol));
        }
        else
        {
            Emit(OpCode.LoadLocal, SlotOf(symbol));
        }
    }

    /// <summary>Stores the value on top of the stack into a variable (pops it).</summary>
    private void EmitStoreVariable(Symbol symbol)
    {
        if (_captureIndex.TryGetValue(symbol, out var captured))
        {
            Emit(OpCode.StoreCaptured, captured);
        }
        else if (_cells.Contains(symbol))
        {
            Emit(OpCode.StoreCell, SlotOf(symbol));
        }
        else
        {
            Emit(OpCode.StoreLocal, SlotOf(symbol));
        }
    }

    /// <summary>Initializes a declared variable from the stack, creating a fresh cell if a closure captures it.</summary>
    private void EmitDeclareVariable(LocalSymbol local)
    {
        Emit(OpCode.StoreLocal, SlotOf(local));
        if (_cells.Contains(local))
        {
            Emit(OpCode.MakeCell, SlotOf(local));
        }
    }

    private static bool IsUserStruct(TypeSymbol type) => type is NamedTypeSymbol { TypeKind: TypeKind.Struct, IsNative: false };

    private void EmitStructCopy(TypeSymbol type)
    {
        if (IsUserStruct(type))
        {
            Emit(OpCode.CopyStruct);
        }
        else if (type.StripNullable() is TypeParameterSymbol || (type is NullableTypeSymbol n && IsUserStruct(n.Underlying)))
        {
            Emit(OpCode.CopyIfStruct);
        }
    }

    #endregion

    #region Statements

    private void EmitStatement(BoundStatement statement)
    {
        SetLine(statement.Span);
        switch (statement)
        {
            case BoundBlock block:
                foreach (var s in block.Statements)
                {
                    EmitStatement(s);
                }

                break;

            case BoundLocalDeclaration declaration:
                if (declaration.Initializer is null)
                {
                    PushConstant(_linker.DefaultValue(declaration.Local.Type));
                }
                else
                {
                    EmitValue(declaration.Initializer);
                }

                EmitDeclareVariable(declaration.Local);
                break;

            case BoundDeconstruction deconstruction:
            {
                EmitValue(deconstruction.Value);
                var temp = Temp();
                Emit(OpCode.StoreLocal, temp);
                for (var i = 0; i < deconstruction.Locals.Length; i++)
                {
                    Emit(OpCode.LoadLocal, temp);
                    Emit(OpCode.LoadTupleItem, i);
                    EmitDeclareVariable(deconstruction.Locals[i]);
                }

                break;
            }

            case BoundExpressionStatement expression:
                EmitExpression(expression.Expression, used: false);
                break;

            case BoundIf ifStatement:
            {
                var elseLabel = NewLabel();
                var end = NewLabel();
                EmitCondition(ifStatement.Condition, elseLabel, jumpIfTrue: false);
                EmitStatement(ifStatement.Then);
                if (ifStatement.Else is not null)
                {
                    EmitJump(OpCode.Jump, end);
                    Mark(elseLabel);
                    EmitStatement(ifStatement.Else);
                }
                else
                {
                    Mark(elseLabel);
                }

                Mark(end);
                break;
            }

            case BoundWhile loop:
                EmitWhile(loop);
                break;
            case BoundFor loop:
                EmitFor(loop);
                break;
            case BoundForeach loop:
                EmitForeach(loop);
                break;
            case BoundSwitch switchStatement:
                EmitSwitch(switchStatement);
                break;

            case BoundBreak:
            {
                var (breakLabel, _, depth) = _loops.Peek();
                EmitFinalliesDownTo(depth);
                EmitJump(OpCode.Jump, breakLabel);
                break;
            }

            case BoundContinue:
            {
                var target = _loops.FirstOrDefault(l => l.Continue >= 0);
                EmitFinalliesDownTo(target.FinallyDepth);
                EmitJump(OpCode.Jump, target.Continue);
                break;
            }

            case BoundReturn ret:
                EmitReturn(ret);
                break;

            case BoundThrow thrown:
                if (thrown.Error is null)
                {
                    Emit(OpCode.Rethrow, _catchErrorLocals.Peek());
                }
                else
                {
                    EmitValue(thrown.Error);
                    Emit(OpCode.Throw);
                }

                break;

            case BoundTry tryStatement:
                EmitTry(tryStatement);
                break;

            case BoundYieldReturn yieldReturn:
                EmitValue(yieldReturn.Value);
                Emit(OpCode.Yield);
                break;

            case BoundYieldBreak:
                EmitFinalliesDownTo(0);
                Emit(OpCode.Return, 0);
                break;

            case BoundEmpty:
                break;

            default:
                throw new InvalidOperationException($"Unsupported statement {statement.GetType().Name}.");
        }
    }

    private void EmitReturn(BoundReturn ret)
    {
        if (ret.Value is null)
        {
            EmitFinalliesDownTo(0);
            Emit(OpCode.Return, 0);
            return;
        }

        EmitValue(ret.Value);
        if (_finallies.Count > 0)
        {
            var temp = Temp();
            Emit(OpCode.StoreLocal, temp);
            EmitFinalliesDownTo(0);
            Emit(OpCode.LoadLocal, temp);
        }

        Emit(OpCode.Return, 1);
    }

    /// <summary>Inlines pending finally blocks (innermost first) when leaving try regions by jump or return.</summary>
    private void EmitFinalliesDownTo(int depth)
    {
        var saved = _finallies.ToList();
        for (var i = _finallies.Count - 1; i >= depth; i--)
        {
            var block = saved[i];
            _finallies.RemoveRange(i, _finallies.Count - i);
            EmitStatement(block);
        }

        _finallies.Clear();
        _finallies.AddRange(saved);
    }

    private void EmitWhile(BoundWhile loop)
    {
        var top = NewLabel();
        var continueLabel = NewLabel();
        var end = NewLabel();
        _loops.Push((end, continueLabel, _finallies.Count));
        if (loop.IsDoWhile)
        {
            Mark(top);
            EmitStatement(loop.Body);
            Mark(continueLabel);
            EmitCondition(loop.Condition, top, jumpIfTrue: true);
        }
        else
        {
            Mark(top);
            Mark(continueLabel);
            EmitCondition(loop.Condition, end, jumpIfTrue: false);
            EmitStatement(loop.Body);
            EmitJump(OpCode.Jump, top);
        }

        _loops.Pop();
        Mark(end);
    }

    private void EmitFor(BoundFor loop)
    {
        if (loop.Initializer is not null)
        {
            EmitStatement(loop.Initializer);
        }

        var top = NewLabel();
        var continueLabel = NewLabel();
        var end = NewLabel();
        Mark(top);
        if (loop.Condition is not null)
        {
            EmitCondition(loop.Condition, end, jumpIfTrue: false);
        }

        _loops.Push((end, continueLabel, _finallies.Count));
        EmitStatement(loop.Body);
        _loops.Pop();
        Mark(continueLabel);
        foreach (var increment in loop.Increments)
        {
            EmitExpression(increment, used: false);
        }

        EmitJump(OpCode.Jump, top);
        Mark(end);
    }

    private void EmitForeach(BoundForeach loop)
    {
        var top = NewLabel();
        var continueLabel = NewLabel();
        var end = NewLabel();
        EmitValue(loop.Collection);

        switch (loop.Kind)
        {
            case ForeachKind.Array or ForeachKind.String or ForeachKind.Bytes:
            {
                var collection = Temp();
                var index = Temp();
                Emit(OpCode.StoreLocal, collection);
                PushConstant(Value.FromInt(0));
                Emit(OpCode.StoreLocal, index);
                Mark(top);
                Emit(OpCode.LoadLocal, index);
                Emit(OpCode.LoadLocal, collection);
                Emit(loop.Kind switch { ForeachKind.Array => OpCode.ArrayLength, ForeachKind.String => OpCode.StringLength, _ => OpCode.BytesLength });
                Emit(OpCode.ILt);
                EmitJump(OpCode.JumpIfFalse, end);
                Emit(OpCode.LoadLocal, collection);
                Emit(OpCode.LoadLocal, index);
                Emit(loop.Kind switch { ForeachKind.Array => OpCode.LoadElement, ForeachKind.String => OpCode.StringCharAt, _ => OpCode.BytesAt });
                EmitStructCopy(loop.ElementType);
                EmitDeclareVariable(loop.Variable);
                _loops.Push((end, continueLabel, _finallies.Count));
                EmitStatement(loop.Body);
                _loops.Pop();
                Mark(continueLabel);
                Emit(OpCode.LoadLocal, index);
                PushConstant(Value.FromInt(1));
                Emit(OpCode.IAdd);
                Emit(OpCode.StoreLocal, index);
                EmitJump(OpCode.Jump, top);
                break;
            }

            default:
            {
                var wk = _linker.Compilation.WellKnown;
                var isAsync = loop.Kind == ForeachKind.AsyncIterable;
                var iterableType = isAsync ? wk.AsyncIterable! : wk.Iterable!;
                var getIterator = iterableType.GetDeclaredMembers(isAsync ? "GetAsyncIterator" : "GetIterator").OfType<MethodSymbol>().Single();
                var iteratorType = (NamedTypeSymbol)getIterator.ReturnType;
                var moveNext = iteratorType.Definition.GetDeclaredMembers(isAsync ? "MoveNextAsync" : "MoveNext").OfType<MethodSymbol>().Single();
                var current = iteratorType.Definition.GetDeclaredMembers("Current").OfType<PropertySymbol>().Single().Getter!;
                var iterator = Temp();
                Emit(OpCode.CallVirtual, _linker.SlotOf(getIterator), 1);
                Emit(OpCode.StoreLocal, iterator);
                Mark(top);
                Mark(continueLabel);
                Emit(OpCode.LoadLocal, iterator);
                Emit(OpCode.CallVirtual, _linker.SlotOf(moveNext), 1);
                if (isAsync)
                {
                    Emit(OpCode.Await);
                }

                EmitJump(OpCode.JumpIfFalse, end);
                Emit(OpCode.LoadLocal, iterator);
                Emit(OpCode.CallVirtual, _linker.SlotOf(current), 1);
                EmitStructCopy(loop.ElementType);
                EmitDeclareVariable(loop.Variable);
                _loops.Push((end, continueLabel, _finallies.Count));
                EmitStatement(loop.Body);
                _loops.Pop();
                EmitJump(OpCode.Jump, top);
                break;
            }
        }

        Mark(end);
    }

    private void EmitSwitch(BoundSwitch switchStatement)
    {
        EmitValue(switchStatement.Subject);
        var subject = Temp();
        Emit(OpCode.StoreLocal, subject);
        var end = NewLabel();
        var sectionLabels = switchStatement.Sections.Select(_ => NewLabel()).ToList();
        var defaultIndex = -1;
        for (var i = 0; i < switchStatement.Sections.Length; i++)
        {
            foreach (var (pattern, guard) in switchStatement.Sections[i].Labels)
            {
                if (pattern is null)
                {
                    defaultIndex = i;
                    continue;
                }

                var next = NewLabel();
                EmitPatternTest(pattern, subject, switchStatement.Subject.Type);
                EmitJump(OpCode.JumpIfFalse, next);
                if (guard is not null)
                {
                    EmitCondition(guard, next, jumpIfTrue: false);
                }

                EmitJump(OpCode.Jump, sectionLabels[i]);
                Mark(next);
            }
        }

        EmitJump(OpCode.Jump, defaultIndex >= 0 ? sectionLabels[defaultIndex] : end);
        _loops.Push((end, -1, _finallies.Count));
        for (var i = 0; i < switchStatement.Sections.Length; i++)
        {
            Mark(sectionLabels[i]);
            foreach (var s in switchStatement.Sections[i].Statements)
            {
                EmitStatement(s);
            }

            EmitJump(OpCode.Jump, end);
        }

        _loops.Pop();
        Mark(end);
    }

    private void EmitTry(BoundTry tryStatement)
    {
        var start = NewLabel();
        var bodyEnd = NewLabel();
        var end = NewLabel();
        var protectedEnd = NewLabel();
        var hasFinally = tryStatement.Finally is not null;
        if (hasFinally)
        {
            _finallies.Add(tryStatement.Finally!);
        }

        Mark(start);
        EmitStatement(tryStatement.Body);
        Mark(bodyEnd);
        if (hasFinally)
        {
            _finallies.RemoveAt(_finallies.Count - 1);
            EmitStatement(tryStatement.Finally!);
            _finallies.Add(tryStatement.Finally!);
        }

        EmitJump(OpCode.Jump, end);

        foreach (var clause in tryStatement.Catches)
        {
            var handler = NewLabel();
            var errorLocal = Temp();
            _regions.Add((start, bodyEnd, handler, errorLocal, _linker.TypeTestFor(clause.ErrorType)));
            Mark(handler);
            if (clause.Variable is not null)
            {
                Emit(OpCode.LoadLocal, errorLocal);
                EmitDeclareVariable(clause.Variable);
            }

            _catchErrorLocals.Push(errorLocal);
            EmitStatement(clause.Body);
            _catchErrorLocals.Pop();
            if (hasFinally)
            {
                _finallies.RemoveAt(_finallies.Count - 1);
                EmitStatement(tryStatement.Finally!);
                _finallies.Add(tryStatement.Finally!);
            }

            EmitJump(OpCode.Jump, end);
        }

        Mark(protectedEnd);
        if (hasFinally)
        {
            _finallies.RemoveAt(_finallies.Count - 1);
            var handler = NewLabel();
            var errorLocal = Temp();
            _regions.Add((start, protectedEnd, handler, errorLocal, null));
            Mark(handler);
            EmitStatement(tryStatement.Finally!);
            Emit(OpCode.Rethrow, errorLocal);
        }

        Mark(end);
    }

    #endregion

    #region Expressions

    /// <summary>Emits an expression whose value is stored or passed: user structs are copied (value semantics).</summary>
    private void EmitValue(BoundExpression expression)
    {
        EmitExpression(expression, used: true);
        if (expression is BoundLocal or BoundParameter or BoundField or BoundArrayElement or BoundTupleElement or BoundProperty)
        {
            EmitStructCopy(expression.Type);
        }
    }

    /// <summary>Emits a receiver for member access: structs are not copied so methods can mutate them in place.</summary>
    private void EmitReceiver(BoundExpression receiver) => EmitExpression(receiver, used: true);

    private void EmitExpression(BoundExpression expression, bool used)
    {
        if (expression.ConstantValue is { } constant && expression is not BoundLambda)
        {
            if (used)
            {
                PushConstant(ConstantValue(constant, expression.Type));
            }

            return;
        }

        switch (expression)
        {
            case BoundLiteral literal:
                if (used)
                {
                    PushConstant(ConstantValue(literal.Value, literal.Type));
                }

                return;

            case BoundLocal local:
                EmitLoadVariable(local.Local);
                break;
            case BoundParameter parameter:
                EmitLoadVariable(parameter.Parameter);
                break;
            case BoundThis:
                Emit(OpCode.LoadThis);
                break;
            case BoundConditionalReceiver placeholder:
                Emit(OpCode.LoadLocal, _placeholders[placeholder]);
                break;

            case BoundField field:
                if (field.Field.IsStatic)
                {
                    Emit(OpCode.LoadGlobal, _linker.GlobalSlot(field.Field));
                }
                else
                {
                    EmitReceiver(field.Receiver!);
                    Emit(OpCode.LoadField, field.Field.OriginalDefinition.Slot);
                }

                break;

            case BoundProperty property:
                EmitAccessorCall(property.Property.Getter!, property.Receiver, property.Arguments, extra: null);
                break;

            case BoundArrayElement element:
                EmitReceiver(element.Array);
                EmitValue(element.Index);
                Emit(OpCode.LoadElement);
                break;

            case BoundTupleElement tupleElement:
                EmitReceiver(tupleElement.Tuple);
                Emit(OpCode.LoadTupleItem, tupleElement.Index);
                break;

            case BoundCall call:
                EmitCall(call);
                if (call.Type == PrimitiveType.Void)
                {
                    if (used)
                    {
                        Emit(OpCode.PushNull);
                    }

                    return;
                }

                break;

            case BoundFunctionInvocation invocation:
                EmitValue(invocation.Target);
                foreach (var argument in invocation.Arguments)
                {
                    EmitValue(argument);
                }

                Emit(OpCode.CallFunction, 0, invocation.Arguments.Length);
                if (invocation.Type == PrimitiveType.Void)
                {
                    if (used)
                    {
                        Emit(OpCode.PushNull);
                    }

                    return;
                }

                break;

            case BoundMethodReference reference:
                EmitMethodReference(reference);
                break;

            case BoundLambda lambda:
                EmitClosure(lambda);
                break;

            case BoundObjectCreation creation:
                EmitObjectCreation(creation, used);
                return;

            case BoundArrayCreation array:
                EmitValue(array.Length);
                Emit(OpCode.NewArray, Constant(_linker.DefaultValue(array.ArrayType.Element)));
                break;

            case BoundArrayLiteral array:
                foreach (var element in array.Elements)
                {
                    EmitValue(element);
                }

                Emit(OpCode.NewArrayInit, array.Elements.Length);
                break;

            case BoundTupleLiteral tuple:
                foreach (var element in tuple.Elements)
                {
                    EmitValue(element);
                }

                Emit(OpCode.NewTuple, tuple.Elements.Length);
                break;

            case BoundUnary unary:
                EmitValue(unary.Operand);
                Emit(unary.Operator switch
                {
                    UnaryOperatorKind.IntNegate => OpCode.INeg,
                    UnaryOperatorKind.FloatNegate => OpCode.FNeg,
                    UnaryOperatorKind.DurationNegate => OpCode.DNeg,
                    UnaryOperatorKind.IntBitwiseNot => OpCode.INot,
                    _ => OpCode.BNot,
                });
                break;

            case BoundBinary binary:
                EmitBinary(binary);
                break;

            case BoundLogical logical:
            {
                var shortCircuit = NewLabel();
                var end = NewLabel();
                EmitValue(logical.Left);
                EmitJump(logical.IsAnd ? OpCode.JumpIfFalse : OpCode.JumpIfTrue, shortCircuit);
                EmitValue(logical.Right);
                EmitJump(OpCode.Jump, end);
                Mark(shortCircuit);
                Emit(logical.IsAnd ? OpCode.PushFalse : OpCode.PushTrue);
                Mark(end);
                break;
            }

            case BoundCoalesce coalesce:
            {
                var end = NewLabel();
                EmitValue(coalesce.Left);
                Emit(OpCode.Dup);
                EmitJump(OpCode.JumpIfNotNull, end);
                Emit(OpCode.Pop);
                EmitValue(coalesce.Right);
                Mark(end);
                break;
            }

            case BoundConditionalAccess conditional:
                EmitConditionalAccess(conditional, used);
                return;

            case BoundAssignment assignment:
                EmitAssignment(assignment.Target, () => EmitValue(assignment.Value), used);
                return;

            case BoundCompoundAssignment compound:
                EmitCompoundAssignment(compound, used);
                return;

            case BoundIncrement increment:
                EmitIncrement(increment, used);
                return;

            case BoundConversion conversion:
                EmitConversion(conversion);
                break;

            case BoundConditional conditional:
            {
                var elseLabel = NewLabel();
                var end = NewLabel();
                EmitCondition(conditional.Condition, elseLabel, jumpIfTrue: false);
                EmitValue(conditional.WhenTrue);
                EmitJump(OpCode.Jump, end);
                Mark(elseLabel);
                EmitValue(conditional.WhenFalse);
                Mark(end);
                break;
            }

            case BoundIsPattern isPattern:
            {
                EmitValue(isPattern.Operand);
                var temp = Temp();
                Emit(OpCode.StoreLocal, temp);
                EmitPatternTest(isPattern.Pattern, temp, isPattern.Operand.Type);
                break;
            }

            case BoundSwitchExpression switchExpression:
                EmitSwitchExpression(switchExpression);
                break;

            case BoundAwait await:
                EmitValue(await.Operand);
                Emit(OpCode.Await);
                if (await.Type == PrimitiveType.Void)
                {
                    Emit(OpCode.Pop);
                    if (used)
                    {
                        Emit(OpCode.PushNull);
                    }

                    return;
                }

                break;

            case BoundTypeOf typeOf:
                PushConstant(Value.FromString(typeOf.Operand.ToString()));
                break;

            case BoundInterpolatedString interpolated:
                EmitInterpolation(interpolated);
                break;

            case BoundThrowExpression throwExpression:
                EmitValue(throwExpression.Error);
                Emit(OpCode.Throw);
                Emit(OpCode.PushNull); // unreachable; keeps the stack shape of an expression
                break;

            default:
                throw new InvalidOperationException($"Unsupported expression {expression.GetType().Name} in {_unit.Name}.");
        }

        if (!used)
        {
            Emit(OpCode.Pop);
        }
    }

    private void EmitCall(BoundCall call)
    {
        var method = call.Method;
        if (method.IsStatic)
        {
            foreach (var argument in call.Arguments)
            {
                EmitValue(argument);
            }

            Emit(OpCode.Call, _linker.CallableIndex(_linker.GetCallable(method)), call.Arguments.Length);
            return;
        }

        EmitReceiver(call.Receiver!);
        foreach (var argument in call.Arguments)
        {
            EmitValue(argument);
        }

        var direct = call.IsBaseCall || method.MethodKind == MethodKind.Constructor;
        Emit(direct ? OpCode.Call : OpCode.CallVirtual,
            direct ? _linker.CallableIndex(_linker.GetCallable(method)) : _linker.SlotOf(method), call.Arguments.Length + 1);
    }

    /// <summary>Calls a property or indexer accessor; <paramref name="extra"/> emits the setter's value argument.</summary>
    private void EmitAccessorCall(MethodSymbol accessor, BoundExpression? receiver, ImmutableArray<BoundExpression> arguments, Action? extra)
    {
        var count = arguments.Length + (extra is null ? 0 : 1);
        if (accessor.IsStatic)
        {
            foreach (var argument in arguments)
            {
                EmitValue(argument);
            }

            extra?.Invoke();
            Emit(OpCode.Call, _linker.CallableIndex(_linker.GetCallable(accessor)), count);
            return;
        }

        EmitReceiver(receiver!);
        foreach (var argument in arguments)
        {
            EmitValue(argument);
        }

        extra?.Invoke();
        Emit(OpCode.CallVirtual, _linker.SlotOf(accessor), count + 1);
    }

    private void EmitMethodReference(BoundMethodReference reference)
    {
        if (reference.Method.IsStatic)
        {
            Emit(OpCode.PushNull);
            Emit(OpCode.MakeMethodRef, _linker.CallableIndex(_linker.GetCallable(reference.Method)), 0);
        }
        else
        {
            EmitReceiver(reference.Receiver!);
            Emit(OpCode.MakeMethodRef, _linker.SlotOf(reference.Method), 1);
        }
    }

    private void EmitClosure(BoundLambda lambda)
    {
        var callable = _linker.GetCallable(lambda.Function);
        var captures = _linker.CapturesOf(lambda);
        foreach (var symbol in captures)
        {
            if (_captureIndex.TryGetValue(symbol, out var outer))
            {
                Emit(OpCode.LoadCapturedRef, outer);
            }
            else
            {
                _cells.Add(symbol);
                Emit(OpCode.LoadCellRef, SlotOf(symbol));
            }
        }

        if (_linker.LambdaBody(lambda.Function)?.CapturesThis == true)
        {
            Emit(OpCode.LoadThis);
        }
        else
        {
            Emit(OpCode.PushNull);
        }

        Emit(OpCode.MakeClosure, _linker.CallableIndex(callable), captures.Length);
    }

    private void EmitObjectCreation(BoundObjectCreation creation, bool used)
    {
        var runtimeClass = _linker.GetClass(creation.CreatedType);
        var temp = Temp();
        Emit(OpCode.NewObject, _linker.ClassIndex(runtimeClass));
        Emit(OpCode.StoreLocal, temp);
        if (creation.Constructor is { } constructor)
        {
            Emit(OpCode.LoadLocal, temp);
            foreach (var argument in creation.Arguments)
            {
                EmitValue(argument);
            }

            Emit(OpCode.Call, _linker.CallableIndex(_linker.GetCallable(constructor)), creation.Arguments.Length + 1);
        }

        if (!creation.Initializers.IsEmpty)
        {
            foreach (var initializer in creation.Initializers)
            {
                switch (initializer)
                {
                    case BoundAssignment { Target: BoundField { Receiver: BoundConditionalReceiver fieldReceiver } }:
                        _placeholders[fieldReceiver] = temp;
                        break;
                    case BoundAssignment { Target: BoundProperty { Receiver: BoundConditionalReceiver propertyReceiver } }:
                        _placeholders[propertyReceiver] = temp;
                        break;
                }

                EmitExpression(initializer, used: false);
            }
        }

        if (used)
        {
            Emit(OpCode.LoadLocal, temp);
        }
    }

    private void EmitConditionalAccess(BoundConditionalAccess conditional, bool used)
    {
        var isNull = NewLabel();
        var end = NewLabel();
        EmitValue(conditional.Receiver);
        var temp = Temp();
        Emit(OpCode.StoreLocal, temp);
        _placeholders[conditional.Placeholder] = temp;
        Emit(OpCode.LoadLocal, temp);
        EmitJump(OpCode.JumpIfNull, isNull);
        var producesValue = used && conditional.Access.Type != PrimitiveType.Void;
        EmitExpression(conditional.Access, used: producesValue);
        EmitJump(OpCode.Jump, end);
        Mark(isNull);
        if (producesValue)
        {
            Emit(OpCode.PushNull);
        }

        Mark(end);
        if (used && !producesValue)
        {
            Emit(OpCode.PushNull);
        }
    }

    /// <summary>Stores a value into an assignable target; the value is produced by <paramref name="emitValue"/>.</summary>
    private void EmitAssignment(BoundExpression target, Action emitValue, bool used)
    {
        switch (target)
        {
            case BoundLocal local:
                emitValue();
                if (used)
                {
                    Emit(OpCode.Dup);
                }

                EmitStoreVariable(local.Local);
                return;

            case BoundParameter parameter:
                emitValue();
                if (used)
                {
                    Emit(OpCode.Dup);
                }

                EmitStoreVariable(parameter.Parameter);
                return;

            case BoundConversion { Kind: ConversionKind.ExplicitNullable, Operand: var narrowed }:
                EmitAssignment(narrowed, emitValue, used);
                return;

            case BoundField { Field.IsStatic: true } staticField:
                emitValue();
                if (used)
                {
                    Emit(OpCode.Dup);
                }

                Emit(OpCode.StoreGlobal, _linker.GlobalSlot(staticField.Field));
                return;

            case BoundField field:
            {
                var value = Temp();
                emitValue();
                Emit(OpCode.StoreLocal, value);
                EmitReceiver(field.Receiver!);
                Emit(OpCode.LoadLocal, value);
                Emit(OpCode.StoreField, field.Field.OriginalDefinition.Slot);
                if (used)
                {
                    Emit(OpCode.LoadLocal, value);
                }

                return;
            }

            case BoundArrayElement element:
            {
                var value = Temp();
                emitValue();
                Emit(OpCode.StoreLocal, value);
                EmitReceiver(element.Array);
                EmitValue(element.Index);
                Emit(OpCode.LoadLocal, value);
                Emit(OpCode.StoreElement);
                if (used)
                {
                    Emit(OpCode.LoadLocal, value);
                }

                return;
            }

            case BoundProperty property:
            {
                var value = Temp();
                emitValue();
                Emit(OpCode.StoreLocal, value);
                if (property.Property.Setter is { } setter)
                {
                    EmitAccessorCall(setter, property.Receiver, property.Arguments, () => Emit(OpCode.LoadLocal, value));
                }
                else
                {
                    // Get-only auto-property assigned inside its constructor: write the backing field directly.
                    var backing = property.Property.BackingField!;
                    if (backing.IsStatic)
                    {
                        Emit(OpCode.LoadLocal, value);
                        Emit(OpCode.StoreGlobal, _linker.GlobalSlot(backing));
                    }
                    else
                    {
                        EmitReceiver(property.Receiver!);
                        Emit(OpCode.LoadLocal, value);
                        Emit(OpCode.StoreField, backing.OriginalDefinition.Slot);
                    }
                }

                if (used)
                {
                    Emit(OpCode.LoadLocal, value);
                }

                return;
            }

            default:
                throw new InvalidOperationException($"Cannot assign to {target.GetType().Name}.");
        }
    }

    /// <summary>Evaluates receivers/indices of a target once into temps so read-modify-write touches it exactly once.</summary>
    private BoundExpression Stabilize(BoundExpression target)
    {
        BoundExpression Spill(BoundExpression e)
        {
            if (e is BoundThis or BoundLocal or BoundParameter or BoundConditionalReceiver || e.ConstantValue is not null)
            {
                return e;
            }

            EmitReceiver(e);
            var temp = Temp();
            Emit(OpCode.StoreLocal, temp);
            var placeholder = new BoundConditionalReceiver(e.Span, e.Type);
            _placeholders[placeholder] = temp;
            return placeholder;
        }

        return target switch
        {
            BoundField { Receiver: { } r } f => f with { Receiver = Spill(r) },
            BoundArrayElement a => a with { Array = Spill(a.Array), Index = Spill(a.Index) },
            BoundProperty p => p with { Receiver = p.Receiver is null ? null : Spill(p.Receiver), Arguments = [.. p.Arguments.Select(Spill)] },
            BoundConversion { Kind: ConversionKind.ExplicitNullable } c => c with { Operand = Stabilize(c.Operand) },
            _ => target,
        };
    }

    private void EmitCompoundAssignment(BoundCompoundAssignment compound, bool used)
    {
        var target = Stabilize(compound.Target);
        if (compound.IsCoalesce)
        {
            var end = NewLabel();
            EmitExpression(target, used: true);
            Emit(OpCode.Dup);
            EmitJump(OpCode.JumpIfNotNull, end);
            Emit(OpCode.Pop);
            EmitAssignment(target, () => EmitValue(compound.Value), used: true);
            Mark(end);
            if (!used)
            {
                Emit(OpCode.Pop);
            }

            return;
        }

        EmitAssignment(target, () =>
        {
            EmitExpression(target, used: true);
            if (compound.Operator is null)
            {
                EmitValue(compound.Value);
                Emit(compound.IsFunctionRemove ? OpCode.FunctionRemove : OpCode.FunctionCombine);
                return;
            }

            var op = compound.Operator.Value;
            if (op == BinaryOperatorKind.StringConcat)
            {
                EmitToString(target.Type, null);
                EmitValue(compound.Value);
                EmitToString(compound.Value.Type, null);
                Emit(OpCode.StrConcat);
                return;
            }

            // Promote the target's value to the operator's operand type, apply, then narrow back.
            EmitPromotion(target.Type, op);
            EmitValue(compound.Value);
            Emit(BinaryOpCode(op));
            EmitNarrowing(target.Type, op);
        }, used);
    }

    private void EmitPromotion(TypeSymbol type, BinaryOperatorKind op)
    {
        var isFloatOp = op is >= BinaryOperatorKind.FloatAdd and <= BinaryOperatorKind.FloatGreaterOrEqual;
        if (isFloatOp && type != PrimitiveType.Float)
        {
            Emit(OpCode.ToFloat);
        }
        else if (!isFloatOp && type is PrimitiveType { Primitive: PrimitiveKind.Byte or PrimitiveKind.Char })
        {
            Emit(OpCode.ToInt);
        }
    }

    private void EmitNarrowing(TypeSymbol type, BinaryOperatorKind op)
    {
        switch (type)
        {
            case PrimitiveType { Primitive: PrimitiveKind.Byte }:
                Emit(OpCode.ToByte);
                break;
            case PrimitiveType { Primitive: PrimitiveKind.Char }:
                Emit(OpCode.ToChar);
                break;
            case PrimitiveType { Primitive: PrimitiveKind.Int } when op is >= BinaryOperatorKind.FloatAdd and <= BinaryOperatorKind.FloatModulo:
                Emit(OpCode.ToInt);
                break;
        }
    }

    private void EmitIncrement(BoundIncrement increment, bool used)
    {
        var target = Stabilize(increment.Target);
        var type = target.Type.StripNullable();
        var result = Temp();
        EmitAssignment(target, () =>
        {
            EmitExpression(target, used: true);
            if (increment.IsPostfix)
            {
                Emit(OpCode.Dup);
                Emit(OpCode.StoreLocal, result);
            }

            if (type == PrimitiveType.Float)
            {
                PushConstant(Value.FromFloat(1));
                Emit(increment.IsIncrement ? OpCode.FAdd : OpCode.FSub);
            }
            else
            {
                Emit(OpCode.ToInt);
                PushConstant(Value.FromInt(1));
                Emit(increment.IsIncrement ? OpCode.IAdd : OpCode.ISub);
                if (type is PrimitiveType { Primitive: PrimitiveKind.Byte })
                {
                    Emit(OpCode.ToByte);
                }
                else if (type is PrimitiveType { Primitive: PrimitiveKind.Char })
                {
                    Emit(OpCode.ToChar);
                }
            }

            if (!increment.IsPostfix)
            {
                Emit(OpCode.Dup);
                Emit(OpCode.StoreLocal, result);
            }
        }, used: false);

        if (used)
        {
            Emit(OpCode.LoadLocal, result);
        }
    }

    private void EmitConversion(BoundConversion conversion)
    {
        EmitValue(conversion.Operand);
        var from = conversion.Operand.Type.StripNullable();
        var to = conversion.TargetType.StripNullable();
        switch (conversion.Kind)
        {
            case ConversionKind.ImplicitNumeric:
            case ConversionKind.ExplicitNumeric:
                Emit(to switch
                {
                    PrimitiveType { Primitive: PrimitiveKind.Float } => OpCode.ToFloat,
                    PrimitiveType { Primitive: PrimitiveKind.Byte } => OpCode.ToByte,
                    PrimitiveType { Primitive: PrimitiveKind.Char } => OpCode.ToChar,
                    _ => OpCode.ToInt,
                });
                break;
            case ConversionKind.ExplicitNullable:
                Emit(OpCode.UnwrapNullable);
                if (from != to && Conversions.Classify(from, to) is { Exists: true, Kind: not ConversionKind.Identity } inner)
                {
                    EmitConversion(new BoundConversion(conversion.Span, new BoundConversionMarker(from), inner.Kind, to), skipOperand: true);
                }

                break;
            case ConversionKind.ImplicitNullable:
                if (from != to && Conversions.ClassifyImplicit(from, to) is { Exists: true, Kind: not ConversionKind.Identity } lifted)
                {
                    EmitConversion(new BoundConversion(conversion.Span, new BoundConversionMarker(from), lifted.Kind, to), skipOperand: true);
                }

                break;
            case ConversionKind.Boxing:
                EmitStructCopy(from);
                break;
            case ConversionKind.ExplicitReference:
            case ConversionKind.Unboxing:
                if (to is not TypeParameterSymbol)
                {
                    Emit(OpCode.CastCheck, _linker.TypeTestIndex(conversion.TargetType));
                }

                break;
            case ConversionKind.ExplicitEnum:
                if (to is NamedTypeSymbol { TypeKind: TypeKind.Enum } e)
                {
                    Emit(OpCode.ToEnum, _linker.ClassIndex(_linker.GetClass(e.Definition)));
                }
                else
                {
                    Emit(OpCode.ToInt);
                }

                break;
            case ConversionKind.ImplicitTuple when from is TupleTypeSymbol ft && to is TupleTypeSymbol tt:
            {
                var temp = Temp();
                Emit(OpCode.StoreLocal, temp);
                for (var i = 0; i < tt.Elements.Length; i++)
                {
                    Emit(OpCode.LoadLocal, temp);
                    Emit(OpCode.LoadTupleItem, i);
                    var element = Conversions.ClassifyImplicit(ft.Elements[i], tt.Elements[i]);
                    if (element.Kind is not (ConversionKind.Identity or ConversionKind.ImplicitReference or ConversionKind.NullLiteral))
                    {
                        EmitConversion(new BoundConversion(conversion.Span, new BoundConversionMarker(ft.Elements[i]), element.Kind, tt.Elements[i]), skipOperand: true);
                    }
                }

                Emit(OpCode.NewTuple, tt.Elements.Length);
                break;
            }
        }
    }

    /// <summary>Applies a conversion to the value already on the stack.</summary>
    private void EmitConversion(BoundConversion conversion, bool skipOperand)
    {
        if (!skipOperand)
        {
            EmitConversion(conversion);
            return;
        }

        var placeholder = Temp();
        Emit(OpCode.StoreLocal, placeholder);
        var receiver = new BoundConditionalReceiver(conversion.Span, conversion.Operand.Type);
        _placeholders[receiver] = placeholder;
        EmitConversion(conversion with { Operand = receiver });
    }

    /// <summary>Stand-in operand type for conversions applied to an already-emitted value.</summary>
    private sealed record BoundConversionMarker(TypeSymbol OperandType) : BoundExpression(default, OperandType);

    private void EmitBinary(BoundBinary binary)
    {
        if (binary.Operator == BinaryOperatorKind.StringConcat)
        {
            EmitValue(binary.Left);
            EmitToString(binary.Left.Type, null);
            EmitValue(binary.Right);
            EmitToString(binary.Right.Type, null);
            Emit(OpCode.StrConcat);
            return;
        }

        EmitValue(binary.Left);
        EmitValue(binary.Right);
        Emit(binary.Operator switch
        {
            BinaryOperatorKind.DurationMultiply when binary.Right.Type == PrimitiveType.Float => OpCode.DMulFloat,
            BinaryOperatorKind.DurationDivide when binary.Right.Type == PrimitiveType.Float => OpCode.DDivFloat,
            _ => BinaryOpCode(binary.Operator),
        });
    }

    private static OpCode BinaryOpCode(BinaryOperatorKind op) => op switch
    {
        BinaryOperatorKind.IntAdd => OpCode.IAdd,
        BinaryOperatorKind.IntSubtract => OpCode.ISub,
        BinaryOperatorKind.IntMultiply => OpCode.IMul,
        BinaryOperatorKind.IntDivide => OpCode.IDiv,
        BinaryOperatorKind.IntModulo => OpCode.IRem,
        BinaryOperatorKind.IntShiftLeft => OpCode.IShl,
        BinaryOperatorKind.IntShiftRight => OpCode.IShr,
        BinaryOperatorKind.IntShiftRightUnsigned => OpCode.IUshr,
        BinaryOperatorKind.IntAnd => OpCode.IAnd,
        BinaryOperatorKind.IntOr => OpCode.IOr,
        BinaryOperatorKind.IntXor => OpCode.IXor,
        BinaryOperatorKind.IntLess or BinaryOperatorKind.CompareLess => OpCode.ILt,
        BinaryOperatorKind.IntLessOrEqual or BinaryOperatorKind.CompareLessOrEqual => OpCode.ILe,
        BinaryOperatorKind.IntGreater or BinaryOperatorKind.CompareGreater => OpCode.IGt,
        BinaryOperatorKind.IntGreaterOrEqual or BinaryOperatorKind.CompareGreaterOrEqual => OpCode.IGe,
        BinaryOperatorKind.FloatAdd => OpCode.FAdd,
        BinaryOperatorKind.FloatSubtract => OpCode.FSub,
        BinaryOperatorKind.FloatMultiply => OpCode.FMul,
        BinaryOperatorKind.FloatDivide => OpCode.FDiv,
        BinaryOperatorKind.FloatModulo => OpCode.FRem,
        BinaryOperatorKind.FloatLess => OpCode.FLt,
        BinaryOperatorKind.FloatLessOrEqual => OpCode.FLe,
        BinaryOperatorKind.FloatGreater => OpCode.FGt,
        BinaryOperatorKind.FloatGreaterOrEqual => OpCode.FGe,
        BinaryOperatorKind.BoolAnd => OpCode.BAnd,
        BinaryOperatorKind.BoolOr => OpCode.BOr,
        BinaryOperatorKind.BoolXor => OpCode.BXor,
        BinaryOperatorKind.BytesConcat => OpCode.BytesConcat,
        BinaryOperatorKind.DurationAdd => OpCode.DAdd,
        BinaryOperatorKind.DurationSubtract => OpCode.DSub,
        BinaryOperatorKind.DurationMultiply => OpCode.DMulInt,
        BinaryOperatorKind.DurationDivide => OpCode.DDivInt,
        BinaryOperatorKind.InstantAddDuration => OpCode.TAddD,
        BinaryOperatorKind.InstantSubtractDuration => OpCode.TSubD,
        BinaryOperatorKind.InstantDifference => OpCode.TDiff,
        BinaryOperatorKind.Equal => OpCode.Equal,
        BinaryOperatorKind.NotEqual => OpCode.NotEqual,
        _ => throw new InvalidOperationException($"No opcode for {op}."),
    };

    /// <summary>Turns the value on the stack into a string (Doc 7 §6 concatenation / interpolation).</summary>
    private void EmitToString(TypeSymbol type, string? format)
    {
        if (type == PrimitiveType.String && format is null)
        {
            return;
        }

        var underlying = type.StripNullable();
        if (underlying is PrimitiveType or NamedTypeSymbol { TypeKind: TypeKind.Enum } or NullTypeSymbol || format is not null)
        {
            Emit(OpCode.FormatValue, format is null ? -1 : Constant(Value.FromString(format)));
            return;
        }

        // Objects: null formats as "", otherwise call the (possibly overridden) ToString.
        var temp = Temp();
        var isNull = NewLabel();
        var end = NewLabel();
        Emit(OpCode.StoreLocal, temp);
        Emit(OpCode.LoadLocal, temp);
        EmitJump(OpCode.JumpIfNull, isNull);
        Emit(OpCode.LoadLocal, temp);
        Emit(OpCode.CallVirtual, _linker.ToStringSlot, 1);
        EmitJump(OpCode.Jump, end);
        Mark(isNull);
        PushConstant(Value.FromString(string.Empty));
        Mark(end);
    }

    private void EmitInterpolation(BoundInterpolatedString interpolated)
    {
        PushConstant(Value.FromString(string.Empty));
        foreach (var part in interpolated.Parts)
        {
            if (part.Text is not null)
            {
                PushConstant(Value.FromString(part.Text));
            }
            else
            {
                EmitValue(part.Value!);
                EmitToString(part.Value!.Type, part.Format);
            }

            Emit(OpCode.StrConcat);
        }
    }

    /// <summary>Emits a branch to <paramref name="label"/> when the condition is (or is not) true.</summary>
    private void EmitCondition(BoundExpression condition, int label, bool jumpIfTrue)
    {
        if (condition.ConstantValue is bool constant)
        {
            if (constant == jumpIfTrue)
            {
                EmitJump(OpCode.Jump, label);
            }

            return;
        }

        EmitValue(condition);
        EmitJump(jumpIfTrue ? OpCode.JumpIfTrue : OpCode.JumpIfFalse, label);
    }

    #endregion

    #region Patterns

    /// <summary>Tests the value in local <paramref name="subject"/> against a pattern; pushes a bool and binds designations.</summary>
    private void EmitPatternTest(BoundPattern pattern, int subject, TypeSymbol subjectType)
    {
        switch (pattern)
        {
            case BoundDiscardPattern:
                Emit(OpCode.PushTrue);
                return;

            case BoundTypePattern typePattern:
            {
                var fail = NewLabel();
                var end = NewLabel();
                if (typePattern.MatchedType is not NullableTypeSymbol)
                {
                    Emit(OpCode.LoadLocal, subject);
                    Emit(OpCode.IsType, _linker.TypeTestIndex(typePattern.MatchedType));
                    EmitJump(OpCode.JumpIfFalse, fail);
                }

                if (typePattern.Designation is { } local)
                {
                    Emit(OpCode.LoadLocal, subject);
                    EmitDeclareVariable(local);
                }

                Emit(OpCode.PushTrue);
                EmitJump(OpCode.Jump, end);
                Mark(fail);
                Emit(OpCode.PushFalse);
                Mark(end);
                return;
            }

            case BoundConstantPattern { Value: BoundLiteral { Value: null } }:
                Emit(OpCode.LoadLocal, subject);
                Emit(OpCode.IsNull);
                return;

            case BoundConstantPattern constant:
                Emit(OpCode.LoadLocal, subject);
                EmitValue(constant.Value);
                Emit(OpCode.Equal);
                return;

            case BoundRelationalPattern relational:
            {
                var fail = NewLabel();
                var end = NewLabel();
                Emit(OpCode.LoadLocal, subject);
                EmitJump(OpCode.JumpIfNull, fail);
                Emit(OpCode.LoadLocal, subject);
                if (subjectType.StripNullable() is PrimitiveType { Primitive: PrimitiveKind.Byte or PrimitiveKind.Char }
                    && relational.Operator is >= BinaryOperatorKind.IntLess and <= BinaryOperatorKind.IntGreaterOrEqual)
                {
                    Emit(OpCode.ToInt);
                }
                else if (relational.Operator is >= BinaryOperatorKind.FloatLess and <= BinaryOperatorKind.FloatGreaterOrEqual
                         && subjectType.StripNullable() != PrimitiveType.Float)
                {
                    Emit(OpCode.ToFloat);
                }

                EmitValue(relational.Value);
                Emit(BinaryOpCode(relational.Operator));
                EmitJump(OpCode.Jump, end);
                Mark(fail);
                Emit(OpCode.PushFalse);
                Mark(end);
                return;
            }

            default:
                throw new InvalidOperationException($"Unsupported pattern {pattern.GetType().Name}.");
        }
    }

    private void EmitSwitchExpression(BoundSwitchExpression switchExpression)
    {
        EmitValue(switchExpression.Subject);
        var subject = Temp();
        Emit(OpCode.StoreLocal, subject);
        var end = NewLabel();
        foreach (var arm in switchExpression.Arms)
        {
            var next = NewLabel();
            EmitPatternTest(arm.Pattern, subject, switchExpression.Subject.Type);
            EmitJump(OpCode.JumpIfFalse, next);
            if (arm.Guard is not null)
            {
                EmitCondition(arm.Guard, next, jumpIfTrue: false);
            }

            EmitValue(arm.Result);
            EmitJump(OpCode.Jump, end);
            Mark(next);
        }

        Emit(OpCode.ThrowRuntime, Constant(Value.FromString("ArgumentError")),
            Constant(Value.FromString("No switch arm matched the value.")));
        Emit(OpCode.PushNull);
        Mark(end);
    }

    #endregion
}
