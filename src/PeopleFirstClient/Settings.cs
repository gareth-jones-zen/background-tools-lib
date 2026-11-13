using System.Text.RegularExpressions;

namespace PeopleFirstClient;

/// <summary>Something went wrong reading People First.</summary>
public class PeopleFirstException : Exception
{
    public PeopleFirstException(string message) : base(message)
    {
    }

    public PeopleFirstException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>The sign-in needs a person: a password, MFA, Windows Hello, no remembered account, or it didn't finish
/// in time. The message ends with <see cref="Settings.SignInHint"/>, so each tool can say how its users sign in.</summary>
public sealed class SignInNeededException : PeopleFirstException
{
    public SignInNeededException(string message) : base(message)
    {
    }
}

/// <summary>Where People First is, and how to sign in to it. As Python's <c>Settings</c>: the url loses trailing
/// slashes, and the tenant code defaults to the url's first part.</summary>
public sealed partial class Settings
{
    /// <summary>People First's sign-in button for the organisation's own sign-in.</summary>
    public const string AccountButton = "Organisation account";

    /// <summary>The default for <see cref="SignInHint"/>.</summary>
    public const string DefaultSignInHint = "At a console, sign in to People First in a window.";

    public Settings(string url = "", string tenantCode = "", string account = "", string? edgeProfile = null,
        string signInHint = DefaultSignInHint)
    {
        Url = (url ?? "").TrimEnd('/');
        TenantCode = string.IsNullOrEmpty(tenantCode) ? DefaultTenantCode(Url) : tenantCode;
        Account = account ?? "";
        EdgeProfile = edgeProfile;
        SignInHint = signInHint ?? DefaultSignInHint;
    }

    /// <summary>https://yourorg.people-first.com, with no trailing slash.</summary>
    public string Url { get; }

    /// <summary>Sent as the TenantCode header on every call; without it People First answers "Could not resolve
    /// tenant environment".</summary>
    public string TenantCode { get; }

    /// <summary>The account to pick at the organisation's sign-in (its tile is clicked); "" picks none.</summary>
    public string Account { get; }

    /// <summary>The Edge profile folder that holds the sign-in. Shared profiles share one sign-in, and their tools
    /// take turns (<see cref="ProfileLock"/>).</summary>
    public string? EdgeProfile { get; }

    /// <summary>The end of every SignInNeededException's message.</summary>
    public string SignInHint { get; }

    /// <summary>https://yourorg.people-first.com gives "yourorg"; https://acme-live.people-first.com gives "acme";
    /// "" if the url isn't of that shape.</summary>
    public static string DefaultTenantCode(string url)
    {
        var match = TenantUrl().Match(url ?? "");
        return match.Success ? match.Groups[1].Value.Split('-')[0] : "";
    }

    // Python: re.fullmatch(r"https://([a-z0-9-]+)\.people-first\.com/?", url, re.I)
    [GeneratedRegex(@"\Ahttps://([a-z0-9-]+)\.people-first\.com/?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TenantUrl();
}
