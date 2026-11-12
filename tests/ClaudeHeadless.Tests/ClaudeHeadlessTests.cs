using System.Text;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace ClaudeHeadless.Tests;

public sealed class TempHome : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "claude-headless-tests-" + Guid.NewGuid().ToString("N"));

    public TempHome() => Directory.CreateDirectory(Dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The Python tests (tests/test_queue.py), ported, and more. Expected values were printed by Python 3.14
/// running claude-headless 0.1.0.</summary>
public class LimitTests
{
    private static readonly TimeZoneInfo London = PyTime.Zone("Europe/London");
    private static readonly DateTimeOffset Now = PyTime.At(London, new DateTime(2026, 11, 10, 15, 0, 0));

    [Theory]
    [InlineData("You've hit your session limit · resets 7pm (Europe/London)", "2026-11-10T19:00:00+00:00")]
    [InlineData("resets 7:30pm", "2026-11-10T19:30:00+00:00")]
    [InlineData("resets 9am", "2026-11-11T09:00:00+00:00")]            // tomorrow morning
    [InlineData("resets Nov 12, 7pm", "2026-11-12T19:00:00+00:00")]
    [InlineData("resets 3pm", "2026-11-11T15:00:00+00:00")]            // now exactly is past
    [InlineData("no time here", "2026-11-10T16:00:00+00:00")]
    [InlineData("resets 12am", "2026-11-11T00:00:00+00:00")]
    [InlineData("resets 12pm", "2026-11-11T12:00:00+00:00")]
    [InlineData("RESETS dec 1 10AM", "2026-12-01T10:00:00+00:00")]
    [InlineData("resets Mar 29, 1:30am", "2026-03-29T01:30:00+00:00")]  // in the hour the clocks skip
    [InlineData("resets Jul 1, 9am", "2026-07-01T09:00:00+01:00")]
    public void Reset_times_are_read_from_the_message(string text, string expected) =>
        Assert.Equal(expected, PyTime.IsoFormat(Limits.ResetTime(text, Now, London)));

    [Fact]
    public void A_reset_in_the_hour_that_happens_twice_takes_the_first()
    {
        var now = PyTime.At(London, new DateTime(2026, 10, 24, 15, 0, 0));
        Assert.Equal("2026-10-25T01:30:00+01:00", PyTime.IsoFormat(Limits.ResetTime("resets 1:30am", now, London)));
    }

    [Fact]
    public void A_month_that_isnt_one_is_an_error() =>
        Assert.Throws<FormatException>(() => Limits.ResetTime("resets Foo 3, 7pm", Now, London));

    [Theory]
    [InlineData("You've hit your session limit", true)]
    [InlineData("Claude AI usage limit reached|1762000000", true)]
    [InlineData("you have HIT YOUR weekly LIMIT", true)]
    [InlineData("an ordinary error", false)]
    public void Limit_text(string text, bool limit) => Assert.Equal(limit, Limits.LimitText().IsMatch(text));
}

public class QueueTests : IDisposable
{
    private readonly TempHome home = new();
    private DateTimeOffset now = PyTime.Now(Limits.Zone);
    private readonly JobQueue queue;

    public QueueTests() => queue = new JobQueue(home.Dir, () => now);

    public void Dispose() => home.Dispose();

    [Fact]
    public void One_notification_per_limit()
    {
        var reset = now + TimeSpan.FromHours(2);
        queue.Remember(reset);                          // the runner saw it
        Assert.True(queue.Retry(["a"], ".", reset));
        Assert.False(queue.Retry(["b"], ".", reset));
        Assert.Equal([["a"], ["b"]], queue.Pending().Select(i => i.Argv.ToArray()));
    }

    [Fact]
    public void Jobs_wait_for_the_reset_and_a_passed_limit_is_ignored()
    {
        queue.Retry(["a"], ".", now + TimeSpan.FromHours(1));
        Assert.Null(queue.Take());
        queue.Remember(now - TimeSpan.FromHours(1));
        Assert.NotNull(queue.LimitUntil());             // remember never shortens a known limit
        File.WriteAllText(queue.LimitFile, $"{{\"reset\": \"{PyTime.IsoFormat(now - TimeSpan.FromHours(1))}\"}}");
        Assert.Equal(["a"], queue.Take()!.Argv);
    }

    [Fact]
    public void The_grace_is_five_minutes()
    {
        queue.Remember(now - TimeSpan.FromMinutes(4));
        Assert.NotNull(queue.LimitUntil());
        now += TimeSpan.FromMinutes(2);
        Assert.Null(queue.LimitUntil());
    }

    [Fact]
    public void Same_job_once_and_deadlines_first()
    {
        Assert.True(queue.Add(["a"], "."));
        Assert.False(queue.Add(["a"], "."));
        queue.Add(["urgent"], ".", front: true);
        Assert.Equal([["urgent"], ["a"]], queue.Pending().Select(i => i.Argv.ToArray()));
    }

    [Fact]
    public void Files_are_written_as_python_writes_them()
    {
        now = new DateTimeOffset(2026, 11, 12, 23, 5, 7, TimeSpan.Zero).AddTicks(1234);
        queue.Add([@"C:\Python314\python.exe", "-m", "meeting_summaries", "draft", @"C:\x\café.md"], @"C:\work");
        // Printed by Python: json.dumps(..., indent=2) through write_text, so CRLF and no newline at the end.
        const string expected = "{\r\n  \"items\": [\r\n    {\r\n      \"argv\": [\r\n        \"C:\\\\Python314\\\\python.exe\",\r\n" +
                                "        \"-m\",\r\n        \"meeting_summaries\",\r\n        \"draft\",\r\n        \"C:\\\\x\\\\caf\\u00e9.md\"\r\n" +
                                "      ],\r\n      \"cwd\": \"C:\\\\work\",\r\n      \"added\": \"2026-11-12T23:05:07+00:00\"\r\n    }\r\n  ]\r\n}";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(queue.QueueFile));
        queue.Retry(["b"], ".", now + TimeSpan.FromHours(1));
        Assert.Equal("{\r\n  \"reset\": \"2026-11-13T00:05:07.000123+00:00\",\r\n  \"notified\": true\r\n}", File.ReadAllText(queue.LimitFile));
    }

    [Fact]
    public void Unknown_keys_and_order_survive_a_rewrite()
    {
        File.WriteAllText(queue.QueueFile, "{\"items\": [{\"cwd\": \"x\", \"argv\": [\"a\"], \"added\": \"t\", \"extra\": 1.0}], \"v\": 2}");
        queue.Add(["b"], ".");
        var text = File.ReadAllText(queue.QueueFile);
        Assert.Contains("\"cwd\": \"x\",\r\n      \"argv\": [", text);
        Assert.Contains("\"extra\": 1.0", text);
        Assert.EndsWith("],\r\n  \"v\": 2\r\n}", text);
    }

    [Fact]
    public void A_damaged_file_reads_as_empty()
    {
        File.WriteAllText(queue.QueueFile, "{not json");
        Assert.Empty(queue.Pending());
        File.WriteAllText(queue.LimitFile, "\ufeff{\"reset\": \"2099-01-01T00:00:00+00:00\"}");   // Python refuses a BOM too
        Assert.Null(queue.LimitUntil());
    }

    [Fact]
    public void Run_works_through_the_queue_one_at_a_time()
    {
        var ran = new List<string>();
        var runner = new FakeJobs(argv =>
        {
            ran.Add(argv[0]);
            return argv[0] == "fails" ? new Completed(3, "out", "  went wrong \n") : new Completed(0, "", "");
        });
        foreach (var name in new[] { "a", "fails", "c" })
            queue.Add([name], home.Dir);
        var log = new List<string>();
        Assert.Equal(3, queue.Run(log.Add, runner: runner));
        Assert.Equal(["a", "fails", "c"], ran);
        Assert.Empty(queue.Pending());
        Assert.Equal(["job: a (2 more queued)", "job: fails (1 more queued)", "  exited 3: went wrong", "job: c (0 more queued)"], log);
    }

    [Fact]
    public void Run_stops_when_the_limit_comes_back()
    {
        queue.Add(["a"], ".");
        queue.Add(["b"], ".");
        var runner = new FakeJobs(_ =>
        {
            queue.Remember(now + TimeSpan.FromHours(1));
            return new Completed(0, "", "");
        });
        Assert.Equal(1, queue.Run(_ => { }, runner: runner));
        Assert.Single(queue.Pending());
    }

    [Fact]
    public void A_second_runner_returns_at_once()
    {
        queue.Add(["a"], ".");
        using var held = Locks.SingleInstance(queue.LockName);
        Assert.NotNull(held);
        var other = new Thread(() => Assert.Equal(0, queue.Run(_ => { }, runner: new FakeJobs(_ => throw new InvalidOperationException()))));
        other.Start();
        other.Join();
        Assert.Single(queue.Pending());
    }

    [Fact]
    public void The_lock_name_matches_pythons()
    {
        // win_background.key(r"C:\Users\me\AppData\Local\claude-headless"), printed by Python.
        var python = new JobQueue(@"C:\Users\me\AppData\Local\claude-headless");
        Assert.Equal("claude-headless-runner-53626ade3078509d", python.LockName);
        Assert.Equal(@"C:\x\claude-headless", new JobQueue("C:/x//claude-headless/").Home);
        Assert.Equal(@"C:\L\claude-headless", JobQueue.DefaultHome(n => n == "LOCALAPPDATA" ? @"C:\L" : null));
        Assert.Equal(@"D:\q", JobQueue.DefaultHome(n => n switch { "CLAUDE_HEADLESS_HOME" => "D:/q/", "LOCALAPPDATA" => @"C:\L", _ => null }));
    }

    [Fact]
    public void A_real_job_runs_with_output_read_as_utf8()
    {
        var done = new JobRunner().Run(["cmd.exe", "/c", "echo héllo&& exit 4"], home.Dir, TimeSpan.FromSeconds(30));
        Assert.Equal(4, done.ExitCode);
        Assert.StartsWith("h", done.StandardOutput);
    }

    private sealed class FakeJobs(Func<IReadOnlyList<string>, Completed> run) : IJobRunner
    {
        public Completed Run(IReadOnlyList<string> argv, string cwd, TimeSpan timeout) => run(argv);
    }
}

