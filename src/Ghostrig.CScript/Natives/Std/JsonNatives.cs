using System.Globalization;
using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

/// <summary>An immutable JSON value. Kinds follow the order of the script enum <c>JsonKind</c>.</summary>
internal sealed class JsonNode : ImmutableNativeState, IStructuralState
{
    public const int NullKind = 0, BoolKind = 1, NumberKind = 2, StringKind = 3, ArrayKind = 4, ObjectKind = 5;

    public static readonly JsonNode Null = new() { Kind = NullKind };
    public static readonly JsonNode True = new() { Kind = BoolKind, Bool = true };
    public static readonly JsonNode False = new() { Kind = BoolKind, Bool = false };
    public static readonly JsonNode EmptyArray = new() { Kind = ArrayKind, Items = [] };
    public static readonly JsonNode EmptyObject = new() { Kind = ObjectKind, Members = [] };

    public int Kind { get; private init; }
    public bool Bool { get; private init; }
    public bool IsInt { get; private init; }
    public long Int { get; private init; }
    public double Float { get; private init; }
    public string Str { get; private init; } = string.Empty;
    public JsonNode[] Items { get; private init; } = [];
    public (string Key, JsonNode Value)[] Members { get; private init; } = [];

    public static JsonNode Of(bool b) => b ? True : False;
    public static JsonNode Of(long i) => new() { Kind = NumberKind, IsInt = true, Int = i, Float = i };
    public static JsonNode Of(double d) => double.IsFinite(d)
        ? new() { Kind = NumberKind, Float = d }
        : throw new ScriptErrorRequest("ArgumentError", "JSON cannot represent NaN or infinity.");
    public static JsonNode Of(string s) => new() { Kind = StringKind, Str = s };
    public static JsonNode Array(JsonNode[] items) => new() { Kind = ArrayKind, Items = items };
    public static JsonNode Object((string, JsonNode)[] members) => new() { Kind = ObjectKind, Members = members };

    public JsonNode? Member(string key)
    {
        foreach (var (k, v) in Members)
        {
            if (k == key)
            {
                return v;
            }
        }

        return null;
    }

    public double Number => IsInt ? Int : Float;

    public bool StructurallyEquals(IStructuralState other) => other is JsonNode o && Same(this, o);

    private static bool Same(JsonNode a, JsonNode b) => a.Kind == b.Kind && a.Kind switch
    {
        BoolKind => a.Bool == b.Bool,
        NumberKind => a.IsInt && b.IsInt ? a.Int == b.Int : a.Number == b.Number,
        StringKind => a.Str == b.Str,
        ArrayKind => a.Items.Length == b.Items.Length && a.Items.Zip(b.Items).All(p => Same(p.First, p.Second)),
        ObjectKind => a.Members.Length == b.Members.Length && a.Members.All(m => b.Member(m.Key) is { } v && Same(m.Value, v)),
        _ => true,
    };

    public long StructuralHash() => StableHash.Of(JsonText.Write(this, pretty: false));
}

/// <summary>Strict RFC 8259 reader and writer.</summary>
internal static class JsonText
{
    public static JsonNode Parse(string text)
    {
        var position = 0;
        var value = ParseValue(text, ref position, 0);
        SkipSpace(text, ref position);
        return position == text.Length ? value : throw Error(text, position, "unexpected trailing text");
    }

    private static ScriptErrorRequest Error(string text, int position, string message)
    {
        var line = 1 + text.AsSpan(0, Math.Min(position, text.Length)).Count('\n');
        return new ScriptErrorRequest("FormatError", $"Invalid JSON at line {line}, offset {position}: {message}.");
    }

    private static void SkipSpace(string text, ref int p)
    {
        while (p < text.Length && text[p] is ' ' or '\t' or '\n' or '\r')
        {
            p++;
        }
    }

