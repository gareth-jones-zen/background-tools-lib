using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PeopleFirstClient.Tests;

/// <summary>
/// The lock against Python's people-first-client itself: names computed by running Python's <c>lock_name</c>, and
/// the mutex held by Python and .NET in turn. Needs Python and checkouts of people-first-client and
/// win-background (read only: they're imported from their src folders, nothing is installed or written there).
/// Skipped where they're missing, as on CI; EdgeProfileTests holds the same values, computed by Python, for CI.
/// <para>Where: PEOPLE_FIRST_PYTHON (else C:\Python314\python.exe), and PEOPLE_FIRST_CLIENT_SRC and
/// WIN_BACKGROUND_SRC (else ~/source/repos/people-first-client/src and ~/source/repos/win-background/src).</para>
/// </summary>
public class PythonTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private const string Oracle = """
        import json, sys
        sys.path[:0] = sys.argv[2:4]
        from people_first import lock_name
        from win_background import Lock
        mode = sys.argv[1]
        if mode == "names":
            paths = json.loads(sys.stdin.buffer.read().decode("utf-8"))
            sys.stdout.write(json.dumps([lock_name(p) for p in paths]))
        elif mode == "hold":
            lock = Lock(lock_name(sys.argv[4]))
            print("held" if lock.acquire(5) else "busy", flush=True)
            sys.stdin.readline()
            lock.release()
            print("released", flush=True)
        elif mode == "try":
            lock = Lock(lock_name(sys.argv[4]))
            got = lock.acquire(0)
            print("free" if got else "busy", flush=True)
            if got:
                lock.release()
        """;

    private static (string Python, string Client, string Background) Require()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var python = Environment.GetEnvironmentVariable("PEOPLE_FIRST_PYTHON") ?? @"C:\Python314\python.exe";
        var client = Environment.GetEnvironmentVariable("PEOPLE_FIRST_CLIENT_SRC") ?? Path.Combine(home, @"source\repos\people-first-client\src");
        var background = Environment.GetEnvironmentVariable("WIN_BACKGROUND_SRC") ?? Path.Combine(home, @"source\repos\win-background\src");
        Assert.SkipUnless(File.Exists(python), "No Python here to compare with.");
        Assert.SkipUnless(File.Exists(Path.Combine(client, "people_first", "lock.py")), "No people-first-client checkout here.");
        Assert.SkipUnless(File.Exists(Path.Combine(background, "win_background", "locks.py")), "No win-background checkout here.");
        return (python, client, background);
    }

    private static Process StartOracle(string mode, params string[] more)
    {
        var (python, client, background) = Require();
        var folder = Directory.CreateTempSubdirectory("pf-oracle-").FullName;   // the script's own folder: nothing else in it
        var script = Path.Combine(folder, "oracle.py");
        File.WriteAllText(script, Oracle);
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])["-I", script, mode, client, background, .. more])
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task<string[]> PythonLockNames(IReadOnlyList<string> paths)
    {
        using var python = StartOracle("names");
        await python.StandardInput.BaseStream.WriteAsync(JsonSerializerUtf8(paths), Cancel);
        python.StandardInput.Close();
        var output = await python.StandardOutput.ReadToEndAsync(Cancel);
        var errors = await python.StandardError.ReadToEndAsync(Cancel);
        await python.WaitForExitAsync(Cancel);
        Assert.True(python.ExitCode == 0, errors);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static byte[] JsonSerializerUtf8(IReadOnlyList<string> paths)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var path in paths)
                writer.WriteStringValue(path);
            writer.WriteEndArray();
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task Lock_names_equal_Pythons_for_the_real_profile_and_awkward_spellings()
    {
        Require();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] paths =
        [
            // org-directory's, team-leave's and cover-rota's People First profile on this machine (only the string:
            // the folder is never opened)
            Path.Combine(local, "claude-ea", "org-directory-edge-profile"),
            Path.Combine(local, "claude-ea", "org-directory-edge-profile") + "\\",
            Path.Combine(local, "claude-ea", "org-directory-edge-profile").ToUpperInvariant().Replace('\\', '/'),
            Path.Combine(local, "claude-ea", ".", "x", "..", "org-directory-edge-profile"),
            @"rel\p", "rel", ".", "",
            "C:/x//y/./z/../w", @"C:\foo. ", @"C:\foo\.", @"\\srv\share", @"\\srv\share\a\", @"C:\", @"c:\Lower\CASE",
            @"C:\Users\O’Brien\Ünïcödé\profile", "C:\\Users\\\u0130x\\\u039f\u0394\u039f\u03a3\\\u00df\u1e9e",
            "C:\\p\\\u03a3\u03a3.\\A\u03a3", "C:\\a\\x\u03a3\u0301y\\x\u03a3\u0301", "C:\\\U0001F600\\\u212A\u00C5",
        ];
        var python = await PythonLockNames(paths);
        for (var i = 0; i < paths.Length; i++)
            Assert.True(python[i] == ProfileLock.NameFor(paths[i]), $"path {i}: Python {python[i]}, .NET {ProfileLock.NameFor(paths[i])}");
        TestContext.Current.SendDiagnosticMessage($"this machine's People First profile lock: {python[0]}");
    }

    [Fact]
    public async Task Python_and_dotnet_take_turns_on_the_same_profile()
    {
        Require();
        var profile = Path.Combine(Path.GetTempPath(), "pf-interop-" + Guid.NewGuid().ToString("N"));

        // Python holds it: .NET waits, then times out; once Python lets go, .NET has it.
        using (var python = StartOracle("hold", profile))
        {
            Assert.Equal("held", await python.StandardOutput.ReadLineAsync(Cancel));
            await Assert.ThrowsAsync<TimeoutException>(() => ProfileLock.AcquireAsync(profile, 0.3, Cancel));
            await python.StandardInput.WriteLineAsync();
            python.StandardInput.Flush();
            Assert.Equal("released", await python.StandardOutput.ReadLineAsync(Cancel));
            await python.WaitForExitAsync(Cancel);
        }

        // .NET holds it: Python finds it busy; released, Python finds it free.
        await using (var mine = await ProfileLock.AcquireAsync(profile, 5, Cancel))
        {
            using var python = StartOracle("try", profile);
            Assert.Equal("busy", await python.StandardOutput.ReadLineAsync(Cancel));
            await python.WaitForExitAsync(Cancel);
        }
        using (var python = StartOracle("try", profile))
        {
            Assert.Equal("free", await python.StandardOutput.ReadLineAsync(Cancel));
            await python.WaitForExitAsync(Cancel);
        }
    }
}
