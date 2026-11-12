using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace M365Graph;

/// <summary>A small Microsoft Graph client: sign in once, cache the token, make requests that don't hang.
/// <code>
/// var graph = new GraphClient(new() { CachePath = ..., LoginHint = "you@example.com" });
/// var me = await graph.GetAsync("/me");
/// await foreach (var message in graph.PagesAsync("/teams/{id}/channels/{id}/messages", ["ChannelMessage.Read.All"]))
///     ...
/// </code>
/// What it's for, from running several unattended tools against Graph:
/// - One sign-in per process and per cache file, shared with the Python tools that use the same file.
/// - No browser when nobody's there: unattended, a missing sign-in throws SignInNeededException, and the caller
///   can notify someone.
/// - Scopes per call. Ask only for what a call needs, so a new consent for one tool never breaks another.
/// - A timeout and retries for stalls, 429s and 5xx. A stalled connection once hung a run for 30 minutes.
/// - IPv4 first (<see cref="Net"/>), which fixed sign-ins that took minutes.</summary>
public sealed class GraphClient
{
    private static readonly HttpClient SharedHttp = Net.CreateHttpClient();
    private static readonly HashSet<HttpStatusCode> Retryable =
        [HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway,
         HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout];

    private readonly HttpClient http;
    private readonly SignIn signIn;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public GraphClient(GraphClientOptions? options = null)
        : this(options ?? new GraphClientOptions(), SharedHttp, null, null)
    {
    }

    /// <summary>For tests: a fake HTTP handler, a fake sign-in, and no real waiting.</summary>
    internal GraphClient(GraphClientOptions options, HttpClient http, SignIn? signIn, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        Options = options;
        this.http = http;
        this.signIn = signIn ?? SignIn.For(options, http);
        this.delay = delay ?? Task.Delay;
        Log = options.Log ?? (text => { Console.Error.WriteLine(text); Console.Error.Flush(); });
    }

    public GraphClientOptions Options { get; }

    private Action<string> Log { get; }

    /// <summary>An access token for these scopes (default: the options' scopes). interactive overrides the options:
    /// false never opens a browser and throws SignInNeededException instead.</summary>
    public Task<string> TokenAsync(IEnumerable<string>? scopes = null, bool? interactive = null, CancellationToken cancel = default) =>
        signIn.TokenAsync(Options, scopes, interactive, Log, cancel);

    /// <summary>One Graph request, as JSON (an empty object for an empty reply). url may be relative ("/me") or a
    /// full URL (such as an @odata.nextLink).</summary>
    public async Task<JsonObject> RequestAsync(string url, HttpMethod? method = null, JsonNode? body = null,
        IEnumerable<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null, bool? interactive = null,
        CancellationToken cancel = default)
    {
        var raw = await SendAsync(url, method ?? HttpMethod.Get, body?.ToJsonString(), scopes, headers, interactive, cancel).ConfigureAwait(false);
        if (raw.Length == 0)
            return [];
        return JsonNode.Parse(raw) as JsonObject ?? throw new JsonException($"Graph didn't return a JSON object for {url}.");
    }

    /// <summary>One Graph request, read into a type through System.Text.Json source generation (AOT-safe).</summary>
    public async Task<T?> RequestAsync<T>(string url, JsonTypeInfo<T> type, HttpMethod? method = null, JsonNode? body = null,
        IEnumerable<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null, bool? interactive = null,
        CancellationToken cancel = default)
    {
        var raw = await SendAsync(url, method ?? HttpMethod.Get, body?.ToJsonString(), scopes, headers, interactive, cancel).ConfigureAwait(false);
        return raw.Length == 0 ? default : JsonSerializer.Deserialize(raw, type);
    }

    public Task<JsonObject> GetAsync(string url, IEnumerable<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancel = default) =>
        RequestAsync(url, HttpMethod.Get, null, scopes, headers, null, cancel);

    public Task<T?> GetAsync<T>(string url, JsonTypeInfo<T> type, IEnumerable<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancel = default) =>
        RequestAsync(url, type, HttpMethod.Get, null, scopes, headers, null, cancel);

    /// <summary>Every item across @odata.nextLink pages. stop(item) returning true ends after the current page.</summary>
    public async IAsyncEnumerable<JsonNode?> PagesAsync(string url, IEnumerable<string>? scopes = null,
        IReadOnlyDictionary<string, string>? headers = null, Func<JsonNode?, bool>? stop = null,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        var scopeList = scopes?.ToList();
        string? next = url;
        while (!string.IsNullOrEmpty(next))
        {
            var page = await GetAsync(next, scopeList, headers, cancel).ConfigureAwait(false);
            var done = false;
            if (page["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (stop is not null && stop(item))
                        done = true;
                    yield return item;
                }
            }
            next = done ? null : (string?)page["@odata.nextLink"];
        }
    }

    /// <summary>The request, with a timeout per try and up to three tries for 429, 5xx and dropped connections.
    /// A 429 or 503's Retry-After is honoured; otherwise 5 s, then 10 s.</summary>
    private async Task<byte[]> SendAsync(string url, HttpMethod method, string? body, IEnumerable<string>? scopes,
        IReadOnlyDictionary<string, string>? headers, bool? interactive, CancellationToken cancel)
    {
        if (!url.StartsWith("http", StringComparison.Ordinal))
            url = Graph.BaseUrl + url;
        var scopeList = scopes?.ToList();
        for (var attempt = 1; ; attempt++)
        {
            var token = await TokenAsync(scopeList, interactive, cancel).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    (request.Content ??= new ByteArrayContent([])).Headers.TryAddWithoutValidation(name, value);
            }

            var started = Stopwatch.StartNew();
            TimeSpan wait;
            try
            {
                using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
                var raw = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    if (started.Elapsed > Graph.Slow)
                        Log($"slow Graph call: {started.Elapsed.TotalSeconds:0}s {method} {GraphRequestException.Shorten(url)}");
                    return raw;
                }
                if (!Retryable.Contains(response.StatusCode) || attempt == Graph.Retries)
                    throw new GraphRequestException(response.StatusCode, method.Method, url, Encoding.UTF8.GetString(raw));
                wait = RetryAfter(response) ?? TimeSpan.FromSeconds(5 * attempt);
            }
            catch (Exception error) when (error is not GraphRequestException && attempt < Graph.Retries && Net.IsNetworkError(error, cancel))
            {
                wait = TimeSpan.FromSeconds(5 * attempt);
            }
            Log($"Graph retry {attempt} in {wait.TotalSeconds:0}s: {method} {GraphRequestException.Shorten(url)}");
            await delay(wait, cancel).ConfigureAwait(false);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }
        return null;
    }
}
