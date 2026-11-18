using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace ReviewQueue;

/// <summary>One item for <see cref="QueueFile.Replace"/>: its type and its other fields (a title, dates, a path...).</summary>
public sealed record ReviewItem(string Type, JsonObject Fields);

/// <summary>
/// A review queue a session reads: one JSON file of items that background tools leave for a person, or for an
/// assistant's next session, to look at and clear.
/// <code>
/// {"items": [{"id": "a1b2c3d4", "source": "my-tool", "type": "question", "added": "2026-11-17T09:00:00", "title": "...", ...}]}
/// </code>
/// <para><b>Every write takes a lock between processes and replaces the file atomically</b> (a temporary file beside it,
/// then one move), so two tools writing at the same moment never lose an item and a reader never sees half a file.
/// The lock is a named mutex, <c>Local\{LockName}</c>; any other program that takes the same mutex before writing
/// (a Python one through win-background's <c>exclusive(name)</c>, say) shares it.</para>
/// <para><b>An item's id</b> is the first 8 hex characters of the SHA-256 of its fields (all but id and added) as
/// Python's <c>json.dumps(fields, sort_keys=True)</c> writes them, so the same item gets the same id whichever
/// program wrote it, and adding it twice keeps one.</para>
/// <para><b>The file</b> is written as Python's <c>json.dumps(data, indent=2, ensure_ascii=False)</c> in text mode
/// on Windows: UTF-8 with no byte order mark, CRLF line ends, no newline at the end, and any characters kept as they
/// are. Keys and other top-level fields keep their order. <b>A file that isn't JSON</b> is renamed
/// <c>{name}.broken-{yyyyMMdd-HHmmss}{extension}</c> beside it, for a person to look at, and the queue starts empty;
/// JSON that isn't a queue (no "items" list of objects) is an <see cref="InvalidDataException"/>, and nothing is
/// written.</para>
/// </summary>
public sealed class QueueFile
{
    /// <summary>The fields every item has, which an item's own fields can't use.</summary>
    public static readonly IReadOnlyList<string> Reserved = ["id", "source", "type", "added"];

    private readonly Func<DateTime> clock;

    /// <param name="path">The queue file. It and its folder are made on the first write.</param>
    /// <param name="lockName">The mutex every writer takes; by default <see cref="DefaultLockName"/>.</param>
    /// <param name="lockTimeoutSeconds">How long a write waits for another one to finish before a TimeoutException.</param>
    /// <param name="clock">Local "now", for an item's added time; DateTime.Now by default.</param>
    public QueueFile(string path, string? lockName = null, double lockTimeoutSeconds = 60, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (lockName is not null)
            CheckLockName(lockName);
        Path = System.IO.Path.GetFullPath(path);
        LockName = lockName ?? DefaultLockName(Path);
        LockTimeoutSeconds = lockTimeoutSeconds;
        this.clock = clock ?? (() => DateTime.Now);
    }

    public string Path { get; }

    public string LockName { get; }

    public double LockTimeoutSeconds { get; }

