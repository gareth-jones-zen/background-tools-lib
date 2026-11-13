using System.Net;

namespace M365Graph;

/// <summary>How <see cref="GraphClient.SendRawAsync"/> may retry one call: how many tries in all, which statuses
/// are worth another try, and whether a request that may have reached Graph (a timeout or a dropped connection
/// after it was sent) can be sent again.
/// <para>A GET can always be sent again (<see cref="Read"/>). A PATCH or POST that might have been applied
/// mustn't be: a retried insert can put content in twice, and a retried create can make two pages
/// (<see cref="Write"/>, which retries only a 429, refused before it was looked at, and a request that never
/// connected).</para></summary>
public sealed record GraphRetryPolicy(int Tries, IReadOnlySet<int> Statuses, bool ResendAfterDelivery)
{
    /// <summary>For a call that's safe to repeat: 429, 500, 502, 503 and 504 retried, and a dropped connection
    /// too, up to <see cref="Graph.Retries"/> tries.</summary>
    public static GraphRetryPolicy Read { get; } = new(Graph.Retries, new HashSet<int> { 429, 500, 502, 503, 504 }, true);

    /// <summary>For a call that mustn't be applied twice: only a 429, or a request that never connected, is sent
    /// again. A 5xx comes back as the reply; a timeout or a dropped connection after sending is
    /// <see cref="GraphDeliveryUnknownException"/>.</summary>
    public static GraphRetryPolicy Write { get; } = new(Graph.Retries, new HashSet<int> { 429 }, false);
}

/// <summary>Graph's reply to a raw call, whatever its status: the body's bytes exactly as served (never
/// decompressed or decoded), and its Content-Type as sent.</summary>
public sealed record GraphRawResponse(HttpStatusCode Status, byte[] Body, string? ContentType)
{
    public bool IsSuccess => (int)Status is >= 200 and <= 299;
}

/// <summary>A request that mustn't be repeated was sent, but no answer came back (a timeout or a dropped
/// connection), so it may or may not have been applied. It wasn't sent again.</summary>
public sealed class GraphDeliveryUnknownException(string method, string url, Exception inner)
    : Exception($"No answer to {method} {GraphRequestException.Shorten(url, 80)} ({inner.GetType().Name}: {inner.Message}); "
                + "not sent again, because it may have been applied.", inner)
{
    public string Method { get; } = method;

    /// <summary>The url as the caller gave it (relative or full).</summary>
    public string Url { get; } = url;
}
