using System.IO.Compression;
using Ghostrig.CScript.Runtime;
using static Ghostrig.CScript.Tests.Std.StdProgram;

namespace Ghostrig.CScript.Tests.Std;

public class TextTests
{
    private const string Imports = "import Std.Text;\nimport Std.Collections;";

    [Fact]
    public void StringBuilder_and_Format()
    {
        var result = Run(Imports, """
            string Main() {
                var sb = new StringBuilder("ab");
                sb.Append("cd").Append('!').Insert(0, ">").Replace("cd", "CD").AppendRepeat("-", 3);
                var table = Format.Table([["Port", "State"], ["21", "open"], ["8443", "filtered"]]);
                return $"{sb}|{sb.Length}|{Format.Bytes(4000000000)}|{Format.Bytes(512)}|{Format.Bytes(1536)}|{Format.Number(1234567)}|{Format.Percent(0.256, 1)}|{Format.Hex(255, 4)}|{Format.Binary(5, 8)}|{Format.Ordinal(22)}|{Format.Ordinal(13)}|{Format.Duration(Duration.FromMilliseconds(1500))}|{Format.Duration(Duration.FromMinutes(182))}|{Format.Truncate("verbena-and-rust", 8)}\n{table}";
            }
            """);
        Assert.Equal("""
            >abCD!---|9|3.7 GB|512 B|1.5 KB|1,234,567|25.6%|00ff|00000101|22nd|13th|1.5 s|3 h 2 min|verbena…
            Port  State
            ----  --------
            21    open
            8443  filtered
            """.Replace("\r\n", "\n"), result);
    }

    [Fact]
    public void Regular_expressions_capture_replace_and_split()
    {
        var result = Run(Imports, """
            string Main() {
                var log = "2056-11-14T23:42:00 GET /orchids 200\n2056-11-14T23:43:10 POST /login 401";
                var line = new Regex(@"(?<date>\d{4}-\d{2}-\d{2})T(?<time>[\d:]+) (\w+) (\S+) (\d{3})", "m");
                var all = line.Matches(log);
                var m = line.Match(log)!;
                var masked = new Regex(@"\d{3}$", "m").ReplaceWith(log, x => "***");
                var swapped = new Regex(@"(\w+)@(\w+)").Replace("kael@ny56", "$2:$1");
                var parts = new Regex(@"[,;]\s*").Split("a, b;c");
                return $"{all.Length}|{m.Group("date")}|{all[1].Group(3)}|{m.Group(1)}|{masked.Contains("***")}|{swapped}|{parts.Length}|{new Regex("ORCHID", "i").IsMatch(log)}|{Regex.Escape("a.b")}";
            }
            """);
        Assert.Equal("2|2056-11-14|401|GET|true|ny56:kael|3|true|a\\.b", result);
    }

    [Fact]
    public void Regex_rejects_backtracking_only_constructs() =>
        Assert.Contains("FormatError", ErrorOf(Imports, """string Main() => $"{new Regex(@"(a)\1").IsMatch("aa")}";"""));

    [Fact]
    public void Encodings_round_trip()
    {
        var result = Run(Imports, """
            string Main() {
                var data = bytes.FromText("Verbena & Rust");
                var b64 = Encoding.Base64Encode(data);
                var b32 = Encoding.Base32Encode(bytes.FromText("foobar"));
                var xored = Encoding.Xor(data, bytes.FromHex("2a"));
                return $"{b64}|{Encoding.Base64Decode(b64).ToText()}|{b32}|{Encoding.Base32Decode(b32).ToText()}|{Encoding.UrlEncode("a b&c/d")}|{Encoding.UrlDecode("a%20b")}|{Encoding.HtmlEscape("<b>\"x\"</b>")}|{Encoding.HtmlUnescape("&lt;&amp;&gt;")}|{Encoding.Rot13("Kael")}|{Encoding.Xor(xored, bytes.FromHex("2a")).ToText()}|{Encoding.HexEncode(Encoding.Latin1Encode("é"))}|{Encoding.Base64UrlEncode(bytes.FromHex("fbff"))}";
            }
            """);
        Assert.Equal("VmVyYmVuYSAmIFJ1c3Q=|Verbena & Rust|MZXW6YTBOI======|foobar|a%20b%26c%2Fd|a b|&lt;b&gt;&quot;x&quot;&lt;/b&gt;|<&>|Xnry|Verbena & Rust|e9|-_8", result);
    }

