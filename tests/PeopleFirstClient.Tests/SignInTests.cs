using System.Diagnostics;

namespace PeopleFirstClient.Tests;

/// <summary>The single sign-on paths, against a fake browser. The rule from the 12 Nov 2026 spike: unattended, any
/// Microsoft page that needs a person stops the sign-in at once, not after the 3-minute timeout.</summary>
public class SignInTests
{
    private const string Token = "Bearer secret-token";
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);   // far under the 3 minutes Python waited

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly FakePage LoginPage = new(FakeBrowser.PeopleFirst + "/login", "Login - People First", Button: FakeBrowser.ButtonAt);
    private static readonly FakePage Picker = new(FakeBrowser.MicrosoftLogin, "Sign in to your account", Tile: FakeBrowser.TileAt, Picker: true);

    private static Settings NewSettings() => new(url: "https://example.people-first.com", account: "you@example.com",
        edgeProfile: Path.Combine(Path.GetTempPath(), "pf-test-" + Guid.NewGuid().ToString("N")), signInHint: "Run sign-in at a console.");

    private static SessionOptions Options(FakeBrowser browser, List<string>? log = null, double minutes = 3) => new()
    {
        Tick = TimeSpan.FromMilliseconds(20),
        MicrosoftGrace = TimeSpan.FromMilliseconds(400),
        Minutes = minutes,
        TransportFactory = (_, _) => browser,
        Log = line =>
        {
            if (log is null)
                return;
            lock (log)
                log.Add(line);
        },
    };

    /// <summary>Only ever reads and clicks: no typing, no cookies, no storage.</summary>
    private static void AssertOnlyReadsAndClicks(FakeBrowser browser)
    {
        string[] allowed = ["Browser.getVersion", "Target.setAutoAttach", "Target.createTarget", "Network.enable",
            "Runtime.runIfWaitingForDebugger", "Page.navigate", "Runtime.evaluate", "Input.dispatchMouseEvent", "Browser.close"];
        lock (browser.Methods)
            Assert.All(browser.Methods, method => Assert.Contains(method, allowed));
        Assert.True(browser.Closed, "Edge wasn't closed");
    }

    [Fact]
    public async Task Already_signed_in_takes_the_token_with_no_clicks()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, url) =>
            {
                Assert.Equal(FakeBrowser.PeopleFirst, url);
                b.Show(session, new FakePage(FakeBrowser.PeopleFirst + "/home", "People First"));
                b.SendToken(session, Token);
            },
        };
        using var api = await Session.OpenAsync(NewSettings(), Options(browser), Cancel);
        Assert.Equal(Token, api.Headers.Single(h => h.Key == "Authorization").Value);
        Assert.DoesNotContain("Input.dispatchMouseEvent", browser.Methods);
        AssertOnlyReadsAndClicks(browser);
    }

    [Fact]
    public async Task The_organisation_account_button_is_clicked_on_the_login_page()
    {
        var log = new List<string>();
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, x, y) =>
            {
                Assert.True(FakeBrowser.At(x, y, FakeBrowser.ButtonAt));
                b.Show(session, new FakePage(FakeBrowser.PeopleFirst + "/home", "People First"));   // single sign-on, silently
                b.SendToken(session, Token);
            },
        };
        using var api = await Session.OpenAsync(NewSettings(), Options(browser, log), Cancel);
        Assert.Equal(Token, api.Headers.Single(h => h.Key == "Authorization").Value);
        Assert.Contains("people first: signing in through the organisation account", log);
        AssertOnlyReadsAndClicks(browser);
    }

    [Fact]
    public async Task The_account_is_picked_in_a_pop_up()
    {
        var log = new List<string>();
        string? main = null, popup = null;
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) =>
            {
                main = session;
                b.Show(session, LoginPage);
            },
            OnClick = (b, session, x, y) =>
            {
                if (session == main && FakeBrowser.At(x, y, FakeBrowser.ButtonAt))
                {
                    popup = b.Open(Picker);
                }
                else if (session == popup && FakeBrowser.At(x, y, FakeBrowser.TileAt))
                {
                    b.Show(popup, new FakePage(FakeBrowser.PeopleFirst + "/callback"));
                    b.SendToken(main!, Token);
                }
                else
                {
                    Assert.Fail($"an unexpected click on {session} at {x},{y}");
                }
            },
        };
        using var api = await Session.OpenAsync(NewSettings(), Options(browser, log), Cancel);
        Assert.Equal(Token, api.Headers.Single(h => h.Key == "Authorization").Value);
        Assert.Contains("people first: picked the account at Microsoft's sign-in", log);
        AssertOnlyReadsAndClicks(browser);
    }

    [Fact]
    public async Task The_account_is_picked_in_the_same_tab()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, x, y) =>
            {
                if (FakeBrowser.At(x, y, FakeBrowser.ButtonAt))
                {
                    b.Show(session, Picker);
                }
                else
                {
                    b.Show(session, new FakePage(FakeBrowser.PeopleFirst + "/home", "People First"));
                    b.SendToken(session, Token);
                }
            },
        };
        using var api = await Session.OpenAsync(NewSettings(), Options(browser), Cancel);
        Assert.Equal(Token, api.Headers.Single(h => h.Key == "Authorization").Value);
    }

    public static TheoryData<string, FakePage> PagesThatNeedAPerson => new()
    {
        { "a password", new FakePage(FakeBrowser.MicrosoftLogin, "Sign in", Password: true) },
        { "a verification step", new FakePage(FakeBrowser.MicrosoftLogin, "Sign in", Approve: true) },
        { "a verification step", new FakePage(FakeBrowser.MicrosoftLogin, "Sign in", Code: true) },
        { "Windows Hello", new FakePage(FakeBrowser.MicrosoftLogin, "Sign in", Hello: true) },
        { "an email address", new FakePage(FakeBrowser.MicrosoftLogin, "Sign in", Username: true) },
    };

    /// <summary>The spike's finding: the tile is clicked, then Microsoft wants more. Python only checked for a
    /// password box, so an Authenticator or Windows Hello prompt sat there for the full 3 minutes (and may have
    /// reached the person's phone). Now it stops on the next look.</summary>
    [Theory]
    [MemberData(nameof(PagesThatNeedAPerson))]
    public async Task A_page_that_needs_a_person_after_the_tile_stops_at_once(string wants, FakePage next)
    {
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, x, y) => b.Show(session, FakeBrowser.At(x, y, FakeBrowser.ButtonAt) ? Picker : next),
        };
        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SignInNeededException>(() => Session.OpenAsync(NewSettings(), Options(browser), Cancel));
        Assert.True(clock.Elapsed < Quick, $"took {clock.Elapsed}");
        Assert.Contains(wants, error.Message);
        Assert.EndsWith("Run sign-in at a console.", error.Message);
        Assert.DoesNotContain("you@example.com", error.Message);
        Assert.Equal(2, browser.Methods.Count(m => m == "Input.dispatchMouseEvent") / 3);   // the button and the tile, nothing else
        AssertOnlyReadsAndClicks(browser);
    }

    [Theory]
    [MemberData(nameof(PagesThatNeedAPerson))]
    public async Task A_page_that_needs_a_person_with_no_picker_stops_at_once(string wants, FakePage page)
    {
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, _, _) => b.Show(session, page),
        };
        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SignInNeededException>(() => Session.OpenAsync(NewSettings(), Options(browser), Cancel));
        Assert.True(clock.Elapsed < Quick);
        Assert.Contains(wants, error.Message);
    }

    [Fact]
    public async Task A_password_box_on_the_picker_is_never_clicked_through()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, _, _) => b.Show(session, Picker with { Password = true }),
        };
        var error = await Assert.ThrowsAsync<SignInNeededException>(() => Session.OpenAsync(NewSettings(), Options(browser), Cancel));
        Assert.Contains("a password", error.Message);
        Assert.Equal(1, browser.Methods.Count(m => m == "Input.dispatchMouseEvent") / 3);   // only the People First button
    }

    [Fact]
    public async Task A_Microsoft_page_it_doesnt_recognise_stops_after_the_grace_period()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, x, y) => b.Show(session, FakeBrowser.At(x, y, FakeBrowser.ButtonAt)
                ? Picker
                : new FakePage(FakeBrowser.MicrosoftLogin.Replace("authorize", "kmsi"), "Stay signed in?")),
        };
        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SignInNeededException>(() => Session.OpenAsync(NewSettings(), Options(browser), Cancel));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(400) && clock.Elapsed < Quick, $"took {clock.Elapsed}");
        Assert.Contains("after picking the account", error.Message);
    }

    [Fact]
    public async Task A_picker_without_the_account_stops_after_the_grace_period()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, _, _) => b.Show(session, Picker with { Tile = null }),
        };
        var error = await Assert.ThrowsAsync<SignInNeededException>(() => Session.OpenAsync(NewSettings(), Options(browser), Cancel));
        Assert.Contains("after reaching it", error.Message);
        Assert.Contains("an account to be picked", error.Message);
    }

    [Fact]
    public async Task A_redirect_through_Microsoft_is_not_cut_short()
    {
        // Microsoft's page shows for a moment (under the grace period) between redirects: that's fine.
        var looks = 0;
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, _, _) => b.Show(session, new FakePage(FakeBrowser.MicrosoftLogin, "Working...")),
            OnProbe = (b, session) =>
            {
                if (++looks == 6)
                {
                    b.Show(session, new FakePage(FakeBrowser.PeopleFirst + "/home", "People First"));
                    b.SendToken(session, Token);
                }
            },
        };
        using var api = await Session.OpenAsync(NewSettings(), Options(browser), Cancel);
        Assert.Equal(Token, api.Headers.Single(h => h.Key == "Authorization").Value);
    }

    [Fact]
    public async Task Nothing_happening_ends_at_the_time_limit()
    {
        var browser = new FakeBrowser { OnNavigate = (b, session, _) => b.Show(session, new FakePage(FakeBrowser.PeopleFirst + "/loading", "Loading")) };
        var error = await Assert.ThrowsAsync<SignInNeededException>(() =>
            Session.OpenAsync(NewSettings(), Options(browser, minutes: 0.01), Cancel));
        Assert.Equal("People First didn't finish signing in within 0.01 minutes. Run sign-in at a console.", error.Message);
        AssertOnlyReadsAndClicks(browser);
    }

    [Fact]
    public async Task The_sign_in_window_waits_for_the_person_through_a_password_page()
    {
        var looks = 0;
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) => b.Show(session, LoginPage),
            OnClick = (b, session, x, y) => b.Show(session, FakeBrowser.At(x, y, FakeBrowser.ButtonAt)
                ? Picker
                : new FakePage(FakeBrowser.MicrosoftLogin, "Sign in", Password: true)),
            OnProbe = (b, session) =>
            {
                // The person types their password: some looks later, the page moves on.
                if (++looks == 40)
                {
                    b.Show(session, new FakePage(FakeBrowser.PeopleFirst + "/home", "People First"));
                    b.SendToken(session, Token);
                }
            },
        };
        await SignInWindow.RunAsync(NewSettings(), Options(browser), Cancel);
        Assert.True(looks >= 40);
        AssertOnlyReadsAndClicks(browser);
    }

    [Fact]
    public async Task A_header_that_isnt_a_bearer_token_is_ignored()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = (b, session, _) =>
            {
                b.Show(session, new FakePage(FakeBrowser.PeopleFirst + "/home", "People First"));
                b.SendToken(session, "Basic not-a-bearer");
            },
        };
        await Assert.ThrowsAsync<SignInNeededException>(() => Session.OpenAsync(NewSettings(), Options(browser, minutes: 0.005), Cancel));
    }

    [Fact]
    public async Task Settings_without_a_profile_are_refused() =>
        await Assert.ThrowsAsync<PeopleFirstException>(() => Session.OpenAsync(new Settings(url: "https://x.people-first.com"), cancellationToken: Cancel));

    [Fact]
    public async Task A_held_lock_for_another_profile_is_refused()
    {
        await using var other = await ProfileLock.AcquireAsync(Path.Combine(Path.GetTempPath(), "pf-other-" + Guid.NewGuid().ToString("N")), 5, Cancel);
        await Assert.ThrowsAsync<ArgumentException>(() => Session.OpenAsync(NewSettings(), new SessionOptions { HeldLock = other }, Cancel));
    }

    [Fact]
    public async Task Edge_that_cannot_start_is_a_People_First_error()
    {
        var options = new SessionOptions { TransportFactory = (_, _) => throw new EdgeDevTools.EdgeLaunchException("no Edge here") };
        var error = await Assert.ThrowsAsync<PeopleFirstException>(() => Session.OpenAsync(NewSettings(), options, Cancel));
        Assert.StartsWith("Couldn't start Edge for People First (is another run using its profile?): no Edge here", error.Message);
    }
}
