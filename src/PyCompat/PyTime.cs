using System.Globalization;
using System.Text;

namespace PyCompat;

/// <summary>Python's datetime, as the Python tools wrote and read times: isoformat and fromisoformat in all their
/// forms, strftime (a supported subset, in the C locale), and zoneinfo-style wall-clock arithmetic for an aware
/// datetime (an aware datetime plus a timedelta moves the wall clock, and the offset is worked out again; a wall
/// time that happens twice or not at all takes Python's fold=0 offset). Times compared as strings depend on these
/// being exact.
///
/// Naive times are DateTime with Kind Unspecified (a wall-clock time, as Python's naive datetime); aware times are
/// DateTimeOffset.</summary>
public static class PyTime
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // IANA names, for when the runtime can't map them itself (InvariantGlobalization leaves out ICU).
    private static readonly Dictionary<string, string> Windows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/London"] = "GMT Standard Time",
        ["Europe/Dublin"] = "GMT Standard Time",
        ["Europe/Paris"] = "Romance Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["America/New_York"] = "Eastern Standard Time",
    };

    // ---- Zones and wall-clock arithmetic ----

    /// <summary>ZoneInfo(name): an IANA name or a Windows id ("GMT Standard Time"); "UTC" and "Etc/UTC" are
    /// TimeZoneInfo.Utc. Throws TimeZoneNotFoundException if neither works.</summary>
    public static TimeZoneInfo Zone(string name)
    {
        if (name.Equals("UTC", StringComparison.OrdinalIgnoreCase) || name.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (Windows.TryGetValue(name, out var id))
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            throw;
        }
    }

    /// <summary>The UTC offset Python gives a wall time in a zone, with fold=0: where the wall time happens twice
    /// (the clocks going back), the first (the larger offset); where it doesn't happen (the clocks going forward),
    /// the offset from before the change.</summary>
    public static TimeSpan Offset(TimeZoneInfo zone, DateTime wall)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(wall))
            return zone.GetAmbiguousTimeOffsets(wall).Max();
        if (zone.IsInvalidTime(wall))
            return zone.GetUtcOffset(wall.AddDays(-1));
        return zone.GetUtcOffset(wall);
    }

    /// <summary>A naive wall time made aware in a zone, as Python's wall.replace(tzinfo=zone) does with zoneinfo.</summary>
    public static DateTimeOffset At(TimeZoneInfo zone, DateTime wall) =>
        new(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), Offset(zone, wall));

    /// <summary>t.astimezone(zone).replace(tzinfo=None): the wall time in that zone.</summary>
    public static DateTime Local(DateTimeOffset t, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(t, zone).DateTime, DateTimeKind.Unspecified);

    /// <summary>datetime.now(zone), to the microsecond as Python keeps it.</summary>
    public static DateTimeOffset Now(TimeZoneInfo zone, DateTimeOffset? utcNow = null)
    {
        var now = TimeZoneInfo.ConvertTime(utcNow ?? DateTimeOffset.UtcNow, zone);
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, now.Offset);
    }

    /// <summary>when + delta, as Python adds a timedelta to an aware datetime: on the wall clock, then re-zoned.</summary>
    public static DateTimeOffset AddWall(TimeZoneInfo zone, DateTimeOffset when, TimeSpan delta) => At(zone, when.DateTime + delta);

    /// <summary>A time cut to whole microseconds, as Python's datetime holds it.</summary>
    public static DateTime Micro(DateTime t) => new(t.Ticks - t.Ticks % 10, DateTimeKind.Unspecified);

    /// <summary>datetime.time.max, as a time of day.</summary>
    public static readonly TimeSpan TimeMax = new(TimeSpan.TicksPerDay - 10);

    /// <summary>Weekday as Python's: Monday 0 to Sunday 6.</summary>
    public static int Weekday(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    public static int Weekday(DateTime t) => Weekday(DateOnly.FromDateTime(t));

    // ---- isoformat ----

    /// <summary>datetime.isoformat(timespec=...) for a naive time: "auto" (seconds, plus microseconds only when
    /// they aren't zero), "hours", "minutes", "seconds", "milliseconds" or "microseconds".</summary>
    public static string Iso(DateTime t, string timespec = "auto")
    {
        var micro = (int)(t.Ticks % TimeSpan.TicksPerSecond / 10);
        var seconds = t.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv);
        return timespec switch
        {
            "hours" => t.ToString("yyyy-MM-dd'T'HH", Inv),
            "minutes" => t.ToString("yyyy-MM-dd'T'HH:mm", Inv),
            "seconds" => seconds,
            "milliseconds" => seconds + "." + (micro / 1000).ToString("000", Inv),
            "microseconds" => seconds + "." + micro.ToString("000000", Inv),
            "auto" => seconds + (micro != 0 ? "." + micro.ToString("000000", Inv) : ""),
            _ => throw new ArgumentException($"Unknown timespec value: {timespec}"),
        };
    }

    /// <summary>date.isoformat().</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    /// <summary>datetime.isoformat() for an aware time: microseconds only when there are some, and secondsOnly
    /// (timespec="seconds") leaves them out. The offset is "+HH:MM", with ":SS" only if it has seconds.</summary>
    public static string IsoFormat(DateTimeOffset when, bool secondsOnly = false) =>
        IsoFormat(when, secondsOnly ? "seconds" : "auto");

    /// <summary>datetime.isoformat(timespec=...) for an aware time.</summary>
    public static string IsoFormat(DateTimeOffset when, string timespec)
    {
        var text = Iso(when.DateTime, timespec);
        var offset = when.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        text += $"{sign}{offset.Hours:00}:{offset.Minutes:00}";
        if (offset.Seconds != 0)
            text += $":{offset.Seconds:00}";
        return text;
    }

    // ---- fromisoformat ----

    /// <summary>datetime.fromisoformat for a naive time, in every form Python 3.14 accepts: "YYYY-MM-DD" or
    /// "YYYYMMDD" (or an ISO week date, "YYYY-Www-D"), then any one character, then "HH", "HH:MM", "HHMM",
    /// "HH:MM:SS" or "HHMMSS", with a fraction after "." or "," (digits past six ignored), and "24:00" as midnight
    /// the next day. Throws FormatException with Python's message for anything else, including a string with a
    /// UTC offset (which Python would return aware: use <see cref="FromIsoFormat"/>).</summary>
    public static DateTime FromIso(string text)
    {
        var (wall, offset) = ParseDateTime(text);
        if (offset is not null)
            throw new FormatException($"Invalid isoformat string: {Py.ReprString(text)} has a UTC offset, and a naive time was wanted");
        return wall;
    }

    /// <summary>datetime.fromisoformat for an aware time. A time with an offset (or "Z") keeps it; one with none is
    /// taken as a wall time in zoneForNaive (fold=0). Throws FormatException for anything Python refuses, and for an
    /// offset DateTimeOffset can't hold (seconds, or more than 14 hours).</summary>
    public static DateTimeOffset FromIsoFormat(string text, TimeZoneInfo zoneForNaive)
    {
        var (wall, offset) = ParseDateTime(text);
        if (offset is not { } given)
            return At(zoneForNaive, wall);
        if (given.Ticks % TimeSpan.TicksPerMinute != 0 || given.Duration() > TimeSpan.FromHours(14))
            throw new FormatException($"Invalid isoformat string: {Py.ReprString(text)} has a UTC offset .NET can't hold");
        return new DateTimeOffset(wall, given);
    }

    /// <summary>time.fromisoformat for a naive time: "HH", "HH:MM", "HHMM", "HH:MM:SS" or "HHMMSS", an optional
    /// fraction, an optional leading "T"; "24:00" is midnight (00:00), as Python 3.14 reads it. Throws
    /// FormatException for anything else, including a time with a UTC offset.</summary>
    public static TimeSpan TimeFromIso(string text)
    {
        var body = text.StartsWith('T') ? text[1..] : text;
        Clock clock;
        try
        {
            clock = ParseTime(body);
        }
        catch (FormatException)
        {
            throw Invalid(body);
        }
        if (clock.Offset is not null || clock.Error || clock.Hour > 23 || clock.Minute > 59 || clock.Second > 59)
            throw Invalid(body);
        return new TimeSpan(clock.Hour, clock.Minute, clock.Second) + TimeSpan.FromTicks(clock.Micro * 10L);
    }

    private static FormatException Invalid(string text) => new($"Invalid isoformat string: {Py.ReprString(text)}");

    private readonly record struct Clock(int Hour, int Minute, int Second, int Micro, TimeSpan? Offset, bool NextDay, bool Error);

    /// <summary>CPython's datetime_fromisoformat (Modules/_datetimemodule.c), which is what Python runs.</summary>
    private static (DateTime Wall, TimeSpan? Offset) ParseDateTime(string text)
    {
        if (text.Length < 7)
            throw Invalid(text);
        int year, month, day;
        Clock clock = default;
        try
        {
            var separator = Math.Min(FindSeparator(text), text.Length);   // a slice past the end is clipped, as in Python
            (year, month, day) = ParseDate(text[..separator]);
            if (separator < text.Length)
            {
                var time = text[(separator + 1)..];
                if (time.Length == 0)
                    throw Invalid(text);
                clock = ParseTime(time);
            }
        }
        catch (FormatException)
        {
            throw Invalid(text);
        }
        if (clock.Error)
            throw new FormatException("minute, second, and microsecond must be 0 when hour is 24");
        if (clock.NextDay && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            (year, month, day) = Next(year, month, day);
        if (month is < 1 or > 12)
            throw new FormatException($"month must be in 1..12, not {month}");
        if (year is < 1 or > 9999)
            throw new FormatException($"year {year} is out of range");
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
            throw new FormatException($"day {day} must be in range 1..{DateTime.DaysInMonth(year, month)} for month {month} in year {year}");
        if (clock.Hour > 23)
            throw new FormatException($"hour must be in 0..23, not {clock.Hour}");
        if (clock.Minute > 59)
            throw new FormatException($"minute must be in 0..59, not {clock.Minute}");
        if (clock.Second > 59)
            throw new FormatException($"second must be in 0..59, not {clock.Second}");
        if (clock.Offset is { } offset && offset.Duration() >= TimeSpan.FromDays(1))
            throw new FormatException("offset must be a timedelta strictly between -timedelta(hours=24) and timedelta(hours=24).");
        var wall = new DateTime(year, month, day, clock.Hour, clock.Minute, clock.Second, DateTimeKind.Unspecified).AddTicks(clock.Micro * 10L);
        return (wall, clock.Offset);
    }

    private static (int, int, int) Next(int year, int month, int day)
    {
        var next = new DateOnly(year, month, day).AddDays(1);
        return (next.Year, next.Month, next.Day);
    }

    /// <summary>_find_isoformat_datetime_separator: where the date ends (7, 8 or 10, or as far as the digits go).</summary>
    private static int FindSeparator(string s)
    {
        if (s.Length == 7)
            return 7;
        if (s[4] == '-')
        {
            if (s[5] != 'W')
                return 10;   // YYYY-MM-DD
            if (s.Length > 8 && s[8] == '-')
            {
                if (s.Length == 9)
                    throw new FormatException("Invalid ISO string");
                if (s.Length > 10 && char.IsAsciiDigit(s[10]))
                    return 8;   // YYYY-Www-##: taken as a hyphen at 8
                return 10;      // YYYY-Www-D
            }
            return 8;           // YYYY-Www
        }
        if (s[4] == 'W')
        {
            var idx = 7;        // YYYYWww or YYYYWwwD
            while (idx < s.Length && char.IsAsciiDigit(s[idx]))
                idx++;
            if (idx < 9)
                return idx;
            return idx % 2 == 0 ? 7 : 8;
        }
        return 8;               // YYYYMMDD
    }

    /// <summary>_parse_isoformat_date, with only ASCII digits, as the C version reads them.</summary>
    private static (int Year, int Month, int Day) ParseDate(string d)
    {
        if (d.Length is not (7 or 8 or 10))
            throw new FormatException("Invalid ISO date");
        var year = Digits(d, 0, 4);
        var hasSep = d[4] == '-';
        var pos = 4 + (hasSep ? 1 : 0);
        if (pos < d.Length && d[pos] == 'W')
        {
            pos++;
            var week = Digits(d, pos, 2);
            pos += 2;
            var dayOfWeek = 1;
            if (d.Length > pos)
            {
                if ((d[pos] == '-') != hasSep)
                    throw new FormatException("Inconsistent use of dash separator");
                pos += hasSep ? 1 : 0;
                dayOfWeek = Digits(d, pos, 1);
                pos++;
            }
            if (pos != d.Length)
                throw new FormatException("Invalid ISO date");
            return FromIsoWeek(year, week, dayOfWeek);
        }
        var month = Digits(d, pos, 2);
        pos += 2;
        if ((pos < d.Length && d[pos] == '-') != hasSep)
            throw new FormatException("Inconsistent use of dash separator");
        pos += hasSep ? 1 : 0;
        var day = Digits(d, pos, 2);
        if (pos + 2 != d.Length)
            throw new FormatException("Invalid ISO date");
        return (year, month, day);
    }

    private static (int, int, int) FromIsoWeek(int year, int week, int day)
    {
        if (year is < 1 or > 9999)
            throw new FormatException($"year must be in 1..9999, not {year}");
        if (week < 1 || week > 53 || (week == 53 && ISOWeek.GetWeeksInYear(year) != 53))
            throw new FormatException($"Invalid week: {week}");
        if (day is < 1 or > 7)
            throw new FormatException($"Invalid weekday: {day} (range is [1, 7])");
        var date = ISOWeek.ToDateTime(year, week, (DayOfWeek)(day % 7));
        return (date.Year, date.Month, date.Day);
    }

    /// <summary>_parse_isoformat_time: HH[:MM[:SS[.f]]] then an optional offset ("Z", or +/- and the same shape).</summary>
    private static Clock ParseTime(string t)
    {
        if (t.Length < 2)
            throw new FormatException("Isoformat time too short");
        var tz = t.IndexOfAny(['+', '-', 'Z']);
        var (hour, minute, second, micro) = ParseHms(tz >= 0 ? t[..tz] : t);
        var nextDay = false;
        var error = false;
        if (hour == 24)
        {
            if (minute == 0 && second == 0 && micro == 0)
            {
                hour = 0;
                nextDay = true;
            }
            else
                error = true;
        }
        TimeSpan? offset = null;
        if (tz >= 0)
        {
            if (t[tz] == 'Z')
            {
                if (tz != t.Length - 1)
                    throw new FormatException("Malformed time zone string");
                offset = TimeSpan.Zero;
            }
            else
            {
                var zone = t[(tz + 1)..];
                if (zone.Length is 0 or 1 or 3)
                    throw new FormatException("Malformed time zone string");
                var (h, m, s, us) = ParseHms(zone);
                var span = new TimeSpan(0, h, m, s) + TimeSpan.FromTicks(us * 10L);
                offset = t[tz] == '-' ? -span : span;
            }
        }
        return new Clock(hour, minute, second, micro, offset, nextDay, error);
    }

    private static readonly int[] FractionCorrection = [100000, 10000, 1000, 100, 10];

    /// <summary>_parse_hh_mm_ss_ff: HH[:?MM[:?SS[{.,}fff[fff]]]], the colons used throughout or not at all.</summary>
    private static (int Hour, int Minute, int Second, int Micro) ParseHms(string t)
    {
        var parts = new int[4];
        var pos = 0;
        var hasSep = false;
        for (var part = 0; part < 3; part++)
        {
            if (t.Length - pos < 2)
                throw new FormatException("Incomplete time component");
            parts[part] = Digits(t, pos, 2);
            pos += 2;
            var next = pos < t.Length ? t[pos] : '\0';
            if (part == 0)
                hasSep = next == ':';
            if (pos >= t.Length || part >= 2)
                break;
            if (hasSep && next != ':')
                throw new FormatException("Invalid time separator");
            pos += hasSep ? 1 : 0;
        }
        if (pos < t.Length)
        {
            if (t[pos] is not ('.' or ','))
                throw new FormatException("Invalid microsecond separator");
            pos++;
            var fraction = t[pos..];
            if (fraction.Length == 0 || !fraction.All(char.IsAsciiDigit))
                throw new FormatException("Non-digit values in fraction");
            var used = Math.Min(fraction.Length, 6);
            parts[3] = Digits(fraction, 0, used);
            if (used < 6)
                parts[3] *= FractionCorrection[used - 1];
        }
        return (parts[0], parts[1], parts[2], parts[3]);
    }

    private static int Digits(string s, int at, int count)
    {
        if (at + count > s.Length)
            throw new FormatException("Too few digits");
        var value = 0;
        for (var i = at; i < at + count; i++)
        {
            if (!char.IsAsciiDigit(s[i]))
                throw new FormatException("Not a digit");
            value = value * 10 + (s[i] - '0');
        }
        return value;
    }

    // ---- strftime ----

    /// <summary>The strftime directives supported (in the C locale, so English names). Anything else is refused, so
    /// a format never means something different in .NET.</summary>
    public const string Directives = "aAbBdmyYHIMSp%";

    /// <summary>Whether a strftime format uses only the supported directives.</summary>
    public static bool Supported(string format)
    {
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
                continue;
            if (i + 1 >= format.Length || !Directives.Contains(format[i + 1]))
                return false;
            i++;
        }
        return true;
    }

    private static readonly string[] Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    private static readonly string[] Months =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>strftime in the C locale, for the supported directives (<see cref="Directives"/>). Throws
    /// FormatException for any other.</summary>
    public static string Strftime(DateTime t, string format)
    {
        var b = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%' || i + 1 >= format.Length)
            {
                b.Append(c);
                continue;
            }
            var d = format[++i];
            switch (d)
            {
                case 'a': b.Append(Days[Weekday(t)][..3]); break;
                case 'A': b.Append(Days[Weekday(t)]); break;
                case 'b': b.Append(Months[t.Month - 1][..3]); break;
                case 'B': b.Append(Months[t.Month - 1]); break;
                case 'd': b.Append(t.Day.ToString("00", Inv)); break;
                case 'm': b.Append(t.Month.ToString("00", Inv)); break;
                case 'y': b.Append((t.Year % 100).ToString("00", Inv)); break;
                case 'Y': b.Append(t.Year.ToString(Inv)); break;
                case 'H': b.Append(t.Hour.ToString("00", Inv)); break;
                case 'I': b.Append((t.Hour % 12 == 0 ? 12 : t.Hour % 12).ToString("00", Inv)); break;
                case 'M': b.Append(t.Minute.ToString("00", Inv)); break;
                case 'S': b.Append(t.Second.ToString("00", Inv)); break;
                case 'p': b.Append(t.Hour < 12 ? "AM" : "PM"); break;
                case '%': b.Append('%'); break;
                default: throw new FormatException($"Unsupported strftime directive %{d}");
            }
        }
        return b.ToString();
    }

    public static string Strftime(DateOnly d, string format) => Strftime(d.ToDateTime(TimeOnly.MinValue), format);

    /// <summary>strftime of an aware time's wall clock (no %z or %Z).</summary>
    public static string Strftime(DateTimeOffset t, string format) => Strftime(t.DateTime, format);

    /// <summary>strftime("%a"): the C locale's day abbreviation.</summary>
    public static string Day(DateTimeOffset when) => Strftime(when, "%a");
}
