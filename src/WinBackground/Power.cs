using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinBackground;

/// <summary>A named "keep the system awake" power request (PowerCreateRequest, PowerRequestSystemRequired).
///
/// The reason is what `powercfg /requests` shows, so anyone can see why the machine stayed awake. Windows
/// drops the request when the process exits, so a crash can't leave the machine awake. It keeps the system
/// out of sleep only: the display still turns off and the screensaver still starts.</summary>
public sealed class SystemRequired : IDisposable
{
    private const int Kind = 1;   // PowerRequestSystemRequired
    private nint handle;
    private nint reasonText;

    public SystemRequired(string reason)
    {
        Reason = reason;
        reasonText = Marshal.StringToHGlobalUni(reason);
        var context = new Native.REASON_CONTEXT { Version = 0, Flags = 0x1, SimpleReasonString = reasonText };   // POWER_REQUEST_CONTEXT_VERSION, _SIMPLE_STRING
        handle = Native.PowerCreateRequest(ref context);
        if (handle == 0 || handle == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            Marshal.FreeHGlobal(reasonText);
            reasonText = 0;
            handle = 0;
            throw new Win32Exception(error);
        }
    }

    public string Reason { get; }
    public bool Held { get; private set; }

    /// <summary>Set (true) or clear (false) the request. Does nothing if it's already that way.</summary>
    public void Hold(bool on = true)
    {
        if (on == Held)
            return;
        var done = on ? Native.PowerSetRequest(handle, Kind) : Native.PowerClearRequest(handle, Kind);
        if (!done)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        Held = on;
    }

    public void Close()
    {
        if (handle != 0)
        {
            if (Held)
            {
                Native.PowerClearRequest(handle, Kind);
                Held = false;
            }
            Native.CloseHandle(handle);
            handle = 0;
        }
        if (reasonText != 0)
        {
            Marshal.FreeHGlobal(reasonText);
            reasonText = 0;
        }
    }

    public void Dispose() => Close();
}

/// <summary>The screensaver as it applies now.</summary>
public sealed record Screensaver(bool Active, bool Secure, long Timeout, string? Program, bool Policy)
{
    /// <summary>"On resume, display logon screen", active, chosen and timed: it locks.</summary>
    public bool Locks => Active && Secure && !string.IsNullOrEmpty(Program) && Timeout > 0;
}

/// <summary>Everything that decides when an idle Windows session locks, for one power source.
/// Seconds throughout: SleepAfter 0 is never and null unreadable; SignInAfterSleep 0 is every time and null never.</summary>
public sealed record LockSettings(
    bool OnMains,
    long? SleepAfter,
    string SleepSource,
    long? DisplayOffAfter,
    long? SignInAfterSleep,
    string SignInSource,
    Screensaver? Screensaver,
    long? InactivityLimit,
    bool? ModernStandby = null);

/// <summary>Keeping Windows awake on purpose, and reading when Windows would sleep or lock. Nothing here needs
/// admin rights, and nothing here changes a setting.</summary>
public static class Power
{
    public const uint EsSystemRequired = 0x1, EsDisplayRequired = 0x2, EsAwayModeRequired = 0x40;

    // Power setting GUIDs (powercfg /aliases).
    public const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    public const string StandbyIdle = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";     // sleep after
    public const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
    public const string VideoIdle = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";       // turn off the display after
    public const string SubNone = "fea3413e-7e05-4911-9a71-700331f1c294";
    public const string ConsoleLock = "0e796bdb-100d-47d6-a2d5-f7d2daa51f51";     // require a password on wakeup
    private const string PowerPolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings";
    private const string Desktop = @"Control Panel\Desktop";
    private const string DesktopPolicy = @"Software\Policies\Microsoft\Windows\Control Panel\Desktop";
    private const string SystemPolicy = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const long Never = 0xFFFFFFFF;   // DelayLockInterval "Never"

    /// <summary>True on mains power, false on battery, null if Windows can't tell.</summary>
    public static bool? OnMains()
    {
        if (!Native.GetSystemPowerStatus(out var status) || status.ACLineStatus == 255)
            return null;
        return status.ACLineStatus == 1;
    }

