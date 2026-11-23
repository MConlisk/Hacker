using System.Collections.Immutable;
using Ghostrig.CScript.Natives;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;

namespace Ghostrig.CScript.CodeGen;

/// <summary>
/// Builds a <see cref="ProgramImage"/>: compiles reachable functions on demand (worklist, no recursion), creates runtime
/// classes with virtual tables, assigns method slots and global offsets, and binds native declarations to host code.
/// </summary>
internal sealed class Linker
{
    private readonly NativeRegistry _natives;
    private readonly Dictionary<MethodSymbol, Callable> _callables = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Callable, int> _callableIndex = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<NamedTypeSymbol, RuntimeClass> _classes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RuntimeClass, int> _classIndex = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MethodSymbol, int> _slots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _typeTestIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<ModuleSymbol, int> _globalBase = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MethodSymbol, BoundFunction> _bodies = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<(CodeUnit Unit, Action Compile)> _worklist = new();
    private readonly List<string> _errors = [];
    private readonly ModuleSymbol _core;

    public Linker(Compilation compilation, NativeRegistry natives)
    {
        Compilation = compilation;
        _natives = natives;
        _core = compilation.GetModule("Core") ?? throw new InvalidOperationException("The Core module is missing.");

        foreach (var (method, body) in compilation.Program.Functions)
        {
            _bodies[method] = body;
        }

        foreach (var lambda in compilation.Program.Lambdas)
        {
            _bodies[lambda.Method] = lambda;
        }

        var offset = 0;
        foreach (var module in compilation.AllModules.Reverse())
        {
            _globalBase[module] = offset;
            offset += module.GlobalCount;
        }

        GlobalCount = offset;
        ObjectClass = GetClass(PrimitiveType.Object.MemberSource!);
        ToStringSlot = SlotOf(PrimitiveType.Object.MemberSource!.GetDeclaredMembers("ToString").OfType<MethodSymbol>().Single());
        ArrayClass = GetClass(compilation.WellKnown.ArrayMembers!);
        TaskClass = GetClass(compilation.WellKnown.Task!);
        TaskOfTClass = GetClass(compilation.WellKnown.TaskOfT!);
        GeneratorClass = BuildGeneratorClass();
        AsyncGeneratorClass = BuildAsyncGeneratorClass();
        NativeIteratorClass = BuildNativeIteratorClass();
    }

    public Compilation Compilation { get; }
    public List<Callable> Callables { get; } = [];
    public List<RuntimeClass> Classes { get; } = [];
    public List<TypeTest> TypeTests { get; } = [];
    public int GlobalCount { get; }

    public RuntimeClass ObjectClass { get; }
    public RuntimeClass ArrayClass { get; }
    public RuntimeClass TaskClass { get; }
    public RuntimeClass TaskOfTClass { get; }
    public RuntimeClass GeneratorClass { get; }
    public RuntimeClass AsyncGeneratorClass { get; }
    public RuntimeClass NativeIteratorClass { get; }
    public int ToStringSlot { get; }

    public ProgramImage Link()
    {
        var image = new ProgramImage(this);
        var initializers = new List<CodeUnit>();
        foreach (var module in InitializationOrder())
        {
            initializers.Add(CompileInitializer(module));
            foreach (var method in module.Members.OfType<MethodSymbol>())
            {
                GetCallable(method);
            }

            foreach (var type in module.Members.OfType<NamedTypeSymbol>())
            {
                GetClass(type);
            }
        }

        Drain();
        image.Initializers = [.. initializers];
        image.LinkErrors = [.. _errors];
        return image;
    }

    private void Drain()
    {
        while (_worklist.TryDequeue(out var item))
        {
            item.Compile();
        }
    }

    private IEnumerable<ModuleSymbol> InitializationOrder()
    {
        var ordered = new List<ModuleSymbol>();
        var visited = new HashSet<ModuleSymbol>(ReferenceEqualityComparer.Instance);

        void Visit(ModuleSymbol module)
        {
            if (!visited.Add(module) || module.IsNative || !Compilation.Modules.Contains(module))
            {
                return;
            }

            foreach (var import in module.Imports)
            {
                Visit(import);
            }

            ordered.Add(module);
        }

        foreach (var module in Compilation.Modules)
        {
            Visit(module);
        }

        return ordered;
    }

