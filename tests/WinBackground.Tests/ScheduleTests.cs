using PyCompat;
using Tomlyn.Model;
using WinBackground;

namespace WinBackground.Tests;

/// <summary>v0.6 (22 Nov 2026): the shared [schedule] table, the triggers built from it, and drift.</summary>
public class ScheduleTests
{
    private static TomlTable Table(string toml) => (TomlTable)PyToml.Read(toml, "test.toml")["schedule"];

    private static readonly ScheduleSettings Job15 = new() { EveryMinutes = 15 };
    private static readonly ScheduleSettings WatcherDefaults = new() { EveryMinutes = 5 };

    private static string Script(ScheduleSettings s, TaskKind kind) =>
        new ScheduledTask("t", "My tool").EnableScript(@"C:\x\tool.exe", ["run"], @"C:\x", s.ToTaskSchedule(kind));

    [Fact]
    public void Parses_every_key()
    {
        var s = ScheduleSettings.Parse(Table("""
            [schedule]
            every_minutes = 60
            hours = "08:00-19:00"
            days = "Mon-Fri"
            at_logon = false
            after_unlock_minutes = 2
            end_date = "2027-01-01"
            """), Job15, TaskKind.Job);
        Assert.Equal(60, s.EveryMinutes);
        Assert.Equal((TimeSpan.FromHours(8), TimeSpan.FromHours(19)), s.Hours);
        Assert.Equal(5, s.Days!.Count);
        Assert.DoesNotContain(DayOfWeek.Saturday, s.Days);
        Assert.False(s.AtLogon);
        Assert.Equal(2, s.AfterUnlockMinutes);
        Assert.Equal(new DateOnly(2027, 1, 1), s.EndDate);
    }

    [Fact]
    public void No_table_keeps_the_tools_defaults()
    {
        var defaults = new ScheduleSettings { DailyAt = "07:30" };
        Assert.Same(defaults, ScheduleSettings.Parse(null, defaults, TaskKind.Job));
    }

    [Fact]
    public void Days_take_lists_ranges_full_names_and_a_toml_date()
    {
        Assert.Equal(3, ScheduleSettings.Parse(Table("[schedule]\ndays = [\"Monday\", \"wed\", \"FRI\"]"), Job15, TaskKind.Job).Days!.Count);
        Assert.Equal(3, ScheduleSettings.ParseDays("Fri-Sun").Count);
        Assert.Equal(2, ScheduleSettings.ParseDays("Mon, Thu").Count);
        Assert.Equal(new DateOnly(2027, 1, 1), ScheduleSettings.Parse(Table("[schedule]\nend_date = 2027-01-01"), Job15, TaskKind.Job).EndDate);
    }

    [Theory]
    [InlineData("[schedule]\nevery = 15", "unknown setting every")]
    [InlineData("[schedule]\nevery_minutes = -1", "every_minutes should be a whole number")]
    [InlineData("[schedule]\nevery_minutes = \"15\"", "every_minutes should be a whole number")]
    [InlineData("[schedule]\ndaily_at = \"7.30\"", "daily_at should be a time")]
    [InlineData("[schedule]\nhours = \"8-19\"", "hours should look like")]
    [InlineData("[schedule]\nhours = \"25:00-26:00\"", "hours should look like")]
    [InlineData("[schedule]\ndays = \"Funday\"", "isn't a day name")]
    [InlineData("[schedule]\nat_logon = \"yes\"", "at_logon should be true or false")]
    [InlineData("[schedule]\nend_date = \"1 Jan 2027\"", "end_date should be a date")]
    [InlineData("[schedule]\nevery_minutes = 15\ndaily_at = \"07:30\"", "not both")]
    [InlineData("[schedule]\nevery_minutes = 0\nat_logon = false", "would never run")]
    public void Bad_values_are_errors(string toml, string message)
    {
        var error = Assert.Throws<FormatException>(() => ScheduleSettings.Parse(Table(toml), Job15, TaskKind.Job, "tool.toml [schedule]"));
        Assert.Contains(message, error.Message);
        Assert.Contains("tool.toml [schedule]", error.Message);
    }

    [Theory]
    [InlineData("[schedule]\ndaily_at = \"07:30\"")]
    [InlineData("[schedule]\nafter_unlock_minutes = 1")]
    [InlineData("[schedule]\nat_logon = false")]
    public void A_watcher_only_starts_at_logon(string toml) =>
        Assert.Throws<FormatException>(() => ScheduleSettings.Parse(Table(toml), WatcherDefaults, TaskKind.Watcher));

    [Fact]
    public void Every_minutes_zero_means_daily_at_as_ev_charger_booking_did()
    {
        var s = ScheduleSettings.Parse(Table("[schedule]\nevery_minutes = 0\ndaily_at = \"09:00\""), Job15, TaskKind.Job);
        var t = s.ToTaskSchedule(TaskKind.Job);
        Assert.Null(t.Every);
        Assert.Equal("09:00", t.DailyAt);
    }

    [Fact]
    public void A_watcher_is_logon_only_and_kept_running()
    {
        var script = Script(ScheduleSettings.Parse(Table("[schedule]\nevery_minutes = 5\nhours = \"00:00-24:00\""), WatcherDefaults, TaskKind.Watcher), TaskKind.Watcher);
        Assert.Contains("-AtLogOn", script);
        Assert.DoesNotContain("-RepetitionInterval", script);
        Assert.DoesNotContain("-Daily", script);
        Assert.DoesNotContain("StateChange = 8", script);
        Assert.Contains("-RestartCount 3", script);
        Assert.Contains("(schedule: v1 watcher logon=yes)", script);
    }

