using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WinBackground;

/// <summary>What a child process returned.</summary>
public sealed record Completed(int ExitCode, string StandardOutput, string StandardError);

/// <summary>A child process ran past its timeout and was killed, with everything it started. The message is the
/// library's own; <see cref="PythonMessage"/> is what Python's subprocess.TimeoutExpired said, for a port whose log
/// has to match.</summary>
public sealed class ShellTimeoutException(string program, IReadOnlyList<string> args, TimeSpan timeout)
    : TimeoutException($"{program} didn't finish within {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.")
{
    /// <summary>The program and its arguments, as Python's cmd.</summary>
    public IReadOnlyList<string> Command { get; } = [program, .. args];

    public TimeSpan Timeout { get; } = timeout;

    /// <summary>str(subprocess.TimeoutExpired): "Command '['hook.exe', '-x']' timed out after 60 seconds".</summary>
    public string PythonMessage =>
        $"Command '{Shell.PythonListRepr(Command)}' timed out after {Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds";
}

/// <summary>Starting child processes without a console window flashing up.</summary>
public static class Shell
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Run a program with no window (CREATE_NO_WINDOW), capturing its output. Throws
    /// <see cref="ShellTimeoutException"/> (a TimeoutException, after killing it and everything it started) if it
    /// runs longer than the timeout, and Win32Exception if it can't be started (<see cref="WinErrorText"/> gives
    /// Python's wording).
    /// <para>By default its stdin is closed at once, its output is read in the console's code page, and "\r\n"
    /// becomes "\n". The optional parameters make it Python's
    /// <c>subprocess.run(args, input=stdin, cwd=workingDirectory, capture_output=True, text=True,
    /// encoding="utf-8", errors="replace")</c>:</para>
    /// <list type="bullet">
    /// <item>stdin: text written to the program (in text mode, so each "\n" as "\r\n"), then closed. A program that
    /// exits without reading it is no error, as in Python.</item>
    /// <item>workingDirectory: the folder it runs in (default: this process's).</item>
    /// <item>utf8: stdin, stdout and stderr as UTF-8 with no byte order mark, bad bytes read as U+FFFD, and output
    /// read with universal newlines ("\r\n" and a lone "\r" both become "\n").</item>
    /// </list></summary>
    public static Completed Run(string program, IEnumerable<string> args, TimeSpan timeout, string? stdin = null,
        string? workingDirectory = null, bool utf8 = false)
    {
        var argList = args.ToList();
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        if (utf8)
        {
            info.StandardInputEncoding = Utf8;
            info.StandardOutputEncoding = Utf8;
            info.StandardErrorEncoding = Utf8;
        }
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        foreach (var arg in argList)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {program}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (stdin is not null)
                process.StandardInput.Write(stdin.Replace("\n", "\r\n"));   // text mode, as Python wrote it
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // the program exited without reading its input: Python ignores a broken pipe here too
        }
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new ShellTimeoutException(program, argList, timeout);
        }
        process.WaitForExit();
        return utf8
            ? new Completed(process.ExitCode, Universal(output.Result), Universal(error.Result))
            : new Completed(process.ExitCode, Normalise(output.Result), Normalise(error.Result));
    }

    /// <summary>An OSError's text as Python shows it: "[WinError 2] The system cannot find the file specified".</summary>
    public static string WinErrorText(Win32Exception error)
    {
        var message = new Win32Exception(error.NativeErrorCode).Message.TrimEnd(' ', '\t', '\r', '\n', '.');
        return $"[WinError {error.NativeErrorCode}] {message}";
    }

    /// <summary>repr() of a list of strings, as Python writes it: ['a', "it's"].</summary>
    internal static string PythonListRepr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(PythonRepr)) + "]";

    private static string PythonRepr(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var result = new StringBuilder().Append(quote);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (c == quote)
                        result.Append('\\').Append(c);
                    else if (c < 0x20 || c == 0x7f)
                        result.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else
                        result.Append(c);
                    break;
            }
        }
        return result.Append(quote).ToString();
    }

    private static string Universal(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>A program in System32, by its full path. Never start a system program by bare name: Windows
    /// looks in the running program's own folder and the current folder before System32, and an exe is often run
    /// from wherever its zip was unzipped (Downloads), so a stray powershell.exe there would run instead.</summary>
    public static string SystemProgram(params string[] parts) => Path.Combine([Environment.SystemDirectory, .. parts]);

    /// <summary>Windows PowerShell 5.1, which every Windows 10 and 11 machine has.</summary>
    public static string PowerShellExe => SystemProgram("WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>A PowerShell script, as `powershell -NoProfile -NonInteractive -Command script`.</summary>
    public static Completed PowerShell(string script, double timeoutSeconds = 120) =>
        Run(PowerShellExe, ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(timeoutSeconds));

    /// <summary>A PowerShell single-quoted string. PowerShell ends one at any of ' and the curly quotes U+2018 to
    /// U+201B, and a doubled one stands for itself, so each of those is doubled (as PowerShell's own
    /// CodeGeneration.EscapeSingleQuotedStringContent does). Doubling only ' let a path such as C:\Users\O’Brien
    /// end the string early: a syntax error at best, and a way to inject script at worst.</summary>
    public static string Quote(string text)
    {
        var result = new StringBuilder(text.Length + 2).Append('\'');
        foreach (var c in text)
        {
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
                result.Append(c);
            result.Append(c);
        }
        return result.Append('\'').ToString();
    }

    /// <summary>Arguments joined into one command line by the Microsoft C runtime's rules
    /// (Python's subprocess.list2cmdline), so the task shows exactly what the Python version wrote.</summary>
    public static string ListToCommandLine(IEnumerable<string> args)
    {
        var result = new StringBuilder();
        foreach (var arg in args)
        {
            var backslashes = 0;
            if (result.Length > 0)
                result.Append(' ');
            var needQuote = arg.Contains(' ') || arg.Contains('\t') || arg.Length == 0;
            if (needQuote)
                result.Append('"');
            foreach (var c in arg)
            {
                if (c == '\\')
                    backslashes++;
                else if (c == '"')
                {
                    result.Append('\\', backslashes * 2);
                    backslashes = 0;
                    result.Append("\\\"");
                }
                else
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                    result.Append(c);
                }
            }
            result.Append('\\', backslashes);
            if (needQuote)
            {
                result.Append('\\', backslashes);
                result.Append('"');
            }
        }
        return result.ToString();
    }

    // Python's text mode turns \r\n into \n; the parsers here expect that.
    private static string Normalise(string text) => text.Replace("\r\n", "\n");
}