    [Fact]
    public void Csv_parses_rfc4180_and_typed_columns()
    {
        var result = Run(Imports, """
            string Main() {
                var text = "name,price,notes\r\n\"Ghost Orchid\",120,\"glows, faintly\"\r\nVanda,45,\"say \"\"hi\"\"\"\r\n";
                var rows = Csv.Parse(text);
                var table = Csv.Table(text);
                var prices = table.IntColumn("price");
                var roundTrip = Csv.Parse(Csv.Write(rows));
                return $"{rows.Length}|{rows[1][2]}|{rows[2][2]}|{table.RowCount}|{prices[0] + prices[1]}|{table.Get(1, "name")}|{roundTrip[1][2]}|{Csv.Parse("a;b", ';')[0].Length}";
            }
            """);
        Assert.Equal("3|glows, faintly|say \"hi\"|2|165|Vanda|glows, faintly|2", result);
    }

    [Fact]
    public void Json_parses_queries_builds_and_serializes()
    {
        var result = Run(Imports, """"
            class Host : Serializable { string Name; int[] Ports; bool Online; Host(string n, int[] p) { Name = n; Ports = p; Online = true; } }
            string Main() {
                var doc = JsonValue.Parse("{\"owner\":{\"name\":\"Verbena & Rust\"},\"servers\":[{\"ip\":\"10.56.4.20\",\"ports\":[80,2121]},{\"ip\":\"10.56.4.21\",\"ports\":[8443]}],\"ratio\":0.5}");
                var ports = doc.Query("servers[*].ports[*]");
                var built = JsonValue.EmptyObject.With("id", JsonValue.Of(7)).With("tags", JsonValue.EmptyArray.Append(JsonValue.Of("ftp")));
                var map = new Map<string, int>(); map["a"] = 1; map["b"] = 2;
                var host = JsonValue.Serialize(new Host("drop-node", [22, 2121]));
                return $"{doc.Get("owner").Get("name").AsString()}|{ports.Length}|{doc.Query("$.servers[1].ip")[0].AsString()}|{doc.Get("ratio").AsFloat()}|{doc.Get("servers").Count}|{built}|{JsonValue.Serialize(map)}|{host}|{doc.Get("servers").At(0).Kind}|{JsonValue.Parse("[1, 2.0]").At(1).Kind}|{doc.Has("missing")}";
            }
            """");
        Assert.Equal("""Verbena & Rust|3|10.56.4.21|0.5|2|{"id":7,"tags":["ftp"]}|{"a":1,"b":2}|{"Name":"drop-node","Ports":[22,2121],"Online":true}|Object|Number|false""", result);
    }

    [Fact]
    public void Json_reports_errors_with_positions_and_pretty_prints()
    {
        Assert.Contains("line 2", ErrorOf(Imports, "string Main() => JsonValue.Parse(\"{\\n  \\\"a\\\": tru }\").ToString();"));
        Assert.Equal("{\n  \"a\": [\n    1,\n    2\n  ]\n}", Run(Imports, """string Main() => JsonValue.Parse("{\"a\":[1,2]}").Pretty();"""));
    }

    [Fact]
    public void Cfg_markdown_and_templates()
    {
        var result = Run(Imports, """
            string Main() {
                var cfg = Cfg.Parse("name = ironclad\nversion = 1.2.0\n# comment\n[ftp]\nport = 2121\nanonymous = yes\n");
                var edited = cfg.With("ftp.port", "21").With("ftp.banner", "hi");
                var md = "# Orchids\nSome **bold** and *soft* text with [a link](http://v.ny56).\n- `ghost` orchid";
                var values = new Map<string, string>(); values["name"] = "Kael"; values["amount"] = "120";
                var phish = Template.Render("Dear {{ name|upper }}, your invoice of ${{amount}} is due.", values);
                return $"{cfg.Get("name")}|{cfg.GetInt("ftp.port", 0)}|{cfg.GetBool("ftp.anonymous", false)}|{edited.GetInt("ftp.port", 0)}|{cfg.Sections.Length}|{cfg.Get("nope") == null}|{Markdown.ToPlainText(md)}|{Markdown.Headings(md)[0].Item2}|{Markdown.Links(md)[0].Item2}|{phish}|{Template.RenderOr("{{x}}-{{y}}", values, "?")}";
            }
            """);
        Assert.Equal("ironclad|2121|true|21|1|true|Orchids\nSome bold and soft text with a link.\n• ghost orchid|Orchids|http://v.ny56|Dear KAEL, your invoice of $120 is due.|?-?", result);
    }

    [Fact]
    public void Diff_creates_and_applies_unified_patches()
    {
        var result = Run(Imports, """
            string Main() {
                var before = "void Send() {\n    for (var p in packets) {\n        Hardware.Send(p);\n    }\n}\n";
                var after = "void Send() {\n    Hardware.SendBatch(packets);\n}\n";
                var patch = Diff.Unified(before, after, "a/NetCardDriver.drv", "b/NetCardDriver.drv");
                var applied = Diff.Apply(before, patch);
                var lines = Diff.Lines(before, after);
                var removed = 0;
                foreach (var l in lines) { if (l.Kind == DiffKind.Removed) removed++; }
                return $"{applied == after}|{removed}|{patch.StartsWith("--- a/NetCardDriver.drv")}|{Diff.Words("the quick fox", "the slow fox").Length}";
            }
            """);
        Assert.Equal("true|3|true|4", result);
        Assert.Contains("does not apply", ErrorOf(Imports, """
            string Main() => Diff.Apply("x\ny\n", Diff.Unified("a\nb\n", "a\nc\n", "f", "f"));
            """));
    }

    [Fact]
    public void Tokenizer_and_token_stream_drive_a_recursive_descent_parser()
    {
        var program = Load("""
            module Test;
            import Std.Text;
            Tokenizer lexer = new Tokenizer().Skip(@"\s+").Rule("num", @"\d+").Rule("op", @"[-+*/()]");
            int Expr(TokenStream s) {
                var value = Term(s);
                while (true) {
                    var t = s.Peek();
                    if (t == null || (t.Text != "+" && t.Text != "-")) return value;
                    s.Next();
                    value = t.Text == "+" ? value + Term(s) : value - Term(s);
                }
            }
            int Term(TokenStream s) {
                var value = Factor(s);
                while (s.Peek() != null && (s.Peek()!.Text == "*" || s.Peek()!.Text == "/")) {
                    var op = s.Next().Text;
                    value = op == "*" ? value * Factor(s) : value / Factor(s);
                }
                return value;
            }
            int Factor(TokenStream s) {
                if (s.Accept("op") is Token open) { var v = Expr(s); s.Expect("op"); return v; }
                return int.Parse(s.Expect("num").Text);
            }
            string Main() {
                var tokens = lexer.Tokenize("2 + 3 * (4 - 1)\n  * 2");
                return $"{Expr(lexer.Stream("2 + 3 * (4 - 1) * 2"))}|{tokens.Length}|{tokens[tokens.Length - 1].Line}:{tokens[tokens.Length - 1].Column}";
            }
            """);
        Assert.Equal("20|11|2:5", program.Text());
    }

    [Fact]
    public void Search_globs_fuzzy_ranking_and_grep()
    {
        var result = Run(Imports, """
            string Main() {
                var files = new Map<string, string>();
                files["/var/log/ftp.log"] = "login ok\nlogin FAILED kael\n";
                files["/var/log/http.log"] = "GET /\nPOST /login FAILED\n";
                var hits = Search.GrepFiles("FAILED", files);
                var ranked = Search.FuzzyRank("nc", ["NetCardDriver.drv", "LegacyGPUDriver.drv", "nc.cfg", "notes.txt"], 2);
                return $"{Search.Glob("/sys/drivers/**/*.drv", "/sys/drivers/net/NetCardDriver.drv")}|{Search.Glob("/sys/*.drv", "/sys/net/x.drv")}|{Search.Glob("log[0-9].txt", "log7.txt")}|{Search.Wildcard("*orchid?", "ghost orchids")}|{hits.Length}|{hits[1]}|{string.Join(",", ranked)}|{Search.Fuzzy("xyz", "abc")}";
            }
            """);
        Assert.Equal("true|false|true|true|2|/var/log/http.log:2: POST /login FAILED|nc.cfg,NetCardDriver.drv|0.0", result);
    }
}

public class BinaryCompressTests
{
    private const string Imports = "import Std.Binary;\nimport Std.Compress;";

