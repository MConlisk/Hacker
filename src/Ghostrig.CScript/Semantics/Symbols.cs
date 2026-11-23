using System.Collections.Immutable;
using Ghostrig.CScript.Syntax;

namespace Ghostrig.CScript.Semantics;

public enum SymbolKind
{
    Module,
    Type,
    TypeParameter,
    Field,
    Method,
    Constructor,
    Property,
    Indexer,
    Parameter,
    Local,
}

/// <summary>Base of every named entity the binder resolves names to.</summary>
public abstract class Symbol
{
    protected Symbol(string name) => Name = name;

    public string Name { get; }
    public abstract SymbolKind Kind { get; }

    /// <summary>Declaring module, type or method (null for primitives and the global namespace).</summary>
    public Symbol? Container { get; internal set; }

    /// <summary>Top-level and member declarations are public unless marked private (Doc 7 D2).</summary>
    public bool IsPrivate { get; internal set; }

    public string? DocComment { get; internal set; }

    public Node? Syntax { get; internal set; }

    public SyntaxTree? Tree { get; internal set; }

    public ModuleSymbol? Module => this as ModuleSymbol ?? Container?.Module;

    public override string ToString() => Name;
}

/// <summary>What kind of program a module is, derived from its file extension and attributes (Doc 7 §2).</summary>
public enum ModuleKind
{
    Library,
    App,
    Driver,
    Page,
    Test,

    /// <summary>A built-in declaration module whose bodies are implemented natively (standard and system libraries).</summary>
    Native,
}

public sealed class ModuleSymbol : Symbol
{
    private readonly Dictionary<string, List<Symbol>> _members = new(StringComparer.Ordinal);
    private readonly List<Symbol> _ordered = [];

    public ModuleSymbol(string qualifiedName, ModuleKind moduleKind) : base(qualifiedName)
    {
        ModuleKind = moduleKind;
        ShortName = qualifiedName[(qualifiedName.LastIndexOf('.') + 1)..];
    }

    public override SymbolKind Kind => SymbolKind.Module;
    public ModuleKind ModuleKind { get; internal set; }

    /// <summary>Last segment of the qualified name: <c>Sys.Hardware</c> → <c>Hardware</c>; used as a qualifier.</summary>
    public string ShortName { get; }

    public ImmutableArray<AttributeSyntax> Attributes { get; internal set; } = [];

    public ImmutableArray<ModuleSymbol> Imports { get; internal set; } = [];

    /// <summary>Import aliases (<c>import Net as N;</c>) and short names mapped to imported modules.</summary>
    public IReadOnlyDictionary<string, ModuleSymbol> ImportQualifiers { get; internal set; } = new Dictionary<string, ModuleSymbol>();

    public IReadOnlyList<Symbol> Members => _ordered;

    public IReadOnlyList<Symbol> Lookup(string name) => _members.TryGetValue(name, out var list) ? list : [];

    internal void Add(Symbol symbol)
    {
        symbol.Container = this;
        if (!_members.TryGetValue(symbol.Name, out var list))
        {
            _members[symbol.Name] = list = [];
        }

        list.Add(symbol);
        _ordered.Add(symbol);
    }

    public bool IsNative => ModuleKind == ModuleKind.Native;

    private readonly List<NamedTypeSymbol> _hidden = [];

    /// <summary>Member sources of primitives (<c>string</c>, <c>Array&lt;T&gt;</c>) that are not visible by name.</summary>
    public IReadOnlyList<NamedTypeSymbol> HiddenTypes => _hidden;

    internal void AddHidden(NamedTypeSymbol type)
    {
        type.Container = this;
        _hidden.Add(type);
    }

    /// <summary>Global field slots allocated so far.</summary>
    public int GlobalCount { get; internal set; }
}

public sealed class FieldSymbol : Symbol
{
    public FieldSymbol(string name, TypeSymbol type, bool isConst, bool isStatic) : base(name)
    {
        Type = type;
        IsConst = isConst;
        IsStatic = isStatic;
        OriginalDefinition = this;
    }

