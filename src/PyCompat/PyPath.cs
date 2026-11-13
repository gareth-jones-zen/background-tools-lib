using System.Runtime.InteropServices;
using System.Text;

namespace PyCompat;

/// <summary>Paths handled as Python's pathlib and ntpath did on Windows: ~ and %VARIABLES% expanded the same
/// way, resolve() following links and correcting case, and the same text for a path (it goes into the hooks'
/// arguments and the watcher's lock name).</summary>
public static partial class PyPath
{
    /// <summary>The home folder, as Path.home() finds it.</summary>
    public static string Home(Func<string, string?> env)
    {
        var profile = env("USERPROFILE");
        if (!string.IsNullOrEmpty(profile))
            return Pure(profile);
        var homePath = env("HOMEPATH");
        if (!string.IsNullOrEmpty(homePath))
            return Pure((env("HOMEDRIVE") ?? "") + homePath);
        return Pure(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>pathlib's text for a Windows path: "/" becomes "\", repeated separators and "." parts go, and so
    /// does a trailing separator. ".." is kept, as pathlib keeps it.</summary>
    public static string Pure(string path)
    {
        var p = path.Replace('/', '\\');
        var prefix = "";
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            prefix = @"\\";
            p = p[2..];
        }
        else if (p.Length >= 2 && p[1] == ':')
        {
            prefix = p[..2];
            p = p[2..];
        }
        var rooted = p.StartsWith('\\');
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".");
        var result = prefix + (rooted ? "\\" : "") + string.Join('\\', parts);
        return result.Length == 0 ? "." : result;
    }

