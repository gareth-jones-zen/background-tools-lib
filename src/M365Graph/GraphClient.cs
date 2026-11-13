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
    private static readonly HttpClient SharedRawHttp = Net.CreateHttpClient(Net.CreateHandler(decompress: false));
    private static readonly HashSet<HttpStatusCode> Retryable =
        [HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway,
         HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout];

    private readonly HttpClient http;
    private readonly HttpClient rawHttp;
    private readonly SignIn? signIn;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<string>>? token;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<DateTimeOffset> clock;

    public GraphClient(GraphClientOptions? options = null)
        : this(options ?? new GraphClientOptions(), SharedHttp, null, null)
    {
        rawHttp = SharedRawHttp;
    }

    /// <summary>A client whose requests all go through this handler: for a tool's adapter tests (a fake handler),
    /// or a proxy. The handler is used as it is, so it decides about compression and connecting.
    /// <para>token, when given, stands in for the sign-in: it gets the scopes asked for and returns an access
    /// token, and MSAL is never used (tests need no cache file). Without it, the sign-in is the usual one through
    /// the options' cache file, with MSAL's calls going through the handler too, and isn't shared with other
    /// clients. delay replaces the waits between retries (tests record them rather than wait); clock is "now" for a
    /// Retry-After given as a date.</para></summary>
    public GraphClient(GraphClientOptions options, HttpMessageHandler handler,
        Func<IReadOnlyList<string>, CancellationToken, Task<string>>? token = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        Options = options;
        http = rawHttp = new HttpClient(handler, disposeHandler: false) { Timeout = Graph.Timeout };
        this.token = token;
        if (token is null)
        {
            var client = http;
            signIn = new SignIn(() => new MsalApp(options.CachePath, options.ClientId, options.Tenant, client));
        }
        this.delay = delay ?? Task.Delay;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        Log = options.Log ?? (text => { Console.Error.WriteLine(text); Console.Error.Flush(); });
    }

    /// <summary>For the library's own tests: a fake HTTP client, a fake sign-in, and no real waiting.</summary>
    internal GraphClient(GraphClientOptions options, HttpClient http, SignIn? signIn, Func<TimeSpan, CancellationToken, Task>? delay,
        Func<DateTimeOffset>? clock = null)
    {
        Options = options;
        this.http = rawHttp = http;
        this.signIn = signIn ?? SignIn.For(options, http);
        this.delay = delay ?? Task.Delay;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        Log = options.Log ?? (text => { Console.Error.WriteLine(text); Console.Error.Flush(); });
    }

    public GraphClientOptions Options { get; }

    private Action<string> Log { get; }

    /// <summary>An access token for these scopes (default: the options' scopes). interactive overrides the options:
    /// false never opens a browser and throws SignInNeededException instead.</summary>
    public Task<string> TokenAsync(IEnumerable<string>? scopes = null, bool? interactive = null, CancellationToken cancel = default)
    {
        if (token is not null)
            return token(scopes?.ToList() is { Count: > 0 } named ? named : Options.Scopes.ToList(), cancel);
        return signIn!.TokenAsync(Options, scopes, interactive, Log, cancel);
    }

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

    /// <summary>One request with the body's bytes as they are, and the reply's bytes as served: no JSON, no
    /// "; charset" added to the Content-Type (it's sent exactly as given), and, on the default client, no
    /// compression asked for. For content that isn't JSON (a OneNote page's HTML) and for bodies that have to be
    /// byte for byte what the Python version sent.
    /// <para>The reply comes back whatever its status: a status the policy doesn't retry, or one that still failed
    /// after its tries, is returned for the caller to judge, not thrown. Network failures are retried as the policy
    /// says, and the last one is thrown; a request the policy won't resend, that may have been delivered, throws
    /// <see cref="GraphDeliveryUnknownException"/>. A Retry-After (seconds or a date) is honoured, otherwise the
    /// waits are 5 s, 10 s, 15 s and so on. Retries are logged through the options' Log, as other calls' are.
    /// Default policy: <see cref="GraphRetryPolicy.Read"/>.</para></summary>
    public async Task<GraphRawResponse> SendRawAsync(HttpMethod method, string url, byte[]? body = null, string? contentType = null,
        GraphRetryPolicy? policy = null, IEnumerable<string>? scopes = null, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancel = default)
    {
        policy ??= GraphRetryPolicy.Read;
        var full = url.StartsWith("http", StringComparison.Ordinal) ? url : Graph.BaseUrl + url;
        var scopeList = scopes?.ToList();
        for (var attempt = 1; ; attempt++)
        {
            var accessToken = await TokenAsync(scopeList, null, cancel).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, full);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (contentType is not null)
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    (request.Content ??= new ByteArrayContent([])).Headers.TryAddWithoutValidation(name, value);
            }

            TimeSpan wait;
            try
            {
                using var response = await rawHttp.SendAsync(request, cancel).ConfigureAwait(false);
                var raw = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode || !policy.Statuses.Contains(status) || attempt >= policy.Tries)
                    return new GraphRawResponse(response.StatusCode, raw, ContentTypeOf(response));
                wait = RetryAfter(response) ?? TimeSpan.FromSeconds(5 * attempt);
            }
            catch (Exception error) when (Net.IsNetworkError(error, cancel))
            {
                if (!policy.ResendAfterDelivery && !Net.NeverSent(error))
                    throw new GraphDeliveryUnknownException(method.Method, url, error);
                if (attempt >= policy.Tries)
                    throw;
                wait = TimeSpan.FromSeconds(5 * attempt);
            }
            Log($"Graph retry {attempt} in {wait.TotalSeconds:0}s: {method} {GraphRequestException.Shorten(full)}");
            await delay(wait, cancel).ConfigureAwait(false);
        }
    }

    private static string? ContentTypeOf(HttpResponseMessage response) =>
        response.Content.Headers.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var until = date - clock();
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }
        return null;
    }
}
