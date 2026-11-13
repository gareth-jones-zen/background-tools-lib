using System.Text;
using System.Text.RegularExpressions;

namespace PyCompat.Tests;

/// <summary>v0.3's additions, against what Python 3.14 printed (Generated/PythonExpectedV03.g.cs, from
/// tools/gen/gen_expected.py).</summary>
public class V03OracleTests
{
    private static string Show(string text) => Py.ReprString(text);

    [Fact]
    public void The_tables_come_from_the_python_the_tools_run()
    {
        Assert.Equal("3.14.5", PythonExpectedV03.PythonVersion);
        Assert.Equal("3.14.5", PyUnicode.PythonVersion);
    }

    [Fact]
    public void Lower_and_casefold_match_python()
    {
        foreach (var (text, lower, fold, _) in PythonExpectedV03.Texts)
        {
            Assert.True(lower == PyStr.Lower(text), $"lower {Show(text)}: python {Show(lower)}, dotnet {Show(PyStr.Lower(text))}");
            Assert.True(fold == PyStr.CaseFold(text), $"casefold {Show(text)}: python {Show(fold)}, dotnet {Show(PyStr.CaseFold(text))}");
        }
    }

    [Fact]
    public void Lower_and_casefold_match_python_for_every_character()
    {
        // The tables are Python's, so each character maps as its table entry says; and characters with no entry,
        // ASCII included, are unchanged except A-Z.
        for (var cp = 0; cp <= CodePointSet.Max; cp++)
        {
            var text = PyUnicode.Text(cp);
            var lower = PyUnicode.Map(PyUnicode.LowerFrom, PyUnicode.LowerTo, cp) ?? text;
            if (cp == 0x03A3)
                lower = "σ";   // alone, a capital sigma isn't final
            Assert.True(lower == PyStr.Lower(text), $"U+{cp:X4}");
            Assert.Equal(PyUnicode.Map(PyUnicode.CaseFoldFrom, PyUnicode.CaseFoldTo, cp) ?? text, PyStr.CaseFold(text));
        }
    }

    [Fact]
    public void Repr_matches_python()
    {
        foreach (var (text, repr) in PythonExpectedV03.Reprs)
            Assert.True(repr == Py.ReprString(text), $"python {repr}, dotnet {Py.ReprString(text)}");
    }

    [Fact]
    public void Read_text_raises_pythons_message()
    {
        var folder = Directory.CreateTempSubdirectory("pycompat-");
        try
        {
            var path = Path.Combine(folder.FullName, "t.txt");
            foreach (var (bytes, expected) in PythonExpectedV03.ReadText)
            {
                File.WriteAllBytes(path, bytes);
                string got;
                try
                {
                    got = PyStr.ReadText(path);
                }
                catch (DecoderFallbackException error)
                {
                    got = "ERR:" + error.Message;
                }
                Assert.True(expected == got, $"{Convert.ToHexString(bytes)}: python {Show(expected)}, dotnet {Show(got)}");
            }
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Utf8_errors_carry_the_bytes_and_position()
    {
        var error = Assert.Throws<DecoderFallbackException>(() => PyUtf8.Decode([0x61, 0x62, 0xE2, 0x28, 0xA1]));
        Assert.Equal(2, error.Index);
        Assert.Equal([0xE2], error.BytesUnknown);
        Assert.Null(PyUtf8.FirstError("ok é"u8));
        Assert.Equal((0, 2, "unexpected end of data"), PyUtf8.FirstError([0xE2, 0x82]));
    }

    [Fact]
    public void Regex_search_findall_sub_and_split_match_python()
    {
        foreach (var (pattern, ignoreCase, text, matches, sub, split) in PythonExpectedV03.Regex)
        {
            var rx = PyRegex.Get(pattern, ignoreCase);
            var label = $"{Show(pattern)}{(ignoreCase ? " re.I" : "")} on {Show(text)}";
            var got = rx.FindIter(text).ToList();
            Assert.True(matches.Length == got.Count,
                $"{label}: python {matches.Length} matches, dotnet {got.Count} ({string.Join(", ", got.Select(m => $"{m.Index}:{m.End()}"))})");
            for (var k = 0; k < got.Count; k++)
            {
                Assert.True((matches[k].Start, matches[k].End) == (got[k].Start(), got[k].End()),
                    $"{label}: match {k} python {matches[k].Start}:{matches[k].End}, dotnet {got[k].Start()}:{got[k].End()}");
                Assert.Equal(matches[k].Groups, got[k].Groups());
            }
            Assert.True(sub == rx.Sub("-", text), $"{label}: sub python {Show(sub)}, dotnet {Show(rx.Sub("-", text))}");
            if (split is not null)
                Assert.Equal(split, rx.Split(text));
            Assert.Equal(matches.Length > 0, rx.IsMatch(text));
            Assert.Equal(matches.Length > 0 ? matches[0].Start : -1, rx.Search(text)?.Index ?? -1);
        }
    }

    [Fact]
    public void Ignorecase_matches_what_pythons_re_matches()
    {
        foreach (var (pattern, matched) in PythonExpectedV03.Fold)
        {
            var rx = PyRegex.Get(pattern, true);
            var expected = matched.ToHashSet();
            var wrong = PythonExpectedV03.FoldCandidates.Where(c => rx.FullMatch(PyUnicode.Text(c)) != expected.Contains(c)).ToList();
            Assert.True(wrong.Count == 0, $"{Show(pattern)}: differs for {string.Join(" ", wrong.Take(20).Select(c => $"U+{c:X4}"))}");
        }
    }

    [Fact]
    public void Format_matches_python()
    {
        var sample = new Dictionary<string, object?>
        {
            ["title"] = "Team Sync", ["who"] = "AM", ["empty"] = "", ["date"] = new DateOnly(2026, 11, 2),
            ["start"] = new DateTimeOffset(2026, 11, 12, 10, 5, 7, TimeSpan.Zero), ["naive"] = new DateTime(2026, 11, 12, 9, 0, 0),
            ["micro"] = new DateTime(2026, 11, 12, 9, 0, 0).AddTicks(15000),
            ["bst"] = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.FromHours(1)),
            ["n"] = 42, ["neg"] = -7L, ["big"] = 1234567, ["none"] = null, ["flag"] = true, ["uni"] = "café ☕ \U0001F600",
        };
        // Refused rather than guessed: attribute and index lookups, nested fields, !a, !r of a date, %j.
        string[] unsupported = ["{date.year}", "{title[0]}", "{title!a}", "{date!r}", "{date:%j}", "{title:{n}}"];
        foreach (var (template, expected) in PythonExpectedV03.Formats)
        {
            string got;
            try
            {
                got = "OK:" + PyFormat.Format(template, sample);
            }
            catch (PyFormatException error)
            {
                got = $"{error.Kind}:{error.Message}";
            }
            catch (NotSupportedException)
            {
                Assert.Contains(template, unsupported);
                Assert.NotNull(PyFormat.Problem(template, ["date", "start", "naive", "micro", "bst"]));
                continue;
            }
            Assert.DoesNotContain(template, unsupported);
            Assert.True(expected == got, $"{Show(template)}: python {Show(expected)}, dotnet {Show(got)}");
        }
    }

    [Fact]
    public void Lock_keys_are_win_backgrounds()
    {
        // WinBackground.Locks.Key hashes PyStr.Lower(text) as UTF-8: the same key as Python's
        // sha256(str(text).lower().encode()), so a .NET and a Python tool share the lock.
        foreach (var (text, _, _, key) in PythonExpectedV03.Texts)
        {
            if (key is null)
                continue;   // Python can't encode a lone surrogate; .NET writes U+FFFD and carries on
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(PyStr.Lower(text))))[..16];
            Assert.Equal(key, hash);
        }
    }
}

