using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace M365Graph.Tests;

public sealed record Me(string? DisplayName, string? UserPrincipalName);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Me))]
internal sealed partial class TestJson : JsonSerializerContext;

public class GraphClientTests
{
    [Fact]
    public async Task A_relative_url_goes_to_graph_v1_with_the_token()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.OK, "{\"displayName\": \"Someone\"}");
        var me = await Fake.Client(http).GetAsync("/me?$select=displayName");
        Assert.Equal("Someone", (string?)me["displayName"]);
        var seen = Assert.Single(http.Seen);
        Assert.Equal("https://graph.microsoft.com/v1.0/me?$select=displayName", seen.Url);
        Assert.Equal("Bearer tok", seen.Authorization);
    }

    [Fact]
    public async Task A_full_url_is_used_as_it_is()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.OK, "{}");
        await Fake.Client(http).GetAsync("https://graph.microsoft.com/beta/me");
        Assert.Equal("https://graph.microsoft.com/beta/me", http.Seen[0].Url);
    }

    [Fact]
    public async Task A_body_is_sent_as_json_and_extra_headers_go_on_the_request()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.NoContent);
        var reply = await Fake.Client(http).RequestAsync("/me/events/1", HttpMethod.Patch, new JsonObject { ["location"] = "Rear CP" },
            headers: new Dictionary<string, string> { ["Prefer"] = "outlook.timezone=\"UTC\"" });
        Assert.Empty(reply);
        var seen = http.Seen[0];
        Assert.Equal(HttpMethod.Patch, seen.Method);
        Assert.Equal("{\"location\":\"Rear CP\"}", seen.Body);
        Assert.Equal("application/json", seen.ContentType);
        Assert.Equal("outlook.timezone=\"UTC\"", seen.Request.Headers.GetValues("Prefer").Single());
    }

    [Fact]
    public async Task A_typed_reply_is_read_through_source_generation()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.OK, "{\"displayName\": \"Someone\", \"userPrincipalName\": \"someone@example.com\"}");
        var me = await Fake.Client(http).GetAsync("/me", TestJson.Default.Me);
        Assert.Equal(new Me("Someone", "someone@example.com"), me);
    }

    [Fact]
    public async Task Throttling_is_retried_after_the_retry_after_header()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.TooManyRequests, tweak: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));
        http.Reply(HttpStatusCode.OK, "{\"ok\": true}");
        var waits = new List<TimeSpan>();
        var log = new List<string>();
        var reply = await Fake.Client(http, waits: waits, log: log).GetAsync("/me");
        Assert.True((bool?)reply["ok"]);
        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
        Assert.Equal("Graph retry 1 in 7s: GET https://graph.microsoft.com/v1.0/me", Assert.Single(log));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Server_errors_are_retried_after_5_then_10_seconds_then_given_up(HttpStatusCode status)
    {
        var http = new FakeHandler();
        for (var i = 0; i < 3; i++)
            http.Reply(status, "{\"error\": \"busy\"}");
        var waits = new List<TimeSpan>();
        var error = await Assert.ThrowsAsync<GraphRequestException>(() => Fake.Client(http, waits: waits).GetAsync("/me"));
        Assert.Equal(status, error.Status);
        Assert.Contains("busy", error.Body);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], waits);
        Assert.Equal(3, http.Seen.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Other_errors_are_not_retried(HttpStatusCode status)
    {
        var http = new FakeHandler();
        http.Reply(status, "{\"error\": {\"code\": \"x\"}}");
        var error = await Assert.ThrowsAsync<GraphRequestException>(() => Fake.Client(http).GetAsync("/me"));
        Assert.Equal(status, error.Status);
        Assert.Single(http.Seen);
    }

    [Fact]
    public async Task A_dropped_or_stalled_connection_is_retried()
    {
        var http = new FakeHandler();
        http.Fail(new HttpRequestException("reset", new IOException("forcibly closed")));
        http.Fail(new TaskCanceledException("timed out", new TimeoutException()));
        http.Reply(HttpStatusCode.OK, "{\"ok\": true}");
        var waits = new List<TimeSpan>();
        Assert.True((bool?)(await Fake.Client(http, waits: waits).GetAsync("/me"))["ok"]);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], waits);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_retried()
    {
        var http = new FakeHandler();
        using var cancel = new CancellationTokenSource();
        http.Answers.Enqueue(_ => { cancel.Cancel(); throw new TaskCanceledException(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fake.Client(http).GetAsync("/me", cancel: cancel.Token));
        Assert.Single(http.Seen);
    }

    [Fact]
    public async Task Pages_follow_next_links_and_stop_ends_after_the_current_page()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.OK, "{\"value\": [1, 2], \"@odata.nextLink\": \"https://graph.microsoft.com/v1.0/x?skip=2\"}");
        http.Reply(HttpStatusCode.OK, "{\"value\": [3, 4], \"@odata.nextLink\": \"https://graph.microsoft.com/v1.0/x?skip=4\"}");
        http.Reply(HttpStatusCode.OK, "{\"value\": [5]}");
        var client = Fake.Client(http);

        var all = new List<int>();
        await foreach (var item in client.PagesAsync("/x"))
            all.Add((int)item!);
        Assert.Equal([1, 2, 3, 4, 5], all);
        Assert.Equal("https://graph.microsoft.com/v1.0/x?skip=4", http.Seen[2].Url);

        http.Reply(HttpStatusCode.OK, "{\"value\": [1, 2], \"@odata.nextLink\": \"https://graph.microsoft.com/v1.0/x?skip=2\"}");
        http.Reply(HttpStatusCode.OK, "{\"value\": [3, 4], \"@odata.nextLink\": \"https://graph.microsoft.com/v1.0/x?skip=4\"}");
        var some = new List<int>();
        await foreach (var item in client.PagesAsync("/x", stop: item => (int)item! == 3))
            some.Add((int)item!);
        Assert.Equal([1, 2, 3, 4], some);
        Assert.Equal(5, http.Seen.Count);
    }

    [Fact]
    public async Task Scopes_are_asked_for_per_call()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.OK, "{}");
        var msal = new FakeMsal();
        msal.Silent.Enqueue(() => null);
        var error = await Assert.ThrowsAsync<SignInNeededException>(() => Fake.Client(http, msal).GetAsync("/me/messages", ["Mail.Read"]));
        Assert.Contains("Mail.Read", error.Message);
        Assert.Empty(http.Seen);
    }
}
