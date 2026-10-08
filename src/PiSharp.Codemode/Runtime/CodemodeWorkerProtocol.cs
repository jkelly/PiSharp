// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/runtime/protocol.ts. Messages between the host and the
// engine (a thread or a child process). Tool arguments, results and values cross as JSON strings, as in the original.
using System.Text;
using System.Text.Json;

namespace PiSharp.Codemode;

/// <summary>worker.ts workerData: the script, the tool and global tables, the store snapshot and the engine limits.</summary>
internal sealed record CodemodeStartMessage(string Code, string ToolsJson, string GlobalsJson, string StoreJson,
    long MemoryLimitBytes, long TotalAllocationLimitBytes, int RecursionLimit);

/// <summary>WorkerToHostMessage.</summary>
internal abstract record CodemodeWorkerMessage;
internal sealed record CodemodeCallMessage(long Id, bool Tool, string Name, string? Arguments) : CodemodeWorkerMessage;
internal sealed record CodemodeOutputMessage(CodemodeOutputItem Item) : CodemodeWorkerMessage;
/// <summary><c>Error</c> is the JSON <c>{ name?, message, stack? }</c> of a failed script; <c>Writes</c> the store writes of a
/// successful one.</summary>
internal sealed record CodemodeDoneMessage(bool Ok, string? Value, string? Writes, string? Error) : CodemodeWorkerMessage;
/// <summary>The engine failed outside the script's control.</summary>
internal sealed record CodemodeCrashMessage(string Message) : CodemodeWorkerMessage;
/// <summary>The engine is set up and the script starts now: the deadline counts from here, so starting a worker process is not
/// charged to the script.</summary>
internal sealed record CodemodeReadyMessage : CodemodeWorkerMessage;

/// <summary>HostToWorkerMessage: <c>Payload</c> is the JSON result when ok, otherwise the error message.</summary>
internal sealed record CodemodeResultMessage(long Id, bool Ok, string? Payload);

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
    });

    public static string Encode(CodemodeResultMessage result) => Write(writer =>
    {
        writer.WriteString("type", "result"); writer.WriteNumber("id", result.Id); writer.WriteBoolean("ok", result.Ok);
        Optional(writer, "payload", result.Payload);
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
                Optional(writer, "writes", done.Writes); Optional(writer, "error", done.Error); break;
            case CodemodeCrashMessage crash:
                writer.WriteString("type", "crash"); writer.WriteString("message", crash.Message); break;
            case CodemodeReadyMessage:
                writer.WriteString("type", "ready"); break;
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
            value.GetProperty("memory").GetInt64(), value.GetProperty("total").GetInt64(), value.GetProperty("recursion").GetInt32());
    }

    public static CodemodeResultMessage DecodeResult(string line)
    {
        using var document = JsonDocument.Parse(line); var value = document.RootElement;
        if (Text(value, "type") != "result") throw new FormatException("Expected a result message.");
        return new(value.GetProperty("id").GetInt64(), value.GetProperty("ok").GetBoolean(), Text(value, "payload", false));
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
            "done" => new CodemodeDoneMessage(value.GetProperty("ok").GetBoolean(), Text(value, "value", false), Text(value, "writes", false), Text(value, "error", false)),
            "crash" => new CodemodeCrashMessage(Text(value, "message")!),
            "ready" => new CodemodeReadyMessage(),
            _ => throw new FormatException("Unknown message type.")
        };
    }
}