    /// <summary>The lock for a queue file when none is given: its folder's name and its name without the extension,
    /// joined by a hyphen and lower-cased (as Python's str.lower()), so <c>...\my-tools\review.json</c> is locked as
    /// <c>my-tools-review</c>. Two different queues that happen to share a name only take turns; give a lock name
    /// to keep them apart.</summary>
    public static string DefaultLockName(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(full)) ?? "";
        var stem = System.IO.Path.GetFileNameWithoutExtension(full);
        return PyStr.Lower(folder.Length > 0 ? $"{folder}-{stem}" : stem);
    }

    /// <summary>A mutex name: some text, with no backslash (Windows reserves it for the namespace).</summary>
    public static void CheckLockName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\\'))
            throw new ArgumentException($"A lock name can't contain a backslash: {name}", nameof(name));
    }

    /// <summary>An item's id: the first 8 hex characters of the SHA-256 of all its fields but id and added, as
    /// Python's <c>json.dumps(fields, sort_keys=True)</c> writes them (", " and ": " between, non-ASCII as \uXXXX,
    /// keys in code point order at every level).</summary>
    public static string Id(JsonObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var basis = new JsonObject();
        foreach (var (key, value) in item)
            if (key is not ("id" or "added"))
                basis[key] = value?.DeepClone();
        var text = PyJson.Dump(basis, indent: null, ensureAscii: true, sortKeys: true);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
    }

    /// <summary>Add an item. The same item twice (same source, type and fields) is kept once. Returns its id.</summary>
    public string Add(string source, string type, JsonObject? fields = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(type);
        fields ??= [];
        CheckFields(fields, Reserved);
        var item = new JsonObject { ["source"] = source, ["type"] = type };
        Copy(fields, item);
        var id = Id(item);
        using (Locks.Exclusive(LockName, LockTimeoutSeconds, What))
        {
            var data = Read();
            var items = data["items"]!.AsArray();
            if (!items.Any(i => Text(i!["id"]) == id))
            {
                var stored = new JsonObject
                {
                    ["id"] = id,
                    ["source"] = source,
                    ["type"] = type,
                    ["added"] = Now(),
                };
                Copy(fields, stored);
                items.Add((JsonNode)stored);
                Write(data);
            }
        }
        return id;
    }

    /// <summary>For a source whose items are its current state (a run's open questions, say): drop all of its items
    /// and add these, all with the same added time. An empty list clears the source. Returns the new items' ids.</summary>
    public IReadOnlyList<string> Replace(string source, IEnumerable<ReviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(items);
        var given = items.ToList();
        foreach (var item in given)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Type);
            ArgumentNullException.ThrowIfNull(item.Fields);
            CheckFields(item.Fields, Reserved);
        }
        var ids = new List<string>();
        using (Locks.Exclusive(LockName, LockTimeoutSeconds, What))
        {
            var data = Read();
            var list = data["items"]!.AsArray();
            foreach (var old in list.Where(i => Text(i!["source"]) == source).ToList())
                list.Remove(old);
            var now = Now();
            foreach (var item in given)
            {
                var whole = new JsonObject { ["source"] = source, ["type"] = item.Type };
                Copy(item.Fields, whole);
                var id = Id(whole);
                var stored = new JsonObject { ["id"] = id, ["added"] = now };
                Copy(whole, stored);
                list.Add((JsonNode)stored);
                ids.Add(id);
            }
            Write(data);
        }
        return ids;
    }

    /// <summary>Set fields on this source's items whose fields include every one in <paramref name="match"/> (a
    /// missing field matches null; numbers compare as numbers). The id isn't changed. Returns how many changed; the
    /// file is written only if one did.</summary>
    public int Update(string source, JsonObject match, JsonObject fields)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(fields);
        CheckFields(fields, ["id", "source"]);
        using (Locks.Exclusive(LockName, LockTimeoutSeconds, What))
        {
            var data = Read();
            var changed = 0;
            foreach (var node in data["items"]!.AsArray())
            {
                var item = node!.AsObject();
                if (Text(item["source"]) != source || !match.All(pair => PyJson.Equal(item[pair.Key], pair.Value)))
                    continue;
                foreach (var (key, value) in fields)
                    item[key] = value?.DeepClone();
                changed++;
            }
            if (changed > 0)
                Write(data);
            return changed;
        }
    }

    /// <summary>The items, all or one source's, in the file's order (copies: changing them changes nothing).</summary>
    public IReadOnlyList<JsonObject> Items(string? source = null)
    {
        using (Locks.Exclusive(LockName, LockTimeoutSeconds, What))
            return Read()["items"]!.AsArray()
                .Where(i => source is null || Text(i!["source"]) == source)
                .Select(i => i!.DeepClone().AsObject())
                .ToList();
    }

    /// <summary>Remove items by id, or all of a source's by its name. Returns how many went.</summary>
    public int Done(IEnumerable<string> idsOrSources)
    {
        ArgumentNullException.ThrowIfNull(idsOrSources);
        var gone = idsOrSources.ToHashSet(StringComparer.Ordinal);
        using (Locks.Exclusive(LockName, LockTimeoutSeconds, What))
        {
            var data = Read();
            var list = data["items"]!.AsArray();
            var leaving = list.Where(i => gone.Contains(Text(i!["id"]) ?? "\0") || gone.Contains(Text(i!["source"]) ?? "\0")).ToList();
            foreach (var item in leaving)
                list.Remove(item);
            if (leaving.Count > 0)   // v0.6: nothing matched, nothing written
                Write(data);
            return leaving.Count;
        }
    }

    private string What => $"{System.IO.Path.GetFileName(Path)} write";

    private string Now() => PyTime.Iso(clock(), "seconds");

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static void Copy(JsonObject from, JsonObject to)
    {
        foreach (var (key, value) in from)
            to[key] = value?.DeepClone();
    }

    private static void CheckFields(JsonObject fields, IReadOnlyList<string> reserved)
    {
        foreach (var key in fields.Select(pair => pair.Key))
            if (reserved.Contains(key))
                throw new ArgumentException($"An item's fields can't include {key}: the queue sets it.", nameof(fields));
    }

    /// <summary>Under the lock: the queue as it is. Missing: empty. Not JSON: kept aside as .broken, and empty.</summary>
    internal JsonObject Read()
    {
        string text;
        try
        {
            text = Retry(() => PyJson.ReadStrict(Path));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Empty();
        }
        JsonNode? parsed;
        try
        {
            parsed = PyJson.Parse(PyStr.UniversalNewlines(text));
        }
        catch (JsonException)
        {
            KeepBroken();
            return Empty();
        }
        if (parsed is not JsonObject data || !data.TryGetPropertyValue("items", out var items) || items is not JsonArray list
            || list.Any(i => i is not JsonObject))
            throw new InvalidDataException($"{Path} isn't a review queue: it should hold {{\"items\": [...]}}, a list of objects.");
        return data;
    }

    private static JsonObject Empty() => new() { ["items"] = new JsonArray() };

    private void KeepBroken()
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var broken = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.broken-"
            + $"{clock().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}{System.IO.Path.GetExtension(Path)}");
        Retry(() => File.Move(Path, broken, overwrite: true));
    }

    /// <summary>Under the lock: a temporary file beside the queue, then one move over it.</summary>
    internal void Write(JsonObject data)
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(folder);
        var temp = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.{Environment.ProcessId}.tmp");
        PyStr.WriteText(temp, PyJson.Dump(data, indent: 2, ensureAscii: false));
        try
        {
            Retry(() => File.Move(temp, Path, overwrite: true));
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    /// <summary>A reader holding the file open (an editor, a virus scanner) can briefly stop a move or a read; try
    /// again a few times before giving up.</summary>
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
