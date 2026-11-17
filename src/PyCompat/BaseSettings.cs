using Tomlyn;
using Tomlyn.Model;

namespace PyCompat;

/// <summary>A tool's settings, read with the base file it names: the merged table, and the variables to expand its
/// paths with (the base's [paths], then the environment).</summary>
public sealed record SettingsWithBase(TomlTable Raw, Func<string, string?> Env, string? BasePath);

/// <summary>Settings shared by several tools, kept once in a base file (17 Nov 2026).
///
/// A tool's settings name it at the top: <c>base = "base-config.toml"</c> (relative to the settings file). Then:
/// <list type="bullet">
/// <item><b>The base's <c>[paths]</c> are variables</b> for the tool's paths: <c>EA_LOCAL = "%LOCALAPPDATA%/claude-ea"</c>
/// lets the tool say <c>log_file = "%EA_LOCAL%/x.log"</c>. They're looked up before the environment, and may use the
/// environment and each other.</item>
/// <item><b>Each of the base's other settings that the tool knows</b> (its <c>known</c> keys) <b>and hasn't set itself</b>
/// is added. A table both set (<c>[git]</c>, <c>[review]</c>) is merged key by key, the tool's own keys winning. Base
/// settings the tool doesn't know are left out, so one base serves tools that use different parts of it.</item>
/// </list>
/// The base is read as strictly as any settings file (a key given twice is an error), and can't name a base of its
/// own. The tool's file is still checked strictly by the tool: the <c>base</c> key itself is taken out first.</summary>
public static class BaseSettings
{
    public const string Key = "base";
    public const string PathsKey = "paths";

    /// <summary>Read settings text (from <paramref name="source"/>) with its base, if it names one. Throws
    /// FormatException for a base that's missing or can't be read.</summary>
    public static SettingsWithBase Read(string text, string source, Func<string, string?> env, IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(env);
        var raw = PyToml.Read(text, source);
        if (!raw.TryGetValue(Key, out var named))
            return new SettingsWithBase(raw, env, null);
        raw.Remove(Key);
        if (named is not string name || name.Trim().Length == 0)
            throw new FormatException($"{source}: base should be the path of a base settings file, such as \"base-config.toml\".");
        var folder = Path.GetDirectoryName(PyPath.Resolve(source)) ?? ".";
        var expanded = PyPath.ExpandVars(PyPath.ExpandUser(name.Trim(), env), env);
        var path = PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(folder, expanded);
        if (!File.Exists(path))
            throw new FormatException($"{source}: its base, {path}, doesn't exist.");
        TomlTable baseTable;
        try
        {
            var baseText = PyStr.ReadText(path);
            if (baseText.StartsWith('﻿'))
                baseText = baseText[1..];
            baseTable = PyToml.Read(baseText, path);
        }
        catch (Exception error) when (error is TomlException or IOException or UnauthorizedAccessException)
        {
            throw new FormatException($"{path}: {error.Message.TrimEnd()}", error);
        }
        if (baseTable.ContainsKey(Key))
            throw new FormatException($"{path}: a base can't name a base of its own.");

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseTable.TryGetValue(PathsKey, out var p))
        {
            if (p is not TomlTable table)
                throw new FormatException($"{path}: [paths] should be a table of names and paths, such as ROOT = \"%LOCALAPPDATA%/tools\".");
            foreach (var (key, value) in table)
                paths[key] = value as string ?? throw new FormatException($"{path}: paths.{key} should be a string.");
        }

        var knownKeys = known.ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in baseTable)
        {
            if (key == PathsKey || !knownKeys.Contains(key))
                continue;
            if (!raw.TryGetValue(key, out var own))
                raw[key] = Copy(value);
            else if (own is TomlTable mine && value is TomlTable theirs)
                Merge(mine, theirs);
        }
        return new SettingsWithBase(raw, Variables(paths, env), path);
    }

    /// <summary>name → the base's path for it (expanded, with the environment and the other paths), else the
    /// environment's. A path that refers to itself, round a loop, is left unexpanded rather than recursing.</summary>
    private static Func<string, string?> Variables(Dictionary<string, string> paths, Func<string, string?> env)
    {
        if (paths.Count == 0)
            return env;
        var expanding = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Lookup(string name)
        {
            if (!paths.TryGetValue(name, out var value))
                return env(name);
            if (!expanding.Add(name))
                return null;
            try
            {
                return PyPath.ExpandVars(PyPath.ExpandUser(value, env), Lookup);
            }
            finally
            {
                expanding.Remove(name);
            }
        }
        return Lookup;
    }

    /// <summary>Adds the base table's keys the tool's table doesn't have; tables in both merge the same way.</summary>
    private static void Merge(TomlTable mine, TomlTable theirs)
    {
        foreach (var (key, value) in theirs)
        {
            if (!mine.TryGetValue(key, out var own))
                mine[key] = Copy(value);
            else if (own is TomlTable a && value is TomlTable b)
                Merge(a, b);
        }
    }

    /// <summary>A copy, so the tool can't change the base's table through its own.</summary>
    private static object Copy(object value) => value switch
    {
        TomlTable table => table.Aggregate(new TomlTable(), (copy, pair) => { copy[pair.Key] = Copy(pair.Value); return copy; }),
        TomlArray array => array.Aggregate(new TomlArray(), (copy, item) => { copy.Add(item is null ? null : Copy(item)); return copy; }),
        TomlTableArray tables => tables.Aggregate(new TomlTableArray(), (copy, t) => { copy.Add((TomlTable)Copy(t)); return copy; }),
        _ => value,
    };
}
