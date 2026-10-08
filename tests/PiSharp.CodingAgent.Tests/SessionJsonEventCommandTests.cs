using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Output;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionJsonEventCommandTests
{
    private static string _host = "", _cli = "";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly ModelDescriptor Model = new("json-model", "openai-responses", "authored-provider");
    private const string Visible = "text\0\u6587\U0001f642\u2028\u2029\r\n\t";
    public static (string Name, Func<Task> Run)[] Cases(string dotnetHost, string cliDll)
    {
        if (!Path.IsPathFullyQualified(dotnetHost) || !File.Exists(dotnetHost) ||
            !Path.IsPathFullyQualified(cliDll) || !File.Exists(cliDll)) throw new ArgumentException("Compiled CLI paths required.");
        _host = dotnetHost; _cli = cliDll;
        return
        [
            ("JSON CLI Responses actual tools header delta records durable reopen resume and branch", () => CompiledTools("openai-responses")),
            ("JSON CLI Anthropic actual tools header delta records durable reopen resume and branch", () => CompiledTools("anthropic-messages")),
            ("JSON event subscription shares canonical projection without dropping owned fields or NUL", SharedProjection),
            ("JSON CLI strict arguments and record count depth byte quotas preserve admitted prefix", AdmissionAndLimits),
            ("JSON held writer flush blocks next actual request and source progress retains overlap receipts", BackpressureAndOverlap),
            ("JSON write flush and actual broken pipe failures poison abort and join durable cleanup", OutputFaults),
            ("JSON pre-cancel actual pending delivery and shared concurrent disposal join borrowed resources", CancellationAndDisposal),
            ("JSON CLI Completions actual file turns preserve delta bytes usage durable history resume and branch", () => CompiledTools("openai-completions")),
            ("JSON CLI Completions awaited flush effects fault poisoning and real closed pipe release durable ownership", CompletionsOutputLifecycle)
        ];
    }

    private static async Task CompiledTools(string api)
    {
        using var files = new Files();
        var source = files.In("source.txt"); var target = files.In("target.txt");
        // The admitted read tool rejects binary/control content. Retain the original NUL seed in the denial control below.
        const string seed = "source \u6587\U0001f642\n";
        const string unsupportedSeed = "source \u6587\U0001f642\0\n";
        const string saved = "saved \u6587\0value\n";
        var first = "first " + Visible;
        await File.WriteAllTextAsync(source, seed, Utf8);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        SuccessReport(await Child(files, Create(files, api)));
        var before = await File.ReadAllBytesAsync(files.Session);
        var replaySaved = api == "anthropic-messages" ? saved : "\"content\":\"saved \u6587\\u0000value\\n\"";
        await Script(files, Tool(api, "read", "json-read", new { path = source }, "first user"),
            Tool(api, "write", "json-write", new { path = target, content = saved }, seed),
            Text(api, first, "Successfully wrote", replaySaved));
        var scriptHash = SHA256.HashData(await File.ReadAllBytesAsync(files.Script));
        var result = await Child(files, Prompt(files, api, "first user", ["--allow-read", source, "--allow-write", target], "prompt"));
        var records = SuccessEvents(result); var log = await ReadComplete(files);
        Check(log.OriginalBytes.AsSpan().StartsWith(before), "JSON prompt rewrote initial durable bytes.");
        Correlate(records, log, expectedNewMessages: 6);
        Equal(saved, await File.ReadAllTextAsync(target, Utf8));
        var targetBytes = await File.ReadAllBytesAsync(target);
        Check(targetBytes.SequenceEqual(Utf8.GetBytes(saved)), "JSON tool changed inert NUL file content.");
        Equal(first, records.Last(record => Type(record) == "message_end").GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        if (api == "openai-completions")
        {
            Check(!result.Output.Contains((byte)0), "JSON records carried an unescaped physical NUL.");
            Equal(first, string.Concat(records.Where(record => Type(record) == "message_update" &&
                Type(record.GetProperty("assistantMessageEvent")) == "text_delta")
                .Select(record => record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString())));
            foreach (var assistant in records.Where(record => Type(record) == "message_end" &&
                record.GetProperty("message").GetProperty("role").GetString() == "assistant").Select(record => record.GetProperty("message")))
            {
                Equal(api, assistant.GetProperty("api").GetString()); Equal("openai", assistant.GetProperty("provider").GetString());
                Equal("pisharp-offline-completions-session", assistant.GetProperty("model").GetString());
                var usage = assistant.GetProperty("usage");
                Equal(6, usage.GetProperty("input").GetInt32()); Equal(4, usage.GetProperty("output").GetInt32());
                Equal(2, usage.GetProperty("cacheRead").GetInt32()); Equal(12, usage.GetProperty("totalTokens").GetInt32());
            }
            var final = records.Last(record => Type(record) == "message_end").GetProperty("message");
            Equal("cmpl-text", final.GetProperty("responseId").GetString()); Equal("stop", final.GetProperty("rawStopReason").GetString());
            foreach (var (kind, expected) in new[] { ("toolcall_start", 2), ("toolcall_delta", 4), ("toolcall_end", 2) })
                Equal(expected, records.Count(record => Type(record) == "message_update" && Type(record.GetProperty("assistantMessageEvent")) == kind));
            var write = records.Single(record => Type(record) == "message_update" &&
                Type(record.GetProperty("assistantMessageEvent")) == "toolcall_end" &&
                record.GetProperty("assistantMessageEvent").GetProperty("toolCall").GetProperty("name").GetString() == "write")
                .GetProperty("assistantMessageEvent").GetProperty("toolCall");
            Equal("json-write", write.GetProperty("id").GetString());
            Equal(target, write.GetProperty("arguments").GetProperty("path").GetString());
            Equal(saved, write.GetProperty("arguments").GetProperty("content").GetString());
            var durableWrite = Context(log).LlmMessages.Where(message => message.Role == "assistant")
                .SelectMany(message => message.WireBody.Value.GetProperty("content").EnumerateArray())
                .Single(part => Type(part) == "toolCall" && part.GetProperty("name").GetString() == "write");
            Equal(durableWrite.GetRawText(), write.GetRawText());
        }
        foreach (var id in new[] { "json-read", "json-write" })
        {
            var ended = records.Single(record => Type(record) == "tool_execution_end" && record.GetProperty("toolCallId").GetString()!.StartsWith(id, StringComparison.Ordinal));
            Check(!ended.GetProperty("isError").GetBoolean(), "Allowed actual tool failed in JSON mode.");
            Check(ended.GetProperty("result").TryGetProperty("content", out _) && ended.GetProperty("result").TryGetProperty("details", out _), "Tool result properties disappeared.");
        }
        var afterSourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var afterScriptHash = SHA256.HashData(await File.ReadAllBytesAsync(files.Script));
        Check(sourceHash.SequenceEqual(afterSourceHash) && scriptHash.SequenceEqual(afterScriptHash), "JSON prompt changed source/script inputs.");
        var ancestor = log.ValidatedPrefix[^1].Entry.Id;
        await using (var reopened = await SessionLogStore.OpenAsync(files.Session)) Equal((long)log.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);

        await Script(files, Text(api, "resume " + Visible, "first user", first, seed, "Successfully wrote", "resume user", replaySaved));
        var resume = SuccessEvents(await Child(files, Prompt(files, api, "resume user")));
        var resumed = await ReadComplete(files);
        Check(resumed.OriginalBytes.AsSpan().StartsWith(log.OriginalBytes.AsSpan()), "Independent JSON resume rewrote prefix.");
        Correlate(resume, resumed, 2);
        await Script(files, Text(api, "branch " + Visible, "first user", first, "branch user", replaySaved));
        var branch = SuccessEvents(await Child(files, Prompt(files, api, "branch user", ["--leaf", ancestor])));
        var branched = await ReadComplete(files);
        Check(branched.OriginalBytes.AsSpan().StartsWith(resumed.OriginalBytes.AsSpan()), "JSON selected branch rewrote sibling history.");
        Equal(ancestor, branched.ValidatedPrefix[resumed.ValidatedPrefix.Length].Entry.ParentId);
        Correlate(branch, branched, 2);
        Check(!branch[^2].GetProperty("messages").GetRawText().Contains("resume user", StringComparison.Ordinal), "agent_end included old sibling history.");
        var context = Context(branched);
        Check(!context.LlmMessages.Any(message => message.WireBody.ToString().Contains("resume user", StringComparison.Ordinal)), "Selected context flattened a sibling.");
        var targetAfter = await File.ReadAllBytesAsync(target);
        Check(targetBytes.SequenceEqual(targetAfter), "Resume/branch replayed an old effect.");
        await using (var finalReopen = await SessionLogStore.OpenAsync(files.Session))
            Equal((long)branched.OriginalBytes.Length, finalReopen.Snapshot.CommittedByteLength);
        var unsupportedSource = files.In("nul-source.txt");
        await File.WriteAllTextAsync(unsupportedSource, unsupportedSeed, Utf8);
        var unsupportedBytes = await File.ReadAllBytesAsync(unsupportedSource);
        await Script(files, Tool(api, "read", "nul-read-json", new { path = unsupportedSource }, "read unsupported content"),
            Text(api, "handled unsupported file content", "Only UTF-8 text is supported; image, binary and other encoding support remains unfinished."));
        var unsupported = await Child(files, Prompt(files, api, "read unsupported content", ["--allow-read", unsupportedSource]));
        Equal(1, unsupported.Code); Equal("CompletedWithErrors", Diagnostic(unsupported.Error).GetProperty("code").GetString());
        var unsupportedEvents = Records(Utf8.GetString(unsupported.Output));
        var unsupportedEnd = unsupportedEvents.Single(record => Type(record) == "tool_execution_end");
        Check(unsupportedEnd.GetProperty("isError").GetBoolean(), "NUL read unexpectedly became successful text content.");
        Equal("UnsupportedContent", unsupportedEnd.GetProperty("result").GetProperty("details").GetProperty("fileOperation").GetProperty("code").GetString());
        Correlate(unsupportedEvents, await ReadComplete(files), 4);
        var unsupportedAfter = await File.ReadAllBytesAsync(unsupportedSource);
        Check(unsupportedBytes.SequenceEqual(unsupportedAfter), "Rejected NUL read changed the original source bytes.");
        var deniedTarget = files.In("denied.txt");
        await Script(files, Tool(api, "write", "denied-json", new { path = deniedTarget, content = "unallowed effect" }), Text(api, "handled tool failure"));
        var denied = await Child(files, Prompt(files, api, "deny action"));
        Equal(1, denied.Code); Equal("CompletedWithErrors", Diagnostic(denied.Error).GetProperty("code").GetString());
        var deniedEvents = Records(Utf8.GetString(denied.Output));
        Equal("agent_end", Type(deniedEvents[^2])); Equal("agent_settled", Type(deniedEvents[^1])); Check(!File.Exists(deniedTarget), "JSON denied tool performed a file effect.");
        Check(deniedEvents.Single(record => Type(record) == "tool_execution_end").GetProperty("isError").GetBoolean(), "Tool failure flag disappeared from JSON events.");
        await Script(files, Failure(api));
        var failed = await Child(files, Prompt(files, api, "provider failure"));
        Equal(1, failed.Code); Equal("ProviderFailed", Diagnostic(failed.Error).GetProperty("code").GetString());
        Check(!Utf8.GetString(failed.Error).Contains("private provider payload", StringComparison.Ordinal), "Provider payload leaked into fixed stderr.");
        var failureEvents = Records(Utf8.GetString(failed.Output));
        var lastAssistant = failureEvents.Last(record => Type(record) == "message_end").GetProperty("message");
        Equal("error", lastAssistant.GetProperty("stopReason").GetString());
        Check(lastAssistant.GetProperty("content").GetArrayLength() > 0, "JSON failure omitted known partial content.");
        Correlate(failureEvents, await ReadComplete(files), 2);
        await using var afterFailure = await SessionLogStore.OpenAsync(files.Session);
    }

    private static void Correlate(JsonElement[] records, SessionLogReadResult read, int expectedNewMessages)
    {
        Equal("session", Type(records[0])); Equal(read.Header!.WireBody.ToString(), records[0].GetRawText());
        Equal("agent_start", Type(records[1])); Equal("turn_start", Type(records[2])); Equal("agent_end", Type(records[^2]));
        // Source docs/json.md: agent_settled closes the session-level run after agent_end.
        Equal("""{"type":"agent_settled","aborted":false}""", records[^1].GetRawText());
        Equal(1, records.Count(record => Type(record) == "agent_start")); Equal(1, records.Count(record => Type(record) == "agent_end"));
        Check(records.All(record => Type(record) is not ("session_command_result" or "response")), "Settlement or RPC command output mixed into JSON events.");
        var ended = records.Where(record => Type(record) == "message_end").Select(record => record.GetProperty("message")).ToArray();
        Equal(expectedNewMessages, ended.Length);
        var durable = read.ValidatedPrefix.Where(record => record.Entry.Type == "message")
            .Select(record => record.Entry.WireBody.Value.GetProperty("message")).ToArray();
        Check(ended.All(message => durable.Any(saved => saved.GetRawText() == message.GetRawText())), "An authoritative message_end lacks an identical actual durable body.");
        var agentMessages = records[^2].GetProperty("messages").EnumerateArray().ToArray();
        Equal(ended.Length, agentMessages.Length);
        for (var index = 0; index < ended.Length; index++) Equal(ended[index].GetRawText(), agentMessages[index].GetRawText());
        Check(!records[^2].GetProperty("willRetry").GetBoolean(), "Native fixed-session agent_end invented retry authority.");
        foreach (var update in records.Where(record => Type(record) == "message_update"))
        {
            Check(!update.TryGetProperty("message", out _) && update.TryGetProperty("usage", out _), "JSON update contains a cumulative message or drops usage.");
            var delta = update.GetProperty("assistantMessageEvent");
            Check(!delta.TryGetProperty("partial", out _), "JSON delta includes cumulative partial state.");
            if (Type(delta) == "toolcall_start") Check(delta.TryGetProperty("id", out _) && delta.TryGetProperty("toolName", out _), "Tool start dropped stable identity.");
        }
        foreach (var turn in records.Where(record => Type(record) == "turn_end"))
        {
            Check(durable.Any(saved => saved.GetRawText() == turn.GetProperty("message").GetRawText()), "turn_end assistant differs from durable commit.");
            Check(turn.GetProperty("toolResults").EnumerateArray().All(message => durable.Any(saved => saved.GetRawText() == message.GetRawText())), "turn_end lost finalized tool results.");
        }
    }

    private static async Task SharedProjection()
    {
        using var files = new Files(); var transport = new ProbeTransport(); var executor = new ProbeExecutor();
        var session = await NativeSession(files, transport, executor);
        using var output = new StringWriter { NewLine = "\r\n" };
        var json = new SessionJsonEventOutput(session, output);
        var expected = new List<JsonData>(); var projector = new RpcAgentEventProjector();
        using var comparison = session.Subscribe(new Sink((observation, _) =>
        { expected.AddRange(projector.Project(observation, session.Snapshot, 0)); return ValueTask.CompletedTask; }));
        try
        {
            await json.StartAsync();
            var result = await Within(session.PromptAsync(User(Visible)));
            await session.WaitForIdleAsync(); json.ThrowIfFailed();
            Equal(AgentLoopStopReason.Completed, result.Reason);
            Equal(StopReason.Stop, PiWireJson.ReadMessage(result.Transcript[^1].WireBody.Value).StopReason);
            var records = Records(output.ToString());
            Equal(expected.Count + 2, records.Length);
            Equal("""{"type":"agent_settled","aborted":false}""", records[^1].GetRawText());
            for (var index = 0; index < expected.Count; index++) Equal(expected[index].ToString(), records[index + 1].GetRawText());
            Equal((long)Utf8.GetByteCount(output.ToString()), json.AcknowledgedBytes); Equal(records.Length, json.AcknowledgedRecords);
            var thinking = records.Single(record => Type(record) == "message_end" &&
                record.GetProperty("message").GetProperty("role").GetString() == "assistant" &&
                record.GetProperty("message").GetProperty("content").ValueKind == JsonValueKind.Array &&
                record.GetProperty("message").GetProperty("content").EnumerateArray().Any(content => Type(content) == "thinking"));
            Equal("signature\0\u6587", thinking.GetProperty("message").GetProperty("content")[0].GetProperty("thinkingSignature").GetString());
            Equal("1.00", thinking.GetProperty("message").GetProperty("opaque").GetProperty("scale").GetRawText());
            foreach (var kind in new[] { "tool_execution_update", "tool_execution_end" })
            {
                var value = records.First(record => Type(record) == kind).GetProperty(kind == "tool_execution_update" ? "partialResult" : "result");
                Equal("1.00", value.GetProperty("details").GetProperty("scale").GetRawText());
                Equal(JsonValueKind.Null, value.GetProperty("structuredContent").GetProperty("nil").ValueKind);
                Equal(Visible, value.GetProperty("structuredContent").GetProperty("text").GetString());
                Equal("usage-opaque", value.GetProperty("usage").GetProperty("kind").GetString());
                Equal("retained", value.GetProperty("extensionField").GetString());
            }
            var delta = records.Single(record => Type(record) == "message_update" && Type(record.GetProperty("assistantMessageEvent")) == "thinking_delta")
                .GetProperty("assistantMessageEvent");
            Equal(Visible, delta.GetProperty("delta").GetString()); Equal("1.00", delta.GetProperty("probe").GetProperty("scale").GetRawText());
            var toolMessage = result.Transcript.Single(message => message.Role == "toolResult").WireBody.Value;
            Check(!toolMessage.TryGetProperty("structuredContent", out _), "Programmatic output replaced canonical tool message.");
            Equal(2, transport.Requests.Count); Equal(1, executor.Cleanups);
            Correlate(records, await ReadAcknowledged(files), 4);
        }
        finally { session.Abort(); await json.DisposeAsync(); await session.DisposeAsync(); }
        await using var reopened = await SessionLogStore.OpenAsync(files.Session);
        Equal(new FileInfo(files.Session).Length, reopened.Snapshot.CommittedByteLength);
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(new RpcAgentEventProjector(new(MaximumPendingToolMessages: 0))));
        foreach (var raw in new[] { "{\"type\":\"probe\",\"nested\":{\"x\":1,}}", "{\"type\":\"probe\",\"nested\":{/*opaque*/\"x\":1}}" })
        {
            using var permissive = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            await Throws<JsonlTransportException>(() => Task.FromResult(JsonlRecordFormatter.Format(JsonData.FromElement(permissive.RootElement))));
        }
        await Throws<JsonlTransportException>(() => Task.FromResult(JsonlRecordFormatter.Format(JsonData.Parse("""{"type":"probe","opaque":"\uD800"}"""))));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(JsonlRecordFormatter.Format(JsonData.EmptyObject, new(MaximumJsonDepth: 0))));
    }

    private static async Task AdmissionAndLimits()
    {
        using var files = new Files();
        foreach (var command in new[] { "create", "inspect", "tree" })
        {
            var args = command == "create" ? Create(files, "openai-responses") : new[] { "session", command, "--session", files.Session };
            var result = await Child(files, args.Concat(["--output", "json"]).ToArray());
            Equal(2, result.Code); Equal(0, result.Output.Length); Equal("InvalidArguments", Diagnostic(result.Error).GetProperty("code").GetString());
            Check(!File.Exists(files.Session), "Invalid JSON mode acquired a new durable file.");
        }
        SuccessReport(await Child(files, Create(files, "openai-responses")));
        var before = await File.ReadAllBytesAsync(files.Session);
        await Script(files, Text("openai-responses", "not consumed"));
        foreach (var extra in new[] { new[] { "--output", "JSON" }, new[] { "--output", "json-unknown" },
            new[] { "--output", "json", "--output", "json" }, new[] { "--output", "" }, new[] { "--output" } })
        {
            var args = Prompt(files, "openai-responses", "private rejected input", output: false).Concat(extra).ToArray();
            var result = await Child(files, args);
            Equal(2, result.Code); Equal(0, result.Output.Length); Equal("InvalidArguments", Diagnostic(result.Error).GetProperty("code").GetString());
        }
        var after = await File.ReadAllBytesAsync(files.Session); Check(before.SequenceEqual(after), "Rejected JSON options changed storage.");
        foreach (var invalid in new[] { "\ud800", new string('x', 65_537) })
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Equal(2, await SessionCommands.RunAsync(Prompt(files, "openai-responses", invalid), output, error));
            Equal("", output.ToString());
        }
        foreach (var options in new[]
        {
            new SessionJsonEventOutputOptions(MaximumRecords: 1),
            new SessionJsonEventOutputOptions(MaximumRecordBytes: 257, MaximumTotalBytes: 257),
            new SessionJsonEventOutputOptions(MaximumJsonDepth: 1),
            new SessionJsonEventOutputOptions(MaximumRecordBytes: 257, MaximumTotalBytes: 16_777_216)
        })
        {
            using var bounded = new Files(); var transport = new ProbeTransport();
            var session = await NativeSession(bounded, transport, new ProbeExecutor());
            using var output = new StringWriter(); var json = new SessionJsonEventOutput(session, output, options);
            try
            {
                await json.StartAsync();
                await Throws<IOException>(() => Within(session.PromptAsync(User(Visible))));
            }
            catch (SessionJsonEventOutputException error) { Equal(SessionJsonEventOutputFailure.ResourceLimit, error.Failure); }
            finally { session.Abort(); await json.DisposeAsync(); await session.DisposeAsync(); }
            Equal(SessionJsonEventOutputFailure.ResourceLimit, json.Failure);
            Check(json.AcknowledgedBytes <= options.MaximumTotalBytes && json.AcknowledgedRecords <= options.MaximumRecords,
                "Output quotas exceeded known acknowledged prefix.");
            var read = await ReadComplete(bounded);
            Check(transport.Requests.Count <= 1, "Quota failure ran a follow-up provider request.");
            await using var reopened = await SessionLogStore.OpenAsync(bounded.Session);
            Equal((long)read.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
        }
        foreach (var fits in new[] { false, true })
        {
            using var bounded = new Files(); var session = await NativeSession(bounded, new ProbeTransport(), new ProbeExecutor());
            var header = JsonlRecordFormatter.Format(session.Snapshot.Log.Header.WireBody);
            var size = Utf8.GetByteCount(header); Check(size > header.Length, "UTF-8 boundary control did not contain actual multibyte Unicode.");
            var options = new SessionJsonEventOutputOptions(MaximumRecordBytes: size - (fits ? 0 : 1), MaximumTotalBytes: size);
            using var writer = new StringWriter(); var output = new SessionJsonEventOutput(session, writer, options);
            try
            {
                if (fits) { await output.StartAsync(); Equal(header, writer.ToString()); Equal((long)size, output.AcknowledgedBytes); }
                else { await Throws<SessionJsonEventOutputException>(() => output.StartAsync()); Equal("", writer.ToString()); Equal(0L, output.AcknowledgedBytes); }
            }
            finally { await output.DisposeAsync(); await session.DisposeAsync(); }
        }
    }

    private static async Task BackpressureAndOverlap()
    {
        foreach (var heldKind in new[] { "message_end", "tool_execution_update" })
        {
            using var files = new Files(); var transport = new ProbeTransport(); var executor = new ProbeExecutor { HoldCleanup = heldKind == "tool_execution_update" };
            var session = await NativeSession(files, transport, executor);
            using var writer = new GateWriter(record => Type(record) == heldKind &&
                (heldKind != "message_end" || record.GetProperty("message").GetProperty("role").GetString() == "toolResult"), flush: heldKind == "message_end");
            var output = new SessionJsonEventOutput(session, writer); await output.StartAsync();
            var running = session.PromptAsync(User("held request"));
            try
            {
                await Within(writer.Entered.Task);
                Check(!running.IsCompleted && !session.WaitForIdleAsync().IsCompleted, "Provider/run escaped held event delivery.");
                Equal(1, transport.Requests.Count);
                if (heldKind == "tool_execution_update")
                {
                    await Within(executor.ReportsReturned.Task); await Within(executor.CleanupEntered.Task);
                    Equal(0, executor.Cleanups);
                    Check(!executor.CleanupCompleted.Task.IsCompleted && !running.IsCompleted && !session.WaitForIdleAsync().IsCompleted,
                        "Report return was incorrectly treated as actual executor cleanup settlement.");
                    executor.CleanupRelease.TrySetResult(); await Within(executor.CleanupCompleted.Task);
                    Equal(2, executor.Reports); Equal(1, executor.Cleanups);
                    Check(!writer.Release.Task.IsCompleted && !running.IsCompleted && !session.WaitForIdleAsync().IsCompleted,
                        "Actual executor cleanup escaped the held JSON output ownership.");
                    Check(session.Snapshot.Context.LlmMessages[^1].Role == "assistant", "Undelivered progress changed canonical history.");
                }
                else
                {
                    var read = await ReadAcknowledged(files);
                    var message = read.ValidatedPrefix[^1].Entry.WireBody.Value.GetProperty("message");
                    Equal("toolResult", message.GetProperty("role").GetString());
                    Equal(session.Snapshot.Context.Messages[^1].WireBody.ToString(), message.GetRawText());
                }
                writer.Release.TrySetResult();
                await Within(running); await session.WaitForIdleAsync(); output.ThrowIfFailed();
                Equal(2, transport.Requests.Count); Equal(1, executor.Cleanups);
                Equal(2, Records(writer.Written.ToString()).Count(record => Type(record) == "tool_execution_update"));
                Equal(0, writer.Disposals);
            }
            finally
            {
                executor.CleanupRelease.TrySetResult(); writer.Release.TrySetResult(); session.Abort(); await Join(running);
                await output.DisposeAsync(); await session.DisposeAsync();
            }
            await using var reopened = await SessionLogStore.OpenAsync(files.Session);
            Equal(new FileInfo(files.Session).Length, reopened.Snapshot.CommittedByteLength);
        }
    }

    private static async Task OutputFaults()
    {
        if (OperatingSystem.IsWindows()) await RedirectedFileOwnership();
        foreach (var flush in new[] { false, true })
        {
            using var files = new Files(); var transport = new ProbeTransport(); var executor = new ProbeExecutor();
            var session = await NativeSession(files, transport, executor);
            using var writer = new GateWriter(record => Type(record) == "message_end" &&
                record.GetProperty("message").GetProperty("role").GetString() == "toolResult", flush, fail: true);
            var output = new SessionJsonEventOutput(session, writer); await output.StartAsync();
            var running = session.PromptAsync(User("output failure"));
            try
            {
                await Within(writer.Entered.Task);
                var committed = (await ReadAcknowledged(files)).OriginalBytes.ToArray();
                writer.Release.TrySetResult(); await Throws<IOException>(() => Within(running));
                Equal(SessionJsonEventOutputFailure.OutputFailed, output.Failure); Equal(1, transport.Requests.Count); Equal(1, executor.Cleanups);
                var writes = writer.Writes; var flushes = writer.Flushes;
                await Throws<SessionJsonEventOutputException>(() => output.EmitAsync(new AgentLoopStarted(), default).AsTask());
                Equal(writes, writer.Writes); Equal(flushes, writer.Flushes);
                await output.DisposeAsync(); await session.DisposeAsync();
                var after = await File.ReadAllBytesAsync(files.Session); Check(after.AsSpan().StartsWith(committed), "Output failure removed committed effects.");
                Equal(0, writer.Disposals); Check(!writer.Written.ToString().Contains("private writer payload", StringComparison.Ordinal), "Private exception entered stdout.");
                await using var reopened = await SessionLogStore.OpenAsync(files.Session);
                Equal((long)after.Length, reopened.Snapshot.CommittedByteLength);
            }
            finally { writer.Release.TrySetResult(); session.Abort(); await Join(running); await output.DisposeAsync(); await session.DisposeAsync(); }
        }
        // Every DTO fits the existing SSE/mapper 65,536-character admission cap. The full 128 KiB output is unchanged.
        var largeText = new string('x', 131_072);
        var largeTurn = BoundedResponsesText(largeText);
        using (var files = new Files())
        {
            SuccessReport(await Child(files, Create(files, "openai-responses")));
            await Script(files, largeTurn);
            var records = SuccessEvents(await Child(files, Prompt(files, "openai-responses", "large output positive control")));
            var assistant = records.Last(record => Type(record) == "message_end").GetProperty("message");
            Equal("stop", assistant.GetProperty("stopReason").GetString());
            Equal(largeText, string.Concat(assistant.GetProperty("content").EnumerateArray().Select(part => part.GetProperty("text").GetString())));
            Equal(largeText, string.Concat(records.Where(record => Type(record) == "message_update" &&
                Type(record.GetProperty("assistantMessageEvent")) == "text_delta")
                .Select(record => record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString())));
            Correlate(records, await ReadComplete(files), 2);
            await using var reopened = await SessionLogStore.OpenAsync(files.Session);
        }
        // Actual OS pipe close, rather than an authored writer exception, must release the command's live writer lease.
        using (var files = new Files())
        {
            SuccessReport(await Child(files, Create(files, "openai-responses")));
            await Script(files, largeTurn);
            var sourceHeader = (await ReadComplete(files)).Header!;
            var start = Start(files, Prompt(files, "openai-responses", "broken pipe"));
            using var process = new Process { StartInfo = start }; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Check(process.Start(), "Broken-pipe CLI did not start.");
            var error = Read(process.StandardError.BaseStream, deadline.Token);
            try
            {
                var header = await OneLine(process.StandardOutput.BaseStream, deadline.Token);
                process.StandardOutput.Dispose();
                await process.WaitForExitAsync(deadline.Token);
                var stderr = await error;
                var observed = new ChildResult(process.ExitCode, header, stderr);
                Check(Type(JsonData.Parse(Utf8.GetString(header)).Value) == "session",
                    "Compiled broken-pipe opening header is not the actual session header: " + ChildSummary(observed));
                var admittedHeader = new SessionEntryCodec().ParseUtf8(header);
                Check(admittedHeader.IsHeader && admittedHeader.WireBody.ToString() == sourceHeader.WireBody.ToString(),
                    "Opening current-version header differs from the actual durable source header: " + ChildSummary(observed));
                Check(process.ExitCode == 1, "Broken-pipe child did not fail after actual cleanup: " + ChildSummary(observed));
                var diagnostic = Diagnostic(stderr); Equal("OutputFailed", diagnostic.GetProperty("code").GetString());
                Check(diagnostic.GetProperty("effectsMayHaveCompleted").GetBoolean(), "Broken pipe falsely denied possible admitted effects.");
                await ReadComplete(files);
                await using var reopened = await SessionLogStore.OpenAsync(files.Session);
                Equal(new FileInfo(files.Session).Length, reopened.Snapshot.CommittedByteLength);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                try { await error; } catch (Exception) { }
            }
        }
        // Command diagnostics are fixed and JSON mode does not append a success report after a failing borrowed writer.
        using (var files = new Files())
        {
            SuccessReport(await Child(files, Create(files, "openai-responses")));
            await Script(files, Text("openai-responses", "not reached"));
            using var writer = new GateWriter(_ => true, fail: true); using var error = new StringWriter();
            var pending = SessionCommands.RunAsync(Prompt(files, "openai-responses", "command fault"), writer, error);
            try
            {
                await Within(writer.Entered.Task); writer.Release.TrySetResult(); Equal(1, await Within(pending));
                Equal("OutputFailed", JsonData.Parse(error.ToString()).Value.GetProperty("code").GetString());
                Check(!error.ToString().Contains("private writer payload", StringComparison.Ordinal), "Command exposed writer exception.");
                Equal(1, writer.Writes); Equal(0, writer.Flushes); Equal(0, writer.Disposals);
                await using var reopened = await SessionLogStore.OpenAsync(files.Session);
            }
            finally { writer.Release.TrySetResult(); await Join(pending); }
        }
    }

    private static async Task CompletionsOutputLifecycle()
    {
        const string api = "openai-completions";
        const string saved = "held effect\0\u6587\U0001f642\n";
        var final = "after flush " + Visible;
        foreach (var stage in new[] { "assistant-flush", "result-flush", "result-write-fault", "result-flush-fault" })
        {
            using var files = new Files(); var target = files.In("held-effect.txt");
            SuccessReport(await Child(files, Create(files, api)));
            await Script(files, Tool(api, "write", "held-completions", new { path = target, content = saved }),
                Text(api, final, "Successfully wrote"));
            var holdAssistant = stage == "assistant-flush"; var fail = stage.EndsWith("fault", StringComparison.Ordinal);
            using var writer = new GateWriter(record => Type(record) == "message_end" &&
                record.GetProperty("message").GetProperty("role").GetString() == (holdAssistant ? "assistant" : "toolResult"),
                flush: stage != "result-write-fault", fail: fail);
            using var error = new StringWriter();
            var pending = SessionCommands.RunAsync(Prompt(files, api, "held command", ["--allow-write", target]), writer, error);
            try
            {
                await Within(writer.Entered.Task);
                Check(!pending.IsCompleted, "Actual Completions command escaped an admitted output operation.");
                var acknowledged = await ReadAcknowledged(files); var prefix = acknowledged.OriginalBytes.ToArray();
                Check(acknowledged.SourceComplete && acknowledged.Status == SessionLogReadStatus.Complete && acknowledged.Diagnostics.IsEmpty,
                    "Held JSON delivery lacked a complete acknowledged durable prefix.");
                var history = Context(acknowledged).LlmMessages;
                Equal(holdAssistant ? "assistant" : "toolResult", history[^1].Role);
                Equal(1, history.Count(message => message.Role == "assistant"));
                if (holdAssistant)
                {
                    Equal("toolUse", history[^1].WireBody.Value.GetProperty("stopReason").GetString());
                    Check(!File.Exists(target), "Tool effect bypassed the actual assistant output flush barrier.");
                    Check(!Records(writer.Written.ToString()).Any(record => Type(record) == "tool_execution_start"),
                        "Tool execution started before assistant delivery settled.");
                }
                else
                {
                    var bytes = await File.ReadAllBytesAsync(target);
                    Check(bytes.SequenceEqual(Utf8.GetBytes(saved)), "Known completed output lost inert NUL/Unicode bytes.");
                    Check(!history[^1].WireBody.Value.GetProperty("isError").GetBoolean(), "Allowed effect was not durably successful.");
                }
                var heldWrites = writer.Writes; var heldFlushes = writer.Flushes;
                writer.Release.TrySetResult(); Equal(fail ? 1 : 0, await Within(pending));
                var settled = await ReadComplete(files);
                Check(settled.OriginalBytes.AsSpan().StartsWith(prefix), "Output settlement removed acknowledged work.");
                var settledBytes = await File.ReadAllBytesAsync(target);
                Check(settledBytes.SequenceEqual(Utf8.GetBytes(saved)), "Output fault or release replayed/changed the completed effect.");
                if (fail)
                {
                    var diagnostic = JsonData.Parse(error.ToString()).Value;
                    Equal("OutputFailed", diagnostic.GetProperty("code").GetString());
                    Check(diagnostic.GetProperty("effectsMayHaveCompleted").GetBoolean(), "Output fault denied already acknowledged effects.");
                    Check(!error.ToString().Contains("private writer payload", StringComparison.Ordinal), "Private output fault leaked.");
                    Equal(heldWrites, writer.Writes); Equal(heldFlushes, writer.Flushes);
                    var afterFault = Context(settled).LlmMessages;
                    Equal(1, afterFault.Count(message => message.Role == "assistant"));
                    Check(!afterFault.Any(message => message.Role == "assistant" &&
                        message.WireBody.Value.GetProperty("content").EnumerateArray().Any(part => Type(part) == "text" &&
                            part.GetProperty("text").GetString() == final)), "Poisoned output published a successful subsequent assistant.");
                }
                else
                {
                    Equal("", error.ToString()); var records = Records(writer.Written.ToString());
                    Correlate(records, settled, 4);
                    Equal(final, records.Last(record => Type(record) == "message_end").GetProperty("message")
                        .GetProperty("content")[0].GetProperty("text").GetString());
                }
                Equal(0, writer.Disposals);
                await using var reopened = await SessionLogStore.OpenAsync(files.Session);
                Equal((long)settled.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
            }
            finally { writer.Release.TrySetResult(); await Join(pending); }
        }

        // The full output spans admitted bounded DTOs; no line, mapper or output quota is raised.
        var largeText = string.Concat(Enumerable.Repeat("large\0\u6587\U0001f642\r\n", 8192));
        var turn = BoundedCompletionsText(largeText);
        using (var files = new Files())
        {
            SuccessReport(await Child(files, Create(files, api))); await Script(files, turn);
            var records = SuccessEvents(await Child(files, Prompt(files, api, "intact output control")));
            var finalMessage = records.Last(record => Type(record) == "message_end").GetProperty("message");
            Equal("stop", finalMessage.GetProperty("stopReason").GetString());
            Equal(largeText, finalMessage.GetProperty("content")[0].GetProperty("text").GetString());
            Equal(largeText, string.Concat(records.Where(record => Type(record) == "message_update" &&
                Type(record.GetProperty("assistantMessageEvent")) == "text_delta")
                .Select(record => record.GetProperty("assistantMessageEvent").GetProperty("delta").GetString())));
            Correlate(records, await ReadComplete(files), 2);
            await using var reopened = await SessionLogStore.OpenAsync(files.Session);
        }
        using (var files = new Files())
        {
            SuccessReport(await Child(files, Create(files, api))); await Script(files, turn);
            var sourceHeader = (await ReadComplete(files)).Header!;
            using var process = new Process { StartInfo = Start(files, Prompt(files, api, "closed pipe")) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Check(process.Start(), "Compiled Completions pipe child did not start.");
            var error = Read(process.StandardError.BaseStream, deadline.Token);
            try
            {
                var header = await OneLine(process.StandardOutput.BaseStream, deadline.Token);
                process.StandardOutput.Dispose();
                await process.WaitForExitAsync(deadline.Token); var stderr = await error;
                var observed = new ChildResult(process.ExitCode, header, stderr);
                var admitted = new SessionEntryCodec().ParseUtf8(header);
                Check(admitted.IsHeader && admitted.WireBody.ToString() == sourceHeader.WireBody.ToString(),
                    "Completions pipe header differs from the durable source: " + ChildSummary(observed));
                Check(process.ExitCode == 1, "Actual Completions closed pipe returned success: " + ChildSummary(observed));
                var diagnostic = Diagnostic(stderr); Equal("OutputFailed", diagnostic.GetProperty("code").GetString());
                Check(diagnostic.GetProperty("effectsMayHaveCompleted").GetBoolean(), "Closed pipe denied possible admitted effects.");
                var closed = await ReadComplete(files);
                await using var reopened = await SessionLogStore.OpenAsync(files.Session);
                Equal((long)closed.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                try { await error; } catch (Exception) { }
            }
        }
    }

    private static object BoundedCompletionsText(string text)
    {
        var events = new List<JsonElement>(); var offset = 0;
        while (offset < text.Length)
        {
            var length = Math.Min(4096, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            var next = offset + length;
            var authored = JsonSerializer.SerializeToElement(SessionCommandTests.CompletionsText(text.Substring(offset, length), finish: next == text.Length));
            events.AddRange(authored.GetProperty("events").EnumerateArray().Select(value => value.Clone())); offset = next;
        }
        Check(events.Count <= 256 && events.All(value => value.GetRawText().Length < 65_530),
            "Completions pipe fixture exceeded inherited literal chunk admission.");
        return new { events = events.ToArray() };
    }

    private static async Task RedirectedFileOwnership()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows redirected file qualification is required.");
        using var files = new Files(); var target = files.In("redirected-output.txt");
        await using var borrowed = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.None);
        var original = borrowed.SafeFileHandle;
        await using var redirected = StandardOutputStream.DuplicateRedirectedOutput(original);
        var owned = redirected.SafeFileHandle;
        Check(owned.DangerousGetHandle() != original.DangerousGetHandle() && !redirected.IsAsync,
            "Redirected output did not retain an owned duplicate of the actual synchronous file profile.");
        await using (var writer = new Utf8StreamTextWriter(redirected))
        {
            using var caller = new CancellationTokenSource(); caller.Cancel();
            await Throws<OperationCanceledException>(() => writer.WriteAsync("pre-canceled output".AsMemory(), caller.Token));
            Equal(0L, borrowed.Length);
            await writer.WriteAsync((Visible + "\n").AsMemory()); await writer.FlushAsync();
        }
        Check(redirected.CanWrite, "Borrowed UTF-8 writer closed the owned factory stream.");
        await redirected.DisposeAsync();
        Check(owned.IsClosed && !original.IsClosed && borrowed.CanWrite, "Factory disposal closed original output or leaked its duplicate.");
        borrowed.Seek(0, SeekOrigin.End);
        var suffix = Utf8.GetBytes("original remains writable\0\n");
        await borrowed.WriteAsync(suffix); await borrowed.FlushAsync();
        var expected = Utf8.GetBytes(Visible + "\n").Concat(suffix).ToArray();
        var observed = new byte[expected.Length];
        // Both handles must permit the other's access while the original writer stays owned and open.
        await using (var readback = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.Asynchronous))
        {
            Equal((long)expected.Length, readback.Length);
            await readback.ReadExactlyAsync(observed);
        }
        Check(observed.SequenceEqual(expected),
            "Actual redirected file changed UTF-8/NUL/control bytes or borrowed handle ownership.");
        using var invalid = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        await Throws<IOException>(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return Task.FromResult(StandardOutputStream.DuplicateRedirectedOutput(invalid));
        });
        await borrowed.DisposeAsync();
        await Throws<IOException>(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return Task.FromResult(StandardOutputStream.DuplicateRedirectedOutput(original));
        });
    }

    private static async Task CancellationAndDisposal()
    {
        using (var files = new Files())
        using (var cancellation = new CancellationTokenSource())
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            cancellation.Cancel(); Equal(1, await SessionCommands.RunAsync(Prompt(files, "openai-responses", "pre-cancel"), output, error, cancellation.Token));
            Equal("", output.ToString()); Equal("Canceled", JsonData.Parse(error.ToString()).Value.GetProperty("code").GetString());
            Check(!File.Exists(files.Session), "Pre-cancel acquired a session path.");
        }
        using (var files = new Files())
        {
            var transport = new ProbeTransport(); var session = await NativeSession(files, transport, new ProbeExecutor());
            using var writer = new GateWriter(_ => true); var output = new SessionJsonEventOutput(session, writer);
            using var cancellation = new CancellationTokenSource();
            var starting = output.StartAsync(cancellation.Token);
            try
            {
                await Within(writer.Entered.Task);
                var first = output.DisposeAsync().AsTask(); var second = output.DisposeAsync().AsTask();
                Check(ReferenceEquals(first, second) && !first.IsCompleted, "Concurrent disposal did not share/join actual pending write.");
                cancellation.Cancel();
                Check(!starting.IsCompleted && !first.IsCompleted, "Caller cancellation abandoned a trusted pending writer.");
                writer.Release.TrySetResult(); await Throws<OperationCanceledException>(() => Within(starting)); await Within(first);
                Equal(SessionJsonEventOutputFailure.Canceled, output.Failure); Equal(0, writer.Disposals); Equal(0, transport.Requests.Count);
            }
            finally { writer.Release.TrySetResult(); await Join(starting); await output.DisposeAsync(); await session.DisposeAsync(); }
            await using var reopened = await SessionLogStore.OpenAsync(files.Session);
        }
        using (var files = new Files())
        {
            SuccessReport(await Child(files, Create(files, "openai-responses")));
            await Script(files, Text("openai-responses", "canceled stream"));
            using var writer = new GateWriter(record => Type(record) == "message_update");
            using var error = new StringWriter(); using var cancellation = new CancellationTokenSource();
            var pending = SessionCommands.RunAsync(Prompt(files, "openai-responses", "cancel actual delivery"), writer, error, cancellation.Token);
            try
            {
                await Within(writer.Entered.Task); cancellation.Cancel();
                Check(!pending.IsCompleted, "Command cancellation skipped admitted actual output cleanup.");
                writer.Release.TrySetResult(); Equal(1, await Within(pending));
                Equal("Canceled", JsonData.Parse(error.ToString()).Value.GetProperty("code").GetString());
                Check(Records(writer.Written.ToString()).All(record => Type(record) != "session_command_result"), "Cancellation mixed settlement into JSON stdout.");
                Equal(0, writer.Disposals); await ReadComplete(files);
                await using var reopened = await SessionLogStore.OpenAsync(files.Session);
            }
            finally { writer.Release.TrySetResult(); cancellation.Cancel(); await Join(pending); }
        }
    }

    private static Task<PersistentAgentSession> NativeSession(Files files, ProbeTransport transport, ProbeExecutor executor)
    {
        var raw = JsonSerializer.Serialize(new { type = "session", version = 3, id = "json-session",
            timestamp = "2026-10-01T00:00:00.000Z", cwd = files.Root, unknown = new { text = Visible, nil = (string?)null } });
        // Keep actual Unicode in retained JSON syntax so byte quotas cannot accidentally become UTF-16 quotas.
        var header = new SessionEntryCodec().Parse(raw.Replace("\\u6587", "\u6587", StringComparison.Ordinal)
            .Replace("\\uD83D\\uDE42", "\U0001f642", StringComparison.Ordinal));
        return PersistentAgentSession.CreateAsync(files.Session, header,
            new(Model, transport, [new("probe", executor)]), files.Clock, files.NextId);
    }
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 100 })));
    private sealed class ProbeExecutor : IToolExecutor
    {
        public int Reports, Cleanups;
        public bool HoldCleanup { get; init; }
        public TaskCompletionSource ReportsReturned { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource CleanupRelease { get; } = Gate();
        public TaskCompletionSource CleanupCompleted { get; } = Gate();
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(Value());
        public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, ToolProgressCallback progress, CancellationToken token)
        {
            try
            {
                await progress(Value(), token); Reports++;
                await progress(Value(), token); Reports++;
                ReportsReturned.TrySetResult(); return Value();
            }
            finally
            {
                CleanupEntered.TrySetResult(); if (HoldCleanup) await CleanupRelease.Task;
                Cleanups++; CleanupCompleted.TrySetResult();
            }
        }
        private static ToolResult Value() => new ToolResult([new(Visible)], JsonData.Parse("""{"scale":1.00,"nil":null}"""))
        { StructuredContent = JsonData.Parse(JsonSerializer.Serialize(new { text = Visible, nil = (string?)null })), Usage = JsonData.Parse("""{"kind":"usage-opaque"}""") }
            .WithProperty("extensionField", JsonData.Parse("\"retained\""));
    }
    private sealed class ProbeTransport : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var tool = Requests.Count == 0; Requests.Add(request);
            var fields = JsonFields.Empty.Set("opaque", JsonData.Parse("""{"scale":1.00,"nil":null}"""));
            var call = new ToolCallContent("call-probe", "probe", JsonData.Parse("""{"nil":null,"scale":1.00}"""));
            var thinking = new ThinkingContent(Visible, JsonFields.Empty.Set("thinkingSignature", JsonData.Parse(JsonSerializer.Serialize("signature\0\u6587"))));
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 200,
                tool ? [call] : [thinking, new TextContent(Visible)],
                new(7, 3, 2, 1, 13, new(0.7m, 0.3m, 0.2m, 0.1m, 1.3m)), tool ? StopReason.ToolUse : StopReason.Stop, fields);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (tool)
            {
                yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject });
                yield return new ToolCallDelta(0, "{\"nil\":null,"); yield return new ToolCallEnded(0, call);
            }
            else
            {
                yield return new ThinkingStarted(0, new(""));
                yield return new ThinkingDelta(0, Visible, JsonFields.Empty.Set("probe", JsonData.Parse("""{"scale":1.00,"nil":null}""")));
                yield return new ThinkingEnded(0, Visible, thinking.ExtraProperties);
                yield return new TextStarted(1, new("")); yield return new TextDelta(1, Visible); yield return new TextEnded(1, Visible);
            }
            await Task.CompletedTask; yield return new StreamDone(final.StopReason, final);
        }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> observe) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => observe(observation, token); }

    private sealed class GateWriter(Func<JsonElement, bool> shouldHold, bool flush = false, bool fail = false) : TextWriter
    {
        private bool _held, _holdFlush;
        public override Encoding Encoding => Utf8;
        public StringBuilder Written { get; } = new();
        public TaskCompletionSource Entered { get; } = Gate();
        public TaskCompletionSource Release { get; } = Gate();
        public int Writes, Flushes, Disposals;
        public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            Writes++; var text = buffer.ToString(); var record = JsonData.Parse(text).Value;
            if (!_held && shouldHold(record))
            {
                _held = true;
                if (flush) _holdFlush = true;
                else { Entered.TrySetResult(); await Release.Task; token.ThrowIfCancellationRequested(); if (fail) throw new IOException("private writer payload"); }
            }
            Written.Append(text);
        }
        public override async Task FlushAsync(CancellationToken token)
        {
            Flushes++;
            if (!_holdFlush) return;
            _holdFlush = false; Entered.TrySetResult(); await Release.Task; token.ThrowIfCancellationRequested();
            if (fail) throw new IOException("private writer payload");
        }
        protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
    }
    private static string[] Create(Files files, string api) => ["session", "create", "--session", files.Session, "--workspace", files.Root, "--offline-api", api];
    private static string[] Prompt(Files files, string api, string message, string[]? extra = null, string command = "resume", bool output = true)
    {
        var args = new List<string> { "session", command, "--session", files.Session, "--workspace", files.Root,
            "--offline-api", api, "--offline-script", files.Script, "--message", message };
        if (output) args.AddRange(["--output", "json"]); args.AddRange(extra ?? []); return args.ToArray();
    }
    private static async Task Script(Files files, params object[] turns) =>
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
    private static object Text(string api, string text, params string[] required)
    {
        if (api == "anthropic-messages") return SessionCommandTests.AnthropicText(text, required: required);
        if (api == "openai-completions") return SessionCommandTests.CompletionsText(text, required: required);
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 3, item = new { type = "message", id = "msg-json", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 3, item_id = "msg-json", delta = text },
            new { type = "response.output_item.done", output_index = 3, item = new { type = "message", id = "msg-json", content = new[] { new { type = "output_text", text } } } }, Completed()
        } };
    }
    private static object BoundedResponsesText(string text)
    {
        var events = new List<object>();
        for (var offset = 0; offset < text.Length; offset += 16_384)
        {
            var chunk = text.Substring(offset, Math.Min(16_384, text.Length - offset));
            var index = 3 + offset / 16_384; var id = "msg-json-bounded-" + index;
            events.Add(new { type = "response.output_item.added", output_index = index,
                item = new { type = "message", id, content = Array.Empty<object>() } });
            events.Add(new { type = "response.output_text.delta", output_index = index, item_id = id, delta = chunk });
            events.Add(new { type = "response.output_item.done", output_index = index,
                item = new { type = "message", id, content = new[] { new { type = "output_text", text = chunk } } } });
        }
        events.Add(Completed());
        Check(events.Count <= 256 && events.All(value => JsonSerializer.Serialize(value).Length < 65_536),
            "Broken-pipe control exceeded the existing offline provider DTO profile before output delivery.");
        return new { events = events.ToArray() };
    }
    private static object Tool(string api, string name, string call, object args, params string[] required)
    {
        if (api == "anthropic-messages") return SessionCommandTests.AnthropicTool(name, call, args, required);
        if (api == "openai-completions") return SessionCommandTests.CompletionsTool(name, call, args, required);
        var id = "fc-" + call; var json = JsonSerializer.Serialize(args);
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 9, item = new { type = "function_call", id, call_id = call, name, arguments = "" } },
            new { type = "response.function_call_arguments.delta", output_index = 9, item_id = id, delta = json[..(json.Length / 2)] },
            new { type = "response.output_item.done", output_index = 9, item = new { type = "function_call", id, call_id = call, name, arguments = json } }, Completed()
        } };
    }
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(),
        usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private static object Failure(string api)
    {
        if (api == "openai-completions") return SessionCommandTests.CompletionsText("known partial failure", finish: false);
        if (api == "anthropic-messages") return new { events = new object[]
        {
            new { type = "message_start", message = new { id = "failed-json", role = "assistant", model = "pisharp-offline-session",
                content = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 0 } } },
            new { type = "content_block_start", index = 4, content_block = new { type = "text", text = "" } },
            new { type = "content_block_delta", index = 4, delta = new { type = "text_delta", text = "known partial failure" } },
            new { type = "error", error = new { type = "provider_failure", message = "private provider payload" } }
        } };
        return new { events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "failed-json", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = "failed-json", delta = "known partial failure" },
            new { type = "response.failed", response = new { status = "failed", error = new { message = "private provider payload" } } }
        } };
    }
    private static SessionContextProjection Context(SessionLogReadResult read) => new SessionContextProjector().Project(
        read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray(), read.ValidatedPrefix[^1].Entry.Id);
    private static async Task<SessionLogReadResult> ReadComplete(Files files)
    {
        var read = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.Diagnostics.IsEmpty, "JSON command damaged acknowledged history.");
        Equal(new FileInfo(files.Session).Length, (long)read.OriginalBytes.Length); return read;
    }
    private static async Task<SessionLogReadResult> ReadAcknowledged(Files files)
    {
        await using var stream = new FileStream(files.Session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        return await new SessionLogReader().ReadAsync(stream);
    }
    private sealed record ChildResult(int Code, byte[] Output, byte[] Error);
    private static ProcessStartInfo Start(Files files, string[] args)
    {
        var start = new ProcessStartInfo(_host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["OPENAI_API_KEY"] = "authored-unused-environment-value";
        start.Environment["ANTHROPIC_API_KEY"] = "authored-unused-environment-value";
        start.ArgumentList.Add(_cli); foreach (var arg in args) start.ArgumentList.Add(arg); return start;
    }
    private static async Task<ChildResult> Child(Files files, string[] args)
    {
        using var process = new Process { StartInfo = Start(files, args) }; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Check(process.Start(), "Compiled JSON CLI did not start.");
        var output = Read(process.StandardOutput.BaseStream, deadline.Token); var error = Read(process.StandardError.BaseStream, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, await output, await error); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            foreach (var task in new[] { output, error }) try { await task; } catch (Exception) { }
        }
    }
    private static async Task<byte[]> Read(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token); if (count == 0) return bytes.ToArray();
            Check(count <= 4_194_304 - bytes.Length, "JSON child output exceeds authored bound."); bytes.Write(buffer, 0, count);
        }
    }
    private static async Task<byte[]> OneLine(Stream stream, CancellationToken token)
    {
        using var line = new MemoryStream(); var one = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(one, token); Check(count == 1, "CLI pipe ended before its opening header.");
            line.WriteByte(one[0]); if (one[0] == (byte)'\n') return line.ToArray(); Check(line.Length < 4096, "Authored CLI header exceeds bound.");
        }
    }
    private static JsonElement[] SuccessEvents(ChildResult result)
    {
        Check(result.Code == 0 && result.Error.Length == 0, "Expected successful compiled JSON events: " + ChildSummary(result));
        return Records(Utf8.GetString(result.Output));
    }
    private static void SuccessReport(ChildResult result)
    {
        Check(result.Code == 0 && result.Error.Length == 0, "Expected successful compiled report: " + ChildSummary(result));
        Equal("completed", Diagnostic(result.Output).GetProperty("status").GetString());
    }
    private static string ChildSummary(ChildResult result)
    {
        // Report only fixed command classifications and byte receipts. Never echo command/input/script/exception payloads.
        var code = "none"; var firstType = "none"; bool? effects = null;
        try
        {
            var diagnostic = JsonData.Parse(Utf8.GetString(result.Error)).Value;
            var rawCode = diagnostic.GetProperty("code").GetString();
            code = rawCode is "InvalidArguments" or "InvalidPath" or "WorkspaceMissing" or "WorkspaceMismatch" or "ReservedTarget" or
                "InvalidScript" or "ResourceLimit" or "CommandFailed" or "OfflineProviderMismatch" or "InvalidBashConfiguration" or
                "UnsupportedBashPlatform" or "OutputFailed" or "ProjectionFailed" or "InvalidState" or "Disposed" or "Canceled" or
                "ProviderFailed" or "CompletedWithErrors" or "StandardOutputUnavailable" ? rawCode : "unrecognized";
            if (diagnostic.TryGetProperty("effectsMayHaveCompleted", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                effects = value.GetBoolean();
        }
        catch (Exception) { if (result.Error.Length != 0) code = "unrecognized"; }
        try
        {
            var text = Utf8.GetString(result.Output); var end = text.IndexOf('\n');
            var type = Type(JsonData.Parse(end < 0 ? text : text[..end]).Value);
            firstType = type is "session" or "session_command_result" or "agent_start" or "response" ? type : "unrecognized";
        }
        catch (Exception) { if (result.Output.Length != 0) firstType = "unrecognized"; }
        return JsonSerializer.Serialize(new { exitCode = result.Code, stdoutBytes = result.Output.Length, stderrBytes = result.Error.Length,
            fixedStderrCode = code, effectsMayHaveCompleted = effects, firstStdoutRecordType = firstType,
            stderrSha256 = Convert.ToHexStringLower(SHA256.HashData(result.Error)) });
    }
    private static JsonElement Diagnostic(byte[] bytes)
    { var records = Records(Utf8.GetString(bytes)); Equal(1, records.Length); return records[0]; }
    private static JsonElement[] Records(string text)
    {
        Check(text.EndsWith('\n') && !text.Contains('\r'), "JSON output must use complete LF records without raw carriage returns.");
        var lines = text.Split('\n'); Check(lines[^1] == "" && lines.Take(lines.Length - 1).All(line => line.Length != 0), "JSON output has a blank or partial record.");
        return lines.Take(lines.Length - 1).Select(line => JsonData.Parse(line).Value).ToArray();
    }
    private static string Type(JsonElement value) => value.GetProperty("type").GetString()!;
    private sealed class Files : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private int _id; private long _clock = 1000;
        public string Root { get; }
        public string Session => In("session.jsonl"); public string Script => In("wire.json");
        public string In(string name) => Path.Combine(Root, name);
        public string NextId() => "json-" + Interlocked.Increment(ref _id); public long Clock() => Interlocked.Increment(ref _clock);
        public Files() { Root = Path.Combine(_parent, "pisharp-json-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            Check(target == Root && Path.GetDirectoryName(target) == _parent && Path.GetFileName(target).StartsWith("pisharp-json-cli-", StringComparison.Ordinal), "Refusing unowned recursive cleanup.");
            Directory.Delete(target, recursive: true);
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(8));
    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(8));
    private static async Task Join(Task task)
    { try { await Within(task); } catch (Exception) { if (!task.IsCompleted) throw; } }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected actual failure."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual),
        "JSON event values differ: expected=" + Display(expected) + ", actual=" + Display(actual));
    private static string Display<T>(T value)
    {
        var text = value?.ToString() ?? "<null>";
        return JsonSerializer.Serialize(text.Length <= 256 ? text : text[..256] + " [bounded diagnostic]");
    }
}
