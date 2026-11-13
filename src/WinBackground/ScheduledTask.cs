namespace WinBackground;

/// <summary>A Task Scheduler task, as the tools use one.</summary>
public interface ITask
{
    string Name { get; }

    /// <summary>Create or replace the task (at every logon, kept running) and start it now. Returns the command line.</summary>
    string Enable(string exe, IReadOnlyList<string> args, string? workingDir);

    /// <summary>Create or replace the task with these triggers and limits (a job run every few minutes, say), and
    /// start it now unless the schedule says not to. Returns the command line.
    /// <para>Added after ITask was first published, so it has a default (v0.3): the watcher schedule goes to the
    /// three-argument Enable, as ScheduledTask's does, and any other schedule throws NotSupportedException. A test
    /// fake written before it keeps compiling; one that needs schedules overrides it.</para></summary>
    string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule) =>
        schedule == TaskSchedule.Watcher
            ? Enable(exe, args, workingDir)
            : throw new NotSupportedException($"{GetType().Name} doesn't implement Enable with a schedule.");

    /// <summary>Stop and remove the task. False if there wasn't one.</summary>
    bool Disable();

    /// <summary>Stop the task and wait (up to 30 seconds) for its process to exit. False if there's no task.</summary>
    bool Stop();

    /// <summary>Start the task now, as it is (to undo a Stop when what came after it failed). False if there's no task.
    /// <para>Added after ITask was first published, so it has a default (v0.3) that throws NotSupportedException:
    /// a test fake written before it keeps compiling, and says so plainly if a test reaches it.</para></summary>
    bool Start() => throw new NotSupportedException($"{GetType().Name} doesn't implement Start.");

    /// <summary>Null if there's no task; otherwise state, command, last_run, last_result and next_run.</summary>
    IReadOnlyDictionary<string, string>? Status();
}

/// <summary>Starting a tool at logon through Windows Task Scheduler, by PowerShell's ScheduledTasks module (the
/// same script win-background uses; the COM API isn't available under Native AOT).
///
/// The task runs as you, only while you're logged on, so it needs no admin rights. It catches up a start
/// missed while the laptop was off or asleep. A watcher is restarted if it crashes and never stopped for
/// running too long. Run the tool's own executable directly, never through a wrapper such as a console window
/// or `powershell -File`: stopping the task then stops only the wrapper.</summary>
public sealed class ScheduledTask(string name, string description = "", IReadOnlyDictionary<string, string>? results = null) : ITask
{
    private const string NotRunYet = "267011", Running = "267009";   // SCHED_S_TASK_HAS_NOT_RUN, SCHED_S_TASK_RUNNING
    private readonly IReadOnlyDictionary<string, string> results = results ?? new Dictionary<string, string>();

    public string Name { get; } = name;
    public string Description { get; } = string.IsNullOrEmpty(description) ? name : description;

    /// <summary>PowerShell: the task's -TaskName, and its -TaskPath when it's in a folder ("\Claude EA\Transcript
    /// watcher"). Passing the whole path as -TaskName finds nothing.</summary>
    public string Which
    {
        get
        {
            var trimmed = Name.Trim('\\');
            var cut = trimmed.LastIndexOf('\\');
            if (cut < 0)
                return $"-TaskName {Shell.Quote(trimmed)}";
            return $"-TaskName {Shell.Quote(trimmed[(cut + 1)..])} -TaskPath {Shell.Quote("\\" + trimmed[..cut] + "\\")}";
        }
    }

    /// <summary>The PowerShell that registers the task, for tests and dry runs.</summary>
    public string EnableScript(string exe, IReadOnlyList<string> args, string? workingDir) =>
        EnableScript(exe, args, workingDir, TaskSchedule.Watcher);

    /// <summary>The PowerShell that registers the task with a given schedule (win-background's Task.enable).</summary>
    public string EnableScript(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
    {
        schedule.Validate();
        var line = Shell.ListToCommandLine(args);
        var folder = workingDir is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(workingDir);
        var triggers = new List<string>();
        if (schedule.DailyAt is { } daily)
            triggers.Add($"(New-ScheduledTaskTrigger -Daily -At {Shell.Quote(daily)})");
        if (schedule.AtLogon)
            triggers.Add("(New-ScheduledTaskTrigger -AtLogOn -User $me)");
        if (schedule.Every is { } every)
            // No -RepetitionDuration: without one the repetition is indefinite, and [TimeSpan]::MaxValue, the
            // usual workaround, is refused by Windows 10 and later.
            triggers.Add($"(New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Seconds {(long)every.TotalSeconds}))");
        var limits = schedule.KeepRunning
            ? "-ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5)"
            : $"-ExecutionTimeLimit (New-TimeSpan -Seconds {(long)schedule.TimeLimit.TotalSeconds})";
        return $"""

            $ErrorActionPreference = 'Stop'
            $me = "$env:USERDOMAIN\$env:USERNAME"
            $action = New-ScheduledTaskAction -Execute {Shell.Quote(exe)} -Argument {Shell.Quote(line)} -WorkingDirectory {Shell.Quote(folder)}
            $triggers = @({string.Join(", ", triggers)})
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
                {limits} -MultipleInstances IgnoreNew
            $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
            {StopAndWait()}
            Register-ScheduledTask {Which} -Action $action -Trigger $triggers -Settings $settings -Principal $principal `
                -Description {Shell.Quote(Description)} -Force | Out-Null
            {(schedule.StartNow ? "Start-ScheduledTask " + Which : "")}

            """;
    }

    public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) =>
        Enable(exe, args, workingDir, TaskSchedule.Watcher);

