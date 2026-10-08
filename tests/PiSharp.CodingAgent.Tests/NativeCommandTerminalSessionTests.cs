using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;

/// <summary>Explicit Node opt-in; actual keyboard, native cells and recorded provider share one durable host.</summary>
internal static class NativeCommandTerminalSessionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string host, string harness, string cli, string published,
        string node, string repository, string oracle, string jiti, string reference, string commandInputReference, string runParent)
        => NodeCommandInputPackageTests.CasesTerminal(host, harness, cli, published, node, repository, oracle, jiti,
            reference, commandInputReference, runParent);
}

// Reuse the unchanged package/source/provider assertions rather than a second package scaffold.
internal static partial class NodeCommandInputPackageTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> CasesTerminal(string host, string harness, string cli, string published,
        string node, string repository, string oracle, string jiti, string reference, string commandInputReference, string runParent)
    {
        Check(OperatingSystem.IsWindows(), "Original Commands/Input terminal opt-in requires real Windows ConPTY.");
        var c = new Context(Absolute(host), Absolute(cli), Absolute(published), Absolute(node), Absolute(repository),
            Absolute(oracle), Absolute(jiti), Absolute(reference), Absolute(commandInputReference), Absolute(runParent));
        harness = Absolute(harness);
        foreach (var path in new[] { c.Host, harness, c.Cli, c.Node }) Check(File.Exists(path), "Explicit terminal executable missing.");
        foreach (var path in new[] { c.Published, c.Repository, c.Oracle, c.Jiti, c.Reference, c.CommandInputReference, c.Runs })
            Check(Directory.Exists(path), "Explicit terminal source/package/run parent missing.");
        yield return ("node-command-terminal.original-completion-dialogs-handled-transform-write-save-reopen", () => TerminalWorkflow(c, harness));
        yield return ("node-command-terminal.original-cancel-retirement-denied-write-save-reopen", () => TerminalCancellationPolicy(c, harness));
    }

    private static async Task TerminalWorkflow(Context c, string harness)
    {
        var files = await Files.Create(c, "openai-responses"); await Create(files); var initial = await Complete(files); Loadout(initial);
        var nonce = Guid.NewGuid().ToString("N")[..8]; var original = "?quick explain " + nonce;
        var transformed = "Respond briefly in 1-2 sentences: explain " + nonce;
        var target = files.In("terminal-effect.txt"); var saved = "saved:" + nonce + " 文🙂\n"; var final = "TERMINAL:" + nonce + " 文🙂";
        var firstTurn = JsonSerializer.SerializeToNode(Tool(files.Api, "write", "terminal-write-" + nonce, new { path = target, content = saved }, transformed))!;
        firstTurn["expectedRequest"] = InitialRequest(files.Api, transformed);
        await Script(files, firstTurn, Text(files.Api, final, transformed, "Successfully wrote"));
        var scriptBefore = await File.ReadAllBytesAsync(files.Script); var invocation = await files.Invocation("terminal");
        await WindowsConPtyTerminalSessionFixture.ExecuteCommandInputAsync(c.Host, harness, files.Root, invocation, nonce,
            TerminalArgs(files, ["--allow-write", target]), async terminal =>
            {
                var startup = terminal.Records.Single(row => TerminalResponseId(row, "chat-commands"));
                CatalogShape(files, TerminalGood(startup).GetProperty("data").GetProperty("commands"));
                foreach (var prefix in new[] { "", "p", "extension", "unknown" }) await TerminalCompletion(files, terminal, prefix);
                var from = terminal.RecordCount; var commandId = await terminal.LineAsync("/commands extension");
                var select = await terminal.UiAsync("select", from);
                SameJson(JsonData.Parse("[\"--- Extensions ---\",\"/commands - List available slash commands\"]").Value, select.GetProperty("options"));
                Equal("Available Commands", select.GetProperty("title").GetString());
                await terminal.PublishedAsync(select.GetProperty("id").GetString()!);
                await terminal.ScreenAsync("[ui select] Available Commands");
                Check(!terminal.Records.Any(row => TerminalResponseId(row, commandId)), "Terminal command acknowledged while original select was held.");
                var state = TerminalGood(await terminal.ResponseAsync(await terminal.LineAsync("/state"))).GetProperty("data");
                Check(!state.GetProperty("isStreaming").GetBoolean(), "Held source command invented a provider run.");
                await terminal.ScreenAsync("[state] idle pending=0");
                await TerminalCompletion(files, terminal, "extension");
                await terminal.LineAsync("2"); var confirm = await terminal.UiAsync("confirm", from);
                Equal("commands", confirm.GetProperty("title").GetString()); Equal("View source path?\n" + files.SourcePath, confirm.GetProperty("message").GetString());
                await terminal.PublishedAsync(confirm.GetProperty("id").GetString()!); await terminal.ScreenAsync("[ui confirm] commands");
                await terminal.LineAsync("maybe"); await terminal.ScreenAsync("[ui] Enter yes/no");
                Check(!terminal.Records.Any(row => TerminalResponseId(row, commandId)), "Invalid terminal text approved a real source confirmation.");
                await terminal.LineAsync("yes"); var notification = await terminal.UiAsync("notify", from);
                Equal(files.SourcePath, notification.GetProperty("message").GetString()); Equal("info", notification.GetProperty("notifyType").GetString());
                TerminalHandled(await terminal.ResponseAsync(commandId)); await terminal.RetiredAsync(confirm.GetProperty("id").GetString()!, "Value");
                await terminal.ScreenAsync("[notice]");
                await TerminalHandledNotice(terminal, "PiNg", "pong", "info");
                // The genuine Source editor trims before invoking Input. Its raw-RPC usage-warning
                // branch remains covered by the unchanged Commands/Input process workflow.
                await TerminalHandledNotice(terminal, " PiNg ", "pong", "info");
                await TerminalUnchanged(files, initial); NoAgent(terminal.Records.Select(row => JsonData.FromElement(row)));
                var runFrom = terminal.RecordCount; var promptId = await terminal.LineAsync(original);
                TerminalGood(await terminal.ResponseAsync(promptId)); await terminal.RecordAsync(row => row.GetProperty("type").GetString() == "agent_settled", runFrom);
                await terminal.ScreenAsync("TERMINAL:" + nonce); await terminal.ScreenAsync("[settled]");
                await terminal.ScreenAsync("\\u6587"); await terminal.ScreenAsync("\\U0001f642");
                var tool = terminal.Records.Skip(runFrom).Single(row => row.GetProperty("type").GetString() == "tool_execution_end");
                Equal(CallId(files.Api, "terminal-write-" + nonce), tool.GetProperty("toolCallId").GetString());
                Check(!tool.GetProperty("isError").GetBoolean(), "Terminal native granted write failed.");
            });
        var scriptAfter = await File.ReadAllBytesAsync(files.Script); Check(scriptBefore.AsSpan().SequenceEqual(scriptAfter), "Terminal changed literal provider script bytes.");
        var first = await Complete(files); Prefix(initial, first); Loadout(first); User(Project(first), transformed);
        Equal(final, TerminalAssistant(first)); Equal(saved, await File.ReadAllTextAsync(target, Utf8));
        var receipt = await files.Receipt(1); var command = Operations(receipt, "command").Single();
        Equal("fulfilled", command.GetProperty("status").GetString()); Equal("extension", JsonData.Parse(command.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString()!).Value.GetString());
        CatalogShape(files, JsonData.Parse(command.GetProperty("context").GetProperty("actualNativeCatalog").GetProperty("serializedJson").GetString()!).Value);
        Equal("genuine ExtensionRunner.createCommandContext", command.GetProperty("context").GetProperty("owner").GetString());
        var completions = Operations(receipt, "completion"); Equal(5, completions.Length);
        foreach (var completion in completions)
        {
            Equal("fulfilled", completion.GetProperty("status").GetString()); Equal("commands-1", completion.GetProperty("callbackId").GetString());
            var prefix = completion.GetProperty("supplied").GetProperty("serializedJson").GetString();
            var frozen = files.Golden("commands-completions").GetProperty("dispatches").EnumerateArray()
                .Single(row => row.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString() == prefix);
            Equal(frozen.GetProperty("outcome").GetProperty("returned").GetProperty("serializedJson").GetString(), completion.GetProperty("resultJson").GetString());
        }
        var inputs = Operations(receipt, "input"); Equal(4, inputs.Length);
        Input(inputs[0], "input-transform-1", "PiNg", "rpc", "{\"action\":\"handled\"}");
        Input(inputs[1], "input-transform-1", "PiNg", "rpc", "{\"action\":\"handled\"}");
        Input(inputs[2], "input-transform-1", original, "rpc", JsonSerializer.Serialize(new { action = "transform", text = transformed }));
        Input(inputs[3], "input-transform-2", transformed, "rpc", "{\"action\":\"continue\"}");
        await TerminalReceipt(files, 1);
        await Script(files, Text(files.Api, "REOPEN:" + nonce, transformed, final, "resume:" + nonce));
        var reopenedInvocation = await files.Invocation("terminal-reopen");
        await WindowsConPtyTerminalSessionFixture.ExecuteCommandInputAsync(c.Host, harness, files.Root, reopenedInvocation, nonce,
            TerminalArgs(files, ["--allow-write", target]), async terminal =>
            {
                var history = TerminalGood(terminal.Records.Single(row => TerminalResponseId(row, "chat-history"))).GetProperty("data").GetProperty("messages");
                Check(history.EnumerateArray().Any(row => row.GetProperty("role").GetString() == "assistant" && row.GetProperty("content").EnumerateArray().Any(block => block.TryGetProperty("text", out var text) && text.GetString() == final)), "Actual terminal reopen omitted authoritative saved assistant.");
                await terminal.ScreenAsync("TERMINAL:" + nonce); Check(terminal.Records.All(row => row.GetProperty("type").GetString() != "extension_ui_request"), "Reopen replayed a saved source dialog.");
                var from = terminal.RecordCount; TerminalGood(await terminal.ResponseAsync(await terminal.LineAsync("resume:" + nonce)));
                await terminal.RecordAsync(row => row.GetProperty("type").GetString() == "agent_settled", from); await terminal.ScreenAsync("REOPEN:" + nonce);
            });
        var resumed = await Complete(files); Prefix(first, resumed); Loadout(resumed); Equal("REOPEN:" + nonce, TerminalAssistant(resumed));
        Equal(saved, await File.ReadAllTextAsync(target, Utf8));
        var reopened = await files.Receipt(2); Equal(0, Operations(reopened, "command").Length); Equal(0, Operations(reopened, "completion").Length);
        var reopenedInputs = Operations(reopened, "input"); Equal(2, reopenedInputs.Length);
        foreach (var input in reopenedInputs) Input(input, input.GetProperty("callbackId").GetString()!, "resume:" + nonce, "rpc", "{\"action\":\"continue\"}");
        await TerminalReceipt(files, 2); await TerminalSummary(files, nonce, "command-input-save-reopen", first, resumed);
    }

    private static async Task TerminalCancellationPolicy(Context c, string harness)
    {
        var files = await Files.Create(c, "openai-responses"); await Create(files); var initial = await Complete(files); Loadout(initial);
        var nonce = Guid.NewGuid().ToString("N")[..8]; var original = "?quick denied " + nonce;
        var transformed = "Respond briefly in 1-2 sentences: denied " + nonce; var target = files.In("ungranted-terminal-effect.txt");
        var firstTurn = JsonSerializer.SerializeToNode(Tool(files.Api, "write", "terminal-denied-" + nonce, new { path = target, content = "must not write" }, transformed))!;
        firstTurn["expectedRequest"] = InitialRequest(files.Api, transformed); await Script(files, firstTurn, Text(files.Api, "DENIED:" + nonce, transformed));
        var invocation = await files.Invocation("terminal-cancel-policy");
        await WindowsConPtyTerminalSessionFixture.ExecuteCommandInputAsync(c.Host, harness, files.Root, invocation, nonce, TerminalArgs(files, []), async terminal =>
        {
            var from = terminal.RecordCount; var commandId = await terminal.LineAsync("/commands extension"); var select = await terminal.UiAsync("select", from);
            await terminal.PublishedAsync(select.GetProperty("id").GetString()!); await terminal.ScreenAsync("[ui select] Available Commands");
            TerminalGood(await terminal.ResponseAsync(await terminal.LineAsync("/abort")));
            var canceled = await terminal.ResponseAsync(commandId); Check(!canceled.GetProperty("success").GetBoolean(), "Aborted terminal command became acknowledged success.");
            Equal("Command canceled before acceptance.", canceled.GetProperty("error").GetString());
            await terminal.RetiredAsync(select.GetProperty("id").GetString()!, "Cancelled"); await terminal.ScreenAsync("[ui retired] Cancelled");
            await TerminalUnchanged(files, initial);
            // A new actual select must have its own published identity; Escape cancels its real pending dialog.
            var nextFrom = terminal.RecordCount; var nextId = await terminal.LineAsync("/commands extension"); var next = await terminal.UiAsync("select", nextFrom);
            Check(next.GetProperty("id").GetString() != select.GetProperty("id").GetString(), "Retired dialog identity was reused.");
            await terminal.PublishedAsync(next.GetProperty("id").GetString()!); await terminal.ScreenAsync("[ui select] Available Commands");
            await terminal.CancelDialogAsync(); TerminalHandled(await terminal.ResponseAsync(nextId));
            await terminal.RetiredAsync(next.GetProperty("id").GetString()!, "Cancelled");
            Check(!terminal.Records.Any(row => IsUi(JsonData.FromElement(row), "confirm") || IsUi(JsonData.FromElement(row), "notify")), "Canceled or retired terminal select granted later source UI effects.");
            await TerminalUnchanged(files, initial); NoAgent(terminal.Records.Select(row => JsonData.FromElement(row)));
            var runFrom = terminal.RecordCount; TerminalGood(await terminal.ResponseAsync(await terminal.LineAsync(original)));
            await terminal.RecordAsync(row => row.GetProperty("type").GetString() == "agent_settled", runFrom);
            var tool = terminal.Records.Skip(runFrom).Single(row => row.GetProperty("type").GetString() == "tool_execution_end");
            Equal(CallId(files.Api, "terminal-denied-" + nonce), tool.GetProperty("toolCallId").GetString());
            Check(tool.GetProperty("isError").GetBoolean() && !File.Exists(target), "Original terminal Input transformation bypassed final native write authority.");
            await terminal.ScreenAsync("DENIED:" + nonce);
        });
        var first = await Complete(files); Prefix(initial, first); Loadout(first); User(Project(first), transformed); Equal("DENIED:" + nonce, TerminalAssistant(first));
        var result = Project(first).LlmMessages.Single(row => row.Role == "toolResult").WireBody.Value;
        Equal(CallId(files.Api, "terminal-denied-" + nonce), result.GetProperty("toolCallId").GetString()); Check(result.GetProperty("isError").GetBoolean() && !File.Exists(target), "Final native denial was not durably retained.");
        var receipt = await files.Receipt(1); var commands = Operations(receipt, "command"); Equal(2, commands.Length);
        var rejected = commands.Single(row => row.GetProperty("status").GetString() == "rejected"); Check(rejected.GetProperty("signalAfter").GetProperty("aborted").GetBoolean(), "Original source callback did not observe actual cancellation.");
        var admission = receipt.GetProperty("sourceOperations").EnumerateArray().Single(row => row.GetProperty("kind").GetString() == "command" && row.GetProperty("status").GetString() == "rejected").GetProperty("nativeAdmission");
        Equal("Written", admission.GetProperty("cancellationWrite").GetString()); Check(admission.GetProperty("callerCancellationRequested").GetBoolean(), "Terminal cancellation lost its actual caller token.");
        Equal(1, admission.GetProperty("uiCancellationWrites").GetArrayLength()); Equal("Written", admission.GetProperty("uiCancellationWrites")[0].GetProperty("disposition").GetString());
        Equal("fulfilled", commands.Single(row => row.GetProperty("status").GetString() == "fulfilled").GetProperty("status").GetString());
        await TerminalReceipt(files, 1);
        await Script(files, Text(files.Api, "REOPEN-DENIED:" + nonce, "DENIED:" + nonce, transformed, "resume:" + nonce));
        var reopenedInvocation = await files.Invocation("terminal-reopen-policy");
        await WindowsConPtyTerminalSessionFixture.ExecuteCommandInputAsync(c.Host, harness, files.Root, reopenedInvocation, nonce, TerminalArgs(files, []), async terminal =>
        {
            await terminal.ScreenAsync("DENIED:" + nonce); Check(terminal.Records.All(row => row.GetProperty("type").GetString() != "extension_ui_request"), "Reopen resurrected the canceled source dialog.");
            var from = terminal.RecordCount; TerminalGood(await terminal.ResponseAsync(await terminal.LineAsync("resume:" + nonce)));
            await terminal.RecordAsync(row => row.GetProperty("type").GetString() == "agent_settled", from); await terminal.ScreenAsync("REOPEN-DENIED:" + nonce);
            Check(terminal.Records.All(row => row.GetProperty("type").GetString() != "tool_execution_start"), "Independent terminal reopen replayed a denied native tool.");
        });
        var resumed = await Complete(files); Prefix(first, resumed); Loadout(resumed); Equal("REOPEN-DENIED:" + nonce, TerminalAssistant(resumed)); Check(!File.Exists(target), "Terminal reopen granted the prior denied effect.");
        Equal(0, Operations(await files.Receipt(2), "command").Length); await TerminalReceipt(files, 2); await TerminalSummary(files, nonce, "command-cancel-native-policy", first, resumed);
    }

    private static string[] TerminalArgs(Files files, string[] extra) => new[] { "session", "terminal", "--terminal-preview", "--session", files.Session,
        "--workspace", files.Root, "--offline-api", files.Api, "--offline-script", files.Script }.Concat(files.ExtensionArgs).Concat(extra).ToArray();
    private static async Task TerminalCompletion(Files files, WindowsConPtyTerminalSessionFixture.CommandInputConsole terminal, string prefix)
    {
        var response = TerminalGood(await terminal.ResponseAsync(await terminal.LineAsync("/complete commands" + (prefix.Length == 0 ? "" : " " + prefix))));
        Equal("pisharp_complete_extension_command", response.GetProperty("command").GetString());
        var original = files.Golden("commands-completions").GetProperty("dispatches").EnumerateArray()
            .Single(row => row.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString() == JsonSerializer.Serialize(new { prefix }));
        Equal(original.GetProperty("outcome").GetProperty("returned").GetProperty("serializedJson").GetString(), response.GetProperty("data").GetProperty("completions").GetRawText());
        await terminal.ScreenAsync("[completion commands]");
    }
    private static async Task TerminalHandledNotice(WindowsConPtyTerminalSessionFixture.CommandInputConsole terminal, string text, string message, string kind)
    {
        var from = terminal.RecordCount; var id = await terminal.LineAsync(text); var notification = await terminal.UiAsync("notify", from);
        Equal(message, notification.GetProperty("message").GetString()); Equal(kind, notification.GetProperty("notifyType").GetString());
        TerminalHandled(await terminal.ResponseAsync(id)); await terminal.ScreenAsync("[notice] " + message);
    }
    private static async Task TerminalUnchanged(Files files, SessionLogReadResult expected)
    {
        SessionLogReadResult actual;
        await using (var source = new FileStream(files.Session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous))
            actual = await new SessionLogReader().ReadAsync(source, leaveOpen: true);
        Complete(actual); Check(expected.OriginalBytes.AsSpan().SequenceEqual(actual.OriginalBytes.AsSpan()), "Terminal command/Handled/cancel changed committed provider history.");
    }
    private static string TerminalAssistant(SessionLogReadResult log) => string.Join("", Project(log).LlmMessages.Last(row => row.Role == "assistant")
        .WireBody.Value.GetProperty("content").EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()));
    private static bool TerminalResponseId(JsonElement row, string id) => row.GetProperty("type").GetString() == "response" && row.TryGetProperty("id", out var value) && value.GetString() == id;
    private static JsonElement TerminalGood(JsonElement row) { Check(row.GetProperty("success").GetBoolean(), "Actual terminal RPC failed: " + Bounded(row.GetRawText())); return row; }
    private static void TerminalHandled(JsonElement row) => Equal("handled", TerminalGood(row).GetProperty("data").GetProperty("disposition").GetString());
    private static async Task TerminalReceipt(Files files, int index)
    {
        var receipt = await files.Receipt(index); Equal("initialized", receipt.GetProperty("initializationStage").GetString()); Equal(JsonValueKind.Null, receipt.GetProperty("initializationFailure").ValueKind);
        Equal(0, receipt.GetProperty("activeNativeContexts").GetInt32()); Equal(0, receipt.GetProperty("cleanupFailures").GetArrayLength()); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
        Equal(0, receipt.GetProperty("executedCorpusCountCredit").GetInt32()); Check(!receipt.GetProperty("phaseAcceptanceClaimed").GetBoolean(), "Terminal fixture assigned corpus or phase credit.");
        var load = receipt.GetProperty("sourceLoad"); Check(load.GetProperty("factoryAwaited").GetBoolean() && load.GetProperty("sourceFunctionsRemainInNode").GetBoolean(), "Terminal did not execute whole original source factories.");
        Equal("d86654abb8862e201933517d6f1fce9f88dd117f", load.GetProperty("sourceCommit").GetString()); Equal(ExpectedSha, load.GetProperty("sourceReference").GetProperty("expectedSha256").GetString());
        Equal(2, load.GetProperty("sourceFactoryCount").GetInt32()); Equal(3, load.GetProperty("successfulSourceFactoryInvocations").GetInt32());
        foreach (var (path, sha) in new[] { ("packages/coding-agent/examples/extensions/commands.ts", "36716b53da169936c7e1360a4fde1e2fc0c3356a6505f177235f09c5538c6e4f"),
            ("packages/coding-agent/examples/extensions/input-transform.ts", "cf0f65d610631ca75d18aae1c8f1408139702f674cb2835f8c009b943776474b") })
            Equal(sha, load.GetProperty("sourcePins").EnumerateArray().Single(row => row.GetProperty("path").GetString() == path).GetProperty("sha256").GetString());
        var descriptor = load.GetProperty("commands")[0]; Equal("commands", descriptor.GetProperty("name").GetString()); Equal("List available slash commands", descriptor.GetProperty("description").GetString());
        Equal(files.SourcePath, descriptor.GetProperty("sourcePath").GetString()); Equal(2, descriptor.GetProperty("handler").GetProperty("functions")[0].GetProperty("length").GetInt32());
        Equal(1, descriptor.GetProperty("completion").GetProperty("functions")[0].GetProperty("length").GetInt32());
        foreach (var operation in receipt.GetProperty("sourceOperations").EnumerateArray())
        {
            Check(operation.GetProperty("settled").GetBoolean(), "Terminal original source operation did not join.");
            Equal(JsonValueKind.Null, operation.GetProperty("nativeAdmission").GetProperty("cancellationWriteFailure").ValueKind);
            var observation = operation.GetProperty("observation");
            if (operation.GetProperty("kind").GetString() == "completion") Check(!observation.GetProperty("hostCapabilitiesGranted").GetBoolean(), "Pure terminal completion acquired effects.");
            else Check(observation.GetProperty("publicationJoined").GetBoolean(), "Terminal source publication escaped its native owner.");
        }
        var final = receipt.GetProperty("sourceFinalization"); Check(final.GetProperty("immutableInputsVerified").GetBoolean() && final.GetProperty("invalidated").GetBoolean() && final.GetProperty("clockRestored").GetBoolean(), "Terminal source finalization failed.");
        Equal(final.GetProperty("qualifiedInventories").GetProperty("before").GetRawText(), final.GetProperty("qualifiedInventories").GetProperty("after").GetRawText());
        var termination = receipt.GetProperty("termination"); Equal(0, termination.GetProperty("ExitCode").GetInt32()); Equal(0, termination.GetProperty("Failures").GetArrayLength());
        Check(termination.GetProperty("HasExited").GetBoolean() && !termination.GetProperty("KillAttempted").GetBoolean() && termination.GetProperty("PinsRechecked").GetBoolean() && termination.GetProperty("StdoutAfterProtocolEof").GetBoolean(), "Terminal Node process/streams/pins did not naturally join.");
        Equal(0L, termination.GetProperty("ObservedStderrBytes").GetInt64()); Equal(0L, termination.GetProperty("ObservedStdoutAfterProtocolBytes").GetInt64()); Equal(3, termination.GetProperty("ExplicitStreamCloses").GetInt32());
        var protocol = termination.GetProperty("Protocol"); foreach (var field in new[] { "PendingCalls", "ActiveCallbacks", "PendingWrites", "RegisteredHandles", "BufferedBytes" }) Equal(0L, protocol.GetProperty(field).GetInt64());
        Check(protocol.GetProperty("Stopped").GetBoolean(), "Terminal worker protocol remained active.");
        var assemblies = receipt.GetProperty("assemblies"); var entry = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == "PublishedFixture.NodeCommandInput");
        foreach (var name in new[] { "PiSharp.Compatibility.Node", "PiSharp.ExtensionHost" })
        { var row = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name); Equal(entry.GetProperty("context").GetString(), row.GetProperty("context").GetString()); Check(row.GetProperty("collectible").GetBoolean(), "Terminal private dependency escaped package ALC."); }
        foreach (var name in new[] { "PiSharp.Extensions.Abstractions", "PiSharp.Contracts" })
        { var row = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name); Equal("Default", row.GetProperty("context").GetString()); Check(!row.GetProperty("collectible").GetBoolean(), "Terminal duplicated shared ABI."); }
    }
    private static async Task TerminalSummary(Files files, string nonce, string scenario, SessionLogReadResult before, SessionLogReadResult after)
    {
        var raw = Utf8.GetBytes(JsonSerializer.Serialize(new { schemaVersion = 1, scenario, nonce, api = files.Api,
            canonicalSession = files.Session, initialAcknowledgedByteLength = before.OriginalBytes.Length, reopenedAcknowledgedByteLength = after.OriginalBytes.Length,
            initialSha256 = Convert.ToHexStringLower(SHA256.HashData(before.OriginalBytes.AsSpan())), reopenedSha256 = Convert.ToHexStringLower(SHA256.HashData(after.OriginalBytes.AsSpan())),
            borrowedTerminalWrapper = true, productionCliBootstrapQualified = false, executedCorpusCountCredit = 0, phaseAcceptanceClaimed = false }) + "\n");
        await using var destination = new FileStream(files.In("terminal-summary.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous);
        await destination.WriteAsync(raw); await destination.FlushAsync(); destination.Flush(flushToDisk: true);
    }
}
