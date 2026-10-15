// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/src/runtime/protocol.ts. Messages between the host and the
// engine (a thread or a child process). Tool arguments, results and values cross as JSON strings, as in the original. The
// deterministic fields (VmStartData.deterministic, a result's seq, now and properties, done's during, started and idle) serve
// CodemodeSandbox.Start(); `exhausted` reports an interrupt budget used up, which inline.ts turns into a timeout in its launcher.
using System.Text;
using System.Text.Json;

namespace PiSharp.Codemode;

/// <summary>worker.ts workerData: the script, the tool and global tables, the store snapshot and the engine limits.</summary>
internal sealed record CodemodeStartMessage(string Code, string ToolsJson, string GlobalsJson, string StoreJson,
    long MemoryLimitBytes, long TotalAllocationLimitBytes, int RecursionLimit)
{
    /// <summary>VmStartData.deterministic: run as <c>CodemodeSandbox.Start()</c> does (UTC, a virtual clock from <c>StartedAt</c> that
    /// moves only with delivered results, randomness from <c>Seed</c>, no <c>WeakRef</c>, results applied in <c>seq</c> order, and
    /// <c>started</c>/<c>idle</c> messages).</summary>
    public CodemodeDeterministicStart? Deterministic { get; init; }
    /// <summary>inline.ts interruptBudget: Jint statements the whole execution may run before it fails with a timeout. Null: none.</summary>
    public long? InterruptBudget { get; init; }
}

/// <summary>VmStartData.deterministic.</summary>
internal sealed record CodemodeDeterministicStart(double StartedAt, uint Seed);

/// <summary>WorkerToHostMessage.</summary>
internal abstract record CodemodeWorkerMessage;
internal sealed record CodemodeCallMessage(long Id, bool Tool, string Name, string? Arguments) : CodemodeWorkerMessage;
internal sealed record CodemodeOutputMessage(CodemodeOutputItem Item) : CodemodeWorkerMessage;
/// <summary><c>Error</c> is the JSON <c>{ name?, message, stack? }</c> of a failed script; <c>Writes</c> the store writes of a
/// successful one.</summary>
internal sealed record CodemodeDoneMessage(bool Ok, string? Value, string? Writes, string? Error) : CodemodeWorkerMessage
{
    /// <summary>Deterministic mode: the result the engine was processing when the script finished (0: its first run) and the calls
    /// issued by then.</summary>
    public CodemodeDuring? During { get; init; }
}
/// <summary>The engine failed outside the script's control.</summary>
internal sealed record CodemodeCrashMessage(string Message) : CodemodeWorkerMessage;
/// <summary>The engine is set up and the script starts now: the deadline counts from here, so starting a worker process is not
/// charged to the script.</summary>
internal sealed record CodemodeReadyMessage : CodemodeWorkerMessage;
/// <summary>Deterministic mode, before the script runs: what the engine is, so a replay can refuse a different one.</summary>
internal sealed record CodemodeStartedMessage(string Fingerprint) : CodemodeWorkerMessage;
/// <summary>Deterministic mode, after the script waits again: having processed result <c>Seq</c> (0: the script's first run), it has
/// issued <c>Issued</c> calls in total.</summary>
internal sealed record CodemodeIdleMessage(long Seq, long Issued) : CodemodeWorkerMessage;
/// <summary>The execution used up its interrupt budget (inline.ts reports this as a timeout).</summary>
internal sealed record CodemodeExhaustedMessage(long Budget) : CodemodeWorkerMessage;

/// <summary>HostToWorkerMessage: <c>Payload</c> is the JSON result when ok, otherwise the error message.</summary>
internal sealed record CodemodeResultMessage(long Id, bool Ok, string? Payload)
{
    /// <summary>Deterministic mode: numbers the results from 1 in the order the engine applies them.</summary>
    public long? Seq { get; init; }
    /// <summary>Deterministic mode: the engine's clock while it processes this result, in ms since the epoch.</summary>
    public double? Now { get; init; }
    /// <summary>Deterministic mode, not ok: a JSON object whose properties the script's <c>Error</c> gets.</summary>
    public string? Properties { get; init; }
}

