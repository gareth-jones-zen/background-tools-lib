using System.Text.Json.Nodes;

namespace M365Graph.Tests;

/// <summary>The real MSAL.NET, offline: a cache file laid out as MSAL Python writes it (its key format, string
/// timestamps), read through the cache binding. Any network call fails the test.</summary>
public class MsalCacheTests : Temp
{
    private const string Uid = "00000000-0000-0000-0000-0000000000aa", Tenant = "00000000-0000-0000-0000-0000000000bb";
    private const string Env = "login.microsoftonline.com", Target = "Mail.Read User.Read email openid profile";
    private readonly NoNetwork network = new();

    private string CachePath => Path.Combine(Folder, "msal-cache.json");

    private MsalApp App() => new(CachePath, Graph.CliClientId, Tenant, new HttpClient(network));

    /// <summary>As MSAL Python 1.x writes it: four-space indent, keys with the scopes on the end, times as strings.</summary>
    private static string PythonCache(string accessToken, double expiresInMinutes = 60)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expires = now + (long)(expiresInMinutes * 60);
        var home = $"{Uid}.{Tenant}";
        var cache = new JsonObject
        {
            ["AccessToken"] = new JsonObject
            {
                [$"{home}-{Env}-accesstoken-{Graph.CliClientId}-{Tenant}-{Target.ToLowerInvariant()}"] = new JsonObject
                {
                    ["credential_type"] = "AccessToken", ["secret"] = accessToken, ["home_account_id"] = home,
                    ["environment"] = Env, ["client_id"] = Graph.CliClientId, ["target"] = Target, ["realm"] = Tenant,
                    ["token_type"] = "Bearer", ["cached_at"] = now.ToString(), ["expires_on"] = expires.ToString(),
                    ["extended_expires_on"] = expires.ToString(),
                },
            },
            ["Account"] = new JsonObject
            {
                [$"{home}-{Env}-{Tenant}"] = new JsonObject
                {
                    ["home_account_id"] = home, ["environment"] = Env, ["realm"] = Tenant, ["local_account_id"] = Uid,
                    ["username"] = "someone@example.com", ["authority_type"] = "MSSTS",
                },
            },
            ["RefreshToken"] = new JsonObject
            {
                [$"{home}-{Env}-refreshtoken-{Graph.CliClientId}--{Target.ToLowerInvariant()}"] = new JsonObject
                {
                    ["credential_type"] = "RefreshToken", ["secret"] = "not-a-real-refresh-token", ["home_account_id"] = home,
                    ["environment"] = Env, ["client_id"] = Graph.CliClientId, ["target"] = Target,
                    ["last_modification_time"] = now.ToString(),
                },
            },
            ["IdToken"] = new JsonObject(),
            ["AppMetadata"] = new JsonObject
            {
                [$"appmetadata-{Env}-{Graph.CliClientId}"] = new JsonObject { ["client_id"] = Graph.CliClientId, ["environment"] = Env },
            },
        };
        return cache.ToJsonString(new() { WriteIndented = true, IndentSize = 4 });
    }

    [Fact]
    public async Task A_python_written_access_token_is_used_without_any_network_call_or_rewrite()
    {
        File.WriteAllText(CachePath, PythonCache("access-one"));
        var before = File.ReadAllBytes(CachePath);

        var token = await App().SilentAsync(["User.Read"], TestContext.Current.CancellationToken);

        Assert.Equal("access-one", token?.Token);
        Assert.Equal(0, network.Attempts);
        Assert.Equal(before, File.ReadAllBytes(CachePath));
    }

    [Fact]
    public async Task A_file_replaced_by_another_program_is_read_again()
    {
        File.WriteAllText(CachePath, PythonCache("access-one"));
        var app = App();
        Assert.Equal("access-one", (await app.SilentAsync(["User.Read"], TestContext.Current.CancellationToken))?.Token);

        TokenCacheFile.Write(CachePath, System.Text.Encoding.UTF8.GetBytes(PythonCache("access-two")));
        File.SetLastWriteTimeUtc(CachePath, DateTime.UtcNow.AddSeconds(5));   // in case both writes share a timestamp

        Assert.Equal("access-two", (await app.SilentAsync(["User.Read"], TestContext.Current.CancellationToken))?.Token);
        Assert.Equal(0, network.Attempts);
    }

    [Fact]
    public async Task A_damaged_file_still_signs_in()
    {
        File.WriteAllText(CachePath, PythonCache("access-one") + "}\"");
        Assert.Equal("access-one", (await App().SilentAsync(["User.Read"], TestContext.Current.CancellationToken))?.Token);
    }

    [Fact]
    public async Task No_file_is_no_account_and_no_network()
    {
        Assert.Null(await App().SilentAsync(["User.Read"], TestContext.Current.CancellationToken));
        Assert.Equal(0, network.Attempts);
        Assert.False(File.Exists(CachePath));
    }
}
