using System.Text;
using System.Text.Json.Nodes;
using WinBackground;

namespace ActivityJournal.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("activity-journal-").FullName;

    public string File(string name = "journal.jsonl") => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The journal: lines written and read back, filters, the lock between writers, pruning. Every subject and
/// address here is made up.</summary>
public class JournalTests
{
    /// <summary>A lock name of the test's own, so nothing else on the machine (a real journal's writers) is involved.</summary>
    private static string UniqueLock() => "activity-journal-test-" + Guid.NewGuid().ToString("N");

    private static readonly DateTimeOffset Now = new(2026, 11, 25, 7, 42, 10, 512, TimeSpan.Zero);

    private static Journal Open(TempFolder folder, Func<DateTimeOffset>? clock = null, int keepDays = 90) =>
        new(folder.File(), UniqueLock(), keepDays, 30, clock ?? (() => Now));

    private static JournalEntry Line(string what = "delete", string tool = "mail-triage", string? subject = "Your weekly digest") => new()
    {
        Tool = tool,
        What = what,
        Subject = subject,
        Rule = "Newsletters",
        Why = "Read on the web if at all",
        Item = "<abc@mail.example.com>",
        Undo = "mail-triage restore 0d1e2f3a4b5c",
        Sensitivity = Sensitivity.Private,
    };

    [Fact]
    public void A_line_is_written_as_one_JSON_line_and_read_back()
    {
        using var folder = new TempFolder();
        var journal = Open(folder);
        var written = journal.Write(Line());

        Assert.Matches("^[0-9a-f]{12}$", written.Id);
        Assert.Equal(Now, written.When);
        var bytes = File.ReadAllBytes(journal.Path);
        Assert.NotEqual(0xEF, bytes[0]);   // no byte order mark
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal(
            $$"""{"id":"{{written.Id}}","when":"2026-11-25T07:42:10.512+00:00","tool":"mail-triage","what":"delete","by":"auto","subject":"Your weekly digest","rule":"Newsletters","why":"Read on the web if at all","item":"<abc@mail.example.com>","undo":"mail-triage restore 0d1e2f3a4b5c","sensitivity":"private"}""" + "\n",
            text);

        var back = Assert.Single(journal.Read());
        Assert.Equal(written, back);
    }

    [Fact]
    public void Optional_fields_left_out_and_defaults_kept()
    {
        using var folder = new TempFolder();
        var journal = Open(folder);
        journal.Write(new JournalEntry { Tool = "calendar-bank-holidays", What = "add" });
        var line = JsonNode.Parse(File.ReadAllText(journal.Path))!.AsObject();
        Assert.Equal(["id", "when", "tool", "what", "by", "sensitivity"], line.Select(p => p.Key));
        var back = Assert.Single(journal.Read());
        Assert.Equal(By.Auto, back.By);
        Assert.Equal(Sensitivity.Normal, back.Sensitivity);
        Assert.Null(back.Undo);
        Assert.Null(back.Outcome);
    }

    [Fact]
    public void Text_with_newlines_and_any_characters_stays_on_one_line()
    {
        using var folder = new TempFolder();
        var journal = Open(folder);
        journal.Write(Line(subject: "Café – naïve “quotes” 😀\nsecond line\r\tand a <tag> & \"quote\""));
        var text = File.ReadAllText(journal.Path, Encoding.UTF8);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Café – naïve “quotes”", text, StringComparison.Ordinal);   // kept as they are (an emoji is escaped)
        Assert.Equal("Café – naïve “quotes” 😀\nsecond line\r\tand a <tag> & \"quote\"", journal.Read()[0].Subject);
    }

