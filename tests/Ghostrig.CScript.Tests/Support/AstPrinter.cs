using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Ghostrig.CScript.Syntax;
using Ghostrig.CScript.Text;

namespace Ghostrig.CScript.Tests.Support;

/// <summary>
/// Renders syntax trees as compact S-expressions (spans omitted) so tests can assert tree shapes, e.g.
/// <c>(Binary Add (Literal Int 1) (Binary Multiply (Literal Int 2) (Literal Int 3)))</c>.
/// </summary>
internal static class AstPrinter
{
    public static string Print(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case Enum or bool or long or double or int:
                sb.Append(value);
                return;
            case System.Text.Rune r:
                sb.Append('\'').Append(r).Append('\'');
                return;
            case TypeSyntax t:
                sb.Append('<').Append(t).Append('>');
                return;
            case QualifiedName q:
                sb.Append(q);
                return;
            case Modifiers m:
                if (m.IsPrivate) sb.Append("private ");
                if (m.IsAsync) sb.Append("async ");
                if (m.IsConst) sb.Append("const ");
                if (m.IsStatic) sb.Append("static ");
                return;
            case Node node:
                WriteNode(sb, node);
                return;
            case IEnumerable seq:
                sb.Append('[');
                var first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(' ');
                    Write(sb, item);
                    first = false;
                }

                sb.Append(']');
                return;
            default:
                sb.Append(value);
                return;
        }
    }

    private static void WriteNode(StringBuilder sb, Node node)
    {
        var name = node.GetType().Name;
        foreach (var suffix in new[] { "Expr", "Stmt", "Decl", "Pattern", "Segment" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        sb.Append('(').Append(name);
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name is "Span" or "EqualityContract" or "DocComment" || property.PropertyType == typeof(TextSpan))
            {
                continue;
            }

            var value = property.GetValue(node);
            if (value is null || value is false || value is ICollection { Count: 0 } || (value is IEnumerable e && value is not string && !e.Cast<object>().Any())
                || (value is Modifiers m && m == Modifiers.None))
            {
                continue;
            }

            if (value is ImmutableArray<AttributeSyntax> { IsEmpty: true })
            {
                continue;
            }

            sb.Append(' ');
            Write(sb, value);
        }

        sb.Append(')');
    }
}
