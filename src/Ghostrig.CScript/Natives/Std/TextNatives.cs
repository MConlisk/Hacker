using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

#region States

internal sealed class BuilderState(string initial) : NativeState
{
    public StringBuilder Text { get; private set; } = new(initial);

    protected internal override object CaptureSnapshot() => Text.ToString();

    protected internal override void RestoreSnapshot(object snapshot) => Text = new StringBuilder((string)snapshot);
}

internal sealed class RegexState(Regex regex, string pattern) : ImmutableNativeState
{
    public Regex Regex { get; } = regex;
    public string Pattern { get; } = pattern;
}

internal sealed record MatchState(Match Match, Regex Regex);

internal sealed record DiffLineState(int Kind, string Text);

internal sealed record TokenState(string Kind, string Text, int Index, int Line, int Column);

internal sealed class TokenizerState : NativeState
{
    public List<(string? Kind, Regex Pattern)> Rules { get; private set; } = [];

    protected internal override object CaptureSnapshot() => Rules.ToArray();

    protected internal override void RestoreSnapshot(object snapshot) => Rules = [.. ((string?, Regex)[])snapshot];
}

internal sealed class TokenStreamState(Value[] tokens) : NativeState
{
    public Value[] Tokens { get; } = tokens;
    public int Position { get; set; }

    protected internal override object CaptureSnapshot() => Position;

    protected internal override void RestoreSnapshot(object snapshot) => Position = (int)snapshot;
}

internal sealed record SearchHitState(string File, int Line, string Text);

#endregion

/// <summary>Host implementations of <c>Std.Text</c> (except JSON, CSV and Cfg, see <see cref="JsonNatives"/>).</summary>
internal static class TextNatives
{
    private const string M = "Std.Text";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Register(NativeRegistry r)
    {
        RegisterBuilder(r.Scope($"{M}.StringBuilder"));
        RegisterFormat(r.Scope($"{M}.Format"));
        RegisterRegex(r);
        RegisterEncoding(r.Scope($"{M}.Encoding"));
        RegisterMarkdown(r.Scope($"{M}.Markdown"));
        RegisterTemplate(r.Scope($"{M}.Template"));
        RegisterDiff(r);
        RegisterTokens(r);
        RegisterSearch(r);
    }

    private static Value Str(NativeContext c, string s)
    {
        c.Charge(s.Length / 16);
        return Value.FromString(s);
    }

    #region StringBuilder

    private static void RegisterBuilder(Action<string, StdFunction> F)
    {
        static Value Edit(NativeContext c, Value[] a, Action<StringBuilder> edit)
        {
            var s = c.State<BuilderState>(a[0]);
            c.Mutating(s);
            edit(s.Text);
            return a[0];
        }

        static int Bounded(NativeContext c, long index, int length) =>
            index >= 0 && index <= length ? (int)index : throw c.Throw("IndexError", $"Index {index} is out of range for length {length}.");

        F("StringBuilder/0", (c, a) => CollectionNatives.Init(a[0], new BuilderState(string.Empty)));
        F("StringBuilder/1", (c, a) => CollectionNatives.Init(a[0], new BuilderState(a[1].AsString)));
        F("get_Length/0", (c, a) => Value.FromInt(c.State<BuilderState>(a[0]).Text.Length));
        F("get_Item/1", (c, a) =>
        {
            var text = c.State<BuilderState>(a[0]).Text;
            return Value.FromChar(text[Index(c, a[1].Bits, text.Length)]);
        });
        F("Append/1", (c, a) =>
        {
            c.Charge(a[1].AsString.Length / 16);
            return Edit(c, a, sb => sb.Append(a[1].AsString));
        });
        F("AppendChar", (c, a) => Edit(c, a, sb => sb.Append(char.ConvertFromUtf32((int)a[1].Bits))));
        F("AppendLine/0", (c, a) => Edit(c, a, sb => sb.Append('\n')));
        F("AppendLine/1", (c, a) =>
        {
            c.Charge(a[1].AsString.Length / 16);
            return Edit(c, a, sb => sb.Append(a[1].AsString).Append('\n'));
        });
        F("AppendRepeat/2", (c, a) =>
        {
            var count = Int32(c, a[2].Bits, "Count", 0, 1_000_000);
            c.Charge(a[1].AsString.Length * count / 16);
            return Edit(c, a, sb => sb.Insert(sb.Length, a[1].AsString, count));
        });
        F("Insert/2", (c, a) =>
        {
            var length = c.State<BuilderState>(a[0]).Text.Length;
            var index = Bounded(c, a[1].Bits, length);
            c.Charge(length / 32);
            return Edit(c, a, sb => sb.Insert(index, a[2].AsString));
        });
        F("Remove/2", (c, a) =>
        {
            var length = c.State<BuilderState>(a[0]).Text.Length;
            CollectionNatives.CheckRange(c, a[1].Bits, a[2].Bits, length);
            c.Charge(length / 32);
            return Edit(c, a, sb => sb.Remove((int)a[1].Bits, (int)a[2].Bits));
        });
        F("Replace/2", (c, a) =>
        {
            if (a[1].AsString.Length == 0)
            {
                throw c.Throw("ArgumentError", "Cannot replace an empty string.");
            }

            c.Charge(c.State<BuilderState>(a[0]).Text.Length / 16);
            return Edit(c, a, sb => sb.Replace(a[1].AsString, a[2].AsString));
        });
        F("Clear/0", (c, a) => Edit(c, a, sb => sb.Clear()));
        F("ToString/0", (c, a) => Str(c, c.State<BuilderState>(a[0]).Text.ToString()));
    }

