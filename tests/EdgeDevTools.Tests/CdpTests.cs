using System.Text.Json;

namespace EdgeDevTools.Tests;

/// <summary>The protocol client and the browser's bookkeeping, against a fake transport.</summary>
public class CdpTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Commands_get_their_replies_and_errors()
    {
        var transport = new FakeTransport
        {
            Answer = (method, _, p) => method switch
            {
                "Echo" => $"{{\"got\":{p.GetProperty("x").GetInt32()}}}",
                "Bad" => "!No such thing",
                _ => null,
            },
        };
        await using var cdp = new CdpConnection(transport);
        cdp.Start();
        var reply = await cdp.SendAsync("Echo", w => w.WriteNumber("x", 7), cancellationToken: Cancel);
        Assert.Equal(7, reply.GetProperty("got").GetInt32());
        var error = await Assert.ThrowsAsync<CdpException>(() => cdp.SendAsync("Bad", cancellationToken: Cancel));
        Assert.Equal("No such thing", error.Message);
        await Assert.ThrowsAsync<TimeoutException>(() => cdp.SendAsync("Silent", timeout: TimeSpan.FromMilliseconds(100), cancellationToken: Cancel));
    }

    [Fact]
    public async Task A_closed_connection_fails_what_is_waiting_and_what_comes_after()
    {
        var transport = new FakeTransport { Answer = (_, _, _) => null };
        await using var cdp = new CdpConnection(transport);
        cdp.Start();
        var waiting = cdp.SendAsync("Never", cancellationToken: Cancel);
        transport.Close();
        await Assert.ThrowsAsync<CdpException>(() => waiting);
        await cdp.Closed.WaitAsync(TimeSpan.FromSeconds(5), Cancel);
        await Assert.ThrowsAsync<CdpException>(() => cdp.SendAsync("After", cancellationToken: Cancel));
    }

    [Fact]
    public async Task Events_reach_handlers_and_a_failing_handler_harms_nothing()
    {
        var transport = new FakeTransport();
        await using var cdp = new CdpConnection(transport);
        var seen = new TaskCompletionSource<CdpEvent>();
        var failures = 0;
        cdp.Event += _ => throw new InvalidOperationException("handler bug");
        cdp.Event += e => seen.TrySetResult(e);
        cdp.HandlerError = _ => Interlocked.Increment(ref failures);
        cdp.Start();
        transport.Push("not json at all");
        transport.Event("Page.loadEventFired", "{\"timestamp\":1}", "S1");
        var e = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancel);
        Assert.Equal("Page.loadEventFired", e.Method);
        Assert.Equal("S1", e.SessionId);
        Assert.Equal(1, e.Params.GetProperty("timestamp").GetInt32());
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task Evaluate_returns_the_value_and_reports_a_script_error()
    {
        var transport = new FakeTransport
        {
            Answer = (_, _, p) => p.GetProperty("expression").GetString() == "boom"
                ? "{\"result\":{\"type\":\"object\"},\"exceptionDetails\":{\"text\":\"Uncaught\"}}"
                : "{\"result\":{\"type\":\"string\",\"value\":\"hello\"}}",
        };
        await using var cdp = new CdpConnection(transport);
        cdp.Start();
        Assert.Equal("hello", (await cdp.EvaluateAsync("S1", "document.title", cancellationToken: Cancel)).GetString());
        var sent = transport.Sent.Single();
        Assert.Equal(("Runtime.evaluate", "S1"), (sent.Method, sent.Session));
        Assert.True(sent.Params.GetProperty("returnByValue").GetBoolean());
        await Assert.ThrowsAsync<CdpException>(() => cdp.EvaluateAsync("S1", "boom", cancellationToken: Cancel));
    }

    [Fact]
    public void Messages_are_flat_with_a_session()
    {
        var bytes = CdpConnection.Message(4, "Page.navigate", w => w.WriteString("url", "https://example.com/\"q\""), "S9");
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(4, document.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("S9", document.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("https://example.com/\"q\"", document.RootElement.GetProperty("params").GetProperty("url").GetString());
    }

    [Fact]
    public async Task The_browser_attaches_pages_lets_them_run_and_merges_request_headers()
    {
        var transport = new FakeTransport();
        transport.Answer = (method, session, _) =>
        {
            if (method == "Browser.getVersion")
                return "{\"product\":\"Edg/999.0\"}";
            if (method == "Target.createTarget")
            {
                transport.Event("Target.attachedToTarget",
                    "{\"sessionId\":\"S1\",\"targetInfo\":{\"targetId\":\"T1\",\"type\":\"page\",\"url\":\"about:blank\"},\"waitingForDebugger\":true}");
                return "{\"targetId\":\"T1\"}";
            }
            return "{}";
        };
        var requests = new List<EdgeRequest>();
        await using var browser = await EdgeBrowser.LaunchAsync(new EdgeLaunchOptions
        {
            UserDataDir = Path.GetTempPath(),
            TransportFactory = (_, arguments) =>
            {
                Assert.Contains("--remote-debugging-pipe", arguments);
                Assert.Contains("--headless=new", arguments);
                return transport;
            },
        }, Cancel);
        browser.RequestSent += r =>
        {
            lock (requests)
                requests.Add(r);
        };
        Assert.Equal("Edg/999.0", browser.Version);
        var page = await browser.NewPageAsync("https://example.com/", cancellationToken: Cancel);
        Assert.Equal("S1", page.SessionId);
        Assert.Single(browser.Pages);
        lock (transport.Sent)
        {
            var forPage = transport.Sent.Where(s => s.Session == "S1").Select(s => s.Method).ToList();
            Assert.Equal(["Network.enable", "Target.setAutoAttach", "Runtime.runIfWaitingForDebugger", "Page.navigate"], forPage);
        }

        // The extra-info event can come first; either way the request is seen with every header.
        transport.Event("Network.requestWillBeSentExtraInfo", "{\"requestId\":\"R1\",\"headers\":{\"Authorization\":\"Bearer t\"}}", "S1");
        transport.Event("Network.requestWillBeSent", "{\"requestId\":\"R1\",\"request\":{\"url\":\"https://example.com/api\",\"headers\":{\"Accept\":\"x\"}}}", "S1");
        transport.Event("Network.requestWillBeSent", "{\"requestId\":\"R2\",\"request\":{\"url\":\"https://example.com/b\",\"headers\":{}}}", "S1");
        transport.Event("Network.requestWillBeSentExtraInfo", "{\"requestId\":\"R2\",\"headers\":{\"authorization\":\"Bearer u\"}}", "S1");
        for (var i = 0; i < 100 && Count() < 3; i++)
            await Task.Delay(20, Cancel);
        lock (requests)
        {
            Assert.Equal(3, requests.Count);
            Assert.Equal("Bearer t", requests[0].Header("authorization"));
            Assert.Equal("x", requests[0].Header("ACCEPT"));
            Assert.Null(requests[1].Header("authorization"));
            Assert.Equal("Bearer u", requests[2].Header("Authorization"));
        }

        transport.Event("Target.detachedFromTarget", "{\"sessionId\":\"S1\"}");
        for (var i = 0; i < 100 && browser.Pages.Count > 0; i++)
            await Task.Delay(20, Cancel);
        Assert.Empty(browser.Pages);
        await browser.CloseAsync();
        Assert.Contains(transport.Sent, s => s.Method == "Browser.close");

        int Count()
        {
            lock (requests)
                return requests.Count;
        }
    }

    [Fact]
    public async Task A_browser_that_closes_at_once_is_a_launch_failure()
    {
        var transport = new FakeTransport { Answer = (_, _, _) => null };
        transport.Close();
        var error = await Assert.ThrowsAsync<EdgeLaunchException>(() => EdgeBrowser.LaunchAsync(new EdgeLaunchOptions
        {
            UserDataDir = Path.GetTempPath(),
            TransportFactory = (_, _) => transport,
        }, Cancel));
        Assert.Contains("another Edge", error.Message);
    }

    [Theory]
    [InlineData(new[] { "--a", "b c", "" }, "x.exe --a \"b c\" \"\"")]
    [InlineData(new[] { "--user-data-dir=C:\\Users\\O Brien\\p\\" }, "x.exe \"--user-data-dir=C:\\Users\\O Brien\\p\\\\\"")]
    [InlineData(new[] { "say \"hi\"" }, "x.exe \"say \\\"hi\\\"\"")]
    public void Command_lines_split_back_into_the_same_arguments(string[] arguments, string expected) =>
        Assert.Equal(expected, PipeTransport.CommandLine("x.exe", arguments));
}
