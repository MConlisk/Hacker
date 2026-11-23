using System.Collections.Immutable;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Semantics;

/// <summary>
/// Declaration phase of binding: creates module, type and member symbols, resolves imports, checks capabilities
/// (Doc 7 §10), resolves every type written in a signature, and validates inheritance and interface implementation.
/// Function bodies are bound afterwards by the body binder.
/// </summary>
internal sealed class DeclarationBinder
{
    private readonly Compilation _compilation;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<ModuleSymbol> _modules = [];
    private readonly Dictionary<ModuleSymbol, SyntaxTree> _trees = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModuleSymbol, List<(TypeDecl Syntax, NamedTypeSymbol Symbol)>> _types = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModuleSymbol, List<(EnumDecl Syntax, NamedTypeSymbol Symbol)>> _enums = new(ReferenceEqualityComparer.Instance);

    public DeclarationBinder(Compilation compilation, DiagnosticBag diagnostics)
    {
        _compilation = compilation;
        _diagnostics = diagnostics;
    }

    public ImmutableArray<ModuleSymbol> Bind()
    {
        CreateModules();
        DeclareTypes();
        ResolveImports();
        BindTypeHeaders();
        DeclareMembers();
        AssignFieldSlots();
        ValidateTypes();
        CheckCapabilities();
        return [.. _modules];
    }

    #region Modules and imports

