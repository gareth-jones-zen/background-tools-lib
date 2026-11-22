using CalendarDays;
using Tomlyn.Model;

namespace CalendarDays.Tests;

public sealed class LeaveSettingsTests
{
    private static TomlTable Leave(string toml) => (TomlTable)PyCompat.PyToml.Read(toml, "x.toml")["leave"];

    [Fact]
    public void Defaults_are_the_tools_common_ones()
    {
        var s = LeaveSettings.Parse(null, "x.toml");
        Assert.Equal(LeaveSettings.DefaultSubjects, s.Subjects.ToString());
        Assert.Equal(LeaveSettings.DefaultHalfDay, s.HalfDay.ToString());
        Assert.Equal(LeaveSettings.DefaultBankHolidaySubjects, s.BankHolidaySubjects.ToString());
        Assert.Equal(["Bank holiday"], s.BankHolidayCategories);
        Assert.Empty(s.Categories);
        Assert.Equal(6, s.MinHours);
        Assert.Matches(s.Subjects, "Holiday (Principal Solutions Architect)");
        Assert.Matches(s.Subjects, "summer holidays; Cornwall");
        Assert.DoesNotMatch(s.Subjects, "Bank holiday");                 // a bank holiday is its own kind, not leave
        Assert.Matches(s.BankHolidaySubjects, "Bank holiday - Christmas Day");
    }

