using System.Globalization;
using System.Text;

namespace PyCompat;

/// <summary>An exception str.format raises, named as Python names it (Kind: "KeyError", "IndexError",
/// "ValueError" or "TypeError"), with Python's message: for a KeyError that's the key's repr, as str(KeyError)
/// gives.</summary>
public sealed class PyFormatException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

/// <summary>Python's <c>template.format(**values)</c>, for templates in a tool's settings (file names, titles).
/// Values are text (str), DateOnly (date), DateTime (a naive datetime), DateTimeOffset (an aware one), whole
/// numbers (int, long), bool and null (None).
/// <para>Exactly as Python: <c>{{</c> and <c>}}</c>; <c>{name}</c>; conversions <c>!s</c> and <c>!r</c> (text,
/// numbers, None); a date's or datetime's spec as strftime (<c>{date:%Y-%m}</c>, PyTime's directives, plus
/// <c>%z</c> on an aware time) and its str() with no spec (<c>2026-11-12 10:00:00+00:00</c>); text's spec
/// <c>[[fill]align][0][width][.precision][s]</c>; a whole number's <c>[[fill]align][sign][0][width][,|_][d]</c>.
/// Mistakes raise PyFormatException with Python's exception and message (a missing name is a KeyError, a
/// positional field an IndexError, a malformed template or spec a ValueError).</para>
/// <para>Not supported, so refused (NotSupportedException) rather than guessed: attribute and index lookups
/// (<c>{a.b}</c>, <c>{a[0]}</c>), nested fields in a spec, <c>!a</c>, <c>!r</c> of a date, other number formats,
/// and strftime directives PyTime doesn't have. <see cref="Problem"/> finds these when the settings load.</para></summary>
public static class PyFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>template.format(**values).</summary>
    public static string Format(string template, IReadOnlyDictionary<string, object?> values)
    {
        var b = new StringBuilder();
        foreach (var part in Parse(template))
        {
            if (part.Field is null)
            {
                b.Append(part.Literal);
                continue;
            }
            var name = FieldName(part.Field);
            if (!values.TryGetValue(name, out var value))
                throw new PyFormatException("KeyError", Py.ReprString(name));
            b.Append(FormatValue(value, part.Conversion, part.Spec));
        }
        return b.ToString();
    }

    /// <summary>The names a template uses, in order. Throws PyFormatException for a malformed template, and
    /// NotSupportedException for a field this port doesn't support.</summary>
    public static List<string> FieldNames(string template) =>
        Parse(template).Where(p => p.Field is not null).Select(p => FieldName(p.Field!)).ToList();

    /// <summary>Why this port can't format the template as Python would (an unsupported field or spec), or null.
    /// A template Python itself would fail on isn't a problem here: formatting it raises Python's error. Specs
    /// are checked for text, and for dates where dateFields names the fields holding a date or datetime.</summary>
    public static string? Problem(string template, IEnumerable<string>? dateFields = null)
    {
        var dates = dateFields?.ToHashSet() ?? [];
        List<Part> parts;
        try
        {
            parts = Parse(template);
        }
        catch (PyFormatException)
        {
            return null;
        }
        catch (NotSupportedException error)
        {
            return error.Message;
        }
        foreach (var part in parts.Where(p => p.Field is not null))
        {
            string name;
            try
            {
                name = FieldName(part.Field!);
            }
            catch (PyFormatException)
            {
                continue;
            }
            catch (NotSupportedException error)
            {
                return error.Message;
            }
            if (part.Conversion is 'a')
                return $"{{{part.Field}}}: the !a conversion isn't supported.";
            if (dates.Contains(name))
            {
                if (part.Conversion is 'r')
                    return $"{{{part.Field}}}: !r of a date isn't supported.";
                if (part.Conversion is null && part.Spec.Length > 0 && UnsupportedDirective(part.Spec) is { } d)
                    return $"{{{part.Field}:{part.Spec}}}: the strftime code %{d} isn't supported (only %{string.Join(" %", PyTime.Directives.ToCharArray())} and %z).";
            }
        }
        return null;
    }

    private sealed record Part(string Literal, string? Field, char? Conversion, string Spec);

    /// <summary>string.Formatter's parse (CPython's MarkupIterator), with Python's messages.</summary>
    private static List<Part> Parse(string template)
    {
        var parts = new List<Part>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal.Append('}');
                    i += 2;
                    continue;
                }
                throw new PyFormatException("ValueError", "Single '}' encountered in format string");
            }
            if (c != '{')
            {
                literal.Append(c);
                i++;
                continue;
            }
            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                literal.Append('{');
                i += 2;
                continue;
            }
            if (i + 1 == template.Length)
                throw new PyFormatException("ValueError", "Single '{' encountered in format string");
            // The field runs to the matching '}', counting nested braces.
            var depth = 1;
            var j = i + 1;
            while (j < template.Length)
            {
                if (template[j] == '{')
                    depth++;
                else if (template[j] == '}' && --depth == 0)
                    break;
                j++;
            }
            if (j >= template.Length)
                throw new PyFormatException("ValueError", "expected '}' before end of string");
            if (literal.Length > 0)
            {
                parts.Add(new Part(literal.ToString(), null, null, ""));
                literal.Clear();
            }
            parts.Add(Field(template[(i + 1)..j]));
            i = j + 1;
        }
        if (literal.Length > 0)
            parts.Add(new Part(literal.ToString(), null, null, ""));
        return parts;
    }

    /// <summary>One field's text: name, then !conversion, then :spec (CPython's parse_field).</summary>
    private static Part Field(string text)
    {
        var k = 0;
        var end = '\0';
        while (k < text.Length)
        {
            var c = text[k++];
            if (c == '[')
            {
                while (k < text.Length && text[k] != ']')
                    k++;
                continue;
            }
            if (c == '{')
                throw new PyFormatException("ValueError", "unexpected '{' in field name");
            if (c is ':' or '!')
            {
                end = c;
                break;
            }
        }
        var name = end == '\0' ? text : text[..(k - 1)];
        char? conversion = null;
        var spec = "";
        if (end == '!')
        {
            if (k >= text.Length)
                throw new PyFormatException("ValueError", "unmatched '{' in format spec");
            conversion = text[k++];
            if (k < text.Length)
            {
                if (text[k] != ':')
                    throw new PyFormatException("ValueError", "expected ':' after conversion specifier");
                spec = text[(k + 1)..];
            }
            if (conversion is not ('s' or 'r' or 'a'))
                throw new PyFormatException("ValueError", $"Unknown conversion specifier {conversion}");
        }
        else if (end == ':')
            spec = text[k..];
        if (spec.Contains('{'))
            throw new NotSupportedException($"{{{name}:{spec}}}: a field inside a format spec isn't supported.");
        return new Part("", name, conversion, spec);
    }

    private static string FieldName(string field)
    {
        if (field.Length == 0 || char.IsAsciiDigit(field[0]))
        {
            var digits = field.TakeWhile(char.IsAsciiDigit).Count();
            if (digits == field.Length)
            {
                var index = field.Length == 0 ? "0" : field.TrimStart('0') is { Length: > 0 } t ? t : "0";
                throw new PyFormatException("IndexError", $"Replacement index {index} out of range for positional args tuple");
            }
        }
        if (field.IndexOfAny(['.', '[']) >= 0)
            throw new NotSupportedException($"{{{field}}}: attribute and index lookups aren't supported; use a plain {{name}}.");
        return field;
    }

    private static string FormatValue(object? value, char? conversion, string spec)
    {
        if (conversion is 'a')
            throw new NotSupportedException("the !a conversion isn't supported");
        if (conversion is 's')
            value = Str(value);
        else if (conversion is 'r')
            value = Repr(value);
        return value switch
        {
            null => spec.Length == 0 ? "None" : throw new PyFormatException("TypeError", "unsupported format string passed to NoneType.__format__"),
            string s => FormatText(s, spec),
            bool flag => spec.Length == 0 ? (flag ? "True" : "False") : FormatInt(flag ? 1 : 0, spec),
            int n => FormatInt(n, spec),
            long n => FormatInt(n, spec),
            DateOnly d => spec.Length == 0 ? PyTime.Iso(d) : Strftime(d.ToDateTime(TimeOnly.MinValue), null, spec),
            DateTime t => spec.Length == 0 ? Str(t) : Strftime(t, null, spec),
            DateTimeOffset t => spec.Length == 0 ? Str(t) : Strftime(t.DateTime, t.Offset, spec),
            _ => throw new NotSupportedException($"formatting a {value.GetType().Name} isn't supported"),
        };
    }

    /// <summary>Python's str() of a supported value.</summary>
    public static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool flag => flag ? "True" : "False",
        int n => n.ToString(Inv),
        long n => n.ToString(Inv),
        DateOnly d => PyTime.Iso(d),
        DateTime t => PyTime.Iso(t)[..10] + " " + PyTime.Iso(t)[11..],
        DateTimeOffset t => PyTime.IsoFormat(t)[..10] + " " + PyTime.IsoFormat(t)[11..],
        _ => throw new NotSupportedException($"str() of a {value.GetType().Name} isn't supported"),
    };

    private static string Repr(object? value) => value switch
    {
        string s => Py.ReprString(s),
        null or bool or int or long => Str(value),
        _ => throw new NotSupportedException($"repr() of a {value.GetType().Name} isn't supported"),
    };

    // ---- strftime, as datetime.__format__ on Windows ----

    private const string WindowsDirectives = "aAbBcdHIjmMpSUwWxXyYzZ%";

    private static char? UnsupportedDirective(string spec)
    {
        for (var k = 0; k < spec.Length; k++)
        {
            if (spec[k] != '%')
                continue;
            if (k + 1 >= spec.Length)
                return null;   // Python's ValueError, raised when formatting
            var d = spec[++k];
            if (!PyTime.Directives.Contains(d) && d != 'z' && WindowsDirectives.Contains(d))
                return d;
        }
        return null;
    }

    private static string Strftime(DateTime t, TimeSpan? offset, string spec)
    {
        var b = new StringBuilder();
        for (var k = 0; k < spec.Length; k++)
        {
            var c = spec[k];
            if (c != '%')
            {
                b.Append(c);
                continue;
            }
            if (k + 1 >= spec.Length)
                throw new PyFormatException("ValueError", "Invalid format string");
            var d = spec[++k];
            if (d == 'z')
            {
                if (offset is { } o)
                {
                    var sign = o < TimeSpan.Zero ? '-' : '+';
                    o = o.Duration();
                    b.Append(sign).Append(o.Hours.ToString("00", Inv)).Append(o.Minutes.ToString("00", Inv));
                    if (o.Seconds != 0)
                        b.Append(o.Seconds.ToString("00", Inv));
                }
                continue;
            }
            if (PyTime.Directives.Contains(d))
            {
                b.Append(PyTime.Strftime(t, "%" + d));
                continue;
            }
            if (WindowsDirectives.Contains(d))
                throw new NotSupportedException($"the strftime code %{d} isn't supported");
            throw new PyFormatException("ValueError", "Invalid format string");
        }
        return b.ToString();
    }

    // ---- the format spec mini-language ----

    private sealed record Spec(string Fill, char? Align, char? Sign, bool Zero, int? Width, char? Grouping, int? Precision, char? Type, bool Alternate, bool NoNegativeZero);

    /// <summary>CPython's parse_internal_render_format_spec (the parts that apply to str and int).</summary>
    private static Spec ParseSpec(string spec, string typeName, char defaultType)
    {
        var runes = new List<string>();
        for (var k = 0; k < spec.Length;)
        {
            var (_, w) = PyUnicode.At(spec, k);
            runes.Add(spec.Substring(k, w));
            k += w;
        }
        var p = 0;
        string? fill = null;
        char? align = null;
        static bool IsAlign(string s) => s is "<" or ">" or "=" or "^";
        if (runes.Count >= 2 && IsAlign(runes[1]))
        {
            fill = runes[0];
            align = runes[1][0];
            p = 2;
        }
        else if (runes.Count >= 1 && IsAlign(runes[0]))
        {
            align = runes[0][0];
            p = 1;
        }
        char? sign = null;
        if (p < runes.Count && runes[p] is "+" or "-" or " ")
            sign = runes[p++][0];
        var noNegativeZero = false;
        if (p < runes.Count && runes[p] == "z")
        {
            noNegativeZero = true;
            p++;
        }
        var alternate = false;
        if (p < runes.Count && runes[p] == "#")
        {
            alternate = true;
            p++;
        }
        var zero = false;
        if (fill is null && p < runes.Count && runes[p] == "0")
        {
            zero = true;
            fill = "0";   // and for a number with no alignment given, '=' (FormatInt)
            p++;
        }
        int? width = Number(runes, ref p);
        char? grouping = null;
        if (p < runes.Count && runes[p] is "," or "_")
        {
            grouping = runes[p++][0];
            if (p < runes.Count && runes[p] is "," or "_")
                throw new PyFormatException("ValueError", "Cannot specify both ',' and '_'.");
        }
        int? precision = null;
        if (p < runes.Count && runes[p] == ".")
        {
            p++;
            precision = Number(runes, ref p) ?? throw new PyFormatException("ValueError", "Format specifier missing precision");
        }
        if (runes.Count - p > 1)
            throw new PyFormatException("ValueError", $"Invalid format specifier {Py.ReprString(spec)} for object of type {Py.ReprString(typeName)}");
        var type = runes.Count - p == 1 ? runes[p][0] : defaultType;
        if (runes.Count - p == 1 && runes[p].Length > 1)
            throw new PyFormatException("ValueError", $"Unknown format code '\\x{char.ConvertToUtf32(runes[p], 0):x}' for object of type {Py.ReprString(typeName)}");
        if (grouping is { } g && type is not ('d' or 'e' or 'f' or 'g' or 'E' or 'G' or '%' or 'F' or '\0'))
            throw new PyFormatException("ValueError", $"Cannot specify '{g}' with '{type}'.");
        return new Spec(fill ?? " ", align, sign, zero, width, grouping, precision, type, alternate, noNegativeZero);
    }

    private static int? Number(List<string> runes, ref int p)
    {
        var start = p;
        while (p < runes.Count && runes[p].Length == 1 && char.IsAsciiDigit(runes[p][0]))
            p++;
        if (p == start)
            return null;
        var digits = string.Concat(runes.GetRange(start, p - start));
        if (!int.TryParse(digits, NumberStyles.None, Inv, out var value))
            throw new PyFormatException("ValueError", "Too many decimal digits in format string");
        return value;
    }

    private static string FormatText(string text, string spec)
    {
        if (spec.Length == 0)
            return text;
        var s = ParseSpec(spec, "str", 's');
        if (s.Type != 's')
            throw new PyFormatException("ValueError", $"Unknown format code '{s.Type}' for object of type 'str'");
        if (s.Sign is not null)
            throw new PyFormatException("ValueError", "Sign not allowed in string format specifier");
        if (s.NoNegativeZero)
            throw new PyFormatException("ValueError", "Negative zero coercion (z) not allowed in string format specifier");
        if (s.Alternate)
            throw new PyFormatException("ValueError", "Alternate form (#) not allowed in string format specifier");
        var align = s.Align ?? '<';
        if (align == '=')
            throw new PyFormatException("ValueError", "'=' alignment not allowed in string format specifier");
        if (s.Precision is { } precision)
            text = PyStr.Head(text, precision);
        return Pad(text, s.Fill, align, s.Width);
    }

    private static string FormatInt(long n, string spec)
    {
        if (spec.Length == 0)
            return n.ToString(Inv);
        var s = ParseSpec(spec, "int", 'd');
        if (s.Type is not 'd')
            throw new NotSupportedException($"the integer format code '{s.Type}' isn't supported");
        if (s.Precision is not null)
            throw new PyFormatException("ValueError", "Precision not allowed in integer format specifier");
        if (s.Alternate || s.NoNegativeZero)
            throw new NotSupportedException("'#' and 'z' aren't supported for integers");
        var digits = Math.Abs((decimal)n).ToString(Inv);
        if (s.Grouping is { } g)
            digits = Group(digits, g);
        var sign = n < 0 ? "-" : s.Sign is '+' ? "+" : s.Sign is ' ' ? " " : "";
        var align = s.Align ?? (s.Zero ? '=' : '>');
        if (align == '=')
        {
            if (s.Zero && s.Grouping is not null)
                throw new NotSupportedException("zero padding with a thousands separator isn't supported");
            var pad = (s.Width ?? 0) - sign.Length - digits.Length;
            return sign + (pad > 0 ? string.Concat(Enumerable.Repeat(s.Fill, pad)) : "") + digits;
        }
        return Pad(sign + digits, s.Fill, align, s.Width);
    }

    private static string Group(string digits, char separator)
    {
        var b = new StringBuilder();
        for (var k = 0; k < digits.Length; k++)
        {
            if (k > 0 && (digits.Length - k) % 3 == 0)
                b.Append(separator);
            b.Append(digits[k]);
        }
        return b.ToString();
    }

    private static string Pad(string text, string fill, char align, int? width)
    {
        var length = PyStr.Length(text);
        if (width is not { } w || w <= length)
            return text;
        var pad = w - length;
        string Fill(int count) => string.Concat(Enumerable.Repeat(fill, count));
        return align switch
        {
            '>' => Fill(pad) + text,
            '^' => Fill(pad / 2) + text + Fill(pad - pad / 2),
            _ => text + Fill(pad),
        };
    }
}
