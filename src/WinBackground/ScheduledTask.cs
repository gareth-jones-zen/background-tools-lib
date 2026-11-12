namespace WinBackground;

/// <summary>A Task Scheduler task, as the tools use one.</summary>
public interface ITask
{
    string Name { get; }

    /// <summary>Create or replace the task (at every logon, kept running) and start it now. Returns the command line.</summary>
    string Enable(string exe, IReadOnlyList<string> args, string? workingDir);

    /// <summary>Stop and remove the task. False if there wasn't one.</summary>
    bool Disable();

    /// <summary>Stop the task and wait (up to 30 seconds) for its process to exit. False if there's no task.</summary>
    bool Stop();

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
    public string EnableScript(string exe, IReadOnlyList<string> args, string? workingDir)
    {
        var line = Shell.ListToCommandLine(args);
        var folder = workingDir is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(workingDir);
        const string limits = "-ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5)";
        return $"""

            $ErrorActionPreference = 'Stop'
            $me = "$env:USERDOMAIN\$env:USERNAME"
            $action = New-ScheduledTaskAction -Execute {Shell.Quote(exe)} -Argument {Shell.Quote(line)} -WorkingDirectory {Shell.Quote(folder)}
            $triggers = @((New-ScheduledTaskTrigger -AtLogOn -User $me))
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
                {limits} -MultipleInstances IgnoreNew
            $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
            {StopAndWait()}
            Register-ScheduledTask {Which} -Action $action -Trigger $triggers -Settings $settings -Principal $principal `
                -Description {Shell.Quote(Description)} -Force | Out-Null
            Start-ScheduledTask {Which}

            """;
    }

    public string Enable(string exe, IReadOnlyList<string> args, string? workingDir)
    {
        var done = Shell.PowerShell(EnableScript(exe, args, workingDir));
        if (done.ExitCode != 0)
            throw new InvalidOperationException($"Couldn't create the task: {Clip(done)}");
        return $"{exe} {Shell.ListToCommandLine(args)}";
    }

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
            $alive = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($taskArgs) }
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
