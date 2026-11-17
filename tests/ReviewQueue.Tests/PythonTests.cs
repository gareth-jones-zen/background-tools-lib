using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace ReviewQueue.Tests;

/// <summary>
/// Against Python itself: reference_queue.py (the Python reference's own read, write, add, replace and update) and
/// win-background's lock, imported from a checkout's src folder (read only). Each language reads what the other
/// wrote, rewrites it byte for byte unchanged, takes the other's lock, and writes alongside it without losing items.
/// Skipped where Python or the checkout is missing, as on CI; QueueFileTests holds Python's values for CI.
/// <para>Where: REVIEW_QUEUE_PYTHON (else C:\Python314\python.exe) and WIN_BACKGROUND_SRC (else
/// ~/source/repos/win-background/src).</para>
/// </summary>
public class PythonTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static (string Python, string Background, string Script) Require()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var python = Environment.GetEnvironmentVariable("REVIEW_QUEUE_PYTHON") ?? @"C:\Python314\python.exe";
        var background = Environment.GetEnvironmentVariable("WIN_BACKGROUND_SRC") ?? Path.Combine(home, @"source\repos\win-background\src");
        Assert.SkipUnless(File.Exists(python), "No Python here to compare with.");
        Assert.SkipUnless(File.Exists(Path.Combine(background, "win_background", "locks.py")), "No win-background checkout here.");
        var folder = Directory.CreateTempSubdirectory("review-queue-oracle-").FullName;   // the script's own folder: nothing else in it
        var script = Path.Combine(folder, "reference_queue.py");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "reference_queue.py"), script);
        return (python, background, script);
    }

    private static Process Start(string file, string lockName, params string[] args)
    {
        var (python, background, script) = Require();
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-I", script, background, file, lockName }.Concat(args))
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private static async Task<JsonNode?> Run(string file, string lockName, params string[] args)
    {
        using var python = Start(file, lockName, args);
        python.StandardInput.Close();
        var output = await python.StandardOutput.ReadToEndAsync(Cancel);
        var errors = await python.StandardError.ReadToEndAsync(Cancel);
        await python.WaitForExitAsync(Cancel);
        Assert.True(python.ExitCode == 0, errors);
        return PyJson.Parse(output);
    }

    private static string Json(JsonNode? node) => PyJson.Dump(node);

    private static JsonObject Awkward() => new()
    {
        ["title"] = "Zo\u00eb \u201cZ\u201d \U0001F600 back\\slash \"q\" tab\t \u0001 \u007f",
        ["n"] = 3,
        ["f"] = PyJson.Float(2.5),
        ["ok"] = false,
        ["none"] = null,
        ["list"] = new JsonArray("a", 1, new JsonObject { ["z"] = 1, ["a"] = 2 }),
    };

    [Fact]
    public async Task Python_reads_what_dotnet_wrote_and_writes_it_back_unchanged()
    {
        using var folder = new TempFolder();
        var path = folder.File("pending.json");
        var name = QueueFileTests.UniqueLock();
        var queue = new QueueFile(path, name);
        queue.Add("team-leave", "added", new JsonObject { ["title"] = "Ben annual leave 24/12 to 31/12 added", ["start"] = "2026-12-24", ["end"] = "2026-12-31" });
        queue.Add("s", "t", Awkward());
        queue.Replace("ev", [new ReviewItem("question", new JsonObject { ["title"] = "Thu 26 Nov: Book EV?" })]);
        queue.Update("s", new JsonObject { ["n"] = 3 }, new JsonObject { ["seen"] = "\u00e9" });

        Assert.Equal(Json(new JsonArray(queue.Items().Select(i => (JsonNode)i).ToArray())), Json(await Run(path, name, "items")));
        Assert.Equal(Json(new JsonArray(queue.Items("ev").Select(i => (JsonNode)i).ToArray())), Json(await Run(path, name, "items", "ev")));
        var before = File.ReadAllBytes(path);
        await Run(path, name, "rewrite");
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Dotnet_reads_what_python_wrote_with_the_same_ids()
    {
        using var folder = new TempFolder();
        var path = folder.File("pending.json");
        var name = QueueFileTests.UniqueLock();
        var queue = new QueueFile(path, name);
        var fields = Awkward();
        var pythonId = (string)(await Run(path, name, "add", "s", "t", Json(fields)))!;
        Assert.Equal(pythonId, queue.Add("s", "t", fields));   // the same id, so kept once
        Assert.Single(queue.Items());

        await Run(path, name, "replace", "cover-rota", Json(new JsonArray(new JsonObject { ["type"] = "check", ["title"] = "1 gap", ["gaps"] = new JsonArray("x") })));
        var replaced = queue.Items("cover-rota").Single();
        Assert.Equal(QueueFile.Id(replaced), (string)replaced["id"]!);
        Assert.Equal(["id", "added", "source", "type", "title", "gaps"], replaced.Select(p => p.Key));
        Assert.Equal(1, (long)(await Run(path, name, "update", "cover-rota", "{\"title\": \"1 gap\"}", "{\"seen\": true}"))!);
        Assert.True((bool)queue.Items("cover-rota").Single()["seen"]!);

        var before = File.ReadAllBytes(path);
        queue.Done([]);   // read and written back
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Dotnet_and_python_take_the_same_lock()
    {
        using var folder = new TempFolder();
        var path = folder.File();
        var name = QueueFileTests.UniqueLock();

        using (var python = Start(path, name, "hold"))
        {
            Assert.Equal("\"held\"", await python.StandardOutput.ReadLineAsync(Cancel));
            Assert.True(Locks.Held(name));
            Assert.Throws<TimeoutException>(() => new QueueFile(path, name, 0.5).Add("a", "t"));
            await python.StandardInput.WriteLineAsync("go");
            await python.StandardInput.FlushAsync(Cancel);
            Assert.Equal("\"released\"", await python.StandardOutput.ReadLineAsync(Cancel));
            await python.WaitForExitAsync(Cancel);
        }
        new QueueFile(path, name, 5).Add("a", "t");

        using (Locks.Exclusive(name, 5))
            Assert.Equal("busy", (string)(await Run(path, name, "try"))!);
        Assert.Equal("free", (string)(await Run(path, name, "try"))!);
    }

    [Fact]
    public async Task Python_and_dotnet_writing_at_once_lose_nothing()
    {
        using var folder = new TempFolder();
        var path = folder.File();
        var name = QueueFileTests.UniqueLock();
        var writers = new List<Task>
        {
            Run(path, name, "many", "py1", "15", "py1"),
            Run(path, name, "many", "py2", "15", "py2"),
        };
        writers.AddRange(Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            var queue = new QueueFile(path, name, 60);
            for (var n = 0; n < 15; n++)
                queue.Add($"net{w}", "t", new JsonObject { ["title"] = $"net{w} {n}" });
        }, Cancel)));
        await Task.WhenAll(writers);
        var items = new QueueFile(path, name).Items();
        Assert.Equal(60, items.Count);
        foreach (var source in new[] { "py1", "py2", "net0", "net1" })
            Assert.Equal(15, items.Count(i => (string)i["source"]! == source));
    }
}
