// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/tui.ts (describeTask, userText,
// totalUsage, contextTokens) and runtime.ts (agentOf). The adapters between a ConversationView and pi's interactive components (decision
// 0005, phase P12, risk 7): the components take pi-ai wire messages as System.Text.Json objects, as they do in interactive mode, so an entry's
// `model[0]` and the built-in documents (pi.agent, pi.live, pi.inbox, pi.usage) are read as JsonNode copies of the durable JSON values.
// Upstream reads the same plain JSON through TypeScript casts; a missing field reads as absent here as it does there.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Durable;
using DurableJson = PiSharp.Durable.Chord.JsonValue;

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
