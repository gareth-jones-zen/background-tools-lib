using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RepoCommit;

/// <summary>What one git command returned: its exit code and its output, as text (UTF-8, line ends as git wrote them).</summary>
public sealed record GitOutput(int ExitCode, string Output, string Error);

/// <summary>Runs git as a child process, with no window, no prompts and nothing read from a terminal.</summary>
internal static class GitProcess
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>git on the PATH, by its full path. Only the PATH is searched: Windows would otherwise look in the running
    /// program's own folder and the current folder first, and a tool is often run from wherever its zip was unzipped.
    /// Null if there's none.</summary>
    public static string? Find(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var folder in (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), name);
                if (Path.IsPathRooted(candidate) && File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                // a PATH entry with characters a path can't have: skip it
            }
        }
        return null;
    }

    /// <summary>Run git in a folder. Every pathspec is literal (--literal-pathspecs), so a file named with * or : is just
    /// that file. stdin, if given, is written as UTF-8 bytes exactly (a list of NUL-separated paths). A command that runs
    /// past the timeout is killed and throws TimeoutException; one that can't be started throws Win32Exception.</summary>
    public static GitOutput Run(string program, string folder, IReadOnlyList<string> args, string? stdin, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = folder,
        };
        info.ArgumentList.Add("--literal-pathspecs");
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        // Never wait for a person: no terminal prompt for credentials, and no optional lock on the index (git status
        // otherwise takes index.lock to refresh it, and could make another program's commit fail).
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = Process.Start(info) ?? throw new Win32Exception($"Couldn't start {program}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (stdin is not null)
            {
                var bytes = Utf8.GetBytes(stdin);
                process.StandardInput.BaseStream.Write(bytes);
                process.StandardInput.BaseStream.Flush();
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // git exited without reading it: its exit code says why
        }
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw new TimeoutException($"git {args.FirstOrDefault()} didn't finish within {timeout.TotalSeconds:0} seconds.");
        }
        process.WaitForExit();
        return new GitOutput(process.ExitCode, output.Result, error.Result);
    }
}
