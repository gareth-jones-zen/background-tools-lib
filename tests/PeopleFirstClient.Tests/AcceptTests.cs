namespace PeopleFirstClient.Tests;

/// <summary>GetBytesAsync with its own Accept (20 Nov 2026): a record's attached file comes back as itself only
/// when the request doesn't ask for JSON.</summary>
public class AcceptTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_given_accept_replaces_json_and_the_other_headers_stay()
    {
        var http = new FakeHttp(new() { ["/files/1"] = FakeHttp.Body("%PDF-1.3") });
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(http));
        var bytes = await api.GetBytesAsync("/files/1", "application/pdf", Cancel);
        Assert.Equal("%PDF-1.3", System.Text.Encoding.UTF8.GetString(bytes));
        var (_, method, headers) = Assert.Single(http.Calls);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("application/pdf", headers["Accept"]);
        Assert.Equal("Bearer x", headers["Authorization"]);
        Assert.Equal("example", headers["TenantCode"]);
    }

    [Fact]
    public async Task Without_one_it_still_asks_for_json()
    {
        var http = new FakeHttp(new() { ["/files/1"] = FakeHttp.Body("{}") });
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(http));
        await api.GetBytesAsync("/files/1", Cancel);
        Assert.Equal("application/json", Assert.Single(http.Calls).Headers["Accept"]);
    }

    [Fact]
    public async Task An_empty_accept_is_refused_and_a_refusal_carries_its_status()
    {
        var http = new FakeHttp([]);
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(http));
        await Assert.ThrowsAsync<ArgumentException>(() => api.GetBytesAsync("/files/1", "", Cancel));
        var error = await Assert.ThrowsAsync<PeopleFirstException>(() => api.GetBytesAsync("/files/2", "application/pdf", Cancel));
        Assert.Equal(404, error.Status);
    }
}
