using System.Diagnostics;
using System.Text;

namespace WinBackground.Tests;

/// <summary>NamedLock: a real named mutex, so these use real Windows objects with unique names. Other processes
/// are LockHelper (a NamedLock in another process) and Windows PowerShell (a bare mutex, as the Python tools use).</summary>
public class LockTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string NewName() => "wb-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_single_instance_lock_is_held_once_until_released()
    {
        var name = NewName();
        using (var first = Locks.SingleInstance(name))
        {
            Assert.NotNull(first);
            Assert.True(first.IsHeld);
            Assert.Null(Locks.SingleInstance(name));   // not re-entrant, even on the same thread
            Assert.False(OnOtherThread(name));
        }
        Assert.True(OnOtherThread(name));
    }

    [Fact]
    public async Task A_lock_released_on_another_thread_after_an_await_is_free_in_this_process_and_others()
    {
        // The bug this design fixes: a bare mutex released on the wrong thread isn't released at all. While anyone
        // else has the mutex open (a waiter, here and in another process) it stays held until the process exits.
        var name = NewName();
        var held = new NamedLock(name);
        Assert.True(held.Acquire(0));
        Assert.Equal(1, await RawMutexInAnotherProcess(name));   // held, as a Python tool would see it

        using var waiter = new NamedLock(name);
        var waitingHere = waiter.AcquireAsync(30, Cancel);
        using var shell = StartRawMutex(name, 30_000);
        Assert.Equal("waiting", await ReadLine(shell));
        await HopThreads();
        held.Release();
        Assert.False(held.IsHeld);

        // Each waiter gets it in turn, whichever is first.
        Assert.True(await waitingHere.WaitAsync(Patience, Cancel));
        waiter.Release();
        await shell.WaitForExitAsync(Cancel).WaitAsync(Patience, Cancel);
        Assert.Equal(0, shell.ExitCode);
        Assert.True(OnOtherThread(name));
        Assert.Equal(0, await RunHelper("try", name));
    }

    [Fact]
    public async Task AcquireAsync_then_dispose_after_an_await_frees_it()
    {
        var name = NewName();
        var held = new NamedLock(name);
        Assert.True(await held.AcquireAsync(5, Cancel));
        await HopThreads();
        held.Dispose();
        Assert.False(held.IsHeld);
        Assert.Equal(0, await RunHelper("try", name));
    }

    [Fact]
    public async Task Another_process_holding_it_keeps_it_until_that_process_releases_it()
    {
        var name = NewName();
        using var holder = StartHelper("hold", name);
        Assert.Equal("held", await ReadLine(holder));
        using var waiter = new NamedLock(name);
        Assert.False(waiter.Acquire(0));

        var waiting = waiter.AcquireAsync(30, Cancel);
        await Task.Delay(200, Cancel);
        Assert.False(waiting.IsCompleted);
        await holder.StandardInput.WriteLineAsync("release");
        await holder.StandardInput.FlushAsync(Cancel);

        Assert.True(await waiting.WaitAsync(Patience, Cancel));
        Assert.False(waiter.WasAbandoned);
        Assert.Equal("released", await ReadLine(holder));
        await holder.WaitForExitAsync(Cancel);
        Assert.Equal(0, holder.ExitCode);
    }

    [Fact]
    public async Task A_process_that_exits_still_holding_it_frees_it_as_abandoned()
    {
        var name = NewName();
        using var holder = StartHelper("hold", name);
        Assert.Equal("held", await ReadLine(holder));
        using var waiter = new NamedLock(name);
        Assert.False(waiter.Acquire(0));

        var waiting = waiter.AcquireAsync(30, Cancel);
        await holder.StandardInput.WriteLineAsync("exit");
        await holder.StandardInput.FlushAsync(Cancel);

        Assert.True(await waiting.WaitAsync(Patience, Cancel));
        Assert.True(waiter.WasAbandoned);
        await holder.WaitForExitAsync(Cancel);
        Assert.Equal(3, holder.ExitCode);
        waiter.Release();
        Assert.Equal(0, await RawMutexInAnotherProcess(name));
    }

    [Fact]
    public void Try_once_returns_at_once_and_a_timeout_waits_about_that_long()
    {
        var name = NewName();
        using var holder = new NamedLock(name);
        Assert.True(holder.Acquire(0));
        using var other = new NamedLock(name);

        var clock = Stopwatch.StartNew();
        Assert.False(other.Acquire(0));
        Assert.True(clock.ElapsedMilliseconds < 2000, $"try once took {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        Assert.False(other.Acquire(0.3));
        Assert.InRange(clock.ElapsedMilliseconds, 250, 5000);
        Assert.False(other.IsHeld);
        Assert.Throws<ArgumentOutOfRangeException>(() => other.Acquire(double.NaN));
    }

    [Fact]
    public async Task A_negative_timeout_waits_until_the_holder_releases_from_another_thread()
    {
        var name = NewName();
        var holder = new NamedLock(name);
        Assert.True(holder.Acquire(0));
        using var waiter = new NamedLock(name);

        var waiting = waiter.AcquireAsync(-1, Cancel);
        await Task.Delay(200, Cancel);
        Assert.False(waiting.IsCompleted);
        await Task.Run(holder.Release, Cancel);

        Assert.True(await waiting.WaitAsync(Patience, Cancel));
        Assert.True(waiter.IsHeld);
    }

    [Fact]
    public void Releasing_twice_is_safe_and_the_lock_can_be_taken_again()
    {
        var name = NewName();
        var held = new NamedLock(name);
        held.Release();   // never held: nothing to do
        Assert.True(held.Acquire(0));
        held.Release();
        held.Release();
        Assert.True(OnOtherThread(name));

        Assert.True(held.Acquire(0));
        Assert.False(OnOtherThread(name));
        held.Dispose();
        held.Dispose();
        held.Release();
        Assert.True(OnOtherThread(name));
        Assert.Throws<ObjectDisposedException>(() => held.Acquire(0));
    }

    [Fact]
    public void Disposing_without_releasing_frees_it()
    {
        var name = NewName();
        using (var held = new NamedLock(name))
            Assert.True(held.Acquire(0));
        Assert.True(OnOtherThread(name));
    }

    [Fact]
    public void Acquiring_twice_with_one_object_is_refused()
    {
        using var held = new NamedLock(NewName());
        Assert.True(held.Acquire(0));
        Assert.Throws<InvalidOperationException>(() => held.Acquire(0));
        Assert.True(held.IsHeld);
    }

    [Fact]
    public async Task Cancelling_a_wait_throws_and_leaves_the_object_usable()
    {
        var name = NewName();
        var holder = new NamedLock(name);
        Assert.True(holder.Acquire(0));
        using var waiter = new NamedLock(name);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        cancel.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.AcquireAsync(-1, cancel.Token));
        Assert.False(waiter.IsHeld);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.AcquireAsync(-1, cancel.Token));

        holder.Release();
        Assert.True(waiter.Acquire(0));
    }

    [Fact]
    public async Task Disposing_during_a_wait_stops_it_and_leaks_nothing()
    {
        var name = NewName();
        var holder = new NamedLock(name);
        Assert.True(holder.Acquire(0));
        var waiter = new NamedLock(name);

        var waiting = waiter.AcquireAsync(-1, Cancel);
        await Task.Delay(200, Cancel);
        waiter.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(Patience, Cancel));

        holder.Release();
        Assert.True(OnOtherThread(name));
    }

    [Fact]
    public async Task A_NamedLock_in_another_process_survives_its_own_awaits()
    {
        Assert.Equal(0, await RunHelper("async", NewName()));
    }

    /// <summary>Await until the continuation runs on a different thread; returns the thread it started on.</summary>
    private static async Task<int> HopThreads()
    {
        var start = Environment.CurrentManagedThreadId;
        for (var i = 0; i < 100 && Environment.CurrentManagedThreadId == start; i++)
        {
            await Task.Yield();
            await Task.Delay(5, Cancel).ConfigureAwait(false);
        }
        Assert.NotEqual(start, Environment.CurrentManagedThreadId);
        return start;
    }

    /// <summary>Whether another thread can take the lock (and release it again).</summary>
    private static bool OnOtherThread(string name)
    {
        var got = false;
        var thread = new Thread(() =>
        {
            using var attempt = new NamedLock(name);
            got = attempt.Acquire(0);
        });
        thread.Start();
        thread.Join();
        return got;
    }

    private static Process StartHelper(params string[] arguments) =>
        Start(Path.Combine(AppContext.BaseDirectory, "LockHelper.exe"), arguments);

    private static async Task<int> RunHelper(params string[] arguments)
    {
        using var helper = StartHelper(arguments);
        helper.StandardInput.Close();
        await helper.WaitForExitAsync(Cancel).WaitAsync(Patience, Cancel);
        return helper.ExitCode;
    }

    /// <summary>Exit code 0 if another process could take the same Local\ mutex at once (and released it), 1 if not.</summary>
    private static async Task<int> RawMutexInAnotherProcess(string name)
    {
        using var shell = StartRawMutex(name, 0);
        Assert.Equal("waiting", await ReadLine(shell));
        await shell.WaitForExitAsync(Cancel).WaitAsync(Patience, Cancel);
        return shell.ExitCode;
    }

    /// <summary>Windows PowerShell opening the same Local\ mutex with .NET Framework's Mutex, which is CreateMutex
    /// underneath, as the Python tools' ctypes call is. It prints "waiting" once the mutex is open, then waits up to
    /// the timeout; exit code 0 if it got it (and released it), 1 if not.</summary>
    private static Process StartRawMutex(string name, int milliseconds)
    {
        var script = $$"""
            $ProgressPreference = 'SilentlyContinue'
            $m = New-Object System.Threading.Mutex($false, 'Local\{{name}}')
            [Console]::Out.WriteLine('waiting'); [Console]::Out.Flush()
            try { $got = $m.WaitOne({{milliseconds}}) } catch [System.Threading.AbandonedMutexException] { $got = $true }
            if ($got) { $m.ReleaseMutex(); exit 0 } else { exit 1 }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var shell = Start("powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded);
        shell.StandardInput.Close();
        return shell;
    }

    private static Process Start(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {file}.");
    }

    private static async Task<string?> ReadLine(Process process) =>
        await process.StandardOutput.ReadLineAsync(Cancel).AsTask().WaitAsync(Patience, Cancel);
}
