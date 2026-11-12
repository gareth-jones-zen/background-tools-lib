using System.Text.Json.Nodes;

namespace PyCompat.Tests;

/// <summary>Every expected value here was printed by Python 3.14.</summary>
public class PyStrTests
{
    [Fact]
    public void Strip_uses_pythons_whitespace()
    {
        Assert.Equal("a\u3000b", PyStr.Strip(" \x1c a\u3000b\x85 "));
        Assert.Equal("x", PyStr.Strip("\u2028x\u200a"));
        Assert.Equal("x \t", PyStr.LStrip("\u00a0x \t"));
        Assert.Equal("\tx", PyStr.RStrip("\tx\x1f"));
    }

    [Fact]
    public void Splitlines_knows_every_line_boundary()
    {
        Assert.Equal(["a", "b", "c", "d", "e"], PyStr.SplitLines("a\u001cb\u2028c\r\nd\re\n"));
        Assert.Equal(["", "x"], PyStr.SplitLines("\nx"));
        Assert.Empty(PyStr.SplitLines(""));
    }

    [Fact]
    public void Slices_count_code_points()
    {
        Assert.Equal("x\U0001F600", PyStr.Head("x\U0001F600y", 2));
        Assert.Equal("ab", PyStr.Tail("\U0001F600ab", 2));
        Assert.Equal("\U0001F600ab", PyStr.Tail("\U0001F600ab", 3));
        Assert.Equal("abc", PyStr.Head("abc", 200));
        Assert.Equal(3, PyStr.Length("\U0001F600ab"));
    }

    [Fact]
    public void Text_files_as_windows_text_mode()
    {
        var path = Path.GetTempFileName();
        try
        {
            PyStr.WriteText(path, "a\nb\r\n");
            Assert.Equal("a\r\nb\r\r\n"u8.ToArray(), File.ReadAllBytes(path));
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\r', (byte)'\n', (byte)'y', (byte)'\r']);
            Assert.Equal("\ufeffx\ny\n", PyStr.ReadText(path));
            File.WriteAllBytes(path, [0xff]);
            Assert.Throws<System.Text.DecoderFallbackException>(() => PyStr.ReadText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class PyTimeTests
{
    private static readonly TimeZoneInfo London = PyTime.Zone("Europe/London");

    [Fact]
    public void Wall_times_take_pythons_fold_zero_offset()
    {
        Assert.Equal("2026-10-25T01:30:00+01:00", PyTime.IsoFormat(PyTime.At(London, new DateTime(2026, 10, 25, 1, 30, 0))));
        Assert.Equal("2026-03-29T01:30:00+00:00", PyTime.IsoFormat(PyTime.At(London, new DateTime(2026, 3, 29, 1, 30, 0))));
        var before = PyTime.At(London, new DateTime(2026, 3, 29, 0, 30, 0));
        Assert.Equal("2026-03-29T01:30:00+00:00", PyTime.IsoFormat(PyTime.AddWall(London, before, TimeSpan.FromHours(1))));
    }

    [Fact]
    public void Isoformat_and_back()
    {
        var when = new DateTimeOffset(2026, 11, 12, 23, 5, 7, TimeSpan.Zero).AddTicks(1200);
        Assert.Equal("2026-11-12T23:05:07.000120+00:00", PyTime.IsoFormat(when));
        Assert.Equal("2026-11-12T23:05:07+00:00", PyTime.IsoFormat(when, secondsOnly: true));
        Assert.Equal(when, PyTime.FromIsoFormat("2026-11-12T23:05:07.000120+00:00", London));
        Assert.Equal(TimeSpan.FromHours(1), PyTime.FromIsoFormat("2026-07-01T09:00:00", London).Offset);
        Assert.Equal(TimeSpan.Zero, PyTime.FromIsoFormat("2026-07-01T09:00:00Z", London).Offset);
        Assert.Throws<FormatException>(() => PyTime.FromIsoFormat("soon", London));
        Assert.Equal("Thu", PyTime.Day(when));
    }
}

public class PyJsonTests
{
    [Fact]
    public void Dumps_as_python_with_and_without_indent()
    {
        var data = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["argv"] = new JsonArray("C:\\Py\\python.exe", "-m", "caf\u00e9 \"q\"\t\U0001F600"),
                ["cwd"] = "C:\u0007 b",
            }),
            ["empty"] = new JsonArray(),
            ["o"] = new JsonObject(),
            ["n"] = PyJson.Float(1),
            ["i"] = 3,
            ["b"] = true,
            ["z"] = null,
        };
        Assert.Equal(
            "{\n  \"items\": [\n    {\n      \"argv\": [\n        \"C:\\\\Py\\\\python.exe\",\n        \"-m\",\n" +
            "        \"caf\\u00e9 \\\"q\\\"\\t\\ud83d\\ude00\"\n      ],\n      \"cwd\": \"C:\\u0007 b\"\n    }\n  ],\n" +
            "  \"empty\": [],\n  \"o\": {},\n  \"n\": 1.0,\n  \"i\": 3,\n  \"b\": true,\n  \"z\": null\n}",
            PyJson.Dump(data, 2));
        Assert.Equal("{\"empty\": [], \"n\": 1.0}", PyJson.Dump(new JsonObject { ["empty"] = new JsonArray(), ["n"] = PyJson.Float(1) }));
    }
}
