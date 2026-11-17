using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;
using RepoCommit;
using ReviewQueue;
using WinBackground;

/// <summary>LockHelper aot-smoke: the 13 Nov 2026 additions run once each under Native AOT (the regex interpreter,
/// the kernel32 imports behind PyPath.Resolve, the generated entity table, a raw Graph call over a fake handler,
/// Shell.Run with UTF-8 stdin), and ReviewQueue and RepoCommit (git on the PATH). Prints "ok", or what went wrong and exits 1. No network: the handler is fake.</summary>
internal static class AotSmoke
{
    public static async Task<int> Run()
    {
        var problems = new List<string>();
        void Check(bool good, string what)
        {
            if (!good)
                problems.Add(what);
        }

        Check(PyRegex.IsMatch(@"\bsprint (\d+)\b", "Sprint 4 plan", ignoreCase: true), "PyRegex");
        Check(PyHtml.Unescape("&pound;5 &notit;") == (char)0xA3 + "5 " + (char)0xAC + "it;", "PyHtml");
        Check(PyTime.IsoFormat(PyTime.FromIsoFormat("2026-11-12T24:00", PyTime.Zone("Europe/London"))) == "2026-11-13T00:00:00+00:00", "PyTime");
        Check(PyJson.Dump(new JsonObject { ["b"] = 1, ["a"] = true }, sortKeys: true) == "{\"a\": true, \"b\": 1}", "PyJson");
        Check(string.Join("|", UnifiedDiff.Lines(["a"], ["b"], "x", "y")) == "--- x|+++ y|@@ -1 +1 @@|-a|+b", "UnifiedDiff");
        Check(PyStr.Lower("ABC") == "abc" && PyStr.Int(" 4_2 ") == 42, "PyStr");
        Check(PyPath.Resolve(Path.GetTempPath()).Length > 3, "PyPath.Resolve");
        // v0.3: the generated Unicode tables, named groups, emoji as one character, Python's IGNORECASE, str.format.
        var emoji = char.ConvertFromUtf32(0x1F600);
        Check(PyStr.Lower("İΣAΣ") == "i̇σaς" && PyStr.CaseFold("Straße") == "strasse", "PyStr v0.3");
        Check(PyRegex.Get("(?P<who>[A-Z]{2}) catch-?up").Find("AM catch-up")?.Group("who") == "AM", "PyRegex groups");
        Check(PyRegex.Get(".").FindAll("a" + emoji).Count == 2 && PyRegex.IsMatch("lis", "LİS", true), "PyRegex v0.3");
        Check(PyFormat.Format("{who} {date:%Y-%m}", new Dictionary<string, object?> { ["who"] = "AM", ["date"] = new DateOnly(2026, 11, 2) }) == "AM 2026-11", "PyFormat");
        Check(Py.ReprString("a b") == "'a\\xa0b'" && Locks.Key("a.md") == "fecccc97532467ad", "ReprString and Locks.Key");

        var echo = Shell.Run(Environment.ProcessPath!, ["echo"], TimeSpan.FromSeconds(30), stdin: "x\n", utf8: true);
        Check(echo.ExitCode == 7 && echo.StandardOutput.Contains("x\n", StringComparison.Ordinal), "Shell.Run");

        var graph = new GraphClient(new GraphClientOptions { Log = _ => { } }, new Fake(),
            token: (_, _) => Task.FromResult("t"), delay: (_, _) => Task.CompletedTask);
        var reply = await graph.SendRawAsync(HttpMethod.Post, "/x", Encoding.UTF8.GetBytes("<p/>"), "text/html", GraphRetryPolicy.Write);
        Check(reply.Status == HttpStatusCode.Created && reply.Body.Length == 3, "SendRawAsync");
        Check((string?)(await graph.GetAsync("/me"))["id"] == "1", "GetAsync");

        // ReviewQueue: an id as Python's, a write through the lock, and the file read back.
        var folder = Directory.CreateTempSubdirectory("aot-review-").FullName;
        try
        {
            var queue = new QueueFile(Path.Combine(folder, "queue.json"), "aot-review-" + Environment.ProcessId);
            queue.Replace("ev", [new ReviewItem("question", new JsonObject { ["title"] = "Thu 26 Nov: Travel block says EV but work plan says remote. Book EV?" })]);
            var added = queue.Add("s", "t", new JsonObject { ["title"] = "é" });
            Check(queue.Items("ev").Single()["id"]!.GetValue<string>() == "a0d27046" && queue.Items().Count == 2
                  && queue.Update("s", new JsonObject { ["title"] = "é" }, new JsonObject { ["seen"] = true }) == 1
                  && queue.Done([added]) == 1, "ReviewQueue");
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        // RepoCommit: [git] read from JSON, and one file committed in a new repository (git on the PATH, as on CI).
        var repo = Directory.CreateTempSubdirectory("aot-git-").FullName;
        try
        {
            var settings = GitSettings.Read(JsonNode.Parse("{\"repo\": \".\", \"trailers\": [\"Co-Authored-By: X <x@example.com>\"]}"),
                "x.json", p => Path.Combine(repo, p), "\"git\"");
            foreach (var args in new[] { "init -q -b main", "config user.name T", "config user.email t@example.com", "config commit.gpgsign false" })
                Shell.Run("git", args.Split(' '), TimeSpan.FromSeconds(60), workingDirectory: repo);
            File.WriteAllText(Path.Combine(repo, "a.md"), "a");
            File.WriteAllText(Path.Combine(repo, "b.md"), "b");
            var committed = settings.Open().Commit("A", ["a.md"], trailers: settings.Trailers);
            Check(committed.Committed && committed.Paths.SequenceEqual(["a.md"])
                  && settings.Open().Changed(["a.md", "b.md"]).SequenceEqual(["b.md"]), "RepoCommit");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(repo, true);
        }

        if (problems.Count == 0)
        {
            Console.WriteLine("ok");
            return 0;
        }
        Console.WriteLine("failed: " + string.Join(", ", problems));
        return 1;
    }

    private sealed class Fake : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([1, 2, 3]) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\": \"1\"}") });
    }
}
