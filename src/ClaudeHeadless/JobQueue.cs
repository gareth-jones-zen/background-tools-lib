using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace ClaudeHeadless;

/// <summary>A queued job: a command line and the folder to run it in.</summary>
public sealed record QueuedJob(IReadOnlyList<string> Argv, string Cwd, string Added);

/// <summary>The seat's job queue, and the usage limit it waits on.
///
///     %LOCALAPPDATA%\claude-headless\     (or CLAUDE_HEADLESS_HOME)
///         limit.json    {"reset": iso, "notified": bool}   while a limit is in force
///         queue.json    {"items": [{"argv": [...], "cwd": "...", "added": iso}]}
///         runner.log
///
/// **One queue per seat, whichever tool queued the job, and whichever language it's written in.** Everything on a
/// subscription seat shares one limit, so jobs from different tools have to wait on the same reset and run one at a
/// time after it. The files are written byte for byte as the Python claude-headless writes them, and the runner
/// takes the same named lock, so the Python and .NET versions can share a queue while tools move across.
///
/// **One notification per limit**, not one per job: Retry() says true only the first time a limit is seen, so the
/// caller notifies then and stays quiet for the rest of the batch.</summary>
public sealed class JobQueue
{
    /// <summary>Start a little after the stated reset.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> clock;

    public JobQueue(string? home = null, Func<DateTimeOffset>? clock = null)
    {
        Home = PyPath.Pure(home ?? DefaultHome(Environment.GetEnvironmentVariable));
        this.clock = clock ?? Limits.Now;
    }

    /// <summary>The seat's queue (Python's module-level `queue`).</summary>
    public static JobQueue Default { get; } = new();

    public string Home { get; }
    public string LimitFile => Path.Combine(Home, "limit.json");
    public string QueueFile => Path.Combine(Home, "queue.json");
    public string LogFile => Path.Combine(Home, "runner.log");

    /// <summary>CLAUDE_HEADLESS_HOME if set, otherwise %LOCALAPPDATA%\claude-headless.</summary>
    public static string DefaultHome(Func<string, string?> env)
    {
        if (env("CLAUDE_HEADLESS_HOME") is { Length: > 0 } home)
            return PyPath.Pure(home);
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(Path.Combine(local ?? PyPath.Home(env), "claude-headless"));
    }

    // ---- the files --------------------------------------------------------------------------------

