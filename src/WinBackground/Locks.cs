using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WinBackground;

/// <summary>A named lock between processes: a mutex in the session's Local namespace (<c>Local\{name}</c>, the
/// same object the Python tools' win-background locks use). Windows releases a mutex when the process holding it
/// dies, so a crash can never leave a lock stuck, which a lock file can.
/// <para>A Win32 mutex belongs to the thread that took it, and only that thread can release it. So each held
/// lock has a small thread of its own that takes the mutex, waits, and releases it when asked. Acquire and Release
/// can then be called from any thread, including either side of an <c>await</c>.</para>
/// <para>Two NamedLocks with the same name exclude each other inside one process too, even on one thread: unlike
/// a bare mutex, the lock is not re-entrant. A lock that is never released (or whose object is dropped) stays
/// held until the process exits, as a single-instance lock should.</para></summary>
public sealed class NamedLock : IDisposable
{
    private readonly Lock gate = new();
    private Owner? owner;   // from the start of an acquire until its release has finished
    private bool disposed;

    public NamedLock(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    public string Name { get; }

    /// <summary>Whether this object holds the lock now.</summary>
    public bool IsHeld
    {
        get
        {
            lock (gate)
                return owner is { Held: true };
        }
    }

    /// <summary>Whether the last successful acquire found the mutex abandoned: its previous holder (a process,
    /// or a thread) ended without releasing it. The lock is still ours; this only says the last holder crashed.</summary>
    public bool WasAbandoned { get; private set; }

    /// <summary>Wait up to timeout seconds; 0 means try once, and a negative timeout waits indefinitely. Blocks
    /// the calling thread while it waits: in async code, prefer AcquireAsync.</summary>
    /// <exception cref="InvalidOperationException">This object already holds the lock, or is acquiring it.</exception>
    public bool Acquire(double timeoutSeconds = 0)
    {
        var attempt = Start(timeoutSeconds, CancellationToken.None);
        Outcome outcome;
        try
        {
            outcome = attempt.Result.Task.GetAwaiter().GetResult();
        }
        catch
        {
            Abandon(attempt);
            throw;
        }
        return Settle(attempt, outcome, CancellationToken.None);
    }

    /// <summary>As Acquire, without blocking a thread while it waits. Cancelling stops the wait and throws
    /// OperationCanceledException, unless the lock was taken first, in which case it is held and true returned.</summary>
    public async Task<bool> AcquireAsync(double timeoutSeconds = 0, CancellationToken cancellationToken = default)
    {
        var attempt = Start(timeoutSeconds, cancellationToken);
        Outcome outcome;
        try
        {
            outcome = await attempt.Result.Task.ConfigureAwait(false);
        }
        catch
        {
            Abandon(attempt);
            throw;
        }
        return Settle(attempt, outcome, cancellationToken);
    }

    /// <summary>Release the lock, from any thread. Does nothing if it isn't held, so releasing twice is safe.</summary>
    /// <exception cref="InvalidOperationException">Windows refused the release, or another thread is still
    /// acquiring the lock with this object.</exception>
    public void Release()
    {
        lock (gate)
        {
            if (owner is not { } held)
                return;
            if (!held.Held)
                throw new InvalidOperationException($"The lock {Name} is still being acquired, so it can't be released yet.");
            Finish(held);
        }
    }

    /// <summary>Releases the lock if held, and stops an acquire in progress (which then throws
    /// ObjectDisposedException). The object can't be used again.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            if (owner is { } current)
                Finish(current);
        }
    }

    private Owner Start(double timeoutSeconds, CancellationToken cancellationToken)
    {
        var milliseconds = Milliseconds(timeoutSeconds);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (owner is not null)
                throw new InvalidOperationException(owner.Held
                    ? $"The lock {Name} is already held by this object."
                    : $"The lock {Name} is already being acquired by this object.");
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = new Owner(Name, milliseconds);
            owner = attempt;
            try
            {
                if (cancellationToken.CanBeCanceled)
                    attempt.Registration = cancellationToken.Register(static state => ((Owner)state!).StopWaiting(), attempt);
                // Small stack: the thread only waits. Background, so a held lock never keeps the process alive.
                new Thread(attempt.Run, 256 * 1024) { IsBackground = true, Name = $"NamedLock {Name}" }.Start();
            }
            catch
            {
                CleanUp(attempt);
                throw;
            }
            return attempt;
        }
    }

    private bool Settle(Owner attempt, Outcome outcome, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (outcome is Outcome.Acquired or Outcome.Abandoned && owner == attempt && !disposed)
            {
                attempt.Held = true;
                attempt.Registration.Dispose();
                WasAbandoned = outcome == Outcome.Abandoned;
                return true;
            }
            // Not acquired, or Dispose got there first (and has already released it). The thread is done with
            // the mutex by the time it reports anything other than success.
            CleanUp(attempt);
        }
        ObjectDisposedException.ThrowIf(disposed, this);
        if (outcome == Outcome.Stopped)
            throw new OperationCanceledException(cancellationToken);
        return false;   // timed out, or the mutex belongs to someone we can't open it as (an elevated process)
    }

    private void Abandon(Owner attempt)
    {
        lock (gate)
            CleanUp(attempt);
    }

    /// <summary>Under the gate: tell the owning thread to let go, wait for it to, and check it did.</summary>
    private void Finish(Owner current)
    {
        current.StopWaiting();
        current.ReleaseNow.Set();
        try
        {
            current.Done.Task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // The thread has ended, so Windows treats the mutex as abandoned and the next waiter still gets it;
            // but something is badly wrong, so say so.
            throw new InvalidOperationException($"Couldn't release the lock {Name}: {e.Message}", e);
        }
        finally
        {
            CleanUp(current);
        }
    }

    /// <summary>Under the gate, once the owning thread has finished with the events.</summary>
    private void CleanUp(Owner attempt)
    {
        if (owner == attempt)
            owner = null;
        attempt.Dispose();
    }

    private static int Milliseconds(double timeoutSeconds)
    {
        if (double.IsNaN(timeoutSeconds))
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "The timeout can't be NaN.");
        if (timeoutSeconds < 0 || double.IsPositiveInfinity(timeoutSeconds))
            return Timeout.Infinite;
        return (int)Math.Min(int.MaxValue, Math.Round(timeoutSeconds * 1000));
    }

    private enum Outcome { Acquired, Abandoned, TimedOut, Denied, Stopped }

    /// <summary>One acquire, and the thread that owns the mutex for it.</summary>
    private sealed class Owner(string name, int milliseconds) : IDisposable
    {
        public readonly TaskCompletionSource<Outcome> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim ReleaseNow = new(false);
        private readonly ManualResetEvent stopWaiting = new(false);   // cancelled or disposed while waiting
        private readonly Lock eventsGate = new();
        private bool disposed;
        public bool Held;                                    // guarded by NamedLock.gate
        public CancellationTokenRegistration Registration;

        public void StopWaiting()
        {
            lock (eventsGate)
            {
                if (!disposed)
                    stopWaiting.Set();
            }
        }

        public void Run()
        {
            Mutex? mutex = null;
            Outcome? failed = null;
            try
            {
                try
                {
                    mutex = new Mutex(false, $@"Local\{name}");
                }
                catch (UnauthorizedAccessException)
                {
                    failed = Outcome.Denied;   // it exists, made by a process we can't open it as: someone else's
                    return;
                }
                var outcome = Outcome.Acquired;
                try
                {
                    var index = WaitHandle.WaitAny([mutex, stopWaiting], milliseconds);
                    if (index != 0)
                        outcome = index == WaitHandle.WaitTimeout ? Outcome.TimedOut : Outcome.Stopped;
                }
                catch (AbandonedMutexException)
                {
                    outcome = Outcome.Abandoned;   // the last holder died, so it's ours
                }
                if (outcome is not (Outcome.Acquired or Outcome.Abandoned))
                {
                    failed = outcome;
                    return;
                }
                Result.TrySetResult(outcome);
                ReleaseNow.Wait();
                mutex.ReleaseMutex();   // throws if this thread doesn't own it: never silent
            }
            catch (Exception e)
            {
                if (!Result.TrySetException(e))
                    Done.TrySetException(e);
            }
            finally
            {
                mutex?.Dispose();
                // Report a failure only now, so the handle is closed before anyone acts on it.
                if (failed is { } report)
                    Result.TrySetResult(report);
                Done.TrySetResult();
            }
        }

        public void Dispose()
        {
            lock (eventsGate)
            {
                if (disposed)
                    return;
                disposed = true;
            }
            Registration.Dispose();   // waits for a cancellation callback in progress
            stopWaiting.Dispose();
            ReleaseNow.Dispose();
        }
    }
}