    [Fact]
    public void Readers_and_writers_handle_endianness_varints_and_strings()
    {
        var result = Run(Imports, """
            string Main() {
                var w = new BinaryWriter(Endian.Big);
                w.WriteU16(2121); w.WriteI32(-2); w.WriteVarint(300); w.WriteSignedVarint(-3); w.WriteString("orchid"); w.WriteF64(0.5);
                var data = w.ToBytes();
                var r = new BinaryReader(data, Endian.Big);
                var port = r.ReadU16(); var neg = r.ReadI32(); var v = r.ReadVarint(); var sv = r.ReadSignedVarint(); var s = r.ReadString(); var f = r.ReadF64();
                var le = new BinaryWriter(Endian.Little); le.WriteU32(1);
                return $"{data.Slice(0, 2).ToHex()}|{port}|{neg}|{v}|{sv}|{s}|{f}|{r.AtEnd}|{le.ToBytes().ToHex()}";
            }
            """);
        Assert.Equal("0849|2121|-2|300|-3|orchid|0.5|true|01000000", result);
        Assert.Contains("ProtocolError", ErrorOf(Imports, """string Main() => $"{new BinaryReader(bytes.FromHex("01"), Endian.Big).ReadU32()}";"""));
    }

    [Fact]
    public void Struct_bits_buffers_checksums_and_hexdump()
    {
        var result = Run(Imports, """
            string Main() {
                var packed = Struct.Pack(">HHI", 21, 2121, 3735928559);
                var values = Struct.Unpack(">HHI", packed);
                var buf = new ByteBuffer();
                buf.Append(bytes.FromText("GET ")).Append((byte)47);
                buf[0] = (byte)72;
                var field = Bits.Insert(0, 4, 4, 15);
                var dump = HexDump.Format(bytes.FromText("Hello, Kael!"));
                return $"{packed.ToHex()}|{(int)values[1]}|{Struct.SizeOf("<bhiq")}|{buf.ToBytes().ToText()}|{field}|{Bits.Extract(field, 4, 4)}|{Bits.SignExtend(255, 8)}|{Checksum.Crc32(bytes.FromText("123456789")).ToHex()}|{Checksum.Adler32(bytes.FromText("Wikipedia")).ToHex()}|{Checksum.Fletcher16(bytes.FromText("abcde"))}|{Checksum.Crc16(bytes.FromText("123456789")).ToHex()}|{Checksum.Internet(bytes.FromHex("4500003c1c4640004006 0000ac100a63ac100a0c".Replace(" ", "")))}|{dump.StartsWith("00000000  48 65 6c 6c")}";
            }
            """);
        Assert.Equal("00150849deadbeef|2121|15|HET /|240|15|-1|cbf43926|11e60398|51440|29b1|45542|true", result);
    }

