namespace WinBackground.Tests;

/// <summary>Brought over from ai-keep-awake's parity tests: the parts that test WinBackground alone. Every
/// expected value was printed by Python 3.14 (subprocess.list2cmdline and the win-background parsers).</summary>
public class ParsingTests
{
    [Fact]
    public void List2cmdline_as_python()
    {
        Assert.Equal("--config \"C:\\My Files\\c.toml\" watch", Shell.ListToCommandLine(["--config", @"C:\My Files\c.toml", "watch"]));
        Assert.Equal("\"\" a\\\\\\\"b \"c d\\\\\"", Shell.ListToCommandLine(["", "a\\\"b", "c d\\"]));
    }

    [Fact]
    public void Task_status_is_parsed_as_before()
    {
        var task = new ScheduledTask("ai-keep-awake watch", results: new Dictionary<string, string> { ["3"] = "another" });
        Assert.Null(task.ParseStatus("  \n"));
        Assert.Equal("never", task.ParseStatus("state=Ready\nlast_run=x\nlast_result=267011\n")!["last_run"]);
        Assert.Equal("running", task.ParseStatus("last_result=267009")!["last_result"]);
        Assert.Equal("3 (another)", task.ParseStatus("last_result=3")!["last_result"]);
        Assert.Equal("-TaskName 'Transcript watcher' -TaskPath '\\Claude EA\\'", new ScheduledTask(@"\Claude EA\Transcript watcher").Which);
    }

    [Fact]
    public void Powercfg_output_gives_the_last_two_values()
    {
        string[] lines =
        [
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)",
            "  Subgroup GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20  (Sleep)",
            "    Minimum Possible Setting: 0x00000000",
            "    Possible Settings increment: 0x00000001",
            "  Current AC Power Setting Index: 0x0000012c",
            "  Current DC Power Setting Index: 0x000000b4 ",
        ];
        var text = string.Join("\r\n", lines) + "\r\n";
        Assert.Equal(300, Power.ParsePowercfg(text, ac: true));
        Assert.Equal(180, Power.ParsePowercfg(text, ac: false));
        Assert.Null(Power.ParsePowercfg("Nothing: here", ac: true));
        Assert.Null(Power.ParsePowercfg("a: 1x00\r\nb: 0x\r\n", ac: true));
    }
}