    private static JsonObject? Read(string path)
    {
        try
        {
            return PyJson.Parse(PyJson.ReadStrict(path)) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>json.dumps(data, indent=2) through a temporary file and one atomic replace, written in text mode
    /// (so "\r\n" between lines, and no newline at the end), as the Python version writes it.</summary>
    private void Write(string path, JsonObject data)
    {
        Directory.CreateDirectory(Home);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, PyStr.WindowsNewlines(PyJson.Dump(data, 2)), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private JsonObject ReadQueue()
    {
        var data = Read(QueueFile);
        if (data is null || data["items"] is not JsonArray)
            return new JsonObject { ["items"] = new JsonArray() };
        return data;
    }

    // ---- the limit --------------------------------------------------------------------------------

    /// <summary>The reset time while a limit is in force (plus a few minutes' grace); null once it's passed.</summary>
    public DateTimeOffset? LimitUntil(DateTimeOffset? now = null)
    {
        var data = Read(LimitFile);
        if (data?["reset"] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return null;
        DateTimeOffset reset;
        try
        {
            reset = PyTime.FromIsoFormat(value.GetValue<string>(), Limits.Zone);
        }
        catch (FormatException)
        {
            return null;
        }
        return reset + Grace > (now ?? clock()) ? reset : null;
    }

    private static DateTimeOffset Later(DateTimeOffset reset, DateTimeOffset? known) =>
        known is { } k && k > reset ? k : reset;

    private bool Notified() => Py.Truthy(Read(LimitFile)?["notified"]);

    /// <summary>Record a limit. A limit already known keeps its notified flag, and is never shortened.</summary>
    public DateTimeOffset Remember(DateTimeOffset reset)
    {
        var known = LimitUntil();
        var notified = known is not null && Notified();
        Write(LimitFile, new JsonObject { ["reset"] = PyTime.IsoFormat(Later(reset, known)), ["notified"] = notified });
        return reset;
    }

    // ---- jobs -------------------------------------------------------------------------------------

    /// <summary>Queue a job. The same job twice is queued once. front: true for one with a deadline. True if added.</summary>
    public bool Add(IEnumerable<string> argv, string cwd, bool front = false)
    {
        var data = ReadQueue();
        var items = (JsonArray)data["items"]!;
        var wanted = new JsonArray(argv.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        if (items.Any(item => item is JsonObject o && PyJson.Equal(o["argv"], wanted)))
            return false;
        var item = new JsonObject
        {
            ["argv"] = wanted,
            ["cwd"] = cwd,
            ["added"] = PyTime.IsoFormat(clock(), secondsOnly: true),
        };
        if (front)
            items.Insert(0, item);
        else
            items.Add((JsonNode)item);
        Write(QueueFile, data);
        return true;
    }

    /// <summary>A job that met the limit: queue it for after the reset. True if this limit is news, so the caller
    /// notifies once.</summary>
    public bool Retry(IEnumerable<string> argv, string cwd, DateTimeOffset reset)
    {
        Add(argv, cwd);
        var known = LimitUntil();
        var news = !(known is not null && Notified());
        Write(LimitFile, new JsonObject { ["reset"] = PyTime.IsoFormat(Later(reset, known)), ["notified"] = true });
        return news;
    }

    /// <summary>What's queued, in order. An item the Python version could read but this can't (no argv list) is
    /// shown with an empty command line.</summary>
    public IReadOnlyList<QueuedJob> Pending() =>
        ((JsonArray)ReadQueue()["items"]!).Select(ToJob).ToList();

    private static QueuedJob ToJob(JsonNode? node)
    {
        var item = node as JsonObject;
        var argv = item?["argv"] is JsonArray a ? a.Select(Py.Str).ToList() : [];
        return new QueuedJob(argv, Py.Str(item?["cwd"]), item?["added"] is { } added ? Py.Str(added) : "");
    }

    /// <summary>The next job, removed from the queue, if the limit has passed. Null otherwise.</summary>
    public QueuedJob? Take()
    {
        if (LimitUntil() is not null)
            return null;
        var data = ReadQueue();
        var items = (JsonArray)data["items"]!;
        if (items.Count == 0)
            return null;
        var item = items[0];
        items.RemoveAt(0);
        Write(QueueFile, data);
        return ToJob(item);
    }

    /// <summary>The runner's named lock, shared with the Python runner (the same name for the same home).</summary>
    public string LockName => $"claude-headless-runner-{Locks.Key(Home)}";

    /// <summary>Run queued jobs one at a time until the queue is empty or a limit is back. Only one runner works
    /// the queue at once, Python's or this; a second returns straight away. Returns how many jobs ran.</summary>
    public int Run(Action<string>? log = null, int maxJobs = 50, TimeSpan? timeout = null, IJobRunner? runner = null)
    {
        log ??= Logger.Create(LogFile);
        runner ??= new JobRunner();
        var limit = timeout ?? TimeSpan.FromHours(1);
        using var held = Locks.SingleInstance(LockName);
        if (held is null)
            return 0;
        var ran = 0;
        while (ran < maxJobs)
        {
            var item = Take();
            if (item is null)
                break;
            ran++;
            log($"job: {PyStr.Head(string.Join(' ', item.Argv), 200)} ({Pending().Count} more queued)");
            try
            {
                var done = runner.Run(item.Argv, item.Cwd, limit);
                if (done.ExitCode != 0)
                    log($"  exited {done.ExitCode}: {PyStr.Tail(PyStr.Strip(done.StandardError.Length > 0 ? done.StandardError : done.StandardOutput), 300)}");
            }
            catch (Exception error)
            {
                log($"  failed: {error.GetType().Name}: {error.Message}");
            }
        }
        return ran;
    }
}

/// <summary>Runs one queued job. Tests give the queue a fake.</summary>
public interface IJobRunner
{
    Completed Run(IReadOnlyList<string> argv, string cwd, TimeSpan timeout);
}

/// <summary>Runs a job with no console window, its output read as UTF-8 (errors replaced), stdin closed. A job that
/// runs past the timeout is killed with everything it started, then TimeoutException.</summary>
public sealed class JobRunner : IJobRunner
{
    public Completed Run(IReadOnlyList<string> argv, string cwd, TimeSpan timeout)
    {
        if (argv.Count == 0)
            throw new ArgumentException("an empty command line");
        var info = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in argv.Skip(1))
            info.ArgumentList.Add(arg);
        return ChildProcess.Run(info, null, timeout, $"Command {string.Join(' ', argv)}");
    }
}
