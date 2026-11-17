using PyCompat;
using Tomlyn.Model;

namespace PyCompat.Tests;

/// <summary>BaseSettings: a tool's settings with the shared base file they name.</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "base-settings-" + Guid.NewGuid().ToString("N"));

    private const string Base = """
        login_hint = "me@example.com"
        tenant = "contoso"
        msal_cache = "%ROOT%/msal-cache.json"

        [paths]
        ROOT = "%LOCALAPPDATA%/tools"
        EDGE = "%ROOT%/edge-profile"
        LOOP = "%LOOP%/x"

        [git]
        repo = ".."
        push = true
        trailers = ["Co-Authored-By: someone"]

        [review]
        file = "%ROOT%/pending.json"
        lock = "tools-pending"
        """;

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "tools"));
        File.WriteAllText(Path.Combine(dir, "tools", "base-config.toml"), Base);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string? Env(string name) => name == "LOCALAPPDATA" ? @"C:\Users\me\AppData\Local" : null;

    private SettingsWithBase Read(string text, params string[] known) =>
        BaseSettings.Read(text, Path.Combine(dir, "tools", "tool.toml"), Env, known);

    [Fact]
    public void Without_a_base_the_settings_are_as_written()
    {
        var read = Read("tenant = \"own\"\n", "tenant");
        Assert.Null(read.BasePath);
        Assert.Equal("own", read.Raw["tenant"]);
        Assert.Null(read.Env("ROOT"));
    }

    [Fact]
    public void Known_settings_come_from_the_base_and_the_tools_own_win()
    {
        var read = Read("base = \"base-config.toml\"\ntenant = \"own\"\nlog_file = \"x.log\"\n", "tenant", "login_hint", "log_file");
        Assert.EndsWith("base-config.toml", read.BasePath);
        Assert.False(read.Raw.ContainsKey("base"));
        Assert.Equal("own", read.Raw["tenant"]);
        Assert.Equal("me@example.com", read.Raw["login_hint"]);
        Assert.False(read.Raw.ContainsKey("msal_cache"));   // not one of this tool's settings
        Assert.False(read.Raw.ContainsKey("paths"));
    }

    [Fact]
    public void Tables_merge_key_by_key()
    {
        var read = Read("base = \"base-config.toml\"\n[review]\nsource = \"ev\"\nlock = \"mine\"\n[git]\n", "review", "git");
        var review = (TomlTable)read.Raw["review"];
        Assert.Equal(("ev", "mine", "%ROOT%/pending.json"), ((string)review["source"], (string)review["lock"], (string)review["file"]));
        var git = (TomlTable)read.Raw["git"];
        Assert.Equal(true, git["push"]);
        ((TomlTable)read.Raw["git"])["push"] = false;   // a copy: the next read still sees the base's
        Assert.Equal(true, ((TomlTable)Read("base = \"base-config.toml\"\n[git]\n", "git").Raw["git"])["push"]);
    }

    [Fact]
    public void A_table_applies_only_to_a_tool_that_has_the_section()
    {
        var without = Read("base = \"base-config.toml\"\n", "review", "git", "tenant");
        Assert.False(without.Raw.ContainsKey("review"));
        Assert.False(without.Raw.ContainsKey("git"));
        Assert.Equal("contoso", without.Raw["tenant"]);
        var bare = Read("base = \"base-config.toml\"\n[git]\n", "git");
        Assert.Equal("..", ((TomlTable)bare.Raw["git"])["repo"]);
    }

    [Fact]
    public void Paths_are_variables_before_the_environment()
    {
        var read = Read("base = \"base-config.toml\"\n", "msal_cache");
        Assert.Equal(@"C:\Users\me\AppData\Local/tools", read.Env("ROOT"));
        Assert.Equal(@"C:\Users\me\AppData\Local/tools/edge-profile", read.Env("edge"));   // names ignore case, as Windows does
        Assert.Equal(@"C:\Users\me\AppData\Local/tools/msal-cache.json", PyPath.ExpandVars((string)read.Raw["msal_cache"], read.Env));
        Assert.Equal(@"C:\Users\me\AppData\Local", read.Env("LOCALAPPDATA"));
        Assert.Equal("%LOOP%/x", read.Env("LOOP"));   // a loop is left unexpanded, not recursed
    }

    [Fact]
    public void A_missing_or_bad_base_says_so()
    {
        Assert.Contains("doesn't exist", Assert.Throws<FormatException>(() => Read("base = \"nope.toml\"\n")).Message);
        Assert.Contains("should be the path", Assert.Throws<FormatException>(() => Read("base = 3\n")).Message);
        File.WriteAllText(Path.Combine(dir, "tools", "chain.toml"), "base = \"base-config.toml\"\n");
        Assert.Contains("can't name a base", Assert.Throws<FormatException>(() => Read("base = \"chain.toml\"\n")).Message);
        File.WriteAllText(Path.Combine(dir, "tools", "twice.toml"), "a = 1\na = 2\n");
        Assert.Throws<FormatException>(() => Read("base = \"twice.toml\"\n", "a"));
    }
}
