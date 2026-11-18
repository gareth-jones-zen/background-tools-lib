using System.Text;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace ReviewQueue.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("review-queue-").FullName;

    public string File(string name = "queue.json") => System.IO.Path.Combine(Path, name);

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

public class QueueFileTests
{
    /// <summary>A lock name of the test's own, so nothing else on the machine (a real queue's writers) is involved.</summary>
    internal static string UniqueLock() => "review-queue-test-" + Guid.NewGuid().ToString("N");

    private static QueueFile Queue(TempFolder folder, DateTime? now = null, string name = "queue.json") =>
        new(folder.File(name), UniqueLock(), 30, () => now ?? new DateTime(2026, 11, 17, 9, 30, 5, 123));

    // Ids from Python 3.14.5: hashlib.sha256(json.dumps(fields, sort_keys=True, default=str).encode()).hexdigest()[:8],
    // with the basis text Python wrote, so a mismatch shows which half differs.
    public static TheoryData<string, string, string> IdVectors() => new()
    {
        {
            """{"source": "team-leave", "type": "added", "title": "Ben Wolstencroft annual leave 24/12 to 31/12 added", "start": "2026-12-24", "end": "2026-12-31"}""",
            "{\"end\": \"2026-12-31\", \"source\": \"team-leave\", \"start\": \"2026-12-24\", \"title\": \"Ben Wolstencroft annual leave 24/12 to 31/12 added\", \"type\": \"added\"}",
            "a7910e09"
        },
        {
            """{"source": "ev", "type": "question", "title": "Thu 26 Nov: Travel block says EV but work plan says remote. Book EV?"}""",
            "{\"source\": \"ev\", \"title\": \"Thu 26 Nov: Travel block says EV but work plan says remote. Book EV?\", \"type\": \"question\"}",
            "a0d27046"
        },
        {
            "{\"source\": \"s\", \"type\": \"t\", \"title\": \"Caf\u00e9 \u2013 na\u00efve \u201cquotes\u201d \U0001F600 back\\\\slash \\\"q\\\" new\\nline\\ttab \\u0001 \u007f end\"}",
            "{\"source\": \"s\", \"title\": \"Caf\\u00e9 \\u2013 na\\u00efve \\u201cquotes\\u201d \\ud83d\\ude00 back\\\\slash \\\"q\\\" new\\nline\\ttab \\u0001 \\u007f end\", \"type\": \"t\"}",
            "2c6b1d23"
        },
        {
            """{"source": "cover-rota", "type": "check", "title": "Xmas cover rota: 2 possible gap(s)", "report": "C:\\x\\check.md", "gaps": ["Asimov, Tue 15 Dec: A listed as working, likely on leave", "b"], "written": [], "errors": []}""",
            "{\"errors\": [], \"gaps\": [\"Asimov, Tue 15 Dec: A listed as working, likely on leave\", \"b\"], \"report\": \"C:\\\\x\\\\check.md\", \"source\": \"cover-rota\", \"title\": \"Xmas cover rota: 2 possible gap(s)\", \"type\": \"check\", \"written\": []}",
            "8001e0ab"
        },
        {
            """{"source": "s", "type": "t", "n": 3, "f": 1.5, "g": 1.0, "b": true, "c": false, "z": null, "big": 12345678901234567890, "neg": -7}""",
            "{\"b\": true, \"big\": 12345678901234567890, \"c\": false, \"f\": 1.5, \"g\": 1.0, \"n\": 3, \"neg\": -7, \"source\": \"s\", \"type\": \"t\", \"z\": null}",
            "7c96301c"
        },
        {
            "{\"source\": \"s\", \"type\": \"t\", \"Zeta\": 1, \"alpha\": 2, \"_x\": 3, \"\u00e9\": 4, \"B\": 5, \"a b\": 6, \"\U0001F600\": 7, \"\uff21\": 8}",
            "{\"B\": 5, \"Zeta\": 1, \"_x\": 3, \"a b\": 6, \"alpha\": 2, \"source\": \"s\", \"type\": \"t\", \"\\u00e9\": 4, \"\\uff21\": 8, \"\\ud83d\\ude00\": 7}",
            "509370fb"
        },
        {
            """{"source": "s", "type": "t", "cover": {"b": 1, "a": [{"z": 1, "y": 2}, [], {}]}, "empty": ""}""",
            "{\"cover\": {\"a\": [{\"y\": 2, \"z\": 1}, [], {}], \"b\": 1}, \"empty\": \"\", \"source\": \"s\", \"type\": \"t\"}",
            "fe7f7d72"
        },
    };

