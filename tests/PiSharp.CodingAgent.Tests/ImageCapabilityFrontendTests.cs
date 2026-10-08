using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// Additive frontend qualification. Existing command/strict fixtures and expectations stay unchanged.
internal static partial class ImageCapabilityFrontendTests
{
    private const string Api = "openai-completions", ModelId = "pisharp-offline-completions-session";
    private const string ImageJson = "{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"}";
    private const string PromptText = "retain this canonical image";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string host = "", cli = "";

    internal static (string Name, Func<Task> Run)[] Cases(string dotnetHost, string cliDll)
    {
        if (!Path.IsPathFullyQualified(dotnetHost) || !Path.IsPathFullyQualified(cliDll) || !File.Exists(dotnetHost) || !File.Exists(cliDll))
            throw new ArgumentException("Image frontend tests require explicit existing host and CLI paths.");
        host = dotnetHost; cli = cliDll;
        return new (string Name, Func<Task> Run)[]
        {
            ("image-capability frontend RPC advertised input and complete image request survive opposite-capability durable reopen", RpcReopen),
            ("image-capability frontend immutable A B A bindings retain canonical images and reject stale writes", Replacement),
            ("image-capability frontend actual summary request honors selected identity and budget while retaining image source", Summary),
            ("image-capability frontend catalog operation and ordinal identity mismatches reject before provider requests", Admission)
        }.Concat(ToolCases()).Concat(CopyCases()).Concat(CopyStagingCases()).ToArray();
    }

    private static async Task RpcReopen()
    {
        foreach (var initialVision in new[] { false, true })
        {
            using var files = new Files();
            using var output = new StringWriter(); using var errors = new StringWriter();
            var createArgs = new[] { "session", "create", "--session", files.Session, "--workspace", files.Root,
                "--offline-api", Api }.Concat(ImageArguments(initialVision)).ToArray();
            Equal(0, await SessionCommands.RunAsync(createArgs, output, errors)); Equal("", errors.ToString());
            var initial = await Complete(files.Session);
            var firstExpected = Request(new JsonArray(SystemMessage(), ProjectedImage(initialVision)));
            await files.Script(SessionCommandTests.CompletionsText("first-final", expectedRequest: firstExpected));
            await using (var rpc = new Child(files, initialVision))
            {
                await rpc.Send(new { id = "state", type = "get_state" });
                Advertisement(Good(await rpc.Response("state"), "get_state").GetProperty("data").GetProperty("model"), initialVision);
                await rpc.Send(new { id = "p", type = "prompt", message = PromptText, images = new[] { JsonData.Parse(ImageJson).Value } });
                Good(await rpc.Response("p"), "prompt"); await rpc.Settled(); rpc.Final("first-final");
                await rpc.Send(new { id = "messages", type = "get_messages" });
                Canonical(Good(await rpc.Response("messages"), "get_messages").GetProperty("data").GetProperty("messages"), PromptText);
                await rpc.Send(new { id = "entries", type = "get_entries" });
                await Acknowledged(files.Session, Good(await rpc.Response("entries"), "get_entries").GetProperty("data").GetProperty("entries"));
                await rpc.Finish();
            }
            var first = await Complete(files.Session); Prefix(initial, first); Canonical(Project(first), PromptText);
            // The same durable canonical image is projected by a newly admitted, opposite-capability profile.
            var resumedVision = !initialVision;
            var resumedExpected = Request(new JsonArray(SystemMessage(), ProjectedImage(resumedVision),
                new JsonObject { ["role"] = "assistant", ["content"] = "first-final" }, TextUser("reopened next prompt")));
            await files.Script(SessionCommandTests.CompletionsText("reopened-final", expectedRequest: resumedExpected));
            await using (var rpc = new Child(files, resumedVision))
            {
                await rpc.Send(new { id = "state", type = "get_state" });
                Advertisement(Good(await rpc.Response("state"), "get_state").GetProperty("data").GetProperty("model"), resumedVision);
                await rpc.Send(new { id = "p", type = "prompt", message = "reopened next prompt" });
                Good(await rpc.Response("p"), "prompt"); await rpc.Settled(); rpc.Final("reopened-final");
                await rpc.Send(new { id = "messages", type = "get_messages" });
                Canonical(Good(await rpc.Response("messages"), "get_messages").GetProperty("data").GetProperty("messages"), PromptText);
                await rpc.Finish();
            }
            var reopened = await Complete(files.Session); Prefix(first, reopened); Canonical(Project(reopened), PromptText);
            Check(!Utf8.GetString(reopened.OriginalBytes.AsSpan()).Contains("authored-inert-offline-session-value", StringComparison.Ordinal),
                "Request authorization entered durable history.");
            await using var writer = await SessionLogStore.OpenAsync(files.Session);
            Equal((long)reopened.OriginalBytes.Length, writer.Snapshot.CommittedByteLength);
        }
    }

    private static async Task Replacement()
    {
        using var a = new Files(); using var b = new Files();
        await using var profileA = await Profile(a, false,
            SessionCommandTests.CompletionsText("A-first", expectedRequest: Request(new JsonArray(SystemMessage(), ProjectedImage(false)))),
            SessionCommandTests.CompletionsText("A-returned", expectedRequest: Request(new JsonArray(SystemMessage(), ProjectedImage(false),
                new JsonObject { ["role"] = "assistant", ["content"] = "A-first" }, ProjectedImage(false)))));
        await using var profileB = await Profile(b, true,
            SessionCommandTests.CompletionsText("B-first", expectedRequest: Request(new JsonArray(SystemMessage(), ProjectedImage(true)))));
        var releases = new List<Release>(); var entry = 0;
        var registryA = profileA.Registry; var registryB = profileB.Registry;
        ValueTask<SessionRuntimeLease> Bind(string cwd, long generation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var registry = cwd == a.Root ? registryA : cwd == b.Root ? registryB : throw new InvalidOperationException("Unexpected admitted cwd.");
            var resource = new Release(); releases.Add(resource);
            // These profiles admit no extension/template/skill activation. Each actual
            // lease still owns an immutable view and binds the exact host attachment.
            var lifetime = new ProfileViewLifetime(null);
            var view = new ProfileRuntimeView(null, null, null, null, lifetime, registry);
            var ownership = new ProfileRuntimeViewOwnership(profileA, resource, lifetime.Acquire(), () => view, generation);
            return ValueTask.FromResult(new SessionRuntimeLease(registry, ownership.Resources, ownership.BindOwner));
        }
        var lifecycle = new PersistentSessionLifecycle(registryA, () => 123, () => "image-entry-" + ++entry,
            runtimeForAttachment: Bind);
        await using (var seed = await lifecycle.CreateAsync(b.Session, Header(b, "B"), profileB.SelectedModel))
            await seed.ConfigureAsync(new(SystemMessage: new("system", profileB.InitialSystem)));
        await using var initial = await lifecycle.CreateAsync(a.Session, Header(a, "A"), profileA.SelectedModel);
        await initial.ConfigureAsync(new(SystemMessage: new("system", profileA.InitialSystem)));
        await profileA.AttachOwnerAsync(initial, lifecycle: lifecycle); await using var owner = profileA.Sessions!; var oldA = owner.Current;
        Check(ReferenceEquals(profileA.Registry, registryA), "Initial attachment lost its A runtime view.");
        await SuccessfulPrompt(oldA.Session, "A-first");
        var aFirst = await LiveComplete(a.Session); Canonical(Project(aFirst), PromptText);
        var switched = await owner.SwitchAsync(oldA, new(b.Session)); Check(switched is not null, "B replacement did not commit.");
        var currentB = switched!.Current; Check(oldA.LifetimeToken.IsCancellationRequested && initial.Snapshot.IsRetired, "Old A retained lifetime authority.");
        Check(ReferenceEquals(profileA.Registry, registryB), "B attachment did not bind its captured runtime view.");
        await SuccessfulPrompt(currentB.Session, "B-first");
        var bFirst = await LiveComplete(b.Session); Canonical(Project(bFirst), PromptText);
        await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(oldA, Draft("stale-A")));
        var returned = (await owner.SwitchAsync(currentB, new(a.Session)))!.Current;
        Check(ReferenceEquals(profileA.Registry, registryA), "Returned A attachment did not bind its captured runtime view.");
        Equal(3L, returned.Generation); Check(currentB.LifetimeToken.IsCancellationRequested, "Retired B retained its capability lifetime.");
        var beforeStale = (await LiveComplete(a.Session)).OriginalBytes;
        await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(oldA, Draft("same-path-stale-A")));
        var afterStale = (await LiveComplete(a.Session)).OriginalBytes;
        Check(beforeStale.AsSpan().SequenceEqual(afterStale.AsSpan()), "Returning to the same path restored stale write authority.");
        await SuccessfulPrompt(returned.Session, "A-returned");
        await owner.AppendExtensionEntryAsync(returned, Draft("fresh-A"));
        Check(!profileA.SelectedModelDefinition.DeclaresImageInput && profileB.SelectedModelDefinition.DeclaresImageInput,
            "Replacement mutated another profile's immutable definition.");
        Equal(2, profileA.UsedTurns); Equal(1, profileB.UsedTurns);
        await owner.DisposeAsync();
        Equal(4, releases.Count); Check(releases.All(resource => resource.Calls == 1), "A fresh runtime lease was leaked or released twice.");
        var aFinal = await Complete(a.Session); var bFinal = await Complete(b.Session);
        Prefix(aFirst, aFinal); Prefix(bFirst, bFinal); Canonical(Project(aFinal), PromptText); Canonical(Project(bFinal), PromptText);
        Check(!Utf8.GetString(aFinal.OriginalBytes.AsSpan()).Contains("stale-A", StringComparison.Ordinal), "Stale checkpoint entered durable history.");
        await using var aWriter = await SessionLogStore.OpenAsync(a.Session);
        await using var bWriter = await SessionLogStore.OpenAsync(b.Session);
    }

    private static async Task Summary()
    {
        foreach (var vision in new[] { false, true })
        {
            using var files = new Files(); var canonical = ImagePrompt(); var before = canonical.WireBody.ToString();
            // Actual summary serialization intentionally turns history into text; it supplies no image blocks.
            var prompt = SessionSummaryRequestBuilder.SerializeConversation([canonical]);
            Check(prompt.Contains(PromptText, StringComparison.Ordinal) && !prompt.Contains("AA==", StringComparison.Ordinal), "Summary text unexpectedly forwarded image data.");
            var expected = Request(new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "summarize retained history" },
                TextUser(prompt)), maximumTokens: 51, includeTools: false);
            await using var profile = await Profile(files, vision, SessionCommandTests.CompletionsText("generated summary", expectedRequest: expected));
            var request = new SessionSummaryRequest(SessionSummaryKind.History, profile.SelectedModel,
                "summarize retained history", prompt, 51, null, "image-summary");
            var summary = await profile.SummaryGenerator.GenerateAsync(request);
            Equal("generated summary", summary.Text); Equal(1, profile.UsedTurns);
            Equal(before, canonical.WireBody.ToString()); Same(JsonData.Parse(ImageJson).Value, canonical.WireBody.Value.GetProperty("content")[1]);
            var beforeRejected = profile.UsedTurns;
            await Throws<PiSharp.Sessions.Compaction.SessionCompactionException>(() => profile.SummaryGenerator.GenerateAsync(
                request with { Model = request.Model with { Provider = "OpenAI" } }).AsTask());
            Equal(beforeRejected, profile.UsedTurns);
        }
    }

    private static async Task Admission()
    {
        using var files = new Files();
        await using var profile = await Profile(files, true, SessionCommandTests.CompletionsText("must remain unacquired"));
        var expected = profile.SelectedModel;
        var definitions = new[] { Definition(expected with { Provider = "OpenAI" }), Definition(expected with { Id = expected.Id.ToUpperInvariant() }),
            Definition(expected with { Api = "other-declared-api" }), Definition(expected, "image"), Definition(expected, "classifier") };
        foreach (var definition in definitions)
        {
            var failure = Throws<SessionCommandException>(() => OfflineSessionProfile.RequireSelectedModelDefinition(expected, definition));
            Equal(SessionCommandFailure.InvalidArguments, failure.Failure);
        }
        foreach (var model in new[] { expected with { Provider = "OpenAI" }, expected with { Id = expected.Id.ToUpperInvariant() }, expected with { Api = "other-declared-api" } })
        {
            var failure = Throws<SessionRuntimeRegistryException>(() => profile.Registry.Resolve(model, [ImagePrompt()]));
            Equal(SessionRuntimeRegistryFailure.UnknownModel, failure.Failure);
        }
        Equal(0, profile.UsedTurns); Equal(0, profile.Requests.Length);
        OfflineSessionProfile.RequireSelectedModelDefinition(expected, profile.SelectedModelDefinition);
        var missing = files.In("unsupported-responses.jsonl");
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Equal(2, await SessionCommands.RunAsync(["session", "create", "--session", missing, "--workspace", files.Root,
            "--offline-api", "openai-responses", "--offline-images", "true"], stdout, stderr));
        Check(!File.Exists(missing) && stdout.ToString() == "", "Unsupported capability admission created durable state.");
        Equal("InvalidArguments", JsonData.Parse(stderr.ToString()).Value.GetProperty("code").GetString());
    }

    private static Task<OfflineSessionProfile> Profile(Files files, bool vision, params object[] turns) =>
        OfflineSessionProfile.CreateAsync(files.Root, files.Session, null,
            turns.Select(turn => JsonData.Parse(JsonSerializer.Serialize(turn))).ToImmutableArray(), [], [], CancellationToken.None,
            offlineApi: Api, modelSupportsImages: vision);
    private static async Task SuccessfulPrompt(PersistentAgentSession session, string final)
    {
        await session.PromptAsync(ImagePrompt());
        var last = session.Snapshot.Agent.Messages.Last(entry => entry.Role == "assistant").WireBody.Value;
        Equal("stop", last.GetProperty("stopReason").GetString());
        Check(last.GetProperty("content").EnumerateArray().Any(block => block.GetProperty("type").GetString() == "text" && block.GetProperty("text").GetString() == final),
            "Complete authored request comparison did not produce the expected final assistant.");
    }
    private static TranscriptEntry ImagePrompt() => new("user", JsonData.Parse(JsonSerializer.Serialize(new
    { role = "user", content = new object[] { new { type = "text", text = PromptText }, JsonData.Parse(ImageJson).Value }, timestamp = 123 })));
    private static SessionEntry Header(Files files, string suffix) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    { type = "session", version = 3, id = "image-session-" + suffix, timestamp = "2026-10-03T00:00:00.000Z", cwd = files.Root }));
    private static SessionExtensionEntryDraft Draft(string text) => new("sample.image-capability", "checkpoint", 1, JsonData.Parse(JsonSerializer.Serialize(new { text })));
    private static JsonObject SystemMessage() => new() { ["role"] = "system", ["content"] = "Explicit offline session file tools." };
    private static JsonObject TextUser(string text) => new() { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
    private static JsonObject ProjectedImage(bool vision)
    {
        var result = TextUser(PromptText); var content = result["content"]!.AsArray();
        content.Add(vision ? new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AA==" } } :
            new JsonObject { ["type"] = "text", ["text"] = "(image omitted: model does not support images)" });
        return result;
    }
    private static JsonObject Request(JsonArray messages, int maximumTokens = 8192, bool includeTools = true)
    {
        var result = new JsonObject { ["model"] = ModelId, ["messages"] = messages, ["stream"] = true,
            ["store"] = false, ["stream_options"] = new JsonObject { ["include_usage"] = true }, ["max_completion_tokens"] = maximumTokens };
        if (includeTools)
        {
            // Reuse the existing independently authored file-tool schemas, not a native request projector.
            var declarations = JsonSerializer.SerializeToElement(SessionCommandTests.AnthropicInitialRequest("unused")).GetProperty("tools");
            var tools = new JsonArray();
            foreach (var tool in declarations.EnumerateArray())
            {
                var schema = tool.GetProperty("input_schema");
                tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = tool.GetProperty("name").GetString(),
                    ["description"] = tool.GetProperty("description").GetString(), ["parameters"] = new JsonObject { ["type"] = "object",
                        // Pi's read/write TypeBox schemas have no additionalProperties; non-strict projections send them as declared.
                        ["properties"] = JsonNode.Parse(schema.GetProperty("properties").GetRawText()), ["required"] = JsonNode.Parse(schema.GetProperty("required").GetRawText()) } } });
            }
            result["tools"] = tools;
        }
        return result;
    }
    private static void Advertisement(JsonElement actual, bool vision) => Same(JsonSerializer.SerializeToElement(new
    { id = ModelId, api = Api, provider = "openai", name = "PiSharp authored offline session model", baseUrl = "https://offline-session.invalid/v1",
        reasoning = false, input = vision ? new[] { "text", "image" } : ["text"], contextWindow = 131_072, maxTokens = 8192,
        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 }, provenance = "authored-offline-profile", liveModelCapabilityClaimed = false }), actual);
    private static FrozenCatalogModel Definition(ModelDescriptor model, string type = "chat")
    {
        var raw = new Dictionary<string, object> { ["type"] = type, ["id"] = model.Id, ["api"] = model.Api, ["provider"] = model.Provider,
            ["name"] = "Authored negative selection", ["baseUrl"] = "", ["input"] = new[] { "text", "image" }, ["reasoning"] = false,
            ["contextWindow"] = 64, ["maxTokens"] = 32, ["cost"] = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } };
        if (type == "image") raw["output"] = new[] { "image" };
        var catalog = FrozenModelCatalog.ReadProviderJson(model.Provider, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        { [model.Api] = new Dictionary<string, object> { [type + ":" + model.Id] = raw } }));
        return catalog.Models.Single();
    }
    private static string[] ImageArguments(bool vision) => vision ? ["--offline-images", "true"] : [];
    private static async Task<SessionLogReadResult> Complete(string path)
    { var read = await new SessionLogReader().ReadFileAsync(path); Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete, "Durable image session is incomplete."); return read; }
    private static async Task<SessionLogReadResult> LiveComplete(string path)
    { await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous); return await new SessionLogReader().ReadAsync(source, leaveOpen: true); }
    private static JsonElement Project(SessionLogReadResult read)
    {
        var entries = read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
        var context = new SessionContextProjector().Project(entries, entries.IsEmpty ? null : entries[^1].Id);
        return JsonSerializer.SerializeToElement(context.LlmMessages.Select(entry => entry.WireBody.Value).ToArray());
    }
    private static void Prefix(SessionLogReadResult before, SessionLogReadResult after) =>
        Check(after.OriginalBytes.AsSpan().StartsWith(before.OriginalBytes.AsSpan()), "Capability selection rewrote an existing durable prefix.");
    private static void Canonical(JsonElement messages, string text)
    {
        var users = messages.EnumerateArray().Where(message => message.GetProperty("role").GetString() == "user" &&
            message.GetProperty("content").ValueKind == JsonValueKind.Array && message.GetProperty("content").EnumerateArray().Any(block =>
                block.GetProperty("type").GetString() == "text" && block.GetProperty("text").GetString() == text)).ToArray();
        Check(users.Length > 0, "Canonical image user disappeared.");
        foreach (var user in users) { Equal(2, user.GetProperty("content").GetArrayLength()); Same(JsonData.Parse(ImageJson).Value, user.GetProperty("content")[1]); }
    }
    private static async Task Acknowledged(string path, JsonElement entries)
    {
        var read = await LiveComplete(path); Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete, "RPC entries escaped an incomplete writer checkpoint.");
        var stored = read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToArray(); Equal(entries.GetArrayLength(), stored.Length);
        for (var index = 0; index < stored.Length; index++) Same(entries[index], stored[index].WireBody.Value);
    }
    private sealed class Release : IAsyncDisposable
    { internal int Calls; public ValueTask DisposeAsync() { Calls++; return ValueTask.CompletedTask; } }

    // Same bounded compiled-child/read/join pattern as RpcSessionCommandTests; every process is joined.
    private sealed class Child : IAsyncDisposable
    {
        private readonly Process process;
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        private readonly Task read; private readonly Task<string> error;
        private readonly object gate = new(); private readonly List<JsonData> records = [];
        private TaskCompletionSource changed = NewGate(); private bool finished;
        internal JsonData[] Records { get { lock (gate) return records.ToArray(); } }
        internal Child(Files files, bool vision, string[]? extensionArguments = null, string offlineApi = Api,
            string? markerFile = null)
        {
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
            if (markerFile is not null) start.Environment["PISHARP_IMAGE_FIXTURE_MARKER"] = markerFile;
            start.ArgumentList.Add(cli);
            foreach (var argument in new[] { "session", "rpc", "--session", files.Session, "--workspace", files.Root,
                "--offline-script", files.ScriptPath, "--offline-api", offlineApi }.Concat(ImageArguments(vision))
                .Concat(extensionArguments ?? [])) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new InvalidOperationException("Compiled image RPC host did not start.");
            read = ReadOutput(); error = ReadError();
        }
        internal async Task Send(object command)
        { var wire = JsonSerializer.Serialize(command) + "\n"; Check(Utf8.GetByteCount(wire) <= 1_048_577, "Authored RPC command exceeds bound."); await process.StandardInput.WriteAsync(wire.AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
        internal Task<JsonData> Response(string id) => Wait(record => Type(record) == "response" && record.Value.GetProperty("id").GetString() == id);
        internal Task<JsonData> Settled() => Wait(record => Type(record) == "agent_settled");
        private async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        {
            while (true)
            { Task wake; lock (gate) { var match = records.FirstOrDefault(predicate); if (match is not null) return match; if (read.IsCompleted) throw new InvalidOperationException("Image RPC child ended before its expected record."); wake = changed.Task; } await wake.WaitAsync(deadline.Token); }
        }
        internal void Final(string text)
        {
            JsonElement last; lock (gate) last = records.Last(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant").Value.GetProperty("message");
            if (last.GetProperty("stopReason").GetString() != "stop")
                Console.Error.WriteLine("IMAGE_RPC_FAILURE_RECORDS " + JsonSerializer.Serialize(Records.Select(record => record.Value)));
            Equal("stop", last.GetProperty("stopReason").GetString());
            Check(last.GetProperty("content").EnumerateArray().Any(block => block.GetProperty("type").GetString() == "text" && block.GetProperty("text").GetString() == text), "Actual whole-request comparison did not produce its expected final response.");
        }
        private async Task ReadOutput()
        {
            try
            {
                await using var reader = new JsonlReader(process.StandardOutput.BaseStream); long bytes = 0;
                await foreach (var admission in reader.ReadAdmissionsAsync(deadline.Token))
                {
                    Check(admission.IsAccepted && !admission.IsFinalFrame, "Image RPC output contained malformed or unterminated JSONL."); var record = admission.Record!;
                    bytes += Utf8.GetByteCount(record.ToString()) + 1; Check(bytes <= 2_097_152, "Image RPC output exceeded total bound.");
                    TaskCompletionSource wake; lock (gate) { Check(records.Count < 2048, "Image RPC exceeded record budget."); records.Add(record); wake = changed; changed = NewGate(); } wake.TrySetResult();
                }
            }
            finally { lock (gate) changed.TrySetResult(); }
        }
        private async Task<string> ReadError()
        {
            var builder = new StringBuilder(); var buffer = new char[1024];
            while (true) { var count = await process.StandardError.ReadAsync(buffer.AsMemory(), deadline.Token); if (count == 0) return builder.ToString(); Check(count <= 1_048_576 - builder.Length, "Image RPC diagnostic exceeded budget."); builder.Append(buffer, 0, count); }
        }
        internal async Task Finish()
        { if (!finished) { finished = true; process.StandardInput.Close(); } await process.WaitForExitAsync(deadline.Token); await read; Equal(0, process.ExitCode); Equal("", await error); }
        public async ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } deadline.Cancel(); try { await read; } catch (Exception) { } try { await error; } catch (Exception) { } }
            finally { process.Dispose(); deadline.Dispose(); }
        }
    }
    private sealed class Files : IDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        internal string Root { get; } internal string Session => In("session.jsonl"); internal string ScriptPath => In("script.json");
        internal Files() { Root = Path.Combine(parent, "pisharp-image-capability-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        internal string In(string name) => Path.Combine(Root, name);
        internal Task Script(params object[] turns) => File.WriteAllTextAsync(ScriptPath, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
        public void Dispose()
        { var target = Path.GetFullPath(Root); if (Path.GetDirectoryName(target) != parent || !Path.GetFileName(target).StartsWith("pisharp-image-capability-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing unowned image fixture cleanup."); Directory.Delete(target, recursive: true); }
    }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Type(JsonData record) => record.Value.GetProperty("type").GetString()!;
    private static JsonElement Good(JsonData record, string command)
    { Equal("response", Type(record)); Equal(command, record.Value.GetProperty("command").GetString()); if (!record.Value.GetProperty("success").GetBoolean()) Console.Error.WriteLine("IMAGE_RPC_COMMAND_FAILURE " + record.ToString()); Check(record.Value.GetProperty("success").GetBoolean(), "Image RPC command failed."); return record.Value; }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Same(JsonElement expected, JsonElement actual)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        { var left = expected.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray(); var right = actual.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray(); Equal(left.Length, right.Length); for (var i = 0; i < left.Length; i++) { Equal(left[i].Name, right[i].Name); Same(left[i].Value, right[i].Value); } }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var i = 0; i < expected.GetArrayLength(); i++) Same(expected[i], actual[i]); }
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString());
        else Equal(expected.GetRawText(), actual.GetRawText());
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ: " + expected + " / " + actual);
}
