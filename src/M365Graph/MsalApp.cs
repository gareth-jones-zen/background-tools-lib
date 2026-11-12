using Microsoft.Identity.Client;

namespace M365Graph;

/// <summary>An access token and when it expires.</summary>
public sealed record AccessToken(string Token, DateTimeOffset ExpiresOn);

/// <summary>The three things sign-in needs from MSAL, so the rules around them can be tested without it.</summary>
internal interface IMsalApp
{
    /// <summary>From the cache, refreshing if needed. Null when there's no account, or the scopes need a new sign-in
    /// or consent. Throws on a network failure, which the caller retries.</summary>
    Task<AccessToken?> SilentAsync(IReadOnlyList<string> scopes, CancellationToken cancel);

    /// <summary>A browser sign-in.</summary>
    Task<AccessToken> InteractiveAsync(IReadOnlyList<string> scopes, string? loginHint, CancellationToken cancel);
}

/// <summary>MSAL.NET's public client, bound to the shared cache file in MSAL's unified ("V3") format: the same JSON
/// MSAL Python reads and writes, so .NET and Python tools can share one file.</summary>
internal sealed class MsalApp : IMsalApp
{
    private readonly IPublicClientApplication app;
    private readonly CacheBinding binding;

    public MsalApp(string cachePath, string clientId, string tenant, HttpClient http)
    {
        app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenant}")
            // Without this MSAL.NET files tokens under login.windows.net, its preferred alias from instance discovery,
            // while MSAL Python files them under login.microsoftonline.com. Each can read the other's, but the shared
            // file then holds two of everything and neither reuses the other's access tokens. Off, the cache uses the
            // authority's own host, as Python does, and there's one less call to login.microsoftonline.com.
            .WithInstanceDiscovery(false)
            .WithRedirectUri("http://localhost")          // the system browser, as MSAL Python does
            .WithHttpClientFactory(new Factory(http))     // IPv4 first and the 60-second timeout, for sign-in too
            .Build();
        binding = new CacheBinding(cachePath);
        binding.Bind(app.UserTokenCache);
    }

    public async Task<AccessToken?> SilentAsync(IReadOnlyList<string> scopes, CancellationToken cancel)
    {
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        if (account is null)
            return null;
        try
        {
            var result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancel).ConfigureAwait(false);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException)
        {
            return null;   // as Python's error dict: a scope never consented to, or an expired sign-in
        }
    }

    public async Task<AccessToken> InteractiveAsync(IReadOnlyList<string> scopes, string? loginHint, CancellationToken cancel)
    {
        var builder = app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false);
        if (!string.IsNullOrEmpty(loginHint))
            builder = builder.WithLoginHint(loginHint);
        var result = await builder.ExecuteAsync(cancel).ConfigureAwait(false);
        return new AccessToken(result.AccessToken, result.ExpiresOn);
    }

    private sealed class Factory(HttpClient http) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => http;
    }
}

/// <summary>Keeps MSAL's in-memory cache and the file in step.
///
/// Python loads the file once per process and writes the whole cache back whenever it changed. This does the same,
/// with one difference: before each use it reloads the file if another program has replaced it since, so a
/// long-running .NET watcher picks up a refresh a Python tool made rather than writing back its older view.</summary>
internal sealed class CacheBinding(string path)
{
    private (DateTime Written, long Length)? seen;
    private bool pending;   // a change not yet on disk because Windows held the file: written on the next change

    public void Bind(ITokenCache cache)
    {
        cache.SetBeforeAccess(BeforeAccess);
        cache.SetAfterAccess(AfterAccess);
    }

    private void BeforeAccess(TokenCacheNotificationArgs args)
    {
        var now = Stamp();
        if (now is null || now == seen)
            return;
        var data = TokenCacheFile.Read(path);
        seen = now;
        if (data is not null)
            args.TokenCache.DeserializeMsalV3(data, shouldClearExistingCache: !pending);
    }

    private void AfterAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged && !pending)
            return;
        pending = !TokenCacheFile.Write(path, args.TokenCache.SerializeMsalV3());
        if (!pending)
            seen = Stamp();
    }

    private (DateTime, long)? Stamp()
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
    }
}
