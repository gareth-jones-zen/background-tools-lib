using System.Text;
using System.Text.RegularExpressions;

namespace CalendarDays;

/// <summary>How the half days are marked on a report's leave entry: <c>{title}</c> is the entry's title
/// ("Dan Clark annual leave"). calendar-team-leave-sync's [titles]; these defaults are what it writes.</summary>
public sealed record ReportLeaveFormat(string Am = "{title} (AM)", string Pm = "{title} (PM)", string FromPm = "{title} (from PM)",
    string ToAm = "{title} (to AM)");

/// <summary>A report's leave entry read from its title: the name, the kind ("annual leave" or "on leave", as written),
/// and the halves: <see cref="Each"/> for every day ("AM", "PM", "half": a half day of unknown half), <see cref="First"/>
/// for the first day ("from PM") and <see cref="Last"/> for the last ("to AM"); null where the title says nothing.</summary>
public sealed record ReportLeaveTitle(string Name, string Kind, string? Each, string? First, string? Last);

/// <summary>The one definition of your reports' leave entries (22 Nov 2026): calendar-team-leave-sync writes them
/// (<see cref="Format"/>), mail-triage reads them for the 1:1 rules (<see cref="Parse"/>). The titles are
/// "&lt;Full Name&gt; annual leave" (or "... on leave"), with the halves marked "(AM)" / "(PM)" on a single day,
/// "(from PM)" when a longer absence starts in the afternoon and "(to AM)" when it ends after the morning. Hand-made
/// entries are read with or without the brackets ("on leave PM", "annual leave half day").</summary>
public static class ReportLeaveTitles
{
    public const string Am = "AM", Pm = "PM", Half = "half";

    /// <summary>mail-triage's [one_to_ones] leave_pattern default: the title starts with the name, then the kind.</summary>
    public const string DefaultPattern = @"^{name}\s+(annual leave|on leave)\b";

    /// <summary>calendar-team-leave-sync's [types] default title.</summary>
    public const string DefaultTitle = "{name} annual leave";

    // Python's \s (str patterns) adds U+001C to U+001F to .NET's; kept so titles read exactly as they did.
    private const string S = @"[\s\x1C-\x1F]";
    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex FromMark = R($@"\bfrom{S}+(am|pm)\b");
    private static readonly Regex ToMark = R($@"\bto{S}+(am|pm)\b");
    private static readonly Regex FromOrTo = R($@"\b(from|to){S}+(am|pm)\b");
    private static readonly Regex EachMark = R(@"\b(am|pm)\b");
    private static readonly Regex HalfWord = R(@"\bhalf\b");
    internal static readonly Regex Morning = R(@"\bmorning\b");
    internal static readonly Regex Afternoon = R(@"\bafternoon\b");

    /// <summary>(each day, first day, last day) halves marked in a title, or in the part after the name and kind:
    /// "from AM/PM" is the first day's, "to AM/PM" the last day's, any other AM or PM every day's, and "half" without
    /// one a half day of unknown half ("half"). Exactly mail-triage's reading (OneToOnePlanner.Markers until 22 Nov 2026).</summary>
    public static (string? Each, string? First, string? Last) Markers(string text)
    {
        var first = FromMark.Match(text);
        var last = ToMark.Match(text);
        var rest = FromOrTo.Replace(text, " ");
        var each = EachMark.Match(rest);
        var eachKind = each.Success ? each.Groups[1].Value.ToUpperInvariant() : HalfWord.IsMatch(rest) ? Half : null;
        return (eachKind, first.Success ? first.Groups[1].Value.ToUpperInvariant() : null, last.Success ? last.Groups[1].Value.ToUpperInvariant() : null);
    }

    /// <summary>A report's leave title, if <paramref name="subject"/> is one for <paramref name="name"/>: the
    /// <paramref name="pattern"/> ({name} for the name, escaped) must match at the start, and the halves are read from
    /// what follows it. Null if it isn't theirs.</summary>
    public static ReportLeaveTitle? Parse(string subject, string name, string pattern = DefaultPattern)
    {
        if (!pattern.Contains("{name}", StringComparison.Ordinal))
            throw new ArgumentException("The leave pattern needs {name}, where the person's name goes.", nameof(pattern));
        var found = R(pattern.Replace("{name}", Regex.Escape(name), StringComparison.Ordinal)).Match(subject);
        if (!found.Success || found.Index != 0)
            return null;
        var (each, first, last) = Markers(subject[(found.Index + found.Length)..]);
        var kind = found.Groups.Count > 1 && found.Groups[1].Success ? found.Groups[1].Value : found.Value;
        return new ReportLeaveTitle(name, kind, each, first, last);
    }

    /// <summary>The halves of an absence (AM / PM, or null for a whole day) from People First's day parts (FULL, HALF_AM,
    /// HALF_PM): one day, AM or PM if only one half is off (both halves, or FULL, is a whole day); several days, the first
    /// day's PM (it starts in the afternoon) and the last day's AM (it ends after the morning). calendar-team-leave-sync's
    /// Absence.Halves.</summary>
    public static (string? Single, string? First, string? Last) Halves(DateOnly start, DateOnly end, string startPart, string endPart,
        string full = "FULL", string am = "HALF_AM", string pm = "HALF_PM")
    {
        string? Mark(string part) => part == am ? Am : part == pm ? Pm : null;
        if (start == end)
        {
            var parts = new HashSet<string>(StringComparer.Ordinal) { startPart, endPart };
            parts.Remove(full);
            return (parts.Count == 1 ? Mark(parts.First()) : null, null, null);
        }
        return (null, startPart == pm ? Pm : null, endPart == am ? Am : null);
    }

    /// <summary>The title for a report's absence: <paramref name="template"/> ([types], "{name} annual leave") with the
    /// name, then the halves marked as <paramref name="format"/> says. Byte for byte what calendar-team-leave-sync wrote
    /// before 22 Nov 2026 (Absence.Title).</summary>
    public static string Format(string template, string name, (string? Single, string? First, string? Last) halves, ReportLeaveFormat? format = null)
    {
        var f = format ?? new ReportLeaveFormat();
        var title = Fill(template, "name", name);
        var (single, first, last) = halves;
        if (single == Am)
            title = Fill(f.Am, "title", title);
        else if (single == Pm)
            title = Fill(f.Pm, "title", title);
        if (first is not null)
            title = Fill(f.FromPm, "title", title);
        if (last is not null)
            title = Fill(f.ToAm, "title", title);
        return title;
    }

    /// <summary>Python's str.format with one field: {field} filled, {{ and }} for literal braces; any other field is
    /// an error (as Python's KeyError).</summary>
    internal static string Fill(string template, string field, string value)
    {
        var text = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                text.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                text.Append('}');
                i++;
            }
            else if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                    throw new FormatException($"'{template}': a single '{{' needs a matching '}}'.");
                var name = template[(i + 1)..close];
                if (name != field)
                    throw new FormatException($"'{template}': only {{{field}}} can be filled in here, not {{{name}}}.");
                text.Append(value);
                i = close;
            }
            else if (c == '}')
                throw new FormatException($"'{template}': a single '}}' isn't allowed (write '}}}}').");
            else
                text.Append(c);
        }
        return text.ToString();
    }
}
