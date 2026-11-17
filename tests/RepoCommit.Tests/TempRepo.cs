using System.Diagnostics;
using System.Text;

namespace RepoCommit.Tests;

/// <summary>A new git repository in the temp folder, with an identity of its own and one first commit, deleted at the end.</summary>
public sealed class TempRepo : IDisposable
{
    public TempRepo(string prefix = "repo-commit-")
    {
        Root = Directory.CreateTempSubdirectory(prefix).FullName;
        Path = System.IO.Path.Combine(Root, "repo");
        Directory.CreateDirectory(Path);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Write("README.md", "first\n");
        Git("add", "README.md");
        Git("commit", "-q", "-m", "First");
    }

    /// <summary>The folder holding the repository (and anything else a test makes, such as a bare remote).</summary>
    public string Root { get; }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', '\\'));

    public void Write(string relative, string text)
    {
        var file = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllText(file, text, new UTF8Encoding(false));
    }

    /// <summary>Commit these files as they are (set-up, not under test).</summary>
    public void Commit(string message, params string[] files)
    {
        Git(["add", "--", .. files]);
        Git(["commit", "-q", "-m", message, "--", .. files]);
    }

    public int Count => int.Parse(Git("rev-list", "--count", "HEAD").Trim(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The last commit's message, as git stored it.</summary>
    public string LastMessage => Git("log", "-1", "--format=%B").TrimEnd('\n');

    /// <summary>The last commit's files: "A\tpath", "M\tpath", "D\tpath", "R100\tfrom\tto".</summary>
    public List<string> LastFiles => [.. Git("show", "--name-status", "-M", "--format=", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>git status --porcelain, one line per entry.</summary>
    public List<string> Status => [.. Git("status", "--porcelain", "-uall").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    public string Git(params string[] args) => GitIn(Path, args);

    public static string GitIn(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = folder,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.quotepath=false");
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
        return output.Result.Replace("\r\n", "\n");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(file, FileAttributes.Normal);   // git's objects are read-only
            Directory.Delete(Root, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
