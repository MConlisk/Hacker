using System.Collections.Immutable;

namespace Ghostrig.CScript.Semantics;

public enum TypeKind
{
    Primitive,
    Class,
    Struct,
    Interface,
    Enum,
    Array,
    Nullable,
    Tuple,
    Function,
    TypeParameter,

    /// <summary>The type of the <c>null</c> literal before conversion.</summary>
    Null,

    /// <summary>Produced after an error; converts to and from everything to suppress cascading diagnostics.</summary>
    Error,
}

/// <summary>
/// Base of the CScript type system (Doc 7 §4). Composite types (arrays, nullables, tuples, function types, constructed
/// generics) have structural equality; declared types and primitives have identity equality.
/// </summary>
public abstract class TypeSymbol : Symbol, IEquatable<TypeSymbol>
{
    protected TypeSymbol(string name) : base(name) { }

    public override SymbolKind Kind => SymbolKind.Type;
    public abstract TypeKind TypeKind { get; }

    /// <summary>Reference types live on the heap and may be null when declared nullable.</summary>
    public abstract bool IsReferenceType { get; }

    public bool IsValueType => !IsReferenceType && TypeKind is not (TypeKind.Null or TypeKind.Error or TypeKind.TypeParameter);

    public bool IsError => TypeKind == TypeKind.Error;

    public virtual TypeSymbol Substitute(TypeMap map) => this;

    public virtual bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => obj is TypeSymbol t && Equals(t);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public static bool operator ==(TypeSymbol? a, TypeSymbol? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(TypeSymbol? a, TypeSymbol? b) => !(a == b);

    /// <summary>Strips one level of <c>T?</c>.</summary>
    public TypeSymbol StripNullable() => this is NullableTypeSymbol n ? n.Underlying : this;

    public bool IsNullable => TypeKind is TypeKind.Nullable or TypeKind.Null;
}

public enum PrimitiveKind
{
    Bool,
    Byte,
    Char,
    Int,
    Float,
    String,
    Bytes,
    Void,
    Object,
    Duration,
    Instant,
}

/// <summary>Built-in types (Doc 7 §4.1–4.2). Their members are declared in the native <c>Core</c> module.</summary>
public sealed class PrimitiveType : TypeSymbol
{
    private PrimitiveType(string name, PrimitiveKind primitive) : base(name) => Primitive = primitive;

    public static readonly PrimitiveType Bool = new("bool", PrimitiveKind.Bool);
    public static readonly PrimitiveType Byte = new("byte", PrimitiveKind.Byte);
    public static readonly PrimitiveType Char = new("char", PrimitiveKind.Char);
    public static readonly PrimitiveType Int = new("int", PrimitiveKind.Int);
    public static readonly PrimitiveType Float = new("float", PrimitiveKind.Float);
    public static readonly PrimitiveType String = new("string", PrimitiveKind.String);
    public static readonly PrimitiveType Bytes = new("bytes", PrimitiveKind.Bytes);
    public static readonly PrimitiveType Void = new("void", PrimitiveKind.Void);
    public static readonly PrimitiveType Object = new("object", PrimitiveKind.Object);
    public static readonly PrimitiveType Duration = new("Duration", PrimitiveKind.Duration);
    public static readonly PrimitiveType Instant = new("Instant", PrimitiveKind.Instant);

    public static readonly ImmutableArray<PrimitiveType> All =
        [Bool, Byte, Char, Int, Float, String, Bytes, Void, Object, Duration, Instant];

    public PrimitiveKind Primitive { get; }

    /// <summary>The <c>Core</c> declaration that supplies this primitive's members (e.g. <c>string.Split</c>).</summary>
    public NamedTypeSymbol? MemberSource { get; internal set; }

    public override TypeKind TypeKind => TypeKind.Primitive;

    public override bool IsReferenceType => Primitive is PrimitiveKind.String or PrimitiveKind.Bytes or PrimitiveKind.Object;

    public bool IsNumeric => Primitive is PrimitiveKind.Byte or PrimitiveKind.Char or PrimitiveKind.Int or PrimitiveKind.Float;

    public bool IsIntegral => Primitive is PrimitiveKind.Byte or PrimitiveKind.Char or PrimitiveKind.Int;

    public static PrimitiveType? FromName(string name) => All.FirstOrDefault(p => p.Name == name && p.Primitive is not (PrimitiveKind.Duration or PrimitiveKind.Instant));
}

public sealed class TypeParameterSymbol : TypeSymbol
{
    public TypeParameterSymbol(string name, int ordinal) : base(name) => Ordinal = ordinal;

