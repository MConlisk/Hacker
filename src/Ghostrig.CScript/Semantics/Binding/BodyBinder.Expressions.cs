using System.Collections.Immutable;
using System.Text;
using Ghostrig.CScript.Diagnostics;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Semantics.Binding;

internal sealed partial class BodyBinder
{
    #region Entry points and conversions

    /// <summary>Binds an expression and converts it implicitly to <paramref name="target"/>.</summary>
    private BoundExpression BindExpression(Expr expr, TypeSymbol target)
    {
        var bound = BindExpressionCore(expr, target);
        return Convert(bound, target, expr.Span, isExplicit: false);
    }

    private BoundExpression BindExpression(Expr expr) => BindExpressionCore(expr, null);

    /// <summary>Rejects method groups, types and modules where a value is required.</summary>
    private BoundExpression BindValue(BoundExpression expr)
    {
        switch (expr)
        {
            case BoundMethodGroup group:
                Report(DiagnosticCodes.ModuleIsNotAValue, expr.Span, group.Methods.FirstOrDefault()?.Name ?? "?", "method group (call it, or assign it to a function type)");
                return new BoundErrorExpression(expr.Span);
            case BoundTypeExpression type:
                Report(DiagnosticCodes.ModuleIsNotAValue, expr.Span, type.ReferencedType, "type");
                return new BoundErrorExpression(expr.Span);
            case BoundModuleExpression module:
                Report(DiagnosticCodes.ModuleIsNotAValue, expr.Span, module.Module.Name, "module");
                return new BoundErrorExpression(expr.Span);
            default:
                return expr;
        }
    }

    private BoundExpression BindCondition(Expr expr) => BindExpression(expr, PrimitiveType.Bool);

    private BoundExpression Convert(BoundExpression expr, TypeSymbol target, TextSpan span, bool isExplicit)
    {
        if (expr is BoundMethodGroup group && target.StripNullable() is FunctionTypeSymbol functionType)
        {
            return BindMethodReference(group, functionType, span);
        }

        expr = BindValue(expr);
        if (expr.Type.IsError || target.IsError)
        {
            return expr;
        }

        if (expr.Type == PrimitiveType.Void)
        {
            Report(DiagnosticCodes.VoidHasNoValue, span);
            return new BoundErrorExpression(span);
        }

        if (expr.Type == target)
        {
            return expr;
        }

        // Integer constants that fit convert implicitly to byte/char (int x = 5; byte b = 5;).
        if (expr.ConstantValue is long constant && target is PrimitiveType { Primitive: PrimitiveKind.Byte or PrimitiveKind.Char } small)
        {
            var fits = small.Primitive == PrimitiveKind.Byte ? constant is >= 0 and <= 255 : constant is >= 0 and <= 0x10FFFF;
            if (fits)
            {
                return new BoundConversion(span, expr, ConversionKind.ExplicitNumeric, target);
            }
        }

        var conversion = isExplicit ? Conversions.Classify(expr.Type, target) : Conversions.ClassifyImplicit(expr.Type, target);
        if (!conversion.Exists)
        {
            if (expr.Type is NullTypeSymbol)
            {
                Report(DiagnosticCodes.NullNotAllowed, span, target);
            }
            else if (!isExplicit && Conversions.Classify(expr.Type, target).Exists)
            {
                Report(DiagnosticCodes.ExplicitCastRequired, span, expr.Type, target);
            }
            else
            {
                Report(DiagnosticCodes.CannotConvert, span, expr.Type, target);
            }

            return new BoundErrorExpression(span);
        }

        return conversion.Kind == ConversionKind.Identity ? expr : new BoundConversion(span, expr, conversion.Kind, target);
    }

    private BoundExpression BindExpressionCore(Expr expr, TypeSymbol? target) => expr switch
    {
        LiteralExpr literal => BindLiteral(literal),
        InterpolatedStringExpr interpolated => BindInterpolated(interpolated),
        NameExpr name => BindName(name),
        MemberAccessExpr member => BindMemberAccess(member),
        InvocationExpr invocation => BindInvocation(invocation),
        ElementAccessExpr element => BindElementAccess(element),
        UnaryExpr unary => BindUnary(unary),
        BinaryExpr binary => BindBinary(binary),
        AssignmentExpr assignment => BindAssignment(assignment),
        ConditionalExpr conditional => BindConditional(conditional, target),
        IsPatternExpr isPattern => BindIs(isPattern),
        CastExpr cast => BindCast(cast),
        NewExpr creation => BindNew(creation),
        NewArrayExpr array => BindNewArray(array),
        ArrayLiteralExpr array => BindArrayLiteral(array, target),
        TupleExpr tuple => BindTuple(tuple, target),
        LambdaExpr lambda => BindLambda(lambda, target?.StripNullable() as FunctionTypeSymbol, scratch: false)
                             ?? (BoundExpression)new BoundErrorExpression(lambda.Span),
        AwaitExpr await => BindAwait(await),
        TypeofExpr typeOf => new BoundTypeOf(typeOf.Span, ResolveType(typeOf.Type)),
        ThisExpr thisExpr => BindThis(thisExpr.Span),
        BaseExpr baseExpr => BindBaseAlone(baseExpr),
        SwitchExpr switchExpr => BindSwitchExpression(switchExpr, target),
        ThrowExpr throwExpr => new BoundThrowExpression(throwExpr.Span, BindErrorValue(throwExpr.Value), target ?? NullTypeSymbol.Instance),
        _ => new BoundErrorExpression(expr.Span),
    };

    #endregion

    #region Literals, names, this

    private static BoundExpression BindLiteral(LiteralExpr literal) => literal.Kind switch
    {
        LiteralKind.Int => new BoundLiteral(literal.Span, PrimitiveType.Int, literal.Value),
        LiteralKind.Float => new BoundLiteral(literal.Span, PrimitiveType.Float, literal.Value),
        LiteralKind.Char => new BoundLiteral(literal.Span, PrimitiveType.Char, literal.Value is Rune r ? (long)r.Value : 0L),
        LiteralKind.String => new BoundLiteral(literal.Span, PrimitiveType.String, literal.Value),
        LiteralKind.Bytes => new BoundLiteral(literal.Span, PrimitiveType.Bytes, literal.Value),
        LiteralKind.Bool => new BoundLiteral(literal.Span, PrimitiveType.Bool, literal.Value),
        _ => new BoundLiteral(literal.Span, NullTypeSymbol.Instance, null),
    };

    private BoundExpression BindInterpolated(InterpolatedStringExpr interpolated)
    {
        var parts = ImmutableArray.CreateBuilder<BoundInterpolationPart>();
        foreach (var segment in interpolated.Segments)
        {
            switch (segment)
            {
                case TextSegment text:
                    parts.Add(new BoundInterpolationPart(text.Text, null, null));
                    break;
                case HoleSegment hole:
                {
                    var value = BindValue(BindExpression(hole.Expression));
                    if (value.Type == PrimitiveType.Void)
                    {
                        Report(DiagnosticCodes.VoidHasNoValue, hole.Span);
                    }

                    parts.Add(new BoundInterpolationPart(null, value, hole.Format));
                    break;
                }
            }
        }

        return new BoundInterpolatedString(interpolated.Span, parts.ToImmutable());
    }

    private BoundExpression BindThis(TextSpan span)
    {
        if (_containingType is null || _function.IsStatic)
        {
            Report(DiagnosticCodes.ThisInStaticContext, span, "this");
            return new BoundErrorExpression(span);
        }

        MarkThisCaptured();
        return new BoundThis(span, ThisType());
    }

    private TypeSymbol ThisType() =>
        _containingType!.TypeParameters.IsEmpty ? _containingType : _containingType.Construct([.. _containingType.TypeParameters.Cast<TypeSymbol>()]);

    private BoundExpression BindBaseAlone(BaseExpr expr)
    {
        Report(DiagnosticCodes.Expected, expr.Span, "a member access after 'base'", "base");
        return new BoundErrorExpression(expr.Span);
    }

    private void MarkThisCaptured()
    {
        for (var f = _function; f.Parent is not null; f = f.Parent)
        {
            f.CapturesThis = true;
        }
    }

