using System.Globalization;
using System.Text.RegularExpressions;
using Tomlyn.Model;

namespace WinBackground;

/// <summary>What a tool's task is: a watcher (started at logon and kept running, polling every_minutes itself) or a
/// job (run by the task on its triggers, then exits).</summary>
public enum TaskKind { Watcher, Job }

/// <summary>The shared scheduling settings: one <c>[schedule]</c> table, the same keys in every tool (GJ, 22 Nov 2026),
/// from which the tool's Windows task is built.
/// <code>
/// [schedule]
/// every_minutes = 15            # a job: how often the task runs it; a watcher: how often it polls (0 = not repeated)
/// daily_at = "07:30"            # a job: also (or instead) daily at this time; not with every_minutes
/// hours = "08:00-19:00"         # the active window: a run outside it does nothing ("00:00-24:00" = all day)
/// days = "Mon-Fri"              # the active days: "Mon-Fri", "Mon,Wed,Fri" or ["Mon", "Wed"] (default every day)
/// at_logon = true               # also at logon (default true; a watcher always starts at logon)
/// after_unlock_minutes = 0      # a job: also this many minutes after each unlock (0 = no unlock trigger, the default)
/// end_date = "2027-01-01"       # from this date a run does nothing (the task stays until `autostart off`)
/// </code>
/// <para><b>Each tool passes its own defaults</b>, so a tool with no <c>[schedule]</c> behaves as before. The table is
/// read strictly: an unknown key is an error.</para>
/// <para><b>The trigger keys</b> (every_minutes for a job, daily_at, at_logon, after_unlock_minutes) are fixed when the
/// task is registered; <see cref="Fingerprint"/> records them in the task's description, and
/// <see cref="ScheduleDrift"/> says when the settings no longer match it ("run autostart on"). <b>The gating keys</b>
/// (hours, days, end_date, and a watcher's every_minutes) are read by each run, so changing them needs no
/// re-registration.</para></summary>
public sealed record ScheduleSettings
{
    public static readonly IReadOnlyList<string> Keys =
        ["every_minutes", "daily_at", "hours", "days", "at_logon", "after_unlock_minutes", "end_date"];

    /// <summary>A job: the task's repetition. A watcher: its polling interval. 0 or null: not repeated.</summary>
    public int? EveryMinutes { get; init; }

    /// <summary>"HH:mm": a job also runs daily at this time.</summary>
    public string? DailyAt { get; init; }

    /// <summary>The active window, inclusive start, exclusive end; To = 24:00 is end of day. Null: all day. A window
    /// that wraps past midnight ("22:00-06:00") is allowed.</summary>
    public (TimeSpan From, TimeSpan To)? Hours { get; init; }

    /// <summary>The active days. Null: every day.</summary>
    public IReadOnlySet<DayOfWeek>? Days { get; init; }

    public bool AtLogon { get; init; } = true;

    /// <summary>Minutes after each unlock; 0 is no unlock trigger.</summary>
    public int AfterUnlockMinutes { get; init; }

    /// <summary>From this date a run does nothing.</summary>
    public DateOnly? EndDate { get; init; }

