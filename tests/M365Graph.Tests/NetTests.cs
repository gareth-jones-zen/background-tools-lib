using System.Net;
using System.Net.Sockets;
using Microsoft.Identity.Client;

namespace M365Graph.Tests;

public class NetTests
{
    [Fact]
    public void Ipv4_addresses_come_first_each_family_in_dns_order()
    {
        IPAddress[] dns = [IPAddress.Parse("2603:1026::1"), IPAddress.Parse("20.190.1.1"), IPAddress.Parse("2603:1026::2"), IPAddress.Parse("40.126.1.1")];
        Assert.Equal(["20.190.1.1", "40.126.1.1", "2603:1026::1", "2603:1026::2"], Net.Ipv4First(dns).Select(a => a.ToString()));
    }

    /// <summary>A server on IPv4 loopback only: "localhost" resolves to ::1 as well, and the handler still gets through.</summary>
    [Fact]
    public async Task The_handler_connects_over_ipv4()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
            var reply = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
            await stream.WriteAsync(reply, TestContext.Current.CancellationToken);
            return ((IPEndPoint)client.Client.RemoteEndPoint!).AddressFamily;
        }, TestContext.Current.CancellationToken);

        using var http = Net.CreateHttpClient();
        var body = await http.GetStringAsync($"http://localhost:{port}/", TestContext.Current.CancellationToken);

        Assert.Equal("ok", body);
        Assert.Equal(AddressFamily.InterNetwork, await server);
    }

    [Fact]
    public void Dropped_and_stalled_connections_count_as_network_errors()
    {
        Assert.True(Net.IsNetworkError(new HttpRequestException("reset")));
        Assert.True(Net.IsNetworkError(new IOException("forcibly closed")));
        Assert.True(Net.IsNetworkError(new SocketException(10054)));
        Assert.True(Net.IsNetworkError(new TaskCanceledException("timeout")));
        Assert.True(Net.IsNetworkError(new MsalServiceException(MsalError.RequestTimeout, "timed out")));
        Assert.True(Net.IsNetworkError(new MsalServiceException("other", "wrapped", new HttpRequestException("reset"))));
    }

    [Fact]
    public void Answers_and_the_callers_own_cancellation_do_not()
    {
        Assert.False(Net.IsNetworkError(new HttpRequestException("404", null, HttpStatusCode.NotFound)));
        Assert.False(Net.IsNetworkError(new MsalServiceException("invalid_grant", "expired")));
        Assert.False(Net.IsNetworkError(new InvalidOperationException()));
        Assert.False(Net.IsNetworkError(new TaskCanceledException(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public void The_constants_match_python()
    {
        Assert.Equal("https://graph.microsoft.com/v1.0", Graph.BaseUrl);
        Assert.Equal("14d82eec-204b-4c2f-b7e8-296a70dab67e", Graph.CliClientId);
        Assert.Equal(TimeSpan.FromSeconds(60), Graph.Timeout);
        Assert.Equal(3, Graph.Retries);
        Assert.EndsWith(Path.Combine("m365-graph", "msal-cache.json"), Graph.DefaultCachePath);
        Assert.Equal(["User.Read"], new GraphClientOptions().Scopes);
        Assert.Equal("organizations", new GraphClientOptions().Tenant);
    }

    [Fact]
    public void Unattended_can_be_asked_without_throwing()
    {
        _ = Unattended.Now();
    }
}