public class RunnerTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly JobQueue queue;

    public RunnerTests() => queue = new JobQueue(home.Dir);

    public void Dispose() => home.Dispose();

    private sealed class FakeClaude(Completed reply) : IProcessStarter
    {
        public List<(string Exe, IReadOnlyList<string> Args, string Input, string Cwd, IReadOnlyDictionary<string, string> Env)> Calls { get; } = [];

        public Completed Run(string exe, IReadOnlyList<string> args, string input, string cwd, IReadOnlyDictionary<string, string> extraEnv, TimeSpan timeout)
        {
            Calls.Add((exe, args, input, cwd, extraEnv));
            return reply;
        }
    }

    private ClaudeRunner Runner(FakeClaude fake) => new(queue, fake, () => @"C:\fake\claude.cmd", _ => { });

    [Fact]
    public void A_result_is_returned_and_the_updater_is_off()
    {
        var fake = new FakeClaude(new Completed(0, "{\"result\": \"# [DRAFT] x\", \"num_turns\": 3}", ""));
        var result = Runner(fake).Run(["-p", "--output-format", "json"], "the prompt", home.Dir, TimeSpan.FromMinutes(1));
        Assert.Equal("# [DRAFT] x", (string?)result["result"]);
        Assert.Equal(@"C:\fake\claude.cmd", fake.Calls[0].Exe);
        Assert.Equal("1", fake.Calls[0].Env["DISABLE_AUTOUPDATER"]);
        Assert.Equal("the prompt", fake.Calls[0].Input);
    }

    [Theory]
    [InlineData("", "You've hit your session limit · resets 7pm (Europe/London)\n", "You've hit your session limit · resets 7pm (Europe/London)")]
    [InlineData("{\"is_error\": true, \"result\": \"Claude AI usage limit reached · resets 9am\"}", "", "Claude AI usage limit reached · resets 9am")]
    [InlineData("{\"is_error\": true, \"api_error_status\": 429}", "", "rate limited")]
    [InlineData("{\"is_error\": true, \"result\": \"\", \"error\": \"rate_limit_error\"}", "", "rate limited")]
    public void The_limit_in_each_shape(string stdout, string stderr, string message)
    {
        var error = Assert.Throws<LimitReachedException>(() =>
            Runner(new FakeClaude(new Completed(1, stdout, stderr))).Run(["-p"], "x", home.Dir, TimeSpan.FromMinutes(1)));
        Assert.Equal(message, error.Message);
        Assert.NotNull(queue.LimitUntil());    // remembered, so the next run doesn't start
        var fake = new FakeClaude(new Completed(0, "{\"result\": \"ok\"}", ""));
        Assert.Throws<LimitReachedException>(() => Runner(fake).Run(["-p"], "x", home.Dir, TimeSpan.FromMinutes(1)));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Other_failures_are_run_errors()
    {
        var error = Assert.Throws<ClaudeRunException>(() =>
            Runner(new FakeClaude(new Completed(2, "", "boom"))).Run(["-p"], "x", home.Dir, TimeSpan.FromMinutes(1)));
        Assert.Equal("claude exited 2: boom", error.Message);
        var reported = Assert.Throws<ClaudeRunException>(() =>
            Runner(new FakeClaude(new Completed(0, "{\"is_error\": true, \"result\": \"bad\"}", ""))).Run(["-p"], "x", home.Dir, TimeSpan.FromMinutes(1)));
        Assert.Equal("claude reported an error: {\"is_error\": true, \"result\": \"bad\"}", reported.Message);
        Assert.Null(queue.LimitUntil());
    }

    [Fact]
    public void The_executable_is_waited_for()
    {
        var tries = 0;
        var slept = 0;
        var runner = new ClaudeRunner(queue, null, () => ++tries < 3 ? null : "claude.exe", _ => slept++);
        Assert.Equal("claude.exe", runner.Exe());
        Assert.Equal(2, slept);
        var never = new ClaudeRunner(queue, null, () => null, _ => slept++);
        Assert.Throws<ClaudeRunException>(never.Exe);
    }

    [Fact]
    public void Which_follows_path_and_pathext_then_npm()
    {
        var bin = Directory.CreateDirectory(Path.Combine(home.Dir, "bin")).FullName;
        var npm = Directory.CreateDirectory(Path.Combine(home.Dir, "npm")).FullName;
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "");
        string? Env(string name) => name switch
        {
            "PATH" => $"{home.Dir}\\nothing;\"{bin}\"",
            "PATHEXT" => ".EXE;.CMD",
            "APPDATA" => home.Dir,
            _ => null,
        };
        Assert.Equal(Path.Combine(npm, "claude.cmd"), ClaudeRunner.Which("claude", Env));
        File.WriteAllText(Path.Combine(bin, "claude.cmd"), "");
        Assert.Equal(Path.Combine(bin, "claude.CMD"), ClaudeRunner.Which("claude", Env));   // as Python: the extension as PATHEXT spells it
        File.WriteAllText(Path.Combine(bin, "claude.exe"), "");
        Assert.Equal(Path.Combine(bin, "claude.EXE"), ClaudeRunner.Which("claude", Env));
    }

    [Fact]
    public void The_prompt_goes_to_a_real_process_with_windows_line_ends()
    {
        // A real child: the bytes it receives on stdin come back on stdout, as hex.
        var script = Path.Combine(home.Dir, "echo.ps1");
        File.WriteAllText(script, "$in = [Console]::OpenStandardInput(); $m = New-Object IO.MemoryStream; $in.CopyTo($m); " +
                                  "'{\"result\": \"' + [BitConverter]::ToString($m.ToArray()) + '\"}'");
        var runner = new ClaudeRunner(queue, null, () => "powershell.exe", _ => { });
        var result = runner.Run(["-NoProfile", "-NonInteractive", "-File", script], "a\nb€", home.Dir, TimeSpan.FromMinutes(1));
        Assert.Equal("61-0D-0A-62-E2-82-AC", (string?)result["result"]);
    }
}

