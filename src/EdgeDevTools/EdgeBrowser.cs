using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace EdgeDevTools;

/// <summary>Edge couldn't be started, or closed before it answered: usually another Edge has the profile open.</summary>
public sealed class EdgeLaunchException : Exception
{
    public EdgeLaunchException(string message) : base(message)
    {
    }

    public EdgeLaunchException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>How to start Edge.</summary>
public sealed class EdgeLaunchOptions
{
    /// <summary>The profile folder (<c>--user-data-dir</c>). Created if missing. Edge won't share a profile with
    /// another Edge, so tools sharing one must take turns (see <see cref="EdgeProfile"/>).</summary>
    public required string UserDataDir { get; init; }

    /// <summary>No window (<c>--headless=new</c>). False shows a normal window, for a person to sign in.</summary>
    public bool Headless { get; init; } = true;

    /// <summary>msedge.exe; found with <see cref="EdgeBrowser.FindExecutable"/> when null.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>More command-line switches, after the standard ones.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>For tests: makes the transport instead of starting Edge. Given the executable and arguments.</summary>
    public Func<string, IReadOnlyList<string>, ICdpTransport>? TransportFactory { get; init; }

    /// <summary>How long Edge has to answer its first command.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>A page, frame or worker the browser has attached to, with its own DevTools session.</summary>
public sealed class EdgeTarget
{
    internal EdgeTarget(string sessionId, string targetId, string type, string url)
    {
        SessionId = sessionId;
        TargetId = targetId;
        Type = type;
        Url = url;
    }

    public string SessionId { get; }
    public string TargetId { get; }

    /// <summary>"page", "iframe", "worker", "service_worker" and so on.</summary>
    public string Type { get; }

    /// <summary>Its address when it attached. Pages move on: ask the page (location.href) for where it is now.</summary>
    public string Url { get; }

    /// <summary>Completes once the target has network events on and has been let run.</summary>
    public Task Ready { get; internal set; } = Task.CompletedTask;
}

/// <summary>A request a page is about to send, with its headers. Raised when the request is seen, and again when
/// Chromium reports the headers it really sent (<c>requestWillBeSentExtraInfo</c>, which has the ones the page's
/// own headers leave out), merged, so a header is seen whichever event carries it.</summary>
public sealed record EdgeRequest(string RequestId, string Url, IReadOnlyDictionary<string, string> Headers, string? SessionId)
{
    /// <summary>A header's value, ignoring the name's case; null if it wasn't sent.</summary>
    public string? Header(string name)
    {
        foreach (var (key, value) in Headers)
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }
}

/// <summary>
/// The installed Microsoft Edge, driven over the DevTools protocol through a pipe (<c>--remote-debugging-pipe</c>).
/// <para>The browser attaches to every page, frame and worker as it starts (<c>Target.setAutoAttach</c>, with
/// each paused until it has network events on), so no request is missed, including those of a sign-in pop-up or a
/// redirect through another site. <see cref="RequestSent"/> sees each request's headers.</para>
/// <para>Edge runs in a job that ends it if this process dies. <see cref="CloseAsync"/> (or disposing) asks Edge
/// to close, waits for it, and ends it if it doesn't.</para>
/// <para>Nothing here reads or changes cookies or storage, and nothing types into a page.</para>
/// </summary>
public sealed class EdgeBrowser : IAsyncDisposable
{
    /// <summary>The switches every launch gets: a quiet Edge with no first-run pages, sync, extensions or
    /// background updates. Proven with People First's sign-in on 12 Nov 2026.
    /// <para><c>--edge-skip-compat-layer-relaunch</c> (13 Nov 2026): when the environment carries a Windows
    /// compatibility layer (<c>__COMPAT_LAYER</c>, which Windows can set on a program double-clicked in Explorer),
    /// Edge relaunches itself without it, and the first process, the one holding the DevTools pipe, exits at once:
    /// "The DevTools connection to Edge has closed" within 200 ms, hidden or visible. Playwright passes the same
    /// switch for the same reason. A double-clicked tool hit this on its first runs.</para></summary>
    public static readonly IReadOnlyList<string> StandardArguments =
    [
        "--remote-debugging-pipe",
        "--edge-skip-compat-layer-relaunch",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-component-update",
        "--disable-background-networking",
        "--disable-sync",
        "--disable-features=msEdgeSidebarV2",
    ];

