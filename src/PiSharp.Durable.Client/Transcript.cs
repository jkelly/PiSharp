// PiSharp-only (decision 0008). Committed entries plus the live generation and tool slots, normalized into renderable transcript items, after
// durable-pi's transcript (apps/dpi-sdk/src/transcript.ts, MIT): a tool call carries its result once the result entry exists, and a result
// whose call is not in view stands alone. PiSharp additions: pi-durable 1.2.0's nested tool calls (code mode) under their parent call,
// images from tool results and user messages, and a compaction failure's error as an event note. The device's /transcript route, the
// hub and a terminal all build items here, so every interface stitches calls and results the same way.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Durable.Client;

/// <summary>Where a tool call stands.</summary>
public enum ToolStatus
{
    /// <summary>Requested; not started.</summary>
    Pending,
    /// <summary>Running.</summary>
    Running,
    /// <summary>Finished with a result.</summary>
    Done,
    /// <summary>Finished with an error result.</summary>
    Error,
}

/// <summary>An image as a data URL's parts.</summary>
public sealed record TranscriptImage(string MimeType, string Data)
{
    /// <summary><c>data:&lt;mime&gt;;base64,&lt;data&gt;</c>.</summary>
    public string DataUrl => $"data:{MimeType};base64,{Data}";
}

/// <summary>A tool call with its arguments, live output, result and nested calls.</summary>
public sealed record ToolCallView
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public JsonNode? Args { get; init; }
    public ToolStatus Status { get; init; }
    /// <summary>Live output while running.</summary>
    public string? Output { get; init; }
    /// <summary>The result text once the result exists.</summary>
    public string? Result { get; init; }
    public long? ResultEntryId { get; init; }
    /// <summary>Images of the result.</summary>
    public IReadOnlyList<TranscriptImage> Images { get; init; } = [];
    /// <summary>Calls this call made (code mode), in start order.</summary>
    public IReadOnlyList<ToolCallView> Nested { get; init; } = [];
}

/// <summary>A block of an assistant message.</summary>
public abstract record AssistantBlockView
{
    private protected AssistantBlockView() { }
}

/// <summary>Text (Markdown).</summary>
public sealed record TextBlockView(string Text) : AssistantBlockView;

/// <summary>Thinking.</summary>
public sealed record ThinkingBlockView(string Text) : AssistantBlockView;

/// <summary>A tool call.</summary>
public sealed record ToolBlockView(ToolCallView Call) : AssistantBlockView;

/// <summary>One item of a transcript. <see cref="Key"/> is stable across rebuilds, for patching a rendered view.</summary>
public abstract record TranscriptItem
{
    private protected TranscriptItem() { }

    /// <summary>A key unique within the transcript.</summary>
    public abstract string Key { get; }

    /// <summary><c>user</c>, <c>assistant</c>, <c>tool-result</c>, <c>tool</c> or <c>event</c>.</summary>
    public abstract string Kind { get; }
}

/// <summary>A user message.</summary>
public sealed record UserItem(long Id, string Text, IReadOnlyList<TranscriptImage> Images) : TranscriptItem
{
    public override string Key => "e" + Id.ToString(CultureInfo.InvariantCulture);
    public override string Kind => "user";
}

/// <summary>An assistant message; <see cref="Id"/> is null for the live one.</summary>
public sealed record AssistantItem(long? Id, bool Streaming, IReadOnlyList<AssistantBlockView> Blocks, string? Error, string? Model) : TranscriptItem
{
    public override string Key => Id is { } id ? "e" + id.ToString(CultureInfo.InvariantCulture) : "live";
    public override string Kind => "assistant";
}

/// <summary>A tool result whose call is not in view (after a compaction or reset).</summary>
public sealed record ToolResultItem(long Id, int Index, string Name, string Text, bool IsError) : TranscriptItem
{
    public override string Key => $"e{Id.ToString(CultureInfo.InvariantCulture)}-{Index.ToString(CultureInfo.InvariantCulture)}";
    public override string Kind => "tool-result";
}

