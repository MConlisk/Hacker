using System.Collections.Immutable;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Semantics.Binding;

/// <summary>Lexical scope for locals; CScript forbids shadowing a local or parameter of the same function.</summary>
internal sealed class Scope(Scope? parent, FunctionContext function)
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.Ordinal);

    public Scope? Parent { get; } = parent;
    public FunctionContext Function { get; } = function;

    public bool TryDeclare(Symbol symbol)
    {
        for (var s = this; s is not null; s = s.Parent)
        {
            if (s._symbols.ContainsKey(symbol.Name))
            {
                return false;
            }
        }

        _symbols[symbol.Name] = symbol;
        return true;
    }

    public Symbol? Lookup(string name)
    {
        for (var s = this; s is not null; s = s.Parent)
        {
            if (s._symbols.TryGetValue(name, out var symbol))
            {
                return symbol;
            }
        }

        return null;
    }
}

/// <summary>Per-function binding state (also one per lambda, chained to its enclosing function).</summary>
internal sealed class FunctionContext(MethodSymbol method, FunctionContext? parent, bool isStatic)
{
    public MethodSymbol Method { get; } = method;
    public FunctionContext? Parent { get; } = parent;
    public bool IsStatic { get; } = isStatic;
    public bool IsAsync => Method.IsAsync;

    /// <summary>Declared return type for functions; inferred for lambdas without a target.</summary>
    public TypeSymbol? ReturnType { get; set; }

    public FunctionFlavor Flavor { get; set; }
    public TypeSymbol? ElementType { get; set; }
    public bool SawYield { get; set; }
    public bool SawReturnValue { get; set; }
    public List<TypeSymbol> InferredReturnTypes { get; } = [];
    public HashSet<LocalSymbol> CapturedLocals { get; } = [];
    public HashSet<ParameterSymbol> CapturedParameters { get; } = [];
    public bool CapturesThis { get; set; }
}

/// <summary>
/// Binds function bodies: name lookup, type checking, overload resolution, operators, conversions, control flow
/// (Doc 7 §4–§9). Produces <see cref="BoundFunction"/>s for code generation.
/// </summary>
internal sealed partial class BodyBinder
{
    private readonly WellKnownTypes _wk;
    private DiagnosticBag _diagnostics;
    private readonly SyntaxTree _tree;
    private readonly ModuleSymbol _module;
    private readonly NamedTypeSymbol? _containingType;
    private readonly List<BoundFunction> _lambdas;
    private readonly Dictionary<Symbol, FunctionContext> _owners = new(ReferenceEqualityComparer.Instance);
    private FunctionContext _function;
    private Scope _scope;
    private int _loopDepth;
    private int _breakableDepth;
    private int _catchDepth;
    private int _lambdaCounter;
    private ImmutableHashSet<Symbol> _nonNull = ImmutableHashSet.Create<Symbol>(ReferenceEqualityComparer.Instance);

    public BodyBinder(WellKnownTypes wellKnown, DiagnosticBag diagnostics, SyntaxTree tree, ModuleSymbol module,
        NamedTypeSymbol? containingType, MethodSymbol method, List<BoundFunction> lambdas)
    {
        _wk = wellKnown;
        _diagnostics = diagnostics;
        _tree = tree;
        _module = module;
        _containingType = containingType;
        _lambdas = lambdas;
        _function = new FunctionContext(method, null, method.IsStatic) { ReturnType = method.ReturnType };
        _scope = new Scope(null, _function);
    }

    #region Entry points

