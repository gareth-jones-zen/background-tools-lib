using System.Text.Json.Nodes;
using PyCompat;

namespace RepoCommit.Tests;

public class SettingsTests
{
    private static object? Section(string toml)
    {
        var table = PyToml.Read(toml, "x.toml");
        return table.TryGetValue("git", out var value) ? value : null;
    }

    private static GitSettings Read(string toml) => GitSettings.Read(Section(toml), "x.toml", p => @"C:\base\" + p);

    [Fact]
    public void A_git_section_is_read()
    {
        var settings = Read("[git]\nrepo = \"..\"\npush = true\ntrailers = [\"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>\"]\n");
        Assert.Equal((@"C:\base\..", true, true), (settings.Repo, settings.Commit, settings.Push));
        Assert.Equal(["Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"], settings.Trailers);
        Assert.Equal(@"C:\", settings.Open().Repo);

        var defaults = Read("[git]\nrepo = \"r\"\n");
        Assert.Equal((true, false), (defaults.Commit, defaults.Push));
        Assert.Empty(defaults.Trailers);
        Assert.False(Read("[git]\nrepo = \"r\"\ncommit = false\n").Commit);
    }

    [Fact]
    public void A_json_object_is_read_the_same_way()
    {
        var json = JsonNode.Parse("""{"repo": "..", "push": true, "trailers": ["Co-Authored-By: X <x@example.com>"]}""");
        var settings = GitSettings.Read(json, "x.json", p => p, "\"git\"");
        Assert.Equal(("..", true, true), (settings.Repo, settings.Commit, settings.Push));
        Assert.Equal(["Co-Authored-By: X <x@example.com>"], settings.Trailers);
        Assert.Equal("repo under \"git\" in x.json should be text, and not empty.",
            Assert.Throws<FormatException>(() => GitSettings.Read(JsonNode.Parse("""{"repo": 5}"""), "x.json", p => p, "\"git\"")).Message);
        Assert.Equal("push under \"git\" in x.json should be true or false.",
            Assert.Throws<FormatException>(() => GitSettings.Read(JsonNode.Parse("""{"repo": "r", "push": "yes"}"""), "x.json", p => p, "\"git\"")).Message);
    }

    [Theory]
    [InlineData("git = 1", "x.toml: git must be a [git] table.")]
    [InlineData("[git]\nrepo = \"r\"\nrepository = \"s\"", "repository under [git] in x.toml isn't a [git] setting (they're repo, commit, push, trailers).")]
    [InlineData("[git]\npush = true", "[git] in x.toml needs repo, the git repository's folder, as \"..\".")]
    [InlineData("[git]\nrepo = \"\"", "repo under [git] in x.toml should be text, and not empty.")]
    [InlineData("[git]\nrepo = \"r\"\npush = 1", "push under [git] in x.toml should be true or false.")]
    [InlineData("[git]\nrepo = \"r\"\ncommit = \"yes\"", "commit under [git] in x.toml should be true or false.")]
    [InlineData("[git]\nrepo = \"r\"\ntrailers = \"Co-Authored-By: X\"", "trailers under [git] in x.toml should be a list of lines such as \"Co-Authored-By: Name <name@example.com>\".")]
    [InlineData("[git]\nrepo = \"r\"\ntrailers = [\"not a trailer\"]", "trailers under [git] in x.toml should be a list of lines such as \"Co-Authored-By: Name <name@example.com>\".")]
    [InlineData("[git]\nrepo = \"r\"\ntrailers = [\"X: a\\nY: b\"]", "trailers under [git] in x.toml should be a list of lines such as \"Co-Authored-By: Name <name@example.com>\".")]
    [InlineData("[git]\nrepo = \"r\"\ntrailers = [1]", "trailers under [git] in x.toml should be a list of lines such as \"Co-Authored-By: Name <name@example.com>\".")]
    public void Bad_settings_are_refused(string toml, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => Read(toml)).Message);
}