public class V03ApiTests
{
    [Fact]
    public void Named_groups_keep_pythons_numbering()
    {
        var rx = PyRegex.Get("(a)(?P<x>b)(c)");
        Assert.Equal(3, rx.GroupCount);
        Assert.Equal(2, rx.GroupIndex["x"]);
        var m = rx.Find("zabc")!;
        Assert.Equal("b", m.Group(2));
        Assert.Equal("b", m.Group("x"));
        Assert.Equal("c", m.Group(3));
        Assert.Equal(["a", "b", "c"], m.Groups());
        Assert.Equal("b", m.GroupDict()["x"]);
        Assert.Equal(1, m.Start());
        Assert.Equal(3, m.Start(3));
        Assert.Equal("c", rx.Wrap(rx.Search("zabc")!).Group(3));
    }

    [Fact]
    public void A_group_that_took_no_part_is_null()
    {
        var m = PyRegex.Get("(a)|(b)").Find("b")!;
        Assert.Null(m.Group(1));
        Assert.Equal(-1, m.Start(1));
        Assert.Equal(["x", "", "y"], PyRegex.Get("(x|y)|z").FindAll("xzy"));
    }

    [Fact]
    public void Findall_with_several_groups_and_split_with_groups_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => PyRegex.Get("(a)(b)").FindAll("ab"));
        Assert.Throws<InvalidOperationException>(() => PyRegex.Get("(,)").Split("a,b"));
        Assert.Throws<ArgumentException>(() => PyRegex.Get("a").Sub(@"\1", "a"));
    }

    [Fact]
    public void The_translation_never_uses_dotnets_ignore_case()
    {
        var (_, options) = PyRegex.Translate("(?im)abc", true);
        Assert.Equal(RegexOptions.Multiline, options);
    }

    [Fact]
    public void Space_patterns_are_pythons()
    {
        Assert.Matches(PyStr.Space, "\u001c");
        Assert.DoesNotMatch(PyStr.NotSpace, "\u001f");
        Assert.Matches("^" + PyStr.NotSpace + "+$", "a\U0001F600");
    }

    [Fact]
    public void Format_reports_unsupported_templates_when_they_load()
    {
        Assert.Null(PyFormat.Problem("{who} catch-up {date:%Y-%m}", ["date"]));
        Assert.NotNull(PyFormat.Problem("{lookup.who}"));
        Assert.NotNull(PyFormat.Problem("{date:%c}", ["date"]));
        Assert.Null(PyFormat.Problem("{title"));   // Python's own error, raised when it's used
        Assert.Equal(["who", "date"], PyFormat.FieldNames("{who} {date:%Y}"));
        Assert.Equal("2026-11-12 10:00:00+00:00", PyFormat.Str(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero)));
    }
}
