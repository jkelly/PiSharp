// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/tool.ts (the ToolTask identity, and harnessError and
// appendToolResult, which generation uses for calls its request did not offer and for the unstarted calls of an aborted sequential round).
// TODO(P9): the tool state machine (phases call and execute, its abort handler, intent, replay, running output and the tool execution API)
// is a placeholder whose handlers fault with "not ported yet (P9)"; a tool round's tool tasks therefore fault, their slots finish without
// an entry, and context derivation synthesizes the missing results.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the tool task (upstream <c>ToolTask</c>, kind <c>pi.tool</c>, version 1). Its input is <c>{assistant, callId}</c>
/// and its result <c>{entryId, control?}</c>.</summary>
public static partial class HarnessTasks
{
    /// <summary>The task. TODO(P9): every phase and the abort handler fault until the tool task is ported.</summary>
    public static DurableTask ToolTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.tool",
        Version = 1,
        Initial = _ => JsonValue.Parse("""{"phase":"call"}"""),
        Phases = BuiltinPlaceholders.Phases("pi.tool", "P9", "call", "execute"),
        Abort = BuiltinPlaceholders.Handler("pi.tool", "P9"),
    });
}

/// <summary>Tool result helpers (upstream <c>tool.ts</c>).</summary>
internal static class ToolResults
{
    /// <summary>An error result whose only content is one Harness diagnostic (upstream <c>harnessError</c>).</summary>
    public static ToolExecutionResult HarnessError(string code, string message) => new()
    {
        Content = JsonArrayValue.Empty,
        IsError = true,
        Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Error, message, code)],
    };

    /// <summary>
    /// Appends a <c>pi.tool-result</c> entry (upstream <c>appendToolResult</c>). The content ends with the rendered diagnostics, so the
    /// stored message is exactly what the model sees; <c>data</c> keeps the structured list. A result's usage is added to <c>pi.usage</c> in
    /// the same commit. <paramref name="call"/> is the pi-ai <c>ToolCall</c>.
    /// </summary>
    public static async Task<TypedEntry<JsonObjectValue>> AppendToolResultAsync(ITx tx, ConversationId conversationId, JsonObjectValue call,
        ToolExecutionResult result, double timestamp, double? durationMs = null)
    {
        var diagnostics = (result.Diagnostics ?? []).Select(DiagnosticJson).ToList();
        var content = (result.Content ?? JsonArrayValue.Empty).ToList();
        if (diagnostics.Count > 0)
            content.Add(new RecordJson.Writer().Add("type", "text").Add("text", RenderDiagnostics(result.Diagnostics!.Value)).Build());
        var name = ((JsonStringValue)call["name"]).Value;
        var message = new RecordJson.Writer()
            .Add("role", "toolResult")
            .Add("toolCallId", call["id"])
            .Add("toolName", name)
            .Add("content", JsonValue.Array(content))
            .Add("details", result.Details)
            .Add("usage", result.Usage)
            .Add("isError", result.IsError ?? false)
            .Number("durationMs", durationMs)
            .Number("timestamp", timestamp)
            .Build();
        if (result.Usage is { } usage) await UsageLedger.RecordUsageAsync(tx, conversationId, "tools", name, usage).ConfigureAwait(false);
        var data = JsonValue.Object([KeyValuePair.Create("diagnostics", (JsonValue)JsonValue.Array(diagnostics))]);
        return await tx.AppendEntryAsync(Entries.ToolResultEntry, conversationId,
            new TypedEntryDraft<JsonObjectValue> { Model = JsonValue.Array(message), Data = data }).ConfigureAwait(false);
    }

    /// <summary>A diagnostic's JSON in upstream <c>toolDiagnostic</c>'s key order: <c>{severity, code?, message}</c>.</summary>
    private static JsonValue DiagnosticJson(ToolDiagnostic diagnostic) => new RecordJson.Writer()
        .Add("severity", WireName.Of(diagnostic.Severity)).Add("code", diagnostic.Code).Add("message", diagnostic.Message).Build();

    private static string RenderDiagnostics(IEnumerable<ToolDiagnostic> diagnostics) =>
        $"<harness>\n{string.Join("\n", diagnostics.Select(diagnostic => $"[{WireName.Of(diagnostic.Severity)}] {diagnostic.Message}"))}\n</harness>";
}