public class CliTests : IDisposable
{
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private sealed class FakeTask : ITask
    {
        public List<(string Exe, IReadOnlyList<string> Args, TaskSchedule? Schedule)> Enabled { get; } = [];
        public string Name => HeadlessCli.TaskName;
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => Enable(exe, args, workingDir, TaskSchedule.Watcher);

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Enabled.Add((exe, args, schedule));
            return exe + " " + string.Join(' ', args);
        }

        public bool Disable() => false;
        public bool Stop() => false;
        public bool Start() => false;
        public IReadOnlyDictionary<string, string>? Status() => new Dictionary<string, string> { ["state"] = "Ready", ["next_run"] = "soon" };
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = new HeadlessHost
        {
            Out = output,
            Err = error,
            Queue = new JobQueue(home.Dir, () => PyTime.At(Limits.Zone, new DateTime(2026, 11, 12, 22, 0, 0))),
            Task = () => task,
            BackgroundCommand = () => (@"C:\tools\claude-headless-background.exe", []),
            JobRunner = new NoJobs(),
        };
        return (HeadlessCli.Run(args, host), output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    private readonly FakeTask task = new();

    private sealed class NoJobs : IJobRunner
    {
        public Completed Run(IReadOnlyList<string> argv, string cwd, TimeSpan timeout) => new(0, "", "");
    }

    [Fact]
    public void Status_shows_the_limit_and_the_queue()
    {
        Assert.Equal((0, "No usage limit in force.\n0 job(s) queued.\n", ""), Run("status"));
        var queue = new JobQueue(home.Dir, () => PyTime.At(Limits.Zone, new DateTime(2026, 11, 12, 22, 0, 0)));
        queue.Add(["python", "-m", "x", new string('y', 200)], ".");
        File.WriteAllText(queue.LimitFile, "{\"reset\": \"2099-11-13T09:00:00+00:00\", \"notified\": true}");
        var (code, output, _) = Run("status");
        Assert.Equal(0, code);
        Assert.StartsWith("Usage limit in force until Fri 09:00.\n1 job(s) queued:\n  2026-11-12T22:00  python -m x yyy", output);
        Assert.Equal(150, output.Split('\n')[2][20..].Length);
    }

    [Fact]
    public void Run_queue_reports_what_ran()
    {
        new JobQueue(home.Dir).Add(["a"], ".");
        Assert.Equal((0, "1 job(s) run; 0 still queued.\n", ""), Run("run-queue"));
    }

    [Fact]
    public void Autostart_runs_the_windowless_twin_every_15_minutes()
    {
        var (code, output, _) = Run("autostart", "on");
        Assert.Equal(0, code);
        Assert.Equal("Autostart is on: 'claude-headless run-queue' runs every 15 minutes and at logon.\n  C:\\tools\\claude-headless-background.exe run-queue\n", output);
        var schedule = task.Enabled[0].Schedule!;
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Every);
        Assert.False(schedule.KeepRunning);
        Assert.Equal(TimeSpan.FromHours(4), schedule.TimeLimit);
        Run("autostart", "on", "--every", "5");
        Assert.Equal(TimeSpan.FromMinutes(5), task.Enabled[1].Schedule!.Every);
        Assert.Equal("Autostart was already off.\n", Run("autostart", "off").Out);
        Assert.Equal("Autostart is on ('claude-headless run-queue').\n  state        Ready\n  last run     \n  last result  \n  next run     soon\n",
            Run("autostart", "status").Out);
    }