    private static JsonNode ParseValue(string text, ref int p, int depth)
    {
        if (depth > 256)
        {
            throw Error(text, p, "nesting is too deep");
        }

        SkipSpace(text, ref p);
        if (p >= text.Length)
        {
            throw Error(text, p, "unexpected end of input");
        }

        switch (text[p])
        {
            case '{':
            {
                p++;
                var members = new List<(string, JsonNode)>();
                SkipSpace(text, ref p);
                if (p < text.Length && text[p] == '}')
                {
                    p++;
                    return JsonNode.Object([.. members]);
                }

                while (true)
                {
                    SkipSpace(text, ref p);
                    if (p >= text.Length || text[p] != '"')
                    {
                        throw Error(text, p, "expected a member name");
                    }

                    var key = ParseString(text, ref p);
                    SkipSpace(text, ref p);
                    if (p >= text.Length || text[p] != ':')
                    {
                        throw Error(text, p, "expected ':'");
                    }

                    p++;
                    var value = ParseValue(text, ref p, depth + 1);
                    var existing = members.FindIndex(m => m.Item1 == key);
                    if (existing >= 0)
                    {
                        members[existing] = (key, value);
                    }
                    else
                    {
                        members.Add((key, value));
                    }

                    SkipSpace(text, ref p);
                    if (p < text.Length && text[p] == ',')
                    {
                        p++;
                        continue;
                    }

                    if (p < text.Length && text[p] == '}')
                    {
                        p++;
                        return JsonNode.Object([.. members]);
                    }

                    throw Error(text, p, "expected ',' or '}'");
                }
            }

            case '[':
            {
                p++;
                var items = new List<JsonNode>();
                SkipSpace(text, ref p);
                if (p < text.Length && text[p] == ']')
                {
                    p++;
                    return JsonNode.Array([.. items]);
                }

                while (true)
                {
                    items.Add(ParseValue(text, ref p, depth + 1));
                    SkipSpace(text, ref p);
                    if (p < text.Length && text[p] == ',')
                    {
                        p++;
                        continue;
                    }

                    if (p < text.Length && text[p] == ']')
                    {
                        p++;
                        return JsonNode.Array([.. items]);
                    }

                    throw Error(text, p, "expected ',' or ']'");
                }
            }

            case '"':
                return JsonNode.Of(ParseString(text, ref p));
            case 't' when string.CompareOrdinal(text, p, "true", 0, 4) == 0:
                p += 4;
                return JsonNode.True;
            case 'f' when string.CompareOrdinal(text, p, "false", 0, 5) == 0:
                p += 5;
                return JsonNode.False;
            case 'n' when string.CompareOrdinal(text, p, "null", 0, 4) == 0:
                p += 4;
                return JsonNode.Null;
            default:
                return ParseNumber(text, ref p);
        }
    }

