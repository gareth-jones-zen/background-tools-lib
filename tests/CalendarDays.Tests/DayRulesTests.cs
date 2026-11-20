using System.Text.RegularExpressions;
using CalendarDays;

namespace CalendarDays.Tests;

public sealed class DayRulesTests
{
    private static readonly LeaveRules Leave = new(
        new Regex(@"^\s*(holiday\b|annual leave|bank holiday|on leave)", RegexOptions.IgnoreCase), ["Bank holiday"], 6);

    private static readonly ElsewhereRules Elsewhere = new(
        new Regex(@"\b(conference|summit|expo)\b", RegexOptions.IgnoreCase), ["workingElsewhere"]);

    private static DayEvent E(string subject, string start, string end, bool allDay = false, string showAs = "busy",
        bool mine = true, int attendees = 0, params string[] categories) =>
        new(subject, DateTime.Parse(start), DateTime.Parse(end), allDay, showAs, categories, mine, attendees);

    private static readonly DateOnly Mon = new(2026, 3, 2);

    [Fact]
    public void Weekends()
    {
        Assert.False(DayRules.Weekend(Mon));
        Assert.True(DayRules.Weekend(Mon.AddDays(5)));
        Assert.True(DayRules.Weekend(Mon.AddDays(6)));
    }

    [Fact]
    public void A_leave_entry_over_the_whole_day_is_a_day_off()
    {
        var day = DayRules.Leave(Leave, [E("Holiday (Engineer)", "2026-03-02T00:00", "2026-03-06T23:59")], Mon);
        Assert.True(day.DayOff);
        Assert.Equal(24, day.Hours, 3);
    }

    [Fact]
    public void A_few_hours_of_leave_is_not_a_day_off_but_is_still_leave()
    {
        var day = DayRules.Leave(Leave, [E("Annual leave", "2026-03-02T13:00", "2026-03-02T17:00")], Mon);
        Assert.False(day.DayOff);
        Assert.Equal(4, day.Hours, 3);
        Assert.Single(day.Entries);
    }

    [Fact]
    public void Overlapping_leave_counts_once()
    {
        var day = DayRules.Leave(Leave, [E("Annual leave", "2026-03-02T09:00", "2026-03-02T13:00"),
                                         E("On leave", "2026-03-02T11:00", "2026-03-02T14:00")], Mon);
        Assert.Equal(5, day.Hours, 3);
        Assert.False(day.DayOff);
    }

    [Fact]
    public void Leave_by_category_and_all_day()
    {
        var day = DayRules.Leave(Leave, [E("Spring holiday", "2026-03-02T00:00", "2026-03-03T00:00", allDay: true, showAs: "free", categories: "Bank holiday")], Mon);
        Assert.True(day.DayOff);
    }

    [Fact]
    public void Someone_elses_meeting_or_an_invite_is_never_leave()
    {
        Assert.False(DayRules.Leave(Leave, [E("Holiday planning", "2026-03-02T00:00", "2026-03-03T00:00", mine: false)], Mon).DayOff);
        Assert.False(DayRules.Leave(Leave, [E("Holiday planning", "2026-03-02T00:00", "2026-03-03T00:00", attendees: 3)], Mon).DayOff);
    }

    [Fact]
    public void Elsewhere_by_all_day_subject_first_then_show_as()
    {
        var events = new[]
        {
            E("At the customer", "2026-03-02T09:00", "2026-03-02T11:00", showAs: "workingElsewhere"),
            E("Example Summit", "2026-03-02T00:00", "2026-03-03T00:00", allDay: true, showAs: "free"),
        };
        var day = DayRules.Elsewhere(Elsewhere, events, Mon);
        Assert.Equal("Example Summit", day!.Event.Subject);
        Assert.Equal("all-day \"Example Summit\"", day.Why);
    }

    [Fact]
    public void A_timed_event_with_a_conference_subject_counts_only_when_asked_for()
    {
        var events = new[] { E("Example Expo", "2026-03-02T08:00", "2026-03-02T17:00") };
        Assert.Null(DayRules.Elsewhere(Elsewhere, events, Mon));
        var day = DayRules.Elsewhere(Elsewhere with { SubjectsAllDayOnly = false, MinHours = 6 }, events, Mon);
        Assert.Equal("\"Example Expo\"", day!.Why);
    }

    [Fact]
    public void Show_as_signal_of_any_length_unless_a_minimum_is_set()
    {
        var events = new[] { E("Site visit", "2026-03-02T10:00", "2026-03-02T12:00", showAs: "workingElsewhere") };
        Assert.Equal("\"Site visit\" shown as workingElsewhere", DayRules.Elsewhere(Elsewhere, events, Mon)!.Why);
        Assert.Null(DayRules.Elsewhere(Elsewhere with { MinHours = 6 }, events, Mon));
    }

    [Fact]
    public void Not_a_signal_leaves_out_a_tools_own_entries()
    {
        var events = new[] { E("Working from the office", "2026-03-02T00:00", "2026-03-03T00:00", allDay: true, showAs: "workingElsewhere") };
        Assert.NotNull(DayRules.Elsewhere(Elsewhere, events, Mon));
        Assert.Null(DayRules.Elsewhere(Elsewhere, events, Mon, e => e.Subject == "Working from the office"));
    }

    [Fact]
    public void Covers_is_any_overlap()
    {
        Assert.True(DayRules.Covers(E("x", "2026-03-01T23:00", "2026-03-02T00:30"), Mon));
        Assert.False(DayRules.Covers(E("x", "2026-03-01T22:00", "2026-03-02T00:00"), Mon));
    }
}
