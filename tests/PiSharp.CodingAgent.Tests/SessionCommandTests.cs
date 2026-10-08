using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Sessions.Storage;

internal static class SessionCommandTests
{
    private static string _host = "", _cli = "";
    public static (string Name, Func<Task> Run)[] Cases(string dotnetHost, string cliDll)
    {
        if (!Path.IsPathFullyQualified(dotnetHost) || !Path.IsPathFullyQualified(cliDll) || !File.Exists(dotnetHost) || !File.Exists(cliDll))
            throw new ArgumentException("Session child tests require existing explicit host and CLI paths.");
        _host = dotnetHost; _cli = cliDll;
        return
        [
            ("Session CLI compiled create/prompt/independent resume preserve actual file effects and durable history", SeparateProcesses),
            ("Session CLI selected leaf appends a sibling branch and read-only tree/inspect preserve bytes", SelectedBranch),
            ("Session CLI denies unallowed final targets and rejects protected/outside authorizations", FilePolicy),
            ("Session CLI read-only damaged inspection cannot become prefix resume success or repair", DamagedInspection),
            ("Session CLI rejects malformed/bounded arguments and scripts before source mutation", Admission),
            ("Session CLI pre-canceled command avoids acquisition and emits fixed cancellation JSON", Cancellation),
            ("Session CLI explicit Anthropic HTTP/SSE file turns survive independent resume and selected branch", AnthropicProcesses),
            ("Session CLI rejects offline family/auth admission and refuses mismatched actual request scripts", AnthropicAdmission),
            ("Session CLI Completions actual file turns preserve chunk authority, durable history and independent branches", CompletionsProcesses),
            ("Session CLI Completions rejects cross-family preflight and keeps failed wire/request/tool authority bounded", CompletionsAdmission),
            ("session-history CLI actual readonly process separates branch raw display model and billing", HistoryViews),
            ("session-history CLI actual root and sibling selections preserve global metadata and billed history", HistorySelections),
            ("session-history CLI damaged future and runtime-option admission remain readonly", HistoryAdmission),
            ("session-history CLI optional replay failures and history resource limits retain typed recovery", HistoryProjectionFailures)
        ];
    }

