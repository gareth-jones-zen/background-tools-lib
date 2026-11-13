using EdgeDevTools;

namespace PeopleFirstClient;

/// <summary>How a session or sign-in window runs. Everything has a default.</summary>
public sealed class SessionOptions
{
    /// <summary>Progress lines (never a value from People First, and never the token).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>How long the sign-in may take, in minutes: 3 for a session (Python's SIGN_IN_MINUTES), 5 for a
    /// sign-in window, when null.</summary>
    public double? Minutes { get; init; }

    /// <summary>How long to wait for another tool to finish with the profile, in seconds (Python's LOCK_WAIT).</summary>
    public double LockWaitSeconds { get; init; } = ProfileLock.DefaultWaitSeconds;

    /// <summary>A ProfileLock this caller already holds for the settings' profile, to use instead of taking one.
    /// See <see cref="ProfileLock"/>: hold one lock for a run.</summary>
    public ProfileLock? HeldLock { get; init; }

    /// <summary>The HttpClient for the API (not disposed by the Api). One is made when null.</summary>
    public HttpClient? Http { get; init; }

    /// <summary>Unattended, how long a Microsoft sign-in page may stay up (after the account was picked, or since
    /// it appeared) before the sign-in is taken to need a person. Redirects pass in a second or two.</summary>
    public TimeSpan MicrosoftGrace { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often the pages are looked at while signing in (the token itself is taken as soon as it's
    /// seen). Python waited 2 seconds between looks.</summary>
    internal TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>For tests: a fake browser instead of Edge.</summary>
    internal Func<string, IReadOnlyList<string>, ICdpTransport>? TransportFactory { get; init; }
}

/// <summary>
/// Signing in to People First and reading it. As Python's <c>session()</c>, except that the browser is closed as
/// soon as the token is captured, and the Api reads with HttpClient (People First needs only the token and
/// TenantCode, no cookies).
/// <code>
/// var settings = new Settings(url: "https://yourorg.people-first.com", account: "you@example.com",
///                             edgeProfile: @"C:\Users\you\AppData\Local\my-tool\edge-profile");
/// using var api = await Session.OpenAsync(settings, new() { Log = Console.WriteLine });
/// var me = await api.MeAsync();
/// foreach (var person in await api.TeamMembersAsync(me)) { ... }
/// </code>
/// </summary>
public static class Session
{
    /// <summary>Sign in unattended (a hidden Edge) and return an Api. Takes the profile's lock for the browser phase
    /// unless <see cref="SessionOptions.HeldLock"/> is given. Throws SignInNeededException if the sign-in needs a
    /// person (at once, for a password, MFA, Windows Hello or no remembered account), TimeoutException if another
    /// tool keeps the profile too long, and PeopleFirstException if Edge can't start.</summary>
    public static Task<Api> OpenAsync(Settings settings, SessionOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenAsync(settings, options ?? new SessionOptions(), headless: true, cancellationToken);

    internal static async Task<Api> OpenAsync(Settings settings, SessionOptions options, bool headless, CancellationToken cancellationToken)
    {
        var authorization = await WithProfileAsync(settings, options,
            () => SsoSignIn.RunAsync(settings, options, headless, options.Minutes ?? 3, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new Api(settings, authorization, options.Http);
    }

    internal static async Task<T> WithProfileAsync<T>(Settings settings, SessionOptions options, Func<Task<T>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrEmpty(settings.EdgeProfile))
            throw new PeopleFirstException("People First settings have no edge_profile.");
        if (options.HeldLock is { } held)
        {
            if (!held.Covers(settings.EdgeProfile))
                throw new ArgumentException("SessionOptions.HeldLock isn't held, or is for another Edge profile.", nameof(options));
            return await run().ConfigureAwait(false);
        }
        await using var own = await ProfileLock.AcquireAsync(settings.EdgeProfile, options.LockWaitSeconds, cancellationToken).ConfigureAwait(false);
        return await run().ConfigureAwait(false);
    }
}

/// <summary>A visible Edge window, for a first sign-in or one that needs a password or MFA. As Python's
/// <c>sign_in_window()</c>: it clicks the organisation button and the account tile as a session would, then waits
/// for the person to finish. Nothing is ever typed for them.</summary>
public static class SignInWindow
{
    /// <summary>Returns once People First is signed in; SignInNeededException if that doesn't happen within the
    /// minutes (5 by default).</summary>
    public static async Task RunAsync(Settings settings, SessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SessionOptions();
        await Session.WithProfileAsync(settings, options,
            () => SsoSignIn.RunAsync(settings, options, headless: false, options.Minutes ?? 5, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Load People First and return the Authorization header its web app sends to its own API.
/// <para>Unattended, it gets through the organisation's single sign-on by clicking "Organisation account" and the
/// remembered account's tile, as Python's did. <b>The fix from the 12 Nov 2026 spike:</b> Python went on waiting
/// for its 3 minutes whenever Microsoft showed anything other than a password box (an Authenticator prompt,
/// Windows Hello, an email box), because a password box was the only thing it checked for. Now any Microsoft page
/// that needs a person stops the sign-in at once, and any other Microsoft page still showing 20 seconds after the
/// tile was clicked (or after it appeared) stops it too.</para>
/// </summary>
internal static class SsoSignIn
{
    public static async Task<string> RunAsync(Settings settings, SessionOptions options, bool headless, double minutes,
        CancellationToken cancellationToken)
    {
        var log = options.Log ?? (_ => { });
        EdgeBrowser browser;
        try
        {
            browser = await EdgeBrowser.LaunchAsync(new EdgeLaunchOptions
            {
                UserDataDir = settings.EdgeProfile!,
                Headless = headless,
                TransportFactory = options.TransportFactory,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EdgeLaunchException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            var first = e.Message.Split('\n')[0];
            throw new PeopleFirstException("Couldn't start Edge for People First (is another run using its profile?): "
                                           + first[..Math.Min(first.Length, 200)], e);
        }
        await using (browser.ConfigureAwait(false))
        {
            browser.Log = log;
            var token = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var apiRoot = settings.Url.TrimEnd('/') + "/api/v1/";
            browser.RequestSent += request =>
            {
                var authorization = request.Header("authorization");
                if (authorization is not null && request.Url.StartsWith(apiRoot, StringComparison.Ordinal)
                    && authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
                    token.TrySetResult(authorization);
            };
            var main = await browser.NewPageAsync(settings.Url, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            return await WaitAsync(browser, main, token.Task, settings, options, headless, minutes, log, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> WaitAsync(EdgeBrowser browser, EdgeTarget main, Task<string> token, Settings settings,
        SessionOptions options, bool headless, double minutes, Action<string> log, CancellationToken cancellationToken)
    {
        var probe = MicrosoftSignIn.ProbeScript(Settings.AccountButton, settings.Account.Length > 0 ? settings.Account : null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var limit = TimeSpan.FromMinutes(minutes);
        bool clicked = false, picked = false;
        TimeSpan? pickedAt = null, microsoftSince = null;
        while (clock.Elapsed < limit)
        {
            await Task.WhenAny(token, Task.Delay(options.Tick, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (token.IsCompleted)
                return await token.ConfigureAwait(false);
            var onMicrosoft = false;
            var pages = browser.Pages.OrderBy(p => p == main ? 0 : 1).ToList();
            foreach (var page in pages)
            {
                if (token.IsCompleted)
                    break;
                SignInProbe? seen;
                try
                {
                    seen = MicrosoftSignIn.Parse(await browser.Connection.EvaluateAsync(page.SessionId, probe, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false));
                }
                catch (Exception e) when (e is CdpException or TimeoutException)
                {
                    continue;   // between documents, or gone
                }
                if (seen is null)
                    continue;
                if (page == main && !clicked && seen.Title.Contains("Login", StringComparison.Ordinal) && seen.Button is { } button)
                {
                    await browser.ClickAsync(page, button.X, button.Y, cancellationToken).ConfigureAwait(false);
                    clicked = true;
                    log("people first: signing in through the organisation account");
                    continue;
                }
                var kind = MicrosoftSignIn.Classify(seen);
                if (kind == SignInPage.NotMicrosoft)
                    continue;
                onMicrosoft = true;
                microsoftSince ??= clock.Elapsed;
                if (!picked && seen.Tile is { } tile && kind is SignInPage.AccountPicker or SignInPage.Other)
                {
                    await browser.ClickAsync(page, tile.X, tile.Y, cancellationToken).ConfigureAwait(false);
                    picked = true;
                    pickedAt = clock.Elapsed;
                    log("people first: picked the account at Microsoft's sign-in");
                    continue;
                }
                if (!headless)
                    continue;   // a person is there to finish it
                if (MicrosoftSignIn.NeedsAPerson(kind))
                    throw new SignInNeededException($"People First's sign-in wants {MicrosoftSignIn.Describe(kind)}. {settings.SignInHint}");
                var since = pickedAt ?? microsoftSince.Value;
                if (clock.Elapsed - since > options.MicrosoftGrace)
                    throw new SignInNeededException(
                        $"People First's sign-in was still at Microsoft's sign-in ({MicrosoftSignIn.Describe(kind)}) " +
                        $"{options.MicrosoftGrace.TotalSeconds:0} seconds after {(picked ? "picking the account" : "reaching it")}. {settings.SignInHint}");
            }
            if (!onMicrosoft)
                microsoftSince = null;
        }
        if (token.IsCompleted)
            return await token.ConfigureAwait(false);
        throw new SignInNeededException($"People First didn't finish signing in within {minutes:0.##} minutes. {settings.SignInHint}");
    }
}