    public BoundFunction BindFunction(BlockStmt? body, Expr? expressionBody, ConstructorInitializer? initializer = null)
    {
        DetermineFlavor();
        foreach (var parameter in _function.Method.Parameters)
        {
            Declare(parameter, parameter.Syntax?.Span ?? default);
        }

        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        if (_function.Method.MethodKind == MethodKind.Constructor)
        {
            if (BindBaseConstructorCall(initializer) is { } baseCall)
            {
                statements.Add(baseCall);
            }
        }

        if (expressionBody is not null)
        {
            var expected = EffectiveReturnType();
            if (expected == PrimitiveType.Void || _function.Flavor is FunctionFlavor.Iterator)
            {
                statements.Add(new BoundExpressionStatement(expressionBody.Span, BindExpression(expressionBody)));
            }
            else
            {
                statements.Add(new BoundReturn(expressionBody.Span, BindExpression(expressionBody, expected)));
            }
        }
        else if (body is not null)
        {
            var bound = BindBlock(body);
            statements.AddRange(bound.Statements);
            var expected = EffectiveReturnType();
            if (expected != PrimitiveType.Void && !expected.IsError && _function.Flavor is FunctionFlavor.Normal or FunctionFlavor.Async
                && ControlFlow.CanCompleteNormally(bound))
            {
                Report(DiagnosticCodes.NotAllPathsReturn, ClosingBrace(body), _function.Method.Name);
            }
        }

        return Finish(new BoundBlock(body?.Span ?? expressionBody?.Span ?? default, statements.ToImmutable()));
    }

    /// <summary>Binds a module global or field initializer in a static or instance context.</summary>
    public BoundExpression BindInitializer(Expr expr, TypeSymbol? targetType) =>
        targetType is null || targetType.IsError ? BindExpression(expr) : BindExpression(expr, targetType);

    private BoundFunction Finish(BoundBlock body) => new(
        _function.Method,
        body,
        _function.Flavor,
        _function.ElementType,
        [.. _function.CapturedLocals],
        [.. _function.CapturedParameters],
        _function.CapturesThis);

    private void DetermineFlavor()
    {
        var method = _function.Method;
        var returnType = method.ReturnType;
        if (method.Syntax is FunctionDecl decl && ContainsYield(decl.Body))
        {
            if (returnType is NamedTypeSymbol n && n.TypeArguments.Length == 1)
            {
                if (ReferenceEquals(n.Definition, _wk.Iterable) || ReferenceEquals(n.Definition, _wk.Iterator))
                {
                    _function.Flavor = FunctionFlavor.Iterator;
                    _function.ElementType = n.TypeArguments[0];
                }
                else if (ReferenceEquals(n.Definition, _wk.AsyncIterable))
                {
                    _function.Flavor = FunctionFlavor.AsyncIterator;
                    _function.ElementType = n.TypeArguments[0];
                }
            }

            if (_function.Flavor == FunctionFlavor.Normal)
            {
                Report(DiagnosticCodes.YieldNotAllowed, decl.ReturnType.Span, string.Empty);
            }

            return;
        }

        if (method.IsAsync)
        {
            _function.Flavor = FunctionFlavor.Async;
        }
    }

    /// <summary>The type <c>return</c> statements must produce (the T of Task&lt;T&gt; in async functions).</summary>
    private TypeSymbol EffectiveReturnType()
    {
        var declared = _function.ReturnType ?? PrimitiveType.Void;
        if (_function.IsAsync && declared is NamedTypeSymbol n)
        {
            if (ReferenceEquals(n.Definition, _wk.TaskOfT))
            {
                return n.TypeArguments[0];
            }

            if (ReferenceEquals(n.Definition, _wk.Task))
            {
                return PrimitiveType.Void;
            }
        }

        return declared;
    }

    internal static bool ContainsYield(Node? node) => node switch
    {
        null => false,
        YieldReturnStmt or YieldBreakStmt => true,
        LambdaExpr => false,
        BlockStmt b => b.Statements.Any(ContainsYield),
        IfStmt i => ContainsYield(i.Then) || ContainsYield(i.Else),
        WhileStmt w => ContainsYield(w.Body),
        DoWhileStmt d => ContainsYield(d.Body),
        ForStmt f => ContainsYield(f.Body),
        ForeachStmt f => ContainsYield(f.Body),
        SwitchStmt s => s.Sections.SelectMany(x => x.Statements).Any(ContainsYield),
        TryStmt t => ContainsYield(t.Body) || t.Catches.Any(c => ContainsYield(c.Body)) || ContainsYield(t.Finally),
        _ => false,
    };

    private static TextSpan ClosingBrace(BlockStmt body) => new(Math.Max(body.Span.Start, body.Span.End - 1), 1);

