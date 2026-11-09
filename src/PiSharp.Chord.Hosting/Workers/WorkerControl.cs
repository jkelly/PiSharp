using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Hosting.Plugins;

namespace PiSharp.Chord.Hosting.Workers;

public sealed record WorkerAddress(string Kind, JsonObject Parameters, string Key);
public sealed record WorkerStart(WorkerAddress Address, JsonObject Principal, JsonObject ParentPrincipal, PluginBuildSet Plugins, string LockDirectory, JsonNode? Data = null);
internal sealed record WorkerMessage(string Kind, WorkerStart? Start = null, PluginBuildSet? Plugins = null, long Id = 0, string? Error = null, int Failures = 0, bool? Idle = null);

/// <summary>Private native process control. Chord object traffic uses the separate, unchanged framed ILink.</summary>
internal sealed class WorkerControl : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly StreamReader reader;
    private readonly SemaphoreSlim writer = new(1, 1);
    internal WorkerControl(Stream stream) { this.stream = stream; reader = new(stream, new UTF8Encoding(false, true), false, 4096, true); }
    internal async Task SendAsync(WorkerMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message); if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("worker control exceeds limit");
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); await stream.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { writer.Release(); }
    }
    internal async Task<WorkerMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var line = new StringBuilder(); var buffer = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("worker control disconnected");
            if (buffer[0] == '\n') break;
            if (line.Length >= 4 * 1024 * 1024) throw new InvalidDataException("worker control exceeds limit");
            line.Append(buffer[0]);
        }
        return JsonSerializer.Deserialize<WorkerMessage>(line.ToString()) ?? throw new InvalidDataException("empty worker control message");
    }
    public ValueTask DisposeAsync() { reader.Dispose(); stream.Dispose(); writer.Dispose(); return ValueTask.CompletedTask; }
}