    #endregion

    #region Format

    internal static string Bytes(long count)
    {
        if (Math.Abs(count) < 1024)
        {
            return $"{count} B";
        }

        string[] units = ["KB", "MB", "GB", "TB", "PB", "EB"];
        var value = Math.Abs((double)count);
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{(count < 0 ? "-" : string.Empty)}{Math.Floor(value * 10) / 10:0.0} {units[unit]}".Replace(",", ".", StringComparison.Ordinal);
    }

    internal static string Duration(long nanoseconds)
    {
        var sign = nanoseconds < 0 ? "-" : string.Empty;
        var ns = Math.Abs((decimal)nanoseconds);
        string Trim(decimal v) => v.ToString("0.#", Invariant);
        return ns switch
        {
            < 1_000 => $"{sign}{ns} ns",
            < 1_000_000 => $"{sign}{Trim(ns / 1_000)} µs",
            < 1_000_000_000 => $"{sign}{Trim(ns / 1_000_000)} ms",
            < 60_000_000_000 => $"{sign}{Trim(ns / 1_000_000_000)} s",
            < 3_600_000_000_000 => $"{sign}{(long)(ns / 60_000_000_000)} min {(long)(ns % 60_000_000_000 / 1_000_000_000)} s",
            _ => $"{sign}{(long)(ns / 3_600_000_000_000)} h {(long)(ns % 3_600_000_000_000 / 60_000_000_000)} min",
        };
    }

    private static void RegisterFormat(Action<string, StdFunction> F)
    {
        F("Bytes/1", (c, a) => Value.FromString(Bytes(a[0].Bits)));
        F("Duration/1", (c, a) => Value.FromString(Duration(a[0].Bits)));
        F("Number/1", (c, a) => Value.FromString(a[0].Bits.ToString("N0", Invariant)));
        F("Fixed/2", (c, a) => Value.FromString(MathNatives.D(a[0]).ToString("F" + Int32(c, a[1].Bits, "Decimals", 0, 15), Invariant)));
        F("Percent/2", (c, a) => Value.FromString((MathNatives.D(a[0]) * 100).ToString("F" + Int32(c, a[1].Bits, "Decimals", 0, 15), Invariant) + "%"));
        F("Hex/2", (c, a) => Value.FromString(a[0].Bits.ToString("x" + Int32(c, a[1].Bits, "Width", 0, 16), Invariant)));
        F("Binary/2", (c, a) => Value.FromString(Convert.ToString(a[0].Bits, 2).PadLeft(Int32(c, a[1].Bits, "Width", 0, 64), '0')));
        F("Ordinal/1", (c, a) =>
        {
            var n = a[0].Bits;
            var suffix = (Math.Abs(n) % 100) is 11 or 12 or 13 ? "th" : (Math.Abs(n) % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return Value.FromString(n.ToString(Invariant) + suffix);
        });
        F("Plural/3", (c, a) => Value.FromString($"{a[0].Bits.ToString(Invariant)} {(a[0].Bits == 1 ? a[1].AsString : a[2].AsString)}"));
        F("Center/2", (c, a) =>
        {
            var (text, width) = (a[0].AsString, Int32(c, a[1].Bits, "Width", 0, 1_000_000));
            var left = Math.Max(0, (width - text.Length) / 2);
            return Value.FromString(text.PadLeft(text.Length + left).PadRight(width));
        });
        F("Table/1", (c, a) =>
        {
            var rows = Items(a[0]).Select(row => Items(row).Select(v => v.AsString).ToArray()).ToArray();
            if (rows.Length == 0)
            {
                return Value.FromString(string.Empty);
            }

            var columns = rows.Max(r => r.Length);
            var widths = Enumerable.Range(0, columns).Select(i => rows.Max(r => i < r.Length ? r[i].Length : 0)).ToArray();
            string Line(string[] row) => string.Join("  ", Enumerable.Range(0, columns).Select(i => (i < row.Length ? row[i] : string.Empty).PadRight(widths[i]))).TrimEnd();
            var lines = new List<string> { Line(rows[0]), string.Join("  ", widths.Select(w => new string('-', w))) };
            lines.AddRange(rows.Skip(1).Select(Line));
            c.Charge(rows.Sum(r => r.Length));
            return Value.FromString(string.Join("\n", lines));
        });
        F("Truncate/2", (c, a) =>
        {
            var (text, width) = (a[0].AsString, Int32(c, a[1].Bits, "Width", 1, 1_000_000));
            return Value.FromString(text.Length <= width ? text : text[..(width - 1)] + "…");
        });
    }

    #endregion

    #region Regex

    internal static Regex Compile(NativeContext c, string pattern, string flags = "")
    {
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        foreach (var flag in flags)
        {
            options |= flag switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                _ => throw c.Throw("ArgumentError", $"Unknown regex flag '{flag}' (use i, m, s, x)."),
            };
        }

        try
        {
            c.Charge(1 + (pattern.Length / 4));
            return new Regex(pattern, options);
        }
        catch (NotSupportedException)
        {
            throw c.Throw("FormatError", $"Regex '{pattern}' uses a construct that is not supported (backreferences, lookaround, atomic groups).");
        }
        catch (ArgumentException e)
        {
            throw c.Throw("FormatError", $"Invalid regex '{pattern}': {e.Message}");
        }
    }

