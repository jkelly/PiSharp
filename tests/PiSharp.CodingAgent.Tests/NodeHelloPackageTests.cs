using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

/// <summary>Explicit opt-in compiled-package/source/provider/durable workflow. Never register in the ordinary native gate.</summary>
internal static class NodeHelloPackageTests
{
    private const string Parameters = "{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name to greet\"}}}";
    private const string ExpectedSha = "f005d29a78a238bd30c179ffe27a3eb1a429619edab8eb549004aaa0b78112ae";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Context(string Host, string Cli, string Published, string Node, string Repository,
        string Oracle, string Jiti, string Reference, string HelloReference, string Runs);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string host, string cli, string published,
        string node, string repository, string oracle, string jiti, string reference, string helloReference, string runParent)
    {
        var c = new Context(Absolute(host), Absolute(cli), Absolute(published), Absolute(node), Absolute(repository),
            Absolute(oracle), Absolute(jiti), Absolute(reference), Absolute(helloReference), Absolute(runParent));
        foreach (var path in new[] { c.Host, c.Cli, c.Node }) Check(File.Exists(path), "Explicit Hello executable/CLI missing.");
        foreach (var path in new[] { c.Published, c.Repository, c.Oracle, c.Jiti, c.Reference, c.HelloReference, c.Runs }) Check(Directory.Exists(path), "Explicit Hello input/run parent missing.");
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
            yield return ("node-hello." + api + ".original-source-preparation-execute-provider-durable-rpc-resume-denied-branch", () => Durable(c, api));
        yield return ("node-hello.original-validation-errors-final-invalid-replacement-native-write-denial", () => Authority(c));
    }

    private static async Task Durable(Context c, string api)
    {
        var files = await Files.Create(c, api); await Create(files); var initial = await Complete(files); Loadout(initial);
        const string user = "genuine Hello workflow", unicode = "Ada \U0001f44b\nnext";
        var first = JsonSerializer.SerializeToNode(Tool(api, "hello", "hello-1", new { name = "Ada" }, user))!;
        first["expectedRequest"] = InitialRequest(api, user);
        await Script(files, first, Tool(api, "hello", "hello-2", new { name = unicode }, "Hello, Ada!"), Text(api, "hello-cli-final", "Hello, " + unicode + "!"));
        var scriptBefore = await File.ReadAllBytesAsync(files.Script);
        var cli = await OneShot(files, "prompt", ["--message", user]); Clean(cli);
        var report = JsonData.Parse(cli.Output).Value; Equal("completed", report.GetProperty("status").GetString());
        Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Hello CLI receipt preceded durable flush.");
        Equal(3, report.GetProperty("usedScriptTurns").GetInt32()); Requests(report, api, 3);
        Equal(2, report.GetProperty("actions").GetArrayLength());
        foreach (var action in report.GetProperty("actions").EnumerateArray())
        { Equal("hello", action.GetProperty("ToolName").GetString()); Equal("fixture.node.hello/1/hello", action.GetProperty("Target").GetString()); Check(action.GetProperty("allowed").GetBoolean(), "Real Hello final policy refused its approved target."); }
        var scriptAfter = await File.ReadAllBytesAsync(files.Script); Check(scriptBefore.AsSpan().SequenceEqual(scriptAfter), "Source workflow rewrote authored provider input.");
        var cliLog = await Complete(files); Check(cliLog.OriginalBytes.AsSpan().StartsWith(initial.OriginalBytes.AsSpan()), "Hello CLI rewrote initial durable history.");
        Loadout(cliLog); var ancestor = Project(cliLog).LeafId!;
        AssertDurableResult(Project(cliLog), api, "hello-1", new { name = "Ada" }, "Ada", false, SourceResult(files, "hello-result"));
        AssertDurableResult(Project(cliLog), api, "hello-2", new { name = unicode }, unicode, false, SourceResult(files, "hello-unicode"));
        var cliSource = await files.Receipt(1); Equal(4, cliSource.GetProperty("sourceOperations").GetArrayLength());
        SourceCall(files, cliSource, 0, "hello-result", api, "hello-1"); SourceCall(files, cliSource, 2, "hello-unicode", api, "hello-2");

        // Actual upstream conversion precedes native initial schema validation; durable authored input remains numeric.
        await Script(files, Tool(api, "hello", "prep-number", new { name = 42 }, "hello-cli-final", "hello-rpc-resume"), Text(api, "hello-rpc-final", "Hello, 42!"));
        JsonElement acknowledged;
        await using (var rpc = await RpcChild.Start(files, []))
        {
            await rpc.Send(new { id = "prompt", type = "prompt", message = "hello-rpc-resume" }); Good(await rpc.Response("prompt"));
            await rpc.Wait(record => Type(record) == "agent_settled"); RpcResult(rpc.Records, api, "prep-number", "42", false);
            await rpc.Send(new { id = "state", type = "get_state" }); var state = Good(await rpc.Response("state")).GetProperty("data");
            Equal(api, state.GetProperty("model").GetProperty("api").GetString()); Check(!state.GetProperty("isStreaming").GetBoolean(), "Hello RPC remained active after settlement.");
            await rpc.Send(new { id = "entries", type = "get_entries" }); acknowledged = Good(await rpc.Response("entries")).GetProperty("data").GetProperty("entries");
            await Acknowledged(files, acknowledged); Clean(await rpc.Finish());
        }
        var resumed = await Complete(files); Check(resumed.OriginalBytes.AsSpan().StartsWith(cliLog.OriginalBytes.AsSpan()), "Hello independent reopen rewrote acknowledged bytes.");
        Equal(acknowledged.GetArrayLength(), resumed.ValidatedPrefix.Length - 1); Loadout(resumed);
        AssertDurableResult(Project(resumed), api, "prep-number", new { name = 42 }, "42", false, SourceResult(files, "number"));
        var resumedSource = await files.Receipt(2); Equal(2, resumedSource.GetProperty("sourceOperations").GetArrayLength()); SourceCall(files, resumedSource, 0, "number", api, "prep-number");

        // Original hello-denied input has no ID. This is an explicit new provider ID, not a fabricated corpus ID.
        await Script(files, Tool(api, "hello", "native-hello-denied", new { name = "Ada" }, "hello-cli-final", "hello-rpc-branch"), Text(api, "hello-branch-final"));
        string branchLeaf;
        await using (var branch = await RpcChild.Start(files, ["--leaf", ancestor, "--deny-extension-tool", "hello"]))
        {
            await branch.Send(new { id = "prompt", type = "prompt", message = "hello-rpc-branch" }); Good(await branch.Response("prompt"));
            await branch.Wait(record => Type(record) == "agent_settled"); RpcResult(branch.Records, api, "native-hello-denied", null, true);
            await branch.Send(new { id = "messages", type = "get_messages" }); var messages = Good(await branch.Response("messages")).GetProperty("data").GetProperty("messages");
            Check(!messages.GetRawText().Contains("hello-rpc-resume", StringComparison.Ordinal), "Hello branch flattened sibling provider history.");
            await branch.Send(new { id = "entries", type = "get_entries" }); var entries = Good(await branch.Response("entries")).GetProperty("data");
            branchLeaf = entries.GetProperty("leafId").GetString()!; await Acknowledged(files, entries.GetProperty("entries")); Clean(await branch.Finish());
        }
        var final = await Complete(files); Loadout(final); Check(final.OriginalBytes.AsSpan().StartsWith(resumed.OriginalBytes.AsSpan()), "Hello branch rewrote physical history.");
        Equal(ancestor, final.ValidatedPrefix[resumed.ValidatedPrefix.Length].Entry.ParentId); var selected = Project(final, branchLeaf);
        Check(!selected.LlmMessages.Any(message => message.WireBody.ToString().Contains("hello-rpc-resume", StringComparison.Ordinal)), "Durable selected branch includes sibling messages.");
        AssertDurableResult(selected, api, "native-hello-denied", new { name = "Ada" }, null, true);
        var denied = await files.Receipt(3); Equal(1, denied.GetProperty("sourceOperations").GetArrayLength());
        var prepared = Prepared(denied, 0); Equal(files.Golden("hello-denied").GetProperty("preparation").GetProperty("returned").GetProperty("serializedJson").GetString(), prepared.GetProperty("preparedJson").GetString());
        Check(!denied.GetProperty("sourceOperations").EnumerateArray().Any(op => op.GetProperty("kind").GetString() == "execute"), "Actual final policy denial reached original source execution.");
        await files.VerifyReceipts(4);
    }

    private static async Task Authority(Context c)
    {
        var files = await Files.Create(c, "openai-responses", authoredFinalReplacement: true); await Create(files); var initial = await Complete(files);
        var target = files.In("native-write-must-not-exist.txt");
        await Script(files, Tool(files.Api, "hello", "missing-name", new { }), Tool(files.Api, "hello", "object-name", new { name = new { value = "Ada" } }),
            Tool(files.Api, "hello", "array-name", new { name = new[] { "Ada" } }), Tool(files.Api, "hello", "invalid-final", new { name = "fixture-invalid-replacement" }),
            Tool(files.Api, "write", "ungranted-write", new { path = target, content = "no native write grant" }), Text(files.Api, "hello-authority-final"));
        var run = await OneShot(files, "prompt", ["--message", "genuine preparation failures and native authority"]); Equal(1, run.ExitCode); Equal("", run.Error);
        var report = JsonData.Parse(run.Output).Value; Equal("completed_with_errors", report.GetProperty("status").GetString());
        Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean() && report.GetProperty("toolErrors").GetBoolean(), "Hello denial lost durable error acknowledgement.");
        Requests(report, files.Api, 6); Equal(6, report.GetProperty("usedScriptTurns").GetInt32()); Equal(1, report.GetProperty("actions").GetArrayLength());
        Check(!report.GetProperty("actions")[0].GetProperty("allowed").GetBoolean() && !File.Exists(target), "Initial preparation bypassed final native file policy.");
        var log = await Complete(files); Loadout(log); Check(log.OriginalBytes.AsSpan().StartsWith(initial.OriginalBytes.AsSpan()), "Rejected Hello actions rewrote durable history.");
        var results = Project(log).LlmMessages.Where(message => message.Role == "toolResult").ToArray(); Equal(5, results.Length);
        foreach (var result in results) Check(result.WireBody.Value.GetProperty("isError").GetBoolean(), "Rejected source/native action became tool success.");
        var receipt = await files.Receipt(1); var operations = receipt.GetProperty("sourceOperations"); Equal(4, operations.GetArrayLength());
        for (var index = 0; index < 3; index++)
        {
            var op = operations[index]; Equal("prepare", op.GetProperty("kind").GetString()); Equal("rejected", op.GetProperty("status").GetString());
            var observed = op.GetProperty("observation"); var error = observed.GetProperty("thrown");
            Check(error.GetProperty("message").GetString()!.Contains("Validation failed for tool", StringComparison.Ordinal), "Whole source validator error was replaced.");
            Check(error.GetProperty("stack").GetString()!.Contains("validation.ts", StringComparison.Ordinal), "Actual whole-source error stack missing.");
            Equal(observed.GetProperty("originalBefore").GetRawText(), observed.GetProperty("originalAfter").GetRawText());
            var golden = files.Golden(new[] { "missing-name", "object-name", "array-name" }[index]);
            using var goldenError = JsonDocument.Parse(golden.GetProperty("preparation").GetProperty("thrown").GetProperty("serializedJson").GetString()!);
            Equal(goldenError.RootElement.GetProperty("name").GetString(), error.GetProperty("name").GetString());
            Equal(goldenError.RootElement.GetProperty("message").GetString(), error.GetProperty("message").GetString());
        }
        var finalPrepared = Prepared(receipt, 3); Equal("{\"name\":\"fixture-invalid-replacement\"}", finalPrepared.GetProperty("preparedJson").GetString());
        Check(operations.EnumerateArray().All(op => op.GetProperty("kind").GetString() == "prepare"), "Final invalid replacement was reconverted or executed.");
        Check(receipt.GetProperty("authoredFinalReplacement").GetBoolean(), "Separate authored adversarial hook was not installed.");
        await files.VerifyReceipts(2);
    }

    private static JsonElement Prepared(JsonElement receipt, int index)
    {
        var op = receipt.GetProperty("sourceOperations")[index]; Check(op.GetProperty("settled").GetBoolean(), "Original preparation did not settle.");
        Equal("prepare", op.GetProperty("kind").GetString()); Equal("fulfilled", op.GetProperty("status").GetString()); var observation = op.GetProperty("observation");
        Equal(observation.GetProperty("originalBefore").GetRawText(), observation.GetProperty("originalAfter").GetRawText());
        Equal(observation.GetProperty("schemaBefore").GetRawText(), observation.GetProperty("schemaAfter").GetRawText());
        using var original = JsonDocument.Parse(observation.GetProperty("originalBefore").GetProperty("serializedJson").GetString()!);
        Check(!original.RootElement.TryGetProperty("id", out _), "Pure preparation invented an invocation identity."); return observation;
    }
    private static void SourceCall(Files files, JsonElement receipt, int index, string goldenId, string api, string authoredCallId)
    {
        var golden = files.Golden(goldenId); var prepared = Prepared(receipt, index);
        Equal(golden.GetProperty("preparation").GetProperty("returned").GetProperty("serializedJson").GetString(), prepared.GetProperty("preparedJson").GetString());
        SameJson(golden.GetProperty("preparation").GetProperty("returned"), prepared.GetProperty("returned"));
        var execution = receipt.GetProperty("sourceOperations")[index + 1]; Equal("execute", execution.GetProperty("kind").GetString()); Equal("fulfilled", execution.GetProperty("status").GetString());
        Check(execution.GetProperty("settled").GetBoolean(), "Original execution settlement missing."); var observed = execution.GetProperty("observation"); var invocation = observed.GetProperty("invocation");
        Equal(5, invocation.GetProperty("suppliedArgumentCount").GetInt32()); Equal(CallId(api, authoredCallId), invocation.GetProperty("toolCallId").GetString());
        Equal(prepared.GetProperty("preparedJson").GetString(), invocation.GetProperty("params").GetProperty("serializedJson").GetString());
        Equal(invocation.GetProperty("params").GetRawText(), observed.GetProperty("parametersAfter").GetRawText());
        Check(!invocation.GetProperty("signal").GetProperty("aborted").GetBoolean(), "Source began with a cancelled invocation.");
        Equal("onUpdate", invocation.GetProperty("onUpdate").GetProperty("name").GetString()); Equal(1, invocation.GetProperty("onUpdate").GetProperty("length").GetInt32());
        Equal("genuine ExtensionRunner.createToolContext", invocation.GetProperty("context").GetProperty("owner").GetString());
        Equal(files.Root, invocation.GetProperty("context").GetProperty("cwd").GetString()); Check(!invocation.GetProperty("context").GetProperty("hasUI").GetBoolean(), "Hello runner acquired an invented UI capability.");
        Equal(0, observed.GetProperty("updates").GetArrayLength()); Check(observed.GetProperty("updateDeliveryJoined").GetBoolean(), "Source progress settlement was not joined.");
        Equal(golden.GetProperty("execution").GetProperty("returned").GetProperty("serializedJson").GetString(), observed.GetProperty("resultJson").GetString());
        SameJson(golden.GetProperty("execution").GetProperty("returned"), observed.GetProperty("returned"));
    }
    private static JsonElement SourceResult(Files files, string id) => JsonData.Parse(files.Golden(id).GetProperty("execution").GetProperty("returned").GetProperty("serializedJson").GetString()!).Value;
    private static void AssertDurableResult(SessionContextProjection project, string api, string call, object arguments, string? greeted, bool error, JsonElement? sourceResult = null)
    {
        var actualId = CallId(api, call); var block = project.LlmMessages.Where(message => message.Role == "assistant")
            .SelectMany(message => message.WireBody.Value.GetProperty("content").EnumerateArray()).Single(item => item.GetProperty("type").GetString() == "toolCall" && item.GetProperty("id").GetString() == actualId);
        Equal(JsonSerializer.Serialize(arguments), block.GetProperty("arguments").GetRawText());
        var result = project.LlmMessages.Single(message => message.Role == "toolResult" && message.WireBody.Value.GetProperty("toolCallId").GetString() == actualId).WireBody.Value;
        Equal(error, result.GetProperty("isError").GetBoolean()); if (error) return;
        Equal(1, result.GetProperty("content").GetArrayLength()); Equal("text", result.GetProperty("content")[0].GetProperty("type").GetString());
        Equal("Hello, " + greeted + "!", result.GetProperty("content")[0].GetProperty("text").GetString());
        // ToolResultValueCodec retains genuine JS JSON through WriteRawValue; C# escaping is not the source encoding.
        var expected = sourceResult ?? throw new InvalidOperationException("Successful durable Hello result requires its exact frozen source result.");
        Equal(expected.GetProperty("details").GetRawText(), result.GetProperty("details").GetRawText());
    }
    private static void RpcResult(JsonData[] records, string api, string call, string? greeted, bool error)
    {
        var end = records.Single(record => Type(record) == "tool_execution_end").Value; Equal(CallId(api, call), end.GetProperty("toolCallId").GetString()); Equal(error, end.GetProperty("isError").GetBoolean());
        if (!error) Equal("Hello, " + greeted + "!", end.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Equal(2, records.Count(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant"));
        Equal(1, records.Count(record => Type(record) == "agent_settled")); Equal(0, records.Count(record => Type(record) is "tool_execution_update" or "extension_ui_request"));
    }
    private static void Requests(JsonElement report, string api, int count)
    {
        Equal(count, report.GetProperty("requests").GetArrayLength());
        foreach (var request in report.GetProperty("requests").EnumerateArray())
        {
            Equal(api, request.GetProperty("api").GetString()); Check(request.GetProperty("authoredInertAuthValidated").GetBoolean() && request.GetProperty("historyRequirementsSatisfied").GetBoolean(), "Actual recorded provider request admission failed.");
            Check(request.GetProperty("toolNames").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["read", "write", "hello"]), "Source descriptor advertisement differs.");
        }
    }
    private static void Loadout(SessionLogReadResult log)
    {
        var system = log.ValidatedPrefix.Skip(1).Select(record => record.Entry.WireBody.Value).Single(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "system");
        var tools = system.GetProperty("message").GetProperty("toolsAdded"); Check(tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(["read", "write", "hello"]), "Approved durable source-tool loadout differs.");
        Equal("A simple greeting tool", tools[2].GetProperty("description").GetString()); Equal(Parameters, tools[2].GetProperty("parameters").GetRawText());
    }
    private static async Task Create(Files files)
    {
        var result = await OneShot(files, "create", []); Check(result.ExitCode == 0 && result.Error.Length == 0,
            "Actual original Hello activation failed: exit=" + result.ExitCode + " stderr=" + Bounded(result.Error) + " stdout=" + Bounded(result.Output) + " package=" + await files.LastFailure());
        Check(JsonData.Parse(result.Output).Value.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Hello create did not acknowledge durable flush.");
    }
    private static async Task<SessionLogReadResult> Complete(Files files)
    {
        var log = await new SessionLogReader().ReadFileAsync(files.Session); Complete(log);
        await using var opened = await SessionLogStore.OpenAsync(files.Session); Equal((long)log.OriginalBytes.Length, opened.Snapshot.CommittedByteLength); return log;
    }
    private static void Complete(SessionLogReadResult log) => Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.ValidatedPrefixByteLength == log.OriginalBytes.Length, "Actual Hello session log is incomplete.");
    private static SessionContextProjection Project(SessionLogReadResult log, string? leaf = null) => leaf is null
        ? new SessionContextProjector().ProjectLatest(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray())
        : new SessionContextProjector().Project(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray(), leaf);
    private static async Task Acknowledged(Files files, JsonElement entries)
    {
        SessionLogReadResult log; await using (var source = new FileStream(files.Session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous))
            log = await new SessionLogReader().ReadAsync(source, leaveOpen: true);
        Complete(log); Equal(entries.GetArrayLength(), log.ValidatedPrefix.Length - 1);
        for (var index = 0; index < entries.GetArrayLength(); index++) Equal(entries[index].GetRawText(), log.ValidatedPrefix[index + 1].Entry.WireBody.ToString());
        Equal((long)log.OriginalBytes.Length, new FileInfo(files.Session).Length);
    }

    private static Task Script(Files files, params object[] turns) => File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
    private static object Tool(string api, string name, string call, object arguments, params string[] required)
    {
        var json = JsonSerializer.Serialize(arguments); var middle = json.Length / 2;
        object[] events = api switch
        {
            "anthropic-messages" => [AnthropicStart("msg-" + call), new { type = "content_block_start", index = 9, content_block = new { type = "tool_use", id = call, name, input = new { } } },
                new { type = "content_block_delta", index = 9, delta = new { type = "input_json_delta", partial_json = json[..middle] } }, new { type = "content_block_delta", index = 9, delta = new { type = "input_json_delta", partial_json = json[middle..] } },
                new { type = "content_block_stop", index = 9 }, AnthropicFinish("tool_use"), new { type = "message_stop" }],
            "openai-completions" => [new { id = "cmpl-" + call, @object = "chat.completion.chunk", model = "pisharp-offline-completions-session", choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 9, id = call, type = "function", function = new { name, arguments = json[..middle] } } } }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 9, function = new { arguments = json[middle..] } } } }, finish_reason = (string?)null } } }, CompletionsFinish("tool_calls"), CompletionsUsage()],
            "openai-responses" => [new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = "" } }, new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = json } }, ResponsesFinish()],
            _ => throw new InvalidOperationException("Unapproved Hello provider family.")
        };
        return new { requiredInputTexts = required, events };
    }
    private static object Text(string api, string text, params string[] required)
    {
        object[] events = api switch
        {
            "anthropic-messages" => [AnthropicStart("msg-text"), new { type = "content_block_start", index = 4, content_block = new { type = "text", text = "" } }, new { type = "content_block_delta", index = 4, delta = new { type = "text_delta", text } }, new { type = "content_block_stop", index = 4 }, AnthropicFinish("end_turn"), new { type = "message_stop" }],
            "openai-completions" => [new { id = "cmpl-text", @object = "chat.completion.chunk", model = "pisharp-offline-completions-session", choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } } }, CompletionsFinish("stop"), CompletionsUsage()],
            "openai-responses" => [new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg", content = Array.Empty<object>() } }, new { type = "response.output_text.delta", output_index = 0, item_id = "msg", delta = text }, new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg", content = new[] { new { type = "output_text", text } } } }, ResponsesFinish()],
            _ => throw new InvalidOperationException("Unapproved Hello provider family.")
        }; return new { requiredInputTexts = required, events };
    }
    private static object AnthropicStart(string id) => new { type = "message_start", message = new { id, role = "assistant", model = "pisharp-offline-session", content = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 0, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 } } };
    private static object AnthropicFinish(string reason) => new { type = "message_delta", delta = new { stop_reason = reason }, usage = new { output_tokens = 4 } };
    private static object CompletionsFinish(string reason) => new { choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } } };
    private static object CompletionsUsage() => new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 8, completion_tokens = 4, total_tokens = 12 } };
    private static object ResponsesFinish() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private static JsonNode InitialRequest(string api, string user)
    {
        // Complete independently authored HTTP predicate. Registered input admission commits a content array.
        var declarations = JsonNode.Parse("""
            [{"name":"read","description":"Read UTF-8 text file contents, capped at 2000 lines or 50 KiB. Use offset/limit to continue. Images, binary and other encodings are unsupported in this profile.","input_schema":{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to read (relative or absolute)"},"offset":{"type":"integer","minimum":1,"description":"Line number to start reading from (1-indexed)"},"limit":{"type":"integer","minimum":0,"description":"Maximum number of lines to read"}},"required":["path"]},"eager_input_streaming":true},
             {"name":"write","description":"Write UTF-8 text content to a file, creating parent directories and overwriting existing contents. Bounded text profile; this is not atomic replacement.","input_schema":{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to write (relative or absolute)"},"content":{"type":"string","description":"Content to write to the file"}},"required":["path","content"]},"eager_input_streaming":true}]
            """)!.AsArray();
        declarations.Add(new JsonObject { ["name"] = "hello", ["description"] = "A simple greeting tool", ["input_schema"] = JsonNode.Parse(Parameters) });
        if (api == "anthropic-messages")
        {
            declarations[2]!["eager_input_streaming"] = true; declarations[2]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            return new JsonObject { ["model"] = "pisharp-offline-session", ["max_tokens"] = 8192, ["stream"] = true, ["tools"] = declarations,
                ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Explicit offline session file tools.", ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }),
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = user, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }) }) };
        }
        var tools = new JsonArray(); foreach (var row in declarations)
        {
            var parameters = row!["input_schema"]!.DeepClone(); if (row["name"]!.GetValue<string>() != "hello") parameters["additionalProperties"] = false;
            var function = new JsonObject { ["name"] = row["name"]!.DeepClone(), ["description"] = row["description"]!.DeepClone(), ["parameters"] = parameters };
            if (api == "openai-completions") tools.Add(new JsonObject { ["type"] = "function", ["function"] = function }); else { function["type"] = "function"; function["strict"] = false; tools.Add(function); }
        }
        if (api == "openai-completions") return new JsonObject { ["model"] = "pisharp-offline-completions-session", ["stream"] = true, ["store"] = false, ["max_completion_tokens"] = 8192, ["stream_options"] = new JsonObject { ["include_usage"] = true }, ["tools"] = tools,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "Explicit offline session file tools." }, new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = user }) }) };
        return new JsonObject { ["model"] = "pisharp-offline-session", ["stream"] = true, ["store"] = false, ["tools"] = tools,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "Explicit offline session file tools." }, new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = user }) }) };
    }

    private sealed record Result(int ExitCode, string Output, string Error);
    private static ProcessStartInfo Start(Files files, string invocation, string command, IEnumerable<string> extra)
    {
        var start = new ProcessStartInfo(files.C.Host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = Utf8 };
        start.Environment["PISHARP_NATIVE_NODE_HELLO_OPTIONS"] = Path.Combine(invocation, "options.json"); start.ArgumentList.Add(files.C.Cli);
        foreach (var argument in new[] { "session", command, "--session", files.Session, "--workspace", files.Root, "--offline-api", files.Api }.Concat(command == "create" ? [] : new[] { "--offline-script", files.Script }).Concat(files.ExtensionArgs).Concat(extra)) start.ArgumentList.Add(argument);
        return start;
    }
    private static async Task<Result> OneShot(Files files, string command, string[] extra)
    {
        var invocation = await files.Invocation(command); using var process = Process.Start(Start(files, invocation, command, extra)) ?? throw new IOException("Actual Hello CLI did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); process.StandardInput.Close();
        using var stdout = new RetainedStream(process.StandardOutput.BaseStream); using var stderr = new RetainedStream(process.StandardError.BaseStream);
        var output = Drain(stdout); var error = Drain(stderr); Exception? primary = null;
        try { await JoinAlive(process, output, error, deadline.Token); }
        catch (Exception failure) { primary = failure; }
        finally { await StopAndRetain(process, invocation, stdout, stderr, output, error); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw(); return new(process.ExitCode, Utf8.GetString(stdout.Bytes), Utf8.GetString(stderr.Bytes));
    }
    private static async Task Drain(Stream source) { var buffer = new byte[8192]; while (await source.ReadAsync(buffer) != 0) { } }
    private static async Task JoinAlive(Process process, Task output, Task error, CancellationToken token)
    {
        var exit = process.WaitForExitAsync(); var all = Task.WhenAll(exit, output, error); var watches = new List<Task> { all, output, error };
        while (!all.IsCompleted) { var first = await Task.WhenAny(watches).WaitAsync(token); await first; if (ReferenceEquals(first, all)) break; watches.Remove(first); }
        await all;
    }
    private static async Task StopAndRetain(Process process, string invocation, RetainedStream stdout, RetainedStream stderr, Task output, Task error)
    {
        var killed = false;
        try { if (!process.HasExited) { killed = true; process.Kill(entireProcessTree: true); } await process.WaitForExitAsync(); }
        finally
        {
            Exception? streamFailure = null; try { await Task.WhenAll(output, error); } catch (Exception failure) { streamFailure = failure; }
            await File.WriteAllBytesAsync(Path.Combine(invocation, "cli.stdout.jsonl"), stdout.Bytes); await File.WriteAllBytesAsync(Path.Combine(invocation, "cli.stderr.bin"), stderr.Bytes);
            await File.WriteAllTextAsync(Path.Combine(invocation, "cli.exit.json"), JsonSerializer.Serialize(new { exitCode = process.ExitCode, hasExited = process.HasExited, killed, outputJoined = output.IsCompleted, errorJoined = error.IsCompleted,
                outputSucceeded = output.IsCompletedSuccessfully, errorSucceeded = error.IsCompletedSuccessfully, streamFailure = streamFailure?.ToString() }) + "\n", Utf8);
        }
    }
    private sealed class RpcChild : IAsyncDisposable
    {
        private readonly Process process; private readonly string invocation; private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        private readonly object gate = new(); private readonly List<JsonData> records = []; private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RetainedStream stdout, stderr; private readonly Task output, error; private bool closed;
        internal JsonData[] Records { get { lock (gate) return records.ToArray(); } }
        private RpcChild(Files files, string invocation, string[] extra)
        {
            this.invocation = invocation; process = Process.Start(NodeHelloPackageTests.Start(files, invocation, "rpc", extra)) ?? throw new IOException("Actual Hello RPC did not start.");
            stdout = new(process.StandardOutput.BaseStream); stderr = new(process.StandardError.BaseStream); output = ReadOutput(); error = Drain(stderr);
        }
        internal static async Task<RpcChild> Start(Files files, string[] extra) => new(files, await files.Invocation("rpc"), extra);
        internal async Task Send(object command) { await process.StandardInput.WriteAsync((JsonSerializer.Serialize(command) + "\n").AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
        internal Task<JsonData> Response(string id) => Wait(record => Type(record) == "response" && record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        internal async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        {
            while (true)
            {
                Task next; lock (gate) { var match = records.FirstOrDefault(predicate); if (match is not null) return match;
                    if (output.IsFaulted) ExceptionDispatchInfo.Capture(output.Exception!.InnerException!).Throw();
                    Check(!output.IsCompleted, "Actual Hello RPC ended before its observer: stderr=" + Bounded(Utf8.GetString(stderr.Bytes))); next = changed.Task; }
                var signal = await Task.WhenAny(next, output, error).WaitAsync(deadline.Token); if (ReferenceEquals(signal, error)) { await error; await Task.WhenAny(next, output).WaitAsync(deadline.Token); } else await signal;
            }
        }
        private async Task ReadOutput()
        {
            try
            {
                await using var reader = new JsonlReader(stdout);
                await foreach (var frame in reader.ReadAdmissionsAsync())
                {
                    Check(frame.IsAccepted && !frame.IsFinalFrame, "Actual Hello RPC stdout has a malformed/truncated frame."); TaskCompletionSource signal;
                    lock (gate) { Check(records.Count < 2048, "Actual Hello RPC record limit."); records.Add(frame.Record!); signal = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); } signal.TrySetResult();
                }
            }
            finally { lock (gate) changed.TrySetResult(); }
        }
        internal async Task<Result> Finish()
        { if (!closed) { closed = true; process.StandardInput.Close(); } await JoinAlive(process, output, error, deadline.Token); return new(process.ExitCode, Utf8.GetString(stdout.Bytes), Utf8.GetString(stderr.Bytes)); }
        public async ValueTask DisposeAsync()
        { try { await StopAndRetain(process, invocation, stdout, stderr, output, error); } finally { stdout.Dispose(); stderr.Dispose(); process.Dispose(); deadline.Dispose(); } }
    }
    // Exact byte reads feeding the normal decoder; never reconstruct stdout from decoded records.
    private sealed class RetainedStream(Stream source) : Stream
    {
        private readonly object gate = new(); private readonly MemoryStream bytes = new(); internal byte[] Bytes { get { lock (gate) return bytes.ToArray(); } }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private void Retain(ReadOnlySpan<byte> value) { lock (gate) { Check(value.Length <= 2_097_152 - bytes.Length, "Actual Hello I/O receipt limit."); bytes.Write(value); } }
        public override int Read(byte[] buffer, int offset, int count) { var read = source.Read(buffer, offset, count); Retain(buffer.AsSpan(offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await source.ReadAsync(buffer, cancellationToken); Retain(buffer.Span[..read]); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) bytes.Dispose(); base.Dispose(disposing); }
    }

    private sealed class Files
    {
        internal Context C { get; } internal string Api { get; } internal string Root { get; } private readonly bool authoredFinalReplacement;
        private readonly List<string> invocations = []; private readonly JsonData golden;
        internal string In(string name) => Path.Combine(Root, name); internal string Session => In("session.jsonl"); internal string Script => In("script.json");
        internal string Package => In("published"); internal string Manifest => In("manifest.json"); internal string Approval => In("approval.json"); internal string Snapshots => In("snapshots");
        internal string[] ExtensionArgs => ["--extension-package", Package, "--extension-manifest", Manifest, "--extension-approval", Approval, "--extension-snapshot-root", Snapshots, "--enable-extension-tool", "hello"];
        private Files(Context c, string api, bool replacement, JsonData expected) { C = c; Api = api; authoredFinalReplacement = replacement; golden = expected; Root = Path.Combine(c.Runs, "node-hello-" + api + "-" + Guid.NewGuid().ToString("N")); }
        internal JsonElement Golden(string id) => golden.Value.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == id);
        internal static async Task<Files> Create(Context c, string api, bool authoredFinalReplacement = false)
        {
            var expected = await File.ReadAllBytesAsync(Path.Combine(c.HelloReference, "fixtures/reference/node-hello-preparation/expected.json")); Equal(ExpectedSha, Convert.ToHexStringLower(SHA256.HashData(expected)));
            var files = new Files(c, api, authoredFinalReplacement, JsonData.Parse(Utf8.GetString(expected))); foreach (var path in new[] { files.Root, files.Package, files.Snapshots }) Directory.CreateDirectory(path);
            Check(File.Exists(Path.Combine(c.Published, "PublishedFixture.NodeHello.dll")), "Root must publish the actual Hello package first.");
            foreach (var path in Directory.GetFiles(c.Published, "*", SearchOption.AllDirectories)) { var target = Path.Combine(files.Package, Path.GetRelativePath(c.Published, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target); }
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var path in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) hashes.Add(Path.GetRelativePath(files.Package, path).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
            var metadata = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.node.hello", packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = "PublishedFixture.NodeHello.dll", entryType = "PublishedNodeHelloFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers", "tool-invocation-context" }, declaredCapabilities = new[] { "observations" }, resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.NodeHello.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            await File.WriteAllTextAsync(files.Manifest, metadata, Utf8);
            await File.WriteAllTextAsync(files.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = files.Package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(metadata))), artifactHashes = hashes, sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1, sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = new[] { "hello" },
                systemImports = new[] { new { profile = "windows-worker-directory-0", artifact = "PiSharp.ExtensionHost.dll", sha256 = hashes["PiSharp.ExtensionHost.dll"] } } }), Utf8); return files;
        }
        internal async Task<string> Invocation(string command)
        {
            var root = In("invocation-" + (invocations.Count + 1) + "-" + command); Directory.CreateDirectory(root); invocations.Add(root);
            await File.WriteAllTextAsync(Path.Combine(root, "options.json"), JsonSerializer.Serialize(new { schemaVersion = 1, node = C.Node, repository = C.Repository, oracle = C.Oracle, jiti = C.Jiti, reference = C.Reference, helloReference = C.HelloReference, workspace = Root,
                runRoot = Path.Combine(root, "worker"), receipt = Path.Combine(root, "package.receipt.json"), workerGeneration = invocations.Count, sessionGeneration = invocations.Count, authoredFinalReplacement }), Utf8); return root;
        }
        internal async Task<JsonElement> Receipt(int index)
        { var bytes = await File.ReadAllBytesAsync(Path.Combine(invocations[index], "package.receipt.json")); Check(bytes.Length <= 2_097_152, "Hello package receipt bound."); return JsonData.Parse(Utf8.GetString(bytes)).Value; }
        internal async Task<string> LastFailure() { var path = Path.Combine(invocations[^1], "package.receipt.json"); return File.Exists(path) && new FileInfo(path).Length <= 2_097_152 ? Bounded(await File.ReadAllTextAsync(path)) : "no bounded actual package receipt"; }
        internal async Task VerifyReceipts(int expected)
        {
            Equal(expected, invocations.Count); Equal(0, Directory.GetDirectories(Snapshots).Length);
            for (var index = 0; index < invocations.Count; index++)
            {
                var receipt = await Receipt(index); Equal("initialized", receipt.GetProperty("initializationStage").GetString()); Equal(JsonValueKind.Null, receipt.GetProperty("initializationFailure").ValueKind);
                Equal(0, receipt.GetProperty("activeNativeContexts").GetInt32()); Equal(0, receipt.GetProperty("cleanupFailures").GetArrayLength()); Equal(0, receipt.GetProperty("executedCorpusCountCredit").GetInt32()); Check(!receipt.GetProperty("phaseAcceptanceClaimed").GetBoolean(), "Workflow assigned premature phase credit.");
                var load = receipt.GetProperty("sourceLoad"); Check(load.GetProperty("factoryAwaited").GetBoolean() && load.GetProperty("sourceFunctionRemainsInNode").GetBoolean(), "Genuine source activation proof missing.");
                Equal("0aa4e9800c2526914d4c1edb00b2cfa9bd9dd6da5218289994cccd5f5bfa4934", load.GetProperty("source").GetProperty("sha256").GetString()); Equal(Parameters, load.GetProperty("descriptor").GetProperty("parametersJson").GetString()); Equal("Hello", load.GetProperty("descriptor").GetProperty("label").GetString());
                foreach (var field in new[] { "actualPublicTypeIdentity", "actualPublicValidationIdentity", "actualDefineToolIdentity", "originalSchemaRetained" }) Check(load.GetProperty("moduleIdentity").GetProperty(field).GetBoolean(), "Actual source identity missing: " + field);
                Check(!load.GetProperty("moduleIdentity").GetProperty("symbolForTypeboxKindPresent").GetBoolean(), "Qualified TypeBox metadata fact differs.");
                Check(load.GetProperty("schema").GetProperty("descriptors").EnumerateArray().Any(row => row.GetProperty("key").GetString() == "~kind" && !row.GetProperty("enumerable").GetBoolean()), "Live nonenumerable TypeBox kind was lost.");
                foreach (var path in new[] { "upstream/packages/coding-agent/src/core/extensions/loader.ts", "upstream/packages/coding-agent/src/core/extensions/runner.ts", "upstream/packages/ai/src/utils/validation.ts" }) Check(load.GetProperty("loadedModules").EnumerateArray().Any(row => row.GetProperty("path").GetString() == path), "Whole genuine loaded module absent: " + path);
                Check(load.GetProperty("loadedModules").EnumerateArray().Any(row => row.GetProperty("path").GetString()!.StartsWith("jiti-root/node_modules/jiti/", StringComparison.Ordinal)), "Official Jiti module missing.");
                Equal(ExpectedSha, load.GetProperty("preparationReference").GetProperty("expectedSha256").GetString());
                var final = receipt.GetProperty("sourceFinalization"); Check(final.GetProperty("immutableInputsVerified").GetBoolean() && final.GetProperty("invalidated").GetBoolean(), "Actual source finalization/immutable verification missing.");
                Equal(final.GetProperty("qualifiedInventories").GetProperty("before").GetRawText(), final.GetProperty("qualifiedInventories").GetProperty("after").GetRawText());
                if (index == 0) Equal(0, receipt.GetProperty("sourceOperations").GetArrayLength());
                var termination = receipt.GetProperty("termination"); Equal(0, termination.GetProperty("ExitCode").GetInt32());
                Check(termination.GetProperty("HasExited").GetBoolean() && !termination.GetProperty("KillAttempted").GetBoolean() && termination.GetProperty("PinsRechecked").GetBoolean(), "Actual worker natural-exit/pin join missing.");
                Equal(0, termination.GetProperty("Failures").GetArrayLength()); Equal(0L, termination.GetProperty("ObservedStderrBytes").GetInt64()); Equal(0L, termination.GetProperty("ObservedStdoutAfterProtocolBytes").GetInt64());
                Check(termination.GetProperty("StdoutAfterProtocolEof").GetBoolean(), "Actual Node stdout EOF was not joined."); Equal(3, termination.GetProperty("ExplicitStreamCloses").GetInt32());
                var protocol = termination.GetProperty("Protocol"); foreach (var field in new[] { "PendingCalls", "ActiveCallbacks", "PendingWrites", "RegisteredHandles", "BufferedBytes" }) Equal(0L, protocol.GetProperty(field).GetInt64()); Check(protocol.GetProperty("Stopped").GetBoolean(), "Worker protocol remained open.");
                var assemblies = receipt.GetProperty("assemblies"); var entry = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == "PublishedFixture.NodeHello");
                foreach (var name in new[] { "PiSharp.Compatibility.Node", "PiSharp.ExtensionHost" }) { var actual = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name); Equal(entry.GetProperty("context").GetString(), actual.GetProperty("context").GetString()); Check(actual.GetProperty("collectible").GetBoolean(), "Private Hello assembly escaped its package."); }
                foreach (var name in new[] { "PiSharp.Extensions.Abstractions", "PiSharp.Contracts" }) { var actual = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name); Equal("Default", actual.GetProperty("context").GetString()); Check(!actual.GetProperty("collectible").GetBoolean(), "Shared ABI identity was duplicated."); }
                foreach (var name in new[] { "launch.receipt.json", "termination.json" }) Check(File.Exists(Path.Combine(invocations[index], "worker", name)), "Physical supervisor receipt missing.");
                var cli = JsonData.Parse(await File.ReadAllTextAsync(Path.Combine(invocations[index], "cli.exit.json"))).Value; Check(cli.GetProperty("hasExited").GetBoolean() && !cli.GetProperty("killed").GetBoolean() && cli.GetProperty("outputSucceeded").GetBoolean() && cli.GetProperty("errorSucceeded").GetBoolean(), "Actual CLI pipes/process did not naturally join.");
            }
        }
    }
    private static string Absolute(string path) { Check(Path.IsPathFullyQualified(path), "Explicit absolute Hello gate path required."); return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
    private static string CallId(string api, string call) => api switch { "openai-responses" => call + "|fc-" + call, "anthropic-messages" or "openai-completions" => call, _ => throw new InvalidOperationException("Unapproved identity mapping.") };
    private static string Type(JsonData record) => record.Value.GetProperty("type").GetString()!;
    private static JsonElement Good(JsonData record) { Check(record.Value.GetProperty("success").GetBoolean(), "Actual Hello RPC command failed: " + Bounded(record.ToString())); return record.Value; }
    private static void Clean(Result result) => Check(result.ExitCode == 0 && result.Error.Length == 0, "Actual Hello CLI/RPC failure: exit=" + result.ExitCode + " stderr=" + Bounded(result.Error) + " stdout=" + Bounded(result.Output));
    private static string Bounded(string value) => JsonSerializer.Serialize(value.Length <= 6144 ? value : value[..6144] + "[truncated; complete raw bytes retained]");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void SameJson(JsonElement expected, JsonElement actual) => Check(JsonElement.DeepEquals(expected, actual), "Complete source observation differs; JSON formatting is not source data.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Actual values differ: expected=" + expected + " actual=" + actual);
}
