using System.Net;
using System.Text;

namespace M365Graph.Tests;

/// <summary>A folder of its own for each test, removed afterwards.</summary>
public abstract class Temp : IDisposable
{
    protected string Folder { get; } = Path.Combine(Path.GetTempPath(), "m365graph-tests-" + Guid.NewGuid().ToString("N"));

    protected Temp() => Directory.CreateDirectory(Folder);

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}

/// <summary>MSAL stood in for: counts calls and returns what the test queues.</summary>
internal sealed class FakeMsal : IMsalApp
{
    public Queue<Func<AccessToken?>> Silent { get; } = new();
    public Func<AccessToken>? Interactive { get; set; }
    public int SilentCalls { get; private set; }
    public List<(IReadOnlyList<string> Scopes, string? Hint)> InteractiveCalls { get; } = [];

    public Task<AccessToken?> SilentAsync(IReadOnlyList<string> scopes, CancellationToken cancel)
    {
        SilentCalls++;
        return Task.FromResult(Silent.Count > 0 ? Silent.Dequeue()() : null);
    }

    public Task<AccessToken> InteractiveAsync(IReadOnlyList<string> scopes, string? loginHint, CancellationToken cancel)
    {
        InteractiveCalls.Add((scopes, loginHint));
        return Task.FromResult((Interactive ?? throw new InvalidOperationException("no browser in tests"))());
    }
}

/// <summary>An HTTP server stood in for: each request gets the next queued answer, and is recorded.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Answers { get; } = new();
    public List<(HttpMethod Method, string Url, string? Authorization, string? Body, string? ContentType, HttpRequestMessage Request)> Seen { get; } = [];

    public void Reply(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? tweak = null) =>
        Answers.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            tweak?.Invoke(response);
            return response;
        });

    public void Fail(Exception error) => Answers.Enqueue(_ => throw error);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancel);
        Seen.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body,
            request.Content?.Headers.ContentType?.MediaType, request));
        if (Answers.Count == 0)
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        return Answers.Dequeue()(request);
    }
}

/// <summary>A handler that fails any request, so a test can be sure nothing reached the network.</summary>
internal sealed class NoNetwork : HttpMessageHandler
{
    public int Attempts { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        Attempts++;
        throw new InvalidOperationException($"No network in tests: {request.RequestUri}");
    }
}

internal static class Fake
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public static AccessToken Token(string value, double minutes = 60) => new(value, Now.AddMinutes(minutes));

    /// <summary>A sign-in over a fake MSAL, at a fixed time, recording waits instead of waiting.</summary>
    public static SignIn SignIn(FakeMsal msal, List<TimeSpan>? waits = null, bool unattended = true, Func<DateTimeOffset>? clock = null) =>
        new(() => msal, clock ?? (() => Now), () => unattended, (wait, _) => { waits?.Add(wait); return Task.CompletedTask; });

    public static GraphClient Client(FakeHandler handler, FakeMsal? msal = null, List<TimeSpan>? waits = null, List<string>? log = null)
    {
        msal ??= new FakeMsal();
        if (msal.Silent.Count == 0)
            for (var i = 0; i < 20; i++)
                msal.Silent.Enqueue(() => Token("tok"));
        var options = new GraphClientOptions { Log = text => log?.Add(text) };
        return new GraphClient(options, new HttpClient(handler), SignIn(msal, waits),
            (wait, _) => { waits?.Add(wait); return Task.CompletedTask; });
    }
}
