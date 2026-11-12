using WinBackground;

// LockHelper hold <name>   take the lock, print "held" or "busy", then on a line from stdin:
//                          "release" releases it and prints "released"; "exit" ends the process still holding it
// LockHelper try <name>    exit 0 if the lock could be taken (and releases it), 1 if not
// LockHelper async <name>  take it, hop threads with await, release, and check it's free: prints "ok" or why not
switch (args)
{
    case ["hold", var name]:
    {
        var held = new NamedLock(name);
        if (!held.Acquire(0))
        {
            Console.WriteLine("busy");
            return 1;
        }
        Console.WriteLine("held");
        switch (Console.ReadLine())
        {
            case "release":
                held.Release();
                Console.WriteLine("released");
                return 0;
            default:
                Environment.Exit(3);   // still holding it: Windows abandons the mutex as the process ends
                return 3;
        }
    }
    case ["try", var name]:
    {
        using var attempt = new NamedLock(name);
        return attempt.Acquire(0) ? 0 : 1;
    }
    case ["async", var name]:
        return await AsyncRoundTrip(name);
    default:
        Console.Error.WriteLine("usage: LockHelper hold|try|async <name>");
        return 2;
}

static async Task<int> AsyncRoundTrip(string name)
{
    var first = new NamedLock(name);
    if (!await first.AcquireAsync(5))
    {
        Console.WriteLine("couldn't take it");
        return 1;
    }
    var acquiredOn = Environment.CurrentManagedThreadId;
    for (var i = 0; i < 50 && Environment.CurrentManagedThreadId == acquiredOn; i++)
        await Task.Delay(10).ConfigureAwait(false);
    first.Release();
    var free = false;
    var thread = new Thread(() =>
    {
        using var second = new NamedLock(name);
        free = second.Acquire(0);
    });
    thread.Start();
    thread.Join();
    Console.WriteLine(free ? "ok" : "still held after release");
    return free ? 0 : 1;
}
