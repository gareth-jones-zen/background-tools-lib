using System.Security.Cryptography;
using System.Text;
using PyCompat;

namespace EdgeDevTools;

/// <summary>
/// An Edge profile folder: its normal spelling, the key and lock names derived from it, and whether an Edge has it
/// open.
/// <para><b>The key must match the Python tools exactly</b>, so a Python and a .NET tool sharing a profile take
/// turns during the changeover. Python's people-first-client names its lock
/// <c>people-first-edge-{key(Path(os.path.abspath(profile)))}</c>, where win-background's key is the first 16 hex
/// characters of the SHA-256 of <c>str(path).lower()</c> in UTF-8. So <see cref="Normalise"/> gives what
/// <c>str(pathlib.Path(os.path.abspath(p)))</c> gives on Windows, and <see cref="Key"/> lower-cases as Python's
/// <c>str.lower()</c> does (PyCompat's <c>PyStr.Lower</c>), which differs from .NET's invariant lower case in
/// two places: U+0130 (capital I with dot) becomes two characters, and a capital sigma at the end of a word
/// becomes a final sigma.</para>
/// <para>It lives here rather than in WinBackground because it's about Edge's profile folder (the in-use check
/// reads Edge's own lock file), and EdgeDevTools is already Windows-only and small, so a future Playwright-based
/// project can reference it cheaply.</para>
/// </summary>
public static class EdgeProfile
{
    /// <summary><c>str(pathlib.Path(os.path.abspath(path)))</c> on Windows: a full path with backslashes, "." and
    /// ".." resolved, doubled separators and a trailing separator removed (except at a root, and a UNC share's
    /// root keeps its trailing backslash, as pathlib writes it), and the last part's trailing dots and spaces
    /// dropped (Windows does that). Relative paths are from the current folder. Case and 8.3 names are kept as
    /// written, and links aren't followed, as in Python.</summary>
    public static string Normalise(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path.Length == 0 ? "." : path);
        var root = Path.GetPathRoot(full) ?? "";
        var trimmed = full.TrimEnd('\\', '/');
        if (trimmed.Length <= root.TrimEnd('\\', '/').Length)
            return root.EndsWith('\\') ? root : root + "\\";
        return trimmed;
    }

    /// <summary>win-background's key for the profile: 16 hex characters, the same in Python and .NET.</summary>
    public static string Key(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PyStr.Lower(Normalise(path)))))[..16];

    /// <summary>A lock name for the profile: <c>{prefix}-{Key(path)}</c>. People First's is
    /// "people-first-edge".</summary>
    public static string LockName(string prefix, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return $"{prefix}-{Key(path)}";
    }

    /// <summary>Whether an Edge has the profile open now. Edge holds <c>lockfile</c> in the profile open for
    /// writing, shared for reading only, and deletes it when it closes; so it's open when the file is there and
    /// can't be opened for writing. Nothing is written: a stale file left by a crash is opened and closed again
    /// unchanged.</summary>
    public static bool IsOpen(string path)
    {
        var lockFile = Path.Combine(path, "lockfile");
        if (!File.Exists(lockFile))
            return false;
        try
        {
            using (new FileStream(lockFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                return false;
        }
        catch (FileNotFoundException)
        {
            return false;   // closed between the two checks
        }
        catch (IOException)
        {
            return true;    // a sharing violation: Edge has it
        }
        catch (UnauthorizedAccessException)
        {
            return true;    // being deleted as Edge closes, or not ours to open: either way, don't start another
        }
    }
}
