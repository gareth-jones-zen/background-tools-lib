namespace M365Graph;

/// <summary>Getting a token: the cached one while it has five minutes left, else silently from the cache file
/// (retried on a dropped connection), else a browser sign-in only if someone is there to complete it.
///
/// One per process for each cache file, client and tenant (<see cref="For"/>): every GraphClient on the same cache
/// shares its MSAL app and tokens, so a tool that makes fifty calls signs in once.</summary>
internal sealed class SignIn
{
    private static readonly Dictionary<(string, string, string), SignIn> Shared = [];
    private static readonly Lock SharedLock = new();

    private readonly Lazy<IMsalApp> app;
    private readonly Dictionary<string, AccessToken> tokens = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<bool> unattended;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    internal SignIn(Func<IMsalApp> app, Func<DateTimeOffset>? clock = null, Func<bool>? unattended = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.app = new Lazy<IMsalApp>(app);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.unattended = unattended ?? Unattended.Now;
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>The shared sign-in for these settings, made on first use.</summary>
    internal static SignIn For(GraphClientOptions options, HttpClient http)
    {
        var key = (Path.GetFullPath(options.CachePath).ToLowerInvariant(), options.ClientId, options.Tenant);
        lock (SharedLock)
        {
            if (!Shared.TryGetValue(key, out var signIn))
                Shared[key] = signIn = new SignIn(() => new MsalApp(options.CachePath, options.ClientId, options.Tenant, http));
            return signIn;
        }
    }

    public async Task<string> TokenAsync(GraphClientOptions options, IEnumerable<string>? scopes, bool? interactive,
        Action<string> log, CancellationToken cancel)
    {
        var wanted = (scopes?.ToList() is { Count: > 0 } named ? named : options.Scopes.ToList())
            .Order(StringComparer.Ordinal).ToList();
        var key = string.Join(" ", wanted);
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (tokens.TryGetValue(key, out var held) && held.ExpiresOn > clock().AddMinutes(5))
                return held.Token;
            var mayAsk = interactive ?? options.Interactive ?? !unattended();

            var token = await SilentlyAsync(wanted, log, cancel).ConfigureAwait(false);
            if (token is null)
            {
                if (!mayAsk)
                    throw new SignInNeededException(
                        $"No cached sign-in for {string.Join(", ", wanted)}, or it has expired. {options.SignInHint}");
                log("Opening a browser to sign in to Microsoft...");
                try
                {
                    token = await app.Value.InteractiveAsync(wanted, options.LoginHint, cancel).ConfigureAwait(false);
                }
                catch (Microsoft.Identity.Client.MsalException error) when (!Net.IsNetworkError(error, cancel))
                {
                    throw new SignInNeededException($"Sign-in failed: {error.ErrorCode}: {GraphRequestException.Shorten(error.Message, 300)}", error);
                }
            }
            tokens[key] = token;
            return token.Token;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Silent acquisition, retried on a dropped connection. MSAL refreshes over HTTP, and the odd connection
    /// reset from login.microsoftonline.com otherwise failed the whole run (m365-graph 0.1.2, 11 Nov 2026).</summary>
    private async Task<AccessToken?> SilentlyAsync(IReadOnlyList<string> scopes, Action<string> log, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await app.Value.SilentAsync(scopes, cancel).ConfigureAwait(false);
            }
            catch (Exception error) when (attempt < Graph.Retries && Net.IsNetworkError(error, cancel))
            {
                log($"sign-in refresh retry {attempt} in {5 * attempt}s: {error.GetType().Name}");
                await delay(TimeSpan.FromSeconds(5 * attempt), cancel).ConfigureAwait(false);
            }
        }
    }
}