    public override SymbolKind Kind => SymbolKind.Field;

    public TypeSymbol Type { get; internal set; }
    public bool IsConst { get; }

    /// <summary>Module globals and enum members are static; type fields are instance fields.</summary>
    public bool IsStatic { get; }

    /// <summary>Compile-time value for constants and enum members.</summary>
    public object? ConstantValue { get; internal set; }

    public bool IsPersistent { get; internal set; }

    /// <summary>Slot index within the owning object or module globals (assigned by the binder).</summary>
    public int Slot { get; internal set; } = -1;

    internal FieldSymbol Substitute(TypeMap map) =>
        map.IsEmpty ? this : new FieldSymbol(Name, Type.Substitute(map), IsConst, IsStatic)
        {
            Container = Container, IsPrivate = IsPrivate, DocComment = DocComment, Syntax = Syntax, Tree = Tree,
            ConstantValue = ConstantValue, Slot = Slot, OriginalDefinition = OriginalDefinition,
        };

    /// <summary>The declared field this (possibly substituted) field was derived from.</summary>
    public FieldSymbol OriginalDefinition { get; private init; }

    public override string ToString() => $"{Container?.Name}.{Name}";
}

public sealed class ParameterSymbol : Symbol
{
    public ParameterSymbol(string name, TypeSymbol type, int ordinal, bool isParams, bool hasDefault) : base(name)
    {
        Type = type;
        Ordinal = ordinal;
        IsParams = isParams;
        HasDefault = hasDefault;
    }

    public override SymbolKind Kind => SymbolKind.Parameter;
    public TypeSymbol Type { get; internal set; }
    public int Ordinal { get; }
    public bool IsParams { get; }
    public bool HasDefault { get; }
    public object? DefaultValue { get; internal set; }

    internal ParameterSymbol Substitute(TypeMap map) =>
        new(Name, Type.Substitute(map), Ordinal, IsParams, HasDefault) { DefaultValue = DefaultValue, Container = Container };

    public override string ToString() => $"{Type} {Name}";
}

public sealed class LocalSymbol : Symbol
{
    public LocalSymbol(string name, TypeSymbol type, bool isConst) : base(name)
    {
        Type = type;
        IsConst = isConst;
    }

    public override SymbolKind Kind => SymbolKind.Local;
    public TypeSymbol Type { get; internal set; }
    public bool IsConst { get; }
    public object? ConstantValue { get; internal set; }
}

public enum MethodKind
{
    /// <summary>Module-level function.</summary>
    Function,

    /// <summary>Instance method on a class, struct or interface.</summary>
    Method,
    Constructor,
    PropertyGetter,
    PropertySetter,
    IndexerGetter,
    IndexerSetter,

    /// <summary>A lambda / closure body.</summary>
    Lambda,
}

public sealed class MethodSymbol : Symbol
{
    public MethodSymbol(string name, MethodKind methodKind) : base(name)
    {
        MethodKind = methodKind;
        OriginalDefinition = this;
    }

    public override SymbolKind Kind => MethodKind == MethodKind.Constructor ? SymbolKind.Constructor : SymbolKind.Method;