    [Fact]
    public void A_job_every_15_minutes_and_at_logon()
    {
        var script = Script(Job15, TaskKind.Job);
        Assert.Contains("-AtLogOn", script);
        Assert.Contains("-RepetitionInterval (New-TimeSpan -Seconds 900)", script);
        Assert.DoesNotContain("StateChange = 8", script);
        Assert.Contains("-ExecutionTimeLimit (New-TimeSpan -Seconds 1800)", script);
        Assert.Contains("'My tool (schedule: v1 job every=15 daily=- logon=yes unlock=0)'", script);
    }

    [Fact]
    public void A_daily_job_at_07_30_and_logon()
    {
        var script = Script(new ScheduleSettings { DailyAt = "07:30" }, TaskKind.Job);
        Assert.Contains("-Daily -At '07:30'", script);
        Assert.Contains("-AtLogOn", script);
        Assert.DoesNotContain("-RepetitionInterval", script);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void After_unlock_zero_is_no_unlock_trigger(int minutes, bool trigger)
    {
        var script = Script(Job15 with { AfterUnlockMinutes = minutes }, TaskKind.Job);
        Assert.Equal(trigger, script.Contains("StateChange = 8"));
        if (trigger)
            Assert.Contains("$t.Delay = 'PT1M'", script);
    }

    [Fact]
    public void End_date_hours_and_days_gate_a_run_but_not_the_triggers()
    {
        var s = new ScheduleSettings
        {
            DailyAt = "07:30",
            EndDate = new DateOnly(2027, 1, 1),
            Hours = (TimeSpan.FromHours(8), TimeSpan.FromHours(19)),
            Days = ScheduleSettings.ParseDays("Mon-Fri"),
        };
        Assert.True(s.IsActive(new DateTime(2026, 12, 31, 9, 0, 0)));                       // a Thursday
        Assert.False(s.IsActive(new DateTime(2027, 1, 1, 9, 0, 0), out var why));
        Assert.Contains("end_date", why);
        Assert.False(s.IsActive(new DateTime(2026, 12, 26, 9, 0, 0), out why));              // a Saturday
        Assert.Contains("days", why);
        Assert.False(s.IsActive(new DateTime(2026, 12, 31, 19, 0, 0), out why));
        Assert.Contains("08:00-19:00", why);
        Assert.Equal(s.Fingerprint(TaskKind.Job), (s with { EndDate = null, Hours = null, Days = null }).Fingerprint(TaskKind.Job));
    }

    [Fact]
    public void Hours_can_be_all_day_or_wrap_past_midnight()
    {
        var all = new ScheduleSettings { Hours = ScheduleSettings.ParseHours("00:00-24:00") };
        Assert.True(all.IsActive(new DateTime(2026, 11, 22, 23, 59, 0)));
        var night = new ScheduleSettings { Hours = ScheduleSettings.ParseHours("22:00-06:00") };
        Assert.True(night.IsActive(new DateTime(2026, 11, 22, 23, 0, 0)));
        Assert.True(night.IsActive(new DateTime(2026, 11, 22, 5, 0, 0)));
        Assert.False(night.IsActive(new DateTime(2026, 11, 22, 12, 0, 0)));
    }

    [Fact]
    public void Drift_matches_changes_and_unknowns()
    {
        var fp = Job15.Fingerprint(TaskKind.Job);
        var registered = new Dictionary<string, string> { ["state"] = "Ready", ["description"] = ScheduleDrift.WithFingerprint("My tool", fp) };
        Assert.Equal(DriftState.Matches, ScheduleDrift.Check(registered, fp));
        Assert.Equal(DriftState.Changed, ScheduleDrift.Check(registered, (Job15 with { EveryMinutes = 60 }).Fingerprint(TaskKind.Job)));
        Assert.Equal(DriftState.Changed, ScheduleDrift.Check(registered, (Job15 with { AfterUnlockMinutes = 1 }).Fingerprint(TaskKind.Job)));
        Assert.Equal(DriftState.Unknown, ScheduleDrift.Check(new Dictionary<string, string> { ["description"] = "My tool" }, fp));
        Assert.Equal(DriftState.NotRegistered, ScheduleDrift.Check((IReadOnlyDictionary<string, string>?)null, fp));
        Assert.Contains("autostart on", ScheduleDrift.Advice(DriftState.Changed, "my-tool"));
        Assert.Null(ScheduleDrift.Advice(DriftState.Matches, "my-tool"));
    }

    [Fact]
    public void Re_registering_replaces_the_fingerprint()
    {
        var once = ScheduleDrift.WithFingerprint("My tool", "v1 job every=15 daily=- logon=yes unlock=1");
        var twice = ScheduleDrift.WithFingerprint(once, "v1 job every=60 daily=- logon=yes unlock=0");
        Assert.Equal("My tool (schedule: v1 job every=60 daily=- logon=yes unlock=0)", twice);
        Assert.Equal("v1 job every=60 daily=- logon=yes unlock=0", ScheduleDrift.FromDescription(twice));
    }

    [Fact]
    public void Status_reads_the_description_and_a_schedule_without_a_fingerprint_is_unchanged()
    {
        var info = new ScheduledTask("t").ParseStatus("state=Ready\ndescription=My tool (schedule: v1 watcher logon=yes)\n");
        Assert.Equal("v1 watcher logon=yes", ScheduleDrift.FromDescription(info!["description"]));
        var plain = new ScheduledTask("t", "My tool").EnableScript("x", [], null, new TaskSchedule { Every = TimeSpan.FromMinutes(15), KeepRunning = false });
        Assert.Contains("-Description 'My tool' -Force", plain);
    }
}
