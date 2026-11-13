using System.ComponentModel;

namespace WinBackground.Tests;

/// <summary>What the ports needed added (13 Nov 2026): Shell.Run's stdin, working folder and UTF-8 (from
/// teams-channel-notes' ShellRun), the log's CRLF for a newline inside a message (onenote-sync), ConsoleText
/// (teams-channel-notes) and Locks.Exclusive (mail-triage). The child process is LockHelper's echo mode.</summary>
public class AdditionsTests : IDisposable
{
    private static readonly string Helper = Path.Combine(AppContext.BaseDirectory, "LockHelper.exe");
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);
    private readonly string folder = Directory.CreateTempSubdirectory("wb-additions-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Run_sends_stdin_in_text_mode_from_a_working_folder_and_reads_utf8()
    {
        var input = "caf" + (char)0xE9 + " " + Emoji + "\nline two\n";
        var done = Shell.Run(Helper, ["echo"], TimeSpan.FromSeconds(30), stdin: input, workingDirectory: folder, utf8: true);
        Assert.Equal(7, done.ExitCode);
        // stdin went as UTF-8 with CRLF; the output's CRLF and lone CR became LF, and the bad byte U+FFFD.
        Assert.Equal("cwd=" + folder + "\n" + "caf" + (char)0xE9 + " " + Emoji + "\nline two\n" + (char)0xFFFD + "\nx\n", done.StandardOutput);
        Assert.Equal("err", done.StandardError);
    }

    [Fact]
    public void Run_by_default_closes_stdin_and_keeps_its_old_behaviour()
    {
        var done = Shell.Run(Helper, ["echo"], TimeSpan.FromSeconds(30));
        Assert.Equal(7, done.ExitCode);
        Assert.StartsWith("cwd=" + Environment.CurrentDirectory + "\n", done.StandardOutput);
        Assert.EndsWith("\nx\r", done.StandardOutput);   // only CRLF is normalised by default; a lone CR stays
    }

    [Fact]
    public void A_timeout_kills_it_and_says_so_in_both_wordings()
    {
        var error = Assert.Throws<ShellTimeoutException>(() => Shell.Run(Helper, ["sleep"], TimeSpan.FromSeconds(1)));
        Assert.Equal($"{Helper} didn't finish within 1 seconds.", error.Message);
        Assert.Equal($"Command '['{Helper.Replace("\\", "\\\\")}', 'sleep']' timed out after 1 seconds", error.PythonMessage);
        Assert.IsAssignableFrom<TimeoutException>(error);
    }

    [Fact]
    public void A_program_that_cannot_start_reads_as_pythons_oserror()
    {
        var error = Assert.Throws<Win32Exception>(() => Shell.Run(Path.Combine(folder, "missing.exe"), [], TimeSpan.FromSeconds(5), stdin: "x", utf8: true));
        Assert.Equal("[WinError 2] The system cannot find the file specified", Shell.WinErrorText(error));
    }

    [Fact]
    public void Python_list_repr_picks_quotes_as_python_does() =>
        Assert.Equal("['a', \"it's\", 'C:\\\\x', 'q\\'\"']", Shell.PythonListRepr(["a", "it's", "C:\\x", "q'\""]));

    [Fact]
    public void The_log_writes_a_newline_inside_a_message_as_crlf_like_python()
    {
        var path = Path.Combine(folder, "tool.log");
        var echo = new StringWriter();
        var log = Logger.Create(path, echo, stampEcho: false, clock: () => new DateTime(2026, 11, 13, 9, 5, 7));
        log("--- a\n+++ b\n@@ -1 +1 @@");
        log("kept\r\nas python");
        Assert.Equal("2026-11-13 09:05:07  --- a\r\n+++ b\r\n@@ -1 +1 @@\r\n2026-11-13 09:05:07  kept\r\r\nas python\r\n", File.ReadAllText(path));
        Assert.Equal("--- a\r\n+++ b\r\n@@ -1 +1 @@\r\nkept\r\r\nas python\r\n", echo.ToString());
    }

    [Fact]
    public void Console_text_redirected_is_utf8_without_a_byte_order_mark()
    {
        // Under the test runner stdout is redirected, so this is the UTF-8 writer, not WriteConsoleW.
        var writer = ConsoleText.Out;
        Assert.Equal("utf-8", writer.Encoding.WebName);
        Assert.Empty(writer.Encoding.GetPreamble());
        Assert.NotNull(ConsoleText.Error);
    }

    [Fact]
    public void Exclusive_holds_the_lock_and_times_out_in_pythons_words()
    {
        var name = $"wb-exclusive-test-{Guid.NewGuid():N}";
        using (var held = Locks.Exclusive(name, 1, "triage run"))
        {
            Assert.True(held.IsHeld);
            var error = Assert.Throws<TimeoutException>(() => Locks.Exclusive(name, 0.06, "triage run"));
            Assert.Equal("Another triage run didn't finish within 0 minutes.", error.Message);
            Assert.Null(Locks.SingleInstance(name));
        }
        using var again = Locks.Exclusive(name, 0);
        Assert.True(again.IsHeld);
    }

    [Theory]
    [InlineData(600, "10")]
    [InlineData(90, "2")]     // 1.5 rounds half to even, as Python's "{:.0f}"
    [InlineData(150, "2")]    // 2.5 likewise
    [InlineData(210, "4")]    // 3.5
    [InlineData(29, "0")]
    public void Exclusive_rounds_minutes_as_python(double seconds, string minutes) => Assert.Equal(minutes, Locks.Minutes(seconds));
}