    private BoundExpression BindName(NameExpr name)
    {
        var span = name.Span;
        if (name.TypeArguments.IsEmpty && _scope.Lookup(name.Name) is { } local)
        {
            return BindVariable(local, span);
        }

        // Members of the enclosing type (instance or static), including inherited ones.
        if (_containingType is not null)
        {
            var members = MemberSourceOf(ThisType())!.LookupMembers(name.Name).ToList();
            if (members.Count > 0)
            {
                return BindTypeMembers(members, receiver: null, implicitThis: true, name.TypeArguments, span, name.Name);
            }
        }

        // Module members, then imported modules.
        var moduleMembers = _module.Lookup(name.Name).Where(m => m is not NamedTypeSymbol t || Arity(t) == name.TypeArguments.Length).ToList();
        if (moduleMembers.Count > 0)
        {
            return BindModuleMembers(moduleMembers, name.TypeArguments, span, name.Name);
        }

        var imported = _module.Imports.SelectMany(m => m.Lookup(name.Name).Where(s => !s.IsPrivate))
            .Where(m => m is not NamedTypeSymbol t || Arity(t) == name.TypeArguments.Length)
            .Distinct()
            .ToList();
        if (imported.Count > 0)
        {
            var nonMethods = imported.Where(s => s is not MethodSymbol).ToList();
            if (nonMethods.Count > 1 || (nonMethods.Count == 1 && imported.Count > 1))
            {
                Report(DiagnosticCodes.AmbiguousName, span, name.Name, string.Join(" and ", imported.Select(s => $"'{s.Module?.Name}.{s.Name}'").Distinct()));
                return new BoundErrorExpression(span);
            }

            return BindModuleMembers(imported, name.TypeArguments, span, name.Name);
        }

        if (name.TypeArguments.IsEmpty && TypeResolver.ResolveQualifier(_module, name.Name) is { } qualifier)
        {
            return new BoundModuleExpression(span, qualifier);
        }

        if (name.TypeArguments.IsEmpty && PrimitiveType.FromName(name.Name) is { } primitive)
        {
            return new BoundTypeExpression(span, primitive);
        }

        var typeParameter = OutermostMethod().TypeParameters.FirstOrDefault(p => p.Name == name.Name)
                            ?? _containingType?.TypeParameters.FirstOrDefault(p => p.Name == name.Name);
        if (typeParameter is not null)
        {
            return new BoundTypeExpression(span, typeParameter);
        }

        Report(DiagnosticCodes.NameNotFound, span, name.Name);
        return new BoundErrorExpression(span);
    }

    private static int Arity(NamedTypeSymbol t) => t.TypeParameters.Length;

    private BoundExpression BindVariable(Symbol symbol, TextSpan span)
    {
        if (_owners.TryGetValue(symbol, out var owner) && !ReferenceEquals(owner, _function))
        {
            switch (symbol)
            {
                case LocalSymbol l:
                    owner.CapturedLocals.Add(l);
                    break;
                case ParameterSymbol p:
                    owner.CapturedParameters.Add(p);
                    break;
            }
        }

        BoundExpression result = symbol switch
        {
            LocalSymbol local => new BoundLocal(span, local),
            ParameterSymbol parameter => new BoundParameter(span, parameter),
            _ => new BoundErrorExpression(span),
        };

        // Flow narrowing: a nullable variable proven non-null reads as its underlying type.
        if (result.Type is NullableTypeSymbol nullable && _nonNull.Contains(symbol))
        {
            return new BoundConversion(span, result, ConversionKind.ExplicitNullable, nullable.Underlying);
        }

        return result;
    }

    private BoundExpression BindModuleMembers(List<Symbol> members, ImmutableArray<TypeSyntax> typeArguments, TextSpan span, string name)
    {
        var methods = members.OfType<MethodSymbol>().ToImmutableArray();
        if (!methods.IsEmpty)
        {
            return new BoundMethodGroup(span, null, methods, ResolveTypeArguments(typeArguments));
        }

        return members[0] switch
        {
            FieldSymbol field => new BoundField(span, null, field),
            NamedTypeSymbol type => new BoundTypeExpression(span, typeArguments.IsEmpty ? type : type.Construct(ResolveTypeArguments(typeArguments))),
            TypeSymbol type => new BoundTypeExpression(span, type),
            _ => Error(span, DiagnosticCodes.NameNotFound, name),
        };
    }

    private ImmutableArray<TypeSymbol> ResolveTypeArguments(ImmutableArray<TypeSyntax> syntax) => [.. syntax.Select(t => ResolveType(t))];

    private BoundExpression Error(TextSpan span, DiagnosticDescriptor descriptor, params object[] args)
    {
        Report(descriptor, span, args);
        return new BoundErrorExpression(span);
    }

    #endregion

    #region Member access

    private BoundExpression BindMemberAccess(MemberAccessExpr access)
    {
        var span = access.Span;
        if (access.Target is BaseExpr baseExpr)
        {
            if (_containingType?.BaseType is not { } baseType || _function.IsStatic)
            {
                return Error(baseExpr.Span, DiagnosticCodes.NoBaseType);
            }

            MarkThisCaptured();
            var members = baseType.LookupMembers(access.Member).ToList();
            if (members.Count == 0)
            {
                return Error(span, DiagnosticCodes.MemberNotFound, baseType, access.Member);
            }

            return BindTypeMembers(members, new BoundThis(baseExpr.Span, baseType), implicitThis: false, access.TypeArguments, span, access.Member, isBase: true);
        }

        var receiver = BindExpressionCore(access.Target, null);
        return BindMemberOf(receiver, access.Member, access.TypeArguments, access.IsConditional, span);
    }

    private BoundExpression BindMemberOf(BoundExpression receiver, string memberName, ImmutableArray<TypeSyntax> typeArguments, bool isConditional, TextSpan span)
    {
        switch (receiver)
        {
            case BoundErrorExpression:
                return receiver;
            case BoundModuleExpression module:
            {
                var sameModule = ReferenceEquals(module.Module, _module);
                var members = module.Module.Lookup(memberName)
                    .Where(m => sameModule || !m.IsPrivate)
                    .Where(m => m is not NamedTypeSymbol t || Arity(t) == typeArguments.Length)
                    .ToList();
                return members.Count == 0
                    ? Error(span, DiagnosticCodes.MemberNotFound, module.Module.Name, memberName)
                    : BindModuleMembers(members, typeArguments, span, memberName);
            }

            case BoundTypeExpression typeExpression:
            {
                var source = MemberSourceOf(typeExpression.ReferencedType);
                var members = source?.LookupMembers(memberName).ToList() ?? [];
                if (members.Count == 0)
                {
                    return Error(span, DiagnosticCodes.MemberNotFound, typeExpression.ReferencedType, memberName);
                }

                return BindTypeMembers(members, receiver: null, implicitThis: false, typeArguments, span, memberName, staticAccess: true);
            }

            case BoundMethodGroup:
                return Error(span, DiagnosticCodes.MemberNotFound, "method group", memberName);
        }

        receiver = BindValue(receiver);
        if (receiver.Type == PrimitiveType.Void)
        {
            return Error(span, DiagnosticCodes.VoidHasNoValue);
        }

        if (isConditional)
        {
            if (!receiver.Type.IsNullable && receiver.Type.IsValueType)
            {
                // a?.b on a non-nullable value type is just a.b.
                return BindMemberOf(receiver, memberName, typeArguments, false, span);
            }

            var placeholder = new BoundConditionalReceiver(receiver.Span, receiver.Type.StripNullable());
            var access = BindMemberOf(placeholder, memberName, typeArguments, false, span);
            var resultType = access.Type == PrimitiveType.Void || access.Type.IsNullable || access.Type.IsError
                ? access.Type
                : NullableTypeSymbol.Create(access.Type);
            return new BoundConditionalAccess(span, receiver, placeholder, access, resultType);
        }

        if (receiver.Type is NullableTypeSymbol && receiver is not BoundConditionalReceiver)
        {
            Report(DiagnosticCodes.PossibleNullDereference, receiver.Span, Describe(receiver));
        }

        var type = receiver.Type.StripNullable();
        if (type is TupleTypeSymbol tuple && memberName.StartsWith("Item", StringComparison.Ordinal)
            && int.TryParse(memberName.AsSpan(4), out var index) && index >= 1 && index <= tuple.Elements.Length)
        {
            return new BoundTupleElement(span, receiver, index - 1, tuple.Elements[index - 1]);
        }

        var memberSource = MemberSourceOf(type);
        var found = memberSource is not null
            ? memberSource.LookupMembers(memberName).ToList()
            : type is TypeParameterSymbol ? SelfAndInterfaces(type).SelectMany(t => t.LookupMembers(memberName)).ToList() : [];
        if (found.Count == 0 && type is not TypeParameterSymbol && PrimitiveType.Object.MemberSource is { } objectMembers)
        {
            found = objectMembers.LookupMembers(memberName).ToList();
        }

        if (found.Count == 0)
        {
            return type.IsError ? new BoundErrorExpression(span) : Error(span, DiagnosticCodes.MemberNotFound, type, memberName);
        }

        return BindTypeMembers(found, receiver, implicitThis: false, typeArguments, span, memberName);
    }

    private static string Describe(BoundExpression expr) => expr switch
    {
        BoundLocal l => l.Local.Name,
        BoundParameter p => p.Parameter.Name,
        BoundField f => f.Field.Name,
        BoundProperty p => p.Property.Name,
        _ => "expression",
    };

