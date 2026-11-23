using Ghostrig.CScript.Natives;
using Ghostrig.CScript.Semantics;

namespace Ghostrig.CScript.Tests.Std;

public class StdLibraryTests
{
    private static IEnumerable<MethodSymbol> NativeMethods(ModuleSymbol module) =>
        module.Members.OfType<MethodSymbol>()
            .Concat(module.Members.Concat(module.HiddenTypes).OfType<NamedTypeSymbol>().SelectMany(t => t.Members).SelectMany(m => m switch
            {
                MethodSymbol method => [method],
                PropertySymbol p => new[] { p.Getter, p.Setter }.OfType<MethodSymbol>(),
                _ => [],
            }))
            .Where(m => m.IsNative);

    [Fact]
    public void Every_native_declaration_in_every_library_module_is_implemented()
    {
        var modules = StandardLibrary.Default.Modules;
        Assert.Contains(modules, m => m.Name == "Std.Collections");
        var declared = modules.SelectMany(NativeMethods).Select(m => m.NativeKey).Distinct().ToList();
        var missing = declared.Where(key => !NativeRegistry.Default.TryGet(key, out _)).ToList();
        Assert.True(missing.Count == 0, $"Missing natives ({missing.Count}):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_registered_native_has_a_declaration()
    {
        var declared = StandardLibrary.Default.Modules.SelectMany(NativeMethods).Select(m => m.NativeKey).ToHashSet();
        var orphans = NativeRegistry.Default.Keys.Where(k => !declared.Contains(k)).ToList();
        Assert.True(orphans.Count == 0, $"Natives without declarations ({orphans.Count}):\n" + string.Join("\n", orphans));
    }

    [Fact]
    public void Standard_library_modules_have_no_diagnostics() =>
        Assert.Empty(StandardLibrary.Default.Diagnostics);
}
