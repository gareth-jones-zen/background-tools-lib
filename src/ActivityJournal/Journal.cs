using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace ActivityJournal;

/// <summary>
/// A shared journal of what background tools did: <b>one JSON line per thing done</b>, in one file every tool appends to
/// (JSON Lines: UTF-8 with no byte order mark, each line ending in LF), for a person, or the screen that shows them
/// "while you were away", to read back by time and by tool.
/// <para><b>Writes take a lock between processes</b> (a <see cref="NamedLock"/>, <c>Local\{LockName}</c>) and append one
/// whole line, so tools writing at the same moment never interleave. <b>Reading takes no lock</b>: it opens the file
/// shared, and a last line with no newline (a writer stopped half-way) is left out.</para>
/// <para><b>Old lines are pruned</b>: a write whose first line is more than <see cref="KeepDays"/> days old (and a day's
/// grace) rewrites the file without them, through a temporary file and one move, at most about once a day. A reader
/// holding the file open without sharing it only delays the pruning to a later write.</para>
/// </summary>
public sealed class Journal
{
    /// <summary>The usual file name, in the tools' state folder: <c>%EA_LOCAL%\journal.jsonl</c>.</summary>
    public const string FileName = "journal.jsonl";

    public const int DefaultKeepDays = 90;

    private static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

    private readonly Func<DateTimeOffset> clock;

    /// <param name="path">The journal file. It and its folder are made on the first write.</param>
    /// <param name="lockName">The mutex every writer takes; by default <see cref="DefaultLockName"/>.</param>
    /// <param name="keepDays">How long a line is kept (at least 1).</param>
    /// <param name="lockTimeoutSeconds">How long a write waits for another one before a TimeoutException.</param>
    /// <param name="clock">"Now", for a line's time and for pruning; DateTimeOffset.Now by default.</param>
    public Journal(string path, string? lockName = null, int keepDays = DefaultKeepDays, double lockTimeoutSeconds = 30,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepDays, 1);
        if (lockName is not null && (lockName.Length == 0 || lockName.Contains('\\')))
            throw new ArgumentException($"A lock name is some text with no backslash: {lockName}", nameof(lockName));
        Path = System.IO.Path.GetFullPath(path);
        LockName = lockName ?? DefaultLockName(Path);
        KeepDays = keepDays;
        LockTimeoutSeconds = lockTimeoutSeconds;
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Path { get; }

    public string LockName { get; }

    public int KeepDays { get; }

    public double LockTimeoutSeconds { get; }

