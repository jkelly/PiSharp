// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/tui.ts (describeTask, userText,
// totalUsage, contextTokens) and runtime.ts (agentOf). The adapters between a ConversationView and pi's interactive components (decision
// 0005, phase P12, risk 7): the components take pi-ai wire messages as System.Text.Json objects, as they do in interactive mode, so an entry's
// `model[0]` and the built-in documents (pi.agent, pi.live, pi.inbox, pi.usage) are read as JsonNode copies of the durable JSON values.
// Upstream reads the same plain JSON through TypeScript casts; a missing field reads as absent here as it does there. PiSharp-only since
// pi-durable 1.2.0 (durable work list area l): DisplayResult and NestedRow.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Durable;
using DurableArray = PiSharp.Durable.Chord.JsonArrayValue;
using DurableJson = PiSharp.Durable.Chord.JsonValue;
using DurableObject = PiSharp.Durable.Chord.JsonObjectValue;

namespace PiSharp.Cli.Durable;

/// <summary>The durable JSON a TUI frame reads, as System.Text.Json nodes for pi's interactive components.</summary>
internal static class DurableTuiState
{
    // A revision shares unchanged documents and entries by reference, so each is converted once.
    private static readonly ConditionalWeakTable<DurableJson, JsonNode> Converted = new();

    /// <summary>The value as a JsonNode; the same instance for the same durable value. Callers must not re-parent or mutate it.</summary>
    public static JsonNode? Node(DurableJson? value)
    {
        if (value is null) return null;
        return Converted.GetValue(value, static json => JsonNode.Parse(json.ToJson())!);
    }

    /// <summary>The built-in document of <paramref name="kind"/>, or <paramref name="fallback"/> when the view has none (upstream
    /// <c>view.conversation.docs[kind] ?? fallback</c>).</summary>
    public static JsonObject Doc(ConversationView view, string kind, Func<JsonObject> fallback) =>
        Node(view.Doc(kind)) as JsonObject ?? fallback();

    /// <summary>The entry's first wire message (upstream <c>entry.model?.[0]</c>), or null.</summary>
    public static JsonObject? Message(EntryRecord entry) =>
        entry.Model is { Count: > 0 } model ? Node(model) is JsonArray array ? array[0] as JsonObject : null : null;

    /// <summary>A string field, or null when absent or not a string.</summary>
    public static string? Str(JsonNode? node, string key) =>
        node is JsonObject value && value[key] is JsonValue field && field.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A number field, or null when absent or not a number.</summary>
    public static double? Num(JsonNode? node, string key) =>
        node is JsonObject value && value[key] is JsonValue field && field.TryGetValue<double>(out var number) ? number : null;

    /// <summary>The field as an object, or null.</summary>
    public static JsonObject? Obj(JsonNode? node, string key) => node is JsonObject value ? value[key] as JsonObject : null;

    /// <summary>The field as an array, or an empty sequence.</summary>
    public static IEnumerable<JsonNode?> Arr(JsonNode? node, string key) =>
        node is JsonObject value && value[key] is JsonArray array ? array : [];

    /// <summary>JavaScript's <c>String(number)</c> for the integers and plain decimals the documents hold.</summary>
    public static string JsNumber(double value) =>
        double.IsInteger(value) && Math.Abs(value) < 1e21 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>PiSharp-only (decision 0007): the <c>pi.agent</c> document's service tier, or null.</summary>
    public static string? ServiceTierOf(ConversationView view) =>
        view.Doc("pi.agent") is { } agent && agent.TryGetValue("serviceTier", out var tier) && tier is PiSharp.Durable.Chord.JsonStringValue text ? text.Value : null;

    /// <summary>The <c>pi.agent</c> document's model and thinking level (upstream <c>agentOf</c>).</summary>
    public static (ModelRef? Model, string? ThinkingLevel) AgentOf(ConversationView view)
    {
        var agent = view.Doc("pi.agent");
        ModelRef? model = null;
        if (agent is not null && agent.TryGetValue("model", out var reference) && reference is PiSharp.Durable.Chord.JsonObjectValue value &&
            value.TryGetValue("provider", out var provider) && provider is PiSharp.Durable.Chord.JsonStringValue providerText &&
            value.TryGetValue("modelId", out var modelId) && modelId is PiSharp.Durable.Chord.JsonStringValue modelIdText)
            model = new ModelRef(providerText.Value, modelIdText.Value);
        string? thinking = agent is not null && agent.TryGetValue("thinkingLevel", out var level) && level is PiSharp.Durable.Chord.JsonStringValue text
            ? text.Value : null;
        return (model, thinking);
    }

