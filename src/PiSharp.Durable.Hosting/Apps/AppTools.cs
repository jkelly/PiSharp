// PiSharp-only (decision 0008): the tools durable-pi gives its agents (apps/dpi-backend/src/extensions.ts and history.ts, MIT), on PiSharp's
// durable library: read_state and update_state on the device's App State, search_history over the full history (compacted and reset
// entries too), handoff, subagent (pisharp durable's subagent tool, src/PiSharp.Cli/Durable/DurableSubagent.cs, as a library tool) and
// start_session, which creates a session exactly once through the catalog. Replays are safe: receipts and task-scoped records make a rerun
// after a restart find what the first run did.
using System.Collections.Immutable;
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;

namespace PiSharp.Durable.Hosting;

/// <summary>The tools of the <c>durable-apps</c> extension, built for one session.</summary>
internal static class AppTools
{
    /// <summary>Marks a tool call's transcript entry as written, so a rerun does not write it twice.</summary>
    private static readonly TaskDocToken EntryReceipt = Documents.DefineTaskDoc("pisharp.entry-receipt", 1, () => JsonObjectValue.Empty);

    /// <summary>Marks the session a <c>start_session</c> call created.</summary>
    internal const string StateChangeEntry = "pisharp.state-change";

    // ─── App State ──────────────────────────────────────────────────────────

    public static ToolRegistration ReadState(DeviceAppState state) => Define.Tool(new ToolRegistration
    {
        Name = "read_state",
        Description = "Read the device's shared App State: one JSON object every agent, session and fork on this device shares. Give a JSON " +
            "Pointer path (\"/goal\", \"/notes/0\") to read one value; leave it out to read the whole document.",
        Parameters = Schema("""{"type":"object","properties":{"path":{"type":"string","description":"A JSON Pointer such as /goal; empty or absent reads the whole document."}}}"""),
        Replay = ToolReplay.Safe,
        Execute = (arguments, _, _) =>
        {
            var path = String(arguments, "path") ?? "";
            var current = state.Get();
            var value = AppStateDocument.Read(current.Value, path);
            return Task.FromResult(Text(JsonValue.Object([new("path", path), new("revision", current.Revision), new("value", value ?? JsonValue.Null),
                new("exists", value is not null)]).ToJson()));
        },
    });