    public int Ordinal { get; }

    /// <summary>Interface constraints (Doc 7 §4.3: constraints are interfaces only).</summary>
    public ImmutableArray<TypeSymbol> Constraints { get; internal set; } = [];

    public override TypeKind TypeKind => TypeKind.TypeParameter;

    /// <summary>Type parameters are treated as reference-like (boxed) at runtime; null requires <c>T?</c>.</summary>
    public override bool IsReferenceType => false;

    public override TypeSymbol Substitute(TypeMap map) => map.TryGet(this, out var replacement) ? replacement : this;
}

/// <summary>
/// A class, struct, interface or enum. A generic declaration is the <see cref="Definition"/>; constructed instances
/// (<c>List&lt;int&gt;</c>) share it and carry <see cref="TypeArguments"/>. Members are substituted on demand.
/// </summary>
public sealed class NamedTypeSymbol : TypeSymbol
{
    private readonly List<Symbol> _members;
    private readonly Dictionary<string, List<Symbol>> _byName;
    private readonly NamedTypeSymbol? _definition;
    private TypeMap? _map;
    private Dictionary<Symbol, Symbol>? _substituted;

    public NamedTypeSymbol(string name, TypeKind kind) : base(name)
    {
        if (kind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        TypeKind = kind;
        _members = [];
        _byName = new Dictionary<string, List<Symbol>>(StringComparer.Ordinal);
    }

    private NamedTypeSymbol(NamedTypeSymbol definition, ImmutableArray<TypeSymbol> typeArguments) : base(definition.Name)
    {
        _definition = definition;
        TypeKind = definition.TypeKind;
        TypeArguments = typeArguments;
        Container = definition.Container;
        DocComment = definition.DocComment;
        Syntax = definition.Syntax;
        Tree = definition.Tree;
        IsPrivate = definition.IsPrivate;
        _members = definition._members;
        _byName = definition._byName;
    }

    public override TypeKind TypeKind { get; }

    public override bool IsReferenceType => TypeKind is TypeKind.Class or TypeKind.Interface;

    public NamedTypeSymbol Definition => _definition ?? this;

    public bool IsConstructed => _definition is not null;

    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; internal set; } = [];

    /// <summary>For a definition, its own type parameters; for a constructed type, the substituted arguments.</summary>
    public ImmutableArray<TypeSymbol> TypeArguments { get; private set; } = [];

    /// <summary>Marks a native declaration (bodies supplied by the host).</summary>
    public bool IsNative { get; internal set; }

    private NamedTypeSymbol? _baseType;
    private ImmutableArray<NamedTypeSymbol> _interfaces = [];

    public NamedTypeSymbol? BaseType
    {
        get => IsConstructed ? (NamedTypeSymbol?)Definition.BaseType?.Substitute(Map) : _baseType;
        internal set => _baseType = value;
    }

    public ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get => IsConstructed ? [.. Definition.Interfaces.Select(i => (NamedTypeSymbol)i.Substitute(Map))] : _interfaces;
        internal set => _interfaces = value;
    }

    /// <summary>Number of instance field slots, including inherited ones (assigned by the binder).</summary>
    public int InstanceFieldCount { get; internal set; }

    private TypeMap Map => _map ??= IsConstructed ? TypeMap.Create(Definition.TypeParameters, TypeArguments) : TypeMap.Empty;

    public IEnumerable<Symbol> Members => _members.Select(SubstituteMember);

    /// <summary>Members declared directly on this type with the given name (not inherited).</summary>
    public IEnumerable<Symbol> GetDeclaredMembers(string name) =>
        _byName.TryGetValue(name, out var list) ? list.Select(SubstituteMember) : [];

