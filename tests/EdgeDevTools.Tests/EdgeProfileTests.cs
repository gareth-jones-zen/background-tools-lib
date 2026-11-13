namespace EdgeDevTools.Tests;

/// <summary>EdgeProfile: the key has to be the string Python's people-first-client hashes, byte for byte, or a
/// Python and a .NET tool would open the same profile at once during the changeover. The expected values were
/// computed by running Python (people_first.lock_name, win-background 0.3.0) on 13 Nov 2026; the live comparison
/// with Python itself is in PeopleFirstClient.Tests.</summary>
public class EdgeProfileTests
{
    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\claude-ea\org-directory-edge-profile", "271dbba6c040cb24")]
    [InlineData(@"C:\Users\someone\AppData\Local\claude-ea\org-directory-edge-profile\", "271dbba6c040cb24")]
    [InlineData(@"c:/users/SOMEONE/AppData//Local/./claude-ea/x/../org-directory-edge-profile", "271dbba6c040cb24")]
    [InlineData("C:/x//y/./z/../w", "8fa9b10b5595d4c5")]
    [InlineData(@"C:\foo. ", "5ebc2bc55967076b")]
    [InlineData(@"C:\foo\.", "5ebc2bc55967076b")]
    [InlineData(@"\\srv\share", "c0895788c2fabd98")]
    [InlineData(@"\\srv\share\a\", "d594ede1235125e8")]
    [InlineData(@"C:\", "3dc8472eaebe1a9d")]
    [InlineData(@"C:\a\..\..\..\b", "c18c3dc926c0dd64")]
    [InlineData(@"c:\Lower\CASE", "c7597b663f4b4de0")]
    [InlineData("C:\\Users\\\u0130x\\\u039f\u0394\u039f\u03a3\\\u00df\u1e9e", "84d376d440f337c8")]
    [InlineData("C:\\p\\\u03a3\u03a3.\\A\u03a3", "cdecf1a17fe9296b")]
    [InlineData("C:\\a\\x\u03a3\u0301y\\x\u03a3\u0301", "42f8d201473c7130")]
    public void The_key_is_the_one_Python_computes(string path, string key)
    {
        Assert.Equal(key, EdgeProfile.Key(path));
        Assert.Equal("people-first-edge-" + key, EdgeProfile.LockName("people-first-edge", path));
    }

    [Theory]
    [InlineData(@"C:\Users\x\profile\", @"C:\Users\x\profile")]
    [InlineData("C:/x//y/./z/../w", @"C:\x\y\w")]
    [InlineData(@"C:\foo. ", @"C:\foo")]
    [InlineData(@"\\srv\share", @"\\srv\share\")]
    [InlineData(@"\\srv\share\a\", @"\\srv\share\a")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"c:\Lower\CASE", @"c:\Lower\CASE")]
    public void Normalise_spells_paths_as_pathlib_does(string path, string expected) =>
        Assert.Equal(expected, EdgeProfile.Normalise(path));

    [Fact]
    public void A_relative_path_is_from_the_current_folder() =>
        Assert.Equal(EdgeProfile.Key(Path.Combine(Environment.CurrentDirectory, "rel", "p")), EdgeProfile.Key(@"rel\p"));

    [Theory]
    [InlineData("ABC", "abc")]
    [InlineData("\u0130", "i\u0307")]                 // Python's full mapping: two characters
    [InlineData("\u039f\u0394\u039f\u03a3", "\u03bf\u03b4\u03bf\u03c2")]   // final sigma
    [InlineData("\u03a3", "\u03c3")]                  // alone: not final (nothing cased before it)
    [InlineData("A\u03a3B", "a\u03c3b")]
    [InlineData("A\u03a3.", "a\u03c2.")]              // '.' is case-ignorable
    [InlineData("\u1e9e", "\u00df")]
    public void PythonLower_matches_str_lower(string text, string expected) =>
        Assert.Equal(expected, PyCompat.PyStr.Lower(text));

    [Fact]
    public void A_missing_or_stale_lock_file_is_not_open()
    {
        var folder = Directory.CreateTempSubdirectory("edge-profile-test-").FullName;
        try
        {
            Assert.False(EdgeProfile.IsOpen(folder));
            File.WriteAllText(Path.Combine(folder, "lockfile"), "");
            Assert.False(EdgeProfile.IsOpen(folder));   // left by a crash: nobody holds it
            using (new FileStream(Path.Combine(folder, "lockfile"), FileMode.Open, FileAccess.Write, FileShare.Read))
                Assert.True(EdgeProfile.IsOpen(folder));   // held as Edge holds it
            Assert.Equal("", File.ReadAllText(Path.Combine(folder, "lockfile")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
