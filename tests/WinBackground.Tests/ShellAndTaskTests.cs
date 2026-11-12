using System.Globalization;

namespace WinBackground.Tests;

/// <summary>Starting system programs and quoting for PowerShell. Brought over from ai-keep-awake (its review of
/// 12 Nov 2026, findings 3 and 4). The PowerShell these run only echoes.</summary>
public class ShellTests
{
    private static string CodePoints(string text) =>
        string.Join(",", text.Select(c => ((int)c).ToString(CultureInfo.InvariantCulture)));

    [Fact]
    public void System_programs_are_started_by_their_full_path()
    {
        var system = Environment.SystemDirectory;
        Assert.Equal(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), Shell.PowerShellExe);
        Assert.Equal(Path.Combine(system, "powercfg.exe"), Power.PowercfgExe);
        Assert.True(Path.IsPathFullyQualified(Shell.PowerShellExe));
        Assert.True(File.Exists(Shell.PowerShellExe));
        Assert.True(File.Exists(Power.PowercfgExe));
    }

    [Fact]
    public void Quote_doubles_every_quote_powershell_ends_a_string_at()
    {
        Assert.Equal("'plain'", Shell.Quote("plain"));
        Assert.Equal("'it''s'", Shell.Quote("it's"));
        Assert.Equal("'C:\\Users\\O\u2019\u2019Brien'", Shell.Quote("C:\\Users\\O\u2019Brien"));
        Assert.Equal("'\u2018\u2018a\u2019\u2019 \u201A\u201Ab\u201B\u201B'", Shell.Quote("\u2018a\u2019 \u201Ab\u201B"));
        Assert.Equal("'\u201C\u201D'", Shell.Quote("\u201C\u201D"));   // double curly quotes don't end a single-quoted string
    }

    [Fact]
    public void Quoted_text_reaches_powershell_unchanged()
    {
        // Every kind of single quote, and what would run if the quoting failed. The script only echoes the
        // string's code points (so the console's code page can't garble the comparison).
        var text = "C:\\Users\\O\u2019Brien \u2018a\u2019 \u201Ab\u201B 'c' $(Write-Output injected) `n \"d\" \u201Ce\u201D";
        var done = Shell.PowerShell($"[string]::Join(',', [int[]][char[]]{Shell.Quote(text)})", 60);
        Assert.Equal(0, done.ExitCode);
        Assert.Equal(CodePoints(text), done.StandardOutput.Trim());
    }
}

/// <summary>Task Scheduler scripts, without creating, changing or stopping any task. Brought over from
/// ai-keep-awake (its review of 12 Nov 2026, finding 2, and Start for finding 1).</summary>
public class ScheduledTaskTests
{
    [Fact]
    public void Stop_and_wait_never_waits_for_its_own_powershell()
    {
        // The script that registers the task contains the task's arguments, so the powershell.exe running it
        // matched its own command line and the wait always ran the full 30 seconds.
        var task = new ScheduledTask("background-tools-lib test watch");
        Assert.Contains(ScheduledTask.AliveFilter, task.StopAndWait());
        Assert.Contains("$_.ProcessId -ne $PID", ScheduledTask.AliveFilter);
        Assert.Contains("$_.ProcessId -ne $PID", task.EnableScript(@"C:\x\tool.exe", ["--config", @"C:\x\c.toml", "watch"], @"C:\x"));
    }

    [Fact]
    public void The_alive_filter_does_not_find_the_script_running_it()
    {
        // Read-only: lists processes whose command line holds a unique marker. Only this script's own
        // powershell.exe does, and the filter must leave it out.
        var marker = $"--config C:\\background-tools-lib-test-{Guid.NewGuid():N}\\c.toml watch";
        var done = Shell.PowerShell($$"""
            $taskArgs = {{Shell.Quote(marker)}}
            $alive = {{ScheduledTask.AliveFilter}}
            $self = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($taskArgs) }
            "alive=$(@($alive).Count) self=$(@($self).Count)"
            """, 60);
        Assert.Equal(0, done.ExitCode);
        Assert.Equal("alive=0 self=1", done.StandardOutput.Trim());   // self=1 shows the marker really was there to find
    }

    [Fact]
    public void Start_runs_and_reports_no_task()
    {
        // A name no task can have, so Get-ScheduledTask finds nothing and nothing is started: this only checks
        // that the script runs and says there was no task.
        Assert.False(new ScheduledTask($"background-tools-lib test {Guid.NewGuid():N}").Start());
    }
}