    public static ToolRegistration UpdateState(DeviceAppState state, string sessionId) => Define.Tool(new ToolRegistration
    {
        Name = "update_state",
        Description = "Change one path of the device's shared App State. operation is create (a new field, or an array insert; \"-\" appends), " +
            "update (an existing value; the empty path replaces the whole document), delete, or increment (a number, by value_json or 1). " +
            "value_json is the new value as JSON text, for example \"true\", \"3\", \"\\\"text\\\"\" or \"{\\\"a\\\":1}\". Other agents may change " +
            "other paths at the same time; one call changes one path atomically.",
        Parameters = Schema("""{"type":"object","required":["operation","path"],"properties":{"operation":{"type":"string","enum":["create","update","delete","increment"]},"path":{"type":"string","description":"A JSON Pointer such as /progress."},"value_json":{"type":"string","description":"The value as JSON text; not used by delete."}}}"""),
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var change = new AppStateChange(Operation(String(arguments, "operation")), String(arguments, "path") ?? "",
                String(arguments, "value_json") is { } json ? ParseValue(json) : null);
            AppStateValue result;
            try
            {
                result = state.Change(change, receiptKey: $"agent:{sessionId}:{api.TaskId.Value}");
            }
            catch (HostInvalidException invalid)
            {
                return Error(invalid.Message);
            }
            await WriteEntryOnceAsync(api, new EntryDraft { Kind = StateChangeEntry, Data = DeviceAppState.Describe(change, result.Revision) }, context)
                .ConfigureAwait(false);
            return Text(JsonValue.Object([new("ok", true), new("revision", result.Revision),
                new("value", AppStateDocument.Read(result.Value, change.Path) ?? JsonValue.Null)]).ToJson());
        },
    });

    // ─── History ────────────────────────────────────────────────────────────

    public static ToolRegistration SearchHistory(Func<ConversationId, DurableContext, Task<IConversation>> conversation) => Define.Tool(new ToolRegistration
    {
        Name = "search_history",
        Description = "Search this agent's full history, including entries the model no longer sees after a compaction or a reset. query " +
            "matches entries containing all of its words. Filter by tool name, kind (user, assistant, tool_call, tool_result, event) or entry " +
            "range; give entry to read one entry in full. Results page with offset and limit.",
        Parameters = Schema("""{"type":"object","properties":{"query":{"type":"string"},"tool":{"type":"string"},"kind":{"type":"string","enum":["user","assistant","tool_call","tool_result","event"]},"from_entry":{"type":"integer"},"to_entry":{"type":"integer"},"entry":{"type":"integer","description":"Read one entry in full."},"order":{"type":"string","enum":["newest","oldest"]},"limit":{"type":"integer","minimum":1,"maximum":50},"offset":{"type":"integer","minimum":0}}}"""),
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var target = await conversation(api.ConversationId, context).ConfigureAwait(false);
            var result = await History.SearchAsync(target, HistoryQuery.From((JsonObjectValue)arguments), context).ConfigureAwait(false);
            return Text(result.ToJson());
        },
    });

    // ─── Handoff ────────────────────────────────────────────────────────────

    public static ToolRegistration Handoff() => Define.Tool(new ToolRegistration
    {
        Name = "handoff",
        Description = "Start a new context from a handoff note: the model will see only the note and what follows. Older entries stay " +
            "stored, and search_history still finds them. Use it when the context is long and the next step needs little of it.",
        Parameters = Schema("""{"type":"object","required":["note"],"properties":{"note":{"type":"string","description":"Everything the next context needs to continue."}}}"""),
        Replay = ToolReplay.Safe,
        Execute = (arguments, _, _) => Task.FromResult(new ToolExecutionResult
        {
            Output = Content("New context started from the handoff note."),
            Control = new ToolControl { Handoff = String(arguments, "note") ?? "" },
        }),
    });

    // ─── Subagent ───────────────────────────────────────────────────────────

    public static ToolRegistration Subagent() => Define.Tool(new ToolRegistration
    {
        Name = "subagent",
        Description = "Delegate a self-contained task to a subagent with the same model and tools, and get its answer back. Give it everything " +
            "it needs to know; it does not see this conversation.",
        Parameters = Schema("""{"type":"object","required":["task"],"properties":{"task":{"type":"string","description":"The task, with all the context it needs."}}}"""),
        // A rerun after a crash finds the child it created and the submission it made.
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var task = String(arguments, "task") ?? "";
            var child = await api.CommitAsync(async tx =>
            {
                var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = api.TaskId }, 1).ConfigureAwait(false)).Items.FirstOrDefault();
                if (existing is not null) return existing.Id;
                // Starts as a copy of this conversation's agent; it cannot delegate further, and nobody sees a question it would ask.
                var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId)).ConfigureAwait(false);
                var withheld = api.Registry.Tools().Select(registered => registered.Tool)
                    .Where(tool => tool.Name is "subagent" or "ask_user_question" or "exit_plan_mode" or "start_session").ToList();
                if (withheld.Count > 0)
                    await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Tools = ToolSelection.Without(withheld) }).ConfigureAwait(false);
                return created.Id;
            }, context).ConfigureAwait(false);
            var details = JsonValue.Object([new("conversationId", child.ToJson())]);
            await api.DetailsAsync(details, context).ConfigureAwait(false);
            var handle = (await api.ConversationAsync(child, context).ConfigureAwait(false))!;
            var settled = await (await handle.SubmitAsync(SubmissionDraft.Input(task, requestId: $"subagent:{api.TaskId.Value}"), context).ConfigureAwait(false))
                .WaitAsync(context).ConfigureAwait(false);
            if (settled.Status != SubmissionStatus.Done || settled.Answer is not { } answer)
                throw new InvalidOperationException($"Subagent {child.Value} did not answer: {settled.Reason ?? settled.Status.ToString().ToLowerInvariant()}");
            var entry = await api.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), context).ConfigureAwait(false);
            return new ToolExecutionResult { Output = Content(SubmissionAnswers.TextOf(entry)), Details = details };
        },
    });

    // ─── Sessions ───────────────────────────────────────────────────────────

    public static ToolRegistration StartSession(DurableDeviceHost host, string sessionId) => Define.Tool(new ToolRegistration
    {
        Name = "start_session",
        Description = "Start a new session on this device, exactly once, and optionally give its Main agent a first prompt. The new session " +
            "has its own working directory and conversation; it shares this device's App State.",
        Parameters = Schema("""{"type":"object","required":["name","cwd"],"properties":{"name":{"type":"string","description":"Session name."},"cwd":{"type":"string","description":"Working directory. Relative paths resolve against this session's cwd."},"model":{"type":"string","description":"Model id, for example \"openai/gpt-6.1-sol\". Defaults to this agent's model."},"prompt":{"type":"string","description":"First prompt for the new session's Main agent."}}}"""),
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var agent = await api.AgentAsync(context).ConfigureAwait(false);
            var cwd = String(arguments, "cwd") ?? "";
            if (!Path.IsPathRooted(cwd) && !cwd.StartsWith('~') && agent.Cwd is { } here) cwd = Path.Join(here, cwd);
            ModelRef? model = agent.Model;
            if (String(arguments, "model") is { Length: > 0 } named)
            {
                var slash = named.IndexOf('/', StringComparison.Ordinal);
                if (slash <= 0) return Error($"model must be provider/model, not {named}.");
                model = new ModelRef(named[..slash], named[(slash + 1)..]);
            }
            var key = $"start_session:{sessionId}:{api.TaskId.Value}";
            HostedSession created;
            try
            {
                created = await host.CreateSessionAsync(new SessionCreateRequest
                {
                    Name = String(arguments, "name"),
                    Cwd = cwd,
                    Model = model,
                    ThinkingLevel = agent.ThinkingLevel,
                    SpawnKey = key,
                }, context.CancellationToken).ConfigureAwait(false);
            }
            catch (HostInvalidException invalid)
            {
                return Error(invalid.Message);
            }
            if (String(arguments, "prompt") is { Length: > 0 } prompt && created.IsAvailable)
                await created.SendAsync(1, new SendRequest(prompt, key + ":prompt"), context.CancellationToken).ConfigureAwait(false);
            return new ToolExecutionResult
            {
                Output = Content($"Started session {created.Name} ({created.Id}) in {created.Cwd}."),
                Details = JsonValue.Object([new("sessionId", created.Id)]),
            };
        },
    });

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Appends a passive entry to the calling conversation once, however often the call reruns.</summary>
    internal static Task WriteEntryOnceAsync(IToolExecutionApi api, EntryDraft entry, DurableContext context) => api.CommitAsync(async tx =>
    {
        var receipt = await tx.DocAsync(EntryReceipt, api.TaskId).ConfigureAwait(false);
        if (receipt.ContainsKey("entry")) return;
        var written = await tx.AppendEntryAsync(api.ConversationId, entry).ConfigureAwait(false);
        receipt.Set("entry", written.Id.Value);
    }, context);

    internal static JsonObjectValue Schema(string json) => (JsonObjectValue)JsonValue.Parse(json);

    internal static JsonArrayValue Content(string text) => JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", text)])]);

    internal static ToolExecutionResult Text(string text) => new() { Output = Content(text) };

    internal static ToolExecutionResult Error(string text) => new() { Output = Content(text), IsError = true };

    internal static string? String(JsonValue arguments, string key) =>
        arguments is JsonObjectValue json && json.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;

    internal static double? Number(JsonValue arguments, string key) =>
        arguments is JsonObjectValue json && json.TryGetValue(key, out var value) && value is JsonNumberValue number ? number.Value : null;

    private static AppStateOperation Operation(string? name) => name switch
    {
        "create" => AppStateOperation.Create,
        "update" => AppStateOperation.Update,
        "delete" => AppStateOperation.Delete,
        "increment" => AppStateOperation.Increment,
        _ => throw new ArgumentException($"Unknown operation {name}; use create, update, delete or increment."),
    };

    private static JsonValue ParseValue(string json)
    {
        try { return JsonValue.Parse(json); }
        catch (Exception) { return json; } // Not JSON: the text itself.
    }
}

