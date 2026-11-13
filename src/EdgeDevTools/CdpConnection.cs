using System.Collections.Concurrent;
using System.Text.Json;

namespace EdgeDevTools;

/// <summary>A DevTools protocol error: an error reply, or the connection closing.</summary>
public class CdpException : Exception
{
    public CdpException(string message) : base(message)
    {
    }

    public CdpException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>An event from the browser: its method, the session it came from (null for the browser itself), and
/// its parameters (an object, or Undefined when it had none).</summary>
public sealed record CdpEvent(string Method, string? SessionId, JsonElement Params);

/// <summary>
/// The DevTools protocol over a transport: numbered commands with replies, and events. Messages are written with
/// Utf8JsonWriter and read with JsonDocument, so there's no reflection anywhere (AOT- and trim-safe). Sessions are
/// "flat": a command for a page carries that page's sessionId.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    /// <summary>How long a command waits for its reply unless told otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ICdpTransport transport;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> waiting = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int nextId;
    private int started;

    public CdpConnection(ICdpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.transport = transport;
    }

    /// <summary>Every event, on the transport's reader thread, one at a time: keep handlers quick, and never wait
    /// on a command's reply inside one (the reply arrives on the same thread). An exception from a handler is
    /// passed to <see cref="HandlerError"/> and otherwise ignored.</summary>
    public event Action<CdpEvent>? Event;

    /// <summary>Called with any exception an <see cref="Event"/> handler throws.</summary>
    public Action<Exception>? HandlerError { get; set; }

    /// <summary>Completes when the connection has ended.</summary>
    public Task Closed => closed.Task;

    public bool IsClosed => closed.Task.IsCompleted;

    public ICdpTransport Transport => transport;

    /// <summary>Start reading messages. Subscribe to <see cref="Event"/> first, so nothing is missed.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("Already started.");
        transport.Start(Receive);
    }

    private void Receive(byte[]? message)
    {
        if (message is null)
        {
            closed.TrySetResult();
            foreach (var id in waiting.Keys)
                if (waiting.TryRemove(id, out var pending))
                    pending.TrySetException(new CdpException("The DevTools connection to Edge has closed."));
            return;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return;   // not ours to make sense of
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            string? session = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var number))
            {
                if (!waiting.TryRemove(number, out var reply))
                    return;
                if (root.TryGetProperty("error", out var error))
                    reply.TrySetException(new CdpException(error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()! : error.GetRawText()));
                else
                    reply.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
            else if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
            {
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                var handlers = Event;
                if (handlers is null)
                    return;
                var e = new CdpEvent(method.GetString()!, session, parameters);
                foreach (var handler in handlers.GetInvocationList())
                {
                    try
                    {
                        ((Action<CdpEvent>)handler)(e);
                    }
                    catch (Exception failure)
                    {
                        HandlerError?.Invoke(failure);
                    }
                }
            }
        }
    }

    /// <summary>Send a command and wait for its reply's result (an object). <paramref name="parameters"/> writes
    /// the members of the params object. Throws CdpException for an error reply or a closed connection, and
    /// TimeoutException when no reply comes in time.</summary>
    public async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? parameters = null, string? sessionId = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        if (IsClosed)
            throw new CdpException("The DevTools connection to Edge has closed.");
        var id = Interlocked.Increment(ref nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = reply;
        try
        {
            await transport.SendAsync(Message(id, method, parameters, sessionId)).ConfigureAwait(false);
            if (IsClosed)
                reply.TrySetException(new CdpException("The DevTools connection to Edge has closed."));
            var limit = timeout ?? DefaultTimeout;
            try
            {
                return await reply.Task.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"{method} had no reply in {limit.TotalSeconds:g} s.");
            }
        }
        finally
        {
            waiting.TryRemove(id, out _);
        }
    }

    internal static byte[] Message(int id, string method, Action<Utf8JsonWriter>? parameters, string? sessionId)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (sessionId is not null)
                writer.WriteString("sessionId", sessionId);
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            parameters?.Invoke(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Runtime.evaluate in a page or frame session: the value (returned by value, promises awaited), or
    /// an Undefined element when the script gave undefined. A script that throws is a CdpException.</summary>
    public async Task<JsonElement> EvaluateAsync(string sessionId, string expression, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("Runtime.evaluate", w =>
        {
            w.WriteString("expression", expression);
            w.WriteBoolean("returnByValue", true);
            w.WriteBoolean("awaitPromise", true);
        }, sessionId, timeout, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
            return default;
        if (result.TryGetProperty("exceptionDetails", out var failure))
            throw new CdpException("The page's script failed: " + (failure.TryGetProperty("text", out var text) ? text.GetString() : "no detail"));
        return result.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var v) ? v : default;
    }

    /// <summary>A left click at a point in the page's viewport, as a person's would arrive (move, press, release).</summary>
    public async Task ClickAsync(string sessionId, double x, double y, CancellationToken cancellationToken = default)
    {
        foreach (var kind in MouseClick)
        {
            await SendAsync("Input.dispatchMouseEvent", w =>
            {
                w.WriteString("type", kind);
                w.WriteNumber("x", x);
                w.WriteNumber("y", y);
                if (kind != "mouseMoved")
                {
                    w.WriteString("button", "left");
                    w.WriteNumber("clickCount", 1);
                }
            }, sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly string[] MouseClick = ["mouseMoved", "mousePressed", "mouseReleased"];

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
