using System.Collections.Immutable;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Syntax;

namespace Ghostrig.CScript.Semantics;

/// <summary>
/// A set of CScript modules bound together against referenced compilations (normally the standard library).
/// Binding is lazy and happens once; the result is immutable from the caller's point of view.
/// </summary>
public sealed class Compilation
{
    /// <summary>Paths of built-in declaration files start with this prefix (e.g. <c>&lt;stdlib:Core&gt;</c>).</summary>
    public const string NativePathPrefix = "<stdlib:";

    private readonly Lazy<BindResult> _bound;

    private Compilation(ImmutableArray<SyntaxTree> trees, ImmutableArray<Compilation> references)
    {
        Trees = trees;
        References = references;
        _bound = new Lazy<BindResult>(Bind, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Creates a compilation; the standard library is referenced automatically unless references are given.</summary>
    public static Compilation Create(IEnumerable<SyntaxTree> trees, IEnumerable<Compilation>? references = null)
    {
        ArgumentNullException.ThrowIfNull(trees);
        return new Compilation([.. trees], references is null ? [StandardLibrary.Default] : [.. references]);
    }

    public static Compilation Create(params SyntaxTree[] trees) => Create((IEnumerable<SyntaxTree>)trees);

    public ImmutableArray<SyntaxTree> Trees { get; }

    public ImmutableArray<Compilation> References { get; }

    /// <summary>Modules declared by this compilation (excluding references).</summary>
    public ImmutableArray<ModuleSymbol> Modules => _bound.Value.Modules;

    public ImmutableArray<Diagnostic> Diagnostics => _bound.Value.Diagnostics;

    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    public WellKnownTypes WellKnown => _bound.Value.WellKnown;

    /// <summary>Finds a module by qualified name here or in any reference.</summary>
    public ModuleSymbol? GetModule(string qualifiedName) =>
        Modules.FirstOrDefault(m => m.Name == qualifiedName)
        ?? References.Select(r => r.GetModule(qualifiedName)).FirstOrDefault(m => m is not null);

    /// <summary>All modules visible to this compilation (own modules first, then references).</summary>
    public IEnumerable<ModuleSymbol> AllModules => Modules.Concat(References.SelectMany(r => r.AllModules)).DistinctBy(m => m.Name);

    private BindResult Bind()
    {
        var diagnostics = new DiagnosticBag();
        foreach (var tree in Trees)
        {
            diagnostics.AddRange(tree.Diagnostics);
        }

        var binder = new DeclarationBinder(this, diagnostics);
        var modules = binder.Bind();
        var wellKnown = References.FirstOrDefault()?.WellKnown ?? WellKnownTypes.From(modules);
        return new BindResult(modules, [.. diagnostics], wellKnown);
    }

    private sealed record BindResult(ImmutableArray<ModuleSymbol> Modules, ImmutableArray<Diagnostic> Diagnostics, WellKnownTypes WellKnown);
}

/// <summary>The built-in declaration files compiled once and shared by every user compilation.</summary>
public static class StandardLibrary
{
    private static readonly Lazy<Compilation> LazyDefault = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The standard and system library declarations embedded in this assembly.</summary>
    public static Compilation Default => LazyDefault.Value;

    public static IEnumerable<SyntaxTree> LoadTrees()
    {
        var assembly = typeof(StandardLibrary).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Stdlib.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var moduleName = name["Stdlib.".Length..^".csd".Length];
            yield return SyntaxTree.Parse(reader.ReadToEnd(), $"{Compilation.NativePathPrefix}{moduleName}>");
        }
    }

    private static Compilation Load()
    {
        var compilation = Compilation.Create(LoadTrees(), references: []);
        if (compilation.HasErrors)
        {
            throw new InvalidOperationException(
                "The embedded standard library failed to compile:\n" + string.Join("\n", compilation.Diagnostics.Where(d => d.IsError)));
        }

        return compilation;
    }
}

/// <summary>Types the language itself depends on (foreach, async/await, throw, primitives' interfaces).</summary>
public sealed class WellKnownTypes
{
    private WellKnownTypes() { }

    public NamedTypeSymbol? Iterable { get; private init; }
    public NamedTypeSymbol? Iterator { get; private init; }
    public NamedTypeSymbol? AsyncIterable { get; private init; }
    public NamedTypeSymbol? Task { get; private init; }
    public NamedTypeSymbol? TaskOfT { get; private init; }
    public NamedTypeSymbol? Error { get; private init; }
    public NamedTypeSymbol? Args { get; private init; }

    /// <summary>Member source for array types (<c>T[]</c> → <c>Array&lt;T&gt;</c>).</summary>
    public NamedTypeSymbol? ArrayMembers { get; private init; }

    public static WellKnownTypes Empty { get; } = new();

    internal static WellKnownTypes From(IEnumerable<ModuleSymbol> modules)
    {
        var core = modules.FirstOrDefault(m => m.Name == "Core");
        if (core is null)
        {
            return Empty;
        }

        NamedTypeSymbol? Find(string name, int arity) =>
            core.Lookup(name).OfType<NamedTypeSymbol>().FirstOrDefault(t => t.TypeParameters.Length == arity);

        return new WellKnownTypes
        {
            Iterable = Find("Iterable", 1),
            Iterator = Find("Iterator", 1),
            AsyncIterable = Find("AsyncIterable", 1),
            Task = Find("Task", 0),
            TaskOfT = Find("Task", 1),
            Error = Find("Error", 0),
            Args = Find("Args", 0),
            ArrayMembers = core.HiddenTypes.FirstOrDefault(t => t.Name == "Array"),
        };
    }
}
