using PyCompat;
using WinBackground;

namespace ClaudeHeadless;

/// <summary>claude-headless: the seat's queue of Claude Code jobs waiting out a usage limit.
///
///     claude-headless status          the limit, if one is in force, and what's queued
///     claude-headless run-queue       run queued jobs one at a time, until done or the limit is back
///     claude-headless autostart on    run the queue every 15 minutes (Task Scheduler); off, status
///
/// Other tools (meeting-summaries) pass their own `autostart` and `run-queue` here, because the queue belongs to
/// the seat, not to any one tool.</summary>
public static class HeadlessCli
{
    public const string TaskName = "claude-headless run-queue";
    public const string TaskDescription = "Runs Claude Code jobs that waited out the usage limit (claude-headless run-queue).";

    public const string Usage = """
        usage: claude-headless [-h] [--version] {status,run-queue,autostart} ...

        claude-headless: the seat's queue of Claude Code jobs waiting out a usage limit.

            claude-headless status          the limit, if one is in force, and what's queued
            claude-headless run-queue       run queued jobs one at a time, until done or the limit is back
            claude-headless autostart on    run the queue every 15 minutes (Task Scheduler); off, status
        """;

    /// <summary>Runs a command; returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args, HeadlessHost host)
    {
        var rest = args.ToList();
        if (rest.Count == 0 || rest[0] is "-h" or "--help")
        {
            (rest.Count == 0 ? host.Err : host.Out).WriteLine(Usage);
            return rest.Count == 0 ? 2 : 0;
        }
        if (rest[0] == "--version")
        {
            host.Out.WriteLine(host.Version);
            return 0;
        }
        var queue = host.Queue;
        switch (rest[0])
        {
            case "status":
                if (rest.Count != 1)
                    return Bad(host, $"unrecognized arguments: {string.Join(' ', rest.Skip(1))}");
                var reset = queue.LimitUntil();
                host.Out.WriteLine(reset is { } r
                    ? $"Usage limit in force until {PyTime.Day(r)} {Limits.Clock(r)}."
                    : "No usage limit in force.");
                var items = queue.Pending();
                host.Out.WriteLine($"{items.Count} job(s) queued" + (items.Count > 0 ? ":" : "."));
                foreach (var item in items)
                    host.Out.WriteLine($"  {PyStr.Head(item.Added, 16)}  {PyStr.Head(string.Join(' ', item.Argv), 150)}");
                return 0;
            case "run-queue":
                if (rest.Count != 1)
                    return Bad(host, $"unrecognized arguments: {string.Join(' ', rest.Skip(1))}");
                var ran = queue.Run(Logger.Create(queue.LogFile, host.LogEcho), runner: host.JobRunner);
                host.Out.WriteLine($"{ran} job(s) run; {queue.Pending().Count} still queued.");
                return 0;
            case "autostart":
                return Autostart(rest.Skip(1).ToList(), host);
            default:
                return Bad(host, $"argument command: invalid choice: '{rest[0]}' (choose from 'status', 'run-queue', 'autostart')");
        }
    }

    private static int Autostart(List<string> args, HeadlessHost host)
    {
        var every = 15;
        string? action = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--every" && i + 1 < args.Count && int.TryParse(args[i + 1], out var minutes))
            {
                every = minutes;
                i++;
            }
            else if (args[i].StartsWith("--every=", StringComparison.Ordinal) && int.TryParse(args[i][8..], out var m))
                every = m;
            else if (action is null && args[i] is "on" or "off" or "status")
                action = args[i];
            else
                return Bad(host, $"unrecognized arguments: {args[i]}");
        }
        if (action is null)
            return Bad(host, "the following arguments are required: action");
        var task = host.Task();
        switch (action)
        {
            case "on":
                var (exe, prefix) = host.BackgroundCommand();
                var line = task.Enable(exe, [.. prefix, "run-queue"], null, new TaskSchedule
                {
                    AtLogon = true,
                    Every = TimeSpan.FromMinutes(every),
                    KeepRunning = false,
                    TimeLimit = TimeSpan.FromHours(4),
                });
                host.Out.WriteLine($"Autostart is on: '{task.Name}' runs every {every} minutes and at logon.");
                host.Out.WriteLine($"  {line}");
                return 0;
            case "off":
                host.Out.WriteLine(task.Disable() ? "Autostart is off." : "Autostart was already off.");
                return 0;
            default:
                var info = task.Status();
                if (info is null)
                {
                    host.Out.WriteLine("Autostart is off.");
                    return 0;
                }
                host.Out.WriteLine($"Autostart is on ('{task.Name}').");
                foreach (var key in new[] { "state", "last_run", "last_result", "next_run" })
                    host.Out.WriteLine($"  {key.Replace('_', ' '),-12} {info.GetValueOrDefault(key, "")}");
                return 0;
        }
    }

    private static int Bad(HeadlessHost host, string message)
    {
        host.Err.WriteLine("usage: claude-headless [-h] [--version] {status,run-queue,autostart} ...");
        host.Err.WriteLine($"claude-headless: error: {message}");
        return 2;
    }
}

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class HeadlessHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where run-queue's log lines are echoed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public JobQueue Queue { get; init; } = JobQueue.Default;
    public IJobRunner? JobRunner { get; init; }
    public Func<ITask> Task { get; init; } = () => new ScheduledTask(HeadlessCli.TaskName, HeadlessCli.TaskDescription);
    public string Version { get; init; } = "0.1.0";

    /// <summary>The windowless program the task runs, and any arguments before the command: by default
    /// claude-headless-background.exe beside the running program.</summary>
    public Func<(string Exe, IReadOnlyList<string> Prefix)> BackgroundCommand { get; init; } =
        () => (Path.Combine(AppContext.BaseDirectory, "claude-headless-background.exe"), []);
}
