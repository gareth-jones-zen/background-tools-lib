namespace ReviewQueue;

/// <summary>A tool's <c>[review]</c> settings: where its items go, and under what source.
/// <code>
/// [review]                                      # optional: no section, no review queue
/// file = "%LOCALAPPDATA%/my-tools/review.json"  # the queue file
/// source = "my-tool"                            # the name this tool's items are filed under
/// lock = "my-tools-review"                      # optional: the writers' lock (default: folder-name, see QueueFile)
/// </code></summary>
public sealed record ReviewSettings(string File, string Source, string? Lock = null)
{
    public static readonly IReadOnlyList<string> Keys = ["file", "source", "lock"];

    /// <summary>The queue these settings name.</summary>
    public QueueFile Open(Func<DateTime>? clock = null) => new(File, Lock, clock: clock);

    /// <summary>A <c>[review]</c> table as a TOML reader gives it (a string-keyed dictionary), checked strictly: an
    /// unknown key, a value that isn't text or an empty one is a FormatException naming <paramref name="where"/> (the
    /// settings file). <paramref name="path"/> turns the file setting into a full path, as the tool does its other
    /// paths (expanding ~ and %VARIABLES%, relative to the settings file).</summary>
    public static ReviewSettings Read(object? section, string where, Func<string, string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (section is not IDictionary<string, object> table)
            throw new FormatException($"{where}: review must be a [review] table.");
        foreach (var key in table.Keys)
            if (!Keys.Contains(key))
                throw new FormatException($"{key} under [review] in {where} isn't a [review] setting (they're {string.Join(", ", Keys)}).");
        string? Text(string key) => table.TryGetValue(key, out var value)
            ? value is string { Length: > 0 } text ? text : throw new FormatException($"{key} under [review] in {where} should be text, and not empty.")
            : null;
        var file = Text("file") ?? throw new FormatException(
            $"[review] in {where} needs file, the review queue's path, as \"%LOCALAPPDATA%/my-tools/review.json\".");
        var source = Text("source") ?? throw new FormatException(
            $"[review] in {where} needs source, the name this tool's items go under, as \"my-tool\".");
        var lockName = Text("lock");
        if (lockName is not null && lockName.Contains('\\'))
            throw new FormatException($"lock under [review] in {where} can't contain a backslash.");
        return new ReviewSettings(path(file), source, lockName);
    }
}