    [Theory]
    [InlineData("Rle", "Encode", "Decode")]
    [InlineData("Lz77", "Compress", "Decompress")]
    [InlineData("Deflate", "Compress", "Decompress")]
    [InlineData("Huffman", "Encode", "Decode")]
    public void Codecs_round_trip_and_shrink_repetitive_data(string codec, string encode, string decode)
    {
        var program = Load($$"""
            module Test;
            import Std.Compress;
            bytes Sample() {
                var text = "";
                for (var i = 0; i < 40; i++) { text += "BIO-ASSET-09 manifest line " + (i % 4) + "\n"; }
                return bytes.FromText(text + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            }
            bytes Runs() => bytes.FromText(string.Repeat("a", 3000) + string.Repeat("b", 3000));
            bytes Packed() => {{codec}}.{{encode}}(Sample());
            string Main() {
                var packed = Packed();
                return $"{{{codec}}.{{decode}}(packed) == Sample()}|{{{codec}}.{{encode}}(Runs()).Length < Runs().Length / 4}|{{{codec}}.{{decode}}({{codec}}.{{encode}}(bytes.Empty)).Length}";
            }
            """);
        Assert.Equal("true|true|0", program.Text());
        if (codec == "Deflate")
        {
            // Output must be standard DEFLATE: the platform inflater reads it.
            var packed = program.Call("Packed").As<BytesObject>().Data;
            using var stream = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            Assert.Equal(program.Call("Sample").As<BytesObject>().Data, output.ToArray());
        }
    }

    [Fact]
    public void Corrupt_compressed_data_raises_FormatError()
    {
        Assert.Contains("FormatError", ErrorOf(Imports, """string Main() => Lz77.Decompress(bytes.FromHex("0000001001ffff")).ToHex();"""));
        Assert.Contains("FormatError", ErrorOf(Imports, """string Main() => Deflate.Decompress(bytes.FromHex("ffffffff")).ToHex();"""));
    }
}
