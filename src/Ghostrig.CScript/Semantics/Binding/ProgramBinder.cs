using System.Collections.Immutable;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Syntax;

namespace Ghostrig.CScript.Semantics.Binding;

/// <summary>
/// Binds every body in a compilation: functions, methods, constructors (with synthesized defaults), accessors,
/// module globals and field initializers. Native modules have no bodies and are skipped.
/// </summary>
internal sealed class ProgramBinder(WellKnownTypes wellKnown, DiagnosticBag diagnostics)
{
    private readonly Dictionary<MethodSymbol, BoundFunction> _functions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModuleSymbol, ImmutableArray<(FieldSymbol, BoundExpression)>> _globals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<NamedTypeSymbol, ImmutableArray<(Symbol, BoundExpression)>> _fields = new(ReferenceEqualityComparer.Instance);
    private readonly List<BoundFunction> _lambdas = [];

    public BoundProgram Bind(ImmutableArray<ModuleSymbol> modules)
    {
        var userModules = modules.Where(m => !m.IsNative && m.Tree is not null).ToList();
        foreach (var module in userModules)
        {
            SynthesizeDefaultConstructors(module);
        }

        foreach (var module in userModules)
        {
            BindGlobals(module);
        }

        // Library declaration files have no initializers, but their module-level constants (Std.Math.Pi) must fold.
        foreach (var module in modules.Where(m => m.IsNative && m.Tree is not null))
        {
            BindNativeConstants(module);
        }

        foreach (var module in userModules)
        {
            BindModuleBodies(module);
            CheckEntryPoint(module);
        }

        return new BoundProgram(
            _functions.ToImmutableDictionary(ReferenceEqualityComparer.Instance),
            _globals.ToImmutableDictionary(ReferenceEqualityComparer.Instance),
            _fields.ToImmutableDictionary(ReferenceEqualityComparer.Instance),
            [.. _lambdas]);
    }

