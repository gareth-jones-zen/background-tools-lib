using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace M365Graph.Tests;

/// <summary>What the ports needed (13 Nov 2026): a public way to put a fake handler under a GraphClient, raw byte
/// requests with a retry policy per call (onenote-sync's RawGraph), and connect failures that say so by type.</summary>
public class RawAndHandlerTests
{
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Method, string Url, string? ContentType, byte[]? Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancel);
            var type = request.Content?.Headers.TryGetValues("Content-Type", out var values) == true ? string.Join(",", values) : null;
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), type, body, request.Headers.Authorization?.ToString()));
            return answer(request, Requests.Count);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly GraphRetryPolicy Read5 = GraphRetryPolicy.Read with { Tries = 5 };
    private static readonly GraphRetryPolicy Write5 = GraphRetryPolicy.Write with { Tries = 5 };

    private static (GraphClient, Handler, List<TimeSpan>, List<IReadOnlyList<string>>) Make(Func<HttpRequestMessage, int, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        var waits = new List<TimeSpan>();
        var scopes = new List<IReadOnlyList<string>>();
        var client = new GraphClient(new GraphClientOptions { Scopes = ["Notes.ReadWrite.All"], Log = _ => { } }, handler,
            token: (asked, _) => { scopes.Add(asked); return Task.FromResult("token"); },
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
            clock: () => Now);
        return (client, handler, waits, scopes);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, byte[]? body = null) =>
        new(status) { Content = new ByteArrayContent(body ?? []) };

    [Fact]
    public async Task Raw_content_is_the_bytes_as_served()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0xFF, 13, 10 };
        var (client, handler, _, scopes) = Make((_, _) => Answer(HttpStatusCode.OK, bytes));
        var reply = await client.SendRawAsync(HttpMethod.Get, "/users/a%40b.c/onenote/pages/1-abc/content?includeIDs=true",
            cancel: TestContext.Current.CancellationToken);
        Assert.Equal(bytes, reply.Body);
        Assert.True(reply.IsSuccess);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/a%40b.c/onenote/pages/1-abc/content?includeIDs=true", handler.Requests[0].Url);
        Assert.Equal("Bearer token", handler.Requests[0].Authorization);
        Assert.Equal(["Notes.ReadWrite.All"], scopes[0]);
    }

    [Fact]
    public async Task A_get_is_retried_and_retry_after_can_be_a_date()
    {
        var (client, handler, waits, _) = Make((_, n) =>
        {
            if (n == 1)
            {
                var busy = Answer(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(7));
                return busy;
            }
            return n == 2 ? Answer(HttpStatusCode.BadGateway) : Answer(HttpStatusCode.OK, [1]);
        });
        var reply = await client.SendRawAsync(HttpMethod.Get, "/p", policy: Read5, cancel: TestContext.Current.CancellationToken);
        Assert.Equal([1], reply.Body);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10)], waits);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task An_error_status_comes_back_rather_than_being_thrown()
    {
        var (client, handler, waits, _) = Make((_, _) => Answer(HttpStatusCode.Forbidden, Encoding.UTF8.GetBytes("denied")));
        var reply = await client.SendRawAsync(HttpMethod.Get, "/p", cancel: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, reply.Status);
        Assert.False(reply.IsSuccess);
        Assert.Equal("denied", Encoding.UTF8.GetString(reply.Body));
        Assert.Single(handler.Requests);
        Assert.Empty(waits);

        var (busy, busyHandler, _, _) = Make((_, _) => Answer(HttpStatusCode.ServiceUnavailable));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await busy.SendRawAsync(HttpMethod.Get, "/p", cancel: TestContext.Current.CancellationToken)).Status);
        Assert.Equal(Graph.Retries, busyHandler.Requests.Count);   // the default policy's tries, then the reply
    }

    [Fact]
    public async Task A_patch_is_sent_once_with_the_exact_content_type()
    {
        var (client, handler, _, _) = Make((_, _) => Answer(HttpStatusCode.BadGateway));
        var body = Encoding.UTF8.GetBytes("[{\"target\": \"ul:1\"}]");
        var reply = await client.SendRawAsync(HttpMethod.Patch, "/p/content", body, "application/json", Write5,
            cancel: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, reply.Status);   // the caller decides what a 5xx on a write means
        Assert.Single(handler.Requests);
        Assert.Equal("application/json", handler.Requests[0].ContentType);   // no "; charset=utf-8"
        Assert.Equal(body, handler.Requests[0].Body);
        var (html, htmlHandler, _, _) = Make((_, _) => Answer(HttpStatusCode.Created, Encoding.UTF8.GetBytes("{\"id\": \"0-new\"}")));
        await html.SendRawAsync(HttpMethod.Post, "/sections/s/pages", [0x3C], "text/html", Write5, cancel: TestContext.Current.CancellationToken);
        Assert.Equal("text/html", htmlHandler.Requests[0].ContentType);
    }

    [Fact]
    public async Task A_write_is_retried_after_a_429_but_not_after_a_timeout()
    {
        var (client, handler, _, _) = Make((_, n) => n == 1 ? Answer(HttpStatusCode.TooManyRequests) : Answer(HttpStatusCode.NoContent));
        Assert.True((await client.SendRawAsync(HttpMethod.Patch, "/p", [1], "application/json", Write5, cancel: TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(2, handler.Requests.Count);

        var (timedOut, again, _, _) = Make((_, _) => throw new TaskCanceledException("timed out", new TimeoutException()));
        var error = await Assert.ThrowsAsync<GraphDeliveryUnknownException>(() =>
            timedOut.SendRawAsync(HttpMethod.Patch, "/p", [1], "application/json", Write5, cancel: TestContext.Current.CancellationToken));
        Assert.Single(again.Requests);
        Assert.Equal("No answer to PATCH /p (TaskCanceledException: timed out); not sent again, because it may have been applied.", error.Message);
        Assert.Equal(("PATCH", "/p"), (error.Method, error.Url));
    }

    [Fact]
    public async Task A_write_that_never_connected_is_retried()
    {
        var (client, handler, _, _) = Make((_, n) => n == 1
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
            : Answer(HttpStatusCode.NoContent));
        await client.SendRawAsync(HttpMethod.Patch, "/p", [1], "application/json", Write5, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_get_that_keeps_failing_throws_the_last_network_error()
    {
        var (client, handler, waits, _) = Make((_, _) => throw new HttpRequestException("reset", new IOException("reset")));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendRawAsync(HttpMethod.Get, "/p", policy: Read5, cancel: TestContext.Current.CancellationToken));
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal([5, 10, 15, 20], waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public async Task The_json_calls_go_through_an_injected_handler_too()
    {
        var (client, handler, _, scopes) = Make((_, n) => n == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"value\": [{\"id\": \"1\"}], \"@odata.nextLink\": \"https://graph.microsoft.com/v1.0/next\"}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"value\": [{\"id\": \"2\"}]}") });
        var ids = new List<string?>();
        await foreach (var item in client.PagesAsync("/me/messages", ["Mail.Read"], cancel: TestContext.Current.CancellationToken))
            ids.Add((string?)item!["id"]);
        Assert.Equal(["1", "2"], ids);
        Assert.Equal("https://graph.microsoft.com/v1.0/next", handler.Requests[1].Url);
        Assert.Equal(["Mail.Read"], scopes[0]);
        Assert.Equal("token", await client.TokenAsync(cancel: TestContext.Current.CancellationToken));
        Assert.Equal(["Notes.ReadWrite.All"], scopes[^1]);   // no scopes named: the options' scopes
    }

    [Fact]
    public void Never_sent_is_read_from_the_error_kind()
    {
        Assert.True(Net.NeverSent(new HttpRequestException(HttpRequestError.ConnectionError, "x")));
        Assert.True(Net.NeverSent(new HttpRequestException(HttpRequestError.NameResolutionError, "x")));
        Assert.True(Net.NeverSent(new InvalidOperationException("outer", new HttpRequestException(HttpRequestError.SecureConnectionError, "x"))));
        Assert.False(Net.NeverSent(new HttpRequestException(HttpRequestError.ResponseEnded, "x")));
        Assert.False(Net.NeverSent(new TaskCanceledException("timed out", new TimeoutException())));
        Assert.Equal(DecompressionMethods.None, Net.CreateHandler(decompress: false).AutomaticDecompression);
        Assert.Equal(DecompressionMethods.All, Net.CreateHandler().AutomaticDecompression);
    }

    [Fact]
    public async Task A_refused_connection_is_a_connection_error()
    {
        using var http = Net.CreateHttpClient(Net.CreateHandler());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://127.0.0.1:1/", TestContext.Current.CancellationToken));
        Assert.Equal(HttpRequestError.ConnectionError, error.HttpRequestError);
        Assert.True(Net.NeverSent(error));
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_a_name_resolution_error()
    {
        using var http = Net.CreateHttpClient(Net.CreateHandler());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://no-such-host.invalid/", TestContext.Current.CancellationToken));
        // SocketsHttpHandler wraps what the connect callback throws in its own ConnectionError; ours is inside it.
        var kinds = new List<HttpRequestError>();
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (e is HttpRequestException h)
                kinds.Add(h.HttpRequestError);
        Assert.Contains(HttpRequestError.NameResolutionError, kinds);
        Assert.True(Net.NeverSent(error));
    }
}
