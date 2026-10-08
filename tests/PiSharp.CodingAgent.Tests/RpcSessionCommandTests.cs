using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Rpc.Ui;
using PiSharp.CodingAgent;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static class RpcSessionCommandTests
{
    private static string _host = "", _cli = "";
    public static (string Name, Func<Task> Run)[] Cases(string dotnetHost, string cliDll)
    {
        if (!Path.IsPathFullyQualified(dotnetHost) || !Path.IsPathFullyQualified(cliDll) || !File.Exists(dotnetHost) || !File.Exists(cliDll))
            throw new ArgumentException("RPC child tests require existing explicit host and CLI paths.");
        _host = dotnetHost; _cli = cliDll;
        return
        [
            ("RPC compiled host reads state/steering/full clear while actual offline HTTP work is gated", AsyncQueues),
            ("RPC compiled host executes authorized file tools with canonical durable entries and shared LF output", ToolTurns),
            ("RPC compiled host abort cancels actual gated work, retains queues and commits aborted assistant", AbortWork),
            ("RPC compiled active EOF awaits aborted assistant/events and closes acknowledged durable writer", ActiveEof),
            ("RPC independent process resume and explicit leaf preserve source bytes and selected ancestry", ReopenBranch),
            ("RPC compiled host recovers malformed frames and rejects startup bounds without durable mutation", Admission),
            ("RPC direct host write/flush faults poison gated response authority and await durable close", OutputFailures),
            ("RPC shared output failures retain RpcHostFailed with zero new cleanup failures for every API", SharedOutputFailuresForApi),
            ("RPC retained output fault preserves original phase and genuine late cleanup cause order", OutputFailurePhases),
            ("RPC actual caller cancellation returns Canceled only after aborted durable work and lease closure", CallerCancellation),
            ("RPC explicit Anthropic file turns and independent selected branch preserve durable API state", AnthropicSessions),
            ("RPC Anthropic HTTP gate preserves queue/abort/EOF, output-fault authority and caller cancellation barriers", AnthropicLifecycle),
            ("RPC Completions actual file turns, acknowledged entries and independent selected branch retain provider identity", CompletionsSessions),
            ("RPC Completions preserves held HTTP queues, abort/EOF, output faults and strict startup admission", CompletionsLifecycle)
        ];
    }

    private static Task AsyncQueues() => AsyncQueuesForApi(null);
    private static async Task AsyncQueuesForApi(string? api)
    {
        using var files = new Files(); await Create(files, api);
        await Script(files.Script, WireText(api, "done", gate: true));
        await using var child = new Child(files, ApiArgs(api));
        var message = "prompt \u2028\u2029 \u6587\U0001f642";
        await child.Send(new { id = "prompt", type = "prompt", message });
        Success(await child.Response("prompt"), "prompt");
        await child.Wait(record => Type(record) == "message_start" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant");
        await child.Send(new { id = "held", type = "get_state" });
        var held = Success(await child.Response("held"), "get_state");
        Check(held.GetProperty("data").GetProperty("isStreaming").GetBoolean(), "State did not observe actual running work.");
        Equal(2, held.GetProperty("data").GetProperty("messageCount").GetInt32());
        Equal("authored-offline-profile", held.GetProperty("data").GetProperty("model").GetProperty("provenance").GetString());
        Check(!held.GetProperty("data").GetProperty("model").GetProperty("liveModelCapabilityClaimed").GetBoolean(), "Authored model became a live capability claim.");
        Equal(api ?? "openai-responses", held.GetProperty("data").GetProperty("model").GetProperty("api").GetString());
        Equal(api == "anthropic-messages" ? "anthropic" : "openai", held.GetProperty("data").GetProperty("model").GetProperty("provider").GetString());
        foreach (var (id, type, text) in new[] { ("s1", "steer", "steer one"), ("s2", "steer", "steer two"), ("f1", "follow_up", "follow one"), ("f2", "follow_up", "follow two") })
        { await child.Send(new { id, type, message = text }); Success(await child.Response(id), type); }
        await child.Send(new { id = "queued", type = "get_state" });
        Equal(4, Success(await child.Response("queued"), "get_state").GetProperty("data").GetProperty("pendingMessageCount").GetInt32());
        await child.Send(new { id = "clear", type = "clear_queue" });
        var cleared = Success(await child.Response("clear"), "clear_queue").GetProperty("data");
        Strings(["steer one", "steer two"], cleared.GetProperty("steering"));
        Strings(["follow one", "follow two"], cleared.GetProperty("followUp"));
        await child.Send(new { id = "release", type = "get_state" });
        var releasing = Success(await child.Response("release"), "get_state").GetProperty("data");
        Check(releasing.GetProperty("isStreaming").GetBoolean(), "Gate opened before its state response was captured/flushed.");
        Equal(0, releasing.GetProperty("pendingMessageCount").GetInt32());
        await child.Settled();
        await child.Send(new { id = "messages", type = "get_messages" });
        var messages = Success(await child.Response("messages"), "get_messages").GetProperty("data").GetProperty("messages");
        Check(messages.EnumerateArray().Any(entry => entry.GetProperty("role").GetString() == "user" &&
            entry.GetProperty("content").ValueKind == JsonValueKind.Array && entry.GetProperty("content").GetArrayLength() == 1 &&
            entry.GetProperty("content")[0].GetProperty("type").GetString() == "text" &&
            entry.GetProperty("content")[0].GetProperty("text").GetString() == message),
            "LF framing changed Unicode separators or prompt content.");
        Check(!messages.GetRawText().Contains("steer one", StringComparison.Ordinal), "Cleared queued text reached history.");
        var result = await child.Finish(); Clean(result);
        Equal(1, child.Records.Count(record => Type(record) == "agent_start"));
        Equal(1, child.Records.Count(record => Type(record) == "agent_settled"));
        Equal(1, child.Records.Count(record => Type(record) == "response" && Id(record) == "prompt"));
    }

    private static Task ToolTurns() => ToolTurnsForApi(null);
    private static async Task ToolTurnsForApi(string? api)
    {
        using var files = new Files(); await Create(files, api);
        var source = files.In("source.txt"); var output = files.In("output.txt");
        await File.WriteAllTextAsync(source, "existing source \u6587\n", new UTF8Encoding(false));
        var sourceBytes = await File.ReadAllBytesAsync(source); var initial = await File.ReadAllBytesAsync(files.Session);
        await Script(files.Script, api == "anthropic-messages" ?
                SessionCommandTests.AnthropicTool("read", "read", new { path = source }, ["tool request"], SessionCommandTests.AnthropicInitialRequest("tool request")) :
                WireTool(api, "read", "read", new { path = source }, "tool request"),
            WireTool(api, "write", "write", new { path = output, content = "saved output \U0001f642\n" }, "existing source"),
            WireText(api, "tool final", required: ["Successfully wrote"]));
        var scriptBytes = await File.ReadAllBytesAsync(files.Script);
        JsonElement entries; string? leaf;
        await using (var child = new Child(files, ApiArgs(api, "--allow-read", source, "--allow-write", output)))
        {
            await child.Send(new { id = "p", type = "prompt", message = "tool request" });
            Success(await child.Response("p"), "prompt"); await child.Settled();
            await child.Send(new { id = "api-state", type = "get_state" });
            var model = Success(await child.Response("api-state"), "get_state").GetProperty("data").GetProperty("model");
            Equal(api ?? "openai-responses", model.GetProperty("api").GetString());
            Equal(api == "anthropic-messages" ? "https://offline-session.invalid" : "https://offline-session.invalid/v1", model.GetProperty("baseUrl").GetString());
            Check(!model.GetProperty("liveModelCapabilityClaimed").GetBoolean(), "Offline API state became a live catalogue claim.");
            await child.Send(new { id = "entries", type = "get_entries" });
            var data = Success(await child.Response("entries"), "get_entries").GetProperty("data");
            entries = data.GetProperty("entries"); leaf = data.GetProperty("leafId").GetString();
            SessionLogReadResult acknowledged;
            // The child still owns its writer. Explicitly share that write access and await our read lease's close.
            await using (var acknowledgedSource = new FileStream(files.Session, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
                acknowledged = await new SessionLogReader().ReadAsync(acknowledgedSource, leaveOpen: true);
            Check(acknowledged.SourceComplete && acknowledged.ValidatedPrefix.Length == entries.GetArrayLength() + 1,
                "get_entries escaped before the actual durable checkpoint.");
            Check(acknowledged.Status == SessionLogReadStatus.Complete &&
                acknowledged.ValidatedPrefixByteLength == acknowledged.OriginalBytes.Length &&
                new FileInfo(files.Session).Length == acknowledged.OriginalBytes.Length,
                "Live checkpoint observation omitted acknowledged bytes or retained an incomplete log.");
            Check(entries.EnumerateArray().All(entry => entry.GetProperty("type").GetString() != "session"), "RPC inserted a session header.");
            Check(entries.EnumerateArray().Any(entry => entry.GetProperty("type").GetString() == "message" &&
                entry.GetProperty("message").GetProperty("role").GetString() == "toolResult"), "Durable tool results missing.");
            foreach (var entry in entries.EnumerateArray())
            {
                Check(entry.GetProperty("timestamp").ValueKind == JsonValueKind.String, "Entry timestamp was converted to message numeric time.");
                if (entry.GetProperty("type").GetString() == "message")
                    Check(entry.GetProperty("message").GetProperty("timestamp").TryGetInt64(out _), "Canonical message timestamp was rewritten.");
            }
            Clean(await child.Finish());
            Equal(2, child.Records.Count(record => Type(record) == "tool_execution_start"));
            Equal(2, child.Records.Count(record => Type(record) == "tool_execution_end"));
            if (api == "openai-completions")
            {
                var updates = child.Records.Where(record => Type(record) == "message_update")
                    .Select(record => record.Value.GetProperty("assistantMessageEvent")).ToArray();
                Equal(2, updates.Count(update => update.GetProperty("type").GetString() == "toolcall_start"));
                Equal(4, updates.Count(update => update.GetProperty("type").GetString() == "toolcall_delta"));
                Equal(2, updates.Count(update => update.GetProperty("type").GetString() == "toolcall_end"));
                var completed = child.Records.Last(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant")
                    .Value.GetProperty("message");
                Equal("openai-completions", completed.GetProperty("api").GetString()); Equal("pisharp-offline-completions-session", completed.GetProperty("model").GetString());
                Equal("cmpl-text", completed.GetProperty("responseId").GetString()); Equal("stop", completed.GetProperty("rawStopReason").GetString());
            }
        }
        Equal("saved output \U0001f642\n", await File.ReadAllTextAsync(output));
        var sourceAfter = await File.ReadAllBytesAsync(source); var scriptAfter = await File.ReadAllBytesAsync(files.Script);
        Check(sourceBytes.SequenceEqual(sourceAfter) && scriptBytes.SequenceEqual(scriptAfter), "Existing source/script was overwritten.");
        var read = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.OriginalBytes.AsSpan().StartsWith(initial), "Durable source prefix was rewritten.");
        var stored = read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToArray();
        Equal(entries.GetArrayLength(), stored.Length);
        for (var index = 0; index < stored.Length; index++) Same(entries[index], stored[index].WireBody.Value);
        Equal(leaf, stored[^1].Id);
        Check(!Encoding.UTF8.GetString(read.OriginalBytes.ToArray()).Contains("authored-inert-offline-session-value", StringComparison.Ordinal), "Request authorization reached persistence.");
        var denied = files.In("unallowed.txt");
        await Script(files.Script, WireTool(api, "write", "denied", new { path = denied, content = "no permission" }), WireText(api, "denial final"));
        await using (var child = new Child(files, ApiArgs(api)))
        {
            await child.Send(new { id = "p", type = "prompt", message = "deny unallowed effect" });
            Success(await child.Response("p"), "prompt"); await child.Settled();
            Check(child.Records.Single(record => Type(record) == "tool_execution_end").Value.GetProperty("isError").GetBoolean(),
                "Unallowed final target lost its structured tool error.");
            Clean(await child.Finish());
        }
        Check(!File.Exists(denied), "RPC host bypassed mandatory per-file policy.");
    }

    private static Task AbortWork() => AbortWorkForApi(null);
    private static async Task AbortWorkForApi(string? api)
    {
        using var files = new Files(); await Create(files, api); await Script(files.Script, WireText(api, "must not complete", gate: true));
        await using var child = new Child(files, ApiArgs(api));
        await child.Send(new { id = "p", type = "prompt", message = "cancel actual work" });
        Success(await child.Response("p"), "prompt");
        await child.Wait(record => Type(record) == "message_start" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant");
        await child.Send(new { id = "queued", type = "follow_up", message = "retain after abort" });
        Success(await child.Response("queued"), "follow_up");
        await child.Send(new { id = "steering", type = "steer", message = "retain steering" });
        Success(await child.Response("steering"), "steer");
        await child.Send(new { id = "abort", type = "abort" });
        Success(await child.Response("abort"), "abort"); await child.Settled();
        var assistant = child.Records.Single(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant").Value.GetProperty("message");
        Equal("aborted", assistant.GetProperty("stopReason").GetString());
        Check(!assistant.GetRawText().Contains("must not complete", StringComparison.Ordinal), "Canceled fake response acquired terminal authority.");
        await child.Send(new { id = "state", type = "get_state" });
        var state = Success(await child.Response("state"), "get_state").GetProperty("data");
        Check(!state.GetProperty("isStreaming").GetBoolean(), "Abort acknowledged before idle.");
        Equal(2, state.GetProperty("pendingMessageCount").GetInt32());
        await child.Send(new { id = "clear", type = "clear_queue" });
        var retained = Success(await child.Response("clear"), "clear_queue").GetProperty("data");
        Strings(["retain after abort"], retained.GetProperty("followUp")); Strings(["retain steering"], retained.GetProperty("steering"));
        Clean(await child.Finish());
        Equal(1, child.Records.Count(record => Type(record) == "agent_start"));
        Equal(0, child.Records.Count(record => Type(record).StartsWith("tool_execution", StringComparison.Ordinal)));
        var read = await new SessionLogReader().ReadFileAsync(files.Session);
        var stored = AcknowledgedCanceledAssistant(read, child.Records, api);
        Same(assistant, stored);
    }

    private static Task ActiveEof() => ActiveEofForApi(null);
    private static async Task ActiveEofForApi(string? api)
    {
        using var files = new Files(); await Create(files, api); var initial = await File.ReadAllBytesAsync(files.Session);
        await Script(files.Script, WireText(api, "unacquired response", gate: true));
        await using var child = new Child(files, ApiArgs(api));
        await child.Send(new { id = "p", type = "prompt", message = "EOF while request is held" });
        Success(await child.Response("p"), "prompt");
        await child.Wait(record => Type(record) == "message_start" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant");
        await child.Send(new { id = "queued", type = "follow_up", message = "do not continue during EOF" });
        Success(await child.Response("queued"), "follow_up");
        Clean(await child.Finish());
        var ended = child.Records.Single(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant");
        Equal("aborted", ended.Value.GetProperty("message").GetProperty("stopReason").GetString());
        Check(child.Records.Any(record => Type(record) == "agent_end") && child.Records.Any(record => Type(record) == "agent_settled"),
            "EOF lost awaited terminal protocol records.");
        Equal(1, child.Records.Count(record => Type(record) == "agent_start"));
        var read = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.OriginalBytes.AsSpan().StartsWith(initial), "EOF produced a damaged/rewritten log.");
        Same(ended.Value.GetProperty("message"), AcknowledgedCanceledAssistant(read, child.Records, api));
        // An independent writer acquisition after child exit proves its actual durable lease closed.
        await using var reopened = await SessionLogStore.OpenAsync(files.Session);
        Equal((long)read.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
    }

    private static JsonElement AcknowledgedCanceledAssistant(SessionLogReadResult read, JsonData[] records, string? api)
    {
        var entries = read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
        if (api != "openai-completions") return entries.Last(entry => entry.Type == "message").WireBody.Value.GetProperty("message");
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete &&
            read.ValidatedPrefixByteLength == read.OriginalBytes.Length, "Canceled diagnostic did not retain a complete acknowledged log.");
        var view = NativeSessionDiagnosticProjector.Project(new SessionContextProjector().Project(entries, entries[^1].Id));
        Equal(0, view.Issues.Length);
        var observation = view.Entries.Single();
        Equal(NativeSessionDiagnosticProvenance.DeclaredNativeRecord, observation.Provenance);
        Equal(new NativeChatDiagnostic(NativeChatAdapter.OpenAICompletions, NativeChatFailureCode.Cancelled), observation.Diagnostic);
        Equal(1L, observation.OperationGeneration);
        Equal<NativeChatDiagnostic?>(null, observation.CleanupDiagnostic);
        var assistantIndex = Array.FindIndex(entries.ToArray(), entry => entry.Id == observation.AssistantEntryId);
        var recordIndex = Array.FindIndex(entries.ToArray(), entry => entry.Id == observation.RecordEntryId);
        Check(assistantIndex >= 0 && recordIndex > assistantIndex && recordIndex == entries.Length - 1,
            "Canceled native record was not acknowledged after its associated assistant.");
        var assistant = entries[assistantIndex].WireBody.Value.GetProperty("message");
        Check(!assistant.TryGetProperty("openAICompletionsFailure", out _), "Native cancellation leaked into the Pi assistant body.");
        var nativeIndex = Array.FindIndex(records, record => Type(record) == "pisharp_chat_diagnostic");
        var native = records.Single(record => Type(record) == "pisharp_chat_diagnostic").Value;
        Equal(1, native.GetProperty("schemaVersion").GetInt32());
        var expected = NativeSessionDiagnosticProjector.ToWire(view).Value.GetProperty("entries")[0];
        Same(expected, native.GetProperty("data"));
        Check(Array.FindIndex(records, record => Type(record) == "turn_end") < nativeIndex &&
            nativeIndex < Array.FindIndex(records, record => Type(record) == "agent_settled"),
            "Canceled diagnostic escaped acknowledged turn/final settlement ordering.");
        return assistant;
    }

    private static Task ReopenBranch() => ReopenBranchForApi(null);
    private static async Task ReopenBranchForApi(string? api)
    {
        using var files = new Files(); await Create(files, api);
        await Script(files.Script, WireText(api, "first final", required: ["first user"]));
        string ancestor;
        await using (var first = new Child(files, ApiArgs(api)))
        {
            await first.Send(new { id = "p", type = "prompt", message = "first user" }); Success(await first.Response("p"), "prompt"); await first.Settled();
            await first.Send(new { id = "e", type = "get_entries" });
            ancestor = Success(await first.Response("e"), "get_entries").GetProperty("data").GetProperty("leafId").GetString()!;
            Clean(await first.Finish());
        }
        var firstBytes = await File.ReadAllBytesAsync(files.Session);
        await Script(files.Script, WireText(api, "resumed final", required: ["first user", "first final", "resumed user"]));
        await using (var second = new Child(files, ApiArgs(api)))
        {
            await second.Send(new { id = "p", type = "prompt", message = "resumed user" }); Success(await second.Response("p"), "prompt"); await second.Settled();
            AssertStopped(second, "resumed final"); Clean(await second.Finish());
        }
        var oldLog = await new SessionLogReader().ReadFileAsync(files.Session);
        var oldBytes = oldLog.OriginalBytes.ToArray(); Check(oldBytes.AsSpan().StartsWith(firstBytes), "Resume changed historical bytes.");
        await Script(files.Script, WireText(api, "branch final", required: ["first user", "first final", "branch user"]));
        await using (var branch = new Child(files, ApiArgs(api, "--leaf", ancestor)))
        {
            await branch.Send(new { id = "p", type = "prompt", message = "branch user" }); Success(await branch.Response("p"), "prompt"); await branch.Settled();
            AssertStopped(branch, "branch final");
            await branch.Send(new { id = "m", type = "get_messages" });
            var selected = Success(await branch.Response("m"), "get_messages").GetProperty("data").GetProperty("messages");
            Check(!selected.GetRawText().Contains("resumed user", StringComparison.Ordinal), "RPC model context inserted sibling history.");
            Clean(await branch.Finish());
        }
        var final = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(final.OriginalBytes.AsSpan().StartsWith(oldBytes), "Selected branch changed original source prefix.");
        Equal(ancestor, final.ValidatedPrefix[oldLog.ValidatedPrefix.Length].Entry.ParentId);
    }

    private static async Task AnthropicSessions()
    {
        await ToolTurnsForApi("anthropic-messages");
        await ReopenBranchForApi("anthropic-messages");
    }
    private static async Task AnthropicLifecycle()
    {
        await AsyncQueuesForApi("anthropic-messages");
        await AbortWorkForApi("anthropic-messages");
        await ActiveEofForApi("anthropic-messages");
        await OutputFailuresForApi("anthropic-messages");
        await CallerCancellationForApi("anthropic-messages");
    }

    private static async Task CompletionsSessions()
    {
        await ToolTurnsForApi("openai-completions");
        await ReopenBranchForApi("openai-completions");
    }
    private static async Task CompletionsLifecycle()
    {
        await AsyncQueuesForApi("openai-completions");
        await AbortWorkForApi("openai-completions");
        await ActiveEofForApi("openai-completions");
        await OutputFailuresForApi("openai-completions");
        await CallerCancellationForApi("openai-completions");
        using var files = new Files(); await Create(files, "openai-completions");
        var before = await File.ReadAllBytesAsync(files.Session);
        await Script(files.Script, SessionCommandTests.CompletionsText("unused response"));
        await using (var child = new Child(files, ApiArgs("openai-completions")))
        {
            await child.Raw("{broken}\n");
            var rejected = await child.Wait(record => Type(record) == "response" && record.Value.GetProperty("command").GetString() == "parse");
            Check(!rejected.Value.GetProperty("success").GetBoolean() && !rejected.Value.TryGetProperty("id", out _), "Invalid input acquired RPC correlation or success.");
            await child.Send(new { id = "state", type = "get_state" }); Success(await child.Response("state"), "get_state");
            Clean(await child.Finish());
        }
        // Known typed Responses/Anthropic events are rejected before acquiring the durable writer.
        foreach (var turn in new[] { Text("wrong wire"), SessionCommandTests.AnthropicText("wrong wire") })
        {
            await Script(files.Script, turn);
            await using var child = new Child(files, ApiArgs("openai-completions"));
            var result = await child.Finish(); Equal(2, result.ExitCode); Equal(0, child.Records.Length);
            Equal("InvalidScript", JsonData.Parse(result.Error).Value.GetProperty("code").GetString());
        }
        await Script(files.Script, Text("wrong model profile"));
        await using (var child = new Child(files))
        {
            var result = await child.Finish();
            CheckProviderMismatch(result, child.Records.Length, "completions-session/responses-runtime");
        }
        var after = await File.ReadAllBytesAsync(files.Session); Check(before.SequenceEqual(after), "Read-only/malformed/preflight RPC work changed Completions durable bytes.");
        await using var lease = await SessionLogStore.OpenAsync(files.Session); Equal((long)before.Length, lease.Snapshot.CommittedByteLength);
    }

    private static async Task Admission()
    {
        using var files = new Files(); await Create(files); var before = await File.ReadAllBytesAsync(files.Session);
        await Script(files.Script, Text("unused"));
        await using (var child = new Child(files))
        {
            await child.Raw("{broken}\n");
            var rejected = await child.Wait(record => Type(record) == "response" && record.Value.GetProperty("command").GetString() == "parse");
            Check(!rejected.Value.GetProperty("success").GetBoolean() && !rejected.Value.TryGetProperty("id", out _), "Malformed frame gained correlation or success.");
            await child.Send(new { id = "state", type = "get_state" }); Success(await child.Response("state"), "get_state");
            await child.Send(new { id = "unknown", type = "unfinished_command" });
            Check(! (await child.Response("unknown")).Value.GetProperty("success").GetBoolean(), "Unimplemented command silently succeeded.");
            Clean(await child.Finish());
        }
        var after = await File.ReadAllBytesAsync(files.Session); Check(before.SequenceEqual(after), "Read-only RPC commands appended session state.");
        await using (var child = new Child(files))
        {
            await child.Raw("{\"type\":\"get_state\",\"id\":\"\\ud800\"}\n");
            var result = await child.Finish(); Equal(1, result.ExitCode);
            Equal("RpcHostFailed", JsonData.Parse(result.Error).Value.GetProperty("code").GetString());
            Check(child.Records.Length == 0, "Unpaired Unicode gained a fabricated successful RPC frame.");
        }
        await File.WriteAllTextAsync(files.Script, new string(' ', 1_048_577));
        await using (var oversized = new Child(files))
        {
            var result = await oversized.Finish();
            Equal(2, result.ExitCode); Equal(0, oversized.Records.Length);
            Equal("ResourceLimit", JsonData.Parse(result.Error).Value.GetProperty("code").GetString());
        }
        await Script(files.Script, new { rpcGate = new { releaseOnGetStateId = new string('x', 129) }, events = new[] { Completed() } });
        await using (var gate = new Child(files))
        {
            var result = await gate.Finish();
            Equal(2, result.ExitCode); Equal(0, gate.Records.Length);
            Equal("InvalidScript", JsonData.Parse(result.Error).Value.GetProperty("code").GetString());
        }
        await Script(files.Script, SessionCommandTests.AnthropicText("wrong runtime"));
        await using (var wrongFamily = new Child(files, ["--offline-api", "anthropic-messages"]))
        {
            var result = await wrongFamily.Finish();
            CheckProviderMismatch(result, wrongFamily.Records.Length, "responses-session/anthropic-runtime");
        }
        await Script(files.Script, Text("wrong wire family"));
        await using (var wrongWire = new Child(files, ["--offline-api", "anthropic-messages"]))
        {
            var result = await wrongWire.Finish(); Equal(2, result.ExitCode); Equal(0, wrongWire.Records.Length);
            Equal("InvalidScript", JsonData.Parse(result.Error).Value.GetProperty("code").GetString());
        }
        foreach (var extra in new[] { new[] { "--offline-api", "unimplemented-provider" }, new[] { "--api-key", "private rejected auth value" } })
            await using (var rejected = new Child(files, extra))
            {
                var result = await rejected.Finish(); Equal(2, result.ExitCode); Equal(0, rejected.Records.Length);
                Equal("InvalidArguments", JsonData.Parse(result.Error).Value.GetProperty("code").GetString());
                Check(!result.Error.Contains("private rejected auth value", StringComparison.Ordinal), "Rejected credential text entered a diagnostic.");
            }
        var final = await File.ReadAllBytesAsync(files.Session); Check(before.SequenceEqual(final), "Rejected host acquisition changed durable source.");
    }

    private static Task CallerCancellation() => CallerCancellationForApi(null);
    private static async Task CallerCancellationForApi(string? api)
    {
        using var files = new Files(); await Create(files, api);
        var initial = await File.ReadAllBytesAsync(files.Session);
        await Script(files.Script, WireText(api, "must not complete held response", gate: true));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        using var input = new ControlledInput(); using var output = new ObservedOutput(); using var error = new StringWriter();
        var host = RpcSessionCommand.RunAsync(new[] { "session", "rpc", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", files.Script }.Concat(ApiArgs(api)).ToArray(), input, output, error, caller.Token);
        try
        {
            await input.Send(new { id = "p", type = "prompt", message = "caller cancellation" }, deadline.Token);
            await output.AssistantStarted.Task.WaitAsync(deadline.Token);
            await input.Send(new { id = "held", type = "get_state" }, deadline.Token);
            var held = await output.HeldState.Task.WaitAsync(deadline.Token);
            Check(Success(held, "get_state").GetProperty("data").GetProperty("isStreaming").GetBoolean(), "Caller control did not reach an admitted held generation.");
            Check(!host.IsCompleted, "Host settled before the actual caller cancellation.");
            caller.Cancel();
            Equal(1, await host.WaitAsync(deadline.Token));
            var diagnostic = JsonData.Parse(error.ToString()).Value;
            Equal("Canceled", diagnostic.GetProperty("code").GetString());
            Check(diagnostic.GetProperty("effectsMayHaveCompleted").GetBoolean(), "Caller cancellation hid possible committed effects.");
            Check(!input.WasDisposed && !output.WasDisposed, "Host disposed borrowed caller streams.");
            var ended = output.Records.Single(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant");
            Equal("aborted", ended.Value.GetProperty("message").GetProperty("stopReason").GetString());
            Check(!ended.ToString().Contains("must not complete held response", StringComparison.Ordinal), "Caller cancellation acquired a held successful response.");
            var read = await new SessionLogReader().ReadFileAsync(files.Session, cancellationToken: deadline.Token);
            Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.OriginalBytes.AsSpan().StartsWith(initial), "Caller cancellation rewrote or damaged acknowledged history.");
            Check(read.ValidatedPrefix.Any(record => record.Entry.Type == "message" &&
                record.Entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "assistant" &&
                record.Entry.WireBody.Value.GetProperty("message").GetProperty("stopReason").GetString() == "aborted"), "Host returned before durable abort acknowledgment.");
            await using var reopened = await SessionLogStore.OpenAsync(files.Session, cancellationToken: deadline.Token);
            Equal((long)read.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
        }
        finally
        {
            caller.Cancel(); input.Complete();
            await host;
        }
    }

    private static Task OutputFailures() => OutputFailuresForApi(null);
    private static async Task SharedOutputFailuresForApi()
    {
        foreach (var api in new string?[] { null, "anthropic-messages", "openai-completions" })
            await OutputFailuresForApi(api);
    }
    private static async Task OutputFailurePhases()
    {
        await OutputFailuresForApi(null, capturePhases: true);
        await OutputFailuresForApi(null, capturePhases: true, newCleanupCause: new IOException("authored new terminal cleanup"));
    }
    private static async Task OutputFailuresForApi(string? api, bool capturePhases = false, Exception? newCleanupCause = null)
    {
        foreach (var failFlush in new[] { false, true })
        {
            using var files = new Files(); await Create(files, api);
            var initial = await File.ReadAllBytesAsync(files.Session); var target = files.In("must-not-write.txt");
            var turn = JsonSerializer.SerializeToElement(WireTool(api, "write", "output-fault", new { path = target, content = "unacquired effect" }));
            var gatedTurn = turn.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
            gatedTurn["rpcGate"] = new { releaseOnGetStateId = "release" };
            await Script(files.Script, gatedTurn, WireText(api, "must not continue"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var input = new ControlledInput();
            using var output = new FaultOutput(failFlush);
            using var error = new StringWriter();
            var args = new[] { "session", "rpc", "--session", files.Session, "--workspace", files.Root,
                "--offline-script", files.Script, "--allow-write", target }.Concat(ApiArgs(api)).ToArray();
            RpcSessionShutdownSettlement? captured = null;
            var host = capturePhases
                ? RpcSessionCommand.RunWithPresentationAsync(args, input, output, error, new PhaseObserver(), deadline.Token,
                    stopTerminalAndJoin: settlement =>
                    {
                        captured = settlement;
                        return ValueTask.FromResult(settlement.AcknowledgeTerminalStopped(newCleanupCause is null ? [] : [newCleanupCause]));
                    })
                : RpcSessionCommand.RunAsync(args, input, output, error, deadline.Token);
            try
            {
                await input.Send(new { id = "p", type = "prompt", message = "held effect" }, deadline.Token);
                // The actual provider starts after this awaited input/message barrier; its first response remains gated.
                await output.AssistantStarted.Task.WaitAsync(deadline.Token);
                await input.Send(new { id = "release", type = "get_state" }, deadline.Token);
                var failedRecord = await output.FaultEntered.Task.WaitAsync(deadline.Token);
                Check(Success(failedRecord, "get_state").GetProperty("data").GetProperty("isStreaming").GetBoolean(),
                    "Fault control did not observe a held running generation.");
                Check(!host.IsCompleted && !File.Exists(target), "Output fault gate was bypassed before release.");
                // This wakes a failed write OR failed flush, never a successful get_state delivery.
                output.ReleaseFailure();
                Equal(1, await host.WaitAsync(deadline.Token));
                Equal(newCleanupCause is null ? "RpcHostFailed" : "CleanupFailed", JsonData.Parse(error.ToString()).Value.GetProperty("code").GetString());
                Equal(newCleanupCause is null ? 0 : 1, JsonData.Parse(error.ToString()).Value.GetProperty("cleanupFailureCount").GetInt32());
                if (capturePhases)
                {
                    var phase = captured ?? throw new InvalidOperationException("Output failure omitted original shutdown settlement.");
                    Check(phase.Session is { IsDisposed: false, Fault.Failure: PersistentAgentSessionFailure.RunFailed },
                        "Control did not retain the actual operation fault before runtime cleanup.");
                    Check(phase.Failures.Length == 1 && phase.Failures[0] is RpcDispatchException { Failure: RpcDispatchFailure.OutputFailed },
                        "Phase one duplicated the output operation as cleanup.");
                    Check(ReferenceEquals(output.Failure, phase.Failures[0].InnerException), "Original output cause identity changed.");
                    Check((await phase.RuntimeCleanup).IsEmpty, "Successful physical resource cleanup fabricated failures.");
                    var final = await phase.Completion;
                    Equal(newCleanupCause is null ? 1 : 2, final.Length);
                    Check(ReferenceEquals(final[0], phase.Failures[0]), "Final validation replaced the original failure with session poison metadata.");
                    if (newCleanupCause is not null)
                        Check(ReferenceEquals(final[1], newCleanupCause), "New cleanup cause identity or operation-first ordering changed.");
                }
                Check(!error.ToString().Contains("private sink failure", StringComparison.Ordinal), "Host leaked a sink exception.");
                Check(!input.WasDisposed && !output.WasDisposed, "Host disposed borrowed stdio.");
                Check(!File.Exists(target), "Failed output release acquired a scripted executable response.");
                var read = await new SessionLogReader().ReadFileAsync(files.Session, cancellationToken: deadline.Token);
                Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.OriginalBytes.AsSpan().StartsWith(initial),
                    "Output fault cleanup damaged or rewrote the acknowledged session.");
                Check(read.ValidatedPrefix.Any(record => record.Entry.Type == "message" &&
                    record.Entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user"),
                    "Control did not reach the real durable input barrier.");
                Check(!read.ValidatedPrefix.Any(record => record.Entry.Type == "message" &&
                    record.Entry.WireBody.Value.GetProperty("message").GetRawText().Contains("output-fault", StringComparison.Ordinal)),
                    "Failed delivery granted the held scripted tool call terminal authority.");
                Check(!output.Records.Any(record => Type(record) == "tool_execution_start"), "Tool execution began after the poisoned gate.");
                // A real independent exclusive writer proves host return joined durable cleanup.
                await using var reopened = await SessionLogStore.OpenAsync(files.Session, cancellationToken: deadline.Token);
                Equal((long)read.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
            }
            finally
            {
                output.ReleaseFailure(); input.Complete(); deadline.Cancel();
                await host;
            }
        }
    }

    private static void AssertStopped(Child child, string text)
    {
        var final = child.Records.Last(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant").Value.GetProperty("message");
        Equal("stop", final.GetProperty("stopReason").GetString());
        Check(final.GetProperty("content").EnumerateArray().Any(content => content.GetProperty("type").GetString() == "text" && content.GetProperty("text").GetString() == text),
            "Resumed actual request failed its authored history requirements.");
    }
    private static async Task Script(string path, params object[] turns) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), new UTF8Encoding(false));
    private static string[] ApiArgs(string? api, params string[] extra) => api is null ? extra : new[] { "--offline-api", api }.Concat(extra).ToArray();
    private static object WireText(string? api, string text, bool gate = false, string[]? required = null) => api switch
    {
        "openai-completions" => SessionCommandTests.CompletionsText(text, gate, required),
        "anthropic-messages" => SessionCommandTests.AnthropicText(text, gate, required),
        _ => Text(text, gate, required)
    };
    private static object WireTool(string? api, string name, string call, object args, params string[] required) => api switch
    {
        "openai-completions" => SessionCommandTests.CompletionsTool(name, call, args, required),
        "anthropic-messages" => SessionCommandTests.AnthropicTool(name, call, args, required),
        _ => Tool(name, call, args, required)
    };
    private static object Text(string text, bool gate = false, string[]? required = null)
    {
        var id = "msg-" + text.Replace(' ', '-');
        var result = new Dictionary<string, object?> { ["requiredInputTexts"] = required ?? [], ["events"] = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id, content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = id, delta = text },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id, content = new[] { new { type = "output_text", text } } } }, Completed()
        } };
        if (gate) result["rpcGate"] = new { releaseOnGetStateId = "release" };
        return result;
    }
    private static object Tool(string name, string call, object args, params string[] required)
    {
        var id = "fc-" + call; var arguments = JsonSerializer.Serialize(args);
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id, call_id = call, name, arguments = "" } },
            new { type = "response.function_call_arguments.delta", output_index = 0, item_id = id, delta = arguments[..(arguments.Length / 2)] },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id, call_id = call, name, arguments } }, Completed()
        } };
    }
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(),
        usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private static async Task Create(Files files, string? api = null)
    {
        var start = Start(files);
        foreach (var arg in new[] { "session", "create", "--session", files.Session, "--workspace", files.Root }.Concat(ApiArgs(api))) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Compiled create did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = ReadError(process.StandardOutput, deadline.Token); var error = ReadError(process.StandardError, deadline.Token);
        Exception? primaryFailure = null;
        try { await process.WaitForExitAsync(deadline.Token); Equal(0, process.ExitCode); Equal("", await error); JsonData.Parse(await output); }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            try
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                finally { await process.WaitForExitAsync(); }
            }
            catch (Exception) when (primaryFailure is not null) { /* Preserve the original assertion/deadline failure. */ }
            finally
            {
                // WhenAll settles both originals even if either reader faults. Their
                // diagnostic deadline never replaces the process or reader joins.
                try { await Task.WhenAll(output, error); }
                catch (Exception) when (primaryFailure is not null) { /* Both readers joined; retain the primary failure. */ }
            }
        }
    }
    private static ProcessStartInfo Start(Files files)
    {
        var start = new ProcessStartInfo(_host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true), StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true) };
        start.Environment["ANTHROPIC_API_KEY"] = "sk-ant-oat-authored-unused-environment-noncredential";
        start.Environment["OPENAI_API_KEY"] = "authored-unused-environment-noncredential";
        start.ArgumentList.Add(_cli); return start;
    }
    private sealed record Result(int ExitCode, string Error);
    private abstract class TestStream : Stream
    {
        public bool WasDisposed { get; private set; }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) WasDisposed = true; base.Dispose(disposing); }
    }
    private sealed class ControlledInput : TestStream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        private byte[]? _current; private int _offset;
        public override bool CanRead => !WasDisposed;
        public override bool CanWrite => false;
        public ValueTask Send(object command, CancellationToken token) =>
            _chunks.Writer.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n"), token);
        public void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            while (_current is null)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (_chunks.Reader.TryRead(out var next)) { _current = next; _offset = 0; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            if (_offset == _current.Length) _current = null;
            return count;
        }
    }
    private sealed class ObservedOutput : TestStream
    {
        private readonly List<JsonData> _records = [];
        private JsonData? _pending;
        public TaskCompletionSource AssistantStarted { get; } = NewGate();
        public TaskCompletionSource<JsonData> HeldState { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonData[] Records { get { lock (_records) return _records.ToArray(); } }
        public override bool CanRead => false;
        public override bool CanWrite => !WasDisposed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(buffer.Length <= 1_048_577, "Direct caller host emitted an unbounded frame.");
            _pending = JsonData.Parse(Encoding.UTF8.GetString(buffer.Span));
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending is not { } record) return Task.CompletedTask;
            _pending = null;
            lock (_records) { Check(_records.Count < 2048, "Direct caller record bound exceeded."); _records.Add(record); }
            if (Type(record) == "message_start" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant")
                AssistantStarted.TrySetResult();
            if (Type(record) == "response" && Id(record) == "held") HeldState.TrySetResult(record);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultOutput(bool failFlush) : TestStream
    {
        public IOException Failure { get; } = new("private sink failure");
        private readonly TaskCompletionSource _failure = NewGate();
        private readonly List<JsonData> _records = [];
        private JsonData? _pending;
        private bool _faulted;
        public TaskCompletionSource AssistantStarted { get; } = NewGate();
        public TaskCompletionSource<JsonData> FaultEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonData[] Records { get { lock (_records) return _records.ToArray(); } }
        public override bool CanRead => false;
        public override bool CanWrite => !WasDisposed;
        public void ReleaseFailure() => _failure.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_faulted) throw Failure;
            Check(buffer.Length <= 1_048_577, "Direct host emitted an unbounded frame.");
            _pending = JsonData.Parse(Encoding.UTF8.GetString(buffer.Span));
            if (!failFlush && IsRelease(_pending))
            {
                _faulted = true; FaultEntered.TrySetResult(_pending);
                await _failure.Task.WaitAsync(cancellationToken);
                throw Failure;
            }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_faulted) throw Failure;
            if (_pending is null) return;
            if (failFlush && IsRelease(_pending))
            {
                _faulted = true; FaultEntered.TrySetResult(_pending);
                await _failure.Task.WaitAsync(cancellationToken);
                throw Failure;
            }
            var record = _pending; _pending = null;
            lock (_records) { Check(_records.Count < 2048, "Direct host record bound exceeded."); _records.Add(record); }
            if (Type(record) == "message_start" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant")
                AssistantStarted.TrySetResult();
        }
        private static bool IsRelease(JsonData record) => Type(record) == "response" && Id(record) == "release" &&
            record.Value.GetProperty("command").GetString() == "get_state";
    }
    private sealed class PhaseObserver : IRpcExtensionUiPresentationObserver
    {
        public ValueTask PublishedAsync(RpcExtensionUiPresentation value, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask RetiredAsync(RpcExtensionUiRetirement value, CancellationToken token) => ValueTask.CompletedTask;
    }
    private sealed class Child : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
        private readonly Task _read;
        private readonly Task<string> _error;
        private readonly object _gate = new();
        private readonly List<JsonData> _records = [];
        private TaskCompletionSource _changed = NewGate();
        private bool _finished;
        public JsonData[] Records { get { lock (_gate) return _records.ToArray(); } }
        public Child(Files files, string[]? extra = null)
        {
            var start = Start(files);
            foreach (var arg in new[] { "session", "rpc", "--session", files.Session, "--workspace", files.Root, "--offline-script", files.Script }.Concat(extra ?? []))
                start.ArgumentList.Add(arg);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Compiled RPC did not start.");
            _read = ReadOutput(); _error = ReadError(_process.StandardError, _deadline.Token);
        }
        public Task Send(object command) => Raw(JsonSerializer.Serialize(command) + "\n");
        public async Task Raw(string text)
        {
            Check(Encoding.UTF8.GetByteCount(text) <= 1_048_577, "Authored command exceeds child bound.");
            await _process.StandardInput.WriteAsync(text.AsMemory(), _deadline.Token);
            await _process.StandardInput.FlushAsync(_deadline.Token);
        }
        public Task<JsonData> Response(string id) => Wait(record => Type(record) == "response" && Id(record) == id);
        public Task<JsonData> Settled() => Wait(record => Type(record) == "agent_settled");
        public async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    var match = _records.FirstOrDefault(predicate); if (match is not null) return match;
                    if (_read.IsCompleted) throw new InvalidOperationException("Child ended before expected protocol record.");
                    changed = _changed.Task;
                }
                await changed.WaitAsync(_deadline.Token);
            }
        }
        private async Task ReadOutput()
        {
            try
            {
                await using var reader = new JsonlReader(_process.StandardOutput.BaseStream);
                long bytes = 0;
                await foreach (var admission in reader.ReadAdmissionsAsync(_deadline.Token))
                {
                    Check(admission.IsAccepted && !admission.IsFinalFrame, "Compiled stdout emitted rejected or unterminated JSONL.");
                    var record = admission.Record!;
                    bytes += Encoding.UTF8.GetByteCount(record.ToString()) + 1;
                    Check(bytes <= 2_097_152, "Child stdout exceeds authored total bound.");
                    TaskCompletionSource changed;
                    lock (_gate)
                    {
                        Check(_records.Count < 2048, "Child record count exceeds authored bound.");
                        _records.Add(record); changed = _changed; _changed = NewGate();
                    }
                    changed.TrySetResult();
                }
            }
            finally { lock (_gate) _changed.TrySetResult(); }
        }
        public async Task<Result> Finish()
        {
            if (!_finished) { _finished = true; _process.StandardInput.Close(); }
            await _process.WaitForExitAsync(_deadline.Token); await _read;
            return new(_process.ExitCode, await _error);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                finally { await _process.WaitForExitAsync(); }
            }
            finally
            {
                try { await _read; } catch (Exception) { }
                try { await _error; } catch (Exception) { }
                _process.Dispose(); _deadline.Dispose();
            }
        }
    }
    private static async Task<string> ReadError(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[1024];
        while (true)
        { var count = await reader.ReadAsync(buffer, token); if (count == 0) return text.ToString();
            Check(count <= 1_048_576 - text.Length, "Child diagnostic output exceeds bound."); text.Append(buffer, 0, count); }
    }
    private sealed class Files : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string Session => In("session.jsonl");
        public string Script => In("script.json");
        public string In(string name) => Path.Combine(Root, name);
        public Files() { Root = Path.Combine(_parent, "pisharp-rpc-child-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            var target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != _parent || !Path.GetFileName(target).StartsWith("pisharp-rpc-child-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned child cleanup.");
            Directory.Delete(target, recursive: true);
        }
    }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Type(JsonData record) => record.Value.GetProperty("type").GetString()!;
    private static string? Id(JsonData record) => record.Value.TryGetProperty("id", out var id) ? id.GetString() : null;
    private static JsonElement Success(JsonData record, string command)
    { Equal("response", Type(record)); Equal(command, record.Value.GetProperty("command").GetString()); Check(record.Value.GetProperty("success").GetBoolean(), "Command failed."); return record.Value; }
    private static void Strings(string[] expected, JsonElement actual) =>
        Check(expected.SequenceEqual(actual.EnumerateArray().Select(item => item.GetString()!)), "Full queue text/order changed.");
    private static void Clean(Result result) { Equal(0, result.ExitCode); Equal("", result.Error); }
    private static void CheckProviderMismatch(Result result, int frameCount, string variant)
    {
        var code = "missing"; int? cleanupCount = null;
        if (result.Error.Length > 4096) code = "oversized";
        else try
        {
            using var document = JsonDocument.Parse(result.Error);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                    code = value.GetString() switch
                    {
                        "OfflineProviderMismatch" => "OfflineProviderMismatch", "CleanupFailed" => "CleanupFailed",
                        "RpcHostFailed" => "RpcHostFailed", "InvalidScript" => "InvalidScript",
                        "CommandFailed" => "CommandFailed", "Canceled" => "Canceled", _ => "other"
                    };
                if (document.RootElement.TryGetProperty("cleanupFailureCount", out var count) && count.ValueKind == JsonValueKind.Number &&
                    count.TryGetInt32(out var parsed)) cleanupCount = parsed;
            }
        }
        catch (JsonException) { code = "invalid-json"; }
        Console.Error.WriteLine("DIAGNOSTIC " + JsonSerializer.Serialize(new
        { source = "rpc-provider-mismatch", variant, exitCode = result.ExitCode, frameCount, publicCode = code, cleanupCount }));
        var diagnostic = $"variant={variant}; exit={result.ExitCode}; frames={frameCount}; publicCode={code}; cleanupCount={cleanupCount}";
        Check(result.ExitCode == 2, "Provider mismatch exit; " + diagnostic);
        Check(frameCount == 0, "Provider mismatch frames; " + diagnostic);
        Check(code == "OfflineProviderMismatch", "Provider mismatch code; " + diagnostic);
    }
    private static void Same(JsonElement expected, JsonElement actual)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var left = expected.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            var right = actual.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            Equal(left.Length, right.Length);
            for (var index = 0; index < left.Length; index++) { Equal(left[index].Name, right[index].Name); Same(left[index].Value, right[index].Value); }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var index = 0; index < expected.GetArrayLength(); index++) Same(expected[index], actual[index]); }
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString());
        else Equal(expected.GetRawText(), actual.GetRawText());
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
}
