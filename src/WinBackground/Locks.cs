using System.Security.Cryptography;
using System.Text;

namespace WinBackground;

/// <summary>A named lock between processes: a mutex in the session's Local namespace. Windows releases a mutex
/// when the process holding it dies, so a crash can never leave a lock stuck, which a lock file can.</summary>
public sealed class NamedLock : IDisposable
{
    private const uint WaitObject0 = 0, WaitAbandoned = 0x80;
    private nint handle;

    public NamedLock(string name) => Name = name;

    public string Name { get; }

    /// <summary>Wait up to timeout seconds; 0 means try once.</summary>
    public bool Acquire(double timeoutSeconds = 0)
    {
        handle = Native.CreateMutexW(0, false, $@"Local\{Name}");
        if (handle == 0)
            return false;
        var result = Native.WaitForSingleObject(handle, (uint)(timeoutSeconds * 1000));
        if (result is WaitObject0 or WaitAbandoned)   // abandoned: the last holder died, so it's ours
            return true;
        Native.CloseHandle(handle);
        handle = 0;
        return false;
    }

    public void Release()
    {
        if (handle != 0)
        {
            Native.ReleaseMutex(handle);
            Native.CloseHandle(handle);
            handle = 0;
        }
    }

    public void Dispose() => Release();
}

/// <summary>One watcher at a time. Two names that are equal hold the same lock across every tool, so name
/// locks after the thing they protect: Key() turns a path into a short stable name.</summary>
public static class Locks
{
    /// <summary>16 hex characters from a path or other text, ignoring case (Windows paths).</summary>
    public static string Key(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..16];

    /// <summary>The lock for a long-running process, such as a watcher: held until it exits or releases it.
    /// Null if another process (or thread) already holds it.</summary>
    public static NamedLock? SingleInstance(string name)
    {
        var lockObject = new NamedLock(name);
        return lockObject.Acquire(0) ? lockObject : null;
    }
}