    private BoundExpression BindTypeMembers(List<Symbol> members, BoundExpression? receiver, bool implicitThis, ImmutableArray<TypeSyntax> typeArguments,
        TextSpan span, string name, bool staticAccess = false, bool isBase = false)
    {
        var methods = members.OfType<MethodSymbol>().Where(m => m.MethodKind is MethodKind.Method or MethodKind.Function).ToImmutableArray();
        if (!methods.IsEmpty)
        {
            var effectiveReceiver = receiver;
            if (implicitThis)
            {
                var anyInstance = methods.Any(m => !m.IsStatic);
                effectiveReceiver = anyInstance && !_function.IsStatic ? ImplicitThis(span) : null;
            }

            CheckAccessible(methods[0], span);
            return new BoundMethodGroup(span, isBase ? receiver : effectiveReceiver, methods, ResolveTypeArguments(typeArguments))
            {
            };
        }

        var member = members[0];
        CheckAccessible(member, span);
        var isStatic = member switch
        {
            FieldSymbol f => f.IsStatic,
            PropertySymbol p => p.Getter?.IsStatic ?? p.Setter?.IsStatic ?? false,
            _ => true,
        };

        if (member is NamedTypeSymbol nested)
        {
            return new BoundTypeExpression(span, nested);
        }

        if (implicitThis)
        {
            if (isStatic)
            {
                receiver = null;
            }
            else if (_function.IsStatic)
            {
                return Error(span, DiagnosticCodes.StaticMismatch, name, "is an instance member and cannot be used from a static context");
            }
            else
            {
                receiver = ImplicitThis(span);
            }
        }
        else if (staticAccess && !isStatic)
        {
            return Error(span, DiagnosticCodes.StaticMismatch, name, "is an instance member; access it through an object");
        }
        else if (!staticAccess && isStatic && receiver is not null)
        {
            return Error(span, DiagnosticCodes.StaticMismatch, name, "is static; access it through the type name");
        }

        return member switch
        {
            FieldSymbol field => new BoundField(span, isStatic ? null : receiver, field),
            PropertySymbol { IsIndexer: false } property => new BoundProperty(span, isStatic ? null : receiver, property, []),
            _ => Error(span, DiagnosticCodes.MemberNotFound, receiver?.Type ?? (object)"type", name),
        };
    }

    private BoundExpression ImplicitThis(TextSpan span)
    {
        MarkThisCaptured();
        return new BoundThis(new TextSpan(span.Start, 0), ThisType());
    }

    private void CheckAccessible(Symbol member, TextSpan span)
    {
        if (!member.IsPrivate)
        {
            return;
        }

        var owner = member.Container;
        var accessible = owner switch
        {
            NamedTypeSymbol t => _containingType is not null && ReferenceEquals(t.Definition, _containingType.Definition),
            ModuleSymbol m => ReferenceEquals(m, _module),
            _ => true,
        };
        if (!accessible)
        {
            Report(DiagnosticCodes.NotAccessible, span, member.Name, owner?.Name ?? "?");
        }
    }

    #endregion

    #region Invocation and overloads

    /// <summary>An argument awaiting overload resolution: either already bound, or a lambda bound per candidate.</summary>
    private sealed record PendingArgument(TextSpan Span, string? Name, BoundExpression? Bound, LambdaExpr? Lambda, Expr Syntax);

    private ImmutableArray<PendingArgument> BindArguments(ImmutableArray<Argument> arguments) =>
    [
        .. arguments.Select(a => a.Value is LambdaExpr lambda
            ? new PendingArgument(a.Span, a.Name, null, lambda, a.Value)
            : new PendingArgument(a.Span, a.Name, BindValueOrGroup(a.Value), null, a.Value)),
    ];

    /// <summary>Binds an argument; method groups are kept so they can convert to a parameter's function type.</summary>
    private BoundExpression BindValueOrGroup(Expr expr)
    {
        var bound = BindExpression(expr);
        return bound is BoundMethodGroup ? bound : BindValue(bound);
    }

    private BoundExpression BindInvocation(InvocationExpr invocation)
    {
        var span = invocation.Span;
        var target = invocation.Target is MemberAccessExpr or NameExpr
            ? BindExpressionCore(invocation.Target, null)
            : BindValue(BindExpression(invocation.Target));
        var arguments = BindArguments(invocation.Arguments);

        switch (target)
        {
            case BoundErrorExpression:
                BindArgumentsForErrors(arguments);
                return new BoundErrorExpression(span);

            case BoundMethodGroup group:
            {
                var resolved = ResolveOverload(group.Methods, arguments, group.TypeArguments, span, group.Methods[0].Name, null);
                if (resolved is null)
                {
                    return new BoundErrorExpression(span);
                }

                var (method, args) = resolved.Value;
                var receiver = group.Receiver;
                if (method.IsStatic)
                {
                    receiver = null;
                }
                else if (receiver is null)
                {
                    return Error(span, DiagnosticCodes.StaticMismatch, method.Name, "is an instance method and needs an object");
                }

                var isBase = invocation.Target is MemberAccessExpr { Target: BaseExpr };
                return new BoundCall(span, receiver, method, args, isBase);
            }

            case BoundConditionalAccess { Access: BoundMethodGroup conditionalGroup } conditional:
            {
                var resolved = ResolveOverload(conditionalGroup.Methods, arguments, conditionalGroup.TypeArguments, span, conditionalGroup.Methods[0].Name, null);
                if (resolved is null)
                {
                    return new BoundErrorExpression(span);
                }

                var call = new BoundCall(span, conditional.Placeholder, resolved.Value.Method, resolved.Value.Arguments);
                var resultType = call.Type == PrimitiveType.Void || call.Type.IsNullable ? call.Type : NullableTypeSymbol.Create(call.Type);
                return new BoundConditionalAccess(span, conditional.Receiver, conditional.Placeholder, call, resultType);
            }

            case BoundTypeExpression or BoundModuleExpression:
                BindArgumentsForErrors(arguments);
                return Error(span, DiagnosticCodes.NotInvocable, invocation.Target is NameExpr n ? n.Name : "expression");
        }

        var value = BindValue(target);
        if (value.Type.StripNullable() is FunctionTypeSymbol functionType)
        {
            if (value.Type is NullableTypeSymbol)
            {
                Report(DiagnosticCodes.PossibleNullDereference, value.Span, Describe(value));
            }

            if (arguments.Length != functionType.Parameters.Length)
            {
                BindArgumentsForErrors(arguments);
                return Error(span, DiagnosticCodes.NoOverload, functionType, DescribeArguments(arguments));
            }

            var converted = arguments.Select((a, i) => ConvertArgument(a, functionType.Parameters[i])).ToImmutableArray();
            return new BoundFunctionInvocation(span, value, converted, functionType.ReturnType);
        }

        BindArgumentsForErrors(arguments);
        return value.Type.IsError ? new BoundErrorExpression(span) : Error(span, DiagnosticCodes.NotInvocable, value.Type);
    }

    private void BindArgumentsForErrors(ImmutableArray<PendingArgument> arguments)
    {
        foreach (var argument in arguments.Where(a => a.Lambda is not null))
        {
            WithScratchDiagnostics(() => BindLambda(argument.Lambda!, null, scratch: true));
        }
    }

    private BoundExpression ConvertArgument(PendingArgument argument, TypeSymbol parameterType)
    {
        if (argument.Lambda is not null)
        {
            return BindLambda(argument.Lambda, parameterType.StripNullable() as FunctionTypeSymbol, scratch: false) is { } lambda
                ? Convert(lambda, parameterType, argument.Span, isExplicit: false)
                : new BoundErrorExpression(argument.Span);
        }

        return Convert(argument.Bound!, parameterType, argument.Span, isExplicit: false);
    }

    private string DescribeArguments(ImmutableArray<PendingArgument> arguments) =>
        string.Join(", ", arguments.Select(a => a.Lambda is not null ? "lambda" : a.Bound is BoundMethodGroup ? "method group" : a.Bound!.Type.ToString()));

    private (MethodSymbol Method, ImmutableArray<BoundExpression> Arguments)? ResolveOverload(
        ImmutableArray<MethodSymbol> candidates, ImmutableArray<PendingArgument> arguments, ImmutableArray<TypeSymbol> explicitTypeArguments,
        TextSpan span, string name, string? receiverForErrors)
    {
        var applicable = new List<(MethodSymbol Method, ImmutableArray<PendingArgument?> Mapping, bool Expanded, int Score)>();
        foreach (var candidate in candidates)
        {
            foreach (var expanded in new[] { false, true })
            {
                if (TryApply(candidate, arguments, explicitTypeArguments, expanded) is { } application)
                {
                    applicable.Add((application.Method, application.Mapping, expanded, application.Score + (expanded ? 1 : 0)));
                    break;
                }
            }
        }

        if (applicable.Count == 0)
        {
            BindArgumentsForErrors(arguments);
            var generic = candidates.FirstOrDefault(c => !c.TypeParameters.IsEmpty && explicitTypeArguments.IsEmpty && c.Parameters.Length == arguments.Length);
            if (generic is not null && candidates.Length == 1)
            {
                Report(DiagnosticCodes.CannotInferTypeArguments, span, generic.Name);
            }
            else if (candidates.Length > 0 && candidates[0].MethodKind == MethodKind.Constructor)
            {
                Report(DiagnosticCodes.NoMatchingConstructor, span, receiverForErrors ?? name, DescribeArguments(arguments));
            }
            else
            {
                Report(DiagnosticCodes.NoOverload, span, name, DescribeArguments(arguments));
            }

            return null;
        }

        var best = applicable.MinBy(a => a.Score);
        var ties = applicable.Where(a => a.Score == best.Score).ToList();
        if (ties.Count > 1)
        {
            // Prefer non-generic over generic, then fewer default-filled parameters.
            var nonGeneric = ties.Where(t => t.Method.TypeArguments.IsEmpty).ToList();
            if (nonGeneric.Count == 1)
            {
                best = nonGeneric[0];
            }
            else
            {
                Report(DiagnosticCodes.AmbiguousCall, span, string.Join(" and ", ties.Select(t => $"'{t.Method}'")));
                return null;
            }
        }

        return (best.Method, BuildArguments(best.Method, best.Mapping, best.Expanded, span));
    }

