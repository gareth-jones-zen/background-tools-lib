using System.Globalization;

namespace PyCompat;

/// <summary>Python's aware datetimes with a zoneinfo zone: wall-clock arithmetic (an aware datetime plus a
/// timedelta moves the wall clock, and the offset is worked out again), the offset chosen for a wall time
/// that happens twice or not at all (fold=0), and isoformat / fromisoformat.</summary>
public static class PyTime
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // IANA names, for when the runtime can't map them itself (InvariantGlobalization leaves out ICU).
    private static readonly Dictionary<string, string> Windows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/London"] = "GMT Standard Time",
        ["Europe/Dublin"] = "GMT Standard Time",
        ["UTC"] = "UTC",
        ["Etc/UTC"] = "UTC",
        ["Europe/Paris"] = "Romance Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["America/New_York"] = "Eastern Standard Time",
    };

    /// <summary>ZoneInfo(name): an IANA name (or a Windows one). Throws TimeZoneNotFoundException if neither works.</summary>
    public static TimeZoneInfo Zone(string name)
    {
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
    /// (the clocks going back), the first, and where it doesn't happen (the clocks going forward), the offset
    /// from before the change. Both are the offset in force a day earlier.</summary>
    public static TimeSpan Offset(TimeZoneInfo zone, DateTime wall)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(wall) || zone.IsInvalidTime(wall))
            return zone.GetUtcOffset(wall.AddDays(-1));
        return zone.GetUtcOffset(wall);
    }

    /// <summary>An aware datetime for a wall time in a zone.</summary>
    public static DateTimeOffset At(TimeZoneInfo zone, DateTime wall) =>
        new(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), Offset(zone, wall));

    /// <summary>datetime.now(zone), to the microsecond as Python keeps it.</summary>
    public static DateTimeOffset Now(TimeZoneInfo zone, DateTimeOffset? utcNow = null)
    {
        var now = TimeZoneInfo.ConvertTime(utcNow ?? DateTimeOffset.UtcNow, zone);
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, now.Offset);
    }

    /// <summary>when + delta, as Python adds a timedelta to an aware datetime: on the wall clock, then re-zoned.</summary>
    public static DateTimeOffset AddWall(TimeZoneInfo zone, DateTimeOffset when, TimeSpan delta) => At(zone, when.DateTime + delta);

    /// <summary>datetime.isoformat(): microseconds only when there are some; timespec "seconds" leaves them out.</summary>
    public static string IsoFormat(DateTimeOffset when, bool secondsOnly = false)
    {
        var micro = (int)(when.Ticks % TimeSpan.TicksPerSecond / 10);
        var text = when.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv);
        if (!secondsOnly && micro != 0)
            text += "." + micro.ToString("000000", Inv);
        var offset = when.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        text += $"{sign}{offset.Hours:00}:{offset.Minutes:00}";
        if (offset.Seconds != 0)
            text += $":{offset.Seconds:00}";
        return text;
    }

    /// <summary>datetime.fromisoformat for what isoformat writes (and "Z"). A time with no offset is taken as
    /// being in the zone given. Throws FormatException for anything else.</summary>
    public static DateTimeOffset FromIsoFormat(string text, TimeZoneInfo zoneForNaive)
    {
        const DateTimeStyles styles = DateTimeStyles.None;
        if (text.EndsWith('Z') || text.EndsWith('z'))
            text = text[..^1] + "+00:00";
        string[] withOffset =
        [
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mmzzz",
            "yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
        ];
        if (DateTimeOffset.TryParseExact(text, withOffset, Inv, styles, out var aware))
            return aware;
        string[] naive =
        [
            "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        ];
        if (DateTime.TryParseExact(text, naive, Inv, styles, out var wall))
            return At(zoneForNaive, wall);
        throw new FormatException($"Invalid isoformat string: {Py.ReprString(text)}");
    }

    /// <summary>strftime("%a"): the C locale's day abbreviation.</summary>
    public static string Day(DateTimeOffset when) => when.DayOfWeek.ToString()[..3];
}