    #region Indices

    public int CallableIndex(Callable callable)
    {
        if (!_callableIndex.TryGetValue(callable, out var index))
        {
            index = Callables.Count;
            Callables.Add(callable);
            _callableIndex[callable] = index;
        }

        return index;
    }

    public int ClassIndex(RuntimeClass runtimeClass)
    {
        if (!_classIndex.TryGetValue(runtimeClass, out var index))
        {
            index = Classes.Count;
            Classes.Add(runtimeClass);
            _classIndex[runtimeClass] = index;
        }

        return index;
    }

    public int GlobalSlot(FieldSymbol field)
    {
        var module = field.Container switch
        {
            ModuleSymbol m => m,
            NamedTypeSymbol t => t.Module!,
            _ => throw new InvalidOperationException($"Field {field.Name} has no module."),
        };
        return _globalBase[module] + field.OriginalDefinition.Slot;
    }

    /// <summary>The virtual dispatch slot of an instance method: its root definition (Doc 7: virtual by default).</summary>
    public int SlotOf(MethodSymbol method)
    {
        var root = method.OriginalDefinition;
        while (root.Overridden is { } overridden)
        {
            root = overridden.OriginalDefinition;
        }

        root = ObjectMemberFor(root) ?? root;
        if (!_slots.TryGetValue(root, out var slot))
        {
            slot = _slots.Count;
            _slots[root] = slot;
        }

        return slot;
    }

    /// <summary>Methods shaped like object.ToString/HashCode/Equals override object's slots implicitly.</summary>
    private static MethodSymbol? ObjectMemberFor(MethodSymbol method)
    {
        var objectMembers = PrimitiveType.Object.MemberSource;
        if (objectMembers is null || method.IsStatic || method.MethodKind != MethodKind.Method || ReferenceEquals(method.Container, objectMembers))
        {
            return null;
        }

        return method.Name switch
        {
            "ToString" when method.Parameters.IsEmpty => objectMembers.GetDeclaredMembers("ToString").OfType<MethodSymbol>().Single(),
            "HashCode" when method.Parameters.IsEmpty => objectMembers.GetDeclaredMembers("HashCode").OfType<MethodSymbol>().Single(),
            _ => null,
        };
    }

    public int TypeTestIndex(TypeSymbol type)
    {
        var test = TypeTestFor(type);
        if (!_typeTestIndex.TryGetValue(test.Display, out var index))
        {
            index = TypeTests.Count;
            TypeTests.Add(test);
            _typeTestIndex[test.Display] = index;
        }

        return index;
    }

    public TypeTest TypeTestFor(TypeSymbol type) => type switch
    {
        NullableTypeSymbol n => new TypeTest(TypeTestKind.Nullable, n.ToString(), Inner: TypeTestFor(n.Underlying)),
        PrimitiveType p => p.Primitive switch
        {
            PrimitiveKind.Bool => new TypeTest(TypeTestKind.Bool, "bool"),
            PrimitiveKind.Int => new TypeTest(TypeTestKind.Int, "int"),
            PrimitiveKind.Float => new TypeTest(TypeTestKind.Float, "float"),
            PrimitiveKind.Char => new TypeTest(TypeTestKind.Char, "char"),
            PrimitiveKind.Byte => new TypeTest(TypeTestKind.Byte, "byte"),
            PrimitiveKind.Duration => new TypeTest(TypeTestKind.Duration, "Duration"),
            PrimitiveKind.Instant => new TypeTest(TypeTestKind.Instant, "Instant"),
            PrimitiveKind.String => new TypeTest(TypeTestKind.String, "string"),
            PrimitiveKind.Bytes => new TypeTest(TypeTestKind.Bytes, "bytes"),
            _ => new TypeTest(TypeTestKind.Object, "object"),
        },
        ArrayTypeSymbol => new TypeTest(TypeTestKind.Array, "array"),
        TupleTypeSymbol => new TypeTest(TypeTestKind.Tuple, "tuple"),
        FunctionTypeSymbol => new TypeTest(TypeTestKind.Function, "fn"),
        NamedTypeSymbol { TypeKind: TypeKind.Interface } i => new TypeTest(TypeTestKind.Interface, i.Definition.Name, Interface: i.Definition),
        NamedTypeSymbol { TypeKind: TypeKind.Enum } e => new TypeTest(TypeTestKind.Enum, e.Definition.Name, Class: GetClass(e.Definition)),
        NamedTypeSymbol n when ReferenceEquals(n.Definition, Compilation.WellKnown.Task) || ReferenceEquals(n.Definition, Compilation.WellKnown.TaskOfT) =>
            new TypeTest(TypeTestKind.Task, "Task"),
        NamedTypeSymbol n => new TypeTest(TypeTestKind.Class, $"{n.Definition.Module?.Name}.{n.Definition.Name}", Class: GetClass(n.Definition)),
        _ => new TypeTest(TypeTestKind.Object, "object"),
    };