    private static void ChargeScan(NativeContext c, Regex regex, string text) => c.Charge(1 + ((long)text.Length * Math.Max(1, regex.ToString().Length) / 64));

    private static Value MatchValue(NativeContext c, Match match, Regex regex) =>
        match.Success ? c.NewInstance(c.Class(M, "RegexMatch"), new MatchState(match, regex)) : Value.Null;

    private static void RegisterRegex(NativeRegistry r)
    {
        var F = r.Scope($"{M}.Regex");
        static RegexState S(NativeContext c, Value v) => c.State<RegexState>(v);
        F("Regex/1", (c, a) => CollectionNatives.Init(a[0], new RegexState(Compile(c, a[1].AsString), a[1].AsString)));
        F("Regex/2", (c, a) => CollectionNatives.Init(a[0], new RegexState(Compile(c, a[1].AsString, a[2].AsString), a[1].AsString)));
        F("get_Pattern/0", (c, a) => Value.FromString(S(c, a[0]).Pattern));
        F("IsMatch/1", (c, a) =>
        {
            ChargeScan(c, S(c, a[0]).Regex, a[1].AsString);
            return Value.FromBool(S(c, a[0]).Regex.IsMatch(a[1].AsString));
        });
        F("Match/1", (c, a) =>
        {
            ChargeScan(c, S(c, a[0]).Regex, a[1].AsString);
            return MatchValue(c, S(c, a[0]).Regex.Match(a[1].AsString), S(c, a[0]).Regex);
        });
        F("Match/2", (c, a) =>
        {
            var text = a[1].AsString;
            var start = a[2].Bits >= 0 && a[2].Bits <= text.Length ? (int)a[2].Bits : throw c.Throw("IndexError", "Start index out of range.");
            ChargeScan(c, S(c, a[0]).Regex, text);
            return MatchValue(c, S(c, a[0]).Regex.Match(text, start), S(c, a[0]).Regex);
        });
        F("Matches/1", (c, a) =>
        {
            var regex = S(c, a[0]).Regex;
            ChargeScan(c, regex, a[1].AsString);
            return c.NewArray([.. regex.Matches(a[1].AsString).Select(m => MatchValue(c, m, regex))]);
        });
        F("Replace/2", (c, a) =>
        {
            ChargeScan(c, S(c, a[0]).Regex, a[1].AsString);
            return Str(c, S(c, a[0]).Regex.Replace(a[1].AsString, a[2].AsString));
        });
        F("ReplaceWith/2", (c, a) =>
        {
            var (regex, f) = (S(c, a[0]).Regex, Fn(a[2]));
            ChargeScan(c, regex, a[1].AsString);
            return Str(c, regex.Replace(a[1].AsString, m => c.Invoke(f, MatchValue(c, m, regex)).AsString));
        });
        F("Split/1", (c, a) =>
        {
            ChargeScan(c, S(c, a[0]).Regex, a[1].AsString);
            return c.NewArray([.. S(c, a[0]).Regex.Split(a[1].AsString).Select(Value.FromString)]);
        });
        F("Escape/1", (c, a) => Value.FromString(Regex.Escape(a[0].AsString)));
        F("ToString/0", (c, a) => Value.FromString($"/{S(c, a[0]).Pattern}/"));

        var m = r.Scope($"{M}.RegexMatch");
        static MatchState Mt(NativeContext c, Value v) => c.State<MatchState>(v);
        m("get_Value/0", (c, a) => Value.FromString(Mt(c, a[0]).Match.Value));
        m("get_Index/0", (c, a) => Value.FromInt(Mt(c, a[0]).Match.Index));
        m("get_Length/0", (c, a) => Value.FromInt(Mt(c, a[0]).Match.Length));
        m("Group/1", (c, a) =>
        {
            var groups = Mt(c, a[0]).Match.Groups;
            return a[1].Bits >= 0 && a[1].Bits < groups.Count ? Value.FromString(groups[(int)a[1].Bits].Value) : throw c.Throw("IndexError", $"Group {a[1].Bits} does not exist.");
        });
        m("GroupNamed", (c, a) =>
        {
            var state = Mt(c, a[0]);
            return state.Regex.GroupNumberFromName(a[1].AsString) >= 0
                ? Value.FromString(state.Match.Groups[a[1].AsString].Value)
                : throw c.Throw("IndexError", $"No group named '{a[1].AsString}'.");
        });
        m("get_Groups/0", (c, a) => c.NewArray([.. Mt(c, a[0]).Match.Groups.Cast<Group>().Select(g => Value.FromString(g.Value))]));
        m("ToString/0", (c, a) => Value.FromString(Mt(c, a[0]).Match.Value));
    }