    private static string ParseString(string text, ref int p)
    {
        var sb = new StringBuilder();
        p++;
        while (p < text.Length)
        {
            var ch = text[p++];
            if (ch == '"')
            {
                return sb.ToString();
            }

            if (ch < 0x20)
            {
                throw Error(text, p - 1, "control character in string");
            }

            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }

            if (p >= text.Length)
            {
                break;
            }

            var escape = text[p++];
            switch (escape)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u' when p + 4 <= text.Length && ushort.TryParse(text.AsSpan(p, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    p += 4;
                    break;
                default:
                    throw Error(text, p - 1, $"invalid escape '\\{escape}'");
            }
        }

        throw Error(text, p, "unterminated string");
    }

    private static JsonNode ParseNumber(string text, ref int p)
    {
        var start = p;
        if (p < text.Length && text[p] == '-')
        {
            p++;
        }

        if (p >= text.Length || !char.IsAsciiDigit(text[p]))
        {
            throw Error(text, start, "unexpected character");
        }

        if (text[p] == '0' && p + 1 < text.Length && char.IsAsciiDigit(text[p + 1]))
        {
            throw Error(text, p, "leading zeros are not allowed");
        }

        while (p < text.Length && char.IsAsciiDigit(text[p]))
        {
            p++;
        }

        var isInt = true;
        if (p < text.Length && text[p] == '.')
        {
            isInt = false;
            p++;
            if (p >= text.Length || !char.IsAsciiDigit(text[p]))
            {
                throw Error(text, p, "expected a digit after '.'");
            }

            while (p < text.Length && char.IsAsciiDigit(text[p]))
            {
                p++;
            }
        }

        if (p < text.Length && text[p] is 'e' or 'E')
        {
            isInt = false;
            p++;
            if (p < text.Length && text[p] is '+' or '-')
            {
                p++;
            }

            if (p >= text.Length || !char.IsAsciiDigit(text[p]))
            {
                throw Error(text, p, "expected an exponent");
            }

            while (p < text.Length && char.IsAsciiDigit(text[p]))
            {
                p++;
            }
        }

        var literal = text.AsSpan(start, p - start);
        if (isInt && long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonNode.Of(integer);
        }

        return JsonNode.Of(double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    public static string Write(JsonNode node, bool pretty)
    {
        var sb = new StringBuilder();
        Write(sb, node, pretty, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode node, bool pretty, int indent)
    {
        void NewLine(int level)
        {
            if (pretty)
            {
                sb.Append('\n').Append(' ', level * 2);
            }
        }

        switch (node.Kind)
        {
            case JsonNode.NullKind:
                sb.Append("null");
                break;
            case JsonNode.BoolKind:
                sb.Append(node.Bool ? "true" : "false");
                break;
            case JsonNode.NumberKind:
                sb.Append(node.IsInt ? node.Int.ToString(CultureInfo.InvariantCulture) : node.Float.ToString("R", CultureInfo.InvariantCulture));
                break;
            case JsonNode.StringKind:
                WriteString(sb, node.Str);
                break;
            case JsonNode.ArrayKind:
                sb.Append('[');
                for (var i = 0; i < node.Items.Length; i++)
                {
                    sb.Append(i > 0 ? "," : string.Empty);
                    NewLine(indent + 1);
                    Write(sb, node.Items[i], pretty, indent + 1);
                }

                if (node.Items.Length > 0)
                {
                    NewLine(indent);
                }

                sb.Append(']');
                break;
            default:
                sb.Append('{');
                for (var i = 0; i < node.Members.Length; i++)
                {
                    sb.Append(i > 0 ? "," : string.Empty);
                    NewLine(indent + 1);
                    WriteString(sb, node.Members[i].Key);
                    sb.Append(pretty ? ": " : ":");
                    Write(sb, node.Members[i].Value, pretty, indent + 1);
                }

                if (node.Members.Length > 0)
                {
                    NewLine(indent);
                }

                sb.Append('}');
                break;
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ': sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(ch); break;
            }
        }

        sb.Append('"');
    }
}

internal sealed record CsvTableState(string[] Headers, string[][] Rows);

/// <summary>An ordered, immutable INI model: (section, key, value) triples.</summary>
internal sealed record CfgState((string Section, string Key, string Value)[] Entries)
{
    public static (string Section, string Key) Split(string key)
    {
        var dot = key.LastIndexOf('.');
        return dot < 0 ? (string.Empty, key) : (key[..dot], key[(dot + 1)..]);
    }

    public string? Get(string key)
    {
        var (section, name) = Split(key);
        foreach (var e in Entries)
        {
            if (e.Section == section && e.Key == name)
            {
                return e.Value;
            }
        }

        return null;
    }
}

/// <summary>Host implementations of the structured formats in <c>Std.Text</c>: JSON, CSV and Cfg.</summary>
internal static class JsonNatives
{
    private const string M = "Std.Text";

    internal static Value Json(NativeContext c, JsonNode node) => c.NewInstance(c.Class(M, "JsonValue"), node);

    private static JsonNode J(NativeContext c, Value v) => c.State<JsonNode>(v);

    public static void Register(NativeRegistry r)
    {
        RegisterJson(r.Scope($"{M}.JsonValue"));
        RegisterCsv(r);
        RegisterCfg(r.Scope($"{M}.Cfg"));
    }

    /// <summary>Converts a script value to JSON (strings, numbers, arrays, collections, Serializable objects).</summary>
    internal static JsonNode From(NativeContext c, Value value, int depth = 0)
    {
        if (depth > 64)
        {
            throw c.Throw("ArgumentError", "The value is nested too deeply (or cyclic) for JSON.");
        }

        switch (value.Tag)
        {
            case ValueTag.Null: return JsonNode.Null;
            case ValueTag.Bool: return JsonNode.Of(value.AsBool);
            case ValueTag.Int or ValueTag.Byte: return JsonNode.Of(value.Bits);
            case ValueTag.Float: return JsonNode.Of(value.AsFloat);
            case ValueTag.Char: return JsonNode.Of(char.ConvertFromUtf32((int)value.Bits));
            case ValueTag.Duration or ValueTag.Instant: return JsonNode.Of(value.Bits);
            case ValueTag.Enum: return JsonNode.Of(value.ToString());
        }

        c.Charge(1);
        return value.Object switch
        {
            string s => JsonNode.Of(s),
            BytesObject b => JsonNode.Of(Convert.ToBase64String(b.Data)),
            ArrayObject a => JsonNode.Array([.. a.Items.Select(v => From(c, v, depth + 1))]),
            TupleObject t => JsonNode.Array([.. t.Items.Select(v => From(c, v, depth + 1))]),
            ObjectInstance { NativeState: JsonNode node } => node,
            ObjectInstance { NativeState: MapState map } => JsonNode.Object([.. map.Table.Slots().Select(s =>
                (map.Table.KeyAt(s).Object as string ?? Display(c, map.Table.KeyAt(s)), From(c, map.Table.ValueAt(s), depth + 1)))]),
            ObjectInstance { NativeState: IValueSource source } => JsonNode.Array([.. source.Enumerate(c).Select(v => From(c, v, depth + 1))]),
            ObjectInstance { NativeState: null } o when IsSerializable(c, o.Class) => JsonNode.Object([.. Fields(o).Select(f => (f.Name, From(c, f.Value, depth + 1)))]),
            _ => throw c.Throw("ArgumentError", $"{c.Image.ClassOf(value)?.Name ?? "value"} is not Serializable."),
        };
    }

    private static bool IsSerializable(NativeContext c, RuntimeClass runtimeClass)
    {
        var serializable = c.Image.Linker.Compilation.GetModule("Core")!.Lookup("Serializable").OfType<NamedTypeSymbol>().First();
        return runtimeClass.Implements(serializable);
    }

    /// <summary>Instance fields (including auto-property backing fields, named after the property) in declaration order, base first.</summary>
    private static IEnumerable<(string Name, Value Value)> Fields(ObjectInstance instance)
    {
        var chain = new List<NamedTypeSymbol>();
        for (var cls = instance.Class; cls?.Symbol is { } symbol; cls = cls.Base)
        {
            chain.Insert(0, symbol);
        }

        foreach (var type in chain)
        {
            foreach (var member in type.Members)
            {
                var (name, field) = member switch
                {
                    FieldSymbol { IsStatic: false, IsPrivate: false } f => (f.Name, f),
                    PropertySymbol { BackingField: { IsStatic: false } backing, IsPrivate: false } p => (p.Name, backing),
                    _ => (null, null),
                };
                if (name is not null && field!.Slot >= 0 && field.Slot < instance.Fields.Length)
                {
                    yield return (name, instance.Fields[field.Slot]);
                }
            }
        }
    }

    private static IEnumerable<JsonNode> Query(JsonNode root, string path)
    {
        var p = path.Trim();
        if (p.StartsWith('$'))
        {
            p = p[1..];
        }

        IEnumerable<JsonNode> current = [root];
        var i = 0;
        while (i < p.Length)
        {
            if (p[i] == '.')
            {
                i++;
                continue;
            }

            if (p[i] == '[')
            {
                var close = p.IndexOf(']', i);
                if (close < 0)
                {
                    throw new ScriptErrorRequest("FormatError", $"Unclosed '[' in path '{path}'.");
                }

                var inner = p[(i + 1)..close].Trim();
                i = close + 1;
                if (inner == "*")
                {
                    current = current.SelectMany(n => n.Kind == JsonNode.ArrayKind ? n.Items : n.Members.Select(m => m.Value)).ToList();
                }
                else if (long.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
                {
                    current = current.Where(n => n.Kind == JsonNode.ArrayKind)
                        .Select(n => (Node: n, At: index < 0 ? index + n.Items.Length : index))
                        .Where(x => x.At >= 0 && x.At < x.Node.Items.Length)
                        .Select(x => x.Node.Items[x.At]).ToList();
                }
                else
                {
                    var key = inner.Trim('\'', '"');
                    current = current.Select(n => n.Member(key)).OfType<JsonNode>().ToList();
                }

                continue;
            }

            var end = i;
            while (end < p.Length && p[end] is not ('.' or '['))
            {
                end++;
            }

            var name = p[i..end];
            i = end;
            current = name == "*"
                ? current.SelectMany(n => n.Kind == JsonNode.ArrayKind ? n.Items : n.Members.Select(m => m.Value)).ToList()
                : current.Select(n => n.Member(name)).OfType<JsonNode>().ToList();
        }

        return current;
    }

    private static void RegisterJson(Action<string, StdFunction> F)
    {
        static JsonNode Expect(NativeContext c, Value v, int kind, string what) =>
            J(c, v) is var node && node.Kind == kind ? node : throw c.Throw("CastError", $"The JSON value is not {what}.");

        F("Parse/1", (c, a) =>
        {
            c.Charge(1 + (a[0].AsString.Length / 16));
            return Json(c, JsonText.Parse(a[0].AsString));
        });
        F("get_Null/0", (c, _) => Json(c, JsonNode.Null));
        F("get_EmptyObject/0", (c, _) => Json(c, JsonNode.EmptyObject));
        F("get_EmptyArray/0", (c, _) => Json(c, JsonNode.EmptyArray));
        F("OfString", (c, a) => Json(c, JsonNode.Of(a[0].AsString)));
        F("OfInt", (c, a) => Json(c, JsonNode.Of(a[0].Bits)));
        F("OfFloat", (c, a) => Json(c, JsonNode.Of(MathNatives.D(a[0]))));
        F("OfBool", (c, a) => Json(c, JsonNode.Of(a[0].AsBool)));
        F("From/1", (c, a) => Json(c, From(c, a[0])));
        F("Serialize/1", (c, a) => Value.FromString(JsonText.Write(From(c, a[0]), pretty: false)));
        F("get_Kind/0", (c, a) => Value.FromEnum(J(c, a[0]).Kind, c.Class(M, "JsonKind")));
        F("get_IsNull/0", (c, a) => Value.FromBool(J(c, a[0]).Kind == JsonNode.NullKind));
        F("AsString/0", (c, a) => Value.FromString(Expect(c, a[0], JsonNode.StringKind, "a string").Str));
        F("AsInt/0", (c, a) =>
        {
            var n = Expect(c, a[0], JsonNode.NumberKind, "a number");
            return n.IsInt ? Value.FromInt(n.Int) : n.Float == Math.Floor(n.Float) && Math.Abs(n.Float) < 9.2e18
                ? Value.FromInt((long)n.Float)
                : throw c.Throw("CastError", $"{n.Float} is not an integer.");
        });
        F("AsFloat/0", (c, a) => Value.FromFloat(Expect(c, a[0], JsonNode.NumberKind, "a number").Number));
        F("AsBool/0", (c, a) => Value.FromBool(Expect(c, a[0], JsonNode.BoolKind, "a bool").Bool));
        F("get_Count/0", (c, a) => J(c, a[0]) switch
        {
            { Kind: JsonNode.ArrayKind } n => Value.FromInt(n.Items.Length),
            { Kind: JsonNode.ObjectKind } n => Value.FromInt(n.Members.Length),
            _ => throw c.Throw("CastError", "Only arrays and objects have a Count."),
        });
        F("Get/1", (c, a) => Expect(c, a[0], JsonNode.ObjectKind, "an object").Member(a[1].AsString) is { } v
            ? Json(c, v)
            : throw c.Throw("IndexError", $"The JSON object has no member '{a[1].AsString}'."));
        F("TryGet/1", (c, a) => J(c, a[0]) is { Kind: JsonNode.ObjectKind } n && n.Member(a[1].AsString) is { } v ? Json(c, v) : Value.Null);
        F("Has/1", (c, a) => Value.FromBool(J(c, a[0]) is { Kind: JsonNode.ObjectKind } n && n.Member(a[1].AsString) is not null));
        F("get_Keys/0", (c, a) => c.NewArray([.. Expect(c, a[0], JsonNode.ObjectKind, "an object").Members.Select(m => Value.FromString(m.Key))]));
        F("At/1", (c, a) =>
        {
            var n = Expect(c, a[0], JsonNode.ArrayKind, "an array");
            return Json(c, n.Items[Index(c, a[1].Bits, n.Items.Length)]);
        });
        F("get_Items/0", (c, a) => c.NewArray([.. Expect(c, a[0], JsonNode.ArrayKind, "an array").Items.Select(i => Json(c, i))]));
        F("With/2", (c, a) =>
        {
            var n = Expect(c, a[0], JsonNode.ObjectKind, "an object");
            var (key, value) = (a[1].AsString, J(c, a[2]));
            c.Charge(n.Members.Length / 8);
            var members = n.Members.Any(m => m.Key == key)
                ? n.Members.Select(m => m.Key == key ? (key, value) : m).ToArray()
                : [.. n.Members, (key, value)];
            return Json(c, JsonNode.Object(members));
        });
        F("Without/1", (c, a) =>
        {
            var n = Expect(c, a[0], JsonNode.ObjectKind, "an object");
            return Json(c, JsonNode.Object([.. n.Members.Where(m => m.Key != a[1].AsString)]));
        });
        F("Append/1", (c, a) =>
        {
            var n = Expect(c, a[0], JsonNode.ArrayKind, "an array");
            c.Charge(n.Items.Length / 8);
            return Json(c, JsonNode.Array([.. n.Items, J(c, a[1])]));
        });
        F("Query/1", (c, a) =>
        {
            var results = Query(J(c, a[0]), a[1].AsString).ToArray();
            c.Charge(1 + results.Length);
            return c.NewArray([.. results.Select(n => Json(c, n))]);
        });
        F("ToString/0", (c, a) => Value.FromString(JsonText.Write(J(c, a[0]), pretty: false)));
        F("Pretty/0", (c, a) => Value.FromString(JsonText.Write(J(c, a[0]), pretty: true)));
        F("Equals/1", (c, a) => Value.FromBool(J(c, a[0]).StructurallyEquals(J(c, a[1]))));
    }

    #region CSV

    internal static string[][] ParseCsv(NativeContext c, string text, char separator)
    {
        c.Charge(1 + (text.Length / 16));
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"' && field.Length == 0)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (ch == separator)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = true;
            }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
                fieldStarted = false;
            }
            else
            {
                field.Append(ch);
                fieldStarted = true;
            }
        }

        if (quoted)
        {
            throw c.Throw("FormatError", "Unterminated quoted CSV field.");
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }

        return [.. rows];
    }

    private static string WriteCsv(string[][] rows) =>
        string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(f =>
            f.IndexOfAny([',', '"', '\n', '\r']) >= 0 || (f.Length > 0 && (f[0] == ' ' || f[^1] == ' '))
                ? "\"" + f.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : f)))) + (rows.Length > 0 ? "\r\n" : string.Empty);

    private static Value Rows(NativeContext c, string[][] rows) => c.NewArray([.. rows.Select(r => c.NewArray([.. r.Select(Value.FromString)]))]);

    private static void RegisterCsv(NativeRegistry r)
    {
        var F = r.Scope($"{M}.Csv");
        F("Parse/1", (c, a) => Rows(c, ParseCsv(c, a[0].AsString, ',')));
        F("Parse/2", (c, a) => Rows(c, ParseCsv(c, a[0].AsString, (char)a[1].Bits)));
        F("Write/1", (c, a) =>
        {
            var rows = Items(a[0]).Select(row => Items(row).Select(v => v.AsString).ToArray()).ToArray();
            c.Charge(rows.Sum(row => row.Length));
            return Value.FromString(WriteCsv(rows));
        });
        F("Table/1", (c, a) =>
        {
            var rows = ParseCsv(c, a[0].AsString, ',');
            return rows.Length == 0
                ? throw c.Throw("FormatError", "The CSV text has no header row.")
                : c.NewInstance(c.Class(M, "CsvTable"), new CsvTableState(rows[0], rows[1..]));
        });

        var T = r.Scope($"{M}.CsvTable");
        static CsvTableState S(NativeContext c, Value v) => c.State<CsvTableState>(v);
        static int Col(NativeContext c, CsvTableState t, string name)
        {
            var i = System.Array.IndexOf(t.Headers, name);
            return i >= 0 ? i : throw c.Throw("IndexError", $"No column named '{name}'.");
        }

        static string Cell(CsvTableState t, int row, int col) => col < t.Rows[row].Length ? t.Rows[row][col] : string.Empty;
        T("get_Headers/0", (c, a) => c.NewArray([.. S(c, a[0]).Headers.Select(Value.FromString)]));
        T("get_RowCount/0", (c, a) => Value.FromInt(S(c, a[0]).Rows.Length));
        T("Row/1", (c, a) => c.NewArray([.. S(c, a[0]).Rows[Index(c, a[1].Bits, S(c, a[0]).Rows.Length)].Select(Value.FromString)]));
        T("Get/2", (c, a) =>
        {
            var t = S(c, a[0]);
            return Value.FromString(Cell(t, Index(c, a[1].Bits, t.Rows.Length), Col(c, t, a[2].AsString)));
        });
        T("Column/1", (c, a) =>
        {
            var t = S(c, a[0]);
            var col = Col(c, t, a[1].AsString);
            return c.NewArray([.. Enumerable.Range(0, t.Rows.Length).Select(i => Value.FromString(Cell(t, i, col)))]);
        });
        T("IntColumn/1", (c, a) =>
        {
            var t = S(c, a[0]);
            var col = Col(c, t, a[1].AsString);
            return c.NewArray([.. Enumerable.Range(0, t.Rows.Length).Select(i => long.TryParse(Cell(t, i, col).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
                ? Value.FromInt(v)
                : throw c.Throw("FormatError", $"Row {i + 1}, column '{a[1].AsString}': '{Cell(t, i, col)}' is not an integer."))]);
        });
        T("FloatColumn/1", (c, a) =>
        {
            var t = S(c, a[0]);
            var col = Col(c, t, a[1].AsString);
            return c.NewArray([.. Enumerable.Range(0, t.Rows.Length).Select(i => double.TryParse(Cell(t, i, col).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? Value.FromFloat(v)
                : throw c.Throw("FormatError", $"Row {i + 1}, column '{a[1].AsString}': '{Cell(t, i, col)}' is not a number."))]);
        });
        T("ToString/0", (c, a) => Value.FromString($"CsvTable({S(c, a[0]).Headers.Length} columns × {S(c, a[0]).Rows.Length} rows)"));
    }

    #endregion

    #region Cfg

    private static Value Cfg(NativeContext c, CfgState state) => c.NewInstance(c.Class(M, "Cfg"), state);

    private static void RegisterCfg(Action<string, StdFunction> F)
    {
        static CfgState S(NativeContext c, Value v) => c.State<CfgState>(v);
        F("Parse/1", (c, a) =>
        {
            var text = a[0].AsString;
            c.Charge(1 + (text.Length / 16));
            var entries = new List<(string, string, string)>();
            var section = string.Empty;
            var number = 0;
            foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                number++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    section = line.EndsWith(']') ? line[1..^1].Trim() : throw c.Throw("FormatError", $"Line {number}: unclosed section header.");
                    continue;
                }

                var eq = line.IndexOf('=', StringComparison.Ordinal);
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var split = eq >= 0 ? eq : colon;
                if (split <= 0)
                {
                    throw c.Throw("FormatError", $"Line {number}: expected 'key = value'.");
                }

                var value = line[(split + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }

                var key = line[..split].Trim();
                entries.RemoveAll(e => e.Item1 == section && e.Item2 == key);
                entries.Add((section, key, value));
            }

            return Cfg(c, new CfgState([.. entries]));
        });
        F("get_Empty/0", (c, _) => Cfg(c, new CfgState([])));
        F("Get/1", (c, a) => S(c, a[0]).Get(a[1].AsString) is { } v ? Value.FromString(v) : Value.Null);
        F("GetOr/2", (c, a) => Value.FromString(S(c, a[0]).Get(a[1].AsString) ?? a[2].AsString));
        F("GetInt/2", (c, a) => S(c, a[0]).Get(a[1].AsString) is { } v
            ? long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? Value.FromInt(n) : throw c.Throw("FormatError", $"'{a[1].AsString}' is not an integer: '{v}'.")
            : a[2]);
        F("GetBool/2", (c, a) => S(c, a[0]).Get(a[1].AsString)?.ToLowerInvariant() switch
        {
            null => a[2],
            "true" or "yes" or "on" or "1" => Value.True,
            "false" or "no" or "off" or "0" => Value.False,
            var v => throw c.Throw("FormatError", $"'{a[1].AsString}' is not a boolean: '{v}'."),
        });
        F("Has/1", (c, a) => Value.FromBool(S(c, a[0]).Get(a[1].AsString) is not null));
        F("get_Sections/0", (c, a) => c.NewArray([.. S(c, a[0]).Entries.Select(e => e.Section).Where(s => s.Length > 0).Distinct().Select(Value.FromString)]));
        F("KeysOf/1", (c, a) => c.NewArray([.. S(c, a[0]).Entries.Where(e => e.Section == a[1].AsString).Select(e => Value.FromString(e.Key))]));
        F("With/2", (c, a) =>
        {
            var (section, key) = CfgState.Split(a[1].AsString);
            var entries = S(c, a[0]).Entries.ToList();
            var i = entries.FindIndex(e => e.Section == section && e.Key == key);
            if (i >= 0)
            {
                entries[i] = (section, key, a[2].AsString);
            }
            else
            {
                var last = entries.FindLastIndex(e => e.Section == section);
                entries.Insert(last >= 0 ? last + 1 : entries.Count, (section, key, a[2].AsString));
            }

            return Cfg(c, new CfgState([.. entries]));
        });
        F("Without/1", (c, a) =>
        {
            var (section, key) = CfgState.Split(a[1].AsString);
            return Cfg(c, new CfgState([.. S(c, a[0]).Entries.Where(e => !(e.Section == section && e.Key == key))]));
        });
        F("ToString/0", (c, a) =>
        {
            var sb = new StringBuilder();
            string? current = null;
            foreach (var (section, key, value) in S(c, a[0]).Entries.OrderBy(e => e.Section.Length == 0 ? 0 : 1))
            {
                if (section != current && section.Length > 0)
                {
                    sb.Append(sb.Length > 0 ? "\n" : string.Empty).Append('[').Append(section).Append("]\n");
                }

                current = section;
                sb.Append(key).Append(" = ").Append(value).Append('\n');
            }

            return Value.FromString(sb.ToString());
        });
    }

    #endregion
}
