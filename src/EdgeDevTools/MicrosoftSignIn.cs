using System.Text.Json;

namespace EdgeDevTools;

/// <summary>What kind of page a tab is showing, as far as an unattended single sign-on cares.</summary>
public enum SignInPage
{
    /// <summary>Not Microsoft's sign-in at all (the app itself, or a page between redirects).</summary>
    NotMicrosoft,

    /// <summary>"Pick an account": a remembered account's tile can be clicked, which needs no one.</summary>
    AccountPicker,

    /// <summary>A password box.</summary>
    Password,

    /// <summary>An email address box: no account is remembered.</summary>
    Username,

    /// <summary>Multi-factor: an Authenticator approval, a number to match, or a code.</summary>
    Verification,

    /// <summary>Windows Hello, a passkey or a security key.</summary>
    WindowsHello,

    /// <summary>Some other page of Microsoft's (stay signed in?, an error, or one still loading).</summary>
    Other,
}

/// <summary>What one probe of a tab found. Only shapes and outcomes: no page text is kept.</summary>
public sealed record SignInProbe(
    string Href,
    string Title,
    (double X, double Y)? Button,
    (double X, double Y)? Tile,
    bool Password,
    bool Username,
    bool Code,
    bool Approve,
    bool Hello,
    bool Picker)
{
    /// <summary>The page's host, lower-cased; "" if the address isn't absolute.</summary>
    public string Host => Uri.TryCreate(Href, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";
}

/// <summary>
/// Recognising Microsoft's sign-in pages, for tools that ride an organisation's single sign-on with a saved
/// Edge profile. One script per tab per look (<see cref="ProbeScript"/>) reports what's on the page, and
/// <see cref="Classify"/> decides what it is, in C#, where it can be tested without a browser.
/// <para>The rule it serves: unattended, the only Microsoft page that needs no person is the account picker (click
/// the remembered account). A password, an email box, an Authenticator or code prompt, or Windows Hello all need
/// someone, so a tool should stop at once rather than wait out its timeout. The checks look at several signals
/// (visible inputs, Microsoft's element ids, and the page's wording), because Microsoft changes its pages; a page
/// none of them recognises is <see cref="SignInPage.Other"/>, and a tool should give that a short grace period
/// (redirects pass through such pages in a second or two) and then stop too.</para>
/// </summary>
public static class MicrosoftSignIn
{
    /// <summary>Hosts that serve Microsoft's sign-in. Subdomains count too.</summary>
    public static readonly IReadOnlyList<string> Hosts =
    [
        "login.microsoftonline.com",
        "login.microsoft.com",
        "login.windows.net",
        "login.live.com",
        "account.live.com",
        "autologon.microsoftazuread-sso.com",
    ];

    /// <summary>The marker at the start of every probe script (tests' fake browsers look for it).</summary>
    public const string ProbeMarker = "/*edge-devtools:sign-in-probe*/";

    public static bool IsMicrosoftHost(string host) =>
        host.Length > 0 && Hosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)
                                         || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>A script reporting the page's address and title, where a button with exactly this text is (the
    /// app's own "sign in with your organisation" button; null for none), where an element containing the
    /// account is (an account tile), and which sign-in prompts are showing.</summary>
    public static string ProbeScript(string? buttonText, string? account) => ProbeMarker + $$"""
        ((button, account) => {
          const find = {{PageScripts.FindFunction}};
          const shown = sel => Array.from(document.querySelectorAll(sel)).some(e => {
            const r = e.getBoundingClientRect(); const s = getComputedStyle(e);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
          });
          const text = (document.body && document.body.innerText) || '';
          return {
            href: location.href,
            title: document.title || '',
            button: button ? find(button, true) : null,
            tile: account ? find(account, false) : null,
            password: shown('input[type=password]'),
            username: shown('input[name=loginfmt], input[type=email]'),
            code: shown('input[name=otc], input[autocomplete=one-time-code], #idRichContext_DisplaySign'),
            approve: !!document.querySelector('#idDiv_SAOTCAS_Title, #idDiv_SAOTCC_Title, #idDiv_SAOTCS_Title')
              || /approve (the |a )?sign[- ]?in request|open your authenticator app|enter the number shown|verify your identity/i.test(text),
            hello: /windows hello|security key|passkey|face, fingerprint/i.test(text),
            picker: !!document.querySelector('#tilesHolder, [data-test-id="otherTile"]') || /pick an account/i.test(text)
          };
        })({{(buttonText is null ? "null" : PageScripts.JsonString(buttonText))}}, {{(account is null ? "null" : PageScripts.JsonString(account))}})
        """;

    /// <summary>Read a probe's result; null if the page gave nothing usable (it was between documents).</summary>
    public static SignInProbe? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        return new SignInProbe(
            Text(value, "href"), Text(value, "title"),
            value.TryGetProperty("button", out var b) ? PageScripts.Point(b) : null,
            value.TryGetProperty("tile", out var t) ? PageScripts.Point(t) : null,
            Flag(value, "password"), Flag(value, "username"), Flag(value, "code"), Flag(value, "approve"),
            Flag(value, "hello"), Flag(value, "picker"));
    }

    /// <summary>What the page is. A password box outranks everything; then a verification prompt, Windows Hello,
    /// the account picker (or any page showing the account), then an email box.</summary>
    public static SignInPage Classify(SignInProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!IsMicrosoftHost(probe.Host))
            return SignInPage.NotMicrosoft;
        if (probe.Password)
            return SignInPage.Password;
        if (probe.Code || probe.Approve)
            return SignInPage.Verification;
        if (probe.Hello)
            return SignInPage.WindowsHello;
        if (probe.Picker || probe.Tile is not null)
            return SignInPage.AccountPicker;
        if (probe.Username)
            return SignInPage.Username;
        return SignInPage.Other;
    }

    /// <summary>Whether the page needs a person, whatever happens next.</summary>
    public static bool NeedsAPerson(SignInPage page) =>
        page is SignInPage.Password or SignInPage.Username or SignInPage.Verification or SignInPage.WindowsHello;

    /// <summary>"a password", "an email address" and so on, for a message.</summary>
    public static string Describe(SignInPage page) => page switch
    {
        SignInPage.Password => "a password",
        SignInPage.Username => "an email address (no account is remembered)",
        SignInPage.Verification => "a verification step (Authenticator, a number to match, or a code)",
        SignInPage.WindowsHello => "Windows Hello or a security key",
        SignInPage.AccountPicker => "an account to be picked",
        SignInPage.Other => "a page it doesn't recognise",
        _ => "nothing",
    };

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
