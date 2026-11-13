using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyCompat.Tests;

/// <summary>Checks against tables Python 3.14 printed (Generated/, from tools/gen): the whole of a behaviour where
/// it can be enumerated, and the cases the ports met where it can't.</summary>
public class PythonOracleTests
{
    [Fact]
    public void The_tables_come_from_the_python_the_tools_run()
    {
        Assert.Equal("3.14.5", PythonExpected.PythonVersion);
        Assert.Equal("3.14.5", PythonWhitespace.PythonVersion);
        Assert.Equal("3.14.5", PyHtml.PythonVersion);
    }

    [Fact]
    public void Isspace_matches_python_for_every_bmp_character()
    {
        var expected = PythonWhitespace.CodePoints.ToHashSet();
        for (var c = 0; c <= 0xFFFF; c++)
            Assert.True(expected.Contains(c) == PyStr.IsSpace((char)c), $"U+{c:X4}");
    }

    [Fact]
    public void Fromisoformat_matches_python()
    {
        var london = PyTime.Zone("Europe/London");
        foreach (var (input, expected) in PythonExpected.DateTimes)
        {
            string actual;
            try
            {
                var aware = PyTime.FromIsoFormat(input, london);
                actual = HasOffset(expected) ? PyTime.IsoFormat(aware) : PyTime.Iso(PyTime.FromIso(input));
            }
            catch (FormatException error)
            {
                actual = "ERR:" + error.Message;
            }
            Assert.True(expected == actual, $"{input}: Python {expected}, here {actual}");
        }
    }

    private static bool HasOffset(string iso) => !iso.StartsWith("ERR", StringComparison.Ordinal) && iso.Length > 19 && iso[^6] is '+' or '-';

