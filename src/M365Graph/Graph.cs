namespace M365Graph;

/// <summary>The constants m365-graph (Python) exports, with the same values.</summary>
public static class Graph
{
    /// <summary>The Graph v1.0 endpoint; a relative url ("/me") is appended to it.</summary>
    public const string BaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>Microsoft's own public "Microsoft Graph Command Line Tools" app. Works in most tenants without
    /// registering an app; an admin may still have to approve scopes that need admin consent.</summary>
    public const string CliClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    /// <summary>A request that takes longer than this gives up (and is retried).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Tries per request, and per sign-in refresh.</summary>
    public const int Retries = 3;

    /// <summary>A call slower than this is logged.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(15);

    /// <summary>%LOCALAPPDATA%\m365-graph\msal-cache.json, as the Python default.</summary>
    public static string DefaultCachePath =>
        Path.Combine(LocalAppData(), "m365-graph", "msal-cache.json");

    private static string LocalAppData()
    {
        var folder = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrEmpty(folder) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : folder;
    }
}
