using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Output;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static class SessionPrintCommandTests
{
    private static string _host = "", _cli = "";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static (string Name, Func<Task> Run)[] Cases(string dotnetHost, string cliDll)
    {
        if (!Path.IsPathFullyQualified(dotnetHost) || !File.Exists(dotnetHost) ||
            !Path.IsPathFullyQualified(cliDll) || !File.Exists(cliDll)) throw new ArgumentException("Compiled CLI paths required.");
        _host = dotnetHost; _cli = cliDll;
        return
        [
            ("Print CLI Responses actual read-write turns final bytes independent resume and selected branch", () => ToolsAndReopen("openai-responses")),
            ("Print CLI Anthropic actual read-write turns final bytes independent resume and selected branch", () => ToolsAndReopen("anthropic-messages")),
            ("Print final-message projection preserves text block LF Unicode controls and bounded admission", Projection),
            ("Print CLI rejects malformed output modes before durable acquisition and preserves default report", Admission),
            ("Print CLI provider and tool failures preserve durable state and sanitized stderr exit semantics", Failures),
            ("Print command held write and flush failures await closed durable work and pre-cancel admission", OutputAndCancellation),
            ("Print CLI Completions actual file turns exact NUL Unicode bytes usage resume and selected branch", () => ToolsAndReopen("openai-completions")),
            ("Print CLI Completions failures and held output join acknowledged effects and closed durable ownership", CompletionsLifecycle)
        ];
    }

    private static async Task ToolsAndReopen(string api)
    {
        using var files = new Files();
        var source = files.In("source.txt"); var target = files.In("written.txt");
        const string seed = "source \u6587 \U0001f642\n";
        const string saved = "saved \u6587\0value\n";
        const string final = "final \u6587 \U0001f642\u2028\u2029\r\n\tcontrol\0tail\n";
        // OpenAI families carry a JSON argument string; Anthropic carries the decoded input object.
        var replaySaved = api == "anthropic-messages" ? saved : "\"content\":\"saved \u6587\\u0000value\\n\"";
        await File.WriteAllTextAsync(source, seed, Utf8);
        var sourceBytes = await File.ReadAllBytesAsync(source);
        SuccessReport(await Child(files, Create(files, api)));
        var initial = await File.ReadAllBytesAsync(files.Session);
        await Script(files, Tool(api, "read", "read-print", new { path = source }, "first print user"),
            Tool(api, "write", "write-print", new { path = target, content = saved }, seed),
            Text(api, final, "Successfully wrote", replaySaved));
        var scriptBytes = await File.ReadAllBytesAsync(files.Script);
        Printed(await Child(files, Prompt(files, api, "first print user", "print",
            ["--allow-read", source, "--allow-write", target], command: "prompt")), final + "\n");
        Equal(saved, await File.ReadAllTextAsync(target, Utf8));
        var writtenBytes = await File.ReadAllBytesAsync(target);
        Check(writtenBytes.SequenceEqual(Utf8.GetBytes(saved)), "Policy-mediated write changed inert NUL/Unicode file bytes.");
        var log = await CompleteLog(files);
        Check(log.OriginalBytes.AsSpan().StartsWith(initial), "Print rewrote acknowledged initial bytes.");
        var messages = Context(log).LlmMessages;
        Equal(3, messages.Count(message => message.Role == "assistant"));
        var results = messages.Where(message => message.Role == "toolResult").ToArray();
        Equal(2, results.Length);
        Check(results.All(message => !message.WireBody.Value.GetProperty("isError").GetBoolean()), "Actual file turns failed.");
        CheckWriteArguments(messages);
        Check(messages[^1].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString() == final,
            "Durable final text differs from printed bytes.");
        if (api == "openai-completions")
        {
            foreach (var assistant in messages.Where(message => message.Role == "assistant"))
            {
                var body = assistant.WireBody.Value;
                Equal(api, body.GetProperty("api").GetString()); Equal("openai", body.GetProperty("provider").GetString());
                Equal("pisharp-offline-completions-session", body.GetProperty("model").GetString());
                var usage = body.GetProperty("usage");
                Equal(6, usage.GetProperty("input").GetInt32()); Equal(4, usage.GetProperty("output").GetInt32());
                Equal(2, usage.GetProperty("cacheRead").GetInt32()); Equal(12, usage.GetProperty("totalTokens").GetInt32());
            }
            Equal("cmpl-text", messages[^1].WireBody.Value.GetProperty("responseId").GetString());
            Equal("stop", messages[^1].WireBody.Value.GetProperty("rawStopReason").GetString());
        }
        var ancestor = log.ValidatedPrefix[^1].Entry.Id;
        var sourceAfter = await File.ReadAllBytesAsync(source); var scriptAfter = await File.ReadAllBytesAsync(files.Script);
        Check(sourceBytes.SequenceEqual(sourceAfter) && scriptBytes.SequenceEqual(scriptAfter), "Print changed source or script bytes.");
        await using (var reopened = await SessionLogStore.OpenAsync(files.Session))
            Equal((long)log.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);

        await Script(files, Text(api, "resumed text", "first print user", final, seed, "Successfully wrote", "resume user", replaySaved));
        Printed(await Child(files, Prompt(files, api, "resume user", "print")), "resumed text\n");
        var resumed = await CompleteLog(files);
        Check(resumed.OriginalBytes.AsSpan().StartsWith(log.OriginalBytes.AsSpan()), "Independent print resume rewrote history.");
        CheckWriteArguments(Context(resumed).LlmMessages);
        await Script(files, Text(api, "branch text", "first print user", final, "branch user", replaySaved));
        Printed(await Child(files, Prompt(files, api, "branch user", "print", ["--leaf", ancestor])), "branch text\n");
        var branch = await CompleteLog(files);
        Check(branch.OriginalBytes.AsSpan().StartsWith(resumed.OriginalBytes.AsSpan()), "Selected print branch rewrote sibling bytes.");
        Equal(ancestor, branch.ValidatedPrefix[resumed.ValidatedPrefix.Length].Entry.ParentId);
        var selected = Context(branch).LlmMessages;
        CheckWriteArguments(selected);
        Check(!selected.Any(message => message.WireBody.ToString().Contains("resume user", StringComparison.Ordinal)),
            "Selected branch flattened sibling input.");
        var inspect = SuccessReport(await Child(files, ["session", "inspect", "--session", files.Session]));
        Equal(branch.ValidatedPrefix[^1].Entry.Id, inspect.GetProperty("selectedLeafId").GetString());
        Check(inspect.GetProperty("llmMessages").GetRawText().Contains("branch text", StringComparison.Ordinal),
            "Read-only inspection lost printed branch content.");
        var beforeReport = await File.ReadAllBytesAsync(files.Session);
        await Script(files, Text(api, "explicit report", "branch text"));
        var report = SuccessReport(await Child(files, Prompt(files, api, "report user", "report")));
        Equal("explicit report", report.GetProperty("finalAssistant").GetProperty("content")[0].GetProperty("text").GetString());
        Equal(new FileInfo(files.Session).Length, report.GetProperty("committedByteLength").GetInt64());
        Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Explicit report lost acknowledgment.");
        var afterReport = await File.ReadAllBytesAsync(files.Session);
        Check(afterReport.AsSpan().StartsWith(beforeReport), "Report after print rewrote acknowledged history.");
        var finalFileBytes = await File.ReadAllBytesAsync(target);
        Check(writtenBytes.SequenceEqual(finalFileBytes), "Resume/branch/report changed prior acknowledged NUL file data.");

        void CheckWriteArguments(ImmutableArray<TranscriptEntry> history)
        {
            var call = history.Where(message => message.Role == "assistant")
                .SelectMany(message => message.WireBody.Value.GetProperty("content").EnumerateArray())
                .Single(content => content.GetProperty("type").GetString() == "toolCall" && content.GetProperty("name").GetString() == "write");
            var arguments = call.GetProperty("arguments");
            Equal(2, arguments.EnumerateObject().Count());
            Equal(target, arguments.GetProperty("path").GetString()); Equal(saved, arguments.GetProperty("content").GetString());
            var id = call.GetProperty("id").GetString();
            Check(history.Any(message => message.Role == "toolResult" &&
                message.WireBody.Value.GetProperty("toolCallId").GetString() == id &&
                !message.WireBody.Value.GetProperty("isError").GetBoolean()), "Durable NUL call lost its successful result link.");
        }
    }

    private static async Task Projection()
    {
        TranscriptEntry Assistant(StopReason reason, params AssistantContent[] content) =>
            new("assistant", PiWireJson.WriteMessage(new("openai-responses", "openai", "model", 1,
                content.ToImmutableArray(), TokenUsage.Zero, reason)));
        var first = "first\r\n\t\0\u6587\U0001f642\u2028\u2029";
        var last = "last\n";
        using var output = new StringWriter { NewLine = "\r\n" };
        Equal(SessionPrintOutcome.Completed, await SessionPrintOutput.WriteAsync(output, Assistant(StopReason.Stop,
            new ThinkingContent("hidden reasoning"), new TextContent(first),
            new ToolCallContent("call", "read", JsonData.EmptyObject), new TextContent(""), new TextContent(last))));
        Equal(first + "\n\n" + last + "\n", output.ToString());
        foreach (var (reason, expected) in new[]
        { (StopReason.Error, SessionPrintOutcome.ProviderError), (StopReason.Aborted, SessionPrintOutcome.Aborted) })
        {
            using var suppressed = new StringWriter();
            Equal(expected, await SessionPrintOutput.WriteAsync(suppressed, Assistant(reason, new TextContent("must not print"))));
            Equal("", suppressed.ToString());
        }
        using var noAssistant = new StringWriter();
        Equal(SessionPrintOutcome.NoAssistant, await SessionPrintOutput.WriteAsync(noAssistant,
            new("toolResult", JsonData.Parse("""{"role":"toolResult","content":[{"type":"text","text":"not a final assistant"}]}"""))));
        Equal("", noAssistant.ToString());
        using var exact = new StringWriter();
        Equal(SessionPrintOutcome.Completed, await SessionPrintOutput.WriteAsync(exact,
            Assistant(StopReason.Length, new TextContent(new string('x', SessionPrintOutput.MaximumOutputBytes - 1)))));
        Equal(SessionPrintOutput.MaximumOutputBytes, Utf8.GetByteCount(exact.ToString()));
        foreach (var text in new[] { new string('x', SessionPrintOutput.MaximumOutputBytes),
            new string('\u6587', SessionPrintOutput.MaximumOutputBytes / 3 + 1) })
        {
            using var rejected = new StringWriter();
            var failure = await Throws<SessionCommandException>(() => SessionPrintOutput.WriteAsync(rejected,
                Assistant(StopReason.Stop, new TextContent("valid prefix"), new TextContent(text))));
            Equal(SessionCommandFailure.ResourceLimit, failure.Failure); Equal("", rejected.ToString());
        }
        foreach (var reason in new[] { StopReason.Pending, StopReason.Deferred })
        {
            using var unfinished = new StringWriter();
            var failure = await Throws<SessionCommandException>(() => SessionPrintOutput.WriteAsync(unfinished, Assistant(reason)));
            Equal(SessionCommandFailure.CommandFailed, failure.Failure); Equal("", unfinished.ToString());
        }
        using var invalidUnicode = new StringWriter();
        var unpaired = Assistant(StopReason.Stop, new TextContent("sentinel")).WireBody.ToString()
            .Replace("sentinel", "\\ud800", StringComparison.Ordinal);
        await Throws<Exception>(() => SessionPrintOutput.WriteAsync(invalidUnicode, new("assistant", JsonData.Parse(unpaired))));
        Equal("", invalidUnicode.ToString());

        // Exercise the actual command writer, independent of Windows Console.Out's code page.
        using var bytes = new PrintByteStream();
        var writer = new Utf8StreamTextWriter(bytes) { NewLine = "\r\n" };
        Equal(SessionPrintOutcome.Completed, await SessionPrintOutput.WriteAsync(writer, Assistant(StopReason.Stop,
            new TextContent(first), new TextContent(""), new TextContent(last))));
        var projected = first + "\n\n" + last + "\n";
        Check(bytes.Bytes.SequenceEqual(Utf8.GetBytes(projected)), "Actual UTF-8 writer changed Unicode/NUL/LF or inserted a BOM.");
        Equal(3, bytes.Writes); Equal(1, bytes.Flushes);
        await writer.WriteAsync("memory \u6587\U0001f642".AsMemory());
        await writer.WriteLineAsync("line \u2028\u2029");
        await writer.FlushAsync();
        Check(bytes.Bytes.SequenceEqual(Utf8.GetBytes(projected + "memory \u6587\U0001f642line \u2028\u2029\r\n")),
            "Memory/line writer entry points bypassed exact UTF-8 encoding.");
        await writer.DisposeAsync(); writer.Dispose();
        Equal(5, bytes.Writes); Equal(2, bytes.Flushes); Check(!bytes.Disposed, "Writer closed borrowed stdout.");
        await Throws<ObjectDisposedException>(() => writer.WriteAsync("must not retry"));
        bytes.WriteByte(0x21); // Borrowed stream remains usable after both disposal entry points.
        Equal((byte)0x21, bytes.Bytes[^1]);

        foreach (var failFlush in new[] { false, true })
        {
            using var failedBytes = new PrintByteStream(failWrite: !failFlush, failFlush: failFlush);
            var failedWriter = new Utf8StreamTextWriter(failedBytes);
            await Throws<IOException>(() => SessionPrintOutput.WriteAsync(failedWriter,
                Assistant(StopReason.Stop, new TextContent(first))));
            var delivered = failedBytes.Bytes;
            Check(failFlush ? delivered.SequenceEqual(Utf8.GetBytes(first + "\n")) :
                delivered.SequenceEqual(Utf8.GetBytes(first + "\n").Take(3)), "Fault control lost known accepted bytes.");
            await Throws<IOException>(() => failedWriter.WriteAsync("retry forbidden"));
            await Throws<IOException>(() => failedWriter.FlushAsync());
            await failedWriter.DisposeAsync(); failedWriter.Dispose();
            Equal(1, failedBytes.Writes); Equal(failFlush ? 1 : 0, failedBytes.Flushes);
            Check(!failedBytes.Disposed && delivered.SequenceEqual(failedBytes.Bytes),
                "Faulted writer retried, flushed on disposal or closed borrowed output.");
        }
        using var boundedBytes = new PrintByteStream();
        await using var boundedWriter = new Utf8StreamTextWriter(boundedBytes, maximumWriteBytes: 3);
        await boundedWriter.WriteAsync("\u6587");
        await Throws<IOException>(() => boundedWriter.WriteAsync("\u6587x"));
        await Throws<EncoderFallbackException>(() => boundedWriter.WriteAsync("\ud800"));
        Check(boundedBytes.Bytes.SequenceEqual(Utf8.GetBytes("\u6587")), "Rejected write emitted a substituted or oversize prefix.");
        Equal(1, boundedBytes.Writes);
    }

    private static async Task Admission()
    {
        using var files = new Files();
        foreach (var command in new[] { "create", "inspect", "tree" })
        foreach (var output in new[] { "print", "report" })
        {
            string[] args = command == "create" ? Create(files, "openai-responses") : ["session", command, "--session", files.Session];
            Refused(await Child(files, args.Concat(["--output", output]).ToArray()), "InvalidArguments");
            Check(!File.Exists(files.Session), "Invalid non-prompt output mode acquired a durable log.");
        }
        SuccessReport(await Child(files, Create(files, "openai-responses")));
        var before = await File.ReadAllBytesAsync(files.Session);
        await Script(files, Text("openai-responses", "not consumed"));
        foreach (var extra in new[]
        {
            new[] { "--output", "json-unknown" }, new[] { "--output", "PRINT" }, new[] { "--output", "" },
            new[] { "--output", "private-invalid" }, new[] { "--output" },
            new[] { "--output", "print", "--output", "report" }
        })
            Refused(await Child(files, Prompt(files, "openai-responses", "private rejected message", null, extra)), "InvalidArguments");
        var after = await File.ReadAllBytesAsync(files.Session);
        Check(before.SequenceEqual(after), "Rejected print mode changed existing bytes.");
        await Script(files, Text("openai-responses", "default report"));
        var report = SuccessReport(await Child(files, Prompt(files, "openai-responses", "default user", null)));
        Equal("session_command_result", report.GetProperty("type").GetString());
        Equal("default report", report.GetProperty("finalAssistant").GetProperty("content")[0].GetProperty("text").GetString());
    }

    private static async Task Failures()
        => await FailuresForApis("openai-responses", "anthropic-messages");

    private static async Task FailuresForApis(params string[] apis)
    {
        foreach (var api in apis)
        {
            using var files = new Files(); var target = files.In("denied.txt");
            SuccessReport(await Child(files, Create(files, api)));
            await Script(files, Tool(api, "write", "denied-print", new { path = target, content = "forbidden effect" }),
                Text(api, "handled tool failure"));
            var denied = await Child(files, Prompt(files, api, "deny tool", "print"));
            Equal(1, denied.Code); Equal("handled tool failure\n", Utf8.GetString(denied.Output));
            Equal("CompletedWithErrors", Error(denied).GetProperty("code").GetString());
            Check(!File.Exists(target), "Unallowed final action executed in print mode.");
            var log = await CompleteLog(files);
            Check(Context(log).LlmMessages.Any(message => message.Role == "toolResult" &&
                message.WireBody.Value.GetProperty("isError").GetBoolean()), "Tool failure was not durably committed.");

            await Script(files, Failure(api));
            var failed = await Child(files, Prompt(files, api, "provider fails", "print"));
            Equal(1, failed.Code); Equal(0, failed.Output.Length);
            Equal("ProviderFailed", Error(failed).GetProperty("code").GetString());
            Check(!Utf8.GetString(failed.Error).Contains("private provider payload", StringComparison.Ordinal),
                "Provider payload leaked into print stderr.");
            var final = Context(await CompleteLog(files)).LlmMessages[^1].WireBody.Value;
            Equal("assistant", final.GetProperty("role").GetString()); Equal("error", final.GetProperty("stopReason").GetString());
            Check(final.GetProperty("content").GetArrayLength() > 0, "Provider failure control lost known partial content.");
            await using var reopened = await SessionLogStore.OpenAsync(files.Session);
            Equal(new FileInfo(files.Session).Length, reopened.Snapshot.CommittedByteLength);
        }
    }

    private static async Task OutputAndCancellation()
        => await OutputAndCancellationForApi("openai-responses");

    private static async Task CompletionsLifecycle()
    {
        await FailuresForApis("openai-completions");
        await OutputAndCancellationForApi("openai-completions");
    }

    private static async Task OutputAndCancellationForApi(string api)
    {
        using (var canceledFiles = new Files())
        using (var cancellation = new CancellationTokenSource())
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            cancellation.Cancel();
            var code = await SessionCommands.RunAsync(Prompt(canceledFiles, api, "pre-cancel", "print"),
                output, error, cancellation.Token);
            Equal(1, code); Equal("", output.ToString());
            Equal("Canceled", JsonData.Parse(error.ToString()).Value.GetProperty("code").GetString());
            Check(!File.Exists(canceledFiles.Session), "Pre-cancelled print acquired storage.");
        }
        foreach (var stage in new[] { "write", "flush" })
        {
            using var files = new Files(); var target = files.In("writer-effect.txt");
            SuccessReport(await Child(files, Create(files, api)));
            await Script(files, Tool(api, "write", "output-print", new { path = target, content = "known completed effect" }),
                Text(api, "writer final", "Successfully wrote"));
            using var output = new ObservedWriter(files, stage);
            using var error = new StringWriter(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pending = SessionCommands.RunAsync(Prompt(files, api, "writer user", "print",
                ["--allow-write", target]), output, error);
            try
            {
                await output.Entered.Task.WaitAsync(deadline.Token);
                Check(!pending.IsCompleted, "Command returned before the actual held writer settled.");
                Equal("known completed effect", await File.ReadAllTextAsync(target, Utf8));
                var settledBytes = await File.ReadAllBytesAsync(files.Session);
                Equal((long)settledBytes.Length, output.AcknowledgedBytes);
                Check(output.DurableChecked && output.Disposals == 0, "Print did not close coordinator or disposed borrowed output.");
                output.Release.TrySetResult();
                Equal(1, await pending.WaitAsync(deadline.Token));
                var diagnostic = JsonData.Parse(error.ToString()).Value;
                Equal("CommandFailed", diagnostic.GetProperty("code").GetString());
                Check(diagnostic.GetProperty("effectsMayHaveCompleted").GetBoolean(), "Output fault falsely denied completed effects.");
                Check(!error.ToString().Contains("private writer payload", StringComparison.Ordinal), "Writer exception leaked.");
                Equal(stage == "write" ? "" : "writer final\n", output.Written.ToString());
                Equal(1, output.WriteCalls); Equal(stage == "write" ? 0 : 1, output.FlushCalls);
                var after = await File.ReadAllBytesAsync(files.Session);
                Check(settledBytes.SequenceEqual(after), "Output failure rewrote or retried acknowledged work.");
                await using var reopened = await SessionLogStore.OpenAsync(files.Session);
                Equal((long)after.Length, reopened.Snapshot.CommittedByteLength);
            }
            finally
            {
                output.Release.TrySetResult();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { if (!pending.IsCompleted) throw; }
            }
        }
    }

    private sealed class ObservedWriter(Files files, string failureStage) : TextWriter
    {
        public override Encoding Encoding => Utf8;
        public int WriteCalls, FlushCalls, Disposals;
        public bool DurableChecked;
        public long AcknowledgedBytes;
        public StringBuilder Written { get; } = new();
        public TaskCompletionSource Entered { get; } = Gate();
        public TaskCompletionSource Release { get; } = Gate();
        public override async Task WriteAsync(string? value)
        {
            WriteCalls++;
            await using (var lease = await SessionLogStore.OpenAsync(files.Session))
            {
                AcknowledgedBytes = lease.Snapshot.CommittedByteLength;
                Equal(new FileInfo(files.Session).Length, AcknowledgedBytes);
                var context = new SessionContextProjector().Project(lease.Snapshot.Entries, lease.Snapshot.LeafId);
                Equal("writer final", context.LlmMessages[^1].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
                DurableChecked = true;
            }
            if (failureStage == "write") await FailAfterGate();
            Written.Append(value);
        }
        public override async Task FlushAsync()
        {
            FlushCalls++;
            if (failureStage == "flush") await FailAfterGate();
        }
        private async Task FailAfterGate()
        { Entered.TrySetResult(); await Release.Task; throw new IOException("private writer payload"); }
        protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
    }

    private static SessionContextProjection Context(SessionLogReadResult read) =>
        new SessionContextProjector().Project(read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray(),
            read.ValidatedPrefix[^1].Entry.Id);
    private static async Task<SessionLogReadResult> CompleteLog(Files files)
    {
        var read = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.Diagnostics.IsEmpty,
            "Print left damaged or unacknowledged durable history.");
        Equal(new FileInfo(files.Session).Length, (long)read.OriginalBytes.Length);
        return read;
    }
    private static string[] Create(Files files, string api) =>
        ["session", "create", "--session", files.Session, "--workspace", files.Root, "--offline-api", api];
    private static string[] Prompt(Files files, string api, string message, string? output, string[]? extra = null, string command = "resume")
    {
        var args = new List<string> { "session", command, "--session", files.Session, "--workspace", files.Root,
            "--offline-api", api, "--offline-script", files.Script, "--message", message };
        if (output is not null) args.AddRange(["--output", output]);
        args.AddRange(extra ?? []); return args.ToArray();
    }
    private static async Task Script(Files files, params object[] turns) =>
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
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
    private static object Text(string api, string text, params string[] required)
    {
        if (api == "anthropic-messages") return SessionCommandTests.AnthropicText(text, required: required);
        if (api == "openai-completions") return SessionCommandTests.CompletionsText(text, required: required);
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 3, item = new { type = "message", id = "msg-print", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 3, item_id = "msg-print", delta = text },
            new { type = "response.output_item.done", output_index = 3, item = new { type = "message", id = "msg-print",
                content = new[] { new { type = "output_text", text } } } }, Completed()
        } };
    }
    private static object Failure(string api)
    {
        // Real admitted Completions chunks without a finish reason fail at EOF and retain their observed text.
        if (api == "openai-completions") return SessionCommandTests.CompletionsText("known partial suppressed", finish: false);
        if (api == "anthropic-messages") return new { events = new object[]
        {
            new { type = "message_start", message = new { id = "failed-print", role = "assistant", model = "pisharp-offline-session",
                content = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 0 } } },
            new { type = "content_block_start", index = 4, content_block = new { type = "text", text = "" } },
            new { type = "content_block_delta", index = 4, delta = new { type = "text_delta", text = "known partial suppressed" } },
            new { type = "error", error = new { type = "provider_failure", message = "private provider payload" } }
        } };
        return new { events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "failed-print", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = "failed-print", delta = "known partial suppressed" },
            new { type = "response.failed", response = new { status = "failed", error = new { message = "private provider payload" } } }
        } };
    }
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(),
        usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private sealed record Result(int Code, byte[] Output, byte[] Error);
    private static async Task<Result> Child(Files files, string[] args)
    {
        var start = new ProcessStartInfo(_host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["OPENAI_API_KEY"] = "authored-unused-environment-value";
        start.Environment["ANTHROPIC_API_KEY"] = "authored-unused-environment-value";
        start.ArgumentList.Add(_cli); foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Check(process.Start(), "Compiled print CLI did not start.");
        var output = Read(process.StandardOutput.BaseStream, deadline.Token);
        var error = Read(process.StandardError.BaseStream, deadline.Token);
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
            Check(count <= 1_048_576 - bytes.Length, "Child output exceeds authored byte bound.");
            bytes.Write(buffer, 0, count);
        }
    }
    private static void Printed(Result result, string expected)
    { Equal(0, result.Code); Equal(0, result.Error.Length); Check(result.Output.SequenceEqual(Utf8.GetBytes(expected)), "Exact final print bytes differ."); }
    private static JsonElement SuccessReport(Result result)
    { Equal(0, result.Code); Equal(0, result.Error.Length); var report = Record(result.Output); Equal("completed", report.GetProperty("status").GetString()); return report; }
    private static JsonElement Error(Result result)
    { var record = Record(result.Error); Equal("failed", record.GetProperty("status").GetString()); return record; }
    private static void Refused(Result result, string code)
    {
        Equal(2, result.Code); Equal(0, result.Output.Length); Equal(code, Error(result).GetProperty("code").GetString());
        Check(!Utf8.GetString(result.Error).Contains("private", StringComparison.Ordinal), "Rejected option payload leaked.");
    }
    private static JsonElement Record(byte[] bytes)
    {
        var text = Utf8.GetString(bytes);
        Check(text.EndsWith('\n') && text.Count(character => character == '\n') == 1, "Expected one LF JSON record.");
        return JsonData.Parse(text).Value;
    }
    private sealed class Files : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } = "";
        public string Session => In("session.jsonl");
        public string Script => In("wire.json");
        public string In(string name) => Path.Combine(Root, name);
        public Files() { Root = Path.Combine(_parent, "pisharp-print-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            Check(target == Root && Path.GetDirectoryName(target) == _parent &&
                Path.GetFileName(target).StartsWith("pisharp-print-cli-", StringComparison.Ordinal), "Refusing unowned recursive cleanup.");
            Directory.Delete(target, recursive: true);
        }
    }
    private sealed class PrintByteStream(bool failWrite = false, bool failFlush = false) : Stream
    {
        private readonly MemoryStream bytes = new();
        public byte[] Bytes => bytes.ToArray();
        public int Writes { get; private set; }
        public int Flushes { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !Disposed;
        public override long Length => bytes.Length;
        public override long Position { get => bytes.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Deliver(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => Deliver(buffer);
        private void Deliver(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(Disposed, this); Writes++;
            if (failWrite)
            {
                bytes.Write(buffer[..Math.Min(3, buffer.Length)]);
                throw new IOException("Authored output write failed after accepting a prefix.");
            }
            bytes.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Deliver(buffer.Span); return ValueTask.CompletedTask; }
        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(Disposed, this); Flushes++;
            if (failFlush) throw new IOException("Authored output flush failed.");
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
        protected override void Dispose(bool disposing)
        { Disposed = true; if (disposing) bytes.Dispose(); base.Dispose(disposing); }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected failure."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Print values differ.");
}