    private static string HistoryInput(Files files)
    {
        var usage = new { input = 1, output = 2, cacheRead = 3, cacheWrite = 4, totalTokens = 1000,
            cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0.25 }, opaque = "retained" };
        object Message(string text) => new { role = "assistant", content = new[] { new { type = "text", text } },
            api = "fixture", provider = "fixture", model = "fixture", stopReason = "stop", timestamp = 1, usage };
        object Record(string id, string? parent, string type, object fields)
        {
            var body = JsonSerializer.Serialize(fields);
            using var document = JsonDocument.Parse("{\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) +
                ",\"type\":" + JsonSerializer.Serialize(type) + ",\"timestamp\":\"2026-01-01T00:00:00.000Z\"," + body[1..^1] + "}");
            return document.RootElement.Clone();
        }
        object[] records = [new { type = "session", version = 3, id = "readonly", timestamp = "2026-01-01T00:00:00.000Z", cwd = files.Root },
            Record("s", null, "message", new { message = new { role = "system", content = "original system", timestamp = 0 } }),
            Record("u", "s", "message", new { message = new { role = "user", content = "original user", timestamp = 0 } }),
            Record("a", "u", "message", new { message = Message("billed before compact") }),
            Record("h", "a", "custom_message", new { customType = "hidden", content = "hidden context", display = false }),
            Record("usage", "h", "usage", new { kind = "future_kind", provider = "fixture", model = "fixture", usage }),
            Record("c", "usage", "compaction", new { summary = "checkpoint", firstKeptEntryId = "absent", tokensBefore = 10, usage,
                systemMessage = new { role = "system", content = "checkpoint system", timestamp = 1 } }),
            Record("after", "c", "message", new { message = Message("after compact") }),
            Record("right", "s", "message", new { message = Message("right sibling") }),
            Record("name", "right", "session_info", new { name = "Global name" })];
        return string.Join("\n", records.Select(record => JsonSerializer.Serialize(record))) + "\n";
    }
    private static async Task HistoryViews()
    {
        using var files = new Files(); var source = HistoryInput(files); await File.WriteAllTextAsync(files.Session, source, new UTF8Encoding(false));
        var result = Success(await Child(files, "session", "history", "--session", files.Session, "--leaf", "after"));
        Equal("history", result.GetProperty("command").GetString()); Equal(9, result.GetProperty("fullHistory").GetArrayLength());
        Equal(7, result.GetProperty("branchHistory").GetArrayLength()); Equal(7, result.GetProperty("historyMessages").GetArrayLength());
        Equal(3, result.GetProperty("modelMessages").GetArrayLength());
        Check(result.GetProperty("historyMessages").GetRawText().Contains("billed before compact", StringComparison.Ordinal) &&
            !result.GetProperty("modelMessages").GetRawText().Contains("billed before compact", StringComparison.Ordinal), "Compaction changed raw inspection history.");
        Check(!result.GetProperty("displayMessages").GetRawText().Contains("hidden context", StringComparison.Ordinal) &&
            result.GetProperty("historyMessages").GetRawText().Contains("hidden context", StringComparison.Ordinal), "Hidden display policy rewrote raw history.");
        Equal(50d, result.GetProperty("sessionStatistics").GetProperty("totals").GetProperty("total").GetDouble());
        Equal(40d, result.GetProperty("branchStatistics").GetProperty("totals").GetProperty("total").GetDouble());
        Equal("checkpoint system", result.GetProperty("effectiveSystemMessage").GetProperty("content").GetString());
        Equal("Global name", result.GetProperty("sessionName").GetString());
        Check(result.GetProperty("readOnly").GetBoolean() && !result.GetProperty("networkUsed").GetBoolean(), "History acquired write/network authority.");
        Equal(source, await File.ReadAllTextAsync(files.Session));
        Equal(1, Directory.GetFiles(files.Root).Length);
    }
    private static async Task HistorySelections()
    {
        using var files = new Files(); var source = HistoryInput(files); await File.WriteAllTextAsync(files.Session, source, new UTF8Encoding(false));
        foreach (var leaf in new[] { "s", "u", "a", "h", "usage", "c", "after", "right", "name" })
        {
            var result = Success(await Child(files, "session", "history", "--session", files.Session, "--leaf", leaf));
            Equal(leaf, result.GetProperty("selectedLeafId").GetString()); Equal("Global name", result.GetProperty("sessionName").GetString());
            Equal(50d, result.GetProperty("sessionStatistics").GetProperty("totals").GetProperty("total").GetDouble());
            var branch = result.GetProperty("branchHistory").GetRawText();
            Check(leaf is "right" or "name" ? !branch.Contains("after compact", StringComparison.Ordinal) : !branch.Contains("right sibling", StringComparison.Ordinal), "Sibling leaked into selected history.");
        }
        var root = Success(await Child(files, "session", "history", "--session", files.Session, "--root"));
        Equal(0, root.GetProperty("branchHistory").GetArrayLength()); Equal(0, root.GetProperty("modelMessages").GetArrayLength());
        Equal(0d, root.GetProperty("branchStatistics").GetProperty("totals").GetProperty("total").GetDouble());
        Equal(50d, root.GetProperty("sessionStatistics").GetProperty("totals").GetProperty("total").GetDouble());
        Equal(source, await File.ReadAllTextAsync(files.Session));
    }
    private static async Task HistoryAdmission()
    {
        using var files = new Files(); var source = HistoryInput(files); await File.WriteAllTextAsync(files.Session, source, new UTF8Encoding(false));
        Refused(await Child(files, "session", "history", "--session", files.Session, "--workspace", files.Root), "InvalidArguments");
        Refused(await Child(files, "session", "history", "--session", files.Session, "--message", "private rejected message"), "InvalidArguments");
        foreach (var damaged in new[] { source + "{\"type\":\"message\",", source.Replace("\"version\":3", "\"version\":4", StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(files.Session, damaged, new UTF8Encoding(false));
            var failed = await Child(files, "session", "history", "--session", files.Session);
            Equal(1, failed.ExitCode); Equal("", failed.Error);
            var report = JsonData(failed.Output); Equal("recovery_required", report.GetProperty("status").GetString());
            Check(!report.TryGetProperty("sessionStatistics", out _), "Unsafe prefix acquired authoritative accounting.");
            Equal(damaged, await File.ReadAllTextAsync(files.Session));
        }
    }
    private static async Task HistoryProjectionFailures()
    {
        using var files = new Files(); var source = HistoryInput(files);
        foreach (var optional in new[] { "\"sections\":[\"private rejected message\"]", "\"toolsAdded\":\"private rejected message\"", "\"toolsRemoved\":[{\"name\":42}]" })
        {
            var bytes = source.Replace("\"content\":\"original system\"", "\"content\":\"original system\"," + optional, StringComparison.Ordinal);
            await File.WriteAllTextAsync(files.Session, bytes, new UTF8Encoding(false));
            await Recovery("UnsupportedMessage", bytes);
        }
        var header = JsonSerializer.Serialize(new { type = "session", version = 3, id = "bounded", timestamp = "2026-01-01T00:00:00.000Z", cwd = files.Root });
        // 6.4MiB of valid raw UTF-8 is within the unchanged 8MiB reader admission. The composed JSON
        // default escaping expands this system text beyond the unchanged 16Mi character replay bound.
        var bodies = Enumerable.Range(0, 16).Select(index => JsonSerializer.Serialize(new { type = "message", id = "s" + index,
            parentId = index == 0 ? null : "s" + (index - 1), timestamp = "2026-01-01T00:00:00.000Z",
            message = new { role = "system", content = new string('\u00e9', 200_000), timestamp = index } },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var bounded = header + "\n" + string.Join("\n", bodies) + "\n";
        Check(Encoding.UTF8.GetByteCount(bounded) < 8_388_608, "History fixture exceeded source admission.");
        await File.WriteAllTextAsync(files.Session, bounded, new UTF8Encoding(false));
        await Recovery("ResourceLimit", bounded);
        async Task Recovery(string failure, string original)
        {
            var result = await Child(files, "session", "history", "--session", files.Session);
            Equal(1, result.ExitCode); Equal("", result.Error); var report = JsonData(result.Output);
            Equal("recovery_required", report.GetProperty("status").GetString()); Equal(failure, report.GetProperty("historyProjectionFailure").GetString());
            Check(report.GetProperty("sourceComplete").GetBoolean() && report.GetProperty("readStatus").GetString() == "Complete" &&
                !report.TryGetProperty("sessionStatistics", out _) && !result.Output.Contains("private rejected message", StringComparison.Ordinal), "Unsupported history became authoritative prefix accounting or leaked rejected content.");
            Equal(original, await File.ReadAllTextAsync(files.Session));
        }
    }

    private static async Task SeparateProcesses()
    {
        using var files = new Files();
        var source = files.Child("source \u6587.txt"); var output = files.Child("result.txt");
        await File.WriteAllTextAsync(source, "existing user source\n", new UTF8Encoding(false));
        var firstScript = files.Child("first.json");
        await Script(firstScript, Tool("read", "read-1", new { path = source }, "first user"),
            Tool("write", "write-1", new { path = output, content = "authorized output \u6587\U0001f642\n" }, "existing user source"),
            Text("first final", "Successfully wrote"));
        var originalSource = await File.ReadAllBytesAsync(source); var scriptBytes = await File.ReadAllBytesAsync(firstScript);
        var created = Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root));
        Check(created.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Create returned before acknowledgement.");
        var creationBytes = await File.ReadAllBytesAsync(files.Session);
        var first = Success(await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", firstScript, "--message", "first user", "--allow-read", source, "--allow-write", output));
        Equal(3, first.GetProperty("requests").GetArrayLength());
        Equal(2, first.GetProperty("actions").GetArrayLength());
        Check(first.GetProperty("actions").EnumerateArray().All(action => action.GetProperty("allowed").GetBoolean()), "Explicit final targets were denied.");
        Equal("authorized output \u6587\U0001f642\n", await File.ReadAllTextAsync(output));
        var firstBytes = await File.ReadAllBytesAsync(files.Session);
        Check(firstBytes.AsSpan().StartsWith(creationBytes), "Prompt changed creation bytes.");
        Equal((long)firstBytes.Length, first.GetProperty("committedByteLength").GetInt64());
        var secondScript = files.Child("resume.json");
        await Script(secondScript, Text("resumed final", "first user", "first final", "existing user source", "Successfully wrote", "resumed user"));
        var resumed = Success(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", secondScript, "--message", "resumed user"));
        Equal(first.GetProperty("selectedLeafId").GetString(), resumed.GetProperty("previousSelectedLeafId").GetString());
        Equal(5, resumed.GetProperty("requests")[0].GetProperty("requiredHistoryChecks").GetInt32());
        Check(resumed.GetProperty("requests")[0].GetProperty("historyRequirementsSatisfied").GetBoolean(), "Reopened provider did not observe actual history.");
        var final = await File.ReadAllBytesAsync(files.Session);
        Check(final.AsSpan().StartsWith(firstBytes), "Independent resume changed previous durable bytes.");
        Equal((long)final.Length, resumed.GetProperty("committedByteLength").GetInt64());
        var sourceAfter = await File.ReadAllBytesAsync(source); var scriptAfter = await File.ReadAllBytesAsync(firstScript);
        Check(originalSource.SequenceEqual(sourceAfter) && scriptBytes.SequenceEqual(scriptAfter), "Existing source/script bytes were changed.");
        Check(!Encoding.UTF8.GetString(final).Contains("authored-inert-offline-session-value", StringComparison.Ordinal), "Inert request authorization entered session data.");
    }

    private static async Task SelectedBranch()
    {
        using var files = new Files();
        Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root));
        var script = files.Child("text.json");
        await Script(script, Text("ancestor final", "ancestor user"));
        var first = Success(await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "ancestor user"));
        var ancestor = first.GetProperty("selectedLeafId").GetString()!;
        await Script(script, Text("later final", "ancestor user", "later user"));
        var later = Success(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "later user"));
        var oldLog = await new SessionLogReader().ReadFileAsync(files.Session);
        var oldBytes = oldLog.OriginalBytes.ToArray(); var oldRecords = oldLog.ValidatedPrefix.Length;
        await Script(script, Text("branch final", "ancestor user", "branch user"));
        var branch = Success(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "branch user", "--leaf", ancestor));
        Equal(ancestor, branch.GetProperty("previousSelectedLeafId").GetString());
        var branched = await new SessionLogReader().ReadFileAsync(files.Session);
        Equal(ancestor, branched.ValidatedPrefix[oldRecords].Entry.ParentId);
        Check(branched.OriginalBytes.AsSpan().StartsWith(oldBytes), "Branch append rewrote sibling bytes.");
        var before = await File.ReadAllBytesAsync(files.Session);
        var selected = Success(await Child(files, "session", "inspect", "--session", files.Session, "--leaf", later.GetProperty("selectedLeafId").GetString()!));
        Check(selected.GetProperty("llmMessages").EnumerateArray().Any(message => message.GetRawText().Contains("later user", StringComparison.Ordinal)), "Explicit old leaf was not inspected.");
        Check(selected.GetProperty("llmMessages").EnumerateArray().All(message => !message.GetRawText().Contains("branch user", StringComparison.Ordinal)), "Sibling leaked into selected context.");
        var tree = Success(await Child(files, "session", "tree", "--session", files.Session));
        Equal(2, tree.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("Id").GetString() == ancestor).GetProperty("children").GetArrayLength());
        var root = Success(await Child(files, "session", "inspect", "--session", files.Session, "--root"));
        Equal(0, root.GetProperty("llmMessages").GetArrayLength());
        var after = await File.ReadAllBytesAsync(files.Session);
        Check(before.SequenceEqual(after), "Read-only inspection/tree mutated the source log.");
    }

    private static async Task FilePolicy()
    {
        using var files = new Files();
        Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root));
        var target = files.Child("must-not-exist.txt"); var script = files.Child("denial.json");
        await Script(script, Tool("write", "denied-write", new { path = target, content = "not authorized" }), Text("denial observed"));
        var denied = await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "deny without explicit allowance");
        Equal(1, denied.ExitCode); Equal("", denied.Error);
        var report = JsonData(denied.Output);
        Check(report.GetProperty("toolErrors").GetBoolean() && !report.GetProperty("actions")[0].GetProperty("allowed").GetBoolean(), "Policy denial was hidden as success.");
        Check(!File.Exists(target), "Unallowed native write effect occurred.");
        var original = await File.ReadAllBytesAsync(files.Session); var fixture = await File.ReadAllBytesAsync(script);
        Refused(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root, "--offline-script", script,
            "--message", "protected", "--allow-write", files.Session), "ReservedTarget");
        Refused(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root, "--offline-script", script,
            "--message", "protected script", "--allow-write", script), "ReservedTarget");
        Refused(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root, "--offline-script", script,
            "--message", "escape", "--allow-write", Path.Combine(Path.GetDirectoryName(files.Root)!, "outside.txt")), "ReservedTarget");
        var after = await File.ReadAllBytesAsync(files.Session); var fixtureAfter = await File.ReadAllBytesAsync(script);
        Check(original.SequenceEqual(after) && fixture.SequenceEqual(fixtureAfter), "Rejected authorization mutated source bytes.");
    }

    private static async Task DamagedInspection()
    {
        using var files = new Files();
        Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root));
        await File.AppendAllTextAsync(files.Session, """{"type":"message","id":"truncated""");
        var before = await File.ReadAllBytesAsync(files.Session);
        var inspected = await Child(files, "session", "inspect", "--session", files.Session);
        Equal(1, inspected.ExitCode); Equal("", inspected.Error);
        var report = JsonData(inspected.Output);
        Equal("recovery_required", report.GetProperty("status").GetString());
        Equal("recovery_required", report.GetProperty("resumeEligibility").GetString());
        Check(report.GetProperty("diagnostics").GetArrayLength() > 0 && report.GetProperty("llmMessages").ValueKind == JsonValueKind.Null,
            "Damaged prefix gained a successful runnable projection.");
        var script = files.Child("unused.json"); await Script(script, Text("unused"));
        var resumed = await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "cannot repair");
        Equal(1, resumed.ExitCode); Equal("", resumed.Output);
        Equal("CommandFailed", JsonData(resumed.Error).GetProperty("code").GetString());
        var after = await File.ReadAllBytesAsync(files.Session);
        Check(before.SequenceEqual(after), "Damaged log was silently repaired or appended.");
    }

    private static async Task Admission()
    {
        using var files = new Files();
        Refused(await Child(files, "session", "create", "--session", "relative.jsonl", "--workspace", files.Root), "InvalidPath");
        Refused(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root, "--unknown", "x"), "InvalidArguments");
        Check(!File.Exists(files.Session), "Rejected create acquired a session.");
        Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root));
        var before = await File.ReadAllBytesAsync(files.Session);
        var existing = await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root);
        Equal(1, existing.ExitCode); Equal("", existing.Output);
        var script = files.Child("bad.json"); await File.WriteAllTextAsync(script, """{"schemaVersion":1,"turns":[}""");
        Refused(await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "private rejected message"), "InvalidScript");
        Refused(await Child(files, "session", "inspect", "--session", files.Session, "--root", "--leaf", "x"), "InvalidArguments");
        Refused(await Child(files, "session", "inspect", "--session", files.Session, "--session", files.Session), "InvalidArguments");
        await File.WriteAllTextAsync(script, new string(' ', 1_048_577));
        Refused(await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "oversized fixture"), "ResourceLimit");
        var after = await File.ReadAllBytesAsync(files.Session);
        Check(before.SequenceEqual(after), "Rejected commands changed the existing log.");
    }

    private static async Task Cancellation()
    {
        using var files = new Files();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = await SessionCommands.RunAsync(["session", "create", "--session", files.Session, "--workspace", files.Root], output, error, canceled.Token);
        Equal(1, code); Equal("", output.ToString());
        Equal("Canceled", JsonData(error.ToString()).GetProperty("code").GetString());
        Check(!File.Exists(files.Session), "Pre-canceled command created durable storage.");
    }

    private static async Task AnthropicProcesses()
    {
        using var files = new Files();
        var source = files.Child("anthropic-source.txt"); var target = files.Child("anthropic-written.txt");
        var script = files.Child("anthropic.json");
        await File.WriteAllTextAsync(source, "Anthropic source \u6587\n", new UTF8Encoding(false));
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var created = Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "anthropic-messages"));
        Equal("anthropic", created.GetProperty("model").GetProperty("Provider").GetString());
        Equal("anthropic-messages", created.GetProperty("offlineApi").GetString());
        var initial = await File.ReadAllBytesAsync(files.Session);
        await Script(script,
            AnthropicTool("read", "ant-read", new { path = source }, ["Anthropic user"], AnthropicInitialRequest("Anthropic user")),
            AnthropicTool("write", "ant-write", new { path = target, content = "Anthropic saved \U0001f642\n" }, ["Anthropic source"]),
            AnthropicText("Anthropic final", required: ["Successfully wrote"]));
        var scriptBytes = await File.ReadAllBytesAsync(script);
        var first = Success(await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "anthropic-messages", "--offline-script", script, "--message", "Anthropic user",
            "--allow-read", source, "--allow-write", target));
        Equal(3, first.GetProperty("requests").GetArrayLength());
        foreach (var request in first.GetProperty("requests").EnumerateArray())
        {
            Equal("anthropic-messages", request.GetProperty("api").GetString());
            Equal("https://offline-session.invalid/v1/messages?beta=true", request.GetProperty("requestUri").GetString());
            Equal("x-api-key", request.GetProperty("authHeader").GetString());
            Equal("application/json", request.GetProperty("contentType").GetString());
            Check(request.GetProperty("authoredInertAuthValidated").GetBoolean(), "Actual HTTP request bypassed inert authentication admission.");
            Check(request.GetProperty("toolNames").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["read", "write"]),
                "Anthropic actual HTTP request lost ordered declarations.");
        }
        Equal("Anthropic saved \U0001f642\n", await File.ReadAllTextAsync(target));
        var firstBytes = await File.ReadAllBytesAsync(files.Session);
        var scriptAfterPrompt = await File.ReadAllBytesAsync(script);
        Check(scriptBytes.SequenceEqual(scriptAfterPrompt), "Anthropic prompt changed its authored wire script.");
        Check(firstBytes.AsSpan().StartsWith(initial), "Anthropic prompt rewrote initial durable bytes.");
        Equal((long)firstBytes.Length, first.GetProperty("committedByteLength").GetInt64());
        Check(first.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Anthropic prompt reported before durable acknowledgment.");
        var ancestor = first.GetProperty("selectedLeafId").GetString()!;
        await Script(script, AnthropicText("Anthropic resumed", required: ["Anthropic user", "Anthropic final", "Anthropic source", "Successfully wrote", "resume user"]));
        var resumed = Success(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "anthropic-messages", "--offline-script", script, "--message", "resume user"));
        Equal(ancestor, resumed.GetProperty("previousSelectedLeafId").GetString());
        Equal(5, resumed.GetProperty("requests")[0].GetProperty("requiredHistoryChecks").GetInt32());
        var beforeBranch = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(beforeBranch.OriginalBytes.AsSpan().StartsWith(firstBytes), "Independent Anthropic resume rewrote acknowledged history.");
        await Script(script, AnthropicText("Anthropic branch", required: ["Anthropic user", "Anthropic final", "branch user"]));
        var branch = Success(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "anthropic-messages", "--offline-script", script, "--message", "branch user", "--leaf", ancestor));
        Equal(ancestor, branch.GetProperty("previousSelectedLeafId").GetString());
        var afterBranch = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(afterBranch.OriginalBytes.AsSpan().StartsWith(beforeBranch.OriginalBytes.AsSpan()), "Anthropic selected branch rewrote sibling bytes.");
        Equal(ancestor, afterBranch.ValidatedPrefix[beforeBranch.ValidatedPrefix.Length].Entry.ParentId);
        var selected = Success(await Child(files, "session", "inspect", "--session", files.Session));
        Check(!selected.GetProperty("llmMessages").GetRawText().Contains("resume user", StringComparison.Ordinal), "Anthropic branch flattened sibling history.");
        var assistants = selected.GetProperty("llmMessages").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "assistant").ToArray();
        Check(assistants.Length == 4 && assistants.All(message => message.GetProperty("api").GetString() == "anthropic-messages" &&
            message.GetProperty("provider").GetString() == "anthropic"), "Durable assistants lost selected API identity.");
        var sourceAfter = await File.ReadAllBytesAsync(source);
        Check(sourceBytes.SequenceEqual(sourceAfter), "Anthropic read changed source contents.");
        Check(!Encoding.UTF8.GetString(afterBranch.OriginalBytes.ToArray()).Contains("authored-inert-offline-session-value", StringComparison.Ordinal),
            "Offline request authorization entered durable history.");
        await using var reopened = await SessionLogStore.OpenAsync(files.Session);
        Equal((long)afterBranch.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
    }

    private static async Task AnthropicAdmission()
    {
        using var files = new Files();
        Refused(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "anthropic-messages", "--api-key", "private rejected message"), "InvalidArguments");
        Refused(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "unknown-provider"), "InvalidArguments");
        Refused(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "anthropic-messages", "--offline-api", "openai-responses"), "InvalidArguments");
        Check(!File.Exists(files.Session), "Rejected provider/auth flags acquired a durable log.");
        Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root, "--offline-api", "anthropic-messages"));
        var before = await File.ReadAllBytesAsync(files.Session); var script = files.Child("family.json"); var target = files.Child("never-written.txt");
        await Script(script, Tool("write", "wrong-api", new { path = target, content = "must not execute" }));
        Refused(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "wrong runtime", "--allow-write", target), "OfflineProviderMismatch");
        Refused(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root, "--offline-api", "anthropic-messages",
            "--offline-script", script, "--message", "wrong wire family", "--allow-write", target), "InvalidScript");
        var afterRejected = await File.ReadAllBytesAsync(files.Session);
        Check(before.SequenceEqual(afterRejected) && !File.Exists(target), "Wrong family admission changed durable state or files.");
        await Script(script, AnthropicTool("write", "wrong-body", new { path = target, content = "must not execute" },
            expectedRequest: new { model = "private wrong expected request" }));
        var mismatch = await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "anthropic-messages", "--offline-script", script, "--message", "request mismatch", "--allow-write", target);
        Equal(1, mismatch.ExitCode); Equal("", mismatch.Error);
        var failure = JsonData(mismatch.Output);
        Equal("completed_with_errors", failure.GetProperty("status").GetString());
        Equal(0, failure.GetProperty("requests").GetArrayLength()); Equal(0, failure.GetProperty("actions").GetArrayLength());
        Equal("error", failure.GetProperty("finalAssistant").GetProperty("stopReason").GetString());
        Check(!File.Exists(target) && !mismatch.Output.Contains("private wrong expected request", StringComparison.Ordinal),
            "Rejected actual HTTP request granted executable response authority or leaked its expected body.");
        using var other = new Files();
        Success(await Child(other, "session", "create", "--session", other.Session, "--workspace", other.Root));
        var responseBytes = await File.ReadAllBytesAsync(other.Session); var otherScript = other.Child("anthropic.json");
        await Script(otherScript, AnthropicText("unused"));
        Refused(await Child(other, "session", "resume", "--session", other.Session, "--workspace", other.Root,
            "--offline-api", "anthropic-messages", "--offline-script", otherScript, "--message", "reverse wrong runtime"), "OfflineProviderMismatch");
        var responseAfter = await File.ReadAllBytesAsync(other.Session);
        Check(responseBytes.SequenceEqual(responseAfter), "Reverse wrong family rewrote Responses session.");
    }

    private static async Task CompletionsProcesses()
    {
        using var files = new Files(); var source = files.Child("completions-source.txt"); var target = files.Child("completions-result.txt");
        var script = files.Child("completions.json");
        const string saved = "saved \u6587\0value\n";
        const string replaySaved = "\"content\":\"saved \u6587\\u0000value\\n\"";
        await File.WriteAllTextAsync(source, "Completions source \u6587\n", new UTF8Encoding(false));
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var created = Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "openai-completions"));
        Equal("openai-completions", created.GetProperty("offlineApi").GetString());
        Equal("pisharp-offline-completions-session", created.GetProperty("model").GetProperty("ModelId").GetString());
        var initial = await File.ReadAllBytesAsync(files.Session);
        await Script(script, CompletionsTool("read", "completion-read", new { path = source }, ["Completions user"], CompletionsInitialRequest("Completions user")),
            CompletionsTool("write", "completion-write", new { path = target, content = saved }, ["Completions source"]),
            CompletionsText("Completions final \U0001f642", required: ["Successfully wrote", replaySaved]));
        var scriptBytes = await File.ReadAllBytesAsync(script);
        var first = Success(await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "openai-completions", "--offline-script", script, "--message", "Completions user",
            "--allow-read", source, "--allow-write", target));
        Equal(3, first.GetProperty("requests").GetArrayLength()); Equal(2, first.GetProperty("actions").GetArrayLength());
        Check(first.GetProperty("actions").EnumerateArray().All(value => value.GetProperty("allowed").GetBoolean()), "Explicit Completions file targets were denied.");
        foreach (var request in first.GetProperty("requests").EnumerateArray())
        {
            Equal("openai-completions", request.GetProperty("api").GetString()); Equal("openai", request.GetProperty("provider").GetString());
            Equal("https://offline-session.invalid/v1/chat/completions", request.GetProperty("requestUri").GetString());
            Equal("application/json", request.GetProperty("contentType").GetString());
            Check(request.GetProperty("authoredInertAuthValidated").GetBoolean() && request.GetProperty("historyRequirementsSatisfied").GetBoolean(), "Actual fake HTTP request bypassed profile checks.");
            Check(request.GetProperty("toolNames").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "read", "write" }), "Completions nested function declarations were lost.");
        }
        var assistant = first.GetProperty("finalAssistant"); Equal("openai-completions", assistant.GetProperty("api").GetString());
        Equal("cmpl-text", assistant.GetProperty("responseId").GetString()); Equal("stop", assistant.GetProperty("rawStopReason").GetString());
        Equal(6L, assistant.GetProperty("usage").GetProperty("input").GetInt64()); Equal(2L, assistant.GetProperty("usage").GetProperty("cacheRead").GetInt64());
        Equal(4L, assistant.GetProperty("usage").GetProperty("output").GetInt64()); Equal(12L, assistant.GetProperty("usage").GetProperty("totalTokens").GetInt64());
        var written = await File.ReadAllBytesAsync(target); Check(written.SequenceEqual(Encoding.UTF8.GetBytes(saved)), "Completions tool changed inert NUL/Unicode file bytes.");
        var scriptAfterFirst = await File.ReadAllBytesAsync(script); Check(scriptBytes.SequenceEqual(scriptAfterFirst), "Completions command rewrote its literal script.");
        var firstLog = await new SessionLogReader().ReadFileAsync(files.Session); var firstBytes = firstLog.OriginalBytes.ToArray();
        var plainUser = firstLog.ValidatedPrefix.Where(record => record.Entry.Type == "message")
            .Select(record => record.Entry.WireBody.Value.GetProperty("message"))
            .Single(message => message.GetProperty("role").GetString() == "user").GetProperty("content");
        Check(plainUser.ValueKind == JsonValueKind.String && plainUser.GetString() == "Completions user",
            "Reload routing changed the plain one-shot scalar transcript.");
        Check(firstLog.Status == SessionLogReadStatus.Complete && firstLog.SourceComplete && firstBytes.AsSpan().StartsWith(initial), "Completions prompt damaged or rewrote creation bytes.");
        Equal((long)firstBytes.Length, first.GetProperty("committedByteLength").GetInt64());
        var call = firstLog.ValidatedPrefix.Where(record => record.Entry.Type == "message")
            .Select(record => record.Entry.WireBody.Value.GetProperty("message")).Where(message => message.GetProperty("role").GetString() == "assistant")
            .SelectMany(message => message.GetProperty("content").EnumerateArray()).Single(part => part.GetProperty("type").GetString() == "toolCall" && part.GetProperty("name").GetString() == "write");
        Equal("completion-write", call.GetProperty("id").GetString()); Equal(saved, call.GetProperty("arguments").GetProperty("content").GetString());
        var ancestor = first.GetProperty("selectedLeafId").GetString()!;
        await Script(script, CompletionsText("Completions resumed", required: ["Completions user", "Completions final", "Completions source", replaySaved, "resumed user"]));
        var resumed = Success(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "openai-completions", "--offline-script", script, "--message", "resumed user"));
        Equal(ancestor, resumed.GetProperty("previousSelectedLeafId").GetString());
        var resumedLog = await new SessionLogReader().ReadFileAsync(files.Session); var resumedBytes = resumedLog.OriginalBytes.ToArray();
        Check(resumedBytes.AsSpan().StartsWith(firstBytes), "Completions independent resume rewrote accepted history.");
        Equal((long)resumedBytes.Length, resumed.GetProperty("committedByteLength").GetInt64());
        await Script(script, CompletionsText("Completions branch", required: ["Completions user", "Completions final", replaySaved, "branch user"]));
        Success(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "openai-completions", "--offline-script", script, "--message", "branch user", "--leaf", ancestor));
        var branch = await new SessionLogReader().ReadFileAsync(files.Session);
        Equal(ancestor, branch.ValidatedPrefix[resumedLog.ValidatedPrefix.Length].Entry.ParentId);
        Check(branch.OriginalBytes.AsSpan().StartsWith(resumedBytes), "Completions branch rewrote physical sibling records.");
        var selected = Success(await Child(files, "session", "inspect", "--session", files.Session));
        Check(!selected.GetProperty("llmMessages").GetRawText().Contains("resumed user", StringComparison.Ordinal), "Selected Completions ancestry inserted a sibling input.");
        var sourceAfter = await File.ReadAllBytesAsync(source);
        Check(sourceBytes.SequenceEqual(sourceAfter), "Completions source bytes were changed.");
        Check(!Encoding.UTF8.GetString(branch.OriginalBytes.ToArray()).Contains("authored-inert-offline-session-value", StringComparison.Ordinal), "Request key entered Completions durable history.");
        await using var reopened = await SessionLogStore.OpenAsync(files.Session); Equal((long)branch.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
    }

    private static async Task CompletionsAdmission()
    {
        using var files = new Files(); var script = files.Child("invalid-completions.json"); var target = files.Child("never-created.txt");
        Success(await Child(files, "session", "create", "--session", files.Session, "--workspace", files.Root, "--offline-api", "openai-completions"));
        var before = await File.ReadAllBytesAsync(files.Session);
        foreach (var wrong in new[] { Text("wrong Responses"), AnthropicText("wrong Anthropic"),
            new { events = new[] { new { choices = "not an array" } } },
            new { events = new[] { JsonSerializer.Deserialize<JsonElement>("{\"choices\":[],\"opaque\":1e999}") } } })
        {
            await Script(script, wrong);
            Refused(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
                "--offline-api", "openai-completions", "--offline-script", script, "--message", "must not append"), "InvalidScript");
        }
        await Script(script, Text("wrong registry"));
        Refused(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", script, "--message", "wrong API model"), "OfflineProviderMismatch");
        await Script(script, CompletionsText(new string('x', 140_000)));
        Refused(await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", "openai-completions", "--offline-script", script, "--message", "large literal chunk"), "ResourceLimit");
        var unchanged = await File.ReadAllBytesAsync(files.Session); Check(before.SequenceEqual(unchanged), "Completions preflight/API rejection appended durable input.");
        foreach (var failure in new[] { "request", "arguments", "eof" })
        {
            object turn = failure == "request" ? CompletionsTool("write", "request-denial", new { path = target, content = "no authority" }, expectedRequest: new { model = "private wrong expected request" }) :
                failure == "arguments" ? CompletionsTool("write", "bad-arguments", new { path = target, content = "no authority" }, rawArguments: "{") :
                CompletionsText("partial text cannot become success", finish: false);
            await Script(script, turn);
            var failed = await Child(files, "session", "prompt", "--session", files.Session, "--workspace", files.Root,
                "--offline-api", "openai-completions", "--offline-script", script, "--message", failure, "--allow-write", target);
            Equal(1, failed.ExitCode); Equal("", failed.Error); var report = JsonData(failed.Output);
            Equal("completed_with_errors", report.GetProperty("status").GetString()); Equal("error", report.GetProperty("finalAssistant").GetProperty("stopReason").GetString());
            Equal(0, report.GetProperty("actions").GetArrayLength()); Equal(failure == "request" ? 0 : 1, report.GetProperty("requests").GetArrayLength());
            Check(!report.GetProperty("finalAssistant").TryGetProperty("openAICompletionsFailure", out _), "Native diagnostic leaked into the Pi assistant body.");
            if (failure != "request")
            {
                var diagnostic=report.GetProperty("nativeDiagnostics").GetProperty("entries").EnumerateArray().Last();
                Equal(failure == "arguments" ? "MalformedStream" : "UnexpectedEof", diagnostic.GetProperty("diagnostic").GetProperty("code").GetString());
                Equal("declared-native-record",diagnostic.GetProperty("provenance").GetString());
                var inspected=await Child(files,"session","inspect","--session",files.Session);
                Success(inspected);
                var reopenedDiagnostic=JsonData(inspected.Output).GetProperty("nativeDiagnostics").GetProperty("entries").EnumerateArray().Last();
                Equal(diagnostic.GetRawText(),reopenedDiagnostic.GetRawText());
            }
            Check(!File.Exists(target) && !failed.Output.Contains("private wrong expected request", StringComparison.Ordinal), "Failed literal stream/request acquired effect authority or leaked expected payload.");
            var closed = await new SessionLogReader().ReadFileAsync(files.Session); Check(closed.SourceComplete && closed.Status == SessionLogReadStatus.Complete, "Completions wire failure left an incomplete log.");
            Equal((long)closed.OriginalBytes.Length, report.GetProperty("committedByteLength").GetInt64());
            await using var reopened = await SessionLogStore.OpenAsync(files.Session); Equal((long)closed.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
        }
    }

    // Authored source-informed literal SSE observations, independent of the native request/stream projectors.
    internal static object CompletionsText(string text, bool gate = false, string[]? required = null, object? expectedRequest = null, bool finish = true)
    {
        var middle = text.Length / 2;
        if (middle > 0 && char.IsHighSurrogate(text[middle - 1])) middle--;
        var events = new List<object>
        {
            new { id = "cmpl-text", @object = "chat.completion.chunk", model = "pisharp-offline-completions-session", choices = new[] { new { index = 0, delta = new { role = "assistant", content = text[..middle] }, finish_reason = (string?)null } } },
            new { choices = new[] { new { index = 0, delta = new { content = text[middle..] }, finish_reason = (string?)null } } }
        };
        if (finish) { events.Add(CompletionsFinish("stop")); events.Add(CompletionsUsage()); }
        var result = new Dictionary<string, object?> { ["requiredInputTexts"] = required ?? [], ["events"] = events };
        if (gate) result["rpcGate"] = new { releaseOnGetStateId = "release" };
        if (expectedRequest is not null) result["expectedRequest"] = expectedRequest;
        return result;
    }
    internal static object CompletionsTool(string name, string call, object arguments, string[]? required = null,
        object? expectedRequest = null, string? rawArguments = null)
    {
        var json = rawArguments ?? JsonSerializer.Serialize(arguments); var middle = json.Length / 2;
        var result = new Dictionary<string, object?> { ["requiredInputTexts"] = required ?? [], ["events"] = new object[]
        {
            new { id = "cmpl-" + call, @object = "chat.completion.chunk", model = "pisharp-offline-completions-session", choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 9, id = call, type = "function", function = new { name, arguments = json[..middle] } } } }, finish_reason = (string?)null } } },
            new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 9, function = new { arguments = json[middle..] } } } }, finish_reason = (string?)null } } },
            CompletionsFinish("tool_calls"), CompletionsUsage()
        } };
        if (expectedRequest is not null) result["expectedRequest"] = expectedRequest;
        return result;
    }
    private static object CompletionsFinish(string reason) => new { choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } } };
    private static object CompletionsUsage() => new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 8, completion_tokens = 4,
        total_tokens = 12, prompt_tokens_details = new { cached_tokens = 2, cache_write_tokens = 0 } } };
    private static object CompletionsInitialRequest(string user)
    {
        // Anthropic's authored input schemas omit additionalProperties, as its converter does.
        // Completions preserves the closed registered schemas; expected fields stay authored here.
        var declarations = JsonSerializer.SerializeToElement(AnthropicInitialRequest(user)).GetProperty("tools");
        return new { model = "pisharp-offline-completions-session", stream = true, store = false,
            max_completion_tokens = 8192, stream_options = new { include_usage = true },
            messages = new[] { new { role = "system", content = "Explicit offline session file tools." }, new { role = "user", content = user } },
            tools = declarations.EnumerateArray().Select(tool => new { type = "function", function = new { name = tool.GetProperty("name").GetString(),
                description = tool.GetProperty("description").GetString(), parameters = new
                {
                    type = tool.GetProperty("input_schema").GetProperty("type").GetString(),
                    properties = tool.GetProperty("input_schema").GetProperty("properties").Clone(),
                    required = tool.GetProperty("input_schema").GetProperty("required").Clone(),
                    additionalProperties = false
                } } }).ToArray() };
    }
    internal static object AnthropicText(string text, bool gate = false, string[]? required = null, object? expectedRequest = null)
    {
        var result = new Dictionary<string, object?> { ["requiredInputTexts"] = required ?? [], ["events"] = new object[]
        {
            AnthropicStart("msg-text"),
            new { type = "content_block_start", index = 4, content_block = new { type = "text", text = "" } },
            new { type = "content_block_delta", index = 4, delta = new { type = "text_delta", text } },
            new { type = "content_block_stop", index = 4 }, AnthropicFinish("end_turn"), new { type = "message_stop" }
        } };
        if (gate) result["rpcGate"] = new { releaseOnGetStateId = "release" };
        if (expectedRequest is not null) result["expectedRequest"] = expectedRequest;
        return result;
    }
    internal static object AnthropicTool(string name, string call, object arguments, string[]? required = null, object? expectedRequest = null)
    {
        var json = JsonSerializer.Serialize(arguments); var split = json.Length / 2;
        var result = new Dictionary<string, object?> { ["requiredInputTexts"] = required ?? [], ["events"] = new object[]
        {
            AnthropicStart("msg-" + call),
            new { type = "content_block_start", index = 9, content_block = new { type = "tool_use", id = call, name, input = new { } } },
            new { type = "content_block_delta", index = 9, delta = new { type = "input_json_delta", partial_json = json[..split] } },
            new { type = "content_block_delta", index = 9, delta = new { type = "input_json_delta", partial_json = json[split..] } },
            new { type = "content_block_stop", index = 9 }, AnthropicFinish("tool_use"), new { type = "message_stop" }
        } };
        if (expectedRequest is not null) result["expectedRequest"] = expectedRequest;
        return result;
    }
    private static object AnthropicStart(string id) => new { type = "message_start", message = new { id, role = "assistant",
        model = "pisharp-offline-session", content = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 0,
            cache_read_input_tokens = 0, cache_creation_input_tokens = 0 } } };
    private static object AnthropicFinish(string reason) => new { type = "message_delta", delta = new { stop_reason = reason },
        usage = new { output_tokens = 4 } };
    internal static object AnthropicInitialRequest(string user) => new
    {
        model = "pisharp-offline-session", max_tokens = 8192, stream = true,
        messages = new[] { new { role = "user", content = new[] { new { type = "text", text = user, cache_control = new { type = "ephemeral" } } } } },
        system = new[] { new { type = "text", text = "Explicit offline session file tools.", cache_control = new { type = "ephemeral" } } },
        tools = new object[]
        {
            new { name = "read", description = "Read UTF-8 text file contents, capped at 2000 lines or 50 KiB. Use offset/limit to continue. Images, binary and other encodings are unsupported in this profile.",
                input_schema = new { type = "object", properties = new { path = new { type = "string", description = "Path to the file to read (relative or absolute)" },
                    offset = new { type = "integer", minimum = 1, description = "Line number to start reading from (1-indexed)" },
                    limit = new { type = "integer", minimum = 0, description = "Maximum number of lines to read" } }, required = new[] { "path" } }, eager_input_streaming = true },
            new { name = "write", description = "Write UTF-8 text content to a file, creating parent directories and overwriting existing contents. Bounded text profile; this is not atomic replacement.",
                input_schema = new { type = "object", properties = new { path = new { type = "string", description = "Path to the file to write (relative or absolute)" },
                    content = new { type = "string", description = "Content to write to the file" } }, required = new[] { "path", "content" } },
                eager_input_streaming = true, cache_control = new { type = "ephemeral" } }
        }
    };

    private static async Task Script(string path, params object[] turns) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), new UTF8Encoding(false));
    private static object Tool(string name, string call, object arguments, params string[] required)
    {
        var id = "fc-" + call; var json = JsonSerializer.Serialize(arguments);
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id, call_id = call, name, arguments = "" } },
            new { type = "response.function_call_arguments.delta", output_index = 0, item_id = id, delta = json[..(json.Length / 2)] },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id, call_id = call, name, arguments = json } }, Completed()
        } };
    }
    private static object Text(string text, params string[] required)
    {
        var id = "msg-" + text.Replace(' ', '-');
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id, content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = id, delta = text },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id, content = new[] { new { type = "output_text", text } } } }, Completed()
        } };
    }
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(),
        usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private sealed record Result(int ExitCode, string Output, string Error);
    private static async Task<Result> Child(Files files, params string[] arguments)
    {
        var start = new ProcessStartInfo(_host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["ANTHROPIC_API_KEY"] = "sk-ant-oat-authored-unused-environment-noncredential";
        start.Environment["OPENAI_API_KEY"] = "authored-unused-environment-noncredential";
        start.ArgumentList.Add(_cli); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Check(process.Start(), "Compiled CLI child did not start.");
        var output = Read(process.StandardOutput, deadline.Token); var error = Read(process.StandardError, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, await output, await error); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            foreach (var task in new[] { output, error }) try { await task; } catch (Exception) { }
        }
    }
    private static async Task<string> Read(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, token); if (count == 0) return result.ToString();
            if (count > 1_048_576 - result.Length) throw new InvalidOperationException("Child output exceeds authored bound.");
            result.Append(buffer, 0, count);
        }
    }
    private sealed class Files : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string Session => Child("session.jsonl");
        public string Child(string name) => Path.Combine(Root, name);
        public Files() { Root = Path.Combine(_parent, "pisharp-session-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-session-cli-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned recursive test cleanup.");
            Directory.Delete(Root, recursive: true);
        }
    }
    private static JsonElement JsonData(string text)
    {
        Check(text.EndsWith('\n') && text.Count(character => character == '\n') == 1, "Command did not emit one LF JSONL record.");
        using var document = JsonDocument.Parse(text); return document.RootElement.Clone();
    }
    private static JsonElement Success(Result result)
    {
        static string Bounded(string text) => JsonSerializer.Serialize(text.Length <= 1536 ? text : text[..1536] + "[truncated]");
        var receipt = "exitCode=" + result.ExitCode + ", stdout=" + Bounded(result.Output) + ", stderr=" + Bounded(result.Error);
        Check(result.ExitCode == 0 && result.Error.Length == 0, "Expected successful CLI receipt; " + receipt);
        var body = JsonData(result.Output);
        Check(body.GetProperty("status").GetString() == "completed", "Expected completed CLI receipt; " + receipt);
        return body;
    }
    private static void Refused(Result result, string code)
    { Equal(2, result.ExitCode); Equal("", result.Output); var body = JsonData(result.Error); Equal(code, body.GetProperty("code").GetString()); Check(!result.Error.Contains("private rejected message", StringComparison.Ordinal), "Rejected payload leaked."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
}