    /// <summary>The lock when none is given: the folder's name and the file's name without its extension, joined by a
    /// hyphen and lower-cased, so <c>...\my-right-hand\journal.jsonl</c> is <c>my-right-hand-journal</c>.</summary>
    public static string DefaultLockName(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(full)) ?? "";
        var stem = System.IO.Path.GetFileNameWithoutExtension(full);
        return PyStr.Lower(folder.Length > 0 ? $"{folder}-{stem}" : stem);
    }

    /// <summary>Append one line. Its id is made here, and its time is now unless one was given. Returns the line as
    /// written. Throws ArgumentException for a line that isn't valid (no tool or what, an unknown sensitivity), a
    /// TimeoutException when another writer holds the lock too long, and IOException when the file can't be written.</summary>
    public JournalEntry Write(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Check();
        var when = entry.When == default ? clock() : entry.When;
        var stored = entry with
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
            When = when.AddTicks(-(when.Ticks % TimeSpan.TicksPerMillisecond)),   // as written: to the millisecond
        };
        var line = stored.ToJson().ToJsonString(LineOptions) + "\n";
        using (Locks.Exclusive(LockName, LockTimeoutSeconds, What))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (PruneDue())
                PruneLocked();
            Retry(() =>
            {
                using var stream = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
                var bytes = new List<byte>();
                if (stream.Length > 0)
                {
                    // A writer that stopped half-way left a line with no end: end it, so this line starts on its own.
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                        bytes.Add((byte)'\n');
                }
                bytes.AddRange(Encoding.UTF8.GetBytes(line));
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes.ToArray());
                stream.Flush(flushToDisk: false);
            });
        }
        return stored;
    }

    /// <summary><see cref="Write"/>, but never throws: a tool's action has already happened, and the journal failing mustn't
    /// undo or fail it. Returns false, telling <paramref name="problem"/> why, when the line couldn't be written.</summary>
    public bool TryWrite(JournalEntry entry, Action<string>? problem = null)
    {
        try
        {
            Write(entry);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            problem?.Invoke($"journal: couldn't write to {Path}: {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    /// <summary>The lines, oldest first: those at or after <paramref name="since"/> (all, if null), and only
    /// <paramref name="tool"/>'s (every tool's, if null). Takes no lock. A line that isn't JSON, or isn't a journal line,
    /// and a last line with no newline (still being written, or cut off) are left out. A missing file is empty.</summary>
    public IReadOnlyList<JournalEntry> Read(DateTimeOffset? since = null, string? tool = null)
    {
        var found = new List<JournalEntry>();
        foreach (var line in Lines())
            if (Parse(line) is { } entry && (since is null || entry.When >= since) && (tool is null || entry.Tool == tool))
                found.Add(entry);
        return found;
    }

    /// <summary>Drop the lines older than KeepDays now (pruning otherwise happens by itself on a write, about once a day).
    /// Returns how many went; 0, changing nothing, when none are that old or the file couldn't be replaced just now.</summary>
    public int Prune()
    {
        using (Locks.Exclusive(LockName, LockTimeoutSeconds, What))
            return PruneLocked();
    }

    private string What => $"{System.IO.Path.GetFileName(Path)} write";

    private DateTimeOffset Cutoff => clock() - TimeSpan.FromDays(KeepDays);

    private static JournalEntry? Parse(string line)
    {
        try
        {
            return JournalEntry.FromJson(JsonNode.Parse(line));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every whole line (ended by LF), read shared so writers and the pruning move aren't held up.</summary>
    private List<string> Lines()
    {
        byte[] bytes;
        try
        {
            bytes = Retry(() =>
            {
                using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            });
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        var end = Array.LastIndexOf(bytes, (byte)'\n');
        if (end < 0)
            return [];
        var text = new UTF8Encoding(false, false).GetString(bytes, 0, end);
        return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }

    /// <summary>Under the lock: whether the first line is older than KeepDays and a day, so pruning runs about daily.</summary>
    private bool PruneDue()
    {
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, false));
            for (var n = 0; n < 5 && reader.ReadLine() is { } line; n++)   // past up to 4 lines that aren't journal lines
                if (Parse(line) is { } first)
                    return first.When < Cutoff - TimeSpan.FromDays(1);
            return false;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Under the lock: rewrite the file without lines older than the cutoff (and without lines that aren't
    /// journal lines). A temporary file beside it, then one move; if the move is refused, the file is left as it was.</summary>
    private int PruneLocked()
    {
        var lines = Lines();
        var cutoff = Cutoff;
        var kept = lines.Where(l => Parse(l) is { } e && e.When >= cutoff).ToList();
        if (kept.Count == lines.Count)
            return 0;
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var temp = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllText(temp, string.Concat(kept.Select(l => l + "\n")), new UTF8Encoding(false));
            Retry(() => File.Move(temp, Path, overwrite: true));
            return lines.Count - kept.Count;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            return 0;
        }
    }

    /// <summary>A reader holding the file open (an editor, a virus scanner) can briefly stop a write or a move; try again
    /// a few times before giving up.</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception error) when (attempt < 5 && error is UnauthorizedAccessException
                                          || attempt < 5 && error is IOException and not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static void Retry(Action action) => Retry(() =>
    {
        action();
        return 0;
    });
}
