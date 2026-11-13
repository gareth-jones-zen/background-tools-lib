using System.Text;

namespace PyCompat;

/// <summary>Python's html module: escape, and unescape ported exactly from CPython's html/__init__.py. Unescape
/// knows every HTML5 named reference (with or without the ";" where HTML5 allows it, and the longest-prefix rule for
/// the rest), and numeric references with the HTML5 rules for invalid ones: the Windows-1252 fix-ups for 0x80 to
/// 0x9F, U+FFFD for NUL, surrogates and anything past U+10FFFF, and nothing for the code points HTML forbids.
/// WebUtility.HtmlDecode knows only HTML 4's names and none of these rules.
///
/// The names are generated from Python's own html.entities.html5 (tools/gen/gen_tables.py), so they can't drift.</summary>
public static class PyHtml
{
    private static readonly Dictionary<string, string> Table = BuildTable();

    private static Dictionary<string, string> BuildTable()
    {
        var pairs = PyHtmlEntities.Pairs;
        var table = new Dictionary<string, string>(pairs.Length / 2, StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
            table[pairs[i]] = pairs[i + 1];
        return table;
    }

    private static readonly Dictionary<int, string> InvalidCharrefs = new()
    {
        [0x00] = "\ufffd", [0x0d] = "\r", [0x80] = "\u20ac", [0x81] = "\u0081", [0x82] = "\u201a", [0x83] = "\u0192",
        [0x84] = "\u201e", [0x85] = "\u2026", [0x86] = "\u2020", [0x87] = "\u2021", [0x88] = "\u02c6", [0x89] = "\u2030",
        [0x8a] = "\u0160", [0x8b] = "\u2039", [0x8c] = "\u0152", [0x8d] = "\u008d", [0x8e] = "\u017d", [0x8f] = "\u008f",
        [0x90] = "\u0090", [0x91] = "\u2018", [0x92] = "\u2019", [0x93] = "\u201c", [0x94] = "\u201d", [0x95] = "\u2022",
        [0x96] = "\u2013", [0x97] = "\u2014", [0x98] = "\u02dc", [0x99] = "\u2122", [0x9a] = "\u0161", [0x9b] = "\u203a",
        [0x9c] = "\u0153", [0x9d] = "\u009d", [0x9e] = "\u017e", [0x9f] = "\u0178",
    };

    private static bool InvalidCodepoint(long n) =>
        n is >= 0x1 and <= 0x8 or 0xb or >= 0xe and <= 0x1f or >= 0x7f and <= 0x9f or >= 0xfdd0 and <= 0xfdef
        || (n <= 0x10ffff && (n & 0xfffe) == 0xfffe);

    /// <summary>The number of named references Python knows (2,231 in Python 3.14).</summary>
    public static int NamedCount => Table.Count;

    /// <summary>The Python version the names were generated from.</summary>
    public static string PythonVersion => PyHtmlEntities.PythonVersion;

    /// <summary>Whether a name (without the "&amp;", with any ";") is in html.entities.html5.</summary>
    public static bool IsEntity(string name) => Table.ContainsKey(name);

    /// <summary>html.escape(text, quote): "&amp;", "&lt;" and "&gt;", and with quote (Python's default) also
    /// "&quot;" and "&#x27;". Nothing else, so "£" stays "£" (WebUtility.HtmlEncode would make it a numeric
    /// reference).</summary>
    public static string Escape(string text, bool quote = true)
    {
        text = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return quote ? text.Replace("\"", "&quot;").Replace("'", "&#x27;") : text;
    }

    /// <summary>html.unescape(text).</summary>
    public static string Unescape(string text)
    {
        if (!text.Contains('&'))
            return text;
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var amp = text.IndexOf('&', i);
            if (amp < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }
            result.Append(text, i, amp - i);
            var end = MatchCharref(text, amp + 1);
            if (end < 0)
            {
                result.Append('&');
                i = amp + 1;
                continue;
            }
            result.Append(Replace(text[(amp + 1)..end]));
            i = end;
        }
        return result.ToString();
    }

    /// <summary>The end of the _charref match at start (just after the "&amp;"), or -1:
    /// #[0-9]+;? | #[xX][0-9a-fA-F]+;? | [^\t\n\f &lt;&amp;#;]{1,32};? (32 code points, as Python's re counts,
    /// not UTF-16 units).</summary>
    private static int MatchCharref(string s, int start)
    {
        var i = start;
        if (i < s.Length && s[i] == '#')
        {
            i++;
            if (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                    i++;
                return i < s.Length && s[i] == ';' ? i + 1 : i;
            }
            if (i + 1 < s.Length && s[i] is 'x' or 'X' && char.IsAsciiHexDigit(s[i + 1]))
            {
                i++;
                while (i < s.Length && char.IsAsciiHexDigit(s[i]))
                    i++;
                return i < s.Length && s[i] == ';' ? i + 1 : i;
            }
            return -1;   // "#" is excluded from the named form, so nothing else can match here
        }
        var count = 0;
        while (i < s.Length && count < 32 && s[i] is not ('\t' or '\n' or '\f' or ' ' or '<' or '&' or '#' or ';'))
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
            i++;
            count++;
        }
        if (count == 0)
            return -1;
        return i < s.Length && s[i] == ';' ? i + 1 : i;
    }

    /// <summary>_replace_charref, for the text after the "&amp;".</summary>
    private static string Replace(string s)
    {
        if (s[0] == '#')
        {
            var hex = s[1] is 'x' or 'X';
            var digits = (hex ? s[2..] : s[1..]).TrimEnd(';');
            var num = Number(digits, hex ? 16 : 10);
            if (num <= 0x9f && InvalidCharrefs.TryGetValue((int)num, out var mapped))
                return mapped;
            if (num is >= 0xD800 and <= 0xDFFF || num > 0x10FFFF)
                return "\ufffd";
            if (InvalidCodepoint(num))
                return "";
            return char.ConvertFromUtf32((int)num);
        }
        if (Table.TryGetValue(s, out var value))
            return value;
        // The longest name that fits, as HTML5 defines it: s[:x] for x from len(s)-1 down to 2, in code points.
        var points = CodePointStarts(s);
        for (var x = points.Count - 2; x > 1; x--)
        {
            var cut = points[x];
            if (Table.TryGetValue(s[..cut], out var prefix))
                return prefix + s[cut..];
        }
        return "&" + s;
    }

    /// <summary>Python's int(digits, base), capped: anything past U+10FFFF is as good as infinite here.</summary>
    private static long Number(string digits, int radix)
    {
        long value = 0;
        foreach (var c in digits)
        {
            var d = c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
            value = value * radix + d;
            if (value > 0x10FFFF)
                return 0x110000;
        }
        return value;
    }

    /// <summary>The UTF-16 index where each code point starts, plus the length at the end.</summary>
    private static List<int> CodePointStarts(string s)
    {
        var starts = new List<int>(s.Length + 1);
        for (var i = 0; i < s.Length; i++)
        {
            starts.Add(i);
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
        }
        starts.Add(s.Length);
        return starts;
    }
}
