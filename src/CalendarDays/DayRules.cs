using System.Text.RegularExpressions;

namespace CalendarDays;

/// <summary>A calendar event as the rules see it, in local time. <see cref="Mine"/>: you organise it (or it's a plain
/// entry of yours); <see cref="Attendees"/>: people invited, rooms and other resources not counted.</summary>
public sealed record DayEvent(string Subject, DateTime Start, DateTime End, bool AllDay, string ShowAs,
    IReadOnlyList<string> Categories, bool Mine, int Attendees);

/// <summary>What counts as leave: your own entries (no one else invited) whose subject matches, or that carry one of
/// the categories, covering at least <see cref="MinHours"/> of the day (or all day).</summary>
public sealed record LeaveRules(Regex Subjects, IReadOnlyList<string> Categories, double MinHours = 6);

/// <summary>What counts as a day away working (a conference, an off-site): an event whose subject matches (all-day
/// only, unless <see cref="SubjectsAllDayOnly"/> is false), or one shown as one of <see cref="ShowAs"/>; either covering
/// at least <see cref="MinHours"/> of the day unless it's all day (0: any length).</summary>
public sealed record ElsewhereRules(Regex? Subjects, IReadOnlyList<string> ShowAs, double MinHours = 0, bool SubjectsAllDayOnly = true);

/// <summary>The leave on a day: whether it makes the day off, the hours it covers (overlaps merged), and the entries.</summary>
public sealed record LeaveDay(bool DayOff, double Hours, IReadOnlyList<DayEvent> Entries);

/// <summary>A day away working: the event that says so, and why.</summary>
public sealed record ElsewhereDay(DayEvent Event, string Why);

/// <summary>The day rules the calendar tools share. Pure.</summary>
public static class DayRules
{
    public static bool Weekend(DateOnly day) => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Whether an event overlaps the day at all.</summary>
    public static bool Covers(DayEvent e, DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        return e.Start < start.AddDays(1) && e.End > start;
    }

    /// <summary>Hours of the window the events cover, overlaps merged (two entries over the same hours count once).</summary>
    public static double Hours(IEnumerable<DayEvent> events, DateTime start, DateTime end)
    {
        double total = 0;
        DateTime? spanStart = null, spanEnd = null;
        foreach (var e in events.Select(e => (S: e.Start < start ? start : e.Start, E: e.End > end ? end : e.End)).Where(x => x.E > x.S).OrderBy(x => x.S))
        {
            if (spanEnd is null || e.S > spanEnd)
            {
                if (spanEnd is not null)
                    total += (spanEnd.Value - spanStart!.Value).TotalHours;
                (spanStart, spanEnd) = (e.S, e.E);
            }
            else if (e.E > spanEnd)
                spanEnd = e.E;
        }
        if (spanEnd is not null)
            total += (spanEnd.Value - spanStart!.Value).TotalHours;
        return total;
    }

    /// <summary>Your own leave entries on a day (subject or category), whatever their length.</summary>
    public static IReadOnlyList<DayEvent> LeaveEntries(LeaveRules rules, IEnumerable<DayEvent> events, DateOnly day) =>
        events.Where(e => e.Mine && e.Attendees == 0 && Covers(e, day)
                          && (rules.Subjects.IsMatch(e.Subject) || e.Categories.Any(c => rules.Categories.Contains(c, StringComparer.OrdinalIgnoreCase))))
            .ToList();

    /// <summary>The leave on a day: a day off when the entries cover at least MinHours of it, or any is all day.</summary>
    public static LeaveDay Leave(LeaveRules rules, IEnumerable<DayEvent> events, DateOnly day)
    {
        var entries = LeaveEntries(rules, events, day);
        var start = day.ToDateTime(TimeOnly.MinValue);
        var hours = Hours(entries, start, start.AddDays(1));
        return new LeaveDay(entries.Count > 0 && (hours >= rules.MinHours - 1e-9 || entries.Any(e => e.AllDay)), hours, entries);
    }

    /// <summary>Whether you're away working on a day: an event whose subject matches first, then one shown as one of
    /// the signals. <paramref name="notASignal"/> leaves out events whose show-as says nothing (a tool's own entries,
    /// which it may show as working elsewhere itself).</summary>
    public static ElsewhereDay? Elsewhere(ElsewhereRules rules, IEnumerable<DayEvent> events, DateOnly day, Func<DayEvent, bool>? notASignal = null)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        var list = events.Where(e => Covers(e, day)).ToList();
        bool Long(DayEvent e) => e.AllDay || rules.MinHours <= 0 || Hours([e], start, start.AddDays(1)) >= rules.MinHours - 1e-9;

        var named = list.FirstOrDefault(e => (e.AllDay || !rules.SubjectsAllDayOnly) && Long(e) && rules.Subjects?.IsMatch(e.Subject) == true);
        if (named is not null)
            return new ElsewhereDay(named, named.AllDay ? $"all-day \"{named.Subject}\"" : $"\"{named.Subject}\"");
        var shown = list.FirstOrDefault(e => notASignal?.Invoke(e) != true && Long(e) && rules.ShowAs.Contains(e.ShowAs, StringComparer.OrdinalIgnoreCase));
        return shown is null ? null : new ElsewhereDay(shown, $"\"{shown.Subject}\" shown as {shown.ShowAs}");
    }
}
