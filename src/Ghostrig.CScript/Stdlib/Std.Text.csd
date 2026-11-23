/// Text processing (Document 7 §13.4). Everything here is deterministic and step-metered (about 1 step per 16 characters
/// processed unless stated otherwise). Values built here are immutable unless the class says otherwise.
module Std.Text;

import Std.Collections;

// ---------------------------------------------------------------- composition and formatting

/// A mutable text buffer. Mutators return the builder for chaining.
class StringBuilder {
    StringBuilder();
    StringBuilder(string initial);
    int Length { get; }
    char this[int index] { get; }
    StringBuilder Append(string text);
    @Native("Std.Text.StringBuilder.AppendChar") StringBuilder Append(char c);
    StringBuilder AppendLine();
    StringBuilder AppendLine(string text);
    StringBuilder AppendRepeat(string text, int count);
    StringBuilder Insert(int index, string text);
    StringBuilder Remove(int start, int count);
    StringBuilder Replace(string oldValue, string newValue);
    StringBuilder Clear();
    string ToString();
}

/// Human-readable number, size and layout formatting (invariant culture).
class Format {
    /// Binary units with one decimal: Format.Bytes(4_000_000_000) == "3.7 GB".
    static string Bytes(int count);
    /// "1.5 s", "250 ms", "3 h 2 min".
    static string Duration(Duration duration);
    /// Thousands separators: "1,234,567".
    static string Number(int value);
    static string Fixed(float value, int decimals);
    /// Format.Percent(0.256, 1) == "25.6%".
    static string Percent(float ratio, int decimals);
    /// Zero-padded hexadecimal of at least width digits.
    static string Hex(int value, int width);
    static string Binary(int value, int width);
    static string Ordinal(int n);
    static string Plural(int count, string singular, string plural);
    static string Center(string text, int width);
    /// Aligns rows into columns separated by two spaces (the first row is a header underlined with dashes).
    static string Table(string[][] rows);
    /// Truncates with an ellipsis to at most width characters.
    static string Truncate(string text, int width);
}

// ---------------------------------------------------------------- regular expressions

/// A regular expression with linear-time matching (no catastrophic backtracking): captures, named groups, classes,
/// anchors, alternation and quantifiers are supported; backreferences and lookaround raise FormatError.
/// Flags: "i" ignore case, "m" multi-line anchors, "s" dot matches newline, "x" ignore pattern whitespace.
/// Cost: (text length × pattern length) / 64 steps.
class Regex {
    Regex(string pattern);
    Regex(string pattern, string flags);
    string Pattern { get; }
    bool IsMatch(string text);
    RegexMatch? Match(string text);
    RegexMatch? Match(string text, int start);
    RegexMatch[] Matches(string text);
    /// $1, ${name} and $$ in the replacement.
    string Replace(string text, string replacement);
    string ReplaceWith(string text, fn(RegexMatch) -> string replacer);
    string[] Split(string text);
    static string Escape(string text);
    string ToString();
}

class RegexMatch {
    string Value { get; }
    int Index { get; }
    int Length { get; }
    /// Group 0 is the whole match; unmatched groups are "". Unnamed groups are numbered first, left to right, then
    /// named groups (so prefer Group("name") when a pattern mixes both).
    string Group(int index);
    @Native("Std.Text.RegexMatch.GroupNamed") string Group(string name);
    string[] Groups { get; }
    string ToString();
}

// ---------------------------------------------------------------- encodings

class Encoding {
    static bytes Utf8Encode(string text);
    static string Utf8Decode(bytes data);
    /// Characters above 127 become '?'.
    static bytes AsciiEncode(string text);
    static string AsciiDecode(bytes data);
    static bytes Latin1Encode(string text);
    static string Latin1Decode(bytes data);
    static string HexEncode(bytes data);
    static bytes HexDecode(string hex);
    static string Base64Encode(bytes data);
    static bytes Base64Decode(string text);
    /// URL- and filename-safe alphabet without padding.
    static string Base64UrlEncode(bytes data);
    static bytes Base64UrlDecode(string text);
    /// RFC 4648 Base32 with padding.
    static string Base32Encode(bytes data);
    static bytes Base32Decode(string text);
    /// Percent-encoding of everything but unreserved characters (RFC 3986).
    static string UrlEncode(string text);
    static string UrlDecode(string text);
    static string HtmlEscape(string text);
    static string HtmlUnescape(string text);
    static string Rot13(string text);
    static string Caesar(string text, int shift);
    /// Repeating-key XOR.
    static bytes Xor(bytes data, bytes key);
}

// ---------------------------------------------------------------- structured text

/// RFC 4180 CSV.
class Csv {
    static string[][] Parse(string text);
    static string[][] Parse(string text, char separator);
    static string Write(string[][] rows);
    /// Parses with the first row as headers.
    static CsvTable Table(string text);
}

class CsvTable {
    string[] Headers { get; }
    int RowCount { get; }
    string[] Row(int index);
    string Get(int row, string column);
    string[] Column(string name);
    /// Raises FormatError for a cell that is not an integer.
    int[] IntColumn(string name);
    float[] FloatColumn(string name);
    string ToString();
}

enum JsonKind { Null, Bool, Number, String, Array, Object }

