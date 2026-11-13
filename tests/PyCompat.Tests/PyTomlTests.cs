using Tomlyn.Model;

namespace PyCompat.Tests;

/// <summary>PyToml (moved from cover-rota) against the two things Tomlyn 2.11.1's TomlSerializer gets wrong (an array of tables reopened
/// after another table loses its earlier entries; a duplicate key is taken as the last value), and the shapes that
/// were already right.</summary>
public sealed class PyTomlTests
{
    [Theory]
    [InlineData("[[a]]\nn=1\n[y]\n[[a]]\nn=2\n[[a]]\nn=3\n", 3)]
    [InlineData("[[a]]\nn=1\n[y.z]\nq=1\n[[a]]\nn=2\n", 2)]
    [InlineData("[[a]]\nx=1\n[[a.b]]\ny=1\n[[a]]\nx=2\n", 2)]
    [InlineData("[[a]]\n[a.sub]\nk=1\n[[a]]\nx=2\n", 2)]
    [InlineData("[x]\n[[a]]\nn=1\n[[a]]\nn=2\n[y]\n", 2)]
    public void A_reopened_array_of_tables_keeps_every_entry(string text, int count) =>
        Assert.Equal(count, ((TomlTableArray)PyToml.Read(text, "t")["a"]).Count);

    [Fact]
    public void A_duplicate_key_is_refused() =>
        Assert.ThrowsAny<Exception>(() => PyToml.Read("a = 1\na = 2\n", "t"));

    [Fact]
    public void Values_dotted_keys_inline_tables_and_sub_arrays()
    {
        var t = PyToml.Read("""
            x.y = "s"
            d = 2027-01-01
            [arch]
            pending = "in"
            [[arch.person]]
            name = "A B"
            [[arch.person]]
            name = "C D"
            [[squad]]
            members = ["Ann", { name = "Bo", aliases = ["B"] }]
            """, "t");
        Assert.Equal("s", ((TomlTable)t["x"])["y"]);
        Assert.IsType<Tomlyn.TomlDateTime>(t["d"]);
        Assert.Equal(2, ((TomlTableArray)((TomlTable)t["arch"])["person"]).Count);
        var members = (TomlArray)((TomlTableArray)t["squad"])[0]["members"];
        Assert.Equal("Ann", members[0]);
        Assert.Equal("Bo", ((TomlTable)members[1]!)["name"]);
    }
}

/// <summary>On the EA's real settings files (none has the shapes Tomlyn gets wrong), the strict reader gives exactly
/// what Tomlyn's own model gives, so switching a tool to it changes nothing for those files. Skipped off GJ's laptop.</summary>
public sealed class PyTomlRealSettingsTests
{
    private static readonly string Tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "OneDrive - Zen Internet", "claude-executive-assistant", "tools");

    [Fact]
    public void The_EA_settings_read_the_same_as_before()
    {
        Assert.SkipUnless(Directory.Exists(Tools), "not on GJ's laptop");
        var files = Directory.GetFiles(Tools, "*.toml");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var strict = Dump(PyToml.Read(text, file));
            var before = Dump(Tomlyn.TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable));
            Assert.True(strict == before, Path.GetFileName(file));
        }
    }

    /// <summary>A canonical text form of a TOML model: keys in order, values with their types.</summary>
    private static string Dump(object? value) => value switch
    {
        TomlTable t => "{" + string.Join(",", t.Select(kv => kv.Key + "=" + Dump(kv.Value))) + "}",
        TomlTableArray a => "[[" + string.Join(",", a.Select(Dump)) + "]]",
        TomlArray a => "[" + string.Join(",", a.Select(Dump)) + "]",
        null => "null",
        _ => value.GetType().Name + ":" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