    private static IEnumerable<NamedTypeSymbol> TypesOf(ModuleSymbol module) =>
        module.Members.OfType<NamedTypeSymbol>().Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface);

    private static void SynthesizeDefaultConstructors(ModuleSymbol module)
    {
        foreach (var type in TypesOf(module).Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct))
        {
            if (!type.GetDeclaredMembers(type.Name).OfType<MethodSymbol>().Any(m => m.MethodKind == MethodKind.Constructor))
            {
                type.AddMember(new MethodSymbol(type.Name, MethodKind.Constructor) { ReturnType = PrimitiveType.Void, Tree = type.Tree });
            }
        }
    }

    private void BindNativeConstants(ModuleSymbol module)
    {
        var initializer = new MethodSymbol("<module-init>", MethodKind.Function) { Container = module, Tree = module.Tree };
        foreach (var field in module.Members.OfType<FieldSymbol>())
        {
            if (field.Syntax is not FieldDecl { Initializer: { } value } decl || !field.IsConst)
            {
                diagnostics.Report(DiagnosticCodes.NotConstant, module.Tree!.Source, field.Syntax!.Span, field.Name);
                continue;
            }

            var bound = new BodyBinder(wellKnown, diagnostics, module.Tree!, module, null, initializer, _lambdas)
                .BindInitializer(value, decl.Type is null ? null : field.Type);
            field.ConstantValue = bound.ConstantValue;
            if (bound.ConstantValue is null)
            {
                diagnostics.Report(DiagnosticCodes.NotConstant, module.Tree!.Source, decl.Span, decl.Name);
            }
        }
    }

    private void BindGlobals(ModuleSymbol module)
    {
        var tree = module.Tree!;
        var initializer = new MethodSymbol("<module-init>", MethodKind.Function) { Container = module, Tree = tree };
        var globals = ImmutableArray.CreateBuilder<(FieldSymbol, BoundExpression)>();

        foreach (var field in module.Members.OfType<FieldSymbol>())
        {
            if (field.Syntax is not FieldDecl { Initializer: { } value } decl)
            {
                if (field.Syntax is FieldDecl { Type: null } untyped)
                {
                    diagnostics.Report(DiagnosticCodes.VarNeedsInitializer, tree.Source, untyped.Span, untyped.Name);
                }
                else if (field.Type.IsReferenceType && field.Type is not NullableTypeSymbol && !field.Type.IsError)
                {
                    diagnostics.Report(DiagnosticCodes.UninitializedLocal, tree.Source, field.Syntax!.Span, field.Name, field.Type);
                }

                continue;
            }

            var binder = new BodyBinder(wellKnown, diagnostics, tree, module, null, initializer, _lambdas);
            var bound = binder.BindInitializer(value, decl.Type is null ? null : field.Type);
            if (decl.Type is null)
            {
                field.Type = bound.Type is NullTypeSymbol || bound.Type == PrimitiveType.Void ? ErrorTypeSymbol.Instance : bound.Type;
            }

            if (field.IsConst)
            {
                field.ConstantValue = bound.ConstantValue;
                if (bound.ConstantValue is null)
                {
                    diagnostics.Report(DiagnosticCodes.NotConstant, tree.Source, decl.Span, decl.Name);
                }

                continue;
            }

            globals.Add((field, bound));
        }

        // Static fields of types initialize with the module.
        foreach (var type in TypesOf(module))
        {
            foreach (var field in type.Members.OfType<FieldSymbol>().Where(f => f.IsStatic && !f.IsConst))
            {
                if (field.Syntax is FieldDecl { Initializer: { } value })
                {
                    var binder = new BodyBinder(wellKnown, diagnostics, tree, module, type, initializer, _lambdas);
                    globals.Add((field, binder.BindInitializer(value, field.Type)));
                }
            }
        }

        _globals[module] = globals.ToImmutable();
    }

    private void BindModuleBodies(ModuleSymbol module)
    {
        var tree = module.Tree!;
        foreach (var function in module.Members.OfType<MethodSymbol>())
        {
            BindMethod(module, tree, null, function);
        }

        foreach (var type in TypesOf(module))
        {
            if (type.TypeKind != TypeKind.Interface)
            {
                BindFieldInitializers(module, tree, type);
            }

            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case MethodSymbol method:
                        BindMethod(module, tree, type, method);
                        break;
                    case PropertySymbol property:
                        if (property.Getter is { } getter)
                        {
                            BindMethod(module, tree, type, getter);
                        }

                        if (property.Setter is { } setter)
                        {
                            BindMethod(module, tree, type, setter);
                        }

                        break;
                }
            }
        }
    }

    private void BindFieldInitializers(ModuleSymbol module, SyntaxTree tree, NamedTypeSymbol type)
    {
        var context = new MethodSymbol("<field-init>", MethodKind.Method) { Container = type, Tree = tree };
        var initializers = ImmutableArray.CreateBuilder<(Symbol, BoundExpression)>();
        foreach (var field in type.Members.OfType<FieldSymbol>().Where(f => !f.IsStatic))
        {
            if (field.Syntax is FieldDecl { Initializer: { } value })
            {
                var binder = new BodyBinder(wellKnown, diagnostics, tree, module, type, context, _lambdas);
                initializers.Add((field, binder.BindInitializer(value, field.Type)));
            }
        }

        _fields[type] = initializers.ToImmutable();
    }

    private void BindMethod(ModuleSymbol module, SyntaxTree tree, NamedTypeSymbol? type, MethodSymbol method)
    {
        if (method.IsNative || method.IsAbstract)
        {
            return;
        }

        var binder = new BodyBinder(wellKnown, diagnostics, tree, module, type, method, _lambdas);
        BoundFunction bound;
        switch (method.Syntax)
        {
            case FunctionDecl decl:
                bound = binder.BindFunction(decl.Body, decl.ExpressionBody);
                break;
            case ConstructorDecl ctor:
                bound = binder.BindFunction(ctor.Body, null, ctor.Initializer);
                break;
            case Accessor accessor when accessor.Body is not null || accessor.ExpressionBody is not null:
                bound = binder.BindFunction(accessor.Body, accessor.ExpressionBody);
                break;
            case null when method.MethodKind == MethodKind.Constructor:
                bound = binder.BindFunction(null, null);
                break;
            default:
                return; // auto-property accessors are synthesized by code generation
        }

        _functions[method] = bound;
    }

    private void CheckEntryPoint(ModuleSymbol module)
    {
        if (module.ModuleKind != ModuleKind.App)
        {
            return;
        }

        var hasMain = module.Lookup("Main").OfType<MethodSymbol>().Any(m =>
            (m.Parameters.Length == 0 || (m.Parameters.Length == 1 && ReferenceEquals((m.Parameters[0].Type as NamedTypeSymbol)?.Definition, wellKnown.Args)))
            && (m.ReturnType == PrimitiveType.Void || (m.IsAsync && m.ReturnType is NamedTypeSymbol n && ReferenceEquals(n.Definition, wellKnown.Task))));
        if (!hasMain)
        {
            diagnostics.Report(DiagnosticCodes.MissingEntryPoint, module.Tree!.Source, module.Tree.Root.ModuleName.Span, module.Name);
        }
    }
}