    private void CreateModules()
    {
        var referenced = _compilation.References.SelectMany(r => r.AllModules).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in _compilation.Trees)
        {
            var unit = tree.Root;
            var name = unit.ModuleName.ToString();
            if (!seen.Add(name) || referenced.Contains(name))
            {
                Report(tree, DiagnosticCodes.DuplicateModule, unit.ModuleName.Span, name);
                continue;
            }

            var module = new ModuleSymbol(name, ClassifyModule(tree))
            {
                Attributes = unit.Attributes,
                DocComment = unit.DocComment,
                Syntax = unit,
                Tree = tree,
            };
            _modules.Add(module);
            _trees[module] = tree;
        }
    }

    private static ModuleKind ClassifyModule(SyntaxTree tree)
    {
        var path = tree.Source.Path;
        if (path.StartsWith(Compilation.NativePathPrefix, StringComparison.Ordinal))
        {
            return ModuleKind.Native;
        }

        if (path.EndsWith(".drv", StringComparison.Ordinal))
        {
            return ModuleKind.Driver;
        }

        if (path.EndsWith(".cform", StringComparison.Ordinal))
        {
            return ModuleKind.Page;
        }

        if (tree.Root.Attributes.Any(a => a.Name == "App"))
        {
            return ModuleKind.App;
        }

        return tree.Root.Members.OfType<FunctionDecl>().Any(f => f.Attributes.Any(a => a.Name == "Test"))
            ? ModuleKind.Test
            : ModuleKind.Library;
    }

    private void ResolveImports()
    {
        var core = FindModule("Core");
        foreach (var module in _modules)
        {
            var tree = _trees[module];
            var imports = new List<ModuleSymbol>();
            var qualifiers = new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);
            if (core is not null && !ReferenceEquals(core, module))
            {
                imports.Add(core);
            }

            foreach (var import in tree.Root.Imports)
            {
                var name = import.Name.ToString();
                var target = FindModule(name);
                if (target is null)
                {
                    Report(tree, DiagnosticCodes.ModuleNotFound, import.Name.Span, name);
                    continue;
                }

                if (!imports.Contains(target))
                {
                    imports.Add(target);
                }

                qualifiers[import.Alias ?? target.ShortName] = target;
                qualifiers[target.Name] = target;
            }

            module.Imports = [.. imports];
            module.ImportQualifiers = qualifiers;
        }
    }

    private ModuleSymbol? FindModule(string name) =>
        _modules.FirstOrDefault(m => m.Name == name) ?? _compilation.References.Select(r => r.GetModule(name)).FirstOrDefault(m => m is not null);

    #endregion

    #region Types

    private void DeclareTypes()
    {
        foreach (var module in _modules)
        {
            var tree = _trees[module];
            var types = _types[module] = [];
            var enums = _enums[module] = [];
            foreach (var member in tree.Root.Members)
            {
                switch (member)
                {
                    case TypeDecl typeDecl:
                        DeclareType(module, tree, typeDecl, types);
                        break;
                    case EnumDecl enumDecl:
                    {
                        var symbol = new NamedTypeSymbol(enumDecl.Name, TypeKind.Enum)
                        {
                            IsPrivate = enumDecl.Modifiers.IsPrivate, DocComment = enumDecl.DocComment, Syntax = enumDecl, Tree = tree,
                        };
                        if (AddTypeToModule(module, tree, symbol, enumDecl.Span))
                        {
                            enums.Add((enumDecl, symbol));
                        }

                        break;
                    }
                }
            }
        }
    }

    private void DeclareType(ModuleSymbol module, SyntaxTree tree, TypeDecl decl, List<(TypeDecl, NamedTypeSymbol)> types)
    {
        var kind = decl.Kind switch
        {
            TypeDeclKind.Class => TypeKind.Class,
            TypeDeclKind.Struct => TypeKind.Struct,
            _ => TypeKind.Interface,
        };
        var symbol = new NamedTypeSymbol(decl.Name, kind)
        {
            IsPrivate = decl.Modifiers.IsPrivate,
            DocComment = decl.DocComment,
            Syntax = decl,
            Tree = tree,
            IsNative = module.IsNative,
        };
        symbol.TypeParameters = [.. decl.TypeParameters.Select((p, i) => new TypeParameterSymbol(p.Name, i) { Container = symbol, Syntax = p, Tree = tree })];

        var primitive = decl.Attributes.FirstOrDefault(a => a.Name == "Primitive");
        if (primitive is not null && module.IsNative)
        {
            var primitiveName = primitive.Arguments.FirstOrDefault() is LiteralExpr { Value: string s } ? s : decl.Name;
            module.AddHidden(symbol);
            types.Add((decl, symbol));
            if (_compilation.References.IsEmpty)
            {
                if (primitiveName == "array")
                {
                    ArrayTypeSymbol.MemberSource = symbol;
                }

                var p = PrimitiveType.All.FirstOrDefault(t => t.Name == primitiveName);
                if (p is not null)
                {
                    p.MemberSource = symbol;
                    p.Container = module;
                    if (p.Primitive is PrimitiveKind.Duration or PrimitiveKind.Instant)
                    {
                        module.Add(p);
                    }
                }
            }

            return;
        }

        if (AddTypeToModule(module, tree, symbol, decl.Span))
        {
            types.Add((decl, symbol));
        }
    }

    private bool AddTypeToModule(ModuleSymbol module, SyntaxTree tree, NamedTypeSymbol symbol, TextSpan span)
    {
        var arity = symbol.TypeParameters.Length;
        if (module.Lookup(symbol.Name).Any(s => s is not MethodSymbol && Arity(s) == arity))
        {
            Report(tree, DiagnosticCodes.DuplicateMember, span, symbol.Name, $"module '{module.Name}'");
            return false;
        }

        module.Add(symbol);
        return true;
    }

    private static int Arity(Symbol s) => s is NamedTypeSymbol n ? n.TypeParameters.Length : 0;

    private void BindTypeHeaders()
    {
        foreach (var module in _modules)
        {
            var tree = _trees[module];
            foreach (var (decl, symbol) in _types[module])
            {
                var scope = new TypeScope(module, symbol, null);
                BindConstraints(tree, scope, decl.Constraints, symbol.TypeParameters);

                NamedTypeSymbol? baseType = null;
                var interfaces = ImmutableArray.CreateBuilder<NamedTypeSymbol>();
                foreach (var baseSyntax in decl.BaseTypes)
                {
                    if (ResolveType(tree, scope, baseSyntax) is not NamedTypeSymbol resolved)
                    {
                        continue;
                    }

                    if (resolved.TypeKind == TypeKind.Interface)
                    {
                        interfaces.Add(resolved);
                    }
                    else if (symbol.TypeKind == TypeKind.Class && resolved.TypeKind == TypeKind.Class && baseType is null && interfaces.Count == 0)
                    {
                        baseType = resolved;
                    }
                    else
                    {
                        var reason = symbol.TypeKind switch
                        {
                            TypeKind.Interface => "interfaces may only extend interfaces",
                            TypeKind.Struct => "structs may only implement interfaces",
                            _ => "a class may extend one class, listed first",
                        };
                        Report(tree, DiagnosticCodes.InvalidBaseType, baseSyntax.Span, resolved, symbol.Name, reason);
                    }
                }

                symbol.BaseType = baseType;
                symbol.Interfaces = interfaces.ToImmutable();
            }

            foreach (var (_, symbol) in _types[module])
            {
                if (HasInheritanceCycle(symbol))
                {
                    Report(tree, DiagnosticCodes.CyclicInheritance, symbol.Syntax!.Span, symbol.Name);
                    symbol.BaseType = null;
                    symbol.Interfaces = [];
                }
            }
        }
    }

    private static bool HasInheritanceCycle(NamedTypeSymbol start)
    {
        var stack = new Stack<(NamedTypeSymbol Type, int Depth)>();
        stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (type, depth) = stack.Pop();
            if (depth > 0 && ReferenceEquals(type.Definition, start))
            {
                return true;
            }

            if (depth > 64)
            {
                return true;
            }

            if (type.Definition.BaseType is { } b)
            {
                stack.Push((b.Definition, depth + 1));
            }

            foreach (var i in type.Definition.Interfaces)
            {
                stack.Push((i.Definition, depth + 1));
            }
        }

        return false;
    }

    private void BindConstraints(SyntaxTree tree, TypeScope scope, ImmutableArray<Constraint> constraints, ImmutableArray<TypeParameterSymbol> parameters)
    {
        foreach (var constraint in constraints)
        {
            var parameter = parameters.FirstOrDefault(p => p.Name == constraint.TypeParameter);
            if (parameter is null)
            {
                Report(tree, DiagnosticCodes.TypeNotFound, constraint.Span, constraint.TypeParameter);
                continue;
            }

            var bounds = ImmutableArray.CreateBuilder<TypeSymbol>();
            foreach (var bound in constraint.Bounds)
            {
                var resolved = ResolveType(tree, scope, bound);
                if (resolved is NamedTypeSymbol { TypeKind: TypeKind.Interface })
                {
                    bounds.Add(resolved);
                }
                else if (!resolved.IsError)
                {
                    Report(tree, DiagnosticCodes.InvalidBaseType, bound.Span, resolved, parameter.Name, "constraints must be interfaces");
                }
            }

            parameter.Constraints = parameter.Constraints.AddRange(bounds);
        }
    }

    #endregion

    #region Members

    private void DeclareMembers()
    {
        foreach (var module in _modules)
        {
            var tree = _trees[module];
            var moduleScope = new TypeScope(module, null, null);

            foreach (var (decl, symbol) in _enums[module])
            {
                DeclareEnumMembers(tree, decl, symbol);
            }

            // Types in base-first order so inherited field slots are known.
            foreach (var (decl, symbol) in _types[module])
            {
                foreach (var memberSyntax in decl.Members)
                {
                    DeclareTypeMember(module, tree, symbol, memberSyntax);
                }
            }

            foreach (var member in tree.Root.Members)
            {
                switch (member)
                {
                    case FunctionDecl function:
                    {
                        var method = DeclareFunction(module, tree, moduleScope, function, null);
                        AddModuleMember(module, tree, method, function.Span);
                        break;
                    }

                    case FieldDecl field:
                    {
                        if (field.Modifiers.IsAsync)
                        {
                            Report(tree, DiagnosticCodes.InvalidModifier, field.Span, "async");
                        }

                        var type = field.Type is null ? ErrorTypeSymbol.Instance : ResolveType(tree, moduleScope, field.Type);
                        var symbol = new FieldSymbol(field.Name, type, field.Modifiers.IsConst, isStatic: true)
                        {
                            IsPrivate = field.Modifiers.IsPrivate, DocComment = field.DocComment, Syntax = field, Tree = tree,
                            IsPersistent = field.Attributes.Any(a => a.Name == "Persist"),
                        };
                        AddModuleMember(module, tree, symbol, field.Span);
                        break;
                    }

                    case PropertyDecl or IndexerDecl or ConstructorDecl:
                        Report(tree, DiagnosticCodes.InvalidModifier, member.Span, "property/constructor outside a type");
                        break;
                }
            }
        }
    }

    private void AddModuleMember(ModuleSymbol module, SyntaxTree tree, Symbol symbol, TextSpan span)
    {
        var clash = module.Lookup(symbol.Name).FirstOrDefault(existing => Clashes(existing, symbol));
        if (clash is not null)
        {
            Report(tree, DiagnosticCodes.DuplicateMember, span, symbol.Name, $"module '{module.Name}'");
            return;
        }

        module.Add(symbol);
    }

    /// <summary>Two declarations clash unless both are methods with different parameter types (overloads).</summary>
    private static bool Clashes(Symbol a, Symbol b)
    {
        if (a is MethodSymbol ma && b is MethodSymbol mb)
        {
            return SameSignature(ma, mb);
        }

        if (a is NamedTypeSymbol || b is NamedTypeSymbol)
        {
            return Arity(a) == Arity(b) && a is not MethodSymbol && b is not MethodSymbol;
        }

        return true;
    }

    internal static bool SameSignature(MethodSymbol a, MethodSymbol b) =>
        a.Parameters.Length == b.Parameters.Length
        && a.TypeParameters.Length == b.TypeParameters.Length
        && a.Parameters.Zip(b.Parameters).All(p => SameParameterType(p.First.Type, p.Second.Type));

    private static bool SameParameterType(TypeSymbol a, TypeSymbol b) =>
        a == b || (a is TypeParameterSymbol ta && b is TypeParameterSymbol tb && ta.Ordinal == tb.Ordinal && ta.Container is MethodSymbol && tb.Container is MethodSymbol);

    private MethodSymbol DeclareFunction(ModuleSymbol module, SyntaxTree tree, TypeScope outer, FunctionDecl decl, NamedTypeSymbol? owner)
    {
        var kind = owner is null ? MethodKind.Function : MethodKind.Method;
        var method = new MethodSymbol(decl.Name, kind)
        {
            IsPrivate = decl.Modifiers.IsPrivate,
            IsAsync = decl.Modifiers.IsAsync,
            IsStaticMember = owner is not null && decl.Modifiers.IsStatic,
            DocComment = decl.DocComment,
            Syntax = decl,
            Tree = tree,
            Container = (Symbol?)owner ?? module,
        };
        method.TypeParameters = [.. decl.TypeParameters.Select((p, i) => new TypeParameterSymbol(p.Name, i) { Container = method, Syntax = p, Tree = tree })];
        var scope = outer with { Method = method };
        BindConstraints(tree, scope, decl.Constraints, method.TypeParameters);

        method.ReturnType = ResolveType(tree, scope, decl.ReturnType, allowVoid: true);
        method.Parameters = BindParameters(tree, scope, decl.Parameters, method);

        if (owner is null && decl.Modifiers.IsStatic)
        {
            Report(tree, DiagnosticCodes.InvalidModifier, decl.Span, "static");
        }

        var hasBody = decl.Body is not null || decl.ExpressionBody is not null;
        var inInterface = owner?.TypeKind == TypeKind.Interface;
        if (inInterface)
        {
            if (hasBody)
            {
                Report(tree, DiagnosticCodes.BodyNotAllowed, decl.Span, decl.Name);
            }

            method.IsAbstract = !method.IsStaticMember;
            method.IsNative = module.IsNative && method.IsStaticMember;
        }
        else if (!hasBody)
        {
            if (module.IsNative)
            {
                method.IsNative = true;
            }
            else
            {
                Report(tree, DiagnosticCodes.MissingBody, decl.Span, decl.Name);
            }
        }

        if (method.IsAsync && !IsValidAsyncReturn(method.ReturnType))
        {
            Report(tree, DiagnosticCodes.InvalidAsyncReturn, decl.ReturnType.Span);
        }

        return method;
    }

    private bool IsValidAsyncReturn(TypeSymbol type)
    {
        var wk = _compilation.References.FirstOrDefault()?.WellKnown;
        return type == PrimitiveType.Void || type.IsError
               || (type is NamedTypeSymbol n && (ReferenceEquals(n.Definition, wk?.Task) || ReferenceEquals(n.Definition, wk?.TaskOfT)
                                                 || n.Definition.Name == "Task"));
    }

    private ImmutableArray<ParameterSymbol> BindParameters(SyntaxTree tree, TypeScope scope, ImmutableArray<Parameter> parameters, Symbol owner)
    {
        var result = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var type = p.Type is null ? ErrorTypeSymbol.Instance : ResolveType(tree, scope, p.Type);
            if (p.IsParams && type is not ArrayTypeSymbol && !type.IsError)
            {
                Report(tree, DiagnosticCodes.InvalidModifier, p.Span, "params (requires an array type)");
            }

            if (p.IsParams && i != parameters.Length - 1)
            {
                Report(tree, DiagnosticCodes.InvalidModifier, p.Span, "params (must be the last parameter)");
            }

            if (!names.Add(p.Name))
            {
                Report(tree, DiagnosticCodes.DuplicateMember, p.Span, p.Name, "the parameter list");
            }

            var symbol = new ParameterSymbol(p.Name, type, i, p.IsParams, p.Default is not null) { Container = owner, Syntax = p, Tree = tree };
            if (p.Default is not null)
            {
                if (ConstantFolder.TryEvaluate(p.Default, out var value))
                {
                    symbol.DefaultValue = value;
                }
                else
                {
                    Report(tree, DiagnosticCodes.NotConstant, p.Default.Span, p.Name);
                }
            }

            result.Add(symbol);
        }

        return result.ToImmutable();
    }

    private void DeclareTypeMember(ModuleSymbol module, SyntaxTree tree, NamedTypeSymbol type, MemberDecl member)
    {
        var scope = new TypeScope(module, type, null);
        var isInterface = type.TypeKind == TypeKind.Interface;
        Symbol? symbol = null;

        switch (member)
        {
            case FunctionDecl function:
                symbol = DeclareFunction(module, tree, scope, function, type);
                break;

            case ConstructorDecl ctor:
            {
                if (isInterface)
                {
                    Report(tree, DiagnosticCodes.InvalidMemberInInterface, ctor.Span, "constructors");
                    return;
                }

                var method = new MethodSymbol(type.Name, MethodKind.Constructor)
                {
                    IsPrivate = ctor.Modifiers.IsPrivate, DocComment = ctor.DocComment, Syntax = ctor, Tree = tree, Container = type,
                    ReturnType = PrimitiveType.Void,
                };
                method.Parameters = BindParameters(tree, scope, ctor.Parameters, method);
                if (ctor.Body is null)
                {
                    if (module.IsNative)
                    {
                        method.IsNative = true;
                    }
                    else
                    {
                        Report(tree, DiagnosticCodes.MissingBody, ctor.Span, type.Name);
                    }
                }

                symbol = method;
                break;
            }

            case FieldDecl field:
            {
                if (isInterface)
                {
                    Report(tree, DiagnosticCodes.InvalidMemberInInterface, field.Span, "fields");
                    return;
                }

                if (field.Type is null)
                {
                    Report(tree, DiagnosticCodes.VarNotAllowedHere, field.Span);
                }

                var fieldType = field.Type is null ? ErrorTypeSymbol.Instance : ResolveType(tree, scope, field.Type);
                var isStatic = field.Modifiers.IsStatic || field.Modifiers.IsConst;
                var f = new FieldSymbol(field.Name, fieldType, field.Modifiers.IsConst, isStatic)
                {
                    IsPrivate = field.Modifiers.IsPrivate, DocComment = field.DocComment, Syntax = field, Tree = tree,
                };
                if (field.Modifiers.IsConst)
                {
                    if (field.Initializer is not null && ConstantFolder.TryEvaluate(field.Initializer, out var value))
                    {
                        f.ConstantValue = value;
                    }
                    else
                    {
                        Report(tree, DiagnosticCodes.NotConstant, field.Span, field.Name);
                    }
                }

                symbol = f;
                break;
            }

            case PropertyDecl property:
                symbol = DeclareProperty(module, tree, scope, type, property);
                break;

            case IndexerDecl indexer:
                symbol = DeclareIndexer(module, tree, scope, type, indexer);
                break;

            case TypeDecl or EnumDecl:
                Report(tree, DiagnosticCodes.NestedTypeNotSupported, member.Span, member.Name);
                return;
        }

        if (symbol is null)
        {
            return;
        }

        var clash = type.GetDeclaredMembers(symbol.Name).FirstOrDefault(existing => Clashes(existing, symbol));
        if (clash is not null)
        {
            Report(tree, DiagnosticCodes.DuplicateMember, member.Span, symbol.Name, $"type '{type.Name}'");
            return;
        }

        type.AddMember(symbol);
    }

    private PropertySymbol DeclareProperty(ModuleSymbol module, SyntaxTree tree, TypeScope scope, NamedTypeSymbol type, PropertyDecl decl)
    {
        var propertyType = ResolveType(tree, scope, decl.Type);
        var property = new PropertySymbol(decl.Name, propertyType, isIndexer: false)
        {
            IsPrivate = decl.Modifiers.IsPrivate, DocComment = decl.DocComment, Syntax = decl, Tree = tree, Container = type,
        };
        var isStatic = decl.Modifiers.IsStatic;
        var isInterface = type.TypeKind == TypeKind.Interface;
        var accessors = decl.ExpressionBody is not null
            ? [new Accessor(decl.Span, true, null, decl.ExpressionBody)]
            : decl.Accessors;
        var isAuto = !isInterface && !module.IsNative && decl.ExpressionBody is null && accessors.All(a => a.Body is null && a.ExpressionBody is null);

        foreach (var accessor in accessors)
        {
            var method = new MethodSymbol(
                (accessor.IsGetter ? "get_" : "set_") + decl.Name,
                accessor.IsGetter ? MethodKind.PropertyGetter : MethodKind.PropertySetter)
            {
                Container = type, Syntax = accessor, Tree = tree, IsPrivate = decl.Modifiers.IsPrivate, IsStaticMember = isStatic,
                ReturnType = accessor.IsGetter ? propertyType : PrimitiveType.Void,
            };
            if (!accessor.IsGetter)
            {
                method.Parameters = [new ParameterSymbol("value", propertyType, 0, false, false) { Container = method }];
            }

            var hasBody = accessor.Body is not null || accessor.ExpressionBody is not null;
            method.IsAbstract = isInterface && !isStatic;
            method.IsNative = !hasBody && module.IsNative && !method.IsAbstract;
            if (accessor.IsGetter)
            {
                property.Getter = method;
            }
            else
            {
                property.Setter = method;
            }
        }

        if (isAuto)
        {
            property.BackingField = new FieldSymbol($"<{decl.Name}>k__field", propertyType, false, isStatic) { Container = type, IsPrivate = true };
        }
        else if (!isInterface && !module.IsNative && accessors.Any(a => a.Body is null && a.ExpressionBody is null))
        {
            Report(tree, DiagnosticCodes.MissingBody, decl.Span, decl.Name);
        }

        return property;
    }

    private PropertySymbol DeclareIndexer(ModuleSymbol module, SyntaxTree tree, TypeScope scope, NamedTypeSymbol type, IndexerDecl decl)
    {
        var elementType = ResolveType(tree, scope, decl.Type);
        var indexer = new PropertySymbol("this[]", elementType, isIndexer: true)
        {
            IsPrivate = decl.Modifiers.IsPrivate, DocComment = decl.DocComment, Syntax = decl, Tree = tree, Container = type,
        };
        indexer.Parameters = BindParameters(tree, scope, decl.Parameters, indexer);
        var isInterface = type.TypeKind == TypeKind.Interface;
        foreach (var accessor in decl.Accessors)
        {
            var method = new MethodSymbol(accessor.IsGetter ? "get_Item" : "set_Item",
                accessor.IsGetter ? MethodKind.IndexerGetter : MethodKind.IndexerSetter)
            {
                Container = type, Syntax = accessor, Tree = tree, IsPrivate = decl.Modifiers.IsPrivate,
                ReturnType = accessor.IsGetter ? elementType : PrimitiveType.Void,
            };
            var parameters = indexer.Parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal, false, false) { Container = method }).ToList();
            if (!accessor.IsGetter)
            {
                parameters.Add(new ParameterSymbol("value", elementType, parameters.Count, false, false) { Container = method });
            }

            method.Parameters = [.. parameters];
            var hasBody = accessor.Body is not null || accessor.ExpressionBody is not null;
            method.IsAbstract = isInterface;
            method.IsNative = !hasBody && module.IsNative && !isInterface;
            if (!hasBody && !isInterface && !module.IsNative)
            {
                Report(tree, DiagnosticCodes.MissingBody, accessor.Span, "indexer accessor");
            }

            if (accessor.IsGetter)
            {
                indexer.Getter = method;
            }
            else
            {
                indexer.Setter = method;
            }
        }

        return indexer;
    }

    private void DeclareEnumMembers(SyntaxTree tree, EnumDecl decl, NamedTypeSymbol type)
    {
        long next = 0;
        var known = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var member in decl.Members)
        {
            var value = next;
            if (member.Value is not null)
            {
                if (ConstantFolder.TryEvaluate(member.Value, out var folded, known) && folded is long l)
                {
                    value = l;
                }
                else
                {
                    Report(tree, DiagnosticCodes.NotConstant, member.Value.Span, member.Name);
                }
            }

            if (!known.TryAdd(member.Name, value))
            {
                Report(tree, DiagnosticCodes.DuplicateMember, member.Span, member.Name, $"enum '{type.Name}'");
                continue;
            }

            type.AddMember(new FieldSymbol(member.Name, type, isConst: true, isStatic: true)
            {
                ConstantValue = value, Syntax = member, Tree = tree,
            });
            next = unchecked(value + 1);
        }
    }

    private void AssignFieldSlots()
    {
        var done = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        foreach (var module in _modules)
        {
            var global = module.GlobalCount;
            foreach (var field in module.Members.OfType<FieldSymbol>())
            {
                field.Slot = global++;
            }

            foreach (var (_, type) in _types[module])
            {
                AssignInstanceSlots(type, done);
                foreach (var property in type.Members.OfType<PropertySymbol>().Where(p => p.BackingField is { IsStatic: true }))
                {
                    property.BackingField!.Slot = global++;
                }

                foreach (var field in type.Members.OfType<FieldSymbol>().Where(f => f.IsStatic && !f.IsConst))
                {
                    field.Slot = global++;
                }
            }

            module.GlobalCount = global;
        }
    }

    private static void AssignInstanceSlots(NamedTypeSymbol type, HashSet<NamedTypeSymbol> done)
    {
        if (!done.Add(type))
        {
            return;
        }

        var slot = 0;
        if (type.BaseType?.Definition is { } baseType)
        {
            AssignInstanceSlots(baseType, done);
            slot = baseType.InstanceFieldCount;
        }

        foreach (var member in type.Members)
        {
            switch (member)
            {
                case FieldSymbol { IsStatic: false } field:
                    field.Slot = slot++;
                    break;
                case PropertySymbol { BackingField: { IsStatic: false } backing }:
                    backing.Slot = slot++;
                    break;
            }
        }

        type.InstanceFieldCount = slot;
    }

    #endregion

    #region Validation

    private void ValidateTypes()
    {
        foreach (var module in _modules)
        {
            var tree = _trees[module];
            foreach (var (decl, type) in _types[module])
            {
                LinkOverrides(type);
                if (type.TypeKind == TypeKind.Interface)
                {
                    continue;
                }

                foreach (var iface in AllInterfaces(type))
                {
                    foreach (var required in iface.Members)
                    {
                        if (!IsImplemented(type, required))
                        {
                            Report(tree, DiagnosticCodes.MissingInterfaceMember, decl.Span, type.Name, $"{iface}.{DisplayName(required)}");
                        }
                    }
                }
            }
        }
    }

    private static string DisplayName(Symbol s) => s is PropertySymbol { IsIndexer: true } ? "this[]" : s.Name;

    private static void LinkOverrides(NamedTypeSymbol type)
    {
        if (type.BaseType is null)
        {
            return;
        }

        foreach (var method in type.Members.OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.Method && !m.IsStatic))
        {
            method.Overridden = type.BaseType.LookupMembers(method.Name).OfType<MethodSymbol>()
                .FirstOrDefault(b => !b.IsStatic && SameSignature(b, method));
        }
    }

    private static IEnumerable<NamedTypeSymbol> AllInterfaces(NamedTypeSymbol type)
    {
        var seen = new HashSet<NamedTypeSymbol>();
        var stack = new Stack<NamedTypeSymbol>(type.Interfaces);
        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            foreach (var i in b.Interfaces)
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

    private static bool IsImplemented(NamedTypeSymbol type, Symbol required)
    {
        for (NamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            switch (required)
            {
                case MethodSymbol { IsStatic: true }:
                    return true;
                case MethodSymbol m when t.GetDeclaredMembers(m.Name).OfType<MethodSymbol>().Any(c => !c.IsStatic && SameSignature(c, m) && c.ReturnType == m.ReturnType):
                    return true;
                case PropertySymbol { IsIndexer: true } p when t.GetDeclaredMembers("this[]").OfType<PropertySymbol>().Any(c => c.Type == p.Type):
                    return true;
                case PropertySymbol p when t.GetDeclaredMembers(p.Name).OfType<PropertySymbol>().Any(c => c.Type == p.Type && (p.Setter is null || c.Setter is not null)):
                    return true;
            }
        }

        return false;
    }

    private void CheckCapabilities()
    {
        foreach (var module in _modules.Where(m => m.ModuleKind is ModuleKind.App or ModuleKind.Driver or ModuleKind.Page or ModuleKind.Test))
        {
            var tree = _trees[module];
            foreach (var import in tree.Root.Imports)
            {
                var direct = module.Imports.FirstOrDefault(m => m.Name == import.Name.ToString());
                if (direct is null)
                {
                    continue;
                }

                foreach (var (native, chain) in ReachableNatives(direct))
                {
                    if (!Capabilities.IsAllowed(module.ModuleKind, native.Name))
                    {
                        Report(tree, DiagnosticCodes.CapabilityDenied, import.Span,
                            module.ModuleKind.ToString().ToLowerInvariant(), native.Name, string.Join(" → ", chain));
                    }
                }
            }
        }
    }

    /// <summary>Native modules reachable through library imports, each with the import chain that reaches it.</summary>
    private static IEnumerable<(ModuleSymbol Native, IReadOnlyList<string> Chain)> ReachableNatives(ModuleSymbol start)
    {
        var seen = new HashSet<ModuleSymbol>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(ModuleSymbol, IReadOnlyList<string>)>();
        queue.Enqueue((start, [start.Name]));
        while (queue.Count > 0)
        {
            var (module, chain) = queue.Dequeue();
            if (!seen.Add(module))
            {
                continue;
            }

            if (module.IsNative)
            {
                yield return (module, chain);
                continue;
            }

            foreach (var next in module.Imports)
            {
                queue.Enqueue((next, [.. chain, next.Name]));
            }
        }
    }

    #endregion

    #region Type resolution

    internal TypeSymbol ResolveType(SyntaxTree tree, TypeScope scope, TypeSyntax syntax, bool allowVoid = false) =>
        new TypeResolver(_diagnostics, tree).Resolve(scope, syntax, allowVoid);

    private void Report(SyntaxTree tree, DiagnosticDescriptor descriptor, TextSpan span, params object[] args) =>
        _diagnostics.Report(descriptor, tree.Source, span, args);

    #endregion
}

