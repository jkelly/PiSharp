using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static class NativeCliExtensionUiTests
{
    private static string host = "", cli = "", published = "";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string cliDll)
    {
        Check(Path.IsPathFullyQualified(dotnetHost) && File.Exists(dotnetHost) && Path.IsPathFullyQualified(cliDll) && File.Exists(cliDll),
            "UI process cases require the actual compiled host and CLI.");
        host = dotnetHost; cli = cliDll;
        var directory = new DirectoryInfo(Path.GetDirectoryName(cli)!);
        while (!File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent ?? throw new InvalidOperationException("Published fixture root is absent.");
        published = Path.Combine(directory.FullName, "artifacts", "extensions", "published-fixtures", "cli-ui");
        return
        [
            ("native CLI UI published dialogs/notifications share RPC and durable final policy across reopen", Dialogs),
            ("native CLI UI print/JSON have explicit unavailable capabilities and never approve", Headless),
            ("native CLI UI unknown/duplicate/null/cancelled replies cannot authorize another request", Replies),
            ("native CLI UI prior callback scope is stale while the same published owner continues", Stale),
            ("native CLI UI finite timeout, held abort and EOF join package and durable cleanup", Shutdown),
            ("native CLI UI approval denial and broken stdout preserve authority and join owned cleanup", Failure)
        ];
    }

    private static async Task Dialogs()
    {
        using var files = await Files.CreateAsync(); await Create(files, "openai-responses");
        var original = await File.ReadAllBytesAsync(files.Session);
        const string final = "final \u6587\0\U0001f642\r\n";
        const string saved = "saved \u6587\0value\n";
        // Exact literal Responses DTOs; a native file action remains separately authorized after UI approval.
        await Script(files, Tool("openai-responses", "dialogs"), ResponsesTool("write", "write", new { path = files.Target, content = saved }, ["ui:approved"]),
            Text("openai-responses", final, ["Successfully wrote"]));
        JsonData[] records;
        await using (var child = new RpcChild(files, "openai-responses", "--allow-write", files.Target))
        {
            await child.Send(new { id = "p", type = "prompt", message = "UI user" }); GoodResponse(await child.Response("p"));
            var select = await child.Ui(0); ExpectUi(select, new { method = "select", title = "choose \u6587", options = new[] { "", "second" }, timeout = 0 });
            await child.Reply(select, new { value = "" });
            var confirm = await child.Ui(1); ExpectUi(confirm, new { method = "confirm", title = "confirm", message = "allow \0\U0001f642" });
            await child.Reply(confirm, new { confirmed = true });
            var input = await child.Ui(2); ExpectUi(input, new { method = "input", title = "input", placeholder = "" });
            await child.Reply(input, new { value = "input\0\u6587" });
            var editor = await child.Ui(3); ExpectUi(editor, new { method = "editor", title = "editor", prefill = "prefill\r\n" });
            await child.Reply(editor, new { value = "edited\r\n\U0001f642\0" });
            var notices = new object[]
            {
                new { method = "notify", message = "notice\0\u6587", notifyType = "warning" },
                new { method = "setStatus", statusKey = "status", statusText = "ready" }, new { method = "setStatus", statusKey = "status" },
                new { method = "setWidget", widgetKey = "widget", widgetLines = new[] { "first", "\u6587\0" }, widgetPlacement = "belowEditor" },
                new { method = "setWidget", widgetKey = "widget" }, new { method = "setTitle", title = "title \U0001f642" },
                new { method = "set_editor_text", text = "edit\n\0" }
            };
            for (var index = 0; index < notices.Length; index++) ExpectUi(await child.Ui(index + 4), notices[index]);
            await child.Wait(record => Type(record) == "agent_settled");
            await child.Send(new { id = "entries", type = "get_entries" });
            var entries = GoodResponse(await child.Response("entries")).GetProperty("data");
            Check(entries.GetProperty("entries").EnumerateArray().Any(entry => entry.TryGetProperty("message", out var message) &&
                message.TryGetProperty("role", out var role) && role.GetString() == "toolResult"), "RPC omitted acknowledged physical tool result.");
            Clean(await child.Finish()); records = child.Records;
        }
        Check(records.Count(record => Type(record) == "extension_ui_request") == 11, "UI framing lost or duplicated a request.");
        var ids = records.Where(record => Type(record) == "extension_ui_request").Select(Id).ToArray();
        Check(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length && ids.All(id => id.Length == 49), "Actual UI IDs are not unique and bounded.");
        var complete = UiResult(records); var details = complete.GetProperty("details");
        Equal("Rpc", details.GetProperty("mode").GetString()); Equal(9, details.GetProperty("features").GetArrayLength());
        Equal(1L, details.GetProperty("connectionGeneration").GetInt64()); Equal(1L, details.GetProperty("sessionGeneration").GetInt64());
        Check(details.GetProperty("approved").GetBoolean(), "Only the explicit true reply should approve the fixture decision.");
        var observations = details.GetProperty("observations"); Equal(11, observations.GetArrayLength());
        Same(JsonSerializer.SerializeToElement(new { method = "select", kind = "Value", reason = (string?)null, value = "" }), observations[0]);
        Same(JsonSerializer.SerializeToElement(new { method = "confirm", kind = "Value", reason = (string?)null, value = true }), observations[1]);
        Equal("input\0\u6587", observations[2].GetProperty("value").GetString()); Equal("edited\r\n\U0001f642\0", observations[3].GetProperty("value").GetString());
        Same(details, complete.GetProperty("structuredContent")); Equal(JsonValueKind.Null, complete.GetProperty("future").ValueKind);
        var expectedDetails = JsonSerializer.SerializeToElement(new
        {
            action = "dialogs", approved = true, mode = "Rpc", connectionGeneration = 1, sessionGeneration = 1,
            features = new[] { "Select", "Confirm", "Input", "Editor", "Notify", "Status", "TextWidget", "Title", "EditorText" },
            observations = new object[]
            {
                new { method = "select", kind = "Value", reason = (string?)null, value = "" },
                new { method = "confirm", kind = "Value", reason = (string?)null, value = true },
                new { method = "input", kind = "Value", reason = (string?)null, value = "input\0\u6587" },
                new { method = "editor", kind = "Value", reason = (string?)null, value = "edited\r\n\U0001f642\0" },
                new { method = "ExtensionUiNotify", kind = "Value", reason = (string?)null, value = 0 },
                new { method = "ExtensionUiStatus", kind = "Value", reason = (string?)null, value = 0 },
                new { method = "ExtensionUiStatus", kind = "Value", reason = (string?)null, value = 0 },
                new { method = "ExtensionUiTextWidget", kind = "Value", reason = (string?)null, value = 0 },
                new { method = "ExtensionUiTextWidget", kind = "Value", reason = (string?)null, value = 0 },
                new { method = "ExtensionUiTitle", kind = "Value", reason = (string?)null, value = 0 },
                new { method = "ExtensionUiEditorText", kind = "Value", reason = (string?)null, value = 0 }
            }
        });
        Same(expectedDetails, details);
        Same(JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = "ui:approved" } }, details = expectedDetails,
            structuredContent = expectedDetails, future = (object?)null
        }), complete);
        var bytes = await File.ReadAllBytesAsync(files.Target); Check(bytes.SequenceEqual(Utf8.GetBytes(saved)), "UI reply changed independently authorized write bytes.");
        var log = await Complete(files); Check(log.OriginalBytes.AsSpan().StartsWith(original), "RPC rewrote committed source bytes.");
        var messages = Context(log).LlmMessages; var durable = messages.Single(message => message.Role == "toolResult" &&
            message.WireBody.Value.GetProperty("toolName").GetString() == "fixture.cli.ui").WireBody.Value;
        Same(details, durable.GetProperty("details"));
        Equal(final, messages[^1].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Check(!log.ValidatedPrefix.Any(record => record.Entry.WireBody.ToString().Contains("extension_ui_request", StringComparison.Ordinal)), "UI transport records entered durable conversation.");
        var assistantEnd = Array.FindIndex(records, record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant");
        var firstUi = Array.FindIndex(records, record => Type(record) == "extension_ui_request");
        Check(assistantEnd >= 0 && firstUi > assistantEnd, "UI callback preceded the committed assistant public barrier.");
        var leaf = Context(log).LeafId!;
        await Script(files, Text("openai-responses", "resumed", ["UI user", "ui:approved", "Successfully wrote", "resume"]));
        Success(await Command(files, "resume", "openai-responses", "resume"));
        await Script(files, Text("openai-responses", "branch", ["UI user", "ui:approved", "branch"]));
        var branched = Success(await Command(files, "resume", "openai-responses", "branch", "--leaf", leaf));
        Equal(leaf, branched.GetProperty("previousSelectedLeafId").GetString());
        var after = await Complete(files); Check(after.OriginalBytes.AsSpan().StartsWith(log.OriginalBytes.AsSpan()), "Reopen/branch rewrote acknowledged bytes.");
        var selected = new SessionContextProjector().Project(after.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray(),
            branched.GetProperty("selectedLeafId").GetString());
        Check(!selected.LlmMessages.Any(message => message.Role == "user" && message.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString() == "resume"), "Selected branch copied sibling input.");
        Markers(files, 4); Equal(1, files.MarkerLines.Count(line => line == "approved"));
    }

    private static async Task Headless()
    {
        foreach (var mode in new[] { "print", "json" })
        {
            using var files = await Files.CreateAsync(); await Create(files, "anthropic-messages");
            const string final = "headless \u6587\0\U0001f642\r\n";
            await Script(files, Tool("anthropic-messages", "dialogs"), Text("anthropic-messages", final, ["ui:denied"]));
            var result = await Command(files, "prompt", "anthropic-messages", "headless", "--output", mode); Clean(result);
            if (mode == "print") Check(result.Output.SequenceEqual(Utf8.GetBytes(final + "\n")), "Headless UI changed final output bytes.");
            else
            {
                var records = JsonLines(result.Output); Equal("session", Type(records[0]));
                Check(!records.Any(record => Type(record) == "extension_ui_request"), "JSON mode acquired an RPC renderer.");
                var complete = UiResult(records); Same(complete.GetProperty("details"), complete.GetProperty("structuredContent"));
                Equal(JsonValueKind.Null, complete.GetProperty("future").ValueKind);
            }
            var log = await Complete(files); var details = Context(log).LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value.GetProperty("details");
            Equal(mode == "json" ? "Json" : "Print", details.GetProperty("mode").GetString());
            Equal(0, details.GetProperty("features").GetArrayLength()); Check(!details.GetProperty("approved").GetBoolean(), "NoUi granted approval.");
            foreach (var observation in details.GetProperty("observations").EnumerateArray())
            { Equal("Unavailable", observation.GetProperty("kind").GetString()); Equal("NoUi", observation.GetProperty("reason").GetString()); Equal(JsonValueKind.Null, observation.GetProperty("value").ValueKind); }
            Check(!files.MarkerLines.Contains("approved"), "Headless callback treated unavailable confirmation as true."); Markers(files, 2);
        }
    }

    private static async Task Replies()
    {
        using var files = await Files.CreateAsync(); await Create(files, "openai-completions");
        await Script(files, Tool("openai-completions", "repeat"), Text("openai-completions", "denied", ["ui:denied"]));
        await using (var child = new RpcChild(files, "openai-completions"))
        {
            await child.Send(new { id = "p", type = "prompt", message = "reply controls" }); GoodResponse(await child.Response("p"));
            var first = await child.Ui(0); await child.Send(new { type = "extension_ui_response", id = "unknown", confirmed = true });
            await child.Reply(first, new { confirmed = (bool?)null });
            var second = await child.Ui(1); Check(Id(first) != Id(second), "Distinct dialogs reused correlation.");
            await child.Reply(first, new { confirmed = true });
            await child.Send(new { id = "state", type = "get_state" }); var state = GoodResponse(await child.Response("state")).GetProperty("data");
            Check(state.GetProperty("isStreaming").GetBoolean(), "Duplicate/unknown reply released the active second dialog.");
            Check(!child.Records.Any(record => Type(record) == "tool_execution_end"), "Second dialog returned before its actual response.");
            await child.Reply(second, new { cancelled = true, confirmed = true }); await child.Wait(record => Type(record) == "agent_settled");
            var result = UiResult(child.Records); var observations = result.GetProperty("details").GetProperty("observations");
            Equal("Unavailable", observations[0].GetProperty("kind").GetString()); Equal("InvalidResponse", observations[0].GetProperty("reason").GetString());
            Equal("Cancelled", observations[1].GetProperty("kind").GetString()); Check(!result.GetProperty("details").GetProperty("approved").GetBoolean(), "Cancelled true reply granted approval.");
            Check(child.Records.Where(record => Type(record) == "response").All(record => Id(record) is "p" or "state"), "UI response invented an ordinary acknowledgement.");
            Clean(await child.Finish());
        }
        await Complete(files); Check(!files.MarkerLines.Contains("approved"), "Rejected correlation authorized fixture effect."); Markers(files, 2);
    }

    private static async Task Stale()
    {
        using var files = await Files.CreateAsync(); await Create(files, "openai-responses");
        await Script(files, Tool("openai-responses", "confirm"), Tool("openai-responses", "stale"), Text("openai-responses", "stale closed", ["ui:denied"]));
        await using (var child = new RpcChild(files, "openai-responses"))
        {
            await child.Send(new { id = "p", type = "prompt", message = "stale callback" }); GoodResponse(await child.Response("p"));
            await child.Reply(await child.Ui(0), new { confirmed = false }); await child.Wait(record => Type(record) == "agent_settled");
            Equal(1, child.Records.Count(record => Type(record) == "extension_ui_request"));
            var result = child.Records.Where(record => Type(record) == "tool_execution_end" &&
                record.Value.GetProperty("toolName").GetString() == "fixture.cli.ui").Last().Value.GetProperty("result");
            var stale = result.GetProperty("details").GetProperty("observations")[0];
            Equal("Unavailable", stale.GetProperty("kind").GetString()); Equal("StaleContext", stale.GetProperty("reason").GetString());
            Check(!result.GetProperty("details").GetProperty("approved").GetBoolean(), "Closed generation scope granted approval.");
            Clean(await child.Finish());
        }
        var log = await Complete(files); Equal(2, Context(log).LlmMessages.Count(message => message.Role == "toolResult")); Markers(files, 2);
    }

    private static async Task Shutdown()
    {
        foreach (var stage in new[] { "timeout", "abort", "eof" })
        {
            using var files = await Files.CreateAsync(); await Create(files, "openai-responses");
            await Script(files, Tool("openai-responses", stage == "timeout" ? "timeout" : "confirm"), Text("openai-responses", "after", ["ui:denied"]));
            await using (var child = new RpcChild(files, "openai-responses"))
            {
                await child.Send(new { id = "p", type = "prompt", message = stage }); GoodResponse(await child.Response("p"));
                if (stage == "timeout")
                {
                    // Actual finite timer; no sleep or timing-based assertion that publication wins the timer.
                    await child.Wait(record => Type(record) == "agent_settled");
                    var outcome = UiResult(child.Records).GetProperty("details").GetProperty("observations")[0];
                    Equal("TimedOut", outcome.GetProperty("kind").GetString()); Equal(JsonValueKind.Null, outcome.GetProperty("value").ValueKind);
                    foreach (var request in child.Records.Where(record => Type(record) == "extension_ui_request"))
                        ExpectUi(request, new { method = "confirm", title = "first", message = "approval", timeout = 25 });
                }
                else
                {
                    ExpectUi(await child.Ui(0), new { method = "confirm", title = "first", message = "approval", timeout = 0 });
                    Check(!files.MarkerLines.Contains("tool-closed"), "Dialog callback was not actually held.");
                    if (stage == "abort")
                    { await child.Send(new { id = "abort", type = "abort" }); GoodResponse(await child.Response("abort")); await child.Wait(record => Type(record) == "agent_settled"); }
                }
                Clean(await child.Finish());
            }
            var log = await Complete(files); Check(!files.MarkerLines.Contains("approved"), "Timeout/shutdown granted approval.");
            Equal(1, files.MarkerLines.Count(line => line == "tool-closed")); Markers(files, 2);
            if (stage != "timeout") Check(!Context(log).LlmMessages.Any(message => message.Role == "assistant" &&
                message.WireBody.Value.GetProperty("content").EnumerateArray().Any(block => block.TryGetProperty("text", out var text) && text.GetString() == "after")),
                "Shutdown acquired a subsequent scripted provider response.");
        }
    }

    private static async Task Failure()
    {
        using (var denied = await Files.CreateAsync())
        {
            var args = Base("create", denied, "openai-responses").Concat(denied.ExtensionArgs).ToArray();
            var noApproval = new List<string>();
            for (var index = 0; index < args.Length; index++)
                if (args[index] == "--extension-approval") index++; else noApproval.Add(args[index]);
            var refused = await OneShot(denied, noApproval.ToArray()); Equal(2, refused.ExitCode); Equal(0, refused.Output.Length);
            Equal("ExecutionApprovalRequired", JsonData.Parse(refused.Error).Value.GetProperty("code").GetString());
            Equal(0, denied.MarkerLines.Length); Check(!File.Exists(denied.Session), "Missing approval acquired a durable writer.");
            Equal(0, Directory.GetDirectories(denied.Snapshots).Length);
        }
        using var files = await Files.CreateAsync(); await Create(files, "openai-responses");
        await Script(files, Tool("openai-responses", "confirm"), ResponsesTool("write", "forbidden", new { path = files.Target, content = "must not execute" }, []),
            Text("openai-responses", "must not acquire"));
        await using (var child = new RpcChild(files, "openai-responses", "--allow-write", files.Target))
        {
            await child.Send(new { id = "p", type = "prompt", message = "broken output" }); GoodResponse(await child.Response("p"));
            var request = await child.Ui(0); ExpectUi(request, new { method = "confirm", title = "first", message = "approval", timeout = 0 });
            child.BreakOutput(); await child.Send(new { id = "state", type = "get_state" });
            var failed = await child.Finish(allowOutputFailure: true); Equal(1, failed.ExitCode);
            Equal("RpcHostFailed", JsonData.Parse(failed.Error).Value.GetProperty("code").GetString());
        }
        await Complete(files); Check(!File.Exists(files.Target), "Output failure executed a later native action.");
        Equal(1, files.MarkerLines.Count(line => line == "tool-closed")); Check(!files.MarkerLines.Contains("approved"), "Failed output supplied approval."); Markers(files, 2);
    }

    private static async Task Create(Files files, string api)
    { _ = Success(await OneShot(files, Base("create", files, api).Concat(files.ExtensionArgs).ToArray())); }
    private static Task<Result> Command(Files files, string command, string api, string message, params string[] extra) =>
        OneShot(files, Base(command, files, api).Concat(["--offline-script", files.Script, "--message", message]).Concat(files.ExtensionArgs).Concat(extra).ToArray());
    private static string[] Base(string command, Files files, string api) =>
        ["session", command, "--session", files.Session, "--workspace", files.Root, "--offline-api", api];
    private static async Task Script(Files files, params object[] turns) =>
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
    private static object Tool(string api, string action) => api switch
    {
        "anthropic-messages" => SessionCommandTests.AnthropicTool("fixture.cli.ui", "ui-" + action, new { action }),
        "openai-completions" => SessionCommandTests.CompletionsTool("fixture.cli.ui", "ui-" + action, new { action }),
        _ => ResponsesTool("fixture.cli.ui", "ui-" + action, new { action }, [])
    };
    private static object Text(string api, string text, string[]? required = null) => api switch
    {
        "anthropic-messages" => SessionCommandTests.AnthropicText(text, required: required),
        "openai-completions" => SessionCommandTests.CompletionsText(text, required: required),
        _ => new { requiredInputTexts = required ?? [], events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = "msg", delta = text },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg", content = new[] { new { type = "output_text", text } } } }, Completed()
        } }
    };
    private static object ResponsesTool(string name, string call, object arguments, string[] required) => new
    {
        requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = "" } },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = JsonSerializer.Serialize(arguments) } }, Completed()
        }
    };
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(),
        usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private static JsonElement UiResult(JsonData[] records) => records.First(record => Type(record) == "tool_execution_end" &&
        record.Value.GetProperty("toolName").GetString() == "fixture.cli.ui").Value.GetProperty("result");
    private static void ExpectUi(JsonData actual, object fields)
    {
        var expected = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        { ["type"] = JsonSerializer.SerializeToElement("extension_ui_request"), ["id"] = JsonSerializer.SerializeToElement(Id(actual)) };
        foreach (var field in JsonSerializer.SerializeToElement(fields).EnumerateObject()) expected.Add(field.Name, field.Value);
        Same(JsonSerializer.SerializeToElement(expected), actual.Value);
    }
    private static void Same(JsonElement expected, JsonElement actual)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var left = expected.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                var right = actual.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                Equal(left.Count, right.Count); foreach (var pair in left) { Check(right.TryGetValue(pair.Key, out var value), "Missing exact UI field " + pair.Key); Same(pair.Value, value); } break;
            case JsonValueKind.Array:
                Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var index = 0; index < expected.GetArrayLength(); index++) Same(expected[index], actual[index]); break;
            case JsonValueKind.String: Equal(expected.GetString(), actual.GetString()); break;
            case JsonValueKind.Number: Equal(expected.GetRawText(), actual.GetRawText()); break;
            default: Equal(expected.GetRawText(), actual.GetRawText()); break;
        }
    }
    private static async Task<SessionLogReadResult> Complete(Files files)
    {
        var log = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.ValidatedPrefixByteLength == log.OriginalBytes.Length, "UI host did not close complete acknowledged physical bytes.");
        await using var exclusive = await SessionLogStore.OpenAsync(files.Session); Equal((long)log.OriginalBytes.Length, exclusive.Snapshot.CommittedByteLength); return log;
    }
    private static SessionContextProjection Context(SessionLogReadResult log) =>
        new SessionContextProjector().ProjectLatest(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray());
    private static void Markers(Files files, int runs)
    {
        foreach (var stage in new[] { "module", "constructor", "initialize", "dispose" }) Equal(runs, files.MarkerLines.Count(line => line == stage));
        Equal("dispose", files.MarkerLines[^1]); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }
    private static string Type(JsonData value) => value.Value.GetProperty("type").GetString()!;
    private static string Id(JsonData value) => value.Value.GetProperty("id").GetString()!;
    private static JsonElement GoodResponse(JsonData value) { Check(value.Value.GetProperty("success").GetBoolean(), "Failed RPC response: " + Bounded(value.ToString())); return value.Value; }
    private static JsonElement Success(Result result)
    { Clean(result); var report = JsonData.Parse(Utf8.GetString(result.Output)).Value; Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Receipt preceded durable checkpoint."); return report; }
    private static void Clean(Result result) => Check(result.ExitCode == 0 && result.Error.Length == 0, "Compiled CLI exit=" + result.ExitCode + " bytes=" + result.Output.Length + " stderr=" + Bounded(result.Error));
    private static void Clean(RpcResult result) => Check(result.ExitCode == 0 && result.Error.Length == 0, "Compiled RPC exit=" + result.ExitCode + " stderr=" + Bounded(result.Error));
    private static string Bounded(string? text) => text is null ? "<null>" : JsonSerializer.Serialize(text.Length <= 1536 ? text : text[..1536] + "[truncated]");
    private static JsonData[] JsonLines(byte[] bytes) { var text = Utf8.GetString(bytes); Check(text.EndsWith('\n'), "Output was not complete LF JSONL."); return text[..^1].Split('\n').Select(JsonData.Parse).ToArray(); }
    private static ProcessStartInfo Start(Files files, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        start.Environment["PISHARP_NATIVE_FIXTURE_MARKERS"] = files.Markers; start.ArgumentList.Add(cli);
        foreach (var arg in args) start.ArgumentList.Add(arg); return start;
    }
    private sealed record Result(int ExitCode, byte[] Output, string Error);
    private sealed record RpcResult(int ExitCode, string Error);
    private static async Task<Result> OneShot(Files files, string[] args)
    {
        using var process = Process.Start(Start(files, args)) ?? throw new InvalidOperationException("Compiled UI CLI did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); process.StandardInput.Close();
        var output = ReadBytes(process.StandardOutput.BaseStream, deadline.Token); var error = ReadBytes(process.StandardError.BaseStream, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, await output, Utf8.GetString(await error)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } foreach (var pending in new[] { output, error }) try { await pending; } catch (Exception) { } }
    }
    private static async Task<byte[]> ReadBytes(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await stream.ReadAsync(buffer, token); if (count == 0) return output.ToArray(); Check(count <= 2_097_152 - output.Length, "Child receipt cap exceeded."); output.Write(buffer, 0, count); }
    }
    private sealed class RpcChild : IAsyncDisposable
    {
        private readonly Process process; private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        private readonly CancellationTokenSource outputCancellation = new(); private readonly object gate = new();
        private readonly List<JsonData> records = []; private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task read; private readonly Task<byte[]> error; private bool closed;
        public JsonData[] Records { get { lock (gate) return records.ToArray(); } }
        public RpcChild(Files files, string api, params string[] extra)
        {
            process = Process.Start(Start(files, Base("rpc", files, api).Concat(["--offline-script", files.Script]).Concat(files.ExtensionArgs).Concat(extra)))
                ?? throw new InvalidOperationException("Compiled UI RPC did not start."); error = ReadBytes(process.StandardError.BaseStream, deadline.Token); read = ReadAsync();
        }
        public async Task Send(object command) { await process.StandardInput.WriteAsync((JsonSerializer.Serialize(command) + "\n").AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
        public Task Reply(JsonData request, object fields)
        {
            var command = new Dictionary<string, object?> { ["type"] = "extension_ui_response", ["id"] = Id(request) };
            foreach (var field in JsonSerializer.SerializeToElement(fields).EnumerateObject()) command.Add(field.Name, field.Value); return Send(command);
        }
        public Task<JsonData> Response(string id) => Wait(record => Type(record) == "response" && record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        public async Task<JsonData> Ui(int index)
        {
            while (true)
            {
                Task next; lock (gate) { var ui = records.Where(record => Type(record) == "extension_ui_request").ToArray(); if (ui.Length > index) return ui[index]; Check(!read.IsCompleted, "RPC ended before UI request."); next = changed.Task; }
                await next.WaitAsync(deadline.Token);
            }
        }
        public async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        {
            while (true) { Task next; lock (gate) { var value = records.FirstOrDefault(predicate); if (value is not null) return value; Check(!read.IsCompleted, "RPC ended before expected record."); next = changed.Task; } await next.WaitAsync(deadline.Token); }
        }
        private async Task ReadAsync()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, outputCancellation.Token);
            try
            {
                await using var reader = new JsonlReader(process.StandardOutput.BaseStream); long bytes = 0;
                await foreach (var admission in reader.ReadAdmissionsAsync(linked.Token))
                {
                    Check(admission.IsAccepted && !admission.IsFinalFrame, "Malformed/truncated actual RPC record."); var value = admission.Record!;
                    bytes += Utf8.GetByteCount(value.ToString()) + 1; Check(bytes <= 2_097_152, "RPC receipt cap exceeded."); TaskCompletionSource signal;
                    lock (gate) { Check(records.Count < 2048, "RPC receipt count exceeded."); records.Add(value); signal = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); } signal.TrySetResult();
                }
            }
            finally { lock (gate) changed.TrySetResult(); }
        }
        public void BreakOutput() { outputCancellation.Cancel(); process.StandardOutput.BaseStream.Dispose(); }
        public async Task<RpcResult> Finish(bool allowOutputFailure = false)
        {
            if (!closed) { closed = true; process.StandardInput.Close(); } await process.WaitForExitAsync(deadline.Token);
            try { await read; } catch (Exception) when (allowOutputFailure) { } return new(process.ExitCode, Utf8.GetString(await error));
        }
        public async ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } outputCancellation.Cancel(); try { await read; } catch (Exception) { } try { await error; } catch (Exception) { } }
            finally { process.Dispose(); deadline.Dispose(); outputCancellation.Dispose(); }
        }
    }
    private sealed class Files : IDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-native-cli-ui-" + Guid.NewGuid().ToString("N"));
        public string Session => In("session.jsonl"); public string Script => In("script.json"); public string Target => In("effect.txt");
        public string Package => In("published"); public string Manifest => In("manifest.json"); public string Approval => In("approval.json");
        public string Snapshots => In("snapshots"); public string Markers => In("markers"); private string In(string value) => Path.Combine(Root, value);
        public string[] MarkerLines => File.Exists(Path.Combine(Markers, "PublishedFixture.CliUi.markers")) ? File.ReadAllLines(Path.Combine(Markers, "PublishedFixture.CliUi.markers")) : [];
        public string[] ExtensionArgs => ["--extension-package", Package, "--extension-manifest", Manifest, "--extension-approval", Approval,
            "--extension-snapshot-root", Snapshots, "--enable-extension-tool", "fixture.cli.ui"];
        public static async Task<Files> CreateAsync()
        {
            var files = new Files();
            try
            {
                Check(File.Exists(Path.Combine(published, "PublishedFixture.CliUi.dll")), "Root must publish PluginCliUi before native execution.");
                foreach (var path in new[] { files.Root, files.Package, files.Snapshots, files.Markers }) Directory.CreateDirectory(path);
                foreach (var source in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
                { var target = Path.Combine(files.Package, Path.GetRelativePath(published, source)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); }
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var source in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    hashes.Add(Path.GetRelativePath(files.Package, source).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source))));
                var metadata = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.cli.ui", packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" },
                    runtimeKind = "native", assembly = "PublishedFixture.CliUi.dll", entryType = "PublishedCliUiFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                    requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" }, declaredCapabilities = new[] { "tools", "observations" },
                    resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.CliUi.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
                await File.WriteAllTextAsync(files.Manifest, metadata, Utf8);
                await File.WriteAllTextAsync(files.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = files.Package,
                    manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(metadata))), artifactHashes = hashes, sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit",
                    policyRevision = "experimental-policy-0", hostGeneration = 1, sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = new[] { "fixture.cli.ui" } }), Utf8);
                return files;
            }
            catch { files.Dispose(); throw; }
        }
        public void Dispose()
        {
            var target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != parent || !Path.GetFileName(target).StartsWith("pisharp-native-cli-ui-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing unowned recursive cleanup.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "UI values differ expected=" + Bounded(expected?.ToString()) + " actual=" + Bounded(actual?.ToString()));
}