    private readonly CdpConnection cdp;
    private readonly ConcurrentDictionary<string, EdgeTarget> targets = new();                          // by session
    private readonly ConcurrentDictionary<string, TaskCompletionSource<EdgeTarget>> byTargetId = new();
    private readonly ConcurrentDictionary<string, string> requestUrls = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> requestHeaders = new();
    private readonly SemaphoreSlim closing = new(1, 1);
    private bool closed;

    private EdgeBrowser(CdpConnection cdp)
    {
        this.cdp = cdp;
    }

    /// <summary>The connection, for commands this class has no method for.</summary>
    public CdpConnection Connection => cdp;

    /// <summary>Edge's product and version ("Edg/142.0..."), from Browser.getVersion.</summary>
    public string Version { get; private set; } = "";

    /// <summary>Every request as it's sent, with its headers. Called on the reader thread: keep it quick.</summary>
    public event Action<EdgeRequest>? RequestSent;

    /// <summary>Called with anything that goes wrong in the background (a handler that throws, a target that
    /// couldn't be set up). Nothing is ever thrown from there.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The attached pages (tabs and pop-ups), newest last.</summary>
    public IReadOnlyList<EdgeTarget> Pages => targets.Values.Where(t => t.Type == "page").ToList();

    /// <summary>msedge.exe: the App Paths registration (machine, then user), then the usual install folders.</summary>
    /// <exception cref="FileNotFoundException">Edge isn't installed.</exception>
    public static string FindExecutable()
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
            if (key?.GetValue(null) is string registered && File.Exists(registered.Trim('"')))
                return registered.Trim('"');
        }
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (folder.Length == 0)
                continue;
            var path = Path.Combine(folder, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException("Microsoft Edge isn't installed (no msedge.exe registered or in Program Files).");
    }

    /// <summary>The command line for these options, after the executable.</summary>
    public static IReadOnlyList<string> Arguments(EdgeLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var arguments = new List<string> { "--user-data-dir=" + Path.GetFullPath(options.UserDataDir) };
        arguments.AddRange(StandardArguments);
        if (options.Headless)
            arguments.Add("--headless=new");
        arguments.AddRange(options.ExtraArguments);
        arguments.Add("about:blank");
        return arguments;
    }

