using System.Collections.Immutable;
using Ghostrig.CScript.Natives;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;

namespace Ghostrig.CScript.CodeGen;

/// <summary>
/// A linked, executable program: every reachable function compiled to bytecode, runtime classes with virtual tables,
/// type tests, global slots and module initializers in dependency order.
/// </summary>
public sealed class ProgramImage : RuntimeTypeResolver
{
    internal ProgramImage(Linker linker)
    {
        Linker = linker;
    }

    internal Linker Linker { get; }

    public IReadOnlyList<Callable> Callables => Linker.Callables;
    public IReadOnlyList<RuntimeClass> Classes => Linker.Classes;
    public IReadOnlyList<TypeTest> TypeTests => Linker.TypeTests;
    public int GlobalCount => Linker.GlobalCount;

    /// <summary>Global initializers, one per user module, ordered so imported modules initialize first.</summary>
    public ImmutableArray<CodeUnit> Initializers { get; internal set; } = [];

    /// <summary>Link problems (e.g. a native declaration with no host implementation).</summary>
    public ImmutableArray<string> LinkErrors { get; internal set; } = [];

    public RuntimeClass ObjectClass => Linker.ObjectClass;
    public RuntimeClass GeneratorClass => Linker.GeneratorClass;
    public RuntimeClass NativeIteratorClass => Linker.NativeIteratorClass;
    public RuntimeClass TaskClass => Linker.TaskClass;
    public RuntimeClass TaskOfTClass => Linker.TaskOfTClass;

    public int ToStringSlot => Linker.ToStringSlot;

    /// <summary>The native registry this image was linked against (reused when hot-swapping).</summary>
    public NativeRegistry Natives { get; internal set; } = NativeRegistry.Default;

    /// <summary>The global slot of a module-level or static field.</summary>
    public int GlobalSlot(FieldSymbol field) => Linker.GlobalSlot(field);

    /// <summary>Looks up a module-level function by module and name (first overload).</summary>
    public Callable? FindFunction(string module, string name)
    {
        var symbol = Linker.Compilation.GetModule(module)?.Lookup(name).OfType<MethodSymbol>().FirstOrDefault();
        return symbol is null ? null : Linker.GetCallable(symbol);
    }

    public Callable GetCallable(MethodSymbol method) => Linker.GetCallable(method);

    /// <summary>A Core error class by name (<c>IndexError</c>, <c>NullError</c>, …).</summary>
    public RuntimeClass ErrorClass(string name) => Linker.CoreClass(name);

    public RuntimeClass CoreClass(string name) => Linker.CoreClass(name);

    public override RuntimeClass? ClassOf(Value value) => value.Tag switch
    {
        ValueTag.Null => null,
        ValueTag.Bool => Linker.PrimitiveClass(PrimitiveType.Bool),
        ValueTag.Int => Linker.PrimitiveClass(PrimitiveType.Int),
        ValueTag.Float => Linker.PrimitiveClass(PrimitiveType.Float),
        ValueTag.Char => Linker.PrimitiveClass(PrimitiveType.Char),
        ValueTag.Byte => Linker.PrimitiveClass(PrimitiveType.Byte),
        ValueTag.Duration => Linker.PrimitiveClass(PrimitiveType.Duration),
        ValueTag.Instant => Linker.PrimitiveClass(PrimitiveType.Instant),
        ValueTag.Enum => (RuntimeClass)value.Object!,
        _ => value.Object switch
        {
            string => Linker.PrimitiveClass(PrimitiveType.String),
            BytesObject => Linker.PrimitiveClass(PrimitiveType.Bytes),
            ArrayObject => Linker.ArrayClass,
            ObjectInstance o => o.Class,
            TaskObject t => t.Class,
            NativeObject n => n.Class,
            GeneratorObject => Linker.GeneratorClass,
            _ => Linker.ObjectClass,
        },
    };

    /// <summary>Produces a ready-to-throw Core error instance.</summary>
    public Value CreateError(string className, string message)
    {
        var instance = ErrorClass(className).CreateInstance();
        instance.NativeState = new ErrorState(ErrorCodes.For(className), message);
        return Value.FromObject(instance);
    }

    public static ProgramImage Link(Compilation compilation, NativeRegistry? natives = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (compilation.HasErrors)
        {
            throw new InvalidOperationException("Cannot link a compilation with errors:\n" + string.Join("\n", compilation.Diagnostics.Where(d => d.IsError)));
        }

        var image = new Linker(compilation, natives ?? NativeRegistry.Default).Link();
        image.Natives = natives ?? NativeRegistry.Default;
        return image;
    }
}

/// <summary>Suspended iterator state created by calling an iterator function (Doc 7 §5.5).</summary>
public sealed class GeneratorObject(CodeUnit code)
{
    public CodeUnit Code { get; } = code;
    public Vm.Frame? Frame { get; set; }
    public Value Current { get; set; }
    public bool Started { get; set; }
    public bool Finished { get; set; }
    public override string ToString() => $"iterator {Code.Name}";
}