    /// <summary>Read a <c>[schedule]</c> table (null when the settings have none) over the tool's defaults. Throws
    /// FormatException naming <paramref name="where"/> for an unknown key or a bad value.</summary>
    public static ScheduleSettings Parse(TomlTable? table, ScheduleSettings defaults, TaskKind kind, string where = "[schedule]")
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var s = defaults;
        if (table is not null)
        {
            var unknown = table.Keys.Where(k => !Keys.Contains(k)).ToList();
            if (unknown.Count > 0)
                throw new FormatException($"{where}: unknown setting{(unknown.Count > 1 ? "s" : "")} {string.Join(", ", unknown)} (known: {string.Join(", ", Keys)}).");
            foreach (var (key, value) in table)
            {
                s = key switch
                {
                    "every_minutes" => s with { EveryMinutes = Int(value, key, where, 0, 1440) },
                    "daily_at" => s with { DailyAt = Time(value, key, where) },
                    "hours" => s with { Hours = ParseHours(Str(value, key, where), where) },
                    "days" => s with { Days = ParseDays(value, where) },
                    "at_logon" => s with { AtLogon = value is bool b ? b : throw new FormatException($"{where}: at_logon should be true or false.") },
                    "after_unlock_minutes" => s with { AfterUnlockMinutes = Int(value, key, where, 0, 1440) },
                    "end_date" => s with { EndDate = ParseDate(value, where) },
                    _ => s,
                };
            }
        }
        s.Validate(kind, where);
        return s;
    }

    /// <summary>The rules every tool shares. A job: every_minutes and daily_at are alternatives (set one; 0 counts as
    /// unset, as calendar-ev-charger-booking's "0: daily at daily_at" did), and it needs at least one trigger. A
    /// watcher: always at logon; daily_at and after_unlock_minutes don't apply.</summary>
    public void Validate(TaskKind kind, string where = "[schedule]")
    {
        var every = EveryMinutes is > 0;
        if (kind == TaskKind.Watcher)
        {
            if (!AtLogon)
                throw new FormatException($"{where}: a watcher starts at logon and keeps running, so at_logon can't be false.");
            if (DailyAt is not null)
                throw new FormatException($"{where}: daily_at is for a job; a watcher keeps running (its every_minutes is how often it polls).");
            if (AfterUnlockMinutes > 0)
                throw new FormatException($"{where}: after_unlock_minutes is for a job; a watcher keeps running.");
            return;
        }
        if (every && DailyAt is not null)
            throw new FormatException($"{where}: set every_minutes or daily_at, not both (every_minutes = 0 runs daily at daily_at).");
        if (!every && DailyAt is null && !AtLogon && AfterUnlockMinutes == 0)
            throw new FormatException($"{where}: the task would never run: set every_minutes, daily_at, at_logon or after_unlock_minutes.");
    }

    /// <summary>The task's triggers and limits. A job's time limit and whether to start it now are the tool's.</summary>
    public TaskSchedule ToTaskSchedule(TaskKind kind, TimeSpan? timeLimit = null, bool startNow = true)
    {
        Validate(kind);
        var fingerprint = Fingerprint(kind);
        if (kind == TaskKind.Watcher)
            return TaskSchedule.Watcher with { Fingerprint = fingerprint, StartNow = startNow };
        return new TaskSchedule
        {
            AtLogon = AtLogon,
            DailyAt = EveryMinutes is > 0 ? null : DailyAt,
            Every = EveryMinutes is > 0 and var m ? TimeSpan.FromMinutes(m) : null,
            AfterUnlock = AfterUnlockMinutes > 0 ? TimeSpan.FromMinutes(AfterUnlockMinutes) : null,
            KeepRunning = false,
            TimeLimit = timeLimit ?? TimeSpan.FromMinutes(30),
            StartNow = startNow,
            Fingerprint = fingerprint,
        };
    }

    /// <summary>The trigger-relevant settings in one readable line, stored in the registered task's description so a
    /// change can be spotted ("v1 job every=15 daily=- logon=yes unlock=0"). Gating keys are left out: each run reads
    /// them.</summary>
    public string Fingerprint(TaskKind kind) => kind == TaskKind.Watcher
        ? "v1 watcher logon=yes"
        : string.Create(CultureInfo.InvariantCulture,
            $"v1 job every={(EveryMinutes is > 0 and var m ? m : 0)} daily={(EveryMinutes is > 0 ? "-" : DailyAt ?? "-")} logon={(AtLogon ? "yes" : "no")} unlock={AfterUnlockMinutes}");

    /// <summary>Whether a run at <paramref name="now"/> (local time) should do its work: inside hours and days, and
    /// before end_date. <paramref name="why"/> says why not, for the log.</summary>
    public bool IsActive(DateTime now, out string? why)
    {
        if (EndDate is { } end && DateOnly.FromDateTime(now) >= end)
        {
            why = $"past end_date ({end:yyyy-MM-dd})";
            return false;
        }
        if (Days is { } days && !days.Contains(now.DayOfWeek))
        {
            why = $"not one of the days ({now:ddd})";
            return false;
        }
        if (Hours is var (from, to))
        {
            var t = now.TimeOfDay;
            var inside = from <= to ? t >= from && t < to : t >= from || t < to;
            if (!inside)
            {
                why = $"outside the hours ({Clock(from)}-{Clock(to)})";
                return false;
            }
        }
        why = null;
        return true;
    }

    public bool IsActive(DateTime now) => IsActive(now, out _);

    private static string Clock(TimeSpan t) => t.TotalHours >= 24 ? "24:00" : $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    private static string Str(object? value, string key, string where) =>
        value as string ?? throw new FormatException($"{where}: {key} should be a string.");

    private static int Int(object? value, string key, string where, int min, int max)
    {
        if (value is not long n || n < min || n > max)
            throw new FormatException($"{where}: {key} should be a whole number from {min} to {max}.");
        return (int)n;
    }

    private static string Time(object? value, string key, string where)
    {
        var text = Str(value, key, where).Trim();
        if (!TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            throw new FormatException($"{where}: {key} should be a time such as \"07:30\".");
        return t.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>"HH:MM-HH:MM"; the end may be 24:00.</summary>
    public static (TimeSpan From, TimeSpan To) ParseHours(string text, string where = "[schedule]")
    {
        var m = Regex.Match(text.Trim(), @"^(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})$");
        TimeSpan Part(int h, int min) => new(h, min, 0);
        if (m.Success)
        {
            int h1 = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m1 = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int h2 = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), m2 = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            var fromOk = h1 < 24 && m1 < 60;
            var toOk = (h2 < 24 && m2 < 60) || (h2 == 24 && m2 == 0);
            if (fromOk && toOk && (h1, m1) != (h2, m2))
                return (Part(h1, m1), Part(h2, m2));
        }
        throw new FormatException($"{where}: hours should look like \"08:00-19:00\" (\"00:00-24:00\" for all day).");
    }

    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>"Mon-Fri", "Mon,Wed,Fri", or a list of day names (three letters or in full, any case).</summary>
    public static IReadOnlySet<DayOfWeek> ParseDays(object? value, string where = "[schedule]")
    {
        IEnumerable<string> parts = value switch
        {
            string s => s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            TomlArray a => a.Select(x => x as string ?? throw new FormatException($"{where}: days should be day names.")),
            _ => throw new FormatException($"{where}: days should be \"Mon-Fri\", \"Mon,Wed,Fri\" or a list of day names."),
        };
        var days = new HashSet<DayOfWeek>();
        foreach (var part in parts)
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1)
                days.Add(Day(range[0], where));
            else if (range.Length == 2)
            {
                var (a, b) = ((int)Day(range[0], where), (int)Day(range[1], where));
                for (var d = a; ; d = (d + 1) % 7)
                {
                    days.Add((DayOfWeek)d);
                    if (d == b)
                        break;
                }
            }
            else
                throw new FormatException($"{where}: days: \"{part}\" isn't a day or a range such as Mon-Fri.");
        }
        if (days.Count == 0)
            throw new FormatException($"{where}: days is empty.");
        return days;
    }

    private static DayOfWeek Day(string name, string where)
    {
        var key = name.Trim().ToLowerInvariant();
        var i = key.Length >= 3 ? Array.IndexOf(DayNames, key[..3]) : -1;
        if (i < 0 || !CultureInfo.InvariantCulture.DateTimeFormat.DayNames[i].StartsWith(name.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"{where}: days: \"{name}\" isn't a day name.");
        return (DayOfWeek)i;
    }

    private static DateOnly ParseDate(object? value, string where) => value switch
    {
        string s when DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        Tomlyn.TomlDateTime t => DateOnly.FromDateTime(t.DateTime.DateTime),
        _ => throw new FormatException($"{where}: end_date should be a date such as \"2027-01-01\"."),
    };
}

