using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Rpc;
using PiSharp.Rpc.Ui;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

/// <summary>Explicit opt-in original Commands/Input package workflow. Never register in the Node-free gate.</summary>
internal static partial class NodeCommandInputPackageTests
{
    private const string ExpectedSha = "d74d53610906cb41ec6c2ad8a10f57d242c2a2846631a0ec4901ef5a3225930d";
    private const string Clock = "2026-10-01T12:00:00.000Z";
    private const string Image = "[{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"}]";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Context(string Host, string Cli, string Published, string Node, string Repository,
        string Oracle, string Jiti, string Reference, string CommandInputReference, string Runs);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string host, string cli, string published,
        string node, string repository, string oracle, string jiti, string reference, string commandInputReference, string runParent)
    {
        var c = new Context(Absolute(host), Absolute(cli), Absolute(published), Absolute(node), Absolute(repository),
            Absolute(oracle), Absolute(jiti), Absolute(reference), Absolute(commandInputReference), Absolute(runParent));
        foreach (var path in new[] { c.Host, c.Cli, c.Node }) Check(File.Exists(path), "Explicit Commands/Input executable/CLI missing.");
        foreach (var path in new[] { c.Published, c.Repository, c.Oracle, c.Jiti, c.Reference, c.CommandInputReference, c.Runs }) Check(Directory.Exists(path), "Explicit Commands/Input input/run parent missing.");
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
            yield return ("node-command-input." + api + ".original-callbacks-dialogs-catalog-provider-durable-cancellation-branch-policy", () => Durable(c, api));
        yield return ("node-command-input.anthropic.original-image-preserved-through-transform-http-durable-reopen", () => Images(c));
        yield return ("node-command-input.actual-host-trusted-extension-source-and-no-ui-diagnostic", () => Hosted(c));
    }

    private static async Task Durable(Context c, string api)
    {
        var files = await Files.Create(c, api); await Create(files); var initial = await Complete(files); Loadout(initial);
        const string original = "?quick explain UTF8", transformed = "Respond briefly in 1-2 sentences: explain UTF8";
        var first = JsonSerializer.SerializeToNode(Text(api, "commands-input-cli-final", transformed))!; first["expectedRequest"] = InitialRequest(api, transformed);
        await Script(files, first); var scriptBefore = await File.ReadAllBytesAsync(files.Script);
        var cli = await OneShot(files, "prompt", ["--message", original]); Clean(cli);
        var report = JsonData.Parse(cli.Output).Value; Equal("completed", report.GetProperty("status").GetString());
        Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "CLI input receipt preceded durable flush.");
        Requests(report, api, 1); Equal(1, report.GetProperty("usedScriptTurns").GetInt32()); Equal(0, report.GetProperty("actions").GetArrayLength());
        var scriptAfter = await File.ReadAllBytesAsync(files.Script); Check(scriptBefore.AsSpan().SequenceEqual(scriptAfter), "Original input workflow rewrote authored provider bytes.");
        var cliLog = await Complete(files); Prefix(initial, cliLog); Loadout(cliLog); User(Project(cliLog), transformed); var ancestor = Project(cliLog).LeafId!;
        var cliSource = await files.Receipt(1); var cliInputs = Operations(cliSource, "input"); Equal(2, cliInputs.Length);
        Input(cliInputs[0], "input-transform-1", original, "interactive", "{\"action\":\"transform\",\"text\":\"Respond briefly in 1-2 sentences: explain UTF8\"}");
        Input(cliInputs[1], "input-transform-2", transformed, "interactive", "{\"action\":\"continue\"}");
        Equal(GoldenInputReturn(files, "input-chain", "input-transform-1"), cliInputs[0].GetProperty("resultJson").GetString());

        const string rpcOriginal = "?quick rpc followup", rpcTransformed = "Respond briefly in 1-2 sentences: rpc followup";
        await Script(files, Text(api, "commands-input-rpc-final", "commands-input-cli-final", rpcTransformed)); JsonElement acknowledged;
        await using (var rpc = await RpcChild.Start(files, []))
        {
            var catalog = await Catalog(rpc, "catalog"); CatalogShape(files, catalog);
            foreach (var prefix in new[] { "", "p", "extension", "unknown" })
            {
                var result = await Completion(rpc, "complete-" + prefix, prefix);
                var frozen = files.Golden("commands-completions").GetProperty("dispatches")
                    .EnumerateArray().Single(row => row.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString() == JsonSerializer.Serialize(new { prefix }));
                Equal(frozen.GetProperty("outcome").GetProperty("returned").GetProperty("serializedJson").GetString(), result.GetRawText());
            }
            var count = rpc.Records.Length;
            await rpc.Send(new { id = "select-command", type = "prompt", message = "/commands extension", source = "extension" });
            var select = await rpc.Ui(count, "select"); Equal("Available Commands", select.GetProperty("title").GetString());
            SameJson(JsonData.Parse("[\"--- Extensions ---\",\"/commands - List available slash commands\"]").Value, select.GetProperty("options"));
            Check(!rpc.Records.Any(row => ResponseId(row, "select-command")), "Command was acknowledged before actual held select settled.");
            await rpc.Send(new { id = "during-select", type = "get_state" }); Check(!Good(await rpc.Response("during-select")).GetProperty("data").GetProperty("isStreaming").GetBoolean(), "Command dialog invented a provider run.");
            SameJson(catalog, await Catalog(rpc, "catalog-held")); SameJson(JsonData.Parse("[{\"value\":\"extension\",\"label\":\"extension\"}]").Value, await Completion(rpc, "complete-held", "extension"));
            await rpc.Send(new { type = "extension_ui_response", id = select.GetProperty("id").GetString(), value = "/commands - List available slash commands" });
            var confirm = await rpc.Ui(count, "confirm"); Equal("commands", confirm.GetProperty("title").GetString());
            Equal("View source path?\n" + files.SourcePath, confirm.GetProperty("message").GetString());
            await rpc.Send(new { type = "extension_ui_response", id = confirm.GetProperty("id").GetString(), confirmed = true });
            var notify = await rpc.Ui(count, "notify"); Equal(files.SourcePath, notify.GetProperty("message").GetString()); Equal("info", notify.GetProperty("notifyType").GetString());
            Handled(await rpc.Response("select-command")); NoAgent(rpc.Records[count..]);
            await EntriesUnchanged(files, rpc, "after-command", cliLog);
            await HandledNotify(rpc, "/commands unknown", "No unknown commands found", "info", "command-empty");
            await HandledNotify(rpc, "/commands skill", "No skill commands found", "info", "command-missing-real-skill-owner");
            await CancelOrHeader(rpc, "command-header", "--- Extensions ---", false);
            await CancelOrHeader(rpc, "command-cancel", null, false);
            await CancelOrHeader(rpc, "command-confirm-false", "/commands - List available slash commands", true);
            await HandledNotify(rpc, "?quick ", "Usage: ?quick <question>", "warning", "input-empty");
            await HandledNotify(rpc, "PiNg", "pong", "info", "input-ping");
            var frozenTime = files.Golden("input-clock").GetProperty("controls").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "clock-implementation").GetProperty("actualLocaleText").GetString()!;
            await HandledNotify(rpc, "time", frozenTime, "info", "input-clock"); await EntriesUnchanged(files, rpc, "after-handled", cliLog);

            // Actual RPC abort joins the admitted original callback and native dialog before accepting a new prompt.
            count = rpc.Records.Length; await rpc.Send(new { id = "cancel-command", type = "prompt", message = "/commands" });
            var held = await rpc.Ui(count, "select"); await rpc.Send(new { id = "abort-command", type = "abort" }); Good(await rpc.Response("abort-command"));
            var cancelled = (await rpc.Response("cancel-command")).Value;
            Check(!cancelled.GetProperty("success").GetBoolean(), "Aborted command became acknowledged success.");
            Equal("Command canceled before acceptance.", cancelled.GetProperty("error").GetString());
            await rpc.Send(new { type = "extension_ui_response", id = held.GetProperty("id").GetString(), value = "/commands - List available slash commands" });
            await rpc.Send(new { id = "after-stale", type = "get_state" }); Good(await rpc.Response("after-stale"));
            NoAgent(rpc.Records[count..]); Check(!rpc.Records[count..].Any(row => IsUi(row, "confirm") || IsUi(row, "notify")), "Stale cancelled dialog reply reentered source effects.");
            await EntriesUnchanged(files, rpc, "after-cancel", cliLog);
            count = rpc.Records.Length;
            await rpc.Send(new { id = "resume", type = "prompt", message = rpcOriginal, source = "extension" }); Good(await rpc.Response("resume"));
            await rpc.Wait(row => Type(row) == "agent_settled"); Equal(1, rpc.Records[count..].Count(row => Type(row) == "agent_settled"));
            await rpc.Send(new { id = "entries", type = "get_entries" }); acknowledged = Good(await rpc.Response("entries")).GetProperty("data").GetProperty("entries"); await Acknowledged(files, acknowledged); Clean(await rpc.Finish());
        }
        var resumed = await Complete(files); Prefix(cliLog, resumed); Loadout(resumed); Equal(acknowledged.GetArrayLength(), resumed.ValidatedPrefix.Length - 1);
        User(Project(resumed), rpcTransformed); Check(!Project(resumed).LlmMessages.Any(message => message.Role == "user" && message.WireBody.ToString().Contains(rpcOriginal, StringComparison.Ordinal)), "RPC untrusted source field spoofed Extension injection.");
        var rpcSource = await files.Receipt(2); var commandOps = Operations(rpcSource, "command"); Equal(7, commandOps.Length);
        foreach (var operation in commandOps)
        {
            Equal(operation.GetProperty("context").GetProperty("actualNativeCatalog").GetProperty("serializedJson").GetString(), CatalogFromTrace(operation.GetProperty("trace")[0].GetProperty("value").GetProperty("serializedJson").GetString()!));
            Equal("genuine ExtensionRunner.createCommandContext", operation.GetProperty("context").GetProperty("owner").GetString());
        }
        var rejected = commandOps.Single(operation => operation.GetProperty("status").GetString() == "rejected"); Check(rejected.GetProperty("signalAfter").GetProperty("aborted").GetBoolean(), "Actual source cancellation receipt lacks aborted signal.");
        var cancellation = rpcSource.GetProperty("sourceOperations").EnumerateArray().Single(operation => operation.GetProperty("kind").GetString() == "command" && operation.GetProperty("status").GetString() == "rejected").GetProperty("nativeAdmission");
        Check(cancellation.GetProperty("callerCancellationRequested").GetBoolean(), "Cancellation disposition lacks the actual native caller token."); Equal("Written", cancellation.GetProperty("cancellationWrite").GetString());
        Equal(1, cancellation.GetProperty("uiCancellationWrites").GetArrayLength()); var uiCancel = cancellation.GetProperty("uiCancellationWrites")[0];
        Equal("ui.select", uiCancel.GetProperty("method").GetString()); Equal("Written", uiCancel.GetProperty("disposition").GetString());
        Check(uiCancel.GetProperty("operationCancellationRequested").GetBoolean() && !uiCancel.GetProperty("sessionCancellationRequested").GetBoolean() && !uiCancel.GetProperty("extensionCancellationRequested").GetBoolean(), "Command cancellation crossed its actual native ownership boundary.");
        Equal(JsonValueKind.Null, cancellation.GetProperty("cancellationWriteFailure").ValueKind);
        Check(cancellation.GetProperty("primary").GetString()!.Contains("Extension worker protocol: Cancelled.", StringComparison.Ordinal), "Actual underlying canceled protocol failure was discarded.");
        var inputs = Operations(rpcSource, "input"); Equal(5, inputs.Length);
        Input(inputs[^2], "input-transform-1", rpcOriginal, "rpc", "{\"action\":\"transform\",\"text\":\"Respond briefly in 1-2 sentences: rpc followup\"}"); Input(inputs[^1], "input-transform-2", rpcTransformed, "rpc", "{\"action\":\"continue\"}");

        // The unchanged Input hook cannot authorize a native write: branch executes the real prepared-action policy.
        var target = files.In("ungranted-original-input-write.txt"); await Script(files, Tool(api, "write", "command-input-denied", new { path = target, content = "must not write" }, "commands-input-cli-final", "branch ordinary"), Text(api, "commands-input-branch-final"));
        string leaf;
        await using (var branch = await RpcChild.Start(files, ["--leaf", ancestor]))
        {
            await branch.Send(new { id = "branch", type = "prompt", message = "branch ordinary" }); Good(await branch.Response("branch")); await branch.Wait(row => Type(row) == "agent_settled");
            var result = branch.Records.Single(row => Type(row) == "tool_execution_end").Value; Equal(CallId(api, "command-input-denied"), result.GetProperty("toolCallId").GetString()); Check(result.GetProperty("isError").GetBoolean() && !File.Exists(target), "Input hook bypassed final native write policy.");
            await branch.Send(new { id = "messages", type = "get_messages" }); Check(!Good(await branch.Response("messages")).GetProperty("data").GetProperty("messages").GetRawText().Contains(rpcTransformed, StringComparison.Ordinal), "Selected branch flattened resumed sibling.");
            await branch.Send(new { id = "entries", type = "get_entries" }); var entries = Good(await branch.Response("entries")).GetProperty("data"); leaf = entries.GetProperty("leafId").GetString()!; await Acknowledged(files, entries.GetProperty("entries")); Clean(await branch.Finish());
        }
        var final = await Complete(files); Prefix(resumed, final); Loadout(final); Equal(ancestor, final.ValidatedPrefix[resumed.ValidatedPrefix.Length].Entry.ParentId);
        var selected = Project(final, leaf); Check(!selected.LlmMessages.Any(message => message.WireBody.ToString().Contains(rpcTransformed, StringComparison.Ordinal)), "Durable selected branch includes sibling input.");
        var call = selected.LlmMessages.Where(message => message.Role == "assistant").SelectMany(message => message.WireBody.Value.GetProperty("content").EnumerateArray()).Single(block => block.GetProperty("type").GetString() == "toolCall"); Equal(CallId(api, "command-input-denied"), call.GetProperty("id").GetString()); SameJson(JsonData.Parse(JsonSerializer.Serialize(new { path = target, content = "must not write" })).Value, call.GetProperty("arguments"));
        Check(selected.LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value.GetProperty("isError").GetBoolean(), "Denied native action lost durable failure."); await files.VerifyReceipts(4);
    }

    private static async Task Images(Context c)
    {
        var files = await Files.Create(c, "anthropic-messages", images: true); await Create(files); var initial = await Complete(files);
        const string original = "?quick explain UTF8", transformed = "Respond briefly in 1-2 sentences: explain UTF8";
        var turn = JsonSerializer.SerializeToNode(Text(files.Api, "image-cli-final", transformed))!; turn["expectedRequest"] = InitialRequest(files.Api, transformed, true); await Script(files, turn);
        await using (var rpc = await RpcChild.Start(files, []))
        {
            await rpc.Send(new { id = "image-initial", type = "prompt", message = original, images = JsonData.Parse(Image).Value }); Good(await rpc.Response("image-initial")); await rpc.Wait(row => Type(row) == "agent_settled");
            await rpc.Send(new { id = "entries", type = "get_entries" }); await Acknowledged(files, Good(await rpc.Response("entries")).GetProperty("data").GetProperty("entries")); Clean(await rpc.Finish());
        }
        var first = await Complete(files); Prefix(initial, first); var user = User(Project(first), transformed); Equal(2, user.GetProperty("content").GetArrayLength()); SameJson(JsonData.Parse(Image).Value[0], user.GetProperty("content")[1]);
        var ops = Operations(await files.Receipt(1), "input"); Equal(2, ops.Length);
        Equal(GoldenInputReturn(files, "input-chain", "input-transform-1"), ops[0].GetProperty("resultJson").GetString());
        foreach (var op in ops) { var supplied = JsonData.Parse(op.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString()!).Value; SameJson(JsonData.Parse(Image).Value, supplied.GetProperty("images")); }
        var resumedTurn = JsonSerializer.SerializeToNode(Text(files.Api, "image-rpc-final", "image-cli-final", "ordinary"))!;
        var resumedRequest = InitialRequest(files.Api, transformed, true); var messages = resumedRequest["messages"]!.AsArray();
        messages[0]!["content"]!.AsArray()[^1]!.AsObject().Remove("cache_control");
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "image-cli-final" }) });
        messages.Add(InitialRequest(files.Api, "ordinary", true)["messages"]![0]!.DeepClone()); resumedTurn["expectedRequest"] = resumedRequest;
        await Script(files, resumedTurn);
        await using (var rpc = await RpcChild.Start(files, []))
        {
            await rpc.Send(new { id = "image-resume", type = "prompt", message = "ordinary", images = JsonData.Parse(Image).Value }); Good(await rpc.Response("image-resume")); await rpc.Wait(row => Type(row) == "agent_settled");
            await rpc.Send(new { id = "entries", type = "get_entries" }); await Acknowledged(files, Good(await rpc.Response("entries")).GetProperty("data").GetProperty("entries")); Clean(await rpc.Finish());
        }
        var final = await Complete(files); Prefix(first, final); Loadout(final); SameJson(JsonData.Parse(Image).Value[0], User(Project(final), "ordinary").GetProperty("content")[1]);
        var reopened = Operations(await files.Receipt(2), "input"); Equal(2, reopened.Length); foreach (var op in reopened) SameJson(JsonData.Parse(Image).Value, JsonData.Parse(op.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString()!).Value.GetProperty("images"));
        await files.VerifyReceipts(3);
    }

    private static async Task Hosted(Context c)
    {
        var files = await Files.Create(c, "openai-responses"); await Create(files); var initial = await Complete(files);
        await Script(files, Text(files.Api, "unused-print-no-ui-provider-turn"));
        var print = await OneShot(files, "prompt", ["--message", "ping"]); Clean(print); var printReport = JsonData.Parse(print.Output).Value;
        Equal("Handled", printReport.GetProperty("inputDisposition").GetString()); Equal(0, printReport.GetProperty("usedScriptTurns").GetInt32()); Requests(printReport, files.Api, 0);
        var printLog = await Complete(files); Equal(initial.OriginalBytes.Length, printLog.OriginalBytes.Length); Check(initial.OriginalBytes.AsSpan().SequenceEqual(printLog.OriginalBytes.AsSpan()), "Original Print/no-UI Handled appended provider input.");
        var printOps = Operations(await files.Receipt(1), "input"); Equal(1, printOps.Length); Input(printOps[0], "input-transform-1", "ping", "interactive", "{\"action\":\"handled\"}");
        var unavailable = printOps[0].GetProperty("trace").EnumerateArray().Single(row => row.GetProperty("kind").GetString() == "native-ui-reply");
        var unavailableValue = JsonData.Parse(unavailable.GetProperty("value").GetProperty("serializedJson").GetString()!).Value.GetProperty("result");
        Check(!unavailableValue.GetProperty("published").GetBoolean(), "Print/no-UI claimed actual notification publication."); Equal("unavailable", unavailableValue.GetProperty("outcome").GetString()); Equal("NoUi", unavailableValue.GetProperty("reason").GetString());
        Equal(files.Golden("input-no-ui").GetProperty("dispatches")[0].GetProperty("outcome").GetProperty("returned").GetProperty("serializedJson").GetString(), printOps[0].GetProperty("resultJson").GetString());
        const string extensionText = "?quick do not transform";
        var first = JsonSerializer.SerializeToNode(Text(files.Api, "trusted-extension-final", extensionText))!; first["expectedRequest"] = InitialRequest(files.Api, extensionText);
        var turns = ImmutableArray.Create(JsonData.Parse(first.ToJsonString()), JsonData.Parse(JsonSerializer.Serialize(Text(files.Api, "actual-no-ui-final", "trusted-extension-final", "ping"))));
        var invocation = await files.Invocation("hosted"); var beforeEnvironment = Environment.GetEnvironmentVariable("PISHARP_NATIVE_NODE_COMMAND_INPUT_OPTIONS");
        var diagnostics = new List<ExtensionEventDiagnostic>(); long next = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_NODE_COMMAND_INPUT_OPTIONS", Path.Combine(invocation, "options.json"));
            var configuration = new NativeExtensionConfiguration(files.Package, files.Manifest, files.Approval, files.Snapshots, []) { EnabledCommands = ["commands"] };
            await using var deniedUi = new RpcExtensionUiCoordinator(clientCapabilities: []);
            await using var profile = await OfflineSessionProfile.CreateAsync(files.Root, files.Session, null, turns, [], [], deadline.Token,
                offlineApi: files.Api, extension: configuration, extensionUi: deniedUi, reportInputDiagnostic: (diagnostic, token) => { token.ThrowIfCancellationRequested(); diagnostics.Add(diagnostic); return ValueTask.CompletedTask; });
            await using (var session = await PersistentAgentSession.OpenWithRegistryAsync(files.Session, profile.Registry, () => 10, () => "hosted-input-" + Interlocked.Increment(ref next), cancellationToken: deadline.Token))
            {
                var trusted = await session.SubmitInputAsync(new(extensionText, PromptInputSource.Extension), profile.InputAdmission, cancellationToken: deadline.Token); Equal(SubmittedInputDisposition.Started, trusted.Disposition); Check(trusted.Run is not null, "Actual trusted Extension input did not enter native Agent.");
                var noUi = await session.SubmitInputAsync(new("ping", PromptInputSource.Rpc), profile.InputAdmission, cancellationToken: deadline.Token); Equal(SubmittedInputDisposition.Started, noUi.Disposition); Check(noUi.Run is not null, "Actual native no-UI failure did not continue input.");
                Equal(2, diagnostics.Count); foreach (var diagnostic in diagnostics) { Equal("input", diagnostic.EventName); Equal(ExtensionEventFailure.HandlerFailed, diagnostic.Failure); }
                Equal(2, profile.UsedTurns); Equal(0, profile.Actions.Length); Check(!session.Snapshot.IsAdmittingInput, "Hosted input callback did not join before session lease release.");
                await File.WriteAllTextAsync(Path.Combine(invocation, "hosted.receipt.json"), JsonSerializer.Serialize(new { trustedDisposition = trusted.Disposition.ToString(), noUiDisposition = noUi.Disposition.ToString(), requests = profile.Requests, actions = profile.Actions,
                    diagnostics = diagnostics.Select(item => new { item.EventName, item.OwnerId, item.OwnerGeneration, item.RegistrationId, failure = item.Failure.ToString() }) }), Utf8);
            }
        }
        finally { Environment.SetEnvironmentVariable("PISHARP_NATIVE_NODE_COMMAND_INPUT_OPTIONS", beforeEnvironment); }
        var final = await Complete(files); Prefix(initial, final); Loadout(final); User(Project(final), extensionText); User(Project(final), "ping");
        var operations = Operations(await files.Receipt(2), "input"); Equal(4, operations.Length);
        foreach (var operation in operations[..2]) Input(operation, operation.GetProperty("callbackId").GetString()!, extensionText, "extension", "{\"action\":\"continue\"}");
        foreach (var operation in operations[2..])
        {
            Equal("rejected", operation.GetProperty("status").GetString()); Check(operation.GetProperty("publicationJoined").GetBoolean(), "No-UI original failure lost publication settlement.");
            var failure = JsonData.Parse(operation.GetProperty("sourceFailure").GetProperty("serializedJson").GetString()!).Value;
            Equal("Native notify capability unavailable.", failure.GetProperty("message").GetString()); Check(failure.GetProperty("stack").GetString()!.Contains("input-transform.ts", StringComparison.Ordinal), "Whole original no-UI stack was replaced.");
            Equal(0, operation.GetProperty("context").GetProperty("actualNativeUiCapabilities").GetProperty("features").GetArrayLength());
        }
        await files.VerifyReceipts(3);
    }
    private static async Task<SessionLogReadResult> Complete(Files files)
    {
        var log = await new SessionLogReader().ReadFileAsync(files.Session); Complete(log);
        await using var opened = await SessionLogStore.OpenAsync(files.Session); Equal((long)log.OriginalBytes.Length, opened.Snapshot.CommittedByteLength); return log;
    }
    private static void Complete(SessionLogReadResult log) => Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.ValidatedPrefixByteLength == log.OriginalBytes.Length, "Actual Commands/Input session log is incomplete.");
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
            _ => throw new InvalidOperationException("Unapproved Commands/Input provider family.")
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
            _ => throw new InvalidOperationException("Unapproved Commands/Input provider family.")
        }; return new { requiredInputTexts = required, events };
    }
    private static object AnthropicStart(string id) => new { type = "message_start", message = new { id, role = "assistant", model = "pisharp-offline-session", content = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 0, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 } } };
    private static object AnthropicFinish(string reason) => new { type = "message_delta", delta = new { stop_reason = reason }, usage = new { output_tokens = 4 } };
    private static object CompletionsFinish(string reason) => new { choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } } };
    private static object CompletionsUsage() => new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 8, completion_tokens = 4, total_tokens = 12 } };
    private static object ResponsesFinish() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private static JsonNode InitialRequest(string api, string user, bool image = false)
    {
        // Complete independently authored HTTP predicate. Registered input admission commits a content array.
        var declarations = JsonNode.Parse("""
            [{"name":"read","description":"Read UTF-8 text file contents, capped at 2000 lines or 50 KiB. Use offset/limit to continue. Images, binary and other encodings are unsupported in this profile.","input_schema":{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to read (relative or absolute)"},"offset":{"type":"integer","minimum":1,"description":"Line number to start reading from (1-indexed)"},"limit":{"type":"integer","minimum":0,"description":"Maximum number of lines to read"}},"required":["path"]},"eager_input_streaming":true},
             {"name":"write","description":"Write UTF-8 text content to a file, creating parent directories and overwriting existing contents. Bounded text profile; this is not atomic replacement.","input_schema":{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to write (relative or absolute)"},"content":{"type":"string","description":"Content to write to the file"}},"required":["path","content"]},"eager_input_streaming":true}]
            """)!.AsArray();
        if (api == "anthropic-messages")
        {
            declarations[1]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = user });
            if (image) content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = "AA==" } });
            content[^1]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            return new JsonObject { ["model"] = "pisharp-offline-session", ["max_tokens"] = 8192, ["stream"] = true, ["tools"] = declarations,
                ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Explicit offline session file tools.", ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }),
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }) };
        }
        Check(!image, "Only the explicit Anthropic profile currently projects images.");
        var tools = new JsonArray(); foreach (var row in declarations)
        {
            var parameters = row!["input_schema"]!.DeepClone(); parameters["additionalProperties"] = false;
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
        start.Environment["PISHARP_NATIVE_NODE_COMMAND_INPUT_OPTIONS"] = Path.Combine(invocation, "options.json"); start.ArgumentList.Add(files.C.Cli);
        foreach (var argument in new[] { "session", command, "--session", files.Session, "--workspace", files.Root, "--offline-api", files.Api }.Concat(files.Images ? new[] { "--offline-images", "true" } : []).Concat(command == "create" ? [] : new[] { "--offline-script", files.Script }).Concat(files.ExtensionArgs).Concat(extra)) start.ArgumentList.Add(argument);
        return start;
    }
    private static async Task<Result> OneShot(Files files, string command, string[] extra)
    {
        var invocation = await files.Invocation(command); using var process = Process.Start(Start(files, invocation, command, extra)) ?? throw new IOException("Actual Commands/Input CLI did not start.");
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
            this.invocation = invocation; process = Process.Start(NodeCommandInputPackageTests.Start(files, invocation, "rpc", extra)) ?? throw new IOException("Actual Commands/Input RPC did not start.");
            stdout = new(process.StandardOutput.BaseStream); stderr = new(process.StandardError.BaseStream); output = ReadOutput(); error = Drain(stderr);
        }
        internal static async Task<RpcChild> Start(Files files, string[] extra) => new(files, await files.Invocation("rpc"), extra);
        internal async Task Send(object command) { await process.StandardInput.WriteAsync((JsonSerializer.Serialize(command) + "\n").AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
        internal Task<JsonData> Response(string id) => Wait(record => Type(record) == "response" && record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        internal async Task<JsonElement> Ui(int from, string method)
        {
            var row = await Wait(candidate => IsUi(candidate, method) && Records.Skip(from).Any(record => ReferenceEquals(candidate, record)));
            return row.Value;
        }
        internal async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        {
            while (true)
            {
                Task next; lock (gate) { var match = records.FirstOrDefault(predicate); if (match is not null) return match;
                    if (output.IsFaulted) ExceptionDispatchInfo.Capture(output.Exception!.InnerException!).Throw();
                    Check(!output.IsCompleted, "Actual Commands/Input RPC ended before its observer: stderr=" + Bounded(Utf8.GetString(stderr.Bytes))); next = changed.Task; }
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
                    Check(frame.IsAccepted && !frame.IsFinalFrame, "Actual Commands/Input RPC stdout has a malformed/truncated frame."); TaskCompletionSource signal;
                    lock (gate) { Check(records.Count < 2048, "Actual Commands/Input RPC record limit."); records.Add(frame.Record!); signal = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); } signal.TrySetResult();
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
        private void Retain(ReadOnlySpan<byte> value) { lock (gate) { Check(value.Length <= 2_097_152 - bytes.Length, "Actual Commands/Input I/O receipt limit."); bytes.Write(value); } }
        public override int Read(byte[] buffer, int offset, int count) { var read = source.Read(buffer, offset, count); Retain(buffer.AsSpan(offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await source.ReadAsync(buffer, cancellationToken); Retain(buffer.Span[..read]); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) bytes.Dispose(); base.Dispose(disposing); }
    }

    private sealed class Files
    {
        internal Context C { get; } internal string Api { get; } internal string Root { get; } internal bool Images { get; }
        private readonly List<string> invocations = []; private readonly JsonData golden;
        internal string In(string name) => Path.Combine(Root, name); internal string Session => In("session.jsonl"); internal string Script => In("script.json");
        internal string Package => In("published"); internal string Manifest => In("manifest.json"); internal string Approval => In("approval.json"); internal string Snapshots => In("snapshots");
        internal string SourcePath => Path.Combine(C.Oracle, "upstream/packages/coding-agent/examples/extensions/commands.ts".Replace('/', Path.DirectorySeparatorChar));
        internal string[] ExtensionArgs => ["--extension-package", Package, "--extension-manifest", Manifest, "--extension-approval", Approval, "--extension-snapshot-root", Snapshots, "--enable-extension-command", "commands"];
        private Files(Context c, string api, bool images, JsonData expected) { C = c; Api = api; Images = images; golden = expected; Root = Path.Combine(c.Runs, "node-command-input-" + api + "-" + Guid.NewGuid().ToString("N")); }
        internal JsonElement Golden(string id) => golden.Value.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == id);
        internal static async Task<Files> Create(Context c, string api, bool images = false)
        {
            var expected = await File.ReadAllBytesAsync(Path.Combine(c.CommandInputReference, "fixtures/reference/node-command-input/expected.json")); Equal(ExpectedSha, Convert.ToHexStringLower(SHA256.HashData(expected)));
            var files = new Files(c, api, images, JsonData.Parse(Utf8.GetString(expected))); foreach (var path in new[] { files.Root, files.Package, files.Snapshots }) Directory.CreateDirectory(path);
            Check(File.Exists(Path.Combine(c.Published, "PublishedFixture.NodeCommandInput.dll")), "Root must publish the actual Commands/Input package first.");
            foreach (var path in Directory.GetFiles(c.Published, "*", SearchOption.AllDirectories)) { var target = Path.Combine(files.Package, Path.GetRelativePath(c.Published, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target); }
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var path in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) hashes.Add(Path.GetRelativePath(files.Package, path).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
            var metadata = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.node.command-input", packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = "PublishedFixture.NodeCommandInput.dll", entryType = "PublishedNodeCommandInputFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" }, declaredCapabilities = new[] { "commands", "observations" }, resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.NodeCommandInput.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            await File.WriteAllTextAsync(files.Manifest, metadata, Utf8);
            await File.WriteAllTextAsync(files.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = files.Package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(metadata))), artifactHashes = hashes, sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1, sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = Array.Empty<string>(), enabledCommands = new[] { "commands" },
                systemImports = new[] { new { profile = "windows-worker-directory-0", artifact = "PiSharp.ExtensionHost.dll", sha256 = hashes["PiSharp.ExtensionHost.dll"] } } }), Utf8); return files;
        }
        internal async Task<string> Invocation(string command)
        {
            var root = In("invocation-" + (invocations.Count + 1) + "-" + command); Directory.CreateDirectory(root); invocations.Add(root);
            await File.WriteAllTextAsync(Path.Combine(root, "options.json"), JsonSerializer.Serialize(new { schemaVersion = 1, node = C.Node, repository = C.Repository, oracle = C.Oracle, jiti = C.Jiti, reference = C.Reference, commandInputReference = C.CommandInputReference, workspace = Root,
                runRoot = Path.Combine(root, "worker"), receipt = Path.Combine(root, "package.receipt.json"), workerGeneration = invocations.Count, sessionGeneration = invocations.Count, inputInstances = 2, controlledClock = Clock }), Utf8); return root;
        }
        internal async Task<JsonElement> Receipt(int index)
        { var bytes = await File.ReadAllBytesAsync(Path.Combine(invocations[index], "package.receipt.json")); Check(bytes.Length <= 2_097_152, "Commands/Input package receipt bound."); return JsonData.Parse(Utf8.GetString(bytes)).Value; }
        internal async Task<string> LastFailure() { var path = Path.Combine(invocations[^1], "package.receipt.json"); return File.Exists(path) && new FileInfo(path).Length <= 2_097_152 ? Bounded(await File.ReadAllTextAsync(path)) : "no bounded actual package receipt"; }
        internal async Task VerifyReceipts(int expected)
        {
            Equal(expected, invocations.Count); Equal(0, Directory.GetDirectories(Snapshots).Length);
            for (var index = 0; index < invocations.Count; index++)
            {
                var receipt = await Receipt(index); Equal("initialized", receipt.GetProperty("initializationStage").GetString()); Equal(JsonValueKind.Null, receipt.GetProperty("initializationFailure").ValueKind);
                Equal(0, receipt.GetProperty("activeNativeContexts").GetInt32()); Equal(0, receipt.GetProperty("cleanupFailures").GetArrayLength()); Equal(0, receipt.GetProperty("executedCorpusCountCredit").GetInt32()); Check(!receipt.GetProperty("phaseAcceptanceClaimed").GetBoolean(), "Workflow assigned premature phase credit.");
                var load = receipt.GetProperty("sourceLoad"); Check(load.GetProperty("factoryAwaited").GetBoolean() && load.GetProperty("sourceFunctionsRemainInNode").GetBoolean(), "Genuine source activation proof missing.");
                Equal("d86654abb8862e201933517d6f1fce9f88dd117f", load.GetProperty("sourceCommit").GetString()); Equal(2, load.GetProperty("sourceFactoryCount").GetInt32()); Equal(3, load.GetProperty("successfulSourceFactoryInvocations").GetInt32());
                Equal(1, load.GetProperty("commands").GetArrayLength()); Equal(2, load.GetProperty("inputHandlers").GetArrayLength());
                var command = load.GetProperty("commands")[0]; Equal("commands", command.GetProperty("name").GetString()); Equal("List available slash commands", command.GetProperty("description").GetString()); Equal(SourcePath, command.GetProperty("sourcePath").GetString());
                Equal("handler", command.GetProperty("handler").GetProperty("functions")[0].GetProperty("name").GetString()); Equal(2, command.GetProperty("handler").GetProperty("functions")[0].GetProperty("length").GetInt32());
                Equal("getArgumentCompletions", command.GetProperty("completion").GetProperty("functions")[0].GetProperty("name").GetString()); Equal(1, command.GetProperty("completion").GetProperty("functions")[0].GetProperty("length").GetInt32());
                foreach (var input in load.GetProperty("inputHandlers").EnumerateArray()) { Equal("registeredHandler", input.GetProperty("callback").GetProperty("functions")[0].GetProperty("name").GetString()); Equal(0, input.GetProperty("callback").GetProperty("functions")[0].GetProperty("length").GetInt32()); }
                foreach (var (path, sha) in new[] { ("packages/coding-agent/examples/extensions/commands.ts", "36716b53da169936c7e1360a4fde1e2fc0c3356a6505f177235f09c5538c6e4f"), ("packages/coding-agent/examples/extensions/input-transform.ts", "cf0f65d610631ca75d18aae1c8f1408139702f674cb2835f8c009b943776474b") })
                    Equal(sha, load.GetProperty("sourcePins").EnumerateArray().Single(row => row.GetProperty("path").GetString() == path).GetProperty("sha256").GetString());
                foreach (var path in new[] { "upstream/packages/coding-agent/src/core/extensions/loader.ts", "upstream/packages/coding-agent/src/core/extensions/runner.ts" }) Check(load.GetProperty("loadedModules").EnumerateArray().Any(row => row.GetProperty("path").GetString() == path), "Whole genuine loaded module absent: " + path);
                foreach (var path in new[] { "upstream/packages/coding-agent/examples/extensions/commands.ts", "upstream/packages/coding-agent/examples/extensions/input-transform.ts" }) Check(load.GetProperty("sourceReads").EnumerateArray().Any(row => row.GetProperty("path").GetString() == path), "Whole Jiti source bytes absent: " + path);
                Check(load.GetProperty("loadedModules").EnumerateArray().Any(row => row.GetProperty("path").GetString()!.StartsWith("jiti-root/node_modules/jiti/", StringComparison.Ordinal)), "Official Jiti module absent."); Equal(ExpectedSha, load.GetProperty("sourceReference").GetProperty("expectedSha256").GetString());
                var locale = load.GetProperty("locale"); Equal("en-US", locale.GetProperty("actualDefaultOptions").GetProperty("locale").GetString()); Equal("UTC", locale.GetProperty("actualDefaultOptions").GetProperty("timeZone").GetString()); Equal(Clock, locale.GetProperty("clockSeam").GetProperty("clock").GetString());
                var final = receipt.GetProperty("sourceFinalization"); Check(final.GetProperty("immutableInputsVerified").GetBoolean() && final.GetProperty("invalidated").GetBoolean() && final.GetProperty("clockRestored").GetBoolean(), "Actual source finalization/clock/immutable verification missing.");
                Equal(final.GetProperty("qualifiedInventories").GetProperty("before").GetRawText(), final.GetProperty("qualifiedInventories").GetProperty("after").GetRawText()); Equal(ExpectedSha, final.GetProperty("sourceReferenceExpectedSha256").GetString());
                if (index == 0) Equal(0, receipt.GetProperty("sourceOperations").GetArrayLength());
                foreach (var operation in receipt.GetProperty("sourceOperations").EnumerateArray())
                {
                    Check(operation.GetProperty("settled").GetBoolean(), "Actual original operation settlement fence absent.");
                    var native = operation.GetProperty("nativeAdmission"); Equal(JsonValueKind.Null, native.GetProperty("cancellationWriteFailure").ValueKind);
                    Equal(native.GetProperty("callerCancellationRequested").GetBoolean() ? "Written" : "NotRequested", native.GetProperty("cancellationWrite").GetString());
                    var observed = operation.GetProperty("observation");
                    if (operation.GetProperty("kind").GetString() == "completion") Check(!observed.GetProperty("hostCapabilitiesGranted").GetBoolean(), "Completion acquired effects.");
                    else { Equal(2, observed.GetProperty("suppliedArgumentCount").GetInt32()); Check(observed.GetProperty("publicationJoined").GetBoolean(), "Actual publication not joined before native lease release."); Equal(observed.GetProperty("suppliedBefore").GetRawText(), observed.GetProperty("suppliedAfter").GetRawText()); }
                }
                var termination = receipt.GetProperty("termination"); Equal(0, termination.GetProperty("ExitCode").GetInt32());
                Check(termination.GetProperty("HasExited").GetBoolean() && !termination.GetProperty("KillAttempted").GetBoolean() && termination.GetProperty("PinsRechecked").GetBoolean(), "Actual worker natural exit/pin join missing.");
                Equal(0, termination.GetProperty("Failures").GetArrayLength()); Equal(0L, termination.GetProperty("ObservedStderrBytes").GetInt64()); Equal(0L, termination.GetProperty("ObservedStdoutAfterProtocolBytes").GetInt64());
                Check(termination.GetProperty("StdoutAfterProtocolEof").GetBoolean(), "Node stdout EOF not joined."); Equal(3, termination.GetProperty("ExplicitStreamCloses").GetInt32());
                var protocol = termination.GetProperty("Protocol"); foreach (var field in new[] { "PendingCalls", "ActiveCallbacks", "PendingWrites", "RegisteredHandles", "BufferedBytes" }) Equal(0L, protocol.GetProperty(field).GetInt64()); Check(protocol.GetProperty("Stopped").GetBoolean(), "Worker protocol remained open.");
                var assemblies = receipt.GetProperty("assemblies"); var entry = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == "PublishedFixture.NodeCommandInput");
                foreach (var name in new[] { "PiSharp.Compatibility.Node", "PiSharp.ExtensionHost" }) { var actual = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name); Equal(entry.GetProperty("context").GetString(), actual.GetProperty("context").GetString()); Check(actual.GetProperty("collectible").GetBoolean(), "Private Node assembly escaped compiled package."); }
                foreach (var name in new[] { "PiSharp.Extensions.Abstractions", "PiSharp.Contracts" }) { var actual = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name); Equal("Default", actual.GetProperty("context").GetString()); Check(!actual.GetProperty("collectible").GetBoolean(), "Shared ABI duplicated."); }
                foreach (var name in new[] { "launch.receipt.json", "termination.json" }) Check(File.Exists(Path.Combine(invocations[index], "worker", name)), "Physical supervisor receipt missing.");
                var launch = JsonData.Parse(await File.ReadAllTextAsync(Path.Combine(invocations[index], "worker", "launch.receipt.json"))).Value; Equal("real-command-input-0", launch.GetProperty("profile").GetString());
                if (File.Exists(Path.Combine(invocations[index], "hosted.receipt.json"))) continue;
                var cli = JsonData.Parse(await File.ReadAllTextAsync(Path.Combine(invocations[index], "cli.exit.json"))).Value; Check(cli.GetProperty("hasExited").GetBoolean() && !cli.GetProperty("killed").GetBoolean() && cli.GetProperty("outputSucceeded").GetBoolean() && cli.GetProperty("errorSucceeded").GetBoolean(), "Actual CLI streams/process did not naturally join.");
            }
        }
    }

    private static async Task Create(Files files)
    {
        var result = await OneShot(files, "create", []); Check(result.ExitCode == 0 && result.Error.Length == 0,
            "Actual original Commands/Input activation failed: exit=" + result.ExitCode + " stderr=" + Bounded(result.Error) + " stdout=" + Bounded(result.Output) + " package=" + await files.LastFailure());
        Check(JsonData.Parse(result.Output).Value.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Create did not acknowledge durable flush.");
    }
    private static JsonElement[] Operations(JsonElement receipt, string kind) => receipt.GetProperty("sourceOperations").EnumerateArray().Where(row => row.GetProperty("kind").GetString() == kind).Select(row => row.GetProperty("observation")).ToArray();
    private static void Input(JsonElement operation, string callbackId, string text, string source, string result)
    {
        Equal("fulfilled", operation.GetProperty("status").GetString()); Equal(callbackId, operation.GetProperty("callbackId").GetString()); Equal(2, operation.GetProperty("suppliedArgumentCount").GetInt32());
        var before = JsonData.Parse(operation.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString()!).Value;
        Equal("input", before.GetProperty("type").GetString()); Equal(text, before.GetProperty("text").GetString()); Equal(source, before.GetProperty("source").GetString());
        Equal(result, operation.GetProperty("resultJson").GetString()); Equal("genuine ExtensionRunner.createContext", operation.GetProperty("context").GetProperty("owner").GetString());
        Equal(operation.GetProperty("suppliedBefore").GetRawText(), operation.GetProperty("suppliedAfter").GetRawText()); Check(operation.GetProperty("publicationJoined").GetBoolean(), "Original input returned before real publication join.");
    }
    private static string? GoldenInputReturn(Files files, string id, string callbackId) => files.Golden(id).GetProperty("controls").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "input-two-handler-callback-ledger-1").GetProperty("ledger")[callbackId == "input-transform-1" ? 0 : 1].GetProperty("returned").GetProperty("serializedJson").GetString();
    private static string CatalogFromTrace(string trace) => JsonData.Parse(trace).Value.GetProperty("catalog").GetRawText();
    private static async Task<JsonElement> Catalog(RpcChild rpc, string id) { await rpc.Send(new { id, type = "get_commands" }); return Good(await rpc.Response(id)).GetProperty("data").GetProperty("commands"); }
    private static void CatalogShape(Files files, JsonElement catalog)
    {
        Equal(1, catalog.GetArrayLength()); var row = catalog[0]; Equal("commands", row.GetProperty("name").GetString()); Equal("List available slash commands", row.GetProperty("description").GetString());
        Equal("extension", row.GetProperty("source").GetString()); Equal("extension", row.GetProperty("sourceInfo").GetProperty("source").GetString()); Equal(files.SourcePath, row.GetProperty("sourceInfo").GetProperty("path").GetString());
        Equal("fixture.node.command-input", row.GetProperty("ownerId").GetString()); Equal(1L, row.GetProperty("ownerGeneration").GetInt64()); Equal("commands-1", row.GetProperty("registrationId").GetString());
    }
    private static async Task<JsonElement> Completion(RpcChild rpc, string id, string prefix) { await rpc.Send(new { id, type = "pisharp_complete_extension_command", command = "commands", prefix }); return Good(await rpc.Response(id)).GetProperty("data").GetProperty("completions"); }
    private static async Task HandledNotify(RpcChild rpc, string message, string expected, string kind, string id)
    {
        var start = rpc.Records.Length; await rpc.Send(new { id, type = "prompt", message }); Handled(await rpc.Response(id)); var notification = await rpc.Ui(start, "notify");
        Equal(expected, notification.GetProperty("message").GetString()); Equal(kind, notification.GetProperty("notifyType").GetString()); Equal(1, rpc.Records[start..].Count(row => IsUi(row, "notify"))); NoAgent(rpc.Records[start..]);
    }
    private static async Task CancelOrHeader(RpcChild rpc, string id, string? selected, bool confirmFalse)
    {
        var start = rpc.Records.Length; await rpc.Send(new { id, type = "prompt", message = "/commands extension" }); var select = await rpc.Ui(start, "select");
        if (selected is null) await rpc.Send(new { type = "extension_ui_response", id = select.GetProperty("id").GetString(), cancelled = true });
        else await rpc.Send(new { type = "extension_ui_response", id = select.GetProperty("id").GetString(), value = selected });
        if (confirmFalse) { var confirm = await rpc.Ui(start, "confirm"); await rpc.Send(new { type = "extension_ui_response", id = confirm.GetProperty("id").GetString(), confirmed = false }); }
        Handled(await rpc.Response(id)); NoAgent(rpc.Records[start..]); Check(!rpc.Records[start..].Any(row => IsUi(row, "notify")), "Header/cancel/false source branch published a notification.");
    }
    private static async Task EntriesUnchanged(Files files, RpcChild rpc, string id, SessionLogReadResult expected)
    {
        await rpc.Send(new { id, type = "get_entries" }); await Acknowledged(files, Good(await rpc.Response(id)).GetProperty("data").GetProperty("entries"));
        SessionLogReadResult observed;
        await using (var source = new FileStream(files.Session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous))
            observed = await new SessionLogReader().ReadAsync(source, leaveOpen: true);
        Complete(observed);
        Check(expected.OriginalBytes.AsSpan().SequenceEqual(observed.OriginalBytes.AsSpan()), "Original Handled/command callback changed acknowledged durable provider history.");
    }
    private static void Handled(JsonData record) => Equal("handled", Good(record).GetProperty("data").GetProperty("disposition").GetString());
    private static void NoAgent(IEnumerable<JsonData> records) => Check(!records.Any(row => Type(row) is "agent_start" or "agent_end" or "agent_settled" or "tool_execution_start" or "message_end"), "Source Handled/command path entered provider/tool generation.");
    private static bool IsUi(JsonData record, string method) => Type(record) == "extension_ui_request" && record.Value.GetProperty("method").GetString() == method;
    private static bool ResponseId(JsonData record, string id) => Type(record) == "response" && record.Value.TryGetProperty("id", out var actual) && actual.GetString() == id;
    private static JsonElement User(SessionContextProjection context, string text) => context.LlmMessages.Where(message => message.Role == "user").Select(message => message.WireBody.Value).Single(value => value.GetProperty("content").ValueKind == JsonValueKind.Array && value.GetProperty("content")[0].GetProperty("text").GetString() == text);
    private static void Prefix(SessionLogReadResult before, SessionLogReadResult after) => Check(after.OriginalBytes.AsSpan().StartsWith(before.OriginalBytes.AsSpan()), "Reopen/branch rewrote committed session history.");
    private static void Loadout(SessionLogReadResult log)
    {
        var system = log.ValidatedPrefix.Skip(1).Select(record => record.Entry.WireBody.Value).Single(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "system");
        Check(system.GetProperty("message").GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(["read", "write"]), "Command/input package added an artificial LLM tool or durable declaration.");
    }
    private static void Requests(JsonElement report, string api, int count)
    {
        Equal(count, report.GetProperty("requests").GetArrayLength()); foreach (var request in report.GetProperty("requests").EnumerateArray())
        { Equal(api, request.GetProperty("api").GetString()); Check(request.GetProperty("authoredInertAuthValidated").GetBoolean() && request.GetProperty("historyRequirementsSatisfied").GetBoolean(), "Recorded provider request predicate failed."); Check(request.GetProperty("toolNames").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["read", "write"]), "Commands/input changed model tool advertisement."); }
    }
    private static string Absolute(string path) { Check(Path.IsPathFullyQualified(path), "Explicit absolute Commands/Input gate path required."); return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
    private static string CallId(string api, string call) => api switch { "openai-responses" => call + "|fc-" + call, "anthropic-messages" or "openai-completions" => call, _ => throw new InvalidOperationException("Unapproved identity mapping.") };
    private static string Type(JsonData record) => record.Value.GetProperty("type").GetString()!;
    private static JsonElement Good(JsonData record) { Check(record.Value.GetProperty("success").GetBoolean(), "Actual Commands/Input RPC command failed: " + Bounded(record.ToString())); return record.Value; }
    private static void Clean(Result result) => Check(result.ExitCode == 0 && result.Error.Length == 0, "Actual Commands/Input CLI/RPC failure: exit=" + result.ExitCode + " stderr=" + Bounded(result.Error) + " stdout=" + Bounded(result.Output));
    private static string Bounded(string value) => JsonSerializer.Serialize(value.Length <= 6144 ? value : value[..6144] + "[truncated; complete raw bytes retained]");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void SameJson(JsonElement expected, JsonElement actual) => Check(JsonElement.DeepEquals(expected, actual), "Complete source observation differs; JSON formatting is not source data.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Actual values differ: expected=" + expected + " actual=" + actual);
}

