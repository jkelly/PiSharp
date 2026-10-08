using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static partial class NativeExtensionSessionCommandTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> HelloSchemaCases(string host, string cli)
    {
        _ = Cases(host, cli); // Reuse explicit compiled-path admission; this does not run the legacy cases.
        return
        [
            ("authored native Hello schema executes through Responses and durable independent reopen/branch", HelloResponses),
            ("authored native Hello schema executes through Anthropic and durable independent reopen/branch", HelloAnthropic),
            ("authored native Hello schema executes through Completions and durable independent reopen/branch", HelloCompletions),
            ("authored native Hello keeps initial/final schema, zero tool grants and native write denial authoritative", HelloAuthority)
        ];
    }
    private static Task HelloResponses() => HelloDurable("openai-responses");
    private static Task HelloAnthropic() => HelloDurable("anthropic-messages");
    private static Task HelloCompletions() => HelloDurable("openai-completions");

    private static async Task HelloDurable(string api)
    {
        using var files = await Files.CreateAsync(); await ConfigureHello(files, enabled: true);
        _ = HelloSuccess(await HelloRun(files, "create", api), api + ":create");
        var initial = await Complete(files); HelloLoadout(initial, enabled: true);
        const string user = "authored Hello user", name = "Ada \U0001f44b\nnext", callId = "native-hello-1";
        var arguments = new { name, opaque = new { ordered = new object?[] { 2, null, "\u6587" }, future = (object?)null } };
        var rawArguments = JsonSerializer.Serialize(arguments); var greeting = "Hello, " + name + "!";
        var turn = JsonSerializer.SerializeToNode(Tool(api, "hello", callId, arguments, user))!;
        turn["expectedRequest"] = HelloInitialRequest(api, user);
        await Script(files, turn, Text(api, "Hello final", [greeting]));
        var script = await File.ReadAllBytesAsync(files.Script);
        var report = HelloSuccess(await HelloRun(files, "prompt", api, user), api + ":prompt");
        Equal(2, report.GetProperty("usedScriptTurns").GetInt32()); Equal(2, report.GetProperty("requests").GetArrayLength());
        var actions = report.GetProperty("actions"); Equal(1, actions.GetArrayLength());
        Equal("hello", actions[0].GetProperty("ToolName").GetString()); Equal("fixture.cli/1/hello", actions[0].GetProperty("Target").GetString());
        Check(actions[0].GetProperty("allowed").GetBoolean(), "Actual final native extension grant was denied.");
        HelloRequests(report, api, enabled: true);
        var scriptAfter = await File.ReadAllBytesAsync(files.Script);
        Check(script.AsSpan().SequenceEqual(scriptAfter), "Tool execution rewrote authored wire input.");
        var log = await Complete(files); HelloLoadout(log, enabled: true);
        Check(log.OriginalBytes.AsSpan().StartsWith(initial.OriginalBytes.AsSpan()), "Hello command rewrote initial durable bytes.");
        var context = Context(log); var call = context.LlmMessages.Single(message => message.Role == "assistant" &&
            message.WireBody.Value.GetProperty("stopReason").GetString() == "toolUse").WireBody.Value.GetProperty("content")[0];
        Equal(HelloActualId(api, callId), call.GetProperty("id").GetString());
        Equal(rawArguments, call.GetProperty("arguments").GetRawText());
        var result = context.LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value;
        Equal(HelloActualId(api, callId), result.GetProperty("toolCallId").GetString());
        Check(!result.GetProperty("isError").GetBoolean(), "Hello result acquired error disposition.");
        Equal(1, result.GetProperty("content").GetArrayLength()); Equal("text", result.GetProperty("content")[0].GetProperty("type").GetString());
        Equal(greeting, result.GetProperty("content")[0].GetProperty("text").GetString());
        Equal(JsonSerializer.Serialize(new { greeted = name }), result.GetProperty("details").GetRawText());
        var receipt = HelloReceipt(files); Equal(HelloActualId(api, callId), receipt.GetProperty("id").GetString());
        Equal(rawArguments, receipt.GetProperty("arguments").GetRawText());
        var leaf = context.LeafId!; var prior = log.OriginalBytes.ToArray();
        await Script(files, Text(api, "Hello resumed", [greeting, "authored resume"]));
        var resumed = HelloSuccess(await HelloRun(files, "resume", api, "authored resume"), api + ":resume"); HelloRequests(resumed, api, enabled: true);
        await Script(files, Text(api, "Hello sibling", [greeting, "authored branch"]));
        var branch = HelloSuccess(await HelloRun(files, "resume", api, "authored branch", extra: ["--leaf", leaf]), api + ":branch");
        HelloRequests(branch, api, enabled: true); Equal(leaf, branch.GetProperty("previousSelectedLeafId").GetString());
        var after = await Complete(files); HelloLoadout(after, enabled: true);
        Check(after.OriginalBytes.AsSpan().StartsWith(prior), "Reopen/branch rewrote acknowledged physical history.");
        var selected = new SessionContextProjector().Project(after.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray(),
            branch.GetProperty("selectedLeafId").GetString());
        Check(!selected.LlmMessages.Any(message => message.Role == "user" &&
            message.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString() == "authored resume"), "Hello branch incorporated the sibling prompt.");
        Equal(result.GetRawText(), selected.LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value.GetRawText());
        Equal(1, files.MarkerLines.Count(line => line.StartsWith("hello-execute:", StringComparison.Ordinal)));
        Equal(1, files.MarkerLines.Count(line => line == "hello-execute-closed"));
        CheckMarkers(files, expectedRuns: 4);
    }

    private static async Task HelloAuthority()
    {
        foreach (var variant in new[] { "initial-schema", "final-schema", "zero-grant", "native-write" })
        {
            var api = variant == "initial-schema" ? "anthropic-messages" : variant == "final-schema" ? "openai-completions" : "openai-responses";
            var enabled = variant != "zero-grant";
            using var files = await Files.CreateAsync(); await ConfigureHello(files, enabled);
            _ = HelloSuccess(await HelloRun(files, "create", api, enabled: enabled), api + ":" + variant + ":create"); var initial = await Complete(files);
            HelloLoadout(initial, enabled);
            var target = files.In("hello-must-not-write.txt");
            object arguments = variant == "initial-schema" ? new { name = (object)7 } :
                variant == "native-write" ? new { path = target, content = "no write grant" } :
                new { name = variant == "final-schema" ? "fixture-invalid-replacement" : "Ada" };
            var toolName = variant == "native-write" ? "write" : "hello";
            await Script(files, Tool(api, toolName, "hello-" + variant, arguments), Text(api, "rejection observed"));
            var run = await HelloRun(files, "prompt", api, "authored rejection", enabled); Equal(1, run.ExitCode); Equal("", run.Error);
            var report = JsonData.Parse(Utf8.GetString(run.Output)).Value;
            Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean() && report.GetProperty("toolErrors").GetBoolean(), "Rejection lost its error or durable acknowledgement.");
            Equal("completed_with_errors", report.GetProperty("status").GetString()); Equal(2, report.GetProperty("usedScriptTurns").GetInt32());
            HelloRequests(report, api, enabled);
            Equal(variant == "native-write" ? 1 : 0, report.GetProperty("actions").GetArrayLength());
            if (variant == "native-write") Check(!report.GetProperty("actions")[0].GetProperty("allowed").GetBoolean(), "Hello activation bypassed final native file policy.");
            Check(!File.Exists(target) && !files.MarkerLines.Any(line => line.StartsWith("hello-execute:", StringComparison.Ordinal)), "Rejected action executed an authored effect.");
            Equal(variant is "final-schema" or "native-write" ? 1 : 0, files.MarkerLines.Count(line => line.StartsWith("hello-call:", StringComparison.Ordinal)));
            var log = await Complete(files); HelloLoadout(log, enabled);
            Check(log.OriginalBytes.AsSpan().StartsWith(initial.OriginalBytes.AsSpan()), "Rejected action rewrote durable history.");
            var context = Context(log); var result = context.LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value;
            Check(result.GetProperty("isError").GetBoolean(), "Rejected tool result became success.");
            Equal(HelloActualId(api, "hello-" + variant), result.GetProperty("toolCallId").GetString());
            Equal(JsonSerializer.Serialize(arguments), context.LlmMessages.Single(message => message.Role == "assistant" &&
                message.WireBody.Value.GetProperty("stopReason").GetString() == "toolUse").WireBody.Value.GetProperty("content")[0].GetProperty("arguments").GetRawText());
            CheckMarkers(files, expectedRuns: 2);
        }
    }

    private static JsonNode HelloInitialRequest(string api, string user)
    {
        // Authored expected HTTP body, independent of the production request projectors.
        var anthropic = JsonSerializer.SerializeToNode(SessionCommandTests.AnthropicInitialRequest(user))!.AsObject();
        var baseline = anthropic["tools"]!.AsArray(); baseline[1]!.AsObject().Remove("cache_control");
        var hello = new JsonObject { ["name"] = "hello", ["description"] = "A simple greeting tool", ["input_schema"] = JsonNode.Parse(NativeStringSchemaTests.HelloParameters) };
        if (api == "anthropic-messages")
        {
            hello["eager_input_streaming"] = true; hello["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            baseline.Add(hello); return anthropic;
        }
        baseline.Add(hello); var tools = new JsonArray();
        foreach (var source in baseline)
        {
            var declaration = source ?? throw new InvalidOperationException("Authored declaration is absent.");
            var parameters = declaration["input_schema"]!.DeepClone();
            if (declaration["name"]!.GetValue<string>() != "hello") parameters["additionalProperties"] = false;
            var function = new JsonObject { ["name"] = declaration["name"]!.DeepClone(), ["description"] = declaration["description"]!.DeepClone(), ["parameters"] = parameters };
            if (api == "openai-completions") tools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            else { function["type"] = "function"; function["strict"] = false; tools.Add(function); }
        }
        if (api == "openai-completions") return new JsonObject { ["model"] = "pisharp-offline-completions-session", ["stream"] = true, ["store"] = false,
            ["max_completion_tokens"] = 8192, ["stream_options"] = new JsonObject { ["include_usage"] = true }, ["tools"] = tools,
            // Registered input admission commits text blocks, which this provider preserves as an array.
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "Explicit offline session file tools." },
                new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = user }) }) };
        return new JsonObject { ["model"] = "pisharp-offline-session", ["stream"] = true, ["store"] = false, ["tools"] = tools,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "Explicit offline session file tools." },
                new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = user }) }) };
    }

    private static async Task ConfigureHello(Files files, bool enabled)
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(files.Manifest))!;
        manifest["entryType"] = "PublishedCliFixture.HelloSchemaEntry";
        manifest["requiredFeatures"] = new JsonArray("owned-descriptor-callbacks", "registered-input-tool-reducers", "tool-invocation-context");
        var metadata = manifest.ToJsonString(); await File.WriteAllTextAsync(files.Manifest, metadata, Utf8);
        var approval = JsonNode.Parse(await File.ReadAllTextAsync(files.Approval))!;
        approval["manifestValueSha256"] = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(metadata)));
        approval["enabledTools"] = enabled ? new JsonArray("hello") : new JsonArray();
        await File.WriteAllTextAsync(files.Approval, approval.ToJsonString(), Utf8);
    }
    private static Task<Result> HelloRun(Files files, string command, string api, string? message = null, bool enabled = true, string[]? extra = null) =>
        OneShot(files, Base(command, files, api).Concat(message is null ? [] : new[] { "--offline-script", files.Script, "--message", message })
            .Concat(new[] { "--extension-package", files.Package, "--extension-manifest", files.Manifest, "--extension-approval", files.Approval, "--extension-snapshot-root", files.Snapshots })
            .Concat(enabled ? new[] { "--enable-extension-tool", "hello" } : []).Concat(extra ?? []).ToArray());
    private static string HelloActualId(string api, string call) => api == "openai-responses" ? call + "|fc-" + call : call;
    private static JsonElement HelloSuccess(Result result, string stage)
    {
        if (result.ExitCode != 0 || result.Error.Length != 0)
        {
            var output = Utf8.GetString(result.Output); const int maximum = 6144;
            throw new InvalidOperationException("Actual Hello CLI stage " + stage + " failed; " + Describe(result) +
                ", stdout=" + JsonSerializer.Serialize(output.Length <= maximum ? output : output[..maximum] + "[truncated]"));
        }
        return Success(result);
    }
    private static JsonElement HelloReceipt(Files files) => JsonData.Parse(files.MarkerLines.Single(line => line.StartsWith("hello-execute:", StringComparison.Ordinal))["hello-execute:".Length..]).Value;
    private static void HelloRequests(JsonElement report, string api, bool enabled)
    {
        foreach (var request in report.GetProperty("requests").EnumerateArray())
        {
            Equal(api, request.GetProperty("api").GetString());
            Check(request.GetProperty("authoredInertAuthValidated").GetBoolean() && request.GetProperty("historyRequirementsSatisfied").GetBoolean(), "Actual offline HTTP/auth/history checks failed.");
            Check(request.GetProperty("toolNames").EnumerateArray().Select(value => value.GetString()).SequenceEqual(enabled ? ["read", "write", "hello"] : ["read", "write"]), "Actual provider tool grants differ.");
        }
    }
    private static void HelloLoadout(SessionLogReadResult log, bool enabled)
    {
        var tools = log.ValidatedPrefix.Skip(1).Select(record => record.Entry.WireBody.Value)
            .Single(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "system")
            .GetProperty("message").GetProperty("toolsAdded");
        Check(tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(enabled ? ["read", "write", "hello"] : ["read", "write"]), "Initial durable Hello loadout differs from the approved grants.");
        if (!enabled) return;
        var hello = tools[2]; Equal("A simple greeting tool", hello.GetProperty("description").GetString());
        Equal(NativeStringSchemaTests.HelloParameters, hello.GetProperty("parameters").GetRawText());
    }
}
