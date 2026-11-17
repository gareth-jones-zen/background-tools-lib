using PyCompat;

namespace ReviewQueue.Tests;

public class SettingsTests
{
    private static object? Section(string toml)
    {
        var table = PyToml.Read(toml, "x.toml");
        return table.TryGetValue("review", out var value) ? value : null;
    }

    private static ReviewSettings Read(string toml) =>
        ReviewSettings.Read(Section(toml), "x.toml", p => p.Replace("%LOCALAPPDATA%", @"C:\L", StringComparison.Ordinal));

    [Fact]
    public void A_review_section_is_read()
    {
        var settings = Read("[review]\nfile = \"%LOCALAPPDATA%/claude-ea/pending.json\"\nsource = \"team-leave\"\n");
        Assert.Equal(new ReviewSettings(@"C:\L/claude-ea/pending.json", "team-leave"), settings);
        Assert.Equal("claude-ea-pending", settings.Open().LockName);
        Assert.Equal("mine", Read("[review]\nfile = \"q.json\"\nsource = \"s\"\nlock = \"mine\"\n").Open().LockName);
    }

    [Theory]
    [InlineData("review = 1", "x.toml: review must be a [review] table.")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = \"s\"\nsorce = \"t\"", "sorce under [review] in x.toml isn't a [review] setting (they're file, source, lock).")]
    [InlineData("[review]\nsource = \"s\"", "[review] in x.toml needs file, the review queue's path, as \"%LOCALAPPDATA%/my-tools/review.json\".")]
    [InlineData("[review]\nfile = \"q.json\"", "[review] in x.toml needs source, the name this tool's items go under, as \"my-tool\".")]
    [InlineData("[review]\nfile = \"\"\nsource = \"s\"", "file under [review] in x.toml should be text, and not empty.")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = 3", "source under [review] in x.toml should be text, and not empty.")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = \"s\"\nlock = 'Global\\x'", "lock under [review] in x.toml can't contain a backslash.")]
    public void Bad_settings_are_refused(string toml, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => Read(toml)).Message);
}
