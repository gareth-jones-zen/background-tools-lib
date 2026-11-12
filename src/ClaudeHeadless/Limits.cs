using System.Globalization;
using System.Text.RegularExpressions;
using PyCompat;

namespace ClaudeHeadless;

/// <summary>Claude's usage limit was reached, or is still in force. Reset is when it lifts.</summary>
public sealed class LimitReachedException(DateTimeOffset reset, string message) : Exception(message)
{
    public DateTimeOffset Reset { get; } = reset;
}

/// <summary>A headless run that failed for any reason other than the usage limit (Python's RuntimeError).</summary>
public sealed class ClaudeRunException(string message) : Exception(message);

/// <summary>Reading the usage limit from Claude Code's messages.</summary>
public static partial class Limits
{
    /// <summary>The seat's time zone: CLAUDE_HEADLESS_TZ, Europe/London by default. Reset times in Claude's
    /// messages are wall-clock times in it.</summary>
    public static TimeZoneInfo Zone { get; } = PyTime.Zone(
        Environment.GetEnvironmentVariable("CLAUDE_HEADLESS_TZ") is { Length: > 0 } tz ? tz : "Europe/London");

    /// <summary>Now, in the seat's zone.</summary>
    public static DateTimeOffset Now() => PyTime.Now(Zone);

    /// <summary>"hit your session limit", "usage limit": text that means the limit.</summary>
    [GeneratedRegex(@"hit your .{0,20}limit|usage limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex LimitText();

    [GeneratedRegex(@"resets\s+(?:([A-Z][a-z]{2})\s+(\d{1,2}),?\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResetText();

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>"... resets 7pm (Europe/London)", "... resets 7:30pm", "... resets Oct 9, 7pm" -> when, in the
    /// seat's zone. An hour from now if it can't be read. A month name that isn't one throws FormatException,
    /// as Python's strptime raises ValueError.</summary>
    public static DateTimeOffset ResetTime(string text, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        zone ??= Zone;
        var current = now is { } given ? TimeZoneInfo.ConvertTime(given, zone) : Now();
        var match = ResetText().Match(text);
        if (!match.Success)
            return PyTime.AddWall(zone, current, TimeSpan.FromHours(1));
        var hour = Number(match.Groups[3].Value) % 12 + (match.Groups[5].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        var minute = match.Groups[4].Success ? Number(match.Groups[4].Value) : 0;
        var wall = current.DateTime;
        var when = new DateTime(wall.Year, wall.Month, wall.Day, hour, minute, 0);
        if (match.Groups[1].Success)
        {
            var month = Array.IndexOf(Months, match.Groups[1].Value.ToLowerInvariant()) + 1;
            if (month == 0)
                throw new FormatException($"time data {Py.ReprString(match.Groups[1].Value)} does not match format '%b'");
            when = new DateTime(when.Year, month, Number(match.Groups[2].Value), hour, minute, 0);
            return PyTime.At(zone, when);
        }
        var result = PyTime.At(zone, when);
        if (result <= current)
            result = PyTime.AddWall(zone, result, TimeSpan.FromDays(1));
        return result;
    }

    // Python's int() of regex \d digits, which may be any decimal digits, not only ASCII.
    private static int Number(string digits)
    {
        var value = 0;
        foreach (var c in digits)
            value = value * 10 + (int)char.GetNumericValue(c);
        return value;
    }

    internal static string Clock(DateTimeOffset when) => when.ToString("HH:mm", CultureInfo.InvariantCulture);
}
