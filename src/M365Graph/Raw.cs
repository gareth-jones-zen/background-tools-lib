using System.Net;

namespace M365Graph;

/// <summary>How a request may be retried: how many tries in all, which statuses are worth another try, and whether
/// a request that may have reached Graph (a 5xx, a timeout or a dropped connection after it was sent) can be sent
/// again.
/// <para><b>A write that may have reached Microsoft is never resent</b>: a retried POST can send an invitation, a
/// forward or a chat message twice, or make two events or pages. So every call gets a policy from its method unless
/// the caller names one (<see cref="For"/>): GET, HEAD, OPTIONS, PUT and DELETE are safe to repeat
/// (<see cref="Read"/>); POST, PATCH and anything else are sent once (<see cref="Write"/>). A PATCH is sent once by
/// default because some send mail (adding attendees sends invitations; an automatic-reply setting answers people);
/// a caller whose POST only reads (getSchedule) or whose PATCH notifies nobody passes <see cref="Read"/> to opt
/// in to retries.</para></summary>
public sealed record GraphRetryPolicy(int Tries, IReadOnlySet<int> Statuses, bool ResendAfterDelivery)
{
    /// <summary>For a call that's safe to repeat: 429, 500, 502, 503 and 504 retried, and a dropped connection
    /// too, up to <see cref="Graph.Retries"/> tries.</summary>
    public static GraphRetryPolicy Read { get; } = new(Graph.Retries, new HashSet<int> { 429, 500, 502, 503, 504 }, true);

    /// <summary>For a call that mustn't be applied twice (send-once): only a 429 (throttled, so not processed) or a
    /// request that never connected (DNS, refused, TLS) is sent again. A 5xx, a timeout or a connection dropped after
    /// sending throws <see cref="GraphDeliveryUnknownException"/>: it may have been applied, so check rather than
    /// resend.</summary>
    public static GraphRetryPolicy Write { get; } = new(Graph.Retries, new HashSet<int> { 429 }, false);

    /// <summary>The policy a call gets when the caller names none: <see cref="Read"/> for GET, HEAD, OPTIONS, PUT
    /// and DELETE (a repeated DELETE that finds the item gone counts as done), <see cref="Write"/> for the rest.</summary>
    public static GraphRetryPolicy For(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options
               || method == HttpMethod.Put || method == HttpMethod.Delete
            ? Read
            : Write;
    }
}

/// <summary>Graph's reply to a raw call, whatever its status: the body's bytes exactly as served (never
/// decompressed or decoded), and its Content-Type as sent.</summary>
public sealed record GraphRawResponse(HttpStatusCode Status, byte[] Body, string? ContentType)
{
    public bool IsSuccess => (int)Status is >= 200 and <= 299;
}

/// <summary>A request that mustn't be repeated was sent, and the answer was a 5xx or never came (a timeout or a
/// dropped connection), so it may or may not have been applied. It wasn't sent again: check, don't resend.</summary>
public sealed class GraphDeliveryUnknownException : Exception
{
    /// <summary>No answer came back after sending.</summary>
    public GraphDeliveryUnknownException(string method, string url, Exception inner)
        : base($"No answer to {method} {GraphRequestException.Shorten(url, 80)} ({inner.GetType().Name}: {inner.Message}); "
               + "not sent again, because it may have been applied.", inner)
    {
        Method = method;
        Url = url;
    }

    /// <summary>Graph answered with a 5xx, which doesn't say whether the write was applied.</summary>
    public GraphDeliveryUnknownException(string method, string url, HttpStatusCode status, string body)
        : base($"Graph answered {(int)status} {status} to {method} {GraphRequestException.Shorten(url, 80)}; "
               + $"not sent again, because it may have been applied: {GraphRequestException.Shorten(body, 300)}")
    {
        Method = method;
        Url = url;
        Status = status;
        Body = body;
    }

    public string Method { get; }

    /// <summary>The url as the caller gave it (relative or full).</summary>
    public string Url { get; }

    /// <summary>The 5xx Graph answered with; null when no answer came.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>The 5xx's body; null when no answer came.</summary>
    public string? Body { get; }
}
