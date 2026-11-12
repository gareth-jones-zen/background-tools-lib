using System.Text;

namespace PyCompat;

/// <summary>Python's str behaviour where it differs from .NET's: whitespace (str.isspace), strip, slicing by code
/// point rather than UTF-16 unit, splitlines, and text files read and written in text mode on Windows.</summary>
public static class PyStr
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>str.isspace for one character. Python counts U+001C to U+001F; .NET's char.IsWhiteSpace doesn't.</summary>
    public static bool IsSpace(char c) => c switch
    {
        ' ' or '\t' or '\n' or '\v' or '\f' or '\r' => true,
        '\x1c' or '\x1d' or '\x1e' or '\x1f' or '\x85' or '\xa0' => true,
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
        return text[start..end];
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
        c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';

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

    /// <summary>Universal newlines, as Python's text mode reads: "\r\n" and a lone "\r" both become "\n".</summary>
    public static string UniversalNewlines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    /// <summary>"\n" as Windows text mode writes it: "\r\n". (An existing "\r\n" becomes "\r\r\n", as in Python.)</summary>
    public static string WindowsNewlines(string text) => text.Replace("\n", "\r\n");

    /// <summary>Path.read_text(encoding="utf-8") on Windows: invalid UTF-8 is an error (DecoderFallbackException),
    /// a byte order mark is kept as U+FEFF, and newlines are universal.</summary>
    public static string ReadText(string path) => UniversalNewlines(StrictUtf8.GetString(File.ReadAllBytes(path)));

    /// <summary>Path.write_text(text, encoding="utf-8") on Windows: "\n" written as "\r\n", no byte order mark.</summary>
    public static void WriteText(string path, string text) => File.WriteAllText(path, WindowsNewlines(text), Utf8);

    /// <summary>Bytes decoded as Python's subprocess text mode with errors="replace": invalid UTF-8 becomes
    /// U+FFFD, and newlines are universal.</summary>
    public static string DecodeOutput(byte[] bytes) => UniversalNewlines(Utf8.GetString(bytes));
}