/// <summary>Whether a registered task still matches its tool's <c>[schedule]</c>.</summary>
public enum DriftState
{
    /// <summary>No task is registered.</summary>
    NotRegistered,
    /// <summary>The task's recorded fingerprint matches the settings.</summary>
    Matches,
    /// <summary>The settings changed since the task was registered: run autostart on.</summary>
    Changed,
    /// <summary>The task was registered before fingerprints were recorded, so it can't be compared: autostart on
    /// records one.</summary>
    Unknown,
}

/// <summary>Spotting a task that no longer matches its settings. The fingerprint is kept in the task's description as
/// "(schedule: v1 job every=15 ...)"; <see cref="ITask.Status"/> returns the description.</summary>
public static class ScheduleDrift
{
    private static readonly Regex Marker = new(@"\(schedule: ([^)]*)\)", RegexOptions.CultureInvariant);

    /// <summary>The description with the fingerprint appended (replacing any earlier one).</summary>
    public static string WithFingerprint(string description, string fingerprint) =>
        $"{Marker.Replace(description ?? "", "").TrimEnd()} (schedule: {fingerprint})".TrimStart();

    /// <summary>The fingerprint recorded in a description, or null.</summary>
    public static string? FromDescription(string? description) =>
        description is not null && Marker.Match(description) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Compare a task's status (from <see cref="ITask.Status"/>; null when there's no task) with a fingerprint.</summary>
    public static DriftState Check(IReadOnlyDictionary<string, string>? status, string fingerprint)
    {
        if (status is null)
            return DriftState.NotRegistered;
        var recorded = FromDescription(status.TryGetValue("description", out var d) ? d : null);
        return recorded is null ? DriftState.Unknown : recorded == fingerprint ? DriftState.Matches : DriftState.Changed;
    }

    /// <summary>Check a task against its tool's schedule settings.</summary>
    public static DriftState Check(ITask task, ScheduleSettings settings, TaskKind kind)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(settings);
        return Check(task.Status(), settings.Fingerprint(kind));
    }

    /// <summary>The line for autostart status, or null when there's nothing to say.</summary>
    public static string? Advice(DriftState state, string tool) => state switch
    {
        DriftState.Changed => $"settings changed since this task was registered: run `{tool} autostart on`",
        DriftState.Unknown => $"registered before its schedule was recorded: run `{tool} autostart on` to record it",
        _ => null,
    };
}