    [Theory]
    [MemberData(nameof(IdVectors))]
    public void Ids_are_pythons(string item, string basis, string id)
    {
        var parsed = PyJson.Parse(item)!.AsObject();
        Assert.Equal(basis, PyJson.Dump(parsed, sortKeys: true));
        Assert.Equal(id, QueueFile.Id(parsed));
        parsed["id"] = "ignored";
        parsed["added"] = "2026-01-01T00:00:00";
        Assert.Equal(id, QueueFile.Id(parsed));
    }

    [Fact]
    public void Ids_from_built_values_are_pythons_too()
    {
        // The same values made in code rather than parsed: ints, a float, true, null.
        var item = new JsonObject
        {
            ["source"] = "s", ["type"] = "t", ["n"] = 3, ["f"] = PyJson.Float(1.5), ["g"] = PyJson.Float(1.0), ["b"] = true,
            ["c"] = false, ["z"] = null, ["big"] = PyJson.Parse("12345678901234567890"), ["neg"] = -7,
        };
        Assert.Equal("7c96301c", QueueFile.Id(item));
    }

    /// <summary>Written by Python 3.14.5 with the reference's _write (json.dumps(indent=2, ensure_ascii=False), text
    /// mode on Windows): one add, one replace, and an add with awkward values.</summary>
    internal const string PythonFile =
        "{\r\n  \"items\": [\r\n    {\r\n      \"id\": \"a7910e09\",\r\n      \"source\": \"team-leave\",\r\n      \"type\": \"added\",\r\n      \"added\": \"2026-11-17T09:30:05\",\r\n      \"title\": \"Ben Wolstencroft annual leave 24/12 to 31/12 added\",\r\n      \"start\": \"2026-12-24\",\r\n      \"end\": \"2026-12-31\"\r\n    },\r\n    {\r\n      \"id\": \"75530838\",\r\n      \"added\": \"2026-11-17T09:31:00\",\r\n      \"source\": \"cover-rota\",\r\n      \"type\": \"check\",\r\n      \"title\": \"Xmas cover rota: 1 possible gap(s)\",\r\n      \"report\": \"C:\\\\Users\\\\x\\\\Xmas cover\\\\check.md\",\r\n      \"gaps\": [\r\n        \"Asimov, Tue 15 Dec: Zo\u00eb \u201cZ\u201d listed as working, likely on leave\"\r\n      ],\r\n      \"written\": [],\r\n      \"errors\": []\r\n    },\r\n    {\r\n      \"id\": \"a41dca56\",\r\n      \"source\": \"s\",\r\n      \"type\": \"t\",\r\n      \"added\": \"2026-11-17T09:32:00\",\r\n      \"title\": \"Tab\\there \ud83d\ude00 \\\\ \\\"q\\\" \\u0001\",\r\n      \"n\": 3,\r\n      \"ok\": true,\r\n      \"none\": null,\r\n      \"nested\": {\r\n        \"b\": [\r\n          1,\r\n          {}\r\n        ],\r\n        \"a\": []\r\n      }\r\n    }\r\n  ]\r\n}";

