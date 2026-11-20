using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PeopleFirstClient;

/// <summary>
/// People First's REST API (<c>/api/v1</c>), as the signed-in person. GETs, and one way to write,
/// <see cref="PostAsync"/>, which is never resent.
/// <para>Every call sends the web app's own <c>Authorization</c> header, <c>TenantCode</c>, an empty
/// <c>EnvironmentCode</c> and <c>Accept: application/json</c>, as the web app does. No cookies are needed, so the
/// browser is closed before any of these run.</para>
/// </summary>
public sealed class Api : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string authorization;
    private readonly string tenantCode;

    /// <summary>An Api with a token already in hand. <paramref name="http"/> is used as given (and not disposed);
    /// without one, the Api makes its own, with a 60-second timeout and IPv4 tried first.</summary>
    public Api(Settings settings, string authorization, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(authorization);
        Root = settings.Url.TrimEnd('/') + "/api/v1";
        this.authorization = authorization;
        tenantCode = settings.TenantCode;
        ownsHttp = http is null;
        this.http = http ?? CreateHttpClient();
    }

    /// <summary>https://yourorg.people-first.com/api/v1</summary>
    public string Root { get; }

    /// <summary>The headers every call sends (for tests; never log them: one is the token).</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> Headers =>
    [
        new("Authorization", authorization),
        new("TenantCode", tenantCode),
        new("EnvironmentCode", ""),
        new("Accept", "application/json"),
    ];

    /// <summary>The raw body of a GET under /api/v1. A status outside 200-299 is a PeopleFirstException naming the
    /// path but not its query (which can hold dates or names).</summary>
    public Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken = default) =>
        GetBytesAsync(path, "application/json", cancellationToken);

    /// <summary>The raw body of a GET under /api/v1, asking for <paramref name="accept"/> rather than JSON: a file
    /// attached to a record (a claim's receipt, say) comes back as itself only when the request doesn't ask for JSON
    /// (with <c>application/json</c> its download link answers with the attachment's details again). Otherwise as
    /// <see cref="GetBytesAsync(string, CancellationToken)"/>.</summary>
    public async Task<byte[]> GetBytesAsync(string path, string accept, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrEmpty(accept);
        using var request = new HttpRequestMessage(HttpMethod.Get, Root + path);
        foreach (var (name, value) in Headers)
            request.Headers.TryAddWithoutValidation(name, name == "Accept" ? accept : value);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is < 200 or > 299)
            throw new PeopleFirstException($"People First answered {status} to {path.Split('?')[0]}") { Status = status };
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A GET under /api/v1, as JSON. Python's <c>get()</c>.</summary>
    public async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var body = await GetBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>A GET under /api/v1, as JSON, with the record's ETag (the <c>ETag</c> header; null if there isn't one).
    /// People First checks a write against it: a POST that changes a record needs <c>If-Match</c> with the ETag of the
    /// GET it was read from, or it's refused with 412 (20 Nov 2026, approving a claim; the web app does the same).</summary>
    public async Task<(JsonElement Body, string? ETag)> GetWithEtagAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, Root + path);
        foreach (var (name, value) in Headers)
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is < 200 or > 299)
            throw new PeopleFirstException($"People First answered {status} to {path.Split('?')[0]}") { Status = status };
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string? etag = response.Headers.ETag?.ToString()
                       ?? (response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null);
        using var document = JsonDocument.Parse(body);
        return (document.RootElement.Clone(), string.IsNullOrEmpty(etag) ? null : etag);
    }

    /// <summary>A POST under /api/v1 with a JSON body (<c>Content-Type: application/json</c>, and the same headers as
    /// a GET); the reply as JSON, or an empty object if it has no body.
    /// <para><b>A write that may have reached People First is never resent: there's no retry at all.</b> What a
    /// failure means, so the caller can read back rather than guess:</para>
    /// <list type="bullet">
    /// <item>4xx: People First refused it, so nothing happened. A PeopleFirstException with <c>Status</c>.</item>
    /// <item>the name didn't resolve, no connection opened, or TLS failed: never sent. A PeopleFirstException with
    /// <c>NeverSent</c>.</item>
    /// <item>a timeout, a dropped connection, a 5xx, or a 2xx whose reply can't be read: it may or may not have
    /// happened. A <see cref="PeopleFirstDeliveryUnknownException"/>.</item>
    /// </list>
    /// <para>Messages name the path but not its query, as a GET's do. Cancelling through
    /// <paramref name="cancellationToken"/> throws OperationCanceledException, and the write may have happened.</para></summary>
    public Task<JsonElement> PostAsync(string path, JsonNode body, CancellationToken cancellationToken = default) =>
        PostAsync(path, body, null, cancellationToken);

    /// <summary>A POST as <see cref="PostAsync(string, JsonNode, CancellationToken)"/>, never resent, with extra
    /// headers (<c>If-Match</c>, say: the ETag from <see cref="GetWithEtagAsync"/>). The usual headers can't be
    /// replaced through it.</summary>
    public async Task<JsonElement> PostAsync(string path, JsonNode body, IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(body);
        var bare = path.Split('?')[0];
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + path);
        foreach (var (name, value) in Headers)
            request.Headers.TryAddWithoutValidation(name, value);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (Headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{name} is one of the usual headers and can't be replaced.", nameof(headers));
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        int status;
        byte[] reply;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            reply = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (NeverSentError(error))
        {
            throw new PeopleFirstException($"Couldn't reach People First to POST {bare}; nothing was sent.", error) { NeverSent = true };
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            // A timeout (HttpClient's is a TaskCanceledException), a reset, a connection dropped mid-reply.
            throw new PeopleFirstDeliveryUnknownException(
                $"POST {bare} to People First may or may not have happened: {Describe(error)}. It wasn't resent.", error);
        }
        if (status is >= 500)
            throw new PeopleFirstDeliveryUnknownException(
                $"People First answered {status} to POST {bare}: it may or may not have happened. It wasn't resent.") { Status = status };
        if (status is < 200 or > 299)
            throw new PeopleFirstException($"People First answered {status} to POST {bare}" + ErrorText(reply)) { Status = status };
        if (reply.Length == 0)
            return EmptyObject();
        try
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new PeopleFirstDeliveryUnknownException(
                $"People First answered {status} to POST {bare}, but its reply isn't JSON. It wasn't resent.", error) { Status = status };
        }
    }

    /// <summary>The start of a refusal's reply (People First explains a 4xx in it), for the message.</summary>
    private static string ErrorText(byte[] reply)
    {
        var text = Encoding.UTF8.GetString(reply).Trim();
        return text.Length == 0 ? "" : ": " + (text.Length > 400 ? text[..400] + "…" : text);
    }

    /// <summary>Whether a failed request certainly never reached the server: the name didn't resolve, no connection
    /// opened, or TLS failed before anything was sent (as M365Graph's Net.NeverSent, which this doesn't reference).
    /// Read from HttpRequestException.HttpRequestError, which the IPv4-first connect sets too.</summary>
    internal static bool NeverSentError(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError })
                return true;
        return false;
    }

    private static string Describe(Exception error) => error switch
    {
        TaskCanceledException { InnerException: TimeoutException } => "it timed out",
        OperationCanceledException => "it was cut short",
        _ => error.Message.Split('\n')[0],
    };

    /// <summary>Your own person id. The profile endpoint has it, under one name or another.</summary>
    public async Task<string> MeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in (string[])["/profile", "/hrm/app/profile", "/iam/profile"])
        {
            JsonElement body;
            try
            {
                body = await GetAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (PeopleFirstException)
            {
                continue;
            }
            if (FindKey(body, ["personId", "personID", "PersonId"]) is { } found)
                return found;
        }
        throw new PeopleFirstException("Couldn't find your own person id in People First's profile.");
    }

    /// <summary>The person ids of someone's direct reports (/team/people).</summary>
    public async Task<IReadOnlyList<string>> TeamAsync(string person, CancellationToken cancellationToken = default)
    {
        var people = new List<string>();
        await foreach (var item in PagesAsync($"/hrm/people/{person}/team/people", "page%5BLimit%5D", "page%5BOffset%5D", cancellationToken).ConfigureAwait(false))
            if (PersonId(item) is { } id)
                people.Add(id);
        return people;
    }

    /// <summary>Someone's direct reports with their names (/team): personId, firstName, lastName, knownAs,
    /// jobTitle.</summary>
    public async Task<IReadOnlyList<JsonElement>> TeamMembersAsync(string person, CancellationToken cancellationToken = default)
    {
        var people = new List<JsonElement>();
        await foreach (var item in PagesAsync($"/hrm/people/{person}/team", "page%5Blimit%5D", "page%5Boffset%5D", cancellationToken).ConfigureAwait(false))
            if (PersonId(item) is not null)
                people.Add(item);
        return people;
    }

    /// <summary>Someone's absences between two dates (inclusive), as People First lists them: the whole body,
    /// <c>{"data": {"events": [...]}, "meta": {...}}</c>. With annotate, meta resolves eventTypeId to type names.
    /// The listing has no reason or notes field, unlike /hrm/absences/{id}, which this never reads.</summary>
    public Task<JsonElement> EventsAsync(string person, string start, string end, bool annotate = true,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder($"viewStartDate={QuotePlus(start)}&viewEndDate={QuotePlus(end)}");
        if (annotate)
            query.Append("&annotate=t");
        return GetAsync($"/hrm/people/{person}/events?{query}", cancellationToken);
    }

    /// <summary>As the string form, with the dates written yyyy-MM-dd (Python's <c>str(date)</c>).</summary>
    public Task<JsonElement> EventsAsync(string person, DateOnly start, DateOnly end, bool annotate = true,
        CancellationToken cancellationToken = default) =>
        EventsAsync(person, start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), annotate, cancellationToken);

    /// <summary>Someone's phone numbers and emails (data.personcontacts); an empty object if there are none.
    /// Personal fields come back only for your own reporting line.</summary>
    public async Task<JsonElement> ContactAsync(string person, CancellationToken cancellationToken = default)
    {
        var body = await GetAsync($"/hrm/people/{person}/contactinfo", cancellationToken).ConfigureAwait(false);
        var contacts = Member(Member(body, "data"), "personcontacts");
        return Truthy(contacts) ? contacts : EmptyObject();
    }

    /// <summary>Python's paging loop: offset by the items seen, until a page is empty or the offset reaches
    /// meta.count.</summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(string path, string limitKey, string offsetKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var offset = 0;
        while (true)
        {
            var body = await GetAsync($"{path}?{limitKey}=100&{offsetKey}={offset}", cancellationToken).ConfigureAwait(false);
            var items = Member(Member(body, "data"), "team");
            var count = 0;
            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    count++;
                    yield return item;
                }
            }
            offset += count;
            if (count == 0 || offset >= Count(Member(Member(body, "meta"), "count")))
                yield break;
        }
    }

    private static JsonElement Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    // int(x or 0), as Python reads meta.count.
    private static int Count(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => (int)value.GetDouble(),
        JsonValueKind.String when value.GetString() is { Length: > 0 } text => int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture),
        _ => 0,
    };

    // item.get("personId") when it's truthy, as text.
    private static string? PersonId(JsonElement item)
    {
        var id = Member(item, "personId");
        return !Truthy(id) ? null : id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
    }

    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.True => true,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        _ => false,
    };

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>Python's _find_key: a non-empty string under one of the names, this level first, then depth first.</summary>
    internal static string? FindKey(JsonElement value, string[] names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                if (names.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } found)
                    return found;
            foreach (var property in value.EnumerateObject())
                if (FindKey(property.Value, names) is { } deeper)
                    return deeper;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (FindKey(item, names) is { } deeper)
                    return deeper;
        }
        return null;
    }

    /// <summary>Python's urllib.parse.quote_plus: letters, digits and _.-~ as they are, a space as +, the rest
    /// %XX of the UTF-8 bytes.</summary>
    internal static string QuotePlus(string value) => Uri.EscapeDataString(value ?? "").Replace("%20", "+", StringComparison.Ordinal);

    /// <summary>An HttpClient with a 60-second timeout whose connections try IPv4 first: on GJ's network IPv6 to
    /// some hosts goes nowhere, and trying addresses in DNS order stalled for minutes (as in M365Graph's Net,
    /// which this doesn't reference, to keep MSAL out of tools that only read People First).</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectIpv4FirstAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("background-tools-lib-peoplefirst", "0.1"));
        return client;
    }

    private static async ValueTask<Stream> ConnectIpv4FirstAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : (await Dns.GetHostAddressesAsync(endpoint.Host, cancel).ConfigureAwait(false))
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var oneAddress = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                oneAddress.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), oneAddress.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                socket.Dispose();
                last = error;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException(HttpRequestError.ConnectionError,
            $"Couldn't connect to {endpoint.Host}:{endpoint.Port} on any of its {addresses.Length} addresses.", last);
    }

    public void Dispose()
    {
        if (ownsHttp)
            http.Dispose();
    }
}
