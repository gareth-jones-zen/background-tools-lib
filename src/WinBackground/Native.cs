using System.Runtime.InteropServices;

namespace WinBackground;

/// <summary>The Windows calls, as source-generated imports (no reflection, so Native AOT is happy).</summary>
internal static partial class Native
{
    // Version, Flags, then a union whose largest member (the "Detailed" form) is 24 bytes on x64:
    // HMODULE, ULONG, ULONG, LPWSTR*. SimpleReasonString shares the union's first 8 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        public nint Rest1;
        public nint Rest2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    internal const int AoAcOffset = 20;              // SYSTEM_POWER_CAPABILITIES.AoAc: the 21st BOOLEAN
    internal const int PowerCapabilitiesSize = 76;   // 24 BOOLEANs, spare3[6], 2 BOOLEANs, 3 scales, 5 states

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint PowerCreateRequest(ref REASON_CONTEXT context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PowerSetRequest(nint handle, int kind);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PowerClearRequest(nint handle, int kind);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetTickCount();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint handle, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenMutexW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockWorkStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoW(uint action, uint param, out uint value, uint winIni);

    [LibraryImport("powrprof.dll")]
    internal static partial uint CallNtPowerInformation(int level, nint input, uint inputLength, out uint output, uint outputLength);

    [LibraryImport("powrprof.dll")]
    internal static unsafe partial byte GetPwrCapabilities(byte* capabilities);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(nint rootPowerKey, out nint activePolicy);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadACValueIndex(nint rootPowerKey, nint scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadDCValueIndex(nint rootPowerKey, nint scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtQueryInformationProcess(nint process, int infoClass, nint* info, int length, out int returned);
}
