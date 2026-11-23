namespace Ghostrig.CScript.Text;

/// <summary>A half-open character range <c>[Start, Start + Length)</c> in a source file.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    public static TextSpan Cover(TextSpan a, TextSpan b) => FromBounds(Math.Min(a.Start, b.Start), Math.Max(a.End, b.End));

    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>1-based line and column.</summary>
public readonly record struct LinePosition(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>Immutable source file contents with line mapping for diagnostics.</summary>
public sealed class SourceText
{
    private readonly int[] _lineStarts;

    public SourceText(string text, string path = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);
        Text = text;
        Path = path;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = [.. starts];
    }

    public string Text { get; }
    public string Path { get; }
    public int Length => Text.Length;
    public char this[int index] => Text[index];

    public string ToString(TextSpan span) => Text.Substring(span.Start, span.Length);

    public LinePosition GetLinePosition(int position)
    {
        var index = Array.BinarySearch(_lineStarts, position);
        var line = index >= 0 ? index : ~index - 1;
        return new LinePosition(line + 1, position - _lineStarts[line] + 1);
    }
}