    /// <summary>
    /// Members with the given name on this type, its base classes and interfaces (most-derived first). A method or
    /// property hides inherited members with the same signature, so an implementation and the interface member it
    /// satisfies are not both overload candidates.
    /// </summary>
    public IEnumerable<Symbol> LookupMembers(string name)
    {
        var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<NamedTypeSymbol>();
        var found = new List<Symbol>();
        queue.Enqueue(this);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type.Definition))
            {
                continue;
            }

            foreach (var member in type.GetDeclaredMembers(name))
            {
                var hidden = member switch
                {
                    MethodSymbol { MethodKind: MethodKind.Method } m => found.OfType<MethodSymbol>()
                        .Any(f => f.MethodKind == MethodKind.Method && f.IsStatic == m.IsStatic && DeclarationBinder.SameSignature(f, m)),
                    PropertySymbol p => found.OfType<PropertySymbol>().Any(f => f.IsIndexer == p.IsIndexer && f.Parameters.Length == p.Parameters.Length),
                    _ => false,
                };
                if (hidden)
                {
                    continue;
                }

                found.Add(member);
                yield return member;
            }

            if (type.BaseType is { } baseType)
            {
                queue.Enqueue(baseType);
            }

            foreach (var i in type.Interfaces)
            {
                queue.Enqueue(i);
            }
        }
    }

    internal void AddMember(Symbol member)
    {
        if (IsConstructed)
        {
            throw new InvalidOperationException("Members are added to the definition only.");
        }

        member.Container = this;
        _members.Add(member);
        if (!_byName.TryGetValue(member.Name, out var list))
        {
            _byName[member.Name] = list = [];
        }

        list.Add(member);
    }

    public NamedTypeSymbol Construct(ImmutableArray<TypeSymbol> typeArguments)
    {
        if (TypeParameters.Length != typeArguments.Length)
        {
            throw new ArgumentException($"{Name} expects {TypeParameters.Length} type arguments.", nameof(typeArguments));
        }

        if (typeArguments.IsEmpty)
        {
            return this;
        }

        return new NamedTypeSymbol(Definition, typeArguments);
    }

    /// <summary>True when this type is, derives from, or implements <paramref name="other"/>.</summary>
    public bool IsSubtypeOf(NamedTypeSymbol other)
    {
        var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<NamedTypeSymbol>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (t.Equals(other))
            {
                return true;
            }

            if (!seen.Add(t))
            {
                continue;
            }

            if (t.BaseType is { } b)
            {
                stack.Push(b);
            }

            foreach (var i in t.Interfaces)
            {
                stack.Push(i);
            }
        }

        return false;
    }

    public override TypeSymbol Substitute(TypeMap map)
    {
        if (map.IsEmpty || (!IsConstructed && TypeParameters.IsEmpty))
        {
            return this;
        }

        var args = IsConstructed ? TypeArguments : [.. TypeParameters.Cast<TypeSymbol>()];
        var substituted = args.Select(a => a.Substitute(map)).ToImmutableArray();
        return args.SequenceEqual(substituted) && IsConstructed ? this : Definition.Construct(substituted);
    }

    private Symbol SubstituteMember(Symbol member)
    {
        if (!IsConstructed)
        {
            return member;
        }

        _substituted ??= new Dictionary<Symbol, Symbol>(ReferenceEqualityComparer.Instance);
        if (!_substituted.TryGetValue(member, out var result))
        {
            result = member switch
            {
                FieldSymbol f => f.Substitute(Map),
                MethodSymbol m => m.Substitute(Map),
                PropertySymbol p => p.Substitute(Map),
                _ => member,
            };
            _substituted[member] = result;
        }

        return result;
    }

    public override bool Equals(TypeSymbol? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is NamedTypeSymbol n && ReferenceEquals(Definition, n.Definition)
               && (IsConstructed || n.IsConstructed)
               && EffectiveArguments.SequenceEqual(n.EffectiveArguments);
    }

    private IEnumerable<TypeSymbol> EffectiveArguments => IsConstructed ? TypeArguments : TypeParameters;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Definition));
        foreach (var a in EffectiveArguments)
        {
            hash.Add(a.GetHashCode());
        }

        return hash.ToHashCode();
    }

    public override string ToString() =>
        IsConstructed ? $"{Name}<{string.Join(", ", TypeArguments)}>"
        : TypeParameters.IsEmpty ? Name : $"{Name}<{string.Join(", ", TypeParameters)}>";
}

public sealed class ArrayTypeSymbol(TypeSymbol element) : TypeSymbol(element.Name + "[]")
{
    /// <summary>The Core <c>Array&lt;T&gt;</c> declaration supplying array members and interfaces.</summary>
    public static NamedTypeSymbol? MemberSource { get; internal set; }

    public NamedTypeSymbol? Members => MemberSource?.Construct([Element]);

    public TypeSymbol Element { get; } = element;
    public override TypeKind TypeKind => TypeKind.Array;
    public override bool IsReferenceType => true;
    public override TypeSymbol Substitute(TypeMap map) => Element.Substitute(map) is var e && e == Element ? this : new ArrayTypeSymbol(e);
    public override bool Equals(TypeSymbol? other) => other is ArrayTypeSymbol a && a.Element == Element;
    public override int GetHashCode() => HashCode.Combine(1, Element);
    public override string ToString() => $"{Element}[]";
}