    /// <summary>Start Edge on the profile and attach to it. Throws EdgeLaunchException if Edge can't start or
    /// closes straight away (another Edge has the profile).</summary>
    public static async Task<EdgeBrowser> LaunchAsync(EdgeLaunchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.UserDataDir);
        var arguments = Arguments(options);
        ICdpTransport transport;
        if (options.TransportFactory is { } factory)
        {
            transport = factory(options.ExecutablePath ?? "msedge.exe", arguments);
        }
        else
        {
            Directory.CreateDirectory(options.UserDataDir);
            if (EdgeProfile.IsOpen(options.UserDataDir))
                throw new EdgeLaunchException("The Edge profile is already open in another Edge (another run may be using it).");
            var executable = options.ExecutablePath ?? FindExecutable();
            try
            {
                transport = PipeTransport.Launch(executable, arguments, showWindow: !options.Headless);
            }
            catch (InvalidOperationException e)
            {
                throw new EdgeLaunchException(e.Message, e);
            }
        }
        var browser = new EdgeBrowser(new CdpConnection(transport));
        try
        {
            await browser.StartAsync(options.StartTimeout, cancellationToken).ConfigureAwait(false);
            return browser;
        }
        catch
        {
            await browser.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartAsync(TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        cdp.HandlerError = e => Log?.Invoke($"edge: an event handler failed: {e.GetType().Name}: {e.Message}");
        cdp.Event += OnEvent;
        cdp.Start();
        JsonElement version;
        try
        {
            version = await cdp.SendAsync("Browser.getVersion", timeout: startTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CdpException or TimeoutException)
        {
            throw new EdgeLaunchException("Edge didn't answer after starting (is another Edge using its profile?): " + e.Message, e);
        }
        Version = version.ValueKind == JsonValueKind.Object && version.TryGetProperty("product", out var product)
            ? product.GetString() ?? "" : "";
        await cdp.SendAsync("Target.setAutoAttach", AutoAttach, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void AutoAttach(Utf8JsonWriter w)
    {
        w.WriteBoolean("autoAttach", true);
        w.WriteBoolean("waitForDebuggerOnStart", true);
        w.WriteBoolean("flatten", true);
    }

    private void OnEvent(CdpEvent e)
    {
        var p = e.Params;
        switch (e.Method)
        {
            case "Target.attachedToTarget":
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("targetInfo", out var info))
                    return;
                var target = new EdgeTarget(Text(p, "sessionId"), Text(info, "targetId"), Text(info, "type"), Text(info, "url"));
                if (target.SessionId.Length == 0)
                    return;
                target.Ready = Task.Run(() => SetUpAsync(target));
                targets[target.SessionId] = target;
                byTargetId.GetOrAdd(target.TargetId, _ => NewWaiter()).TrySetResult(target);
                break;
            }
            case "Target.detachedFromTarget":
                if (p.ValueKind == JsonValueKind.Object)
                    targets.TryRemove(Text(p, "sessionId"), out _);
                break;
            case "Network.requestWillBeSent":
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("request", out var request))
                    return;
                var id = Text(p, "requestId");
                var url = Text(request, "url");
                requestUrls[id] = url;
                var headers = Headers(request.TryGetProperty("headers", out var h) ? h : default);
                if (requestHeaders.TryGetValue(id, out var extra))
                    lock (extra)
                        foreach (var (name, value) in extra)
                            headers[name] = value;
                Raise(new EdgeRequest(id, url, headers, e.SessionId));
                break;
            }
            case "Network.requestWillBeSentExtraInfo":
            {
                if (p.ValueKind != JsonValueKind.Object)
                    return;
                var id = Text(p, "requestId");
                var headers = Headers(p.TryGetProperty("headers", out var h) ? h : default);
                var merged = requestHeaders.GetOrAdd(id, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                Dictionary<string, string> copy;
                lock (merged)
                {
                    foreach (var (name, value) in headers)
                        merged[name] = value;
                    copy = new Dictionary<string, string>(merged, StringComparer.OrdinalIgnoreCase);
                }
                if (requestUrls.TryGetValue(id, out var url))
                    Raise(new EdgeRequest(id, url, copy, e.SessionId));
                break;
            }
        }
    }

    private void Raise(EdgeRequest request)
    {
        var handlers = RequestSent;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<EdgeRequest>)handler)(request);
            }
            catch (Exception e)
            {
                Log?.Invoke($"edge: a request handler failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>Network events on, children attached the same way, then let it run. It's always let run, even if
    /// a step fails, or the page would stay paused.</summary>
    private async Task SetUpAsync(EdgeTarget target)
    {
        try
        {
            if (target.Type is "page" or "iframe" or "worker" or "service_worker" or "shared_worker")
                await Quietly(cdp.SendAsync("Network.enable", sessionId: target.SessionId)).ConfigureAwait(false);
            if (target.Type is "page" or "iframe")
                await Quietly(cdp.SendAsync("Target.setAutoAttach", AutoAttach, target.SessionId)).ConfigureAwait(false);
        }
        finally
        {
            await Quietly(cdp.SendAsync("Runtime.runIfWaitingForDebugger", sessionId: target.SessionId)).ConfigureAwait(false);
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is CdpException or TimeoutException)
        {
            // A target that has gone, or doesn't take that command (a worker and setAutoAttach): nothing to do.
        }
    }

    /// <summary>Open a new tab, ready (network events on), and go to the address.</summary>
    public async Task<EdgeTarget> NewPageAsync(string url, TimeSpan? navigationTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        var created = await cdp.SendAsync("Target.createTarget", w => w.WriteString("url", "about:blank"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var targetId = Text(created, "targetId");
        var page = await byTargetId.GetOrAdd(targetId, _ => NewWaiter()).Task
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await page.Ready.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await NavigateAsync(page, url, navigationTimeout, cancellationToken).ConfigureAwait(false);
        return page;
    }

    /// <summary>Page.navigate: returns once the navigation has started (not when the page has loaded).</summary>
    public async Task NavigateAsync(EdgeTarget page, string url, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var result = await cdp.SendAsync("Page.navigate", w => w.WriteString("url", url), page.SessionId,
            timeout ?? TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("errorText", out var error)
            && error.GetString() is { Length: > 0 } text)
            throw new CdpException($"Edge couldn't load the page: {text}");
    }

    /// <summary>Run script in a page; see <see cref="CdpConnection.EvaluateAsync"/>.</summary>
    public Task<JsonElement> EvaluateAsync(EdgeTarget page, string expression, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return cdp.EvaluateAsync(page.SessionId, expression, cancellationToken: cancellationToken);
    }

    /// <summary>The centre of the deepest visible element whose text is (exact) or contains (not exact, any case)
    /// the text, like Playwright's get_by_text, scrolled into view first; null if there's none.</summary>
    public async Task<(double X, double Y)?> FindTextAsync(EdgeTarget page, string text, bool exact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        JsonElement found;
        try
        {
            found = await cdp.EvaluateAsync(page.SessionId, PageScripts.FindText(text, exact), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            return null;   // the page was between documents
        }
        return PageScripts.Point(found);
    }

    /// <summary>A left click at a point in the page.</summary>
    public Task ClickAsync(EdgeTarget page, double x, double y, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return cdp.ClickAsync(page.SessionId, x, y, cancellationToken);
    }

    /// <summary>Ask Edge to close, wait up to 15 seconds for it to exit, and end it (and everything it started)
    /// if it hasn't. Safe to call more than once.</summary>
    public async Task CloseAsync()
    {
        await closing.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed)
                return;
            closed = true;
            if (!cdp.IsClosed)
            {
                try
                {
                    await cdp.SendAsync("Browser.close", timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (Exception e) when (e is CdpException or TimeoutException)
                {
                    // It closes the connection as it goes, so a missing reply is normal.
                }
            }
            if (!cdp.Transport.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                Log?.Invoke("edge: Edge didn't close within 15 s, so it was ended");
                cdp.Transport.Kill();
                cdp.Transport.WaitForExit(TimeSpan.FromSeconds(10));
            }
            await cdp.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            closing.Release();
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private static TaskCompletionSource<EdgeTarget> NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";

    private static Dictionary<string, string> Headers(JsonElement headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var header in headers.EnumerateObject())
            if (header.Value.ValueKind == JsonValueKind.String)
                result[header.Name] = header.Value.GetString()!;
        return result;
    }
}

/// <summary>Scripts run in pages. Values are put in as JSON string literals, never pasted in raw.</summary>
public static class PageScripts
{
    /// <summary>The finder used by <see cref="EdgeBrowser.FindTextAsync"/>, as a function expression <c>find(text, exact)</c>.</summary>
    internal const string FindFunction = """
        ((text, exact) => {
          const norm = s => (s || '').replace(/\s+/g, ' ').trim();
          const want = exact ? norm(text) : norm(text).toLowerCase();
          if (!want || !document.body) return null;
          let best = null;
          for (const e of document.body.querySelectorAll('*')) {
            const r = e.getBoundingClientRect();
            if (r.width <= 0 || r.height <= 0) continue;
            const t = norm(e.innerText || e.textContent);
            if (exact ? t === want : t.toLowerCase().includes(want)) { if (!best || best.contains(e)) best = e; }
          }
          if (!best) return null;
          best.scrollIntoView({block: 'center', inline: 'center'});
          const r = best.getBoundingClientRect();
          return [r.x + r.width / 2, r.y + r.height / 2];
        })
        """;

    public static string FindText(string text, bool exact) =>
        $"{FindFunction}({JsonString(text)}, {(exact ? "true" : "false")})";

    /// <summary>A JavaScript string literal (JSON-escaped, so safe to paste into a script).</summary>
    public static string JsonString(string value)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            writer.WriteStringValue(value);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>[x, y] from a script, or null.</summary>
    public static (double X, double Y)? Point(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2
        && value[0].ValueKind == JsonValueKind.Number && value[1].ValueKind == JsonValueKind.Number
            ? (value[0].GetDouble(), value[1].GetDouble())
            : null;
}
