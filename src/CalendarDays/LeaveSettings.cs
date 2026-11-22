using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CalendarDays;

/// <summary>What counts as your leave and your bank holidays: the shared <c>[leave]</c> settings (22 Nov 2026), read the
/// same way by every tool that asks "is this a day off?", so they agree.
///
/// <list type="bullet">
/// <item><c>subjects</c>: your own entries whose subject matches are leave (People First's "Holiday (&lt;job title&gt;)",
/// your own "Annual leave", "Summer holidays", ...).</item>
/// <item><c>categories</c>: your own entries in these Outlook categories are leave too (none by default).</item>
/// <item><c>min_hours</c>: leave covering this many hours of a day (or all day) makes the day off; less is a half day.</item>
/// <item><c>half_day</c>: a leave entry whose subject matches is a half day whatever its length (People First's
/// "Holiday (half day) (&lt;job title&gt;)").</item>
/// <item><c>bank_holiday_subjects</c>, <c>bank_holiday_categories</c>: your own entries that mark a bank holiday
/// (calendar-bank-holidays' "Bank holiday" category, or a subject starting "Bank holiday"). A bank holiday is its own
/// kind of day, not leave: tools that treat it as a day off say so themselves.</item>
/// </list>
///
/// A tool's settings hold these in its <c>[leave]</c> table, alongside the tool's own keys (out-of-office's messages,
/// meeting-preps' min_working_days), which the tool names when it parses. With PyCompat.BaseSettings, a base file's
/// <c>[leave]</c> fills in whatever the tool's own <c>[leave]</c> doesn't set, the tool's own winning, so the rules can be
/// kept once in the base.</summary>
public sealed record LeaveSettings
{
    public const string DefaultSubjects = @"^\s*(holiday\b|annual leave|on leave|summer holidays?)";
    public const string DefaultHalfDay = @"\(half day\)";
    public const string DefaultBankHolidaySubjects = @"^\s*bank holiday\b";
    public static readonly IReadOnlyList<string> DefaultBankHolidayCategories = ["Bank holiday"];

    /// <summary>The shared keys of a [leave] table. A tool's own keys are allowed beside them when it names them.</summary>
    public static readonly IReadOnlyList<string> SharedKeys =
        ["subjects", "categories", "min_hours", "half_day", "bank_holiday_subjects", "bank_holiday_categories"];

    public Regex Subjects { get; init; } = Pattern(DefaultSubjects);
    public IReadOnlyList<string> Categories { get; init; } = [];
    public double MinHours { get; init; } = 6;
    public Regex HalfDay { get; init; } = Pattern(DefaultHalfDay);
    public Regex BankHolidaySubjects { get; init; } = Pattern(DefaultBankHolidaySubjects);
    public IReadOnlyList<string> BankHolidayCategories { get; init; } = DefaultBankHolidayCategories;

    /// <summary>The leave rules for <see cref="DayRules.Leave"/>.</summary>
    public LeaveRules Rules => new(Subjects, Categories, MinHours);

    /// <summary>A pattern as the tools compile theirs: case-insensitive, culture-invariant, with a one-second timeout.</summary>
    public static Regex Pattern(string text) =>
        new(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Whether an entry of yours marks a bank holiday: no one else invited, and its subject or a category says so.</summary>
    public bool IsBankHoliday(DayEvent e) =>
        e.Mine && e.Attendees == 0 && (BankHolidaySubjects.IsMatch(e.Subject)
                                       || e.Categories.Any(c => BankHolidayCategories.Contains(c, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Read a [leave] table (a TomlTable, or any string-keyed dictionary; null: the defaults). The shared keys
    /// are read here; <paramref name="toolKeys"/> are the tool's own keys in the same table, accepted and left for the
    /// tool; anything else is an error. <paramref name="defaults"/> stand in for a key not given (a tool whose defaults
    /// differ, as work-location's leave categories do, passes its own). Throws FormatException naming
    /// <paramref name="where"/> (the settings file).</summary>
    public static LeaveSettings Parse(IDictionary<string, object>? table, string where, IEnumerable<string>? toolKeys = null,
        LeaveSettings? defaults = null)
    {
        var d = defaults ?? new LeaveSettings();
        if (table is null)
            return d;
        var known = SharedKeys.Concat(toolKeys ?? []).ToList();
        foreach (var key in table.Keys)
            if (!known.Contains(key, StringComparer.Ordinal))
                throw new FormatException($"{where}: [leave] has an unknown setting '{key}' (known: {string.Join(", ", known)}).");

        Regex Regex(string key, Regex fallback)
        {
            if (!table.TryGetValue(key, out var v))
                return fallback;
            if (v is not string text)
                throw new FormatException($"{where}: [leave] {key} should be text (a regular expression).");
            try
            {
                return Pattern(text);
            }
            catch (ArgumentException error)
            {
                throw new FormatException($"{where}: [leave] {key} isn't a valid regular expression ({error.Message}).", error);
            }
        }
        IReadOnlyList<string> Names(string key, IReadOnlyList<string> fallback)
        {
            if (!table.TryGetValue(key, out var v))
                return fallback;
            if (v is string || v is not IEnumerable items)
                throw new FormatException($"{where}: [leave] {key} should be a list of names, such as [\"Bank holiday\"].");
            var names = new List<string>();
            foreach (var item in items)
                names.Add(item as string ?? throw new FormatException($"{where}: [leave] {key} should be a list of names, such as [\"Bank holiday\"]."));
            return names;
        }
        double Hours(string key, double fallback)
        {
            if (!table.TryGetValue(key, out var v))
                return fallback;
            var n = v switch { long l => l, int i => i, double x => x, _ => double.NaN };
            return n is >= 0 and <= 24
                ? n
                : throw new FormatException($"{where}: [leave] {key} should be a number of hours, 0 to 24 (it's {Convert.ToString(v, CultureInfo.InvariantCulture)}).");
        }

        return new LeaveSettings
        {
            Subjects = Regex("subjects", d.Subjects),
            Categories = Names("categories", d.Categories),
            MinHours = Hours("min_hours", d.MinHours),
            HalfDay = Regex("half_day", d.HalfDay),
            BankHolidaySubjects = Regex("bank_holiday_subjects", d.BankHolidaySubjects),
            BankHolidayCategories = Names("bank_holiday_categories", d.BankHolidayCategories),
        };
    }
}
