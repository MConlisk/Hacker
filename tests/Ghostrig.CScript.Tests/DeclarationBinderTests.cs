using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Syntax;

namespace Ghostrig.CScript.Tests;

public class DeclarationBinderTests
{
    private static Compilation Compile(string source, string path = "<memory>") =>
        Compilation.Create(SyntaxTree.Parse(source, path));

    private static void AssertClean(Compilation compilation) =>
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));

    private static void AssertError(Compilation compilation, string code) =>
        Assert.Contains(compilation.Diagnostics, d => d.Code == code);

    [Fact]
    public void Standard_library_compiles_and_exposes_core_types()
    {
        var stdlib = StandardLibrary.Default;
        AssertClean(stdlib);

        var core = stdlib.GetModule("Core")!;
        Assert.Equal(ModuleKind.Native, core.ModuleKind);
        Assert.Equal([0, 1], core.Lookup("Task").OfType<NamedTypeSymbol>().Select(t => t.TypeParameters.Length).Order());
        Assert.NotNull(stdlib.WellKnown.TaskOfT);
        Assert.NotNull(stdlib.WellKnown.Iterable);
        Assert.Equal("Array", stdlib.WellKnown.ArrayMembers!.Name);

        var stringMembers = PrimitiveType.String.MemberSource!;
        var split = stringMembers.GetDeclaredMembers("Split").OfType<MethodSymbol>().ToList();
        Assert.Equal(2, split.Count);
        Assert.All(split, m => Assert.True(m.IsNative));
        Assert.Equal("Core.string.Split/1", split.Single(m => m.Parameters.Length == 1).NativeKey);
        Assert.Contains(core.Lookup("Duration"), s => ReferenceEquals(s, PrimitiveType.Duration));

        var denied = core.Lookup("AccessDenied").OfType<NamedTypeSymbol>().Single();
        Assert.True(denied.IsSubtypeOf(stdlib.WellKnown.Error!));
    }

    [Fact]
    public void Classes_get_members_slots_and_overrides()
    {
        var compilation = Compile("""
            module Shapes;
            class Shape {
                string Name;
                private int id;
                Shape(string name) { }
                float Area() => 0.0;
                string Describe() => Name;
            }
            class Circle : Shape {
                float Radius { get; set; }
                Circle(float r) : base("circle") { }
                float Area() => 3.14 * Radius * Radius;
                float Area(float scale) => scale;
            }
            """);
        AssertClean(compilation);

        var module = compilation.GetModule("Shapes")!;
        var shape = module.Lookup("Shape").OfType<NamedTypeSymbol>().Single();
        var circle = module.Lookup("Circle").OfType<NamedTypeSymbol>().Single();

        Assert.Same(shape, circle.BaseType);
        Assert.Equal(2, shape.InstanceFieldCount);
        Assert.Equal(3, circle.InstanceFieldCount); // inherited Name, id + Radius backing field
        var radius = circle.GetDeclaredMembers("Radius").OfType<PropertySymbol>().Single();
        Assert.Equal(2, radius.BackingField!.Slot);
        Assert.NotNull(radius.Setter);

        var areas = circle.GetDeclaredMembers("Area").OfType<MethodSymbol>().ToList();
        Assert.Equal(2, areas.Count);
        Assert.Same(shape.GetDeclaredMembers("Area").Single(), areas.Single(a => a.Parameters.IsEmpty).Overridden);
        Assert.Null(areas.Single(a => a.Parameters.Length == 1).Overridden);
    }

    [Fact]
    public void Module_functions_globals_enums_and_constants()
    {
        var compilation = Compile("""
            module Tools;
            enum Flags { None, Read = 1 << 0, Write = 1 << 1, All = Read | Write }
            const int Max = 32;
            int counter = 0;
            int Clamp(int x) => x;
            float Clamp(float x) => x;
            T Pick<T>(T a, T b) where T : Comparable<T> => a;
            async Task<int> CountAsync() { return 1; }
            """);
        AssertClean(compilation);

        var module = compilation.GetModule("Tools")!;
        var flags = module.Lookup("Flags").OfType<NamedTypeSymbol>().Single();
        Assert.Equal([0L, 1L, 2L, 3L], flags.Members.OfType<FieldSymbol>().Select(f => (long)f.ConstantValue!));
        Assert.Equal(2, module.Lookup("Clamp").Count);
        var pick = module.Lookup("Pick").OfType<MethodSymbol>().Single();
        Assert.Equal("Comparable", pick.TypeParameters[0].Constraints.Single().Name);
        Assert.True(module.Lookup("CountAsync").OfType<MethodSymbol>().Single().IsAsync);
        Assert.Equal([0, 1], module.Members.OfType<FieldSymbol>().Select(f => f.Slot));
    }

    [Fact]
    public void Generic_constraints_are_checked_against_primitives_and_classes()
    {
        AssertClean(Compile("""
            module Boxes;
            class Box<T> where T : Comparable<T> { T Value; }
            Box<int> a;
            Box<string> b;
            """));
        AssertError(Compile("""
            module Boxes;
            class Box<T> where T : Comparable<T> { T Value; }
            Box<Args> c;
            """), "CSC3006");
    }

    [Fact]
    public void Interface_members_must_be_implemented()
    {
        AssertClean(Compile("""
            module Feeds;
            class Countdown : Iterator<int> {
                private int n;
                bool MoveNext() => false;
                int Current { get => n; }
            }
            """));
        AssertError(Compile("""
            module Feeds;
            class Broken : Iterator<int> { bool MoveNext() => false; }
            """), "CSC3018");
    }

    [Theory]
    [InlineData("module M; Packet p;", "CSC3003")]
    [InlineData("module M; List<int, int> x;", "CSC3003")]
    [InlineData("module M; Task<int, int> t;", "CSC3005")]
    [InlineData("module M; void v;", "CSC3007")]
    [InlineData("module M; int F(void x) => 0;", "CSC3007")]
    [InlineData("module M; int F(); ", "CSC3009")]
    [InlineData("module M; int F() => 1; int F() => 2;", "CSC3008")]
    [InlineData("module M; class A {} class A {}", "CSC3008")]
    [InlineData("module M; class A : B {} class B : A {}", "CSC3012")]
    [InlineData("module M; async int F() => 1;", "CSC3013")]
    [InlineData("module M; interface I { int F() => 1; }", "CSC3014")]
    [InlineData("module M; class C { var x = 1; }", "CSC3016")]
    [InlineData("module M; class C { class D {} }", "CSC3017")]
    [InlineData("module M; interface I { int x; }", "CSC3019")]
    [InlineData("module M; struct S : Error {}", "CSC3011")]
    [InlineData("module M; import Missing.Module;", "CSC3002")]
    [InlineData("module M; static int F() => 1;", "CSC3020")]
    [InlineData("module M; enum E { A = 1 / 0 }", "CSC3015")]
    public void Declaration_errors_have_stable_codes(string source, string code) => AssertError(Compile(source), code);

    [Fact]
    public void Duplicate_modules_are_rejected()
    {
        var compilation = Compilation.Create(SyntaxTree.Parse("module Dup;"), SyntaxTree.Parse("module Dup;"));
        AssertError(compilation, "CSC3001");
        AssertError(Compile("module Core;"), "CSC3001");
    }

    [Fact]
    public void Imports_qualify_types_and_detect_ambiguity()
    {
        var a = SyntaxTree.Parse("module Lib.Alpha; class Node { }");
        var b = SyntaxTree.Parse("module Lib.Beta; class Node { }");
        AssertClean(Compilation.Create(a, b, SyntaxTree.Parse("module App; import Lib.Alpha; import Lib.Beta as B; Alpha.Node x; B.Node y; Lib.Beta.Node z;")));
        AssertError(Compilation.Create(a, b, SyntaxTree.Parse("module App; import Lib.Alpha; import Lib.Beta; Node n;")), "CSC3004");
        AssertError(Compilation.Create(a, b, SyntaxTree.Parse("module App; Node n;")), "CSC3003");
    }

    [Fact]
    public void Private_types_are_invisible_to_importers()
    {
        var lib = SyntaxTree.Parse("module Lib; private class Secret { } class Open { }");
        AssertClean(Compilation.Create(lib, SyntaxTree.Parse("module App; import Lib; Open o;")));
        AssertError(Compilation.Create(lib, SyntaxTree.Parse("module App; import Lib; Secret s;")), "CSC3003");
    }

    [Fact]
    public void Capabilities_follow_module_kind_through_library_chains()
    {
        var natives = Compilation.Create(
            [
                SyntaxTree.Parse("module Fs; void Touch(string path);", "<stdlib:Fs>"),
                SyntaxTree.Parse("module Sys.Hardware; void SendPacket(bytes p);", "<stdlib:Sys.Hardware>"),
            ],
            references: [StandardLibrary.Default]);
        AssertClean(natives);

        Compilation With(params SyntaxTree[] trees) => Compilation.Create(trees, [StandardLibrary.Default, natives]);
        var helper = SyntaxTree.Parse("module FileHelper; import Fs;", "/lib/user/FileHelper.csc");

        AssertClean(With(SyntaxTree.Parse("@App(\"Notes\") module Notes; import Fs;", "/apps/Notes.csc")));
        AssertClean(With(SyntaxTree.Parse("module NetCardDriver; import Sys.Hardware;", "/sys/drivers/net/NetCardDriver.drv")));

        var page = With(helper, SyntaxTree.Parse("module index; import FileHelper;", "/srv/www/public/index.cform"));
        var denied = Assert.Single(page.Diagnostics, d => d.Code == "CSC3010");
        Assert.Contains("FileHelper → Fs", denied.Message);

        AssertError(With(SyntaxTree.Parse("@App(\"X\") module X; import Sys.Hardware;", "/apps/X.csc")), "CSC3010");
        AssertError(With(SyntaxTree.Parse("module D; import Fs;", "/sys/drivers/D.drv")), "CSC3010");
    }

    [Fact]
    public void Module_kind_is_derived_from_extension_and_attributes()
    {
        var compilation = Compilation.Create(
            SyntaxTree.Parse("@App(\"A\") module A;", "/apps/A.csc"),
            SyntaxTree.Parse("module B;", "/lib/user/B.csc"),
            SyntaxTree.Parse("module C;", "/sys/drivers/C.drv"),
            SyntaxTree.Parse("module index;", "/srv/www/public/index.cform"),
            SyntaxTree.Parse("module T; @Test void Works() { }", "/home/kael/T.csc"));
        AssertClean(compilation);
        Assert.Equal(
            [ModuleKind.App, ModuleKind.Library, ModuleKind.Driver, ModuleKind.Page, ModuleKind.Test],
            compilation.Modules.Select(m => m.ModuleKind));
    }
}