    [Fact]
    public void Bad_arguments_exit_2()
    {
        Assert.Equal(2, Run().Code);
        Assert.Equal(2, Run("nonsense").Code);
        Assert.Equal(2, Run("autostart").Code);
        Assert.Equal(2, Run("status", "extra").Code);
    }

    [Fact]
    public void The_task_script_for_a_job()
    {
        var script = new ScheduledTask(HeadlessCli.TaskName).EnableScript(@"C:\t\claude-headless-background.exe", ["run-queue"], @"C:\t",
            new TaskSchedule { Every = TimeSpan.FromMinutes(15), KeepRunning = false, TimeLimit = TimeSpan.FromHours(4) });
        // The lines win-background's Task.enable writes for the same call (every=15 minutes, keep_running=False,
        // time_limit=4 hours), printed by Python.
        Assert.Contains("$triggers = @((New-ScheduledTaskTrigger -AtLogOn -User $me), (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Seconds 900)))", script);
        Assert.Contains("-ExecutionTimeLimit (New-TimeSpan -Seconds 14400) -MultipleInstances IgnoreNew", script);
        Assert.Contains("Start-ScheduledTask -TaskName 'claude-headless run-queue'", script);
        Assert.Throws<ArgumentException>(() => new ScheduledTask("x").EnableScript("x", [], null, new TaskSchedule { Every = TimeSpan.FromMinutes(5) }));
        // The watcher script is unchanged.
        Assert.Equal(new ScheduledTask("w").EnableScript("x", ["watch"], @"C:\t"),
            new ScheduledTask("w").EnableScript("x", ["watch"], @"C:\t", TaskSchedule.Watcher));
        Assert.Contains("-ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3", new ScheduledTask("w").EnableScript("x", ["watch"], @"C:\t"));
    }
}

