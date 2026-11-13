using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EdgeDevTools;

/// <summary>A DevTools connection's bytes: whole JSON messages each way. <see cref="PipeTransport"/> is the real
/// one; tests supply a fake that answers commands itself.</summary>
public interface ICdpTransport : IAsyncDisposable
{
    /// <summary>The browser's process id, or 0 when there is no process (a fake).</summary>
    int ProcessId { get; }

    /// <summary>Start reading. <paramref name="onMessage"/> is called for every message received, on a background
    /// thread and one at a time, then once with null when the connection ends.</summary>
    void Start(Action<byte[]?> onMessage);

    /// <summary>Send one message (a whole JSON object).</summary>
    Task SendAsync(byte[] message);

    /// <summary>Wait up to the timeout for the browser to exit; true if it has (a fake: at once).</summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>End the browser and everything it started, now.</summary>
    void Kill();
}

/// <summary>
/// <c>--remote-debugging-pipe</c>: Edge reads commands from its C runtime fd 3 and writes replies to fd 4, each
/// message ending in a NUL. .NET's Process can't hand a child extra fds, so this calls CreateProcessW and passes the
/// two pipe handles the way the MSVC runtime inherits fds: STARTUPINFO.lpReserved2 holds (int count, byte
/// flags[count], HANDLE handles[count]), unaligned. Node, and so Playwright, does the same.
/// <para>Edge goes into a job object that ends every process in it when the job's last handle closes. So if this
/// process dies, however it dies, Edge and its children die too. (Edge also exits when the pipe closes; the job
/// covers an Edge that has stopped reading.)</para>
/// </summary>
public sealed unsafe class PipeTransport : ICdpTransport
{
    private readonly FileStream toEdge;
    private readonly FileStream fromEdge;
    private readonly SafeWaitHandle process;
    private readonly SafeWaitHandle job;
    private readonly Lock writeGate = new();
    private Thread? reader;
    private int disposed;

    private PipeTransport(int processId, SafeWaitHandle process, SafeWaitHandle job, FileStream toEdge, FileStream fromEdge)
    {
        ProcessId = processId;
        this.process = process;
        this.job = job;
        this.toEdge = toEdge;
        this.fromEdge = fromEdge;
    }

    public int ProcessId { get; }

    /// <summary>The STARTUPINFO for a launch: the CRT's inherited fds (the pipe on 3 and 4) in lpReserved2, and
    /// standard input, output and error all <b>the NUL device</b> (STARTF_USESTDHANDLES).
    /// <para>Why (13 Nov 2026): Edge's own logging ("ERROR:...sitelist_downloader.cc... Sitelist download failed",
    /// from a company IE-mode policy) appeared in a double-clicked tool's console, and read to its user as the tool
    /// failing. Edge is a GUI program: it writes to the standard handles it inherits, and if it has none it attaches
    /// to its parent's console (Chromium's RouteStdioToConsole), so null handles don't help; NUL handles do.</para></summary>
    internal static Native.STARTUPINFOW Startup(int reservedSize, nint reserved, nint nul) => new()
    {
        cb = (uint)sizeof(Native.STARTUPINFOW),
        cbReserved2 = (ushort)reservedSize,
        lpReserved2 = reserved,
        dwFlags = Native.STARTF_USESTDHANDLES,
        hStdInput = nul,
        hStdOutput = nul,
        hStdError = nul,
    };

    /// <summary>An inheritable handle to the NUL device, read and write.</summary>
    internal static nint OpenNul()
    {
        var inherit = new Native.SECURITY_ATTRIBUTES { nLength = (uint)sizeof(Native.SECURITY_ATTRIBUTES), bInheritHandle = 1 };
        const uint read = 0x80000000, write = 0x40000000, shareBoth = 3, openExisting = 3;
        var handle = Native.CreateFileW("NUL", read | write, shareBoth, ref inherit, openExisting, 0, 0);
        Check(handle != -1 && handle != 0, "CreateFile(NUL)");
        return handle;
    }