    [Fact]
    public void The_file_is_byte_for_byte_pythons()
    {
        using var folder = new TempFolder();
        var path = folder.File();
        var now = new DateTime(2026, 11, 17, 9, 30, 5);
        var queue = new QueueFile(path, UniqueLock(), 30, () => now);
        Assert.Equal("a7910e09", queue.Add("team-leave", "added", new JsonObject
        {
            ["title"] = "Ben Wolstencroft annual leave 24/12 to 31/12 added", ["start"] = "2026-12-24", ["end"] = "2026-12-31",
        }));
        now = new DateTime(2026, 11, 17, 9, 31, 0);
        Assert.Equal(["75530838"], queue.Replace("cover-rota", [new ReviewItem("check", new JsonObject
        {
            ["title"] = "Xmas cover rota: 1 possible gap(s)",
            ["report"] = @"C:\Users\x\Xmas cover\check.md",
            ["gaps"] = new JsonArray("Asimov, Tue 15 Dec: Zo\u00eb \u201cZ\u201d listed as working, likely on leave"),
            ["written"] = new JsonArray(),
            ["errors"] = new JsonArray(),
        })]));
        now = new DateTime(2026, 11, 17, 9, 32, 0);
        Assert.Equal("a41dca56", queue.Add("s", "t", new JsonObject
        {
            ["title"] = "Tab\there \U0001F600 \\ \"q\" \u0001", ["n"] = 3, ["ok"] = true, ["none"] = null,
            ["nested"] = new JsonObject { ["b"] = new JsonArray(1, new JsonObject()), ["a"] = new JsonArray() },
        }));
        Assert.Equal(Encoding.UTF8.GetBytes(PythonFile), File.ReadAllBytes(path));
    }

