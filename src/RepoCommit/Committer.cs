using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;

namespace RepoCommit;

/// <summary>A file moved within the repository: committed as a rename (git records it as one when the content is the
/// same), with both paths in the commit.</summary>
public sealed record Rename(string From, string To);

/// <summary>What <see cref="Committer.Commit"/> did.
/// <para><b>Committed</b>: a commit was made, with <b>Commit</b> its short id and <b>Paths</b> what was in it (repository
/// paths, forward slashes). Nothing to commit is not an error: Committed is false and Error null.</para>
/// <para><b>Skipped</b>: why nothing could be committed because git isn't set up here: git isn't installed, the folder
/// isn't a git repository, or it's a folder inside one rather than its top. Not an error (Error is null): the files are
/// saved, and there's nothing to tell anyone.</para>
/// <para><b>Error</b>: why nothing was committed in a repository that is set up (a path outside it, git refusing: a hook,
/// a lock, a conflict). <b>Pushed</b> and <b>PushError</b>: the push, if one was asked for; a push that fails leaves the
/// commit in place. <b>PushSkipped</b>: why no push was tried (the branch has no upstream).</para></summary>
public sealed record CommitResult(bool Committed, string? Commit, IReadOnlyList<string> Paths, string? Error = null,
    bool Pushed = false, string? PushError = null, string? Skipped = null, string? PushSkipped = null)
{
    public static CommitResult Nothing { get; } = new(false, null, []);

    public static CommitResult Failed(string error) => new(false, null, [], error);

    public static CommitResult Skip(string reason) => new(false, null, [], Skipped: reason);
}

/// <summary>
/// Commits exactly the files a tool wrote into a git repository, and nothing else, then optionally pushes.
/// <code>
/// var git = new Committer(@"C:\notes", log.Info);
/// var result = git.Commit("Ingest exco-notes 2026-10-23", ["reports/exco-notes/2026-10-23.md"],
///     trailers: ["Co-Authored-By: Claude &lt;noreply@anthropic.com&gt;"], push: true);
/// </code>
/// <list type="bullet">
/// <item><b>Only the given paths</b> are staged and committed (<c>git add -A</c>, then <c>git commit</c>, both with the
/// paths as pathspecs), so whatever else is changed or staged in the repository is left exactly as it was. Every
/// pathspec is literal, so a name with <c>*</c> or <c>:</c> in it is only that file.</item>
/// <item><b>A path outside the repository</b> (or inside <c>.git</c>) refuses the whole commit: nothing is staged.</item>
/// <item><b>Nothing changed</b> in the given paths: no commit, and no error.</item>
/// <item><b>Another git holding the index</b> (<c>.git/index.lock</c>): tried again a few times, briefly.</item>
/// <item><b>A push that fails</b> is reported (<see cref="CommitResult.PushError"/>), never thrown, and the commit stays.
/// <b>A branch with no upstream</b> isn't pushed at all (<see cref="CommitResult.PushSkipped"/>), with one log line a
/// process.</item>
/// <item><b>git not set up</b> (not installed, the folder not a repository, or a folder inside a repository rather than
/// its top) is <see cref="CommitResult.Skipped"/>, not an error: logged once a process ("git: not committing: ..."), and
/// <see cref="Changed"/> and <see cref="Tracked"/> return nothing. Found out once a process for each folder.</item>
/// <item><b>Logged without file contents</b>: counts, the commit id and git's own error lines.</item>
/// </list>
/// Paths may be given in full, or relative to the repository's top (as git reports them, so what <see cref="Changed"/>
/// returns can be committed as it is). git is run from the PATH, never asked to prompt.
/// </summary>
public sealed class Committer
{
    private const int Chunk = 100;   // pathspecs per command line, for the commands that can't read them from stdin

    /// <summary>What each folder turned out to be, once a process: its top, or why it's skipped. Errors aren't kept.</summary>
    private static readonly ConcurrentDictionary<string, Probe> Probes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The once-a-process log lines already written.</summary>
    private static readonly ConcurrentDictionary<string, bool> Said = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<string> log;
    private readonly string? program;
    private readonly int lockRetries;
    private readonly TimeSpan lockWait;
    private readonly TimeSpan timeout;
    private readonly TimeSpan pushTimeout;
    private Probe? probe;

    /// <summary>A folder found to be a repository's top (Top), or not set up for committing (Skip, why).</summary>
    private sealed record Probe(string? Top, string? Skip);

