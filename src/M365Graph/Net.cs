using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Identity.Client;

namespace M365Graph;

/// <summary>Connections that prefer IPv4, and how to tell a dropped connection from a real answer.
///
/// login.microsoftonline.com lists eight IPv6 addresses before its IPv4 ones. On a network where IPv6 to Microsoft
/// goes nowhere, trying them in DNS order stalled every sign-in for minutes before reaching IPv4 (22 Oct 2026: a
/// fetch hung for over 25 minutes in tenant discovery). Browsers and curl race the two families, which is why
/// nothing else showed it. Sorting IPv4 first keeps IPv6 as the fallback, so an IPv6-only host still works.
/// Python patches socket.getaddrinfo for the whole process; here it's the handler's ConnectCallback, so it applies
/// to Graph calls and to MSAL's sign-in calls (both use <see cref="CreateHandler"/>), and to nothing else.</summary>
public static class Net
{
    /// <summary>The longest one address may take to connect before the next is tried. Python waits the full timeout
    /// on each; with IPv4 first that only matters when an IPv4 address is dead too.</summary>
    public static readonly TimeSpan PerAddress = TimeSpan.FromSeconds(20);

    /// <summary>A handler whose connections try IPv4 addresses first, then IPv6. decompress false asks for no
    /// compression (no Accept-Encoding), as Python's urllib didn't, so a reply's bytes arrive exactly as served.</summary>
    public static SocketsHttpHandler CreateHandler(bool decompress = true) => new()
    {
        ConnectCallback = ConnectIpv4FirstAsync,
        ConnectTimeout = Graph.Timeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None,
    };

    /// <summary>An HttpClient over <see cref="CreateHandler"/>, with the 60-second timeout.</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? CreateHandler(), disposeHandler: true) { Timeout = Graph.Timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("background-tools-lib-m365graph", "0.1"));
        return client;
    }

    /// <summary>IPv4 addresses first, each family keeping DNS's own order (a stable sort, as Python's sorted()).</summary>
    public static IReadOnlyList<IPAddress> Ipv4First(IEnumerable<IPAddress> addresses) =>
        addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();

    private static async ValueTask<Stream> ConnectIpv4FirstAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var endpoint = context.DnsEndPoint;
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(endpoint.Host, out var literal))
            addresses = [literal];
        else
        {
            try
            {
                addresses = Ipv4First(await Dns.GetHostAddressesAsync(endpoint.Host, cancel).ConfigureAwait(false));
            }
            catch (SocketException error)
            {
                throw new HttpRequestException(HttpRequestError.NameResolutionError, $"Couldn't find {endpoint.Host}: {error.Message}", error);
            }
        }
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var oneAddress = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                oneAddress.CancelAfter(PerAddress);
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
            $"Couldn't connect to {endpoint.Host}:{endpoint.Port} on any of its {addresses.Count} addresses.", last);
    }

    /// <summary>Whether a failed request certainly never reached the server: the name didn't resolve, no connection
    /// opened, or TLS failed before anything was sent. Such a request is safe to send again even when it isn't
    /// idempotent (a PATCH or POST). Read from HttpRequestException.HttpRequestError, which the IPv4-first connect
    /// sets too (NameResolutionError, ConnectionError), so callers needn't match message text.</summary>
    public static bool NeverSent(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError })
                return true;
        return false;
    }

    /// <summary>A dropped, reset or stalled connection, rather than an answer: worth another try. MSAL wraps
    /// transport failures in MsalServiceException ("request_timeout", "service_not_available"), so the inner
    /// exceptions are checked too.</summary>
    public static bool IsNetworkError(Exception error, CancellationToken cancel = default)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case OperationCanceledException when cancel.IsCancellationRequested:
                    return false;
                case HttpRequestException { StatusCode: null }:
                case IOException:
                case SocketException:
                case TimeoutException:
                case OperationCanceledException:
                    return true;
                case MsalServiceException { ErrorCode: MsalError.RequestTimeout or MsalError.ServiceNotAvailable }:
                    return true;
            }
        }
        return false;
    }
}