/// <summary>One watcher at a time. Two names that are equal hold the same lock across every tool, so name
/// locks after the thing they protect: Key() turns a path into a short stable name.</summary>
public static class Locks
{
    /// <summary>16 hex characters from a path or other text, ignoring case (Windows paths).</summary>
    public static string Key(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..16];

    /// <summary>The lock for a long-running process, such as a watcher: held until it exits or releases it.
    /// Null if another process, or another NamedLock in this one, already holds it.</summary>
    public static NamedLock? SingleInstance(string name)
    {
        var lockObject = new NamedLock(name);
        if (lockObject.Acquire(0))
            return lockObject;
        lockObject.Dispose();
        return null;
    }

    /// <summary>Whether some process (this one included) has the lock open, in practice holds it, without ever
    /// acquiring it. A probe that acquired it, even for a moment, could make a watcher starting at that moment
    /// think another was running and exit. The mutex exists only while a process has it open, and a holder keeps
    /// it open. A NamedLock that is still waiting to acquire has it open too, so this says true then as well.</summary>
    public static bool Held(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        const uint synchronize = 0x00100000;
        const int accessDenied = 5;
        var handle = Native.OpenMutexW(synchronize, false, $@"Local\{name}");
        if (handle != 0)
        {
            Native.CloseHandle(handle);
            return true;
        }
        return Marshal.GetLastPInvokeError() == accessDenied;   // there, but someone else's to open
    }
}