    /// <param name="repo">The repository's folder: its top. A folder inside a repository is skipped (nothing is committed
    /// into a repository the folder only happens to sit in).</param>
    /// <param name="log">Where progress and problems go, a line at a time, each starting "git: ".</param>
    /// <param name="program">git itself; by default git on the PATH.</param>
    /// <param name="lockRetries">How many more times a command is tried while another git holds the index.</param>
    /// <param name="lockWait">How long to wait between those tries.</param>
    /// <param name="timeout">How long any one git command may take (default two minutes).</param>
    /// <param name="pushTimeout">How long the push may take (default five minutes).</param>
    public Committer(string repo, Action<string>? log = null, string? program = null, int lockRetries = 20,
        TimeSpan? lockWait = null, TimeSpan? timeout = null, TimeSpan? pushTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(repo);
        Repo = Path.GetFullPath(repo);
        this.log = log ?? (_ => { });
        this.program = program;
        this.lockRetries = Math.Max(0, lockRetries);
        this.lockWait = lockWait ?? TimeSpan.FromMilliseconds(500);
        this.timeout = timeout ?? TimeSpan.FromMinutes(2);
        this.pushTimeout = pushTimeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>The repository folder given, in full.</summary>
    public string Repo { get; }

    /// <summary>The top of the working tree (the folder holding .git), which is <see cref="Repo"/>. Throws
    /// <see cref="InvalidOperationException"/> if it isn't one (see <see cref="Skipped"/>), or git can't say.</summary>
    public string Top => Probed().Top ?? throw new InvalidOperationException(Probed().Skip);

    /// <summary>Why nothing will be committed here because git isn't set up: git isn't installed, <see cref="Repo"/>
    /// isn't a git repository, or it's a folder inside one rather than its top. Null when it's a repository's top (or git
    /// failed in some other way, which <see cref="Commit"/> reports as an error). Found out once a process for each
    /// folder, and logged the first time ("git: not committing: ...").</summary>
    public string? Skipped
    {
        get
        {
            try
            {
                return Skipping();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>The skip reason (logged once a process), or null; throws InvalidOperationException if git can't say.</summary>
    private string? Skipping()
    {
        var skip = Probed().Skip;
        if (skip is not null)
            Once($"{Repo}\0skip", $"git: not committing: {skip}");
        return skip;
    }

    /// <summary>Logs a line the first time this process asks for it.</summary>
    private void Once(string key, string line)
    {
        if (Said.TryAdd(key, true))
            log(line);
    }

    private Probe Probed()
    {
        if (probe is not null)
            return probe;
        var key = (program ?? "") + "\0" + Repo;
        if (Probes.TryGetValue(key, out var known))
            return probe = known;
        return probe = Probes.GetOrAdd(key, FindTop());
    }

    /// <summary>A commit message: the subject, a blank line and the body (if any), then a blank line and the trailers
    /// (if any), one to a line.</summary>
    public static string Message(string subject, string? body = null, IEnumerable<string>? trailers = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var message = new StringBuilder(subject);
        if (!string.IsNullOrEmpty(body))
            message.Append("\n\n").Append(body);
        var lines = trailers?.ToList() ?? [];
        if (lines.Count > 0)
            message.Append("\n\n").Append(string.Join("\n", lines));
        return message.ToString();
    }

    /// <summary>A path as the repository knows it: relative to its top, with forward slashes. Null for anything outside
    /// it, the top itself, or anything in .git. A relative path is taken as relative to the top.</summary>
    public string? RepoPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Contains('\0'))
            return null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(Top, path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var relative = Path.GetRelativePath(Top, full);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        relative = relative.Replace('\\', '/');
        var first = relative.Split('/')[0];
        return string.Equals(first, ".git", StringComparison.OrdinalIgnoreCase) ? null : relative;
    }

    /// <summary>Of these paths, the ones git sees as changed: modified, added, deleted or new (untracked, not ignored). In
    /// git status's order (changed tracked files by path, then new ones by path), as repository paths. Paths outside
    /// the repository are left out. Nothing when git isn't set up here (<see cref="Skipped"/>).</summary>
    public IReadOnlyList<string> Changed(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Skipping() is null ? ChangedIn(Inside(paths)) : [];
    }

    /// <summary>Changed, for paths already relative to the top.</summary>
    private List<string> ChangedIn(List<string> inside)
    {
        var tracked = new List<string>();
        var untracked = new List<string>();
        foreach (var chunk in inside.Chunk(Chunk))
        {
            var done = Git(["status", "--porcelain", "-z", "-uall", "--", .. chunk]);
            if (done.ExitCode != 0)
                throw new GitException("status", done);
            var entries = done.Output.Split('\0');
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Length < 4)
                    continue;
                var status = entry[..2];
                (status == "??" ? untracked : tracked).Add(entry[3..]);
                if (status[0] is 'R' or 'C')
                    i++;   // a rename or copy in the index: the next entry is where it came from
            }
        }
        return [.. Sorted(tracked.Distinct()), .. Sorted(untracked.Distinct())];
    }

    /// <summary>Of these paths, the ones git tracks (in the index), as repository paths. None when git isn't set up here
    /// (<see cref="Skipped"/>).</summary>
    public IReadOnlySet<string> Tracked(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var tracked = new HashSet<string>(StringComparer.Ordinal);
        if (Skipping() is not null)
            return tracked;
        foreach (var chunk in Inside(paths).Chunk(Chunk))
        {
            var done = Git(["ls-files", "-z", "--", .. chunk]);
            if (done.ExitCode != 0)
                throw new GitException("ls-files", done);
            foreach (var path in done.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                tracked.Add(path);
        }
        return tracked;
    }

    /// <summary>Commit the changes in exactly these paths (and both sides of each rename), with this message and the
    /// trailers as its last paragraph, then push if asked. Never throws for git's own failures: they're in the result
    /// and the log. git not set up here is <see cref="CommitResult.Skipped"/>, not an error.</summary>
    public CommitResult Commit(string message, IEnumerable<string> paths, IEnumerable<Rename>? renames = null,
        IEnumerable<string>? trailers = null, bool push = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentNullException.ThrowIfNull(paths);
        var given = paths.ToList();
        foreach (var rename in renames ?? [])
        {
            ArgumentNullException.ThrowIfNull(rename);
            given.Add(rename.From);
            given.Add(rename.To);
        }
        var lines = trailers?.ToList() ?? [];
        if (given.Count == 0)
            return CommitResult.Nothing;
        try
        {
            if (Skipping() is { } skip)
                return CommitResult.Skip(skip);
            var all = new List<string>();
            foreach (var path in given)
            {
                var inside = RepoPath(path ?? throw new ArgumentNullException(nameof(paths)));
                if (inside is null)
                    return Fail($"{path} isn't inside the repository {Top}; nothing committed.");
                if (!all.Contains(inside, StringComparer.Ordinal))
                    all.Add(inside);
            }
            if (all.Count == 0)
                return CommitResult.Nothing;
            var changed = ChangedIn(all);
            if (changed.Count == 0)
            {
                log("git: nothing to commit");
                return CommitResult.Nothing;
            }
            var pathspecs = string.Join('\0', changed);
            var added = Locked(["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], pathspecs);
            if (added.ExitCode != 0)
                return Fail($"git add failed: {Tail(added)}");
            var committed = Locked(["commit", "-q", "-m", lines.Count > 0 ? Message(message, null, lines) : message,
                "--pathspec-from-file=-", "--pathspec-file-nul"], pathspecs);
            if (committed.ExitCode != 0)
                return Fail($"git commit failed: {Tail(committed)}");
            var id = Git(["rev-parse", "--short", "HEAD"]);
            var commit = id.ExitCode == 0 ? id.Output.Trim() : null;
            log($"git: committed {changed.Count} path(s)" + (commit is null ? "" : $" as {commit}"));
            var result = new CommitResult(true, commit, changed);
            return push ? Push(result) : result;
        }
        catch (Exception error) when (error is GitException or Win32Exception or TimeoutException or InvalidOperationException or IOException)
        {
            return Fail(error is Win32Exception ? $"couldn't run git: {error.Message}" : error.Message);
        }
    }

    private CommitResult Push(CommitResult result)
    {
        try
        {
            // No upstream (no remote, or a branch never pushed): nothing to push to, so don't try, and say so once.
            var upstream = Git(["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"]);
            if (upstream.ExitCode != 0)
            {
                const string none = "the branch has no upstream to push to";
                Once($"{Repo}\0push", $"git: not pushing: {none}");
                return result with { PushSkipped = none };
            }
            var done = Git(["push", "-q"], timeoutOverride: pushTimeout);
            if (done.ExitCode == 0)
            {
                log("git: pushed");
                return result with { Pushed = true };
            }
            var why = Tail(done);
            log($"git: push failed: {why}");
            return result with { PushError = why };
        }
        catch (Exception error) when (error is Win32Exception or TimeoutException or IOException)
        {
            log($"git: push failed: {error.Message}");
            return result with { PushError = error.Message };
        }
    }

    private CommitResult Fail(string error)
    {
        log($"git: {error}");
        return CommitResult.Failed(error);
    }

    /// <summary>The repository paths of those inside it, once each, in order.</summary>
    private List<string> Inside(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var inside = new List<string>();
        foreach (var path in paths)
            if (path is not null && RepoPath(path) is { } p && !inside.Contains(p, StringComparer.Ordinal))
                inside.Add(p);
        return inside;
    }

    /// <summary>git's order: by the bytes of the UTF-8 path.</summary>
    private static IEnumerable<string> Sorted(IEnumerable<string> paths) =>
        paths.OrderBy(p => p, Comparer<string>.Create((a, b) =>
            Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b))));

    /// <summary>What <see cref="Repo"/> is: a repository's top, or why it's skipped. Throws InvalidOperationException
    /// (not kept, so it's tried again) when the folder doesn't exist or git fails in some other way.</summary>
    private Probe FindTop()
    {
        var git = program ?? GitProcess.Find();
        if (git is null || (Path.IsPathRooted(git) && !File.Exists(git)))
            return new Probe(null, "git isn't installed (there's no git on the PATH).");
        if (!Directory.Exists(Repo))
            throw new InvalidOperationException($"{Repo} isn't a folder.");
        GitOutput done;
        try
        {
            done = GitProcess.Run(git, Repo, ["rev-parse", "--is-inside-work-tree", "--show-prefix"], null, timeout);
        }
        catch (Exception error) when (error is Win32Exception or TimeoutException or IOException)
        {
            throw new InvalidOperationException($"couldn't run git: {error.Message}", error);
        }
        if (done.ExitCode != 0 && (done.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) || !GitAbove(Repo)))
            return new Probe(null, $"{Repo} isn't a git repository.");
        var lines = done.Output.Replace("\r\n", "\n").Split('\n');
        if (done.ExitCode != 0 || lines[0] != "true")
            throw new InvalidOperationException($"{Repo} isn't in a git working tree" + (done.ExitCode != 0 ? $": {Tail(done)}" : "."));
        var prefix = lines.Length > 1 ? lines[1] : "";
        if (prefix.Length == 0)
            return new Probe(Repo, null);
        var folder = Path.TrimEndingDirectorySeparator(Repo);
        foreach (var _ in prefix.Split('/', StringSplitOptions.RemoveEmptyEntries))
            folder = Path.GetDirectoryName(folder) ?? folder;
        return new Probe(null, $"{Repo} is inside the git repository at {folder}, not its top; nothing is committed into it.");
    }

    /// <summary>Whether this folder or one above it holds a .git (a folder, or a file for a worktree): if none does, git's
    /// refusal means "not a repository" in whatever language git speaks.</summary>
    private static bool GitAbove(string folder)
    {
        for (var at = folder; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
            if (Directory.Exists(Path.Combine(at, ".git")) || File.Exists(Path.Combine(at, ".git")))
                return true;
        return false;
    }

    /// <summary>A command that takes the index lock: tried again while another git holds it.</summary>
    private GitOutput Locked(IReadOnlyList<string> args, string? stdin)
    {
        for (var attempt = 0; ; attempt++)
        {
            var done = Git(args, stdin);
            if (done.ExitCode == 0 || attempt >= lockRetries || !done.Error.Contains("index.lock", StringComparison.Ordinal))
                return done;
            if (attempt == 0)
                log("git: another git is using the index (index.lock); trying again");
            Thread.Sleep(lockWait);
        }
    }

    private GitOutput Git(IReadOnlyList<string> args, string? stdin = null, string? folder = null, TimeSpan? timeoutOverride = null)
    {
        var git = program ?? GitProcess.Find() ?? throw new Win32Exception("git isn't on the PATH.");
        return GitProcess.Run(git, folder ?? Top, args, stdin, timeoutOverride ?? timeout);
    }

    /// <summary>The end of git's complaint, on one line.</summary>
    internal static string Tail(GitOutput done)
    {
        var text = (done.Error.Trim().Length > 0 ? done.Error : done.Output).Trim().Replace("\r\n", "\n").Replace('\n', ' ');
        if (text.Length == 0)
            text = $"exit code {done.ExitCode}";
        return text.Length > 300 ? text[^300..] : text;
    }
}

/// <summary>A git command that had to work didn't.</summary>
public sealed class GitException(string command, GitOutput done) : Exception($"git {command} failed: {Committer.Tail(done)}")
{
    public GitOutput Output { get; } = done;
}