    /// <summary>pathlib's is_absolute() on Windows: a drive and a root ("C:\x", or "\\server\share\x").</summary>
    public static bool IsAbsolute(string path)
    {
        var p = path.Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal) || (p.Length >= 3 && p[1] == ':' && p[2] == '\\');
    }

    /// <summary>Path(x).resolve() on Windows (ntpath.realpath, not strict): made absolute against the current
    /// folder, ".." collapsed, then links followed and the case corrected to what's on disk, through
    /// GetFinalPathNameByHandle. A path that doesn't exist yet resolves its longest existing part and keeps the rest
    /// as given. The text matters: it goes into state-file keys, hook arguments and lock names, so a Python and a
    /// .NET process given the same path in a different case still agree. (Elsewhere than Windows: as
    /// <see cref="Absolute"/>.)</summary>
    public static string Resolve(string path)
    {
        var full = Pure(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows())
            return full;
        var tail = new List<string>();
        var current = full;
        while (true)
        {
            var final = FinalPath(current);
            if (final is not null)
            {
                tail.Reverse();
                return Pure(tail.Count == 0 ? final : Path.Combine([final, .. tail]));
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                return full;
            tail.Add(Path.GetFileName(current));
            current = parent;
        }
    }

    /// <summary>The path made absolute against the current folder with ".." collapsed, by its text alone: no links
    /// followed and the case as given. (This was Resolve until 13 Nov 2026.)</summary>
    public static string Absolute(string path) => Pure(Path.GetFullPath(path));

    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3, FileFlagBackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(nint file, char* buffer, uint size, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>_getfinalpathname without the \\?\ prefix, or null if the path can't be opened.</summary>
    private static unsafe string? FinalPath(string path)
    {
        var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle == -1 || handle == 0)
            return null;
        try
        {
            var buffer = new char[1024];
            while (true)
            {
                uint length;
                fixed (char* p = buffer)
                    length = GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
                if (length == 0)
                    return null;
                if (length < buffer.Length)
                {
                    var final = new string(buffer, 0, (int)length);
                    if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                        return @"\\" + final[8..];
                    return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
                }
                buffer = new char[length + 1];
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>(base / path).resolve(): a path with a root but no drive takes the base's drive, as pathlib does.</summary>
    public static string ResolveAgainst(string basePath, string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith('\\') && !p.StartsWith(@"\\", StringComparison.Ordinal))
            return Resolve(Path.GetPathRoot(Path.GetFullPath(basePath))!.TrimEnd('\\') + p);
        return Resolve(Path.Combine(basePath, path));
    }

    /// <summary>Whether two paths are the same, as pathlib's == compares Windows paths (ignoring case).</summary>
    public static bool Same(string a, string b) => string.Equals(Pure(a), Pure(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>ntpath.expanduser: "~" or "~user" at the start, from USERPROFILE (or HOMEDRIVE and HOMEPATH).</summary>
    public static string ExpandUser(string path, Func<string, string?> env)
    {
        if (!path.StartsWith('~'))
            return path;
        var i = 1;
        while (i < path.Length && path[i] is not ('/' or '\\'))
            i++;
        string userHome;
        var profile = env("USERPROFILE");
        if (!string.IsNullOrEmpty(profile))
            userHome = profile;
        else if (env("HOMEPATH") is { } homePath)
            userHome = (env("HOMEDRIVE") ?? "") + homePath;
        else
            return path;
        if (i != 1)
        {
            var targetUser = path[1..i];
            var currentUser = env("USERNAME");
            if (targetUser != currentUser)
            {
                if (currentUser != Path.GetFileName(userHome.TrimEnd('\\', '/')))
                    return path;
                userHome = Path.Combine(Path.GetDirectoryName(userHome.TrimEnd('\\', '/')) ?? "", targetUser);
            }
        }
        return userHome + path[i..];
    }

    /// <summary>ntpath.expandvars: %VAR%, $VAR and ${VAR}; unknown names left as they are, "%%" and "$$" for a
    /// literal one, and nothing expanded inside single quotes.</summary>
    public static string ExpandVars(string path, Func<string, string?> env)
    {
        if (!path.Contains('$') && !path.Contains('%'))
            return path;
        const string varChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";
        var res = new StringBuilder();
        var index = 0;
        var length = path.Length;
        while (index < length)
        {
            var c = path[index];
            if (c == '\'')
            {
                path = path[(index + 1)..];
                length = path.Length;
                var close = path.IndexOf('\'');
                if (close >= 0)
                {
                    res.Append(c).Append(path[..(close + 1)]);
                    index = close;
                }
                else
                {
                    res.Append(c).Append(path);
                    index = length - 1;
                }
            }
            else if (c == '%')
            {
                if (index + 1 < length && path[index + 1] == '%')
                {
                    res.Append(c);
                    index++;
                }
                else
                {
                    path = path[(index + 1)..];
                    length = path.Length;
                    var close = path.IndexOf('%');
                    if (close < 0)
                    {
                        res.Append('%').Append(path);
                        index = length - 1;
                    }
                    else
                    {
                        var name = path[..close];
                        res.Append(env(name) ?? "%" + name + "%");
                        index = close;
                    }
                }
            }
            else if (c == '$')
            {
                if (index + 1 < length && path[index + 1] == '$')
                {
                    res.Append(c);
                    index++;
                }
                else if (index + 1 < length && path[index + 1] == '{')
                {
                    path = path[(index + 2)..];
                    length = path.Length;
                    var close = path.IndexOf('}');
                    if (close < 0)
                    {
                        res.Append("${").Append(path);
                        index = length - 1;
                    }
                    else
                    {
                        var name = path[..close];
                        res.Append(env(name) ?? "${" + name + "}");
                        index = close;
                    }
                }
                else
                {
                    var name = new StringBuilder();
                    index++;
                    while (index < length && varChars.Contains(path[index]))
                    {
                        name.Append(path[index]);
                        index++;
                    }
                    res.Append(env(name.ToString()) is { } value && name.Length > 0 ? value : "$" + name);
                    if (index < length)
                        index--;
                }
            }
            else
                res.Append(c);
            index++;
        }
        return res.ToString();
    }
}