/// <summary>One JSON object per line over the worker process's standard input and output.</summary>
internal static class CodemodeWireCodec
{
    private static readonly JsonWriterOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Write(Action<Utf8JsonWriter> fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Options)) { writer.WriteStartObject(); fields(writer); writer.WriteEndObject(); }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void Optional(Utf8JsonWriter writer, string name, string? value) { if (value is not null) writer.WriteString(name, value); }

    public static string Encode(CodemodeStartMessage start) => Write(writer =>
    {
        writer.WriteString("type", "start"); writer.WriteString("code", start.Code); writer.WriteString("tools", start.ToolsJson);
        writer.WriteString("globals", start.GlobalsJson); writer.WriteString("store", start.StoreJson);
        writer.WriteNumber("memory", start.MemoryLimitBytes); writer.WriteNumber("total", start.TotalAllocationLimitBytes);
        writer.WriteNumber("recursion", start.RecursionLimit);
        if (start.Deterministic is { } deterministic) { writer.WriteNumber("startedAt", deterministic.StartedAt); writer.WriteNumber("seed", deterministic.Seed); }
        if (start.InterruptBudget is { } budget) writer.WriteNumber("budget", budget);
    });

    public static string Encode(CodemodeResultMessage result) => Write(writer =>
    {
        writer.WriteString("type", "result"); writer.WriteNumber("id", result.Id); writer.WriteBoolean("ok", result.Ok);
        Optional(writer, "payload", result.Payload);
        if (result.Seq is { } seq) writer.WriteNumber("seq", seq);
        if (result.Now is { } now) writer.WriteNumber("now", now);
        Optional(writer, "properties", result.Properties);
    });

    public static string Encode(CodemodeWorkerMessage message) => Write(writer =>
    {
        switch (message)
        {
            case CodemodeCallMessage call:
                writer.WriteString("type", "call"); writer.WriteNumber("id", call.Id); writer.WriteString("target", call.Tool ? "tool" : "global");
                writer.WriteString("name", call.Name); Optional(writer, "args", call.Arguments); break;
            case CodemodeOutputMessage { Item: CodemodeTextOutput text }:
                writer.WriteString("type", "output"); writer.WriteString("kind", text.Console ? "console" : "text"); writer.WriteString("data", text.Text); break;
            case CodemodeOutputMessage { Item: CodemodeImageOutput image }:
                writer.WriteString("type", "output"); writer.WriteString("kind", "image"); writer.WriteString("data", image.Data);
                writer.WriteString("mimeType", image.MimeType); break;
            case CodemodeDoneMessage done:
                writer.WriteString("type", "done"); writer.WriteBoolean("ok", done.Ok); Optional(writer, "value", done.Value);
                Optional(writer, "writes", done.Writes); Optional(writer, "error", done.Error);
                if (done.During is { } during) { writer.WriteNumber("duringSeq", during.Seq); writer.WriteNumber("duringIssued", during.Issued); }
                break;
            case CodemodeCrashMessage crash:
                writer.WriteString("type", "crash"); writer.WriteString("message", crash.Message); break;
            case CodemodeReadyMessage:
                writer.WriteString("type", "ready"); break;
            case CodemodeStartedMessage started:
                writer.WriteString("type", "started"); writer.WriteString("fingerprint", started.Fingerprint); break;
            case CodemodeIdleMessage idle:
                writer.WriteString("type", "idle"); writer.WriteNumber("seq", idle.Seq); writer.WriteNumber("issued", idle.Issued); break;
            case CodemodeExhaustedMessage exhausted:
                writer.WriteString("type", "exhausted"); writer.WriteNumber("budget", exhausted.Budget); break;
            default: throw new ArgumentException("Unknown worker message.", nameof(message));
        }
    });

    private static string? Text(JsonElement value, string name, bool required = true) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()
            : required ? throw new FormatException($"Missing {name}.") : null;

    public static CodemodeStartMessage DecodeStart(string line)
    {
        using var document = JsonDocument.Parse(line); var value = document.RootElement;
        if (Text(value, "type") != "start") throw new FormatException("Expected a start message.");
        return new(Text(value, "code")!, Text(value, "tools")!, Text(value, "globals")!, Text(value, "store")!,
            value.GetProperty("memory").GetInt64(), value.GetProperty("total").GetInt64(), value.GetProperty("recursion").GetInt32())
        {
            Deterministic = value.TryGetProperty("startedAt", out var startedAt) ? new(startedAt.GetDouble(), value.GetProperty("seed").GetUInt32()) : null,
            InterruptBudget = value.TryGetProperty("budget", out var budget) ? budget.GetInt64() : null
        };
    }

    public static CodemodeResultMessage DecodeResult(string line)
    {
        using var document = JsonDocument.Parse(line); var value = document.RootElement;
        if (Text(value, "type") != "result") throw new FormatException("Expected a result message.");
        return new(value.GetProperty("id").GetInt64(), value.GetProperty("ok").GetBoolean(), Text(value, "payload", false))
        {
            Seq = value.TryGetProperty("seq", out var seq) ? seq.GetInt64() : null,
            Now = value.TryGetProperty("now", out var now) ? now.GetDouble() : null,
            Properties = Text(value, "properties", false)
        };
    }

    /// <summary>isWorkerToHostMessage: anything else is a broken bridge.</summary>
    public static CodemodeWorkerMessage DecodeWorker(string line)
    {
        using var document = JsonDocument.Parse(line); var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Not an object.");
        return Text(value, "type") switch
        {
            "call" => new CodemodeCallMessage(value.GetProperty("id").GetInt64(), Text(value, "target") == "tool", Text(value, "name")!, Text(value, "args", false)),
            "output" => new CodemodeOutputMessage(Text(value, "kind") switch
            {
                "image" => new CodemodeImageOutput(Text(value, "data")!, Text(value, "mimeType")!),
                "console" => new CodemodeTextOutput(Text(value, "data")!, true),
                "text" => new CodemodeTextOutput(Text(value, "data")!),
                _ => throw new FormatException("Unknown output kind.")
            }),
            "done" => new CodemodeDoneMessage(value.GetProperty("ok").GetBoolean(), Text(value, "value", false), Text(value, "writes", false), Text(value, "error", false))
            {
                During = value.TryGetProperty("duringSeq", out var seq) ? new(seq.GetInt64(), value.GetProperty("duringIssued").GetInt64()) : null
            },
            "crash" => new CodemodeCrashMessage(Text(value, "message")!),
            "ready" => new CodemodeReadyMessage(),
            "started" => new CodemodeStartedMessage(Text(value, "fingerprint")!),
            "idle" => new CodemodeIdleMessage(value.GetProperty("seq").GetInt64(), value.GetProperty("issued").GetInt64()),
            "exhausted" => new CodemodeExhaustedMessage(value.GetProperty("budget").GetInt64()),
            _ => throw new FormatException("Unknown message type.")
        };
    }
}
