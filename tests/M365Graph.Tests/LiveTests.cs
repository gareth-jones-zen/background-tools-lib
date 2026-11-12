namespace M365Graph.Tests;

/// <summary>Against the real Graph, opt-in only: set M365GRAPH_LIVE_CACHE to a token cache file (and
/// M365GRAPH_LIVE_TENANT to its tenant, if not "organizations"). The file is copied first and only the copy is used,
/// so a refresh never writes to the original. Silent only: it never opens a browser. Skipped otherwise.</summary>
public class LiveTests
{
    [Fact]
    public async Task Me_can_be_read_silently_from_a_copy_of_a_real_cache()
    {
        var source = Environment.GetEnvironmentVariable("M365GRAPH_LIVE_CACHE");
        Assert.SkipWhen(string.IsNullOrEmpty(source), "Set M365GRAPH_LIVE_CACHE to run against the real Graph.");
        var folder = Path.Combine(Path.GetTempPath(), "m365graph-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var copy = Path.Combine(folder, "msal-cache.json");
            File.Copy(source!, copy);
            var graph = new GraphClient(new GraphClientOptions
            {
                CachePath = copy,
                Tenant = Environment.GetEnvironmentVariable("M365GRAPH_LIVE_TENANT") is { Length: > 0 } tenant ? tenant : "organizations",
                Interactive = false,
            });
            var me = await graph.GetAsync("/me?$select=displayName", ["User.Read"], cancel: TestContext.Current.CancellationToken);
            Assert.False(string.IsNullOrEmpty((string?)me["displayName"]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
