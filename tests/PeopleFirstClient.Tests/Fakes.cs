using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EdgeDevTools;

namespace PeopleFirstClient.Tests;

/// <summary>Python's FakeContext: GETs answered from path prefixes (the path after /api/v1), 404 otherwise.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<string, string>> answers;

    public FakeHttp(Dictionary<string, Func<string, string>> answers) => this.answers = answers;

    public List<(string Path, HttpMethod Method, Dictionary<string, string> Headers)> Calls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.OriginalString;
        var path = url[(url.IndexOf("/api/v1", StringComparison.Ordinal) + "/api/v1".Length)..];
        Calls.Add((path, request.Method, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
        foreach (var (prefix, body) in answers)
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(path), Encoding.UTF8, "application/json") });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    public static Func<string, string> Body(string json) => _ => json;
}

/// <summary>What a fake tab is showing, as the sign-in probe would report it.</summary>
public sealed record FakePage(string Href, string Title = "", (double, double)? Button = null, (double, double)? Tile = null,
    bool Password = false, bool Username = false, bool Code = false, bool Approve = false, bool Hello = false, bool Picker = false);

/// <summary>
/// A browser with no browser: it answers the DevTools commands EdgeBrowser and the sign-in send, keeps a page per
/// session, answers the sign-in probe from that page, and lets each scenario react to navigations and clicks.
/// It records every command, so tests can check nothing was typed and no cookie or storage was touched.
/// </summary>
internal sealed class FakeBrowser : ICdpTransport
{
    public const string PeopleFirst = "https://example.people-first.com";
    public const string MicrosoftLogin = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=x";
    public static readonly (double, double) ButtonAt = (100, 200);
    public static readonly (double, double) TileAt = (300, 400);

    private readonly Channel<byte[]?> inbox = Channel.CreateUnbounded<byte[]?>();
    private readonly Dictionary<string, FakePage> pages = [];
    private int targets;
    private int requests;

    public List<string> Methods { get; } = [];

    /// <summary>Called after a page navigates (session, url); may change pages, open others, send the token.</summary>
    public Action<FakeBrowser, string, string>? OnNavigate { get; set; }

    /// <summary>Called on a click (session, x, y).</summary>
    public Action<FakeBrowser, string, double, double>? OnClick { get; set; }

    /// <summary>Called on every probe of a page (session), before it's answered.</summary>
    public Action<FakeBrowser, string>? OnProbe { get; set; }

    public int Probes { get; private set; }

    public int ProcessId => 0;

    public bool Closed { get; private set; }

    public void Start(Action<byte[]?> onMessage) =>
        _ = Task.Run(async () =>
        {
            await foreach (var message in inbox.Reader.ReadAllAsync())
            {
                onMessage(message);
                if (message is null)
                    return;
            }
        });

    public void Show(string session, FakePage page)
    {
        lock (pages)
            pages[session] = page;
    }

    /// <summary>A new tab (a pop-up), attached as Edge would attach it.</summary>
    public string Open(FakePage page)
    {
        var n = Interlocked.Increment(ref targets);
        var session = $"S{n}";
        Show(session, page);
        Event("Target.attachedToTarget",
            $"{{\"sessionId\":\"{session}\",\"targetInfo\":{{\"targetId\":\"T{n}\",\"type\":\"page\",\"url\":{Js(page.Href)}}},\"waitingForDebugger\":true}}");
        return session;
    }

    /// <summary>The web app calling its own API with the token, as the request and its extra-info event.</summary>
    public void SendToken(string session, string token = "Bearer secret-token")
    {
        var id = $"R{Interlocked.Increment(ref requests)}";
        Event("Network.requestWillBeSent", $"{{\"requestId\":\"{id}\",\"request\":{{\"url\":\"{PeopleFirst}/api/v1/hrm/app/profile\",\"headers\":{{}}}}}}", session);
        Event("Network.requestWillBeSentExtraInfo", $"{{\"requestId\":\"{id}\",\"headers\":{{\"authorization\":{Js(token)}}}}}", session);
    }

