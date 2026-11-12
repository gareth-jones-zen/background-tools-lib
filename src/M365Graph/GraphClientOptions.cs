namespace M365Graph;

/// <summary>The settings Python's Client takes, with the same defaults.</summary>
public sealed record GraphClientOptions
{
    /// <summary>The token cache file. Default %LOCALAPPDATA%\m365-graph\msal-cache.json.</summary>
    public string CachePath { get; init; } = Graph.DefaultCachePath;

    /// <summary>A tenant id or domain, or "organizations" for any work account.</summary>
    public string Tenant { get; init; } = "organizations";

    public string ClientId { get; init; } = Graph.CliClientId;

    /// <summary>The account a browser sign-in suggests.</summary>
    public string? LoginHint { get; init; }

    /// <summary>The scopes asked for when a call names none.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = ["User.Read"];

    /// <summary>True may open a browser; false never does (SignInNeededException instead); null decides by whether
    /// anyone is at the console (<see cref="Unattended.Now"/>).</summary>
    public bool? Interactive { get; init; }

    /// <summary>Shown in SignInNeededException, so each tool can say how its own users sign in.</summary>
    public string SignInHint { get; init; } = "Sign in again at a console: python -m m365_graph sign-in";

    /// <summary>Receives slow-call and retry notices. Default: standard error.</summary>
    public Action<string>? Log { get; init; }
}