/// <summary>A running tool whose call is not in an assistant message in view.</summary>
public sealed record ToolItem(ToolCallView Call) : TranscriptItem
{
    public override string Key => "t" + Call.CallId;
    public override string Kind => "tool";
}

/// <summary>Any other entry: a reset, a compaction, an App State change, a job card's anchor, a custom entry.</summary>
public sealed record EventItem(long Id, string EntryKind, string Text, string? Note, JsonNode? Data) : TranscriptItem
{
    public override string Key => "e" + Id.ToString(CultureInfo.InvariantCulture);
    public override string Kind => "event";
}

/// <summary>A tool slot of the live round, or a nested call.</summary>
public sealed record LiveTool
{
    public required string Name { get; init; }
    /// <summary><c>pending</c>, <c>running</c> or <c>done</c>.</summary>
    public string Status { get; init; } = "running";
    public string Output { get; init; } = "";
    public JsonNode? Args { get; init; }
    /// <summary>A nested call's caller.</summary>
    public string? ParentCallId { get; init; }
    /// <summary>A finished nested call's result text.</summary>
    public string? Result { get; init; }
    public bool IsError { get; init; }
    /// <summary>Start order, for listing nested calls.</summary>
    public long Order { get; init; }
    /// <summary>The call's tool task.</summary>
    public long? TaskId { get; init; }
    /// <summary>The slot's details (a question form or a plan review waiting for a person).</summary>
    public JsonNode? Details { get; init; }
}

/// <summary>Builds transcript items, Markdown, previews and usage totals.</summary>
public static class Transcript
{
    /// <summary>The items of <paramref name="entries"/> (ordered by id), then the live message, then running tools whose call is not in
    /// view. <paramref name="nested"/> holds nested calls by their call id.</summary>
    public static IReadOnlyList<TranscriptItem> Build(IReadOnlyList<JsonObject> entries, JsonObject? live = null,
        IReadOnlyDictionary<string, LiveTool>? tools = null, IReadOnlyDictionary<string, LiveTool>? nested = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        tools ??= new Dictionary<string, LiveTool>();
        nested ??= new Dictionary<string, LiveTool>();
        var results = new Dictionary<string, (long EntryId, JsonObject Message)>(StringComparer.Ordinal);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var kind = Str(entry, "kind");
            foreach (var message in Messages(entry))
            {
                if (kind == "pi.tool-result" && Str(message, "role") == "toolResult" && Str(message, "toolCallId") is { } id) results[id] = (Id(entry), message);
                if (kind == "pi.assistant" && Str(message, "role") == "assistant")
                    foreach (var block in Blocks(message))
                        if (Str(block, "type") == "toolCall" && Str(block, "id") is { } callId) callIds.Add(callId);
            }
        }
        if (live is not null)
            foreach (var block in Blocks(live))
                if (Str(block, "type") == "toolCall" && Str(block, "id") is { } callId) callIds.Add(callId);

        var children = nested.Where(pair => pair.Value.ParentCallId is not null).GroupBy(pair => pair.Value.ParentCallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(pair => pair.Value.Order).ToList(), StringComparer.Ordinal);

        IReadOnlyList<ToolCallView> NestedOf(string callId, int depth) =>
            depth > 8 || !children.TryGetValue(callId, out var list) ? [] : [.. list.Select(pair =>
            {
                var (id, tool) = pair;
                return new ToolCallView
                {
                    CallId = id,
                    Name = tool.Name,
                    Args = tool.Args,
                    Status = tool.Status == "done" ? tool.IsError ? ToolStatus.Error : ToolStatus.Done : tool.Status == "running" ? ToolStatus.Running : ToolStatus.Pending,
                    Output = tool.Output.Length > 0 ? tool.Output : null,
                    Result = tool.Result,
                    Nested = NestedOf(id, depth + 1),
                };
            })];

