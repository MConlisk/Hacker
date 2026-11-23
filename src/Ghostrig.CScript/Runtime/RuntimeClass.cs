using Ghostrig.CScript.Semantics;

namespace Ghostrig.CScript.Runtime;

/// <summary>
/// Runtime description of a class, struct, enum or primitive member table: field layout, default field values and the
/// virtual dispatch table keyed by method slot (instance methods are virtual by default, Doc 7 §4.3).
/// </summary>
public sealed class RuntimeClass
{
    private readonly Dictionary<long, string> _enumNames = [];

    public RuntimeClass(string name, NamedTypeSymbol? symbol, RuntimeClass? baseClass)
    {
        Name = name;
        Symbol = symbol;
        Base = baseClass;
        if (baseClass is not null)
        {
            foreach (var (slot, callable) in baseClass.VTable)
            {
                VTable[slot] = callable;
            }

            foreach (var i in baseClass.Interfaces)
            {
                Interfaces.Add(i);
            }
        }
    }

    public string Name { get; }

    /// <summary>The declaring type definition (null for synthetic runtime classes).</summary>
    public NamedTypeSymbol? Symbol { get; }

    public RuntimeClass? Base { get; }

    public bool IsStruct => Symbol?.TypeKind == TypeKind.Struct;

    public bool IsEnum => Symbol?.TypeKind == TypeKind.Enum;

    public int FieldCount { get; internal set; }

    /// <summary>Initial field values (zero/false/null by type; nested structs are zeroed templates cloned per instance).</summary>
    public Value[] FieldDefaults { get; internal set; } = [];

    public Dictionary<int, Callable> VTable { get; } = [];

    /// <summary>Interface definitions implemented (transitively).</summary>
    public HashSet<NamedTypeSymbol> Interfaces { get; } = new(ReferenceEqualityComparer.Instance);

    public bool IsSubclassOf(RuntimeClass other)
    {
        for (var c = this; c is not null; c = c.Base)
        {
            if (ReferenceEquals(c, other))
            {
                return true;
            }
        }

        return false;
    }

    public bool Implements(NamedTypeSymbol interfaceDefinition) => Interfaces.Contains(interfaceDefinition);

    public ObjectInstance CreateInstance()
    {
        var fields = new Value[FieldCount];
        for (var i = 0; i < FieldCount; i++)
        {
            var d = FieldDefaults[i];
            fields[i] = d.Object is ObjectInstance { Class.IsStruct: true } template ? Value.FromObject(template.CloneStruct()) : d;
        }

        return new ObjectInstance(this, fields);
    }

    internal void AddEnumName(long value, string name) => _enumNames.TryAdd(value, name);

    public string EnumName(long value) => _enumNames.TryGetValue(value, out var n) ? n : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => Name;
}

public enum TypeTestKind
{
    Bool,
    Int,
    Float,
    Char,
    Byte,
    Duration,
    Instant,
    String,
    Bytes,
    Object,
    Class,
    Interface,
    Enum,
    Array,
    Tuple,
    Function,
    Task,
    Nullable,
}

/// <summary>
/// A runtime type test used by <c>is</c>, patterns, catch clauses and checked casts. Generic arguments are erased:
/// <c>List&lt;int&gt;</c> and <c>List&lt;string&gt;</c> test as the same class (documented limitation).
/// </summary>
public sealed record TypeTest(TypeTestKind Kind, string Display, RuntimeClass? Class = null, NamedTypeSymbol? Interface = null, TypeTest? Inner = null)
{
    public bool Matches(Value value, RuntimeTypeResolver resolver)
    {
        if (Kind == TypeTestKind.Nullable)
        {
            return value.IsNull || Inner!.Matches(value, resolver);
        }

        if (value.IsNull)
        {
            return false;
        }

        return Kind switch
        {
            TypeTestKind.Bool => value.Tag == ValueTag.Bool,
            TypeTestKind.Int => value.Tag == ValueTag.Int,
            TypeTestKind.Float => value.Tag == ValueTag.Float,
            TypeTestKind.Char => value.Tag == ValueTag.Char,
            TypeTestKind.Byte => value.Tag == ValueTag.Byte,
            TypeTestKind.Duration => value.Tag == ValueTag.Duration,
            TypeTestKind.Instant => value.Tag == ValueTag.Instant,
            TypeTestKind.String => value.Object is string,
            TypeTestKind.Bytes => value.Object is BytesObject,
            TypeTestKind.Object => true,
            TypeTestKind.Array => value.Object is ArrayObject,
            TypeTestKind.Tuple => value.Object is TupleObject,
            TypeTestKind.Function => value.Object is FunctionObject,
            TypeTestKind.Task => value.Object is TaskObject,
            TypeTestKind.Enum => value.Tag == ValueTag.Enum && ReferenceEquals(value.Object, Class),
            TypeTestKind.Class => resolver.ClassOf(value) is { } c && c.IsSubclassOf(Class!),
            TypeTestKind.Interface => resolver.ClassOf(value) is { } ic && ic.Implements(Interface!),
            _ => false,
        };
    }

    public override string ToString() => Display;
}

/// <summary>Maps any value to the runtime class whose virtual table serves its methods.</summary>
public abstract class RuntimeTypeResolver
{
    public abstract RuntimeClass? ClassOf(Value value);
}