    private sealed record Application(MethodSymbol Method, ImmutableArray<PendingArgument?> Mapping, int Score);

    /// <summary>Maps arguments to parameters, infers type arguments, and scores conversions; null when inapplicable.</summary>
    private Application? TryApply(MethodSymbol candidate, ImmutableArray<PendingArgument> arguments, ImmutableArray<TypeSymbol> explicitTypeArguments, bool expanded)
    {
        var parameters = candidate.Parameters;
        if (expanded && (parameters.IsEmpty || !parameters[^1].IsParams))
        {
            return null;
        }

        var fixedCount = expanded ? parameters.Length - 1 : parameters.Length;
        var mapping = new PendingArgument?[parameters.Length];
        var extra = new List<PendingArgument>();
        var position = 0;
        foreach (var argument in arguments)
        {
            if (argument.Name is not null)
            {
                var index = parameters.IndexOf(parameters.FirstOrDefault(p => p.Name == argument.Name)!);
                if (index < 0 || mapping[index] is not null || (expanded && index == parameters.Length - 1))
                {
                    return null;
                }

                mapping[index] = argument;
                continue;
            }

            if (position < fixedCount)
            {
                mapping[position++] = argument;
            }
            else if (expanded)
            {
                extra.Add(argument);
            }
            else
            {
                return null;
            }
        }

        for (var i = 0; i < fixedCount; i++)
        {
            if (mapping[i] is null && !parameters[i].HasDefault)
            {
                return null;
            }
        }

        // Type arguments: explicit or inferred.
        var method = candidate;
        if (!candidate.TypeParameters.IsEmpty)
        {
            if (!explicitTypeArguments.IsEmpty)
            {
                if (explicitTypeArguments.Length != candidate.TypeParameters.Length)
                {
                    return null;
                }

                method = candidate.Construct(explicitTypeArguments);
            }
            else
            {
                var inferred = InferTypeArguments(candidate, mapping, extra, expanded);
                if (inferred is null)
                {
                    return null;
                }

                method = candidate.Construct(inferred.Value);
            }

            if (!SatisfiesMethodConstraints(candidate, method.TypeArguments))
            {
                return null;
            }
        }
        else if (!explicitTypeArguments.IsEmpty)
        {
            return null;
        }

        var score = 0;
        for (var i = 0; i < fixedCount; i++)
        {
            if (mapping[i] is { } argument)
            {
                var rank = ConversionRank(argument, method.Parameters[i].Type);
                if (rank < 0)
                {
                    return null;
                }

                score += rank;
            }
        }

        if (expanded)
        {
            var element = ((ArrayTypeSymbol)method.Parameters[^1].Type).Element;
            foreach (var argument in extra)
            {
                var rank = ConversionRank(argument, element);
                if (rank < 0)
                {
                    return null;
                }

                score += rank;
            }

            // Stash the expanded arguments in a synthetic array literal argument.
            mapping[^1] = new PendingArgument(default, null, null, null, null!) with { Bound = new BoundExpandedParams([.. extra]) };
        }
        else if (!parameters.IsEmpty && parameters[^1].IsParams && mapping[^1] is null)
        {
            mapping[^1] = new PendingArgument(default, null, null, null, null!) with { Bound = new BoundExpandedParams([]) };
        }

        return new Application(method, [.. mapping], score);
    }

    /// <summary>Marker carried through overload resolution for an expanded <c>params</c> argument list.</summary>
    private sealed record BoundExpandedParams(ImmutableArray<PendingArgument> Arguments) : BoundExpression(default, ErrorTypeSymbol.Instance);

    /// <summary>0 = identity, 1 = implicit widening, 2 = other implicit conversion, -1 = not convertible.</summary>
    private int ConversionRank(PendingArgument argument, TypeSymbol parameterType)
    {
        if (argument.Lambda is not null)
        {
            if (parameterType.StripNullable() is not FunctionTypeSymbol functionType)
            {
                return -1;
            }

            var ok = WithScratchDiagnostics(() => BindLambda(argument.Lambda, functionType, scratch: true) is not null);
            return ok ? 1 : -1;
        }

        var bound = argument.Bound!;
        if (bound is BoundMethodGroup group)
        {
            return parameterType.StripNullable() is FunctionTypeSymbol f && FindMethodForFunctionType(group, f) is not null ? 1 : -1;
        }

        if (bound.Type == parameterType)
        {
            return 0;
        }

        if (bound.ConstantValue is long c && parameterType is PrimitiveType { Primitive: PrimitiveKind.Byte } && c is >= 0 and <= 255)
        {
            return 1;
        }

        var conversion = Conversions.ClassifyImplicit(bound.Type, parameterType);
        return conversion.Kind switch
        {
            ConversionKind.None => -1,
            ConversionKind.Identity => 0,
            ConversionKind.Error => 0,
            ConversionKind.ImplicitNumeric or ConversionKind.NullLiteral or ConversionKind.ImplicitNullable => 1,
            _ => 2,
        };
    }

    private ImmutableArray<BoundExpression> BuildArguments(MethodSymbol method, ImmutableArray<PendingArgument?> mapping, bool expanded, TextSpan span)
    {
        var result = ImmutableArray.CreateBuilder<BoundExpression>(method.Parameters.Length);
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var parameter = method.Parameters[i];
            var argument = mapping[i];
            if (argument?.Bound is BoundExpandedParams expandedParams)
            {
                var arrayType = (ArrayTypeSymbol)parameter.Type;
                var elements = expandedParams.Arguments.Select(a => ConvertArgument(a, arrayType.Element)).ToImmutableArray();
                result.Add(new BoundArrayLiteral(span, arrayType, elements));
            }
            else if (argument is null)
            {
                result.Add(DefaultArgument(parameter, span));
            }
            else
            {
                result.Add(ConvertArgument(argument, parameter.Type));
            }
        }