        ToolCallView ToolView(string id, string name, JsonNode? args)
        {
            if (results.TryGetValue(id, out var result))
                return new ToolCallView
                {
                    CallId = id,
                    Name = name,
                    Args = args,
                    Status = Bool(result.Message, "isError") ? ToolStatus.Error : ToolStatus.Done,
                    Result = MessageText(result.Message),
                    ResultEntryId = result.EntryId,
                    Images = Images(result.Message),
                    Nested = NestedOf(id, 0),
                };
            tools.TryGetValue(id, out var running);
            return new ToolCallView
            {
                CallId = id,
                Name = name,
                Args = args ?? running?.Args,
                Status = running?.Status == "running" ? ToolStatus.Running : ToolStatus.Pending,
                Output = running is { Output.Length: > 0 } ? running.Output : null,
                Nested = NestedOf(id, 0),
            };
        }

        List<AssistantBlockView> AssistantBlocks(JsonObject message)
        {
            var blocks = new List<AssistantBlockView>();
            foreach (var block in Blocks(message))
            {
                switch (Str(block, "type"))
                {
                    case "text" when Str(block, "text") is { Length: > 0 } text:
                        blocks.Add(new TextBlockView(text));
                        break;
                    case "thinking" when Str(block, "thinking") is { Length: > 0 } thinking:
                        blocks.Add(new ThinkingBlockView(thinking));
                        break;
                    case "toolCall":
                        blocks.Add(new ToolBlockView(ToolView(Str(block, "id") ?? "", Str(block, "name") ?? "tool", block["arguments"]?.DeepClone())));
                        break;
                }
            }
            return blocks;
        }

