namespace CalendarDays;

/// <summary>Which half of a day a half day is: the morning, the afternoon, or not known (People First's
/// "Holiday (half day) (&lt;job title&gt;)" doesn't say).</summary>
public enum HalfOfDay { Am, Pm, Unknown }

/// <summary>What a day is, as far as your leave goes: nothing, a bank holiday, a day of leave, or a half day.</summary>
public enum DayOffKind { None, BankHoliday, Leave, HalfDay }

/// <summary>A day classified: its kind, the half for a half day (null otherwise), and the entry that decided it (the
/// bank holiday, or the first leave entry).</summary>
public sealed record DayOff(DateOnly Day, DayOffKind Kind, HalfOfDay? Half, DayEvent? Entry, LeaveDay? Leave)
{
    /// <summary>The share of the day away: 1 for leave or a bank holiday, 0.5 for a half day, 0 otherwise.</summary>
    public double Share => Kind switch { DayOffKind.Leave or DayOffKind.BankHoliday => 1, DayOffKind.HalfDay => 0.5, _ => 0 };
}

public static class DayOffRules
{
    /// <summary>Midday, where the morning ends and the afternoon starts, unless a tool says otherwise.</summary>
    public static readonly TimeOnly Midday = new(12, 0);

    /// <summary>What a day is (weekends aren't judged here: a tool's working days are its own). A bank holiday first
    /// (an entry of yours marking one covers the day); then leave (<see cref="DayRules.Leave"/>), a half day when every
    /// leave entry's subject says half day or the entries don't make the day off (under min_hours): the rule
    /// out-of-office and meeting-preps use. A half day says which half where it can (<see cref="HalfOf"/>).</summary>
    public static DayOff Classify(LeaveSettings settings, IReadOnlyCollection<DayEvent> events, DateOnly day, TimeOnly? midday = null)
    {
        var bank = events.FirstOrDefault(e => DayRules.Covers(e, day) && settings.IsBankHoliday(e));
        if (bank is not null)
            return new DayOff(day, DayOffKind.BankHoliday, null, bank, null);
        var leave = DayRules.Leave(settings.Rules, events, day);
        if (leave.Entries.Count == 0)
            return new DayOff(day, DayOffKind.None, null, null, leave);
        var half = leave.Entries.All(e => settings.HalfDay.IsMatch(e.Subject)) || !leave.DayOff;
        return half
            ? new DayOff(day, DayOffKind.HalfDay, HalfOf(leave.Entries, day, midday ?? Midday), leave.Entries[0], leave)
            : new DayOff(day, DayOffKind.Leave, null, leave.Entries[0], leave);
    }

    /// <summary>Which half of <paramref name="day"/> some half-day leave entries are: each says it in its subject
    /// (<see cref="ReportLeaveTitles.Markers"/>: "AM", "(PM)", "from PM" on the first day of a longer entry, "to AM" on
    /// its last, or "morning" / "afternoon"), or, failing that, by its hours when it's a timed entry wholly before or
    /// after midday. If the entries agree, that half; if they cover both halves or can't be told, Unknown.</summary>
    public static HalfOfDay HalfOf(IEnumerable<DayEvent> entries, DateOnly day, TimeOnly midday)
    {
        var halves = entries.Select(e => HalfOf(e, day, midday)).Distinct().ToList();
        return halves.Count == 1 ? halves[0] : HalfOfDay.Unknown;
    }

    /// <summary>Which half of the day one entry covers (see the other overload).</summary>
    public static HalfOfDay HalfOf(DayEvent e, DateOnly day, TimeOnly midday)
    {
        var (each, first, last) = ReportLeaveTitles.Markers(e.Subject);
        var firstDay = DateOnly.FromDateTime(e.Start);
        var lastDay = DateOnly.FromDateTime(e.End.TimeOfDay == TimeSpan.Zero && e.End > e.Start ? e.End.AddTicks(-1) : e.End);
        var mark = day == firstDay && first is not null ? first
                 : day == lastDay && last is not null ? last
                 : each;
        if (mark == ReportLeaveTitles.Am)
            return HalfOfDay.Am;
        if (mark == ReportLeaveTitles.Pm)
            return HalfOfDay.Pm;
        if (ReportLeaveTitles.Morning.IsMatch(e.Subject))
            return HalfOfDay.Am;
        if (ReportLeaveTitles.Afternoon.IsMatch(e.Subject))
            return HalfOfDay.Pm;
        if (!e.AllDay)
        {
            var start = day.ToDateTime(TimeOnly.MinValue);
            var noon = day.ToDateTime(midday);
            var from = e.Start < start ? start : e.Start;
            var to = e.End > start.AddDays(1) ? start.AddDays(1) : e.End;
            if (to > from && to <= noon)
                return HalfOfDay.Am;
            if (to > from && from >= noon)
                return HalfOfDay.Pm;
        }
        return HalfOfDay.Unknown;
    }
}
