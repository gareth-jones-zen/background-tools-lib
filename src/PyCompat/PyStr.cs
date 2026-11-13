using System.Globalization;
using System.Text;

namespace PyCompat;

/// <summary>Python's str behaviour where it differs from .NET's: whitespace (str.isspace), strip and split, slicing
/// and len by code point rather than UTF-16 unit, isdigit and int, lower, expandtabs, ordering by code point,
/// splitlines, and text files read and written in text mode on Windows.</summary>
public static class PyStr
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>A Regex character-class body matching what Python's \s matches: use as [\s\x1c-\x1f]. (.NET's \s is
    /// char.IsWhiteSpace, which leaves out U+001C to U+001F.)</summary>
    public const string SpaceClass = @"\s\x1c-\x1f";

    /// <summary>A .NET Regex pattern for one character Python's \s matches (str.isspace): [\s\x1c-\x1f]. For a tool's
    /// own fixed patterns; patterns written in Python's dialect (settings) go through PyRegex.</summary>
    public const string Space = "[" + SpaceClass + "]";

    /// <summary>A .NET Regex pattern for one UTF-16 unit that isn't Python's whitespace (Python's \S). An emoji is two
    /// units, so "\S+" still takes it whole, but a lone "\S" takes half of it: use PyRegex where that matters.</summary>
    public const string NotSpace = "[^" + SpaceClass + "]";

    /// <summary>str.isspace for one character. Python counts U+001C to U+001F; .NET's char.IsWhiteSpace doesn't.
    /// Checked against every BMP character Python 3.14 accepts (tests/PyCompat.Tests/Generated). A surrogate (half
    /// of a character beyond U+FFFF) is never space, as no such character is.</summary>
    public static bool IsSpace(char c) => c switch
    {
        ' ' or '\t' or '\n' or '\v' or '\f' or '\r' => true,
        '\u001c' or '\u001d' or '\u001e' or '\u001f' or '\u0085' or '\u00a0' => true,
        '\u1680' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' => true,
        >= '\u2000' and <= '\u200a' => true,
        _ => false,
    };

    /// <summary>str.strip() with no argument.</summary>
    public static string Strip(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsSpace(text[start]))
            start++;
        while (end > start && IsSpace(text[end - 1]))
            end--;
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    /// <summary>str.rstrip() with no argument.</summary>
    public static string RStrip(string text)
    {
        var end = text.Length;
        while (end > 0 && IsSpace(text[end - 1]))
            end--;
        return text[..end];
    }

    /// <summary>str.lstrip() with no argument.</summary>
    public static string LStrip(string text)
    {
        var start = 0;
        while (start < text.Length && IsSpace(text[start]))
            start++;
        return text[start..];
    }

    /// <summary>str.rstrip(chars), for chars of the Basic Multilingual Plane.</summary>
    public static string RStrip(string text, string chars)
    {
        var end = text.Length;
        while (end > 0 && chars.Contains(text[end - 1]))
            end--;
        return text[..end];
    }

    /// <summary>str.lstrip(chars), for chars of the Basic Multilingual Plane.</summary>
    public static string LStrip(string text, string chars)
    {
        var start = 0;
        while (start < text.Length && chars.Contains(text[start]))
            start++;
        return text[start..];
    }

    /// <summary>str.split() with no argument: runs of whitespace separate, and there are no empty parts.</summary>
    public static List<string> Split(string text)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && IsSpace(text[i]))
                i++;
            var start = i;
            while (i < text.Length && !IsSpace(text[i]))
                i++;
            if (i > start)
                parts.Add(text[start..i]);
        }
        return parts;
    }

    /// <summary>" ".join(text.split()): the text with its whitespace collapsed.</summary>
    public static string Collapse(string text) => string.Join(' ', Split(text));

    /// <summary>str.isdigit(): not empty, and every code point a Unicode Digit or Decimal (so a superscript two is a
    /// digit, although int() of it fails).</summary>
    public static bool IsDigit(string text)
    {
        if (text.Length == 0)
            return false;
        foreach (var rune in text.EnumerateRunes())
            if (CharUnicodeInfo.GetDigitValue(rune.ToString(), 0) < 0)
                return false;
        return true;
    }

    /// <summary>int(text): decimal digits of any script, an optional sign, single underscores between digits, and
    /// surrounding whitespace, as Python's int() accepts. Anything else is a FormatException (Python's ValueError);
    /// a value too big for a long is an OverflowException.</summary>
    public static long Int(string text)
    {
        var t = Strip(text);
        var negative = false;
        if (t.StartsWith('+') || t.StartsWith('-'))
        {
            negative = t[0] == '-';
            t = t[1..];
        }
        if (t.Length == 0)
            throw new FormatException($"invalid literal for int() with base 10: {Py.ReprString(text)}");
        long value = 0;
        var previousUnderscore = true;
        foreach (var rune in t.EnumerateRunes())
        {
            if (rune.Value == '_' && !previousUnderscore)
            {
                previousUnderscore = true;
                continue;
            }
            var digit = CharUnicodeInfo.GetDecimalDigitValue(rune.ToString(), 0);
            if (digit < 0)
                throw new FormatException($"invalid literal for int() with base 10: {Py.ReprString(text)}");
            value = checked(value * 10 + digit);
            previousUnderscore = false;
        }
        if (previousUnderscore)
            throw new FormatException($"invalid literal for int() with base 10: {Py.ReprString(text)}");
        return negative ? -value : value;
    }

    /// <summary>str.lower(): Python's full lower case (so U+0130, capital I with dot, becomes "i" and a combining
    /// dot) with its final-sigma rule, from Python's own Unicode tables (PyUnicode.g.cs), so it agrees with Python
    /// whatever Unicode version .NET has. A lone surrogate is kept as it is, as in Python. Moved here from
    /// EdgeDevTools' EdgeProfile (PythonLower) on 13 Nov 2026; table-driven since v0.3.</summary>
    public static string Lower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var i = 0;
        while (i < text.Length && text[i] < 0x80 && !char.IsAsciiLetterUpper(text[i]))
            i++;
        if (i == text.Length)
            return text;
        var result = new StringBuilder(text.Length);
        result.Append(text, 0, i);
        while (i < text.Length)
        {
            var (cp, width) = PyUnicode.At(text, i);
            if (cp == 0x03A3)
                result.Append(FinalSigma(text, i) ? 'ς' : 'σ');
            else if (cp < 0x80)
                result.Append(char.ToLowerInvariant((char)cp));
            else if (PyUnicode.Map(PyUnicode.LowerFrom, PyUnicode.LowerTo, cp) is { } lower)
                result.Append(lower);
            else
                result.Append(text, i, width);
            i += width;
        }
        return result.ToString();
    }

    /// <summary>str.casefold(): Python's full case folding (so "ß" becomes "ss" and a final sigma "σ"), from Python's
    /// tables. For comparing text without regard to case, as Python code that calls casefold() does.</summary>
    public static string CaseFold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var (cp, width) = PyUnicode.At(text, i);
            if (cp < 0x80)
                result.Append(char.ToLowerInvariant((char)cp));
            else if (PyUnicode.Map(PyUnicode.CaseFoldFrom, PyUnicode.CaseFoldTo, cp) is { } folded)
                result.Append(folded);
            else
                result.Append(text, i, width);
            i += width;
        }
        return result.ToString();
    }

    // Python's handle_capital_sigma: a final sigma if a cased character comes before (skipping case-ignorable
    // ones) and none comes after.
    private static bool FinalSigma(string text, int at)
    {
        var j = at;
        while (j > 0)
        {
            var (cp, width) = Before(text, j);
            j -= width;
            if (PyUnicode.IsCaseIgnorable(cp))
                continue;
            if (!PyUnicode.IsCased(cp))
                return false;
            j = -1;
            break;
        }
        if (j == 0)
            return false;   // nothing cased before it
        for (var k = at + 1; k < text.Length;)
        {
            var (cp, width) = PyUnicode.At(text, k);
            k += width;
            if (!PyUnicode.IsCaseIgnorable(cp))
                return !PyUnicode.IsCased(cp);
        }
        return true;
    }

    private static (int CodePoint, int Width) Before(string text, int end)
    {
        var c = text[end - 1];
        if (char.IsLowSurrogate(c) && end >= 2 && char.IsHighSurrogate(text[end - 2]))
            return (char.ConvertToUtf32(text[end - 2], c), 2);
        return (c, 1);
    }

    /// <summary>str.expandtabs(tabsize): a tab moves to the next multiple of tabsize; "\n" and "\r" start a new
    /// column. Columns count code points, as Python's do.</summary>
    public static string ExpandTabs(string text, int tabSize = 8)
    {
        var result = new StringBuilder();
        var column = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\t')
            {
                if (tabSize > 0)
                {
                    var spaces = tabSize - column % tabSize;
                    result.Append(' ', spaces);
                    column += spaces;
                }
            }
            else
            {
                result.Append(c);
                if (c is '\n' or '\r')
                    column = 0;
                else if (!(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(text[i - 1])))
                    column++;
            }
        }
        return result.ToString();
    }

    /// <summary>Strings in Python's order: by code point. Ordinal order differs only where a surrogate pair (an
    /// emoji) meets a character from U+E000 to U+FFFF.</summary>
    public static readonly IComparer<string> Order = Comparer<string>.Create(CompareCodePoints);

    /// <summary>Python's comparison of two strings: by code point.</summary>
    public static int CompareCodePoints(string? a, string? b)
    {
        if (ReferenceEquals(a, b))
            return 0;
        if (a is null)
            return -1;
        if (b is null)
            return 1;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x == y)
                continue;
            // A surrogate stands for a code point above U+FFFF, so it sorts after every other BMP character.
            var sx = char.IsSurrogate(x);
            var sy = char.IsSurrogate(y);
            if (sx != sy)
                return sx ? 1 : -1;
            return x.CompareTo(y);
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>The number of code points (Python's len()).</summary>
    public static int Length(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            n++;
        }
        return n;
    }

    /// <summary>text[:count], counted in code points, so an emoji is never cut in half.</summary>
    public static string Head(string text, int count)
    {
        if (count <= 0)
            return "";
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (n == count)
                return text[..i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            n++;
        }
        return text;
    }

    /// <summary>text[-count:], counted in code points.</summary>
    public static string Tail(string text, int count)
    {
        if (count <= 0)
            return text;   // Python's text[-0:] is the whole string
        var n = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1]))
                i--;
            n++;
            if (n == count)
                return text[i..];
        }
        return text;
    }

    /// <summary>Whether a character ends a line for str.splitlines().</summary>
    public static bool IsLineBreak(char c) =>
        c is '\n' or '\r' or '\v' or '\f' or '\u001c' or '\u001d' or '\u001e' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>str.splitlines(): every line boundary Python recognises, "\r\n" as one, no trailing empty line.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!IsLineBreak(c))
                continue;
            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    /// <summary>str.splitlines(keepends=True): each line with its ending.</summary>
    public static List<string> SplitLinesKeepEnds(string text) => Py.SplitLinesKeepEnds(text);

    /// <summary>Universal newlines, as Python's text mode reads: "\r\n" and a lone "\r" both become "\n".</summary>
    public static string UniversalNewlines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    /// <summary>"\n" as Windows text mode writes it: "\r\n". (An existing "\r\n" becomes "\r\r\n", as in Python.)</summary>
    public static string WindowsNewlines(string text) => text.Replace("\n", "\r\n");

    /// <summary>Path.read_text(encoding="utf-8") on Windows: a byte order mark is kept as U+FEFF, and newlines are
    /// universal. Invalid UTF-8 throws DecoderFallbackException with Python's UnicodeDecodeError message (since v0.3),
    /// such as "'utf-8' codec can't decode byte 0xff in position 3: invalid start byte" (PyUtf8).</summary>
    public static string ReadText(string path) => UniversalNewlines(PyUtf8.Decode(File.ReadAllBytes(path)));

    /// <summary>Path.write_text(text, encoding="utf-8") on Windows: "\n" written as "\r\n", no byte order mark.</summary>
    public static void WriteText(string path, string text) => File.WriteAllText(path, WindowsNewlines(text), Utf8);

    /// <summary>Bytes decoded as Python's subprocess text mode with errors="replace": invalid UTF-8 becomes
    /// U+FFFD, and newlines are universal.</summary>
    public static string DecodeOutput(byte[] bytes) => UniversalNewlines(Utf8.GetString(bytes));
}
