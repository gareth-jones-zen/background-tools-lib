using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace ClaudeHeadless;

/// <summary>Runs Claude Code headless. Tools take this rather than ClaudeRunner, so their tests can give a fake and
/// never spend usage.</summary>
public interface IClaudeRunner
{
    /// <summary>command is everything after the executable, such as ["-p", "--output-format", "json", "--model",
    /// "opus"]; the prompt goes on stdin. Returns the parsed JSON result. Throws LimitReachedException at the
    /// usage limit (or while one is in force), TimeoutException, or ClaudeRunException for anything else.</summary>
    JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout);
}

/// <summary>Starts a process with the prompt on stdin and returns what it wrote. Tests give a fake.</summary>
public interface IProcessStarter
{
    Completed Run(string exe, IReadOnlyList<string> args, string input, string cwd, IReadOnlyDictionary<string, string> extraEnv, TimeSpan timeout);
}

/// <summary>One headless Claude Code run: `claude -p ... --output-format json`, with the prompt on stdin.
///
/// Learned on a laptop running it unattended (October 2026):
/// - **The usage limit comes back in three shapes:** plain text on stderr ("You've hit your session limit ·
///   resets 7pm (Europe/London)"), a JSON result with is_error, or an API 429. All become LimitReachedException,
///   with the reset time read from the message (an hour from now if it can't be read).
/// - **Once a limit is known, later runs don't start** until it has passed, so a batch doesn't fail one job
///   after another against it.
/// - **An auto-update briefly removes npm's `claude` shim**, which killed a run; the executable is waited for,
///   and each run turns the auto-updater off for itself.
/// - **No console window**, and the output is read as UTF-8 whatever the console's code page.</summary>
public sealed class ClaudeRunner(JobQueue? queue = null, IProcessStarter? starter = null, Func<string?>? findExe = null,
    Action<TimeSpan>? sleep = null) : IClaudeRunner
{
    private readonly JobQueue queue = queue ?? JobQueue.Default;
    private readonly IProcessStarter starter = starter ?? new ProcessStarter();
    private readonly Func<string?> findExe = findExe ?? (() => Which("claude", Environment.GetEnvironmentVariable));
    private readonly Action<TimeSpan> sleep = sleep ?? Thread.Sleep;

    /// <summary>The claude executable, waiting up to two minutes for it (an auto-update briefly removes it).</summary>
    public string Exe()
    {
        for (var i = 0; i < 24; i++)
        {
            if (findExe() is { } found)
                return found;
            sleep(TimeSpan.FromSeconds(5));
        }
        throw new ClaudeRunException("The claude CLI isn't on the PATH (is Claude Code installed?)");
    }

    public JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout)
    {
        if (queue.LimitUntil() is { } until)
            throw new LimitReachedException(until, $"Claude's usage limit is in force until {Limits.Clock(until)}");
        var done = starter.Run(Exe(), command, prompt, cwd, new Dictionary<string, string> { ["DISABLE_AUTOUPDATER"] = "1" }, timeout);
        JsonNode? parsed;
        try
        {
            parsed = PyJson.Parse(done.StandardOutput);
        }
        catch (JsonException)
        {
            var text = done.StandardError.Length > 0 ? done.StandardError : done.StandardOutput;
            if (Limits.LimitText().IsMatch(text))
                throw new LimitReachedException(queue.Remember(Limits.ResetTime(text)), PyStr.Head(PyStr.Strip(text), 200));
            throw new ClaudeRunException($"claude exited {done.ExitCode}: {PyStr.Head(text, 300)}");
        }
        var result = parsed as JsonObject;
        if (result is null || Py.Truthy(result["is_error"]) || !Py.Truthy(result["result"]))
        {
            var text = PyJson.Dump(parsed);
            var status = result?["api_error_status"];
            var is429 = status is JsonValue v && v.GetValueKind() == JsonValueKind.Number && Py.Float(v) == 429;
            if (is429 || Limits.LimitText().IsMatch(text) || text.Contains("rate_limit", StringComparison.Ordinal))
            {
                var said = result is not null && Py.Truthy(result["result"]) ? Py.Str(result["result"]) : null;
                throw new LimitReachedException(queue.Remember(Limits.ResetTime(said ?? text)), PyStr.Head(said ?? "rate limited", 200));
            }
            throw new ClaudeRunException($"claude reported an error: {PyStr.Head(text, 300)}");
        }
        return result;
    }

    /// <summary>shutil.which on Windows: each folder on the PATH, each extension in PATHEXT. (Python 3.12 and later
    /// also look in the current folder first; this doesn't, so a stray claude.cmd there can't be run.) Falls back
    /// to npm's shim in %APPDATA%\npm, as the Python version does.</summary>
    public static string? Which(string name, Func<string, string?> env)
    {
        var extensions = (env("PATHEXT") is { Length: > 0 } pathext ? pathext : ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var hasExtension = extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        var files = hasExtension ? [name] : extensions.Select(e => name + e).ToArray();
        foreach (var folder in (env("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var file in files)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(folder.Trim('"'), file);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        var npm = Path.Combine(env("APPDATA") ?? "", "npm", "claude.cmd");
        return name == "claude" && File.Exists(npm) ? npm : null;
    }
}

/// <summary>The real process starter: no console window, the environment plus the extra variables.</summary>
public sealed class ProcessStarter : IProcessStarter
{
    public Completed Run(string exe, IReadOnlyList<string> args, string input, string cwd, IReadOnlyDictionary<string, string> extraEnv, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = cwd };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        foreach (var (key, value) in extraEnv)
            info.Environment[key] = value;
        return ChildProcess.Run(info, input, timeout, $"Command {exe}");
    }
}