/// An immutable JSON value (RFC 8259). Objects keep their key order; integers and floats stay distinct.
class JsonValue : Equatable<JsonValue> {
    /// Raises FormatError with the position of the first error.
    static JsonValue Parse(string text);
    static JsonValue Null { get; }
    static JsonValue EmptyObject { get; }
    static JsonValue EmptyArray { get; }
    @Native("Std.Text.JsonValue.OfString") static JsonValue Of(string value);
    @Native("Std.Text.JsonValue.OfInt") static JsonValue Of(int value);
    @Native("Std.Text.JsonValue.OfFloat") static JsonValue Of(float value);
    @Native("Std.Text.JsonValue.OfBool") static JsonValue Of(bool value);
    /// Converts strings, numbers, bools, arrays, Lists, Maps with string keys, JsonValues and Serializable objects (their fields).
    static JsonValue From<T>(T value);
    /// From(value).ToString().
    static string Serialize<T>(T value);

    JsonKind Kind { get; }
    bool IsNull { get; }
    /// Raises CastError when the kind does not match.
    string AsString();
    int AsInt();
    float AsFloat();
    bool AsBool();
    /// Array length or object member count.
    int Count { get; }
    /// Object member (raises IndexError when missing).
    JsonValue Get(string key);
    JsonValue? TryGet(string key);
    bool Has(string key);
    string[] Keys { get; }
    /// Array element.
    JsonValue At(int index);
    JsonValue[] Items { get; }
    /// A copy with the member set (objects) or appended (arrays).
    JsonValue With(string key, JsonValue value);
    JsonValue Without(string key);
    JsonValue Append(JsonValue item);
    /// Path query: "servers[0].ports[2]", "$.owner.name", "items[*].id" (returns every match).
    JsonValue[] Query(string path);
    /// Compact text.
    string ToString();
    /// Indented text.
    string Pretty();
    bool Equals(JsonValue other);
}

/// INI / .cfg files: [section] headers, key = value pairs, # and ; comments.
class Cfg {
    static Cfg Parse(string text);
    static Cfg Empty { get; }
    /// "section.key", or "key" for entries before any section.
    string? Get(string key);
    string GetOr(string key, string fallback);
    int GetInt(string key, int fallback);
    bool GetBool(string key, bool fallback);
    bool Has(string key);
    string[] Sections { get; }
    string[] KeysOf(string section);
    Cfg With(string key, string value);
    Cfg Without(string key);
    string ToString();
}

/// Markdown helpers (rich-text rendering arrives with CForms in M4).
class Markdown {
    /// Strips markup, keeping text, list bullets and code.
    static string ToPlainText(string markdown);
    /// "# Title" lines as (level, text).
    static (int, string)[] Headings(string markdown);
    /// [text](url) links as (text, url).
    static (string, string)[] Links(string markdown);
}

/// {{name}} templating with optional filters: {{name|upper}}, |lower, |trim, |html, |url.
class Template {
    /// Raises FormatError for a placeholder with no value.
    static string Render(string template, Map<string, string> values);
    /// Missing placeholders become fallback.
    static string RenderOr(string template, Map<string, string> values, string fallback);
    static string[] Placeholders(string template);
}

// ---------------------------------------------------------------- diffs

enum DiffKind { Same, Added, Removed }

class DiffLine {
    DiffKind Kind { get; }
    string Text { get; }
    string ToString();
}

/// Myers diff. Cost: (n + m) × edit distance / 16.
class Diff {
    static DiffLine[] Lines(string before, string after);
    static DiffLine[] Words(string before, string after);
    /// Unified patch with three lines of context.
    static string Unified(string before, string after, string fromName, string toName);
    /// Applies a unified patch; raises FormatError when a hunk does not match.
    static string Apply(string original, string patch);
}

// ---------------------------------------------------------------- tokenising and parsing

class Token {
    string Kind { get; }
    string Text { get; }
    int Index { get; }
    int Line { get; }
    int Column { get; }
    string ToString();
}

/// A lexer builder: rules are tried in order at each position (longest match wins ties broken by order).
class Tokenizer {
    Tokenizer();
    Tokenizer Rule(string kind, string pattern);
    /// Matches that are dropped (whitespace, comments).
    Tokenizer Skip(string pattern);
    /// Raises FormatError at the first unrecognised character.
    Token[] Tokenize(string text);
    TokenStream Stream(string text);
}

/// A cursor over tokens for hand-written recursive-descent parsers.
class TokenStream {
    TokenStream(Token[] tokens);
    bool AtEnd { get; }
    Token? Peek();
    Token? PeekAt(int offset);
    /// Raises FormatError at the end of input.
    Token Next();
    /// Consumes and returns the next token if it has the kind, otherwise null.
    Token? Accept(string kind);
    /// Like Accept, but raises FormatError naming the expected kind.
    Token Expect(string kind);
    int Position { get; set; }
}

// ---------------------------------------------------------------- searching

class SearchHit {
    string File { get; }
    int Line { get; }
    string Text { get; }
    string ToString();
}

class Search {
    /// Shell glob: * (within a path segment), ** (any depth), ?, [abc], [a-z], [!x].
    static bool Glob(string pattern, string path);
    /// * and ? over the whole text.
    static bool Wildcard(string pattern, string text);
    /// Subsequence fuzzy score in [0, 1] (0 = no match); consecutive and word-start matches score higher.
    static float Fuzzy(string query, string text);
    /// The best matches, highest score first (ties keep input order).
    static string[] FuzzyRank(string query, Iterable<string> candidates, int limit);
    /// Lines matching a regular expression (Line is 1-based; File is "").
    static SearchHit[] Grep(string pattern, Iterable<string> lines);
    /// Grep over many files (path → content), in map order.
    static SearchHit[] GrepFiles(string pattern, Map<string, string> files);
}
