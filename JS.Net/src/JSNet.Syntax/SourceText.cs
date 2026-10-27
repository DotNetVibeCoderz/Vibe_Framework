namespace JSNet.Syntax;

/// <summary>Immutable source file with a precomputed line map for O(log n) position lookups.</summary>
public sealed class SourceText
{
    private readonly int[] _lineStarts;

    public SourceText(string text, string path = "<memory>")
    {
        Text = text ?? string.Empty;
        Path = path;
        _lineStarts = ComputeLineStarts(Text);
    }

    public string Text { get; }
    public string Path { get; }
    public int Length => Text.Length;
    public int LineCount => _lineStarts.Length;

    public char this[int index] => index < Text.Length ? Text[index] : '\0';

    public static SourceText FromFile(string path) => new(File.ReadAllText(path), System.IO.Path.GetFullPath(path));

    /// <summary>Returns the 1-based line and column of an offset.</summary>
    public (int Line, int Column) GetLineColumn(int offset)
    {
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return (lo + 1, offset - _lineStarts[lo] + 1);
    }

    public int GetOffset(int line, int column)
    {
        if (line < 1) return 0;
        if (line > _lineStarts.Length) return Text.Length;
        return Math.Min(Text.Length, _lineStarts[line - 1] + Math.Max(0, column - 1));
    }

    public string GetLine(int line)
    {
        if (line < 1 || line > _lineStarts.Length) return string.Empty;
        int start = _lineStarts[line - 1];
        int end = line < _lineStarts.Length ? _lineStarts[line] : Text.Length;
        return Text[start..end].TrimEnd('\r', '\n');
    }

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int>(text.Length / 32 + 1) { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }
}

/// <summary>A half-open range [Start, Start+Length) in a source file.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
    public static TextSpan FromBounds(int start, int end) => new(start, Math.Max(0, end - start));
}