public sealed class NullableTypeSymbol : TypeSymbol
{
    private NullableTypeSymbol(TypeSymbol underlying) : base(underlying.Name + "?") => Underlying = underlying;

    /// <summary>Creates <c>T?</c>; <c>T??</c> collapses to <c>T?</c>.</summary>
    public static TypeSymbol Create(TypeSymbol underlying) =>
        underlying is NullableTypeSymbol or ErrorTypeSymbol or NullTypeSymbol ? underlying : new NullableTypeSymbol(underlying);

    public TypeSymbol Underlying { get; }
    public override TypeKind TypeKind => TypeKind.Nullable;
    public override bool IsReferenceType => Underlying.IsReferenceType;
    public override TypeSymbol Substitute(TypeMap map) => Create(Underlying.Substitute(map));
    public override bool Equals(TypeSymbol? other) => other is NullableTypeSymbol n && n.Underlying == Underlying;
    public override int GetHashCode() => HashCode.Combine(2, Underlying);
    public override string ToString() => $"{Underlying}?";
}

public sealed class TupleTypeSymbol(ImmutableArray<TypeSymbol> elements) : TypeSymbol("tuple")
{
    public ImmutableArray<TypeSymbol> Elements { get; } = elements;
    public override TypeKind TypeKind => TypeKind.Tuple;
    public override bool IsReferenceType => false;
    public override TypeSymbol Substitute(TypeMap map) => new TupleTypeSymbol([.. Elements.Select(e => e.Substitute(map))]);
    public override bool Equals(TypeSymbol? other) => other is TupleTypeSymbol t && t.Elements.SequenceEqual(Elements);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(3);
        foreach (var e in Elements)
        {
            hash.Add(e);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"({string.Join(", ", Elements)})";
}

public sealed class FunctionTypeSymbol(ImmutableArray<TypeSymbol> parameters, TypeSymbol returnType) : TypeSymbol("fn")
{
    public ImmutableArray<TypeSymbol> Parameters { get; } = parameters;
    public TypeSymbol ReturnType { get; } = returnType;
    public override TypeKind TypeKind => TypeKind.Function;
    public override bool IsReferenceType => true;

    public override TypeSymbol Substitute(TypeMap map) =>
        new FunctionTypeSymbol([.. Parameters.Select(p => p.Substitute(map))], ReturnType.Substitute(map));

    public override bool Equals(TypeSymbol? other) =>
        other is FunctionTypeSymbol f && f.ReturnType == ReturnType && f.Parameters.SequenceEqual(Parameters);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(4);
        hash.Add(ReturnType);
        foreach (var p in Parameters)
        {
            hash.Add(p);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"fn({string.Join(", ", Parameters)}) -> {ReturnType}";
}

public sealed class NullTypeSymbol : TypeSymbol
{
    public static readonly NullTypeSymbol Instance = new();
    private NullTypeSymbol() : base("null") { }
    public override TypeKind TypeKind => TypeKind.Null;
    public override bool IsReferenceType => true;
}

public sealed class ErrorTypeSymbol : TypeSymbol
{
    public static readonly ErrorTypeSymbol Instance = new();
    private ErrorTypeSymbol() : base("?") { }
    public override TypeKind TypeKind => TypeKind.Error;
    public override bool IsReferenceType => true;
}

/// <summary>Immutable substitution from type parameters to type arguments.</summary>
public sealed class TypeMap
{
    private readonly ImmutableDictionary<TypeParameterSymbol, TypeSymbol> _map;

    private TypeMap(ImmutableDictionary<TypeParameterSymbol, TypeSymbol> map) => _map = map;

    public static readonly TypeMap Empty = new(ImmutableDictionary.Create<TypeParameterSymbol, TypeSymbol>(ReferenceEqualityComparer.Instance));

    public bool IsEmpty => _map.IsEmpty;

    public static TypeMap Create(IEnumerable<TypeParameterSymbol> parameters, IEnumerable<TypeSymbol> arguments)
    {
        var builder = ImmutableDictionary.CreateBuilder<TypeParameterSymbol, TypeSymbol>(ReferenceEqualityComparer.Instance);
        foreach (var (p, a) in parameters.Zip(arguments))
        {
            if (!ReferenceEquals(p, a))
            {
                builder[p] = a;
            }
        }

        return new TypeMap(builder.ToImmutable());
    }

    public bool TryGet(TypeParameterSymbol parameter, out TypeSymbol replacement) => _map.TryGetValue(parameter, out replacement!);
}
