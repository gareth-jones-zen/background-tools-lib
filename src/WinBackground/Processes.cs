using System.Runtime.InteropServices;

namespace WinBackground;

/// <summary>Other processes: whether one is running, and which started this one.</summary>
public static class Processes
{
    /// <summary>Whether a process is running. When in doubt (access denied), true: a lease then simply runs to
    /// its expiry, which is the safe way to be wrong.</summary>
    public static bool Alive(long pid)
    {
        var handle = Native.OpenProcess(0x1000, false, unchecked((uint)pid));   // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == 0)
            return Marshal.GetLastPInvokeError() != 87;                       // ERROR_INVALID_PARAMETER: no such process
        try
        {
            if (!Native.GetExitCodeProcess(handle, out var code))
                return true;
            return code == 259;                                                // STILL_ACTIVE
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>The process that started this one (os.getppid()), or 0 if Windows won't say.</summary>
    public static unsafe long ParentId()
    {
        // PROCESS_BASIC_INFORMATION: ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId,
        // InheritedFromUniqueProcessId; six pointer-sized fields on 64-bit Windows.
        var info = stackalloc nint[6];
        if (Native.NtQueryInformationProcess(Native.GetCurrentProcess(), 0, info, sizeof(nint) * 6, out _) != 0)
            return 0;
        return info[5];
    }
}