    [Fact]
    public void Reads_a_table_with_the_tools_own_keys_beside_the_shared_ones()
    {
        var table = Leave("""
            [leave]
            min_working_days = 5
            subjects = '^\s*(holiday\b|annual leave)'
            half_day = '\(half\)'
            min_hours = 4.5
            categories = ["Away"]
            bank_holiday_categories = ["Bank holiday", "Holiday"]
            """);
        var s = LeaveSettings.Parse(table, "meeting-preps.toml", ["min_working_days", "window_days"]);
        Assert.Equal(@"^\s*(holiday\b|annual leave)", s.Subjects.ToString());
        Assert.Equal(@"\(half\)", s.HalfDay.ToString());
        Assert.Equal(4.5, s.MinHours);
        Assert.Equal(["Away"], s.Categories);
        Assert.Equal(["Bank holiday", "Holiday"], s.BankHolidayCategories);
        Assert.True(s.Subjects.Options.HasFlag(System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public void A_tool_can_pass_its_own_defaults()
    {
        var mine = new LeaveSettings { Categories = ["Holiday"] };
        Assert.Equal(["Holiday"], LeaveSettings.Parse(Leave("[leave]\nmin_hours = 6"), "w.toml", defaults: mine).Categories);
    }

    [Theory]
    [InlineData("[leave]\nwhatever = 1", "unknown setting 'whatever'")]
    [InlineData("[leave]\nmin_working_days = 5", "unknown setting 'min_working_days'")]   // not named by this tool
    [InlineData("[leave]\nsubjects = 3", "subjects should be text")]
    [InlineData("[leave]\nsubjects = '('", "isn't a valid regular expression")]
    [InlineData("[leave]\nmin_hours = 25", "min_hours should be a number of hours")]
    [InlineData("[leave]\nmin_hours = 'six'", "min_hours should be a number of hours")]
    [InlineData("[leave]\ncategories = 'Bank holiday'", "categories should be a list of names")]
    [InlineData("[leave]\nbank_holiday_categories = [1]", "bank_holiday_categories should be a list of names")]
    public void Strict_about_keys_and_types(string toml, string message)
    {
        var error = Assert.Throws<FormatException>(() => LeaveSettings.Parse(Leave(toml), "x.toml"));
        Assert.Contains(message, error.Message);
        Assert.StartsWith("x.toml: ", error.Message);
    }
}

public sealed class DayOffTests
{
    private static readonly LeaveSettings Settings = new();
    private static readonly DateOnly Wed = new(2026, 12, 9);

    private static DayEvent E(string subject, string start, string end, bool allDay = false, bool mine = true, int attendees = 0,
        params string[] categories) =>
        new(subject, DateTime.Parse(start), DateTime.Parse(end), allDay, "busy", categories, mine, attendees);

    [Fact]
    public void A_bank_holiday_by_category_or_subject_comes_first()
    {
        var byCategory = DayOffRules.Classify(Settings, [E("Christmas Day", "2026-12-25T00:00", "2026-12-26T00:00", true, categories: "Bank holiday"),
                                                         E("Holiday (Architect)", "2026-12-16T00:00", "2026-12-31T23:59")], new(2026, 12, 25));
        Assert.Equal(DayOffKind.BankHoliday, byCategory.Kind);
        Assert.Equal(1, byCategory.Share);
        var bySubject = DayOffRules.Classify(Settings, [E("Bank holiday", "2026-12-28T09:00", "2026-12-28T17:00")], new(2026, 12, 28));
        Assert.Equal(DayOffKind.BankHoliday, bySubject.Kind);
        var someoneElses = DayOffRules.Classify(Settings, [E("Bank holiday", "2026-12-28T09:00", "2026-12-28T17:00", mine: false)], new(2026, 12, 28));
        Assert.Equal(DayOffKind.None, someoneElses.Kind);
    }

    [Fact]
    public void People_firsts_full_day_is_leave()
    {
        var day = DayOffRules.Classify(Settings, [E("Holiday (Principal Solutions Architect)", "2026-12-16T00:00", "2026-12-31T23:59")], new(2026, 12, 17));
        Assert.Equal(DayOffKind.Leave, day.Kind);
        Assert.Null(day.Half);
    }

    [Theory]
    [InlineData("Holiday (half day) (Architect)", "2026-12-09T00:00", "2026-12-09T23:59", HalfOfDay.Unknown)]   // People First doesn't say
    [InlineData("Holiday (half day) (Architect)", "2026-12-09T12:00", "2026-12-09T23:59", HalfOfDay.Pm)]        // its hours do
    [InlineData("Holiday (half day) (Architect)", "2026-12-09T08:00", "2026-12-09T12:00", HalfOfDay.Am)]
    [InlineData("Annual leave", "2026-12-09T13:00", "2026-12-09T17:00", HalfOfDay.Pm)]                           // under min_hours
    [InlineData("Annual leave AM", "2026-12-09T00:00", "2026-12-09T23:59", HalfOfDay.Am)]                        // the title says (but 24h: leave)
    [InlineData("Annual leave (PM) (half day)", "2026-12-09T00:00", "2026-12-09T23:59", HalfOfDay.Pm)]
    [InlineData("Annual leave half day - afternoon (half day)", "2026-12-09T00:00", "2026-12-09T23:59", HalfOfDay.Pm)]
    [InlineData("Annual leave - morning (half day)", "2026-12-09T00:00", "2026-12-09T23:59", HalfOfDay.Am)]
    [InlineData("Annual leave", "2026-12-09T10:00", "2026-12-09T14:00", HalfOfDay.Unknown)]                      // across midday
    public void A_half_day_says_which_half_where_it_can(string subject, string start, string end, HalfOfDay half)
    {
        var day = DayOffRules.Classify(Settings, [E(subject, start, end)], Wed);
        if (subject == "Annual leave AM")
        {
            Assert.Equal(DayOffKind.Leave, day.Kind);   // 24 hours without "(half day)": a day off, as before
            Assert.Equal(HalfOfDay.Am, DayOffRules.HalfOf(day.Leave!.Entries, Wed, DayOffRules.Midday));
            return;
        }
        Assert.Equal(DayOffKind.HalfDay, day.Kind);
        Assert.Equal(0.5, day.Share);
        Assert.Equal(half, day.Half);
    }

    [Fact]
    public void From_and_to_halves_apply_to_the_first_and_last_days()
    {
        var e = E("Annual leave (from PM) (to AM)", "2026-12-07T00:00", "2026-12-10T00:00", allDay: true);
        Assert.Equal(HalfOfDay.Pm, DayOffRules.HalfOf(e, new(2026, 12, 7), DayOffRules.Midday));
        Assert.Equal(HalfOfDay.Unknown, DayOffRules.HalfOf(e, new(2026, 12, 8), DayOffRules.Midday));
        Assert.Equal(HalfOfDay.Am, DayOffRules.HalfOf(e, new(2026, 12, 9), DayOffRules.Midday));
    }

    [Fact]
    public void A_morning_and_an_afternoon_together_cant_be_one_half()
    {
        Assert.Equal(HalfOfDay.Unknown, DayOffRules.HalfOf([E("Annual leave AM", "2026-12-09T08:00", "2026-12-09T12:00"),
                                                            E("Annual leave PM", "2026-12-09T13:00", "2026-12-09T17:00")], Wed, DayOffRules.Midday));
    }

    [Fact]
    public void Classify_agrees_with_the_rule_out_of_office_and_meeting_preps_use()
    {
        // their rule: bank holiday entry first; else leave, a half day when every entry says half day or it isn't a day off
        List<DayEvent> events =
        [
            E("Holiday (Architect)", "2026-12-16T00:00", "2026-12-31T23:59"),
            E("Holiday (half day) (Architect)", "2026-12-14T00:00", "2026-12-14T23:59"),
            E("Annual leave", "2026-12-11T13:00", "2026-12-11T16:00"),
            E("Christmas Day", "2026-12-25T00:00", "2026-12-26T00:00", true, categories: "Bank holiday"),
            E("Holiday", "2026-12-10T09:00", "2026-12-10T17:00", mine: true, attendees: 2),   // a meeting called Holiday: not leave
        ];
        for (var d = new DateOnly(2026, 12, 7); d <= new DateOnly(2026, 12, 31); d = d.AddDays(1))
        {
            var bank = events.Any(e => e.Mine && e.Attendees == 0 && DayRules.Covers(e, d) && (Settings.BankHolidaySubjects.IsMatch(e.Subject)
                                         || e.Categories.Any(c => Settings.BankHolidayCategories.Contains(c, StringComparer.OrdinalIgnoreCase))));
            var leave = DayRules.Leave(Settings.Rules, events, d);
            var expected = bank ? DayOffKind.BankHoliday
                : leave.Entries.Count == 0 ? DayOffKind.None
                : leave.Entries.All(e => Settings.HalfDay.IsMatch(e.Subject)) || !leave.DayOff ? DayOffKind.HalfDay
                : DayOffKind.Leave;
            Assert.Equal(expected, DayOffRules.Classify(Settings, events, d).Kind);
        }
    }
}

public sealed class ReportLeaveTitlesTests
{
    // What calendar-team-leave-sync writes ([types] "{name} annual leave", the default [titles]), from its Absence.Title
    // and its golden scenarios: single day full / AM / PM, longer from PM, to AM, both.
    [Theory]
    [InlineData("2026-12-02", "2026-12-02", "FULL", "FULL", "Dan Clark annual leave")]
    [InlineData("2026-12-02", "2026-12-02", "HALF_AM", "HALF_AM", "Dan Clark annual leave (AM)")]
    [InlineData("2026-12-02", "2026-12-02", "HALF_PM", "HALF_PM", "Dan Clark annual leave (PM)")]
    [InlineData("2026-12-02", "2026-12-02", "HALF_AM", "HALF_PM", "Dan Clark annual leave")]          // both halves: the day
    [InlineData("2026-12-02", "2026-12-02", "FULL", "HALF_PM", "Dan Clark annual leave (PM)")]
    [InlineData("2026-12-17", "2026-12-18", "HALF_PM", "FULL", "Dan Clark annual leave (from PM)")]
    [InlineData("2026-12-17", "2026-12-18", "FULL", "HALF_AM", "Dan Clark annual leave (to AM)")]
    [InlineData("2026-12-17", "2026-12-21", "HALF_PM", "HALF_AM", "Dan Clark annual leave (from PM) (to AM)")]
    [InlineData("2026-12-17", "2026-12-21", "HALF_AM", "HALF_PM", "Dan Clark annual leave")]          // AM start, PM end: whole days
    public void Formats_titles_as_team_leave_writes_them(string start, string end, string startPart, string endPart, string title)
    {
        var halves = ReportLeaveTitles.Halves(DateOnly.Parse(start), DateOnly.Parse(end), startPart, endPart);
        Assert.Equal(title, ReportLeaveTitles.Format(ReportLeaveTitles.DefaultTitle, "Dan Clark", halves));
    }

    [Fact]
    public void Formats_with_other_templates_and_marks()
    {
        var f = new ReportLeaveFormat(Am: "{title} - morning", Pm: "{title} - afternoon", FromPm: "{title} from midday", ToAm: "{{to AM}} {title}");
        Assert.Equal("Dan Clark on leave - afternoon", ReportLeaveTitles.Format("{name} on leave", "Dan Clark", (ReportLeaveTitles.Pm, null, null), f));
        Assert.Equal("{to AM} Dan Clark leave from midday", ReportLeaveTitles.Format("{name} leave", "Dan Clark", (null, "PM", "AM"), f));
        Assert.Throws<FormatException>(() => ReportLeaveTitles.Format("{who} leave", "Dan", (null, null, null)));
    }

    // What mail-triage reads ([one_to_ones] leave_pattern's default), from its OneToOnesTests.
    [Theory]
    [InlineData("Jordan Lee annual leave", "annual leave", null, null, null)]
    [InlineData("Jordan Lee on leave", "on leave", null, null, null)]
    [InlineData("Jordan Lee on leave PM", "on leave", "PM", null, null)]
    [InlineData("Jordan Lee annual leave (PM)", "annual leave", "PM", null, null)]
    [InlineData("Jordan Lee annual leave AM", "annual leave", "AM", null, null)]
    [InlineData("Jordan Lee annual leave half day", "annual leave", "half", null, null)]
    [InlineData("Jordan Lee annual leave (from PM)", "annual leave", null, "PM", null)]
    [InlineData("Jordan Lee annual leave (to AM)", "annual leave", null, null, "AM")]
    [InlineData("Jordan Lee annual leave (from PM) (to AM)", "annual leave", null, "PM", "AM")]
    [InlineData("jordan lee ANNUAL LEAVE ( from  pm )", "ANNUAL LEAVE", null, "PM", null)]
    public void Parses_titles_as_mail_triage_reads_them(string subject, string kind, string? each, string? first, string? last)
    {
        Assert.Equal(new ReportLeaveTitle("Jordan Lee", kind, each, first, last), ReportLeaveTitles.Parse(subject, "Jordan Lee"));
    }

    [Theory]
    [InlineData("Robin Hall annual leave")]
    [InlineData("Jordan Leeds annual leave")]
    [InlineData("Re: Jordan Lee annual leave")]
    [InlineData("Jordan Lee annual leaves")]
    public void Not_their_leave(string subject) => Assert.Null(ReportLeaveTitles.Parse(subject, "Jordan Lee"));

    [Fact]
    public void Every_title_team_leave_writes_reads_back_with_the_same_halves()
    {
        string[] parts = ["FULL", "HALF_AM", "HALF_PM"];
        foreach (var days in new[] { 0, 1, 4 })
            foreach (var s in parts)
                foreach (var e in parts)
                {
                    var start = new DateOnly(2026, 12, 14);
                    var halves = ReportLeaveTitles.Halves(start, start.AddDays(days), s, e);
                    var read = ReportLeaveTitles.Parse(ReportLeaveTitles.Format(ReportLeaveTitles.DefaultTitle, "Dan Clark", halves), "Dan Clark")!;
                    Assert.Equal((halves.Single, halves.First, halves.Last), (read.Each, read.First, read.Last));
                }
    }

    [Fact]
    public void A_pattern_needs_the_name() =>
        Assert.Throws<ArgumentException>(() => ReportLeaveTitles.Parse("x", "Dan", "^annual leave"));
}
