using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static partial class NativeExtensionSessionCommandTests
{
    private const string TodoParameters = """
        {"type":"object","properties":{"action":{"type":"string","enum":["list","add","toggle","clear"]},"text":{"type":"string","description":"Todo text (for add)"},"id":{"type":"number","description":"Todo ID (for toggle)"}},"required":["action"]}
        """;
    private const string TodoDescription = "Manage a todo list. Actions: list, add (text), toggle (id), clear";
    public static IEnumerable<(string Name, Func<Task> Run)> TodoCases(string host, string cli)
    {
        _ = Cases(host, cli); // Compile-path admission only; ordinary legacy cases are not run here.
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
        {
            var selected = api;
            yield return ("native stateful Todo " + selected + " commits two calls in one batch, resumes and isolates sibling branches", () => TodoDurable(selected));
        }
        yield return ("native stateful Todo sequential RPC EOF joins, invalid schema and final denial preserve state", TodoRpcAuthority);
    }

    private static async Task TodoDurable(string api)
    {
        using var files = await Files.CreateAsync("todo"); await Create(files, api); var initial = await Complete(files); TodoLoadout(initial);
        const string alpha = "Alpha \u4E2D\uD83D\uDE42\nnext", beta = "Beta", sibling = "Sibling only";
        var batch = TodoBatch(api, [("todo-first", new { action = "add", text = alpha }), ("todo-second", new { action = "add", text = beta })], "todo batch");
        batch["expectedRequest"] = TodoInitialRequest(api, "todo batch");
        await Script(files, batch, Text(api, "batch final", ["Added todo #1: " + alpha, "Added todo #2: " + beta]));
        var literal = await File.ReadAllBytesAsync(files.Script); var report = Success(await Command(files, "prompt", api, "todo batch"));
        TodoRequests(report, api); Equal(2, report.GetProperty("usedScriptTurns").GetInt32()); Equal(2, report.GetProperty("actions").GetArrayLength());
        foreach (var action in report.GetProperty("actions").EnumerateArray())
        { Equal("todo", action.GetProperty("ToolName").GetString()); Equal("sample.todo/1/todo", action.GetProperty("Target").GetString()); Check(action.GetProperty("allowed").GetBoolean(), "Todo batch lost its explicit final grant."); }
        var literalAfter = await File.ReadAllBytesAsync(files.Script); Check(literal.AsSpan().SequenceEqual(literalAfter), "Todo rewrote authored provider bytes.");
        var first = await Complete(files); TodoPrefix(initial, first); TodoLoadout(first); var results = TodoResults(Context(first)); Equal(2, results.Length);
        TodoResult(results[0], api, "todo-first", "Added todo #1: " + alpha, "add", [TodoRow(1, alpha, false)], 2);
        TodoResult(results[1], api, "todo-second", "Added todo #2: " + beta, "add", [TodoRow(1, alpha, false), TodoRow(2, beta, false)], 3);
        var firstSnapshot = results[0].GetRawText(); var ancestor = Context(first).LeafId!;
        var calls = TodoCalls(Context(first)); Equal(2, calls.Length);
        TodoCall(calls[0], api, "todo-first", new { action = "add", text = alpha }); TodoCall(calls[1], api, "todo-second", new { action = "add", text = beta });

        await TodoScript(files, api, "todo-toggle", new { action = "toggle", id = 1 }, "toggle", "toggle final");
        TodoRequests(Success(await Command(files, "resume", api, "toggle")), api); var toggled = await Complete(files); TodoPrefix(first, toggled);
        TodoResult(TodoResults(Context(toggled))[^1], api, "todo-toggle", "Todo #1 completed", "toggle", [TodoRow(1, alpha, true), TodoRow(2, beta, false)], 3);
        Equal(firstSnapshot, TodoResults(Context(toggled))[0].GetRawText());
        await TodoScript(files, api, "todo-list", new { action = "list" }, "list", "list final");
        TodoRequests(Success(await Command(files, "resume", api, "list")), api); var listed = await Complete(files); TodoPrefix(toggled, listed);
        TodoResult(TodoResults(Context(listed))[^1], api, "todo-list", "[x] #1: " + alpha + "\n[ ] #2: " + beta, "list", [TodoRow(1, alpha, true), TodoRow(2, beta, false)], 3);

        await Script(files, TodoBatch(api, [("todo-add-missing", new { action = "add" }), ("todo-id-missing", new { action = "toggle" }), ("todo-not-found", new { action = "toggle", id = 99 })], "semantic errors"), Text(api, "errors final"));
        var errors = Success(await Command(files, "resume", api, "semantic errors")); TodoRequests(errors, api); Check(!errors.GetProperty("toolErrors").GetBoolean(), "Normal Todo semantic error became native execution failure.");
        var errored = await Complete(files); TodoPrefix(listed, errored); var errorResults = TodoResults(Context(errored));
        var unchanged = new[] { TodoRow(1, alpha, true), TodoRow(2, beta, false) };
        TodoResult(errorResults[^3], api, "todo-add-missing", "Error: text required for add", "add", unchanged, 3, "text required");
        TodoResult(errorResults[^2], api, "todo-id-missing", "Error: id required for toggle", "toggle", unchanged, 3, "id required");
        TodoResult(errorResults[^1], api, "todo-not-found", "Todo #99 not found", "toggle", unchanged, 3, "#99 not found");
        await TodoScript(files, api, "todo-clear", new { action = "clear" }, "clear", "clear final");
        TodoRequests(Success(await Command(files, "resume", api, "clear")), api); var cleared = await Complete(files); TodoPrefix(errored, cleared);
        TodoResult(TodoResults(Context(cleared))[^1], api, "todo-clear", "Cleared 2 todos", "clear", [], 1); var clearedLeaf = Context(cleared).LeafId!;

        await TodoScript(files, api, "todo-sibling", new { action = "add", text = sibling }, "sibling", "sibling final");
        var branched = Success(await Command(files, "resume", api, "sibling", "--leaf", ancestor)); TodoRequests(branched, api); Equal(ancestor, branched.GetProperty("previousSelectedLeafId").GetString());
        var afterBranch = await Complete(files); TodoPrefix(cleared, afterBranch); TodoLoadout(afterBranch);
        var selected = new SessionContextProjector().Project(afterBranch.ValidatedPrefix.Skip(1).Select(row => row.Entry).ToImmutableArray(), branched.GetProperty("selectedLeafId").GetString());
        var branchResults = TodoResults(selected); Equal(3, branchResults.Length);
        TodoResult(branchResults[^1], api, "todo-sibling", "Added todo #3: " + sibling, "add", [TodoRow(1, alpha, false), TodoRow(2, beta, false), TodoRow(3, sibling, false)], 4);
        Check(!selected.LlmMessages.Any(row => row.WireBody.ToString().Contains("toggle final", StringComparison.Ordinal) || row.WireBody.ToString().Contains("clear final", StringComparison.Ordinal)), "Todo sibling reconstruction flattened other branch state.");
        Equal(firstSnapshot, branchResults[0].GetRawText());

        await TodoScript(files, api, "todo-main-list", new { action = "list" }, "main again", "main final");
        var restored = Success(await Command(files, "resume", api, "main again", "--leaf", clearedLeaf)); TodoRequests(restored, api); Equal(clearedLeaf, restored.GetProperty("previousSelectedLeafId").GetString());
        var final = await Complete(files); TodoPrefix(afterBranch, final); TodoLoadout(final);
        TodoResult(TodoResults(Context(final))[^1], api, "todo-main-list", "No todos", "list", [], 1);
        Check(!Context(final).LlmMessages.Any(row => row.WireBody.ToString().Contains(sibling, StringComparison.Ordinal)), "Return to cleared branch incorporated sibling Todo.");
        Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }

    private static async Task TodoRpcAuthority()
    {
        const string api = "openai-responses";
        using var files = await Files.CreateAsync("todo"); await Create(files, api); var initial = await Complete(files); TodoLoadout(initial);
        await Script(files, Tool(api, "todo", "rpc-add", new { action = "add", text = "RPC item" }, "rpc add"), Text(api, "rpc add final"),
            Tool(api, "todo", "rpc-toggle", new { action = "toggle", id = 1 }, "rpc toggle"), Text(api, "rpc toggle final"),
            Tool(api, "todo", "rpc-list", new { action = "list" }, "rpc list"), Text(api, "rpc list final"));
        await using (var child = new RpcChild(files, api))
        {
            foreach (var (id, prompt, text, action, done) in new[] { ("rpc-add", "rpc add", "Added todo #1: RPC item", "add", false),
                ("rpc-toggle", "rpc toggle", "Todo #1 completed", "toggle", true), ("rpc-list", "rpc list", "[x] #1: RPC item", "list", true) })
            {
                var from = child.Records.Length; await child.Send(new { id, type = "prompt", message = prompt }); Response(await child.Response(id));
                await child.Wait(row => Type(row) == "agent_settled" && child.Records.Skip(from).Contains(row));
                var events = child.Records.Skip(from).Where(row => Type(row) == "tool_execution_end").ToArray(); Equal(1, events.Length);
                Equal(TodoId(api, id), events[0].Value.GetProperty("toolCallId").GetString()); Check(!events[0].Value.GetProperty("isError").GetBoolean(), "Sequential Todo RPC action failed.");
                TodoContent(events[0].Value.GetProperty("result"), text, action, [TodoRow(1, "RPC item", done)], 2);
                await child.Send(new { id = "entries-" + id, type = "get_entries" }); await TodoAcknowledged(files, Response(await child.Response("entries-" + id)).GetProperty("data").GetProperty("entries"));
            }
            Clean(await child.Finish()); // Actual EOF, reader/writer/process join; no synthetic settlement.
        }
        var rpc = await Complete(files); TodoPrefix(initial, rpc); TodoLoadout(rpc); var results = TodoResults(Context(rpc)); Equal(3, results.Length);
        TodoResult(results[0], api, "rpc-add", "Added todo #1: RPC item", "add", [TodoRow(1, "RPC item", false)], 2);
        TodoResult(results[1], api, "rpc-toggle", "Todo #1 completed", "toggle", [TodoRow(1, "RPC item", true)], 2);
        TodoResult(results[2], api, "rpc-list", "[x] #1: RPC item", "list", [TodoRow(1, "RPC item", true)], 2);
        var before = rpc;
        foreach (var (id, args, denied) in new (string, object, bool)[] { ("bad-action", new { action = "remove" }, false),
            ("bad-id", new { action = "toggle", id = "1" }, false), ("bad-text", new { action = "add", text = 7 }, false),
            ("missing-action", new { text = "must not add" }, false), ("denied-add", new { action = "add", text = "must not add" }, true) })
        {
            await Script(files, Tool(api, "todo", id, args, id), Text(api, "failure observed"));
            var run = await Command(files, "resume", api, id, denied ? ["--deny-extension-tool", "todo"] : []); Equal(1, run.ExitCode); Equal("", run.Error);
            var receipt = JsonData.Parse(Utf8.GetString(run.Output)).Value; Equal("completed_with_errors", receipt.GetProperty("status").GetString());
            Check(receipt.GetProperty("durableCheckpointAcknowledged").GetBoolean() && receipt.GetProperty("toolErrors").GetBoolean(), "Todo rejection lost its native error or durable acknowledgement.");
            TodoRequests(receipt, api); Equal(denied ? 1 : 0, receipt.GetProperty("actions").GetArrayLength());
            if (denied) Check(!receipt.GetProperty("actions")[0].GetProperty("allowed").GetBoolean() && receipt.GetProperty("actions")[0].GetProperty("Target").GetString() == "sample.todo/1/todo", "Todo denial used unknown-tool/schema failure instead of final authority.");
            var after = await Complete(files); TodoPrefix(before, after); TodoLoadout(after); var result = TodoResults(Context(after))[^1];
            Equal(TodoId(api, id), result.GetProperty("toolCallId").GetString()); Check(result.GetProperty("isError").GetBoolean(), "Rejected Todo became successful state.");
            Check(!result.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object || !details.TryGetProperty("todos", out _), "Rejected call published a Todo state snapshot.");
            TodoCall(TodoCalls(Context(after))[^1], api, id, args); before = after;
        }
        await TodoScript(files, api, "list-after-denials", new { action = "list" }, "verify saved state", "saved state final");
        TodoRequests(Success(await Command(files, "resume", api, "verify saved state")), api); var reopened = await Complete(files); TodoPrefix(before, reopened); TodoLoadout(reopened);
        TodoResult(TodoResults(Context(reopened))[^1], api, "list-after-denials", "[x] #1: RPC item", "list", [TodoRow(1, "RPC item", true)], 2);
        Equal(results[0].GetRawText(), TodoResults(Context(reopened))[0].GetRawText()); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }

    private static Task TodoScript(Files files, string api, string id, object args, string prompt, string final) =>
        Script(files, Tool(api, "todo", id, args, prompt), Text(api, final));
    private static JsonObject TodoBatch(string api, (string Id, object Arguments)[] calls, string prompt)
    {
        // One independently authored assistant batch, with distinct actual wire indices and one completion/usage tail.
        var turns = calls.Select(call => JsonSerializer.SerializeToNode(Tool(api, "todo", call.Id, call.Arguments, prompt))!.AsObject()).ToArray();
        var events = new JsonArray();
        if (api == "anthropic-messages") events.Add(turns[0]["events"]![0]!.DeepClone());
        for (var index = 0; index < turns.Length; index++)
        {
            foreach (var original in turns[index]["events"]!.AsArray())
            {
                var row = original!.DeepClone();
                if (api == "openai-responses") { if (row["type"]!.GetValue<string>() == "response.completed") continue; row["output_index"] = index; }
                else if (api == "anthropic-messages")
                { if (!row["type"]!.GetValue<string>().StartsWith("content_block_", StringComparison.Ordinal)) continue; row["index"] = 9 + index; }
                else
                {
                    if (row["choices"]!.AsArray().Count == 0 || row["choices"]![0]!["delta"]!["tool_calls"] is not JsonArray toolCalls) continue;
                    foreach (var call in toolCalls) call!["index"] = 9 + index;
                }
                events.Add(row);
            }
        }
        var tail = turns[0]["events"]!.AsArray();
        if (api == "openai-responses") events.Add(tail[^1]!.DeepClone());
        else { events.Add(tail[^2]!.DeepClone()); events.Add(tail[^1]!.DeepClone()); }
        return new JsonObject { ["requiredInputTexts"] = new JsonArray(prompt), ["events"] = events };
    }
    private static JsonNode TodoInitialRequest(string api, string prompt)
    {
        // Existing independently authored provider body; replace only its sample declaration.
        var request = HelloInitialRequest(api, prompt); var declaration = request["tools"]![2]!;
        if (api == "openai-completions") declaration = declaration["function"]!;
        declaration["name"] = "todo"; declaration["description"] = TodoDescription;
        declaration[api == "anthropic-messages" ? "input_schema" : "parameters"] = JsonNode.Parse(TodoParameters);
        return request;
    }
    private static object TodoRow(long id, string text, bool done) => new { id, text, done };
    private static string TodoId(string api, string id) => api == "openai-responses" ? id + "|fc-" + id : id;
    private static JsonElement[] TodoResults(SessionContextProjection context) => context.LlmMessages.Where(row => row.Role == "toolResult" && row.WireBody.Value.GetProperty("toolName").GetString() == "todo").Select(row => row.WireBody.Value).ToArray();
    private static JsonElement[] TodoCalls(SessionContextProjection context) => context.LlmMessages.Where(row => row.Role == "assistant").SelectMany(row => row.WireBody.Value.GetProperty("content").EnumerateArray())
        .Where(block => block.GetProperty("type").GetString() == "toolCall" && block.GetProperty("name").GetString() == "todo").ToArray();
    private static void TodoCall(JsonElement call, string api, string id, object args)
    { Equal(TodoId(api, id), call.GetProperty("id").GetString()); Equal(JsonSerializer.Serialize(args), call.GetProperty("arguments").GetRawText()); }
    private static void TodoResult(JsonElement result, string api, string id, string text, string action, object[] todos, long nextId, string? error = null)
    { Equal(TodoId(api, id), result.GetProperty("toolCallId").GetString()); Check(!result.GetProperty("isError").GetBoolean(), "Todo semantic result became native error."); TodoContent(result, text, action, todos, nextId, error); }
    private static void TodoContent(JsonElement result, string text, string action, object[] todos, long nextId, string? error = null)
    {
        Equal(1, result.GetProperty("content").GetArrayLength()); Equal("text", result.GetProperty("content")[0].GetProperty("type").GetString()); Equal(text, result.GetProperty("content")[0].GetProperty("text").GetString());
        object details = error is null ? new { action, todos, nextId } : new { action, todos, nextId, error };
        Check(JsonElement.DeepEquals(JsonData.Parse(JsonSerializer.Serialize(details)).Value, result.GetProperty("details")), "Complete Todo details differ from independent expected snapshot.");
    }
    private static void TodoLoadout(SessionLogReadResult log)
    {
        var tools = log.ValidatedPrefix.Skip(1).Select(row => row.Entry.WireBody.Value).Single(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "system").GetProperty("message").GetProperty("toolsAdded");
        Check(tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(["read", "write", "todo"]), "Durable Todo declaration differs from explicit approved loadout.");
        Equal(TodoDescription, tools[2].GetProperty("description").GetString()); Check(JsonElement.DeepEquals(JsonData.Parse(TodoParameters).Value, tools[2].GetProperty("parameters")), "Todo schema was rewritten or widened.");
        Check(!tools[2].GetProperty("parameters").TryGetProperty("additionalProperties", out _), "Todo omitted additionalProperties was rewritten.");
    }
    private static void TodoRequests(JsonElement report, string api)
    {
        foreach (var request in report.GetProperty("requests").EnumerateArray())
        {
            Equal(api, request.GetProperty("api").GetString()); Check(request.GetProperty("authoredInertAuthValidated").GetBoolean() && request.GetProperty("historyRequirementsSatisfied").GetBoolean(), "Todo request bypassed actual offline HTTP/auth/history predicates.");
            Check(request.GetProperty("toolNames").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["read", "write", "todo"]), "Todo approval changed actual provider advertisement.");
        }
    }
    private static void TodoPrefix(SessionLogReadResult before, SessionLogReadResult after) => Check(after.OriginalBytes.AsSpan().StartsWith(before.OriginalBytes.AsSpan()), "Todo reopen/branch rewrote acknowledged bytes.");
    private static async Task TodoAcknowledged(Files files, JsonElement entries)
    {
        SessionLogReadResult read;
        await using (var source = new FileStream(files.Session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous))
            read = await new SessionLogReader().ReadAsync(source, leaveOpen: true);
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.ValidatedPrefixByteLength == read.OriginalBytes.Length, "Todo RPC checkpoint is physically incomplete.");
        Equal(entries.GetArrayLength(), read.ValidatedPrefix.Length - 1);
        for (var index = 0; index < entries.GetArrayLength(); index++) Equal(entries[index].GetRawText(), read.ValidatedPrefix[index + 1].Entry.WireBody.ToString());
        Equal((long)read.OriginalBytes.Length, new FileInfo(files.Session).Length);
    }
}