    private BoundStatement? BindBaseConstructorCall(ConstructorInitializer? initializer)
    {
        var type = _containingType!;
        var span = initializer?.Span ?? _function.Method.Syntax?.Span ?? default;
        if (initializer is { IsBase: false })
        {
            var self = BindConstructorCall(type, initializer.Arguments, span);
            return self is null ? null : new BoundExpressionStatement(span, self);
        }

        if (type.BaseType is not { } baseType)
        {
            if (initializer is { IsBase: true })
            {
                Report(DiagnosticCodes.NoBaseType, span);
            }

            return null;
        }

        var call = BindConstructorCall(baseType, initializer?.Arguments ?? [], span);
        return call is null ? null : new BoundExpressionStatement(span, call);
    }

    private BoundCall? BindConstructorCall(NamedTypeSymbol type, ImmutableArray<Argument> arguments, TextSpan span)
    {
        var constructors = type.GetDeclaredMembers(type.Name).OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.Constructor).ToImmutableArray();
        if (constructors.IsEmpty && arguments.IsEmpty)
        {
            return null;
        }

        var boundArgs = BindArguments(arguments);
        var resolved = ResolveOverload(constructors, boundArgs, [], span, type.Name, receiverForErrors: type.Name);
        return resolved is null
            ? null
            : new BoundCall(span, new BoundThis(span, _containingType!), resolved.Value.Method, resolved.Value.Arguments, IsBaseCall: true);
    }

    #endregion

    #region Statements

    private BoundBlock BindBlock(BlockStmt block)
    {
        var saved = _scope;
        var savedNonNull = _nonNull;
        _scope = new Scope(_scope, _function);
        var statements = ImmutableArray.CreateBuilder<BoundStatement>(block.Statements.Length);
        foreach (var statement in block.Statements)
        {
            statements.Add(BindStatement(statement));
        }

        _scope = saved;
        _nonNull = savedNonNull;
        return new BoundBlock(block.Span, statements.MoveToImmutable());
    }

    private BoundStatement BindEmbedded(Stmt statement)
    {
        // A non-block statement in an if/loop body still gets its own scope.
        var saved = _scope;
        _scope = new Scope(_scope, _function);
        var bound = BindStatement(statement);
        _scope = saved;
        return bound;
    }

    private BoundStatement BindStatement(Stmt statement)
    {
        switch (statement)
        {
            case BlockStmt block:
                return BindBlock(block);
            case EmptyStmt empty:
                return new BoundEmpty(empty.Span);
            case LocalDeclStmt local:
                return BindLocalDeclaration(local);
            case DeconstructionStmt deconstruction:
                return BindDeconstruction(deconstruction);
            case ExprStmt expr:
                return BindExpressionStatement(expr);
            case IfStmt ifStmt:
                return BindIf(ifStmt);
            case WhileStmt whileStmt:
            {
                var condition = BindCondition(whileStmt.Condition);
                var body = BindLoopBody(whileStmt.Body);
                return new BoundWhile(whileStmt.Span, condition, body, IsDoWhile: false);
            }

            case DoWhileStmt doWhile:
            {
                var body = BindLoopBody(doWhile.Body);
                var condition = BindCondition(doWhile.Condition);
                return new BoundWhile(doWhile.Span, condition, body, IsDoWhile: true);
            }

            case ForStmt forStmt:
                return BindFor(forStmt);
            case ForeachStmt foreachStmt:
                return BindForeach(foreachStmt);
            case SwitchStmt switchStmt:
                return BindSwitchStatement(switchStmt);
            case BreakStmt breakStmt:
                if (_breakableDepth == 0)
                {
                    Report(DiagnosticCodes.JumpOutsideLoop, breakStmt.Span, "break", " or switch");
                }

                return new BoundBreak(breakStmt.Span);
            case ContinueStmt continueStmt:
                if (_loopDepth == 0)
                {
                    Report(DiagnosticCodes.JumpOutsideLoop, continueStmt.Span, "continue", string.Empty);
                }

                return new BoundContinue(continueStmt.Span);
            case ReturnStmt returnStmt:
                return BindReturn(returnStmt);
            case ThrowStmt throwStmt:
                return BindThrow(throwStmt);
            case TryStmt tryStmt:
                return BindTry(tryStmt);
            case YieldReturnStmt yieldReturn:
                return BindYieldReturn(yieldReturn);
            case YieldBreakStmt yieldBreak:
                CheckYieldAllowed(yieldBreak.Span);
                return new BoundYieldBreak(yieldBreak.Span);
            default:
                return new BoundEmpty(statement.Span);
        }
    }

    private BoundStatement BindLocalDeclaration(LocalDeclStmt decl)
    {
        var declaredType = decl.Type is null ? null : ResolveType(decl.Type);
        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        foreach (var declarator in decl.Declarators)
        {
            BoundExpression? initializer = null;
            TypeSymbol type;
            if (declaredType is null)
            {
                if (declarator.Initializer is null)
                {
                    Report(DiagnosticCodes.VarNeedsInitializer, declarator.Span, declarator.Name);
                    type = ErrorTypeSymbol.Instance;
                }
                else
                {
                    initializer = BindValue(BindExpression(declarator.Initializer));
                    type = initializer.Type;
                    if (type is NullTypeSymbol || type == PrimitiveType.Void)
                    {
                        Report(DiagnosticCodes.VarNeedsInitializer, declarator.Span, declarator.Name);
                        type = ErrorTypeSymbol.Instance;
                    }
                }
            }
            else
            {
                type = declaredType;
                if (declarator.Initializer is not null)
                {
                    initializer = BindExpression(declarator.Initializer, type);
                }
                else if (decl.IsConst)
                {
                    Report(DiagnosticCodes.NotConstant, declarator.Span, declarator.Name);
                }
                else if (type.IsReferenceType && type is not NullableTypeSymbol && !type.IsError)
                {
                    Report(DiagnosticCodes.UninitializedLocal, declarator.Span, declarator.Name, type);
                }
            }

            var local = new LocalSymbol(declarator.Name, type, decl.IsConst) { Syntax = declarator, Tree = _tree, Container = _function.Method };
            if (decl.IsConst)
            {
                if (initializer?.ConstantValue is { } constant)
                {
                    local.ConstantValue = constant;
                }
                else if (initializer is not null)
                {
                    Report(DiagnosticCodes.NotConstant, declarator.Span, declarator.Name);
                }
            }

            Declare(local, declarator.Span);
            if (initializer is not null && !type.IsNullable && !type.IsError)
            {
                _nonNull = _nonNull.Add(local);
            }

            statements.Add(new BoundLocalDeclaration(declarator.Span, local, initializer));
        }

        return statements.Count == 1 ? statements[0] : new BoundBlock(decl.Span, statements.ToImmutable());
    }

    private BoundStatement BindDeconstruction(DeconstructionStmt decl)
    {
        var value = BindValue(BindExpression(decl.Value));
        var elementTypes = value.Type is TupleTypeSymbol tuple && tuple.Elements.Length == decl.Names.Length
            ? tuple.Elements
            : default;
        if (elementTypes.IsDefault && !value.Type.IsError)
        {
            Report(DiagnosticCodes.DeconstructionArity, decl.Value.Span, value.Type, decl.Names.Length);
        }

        var locals = ImmutableArray.CreateBuilder<LocalSymbol>();
        for (var i = 0; i < decl.Names.Length; i++)
        {
            var type = elementTypes.IsDefault ? ErrorTypeSymbol.Instance : elementTypes[i];
            var local = new LocalSymbol(decl.Names[i], type, isConst: false) { Syntax = decl, Tree = _tree, Container = _function.Method };
            Declare(local, decl.Span);
            locals.Add(local);
        }

        return new BoundDeconstruction(decl.Span, locals.ToImmutable(), value);
    }

    private BoundStatement BindExpressionStatement(ExprStmt stmt)
    {
        var expression = BindExpression(stmt.Expression);
        if (expression is BoundCall call && IsTask(call.Type))
        {
            Report(DiagnosticCodes.UnawaitedTask, stmt.Expression.Span, call.Method.Name);
        }

        return new BoundExpressionStatement(stmt.Span, expression);
    }

    private BoundStatement BindIf(IfStmt stmt)
    {
        var condition = BindCondition(stmt.Condition);
        var savedNonNull = _nonNull;
        var (whenTrue, whenFalse) = NullNarrowing(condition);
        _nonNull = _nonNull.Union(whenTrue);
        var then = BindEmbedded(stmt.Then);
        _nonNull = savedNonNull.Union(whenFalse);
        var otherwise = stmt.Else is null ? null : BindEmbedded(stmt.Else);
        _nonNull = savedNonNull;

        // 'if (x == null) return;' narrows x for the rest of the block.
        if (stmt.Else is null && !ControlFlow.CanCompleteNormally(then))
        {
            _nonNull = _nonNull.Union(whenFalse);
        }

        return new BoundIf(stmt.Span, condition, then, otherwise);
    }

    private BoundStatement BindFor(ForStmt stmt)
    {
        var saved = _scope;
        _scope = new Scope(_scope, _function);
        var initializer = stmt.Initializer is null ? null : BindStatement(stmt.Initializer);
        var condition = stmt.Condition is null ? null : BindCondition(stmt.Condition);
        var increments = stmt.Iterators.Select(e => BindExpression(e)).ToImmutableArray();
        var body = BindLoopBody(stmt.Body);
        _scope = saved;
        return new BoundFor(stmt.Span, initializer, condition, increments, body);
    }

    private BoundStatement BindLoopBody(Stmt body)
    {
        _loopDepth++;
        _breakableDepth++;
        var bound = BindEmbedded(body);
        _loopDepth--;
        _breakableDepth--;
        return bound;
    }

    private BoundStatement BindForeach(ForeachStmt stmt)
    {
        var collection = BindValue(BindExpression(stmt.Collection));
        var (kind, elementType) = ClassifyForeach(collection.Type, stmt.IsAwait);
        if (kind is null)
        {
            if (!collection.Type.IsError)
            {
                Report(DiagnosticCodes.NotIterable, stmt.Collection.Span, collection.Type);
            }

            elementType = ErrorTypeSymbol.Instance;
        }

        if (stmt.IsAwait && !_function.IsAsync)
        {
            Report(DiagnosticCodes.AwaitOutsideAsync, stmt.Span);
        }

        var variableType = elementType!;
        if (stmt.Type is not null)
        {
            variableType = ResolveType(stmt.Type);
            var conversion = Conversions.Classify(elementType!, variableType);
            if (!conversion.Exists)
            {
                Report(DiagnosticCodes.CannotConvert, stmt.Type.Span, elementType!, variableType);
            }
        }

        var saved = _scope;
        _scope = new Scope(_scope, _function);
        var variable = new LocalSymbol(stmt.Variable, variableType, isConst: false) { Syntax = stmt, Tree = _tree, Container = _function.Method };
        Declare(variable, stmt.Span);
        var body = BindLoopBody(stmt.Body);
        _scope = saved;
        return new BoundForeach(stmt.Span, kind ?? ForeachKind.Iterable, variable, collection, variableType, body);
    }

    private (ForeachKind? Kind, TypeSymbol? Element) ClassifyForeach(TypeSymbol type, bool isAwait)
    {
        if (type.IsError)
        {
            return (null, ErrorTypeSymbol.Instance);
        }

        if (isAwait)
        {
            var asyncElement = FindInterfaceArgument(type, _wk.AsyncIterable);
            return asyncElement is null ? (null, null) : (ForeachKind.AsyncIterable, asyncElement);
        }

        switch (type)
        {
            case ArrayTypeSymbol array:
                return (ForeachKind.Array, array.Element);
            case PrimitiveType { Primitive: PrimitiveKind.String }:
                return (ForeachKind.String, PrimitiveType.Char);
            case PrimitiveType { Primitive: PrimitiveKind.Bytes }:
                return (ForeachKind.Bytes, PrimitiveType.Byte);
        }

        var element = FindInterfaceArgument(type, _wk.Iterable);
        return element is null ? (null, null) : (ForeachKind.Iterable, element);
    }

    private BoundStatement BindSwitchStatement(SwitchStmt stmt)
    {
        var subject = BindValue(BindExpression(stmt.Subject));
        var sections = ImmutableArray.CreateBuilder<BoundSwitchSection>();
        _breakableDepth++;
        foreach (var section in stmt.Sections)
        {
            var saved = _scope;
            _scope = new Scope(_scope, _function);
            var labels = ImmutableArray.CreateBuilder<(BoundPattern?, BoundExpression?)>();
            foreach (var label in section.Labels)
            {
                if (label is CaseLabel caseLabel)
                {
                    var pattern = BindPattern(caseLabel.Pattern, subject.Type);
                    var guard = caseLabel.Guard is null ? null : BindCondition(caseLabel.Guard);
                    labels.Add((pattern, guard));
                }
                else
                {
                    labels.Add((null, null));
                }
            }

            var statements = section.Statements.Select(BindStatement).ToImmutableArray();
            _scope = saved;
            sections.Add(new BoundSwitchSection(section.Span, labels.ToImmutable(), statements));
        }

        _breakableDepth--;
        return new BoundSwitch(stmt.Span, subject, sections.ToImmutable());
    }

    private BoundStatement BindReturn(ReturnStmt stmt)
    {
        if (_function.Flavor is FunctionFlavor.Iterator or FunctionFlavor.AsyncIterator)
        {
            if (stmt.Value is not null)
            {
                Report(DiagnosticCodes.YieldNotAllowed, stmt.Span, "; use 'yield return' in an iterator");
            }

            return new BoundYieldBreak(stmt.Span);
        }

        // Lambdas without a known return type infer it from their return statements.
        if (_function.ReturnType is null)
        {
            if (stmt.Value is null)
            {
                return new BoundReturn(stmt.Span, null);
            }

            var inferred = BindValue(BindExpression(stmt.Value));
            _function.InferredReturnTypes.Add(inferred.Type);
            _function.SawReturnValue = true;
            return new BoundReturn(stmt.Span, inferred);
        }

        var expected = EffectiveReturnType();
        if (stmt.Value is null)
        {
            if (expected != PrimitiveType.Void && !expected.IsError)
            {
                Report(DiagnosticCodes.MissingReturnValue, stmt.Span, _function.Method.Name, expected);
            }

            return new BoundReturn(stmt.Span, null);
        }

        if (expected == PrimitiveType.Void)
        {
            Report(DiagnosticCodes.ReturnValueInVoid, stmt.Value.Span, _function.Method.Name);
            return new BoundReturn(stmt.Span, BindExpression(stmt.Value));
        }

        return new BoundReturn(stmt.Span, BindExpression(stmt.Value, expected));
    }

    private BoundStatement BindThrow(ThrowStmt stmt)
    {
        if (stmt.Value is null)
        {
            if (_catchDepth == 0)
            {
                Report(DiagnosticCodes.Expected, stmt.Span, "an error to throw (a bare 'throw;' is only valid in a catch block)", "throw");
            }

            return new BoundThrow(stmt.Span, null);
        }

        return new BoundThrow(stmt.Span, BindErrorValue(stmt.Value));
    }

    private BoundExpression BindErrorValue(Expr expr)
    {
        var value = BindValue(BindExpression(expr));
        if (!value.Type.IsError && (_wk.Error is null || value.Type is not NamedTypeSymbol named || !named.IsSubtypeOf(_wk.Error)))
        {
            Report(DiagnosticCodes.NotAnError, expr.Span, value.Type);
        }

        return value;
    }

    private BoundStatement BindTry(TryStmt stmt)
    {
        var body = BindBlock(stmt.Body);
        var catches = ImmutableArray.CreateBuilder<BoundCatch>();
        foreach (var clause in stmt.Catches)
        {
            var errorType = _wk.Error!;
            if (clause.Type is not null)
            {
                var resolved = ResolveType(clause.Type);
                if (resolved is NamedTypeSymbol named && named.IsSubtypeOf(_wk.Error!))
                {
                    errorType = named;
                    if (named.Definition.Syntax is TypeDecl decl && decl.Attributes.Any(a => a.Name == "Uncatchable"))
                    {
                        Report(DiagnosticCodes.UncatchableError, clause.Type.Span, named);
                    }
                }
                else if (!resolved.IsError)
                {
                    Report(DiagnosticCodes.NotAnError, clause.Type.Span, resolved);
                }
            }

            var saved = _scope;
            _scope = new Scope(_scope, _function);
            LocalSymbol? variable = null;
            if (clause.Variable is not null)
            {
                variable = new LocalSymbol(clause.Variable, errorType, isConst: false) { Syntax = clause, Tree = _tree, Container = _function.Method };
                Declare(variable, clause.Span);
                _nonNull = _nonNull.Add(variable);
            }

            _catchDepth++;
            var catchBody = BindBlock(clause.Body);
            _catchDepth--;
            _scope = saved;
            catches.Add(new BoundCatch(clause.Span, errorType, variable, catchBody));
        }

        var finallyBlock = stmt.Finally is null ? null : BindBlock(stmt.Finally);
        return new BoundTry(stmt.Span, body, catches.ToImmutable(), finallyBlock);
    }

    private BoundStatement BindYieldReturn(YieldReturnStmt stmt)
    {
        if (!CheckYieldAllowed(stmt.Span))
        {
            return new BoundYieldReturn(stmt.Span, BindExpression(stmt.Value));
        }

        return new BoundYieldReturn(stmt.Span, BindExpression(stmt.Value, _function.ElementType!));
    }

    private bool CheckYieldAllowed(TextSpan span)
    {
        _function.SawYield = true;
        if (_function.Flavor is FunctionFlavor.Iterator or FunctionFlavor.AsyncIterator)
        {
            return true;
        }

        if (_function.Method.MethodKind == MethodKind.Lambda)
        {
            Report(DiagnosticCodes.YieldNotAllowed, span, "; lambdas cannot be iterators");
        }

        return false;
    }

    #endregion

    #region Shared helpers

    private void Declare(Symbol symbol, TextSpan span)
    {
        if (!_scope.TryDeclare(symbol))
        {
            Report(DiagnosticCodes.DuplicateLocal, span, symbol.Name);
        }

        _owners[symbol] = _function;
    }

    private TypeSymbol ResolveType(TypeSyntax syntax, bool allowVoid = false) =>
        new TypeResolver(_diagnostics, _tree).Resolve(new TypeScope(_module, _containingType, OutermostMethod()), syntax, allowVoid);

    private MethodSymbol OutermostMethod()
    {
        var f = _function;
        while (f.Parent is not null)
        {
            f = f.Parent;
        }

        return f.Method;
    }

    private bool IsTask(TypeSymbol type) =>
        type is NamedTypeSymbol n && (ReferenceEquals(n.Definition, _wk.Task) || ReferenceEquals(n.Definition, _wk.TaskOfT));

    /// <summary>Finds T when <paramref name="type"/> is or implements <c>definition&lt;T&gt;</c>.</summary>
    private TypeSymbol? FindInterfaceArgument(TypeSymbol type, NamedTypeSymbol? definition)
    {
        if (definition is null)
        {
            return null;
        }

        foreach (var candidate in SelfAndInterfaces(type))
        {
            if (ReferenceEquals(candidate.Definition, definition) && candidate.TypeArguments.Length == 1)
            {
                return candidate.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>The named type itself (or a primitive's member source) plus every interface and base type it inherits.</summary>
    internal IEnumerable<NamedTypeSymbol> SelfAndInterfaces(TypeSymbol type)
    {
        var start = MemberSourceOf(type);
        if (start is null)
        {
            if (type is TypeParameterSymbol tp)
            {
                foreach (var c in tp.Constraints.OfType<NamedTypeSymbol>())
                {
                    foreach (var t in SelfAndInterfaces(c))
                    {
                        yield return t;
                    }
                }
            }

            yield break;
        }

        var seen = new HashSet<NamedTypeSymbol>();
        var stack = new Stack<NamedTypeSymbol>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!seen.Add(t))
            {
                continue;
            }

            yield return t;
            if (t.BaseType is { } b)
            {
                stack.Push(b);
            }

            foreach (var i in t.Interfaces)
            {
                stack.Push(i);
            }
        }
    }

    /// <summary>The named type whose members apply to <paramref name="type"/> (primitives and arrays map to Core declarations).</summary>
    internal NamedTypeSymbol? MemberSourceOf(TypeSymbol type) => type.StripNullable() switch
    {
        NamedTypeSymbol n => n,
        PrimitiveType p => p.MemberSource,
        ArrayTypeSymbol a => _wk.ArrayMembers?.Construct([a.Element]),
        _ => null,
    };

    private void Report(DiagnosticDescriptor descriptor, TextSpan span, params object[] args) =>
        _diagnostics.Report(descriptor, _tree.Source, span, args);

    #endregion
}