    #endregion

    #region Classes

    public RuntimeClass CoreClass(string name) =>
        GetClass(_core.Lookup(name).OfType<NamedTypeSymbol>().FirstOrDefault()
                 ?? throw new InvalidOperationException($"Core type '{name}' not found."));

    public RuntimeClass PrimitiveClass(PrimitiveType primitive) => GetClass(primitive.MemberSource ?? PrimitiveType.Object.MemberSource!);

    public RuntimeClass GetClass(NamedTypeSymbol type)
    {
        var definition = type.Definition;
        if (_classes.TryGetValue(definition, out var existing))
        {
            return existing;
        }

        RuntimeClass? baseClass = null;
        if (definition.BaseType is { } baseType)
        {
            baseClass = GetClass(baseType.Definition);
        }
        else if (!ReferenceEquals(definition, PrimitiveType.Object.MemberSource) && definition.TypeKind != TypeKind.Interface)
        {
            baseClass = GetClass(PrimitiveType.Object.MemberSource!);
        }

        var runtimeClass = new RuntimeClass(definition.Name, definition, baseClass);
        _classes[definition] = runtimeClass;
        ClassIndex(runtimeClass);
        LayoutFields(runtimeClass, definition);
        BuildVirtualTable(runtimeClass, definition);
        return runtimeClass;
    }

    private void LayoutFields(RuntimeClass runtimeClass, NamedTypeSymbol definition)
    {
        if (definition.TypeKind == TypeKind.Enum)
        {
            foreach (var member in definition.Members.OfType<FieldSymbol>())
            {
                runtimeClass.AddEnumName((long)member.ConstantValue!, member.Name);
            }

            return;
        }

        var count = definition.InstanceFieldCount;
        var defaults = new Value[count];
        if (runtimeClass.Base is { } b && b.FieldCount > 0)
        {
            Array.Copy(b.FieldDefaults, defaults, Math.Min(b.FieldCount, count));
        }

        foreach (var member in definition.Members)
        {
            var field = member switch
            {
                FieldSymbol { IsStatic: false } f => f,
                PropertySymbol { BackingField: { IsStatic: false } backing } => backing,
                _ => null,
            };
            if (field is not null && field.Slot >= 0 && field.Slot < count)
            {
                defaults[field.Slot] = DefaultValue(field.Type);
            }
        }

        runtimeClass.FieldCount = count;
        runtimeClass.FieldDefaults = defaults;
    }

    /// <summary>Zero value of a type: 0, 0.0, false, null, the first enum value or a zeroed struct.</summary>
    public Value DefaultValue(TypeSymbol type) => type switch
    {
        PrimitiveType { Primitive: PrimitiveKind.Bool } => Value.False,
        PrimitiveType { Primitive: PrimitiveKind.Int } => Value.FromInt(0),
        PrimitiveType { Primitive: PrimitiveKind.Float } => Value.FromFloat(0),
        PrimitiveType { Primitive: PrimitiveKind.Char } => Value.FromChar(0),
        PrimitiveType { Primitive: PrimitiveKind.Byte } => Value.FromByte(0),
        PrimitiveType { Primitive: PrimitiveKind.Duration } => Value.FromDuration(0),
        PrimitiveType { Primitive: PrimitiveKind.Instant } => Value.FromInstant(0),
        NamedTypeSymbol { TypeKind: TypeKind.Enum } e => Value.FromEnum(0, GetClass(e.Definition)),
        NamedTypeSymbol { TypeKind: TypeKind.Struct, IsNative: false } s => Value.FromObject(GetClass(s.Definition).CreateInstance()),
        _ => Value.Null,
    };

