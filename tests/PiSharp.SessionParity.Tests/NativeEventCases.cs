using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent.Execution;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Sessions.Compaction;
using PiSharp.Tools.Processes;
using static EventFixture;

// Pi v1.1.0 packages/coding-agent/src/core/agent-session.ts (_emitExtensionEvent, setThinkingLevel, _emitModelSelect,
// _emitSessionCompactFailed), core/extensions/runner.ts (emit, emitUserBash) and core/extensions/types.ts, by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> NativeEventCases() =>
    [
        Case("native.agent-loop-events-order-payloads-and-precede-rpc", NativeAgentLoopEvents),
        Case("native.tool-execution-events-with-duration-and-tool-result-messages", NativeToolEvents),
        Case("native.thinking-level-select-and-model-select-payloads", NativeSelectEvents),
        Case("native.session-compact-failed-payload", NativeCompactFailed),
        Case("native.handler-error-reported-with-message-and-next-handler-runs", NativeHandlerErrors),
        Case("native.user-bash-result-operations-invalid-and-failure", NativeUserBash),
    ];

    private sealed record Seen(string Topic, JsonData Value, int RpcRecordsBefore);

    private static async Task<(EventFixture Fixture, List<Seen> Seen)> ObservedAsync(bool withTool, IEnumerable<string> topics, params AssistantMessage[] responses)
    {
        var f = await CreateAsync(withTool, responses); var seen = new List<Seen>();
        await f.Activate(api =>
        {
            foreach (var topic in topics)
                api.Observe(new("observe-" + topic, topic, (value, _, _) =>
                { lock (seen) seen.Add(new(topic, value, f.Output.Lines().Length)); return ValueTask.CompletedTask; }));
        });
        new NativeSessionEventBinding(f.Registry, f.Registry.CaptureSnapshot(), f.Report).Attach(f.Owner, f.Owner.Current);
        return (f, seen);
    }
    private static string[] Keys(JsonData value) => value.Value.EnumerateObject().Select(property => property.Name).ToArray();

    private static async Task NativeAgentLoopEvents()
    {
        var (f, seen) = await ObservedAsync(false, NativeSessionEventBinding.AgentTopics, Response(text: "hello"));
        await using var owned = f;
        f.StartRpc(); await f.PromptAsync();
        var topics = seen.Select(row => row.Topic).ToArray();
        Check(topics.SequenceEqual(["agent_start", "turn_start", "message_start", "message_end", "message_start", "message_update", "message_update",
            "message_update", "message_end", "turn_end", "agent_end"]), "Native event order differs: " + string.Join(",", topics));
        var rpc = f.Frames();
        foreach (var row in seen.Where(row => row.Topic is "agent_start" or "turn_start" or "turn_end" or "agent_end"))
            Check(!rpc.Take(row.RpcRecordsBefore).Any(frame => Type(frame) == row.Topic), row.Topic + " reached RPC listeners before extensions.");
        Equal("""{"type":"agent_start"}""", seen[0].Value.ToString(), "agent_start");
        Check(Keys(seen[1].Value).SequenceEqual(["type", "turnIndex", "timestamp"]) && seen[1].Value.Value.GetProperty("turnIndex").GetInt32() == 0, "turn_start shape");
        Equal("user", seen[2].Value.Value.GetProperty("message").GetProperty("role").GetString(), "user message_start");
        var updates = seen.Where(row => row.Topic == "message_update").Select(row => row.Value.Value).ToArray();
        Check(updates.Select(update => update.GetProperty("assistantMessageEvent").GetProperty("type").GetString()).SequenceEqual(["text_start", "text_delta", "text_end"]),
            "message_update kinds differ.");
        Check(updates.All(update => Keys(JsonData.FromElement(update)).SequenceEqual(["type", "message", "assistantMessageEvent"])), "message_update shape.");
        Equal("hello", updates[1].GetProperty("assistantMessageEvent").GetProperty("delta").GetString(), "delta");
        Equal("hello", updates[2].GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString(), "partial message");
        var assistantEntry = f.Session.Snapshot.Log.Entries.Last(entry => entry.Type == "message");
        Check(JsonElement.DeepEquals(seen[8].Value.Value.GetProperty("message"), assistantEntry.WireBody.Value.GetProperty("message")), "assistant message_end is not the acknowledged message.");
        var turnEnd = seen[9].Value.Value;
        Check(Keys(seen[9].Value).SequenceEqual(["type", "turnIndex", "message", "toolResults", "messageEntryId", "toolResultEntryIds", "outcome",
            "entries", "continue", "context"]), "turn_end shape: " + string.Join(",", Keys(seen[9].Value)));
        Equal(assistantEntry.Id, turnEnd.GetProperty("messageEntryId").GetString(), "messageEntryId");
        Equal("completed", turnEnd.GetProperty("outcome").GetString(), "outcome");
        Check(!turnEnd.GetProperty("continue").GetBoolean() && turnEnd.GetProperty("entries").GetArrayLength() == 0, "boundary defaults");
        var context = turnEnd.GetProperty("context");
        Check(Keys(JsonData.FromElement(context)).SequenceEqual(["contextEntries", "contextMessages", "llmMessages", "pendingMessages", "canContinue"]) &&
            !context.GetProperty("canContinue").GetBoolean(), "turn_end context preview");
        var agentEnd = seen[10].Value.Value.GetProperty("messages");
        Check(agentEnd.GetArrayLength() == 2 && agentEnd[0].GetProperty("role").GetString() == "user" && agentEnd[1].GetProperty("role").GetString() == "assistant",
            "agent_end messages are not the run's new messages.");
        Equal(0, f.Diagnostics.Count, "diagnostics");
    }

    private static async Task NativeToolEvents()
    {
        var (f, seen) = await ObservedAsync(true, ["tool_execution_start", "tool_execution_end", "message_start", "message_end", "turn_end"],
            ToolCall(), Response(text: "done"));
        await using var owned = f;
        await f.Session.SetActiveToolsAsync(["probe"]);
        f.StartRpc(); await f.PromptAsync();
        var start = seen.Single(row => row.Topic == "tool_execution_start").Value;
        Equal("""{"type":"tool_execution_start","toolCallId":"call-1","toolName":"probe","args":{}}""", start.ToString(), "tool_execution_start");
        var end = seen.Single(row => row.Topic == "tool_execution_end").Value.Value;
        Check(Keys(JsonData.FromElement(end)).SequenceEqual(["type", "toolCallId", "toolName", "result", "isError", "durationMs"]), "tool_execution_end shape");
        Check(end.GetProperty("durationMs").GetInt64() >= 0 && !end.GetProperty("isError").GetBoolean(), "tool_execution_end values");
        Equal("probe output", end.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), "tool result");
        var rpcEnd = f.Frames().Single(frame => Type(frame) == "tool_execution_end").Value;
        Check(rpcEnd.TryGetProperty("durationMs", out _), "RPC tool_execution_end lacks durationMs.");
        var toolMessages = seen.Where(row => row.Topic is "message_start" or "message_end" && row.Value.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult").ToArray();
        Check(toolMessages.Select(row => row.Topic).SequenceEqual(["message_start", "message_end"]), "tool result messages");
        var firstTurn = seen.First(row => row.Topic == "turn_end").Value.Value;
        Check(firstTurn.GetProperty("toolResults").GetArrayLength() == 1 && firstTurn.GetProperty("toolResultEntryIds").GetArrayLength() == 1, "turn_end tool results");
    }

    private static async Task NativeSelectEvents()
    {
        var (f, seen) = await ObservedAsync(false, ["thinking_level_select", "model_select"]);
        await using var owned = f;
        var rpc = f.StartRpc();
        await rpc.SubmitAsync(JsonData.Parse("""{"type":"set_thinking_level","id":"t","level":"high"}"""));
        await rpc.SubmitAsync(JsonData.Parse("""{"type":"set_model","id":"m","provider":"fixture","modelId":"parity-events-2"}"""));
        await rpc.SubmitAsync(JsonData.Parse("""{"type":"cycle_model","id":"c"}"""));
        await rpc.SubmitAsync(JsonData.Parse("""{"type":"set_model","id":"same","provider":"fixture","modelId":"parity-events"}"""));
        Equal("""{"type":"thinking_level_select","level":"high","previousLevel":"off"}""", seen[0].Value.ToString(), "thinking_level_select");
        var models = seen.Where(row => row.Topic == "model_select").Select(row => row.Value.Value).ToArray();
        Equal(2, models.Length, "model_select count (unchanged model emits none)");
        Check(Keys(JsonData.FromElement(models[0])).SequenceEqual(["type", "model", "previousModel", "source"]), "model_select shape");
        Check(models[0].GetProperty("model").GetProperty("id").GetString() == "parity-events-2" && models[0].GetProperty("previousModel").GetProperty("id").GetString() == "parity-events" &&
            models[0].GetProperty("source").GetString() == "set", "set model_select payload");
        Check(models[1].GetProperty("model").GetProperty("id").GetString() == "parity-events" && models[1].GetProperty("source").GetString() == "cycle", "cycle model_select payload");
    }

    private static async Task NativeCompactFailed()
    {
        var (f, seen) = await ObservedAsync(false, ["session_compact_failed"]);
        await using var owned = f;
        await ThrowsAsync<SessionCompactionException>(() => f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id,
            new(new(KeepRecentTokens: 1), ContextWindow: 128_000), new FlakySummary("invalid api key")), "failed compaction");
        var failed = seen.Single().Value.Value;
        Check(Keys(JsonData.FromElement(failed)).SequenceEqual(["type", "reason", "errorMessage", "aborted", "willRetry", "fromExtension"]), "session_compact_failed shape");
        Check(failed.GetProperty("reason").GetString() == "manual" && failed.GetProperty("errorMessage").GetString()!.StartsWith("Compaction failed: ", StringComparison.Ordinal) &&
            !failed.GetProperty("aborted").GetBoolean() && !failed.GetProperty("willRetry").GetBoolean() && !failed.GetProperty("fromExtension").GetBoolean(),
            "session_compact_failed values");
    }

    private static async Task NativeHandlerErrors()
    {
        await using var f = await CreateAsync(Response()); var calls = new List<string>();
        await f.Activate(api =>
        {
            api.Observe(new("first", "agent_start", (_, _, _) => { calls.Add("first"); throw new InvalidOperationException("first handler failed"); }));
            api.Observe(new("second", "agent_start", (_, _, _) => { calls.Add("second"); return ValueTask.CompletedTask; }));
        });
        new NativeSessionEventBinding(f.Registry, f.Registry.CaptureSnapshot(), f.Report).Attach(f.Owner, f.Owner.Current);
        f.StartRpc(); await f.PromptAsync();
        Check(calls.SequenceEqual(["first", "second"]), "A failing handler stopped the remaining handlers.");
        var diagnostic = f.Diagnostics.Single();
        Check(diagnostic.EventName == "agent_start" && diagnostic.RegistrationId == "first" && diagnostic.Message == "first handler failed" &&
            diagnostic.ErrorText == "first handler failed", "Diagnostic lacks the handler message.");
        Check(f.Frames().Any(frame => Type(frame) == "agent_settled"), "A handler failure failed the run.");
    }

    private sealed class FakeShell(int? exitCode) : IShellOperations, IExtensionShellOperations
    {
        internal readonly List<string> Commands = [];
        public async ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ProcessRawOutputCallback onData, CancellationToken token)
        { Commands.Add(command); await onData("remote output\n"u8.ToArray()); return exitCode; }
        public async ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ExtensionShellOutputCallback onData, CancellationToken token)
        { Commands.Add(command); await onData("remote output\n"u8.ToArray()); return exitCode; }
    }

    private static async Task NativeUserBash()
    {
        await using var f = await CreateAsync(); var remote = new FakeShell(0); var mode = "result";
        await f.Activate(api => ((IExtensionUserBashRegistry)api).RegisterUserBashHandler(new("bash", (bashEvent, _, _) => mode switch
        {
            "result" => ValueTask.FromResult<ExtensionUserBashPatch?>(new() { Result = new("handled " + bashEvent.Command + (bashEvent.ExcludeFromContext ? " excluded" : ""), 3) }),
            "operations" => ValueTask.FromResult<ExtensionUserBashPatch?>(new() { Operations = remote }),
            "invalid" => ValueTask.FromResult<ExtensionUserBashPatch?>(new()),
            "throw" => throw new InvalidOperationException("handler exploded"),
            _ => ValueTask.FromResult<ExtensionUserBashPatch?>(null)
        })));
        var local = new FakeShell(7); var spill = Path.Combine(f.Root, "spill"); Directory.CreateDirectory(spill);
        var host = new UserBashHost(new ShellCommandExecutor(local, spill))
        { Handlers = [NativeExtensionActivation.CreateUserBashHandler(f.Registry, f.Registry.CaptureSnapshot(), f.Report, CancellationToken.None)] };
        Task<UserBashResult> Run(bool exclude = false) => host.ExecuteAsync(new("ls -la", f.Root, exclude), _ => Task.CompletedTask, CancellationToken.None);
        var handled = await Run(exclude: true);
        Check(handled.Output == "handled ls -la excluded" && handled.ExitCode == 3 && local.Commands.Count == 0, "The handler result was not recorded as is.");
        mode = "operations"; var operated = await Run();
        Check(remote.Commands.SequenceEqual(["ls -la"]) && local.Commands.Count == 0 && operated.Output.TrimEnd('\n') == "remote output" && operated.ExitCode == 0,
            "Handler operations did not replace the local shell: " + operated.Output);
        mode = "invalid"; var invalid = await ThrowsAsync<InvalidOperationException>(() => Run(), "invalid patch");
        Check(invalid.Message.StartsWith("Invalid user_bash handler result", StringComparison.Ordinal) && local.Commands.Count == 0, "Invalid patch fell back.");
        mode = "throw"; await ThrowsAsync<InvalidOperationException>(() => Run(), "throwing handler");
        Check(local.Commands.Count == 0 && f.Diagnostics.Count == 2 && f.Diagnostics.All(d => d.EventName == "user_bash") &&
            f.Diagnostics[1].Message == "handler exploded", "Handler failure did not report or fell back to the local shell.");
        mode = "none"; var fallthrough = await Run();
        Check(local.Commands.SequenceEqual(["ls -la"]) && fallthrough.ExitCode == 7, "A null patch did not continue with local execution.");
    }
}
