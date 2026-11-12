namespace WinBackground.Tests;

/// <summary>New here: lock keys, the log, toasts and processes, which ai-keep-awake only tested through its watcher.
/// The locks themselves are in LockTests.</summary>
public class SmallPartsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "wb-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Lock_keys_are_short_stable_and_ignore_case()
    {
        Assert.Equal(16, Locks.Key(@"C:\Tools\a.toml").Length);
        Assert.Equal(Locks.Key(@"C:\Tools\a.toml"), Locks.Key(@"c:\tools\A.TOML"));
        Assert.NotEqual(Locks.Key("a"), Locks.Key("b"));
    }

    [Fact]
    public void The_log_appends_stamped_lines_and_echoes()
    {
        var path = Path.Combine(folder, "sub", "tool.log");
        var echo = new StringWriter();
        var log = Logger.Create(path, echo, stampEcho: false, clock: () => new DateTime(2026, 11, 12, 9, 5, 7));
        log("one");
        log("two");
        Assert.Equal("2026-11-12 09:05:07  one\r\n2026-11-12 09:05:07  two\r\n", File.ReadAllText(path));
        Assert.Equal("one" + Environment.NewLine + "two" + Environment.NewLine, echo.ToString());
    }

    [Fact]
    public void A_log_that_cannot_be_written_never_throws()
    {
        var log = Logger.Create(Path.Combine(folder, "bad\0name.log"));
        log("still fine");
    }

    [Fact]
    public void A_toast_quotes_its_text_and_caps_it_at_600_characters()
    {
        var script = Toast.Script("It's done", [new string('x', 700)]);
        Assert.Contains("'It''s done'", script);
        Assert.Contains("'" + new string('x', 600) + "'", script);
        Assert.DoesNotContain(new string('x', 601), script);
    }

    [Fact]
    public void This_process_is_alive_and_a_made_up_one_is_not()
    {
        Assert.True(Processes.Alive(Environment.ProcessId));
        Assert.False(Processes.Alive(0x7FFFFFF0));
        Assert.True(Processes.ParentId() > 0);
    }
}
