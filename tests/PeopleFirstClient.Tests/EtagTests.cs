using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PeopleFirstClient.Tests;

/// <summary>GetWithEtagAsync and PostAsync's extra headers (20 Nov 2026): People First refuses a write with 412 unless
/// it carries If-Match with the ETag of the GET the record was read from.</summary>
public class EtagTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Dictionary<string, string> Headers, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add((request.Method, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)), body));
            return answer(request);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null)
            response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    [Fact]
    public async Task A_get_returns_its_etag_and_a_post_sends_it_as_if_match()
    {
        var handler = new Handler(r => r.Method == HttpMethod.Get ? Reply(HttpStatusCode.OK, "{\"data\":{}}", "W/\"abc-1\"") : Reply(HttpStatusCode.OK, ""));
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(handler));
        var (body, etag) = await api.GetWithEtagAsync("/records/1", Cancel);
        Assert.Equal("W/\"abc-1\"", etag);
        Assert.True(body.TryGetProperty("data", out _));
        await api.PostAsync("/records/1/approved", new JsonObject(), new Dictionary<string, string> { ["If-Match"] = etag! }, Cancel);
        var (method, headers, _) = handler.Sent[1];
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("W/\"abc-1\"", headers["If-Match"]);
        Assert.Equal("Bearer x", headers["Authorization"]);
    }

    [Fact]
    public async Task No_etag_is_null()
    {
        var handler = new Handler(_ => Reply(HttpStatusCode.OK, "{}"));
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(handler));
        Assert.Null((await api.GetWithEtagAsync("/records/1", Cancel)).ETag);
    }

    [Fact]
    public async Task A_precondition_failure_is_a_refusal_with_its_status_and_not_resent()
    {
        var handler = new Handler(_ => Reply(HttpStatusCode.PreconditionFailed, "{\"error\":\"etag\"}"));
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(handler));
        var error = await Assert.ThrowsAsync<PeopleFirstException>(() =>
            api.PostAsync("/records/1/approved", new JsonObject(), new Dictionary<string, string> { ["If-Match"] = "x" }, Cancel));
        Assert.Equal(412, error.Status);
        Assert.IsNotType<PeopleFirstDeliveryUnknownException>(error);
        Assert.Contains("etag", error.Message);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task The_usual_headers_cant_be_replaced()
    {
        var handler = new Handler(_ => Reply(HttpStatusCode.OK, ""));
        using var api = new Api(PortedTests.TestSettings, "Bearer x", new HttpClient(handler));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            api.PostAsync("/records/1", new JsonObject(), new Dictionary<string, string> { ["authorization"] = "Bearer y" }, Cancel));
        Assert.Empty(handler.Sent);
    }
}