/// <summary>A <c>search_history</c> request.</summary>
internal sealed record HistoryQuery
{
    public string? Query { get; init; }
    public string? Tool { get; init; }
    public string? Kind { get; init; }
    public long? FromEntry { get; init; }
    public long? ToEntry { get; init; }
    public long? Entry { get; init; }
    public bool Oldest { get; init; }
    public int Limit { get; init; } = 10;
    public int Offset { get; init; }

    public static HistoryQuery From(JsonObjectValue arguments) => new()
    {
        Query = AppTools.String(arguments, "query"),
        Tool = AppTools.String(arguments, "tool"),
        Kind = AppTools.String(arguments, "kind"),
        FromEntry = AppTools.Number(arguments, "from_entry") is { } from ? (long)from : null,
        ToEntry = AppTools.Number(arguments, "to_entry") is { } to ? (long)to : null,
        Entry = AppTools.Number(arguments, "entry") is { } entry ? (long)entry : null,
        Oldest = AppTools.String(arguments, "order") == "oldest",
        Limit = (int)Math.Clamp(AppTools.Number(arguments, "limit") ?? 10, 1, 50),
        Offset = (int)Math.Max(0, AppTools.Number(arguments, "offset") ?? 0),
    };
}

/// <summary>History search over a conversation's fork-aware history (durable-pi history.ts).</summary>
internal static class History
{
    private const int MaxEntries = 10_000;
    private const int SnippetLength = 360;
    private const int FullLength = 4000;