    public Task SendAsync(byte[] message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt32();
        var method = root.GetProperty("method").GetString()!;
        var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        var p = root.GetProperty("params");
        lock (Methods)
            Methods.Add(method);
        string result;
        Action? after = null;
        switch (method)
        {
            case "Browser.getVersion":
                result = "{\"product\":\"Edg/999.0.0.0\"}";
                break;
            case "Target.createTarget":
            {
                var n = Interlocked.Increment(ref targets);
                Show($"S{n}", new FakePage("about:blank"));
                Event("Target.attachedToTarget",
                    $"{{\"sessionId\":\"S{n}\",\"targetInfo\":{{\"targetId\":\"T{n}\",\"type\":\"page\",\"url\":\"about:blank\"}},\"waitingForDebugger\":true}}");
                result = $"{{\"targetId\":\"T{n}\"}}";
                break;
            }
            case "Page.navigate":
            {
                var url = p.GetProperty("url").GetString()!;
                Show(session!, new FakePage(url));
                result = "{\"frameId\":\"F\"}";
                after = () => OnNavigate?.Invoke(this, session!, url);
                break;
            }
            case "Runtime.evaluate":
            {
                var expression = p.GetProperty("expression").GetString()!;
                if (!expression.StartsWith(MicrosoftSignIn.ProbeMarker, StringComparison.Ordinal))
                {
                    result = "{\"result\":{\"type\":\"undefined\"}}";
                    break;
                }
                Probes++;
                OnProbe?.Invoke(this, session!);
                FakePage page;
                lock (pages)
                    page = pages[session!];
                result = $"{{\"result\":{{\"type\":\"object\",\"value\":{ProbeValue(page)}}}}}";
                break;
            }
            case "Input.dispatchMouseEvent":
            {
                result = "{}";
                if (p.GetProperty("type").GetString() == "mouseReleased")
                {
                    var (x, y) = (p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble());
                    after = () => OnClick?.Invoke(this, session!, x, y);
                }
                break;
            }
            case "Browser.close":
                Push($"{{\"id\":{id},\"result\":{{}}}}");
                Closed = true;
                inbox.Writer.TryWrite(null);
                return Task.CompletedTask;
            default:
                result = "{}";
                break;
        }
        Push($"{{\"id\":{id}{(session is null ? "" : $",\"sessionId\":\"{session}\"")},\"result\":{result}}}");
        after?.Invoke();
        return Task.CompletedTask;
    }

    private static string ProbeValue(FakePage page)
    {
        static string Point((double, double)? at) => at is { } p ? $"[{p.Item1},{p.Item2}]" : "null";
        static string Flag(bool value) => value ? "true" : "false";
        return $"{{\"href\":{Js(page.Href)},\"title\":{Js(page.Title)},\"button\":{Point(page.Button)},\"tile\":{Point(page.Tile)}," +
               $"\"password\":{Flag(page.Password)},\"username\":{Flag(page.Username)},\"code\":{Flag(page.Code)}," +
               $"\"approve\":{Flag(page.Approve)},\"hello\":{Flag(page.Hello)},\"picker\":{Flag(page.Picker)}}}";
    }

    private void Event(string method, string paramsJson, string? session = null) =>
        Push($"{{\"method\":\"{method}\",\"params\":{paramsJson}{(session is null ? "" : $",\"sessionId\":\"{session}\"")}}}");

    private void Push(string json) => inbox.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

    private static string Js(string value) => PageScripts.JsonString(value);

    public bool WaitForExit(TimeSpan timeout) => true;

    public void Kill() => inbox.Writer.TryWrite(null);

    public ValueTask DisposeAsync()
    {
        inbox.Writer.TryWrite(null);
        return ValueTask.CompletedTask;
    }

    public static bool At(double x, double y, (double, double) target) => x == target.Item1 && y == target.Item2;
}
