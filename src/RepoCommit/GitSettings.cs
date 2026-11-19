using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoCommit;

/// <summary>A tool's <c>[git]</c> settings: which repository it commits what it writes into, and how.
/// <code>
/// [git]                                   # optional: no section, nothing committed
/// repo = ".."                             # the repository: its top (a folder inside one is skipped)
/// commit = true                           # optional (default true): false keeps the section but commits nothing
/// push = true                             # optional (default false): push after each commit
/// trailers = ["Co-Authored-By: Name &lt;name@example.com&gt;"]   # optional: lines at the end of every message
/// </code>
/// The same keys work as a JSON object, for a tool whose settings are JSON.</summary>
public sealed partial record GitSettings(string Repo, bool Commit = true, bool Push = false)
{
    public static readonly IReadOnlyList<string> Keys = ["repo", "commit", "push", "trailers"];

    /// <summary>Lines added as the last paragraph of every commit message: git trailers, "Key: value".</summary>
    public IReadOnlyList<string> Trailers { get; init; } = [];

    /// <summary>A committer for this repository.</summary>
    public Committer Open(Action<string>? log = null) => new(Repo, log);

    /// <summary>A <c>[git]</c> section as a TOML reader gives it (a string-keyed dictionary) or as a JSON object, checked
    /// strictly: an unknown key, or a value of the wrong kind, is a FormatException naming <paramref name="name"/> (how the
    /// section is written in the settings: "[git]" by default) and <paramref name="where"/> (the settings file).
    /// <paramref name="path"/> turns repo into a full path, as the tool does its other paths.</summary>
    public static GitSettings Read(object? section, string where, Func<string, string> path, string name = "[git]")
    {
        ArgumentNullException.ThrowIfNull(path);
        var table = section switch
        {
            JsonObject json => json.ToDictionary(pair => pair.Key, pair => Plain(pair.Value)),
            IDictionary<string, object> toml => toml.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
            _ => throw new FormatException($"{where}: git must be a {name} table."),
        };
        foreach (var key in table.Keys)
            if (!Keys.Contains(key))
                throw new FormatException($"{key} under {name} in {where} isn't a {name} setting (they're {string.Join(", ", Keys)}).");
        bool Flag(string key, bool fallback) => !table.TryGetValue(key, out var value)
            ? fallback
            : value as bool? ?? throw new FormatException($"{key} under {name} in {where} should be true or false.");
        var repo = table.TryGetValue("repo", out var r)
            ? r is string { Length: > 0 } text ? text : throw new FormatException($"repo under {name} in {where} should be text, and not empty.")
            : throw new FormatException($"{name} in {where} needs repo, the git repository's folder, as \"..\".");
        var trailers = new List<string>();
        if (table.TryGetValue("trailers", out var t))
        {
            var bad = new FormatException($"trailers under {name} in {where} should be a list of lines such as \"Co-Authored-By: Name <name@example.com>\".");
            if (t is string or not IEnumerable)
                throw bad;
            foreach (var item in (IEnumerable)t)
                trailers.Add(item is string line && Trailer().IsMatch(line) ? line : throw bad);
        }
        return new GitSettings(path(repo), Flag("commit", true), Flag("push", false)) { Trailers = trailers };
    }

    /// <summary>A git trailer: a key of letters, digits and hyphens, ": ", and a value, on one line.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9-]*: [^\r\n]*\S[^\r\n]*\z")]
    private static partial Regex Trailer();

    /// <summary>A JSON value as TOML would give it: text, true/false, a number, a list or a table.</summary>
    private static object? Plain(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => array.Select(Plain).ToList(),
        JsonObject obj => obj.ToDictionary(pair => pair.Key, pair => Plain(pair.Value)),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetValue<double>(),
            _ => null,
        },
        _ => null,
    };
}