/// <summary>The declaration context used to resolve type names.</summary>
internal sealed record TypeScope(ModuleSymbol Module, NamedTypeSymbol? Type, MethodSymbol? Method);

/// <summary>Resolves type syntax to symbols (Doc 7 §4, §12 resolution order).</summary>
internal sealed class TypeResolver(DiagnosticBag diagnostics, SyntaxTree tree)
{
    public TypeSymbol Resolve(TypeScope scope, TypeSyntax syntax, bool allowVoid = false)
    {
        var type = ResolveCore(scope, syntax);
        if (!allowVoid && type == PrimitiveType.Void)
        {
            diagnostics.Report(DiagnosticCodes.VoidNotAllowed, tree.Source, syntax.Span);
            return ErrorTypeSymbol.Instance;
        }

        return type;
    }

    private TypeSymbol ResolveCore(TypeScope scope, TypeSyntax syntax)
    {
        switch (syntax)
        {
            case Syntax.ArrayType array:
                return new ArrayTypeSymbol(Resolve(scope, array.Element));
            case Syntax.NullableType nullable:
                return NullableTypeSymbol.Create(Resolve(scope, nullable.Underlying));
            case Syntax.TupleType tuple:
                return new TupleTypeSymbol([.. tuple.Elements.Select(e => Resolve(scope, e))]);
            case Syntax.FunctionType function:
                return new FunctionTypeSymbol([.. function.Parameters.Select(p => Resolve(scope, p))], Resolve(scope, function.Return, allowVoid: true));
            case Syntax.NamedType named:
                return ResolveNamed(scope, named);
            default:
                return ErrorTypeSymbol.Instance;
        }
    }

