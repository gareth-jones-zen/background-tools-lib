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
