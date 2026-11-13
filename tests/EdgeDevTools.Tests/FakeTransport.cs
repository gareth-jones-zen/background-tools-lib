using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EdgeDevTools.Tests;

/// <summary>A transport with no browser: each command is answered by <see cref="Answer"/> (null: no reply), and
/// tests push events. Messages are delivered one at a time on a background task, as the pipe's reader thread does.</summary>
internal sealed class FakeTransport : ICdpTransport
{
    private readonly Channel<byte[]?> inbox = Channel.CreateUnbounded<byte[]?>();
    private Task? pump;

    public List<(string Method, string? Session, JsonElement Params)> Sent { get; } = [];

    /// <summary>(method, session, params) to the result object's JSON, an error ("!message"), or null for no reply.</summary>
    public Func<string, string?, JsonElement, string?> Answer { get; set; } = (_, _, _) => "{}";

    public int ProcessId => 0;

    public void Start(Action<byte[]?> onMessage) =>
        pump = Task.Run(async () =>
        {
            await foreach (var message in inbox.Reader.ReadAllAsync())
            {
                onMessage(message);
                if (message is null)
                    return;
            }
        });

    public Task SendAsync(byte[] message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt32();
        var method = root.GetProperty("method").GetString()!;
        var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        var parameters = root.GetProperty("params").Clone();
        lock (Sent)
            Sent.Add((method, session, parameters));
        var answer = Answer(method, session, parameters);
        if (answer is null)
            return Task.CompletedTask;
        var sessionPart = session is null ? "" : $",\"sessionId\":{JsonSerializerString(session)}";
        Push(answer.StartsWith('!')
            ? $"{{\"id\":{id}{sessionPart},\"error\":{{\"code\":-32000,\"message\":{JsonSerializerString(answer[1..])}}}}}"
            : $"{{\"id\":{id}{sessionPart},\"result\":{answer}}}");
        return Task.CompletedTask;
    }

    public void Push(string json) => inbox.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

    public void Event(string method, string paramsJson, string? session = null) =>
        Push($"{{\"method\":\"{method}\",\"params\":{paramsJson}{(session is null ? "" : $",\"sessionId\":\"{session}\"")}}}");

    public void Close() => inbox.Writer.TryWrite(null);

    public bool WaitForExit(TimeSpan timeout) => true;

    public void Kill() => Close();

    public async ValueTask DisposeAsync()
    {
        Close();
        if (pump is not null)
            await pump;
    }

    public static string JsonSerializerString(string value) => PageScripts.JsonString(value);
}