/// <summary>A real queue file, copied (never touched in place): set CLAUDE_HEADLESS_ROUNDTRIP_FILE to a queue.json and
/// CLAUDE_HEADLESS_ROUNDTRIP_OUT to a folder. A job is added at a fixed time (the bytes are saved there, to compare
/// with what the Python version writes for the same call), then taken off again, which must give back the original
/// bytes.</summary>
public class LiveRoundTripTests
{
    [Fact]
    public void A_copy_of_a_real_queue_round_trips()
    {
        var source = Environment.GetEnvironmentVariable("CLAUDE_HEADLESS_ROUNDTRIP_FILE");
        var output = Environment.GetEnvironmentVariable("CLAUDE_HEADLESS_ROUNDTRIP_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(source) || string.IsNullOrEmpty(output), "no queue file given");
        using var home = new TempHome();
        var at = new DateTimeOffset(2026, 11, 12, 23, 5, 7, TimeSpan.Zero);
        var queue = new JobQueue(home.Dir, () => at);
        File.Copy(source!, queue.QueueFile);
        var original = File.ReadAllBytes(queue.QueueFile);
        var count = queue.Pending().Count;
        Assert.True(queue.Add([@"C:\x\meeting-summaries.exe", "draft", @"C:\y\caf" + "\u00e9.md"], @"C:\work", front: true));
        File.Copy(queue.QueueFile, Path.Combine(output!, "dotnet-added.json"), true);
        Assert.Equal(count + 1, queue.Pending().Count);
        Assert.Equal("meeting-summaries.exe", Path.GetFileName(queue.Take()!.Argv[0]));
        Assert.Equal(original, File.ReadAllBytes(queue.QueueFile));
    }
}