    public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
    {
        var done = Shell.PowerShell(EnableScript(exe, args, workingDir, schedule));
        if (done.ExitCode != 0)
            throw new InvalidOperationException($"Couldn't create the task: {Clip(done)}");
        return $"{exe} {Shell.ListToCommandLine(args)}";
    }

    /// <summary>PowerShell: the processes still running the task, found by its arguments ($taskArgs) in their
    /// command lines. Never this script's own powershell.exe ($PID): Enable's script contains the arguments
    /// literally, so without that it always found itself and waited the full 30 seconds. Not narrowed further by
    /// the program's path either: a launcher (a venv's python, py.exe) can run the real process from another
    /// exe, and missing a live watcher brings back the race below, while a false match only costs a wait.</summary>
    public const string AliveFilter =
        "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.Contains($taskArgs) }";

    /// <summary>PowerShell: stop the task, then wait (up to 30 seconds) until its process has actually gone.
    ///
    /// Stop-ScheduledTask returns before the process exits. Starting again at once lost the race on 11 Nov 2026:
    /// the new watcher found the old one still holding its single-instance lock and exited, then the old one
    /// exited too, leaving no watcher at all. The process is found by the task's own arguments.</summary>
    public string StopAndWait() => $$"""

        $existing = Get-ScheduledTask {{Which}} -ErrorAction SilentlyContinue
        if ($existing) {
          $taskArgs = $existing.Actions[0].Arguments
          Stop-ScheduledTask {{Which}}
          $deadline = (Get-Date).AddSeconds(30)
          while ($taskArgs -and (Get-Date) -lt $deadline) {
            $alive = {{AliveFilter}}
            if (-not $alive) { break }
            Start-Sleep -Milliseconds 500
          }
        }
        """;

    public bool Stop()
    {
        var done = Shell.PowerShell($"""
            {StopAndWait()}
            if ($existing) {"{"} 'stopped' {"}"}
            exit 0

            """);
        if (done.ExitCode != 0)
            throw new InvalidOperationException($"Couldn't stop the task: {Clip(done)}");
        return done.StandardOutput.Contains("stopped");
    }

    public bool Start()
    {
        var done = Shell.PowerShell($$"""

            $existing = Get-ScheduledTask {{Which}} -ErrorAction SilentlyContinue
            if ($existing) { Start-ScheduledTask {{Which}}; 'started' }
            exit 0

            """);
        if (done.ExitCode != 0)
            throw new InvalidOperationException($"Couldn't start the task: {Clip(done)}");
        return done.StandardOutput.Contains("started");
    }

    public bool Disable()
    {
        var done = Shell.PowerShell($"""
            {StopAndWait()}
            if ($existing) {"{"} Unregister-ScheduledTask {Which} -Confirm:$false; 'removed' {"}"}
            exit 0

            """);
        if (done.ExitCode != 0)
            throw new InvalidOperationException($"Couldn't remove the task: {Clip(done)}");
        return done.StandardOutput.Contains("removed");
    }

    public IReadOnlyDictionary<string, string>? Status()
    {
        var done = Shell.PowerShell($$"""

            $task = Get-ScheduledTask {{Which}} -ErrorAction SilentlyContinue
            if ($task) {
              $info = Get-ScheduledTaskInfo {{Which}}
              "state=$($task.State)"
              "command=$($task.Actions[0].Execute) $($task.Actions[0].Arguments)"
              "last_run=$($info.LastRunTime)"
              "last_result=$($info.LastTaskResult)"
              "next_run=$($info.NextRunTime)"
            }
            exit 0

            """);
        return ParseStatus(done.StandardOutput);
    }

    public IReadOnlyDictionary<string, string>? ParseStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var info = new Dictionary<string, string>();
        foreach (var line in text.Trim().Split('\n'))
        {
            var cut = line.IndexOf('=');
            if (cut >= 0)
                info[line[..cut]] = line[(cut + 1)..].TrimEnd('\r');
        }
        info.TryGetValue("last_result", out var result);
        if (result == NotRunYet)
        {
            info["last_run"] = "never";
            info["last_result"] = "-";
        }
        else if (result == Running)
            info["last_result"] = "running";
        else if (result is not null && results.TryGetValue(result, out var meaning))
            info["last_result"] = $"{result} ({meaning})";
        return info;
    }

    private static string Clip(Completed done)
    {
        var text = (string.IsNullOrEmpty(done.StandardError) ? done.StandardOutput : done.StandardError).Trim();
        return text.Length > 400 ? text[..400] : text;
    }
}

/// <summary>When a task runs, and for how long (win-background's Task.enable keywords).</summary>
public sealed record TaskSchedule
{
    /// <summary>A watcher: at every logon, kept running, restarted up to 3 times if it exits with an error.</summary>
    public static TaskSchedule Watcher { get; } = new();

    public bool AtLogon { get; init; } = true;

    /// <summary>"HH:mm": also run daily at this time.</summary>
    public string? DailyAt { get; init; }

    /// <summary>Run repeatedly at this interval, indefinitely, from now. Only for a job (KeepRunning false), and at
    /// least a minute apart. A run still going when the next is due is left to finish, and the next is skipped.</summary>
    public TimeSpan? Every { get; init; }

    /// <summary>A watcher (true), or a job stopped after TimeLimit (false).</summary>
    public bool KeepRunning { get; init; } = true;

    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromMinutes(30);

    public bool StartNow { get; init; } = true;

    internal void Validate()
    {
        if (!AtLogon && DailyAt is null && Every is null)
            throw new ArgumentException("A task needs a trigger: at logon, daily, every, or a mix.");
        if (Every is { } every && (KeepRunning || every < TimeSpan.FromMinutes(1)))
            throw new ArgumentException("Every is for a job (KeepRunning false), at least a minute apart.");
    }
}