    [Fact]
    public void A_python_file_is_read_and_written_back_unchanged()
    {
        using var folder = new TempFolder();
        var path = folder.File();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(PythonFile));
        var queue = Queue(folder);
        Assert.Equal(["a7910e09", "75530838", "a41dca56"], queue.Items().Select(i => (string)i["id"]!));
        var written = File.GetLastWriteTimeUtc(path);
        Assert.Equal(0, queue.Done(["nothing"]));   // nothing matched: not written at all (v0.6)
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        queue.Replace("no-such-source", []);       // a write, changing nothing
        Assert.Equal(Encoding.UTF8.GetBytes(PythonFile), File.ReadAllBytes(path));
    }

    [Fact]
    public void Adding_the_same_item_twice_keeps_one()
    {
        using var folder = new TempFolder();
        var queue = Queue(folder);
        var first = queue.Add("a", "t", new JsonObject { ["title"] = "x" });
        var second = queue.Add("a", "t", new JsonObject { ["title"] = "x" });
        var other = queue.Add("a", "t", new JsonObject { ["title"] = "y" });
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(2, queue.Items().Count);
        Assert.Equal("2026-11-17T09:30:05", (string)queue.Items()[0]["added"]!);   // seconds, as isoformat(timespec="seconds")
    }

    [Fact]
    public void Replace_drops_only_that_sources_items_and_an_empty_list_clears_it()
    {
        using var folder = new TempFolder();
        var queue = Queue(folder);
        queue.Add("keep", "t", new JsonObject { ["title"] = "kept" });
        queue.Replace("ev", [new ReviewItem("question", new JsonObject { ["title"] = "one" }), new ReviewItem("question", new JsonObject { ["title"] = "two" })]);
        Assert.Equal(["one", "two"], queue.Items("ev").Select(i => (string)i["title"]!));
        queue.Replace("ev", [new ReviewItem("question", new JsonObject { ["title"] = "three" })]);
        Assert.Equal(["three"], queue.Items("ev").Select(i => (string)i["title"]!));
        Assert.Equal(["id", "added", "source", "type", "title"], queue.Items("ev")[0].Select(p => p.Key));
        queue.Replace("ev", []);
        Assert.Empty(queue.Items("ev"));
        Assert.Single(queue.Items());
    }

    [Fact]
    public void Update_sets_fields_on_matching_items_and_keeps_their_place_and_id()
    {
        using var folder = new TempFolder();
        var queue = Queue(folder);
        var id = queue.Add("t", "transcript", new JsonObject { ["path"] = "a.md", ["summary_needed"] = true });
        queue.Add("t", "transcript", new JsonObject { ["path"] = "b.md" });
        queue.Add("other", "transcript", new JsonObject { ["path"] = "a.md" });
        Assert.Equal(1, queue.Update("t", new JsonObject { ["path"] = "a.md" }, new JsonObject { ["summary_draft"] = "s.md", ["summary_needed"] = false }));
        var item = queue.Items("t")[0];
        Assert.Equal(id, (string)item["id"]!);
        Assert.Equal(["id", "source", "type", "added", "path", "summary_needed", "summary_draft"], item.Select(p => p.Key));
        Assert.False((bool)item["summary_needed"]!);
        Assert.Equal(2, queue.Update("t", new JsonObject { ["missing"] = null }, new JsonObject { ["seen"] = 1 }));   // missing matches null
        Assert.Equal(0, queue.Update("t", new JsonObject { ["path"] = "c.md" }, new JsonObject { ["x"] = 1 }));
        Assert.Equal(2, queue.Update("t", new JsonObject { ["seen"] = PyJson.Float(1.0) }, new JsonObject { ["y"] = 1 }));   // 1 == 1.0
    }

    [Fact]
    public void Done_removes_by_id_or_by_source()
    {
        using var folder = new TempFolder();
        var queue = Queue(folder);
        var a = queue.Add("one", "t", new JsonObject { ["title"] = "a" });
        queue.Add("one", "t", new JsonObject { ["title"] = "b" });
        queue.Add("two", "t", new JsonObject { ["title"] = "c" });
        Assert.Equal(1, queue.Done([a]));
        Assert.Equal(1, queue.Done(["two"]));
        Assert.Equal(["b"], queue.Items().Select(i => (string)i["title"]!));
    }

    [Fact]
    public void A_file_that_isnt_json_is_kept_aside_and_the_queue_starts_empty()
    {
        using var folder = new TempFolder();
        var path = folder.File("pending.json");
        File.WriteAllText(path, "{\"items\": [ half");
        var queue = new QueueFile(path, UniqueLock(), 30, () => new DateTime(2026, 11, 17, 9, 30, 5));
        queue.Add("a", "t", new JsonObject { ["title"] = "x" });
        var broken = folder.File("pending.broken-20261117-093005.json");
        Assert.Equal("{\"items\": [ half", File.ReadAllText(broken));
        Assert.Single(queue.Items());
    }

    [Fact]
    public void Json_that_isnt_a_queue_is_refused_and_left_alone()
    {
        using var folder = new TempFolder();
        var path = folder.File();
        foreach (var text in new[] { "[]", "{\"items\": {}}", "{\"other\": []}", "{\"items\": [1]}" })
        {
            File.WriteAllText(path, text);
            Assert.Throws<InvalidDataException>(() => Queue(folder).Add("a", "t"));
            Assert.Equal(text, File.ReadAllText(path));
        }
        Assert.Single(Directory.GetFiles(folder.Path));   // no temporary or broken files left
    }

    [Fact]
    public void A_missing_file_and_folder_are_an_empty_queue_and_made_on_the_first_write()
    {
        using var folder = new TempFolder();
        var queue = new QueueFile(Path.Combine(folder.Path, "deeper", "q.json"), UniqueLock());
        Assert.Empty(queue.Items());
        Assert.False(File.Exists(queue.Path));
        queue.Add("a", "t");
        Assert.Single(queue.Items());
        Assert.Equal(["q.json"], Directory.GetFiles(Path.GetDirectoryName(queue.Path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Other_top_level_fields_are_kept()
    {
        using var folder = new TempFolder();
        var path = folder.File();
        File.WriteAllText(path, "{\"version\": 2, \"items\": [], \"note\": \"\u00e9\"}");
        Queue(folder).Add("a", "t");
        Assert.StartsWith("{\r\n  \"version\": 2,\r\n  \"items\": [", File.ReadAllText(path));
        Assert.EndsWith("  ],\r\n  \"note\": \"\u00e9\"\r\n}", File.ReadAllText(path));
    }

    [Fact]
    public void Reserved_fields_are_refused()
    {
        using var folder = new TempFolder();
        var queue = Queue(folder);
        foreach (var key in QueueFile.Reserved)
        {
            Assert.Throws<ArgumentException>(() => queue.Add("a", "t", new JsonObject { [key] = "x" }));
            Assert.Throws<ArgumentException>(() => queue.Replace("a", [new ReviewItem("t", new JsonObject { [key] = "x" })]));
        }
        Assert.Throws<ArgumentException>(() => queue.Update("a", [], new JsonObject { ["id"] = "x" }));
        Assert.Throws<ArgumentException>(() => queue.Update("a", [], new JsonObject { ["source"] = "x" }));
        Assert.False(File.Exists(queue.Path));
    }

    [Fact]
    public void Given_fields_are_copied_not_taken()
    {
        using var folder = new TempFolder();
        var queue = Queue(folder);
        var fields = new JsonObject { ["list"] = new JsonArray("a") };
        queue.Add("a", "t", fields);
        queue.Add("a", "u", fields);   // the same object again: still usable
        fields["list"]!.AsArray().Add((JsonNode?)JsonValue.Create("b"));
        Assert.All(queue.Items(), i => Assert.Single(i["list"]!.AsArray()));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\claude-ea\pending.json", "claude-ea-pending")]
    [InlineData(@"C:\Users\x\AppData\Local\My-Tools\Review.JSON", "my-tools-review")]
    [InlineData(@"C:\q.json", "q")]
    public void The_default_lock_is_the_folder_and_name(string path, string expected) =>
        Assert.Equal(expected, QueueFile.DefaultLockName(path));

    [Fact]
    public void A_lock_name_with_a_backslash_is_refused() =>
        Assert.Throws<ArgumentException>(() => new QueueFile(@"C:\q.json", @"Global\x"));

    [Fact]
    public void A_write_waits_for_the_lock_and_times_out_with_pythons_words()
    {
        using var folder = new TempFolder();
        var name = UniqueLock();
        var queue = new QueueFile(folder.File(), name, lockTimeoutSeconds: 0.3);
        using (Locks.Exclusive(name, 5))
        {
            var error = Assert.Throws<TimeoutException>(() => queue.Add("a", "t"));
            Assert.Equal("Another queue.json write didn't finish within 0 minutes.", error.Message);
        }
        queue.Add("a", "t");
        Assert.Single(queue.Items());
    }

    [Fact]
    public async Task Writers_at_the_same_moment_lose_nothing()
    {
        using var folder = new TempFolder();
        var path = folder.File();
        var name = UniqueLock();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            var queue = new QueueFile(path, name, 60);   // each its own object, as separate tools would be
            for (var n = 0; n < 25; n++)
                queue.Add($"w{writer}", "t", new JsonObject { ["title"] = $"{writer}-{n}" });
            queue.Update($"w{writer}", new JsonObject { ["title"] = $"{writer}-0" }, new JsonObject { ["seen"] = true });
        }, TestContext.Current.CancellationToken)));
        var items = new QueueFile(path, name).Items();
        Assert.Equal(200, items.Count);
        Assert.Equal(200, items.Select(i => (string)i["id"]!).Distinct().Count());
        Assert.Equal(8, items.Count(i => i["seen"] is not null));
        Assert.Single(Directory.GetFiles(folder.Path));
    }
}