    /// <summary>Start the program with the DevTools pipe on fds 3 and 4, in a job that dies with this process. The
    /// arguments should include <c>--remote-debugging-pipe</c>. Its standard input, output and error are
    /// the NUL device, so nothing it prints reaches this process's console or output.</summary>
    public static PipeTransport Launch(string executable, IEnumerable<string> arguments, bool showWindow = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        var security = new Native.SECURITY_ATTRIBUTES { nLength = (uint)sizeof(Native.SECURITY_ATTRIBUTES), bInheritHandle = 1 };
        // fd 3: Edge reads, we write. fd 4: Edge writes, we read. Only Edge's ends are inherited.
        Check(Native.CreatePipe(out var edgeRead, out var ourWrite, ref security, 0), "CreatePipe");
        nint ourRead = 0, edgeWrite = 0, jobHandle = 0, nul = 0;
        var pi = default(Native.PROCESS_INFORMATION);
        var started = false;
        try
        {
            Check(Native.CreatePipe(out ourRead, out edgeWrite, ref security, 0), "CreatePipe");
            Check(Native.SetHandleInformation(ourWrite, Native.HANDLE_FLAG_INHERIT, 0), "SetHandleInformation");
            Check(Native.SetHandleInformation(ourRead, Native.HANDLE_FLAG_INHERIT, 0), "SetHandleInformation");

            jobHandle = Native.CreateJobObjectW(0, null);
            Check(jobHandle != 0, "CreateJobObject");
            var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | Native.JOB_OBJECT_LIMIT_BREAKAWAY_OK;
            Check(Native.SetInformationJobObject(jobHandle, Native.JobObjectExtendedLimitInformation, &limits,
                (uint)sizeof(Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)), "SetInformationJobObject");

            const int count = 5;
            var size = 4 + count + count * IntPtr.Size;
            var reserved = stackalloc byte[size];
            *(int*)reserved = count;
            var flags = reserved + 4;
            var handles = flags + count;
            for (var fd = 0; fd < count; fd++)
            {
                var handle = fd switch { 3 => edgeRead, 4 => edgeWrite, _ => -1 };
                flags[fd] = fd >= 3 ? (byte)(Native.FOPEN | Native.FPIPE) : (byte)0;
                Unsafe.WriteUnaligned(handles + fd * IntPtr.Size, handle);
            }

            var line = (CommandLine(executable, arguments) + "\0").ToCharArray();
            nul = OpenNul();
            var startup = Startup(size, (nint)reserved, nul);
            // Suspended, so Edge is in the job before it can start anything of its own.
            var creation = Native.CREATE_SUSPENDED | (showWindow ? 0 : Native.CREATE_NO_WINDOW);
            fixed (char* command = line)
                started = Native.CreateProcessW(null, command, 0, 0, 1, creation, 0, null, ref startup, out pi);
            if (!started)
                throw new InvalidOperationException($"Couldn't start {Path.GetFileName(executable)} (Windows error {Marshal.GetLastPInvokeError()}).");
            if (!Native.AssignProcessToJobObject(jobHandle, pi.hProcess))
            {
                var error = Marshal.GetLastPInvokeError();
                Native.TerminateProcess(pi.hProcess, 1);
                throw new InvalidOperationException($"Couldn't put {Path.GetFileName(executable)} in its job (Windows error {error}).");
            }
            Native.ResumeThread(pi.hThread);
        }
        catch
        {
            if (started)
                Native.CloseHandle(pi.hProcess);
            if (jobHandle != 0)
                Native.CloseHandle(jobHandle);
            Native.CloseHandle(ourWrite);
            if (ourRead != 0)
                Native.CloseHandle(ourRead);
            throw;
        }
        finally
        {
            Native.CloseHandle(edgeRead);
            if (edgeWrite != 0)
                Native.CloseHandle(edgeWrite);
            if (nul != 0)
                Native.CloseHandle(nul);
            if (started)
                Native.CloseHandle(pi.hThread);
        }
        return new PipeTransport((int)pi.dwProcessId, new SafeWaitHandle(pi.hProcess, true), new SafeWaitHandle(jobHandle, true),
            new FileStream(new SafeFileHandle(ourWrite, true), FileAccess.Write, 0, false),
            new FileStream(new SafeFileHandle(ourRead, true), FileAccess.Read, 0, false));
    }

    public void Start(Action<byte[]?> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        if (reader is not null)
            throw new InvalidOperationException("Already started.");
        reader = new Thread(() => ReadLoop(onMessage)) { IsBackground = true, Name = "edge-devtools-pipe" };
        reader.Start();
    }

    private void ReadLoop(Action<byte[]?> onMessage)
    {
        var pending = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = fromEdge.Read(buffer, 0, buffer.Length)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != 0)
                        continue;
                    pending.Write(buffer, start, i - start);
                    onMessage(pending.ToArray());
                    pending.SetLength(0);
                    start = i + 1;
                }
                pending.Write(buffer, start, read - start);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        onMessage(null);
    }

    public Task SendAsync(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (writeGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            try
            {
                toEdge.Write(message);
                toEdge.WriteByte(0);
                toEdge.Flush();
            }
            catch (IOException e)
            {
                throw new CdpException("The DevTools connection to Edge has closed.", e);
            }
        }
        return Task.CompletedTask;
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        if (process.IsClosed)
            return true;
        var milliseconds = (uint)Math.Clamp((long)timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
        return Native.WaitForSingleObject(process.DangerousGetHandle(), milliseconds) == 0;
    }

    public void Kill()
    {
        if (!job.IsClosed)
            Native.TerminateJobObject(job.DangerousGetHandle(), 1);
    }

    /// <summary>Closes the pipe (Edge exits when it sees that) and the job (which ends anything still running).
    /// Call <see cref="WaitForExit"/> first for a clean close.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return ValueTask.CompletedTask;
        lock (writeGate)
        {
            try
            {
                toEdge.Dispose();
            }
            catch (IOException)
            {
            }
        }
        try
        {
            fromEdge.Dispose();
        }
        catch (IOException)
        {
        }
        job.Dispose();      // kill on close: anything left in the job ends here
        process.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A Windows command line that the MSVC runtime (and so Edge) splits back into the same arguments.</summary>
    internal static string CommandLine(string executable, IEnumerable<string> arguments)
    {
        var line = new StringBuilder();
        Append(line, executable);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Append(line, argument);
        }
        return line.ToString();

        static void Append(StringBuilder line, string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                line.Append(argument);
                return;
            }
            line.Append('"');
            var backslashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    line.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    line.Append('\\', backslashes).Append(c);
                }
                backslashes = 0;
            }
            line.Append('\\', backslashes * 2).Append('"');
        }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException($"{what} failed (Windows error {Marshal.GetLastPInvokeError()}).");
    }
}