    /// <summary>Seconds since the last keyboard or mouse input in this session. Programs being busy don't
    /// count, which is why a machine running a long job looks idle to Windows.</summary>
    public static double IdleSeconds()
    {
        var info = new Native.LASTINPUTINFO { cbSize = 8 };
        if (!Native.GetLastInputInfo(ref info))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return unchecked(Native.GetTickCount() - info.dwTime) / 1000.0;
    }

    /// <summary>Lock the session, as Windows+L does. False if Windows refused.</summary>
    public static bool LockWorkStation() => Native.LockWorkStation();

    /// <summary>What all processes together are asking of the power manager: ES_SYSTEM_REQUIRED (0x1) and
    /// ES_DISPLAY_REQUIRED (0x2), from power requests or SetThreadExecutionState. Needs no admin rights,
    /// unlike `powercfg /requests`. 0 if it can't be read.</summary>
    public static uint ExecutionState()
    {
        if (Native.CallNtPowerInformation(16, 0, 0, out var value, sizeof(uint)) != 0)   // SystemExecutionState
            return 0;
        return value;
    }

    /// <summary>True on a Modern Standby (S0 low power idle) machine, where "Require sign-in" is
    /// DelayLockInterval rather than the "require a password on wakeup" power setting. Null if unknown.</summary>
    public static unsafe bool? ModernStandby()
    {
        var caps = stackalloc byte[Native.PowerCapabilitiesSize + 52];   // room to spare
        if (Native.GetPwrCapabilities(caps) == 0)
            return null;
        return caps[Native.AoAcOffset] != 0;
    }

    // ---- Reading settings ---------------------------------------------------------------------------

    /// <summary>A power setting for one power source: a Group Policy override if there is one, else the active
    /// plan (PowerReadACValueIndex / PowerReadDCValueIndex), else `powercfg /query`.</summary>
    private static (long? Value, string Source) SchemeValue(string subgroup, string setting, bool ac)
    {
        var side = ac ? "AC" : "DC";
        var policy = Registry.Read(Registry.Hive.LocalMachine, $@"{PowerPolicyKey}\{setting}", $"{side}SettingIndex");
        if (policy is not null)
            return (Registry.ToInt(policy), "Group Policy");
        if (Native.PowerGetActiveScheme(0, out var scheme) == 0)
        {
            try
            {
                var sub = Guid.Parse(subgroup);
                var set = Guid.Parse(setting);
                var read = ac
                    ? Native.PowerReadACValueIndex(0, scheme, in sub, in set, out var value)
                    : Native.PowerReadDCValueIndex(0, scheme, in sub, in set, out value);
                if (read == 0)
                    return (value, "power plan");
            }
            finally
            {
                Native.LocalFree(scheme);
            }
        }
        return (Powercfg(subgroup, setting, ac), "powercfg");
    }

    /// <summary>powercfg.exe by its full path (see Shell.SystemProgram).</summary>
    public static string PowercfgExe => Shell.SystemProgram("powercfg.exe");

