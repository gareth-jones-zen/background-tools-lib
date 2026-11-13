using EdgeDevTools;
using WinBackground;

namespace PeopleFirstClient;

/// <summary>
/// One tool at a time on a People First Edge profile. Edge refuses to open a profile that's already open, so tools
/// sharing a sign-in (one profile) must take turns. The lock is a named mutex, <c>people-first-edge-{key}</c>,
/// keyed on the profile's path exactly as Python's people-first-client keys it, so Python and .NET tools take
/// turns with each other too.
/// <para><b>Hold one ProfileLock for a run.</b> Python's lock is re-entrant within a process; WinBackground's
/// NamedLock deliberately isn't (two NamedLocks with one name exclude each other, even on one thread). So a tool
/// that wants the profile for more than one sign-in (several sessions, or a sign-in window then a session) takes a
/// ProfileLock once and passes it in <see cref="SessionOptions.HeldLock"/>. Taking a second ProfileLock for the
/// same profile while this process holds one throws InvalidOperationException at once, rather than waiting on
/// itself for 15 minutes.</para>
/// <para>Without a held lock, <see cref="Session.OpenAsync"/> takes one just for the browser phase (a few
/// seconds): Edge is closed as soon as the token is captured, and the API calls after that don't need the profile.
/// Python held it for the whole with-block, because its API calls went through the browser.</para>
/// </summary>
public sealed class ProfileLock : IDisposable, IAsyncDisposable
{
    /// <summary>The lock name's prefix, as in Python.</summary>
    public const string Prefix = "people-first-edge";

    /// <summary>The default wait for another tool to finish with the profile, in seconds (Python's LOCK_WAIT).</summary>
    public const double DefaultWaitSeconds = 900;

    private static readonly HashSet<string> HeldHere = new(StringComparer.Ordinal);
    private static readonly Lock HeldGate = new();

    private readonly NamedLock named;
    private bool registered = true;   // in HeldHere; guarded by HeldGate

    private ProfileLock(string profile, NamedLock named)
    {
        Profile = profile;
        this.named = named;
    }

    /// <summary>The lock's name for a profile: <c>people-first-edge-{EdgeProfile.Key(profile)}</c>, the same
    /// string Python's <c>lock_name</c> gives.</summary>
    public static string NameFor(string edgeProfile) => EdgeProfile.LockName(Prefix, edgeProfile);

    /// <summary>The profile, normalised.</summary>
    public string Profile { get; }

    public string Name => named.Name;

    public bool IsHeld => named.IsHeld;

    /// <summary>Take the profile's lock, waiting up to the timeout for another tool (or another process) to finish.
    /// Throws TimeoutException when it doesn't, and InvalidOperationException if this process already holds it.</summary>
    public static async Task<ProfileLock> AcquireAsync(string edgeProfile, double timeoutSeconds = DefaultWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(edgeProfile);
        var name = NameFor(edgeProfile);
        lock (HeldGate)
        {
            if (!HeldHere.Add(name))
                throw new InvalidOperationException(
                    "This process already holds the People First profile's lock. Pass the ProfileLock you hold " +
                    "(SessionOptions.HeldLock) rather than taking it again: the lock isn't re-entrant.");
        }
        var named = new NamedLock(name);
        try
        {
            if (!await named.AcquireAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false))
                throw new TimeoutException(
                    $"Another People First read on the same Edge profile didn't finish within {timeoutSeconds / 60:0} minutes.");
            return new ProfileLock(EdgeProfile.Normalise(edgeProfile), named);
        }
        catch
        {
            named.Dispose();
            lock (HeldGate)
                HeldHere.Remove(name);
            throw;
        }
    }

    /// <summary>Whether this lock is held and is the one for this profile.</summary>
    internal bool Covers(string edgeProfile) => IsHeld && Name == NameFor(edgeProfile);

    /// <summary>Let another tool have the profile. Safe to call twice; the object can't be used again.</summary>
    public void Release()
    {
        named.Release();
        Unregister();
    }

    public void Dispose()
    {
        named.Dispose();
        Unregister();
    }

    private void Unregister()
    {
        lock (HeldGate)
        {
            if (registered)
                HeldHere.Remove(Name);
            registered = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