    public static async Task<JsonObjectValue> SearchAsync(IConversation conversation, HistoryQuery query, DurableContext context)
    {
        var all = new List<EntryRecord>();
        JsonObjectValue? cursor = null;
        do
        {
            var batch = await conversation.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 500, cursor, context).ConfigureAwait(false);
            all.AddRange(batch.Items);
            cursor = batch.Next;
        } while (cursor is not null && all.Count < MaxEntries);
        var active = (await conversation.ContextAsync(context).ConfigureAwait(false)).Entries.Select(entry => entry.Id.Value).ToHashSet();
        var lastReset = all.LastOrDefault(entry => entry.Kind == "pi.reset")?.Id.Value;
        var items = all.Where(entry => entry.Kind != "pi.system").Select(entry => Describe(entry, active, lastReset)).ToList();

        if (query.Entry is { } wanted)
        {
            var one = items.FirstOrDefault(item => item.Id == wanted);
            return JsonValue.Object(
            [
                new("total", one is null ? 0 : 1),
                new("results", one is null ? JsonArrayValue.Empty : JsonValue.Array(one.ToJson(Cut(one.Text, FullLength)))),
            ]);
        }
        var words = (query.Query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = items.Where(item =>
            (query.Tool is null ? !item.Tools.All(tool => tool == "search_history") || item.Tools.IsEmpty : item.Tools.Contains(query.Tool)) &&
            (query.Kind is null || item.Kind == query.Kind) &&
            (query.FromEntry is not { } from || item.Id >= from) &&
            (query.ToEntry is not { } to || item.Id <= to) &&
            words.All(word => item.Text.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!query.Oldest) matches.Reverse();
        var page = matches.Skip(query.Offset).Take(query.Limit).ToList();
        var next = query.Offset + page.Count < matches.Count ? (JsonValue)(query.Offset + page.Count) : JsonValue.Null;
        return JsonValue.Object(
        [
            new("total", matches.Count),
            new("offset", query.Offset),
            new("limit", query.Limit),
            new("next_offset", next),
            new("active_from", active.Count == 0 ? JsonValue.Null : (JsonValue)active.Min()),
            new("results", JsonValue.Array(page.Select(item => (JsonValue)item.ToJson(Snippet(item.Text, words))))),
        ]);
    }

    private sealed record Item(long Id, string Kind, string Context, ImmutableArray<string> Tools, string Text)
    {
        public JsonObjectValue ToJson(string text) => JsonValue.Object(
        [
            new("entry", Id),
            new("kind", Kind),
            new("context", Context),
            new("tools", JsonValue.Array(Tools.Select(tool => (JsonValue)tool))),
            new("text", text),
        ]);
    }

    private static Item Describe(EntryRecord entry, HashSet<long> active, long? lastReset)
    {
        var context = active.Contains(entry.Id.Value) ? "active" : lastReset is { } reset && entry.Id.Value < reset ? "before reset" : "compacted";
        var message = entry.Model is { Count: > 0 } model ? model[0] as JsonObjectValue : null;
        switch (entry.Kind)
        {
            case "pi.user":
                return new Item(entry.Id.Value, "user", context, [], MessageText(message));
            case "pi.assistant":
                var calls = Blocks(message).Where(block => Field(block, "type") == "toolCall").ToList();
                var tools = calls.Select(call => Field(call, "name") ?? "").ToImmutableArray();
                var text = string.Join("\n", new[] { MessageText(message) }.Concat(calls.Select(call =>
                    $"{Field(call, "name")}({(call.TryGetValue("arguments", out var args) ? args.ToJson() : "")})")).Where(part => part.Length > 0));
                return new Item(entry.Id.Value, calls.Count > 0 ? "tool_call" : "assistant", context, tools, text);
            case "pi.tool-result":
                return new Item(entry.Id.Value, "tool_result", context, [Field(message, "toolName") ?? ""], MessageText(message));
            default:
                var data = entry.Data is JsonObjectValue fields && Field(fields, "text") is { } described ? described : entry.Data?.ToJson() ?? "";
                return new Item(entry.Id.Value, "event", context, [], $"{entry.Kind} {data}".Trim());
        }
    }

    private static string MessageText(JsonObjectValue? message)
    {
        if (message is null || !message.TryGetValue("content", out var content)) return "";
        return content switch
        {
            JsonStringValue text => text.Value,
            JsonArrayValue blocks => string.Join("\n", blocks.OfType<JsonObjectValue>().Where(block => Field(block, "type") == "text")
                .Select(block => Field(block, "text") ?? "")),
            _ => "",
        };
    }

    private static IEnumerable<JsonObjectValue> Blocks(JsonObjectValue? message) =>
        message is not null && message.TryGetValue("content", out var content) && content is JsonArrayValue blocks ? blocks.OfType<JsonObjectValue>() : [];

    private static string? Field(JsonObjectValue? json, string key) => json is not null && json.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;

    private static string Snippet(string text, string[] words)
    {
        if (text.Length <= SnippetLength) return text;
        var at = words.Length == 0 ? 0 : Math.Max(0, text.IndexOf(words[0], StringComparison.OrdinalIgnoreCase));
        var start = Math.Max(0, Math.Min(at - SnippetLength / 3, text.Length - SnippetLength));
        return (start > 0 ? "…" : "") + text.Substring(start, SnippetLength) + (start + SnippetLength < text.Length ? "…" : "");
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