    private TypeSymbol ResolveNamed(TypeScope scope, Syntax.NamedType named)
    {
        var parts = named.Name.Parts;
        var arity = named.TypeArguments.Length;
        var arguments = named.TypeArguments.Select(a => Resolve(scope, a)).ToImmutableArray();
        TypeSymbol? found;

        if (parts.Length == 1)
        {
            found = LookupSimple(scope, parts[0], arity, named);
        }
        else
        {
            var qualifier = string.Join('.', parts[..^1]);
            var module = ResolveQualifier(scope.Module, qualifier);
            if (module is null)
            {
                diagnostics.Report(DiagnosticCodes.ModuleNotFound, tree.Source, named.Name.Span, qualifier);
                return ErrorTypeSymbol.Instance;
            }

            found = PublicTypes(module, parts[^1], arity, includePrivate: ReferenceEquals(module, scope.Module)).FirstOrDefault();
        }

        if (found is null)
        {
            var anyArity = parts.Length == 1 ? LookupSimple(scope, parts[0], -1, null) : null;
            if (anyArity is NamedTypeSymbol other && other.TypeParameters.Length != arity)
            {
                diagnostics.Report(DiagnosticCodes.WrongTypeArgumentCount, tree.Source, named.Span, other.Name, other.TypeParameters.Length);
            }
            else
            {
                diagnostics.Report(DiagnosticCodes.TypeNotFound, tree.Source, named.Span, named.ToString());
            }

            return ErrorTypeSymbol.Instance;
        }

        if (found is NamedTypeSymbol generic && arity > 0)
        {
            var constructed = generic.Construct(arguments);
            CheckConstraints(generic, arguments, named);
            return constructed;
        }

        return found;
    }