    private void BuildVirtualTable(RuntimeClass runtimeClass, NamedTypeSymbol definition)
    {
        if (definition.TypeKind is TypeKind.Interface or TypeKind.Enum)
        {
            return;
        }

        foreach (var member in definition.Members)
        {
            foreach (var method in InstanceMethodsOf(member))
            {
                runtimeClass.VTable[SlotOf(method)] = GetCallable(method);
            }
        }

        foreach (var iface in AllInterfaces(definition))
        {
            runtimeClass.Interfaces.Add(iface.Definition);
            foreach (var required in iface.Members)
            {
                foreach (var (interfaceMethod, implementation) in FindImplementations(definition, required))
                {
                    runtimeClass.VTable[SlotOf(interfaceMethod)] = GetCallable(implementation);
                }
            }
        }
    }

    private static IEnumerable<MethodSymbol> InstanceMethodsOf(Symbol member) => member switch
    {
        MethodSymbol { IsStatic: false, MethodKind: MethodKind.Method } m => [m],
        PropertySymbol p => new[] { p.Getter, p.Setter }.Where(a => a is { IsStatic: false }).Cast<MethodSymbol>(),
        _ => [],
    };

    private static IEnumerable<NamedTypeSymbol> AllInterfaces(NamedTypeSymbol type)
    {
        var seen = new HashSet<NamedTypeSymbol>();
        var stack = new Stack<NamedTypeSymbol>();
        for (NamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            foreach (var i in t.Interfaces)
            {
                stack.Push(i);
            }
        }

        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (seen.Add(i))
            {
                yield return i;
                foreach (var parent in i.Interfaces)
                {
                    stack.Push(parent);
                }
            }
        }
    }

    private static IEnumerable<(MethodSymbol Interface, MethodSymbol Implementation)> FindImplementations(NamedTypeSymbol type, Symbol required)
    {
        for (NamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            switch (required)
            {
                case MethodSymbol { IsStatic: false } m:
                {
                    var impl = t.GetDeclaredMembers(m.Name).OfType<MethodSymbol>()
                        .FirstOrDefault(c => !c.IsStatic && DeclarationBinder.SameSignature(c, m));
                    if (impl is not null)
                    {
                        yield return (m, impl);
                        yield break;
                    }

                    break;
                }

                case PropertySymbol p:
                {
                    var impl = t.GetDeclaredMembers(p.Name).OfType<PropertySymbol>().FirstOrDefault();
                    if (impl is not null)
                    {
                        if (p.Getter is not null && impl.Getter is not null)
                        {
                            yield return (p.Getter, impl.Getter);
                        }

                        if (p.Setter is not null && impl.Setter is not null)
                        {
                            yield return (p.Setter, impl.Setter);
                        }

                        yield break;
                    }

                    break;
                }

                default:
                    yield break;
            }
        }
    }

    private RuntimeClass BuildGeneratorClass()
    {
        var generator = new RuntimeClass("Iterator", null, ObjectClass);
        var wk = Compilation.WellKnown;
        generator.Interfaces.Add(wk.Iterable!);
        generator.Interfaces.Add(wk.Iterator!);
        var getIterator = wk.Iterable!.GetDeclaredMembers("GetIterator").OfType<MethodSymbol>().Single();
        var moveNext = wk.Iterator!.GetDeclaredMembers("MoveNext").OfType<MethodSymbol>().Single();
        var current = wk.Iterator.GetDeclaredMembers("Current").OfType<PropertySymbol>().Single().Getter!;
        generator.VTable[SlotOf(getIterator)] = Register(new IntrinsicCallable("GetIterator", 1, IntrinsicKind.GeneratorGetIterator));
        generator.VTable[SlotOf(moveNext)] = Register(new IntrinsicCallable("MoveNext", 1, IntrinsicKind.GeneratorMoveNext));
        generator.VTable[SlotOf(current)] = Register(new IntrinsicCallable("get_Current", 1, IntrinsicKind.GeneratorCurrent));
        ClassIndex(generator);
        return generator;
    }

    /// <summary>Runtime class of async iterator objects: both the AsyncIterable and its AsyncIterator.</summary>
    private RuntimeClass BuildAsyncGeneratorClass()
    {
        var generator = new RuntimeClass("AsyncIterator", null, ObjectClass);
        var iterable = Compilation.WellKnown.AsyncIterable!;
        var getIterator = iterable.GetDeclaredMembers("GetAsyncIterator").OfType<MethodSymbol>().Single();
        var iterator = ((NamedTypeSymbol)getIterator.ReturnType).Definition;
        var moveNext = iterator.GetDeclaredMembers("MoveNextAsync").OfType<MethodSymbol>().Single();
        var current = iterator.GetDeclaredMembers("Current").OfType<PropertySymbol>().Single().Getter!;
        generator.Interfaces.Add(iterable);
        generator.Interfaces.Add(iterator);
        generator.VTable[SlotOf(getIterator)] = Register(new NativeCallable("GetAsyncIterator", 1, true, (_, a) => a[0], "<async-iterator>.GetAsyncIterator"));
        generator.VTable[SlotOf(moveNext)] = Register(new NativeCallable("MoveNextAsync", 1, true,
            (ctx, a) => Value.FromObject(ctx.MoveNextAsync(a[0].As<AsyncGeneratorObject>())), "<async-iterator>.MoveNextAsync"));
        generator.VTable[SlotOf(current)] = Register(new NativeCallable("get_Current", 1, true,
            (_, a) => a[0].As<AsyncGeneratorObject>().Current, "<async-iterator>.Current"));
        ClassIndex(generator);
        return generator;
    }

    /// <summary>Runtime class for host-implemented iterators (<see cref="NativeIterator"/>).</summary>
    private RuntimeClass BuildNativeIteratorClass()
    {
        var iterator = new RuntimeClass("Iterator", null, ObjectClass);
        var wk = Compilation.WellKnown;
        iterator.Interfaces.Add(wk.Iterable!);
        iterator.Interfaces.Add(wk.Iterator!);
        var getIterator = wk.Iterable!.GetDeclaredMembers("GetIterator").OfType<MethodSymbol>().Single();
        var moveNext = wk.Iterator!.GetDeclaredMembers("MoveNext").OfType<MethodSymbol>().Single();
        var current = wk.Iterator.GetDeclaredMembers("Current").OfType<PropertySymbol>().Single().Getter!;
        iterator.VTable[SlotOf(getIterator)] = Register(new NativeCallable("GetIterator", 1, true, (_, a) => a[0], "<iterator>.GetIterator"));
        iterator.VTable[SlotOf(moveNext)] = Register(new NativeCallable("MoveNext", 1, true,
            (ctx, a) => Value.FromBool(((NativeIterator)a[0].As<NativeObject>().State).MoveNext(ctx)), "<iterator>.MoveNext"));
        iterator.VTable[SlotOf(current)] = Register(new NativeCallable("get_Current", 1, true,
            (_, a) => ((NativeIterator)a[0].As<NativeObject>().State).Current, "<iterator>.Current"));
        ClassIndex(iterator);
        return iterator;
    }

    private Callable Register(Callable callable)
    {
        CallableIndex(callable);
        return callable;
    }

    #endregion

    #region Callables

    public Callable GetCallable(MethodSymbol method)
    {
        var definition = method.OriginalDefinition;
        if (_callables.TryGetValue(definition, out var existing))
        {
            return existing;
        }

        var argumentCount = definition.Parameters.Length + (definition.IsStatic ? 0 : 1);
        Callable callable;
        if (definition.IsNative)
        {
            callable = BindNative(definition, argumentCount);
        }
        else if (definition.IsAbstract)
        {
            callable = new NativeCallable(definition.Name, argumentCount, true,
                (_, _) => throw new InvalidOperationException($"Abstract member {definition.Name} invoked directly."), "<abstract>");
        }
        else
        {
            var unit = new CodeUnit(DisplayName(definition), argumentCount, !definition.IsStatic || CapturesThis(definition), definition)
            {
                SourcePath = definition.Tree?.Source.Path ?? "<synthesized>",
            };
            callable = unit;
            _worklist.Enqueue((unit, () => CompileFunction(unit, definition)));
        }

        callable.ReturnsValue = definition.ReturnType != PrimitiveType.Void;
        _callables[definition] = callable;
        CallableIndex(callable);
        return callable;
    }

    private bool CapturesThis(MethodSymbol method) => method.MethodKind == MethodKind.Lambda && _bodies.TryGetValue(method, out var b) && b.CapturesThis;

    private static string DisplayName(MethodSymbol method) => method.Container switch
    {
        NamedTypeSymbol t => $"{t.Name}.{method.Name}",
        ModuleSymbol m => $"{m.Name}.{method.Name}",
        MethodSymbol outer => $"{DisplayName(outer)}.{method.Name}",
        _ => method.Name,
    };

    private Callable BindNative(MethodSymbol method, int argumentCount)
    {
        var key = method.NativeKey;
        if (_natives.TryGet(key, out var function))
        {
            return new NativeCallable(DisplayName(method), argumentCount, !method.IsStatic, function, key);
        }

        _errors.Add($"No native implementation for '{key}'.");
        return new NativeCallable(DisplayName(method), argumentCount, !method.IsStatic,
            (_, _) => throw new ScriptErrorRequest("Error", $"'{key}' is not implemented by this host."), key);
    }

    private void CompileFunction(CodeUnit unit, MethodSymbol method)
    {
        if (_bodies.TryGetValue(method, out var body))
        {
            new FunctionCompiler(this, unit, method, body).Compile();
            return;
        }

        // Auto-property accessors are synthesized here.
        if (method.MethodKind is MethodKind.PropertyGetter or MethodKind.PropertySetter
            && method.Container is NamedTypeSymbol type
            && type.GetDeclaredMembers(method.Name[4..]).OfType<PropertySymbol>().FirstOrDefault() is { BackingField: { } backing })
        {
            FunctionCompiler.CompileAutoAccessor(this, unit, method, backing);
            return;
        }

        throw new InvalidOperationException($"No body was bound for '{DisplayName(method)}'.");
    }

    private CodeUnit CompileInitializer(ModuleSymbol module)
    {
        var symbol = new MethodSymbol("<module-init>", MethodKind.Function) { Container = module, Tree = module.Tree };
        var unit = new CodeUnit($"{module.Name}.<init>", 0, false, symbol) { SourcePath = module.Tree?.Source.Path ?? "<module>" };
        var initializers = Compilation.Program.GlobalInitializers.GetValueOrDefault(module, []);
        FunctionCompiler.CompileModuleInitializer(this, unit, symbol, initializers);
        CallableIndex(unit);
        return unit;
    }

    public ImmutableArray<(Symbol Member, BoundExpression Value)> FieldInitializers(NamedTypeSymbol type) =>
        Compilation.Program.FieldInitializers.GetValueOrDefault(type.Definition, []);

    public BoundFunction? LambdaBody(MethodSymbol method) => _bodies.GetValueOrDefault(method);

    private readonly Dictionary<MethodSymbol, ImmutableArray<Symbol>> _captures = new(ReferenceEqualityComparer.Instance);

    /// <summary>Outer variables a lambda references (directly or via nested lambdas), in first-use order.</summary>
    public ImmutableArray<Symbol> CapturesOf(BoundLambda lambda)
    {
        if (!_captures.TryGetValue(lambda.Function, out var captures))
        {
            captures = BoundTreeWalker.FreeVariables(lambda);
            _captures[lambda.Function] = captures;
        }

        return captures;
    }

    #endregion
}
