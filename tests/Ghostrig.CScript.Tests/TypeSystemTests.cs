using Ghostrig.CScript.Semantics;

namespace Ghostrig.CScript.Tests;

public class TypeSystemTests
{
    private static (NamedTypeSymbol List, TypeParameterSymbol T) GenericList()
    {
        var t = new TypeParameterSymbol("T", 0);
        var list = new NamedTypeSymbol("List", TypeKind.Class) { TypeParameters = [t] };
        var add = new MethodSymbol("Add", MethodKind.Method) { ReturnType = PrimitiveType.Void };
        add.Parameters = [new ParameterSymbol("item", t, 0, false, false)];
        list.AddMember(add);
        list.AddMember(new PropertySymbol("First", t, isIndexer: false));
        return (list, t);
    }

    [Fact]
    public void Constructed_generics_are_structurally_equal_and_substitute_members()
    {
        var (list, _) = GenericList();
        var a = list.Construct([PrimitiveType.Int]);
        var b = list.Construct([PrimitiveType.Int]);
        var c = list.Construct([PrimitiveType.String]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual<TypeSymbol>(list, a);
        Assert.Equal("List<int>", a.ToString());

        var add = Assert.IsType<MethodSymbol>(a.GetDeclaredMembers("Add").Single());
        Assert.Equal(PrimitiveType.Int, add.Parameters[0].Type);
        Assert.Same(list.GetDeclaredMembers("Add").Single(), add.OriginalDefinition);
        Assert.Equal(PrimitiveType.String, Assert.IsType<PropertySymbol>(c.GetDeclaredMembers("First").Single()).Type);
    }

    [Fact]
    public void Nested_substitution_and_composite_types()
    {
        var (list, t) = GenericList();
        var nested = list.Construct([new ArrayTypeSymbol(t)]);
        var map = TypeMap.Create([t], [PrimitiveType.Byte]);
        Assert.Equal("List<byte[]>", nested.Substitute(map).ToString());

        Assert.Equal(new ArrayTypeSymbol(PrimitiveType.Int), new ArrayTypeSymbol(PrimitiveType.Int));
        Assert.Equal(
            new FunctionTypeSymbol([PrimitiveType.Int], PrimitiveType.Bool),
            new FunctionTypeSymbol([PrimitiveType.Int], PrimitiveType.Bool));
        Assert.Equal("fn(int) -> bool", new FunctionTypeSymbol([PrimitiveType.Int], PrimitiveType.Bool).ToString());
    }

    [Fact]
    public void Generic_methods_construct_with_type_arguments()
    {
        var t = new TypeParameterSymbol("T", 0);
        var max = new MethodSymbol("Max", MethodKind.Function) { TypeParameters = [t], ReturnType = t };
        max.Parameters = [new ParameterSymbol("a", t, 0, false, false), new ParameterSymbol("b", t, 1, false, false)];

        var maxInt = max.Construct([PrimitiveType.Int]);
        Assert.Equal("int Max<int>(int a, int b)", maxInt.ToString());
        Assert.Same(max, maxInt.OriginalDefinition);
    }

    [Fact]
    public void Inheritance_and_interfaces()
    {
        var comparable = new NamedTypeSymbol("Comparable", TypeKind.Interface);
        var error = new NamedTypeSymbol("Error", TypeKind.Class);
        var denied = new NamedTypeSymbol("AccessDenied", TypeKind.Class) { BaseType = error, Interfaces = [comparable] };

        Assert.True(denied.IsSubtypeOf(error));
        Assert.True(denied.IsSubtypeOf(comparable));
        Assert.False(error.IsSubtypeOf(denied));
    }

    [Theory]
    [MemberData(nameof(ConversionCases))]
    public void Conversion_classification(TypeSymbol from, TypeSymbol to, ConversionKind expected) =>
        Assert.Equal(expected, Conversions.Classify(from, to).Kind);

    public static IEnumerable<object[]> ConversionCases()
    {
        var error = new NamedTypeSymbol("Error", TypeKind.Class);
        var denied = new NamedTypeSymbol("AccessDenied", TypeKind.Class) { BaseType = error };
        var packet = new NamedTypeSymbol("Packet", TypeKind.Struct);
        var mode = new NamedTypeSymbol("TransferMode", TypeKind.Enum);

        yield return [PrimitiveType.Int, PrimitiveType.Int, ConversionKind.Identity];
        yield return [PrimitiveType.Byte, PrimitiveType.Int, ConversionKind.ImplicitNumeric];
        yield return [PrimitiveType.Int, PrimitiveType.Float, ConversionKind.ImplicitNumeric];
        yield return [PrimitiveType.Float, PrimitiveType.Int, ConversionKind.ExplicitNumeric];
        yield return [PrimitiveType.Int, PrimitiveType.Byte, ConversionKind.ExplicitNumeric];
        yield return [NullTypeSymbol.Instance, PrimitiveType.String, ConversionKind.None];
        yield return [NullTypeSymbol.Instance, NullableTypeSymbol.Create(PrimitiveType.String), ConversionKind.NullLiteral];
        yield return [NullTypeSymbol.Instance, PrimitiveType.Int, ConversionKind.None];
        yield return [NullTypeSymbol.Instance, NullableTypeSymbol.Create(PrimitiveType.Int), ConversionKind.NullLiteral];
        yield return [PrimitiveType.Int, NullableTypeSymbol.Create(PrimitiveType.Int), ConversionKind.ImplicitNullable];
        yield return [NullableTypeSymbol.Create(PrimitiveType.Int), PrimitiveType.Int, ConversionKind.ExplicitNullable];
        yield return [denied, error, ConversionKind.ImplicitReference];
        yield return [error, denied, ConversionKind.ExplicitReference];
        yield return [packet, PrimitiveType.Object, ConversionKind.Boxing];
        yield return [PrimitiveType.Object, packet, ConversionKind.Unboxing];
        yield return [PrimitiveType.String, PrimitiveType.Object, ConversionKind.ImplicitReference];
        yield return [mode, PrimitiveType.Int, ConversionKind.ExplicitEnum];
        yield return [PrimitiveType.String, PrimitiveType.Int, ConversionKind.None];
        yield return [
            new TupleTypeSymbol([PrimitiveType.Byte, PrimitiveType.String]),
            new TupleTypeSymbol([PrimitiveType.Int, PrimitiveType.Object]),
            ConversionKind.ImplicitTuple,
        ];
        yield return [ErrorTypeSymbol.Instance, PrimitiveType.Int, ConversionKind.Error];
    }

    [Fact]
    public void Nullable_of_nullable_collapses()
    {
        var once = NullableTypeSymbol.Create(PrimitiveType.String);
        Assert.Equal(once, NullableTypeSymbol.Create(once));
        Assert.Equal(PrimitiveType.String, once.StripNullable());
        Assert.True(once.IsReferenceType);
        Assert.False(NullableTypeSymbol.Create(PrimitiveType.Int).IsReferenceType);
    }

    [Fact]
    public void Primitive_lookup_excludes_library_value_types()
    {
        Assert.Same(PrimitiveType.Int, PrimitiveType.FromName("int"));
        Assert.Null(PrimitiveType.FromName("Duration"));
    }
}
