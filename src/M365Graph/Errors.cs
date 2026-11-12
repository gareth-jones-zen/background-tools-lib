using System.Net;

namespace M365Graph;

/// <summary>No usable sign-in for these scopes, and none can be done now (nobody at the console, or interactive
/// sign-in switched off). The message says how to sign in again; a tool can notify someone with it.</summary>
public sealed class SignInNeededException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Graph answered with an error status that wasn't worth retrying, or kept failing.</summary>
public sealed class GraphRequestException(HttpStatusCode status, string method, string url, string body)
    : Exception($"Graph {(int)status} {status} for {method} {Shorten(url)}: {Shorten(body, 300)}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;

    internal static string Shorten(string text, int length = 120) => text.Length <= length ? text : text[..length];
}