    [Fact]
    public void A_naive_fromiso_refuses_an_offset_and_an_aware_one_keeps_it()
    {
        Assert.Throws<FormatException>(() => PyTime.FromIso("2026-11-12T09:05:07+01:00"));
        Assert.Equal(TimeSpan.FromHours(1), PyTime.FromIsoFormat("2026-11-12T09:05:07+01:00", TimeZoneInfo.Utc).Offset);
        Assert.Throws<FormatException>(() => PyTime.FromIsoFormat("2026-11-12T09:05:07+01:00:30", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Time_fromisoformat_matches_python()
    {
        foreach (var (input, expected) in PythonExpected.Times)
        {
            if (expected.StartsWith("ERR", StringComparison.Ordinal) || expected.Contains('+'))
            {
                Assert.Throws<FormatException>(() => PyTime.TimeFromIso(input));   // aware times are refused here
                continue;
            }
            var time = PyTime.TimeFromIso(input);
            var text = new DateTime(2000, 1, 1).Add(time).ToString(time.Ticks % TimeSpan.TicksPerSecond == 0 ? "HH:mm:ss" : "HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            Assert.True(expected == text, $"{input}: Python {expected}, here {text}");
        }
    }

    [Fact]
    public void Unified_diff_matches_difflib()
    {
        foreach (var (a, b, n, of, lines) in PythonExpected.Diffs)
        {
            Assert.Equal(of, UnifiedDiff.Of(a, b, "a", "b", n));
            Assert.Equal(lines, string.Join("\n", UnifiedDiff.Lines(a.Select(x => x.TrimEnd('\n')).ToList(), b.Select(x => x.TrimEnd('\n')).ToList(), "a", "b", n)));
        }
    }

    [Fact]
    public void Json_dumps_matches_python_in_every_mode()
    {
        foreach (var (json, plain, indented, sorted, zeroSorted) in PythonExpected.Json)
        {
            var node = PyJson.Parse(json);
            Assert.Equal(plain, PyJson.Dump(node));
            Assert.Equal(indented, PyJson.Dump(node, 2, ensureAscii: false));
            Assert.Equal(sorted, PyJson.Dump(node, 1, sortKeys: true));
            Assert.Equal(zeroSorted, PyJson.Dump(node, 0, ensureAscii: false, sortKeys: true));
        }
    }

    [Fact]
    public void Unescape_matches_python()
    {
        foreach (var (input, expected) in PythonExpected.Unescape)
            Assert.True(expected == PyHtml.Unescape(input), input);
    }

    [Fact]
    public void Lower_matches_python()
    {
        foreach (var (input, expected) in PythonExpected.Lower)
            Assert.True(expected == PyStr.Lower(input), input);
    }

    [Fact]
    public void Int_matches_python()
    {
        foreach (var (input, expected) in PythonExpected.Ints)
        {
            if (expected == "ERR")
                Assert.Throws<FormatException>(() => PyStr.Int(input));
            else if (!long.TryParse(expected, CultureInfo.InvariantCulture, out var value))
                Assert.Throws<OverflowException>(() => PyStr.Int(input));   // past a long
            else
                Assert.Equal(value, PyStr.Int(input));
        }
    }
}

/// <summary>mail-triage's port: the regex dialect and its guard (expected values from Python 3.14).</summary>
public class PyRegexTests
{
    [Theory]
    [InlineData("(?<name>x)", "named group")]
    [InlineData("(?P=name)", "isn't a group before it")]
    [InlineData("(?P<n>a)(?P<n>b)", "twice")]
    [InlineData("(?P<né>a)", "ASCII")]
    [InlineData(@"\p{L}", @"\p")]
    [InlineData("a*+", "possessive")]
    [InlineData("a{2}+", "possessive")]
    [InlineData("x{3,2}", "minimum is greater")]
    [InlineData("abc(?i)", "middle")]
    [InlineData("(?x)a b", "inline flag x")]
    [InlineData("(?a)x", "inline flag a")]
    [InlineData("(?L)x", "inline flag L")]
    [InlineData("(?(1)a|b)", "conditional")]
    [InlineData(@"(a)\1", "backreference")]
    [InlineData(@"\G", @"\G")]
    [InlineData(@"\N{EM DASH}", @"\N")]
    [InlineData(@"[\B]", @"\B inside")]
    public void The_guard_refuses_what_python_and_dotnet_read_differently(string pattern, string why)
    {
        var error = Assert.Throws<PyRegexUnsupportedException>(() => PyRegex.Translate(pattern));
        Assert.Contains(why, error.Message);
    }

    [Fact]
    public void A_group_reference_with_ignorecase_is_refused()
    {
        Assert.Null(PyRegex.Problem("(?P<n>a)-") is { } p ? p : null);
        var error = Assert.Throws<PyRegexUnsupportedException>(() => PyRegex.Translate("(?P<n>a)(?P=n)", true));
        Assert.Contains("IGNORECASE", error.Message);
    }

    [Theory]
    // Read the same way by both since v0.3, where they were refused before: classes, escapes and quantifiers are
    // worked out as Python reads them, so .NET's own meanings ("-[" subtraction, "[:") never arise.
    [InlineData("(?P<name>x)(?P=name)")]
    [InlineData("[a-z-[aeiou]]")]
    [InlineData("x{,3}")]
    [InlineData("(?u)x")]
    [InlineData(@"\z")]
    [InlineData(@"\U0001F600")]
    [InlineData("(?>a)")]
    [InlineData("[[:alpha:]]")]
    [InlineData(@"[\W]")]
    public void Patterns_refused_before_v03_are_translated(string pattern) => PyRegex.Translate(pattern);

    [Theory]
    [InlineData(@"@news\.example\.com$")]
    [InlineData(@"^[^\n]*\bsprint (\d+|notes)\b")]
    [InlineData(@"@([\w-]+\.)*salesforce\.com$")]
    [InlineData(@"^(\w+ )?(holidays?|annual leave|leave|bank holiday)\b")]
    [InlineData("(?is)<tr.*?</tr>")]
    [InlineData("(?i:abc)def")]
    [InlineData(@"you.re invited|connect:")]
    [InlineData(@"[]a]")]
    [InlineData(@"\Z")]
    [InlineData(@"Jordan\ Lee\s+(annual leave|on leave)\b")]
    public void The_patterns_in_use_are_accepted(string pattern) => Assert.Null(PyRegex.Problem(pattern));

    [Theory]
    [InlineData(@"\w", "\u0301", false)]        // a combining mark: not a word character in Python
    [InlineData(@"\w", "\u2160", true)]         // Roman numeral one (Nl): a word character in Python
    [InlineData(@"\w", "\u00b2", true)]         // superscript two (No)
    [InlineData(@"\w", "\u203f", false)]        // undertie (Pc): only "_" in Python
    [InlineData(@"\s", "\u001c", true)]         // a file separator is whitespace in Python
    [InlineData(@"x\Z", "x\n", false)]          // \Z is the very end in Python
    [InlineData(@"x$", "x\n", true)]            // $ also matches before a final newline, in both
    [InlineData(@"\bcafe\b", "cafe\u0301", true)]
    [InlineData(@"\b\u00e9", "x\u00e9", false)]
    public void Translated_patterns_behave_as_pythons(string pattern, string text, bool expected) =>
        Assert.Equal(expected, PyRegex.IsMatch(pattern, text));

    [Theory]
    [InlineData("sprint", "\u017fprint", true)]   // long s: Python's re.I folds it to "s"; .NET's doesn't, so it's added
    [InlineData("^Kim", "\u212aim", true)]        // the Kelvin sign: both fold it to "k"
    [InlineData("lis", "L\u0130S", true)]          // dotted capital I
    [InlineData("[a-z]+", "\u0131", true)]         // dotless i, in a range
    [InlineData("[^is]", "\u0131", false)]
    [InlineData("(?i)sprint", "\u017fprint", true)]
    public void Case_insensitive_matching_folds_as_pythons(string pattern, string text, bool expected) =>
        Assert.Equal(expected, PyRegex.IsMatch(pattern, text, true));

    [Fact]
    public void Match_and_fullmatch_anchor_as_python_does()
    {
        Assert.NotNull(PyRegex.Get("bank holiday\\b", true).MatchStart("Bank holiday (England)"));
        Assert.Null(PyRegex.Get("bank holiday\\b", true).MatchStart("The bank holiday"));
        Assert.True(PyRegex.Get(PyRegex.Escape("Weekly 1-2-1 - Dan"), true).FullMatch("weekly 1-2-1 - dan"));
        Assert.False(PyRegex.Get(PyRegex.Escape("Weekly 1-2-1 - Dan"), true).FullMatch("Weekly 1-2-1 - Dan\n"));
        Assert.True(PyRegex.Get("a|ab").FullMatch("ab"));
    }

    [Fact]
    public void Re_escape_is_pythons() =>
        Assert.Equal(@"Jordan\ O'Neil\-Smith\ \#1\&\~", PyRegex.Escape("Jordan O'Neil-Smith #1&~"));

    [Fact]
    public void A_slow_pattern_times_out_rather_than_hanging() =>
        Assert.Throws<RegexMatchTimeoutException>(() => PyRegex.IsMatch("^(a+)+$", new string('a', 40) + "!"));

    [Fact]
    public void Sub_findall_and_split_take_text_literally()
    {
        Assert.Equal("a$1b", PyRegex.Get("-").Sub("$1", "a-b"));
        Assert.Equal(["12", "3"], PyRegex.Get(@"\d+").FindAll("a12b3"));
        Assert.Equal(["a", "b", "c"], PyRegex.Get(",").Split("a,b,c"));
    }
}

/// <summary>mail-triage's port: isoformat, strftime, code points and London time.</summary>
public class PyTimeAndTextTests
{
    [Fact]
    public void Isoformat_in_every_form()
    {
        var t = new DateTime(2026, 11, 10, 15, 6, 29).AddTicks(1010200);
        Assert.Equal("2026-11-10T15", PyTime.Iso(t, "hours"));
        Assert.Equal("2026-11-10T15:06", PyTime.Iso(t, "minutes"));
        Assert.Equal("2026-11-10T15:06:29", PyTime.Iso(t, "seconds"));
        Assert.Equal("2026-11-10T15:06:29.101", PyTime.Iso(t, "milliseconds"));
        Assert.Equal("2026-11-10T15:06:29.101020", PyTime.Iso(t, "microseconds"));
        Assert.Equal("2026-11-10T15:06:29.101020", PyTime.Iso(t));
        Assert.Equal("2026-11-10T15:06:29", PyTime.Iso(new DateTime(2026, 11, 10, 15, 6, 29)));
        Assert.Equal("2026-11-10T15:06:29.000000", PyTime.Iso(new DateTime(2026, 11, 10, 15, 6, 29), "microseconds"));
        Assert.Equal(new DateTime(2026, 11, 10, 15, 6, 29).AddTicks(1010200), PyTime.Micro(t.AddTicks(7)));
        Assert.Equal("2026-11-10", PyTime.Iso(new DateOnly(2026, 11, 10)));
        Assert.Equal("2026-11-10T15:06+00:00", PyTime.IsoFormat(new DateTimeOffset(t, TimeSpan.Zero), "minutes"));
        Assert.Throws<ArgumentException>(() => PyTime.Iso(t, "days"));
    }

    [Fact]
    public void Strftime_in_english_and_only_the_supported_directives()
    {
        var t = new DateTime(2026, 11, 13, 9, 5, 0);
        Assert.Equal("Fri 13 Nov 09:05", PyTime.Strftime(t, "%a %d %b %H:%M"));
        Assert.Equal("13/11", PyTime.Strftime(t, "%d/%m"));
        Assert.Equal("Friday 13 November 2026 09AM 100%", PyTime.Strftime(t, "%A %d %B %Y %I%p 100%%"));
        Assert.Equal("26 12PM", PyTime.Strftime(new DateTime(2026, 1, 1, 12, 0, 0), "%y %I%p"));
        Assert.True(PyTime.Supported("%d/%m"));
        Assert.False(PyTime.Supported("%-d/%m"));
        Assert.False(PyTime.Supported("%j"));
        Assert.Throws<FormatException>(() => PyTime.Strftime(t, "%j"));
        Assert.Equal("Fri", PyTime.Day(new DateTimeOffset(t, TimeSpan.Zero)));
        Assert.Equal(4, PyTime.Weekday(t));
    }

    [Fact]
    public void Code_points_not_utf16_units()
    {
        Assert.Equal("ab\U0001F600", PyStr.Head("ab\U0001F600cd", 3));
        Assert.True(PyStr.CompareCodePoints("\U0001F600", "\uff5c") > 0);       // U+1F600 sorts after U+FF5C, as in Python
        Assert.True(string.CompareOrdinal("\U0001F600", "\uff5c") < 0);          // ordinal gets it the other way round
        Assert.Equal(["\uff5c", "\U0001F600"], new[] { "\U0001F600", "\uff5c" }.Order(PyStr.Order));
        Assert.Equal("x", PyStr.Strip("\u001c x \u2028"));
        Assert.Equal(["a", "b"], PyStr.Split(" a\u001fb "));
        Assert.True(PyStr.IsDigit("\u00b2"));
        Assert.False(PyStr.IsDigit(""));
        Assert.Throws<FormatException>(() => PyStr.Int("\u00b2"));
        Assert.Equal(3, PyStr.Int("\u0663"));
    }

    [Fact]
    public void London_time_as_zoneinfo_reads_it_under_invariant_globalisation()
    {
        foreach (var zone in new[] { PyTime.Zone("GMT Standard Time"), PyTime.Zone("Europe/London") })
        {
            Assert.Equal(TimeSpan.Zero, PyTime.At(zone, new DateTime(2026, 11, 12, 12, 0, 0)).Offset);
            Assert.Equal(TimeSpan.FromHours(1), PyTime.At(zone, new DateTime(2026, 7, 1, 12, 0, 0)).Offset);
            // 01:30 on 25 Oct 2026 happens twice: Python's fold=0 takes the first (summer time).
            Assert.Equal(TimeSpan.FromHours(1), PyTime.At(zone, new DateTime(2026, 10, 25, 1, 30, 0)).Offset);
            // 01:30 on 29 Mar 2026 doesn't happen: Python takes the offset from before the change.
            Assert.Equal(TimeSpan.Zero, PyTime.At(zone, new DateTime(2026, 3, 29, 1, 30, 0)).Offset);
            Assert.Equal(new DateTime(2026, 7, 1, 13, 0, 0), PyTime.Local(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), zone));
        }
        Assert.Same(TimeZoneInfo.Utc, PyTime.Zone("UTC"));
        Assert.Throws<TimeZoneNotFoundException>(() => PyTime.Zone("Nowhere/Special"));
    }

    [Fact]
    public void Time_max_is_pythons() => Assert.Equal("23:59:59.9999990", PyTime.TimeMax.ToString("hh\\:mm\\:ss\\.fffffff", CultureInfo.InvariantCulture));
}

/// <summary>onenote-sync's and teams-channel-notes' ports: html, expandtabs, strip, json and difflib.</summary>
public class OneNoteAndTeamsTests
{
    [Theory]
    [InlineData("a &amp; b", "a & b")]
    [InlineData("&#163;5 &#x2013; &pound;", "\u00a35 \u2013 \u00a3")]
    [InlineData("&amp &lt", "& <")]                       // no ";" for the legacy names
    [InlineData("&notit; &notin;", "\u00acit; \u2209")]    // the longest-prefix rule
    [InlineData("&#128; &#x9d; &#0; &#xD800; &#1114112;", "\u20ac \u009d \ufffd \ufffd \ufffd")]   // cp1252, kept, NUL, surrogate, too big
    [InlineData("&#1;&#xfffe;x", "x")]                    // forbidden code points vanish
    [InlineData("&#128522;", "\U0001F60A")]
    [InlineData("&bogus; & &;", "&bogus; & &;")]
    [InlineData("&#39", "'")]
    public void Unescape_is_pythons(string html, string expected) => Assert.Equal(expected, PyHtml.Unescape(html));

    [Fact]
    public void Every_html5_name_is_known()
    {
        Assert.Equal(2231, PyHtml.NamedCount);
        Assert.True(PyHtml.IsEntity("amp"));
        Assert.True(PyHtml.IsEntity("amp;"));
        Assert.False(PyHtml.IsEntity("bogus;"));
    }

    [Fact]
    public void Escape_is_pythons_with_and_without_quote()
    {
        Assert.Equal("\u00a3 &amp; &lt;b&gt; \"q\" 'a' \u2013", PyHtml.Escape("\u00a3 & <b> \"q\" 'a' \u2013", quote: false));
        Assert.Equal("&amp;&quot;&#x27;", PyHtml.Escape("&\"'"));
    }

    [Theory]
    [InlineData("\t", 4, "    ")]
    [InlineData("  \t", 4, "    ")]
    [InlineData(" \t \t", 4, "        ")]
    [InlineData("ab\tc", 8, "ab      c")]
    [InlineData("\U0001F600\tx", 4, "\U0001F600   x")]
    [InlineData("a\n\tb", 4, "a\n    b")]
    public void ExpandTabs_works_by_column(string text, int size, string expected) => Assert.Equal(expected, PyStr.ExpandTabs(text, size));

    [Theory]
    [InlineData("  a b  ", "a b")]
    [InlineData("\u001c\u2028a\u3000", "a")]
    [InlineData("", "")]
    [InlineData("\u200b", "\u200b")]
    [InlineData("\u001cx\u00a0", "x")]
    public void Strip_is_pythons(string text, string expected) => Assert.Equal(expected, PyStr.Strip(text));

    [Fact]
    public void Strip_with_chars_and_collapse()
    {
        Assert.Equal("Heading", PyStr.LStrip("## Heading", "#").Trim());
        Assert.Equal("Done", PyStr.RStrip("Done?:", "?:"));
        Assert.Equal("a b c", PyStr.Collapse(" a\u001f\u001fb\r\n\u0085c "));
    }

    [Fact]
    public void Slices_from_a_negative_start()
    {
        Assert.Equal([2, 3], Py.From([1, 2, 3], -2));
        Assert.Equal([1, 2, 3], Py.From([1, 2, 3], 0));
        Assert.Equal([1, 2, 3], Py.From([1, 2, 3], -9));
        Assert.Empty(Py.From([1, 2, 3], 5));
    }

    [Fact]
    public void Patch_bodies_and_state_are_pythons_json_dumps()
    {
        // onenote-sync's PATCH body (json.dumps of a list of dicts) and state file (indent=2, ensure_ascii=False).
        var body = new JsonArray(
            new JsonObject { ["target"] = "p:{1}", ["action"] = "insert", ["position"] = "after", ["content"] = "<li>\u00a3 \u2013 \"q\"\U0001F60A</li>" },
            new JsonObject { ["target"] = null, ["action"] = "replace", ["content"] = "x" });
        Assert.Equal("[{\"target\": \"p:{1}\", \"action\": \"insert\", \"position\": \"after\", \"content\": "
                     + "\"<li>\\u00a3 \\u2013 \\\"q\\\"\\ud83d\\ude0a</li>\"}, {\"target\": null, \"action\": \"replace\", \"content\": \"x\"}]",
            PyJson.Dump(body));
        var state = new JsonObject { ["other"] = new JsonObject { ["Digital"] = "x \u00a3" }, ["empty"] = new JsonObject() };
        Assert.Equal("{\n  \"other\": {\n    \"Digital\": \"x \u00a3\"\n  },\n  \"empty\": {}\n}", PyJson.Dump(state, 2, ensureAscii: false));
        Assert.Equal("{}", PyJson.Dump(new JsonObject(), 2, ensureAscii: false));
        Assert.Equal("\"caf\\u00e9\\n\"", PyJson.Quote("caf\u00e9\n"));
        Assert.Equal("\"caf\u00e9\\u0007\"", PyJson.Quote("caf\u00e9\u0007", ensureAscii: false));
    }

    [Fact]
    public void Dry_run_diff_is_pythons()
    {
        string[] old = ["Old"];
        string[] wanted = ["Registry migration - design done; Team A starts 11/11", "supplier cut-over 09/02/27", "Address pool - very low & being fixed"];
        Assert.Equal("--- on the page now\n+++ to post\n@@ -1 +1,3 @@\n-Old\n"
                     + "+Registry migration - design done; Team A starts 11/11\n+supplier cut-over 09/02/27\n+Address pool - very low & being fixed",
            string.Join("\n", UnifiedDiff.Lines(old, wanted, "on the page now", "to post", 0)));
        Assert.Empty(UnifiedDiff.Lines(old, old, "a", "b"));
        Assert.Equal("--- f (now)\n+++ f (after)\n@@ -0,0 +1,2 @@\n+{\n+}\n", UnifiedDiff.Of([], ["{\n", "}\n"], "f (now)", "f (after)"));
        Assert.Equal("", UnifiedDiff.Of(["x\n"], ["x\n"], "f", "g"));
    }
}

/// <summary>teams-channel-notes' port: Path.resolve() following links and correcting case.</summary>
public class PyPathResolveTests
{
    [Fact]
    public void Resolve_corrects_case_and_keeps_a_missing_tail()
    {
        var root = Path.Combine(Path.GetTempPath(), "pycompat-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Mixed Case"));
        try
        {
            var real = PyPath.Resolve(root);
            Assert.Equal(PyPath.Pure(Path.Combine(real, "Mixed Case")), PyPath.Resolve(Path.Combine(root, "MIXED CASE")));
            Assert.Equal(PyPath.Pure(Path.Combine(real, "Mixed Case", "nope", "x.md")),
                PyPath.Resolve(Path.Combine(root, "mixed case", "nope", "deeper", "..", "x.md")));
            Assert.Equal(PyPath.Pure(Path.Combine(root, "MIXED CASE")), PyPath.Absolute(Path.Combine(root, "MIXED CASE")));
            Assert.Equal(PyPath.Pure(Path.Combine(real, "Mixed Case")), PyPath.ResolveAgainst(root, "mixed case"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_matches_pathlib()
    {
        var root = Path.Combine(Path.GetTempPath(), "pycompat-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Mixed Case"));
        try
        {
            string[] paths = [root, Path.Combine(root, "mixed case", "missing", "..", "x.md"), Path.Combine(root, "MIXED CASE"),
                Path.GetTempPath(), Path.Combine(root, "nope", "deeper", "file.toml")];
            var python = PythonSays("import sys,pathlib; print('\\n'.join(str(pathlib.Path(p).resolve()) for p in sys.argv[1:]))", paths);
            Assert.SkipWhen(python is null, "python isn't here");
            Assert.Equal(python!, paths.Select(PyPath.Resolve).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static List<string>? PythonSays(string code, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.ArgumentList.Add("-I");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(code);
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