    [Fact]
    public void Long_text_is_cut()
    {
        using var folder = new TempFolder();
        var journal = Open(folder);
        journal.Write(Line(subject: new string('x', 5000)));
        var subject = journal.Read()[0].Subject!;
        Assert.Equal(JournalEntry.MaxText, subject.Length);
        Assert.EndsWith("…", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_returned_is_the_line_read_back_to_the_millisecond()
    {
        using var folder = new TempFolder();
        var journal = Open(folder, () => Now.AddTicks(1234));
        var written = journal.Write(Line());
        Assert.Equal(Now, written.When);
        Assert.Equal(written, Assert.Single(journal.Read()));
    }

    [Fact]
    public void A_given_time_is_kept_with_its_offset()
    {
        using var folder = new TempFolder();
        var journal = Open(folder);
        var at = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.FromHours(1));
        journal.Write(Line() with { When = at });
        Assert.Contains("\"when\":\"2026-07-01T09:00:00.000+01:00\"", File.ReadAllText(journal.Path), StringComparison.Ordinal);
        Assert.Equal(at, journal.Read()[0].When);
        Assert.Equal(TimeSpan.FromHours(1), journal.Read()[0].When.Offset);
    }

    [Theory]
    [InlineData("", "delete", "normal", "auto")]
    [InlineData("mail-triage", "", "normal", "auto")]
    [InlineData("mail-triage", "de\nlete", "normal", "auto")]
    [InlineData("mail-triage", "delete", "secret", "auto")]
    [InlineData("mail-triage", "delete", "normal", "robot")]
    public void A_line_that_isnt_valid_is_refused_and_nothing_written(string tool, string what, string sensitivity, string by)
    {
        using var folder = new TempFolder();
        var journal = Open(folder);
        Assert.Throws<ArgumentException>(() => journal.Write(new JournalEntry { Tool = tool, What = what, Sensitivity = sensitivity, By = by }));
        Assert.False(File.Exists(journal.Path));
        Assert.False(journal.TryWrite(new JournalEntry { Tool = tool, What = what, Sensitivity = sensitivity, By = by }));
    }

    [Fact]
    public void Read_filters_by_time_and_tool()
    {
        using var folder = new TempFolder();
        var at = Now;
        var journal = Open(folder, () => at);
        journal.Write(Line("delete"));
        at = Now.AddHours(1);
        journal.Write(Line("add", tool: "calendar-bank-holidays", subject: "Boxing Day"));
        at = Now.AddHours(2);
        journal.Write(Line("file"));

        Assert.Equal(3, journal.Read().Count);
        Assert.Equal(["add", "file"], journal.Read(since: Now.AddHours(1)).Select(e => e.What));
        Assert.Equal(["delete", "file"], journal.Read(tool: "mail-triage").Select(e => e.What));
        Assert.Equal(["file"], journal.Read(since: Now.AddMinutes(61), tool: "mail-triage").Select(e => e.What));
        Assert.Empty(journal.Read(tool: "nobody"));
        // The same moment in another offset is the same time.
        Assert.Equal(2, journal.Read(since: Now.AddHours(1).ToOffset(TimeSpan.FromHours(-5))).Count);
    }

    [Fact]
    public void A_missing_file_reads_as_empty()
    {
        using var folder = new TempFolder();
        Assert.Empty(new Journal(System.IO.Path.Combine(folder.Path, "nowhere", "journal.jsonl"), UniqueLock()).Read());
    }

    [Fact]
    public void Lines_that_arent_journal_lines_and_a_cut_off_last_line_are_left_out()
    {
        using var folder = new TempFolder();
        var journal = Open(folder);
        journal.Write(Line("delete"));
        File.AppendAllText(journal.Path, "not json\n{\"tool\":\"x\"}\n[1,2]\n{\"tool\":\"mail-triage\",\"what\":\"fi", new UTF8Encoding(false));
        Assert.Equal(["delete"], journal.Read().Select(e => e.What));

        // The next write ends the cut-off line first, so its own line is whole.
        journal.Write(Line("keep"));
        Assert.Equal(["delete", "keep"], journal.Read().Select(e => e.What));
        Assert.Contains("\"what\":\"fi\n{\"id\":", File.ReadAllText(journal.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_sensitivity_read_back_counts_as_restricted()
    {
        using var folder = new TempFolder();
        var journal = Open(folder);
        File.WriteAllText(journal.Path, "{\"when\":\"2026-11-25T07:00:00.000+00:00\",\"tool\":\"t\",\"what\":\"w\",\"sensitivity\":\"newer-level\",\"extra\":1}\n");
        Assert.Equal(Sensitivity.Restricted, Assert.Single(journal.Read()).Sensitivity);
    }

    [Fact]
    public async Task Writers_at_the_same_moment_lose_nothing()
    {
        using var folder = new TempFolder();
        var path = folder.File();
        var name = UniqueLock();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            var journal = new Journal(path, name, lockTimeoutSeconds: 60);   // each its own object, as separate tools would be
            for (var n = 0; n < 25; n++)
                journal.Write(new JournalEntry { Tool = $"tool-{writer}", What = "did", Subject = $"{writer}-{n}" });
        }, TestContext.Current.CancellationToken)));
        var lines = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(200, lines.Length);
        var all = new Journal(path, name).Read();
        Assert.Equal(200, all.Count);
        Assert.Equal(200, all.Select(e => e.Id).Distinct().Count());
        Assert.Equal(25, all.Count(e => e.Tool == "tool-3"));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void A_write_waits_for_the_lock_and_times_out()
    {
        using var folder = new TempFolder();
        var name = UniqueLock();
        var journal = new Journal(folder.File(), name, lockTimeoutSeconds: 0.3);
        using (Locks.Exclusive(name, 5))
        {
            Assert.Throws<TimeoutException>(() => journal.Write(Line()));
            Assert.False(journal.TryWrite(Line()));
            Assert.Empty(journal.Read());   // reading takes no lock
        }
        journal.Write(Line());
        Assert.Single(journal.Read());
    }

    [Fact]
    public void The_default_lock_is_the_folder_and_file_name()
    {
        Assert.Equal("my-right-hand-journal", Journal.DefaultLockName(@"C:\Users\someone\AppData\Local\My-Right-Hand\journal.jsonl"));
        Assert.Throws<ArgumentException>(() => new Journal("j.jsonl", @"Local\x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Journal("j.jsonl", keepDays: 0));
    }

    [Fact]
    public void Old_lines_are_pruned_on_a_write_about_once_a_day()
    {
        using var folder = new TempFolder();
        var at = Now.AddDays(-100);
        var journal = Open(folder, () => at, keepDays: 90);
        journal.Write(Line("old"));           // 100 days ago
        at = Now.AddDays(-89);
        journal.Write(Line("recent"));        // 89 days ago

        // 90.5 days after the first: past KeepDays, but within the day's grace, so not rewritten yet.
        at = Now.AddDays(-100).AddDays(90.5);
        journal.Write(Line("a"));
        Assert.Equal(["old", "recent", "a"], journal.Read().Select(e => e.What));

        // Now (100 days after the first): due, so the old line goes and the rest stay, in order.
        at = Now;
        journal.Write(Line("b"));
        Assert.Equal(["recent", "a", "b"], journal.Read().Select(e => e.What));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void Prune_by_hand_says_how_many_went()
    {
        using var folder = new TempFolder();
        var at = Now.AddDays(-30);
        var journal = Open(folder, () => at, keepDays: 7);
        journal.Write(Line("a"));
        journal.Write(Line("b"));
        at = Now;
        Assert.Equal(2, journal.Prune());
        Assert.Empty(journal.Read());
        Assert.Equal(0, journal.Prune());
    }

    [Fact]
    public void A_reader_holding_the_file_open_doesnt_stop_a_write_and_pruning_waits()
    {
        using var folder = new TempFolder();
        var at = Now.AddDays(-100);
        var journal = Open(folder, () => at);
        journal.Write(Line("old"));
        at = Now;
        using (new FileStream(journal.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // shares writing, not deleting
        {
            journal.Write(Line("new"));   // the move over it is refused: written, and pruning waits
            Assert.Equal(["old", "new"], journal.Read().Select(e => e.What));
        }
        journal.Write(Line("next"));
        Assert.Equal(["new", "next"], journal.Read().Select(e => e.What));
        Assert.Single(Directory.GetFiles(folder.Path));
    }
}