    /// <summary>The fallback: `powercfg /query`. Its labels are translated, so take the last two hex numbers (AC, DC).</summary>
    private static long? Powercfg(string subgroup, string setting, bool ac)
    {
        try
        {
            var done = Shell.Run(PowercfgExe, ["/query", "SCHEME_CURRENT", subgroup, setting], TimeSpan.FromSeconds(15));
            return ParsePowercfg(done.StandardOutput, ac);
        }
        catch (Exception error) when (error is TimeoutException or Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public static long? ParsePowercfg(string text, bool ac)
    {
        // The Python's regex, `:\s*0x([0-9a-fA-F]+)\s*$` per line, by hand: Regex would double the program's size.
        var found = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            var x = line.LastIndexOf('x');
            var hex = x >= 0 ? line[(x + 1)..] : "";
            if (x < 1 || hex.Length == 0 || !hex.All(char.IsAsciiHexDigit) || line[x - 1] != '0')
                continue;
            if (line[..(x - 1)].TrimEnd().EndsWith(':'))
                found.Add(hex);
        }
        if (found.Count < 2)
            return null;
        return Convert.ToInt64(found[ac ? found.Count - 2 : found.Count - 1], 16);
    }

    private static bool Side(bool? ac) => ac ?? OnMains() is not false;

    /// <summary>Seconds of idle before Windows sleeps, for mains (true), battery (false) or whichever it's on
    /// now. 0 means never.</summary>
    public static (long? Seconds, string Source) SleepTimeout(bool? ac = null) => SchemeValue(SubSleep, StandbyIdle, Side(ac));

    /// <summary>Seconds of idle before the display turns off. 0 means never.</summary>
    public static (long? Seconds, string Source) DisplayTimeout(bool? ac = null) => SchemeValue(SubVideo, VideoIdle, Side(ac));

    /// <summary>Whether waking from sleep needs a sign-in: seconds asleep before it does (0 every time, null
    /// never), and where that came from.
    ///
    /// Settings > Accounts > Sign-in options > "If you've been away, when should Windows require you to sign in
    /// again?" On Modern Standby that's DelayLockInterval in HKCU\Control Panel\Desktop (0 every time,
    /// 60/180/300/900 seconds, 0xFFFFFFFF never; missing is taken as every time). On S3 machines it's the
    /// "require a password on wakeup" power setting (CONSOLELOCK: 1 yes, 0 no). A Group Policy value for that
    /// setting wins on either.</summary>
    public static (long? Seconds, string Source) SignInOnWake(bool? ac = null)
    {
        var side = Side(ac);
        var policy = Registry.Read(Registry.Hive.LocalMachine, $@"{PowerPolicyKey}\{ConsoleLock}", $"{(side ? "AC" : "DC")}SettingIndex");
        if (policy is not null)
            return (Registry.ToInt(policy) != 0 ? 0 : null, "Group Policy (require a password on wakeup)");
        if (ModernStandby() is not false)
        {
            var delay = Registry.Read(Registry.Hive.CurrentUser, Desktop, "DelayLockInterval");
            if (delay is null)
                return (0, "DelayLockInterval not set (every time)");
            var seconds = Registry.ToInt(delay) & 0xFFFFFFFF;
            return (seconds == Never ? null : seconds, "DelayLockInterval");
        }
        var (value, source) = SchemeValue(SubNone, ConsoleLock, side);
        return (value == 0 ? null : 0, $"require a password on wakeup ({source})");
    }

    /// <summary>The screensaver as it applies now. SystemParametersInfo gives the effective active, timeout and
    /// secure values, Group Policy included; the program comes from the policy key, then the user's own.</summary>
    public static Screensaver Screensaver()
    {
        Native.SystemParametersInfoW(0x10, 0, out var active, 0);    // SPI_GETSCREENSAVEACTIVE
        Native.SystemParametersInfoW(0x0E, 0, out var timeout, 0);   // SPI_GETSCREENSAVETIMEOUT
        Native.SystemParametersInfoW(0x76, 0, out var secure, 0);    // SPI_GETSCREENSAVESECURE
        string[] names = ["ScreenSaveActive", "ScreenSaverIsSecure", "ScreenSaveTimeOut", "SCRNSAVE.EXE"];
        var policy = names.Select(name => Registry.Read(Registry.Hive.CurrentUser, DesktopPolicy, name)).ToArray();
        var program = Registry.Text(policy[3]);
        if (string.IsNullOrEmpty(program))
            program = Registry.Text(Registry.Read(Registry.Hive.CurrentUser, Desktop, "SCRNSAVE.EXE"));
        return new Screensaver(active != 0, secure != 0, timeout, string.IsNullOrEmpty(program) ? null : program,
            policy.Any(value => value is not null));
    }

    /// <summary>"Interactive logon: Machine inactivity limit" (InactivityTimeoutSecs): Windows locks after this
    /// many seconds of idle, whatever else is set. Null if not set.</summary>
    public static long? InactivityLimit()
    {
        var value = Registry.Read(Registry.Hive.LocalMachine, SystemPolicy, "InactivityTimeoutSecs");
        if (value is null)
            return null;
        var text = (value is string s ? s : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))?.Trim();
        return long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number)
               && number != 0 ? number : null;
    }

    /// <summary>Read every setting that decides when an idle session locks. Read-only; changes nothing.</summary>
    public static LockSettings LockSettings(bool? ac = null)
    {
        var side = Side(ac);
        var (sleep, sleepSource) = SleepTimeout(side);
        var (display, _) = DisplayTimeout(side);
        var (signIn, signInSource) = SignInOnWake(side);
        Screensaver? saver;
        try
        {
            saver = Screensaver();
        }
        catch (Win32Exception)
        {
            saver = null;
        }
        return new LockSettings(side, sleep, sleepSource, display, signIn, signInSource, saver, InactivityLimit(), ModernStandby());
    }
}
