using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Identity.Client;

namespace M365Graph.Tests;

public class SignInTests
{
    private static readonly GraphClientOptions Options = new() { SignInHint = "Run the sign-in command." };
    private static readonly List<string> Log = [];

    private static Task<string> Token(SignIn signIn, IEnumerable<string>? scopes = null, bool? interactive = null, GraphClientOptions? options = null) =>
        signIn.TokenAsync(options ?? Options, scopes, interactive, Log.Add, CancellationToken.None);

    [Fact]
    public async Task A_token_is_reused_until_five_minutes_before_it_expires()
    {
        var now = Fake.Now;
        var msal = new FakeMsal();
        msal.Silent.Enqueue(() => Fake.Token("first", minutes: 10));
        msal.Silent.Enqueue(() => Fake.Token("second"));
        var signIn = Fake.SignIn(msal, clock: () => now);

        Assert.Equal("first", await Token(signIn));
        now = now.AddMinutes(4);
        Assert.Equal("first", await Token(signIn));
        now = now.AddMinutes(2);   // 4 minutes left: refreshed
        Assert.Equal("second", await Token(signIn));
        Assert.Equal(2, msal.SilentCalls);
    }

    [Fact]
    public async Task Scopes_are_sorted_so_the_same_set_shares_a_token_and_none_means_the_defaults()
    {
        var msal = new FakeMsal();
        msal.Silent.Enqueue(() => Fake.Token("mail"));
        msal.Silent.Enqueue(() => Fake.Token("default"));
        var signIn = Fake.SignIn(msal);

        Assert.Equal("mail", await Token(signIn, ["Mail.Read", "Calendars.Read"]));
        Assert.Equal("mail", await Token(signIn, ["Calendars.Read", "Mail.Read"]));
        Assert.Equal("default", await Token(signIn, []));
        Assert.Equal("default", await Token(signIn));
        Assert.Equal(2, msal.SilentCalls);
    }

    [Fact]
    public async Task Unattended_with_no_cached_sign_in_never_opens_a_browser()
    {
        var msal = new FakeMsal { Interactive = () => Fake.Token("browser") };
        var error = await Assert.ThrowsAsync<SignInNeededException>(() => Token(Fake.SignIn(msal, unattended: true), ["Mail.Read"]));
        Assert.Contains("No cached sign-in for Mail.Read", error.Message);
        Assert.Contains("Run the sign-in command.", error.Message);
        Assert.Empty(msal.InteractiveCalls);
    }

    [Fact]
    public async Task Interactive_false_wins_even_with_someone_at_the_console()
    {
        var msal = new FakeMsal { Interactive = () => Fake.Token("browser") };
        var attended = Fake.SignIn(msal, unattended: false);
        await Assert.ThrowsAsync<SignInNeededException>(() => Token(attended, interactive: false));
        await Assert.ThrowsAsync<SignInNeededException>(() => Token(attended, options: Options with { Interactive = false }));
        Assert.Empty(msal.InteractiveCalls);
    }

    [Fact]
    public async Task Someone_at_the_console_gets_a_browser_with_the_login_hint()
    {
        var msal = new FakeMsal { Interactive = () => Fake.Token("browser") };
        var options = Options with { LoginHint = "someone@example.com" };
        Assert.Equal("browser", await Token(Fake.SignIn(msal, unattended: false), options: options));
        Assert.Equal("someone@example.com", Assert.Single(msal.InteractiveCalls).Hint);
        Assert.Contains("Opening a browser to sign in to Microsoft...", Log);
    }

    [Fact]
    public async Task Interactive_true_in_the_options_overrides_the_console_check()
    {
        var msal = new FakeMsal { Interactive = () => Fake.Token("browser") };
        Assert.Equal("browser", await Token(Fake.SignIn(msal, unattended: true), options: Options with { Interactive = true }));
    }

    [Fact]
    public async Task A_failed_browser_sign_in_is_sign_in_needed()
    {
        var msal = new FakeMsal { Interactive = () => throw new MsalClientException("authentication_canceled", "User canceled") };
        var error = await Assert.ThrowsAsync<SignInNeededException>(() => Token(Fake.SignIn(msal, unattended: false)));
        Assert.Contains("authentication_canceled", error.Message);
    }

    [Fact]
    public async Task A_dropped_connection_during_the_refresh_is_retried_after_5_then_10_seconds()
    {
        var msal = new FakeMsal();
        msal.Silent.Enqueue(() => throw new HttpRequestException("reset", new SocketException(10054)));
        msal.Silent.Enqueue(() => throw new MsalServiceException(MsalError.RequestTimeout, "timed out"));
        msal.Silent.Enqueue(() => Fake.Token("third time"));
        var waits = new List<TimeSpan>();
        var log = new List<string>();

        var token = await Fake.SignIn(msal, waits).TokenAsync(Options, null, null, log.Add, CancellationToken.None);

        Assert.Equal("third time", token);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], waits);
        Assert.Equal("sign-in refresh retry 1 in 5s: HttpRequestException", log[0]);
    }

    [Fact]
    public async Task A_refresh_that_keeps_dropping_fails_after_three_tries()
    {
        var msal = new FakeMsal();
        for (var i = 0; i < 3; i++)
            msal.Silent.Enqueue(() => throw new HttpRequestException("reset"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Token(Fake.SignIn(msal)));
        Assert.Equal(3, msal.SilentCalls);
    }

    [Fact]
    public async Task Anything_other_than_a_dropped_connection_is_not_retried()
    {
        var msal = new FakeMsal();
        msal.Silent.Enqueue(() => throw new MsalServiceException("invalid_grant", "nope"));
        await Assert.ThrowsAsync<MsalServiceException>(() => Token(Fake.SignIn(msal)));
        Assert.Equal(1, msal.SilentCalls);
    }

    [Fact]
    public void One_sign_in_per_cache_file_client_and_tenant()
    {
        var path = Path.Combine(Path.GetTempPath(), "m365graph-shared-" + Guid.NewGuid().ToString("N"), "Cache.json");
        using var http = new HttpClient(new NoNetwork());
        var a = SignIn.For(new GraphClientOptions { CachePath = path }, http);
        var b = SignIn.For(new GraphClientOptions { CachePath = path.ToUpperInvariant() }, http);
        var c = SignIn.For(new GraphClientOptions { CachePath = path, Tenant = "contoso.com" }, http);
        Assert.Same(a, b);
        Assert.NotSame(a, c);
    }
}