        return result.MoveToImmutable();
    }

    private BoundExpression DefaultArgument(ParameterSymbol parameter, TextSpan span)
    {
        var value = parameter.DefaultValue;
        var literalType = value switch
        {
            long => PrimitiveType.Int,
            double => PrimitiveType.Float,
            bool => PrimitiveType.Bool,
            string => PrimitiveType.String,
            Rune => PrimitiveType.Char,
            _ => (TypeSymbol)NullTypeSymbol.Instance,
        };
        var literal = new BoundLiteral(span, literalType, value is Rune r ? (long)r.Value : value);
        return Convert(literal, parameter.Type, span, isExplicit: false);
    }

    private bool SatisfiesMethodConstraints(MethodSymbol definition, ImmutableArray<TypeSymbol> arguments)
    {
        var map = TypeMap.Create(definition.TypeParameters, arguments);
        for (var i = 0; i < arguments.Length; i++)
        {
            foreach (var constraint in definition.TypeParameters[i].Constraints.OfType<NamedTypeSymbol>())
            {
                var target = (NamedTypeSymbol)constraint.Substitute(map);
                var argument = arguments[i];
                if (!argument.IsError && !Conversions.ClassifyImplicit(argument, target).Exists
                    && !SelfAndInterfaces(argument).Any(t => t.Equals(target)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    #endregion

    #region Type inference

    private ImmutableArray<TypeSymbol>? InferTypeArguments(MethodSymbol definition, PendingArgument?[] mapping, List<PendingArgument> extra, bool expanded)
    {
        var bindings = new Dictionary<TypeParameterSymbol, TypeSymbol>(ReferenceEqualityComparer.Instance);
        var parameters = definition.Parameters;

        IEnumerable<(PendingArgument Argument, TypeSymbol ParameterType)> Pairs()
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                if (expanded && i == parameters.Length - 1)
                {
                    var element = ((ArrayTypeSymbol)parameters[i].Type).Element;
                    foreach (var e in extra)
                    {
                        yield return (e, element);
                    }
                }
                else if (mapping[i] is { } argument)
                {
                    yield return (argument, parameters[i].Type);
                }
            }
        }

        var pairs = Pairs().ToList();

        // Phase 1: ordinary arguments.
        foreach (var (argument, parameterType) in pairs.Where(p => p.Argument.Lambda is null))
        {
            if (argument.Bound is { } bound and not BoundMethodGroup)
            {
                Unify(parameterType, bound.Type, bindings, definition);
            }
        }

        // Phase 2: lambdas whose parameter types are now known contribute their return types.
        for (var round = 0; round < 3; round++)
        {
            foreach (var (argument, parameterType) in pairs.Where(p => p.Argument.Lambda is not null))
            {
                if (parameterType.StripNullable() is not FunctionTypeSymbol functionType)
                {
                    continue;
                }

                var map = TypeMap.Create(bindings.Keys, bindings.Values);
                var parameterTypes = functionType.Parameters.Select(p => p.Substitute(map)).ToImmutableArray();
                if (parameterTypes.Any(t => ContainsTypeParameterOf(t, definition)) && argument.Lambda!.Parameters.Any(p => p.Type is null))
                {
                    continue;
                }

                var provisional = new FunctionTypeSymbol(parameterTypes, ErrorTypeSymbol.Instance);
                var lambda = WithScratchDiagnostics(() => BindLambda(argument.Lambda!, provisional, scratch: true, inferReturn: true));
                if (lambda is not null)
                {
                    Unify(functionType.ReturnType, lambda.FunctionType.ReturnType, bindings, definition);
                }
            }
        }

        if (definition.TypeParameters.Any(p => !bindings.ContainsKey(p)))
        {
            return null;
        }

        return [.. definition.TypeParameters.Select(p => bindings[p])];
    }

    private static bool ContainsTypeParameterOf(TypeSymbol type, MethodSymbol method) => type switch
    {
        TypeParameterSymbol tp => ReferenceEquals(tp.Container, method.OriginalDefinition),
        ArrayTypeSymbol a => ContainsTypeParameterOf(a.Element, method),
        NullableTypeSymbol n => ContainsTypeParameterOf(n.Underlying, method),
        NamedTypeSymbol n => n.IsConstructed && n.TypeArguments.Any(a => ContainsTypeParameterOf(a, method)),
        FunctionTypeSymbol f => f.Parameters.Any(p => ContainsTypeParameterOf(p, method)) || ContainsTypeParameterOf(f.ReturnType, method),
        TupleTypeSymbol t => t.Elements.Any(e => ContainsTypeParameterOf(e, method)),
        _ => false,
    };

    private void Unify(TypeSymbol parameter, TypeSymbol argument, Dictionary<TypeParameterSymbol, TypeSymbol> bindings, MethodSymbol method)
    {
        if (argument.IsError || argument is NullTypeSymbol)
        {
            return;
        }

        switch (parameter)
        {
            case TypeParameterSymbol tp when ReferenceEquals(tp.Container, method.OriginalDefinition):
                if (!bindings.TryGetValue(tp, out var existing))
                {
                    bindings[tp] = argument;
                }
                else if (existing != argument && Conversions.ClassifyImplicit(existing, argument).Exists)
                {
                    bindings[tp] = argument; // widen (e.g. int then float → float)
                }

                return;
            case NullableTypeSymbol n:
                Unify(n.Underlying, argument.StripNullable(), bindings, method);
                return;
            case ArrayTypeSymbol pa when argument is ArrayTypeSymbol aa:
                Unify(pa.Element, aa.Element, bindings, method);
                return;
            case TupleTypeSymbol pt when argument is TupleTypeSymbol at && pt.Elements.Length == at.Elements.Length:
                foreach (var (p, a) in pt.Elements.Zip(at.Elements))
                {
                    Unify(p, a, bindings, method);
                }

                return;
            case FunctionTypeSymbol pf when argument is FunctionTypeSymbol af && pf.Parameters.Length == af.Parameters.Length:
                foreach (var (p, a) in pf.Parameters.Zip(af.Parameters))
                {
                    Unify(p, a, bindings, method);
                }

                Unify(pf.ReturnType, af.ReturnType, bindings, method);
                return;
            case NamedTypeSymbol pn when pn.IsConstructed:
                var match = SelfAndInterfaces(argument).FirstOrDefault(t => ReferenceEquals(t.Definition, pn.Definition));
                if (match is not null && match.IsConstructed)
                {
                    foreach (var (p, a) in pn.TypeArguments.Zip(match.TypeArguments))
                    {
                        Unify(p, a, bindings, method);
                    }
                }

                return;
        }
    }

    private T WithScratchDiagnostics<T>(Func<T> action)
    {
        var saved = _diagnostics;
        var savedScope = _scope;
        var savedFunction = _function;
        var savedNonNull = _nonNull;
        _diagnostics = new DiagnosticBag();
        try
        {
            var result = action();
            return _diagnostics.HasErrors ? default! : result;
        }
        finally
        {
            _diagnostics = saved;
            _scope = savedScope;
            _function = savedFunction;
            _nonNull = savedNonNull;
        }
    }

    #endregion

    #region Lambdas and method references

    /// <summary>
    /// Binds a lambda against an optional target function type. Without a target, parameter types must be explicit and the
    /// return type is inferred. With <paramref name="inferReturn"/>, the target's return type is ignored and inferred.
    /// </summary>
    private BoundLambda? BindLambda(LambdaExpr lambda, FunctionTypeSymbol? target, bool scratch, bool inferReturn = false)
    {
        if (target is not null && target.Parameters.Length != lambda.Parameters.Length)
        {
            Report(DiagnosticCodes.CannotConvert, lambda.Span, "lambda", target);
            return null;
        }

        var method = new MethodSymbol($"<lambda>{++_lambdaCounter}", MethodKind.Lambda)
        {
            IsAsync = lambda.IsAsync, Syntax = lambda, Tree = _tree, Container = _function.Method,
        };

        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        for (var i = 0; i < lambda.Parameters.Length; i++)
        {
            var p = lambda.Parameters[i];
            TypeSymbol type;
            if (p.Type is not null)
            {
                type = ResolveType(p.Type);
                if (target is not null && type != target.Parameters[i] && !target.Parameters[i].IsError)
                {
                    Report(DiagnosticCodes.CannotConvert, p.Span, type, target.Parameters[i]);
                }
            }
            else if (target is not null)
            {
                type = target.Parameters[i];
            }
            else
            {
                Report(DiagnosticCodes.LambdaNeedsTarget, lambda.Span);
                return null;
            }

            parameters.Add(new ParameterSymbol(p.Name, type, i, false, false) { Container = method, Syntax = p, Tree = _tree });
        }

        method.Parameters = parameters.ToImmutable();

        // Inside the lambda, 'return' produces the target's return type (or the T of Task<T> for async lambdas).
        TypeSymbol? declaredReturn = null;
        if (target is not null && !inferReturn && !target.ReturnType.IsError)
        {
            declaredReturn = target.ReturnType;
        }

        method.ReturnType = declaredReturn ?? PrimitiveType.Void;

        var savedFunction = _function;
        var savedScope = _scope;
        var savedLoop = (_loopDepth, _breakableDepth, _catchDepth);
        _function = new FunctionContext(method, savedFunction, savedFunction.IsStatic) { ReturnType = declaredReturn };
        if (lambda.IsAsync)
        {
            _function.Flavor = FunctionFlavor.Async;
        }

        _scope = new Scope(savedScope, _function);
        (_loopDepth, _breakableDepth, _catchDepth) = (0, 0, 0);
        BoundBlock body;
        TypeSymbol returnType;
        try
        {
            foreach (var parameter in method.Parameters)
            {
                Declare(parameter, parameter.Syntax?.Span ?? lambda.Span);
            }

            var bodyReturn = declaredReturn is null ? null : EffectiveReturnType();
            if (lambda.ExpressionBody is not null)
            {
                BoundExpression value;
                if (bodyReturn is null)
                {
                    value = BindValueOrVoid(lambda.ExpressionBody);
                    returnType = WrapAsync(lambda.IsAsync, value.Type);
                }
                else if (bodyReturn == PrimitiveType.Void)
                {
                    value = BindExpression(lambda.ExpressionBody);
                    returnType = declaredReturn!;
                }
                else
                {
                    value = BindExpression(lambda.ExpressionBody, bodyReturn);
                    returnType = declaredReturn!;
                }

                body = new BoundBlock(lambda.ExpressionBody.Span,
                [
                    value.Type == PrimitiveType.Void || bodyReturn == PrimitiveType.Void
                        ? new BoundExpressionStatement(value.Span, value)
                        : new BoundReturn(value.Span, value),
                ]);
            }
            else
            {
                body = BindBlock(lambda.BlockBody!);
                if (declaredReturn is null)
                {
                    var inferred = _function.InferredReturnTypes.Count == 0 ? PrimitiveType.Void : BestCommonType(_function.InferredReturnTypes);
                    if (inferred is null)
                    {
                        Report(DiagnosticCodes.LambdaNeedsTarget, lambda.Span);
                        inferred = ErrorTypeSymbol.Instance;
                    }

                    returnType = WrapAsync(lambda.IsAsync, inferred);
                }
                else
                {
                    returnType = declaredReturn;
                    if (bodyReturn != PrimitiveType.Void && !bodyReturn!.IsError && ControlFlow.CanCompleteNormally(body))
                    {
                        Report(DiagnosticCodes.NotAllPathsReturn, lambda.Span, "lambda");
                    }
                }
            }

            method.ReturnType = returnType;
            var functionType = new FunctionTypeSymbol([.. method.Parameters.Select(p => p.Type)], returnType);
            var bound = new BoundLambda(lambda.Span, method, body, functionType);
            if (!scratch)
            {
                _lambdas.Add(new BoundFunction(method, body, _function.Flavor, null, [.. _function.CapturedLocals],
                    [.. _function.CapturedParameters], _function.CapturesThis));
            }

            return bound;
        }
        finally
        {
            _function = savedFunction;
            _scope = savedScope;
            (_loopDepth, _breakableDepth, _catchDepth) = savedLoop;
        }
    }

    private BoundExpression BindValueOrVoid(Expr expr)
    {
        var bound = BindExpression(expr);
        return bound.Type == PrimitiveType.Void ? bound : BindValue(bound);
    }

    private TypeSymbol WrapAsync(bool isAsync, TypeSymbol inner)
    {
        if (!isAsync)
        {
            return inner;
        }

        if (inner == PrimitiveType.Void)
        {
            return (TypeSymbol?)_wk.Task ?? ErrorTypeSymbol.Instance;
        }

        return _wk.TaskOfT?.Construct([inner]) ?? (TypeSymbol)ErrorTypeSymbol.Instance;
    }

    private BoundExpression BindMethodReference(BoundMethodGroup group, FunctionTypeSymbol target, TextSpan span)
    {
        var method = FindMethodForFunctionType(group, target);
        if (method is null)
        {
            return Error(span, DiagnosticCodes.CannotConvert, $"method group '{group.Methods[0].Name}'", target);
        }

        var receiver = method.IsStatic ? null : group.Receiver;
        if (!method.IsStatic && receiver is null)
        {
            return Error(span, DiagnosticCodes.StaticMismatch, method.Name, "is an instance method and needs an object");
        }

        return new BoundMethodReference(span, receiver, method, target);
    }

    private static MethodSymbol? FindMethodForFunctionType(BoundMethodGroup group, FunctionTypeSymbol target) =>
        group.Methods.FirstOrDefault(m => m.TypeParameters.IsEmpty
                                          && m.Parameters.Length == target.Parameters.Length
                                          && m.Parameters.Zip(target.Parameters).All(p => p.First.Type == p.Second)
                                          && (m.ReturnType == target.ReturnType || Conversions.ClassifyImplicit(m.ReturnType, target.ReturnType).IsImplicit));

    #endregion

    #region Operators

    private BoundExpression BindUnary(UnaryExpr unary)
    {
        var span = unary.Span;
        switch (unary.Operator)
        {
            case UnaryOperator.NullForgiving:
            {
                var operand = BindValue(BindExpression(unary.Operand));
                return operand.Type is NullableTypeSymbol n
                    ? new BoundConversion(span, operand, ConversionKind.ExplicitNullable, n.Underlying)
                    : operand;
            }

            case UnaryOperator.PreIncrement or UnaryOperator.PreDecrement or UnaryOperator.PostIncrement or UnaryOperator.PostDecrement:
            {
                var target = BindValue(BindExpression(unary.Operand));
                if (!CheckAssignable(target, unary.Operand.Span))
                {
                    return new BoundErrorExpression(span);
                }

                if (target.Type is not PrimitiveType { IsNumeric: true } && !target.Type.IsError)
                {
                    return Error(span, DiagnosticCodes.UnaryOperatorNotDefined, Operators.Symbol(unary.Operator), target.Type);
                }

                var isIncrement = unary.Operator is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement;
                var isPostfix = unary.Operator is UnaryOperator.PostIncrement or UnaryOperator.PostDecrement;
                return new BoundIncrement(span, target, isIncrement, isPostfix);
            }

            case UnaryOperator.Plus:
            {
                var operand = BindValue(BindExpression(unary.Operand));
                return operand.Type is PrimitiveType { IsNumeric: true } || operand.Type.IsError
                    ? operand
                    : Error(span, DiagnosticCodes.UnaryOperatorNotDefined, "+", operand.Type);
            }

            default:
            {
                var operand = BindValue(BindExpression(unary.Operand));
                if (operand.Type.IsError)
                {
                    return new BoundErrorExpression(span);
                }

                var resolved = Operators.ResolveUnary(unary.Operator, operand.Type);
                if (resolved is null)
                {
                    return Error(span, DiagnosticCodes.UnaryOperatorNotDefined, Operators.Symbol(unary.Operator), operand.Type);
                }

                var converted = Convert(operand, resolved.Value.Operand, unary.Operand.Span, isExplicit: false);
                return new BoundUnary(span, resolved.Value.Kind, converted, resolved.Value.Operand);
            }
        }
    }

    private BoundExpression BindBinary(BinaryExpr binary)
    {
        var span = binary.Span;
        switch (binary.Operator)
        {
            case BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr:
            {
                var isAnd = binary.Operator == BinaryOperator.LogicalAnd;
                var left = BindCondition(binary.Left);
                var savedNonNull = _nonNull;
                var (whenTrue, whenFalse) = NullNarrowing(left);
                _nonNull = _nonNull.Union(isAnd ? whenTrue : whenFalse);
                var right = BindCondition(binary.Right);
                _nonNull = savedNonNull;
                return new BoundLogical(span, isAnd, left, right);
            }

            case BinaryOperator.Coalesce:
            {
                var left = BindValue(BindExpression(binary.Left));
                var underlying = left.Type.StripNullable();
                if (!left.Type.IsNullable && !left.Type.IsReferenceType && !left.Type.IsError)
                {
                    return Error(span, DiagnosticCodes.BinaryOperatorNotDefined, "??", left.Type, "?");
                }

                var right = BindExpressionCore(binary.Right, underlying);
                right = BindValue(right);
                TypeSymbol resultType = Conversions.ClassifyImplicit(right.Type, underlying).Exists || right is BoundThrowExpression
                    ? underlying
                    : right.Type.IsNullable && Conversions.ClassifyImplicit(underlying, right.Type.StripNullable()).Exists
                        ? right.Type
                        : underlying;
                return new BoundCoalesce(span, left, Convert(right, resultType, binary.Right.Span, isExplicit: false), resultType);
            }
        }

        var l = BindValue(BindExpression(binary.Left));
        var r = BindValue(BindExpression(binary.Right));
        return BindBinaryOperator(binary.Operator, l, r, span);
    }

    private BoundExpression BindBinaryOperator(BinaryOperator op, BoundExpression left, BoundExpression right, TextSpan span)
    {
        if (left.Type.IsError || right.Type.IsError)
        {
            return new BoundErrorExpression(span);
        }

        if (left.Type == PrimitiveType.Void || right.Type == PrimitiveType.Void)
        {
            return Error(left.Type == PrimitiveType.Void ? left.Span : right.Span, DiagnosticCodes.VoidHasNoValue);
        }

        var signature = Operators.ResolveBinary(op, left.Type, right.Type);
        if (signature is null)
        {
            return Error(span, DiagnosticCodes.BinaryOperatorNotDefined, Operators.Symbol(op), left.Type, right.Type);
        }

        var s = signature.Value;
        var convertedLeft = s.Kind == BinaryOperatorKind.StringConcat ? left : Convert(left, s.Left, left.Span, isExplicit: false);
        var convertedRight = s.Kind == BinaryOperatorKind.StringConcat ? right : Convert(right, s.Right, right.Span, isExplicit: false);
        return new BoundBinary(span, s.Kind, convertedLeft, convertedRight, s.Result);
    }

    private BoundExpression BindAssignment(AssignmentExpr assignment)
    {
        var span = assignment.Span;
        var target = BindValue(BindExpression(assignment.Target));
        if (!CheckAssignable(target, assignment.Target.Span))
        {
            BindExpression(assignment.Value);
            return new BoundErrorExpression(span);
        }

        if (assignment.Operator is null)
        {
            var value = BindExpression(assignment.Value, target.Type);
            NarrowAfterAssignment(target, value);
            return new BoundAssignment(span, target, value);
        }

        // Function-typed targets: += combines handlers, -= removes them (CForms events).
        if (target.Type.StripNullable() is FunctionTypeSymbol functionType
            && assignment.Operator is BinaryOperator.Add or BinaryOperator.Subtract)
        {
            var handler = BindExpression(assignment.Value, functionType);
            return new BoundCompoundAssignment(span, target, null, assignment.Operator == BinaryOperator.Subtract, handler);
        }

        if (assignment.Operator == BinaryOperator.Coalesce)
        {
            var fallback = BindExpression(assignment.Value, target.Type.StripNullable());
            return new BoundCompoundAssignment(span, target, null, false, fallback, IsCoalesce: true);
        }

        var right = BindValue(BindExpression(assignment.Value));
        var signature = Operators.ResolveBinary(assignment.Operator.Value, target.Type, right.Type);
        if (signature is null)
        {
            return target.Type.IsError || right.Type.IsError
                ? new BoundErrorExpression(span)
                : Error(span, DiagnosticCodes.BinaryOperatorNotDefined, Operators.Symbol(assignment.Operator.Value) + "=", target.Type, right.Type);
        }

        // Compound assignment narrows implicitly back to the target type (b += 1 on a byte), like C#.
        if (!Conversions.Classify(signature.Value.Result, target.Type).Exists && signature.Value.Kind != BinaryOperatorKind.StringConcat)
        {
            return Error(span, DiagnosticCodes.CannotConvert, signature.Value.Result, target.Type);
        }

        var convertedRight = signature.Value.Kind == BinaryOperatorKind.StringConcat
            ? right
            : Convert(right, signature.Value.Right, right.Span, isExplicit: false);
        return new BoundCompoundAssignment(span, target, signature.Value.Kind, false, convertedRight);
    }

    private bool CheckAssignable(BoundExpression target, TextSpan span)
    {
        switch (target)
        {
            case BoundErrorExpression:
                return false;
            case BoundLocal { Local.IsConst: true } local:
                Report(DiagnosticCodes.AssignToConstant, span, local.Local.Name);
                return false;
            case BoundField { Field.IsConst: true } field:
                Report(DiagnosticCodes.AssignToConstant, span, field.Field.Name);
                return false;
            case BoundConversion { Kind: ConversionKind.ExplicitNullable, Operand: BoundLocal or BoundParameter }:
                return true;
            case BoundLocal or BoundParameter or BoundField or BoundArrayElement:
                return true;
            case BoundProperty property when property.Property.Setter is not null || property.Property.BackingField is not null
                                             && (property.Property.Setter is not null || IsInsideConstructorOf(property.Property)):
                return true;
            default:
                Report(DiagnosticCodes.NotAssignable, span, Describe(target));
                return false;
        }
    }

    private bool IsInsideConstructorOf(PropertySymbol property) =>
        OutermostMethod().MethodKind == MethodKind.Constructor && property.Container is NamedTypeSymbol t
        && _containingType is not null && ReferenceEquals(t.Definition, _containingType.Definition);

    private void NarrowAfterAssignment(BoundExpression target, BoundExpression value)
    {
        var symbol = target switch
        {
            BoundLocal l => (Symbol)l.Local,
            BoundParameter p => p.Parameter,
            BoundConversion { Operand: BoundLocal l } => l.Local,
            BoundConversion { Operand: BoundParameter p } => p.Parameter,
            _ => null,
        };
        if (symbol is null)
        {
            return;
        }

        var valueIsNonNull = !value.Type.IsNullable && value.Type is not NullTypeSymbol && value is not BoundLiteral { Value: null };
        _nonNull = valueIsNonNull ? _nonNull.Add(symbol) : _nonNull.Remove(symbol);
    }

    /// <summary>Variables proven non-null when <paramref name="condition"/> is true / false.</summary>
    private static (ImmutableHashSet<Symbol> WhenTrue, ImmutableHashSet<Symbol> WhenFalse) NullNarrowing(BoundExpression condition)
    {
        var empty = ImmutableHashSet.Create<Symbol>(ReferenceEqualityComparer.Instance);
        switch (condition)
        {
            case BoundBinary { Operator: BinaryOperatorKind.NotEqual or BinaryOperatorKind.Equal } b
                when (NullTestTarget(b.Left, b.Right) ?? NullTestTarget(b.Right, b.Left)) is { } symbol:
                return b.Operator == BinaryOperatorKind.NotEqual ? (empty.Add(symbol), empty) : (empty, empty.Add(symbol));
            case BoundUnary { Operator: UnaryOperatorKind.BoolNot } not:
            {
                var (t, f) = NullNarrowing(not.Operand);
                return (f, t);
            }

            case BoundLogical { IsAnd: true } and:
            {
                var (lt, _) = NullNarrowing(and.Left);
                var (rt, _) = NullNarrowing(and.Right);
                return (lt.Union(rt), empty);
            }

            case BoundLogical { IsAnd: false } or:
            {
                var (_, lf) = NullNarrowing(or.Left);
                var (_, rf) = NullNarrowing(or.Right);
                return (empty, lf.Union(rf));
            }

            case BoundIsPattern { Pattern: BoundTypePattern or BoundConstantPattern { Value.ConstantValue: not null } } isPattern
                when Variable(isPattern.Operand) is { } tested:
                return (empty.Add(tested), empty);
            case BoundIsPattern { Pattern: BoundConstantPattern { Value: BoundLiteral { Value: null } } } isNull
                when Variable(isNull.Operand) is { } testedNull:
                return (empty, empty.Add(testedNull));
            default:
                return (empty, empty);
        }
    }

    private static Symbol? NullTestTarget(BoundExpression candidate, BoundExpression other) =>
        other is BoundLiteral { Value: null } or BoundConversion { Operand: BoundLiteral { Value: null } } ? Variable(candidate) : null;

    private static Symbol? Variable(BoundExpression expr) => expr switch
    {
        BoundLocal l => l.Local,
        BoundParameter p => p.Parameter,
        BoundConversion { Operand: var inner } => Variable(inner),
        _ => null,
    };

    #endregion

    #region Conditional, cast, creation, arrays, tuples

    private BoundExpression BindConditional(ConditionalExpr conditional, TypeSymbol? target)
    {
        var condition = BindCondition(conditional.Condition);
        var (whenTrueSet, whenFalseSet) = NullNarrowing(condition);
        var saved = _nonNull;
        _nonNull = saved.Union(whenTrueSet);
        var whenTrue = BindValue(BindExpressionCore(conditional.WhenTrue, target));
        _nonNull = saved.Union(whenFalseSet);
        var whenFalse = BindValue(BindExpressionCore(conditional.WhenFalse, target));
        _nonNull = saved;

        var type = target ?? BestCommonType([whenTrue.Type, whenFalse.Type]);
        if (type is null)
        {
            return Error(conditional.Span, DiagnosticCodes.CannotConvert, whenFalse.Type, whenTrue.Type);
        }

        return new BoundConditional(conditional.Span, condition,
            Convert(whenTrue, type, conditional.WhenTrue.Span, false), Convert(whenFalse, type, conditional.WhenFalse.Span, false), type);
    }

    /// <summary>The type every candidate converts to implicitly; nulls lift reference/value types to nullable.</summary>
    private static TypeSymbol? BestCommonType(IReadOnlyList<TypeSymbol> types)
    {
        var real = types.Where(t => t is not NullTypeSymbol && !t.IsError).Distinct().ToList();
        var sawNull = types.Any(t => t is NullTypeSymbol);
        if (real.Count == 0)
        {
            return types.Any(t => t.IsError) ? ErrorTypeSymbol.Instance : sawNull ? null : PrimitiveType.Void;
        }

        var best = real.FirstOrDefault(candidate => real.All(t => Conversions.ClassifyImplicit(t, candidate).Exists));
        if (best is null)
        {
            return null;
        }

        return sawNull ? NullableTypeSymbol.Create(best) : best;
    }

    private BoundExpression BindCast(CastExpr cast)
    {
        var type = ResolveType(cast.Type);
        var operand = BindValue(BindExpressionCore(cast.Operand, type));
        return Convert(operand, type, cast.Span, isExplicit: true);
    }

    private BoundExpression BindNew(NewExpr creation)
    {
        var span = creation.Span;
        var type = ResolveType(creation.Type);
        var arguments = BindArguments(creation.Arguments);
        if (type.IsError)
        {
            BindArgumentsForErrors(arguments);
            return new BoundErrorExpression(span);
        }

        if (type is not NamedTypeSymbol named || named.TypeKind is TypeKind.Interface or TypeKind.Enum)
        {
            BindArgumentsForErrors(arguments);
            var reason = type is NamedTypeSymbol { TypeKind: TypeKind.Interface } ? " (it is an interface)" : string.Empty;
            return Error(span, DiagnosticCodes.CannotInstantiate, type, reason);
        }

        var constructors = named.GetDeclaredMembers(named.Name).OfType<MethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor).ToImmutableArray();
        MethodSymbol? constructor = null;
        ImmutableArray<BoundExpression> boundArgs = [];
        if (constructors.IsEmpty)
        {
            if (named.TypeKind == TypeKind.Class && named.IsNative)
            {
                BindArgumentsForErrors(arguments);
                return Error(span, DiagnosticCodes.CannotInstantiate, named, " (it has no public constructor)");
            }

            if (!arguments.IsEmpty)
            {
                BindArgumentsForErrors(arguments);
                return Error(span, DiagnosticCodes.NoMatchingConstructor, named, DescribeArguments(arguments));
            }
        }
        else
        {
            var resolved = ResolveOverload(constructors, arguments, [], span, named.Name, named.ToString());
            if (resolved is null)
            {
                return new BoundErrorExpression(span);
            }

            (constructor, boundArgs) = resolved.Value;
            CheckAccessible(constructor, span);
        }

        var initializers = ImmutableArray.CreateBuilder<BoundExpression>();
        var placeholder = new BoundConditionalReceiver(span, named);
        foreach (var init in creation.Initializers)
        {
            var memberName = ((NameExpr)init.Target).Name;
            var member = BindMemberOf(placeholder, memberName, [], false, init.Target.Span);
            if (member is BoundErrorExpression)
            {
                continue;
            }

            if (CheckAssignable(member, init.Target.Span))
            {
                initializers.Add(new BoundAssignment(init.Span, member, BindExpression(init.Value, member.Type)));
            }
        }

        return new BoundObjectCreation(span, named, constructor, boundArgs, initializers.ToImmutable());
    }

    private BoundExpression BindNewArray(NewArrayExpr array)
    {
        var element = ResolveType(array.ElementType);
        if (element.IsReferenceType && element is not NullableTypeSymbol && !element.IsError)
        {
            // new string[n] starts filled with nulls; require the nullable element type to keep null-safety honest.
            Report(DiagnosticCodes.NullNotAllowed, array.ElementType.Span, element);
        }

        var length = BindExpression(array.Length, PrimitiveType.Int);
        return new BoundArrayCreation(array.Span, new ArrayTypeSymbol(element), length);
    }

    private BoundExpression BindArrayLiteral(ArrayLiteralExpr array, TypeSymbol? target)
    {
        var targetArray = target?.StripNullable() as ArrayTypeSymbol;
        if (targetArray is not null)
        {
            var converted = array.Elements.Select(e => BindExpression(e, targetArray.Element)).ToImmutableArray();
            return new BoundArrayLiteral(array.Span, targetArray, converted);
        }

        var elements = array.Elements.Select(e => BindValue(BindExpression(e))).ToImmutableArray();
        var elementType = BestCommonType(elements.Select(e => e.Type).ToList());
        if (elements.IsEmpty || elementType is null || elementType == PrimitiveType.Void)
        {
            return Error(array.Span, DiagnosticCodes.CannotInferTypeArguments, "array literal");
        }

        var arrayType = new ArrayTypeSymbol(elementType);
        return new BoundArrayLiteral(array.Span, arrayType, [.. elements.Select(e => Convert(e, elementType, e.Span, false))]);
    }

    private BoundExpression BindTuple(TupleExpr tuple, TypeSymbol? target)
    {
        var targetTuple = target?.StripNullable() as TupleTypeSymbol;
        var elements = tuple.Elements.Select((e, i) =>
            targetTuple is not null && i < targetTuple.Elements.Length
                ? BindExpression(e, targetTuple.Elements[i])
                : BindValue(BindExpression(e))).ToImmutableArray();
        var type = new TupleTypeSymbol([.. elements.Select(e => e.Type)]);
        return new BoundTupleLiteral(tuple.Span, type, elements);
    }

    private BoundExpression BindElementAccess(ElementAccessExpr access)
    {
        var span = access.Span;
        var receiver = BindValue(BindExpression(access.Target));
        if (receiver.Type.IsError)
        {
            return new BoundErrorExpression(span);
        }

        if (receiver.Type is NullableTypeSymbol)
        {
            Report(DiagnosticCodes.PossibleNullDereference, receiver.Span, Describe(receiver));
        }

        var type = receiver.Type.StripNullable();
        if (type is ArrayTypeSymbol arrayType && access.Indices.Length == 1)
        {
            return new BoundArrayElement(span, receiver, BindExpression(access.Indices[0], PrimitiveType.Int), arrayType.Element);
        }

        var indexers = MemberSourceOf(type)?.LookupMembers("this[]").OfType<PropertySymbol>().ToList() ?? [];
        if (indexers.Count == 0)
        {
            return Error(span, DiagnosticCodes.MemberNotFound, type, "this[]");
        }

        var arguments = BindArguments([.. access.Indices.Select(i => new Argument(i.Span, null, i))]);
        var getters = indexers.Select(p => p.Getter ?? p.Setter!).ToImmutableArray();
        var resolved = ResolveOverload(getters, arguments, [], span, "indexer", null);
        if (resolved is null)
        {
            return new BoundErrorExpression(span);
        }

        var indexer = indexers.First(p => ReferenceEquals(p.Getter ?? p.Setter, resolved.Value.Method));
        var args = resolved.Value.Arguments.Take(indexer.Parameters.Length).ToImmutableArray();
        return new BoundProperty(span, receiver, indexer, args);
    }

    private BoundExpression BindAwait(AwaitExpr await)
    {
        var span = await.Span;
        var operand = BindValue(BindExpression(await.Operand));
        if (!_function.IsAsync)
        {
            Report(DiagnosticCodes.AwaitOutsideAsync, span);
        }

        if (operand.Type.IsError)
        {
            return new BoundErrorExpression(span);
        }

        if (operand.Type is NamedTypeSymbol n && ReferenceEquals(n.Definition, _wk.TaskOfT))
        {
            return new BoundAwait(span, operand, n.TypeArguments[0]);
        }

        if (operand.Type is NamedTypeSymbol t && ReferenceEquals(t.Definition, _wk.Task))
        {
            return new BoundAwait(span, operand, PrimitiveType.Void);
        }

        return Error(span, DiagnosticCodes.NotAwaitable, operand.Type);
    }

    #endregion

    #region Patterns and switch expressions

    private BoundExpression BindIs(IsPatternExpr isPattern)
    {
        var operand = BindValue(BindExpression(isPattern.Operand));
        var pattern = BindPattern(isPattern.Pattern, operand.Type);
        return new BoundIsPattern(isPattern.Span, operand, pattern);
    }

    private BoundPattern BindPattern(Pattern pattern, TypeSymbol subjectType)
    {
        switch (pattern)
        {
            case DiscardPattern discard:
                return new BoundDiscardPattern(discard.Span);

            case VarPattern varPattern:
            {
                var local = new LocalSymbol(varPattern.Designation, subjectType, isConst: false)
                {
                    Syntax = varPattern, Tree = _tree, Container = _function.Method,
                };
                Declare(local, varPattern.Span);
                return new BoundTypePattern(varPattern.Span, NullableTypeSymbol.Create(subjectType), local);
            }

            case TypePattern typePattern:
                return BindTypePattern(typePattern.Span, ResolveType(typePattern.Type), typePattern.Designation, subjectType);

            case RelationalPattern relational:
            {
                var value = BindValue(BindExpression(relational.Value));
                var signature = Operators.ResolveBinary(relational.Operator, subjectType.StripNullable(), value.Type);
                if (signature is null || signature.Value.Result != PrimitiveType.Bool)
                {
                    Report(DiagnosticCodes.BinaryOperatorNotDefined, relational.Span, Operators.Symbol(relational.Operator), subjectType, value.Type);
                    return new BoundDiscardPattern(relational.Span);
                }

                return new BoundRelationalPattern(relational.Span, signature.Value.Kind, Convert(value, signature.Value.Right, value.Span, false));
            }

            case ConstantPattern constant:
            {
                // 'case Packet:' is syntactically a constant but names a type.
                if (constant.Value is NameExpr or MemberAccessExpr)
                {
                    var probe = WithScratchDiagnostics(() => BindExpressionCore(constant.Value, null));
                    if (probe is BoundTypeExpression typeExpression)
                    {
                        return BindTypePattern(constant.Span, typeExpression.ReferencedType, null, subjectType);
                    }
                }

                var value = BindExpressionCore(constant.Value, subjectType);
                value = value is BoundLiteral { Value: null } ? value : Convert(value, subjectType.StripNullable(), constant.Value.Span, false);
                if (value is BoundLiteral { Value: null } && !subjectType.IsNullable && !subjectType.IsReferenceType && !subjectType.IsError)
                {
                    Report(DiagnosticCodes.PatternNeverMatches, constant.Span, "null", subjectType);
                }
                else if (value.ConstantValue is null && value is not BoundLiteral { Value: null } && !value.Type.IsError)
                {
                    Report(DiagnosticCodes.NotConstant, constant.Span, "pattern");
                }

                return new BoundConstantPattern(constant.Span, value);
            }

            default:
                return new BoundDiscardPattern(pattern.Span);
        }
    }

    private BoundPattern BindTypePattern(TextSpan span, TypeSymbol matched, string? designation, TypeSymbol subjectType)
    {
        if (!matched.IsError && !subjectType.IsError && !Conversions.Classify(subjectType.StripNullable(), matched).Exists
            && !Conversions.Classify(matched, subjectType.StripNullable()).Exists)
        {
            Report(DiagnosticCodes.PatternNeverMatches, span, matched, subjectType);
        }

        LocalSymbol? local = null;
        if (designation is not null)
        {
            local = new LocalSymbol(designation, matched, isConst: false) { Tree = _tree, Container = _function.Method };
            Declare(local, span);
        }

        return new BoundTypePattern(span, matched, local);
    }

    private BoundExpression BindSwitchExpression(SwitchExpr switchExpr, TypeSymbol? target)
    {
        var subject = BindValue(BindExpression(switchExpr.Subject));
        var arms = new List<(SwitchArm Syntax, BoundPattern Pattern, BoundExpression? Guard, BoundExpression Result, ImmutableArray<LocalSymbol> Locals)>();
        foreach (var arm in switchExpr.Arms)
        {
            var saved = _scope;
            _scope = new Scope(_scope, _function);
            var pattern = BindPattern(arm.Pattern, subject.Type);
            var guard = arm.Guard is null ? null : BindCondition(arm.Guard);
            var result = BindValue(BindExpressionCore(arm.Result, target));
            var locals = pattern is BoundTypePattern { Designation: { } d } ? ImmutableArray.Create(d) : [];
            _scope = saved;
            arms.Add((arm, pattern, guard, result, locals));
        }

        var type = target ?? BestCommonType(arms.Where(a => a.Result is not BoundThrowExpression).Select(a => a.Result.Type).ToList());
        if (type is null)
        {
            return Error(switchExpr.Span, DiagnosticCodes.CannotInferTypeArguments, "switch expression");
        }

        var bound = arms.Select(a => new BoundSwitchArm(a.Syntax.Span, a.Pattern, a.Guard, Convert(a.Result, type, a.Syntax.Result.Span, false), a.Locals))
            .ToImmutableArray();
        return new BoundSwitchExpression(switchExpr.Span, subject, bound, type);
    }

    #endregion
}