    #endregion

    #region Encoding

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += 5)
        {
            var chunk = new byte[5];
            var n = Math.Min(5, data.Length - i);
            System.Array.Copy(data, i, chunk, 0, n);
            var bits = ((ulong)chunk[0] << 32) | ((ulong)chunk[1] << 24) | ((ulong)chunk[2] << 16) | ((ulong)chunk[3] << 8) | chunk[4];
            var chars = (int)Math.Ceiling(n * 8 / 5.0);
            for (var j = 0; j < 8; j++)
            {
                sb.Append(j < chars ? Base32Alphabet[(int)((bits >> (35 - (j * 5))) & 31)] : '=');
            }
        }

        return sb.ToString();
    }

    private static byte[] Base32Decode(NativeContext c, string text)
    {
        var clean = text.Trim().TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        ulong buffer = 0;
        var bits = 0;
        foreach (var ch in clean)
        {
            var v = Base32Alphabet.IndexOf(ch, StringComparison.Ordinal);
            if (v < 0)
            {
                throw c.Throw("FormatError", $"'{ch}' is not a Base32 character.");
            }

            buffer = (buffer << 5) | (uint)v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
            }
        }

        return [.. output];
    }

    private static string Shift(string text, int shift)
    {
        var k = ((shift % 26) + 26) % 26;
        return string.Concat(text.Select(ch => ch switch
        {
            >= 'a' and <= 'z' => (char)('a' + ((ch - 'a' + k) % 26)),
            >= 'A' and <= 'Z' => (char)('A' + ((ch - 'A' + k) % 26)),
            _ => ch,
        }));
    }

    private static void RegisterEncoding(Action<string, StdFunction> F)
    {
        static Value B(NativeContext c, byte[] data)
        {
            c.Charge(data.Length / 16);
            return Value.FromObject(new BytesObject(data));
        }

        static byte[] D(Value v) => v.As<BytesObject>().Data;
        static Value Decode(NativeContext c, Func<byte[]> decode, string what)
        {
            try
            {
                return B(c, decode());
            }
            catch (FormatException)
            {
                throw c.Throw("FormatError", $"Invalid {what} text.");
            }
        }

        F("Utf8Encode/1", (c, a) => B(c, Encoding.UTF8.GetBytes(a[0].AsString)));
        F("Utf8Decode/1", (c, a) => Str(c, Encoding.UTF8.GetString(D(a[0]))));
        F("AsciiEncode/1", (c, a) => B(c, Encoding.ASCII.GetBytes(a[0].AsString)));
        F("AsciiDecode/1", (c, a) => Str(c, string.Concat(D(a[0]).Select(b => b < 128 ? (char)b : '?'))));
        F("Latin1Encode/1", (c, a) => B(c, Encoding.Latin1.GetBytes(a[0].AsString)));
        F("Latin1Decode/1", (c, a) => Str(c, Encoding.Latin1.GetString(D(a[0]))));
        F("HexEncode/1", (c, a) => Str(c, Convert.ToHexString(D(a[0])).ToLowerInvariant()));
        F("HexDecode/1", (c, a) => Decode(c, () => Convert.FromHexString(a[0].AsString.Replace(" ", string.Empty, StringComparison.Ordinal)), "hexadecimal"));
        F("Base64Encode/1", (c, a) => Str(c, Convert.ToBase64String(D(a[0]))));
        F("Base64Decode/1", (c, a) => Decode(c, () => Convert.FromBase64String(a[0].AsString), "Base64"));
        F("Base64UrlEncode/1", (c, a) => Str(c, Convert.ToBase64String(D(a[0])).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
        F("Base64UrlDecode/1", (c, a) => Decode(c, () =>
        {
            var s = a[0].AsString.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
        }, "Base64Url"));
        F("Base32Encode/1", (c, a) => Str(c, Base32(D(a[0]))));
        F("Base32Decode/1", (c, a) => B(c, Base32Decode(c, a[0].AsString)));
        F("UrlEncode/1", (c, a) => Str(c, Uri.EscapeDataString(a[0].AsString)));
        F("UrlDecode/1", (c, a) => Str(c, Uri.UnescapeDataString(a[0].AsString)));
        F("HtmlEscape/1", (c, a) => Str(c, a[0].AsString.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal)));
        F("HtmlUnescape/1", (c, a) => Str(c, WebUtility.HtmlDecode(a[0].AsString)));
        F("Rot13/1", (c, a) => Str(c, Shift(a[0].AsString, 13)));
        F("Caesar/2", (c, a) => Str(c, Shift(a[0].AsString, (int)(a[1].Bits % 26))));
        F("Xor/2", (c, a) =>
        {
            var (data, key) = (D(a[0]), D(a[1]));
            if (key.Length == 0)
            {
                throw c.Throw("ArgumentError", "The XOR key is empty.");
            }

            return B(c, [.. data.Select((b, i) => (byte)(b ^ key[i % key.Length]))]);
        });
    }

    #endregion

    #region Markdown and templates

    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex LinkPattern = new(@"(!?)\[([^\]]*)\]\(([^)\s]*)(?:\s+""[^""]*"")?\)", RegexOptions.CultureInvariant);
    private static readonly Regex EmphasisPattern = new(@"(\*\*|__)(.+?)\1|(\*|_)(?=\S)(.+?)(?<=\S)\3|~~(.+?)~~", RegexOptions.CultureInvariant);
    private static readonly Regex CodeSpan = new(@"`([^`]*)`", RegexOptions.CultureInvariant);
    private static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d+[.)])\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*(?:\|\s*([a-z]+)\s*)?\}\}", RegexOptions.CultureInvariant);

    private static void RegisterMarkdown(Action<string, StdFunction> F)
    {
        F("ToPlainText/1", (c, a) =>
        {
            var lines = new List<string>();
            var inFence = false;
            foreach (var raw in a[0].AsString.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    lines.Add(raw);
                    continue;
                }

                var line = raw;
                if (HeadingLine.Match(line) is { Success: true } h)
                {
                    line = h.Groups[2].Value;
                }

                line = line.TrimStart().StartsWith('>') ? line.TrimStart()[1..].TrimStart() : line;
                line = ListItem.Replace(line, m => m.Groups[1].Value + "• ");
                line = LinkPattern.Replace(line, m => m.Groups[2].Value);
                line = CodeSpan.Replace(line, m => m.Groups[1].Value);
                for (var i = 0; i < 3; i++)
                {
                    line = EmphasisPattern.Replace(line, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value);
                }

                if (line.Trim() is "---" or "***" or "___")
                {
                    line = string.Empty;
                }

                lines.Add(line);
            }

            return Str(c, string.Join("\n", lines).Trim('\n'));
        });
        F("Headings/1", (c, a) => c.NewArray([.. a[0].AsString.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => HeadingLine.Match(l)).Where(m => m.Success)
            .Select(m => Tuple(Value.FromInt(m.Groups[1].Length), Value.FromString(m.Groups[2].Value)))]));
        F("Links/1", (c, a) => c.NewArray([.. LinkPattern.Matches(a[0].AsString).Where(m => m.Groups[1].Value.Length == 0)
            .Select(m => Tuple(Value.FromString(m.Groups[2].Value), Value.FromString(m.Groups[3].Value)))]));
    }

    private static string Render(NativeContext c, string template, MapState values, string? fallback) =>
        Placeholder.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            string text;
            if (values.Table.TryGetValue(Value.FromString(name), out var v))
            {
                text = Display(c, v);
            }
            else
            {
                text = fallback ?? throw c.Throw("FormatError", $"The template has no value for '{name}'.");
            }

            return m.Groups[2].Success ? m.Groups[2].Value switch
            {
                "upper" => text.ToUpperInvariant(),
                "lower" => text.ToLowerInvariant(),
                "trim" => text.Trim(),
                "html" => WebUtility.HtmlEncode(text),
                "url" => Uri.EscapeDataString(text),
                var other => throw c.Throw("FormatError", $"Unknown template filter '{other}' (use upper, lower, trim, html, url)."),
            } : text;
        });

    private static void RegisterTemplate(Action<string, StdFunction> F)
    {
        F("Render/2", (c, a) => Str(c, Render(c, a[0].AsString, c.State<MapState>(a[1]), null)));
        F("RenderOr/3", (c, a) => Str(c, Render(c, a[0].AsString, c.State<MapState>(a[1]), a[2].AsString)));
        F("Placeholders/1", (c, a) => c.NewArray([.. Placeholder.Matches(a[0].AsString).Select(m => m.Groups[1].Value).Distinct().Select(Value.FromString)]));
    }

    #endregion

    #region Diff

    private const int Same = 0, Added = 1, Removed = 2;

    private static string[] SplitLines(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    /// <summary>Myers' O((N+M)·D) shortest edit script.</summary>
    internal static List<(int Kind, string Text)> Myers(NativeContext c, string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var v = new int[(2 * max) + 2];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            c.Charge(1 + ((n + m) / 16));
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]) ? v[max + k + 1] : v[max + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[max + k] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(trace, a, b, max, d);
                }
            }
        }

        return [];
    }

    private static List<(int, string)> Backtrack(List<int[]> trace, string[] a, string[] b, int max, int dFinal)
    {
        var result = new List<(int, string)>();
        int x = a.Length, y = b.Length;
        for (var d = dFinal; d >= 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var prevK = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]) ? k + 1 : k - 1;
            var prevX = v[max + prevK];
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                result.Add((Same, a[--x]));
                y--;
            }

            if (d > 0)
            {
                if (x == prevX)
                {
                    result.Add((Added, b[--y]));
                }
                else
                {
                    result.Add((Removed, a[--x]));
                }
            }
        }

        result.Reverse();
        return result;
    }

    private static Value Lines(NativeContext c, List<(int Kind, string Text)> edits) =>
        c.NewArray([.. edits.Select(e => c.NewInstance(c.Class(M, "DiffLine"), new DiffLineState(e.Kind, e.Text)))]);

    private static string Unified(List<(int Kind, string Text)> edits, string from, string to)
    {
        var sb = new StringBuilder($"--- {from}\n+++ {to}\n");
        const int context = 3;
        var changes = edits.Select((e, i) => (e, i)).Where(p => p.e.Kind != Same).Select(p => p.i).ToList();
        var index = 0;
        while (index < changes.Count)
        {
            var start = Math.Max(0, changes[index] - context);
            var end = changes[index];
            while (index < changes.Count && changes[index] <= end + (2 * context))
            {
                end = changes[index++];
            }

            end = Math.Min(edits.Count - 1, end + context);
            int oldLine = 1 + edits.Take(start).Count(e => e.Kind != Added), newLine = 1 + edits.Take(start).Count(e => e.Kind != Removed);
            var hunk = edits.Skip(start).Take(end - start + 1).ToList();
            var oldCount = hunk.Count(e => e.Kind != Added);
            var newCount = hunk.Count(e => e.Kind != Removed);
            sb.Append($"@@ -{(oldCount == 0 ? oldLine - 1 : oldLine)},{oldCount} +{(newCount == 0 ? newLine - 1 : newLine)},{newCount} @@\n");
            foreach (var (kind, text) in hunk)
            {
                sb.Append(kind switch { Added => '+', Removed => '-', _ => ' ' }).Append(text).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string Apply(NativeContext c, string original, string patch)
    {
        var lines = SplitLines(original).ToList();
        var patchLines = SplitLines(patch);
        var offset = 0;
        for (var i = 0; i < patchLines.Length; i++)
        {
            var header = Regex.Match(patchLines[i], @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");
            if (!header.Success)
            {
                continue;
            }

            var hunk = new List<string>();
            for (i++; i < patchLines.Length && !patchLines[i].StartsWith("@@", StringComparison.Ordinal); i++)
            {
                if (patchLines[i].Length > 0 && patchLines[i][0] is ' ' or '-' or '+')
                {
                    hunk.Add(patchLines[i]);
                }
                else if (patchLines[i].Length == 0)
                {
                    hunk.Add(" ");
                }
            }

            i--;
            var expected = hunk.Where(h => h[0] != '+').Select(h => h[1..]).ToList();
            var replacement = hunk.Where(h => h[0] != '-').Select(h => h[1..]).ToList();
            var oldStart = int.Parse(header.Groups[1].Value, Invariant);
            var at = Math.Max(0, (expected.Count == 0 ? oldStart : oldStart - 1) + offset);
            bool MatchesAt(int position) => position >= 0 && position + expected.Count <= lines.Count
                && expected.Select((e, j) => lines[position + j] == e).All(x => x);
            if (!MatchesAt(at))
            {
                var candidates = Enumerable.Range(0, Math.Max(0, lines.Count - expected.Count + 1)).Where(MatchesAt).ToList();
                at = candidates.Count == 1 ? candidates[0] : throw c.Throw("FormatError", $"Patch hunk at line {oldStart} does not apply.");
            }

            lines.RemoveRange(at, expected.Count);
            lines.InsertRange(at, replacement);
            offset += replacement.Count - expected.Count;
            c.Charge(1 + (hunk.Count / 4));
        }

        return string.Join("\n", lines) + (original.EndsWith('\n') || lines.Count > 0 ? "\n" : string.Empty);
    }

    private static void RegisterDiff(NativeRegistry r)
    {
        var F = r.Scope($"{M}.Diff");
        F("Lines/2", (c, a) => Lines(c, Myers(c, SplitLines(a[0].AsString), SplitLines(a[1].AsString))));
        F("Words/2", (c, a) =>
        {
            static string[] Words(string s) => s.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return Lines(c, Myers(c, Words(a[0].AsString), Words(a[1].AsString)));
        });
        F("Unified/4", (c, a) => Str(c, Unified(Myers(c, SplitLines(a[0].AsString), SplitLines(a[1].AsString)), a[2].AsString, a[3].AsString)));
        F("Apply/2", (c, a) => Str(c, Apply(c, a[0].AsString, a[1].AsString)));

        var L = r.Scope($"{M}.DiffLine");
        L("get_Kind/0", (c, a) => Value.FromEnum(c.State<DiffLineState>(a[0]).Kind, c.Class(M, "DiffKind")));
        L("get_Text/0", (c, a) => Value.FromString(c.State<DiffLineState>(a[0]).Text));
        L("ToString/0", (c, a) => Value.FromString((c.State<DiffLineState>(a[0]).Kind switch { Added => "+", Removed => "-", _ => " " }) + c.State<DiffLineState>(a[0]).Text));
    }

    #endregion

    #region Tokenizer and TokenStream

    private static Value TokenValue(NativeContext c, TokenState state) => c.NewInstance(c.Class(M, "Token"), state);

    private static Value[] Tokenize(NativeContext c, TokenizerState rules, string text)
    {
        var tokens = new List<Value>();
        int position = 0, line = 1, column = 1;
        while (position < text.Length)
        {
            string? bestKind = null;
            var bestLength = 0;
            var skip = false;
            foreach (var (kind, pattern) in rules.Rules)
            {
                c.Charge(1);
                var m = pattern.Match(text, position, text.Length - position);
                if (m.Success && m.Index == position && m.Length > bestLength)
                {
                    (bestKind, bestLength, skip) = (kind, m.Length, kind is null);
                }
            }

            if (bestLength == 0)
            {
                throw c.Throw("FormatError", $"Unexpected character '{text[position]}' at line {line}, column {column}.");
            }

            var lexeme = text.Substring(position, bestLength);
            if (!skip)
            {
                tokens.Add(TokenValue(c, new TokenState(bestKind!, lexeme, position, line, column)));
            }

            foreach (var ch in lexeme)
            {
                (line, column) = ch == '\n' ? (line + 1, 1) : (line, column + 1);
            }

            position += bestLength;
        }

        return [.. tokens];
    }

    private static void RegisterTokens(NativeRegistry r)
    {
        var T = r.Scope($"{M}.Token");
        static TokenState S(NativeContext c, Value v) => c.State<TokenState>(v);
        T("get_Kind/0", (c, a) => Value.FromString(S(c, a[0]).Kind));
        T("get_Text/0", (c, a) => Value.FromString(S(c, a[0]).Text));
        T("get_Index/0", (c, a) => Value.FromInt(S(c, a[0]).Index));
        T("get_Line/0", (c, a) => Value.FromInt(S(c, a[0]).Line));
        T("get_Column/0", (c, a) => Value.FromInt(S(c, a[0]).Column));
        T("ToString/0", (c, a) => Value.FromString($"{S(c, a[0]).Kind}({S(c, a[0]).Text})"));

        var Z = r.Scope($"{M}.Tokenizer");
        static Value AddRule(NativeContext c, Value[] a, string? kind, string pattern) =>
            Mutate<TokenizerState>(c, a[0], s =>
            {
                s.Rules.Add((kind, Compile(c, "^(?:" + pattern + ")")));
                return a[0];
            });
        Z("Tokenizer/0", (c, a) => CollectionNatives.Init(a[0], new TokenizerState()));
        Z("Rule/2", (c, a) => AddRule(c, a, a[1].AsString, a[2].AsString));
        Z("Skip/1", (c, a) => AddRule(c, a, null, a[1].AsString));
        Z("Tokenize/1", (c, a) => c.NewArray(Tokenize(c, c.State<TokenizerState>(a[0]), a[1].AsString)));
        Z("Stream/1", (c, a) => c.NewInstance(c.Class(M, "TokenStream"), new TokenStreamState(Tokenize(c, c.State<TokenizerState>(a[0]), a[1].AsString))));

        var Q = r.Scope($"{M}.TokenStream");
        static TokenStreamState Ts(NativeContext c, Value v) => c.State<TokenStreamState>(v);
        static string Where(NativeContext c, TokenStreamState s) => s.Position < s.Tokens.Length
            ? $"line {c.State<TokenState>(s.Tokens[s.Position]).Line}, column {c.State<TokenState>(s.Tokens[s.Position]).Column}"
            : "end of input";
        Q("TokenStream/1", (c, a) => CollectionNatives.Init(a[0], new TokenStreamState((Value[])Items(a[1]).Clone())));
        Q("get_AtEnd/0", (c, a) => Value.FromBool(Ts(c, a[0]).Position >= Ts(c, a[0]).Tokens.Length));
        Q("Peek/0", (c, a) => Ts(c, a[0]) is var s && s.Position < s.Tokens.Length ? s.Tokens[s.Position] : Value.Null);
        Q("PeekAt/1", (c, a) => Ts(c, a[0]) is var s && s.Position + a[1].Bits is var i && i >= 0 && i < s.Tokens.Length ? s.Tokens[i] : Value.Null);
        Q("Next/0", (c, a) =>
        {
            var s = Ts(c, a[0]);
            if (s.Position >= s.Tokens.Length)
            {
                throw c.Throw("FormatError", "Unexpected end of input.");
            }

            c.Mutating(s);
            return s.Tokens[s.Position++];
        });
        Q("Accept/1", (c, a) =>
        {
            var s = Ts(c, a[0]);
            if (s.Position < s.Tokens.Length && c.State<TokenState>(s.Tokens[s.Position]).Kind == a[1].AsString)
            {
                c.Mutating(s);
                return s.Tokens[s.Position++];
            }

            return Value.Null;
        });
        Q("Expect/1", (c, a) =>
        {
            var s = Ts(c, a[0]);
            if (s.Position < s.Tokens.Length && c.State<TokenState>(s.Tokens[s.Position]).Kind == a[1].AsString)
            {
                c.Mutating(s);
                return s.Tokens[s.Position++];
            }

            var found = s.Position < s.Tokens.Length ? $"'{c.State<TokenState>(s.Tokens[s.Position]).Text}'" : "end of input";
            throw c.Throw("FormatError", $"Expected {a[1].AsString} but found {found} at {Where(c, s)}.");
        });
        Q("get_Position/0", (c, a) => Value.FromInt(Ts(c, a[0]).Position));
        Q("set_Position/1", (c, a) => Mutate<TokenStreamState>(c, a[0], s =>
        {
            s.Position = (int)Math.Clamp(a[1].Bits, 0, s.Tokens.Length);
            return Value.Null;
        }));
    }

    #endregion

    #region Search

    internal static string GlobToRegex(string glob, bool paths)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var ch = glob[i];
            switch (ch)
            {
                case '*' when paths && i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }

                    break;
                case '*':
                    sb.Append(paths ? "[^/]*" : ".*");
                    break;
                case '?':
                    sb.Append(paths ? "[^/]" : ".");
                    break;
                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }

                    var set = glob[(i + 1)..close];
                    sb.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]).Replace(@"\-", "-", StringComparison.Ordinal) : Regex.Escape(set).Replace(@"\-", "-", StringComparison.Ordinal)).Append(']');
                    i = close;
                    break;
                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        return sb.Append('$').ToString();
    }

    internal static double Fuzzy(string query, string text)
    {
        if (query.Length == 0)
        {
            return 1;
        }

        var (q, t) = (query.ToLowerInvariant(), text.ToLowerInvariant());
        double score = 0;
        int ti = 0, previous = -2;
        foreach (var ch in q)
        {
            var found = t.IndexOf(ch, ti);
            if (found < 0)
            {
                return 0;
            }

            var wordStart = found == 0 || !char.IsLetterOrDigit(text[found - 1]) || (char.IsUpper(text[found]) && char.IsLower(text[found - 1]));
            score += 1 + (found == previous + 1 ? 1 : 0) + (wordStart ? 1 : 0);
            previous = found;
            ti = found + 1;
        }

        return score / (3.0 * q.Length) * (0.5 + (0.5 * q.Length / t.Length));
    }

    private static Value Hits(NativeContext c, IEnumerable<SearchHitState> hits) =>
        c.NewArray([.. hits.Select(h => c.NewInstance(c.Class(M, "SearchHit"), h))]);

    private static void RegisterSearch(NativeRegistry r)
    {
        var F = r.Scope($"{M}.Search");
        F("Glob/2", (c, a) =>
        {
            var regex = Compile(c, GlobToRegex(a[0].AsString, paths: true));
            return Value.FromBool(regex.IsMatch(a[1].AsString));
        });
        F("Wildcard/2", (c, a) => Value.FromBool(Compile(c, GlobToRegex(a[0].AsString, paths: false), "s").IsMatch(a[1].AsString)));
        F("Fuzzy/2", (c, a) =>
        {
            c.Charge(1 + (a[1].AsString.Length / 16));
            return Value.FromFloat(Fuzzy(a[0].AsString, a[1].AsString));
        });
        F("FuzzyRank/3", (c, a) =>
        {
            var query = a[0].AsString;
            var limit = Int32(c, a[2].Bits, "Limit", 0);
            var ranked = Iterate(c, a[1]).Select((v, i) => (Text: v.AsString, Index: i, Score: Fuzzy(query, v.AsString)))
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Index).Take(limit).ToList();
            c.Charge(ranked.Count + 1);
            return c.NewArray([.. ranked.Select(x => Value.FromString(x.Text))]);
        });
        F("Grep/2", (c, a) =>
        {
            var regex = Compile(c, a[0].AsString);
            var hits = new List<SearchHitState>();
            var number = 0;
            foreach (var line in Iterate(c, a[1]))
            {
                number++;
                ChargeScan(c, regex, line.AsString);
                if (regex.IsMatch(line.AsString))
                {
                    hits.Add(new SearchHitState(string.Empty, number, line.AsString));
                }
            }

            return Hits(c, hits);
        });
        F("GrepFiles/2", (c, a) =>
        {
            var regex = Compile(c, a[0].AsString);
            var files = c.State<MapState>(a[1]).Table;
            var hits = new List<SearchHitState>();
            foreach (var slot in files.Slots())
            {
                var lines = files.ValueAt(slot).AsString.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    ChargeScan(c, regex, lines[i]);
                    if (regex.IsMatch(lines[i]))
                    {
                        hits.Add(new SearchHitState(files.KeyAt(slot).AsString, i + 1, lines[i]));
                    }
                }
            }

            return Hits(c, hits);
        });

        var H = r.Scope($"{M}.SearchHit");
        H("get_File/0", (c, a) => Value.FromString(c.State<SearchHitState>(a[0]).File));
        H("get_Line/0", (c, a) => Value.FromInt(c.State<SearchHitState>(a[0]).Line));
        H("get_Text/0", (c, a) => Value.FromString(c.State<SearchHitState>(a[0]).Text));
        H("ToString/0", (c, a) =>
        {
            var h = c.State<SearchHitState>(a[0]);
            return Value.FromString(h.File.Length > 0 ? $"{h.File}:{h.Line}: {h.Text}" : $"{h.Line}: {h.Text}");
        });
    }

    #endregion
}