    /// <summary>Upstream <c>userText</c>: a string content, or its text blocks joined.</summary>
    public static string UserText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (content is not JsonArray blocks) return "";
        return string.Concat(blocks.Where(block => Str(block, "type") == "text").Select(block => Str(block, "text") ?? ""));
    }

    /// <summary>Upstream <c>totalUsage</c>: the conversation's spend over every model and tool bucket.</summary>
    public static (double Input, double Output, double CacheRead, double CacheWrite, double Cost) TotalUsage(JsonObject usage)
    {
        double input = 0, output = 0, cacheRead = 0, cacheWrite = 0, cost = 0;
        foreach (var bucket in new[] { Obj(usage, "models"), Obj(usage, "tools") })
        {
            if (bucket is null) continue;
            foreach (var (_, entry) in bucket)
            {
                input += Num(entry, "input") ?? 0;
                output += Num(entry, "output") ?? 0;
                cacheRead += Num(entry, "cacheRead") ?? 0;
                cacheWrite += Num(entry, "cacheWrite") ?? 0;
                cost += Num(Obj(entry, "cost"), "total") ?? 0;
            }
        }
        return (input, output, cacheRead, cacheWrite, cost);
    }

    /// <summary>Upstream <c>contextTokens</c>: context size from the newest successful answer after the newest compaction; unknown
    /// before one.</summary>
    public static double? ContextTokens(IReadOnlyList<EntryRecord> entries)
    {
        // Kept entries follow the summary in the view but are older than it; only later answers measure the new context.
        var compacted = entries.Where(entry => entry.Kind == "pi.compaction").Select(entry => entry.Id.Value).DefaultIfEmpty(0).Max();
        compacted = Math.Max(0, compacted);
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            var message = Message(entry);
            if (entry.Id.Value < compacted || entry.Kind != "pi.assistant" || Str(message, "role") != "assistant") continue;
            var stopReason = Str(message, "stopReason");
            if (stopReason is "aborted" or "error") continue;
            var usage = Obj(message, "usage");
            var total = Num(usage, "totalTokens") ?? 0;
            return total != 0 ? total
                : (Num(usage, "input") ?? 0) + (Num(usage, "output") ?? 0) + (Num(usage, "cacheRead") ?? 0) + (Num(usage, "cacheWrite") ?? 0);
        }
        return null;
    }

    /// <summary>
    /// A <c>pi.tool-result</c> message as its card shows it (PiSharp-only, pi-durable 1.2.0). The Harness appends a tool's diagnostics to the
    /// content the model reads as one <c>&lt;harness&gt;</c> text block (<c>[error] Command exited with code 2</c> for a failed command,
    /// <c>[info] Read image file [image/png]</c> for an image); the entry's <c>data.diagnostics</c> lists them. The card shows their messages
    /// as plain lines instead, as pi shows a failed command's exit code after its output. Any other message is returned as it is.
    /// </summary>
    public static JsonObject DisplayResult(EntryRecord entry, JsonObject message)
    {
        if (entry.Data is not DurableObject data || !data.TryGetValue("diagnostics", out var listed) ||
            listed is not DurableArray { Count: > 0 } diagnostics || message["content"] is not JsonArray { Count: > 0 } content) return message;
        var items = diagnostics.Select(Node).ToList();
        var rendered = "<harness>\n" + string.Join("\n", items.Select(item => $"[{Str(item, "severity")}] {Str(item, "message")}")) + "\n</harness>";
        if (Str(content[^1], "type") != "text" || Str(content[^1], "text") != rendered) return message;
        var copy = (JsonObject)message.DeepClone();
        var blocks = (JsonArray)copy["content"]!;
        var output = blocks.Take(blocks.Count - 1).Any(block => Str(block, "type") == "text" && Str(block, "text") is { Length: > 0 });
        var lines = string.Join("\n", items.Select(item => Str(item, "message") ?? ""));
        blocks[^1] = new JsonObject { ["type"] = "text", ["text"] = output ? "\n" + lines : lines };
        return copy;
    }

    /// <summary>
    /// The row of a nested call in pi's codemode row shape <c>{name, args, status, durationMs?, cost?, error?}</c> (PiSharp-only, pi-durable
    /// 1.2.0): pending and running are <c>running</c>, a call that left <c>pi.live</c> unfinished is <c>cancelled</c>, and a done call is
    /// <c>ok</c> or <c>error</c> by its summary (one without a summary faulted or was orphaned).
    /// </summary>
    public static JsonObject NestedRow(DurableNestedCall call)
    {
        var slot = Node(call.Slot);
        var summary = Obj(slot, "summary");
        var status = Str(slot, "status") == "done"
            ? summary is not null && !(summary["isError"] is JsonValue isError && isError.TryGetValue<bool>(out var error) && error) ? "ok" : "error"
            : call.Left ? "cancelled" : "running";
        var row = new JsonObject
        {
            ["name"] = Str(slot, "name") ?? "",
            ["args"] = call.Slot.TryGetValue("arguments", out var arguments) ? arguments.ToJson() : "",
            ["status"] = status,
        };
        if (Num(summary, "durationMs") is { } duration) row["durationMs"] = duration;
        if (Num(Obj(Obj(summary, "usage"), "cost"), "total") is { } cost && cost != 0) row["cost"] = cost;
        if (Str(summary, "error") is { Length: > 0 } text) row["error"] = text;
        return row;
    }

    /// <summary>Upstream <c>describeTask</c>: one line of the task panel.</summary>
    public static string DescribeTask(TaskGraphNode node)
    {
        var state = node.State.ToJson();
        var status = Str(Node(state), "status") ?? "undefined";
        // Upstream interpolates the fields as they are; an absent phase prints as JavaScript's "undefined".
        var description = status switch
        {
            "waiting" => $"waiting on {string.Join(", ", node.State.On.Select(id => id.Value.ToString(CultureInfo.InvariantCulture)))}",
            "completing" => $"completing ({Str(Node(state), "outcome") ?? "undefined"})",
            _ => $"{status} {node.State.Phase ?? "undefined"}",
        };
        var flags = new[] { node.Background ? "background" : "", node.AbortRequested ? "aborting" : "" }.Where(flag => flag.Length > 0).ToList();
        var owned = node.Conversations.Length > 0
            ? $" owns conversation {string.Join(", ", node.Conversations.Select(id => id.Value.ToString(CultureInfo.InvariantCulture)))}" : "";
        return $"{node.Kind} #{node.Id.Value.ToString(CultureInfo.InvariantCulture)}: {description}" +
            $"{(flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "")}{owned}";
    }
}