    private void CheckConstraints(NamedTypeSymbol generic, ImmutableArray<TypeSymbol> arguments, Syntax.NamedType syntax)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            var parameter = generic.TypeParameters[i];
            foreach (var constraint in parameter.Constraints.OfType<NamedTypeSymbol>())
            {
                if (!SatisfiesConstraint(argument, constraint, parameter, generic, arguments))
                {
                    diagnostics.Report(DiagnosticCodes.ConstraintViolation, tree.Source, syntax.TypeArguments[i].Span, argument, constraint, generic.Name);
                }
            }
        }
    }

    internal static bool SatisfiesConstraint(TypeSymbol argument, NamedTypeSymbol constraint, TypeParameterSymbol parameter,
        NamedTypeSymbol generic, ImmutableArray<TypeSymbol> arguments)
    {
        if (argument.IsError)
        {
            return true;
        }

        var target = (NamedTypeSymbol)constraint.Substitute(TypeMap.Create(generic.TypeParameters, arguments));
        return Conversions.ClassifyImplicit(argument, target).Exists
               || (argument is TypeParameterSymbol tp && tp.Constraints.OfType<NamedTypeSymbol>().Any(c => c.IsSubtypeOf(target)));
    }

    /// <summary>Unqualified lookup: type parameters, primitives, own module, imports. <paramref name="arity"/> -1 matches any.</summary>
    private TypeSymbol? LookupSimple(TypeScope scope, string name, int arity, Syntax.NamedType? syntax)
    {
        if (arity <= 0)
        {
            var typeParameter = scope.Method?.TypeParameters.FirstOrDefault(p => p.Name == name)
                                ?? scope.Type?.TypeParameters.FirstOrDefault(p => p.Name == name);
            if (typeParameter is not null)
            {
                return typeParameter;
            }

            if (PrimitiveType.FromName(name) is { } primitive)
            {
                return primitive;
            }
        }

        var own = PublicTypes(scope.Module, name, arity, includePrivate: true).FirstOrDefault();
        if (own is not null)
        {
            return own;
        }

        var candidates = scope.Module.Imports.SelectMany(m => PublicTypes(m, name, arity, includePrivate: false)).Distinct().ToList();
        if (candidates.Count > 1 && syntax is not null)
        {
            diagnostics.Report(DiagnosticCodes.AmbiguousName, tree.Source, syntax.Span, name,
                string.Join(" and ", candidates.Select(c => $"'{c.Module?.Name}.{c.Name}'")));
            return ErrorTypeSymbol.Instance;
        }

        return candidates.FirstOrDefault();
    }

    private static IEnumerable<TypeSymbol> PublicTypes(ModuleSymbol module, string name, int arity, bool includePrivate) =>
        module.Lookup(name).OfType<TypeSymbol>()
            .Where(t => (includePrivate || !t.IsPrivate) && (arity < 0 || (t is NamedTypeSymbol n ? n.TypeParameters.Length : 0) == arity));

    internal static ModuleSymbol? ResolveQualifier(ModuleSymbol module, string qualifier)
    {
        if (module.ImportQualifiers.TryGetValue(qualifier, out var imported))
        {
            return imported;
        }

        if (module.Name == qualifier || module.ShortName == qualifier)
        {
            return module;
        }

        return module.Imports.FirstOrDefault(m => m.Name == qualifier);
    }
}

/// <summary>Which libraries each module kind may reach (Doc 7 §10).</summary>
public static class Capabilities
{
    public static bool IsAllowed(ModuleKind kind, string nativeModule)
    {
        if (kind is ModuleKind.Library or ModuleKind.Native)
        {
            return true;
        }

        if (nativeModule == "Core" || nativeModule.StartsWith("Std.", StringComparison.Ordinal))
        {
            return !(kind == ModuleKind.Driver && nativeModule == "Std.Test");
        }

        return nativeModule switch
        {
            "Gfx" or "Input" or "CForms" or "Media" => kind is ModuleKind.App or ModuleKind.Test or ModuleKind.Page,
            "Sys" => true,
            "Log" => kind is ModuleKind.App or ModuleKind.Test or ModuleKind.Driver,
            "Sys.Hardware" => kind == ModuleKind.Driver,
            _ => kind is ModuleKind.App or ModuleKind.Test,
        };
    }
}
