using System.Globalization;
using Microsoft.Win32;

namespace WinBackground;

/// <summary>Reading registry values the way the Python version did (winreg.QueryValueEx): DWORDs unsigned,
/// strings unexpanded, and null for anything missing or unreadable.</summary>
internal static class Registry
{
    internal enum Hive { CurrentUser, LocalMachine }

    internal static object? Read(Hive hive, string path, string name)
    {
        try
        {
            var root = hive == Hive.CurrentUser ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine;
            using var key = root.OpenSubKey(path);
            if (key is null)
                return null;
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
                return null;
            return key.GetValueKind(name) switch
            {
                RegistryValueKind.DWord => (long)unchecked((uint)(int)value),
                RegistryValueKind.QWord => unchecked((long)value),
                _ => value,
            };
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Python's int(value): a number as it is, text parsed (or FormatException), anything else refused.</summary>
    internal static long ToInt(object value) => value switch
    {
        long number => number,
        string text => long.Parse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"A registry value of type {value.GetType().Name} isn't a number."),
    };

    internal static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