    public MethodKind MethodKind { get; }
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; internal set; } = [];
    public ImmutableArray<ParameterSymbol> Parameters { get; internal set; } = [];
    public TypeSymbol ReturnType { get; internal set; } = PrimitiveType.Void;
    public bool IsAsync { get; internal set; }

    /// <summary>Body is provided by the host (native standard / system library).</summary>
    public bool IsNative { get; internal set; }

    /// <summary>Declared without a body (interface member).</summary>
    public bool IsAbstract { get; internal set; }

    /// <summary>A <c>static</c> member declared inside a type.</summary>
    public bool IsStaticMember { get; internal set; }

    /// <summary>Methods that overrides by name and parameter types (instance methods are virtual by default).</summary>
    public MethodSymbol? Overridden { get; internal set; }

    public bool IsStatic => MethodKind is MethodKind.Function or MethodKind.Lambda || IsStaticMember;

    /// <summary>Set when the method is a generic instantiation or a member of a constructed type.</summary>
    public MethodSymbol OriginalDefinition { get; private init; }

    public ImmutableArray<TypeSymbol> TypeArguments { get; private set; } = [];

    /// <summary>Stable key used to link native implementations: <c>Module.Type.Member/arity</c>.</summary>
    public string NativeKey
    {
        get
        {
            var original = OriginalDefinition;
            var owner = original.Container switch
            {
                NamedTypeSymbol t => $"{t.Module?.Name}.{t.Name}",
                ModuleSymbol m => m.Name,
                _ => "?",
            };
            return $"{owner}.{original.Name}/{original.Parameters.Length}";
        }
    }

    public bool IsGenericDefinition => !TypeParameters.IsEmpty && TypeArguments.IsEmpty;

    internal MethodSymbol Substitute(TypeMap map, ImmutableArray<TypeSymbol> typeArguments = default)
    {
        if (map.IsEmpty && typeArguments.IsDefaultOrEmpty)
        {
            return this;
        }

        var result = new MethodSymbol(Name, MethodKind)
        {
            Container = Container, IsPrivate = IsPrivate, DocComment = DocComment, Syntax = Syntax, Tree = Tree,
            IsAsync = IsAsync, IsNative = IsNative, IsAbstract = IsAbstract, IsStaticMember = IsStaticMember, Overridden = Overridden,
            TypeParameters = typeArguments.IsDefaultOrEmpty ? TypeParameters : [],
            ReturnType = ReturnType.Substitute(map),
            OriginalDefinition = OriginalDefinition,
            TypeArguments = typeArguments.IsDefaultOrEmpty ? TypeArguments : typeArguments,
        };
        result.Parameters = [.. Parameters.Select(p => p.Substitute(map))];
        return result;
    }

    /// <summary>Instantiates a generic method definition with explicit or inferred type arguments.</summary>
    public MethodSymbol Construct(ImmutableArray<TypeSymbol> typeArguments)
    {
        if (typeArguments.Length != TypeParameters.Length)
        {
            throw new ArgumentException("Type argument count mismatch.", nameof(typeArguments));
        }

        var map = TypeMap.Create(TypeParameters, typeArguments);
        return Substitute(map, typeArguments);
    }

    public override string ToString()
    {
        var generic = TypeArguments.IsDefaultOrEmpty
            ? TypeParameters.IsEmpty ? string.Empty : $"<{string.Join(", ", TypeParameters)}>"
            : $"<{string.Join(", ", TypeArguments)}>";
        return $"{ReturnType} {Name}{generic}({string.Join(", ", Parameters)})";
    }
}

/// <summary>A property or indexer: a pair of accessor methods.</summary>
public sealed class PropertySymbol : Symbol
{
    public PropertySymbol(string name, TypeSymbol type, bool isIndexer) : base(name)
    {
        Type = type;
        IsIndexer = isIndexer;
    }

    public override SymbolKind Kind => IsIndexer ? SymbolKind.Indexer : SymbolKind.Property;
    public TypeSymbol Type { get; internal set; }
    public bool IsIndexer { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; internal set; } = [];
    public MethodSymbol? Getter { get; internal set; }
    public MethodSymbol? Setter { get; internal set; }

    /// <summary>A property with neither accessor bodies nor an expression body is backed by a hidden field.</summary>
    public FieldSymbol? BackingField { get; internal set; }

    internal PropertySymbol Substitute(TypeMap map)
    {
        if (map.IsEmpty)
        {
            return this;
        }

        return new PropertySymbol(Name, Type.Substitute(map), IsIndexer)
        {
            Container = Container, IsPrivate = IsPrivate, DocComment = DocComment, Syntax = Syntax, Tree = Tree,
            Parameters = [.. Parameters.Select(p => p.Substitute(map))],
            Getter = Getter?.Substitute(map),
            Setter = Setter?.Substitute(map),
            BackingField = BackingField?.Substitute(map),
        };
    }
}