        var items = new List<TranscriptItem>();
        foreach (var entry in entries)
        {
            var kind = Str(entry, "kind") ?? "";
            var id = Id(entry);
            switch (kind)
            {
                case "pi.system":
                    continue;
                case "pi.user":
                    items.Add(new UserItem(id, string.Join("\n", Messages(entry).Select(MessageText)), [.. Messages(entry).SelectMany(Images)]));
                    break;
                case "pi.assistant":
                {
                    var messages = Messages(entry).Where(message => Str(message, "role") == "assistant").ToList();
                    items.Add(new AssistantItem(id, false, [.. messages.SelectMany(AssistantBlocks)],
                        messages.Select(AssistantError).FirstOrDefault(error => error is not null), messages.Count > 0 ? Str(messages[0], "model") : null));
                    break;
                }
                case "pi.tool-result":
                {
                    var index = 0;
                    foreach (var message in Messages(entry).Where(message => Str(message, "role") == "toolResult"))
                    {
                        if (Str(message, "toolCallId") is { } callId && callIds.Contains(callId)) continue;
                        items.Add(new ToolResultItem(id, index++, Str(message, "toolName") ?? "tool", MessageText(message), Bool(message, "isError")));
                    }
                    break;
                }
                default:
                {
                    var notes = Messages(entry).ToList();
                    items.Add(new EventItem(id, kind, EventText(entry), notes.Count > 0 ? string.Join("\n", notes.Select(MessageText)) : null,
                        entry["data"]?.DeepClone()));
                    break;
                }
            }
        }
        if (live is not null) items.Add(new AssistantItem(null, true, AssistantBlocks(live), null, Str(live, "model")));
        foreach (var (callId, tool) in tools)
            if (tool.Status != "done" && !callIds.Contains(callId)) items.Add(new ToolItem(ToolView(callId, tool.Name, tool.Args)));
        return items;
    }

    /// <summary>A message's text blocks, joined (a string content is its own text).</summary>
    public static string MessageText(JsonObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message["content"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonArray blocks => string.Concat(blocks.OfType<JsonObject>().Select(block => Str(block, "type") == "text" ? Str(block, "text") ?? "" : "")),
            _ => "",
        };
    }

    /// <summary>Why an assistant message has no full answer, or null.</summary>
    public static string? AssistantError(JsonObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Str(message, "errorMessage") is { Length: > 0 } error) return error;
        return Str(message, "stopReason") switch
        {
            "aborted" => "Interrupted. Partial response preserved.",
            "error" => "Provider request failed.",
            _ => null,
        };
    }

    /// <summary>The text of the newest assistant entry with text, for previews.</summary>
    public static string? LastAssistantText(IReadOnlyList<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (Str(entries[i], "kind") != "pi.assistant") continue;
            var text = string.Concat(Messages(entries[i]).Where(message => Str(message, "role") == "assistant").Select(MessageText)).Trim();
            if (text.Length > 0) return text;
        }
        return null;
    }

    /// <summary>The transcript as Markdown (durable-pi <c>toMarkdown</c>).</summary>
    public static string ToMarkdown(IReadOnlyList<TranscriptItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var parts = new List<string>();
        static string Cut(string text, int length) => text.Length > length ? text[..length] : text;
        foreach (var item in items)
        {
            switch (item)
            {
                case UserItem user:
                    parts.Add($"### You (#{user.Id})\n\n{user.Text}");
                    break;
                case AssistantItem assistant:
                {
                    var blocks = assistant.Blocks.Select(block => block switch
                    {
                        TextBlockView text => text.Text,
                        ThinkingBlockView thinking => $"> _thinking:_ {Cut(thinking.Text.Replace('\n', ' '), 400)}",
                        ToolBlockView tool => $"- `{tool.Call.Name}` {StatusText(tool.Call.Status)}" +
                            (tool.Call.Result is { } result ? $"\n\n```\n{Cut(result, 2000)}\n```" : ""),
                        _ => "",
                    }).ToList();
                    if (assistant.Error is { } error) blocks.Add($"> {error}");
                    parts.Add($"### Agent ({(assistant.Id is { } id ? "#" + id.ToString(CultureInfo.InvariantCulture) : "working")})\n\n{string.Join("\n\n", blocks)}");
                    break;
                }
                case ToolResultItem result:
                    parts.Add($"- `{result.Name}` {(result.IsError ? "error" : "result")}\n\n```\n{Cut(result.Text, 2000)}\n```");
                    break;
                case ToolItem tool:
                    parts.Add($"- `{tool.Call.Name}` {StatusText(tool.Call.Status)}");
                    break;
                case EventItem item1:
                    parts.Add($"_{item1.Text}_");
                    break;
            }
        }
        return string.Join("\n\n", parts) + "\n";
    }

    /// <summary>The items as durable-pi's <c>/transcript?format=json</c> items.</summary>
    public static JsonArray ToJson(IReadOnlyList<TranscriptItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var array = new JsonArray();
        foreach (var item in items)
        {
            JsonObject json = item switch
            {
                UserItem user => new() { ["kind"] = "user", ["id"] = user.Id, ["text"] = user.Text },
                AssistantItem assistant => Assistant(assistant),
                ToolResultItem result => new() { ["kind"] = "tool-result", ["id"] = result.Id, ["name"] = result.Name, ["text"] = result.Text, ["isError"] = result.IsError },
                ToolItem tool => new() { ["kind"] = "tool", ["id"] = tool.Call.CallId, ["call"] = Call(tool.Call) },
                EventItem item1 => Event(item1),
                _ => [],
            };
            array.Add(json);
        }
        return array;

        static JsonObject Assistant(AssistantItem assistant)
        {
            var json = new JsonObject
            {
                ["kind"] = "assistant",
                ["id"] = assistant.Id is { } id ? JsonValue.Create(id) : JsonValue.Create("live"),
                ["streaming"] = assistant.Streaming,
                ["blocks"] = new JsonArray([.. assistant.Blocks.Select(block => (JsonNode)(block switch
                {
                    TextBlockView text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                    ThinkingBlockView thinking => new JsonObject { ["type"] = "thinking", ["text"] = thinking.Text },
                    ToolBlockView tool => new JsonObject { ["type"] = "tool", ["call"] = Call(tool.Call) },
                    _ => new JsonObject(),
                }))]),
            };
            if (assistant.Error is not null) json["error"] = assistant.Error;
            if (assistant.Model is not null) json["model"] = assistant.Model;
            return json;
        }

        static JsonObject Event(EventItem item)
        {
            var json = new JsonObject { ["kind"] = "event", ["id"] = item.Id, ["entryKind"] = item.EntryKind, ["text"] = item.Text };
            if (item.Note is not null) json["note"] = item.Note;
            if (item.Data is not null) json["data"] = item.Data.DeepClone();
            return json;
        }
    }

    /// <summary>A tool call as durable-pi's <c>ToolCallView</c> JSON, with PiSharp's <c>nested</c>.</summary>
    public static JsonObject Call(ToolCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var json = new JsonObject
        {
            ["callId"] = call.CallId,
            ["name"] = call.Name,
            ["args"] = call.Args?.DeepClone(),
            ["status"] = StatusText(call.Status),
        };
        if (call.Output is not null) json["output"] = call.Output;
        if (call.Result is not null) json["result"] = call.Result;
        if (call.ResultEntryId is { } entry) json["resultEntryId"] = entry;
        if (call.Nested.Count > 0) json["nested"] = new JsonArray([.. call.Nested.Select(child => (JsonNode)Call(child))]);
        return json;
    }

    /// <summary><c>pending</c>, <c>running</c>, <c>done</c> or <c>error</c>.</summary>
    public static string StatusText(ToolStatus status) => status switch
    {
        ToolStatus.Running => "running",
        ToolStatus.Done => "done",
        ToolStatus.Error => "error",
        _ => "pending",
    };

    /// <summary>Totals over a usage state's models.</summary>
    public static UsageSummary Sum(UsageTotals? usage)
    {
        var total = new UsageSummary();
        foreach (var model in usage?.Models.Values ?? [])
        {
            total.Tokens += model.TotalTokens > 0 ? model.TotalTokens : model.Input + model.Output + model.CacheRead + model.CacheWrite;
            total.Cost += model.Cost.Total;
            total.Input += model.Input;
            total.Output += model.Output;
            total.CacheRead += model.CacheRead;
            total.CacheWrite += model.CacheWrite;
            total.InputCost += model.Cost.Input;
            total.OutputCost += model.Cost.Output;
            total.CacheReadCost += model.Cost.CacheRead;
            total.CacheWriteCost += model.Cost.CacheWrite;
        }
        return total;
    }

    internal static string EventText(JsonObject entry)
    {
        if (entry["data"] is JsonObject data && Str(data, "text") is { } text) return text;
        return Str(entry, "kind") switch
        {
            "pi.reset" => "New context started. Earlier entries remain stored.",
            "pi.compaction" => "Context compacted. Earlier entries remain stored.",
            var kind => kind ?? "",
        };
    }

    internal static IEnumerable<JsonObject> Messages(JsonObject entry) => entry["model"] is JsonArray model ? model.OfType<JsonObject>() : [];

    internal static IEnumerable<JsonObject> Blocks(JsonObject message) => message["content"] is JsonArray content ? content.OfType<JsonObject>() : [];

    internal static IReadOnlyList<TranscriptImage> Images(JsonObject message) =>
        [.. Blocks(message).Where(block => Str(block, "type") == "image" && Str(block, "data") is not null)
            .Select(block => new TranscriptImage(Str(block, "mimeType") ?? "image/png", Str(block, "data")!))];

    internal static string? Str(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static bool Bool(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    internal static long Id(JsonObject json, string key = "id") =>
        json[key] is JsonValue value && value.TryGetValue<double>(out var number) ? (long)number : 0;

    /// <summary>A JSON value as text: a string as itself, anything else as JSON.</summary>
    public static string Describe(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };
}

/// <summary>Usage totals of a session or agent.</summary>
public sealed class UsageSummary
{
    public double Tokens { get; set; }
    public double Cost { get; set; }
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheRead { get; set; }
    public double CacheWrite { get; set; }
    public double InputCost { get; set; }
    public double OutputCost { get; set; }
    public double CacheReadCost { get; set; }
    public double CacheWriteCost { get; set; }
}
