namespace WinBackground.Tests;

/// <summary>v0.3: Locks.Key lower-cases as Python's str.lower() does, and ITask's later members have defaults.</summary>
public class V03Tests
{
    [Theory]
    // Expected keys from win_background.locks.key() under Python 3.14.5 (sha256 of str(text).lower(), UTF-8).
    [InlineData("C:\\Users\\x\\tools\\meeting-preps.toml", "4c0d9da5adb94e68")]
    [InlineData("a.md", "fecccc97532467ad")]
    [InlineData("C:\\Users\\\u0130lker\\tools\\x.toml", "851898b5a1654015")]   // capital I with dot: two characters in Python
    [InlineData("D:\\\u039f\u0394\u039f\u03a3\\x.toml", "7226a5f085889a5e")]   // a final sigma before the backslash
    [InlineData("D:\\\u039f\u0394\u039f\u03a3.toml", "0646f5135eb13220")]
    [InlineData("\U0001F600 Notes", "b4d5c5e05a470a23")]
    [InlineData("Stra\u00dfe \u1e9e", "53110024bc5c103f")]
    public void Lock_keys_are_win_backgrounds(string text, string expected) => Assert.Equal(expected, Locks.Key(text));

    [Fact]
    public void Keys_ignore_case_as_before_for_ordinary_paths() =>
        Assert.Equal(Locks.Key(@"C:\Users\x\tools\onenote-sync.toml"), Locks.Key(@"c:\users\X\TOOLS\onenote-sync.toml"));

    /// <summary>A fake written before ITask had the scheduled Enable and Start: it still compiles.</summary>
    private sealed class OldFake : ITask
    {
        public List<string> Calls { get; } = [];

        public string Name => "old fake";

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir)
        {
            Calls.Add("enable");
            return exe;
        }

        public bool Disable() => true;

        public bool Stop() => true;

        public IReadOnlyDictionary<string, string>? Status() => null;
    }

    [Fact]
    public void An_old_fake_still_implements_itask()
    {
        ITask task = new OldFake();
        Assert.Equal("x.exe", task.Enable("x.exe", [], null, TaskSchedule.Watcher));
        Assert.Equal(["enable"], ((OldFake)task).Calls);
        Assert.Throws<NotSupportedException>(() => task.Enable("x.exe", [], null, new TaskSchedule { AtLogon = false, DailyAt = "07:30" }));
        Assert.Throws<NotSupportedException>(() => task.Start());
    }
}
