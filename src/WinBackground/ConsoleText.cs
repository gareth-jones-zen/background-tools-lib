using System.Runtime.InteropServices;
using System.Text;

namespace WinBackground;

/// <summary>stdout and stderr as Python writes them on Windows: to a console, as Unicode (WriteConsoleW), so an
/// emoji shows rather than "?"; redirected to a file or pipe, as UTF-8 with no byte order mark, which is what a
/// caller that ran the Python tool decodes. .NET's own Console.Out uses the console's code page for both. For a
/// tool's console exe: <c>Console.SetOut(ConsoleText.Out); Console.SetError(ConsoleText.Error);</c> (first written
/// for teams-channel-notes' port).</summary>
public static partial class ConsoleText
{
    private const int StdOutput = -11, StdError = -12;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteConsoleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteConsole(nint handle, char* buffer, uint count, out uint written, nint reserved);

    public static TextWriter Out => For(StdOutput, Console.OpenStandardOutput);
    public static TextWriter Error => For(StdError, Console.OpenStandardError);

    private static TextWriter For(int which, Func<Stream> open)
    {
        var handle = GetStdHandle(which);
        if (handle != 0 && handle != -1 && GetConsoleMode(handle, out _))
            return new ConsoleWriter(handle);
        return new StreamWriter(open(), new UTF8Encoding(false)) { AutoFlush = true };
    }

    private sealed class ConsoleWriter(nint handle) : TextWriter
    {
        public override Encoding Encoding => Encoding.Unicode;

        public override void Write(char value) => Write(value.ToString());

        public override unsafe void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            fixed (char* p = value)
                WriteConsole(handle, p, (uint)value.Length, out _, 0);
        }
    }
}
