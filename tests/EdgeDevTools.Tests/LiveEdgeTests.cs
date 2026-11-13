using System.Diagnostics;

namespace EdgeDevTools.Tests;

/// <summary>The real Edge, headless, on a new empty profile in the temp folder: nothing is loaded from the network
/// (data: and about:blank pages only). Skipped where Edge isn't installed.</summary>
public class LiveEdgeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string RequireEdge()
    {
        try
        {
            return EdgeBrowser.FindExecutable();
        }
        catch (FileNotFoundException)
        {
            Assert.Skip("Microsoft Edge isn't installed here.");
            throw;
        }
    }

    [Theory]
    [InlineData("hidden", "")]
    [InlineData("visible", "")]
    [InlineData("hidden", "RunAsInvoker")]
    [InlineData("visible", "RunAsInvoker")]
    [InlineData("visible", "HighDpiAware")]
    public async Task Edge_starts_hidden_or_visible_even_under_a_compatibility_layer(string how, string layer)
    {
        // Regression, 13 Nov 2026: with __COMPAT_LAYER in the environment (Windows can set it on a program
        // double-clicked in Explorer), Edge relaunched itself without it and the process holding the pipe exited:
        // "The DevTools connection to Edge has closed" within 200 ms. The visible cases open a window briefly.
        RequireEdge();
        var profile = Directory.CreateTempSubdirectory("edge-devtools-test-").FullName;
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "EdgeHelper.exe");
            var start = new ProcessStartInfo(helper, ["launch", profile, how])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (layer.Length > 0)
                start.Environment["__COMPAT_LAYER"] = layer;
            using var child = Process.Start(start)!;
            var output = await child.StandardOutput.ReadToEndAsync(Cancel).WaitAsync(TimeSpan.FromSeconds(90), Cancel);
            await child.WaitForExitAsync(Cancel);
            Assert.Equal("ok", output.Trim());
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            DeleteQuietly(profile);
        }
    }

    [Fact]
    public async Task Edge_starts_runs_script_and_closes_cleanly_releasing_its_profile()
    {
        RequireEdge();
        var profile = Directory.CreateTempSubdirectory("edge-devtools-test-").FullName;
        try
        {
            int pid;
            await using (var browser = await EdgeBrowser.LaunchAsync(new EdgeLaunchOptions { UserDataDir = profile }, Cancel))
            {
                pid = browser.Connection.Transport.ProcessId;
                Assert.StartsWith("Edg", browser.Version);
                Assert.True(EdgeProfile.IsOpen(profile));
                await Assert.ThrowsAsync<EdgeLaunchException>(() => EdgeBrowser.LaunchAsync(new EdgeLaunchOptions { UserDataDir = profile }, Cancel));

                var page = await browser.NewPageAsync("data:text/html,<title>t</title><div style='height:3000px'></div><a>Pick me</a>", cancellationToken: Cancel);
                string? title = null;
                for (var i = 0; i < 50 && title != "t"; i++)
                {
                    title = (await browser.EvaluateAsync(page, "document.title", Cancel)).GetString();
                    await Task.Delay(100, Cancel);
                }
                Assert.Equal("t", title);
                Assert.NotNull(await browser.FindTextAsync(page, "pick ME", exact: false, Cancel));   // scrolled into view
                Assert.Null(await browser.FindTextAsync(page, "pick ME", exact: true, Cancel));
                var seen = MicrosoftSignIn.Parse(await browser.EvaluateAsync(page, MicrosoftSignIn.ProbeScript("Organisation account", "pick me"), Cancel));
                Assert.NotNull(seen);
                Assert.NotNull(seen.Tile);
                Assert.Null(seen.Button);
                Assert.Equal(SignInPage.NotMicrosoft, MicrosoftSignIn.Classify(seen));
            }
            Assert.True(Exited(pid, TimeSpan.FromSeconds(10)));
            for (var i = 0; i < 50 && EdgeProfile.IsOpen(profile); i++)
                await Task.Delay(100, Cancel);
            Assert.False(EdgeProfile.IsOpen(profile));
        }
        finally
        {
            DeleteQuietly(profile);
        }
    }

    [Fact]
    public async Task Edge_dies_with_the_process_that_started_it()
    {
        RequireEdge();
        var profile = Directory.CreateTempSubdirectory("edge-devtools-test-").FullName;
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "EdgeHelper.exe");
            using var parent = Process.Start(new ProcessStartInfo(helper, ["hold", profile])
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var line = await parent.StandardOutput.ReadLineAsync(Cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Cancel);
            Assert.StartsWith("edge ", line);
            var pid = int.Parse(line!["edge ".Length..]);
            Assert.False(Exited(pid, TimeSpan.Zero));
            parent.Kill();   // not the tree: only the parent, as a crash would
            Assert.True(Exited(pid, TimeSpan.FromSeconds(15)), "Edge outlived the process that started it");
        }
        finally
        {
            DeleteQuietly(profile);
        }
    }

    [Fact]
    public async Task What_Edge_prints_never_reaches_our_console()
    {
        // Regression, 13 Nov 2026: Edge's own logging (a company policy's "Sitelist download failed") appeared in a
        // double-clicked tool's console. Edge is a GUI program: it writes to the standard handles it inherits, and
        // with none it attaches to its parent's console. The helper runs with a hidden console of its own, launches
        // Edge told to log verbosely to stderr, then counts the characters left in that console.
        RequireEdge();
        var profile = Directory.CreateTempSubdirectory("edge-devtools-test-").FullName;
        var result = Path.Combine(profile, "..", Path.GetFileName(profile) + ".result");
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "EdgeHelper.exe");
            using var parent = Process.Start(new ProcessStartInfo(helper, ["quiet", profile, result])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            await parent.WaitForExitAsync(Cancel).WaitAsync(TimeSpan.FromSeconds(90), Cancel);
            Assert.Equal(0, parent.ExitCode);
            Assert.Equal("0", File.ReadAllText(result));
        }
        finally
        {
            DeleteQuietly(profile);
            File.Delete(result);
        }
    }

    [Fact]
    public void The_launch_gives_the_program_NUL_for_its_standard_handles()
    {
        var startup = PipeTransport.Startup(29, 1234, 77);
        Assert.Equal(Native.STARTF_USESTDHANDLES, startup.dwFlags & Native.STARTF_USESTDHANDLES);
        Assert.Equal(77, startup.hStdInput);
        Assert.Equal(77, startup.hStdOutput);
        Assert.Equal(77, startup.hStdError);
        Assert.Equal(1234, startup.lpReserved2);
    }

    private static bool Exited(int pid, TimeSpan wait)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(wait);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void DeleteQuietly(string folder)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                Directory.Delete(folder, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);   // Edge's children let go of files a moment after it exits
            }
        }
    }
}
