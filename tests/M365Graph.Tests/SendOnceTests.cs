using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace M365Graph.Tests;

/// <summary>A write that may have reached Microsoft is never resent: a POST or PATCH is sent once unless the caller
/// opts in, a 5xx or a lost answer after sending is "may have been applied", and reads, PUTs and DELETEs still retry.</summary>
public class SendOnceTests
{
    private static readonly JsonObject Body = new() { ["subject"] = "x" };

    [Theory]
    [InlineData("POST", HttpStatusCode.InternalServerError)]
    [InlineData("POST", HttpStatusCode.BadGateway)]
    [InlineData("POST", HttpStatusCode.ServiceUnavailable)]
    [InlineData("POST", HttpStatusCode.GatewayTimeout)]
    [InlineData("PATCH", HttpStatusCode.ServiceUnavailable)]
    public async Task A_5xx_to_a_write_is_not_retried_and_may_have_been_applied(string method, HttpStatusCode status)
    {
        var http = new FakeHandler();
        http.Reply(status, "{\"error\": \"busy\"}");
        var waits = new List<TimeSpan>();
        var error = await Assert.ThrowsAsync<GraphDeliveryUnknownException>(() =>
            Fake.Client(http, waits: waits).RequestAsync("/me/events", new HttpMethod(method), Body));
        Assert.Single(http.Seen);
        Assert.Empty(waits);
        Assert.Equal(status, error.Status);
        Assert.Equal((method, "/me/events"), (error.Method, error.Url));
        Assert.Contains("may have been applied", error.Message);
        Assert.Contains("busy", error.Body);
    }

    [Fact]
    public async Task A_timeout_or_a_dropped_connection_after_sending_a_write_is_not_retried()
    {
        foreach (var failure in new Exception[]
                 {
                     new TaskCanceledException("timed out", new TimeoutException()),
                     new HttpRequestException("reset", new IOException("forcibly closed")),
                     new HttpRequestException(HttpRequestError.ResponseEnded, "ended"),
                 })
        {
            var http = new FakeHandler();
            http.Fail(failure);
            var error = await Assert.ThrowsAsync<GraphDeliveryUnknownException>(() =>
                Fake.Client(http).RequestAsync("/me/sendMail", HttpMethod.Post, Body));
            Assert.Single(http.Seen);
            Assert.Null(error.Status);
            Assert.Same(failure, error.InnerException);
        }
    }

    [Fact]
    public async Task A_write_that_never_connected_or_was_throttled_is_sent_again()
    {
        var http = new FakeHandler();
        http.Fail(new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        http.Reply(HttpStatusCode.TooManyRequests, tweak: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)));
        http.Reply(HttpStatusCode.Created, "{\"id\": \"1\"}");
        var waits = new List<TimeSpan>();
        var reply = await Fake.Client(http, waits: waits).RequestAsync("/me/events", HttpMethod.Post, Body);
        Assert.Equal("1", (string?)reply["id"]);
        Assert.Equal(3, http.Seen.Count);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    public async Task A_write_still_throttled_after_its_tries_is_a_refusal_not_an_unknown()
    {
        var http = new FakeHandler();
        for (var i = 0; i < 3; i++)
            http.Reply(HttpStatusCode.TooManyRequests);
        var error = await Assert.ThrowsAsync<GraphRequestException>(() => Fake.Client(http).RequestAsync("/me/events", HttpMethod.Post, Body));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.Status);
        Assert.Equal(3, http.Seen.Count);
    }

    [Fact]
    public async Task A_4xx_to_a_write_is_a_refusal()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.BadRequest, "{\"error\": \"no\"}");
        await Assert.ThrowsAsync<GraphRequestException>(() => Fake.Client(http).RequestAsync("/me/events", HttpMethod.Post, Body));
        Assert.Single(http.Seen);
    }

    [Fact]
    public async Task A_get_still_retries_a_5xx_and_a_dropped_connection()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.BadGateway);
        http.Fail(new TaskCanceledException("timed out", new TimeoutException()));
        http.Reply(HttpStatusCode.OK, "{\"ok\": true}");
        Assert.True((bool?)(await Fake.Client(http).GetAsync("/me"))["ok"]);
        Assert.Equal(3, http.Seen.Count);
    }

    [Fact]
    public async Task A_caller_can_opt_a_post_or_patch_into_retries()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.ServiceUnavailable);
        http.Reply(HttpStatusCode.OK, "{\"value\": []}");
        await Fake.Client(http).RequestAsync("/me/calendar/getSchedule", HttpMethod.Post, Body, policy: GraphRetryPolicy.Read);
        Assert.Equal(2, http.Seen.Count);
    }

    [Fact]
    public async Task A_put_is_retried()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.BadGateway);
        http.Reply(HttpStatusCode.OK, "{}");
        await Fake.Client(http).RequestAsync("/x", HttpMethod.Put, Body);
        Assert.Equal(2, http.Seen.Count);
    }

    [Fact]
    public async Task A_retried_delete_that_finds_it_gone_is_done()
    {
        var http = new FakeHandler();
        http.Fail(new TaskCanceledException("timed out", new TimeoutException()));
        http.Reply(HttpStatusCode.NotFound, "{\"error\": {\"code\": \"ErrorItemNotFound\"}}");
        var log = new List<string>();
        Assert.Empty(await Fake.Client(http, log: log).RequestAsync("/me/events/1", HttpMethod.Delete));
        Assert.Equal(2, http.Seen.Count);
        Assert.Contains(log, line => line.Contains("found it gone", StringComparison.Ordinal));

        var first = new FakeHandler();
        first.Reply(HttpStatusCode.NotFound, "{}");
        var gone = await Assert.ThrowsAsync<GraphRequestException>(() => Fake.Client(first).RequestAsync("/me/events/1", HttpMethod.Delete));
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);   // on the first try it's an error, as before
    }

    [Fact]
    public void The_default_policy_comes_from_the_method()
    {
        Assert.Same(GraphRetryPolicy.Read, GraphRetryPolicy.For(HttpMethod.Get));
        Assert.Same(GraphRetryPolicy.Read, GraphRetryPolicy.For(HttpMethod.Put));
        Assert.Same(GraphRetryPolicy.Read, GraphRetryPolicy.For(HttpMethod.Delete));
        Assert.Same(GraphRetryPolicy.Write, GraphRetryPolicy.For(HttpMethod.Post));
        Assert.Same(GraphRetryPolicy.Write, GraphRetryPolicy.For(HttpMethod.Patch));
        Assert.Same(GraphRetryPolicy.Write, GraphRetryPolicy.For(new HttpMethod("MERGE")));
    }

    [Fact]
    public async Task A_raw_post_is_sent_once_by_default()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.InternalServerError);
        var client = new GraphClient(new GraphClientOptions { Log = _ => { } }, http, token: (_, _) => Task.FromResult("t"),
            delay: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<GraphDeliveryUnknownException>(() => client.SendRawAsync(HttpMethod.Post, "/x", [1], "application/json"));
        Assert.Single(http.Seen);
    }
}
