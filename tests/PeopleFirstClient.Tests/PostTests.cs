using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PeopleFirstClient.Tests;

/// <summary>Api.PostAsync: the one write. What matters most is that a POST that may have reached People First is
/// never sent twice, and that each failure says whether it might have happened.</summary>
public class PostTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Answers every request with the given function, and records what was sent (body read as text).</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, Dictionary<string, string> Headers, string? ContentType, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add((request.Method, request.RequestUri!.OriginalString, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                request.Content?.Headers.ContentType?.ToString(), body));
            return answer(request);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Api ApiOver(Handler handler) => new(PortedTests.TestSettings, "Bearer x", new HttpClient(handler));

    private static JsonObject Template(string id = "A1") => new()
    {
        ["data"] = new JsonObject { ["absences"] = new JsonObject { ["_links"] = new JsonObject(), ["id"] = id } },
    };

    [Fact]
    public async Task Posts_json_with_the_usual_headers()
    {
        var handler = new Handler(_ => Reply(HttpStatusCode.OK, "{\"data\":{\"absences\":{\"id\":\"A1\"}}}"));
        using var api = ApiOver(handler);
        var reply = await api.PostAsync("/hrm/absences/approved?absence=2&absenceapproval=1", Template(), Cancel);
        Assert.Equal("A1", reply.GetProperty("data").GetProperty("absences").GetProperty("id").GetString());
        var (method, url, headers, contentType, body) = Assert.Single(handler.Sent);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://example.people-first.com/api/v1/hrm/absences/approved?absence=2&absenceapproval=1", url);
        Assert.Equal("Bearer x", headers["Authorization"]);
        Assert.Equal("example", headers["TenantCode"]);
        Assert.Equal("application/json", headers["Accept"]);
        Assert.True(headers.ContainsKey("EnvironmentCode"));
        Assert.Equal("application/json", contentType);
        Assert.Equal("{\"data\":{\"absences\":{\"_links\":{},\"id\":\"A1\"}}}", body);
    }

    [Fact]
    public async Task An_empty_reply_is_an_empty_object()
    {
        using var api = ApiOver(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var reply = await api.PostAsync("/hrm/absences/approved", Template(), Cancel);
        Assert.Equal("{}", reply.GetRawText());
    }

    [Fact]
    public async Task A_refusal_is_not_resent_and_says_nothing_happened()
    {
        var handler = new Handler(_ => Reply(HttpStatusCode.Forbidden, "{}"));
        using var api = ApiOver(handler);
        var error = await Assert.ThrowsAsync<PeopleFirstException>(() => api.PostAsync("/hrm/absences/approved?absence=2", Template(), Cancel));
        Assert.IsNotType<PeopleFirstDeliveryUnknownException>(error);
        Assert.Equal(403, error.Status);
        Assert.False(error.NeverSent);
        Assert.Equal("People First answered 403 to POST /hrm/absences/approved", error.Message);
        Assert.Single(handler.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_5xx_may_have_happened_and_is_not_resent(HttpStatusCode status)
    {
        var handler = new Handler(_ => Reply(status));
        using var api = ApiOver(handler);
        var error = await Assert.ThrowsAsync<PeopleFirstDeliveryUnknownException>(() => api.PostAsync("/hrm/absences/approved?absence=2", Template(), Cancel));
        Assert.Equal((int)status, error.Status);
        Assert.DoesNotContain("absence=2", error.Message);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task A_timeout_may_have_happened_and_is_not_resent()
    {
        var handler = new Handler(_ => throw new TaskCanceledException("timed out", new TimeoutException()));
        using var api = ApiOver(handler);
        var error = await Assert.ThrowsAsync<PeopleFirstDeliveryUnknownException>(() => api.PostAsync("/hrm/absences/approved", Template(), Cancel));
        Assert.Contains("timed out", error.Message);
        Assert.False(error.NeverSent);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task HttpClients_own_timeout_may_have_happened_and_is_not_resent()
    {
        var handler = new Handler(_ =>
        {
            Thread.Sleep(500);
            return Reply(HttpStatusCode.OK);
        });
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) });
        await Assert.ThrowsAsync<PeopleFirstDeliveryUnknownException>(() => api.PostAsync("/hrm/absences/approved", Template(), Cancel));
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task A_dropped_connection_may_have_happened_and_is_not_resent()
    {
        var handler = new Handler(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.",
            new IOException("reset")));
        using var api = ApiOver(handler);
        var error = await Assert.ThrowsAsync<PeopleFirstDeliveryUnknownException>(() => api.PostAsync("/hrm/absences/approved", Template(), Cancel));
        Assert.Contains("response ended prematurely", error.Message);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task A_2xx_reply_that_isnt_json_is_unknown()
    {
        var handler = new Handler(_ => Reply(HttpStatusCode.OK, "<html>"));
        using var api = ApiOver(handler);
        var error = await Assert.ThrowsAsync<PeopleFirstDeliveryUnknownException>(() => api.PostAsync("/hrm/absences/approved", Template(), Cancel));
        Assert.Equal(200, error.Status);
        Assert.Single(handler.Sent);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task A_connection_that_never_opened_says_so_and_is_not_retried(HttpRequestError kind)
    {
        var handler = new Handler(_ => throw new HttpRequestException(kind, "no connection"));
        using var api = ApiOver(handler);
        var error = await Assert.ThrowsAsync<PeopleFirstException>(() => api.PostAsync("/hrm/absences/approved", Template(), Cancel));
        Assert.IsNotType<PeopleFirstDeliveryUnknownException>(error);
        Assert.True(error.NeverSent);
        Assert.Null(error.Status);
        Assert.Single(handler.Sent);
    }

    /// <summary>The real handler and the IPv4-first connect, against a port nothing listens on: the connect's own
    /// failure is recognised as never sent.</summary>
    [Fact]
    public async Task The_real_connect_failure_is_never_sent()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        using var api = new Api(new Settings(url: $"https://127.0.0.1:{port}", tenantCode: "example"), "Bearer x");
        var error = await Assert.ThrowsAsync<PeopleFirstException>(() => api.PostAsync("/hrm/absences/approved", Template(), Cancel));
        Assert.True(error.NeverSent, error.ToString());
    }

    [Fact]
    public async Task A_cancelled_post_is_the_callers_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler(_ =>
        {
            cancel.Cancel();
            throw new TaskCanceledException();
        });
        using var api = ApiOver(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.PostAsync("/hrm/absences/approved", Template(), cancel.Token));
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task A_get_refusal_carries_its_status()
    {
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(new FakeHttp([])));
        var error = await Assert.ThrowsAsync<PeopleFirstException>(() => api.GetAsync("/hrm/absences/A1/absenceChanges", Cancel));
        Assert.Equal(404, error.Status);
    }
}
