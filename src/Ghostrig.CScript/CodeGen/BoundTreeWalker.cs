using System.Collections.Immutable;
using Ghostrig.CScript.Semantics;

namespace Ghostrig.CScript.CodeGen;

/// <summary>Enumerates bound-tree children; used for free-variable (closure capture) analysis.</summary>
internal static class BoundTreeWalker
{
    public static IEnumerable<BoundNode> Children(BoundNode node)
    {
        switch (node)
        {
            case BoundField f when f.Receiver is not null: yield return f.Receiver; break;
            case BoundProperty p:
                if (p.Receiver is not null) yield return p.Receiver;
                foreach (var a in p.Arguments) yield return a;
                break;
            case BoundArrayElement e: yield return e.Array; yield return e.Index; break;
            case BoundTupleElement t: yield return t.Tuple; break;
            case BoundCall c:
                if (c.Receiver is not null) yield return c.Receiver;
                foreach (var a in c.Arguments) yield return a;
                break;
            case BoundFunctionInvocation fi:
                yield return fi.Target;
                foreach (var a in fi.Arguments) yield return a;
                break;
            case BoundMethodReference mr when mr.Receiver is not null: yield return mr.Receiver; break;
            case BoundObjectCreation o:
                foreach (var a in o.Arguments) yield return a;
                foreach (var i in o.Initializers) yield return i;
                break;
            case BoundArrayCreation ac: yield return ac.Length; break;
            case BoundArrayLiteral al: foreach (var e in al.Elements) yield return e; break;
            case BoundTupleLiteral tl: foreach (var e in tl.Elements) yield return e; break;
            case BoundUnary u: yield return u.Operand; break;
            case BoundBinary b: yield return b.Left; yield return b.Right; break;
            case BoundLogical l: yield return l.Left; yield return l.Right; break;
            case BoundCoalesce co: yield return co.Left; yield return co.Right; break;
            case BoundConditionalAccess ca: yield return ca.Receiver; yield return ca.Access; break;
            case BoundAssignment a: yield return a.Target; yield return a.Value; break;
            case BoundCompoundAssignment ca2: yield return ca2.Target; yield return ca2.Value; break;
            case BoundIncrement inc: yield return inc.Target; break;
            case BoundConversion cv: yield return cv.Operand; break;
            case BoundConditional cd: yield return cd.Condition; yield return cd.WhenTrue; yield return cd.WhenFalse; break;
            case BoundIsPattern ip: yield return ip.Operand; yield return ip.Pattern; break;
            case BoundSwitchExpression se:
                yield return se.Subject;
                foreach (var arm in se.Arms)
                {
                    yield return arm.Pattern;
                    if (arm.Guard is not null) yield return arm.Guard;
                    yield return arm.Result;
                }

                break;
            case BoundLambda lambda: yield return lambda.Body; break;
            case BoundAwait aw: yield return aw.Operand; break;
            case BoundInterpolatedString s:
                foreach (var part in s.Parts.Where(p => p.Value is not null)) yield return part.Value!;
                break;
            case BoundThrowExpression te: yield return te.Error; break;
            case BoundConstantPattern cp: yield return cp.Value; break;
            case BoundRelationalPattern rp: yield return rp.Value; break;
            case BoundBlock block: foreach (var s in block.Statements) yield return s; break;
            case BoundLocalDeclaration ld when ld.Initializer is not null: yield return ld.Initializer; break;
            case BoundDeconstruction d: yield return d.Value; break;
            case BoundExpressionStatement es: yield return es.Expression; break;
            case BoundIf i:
                yield return i.Condition; yield return i.Then;
                if (i.Else is not null) yield return i.Else;
                break;
            case BoundWhile w: yield return w.Condition; yield return w.Body; break;
            case BoundFor fr:
                if (fr.Initializer is not null) yield return fr.Initializer;
                if (fr.Condition is not null) yield return fr.Condition;
                foreach (var inc2 in fr.Increments) yield return inc2;
                yield return fr.Body;
                break;
            case BoundForeach fe: yield return fe.Collection; yield return fe.Body; break;
            case BoundSwitch sw:
                yield return sw.Subject;
                foreach (var section in sw.Sections)
                {
                    foreach (var (pattern, guard) in section.Labels)
                    {
                        if (pattern is not null) yield return pattern;
                        if (guard is not null) yield return guard;
                    }

                    foreach (var st in section.Statements) yield return st;
                }

                break;
            case BoundReturn r when r.Value is not null: yield return r.Value; break;
            case BoundThrow t when t.Error is not null: yield return t.Error; break;
            case BoundTry tr:
                yield return tr.Body;
                foreach (var c in tr.Catches) yield return c.Body;
                if (tr.Finally is not null) yield return tr.Finally;
                break;
            case BoundYieldReturn yr: yield return yr.Value; break;
        }
    }

    public static IEnumerable<BoundNode> DescendantsAndSelf(BoundNode node)
    {
        var stack = new Stack<BoundNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in Children(current).Reverse())
            {
                stack.Push(child);
            }
        }
    }

    /// <summary>Locals and parameters referenced inside <paramref name="lambda"/> but declared outside it.</summary>
    public static ImmutableArray<Symbol> FreeVariables(BoundLambda lambda)
    {
        var nodes = DescendantsAndSelf(lambda.Body).ToList();
        var inner = new HashSet<Symbol>(ReferenceEqualityComparer.Instance) { lambda.Function };
        foreach (var nested in nodes.OfType<BoundLambda>())
        {
            inner.Add(nested.Function);
        }

        var result = new List<Symbol>();
        var seen = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            Symbol? symbol = node switch
            {
                BoundLocal l => l.Local,
                BoundParameter p => p.Parameter,
                _ => null,
            };
            if (symbol?.Container is not null && !inner.Contains(symbol.Container) && seen.Add(symbol))
            {
                result.Add(symbol);
            }
        }

        return [.. result];
    }
}
