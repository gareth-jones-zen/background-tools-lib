using System.Diagnostics;
using System.Text;

namespace WinBackground;

/// <summary>What a child process returned.</summary>
public sealed record Completed(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Starting child processes without a console window flashing up.</summary>
public static class Shell
{
    /// <summary>Run a program with no window (CREATE_NO_WINDOW), capturing its output. Throws TimeoutException
    /// (after killing it) if it runs longer than the timeout.</summary>
    public static Completed Run(string program, IEnumerable<string> args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {program}.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{program} didn't finish within {timeout.TotalSeconds:0} seconds.");
        }
        process.WaitForExit();
        return new Completed(process.ExitCode, Normalise(output.Result), Normalise(error.Result));
    }

    /// <summary>A PowerShell script, as `powershell -NoProfile -NonInteractive -Command script`.</summary>
    public static Completed PowerShell(string script, double timeoutSeconds = 120) =>
        Run("powershell", ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(timeoutSeconds));

    /// <summary>A PowerShell single-quoted string.</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

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
