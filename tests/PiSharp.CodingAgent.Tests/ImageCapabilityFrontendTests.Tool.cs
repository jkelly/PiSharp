using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static partial class ImageCapabilityFrontendTests
{
    private const string ImageTool = "fixture.image.render", ToolUser = "execute native image tool", CallId = "native-image-call";
    private const string ImageToolSchema = "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\"}},\"required\":[\"mode\"],\"additionalProperties\":false}";
    private const string ToolPlaceholder = "(tool image omitted: model does not support images)";
    private static (string Name, Func<Task> Run)[] ToolCases() =>
    [
        ("image-capability frontend RPC native images progress and hook content survive opposite-capability reopen", NativeToolRpc),
        ("image-capability frontend RPC native ordinary and actual summary requests bind both admitted providers", NativeToolSummary)
    ];

    private static async Task NativeToolRpc()
    {
        foreach (var api in new[] { Api, "anthropic-messages" })
        foreach (var vision in new[] { false, true })
        foreach (var mode in new[] { "plain", "patch" })
        {
            using var files = new Files(); var package = await ImagePackage(files);
            var first = await ExecuteImageTool(files, package, api, vision, mode);
            var expected = ToolContinuation(api, !vision, mode, "after native reopen");
            await files.Script(api == Api ? SessionCommandTests.CompletionsText("reopened-image-final", expectedRequest: expected) :
                SessionCommandTests.AnthropicText("reopened-image-final", expectedRequest: expected));
            await using (var rpc = new Child(files, !vision, package.Arguments, api, package.Marker))
            {
                await rpc.Send(new { id = "state", type = "get_state" });
                var model = Good(await rpc.Response("state"), "get_state").GetProperty("data").GetProperty("model");
                Same(JsonSerializer.SerializeToElement(new { id = api == Api ? ModelId : "pisharp-offline-session", api,
                    provider = api == Api ? "openai" : "anthropic", name = "PiSharp authored offline session model",
                    baseUrl = api == Api ? "https://offline-session.invalid/v1" : "https://offline-session.invalid", reasoning = false,
                    input = !vision ? new[] { "text", "image" } : ["text"], contextWindow = 131_072, maxTokens = 8192,
                    cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 }, provenance = "authored-offline-profile",
                    liveModelCapabilityClaimed = false }), model);
                await rpc.Send(new { id = "p", type = "prompt", message = "after native reopen" });
                Good(await rpc.Response("p"), "prompt"); await rpc.Settled(); rpc.Final("reopened-image-final");
                Check(!rpc.Records.Any(record => Type(record).StartsWith("tool_execution_", StringComparison.Ordinal)),
                    "Durable image-history reopen re-executed the completed native tool.");
                await rpc.Send(new { id = "messages", type = "get_messages" });
                ToolCanonical(Good(await rpc.Response("messages"), "get_messages").GetProperty("data").GetProperty("messages"), mode);
                await rpc.Send(new { id = "entries", type = "get_entries" });
                await Acknowledged(files.Session, Good(await rpc.Response("entries"), "get_entries").GetProperty("data").GetProperty("entries"));
                await rpc.Finish();
            }
            var reopened = await Complete(files.Session); Prefix(first, reopened); ToolCanonical(Project(reopened), mode);
            Check(!Utf8.GetString(reopened.OriginalBytes.AsSpan()).Contains("authored-inert-offline-session-value", StringComparison.Ordinal),
                "Native image authorization entered durable history.");
            Markers(package, mode, runs: 2);
            await using (var writer = await SessionLogStore.OpenAsync(files.Session))
                Equal((long)reopened.OriginalBytes.Length, writer.Snapshot.CommittedByteLength);
            Console.WriteLine("IMAGE_NATIVE_DURABLE_REOPEN_PASS " + JsonSerializer.Serialize(new { api, vision, mode,
                reopenedVision = !vision, fullExpectedHttpRequests = 3, completedToolReexecuted = false,
                durablePrefixPreserved = true, pluginDisposalJoined = true, writerReacquired = true }));
        }
    }

    private static async Task NativeToolSummary()
    {
        foreach (var api in new[] { Api, "anthropic-messages" })
        foreach (var vision in new[] { false, true })
        {
            using var files = new Files(); var package = await ImagePackage(files);
            var original = await ExecuteImageTool(files, package, api, vision, "patch");
            var context = new SessionContextProjector().Project(original.ValidatedPrefix.Skip(1).Select(row => row.Entry).ToImmutableArray(),
                original.ValidatedPrefix[^1].Entry.Id);
            var source = context.LlmMessages.Single(message => message.Role == "toolResult");
            var canonicalBefore = source.WireBody.ToString();
            var prompt = SessionSummaryRequestBuilder.SerializeConversation(context.LlmMessages);
            Check(prompt.Contains("[Tool result]: patched beforepatched after", StringComparison.Ordinal), "Actual summary lost admitted tool text.");
            Check(!prompt.Contains("hook-one", StringComparison.Ordinal) && !prompt.Contains("Ag==", StringComparison.Ordinal),
                "Actual text summary unexpectedly serialized canonical image bytes.");
            var expected = SummaryBody(api, prompt);
            var turn = api == Api ? SessionCommandTests.CompletionsText("tool history summary", expectedRequest: expected) :
                SessionCommandTests.AnthropicText("tool history summary", expectedRequest: expected);
            await using var profile = await OfflineSessionProfile.CreateAsync(files.Root, files.Session, null,
                [JsonData.Parse(JsonSerializer.Serialize(turn))], [], [], CancellationToken.None, offlineApi: api, modelSupportsImages: vision);
            Equal(vision, profile.SelectedModelDefinition.DeclaresImageInput);
            var request = new SessionSummaryRequest(SessionSummaryKind.History, profile.SelectedModel,
                "summarize native image history", prompt, 51, null, "native-image-summary");
            Equal("tool history summary", (await profile.SummaryGenerator.GenerateAsync(request)).Text);
            Equal(1, profile.UsedTurns); Equal(canonicalBefore, source.WireBody.ToString());
            Same(ToolContent("patch"), source.WireBody.Value.GetProperty("content"));
            var after = await Complete(files.Session); Prefix(original, after);
            Check(original.OriginalBytes.AsSpan().SequenceEqual(after.OriginalBytes.AsSpan()), "Standalone summary changed durable image history.");
            Markers(package, "patch", runs: 1);
        }
    }

    private static async Task<SessionLogReadResult> ExecuteImageTool(Files files, ImagePackageInfo package, string api, bool vision, string mode)
    {
        using var output = new StringWriter(); using var errors = new StringWriter();
        Equal(0, await SessionCommands.RunAsync(new[] { "session", "create", "--session", files.Session, "--workspace", files.Root,
            "--offline-api", api }.Concat(ImageArguments(vision)).Concat(package.Arguments).ToArray(), output, errors)); Equal("", errors.ToString());
        var initial = await Complete(files.Session); var first = ToolInitial(api);
        var call = api == Api ? SessionCommandTests.CompletionsTool(ImageTool, CallId, new { mode }, expectedRequest: first) :
            SessionCommandTests.AnthropicTool(ImageTool, CallId, new { mode }, expectedRequest: first);
        var final = api == Api ? SessionCommandTests.CompletionsText("native-image-final", expectedRequest: ToolContinuation(api, vision, mode)) :
            SessionCommandTests.AnthropicText("native-image-final", expectedRequest: ToolContinuation(api, vision, mode));
        await files.Script(call, final);
        await using (var rpc = new Child(files, vision, package.Arguments, api, package.Marker))
        {
            await rpc.Send(new { id = "state", type = "get_state" });
            var model = Good(await rpc.Response("state"), "get_state").GetProperty("data").GetProperty("model");
            Equal(api, model.GetProperty("api").GetString());
            Check(model.GetProperty("input").EnumerateArray().Select(value => value.GetString()).SequenceEqual(vision ? new[] { "text", "image" } : ["text"]),
                "Native image tool model advertisement differs from the admitted definition.");
            await rpc.Send(new { id = "p", type = "prompt", message = ToolUser }); Good(await rpc.Response("p"), "prompt");
            await rpc.Settled(); rpc.Final("native-image-final");
            var events = rpc.Records; var ended = events.Single(record => Type(record) == "tool_execution_end").Value;
            Check(!ended.GetProperty("isError").GetBoolean(), "Native image callback or after-hook failed.");
            var actualResult = ended.GetProperty("result"); Same(ToolResultBody(mode), actualResult);
            // This is the released functional API, not reflection over a disconnected raw supplement.
            var decoded = ToolResultValueCodec.Read(JsonData.FromElement(actualResult));
            Same(ToolContent(mode), decoded.ContentValue.Value);
            Throws<InvalidOperationException>(() => { _ = decoded.Content; });
            var message = new ToolResultMessage(CallId, ImageTool, [], JsonData.FromElement(actualResult.GetProperty("details")), false)
                { ContentValue = decoded.ContentValue };
            Same(ToolContent(mode), message.ContentValue.Value);
            Same(ToolContent(mode), ToolResultMessageMaterializer.ToTranscript(message, 123).WireBody.Value.GetProperty("content"));
            var updates = events.Where(record => Type(record) == "tool_execution_update").ToArray(); Equal(mode == "patch" ? 1 : 0, updates.Length);
            if (mode == "patch") Same(JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"partial image\"},{\"type\":\"image\",\"data\":\"AQ==\",\"mimeType\":\"image/png\",\"source\":\"progress\"}],\"future\":null}").Value,
                updates[0].Value.GetProperty("partialResult"));
            var messages = events.Where(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult").ToArray();
            Equal(1, messages.Length); Same(ToolContent(mode), messages[0].Value.GetProperty("message").GetProperty("content"));
            await rpc.Send(new { id = "messages", type = "get_messages" });
            ToolCanonical(Good(await rpc.Response("messages"), "get_messages").GetProperty("data").GetProperty("messages"), mode);
            await rpc.Send(new { id = "entries", type = "get_entries" });
            await Acknowledged(files.Session, Good(await rpc.Response("entries"), "get_entries").GetProperty("data").GetProperty("entries"));
            await rpc.Finish();
        }
        var complete = await Complete(files.Session); Prefix(initial, complete); ToolCanonical(Project(complete), mode);
        Markers(package, mode, runs: 1);
        Console.WriteLine("IMAGE_NATIVE_TOOL_SCENARIO_PASS " + JsonSerializer.Serialize(new { api, vision, mode, fullExpectedHttpRequests = 2,
            canonicalDurableSha256 = Convert.ToHexStringLower(SHA256.HashData(complete.OriginalBytes.AsSpan())), pluginDisposalJoined = true }));
        return complete;
    }

    private static JsonObject ToolInitial(string api)
    {
        if (api == Api)
        {
            var request = Request(new JsonArray(SystemMessage(), TextUser(ToolUser)));
            request["tools"]!.AsArray().Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
                { ["name"] = ImageTool, ["description"] = "Authored native mixed image result", ["parameters"] = JsonNode.Parse(ImageToolSchema) } });
            return request;
        }
        var anthropic = JsonSerializer.SerializeToNode(SessionCommandTests.AnthropicInitialRequest(ToolUser))!.AsObject();
        anthropic["tools"]!.AsArray()[^1]!.AsObject().Remove("cache_control");
        // Pinned Source non-strict Anthropic input_schema contains type/properties/required only.
        var imageSchema = JsonNode.Parse(ImageToolSchema)!.AsObject(); imageSchema.Remove("additionalProperties");
        anthropic["tools"]!.AsArray().Add(new JsonObject { ["name"] = ImageTool, ["description"] = "Authored native mixed image result",
            ["input_schema"] = imageSchema, ["eager_input_streaming"] = true,
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } });
        return anthropic;
    }
    private static JsonObject ToolContinuation(string api, bool vision, string mode, string? reopenedUser = null)
    {
        var request = ToolInitial(api); var messages = request["messages"]!.AsArray();
        var content = ToolContent(mode);
        if (api == Api)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray(new JsonObject
                { ["id"] = CallId, ["type"] = "function", ["function"] = new JsonObject { ["name"] = ImageTool, ["arguments"] = JsonSerializer.Serialize(new { mode }) } }) });
            var texts = content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()!).ToArray();
            var text = vision ? string.Join('\n', texts) : texts[0] + "\n" + ToolPlaceholder + "\n" + texts[1];
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = CallId, ["content"] = text });
            if (vision)
            {
                var images = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Attached image(s) from tool result:" });
                foreach (var block in content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "image"))
                    images.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject
                        { ["url"] = "data:" + block.GetProperty("mimeType").GetString() + ";base64," + block.GetProperty("data").GetString() } });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = images });
            }
            if (reopenedUser is not null)
            { messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "native-image-final" }); messages.Add(TextUser(reopenedUser)); }
        }
        else
        {
            messages[0]!["content"]!.AsArray()[^1]!.AsObject().Remove("cache_control");
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject
                { ["type"] = "tool_use", ["id"] = CallId, ["name"] = ImageTool, ["input"] = new JsonObject { ["mode"] = mode } }) });
            JsonNode resultContent;
            if (vision)
            {
                var blocks = new JsonArray();
                foreach (var block in content.EnumerateArray())
                    blocks.Add(block.GetProperty("type").GetString() == "text" ? new JsonObject { ["type"] = "text", ["text"] = block.GetProperty("text").GetString() } :
                        new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64",
                            ["media_type"] = block.GetProperty("mimeType").GetString(), ["data"] = block.GetProperty("data").GetString() } });
                resultContent = blocks;
            }
            else
            {
                var text = content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()!).ToArray();
                resultContent = JsonValue.Create(text[0] + "\n" + ToolPlaceholder + "\n" + text[1])!;
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = CallId,
                ["content"] = resultContent, ["is_error"] = false, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }) });
            if (reopenedUser is not null)
            {
                // Cache belongs to the actual final user block, exactly as in the unchanged Source projector.
                messages[^1]!["content"]!.AsArray()[^1]!.AsObject().Remove("cache_control");
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject
                    { ["type"] = "text", ["text"] = "native-image-final" }) });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject
                    { ["type"] = "text", ["text"] = reopenedUser, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }) });
            }
        }
        return request;
    }
    private static JsonObject SummaryBody(string api, string prompt) => api == Api ?
        Request(new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "summarize native image history" }, TextUser(prompt)), 51, includeTools: false) :
        new JsonObject { ["model"] = "pisharp-offline-session", ["max_tokens"] = 51, ["stream"] = true,
            ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "summarize native image history" }),
            ["messages"] = new JsonArray(TextUser(prompt)) };
    private static JsonElement ToolContent(string mode) => JsonData.Parse(mode == "patch" ?
        "[{\"type\":\"text\",\"text\":\"patched before\"},{\"type\":\"image\",\"data\":\"Ag==\",\"mimeType\":\"image/png\",\"source\":\"hook-one\"},{\"type\":\"image\",\"data\":\"Aw==\",\"mimeType\":\"image/jpeg\",\"source\":\"hook-two\"},{\"type\":\"text\",\"text\":\"patched after\"}]" :
        "[{\"type\":\"text\",\"text\":\"before image\"},{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\",\"source\":\"native\"},{\"type\":\"text\",\"text\":\"after image\"}]").Value;
    private static JsonElement ToolResultBody(string mode) => JsonSerializer.SerializeToElement(new
    { content = ToolContent(mode), details = mode == "patch" ? JsonData.Parse("{\"hook\":\"patched\"}").Value : JsonSerializer.SerializeToElement(new { mode }),
        future = (object?)null, opaque = new { ordered = new[] { 2, 1 } } });
    private static void ToolCanonical(JsonElement messages, string mode)
    {
        var tool = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "toolResult");
        Equal(CallId, tool.GetProperty("toolCallId").GetString()); Equal(ImageTool, tool.GetProperty("toolName").GetString());
        Check(!tool.GetProperty("isError").GetBoolean(), "Canonical native tool image became an error.");
        Same(ToolContent(mode), tool.GetProperty("content")); Same(ToolResultBody(mode).GetProperty("details"), tool.GetProperty("details"));
    }

    private sealed record ImagePackageInfo(string[] Arguments, string Marker);
    private static async Task<ImagePackageInfo> ImagePackage(Files files)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(cli)!);
        while (!File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent ?? throw new InvalidOperationException("Cannot locate owned image plugin publication.");
        var published = Path.Combine(directory.FullName, "artifacts", "extensions", "published-fixtures", "image");
        const string assembly = "PublishedFixture.Image.dll";
        Check(File.Exists(Path.Combine(published, assembly)), "Publish the authored image fixture before these cases.");
        var package = files.In("published"); var snapshots = files.In("snapshots"); Directory.CreateDirectory(package); Directory.CreateDirectory(snapshots);
        foreach (var source in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
        { var copy = Path.GetFullPath(Path.Combine(package, Path.GetRelativePath(published, source))); Check(copy.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Fixture publication escaped owned copy.");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(source, copy); }
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            hashes.Add(Path.GetRelativePath(package, file).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
        var metadata = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.image", packageVersion = "0.0.1",
            hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly,
            entryType = "PublishedImageFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
            requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers", "tool-invocation-context" },
            declaredCapabilities = new[] { "tools" }, resourcePaths = hashes.Keys.Where(relative => relative != assembly).ToArray(),
            explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
        var manifest = files.In("manifest.json"); var approval = files.In("approval.json"); await File.WriteAllTextAsync(manifest, metadata, Utf8);
        await File.WriteAllTextAsync(approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
            packageRoot = package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(metadata))), artifactHashes = hashes,
            sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
            sessionPath = files.Session, workspace = files.Root, snapshotRoot = snapshots, enabledTools = new[] { ImageTool } }), Utf8);
        return new(["--extension-package", package, "--extension-manifest", manifest, "--extension-approval", approval,
            "--extension-snapshot-root", snapshots, "--enable-extension-tool", ImageTool], files.In("image-plugin.markers"));
    }
    private static void Markers(ImagePackageInfo package, string mode, int runs)
    {
        var stages = File.ReadAllLines(package.Marker);
        foreach (var stage in new[] { "module", "constructor", "initialize", "dispose" }) Equal(runs, stages.Count(value => value == stage));
        foreach (var stage in new[] { "execute:" + mode, "execute-closed", "result-hook" }) Equal(1, stages.Count(value => value == stage));
        Equal(mode == "patch" ? 1 : 0, stages.Count(value => value == "progress-acknowledged"));
    }
}
