using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Providers;
using PiSharp.Contracts;
using PiSharp.Agent;
using NativeAgent = PiSharp.Agent.Agent;

/// <summary>Authored offline integration regressions. No genuine Source executions or package acceptance.</summary>
internal static class PiMessagesAgentIntegrationTests
{
    private static readonly ModelDescriptor Model = new("inert", "pi-messages", "authored");
    private const string Usage = """{"input":3,"output":2,"cacheRead":1,"cacheWrite":0,"totalTokens":6,"cost":{"input":0.1,"output":0.2,"cacheRead":0.01,"cacheWrite":0,"total":0.31}}""";
    private static readonly TimeSpan DiagnosticDeadline = TimeSpan.FromSeconds(5);
    private sealed class OwnedCaseState
    {
        public bool DeadlineExceeded;
        public string? IncompleteReceipt;
    }
    private static readonly OwnedCaseState CaseState = new();
    public static bool DeadlineExceeded => CaseState.DeadlineExceeded;
    public static string? IncompleteReceipt => CaseState.IncompleteReceipt;

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("pi-messages-agent.inner-timeout-preserves-original-failure", InnerTimeoutPreservesFailure);
        yield return ("pi-messages-agent.outer-deadline-retains-original-join", OuterDeadlineRetainsJoin);
        yield return ("pi-messages-agent.registry-rejects-before-provider-effects", RegistryAdmission);
        yield return ("pi-messages-agent.direct-and-simple-owned-options", OptionsEntryPoints);
        yield return ("pi-messages-agent.released-radius-all-28-row-routing", ReleasedCatalogRows);
        yield return ("pi-messages-agent.held-http-assistant-result-barriers-and-continuation", ToolRoundTrip);
        yield return ("pi-messages-agent.failed-truncated-and-cleanup-assistants-have-no-authority", FailureAuthority);
        yield return ("pi-messages-agent.abort-joins-held-http-before-assistant-settlement", AbortOwnership);
        yield return ("pi-messages-agent.reviewed-wire-error-and-preview-failures-have-no-authority", ReviewedFailuresNoAuthority);
    }

    public static bool OwnsCase(string name) => name.StartsWith("pi-messages-agent.", StringComparison.Ordinal);
    public static async Task RunOwnedCaseAsync(string name, Func<Task> run, string? report)
    {
        var original = run();
        using var deadlineLifetime = new CancellationTokenSource();
        try
        {
            await RunOwnedOperationAsync(name, original, report,
                Task.Delay(TimeSpan.FromSeconds(20), deadlineLifetime.Token), CaseState);
        }
        finally { deadlineLifetime.Cancel(); }
    }

    private static async Task RunOwnedOperationAsync(string name, Task original, string? report,
        Task deadline, OwnedCaseState state, Action? receiptWritten = null)
    {
        await Task.WhenAny(original, deadline);
        // A completed original owns its failure, including any inner gate TimeoutException.
        if (original.IsCompleted) { await original; return; }
        state.DeadlineExceeded = true;
        state.IncompleteReceipt = Path.Combine(report is null ? Path.GetTempPath() : Path.GetDirectoryName(report)!,
            "pi-messages-agent-incomplete-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(state.IncompleteReceipt)!);
            using var file = new FileStream(state.IncompleteReceipt, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { schemaVersion = 1, testId = name, status = "INCOMPLETE_NONPASSING", laterCaseAdmission = "STOPPED",
                    originalTaskOwned = true, runtimeAcceptance = false }));
            file.Write(bytes); file.Flush(flushToDisk: true);
            receiptWritten?.Invoke();
        }
        finally
        {
            // A receipt I/O failure also grants no detach authority. This can intentionally remain pending.
            try { await original; } catch { }
        }
        throw new TimeoutException("Pi Messages case exceeded its diagnostic deadline; original task joined.");
    }

    private static async Task InnerTimeoutPreservesFailure()
    {
        var state = new OwnedCaseState();
        var expected = new TimeoutException("injected inner gate timeout");
        async Task InnerGateFailure() { await Task.Yield(); throw expected; }
        var deadline = Gate();
        try
        {
            await RunOwnedOperationAsync("control.inner-timeout", InnerGateFailure(), null, deadline.Task, state);
            throw new InvalidOperationException("Inner failure passed.");
        }
        catch (TimeoutException actual)
        {
            Check(ReferenceEquals(expected, actual) && actual.StackTrace!.Contains(nameof(InnerGateFailure), StringComparison.Ordinal),
                "Inner timeout lost its original exception or stack.");
            Check(!state.DeadlineExceeded && state.IncompleteReceipt is null, "Inner timeout was misclassified as outer expiry.");
        }
    }

    private static async Task OuterDeadlineRetainsJoin()
    {
        foreach (var originalFails in new[] { false, true })
        {
            var state = new OwnedCaseState(); var release = Gate(); var receiptWritten = Gate();
            var returned = false;
            async Task Original()
            {
                try { await release.Task; if (originalFails) throw new InvalidOperationException("injected original failure"); }
                finally { returned = true; }
            }
            var original = Original();
            var deadlineObserved = false;
            var wrapper = RunOwnedOperationAsync("control.outer-deadline", original, null, Task.CompletedTask,
                state, () => receiptWritten.TrySetResult());
            try
            {
                await Task.WhenAny(receiptWritten.Task, wrapper).WaitAsync(DiagnosticDeadline);
                Check(receiptWritten.Task.IsCompleted && state.DeadlineExceeded && !wrapper.IsCompleted && !returned,
                    "Outer deadline abandoned its pending original.");
                using var receipt = JsonDocument.Parse(File.ReadAllText(state.IncompleteReceipt!));
                Equal("INCOMPLETE_NONPASSING", receipt.RootElement.GetProperty("status").GetString());
                Equal("STOPPED", receipt.RootElement.GetProperty("laterCaseAdmission").GetString());
                Check(receipt.RootElement.GetProperty("originalTaskOwned").GetBoolean(), "Receipt lost original ownership.");
            }
            finally
            {
                release.TrySetResult();
                try { await wrapper; }
                catch (TimeoutException actual)
                {
                    Equal("Pi Messages case exceeded its diagnostic deadline; original task joined.", actual.Message);
                    deadlineObserved = true;
                }
            }
            Check(deadlineObserved && returned && original.IsCompleted && state.DeadlineExceeded,
                "Outer deadline passed or returned before original settlement.");
            Check(!DeadlineExceeded, "Expected control expiry contaminated real case admission.");
        }
    }

    private static Task RegistryAdmission()
    {
        var effects = new EffectTransport();
        var provider = new MutableProvider("authored", [Model], effects);
        var registry = new ModelTransportRegistry([provider]);
        provider.List.Clear(); // Snapshot must remain usable, without exposing a mutable provider catalog.
        Check(registry.Resolve(Model) == effects && registry.Models.Length == 1, "Registry did not freeze selection.");
        foreach (var bad in new[] { Model with { Id = "INERT" }, Model with { Api = "other" }, Model with { Provider = "other" } })
            Throws<ArgumentException>(() => registry.StreamAsync(new(bad, Inputs(), 123)));
        Throws<ArgumentException>(() => new ModelTransportRegistry([
            new MutableProvider("authored", [Model], effects), new MutableProvider("authored", [Model], effects)]));
        Throws<ArgumentException>(() => new ModelTransportRegistry([new MutableProvider("wrong", [Model], effects)]));
        Throws<ArgumentException>(() => new ModelTransportRegistry([new MutableProvider("authored", [], effects)]));
        Throws<ArgumentNullException>(() => registry.Resolve(null!));
        Check(effects.Calls == 0, "Invalid or duplicate selection admitted a provider.");
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected send.")));
        var catalog = Catalog();
        var pi = new PiMessagesModelProvider(catalog, client);
        Throws<ArgumentException>(() => pi.Transport.StreamAsync(new(Model with { Id = "missing" }, Inputs(), 123)));
        var wrongApi = FrozenModelCatalog.ReadProviderJson("authored", Encoding.UTF8.GetBytes(
            catalog.Raw.ToString().Replace("pi-messages", "other-api", StringComparison.Ordinal)));
        Throws<ArgumentException>(() => new PiMessagesModelProvider(wrongApi, client));
        var invalidEndpoint = FrozenModelCatalog.ReadProviderJson("authored", Encoding.UTF8.GetBytes(
            catalog.Raw.ToString().Replace("https://pi-messages.invalid/v1", "opaque endpoint", StringComparison.Ordinal)));
        Throws<PiSharp.AI.Protocols.PiMessages.PiMessagesException>(() => new PiMessagesModelProvider(invalidEndpoint, client));
        Check(ReferenceEquals(catalog.Models[0].Raw, pi.GetCatalogModel(Model).Raw), "Complete owned raw model was replaced.");
        return Task.CompletedTask;
    }

    private static async Task OptionsEntryPoints()
    {
        foreach (var simple in new[] { false, true })
        foreach (var configured in new[] { false, true })
        {
            var bodies = new List<JsonData>(); var requests = 0; var payloadHooks = 0;
            using var handler = new Handler(async (request, token) =>
            {
                requests++;
                Check(request.Headers.Authorization?.Parameter == "inert-key", "Explicit key was lost.");
                if (configured) Check(request.Headers.GetValues("X-Owned").Single() == "yes", "Injected header was lost.");
                Equal(configured ? "?debug=1" : "", request.RequestUri!.Query);
                bodies.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
                return Response(new OwnedBody(TextWire()));
            });
            using var client = new HttpClient(handler);
            var options = new PiMessagesProviderOptions("inert-key")
            {
                Temperature = configured ? 0.25 : null, MaxTokens = configured ? 123.5 : null,
                Reasoning = configured ? "high" : null, CacheRetention = configured ? "" : null,
                SessionId = configured ? "owned-session" : null,
                ToolChoice = configured ? JsonData.Parse("false") : null,
                Debug = configured,
                Headers = configured ? JsonData.Parse("""{"X-Owned":"yes"}""") : null,
                Environment = configured ? JsonData.Parse("""{"PI_CACHE_RETENTION":"long"}""") : null,
                // Should never be consulted for auth. A scoped truthy cache env wins over this lookup.
                EnvironmentLookup = _ => configured ? "short" : null,
                Hooks = new() { OnPayload = (observation, selected, _) =>
                {
                    payloadHooks++; Check(selected == Model, "Callback saw another model.");
                    if (!configured) Check(observation.OwnUndefinedPaths.Length == 6, "Unset options acquired defaults.");
                    return ValueTask.FromResult<JsonData?>(null);
                } }
            };
            var provider = simple ? PiMessagesModelProvider.CreateSimple(Catalog(), client, options) :
                PiMessagesModelProvider.CreateDirect(Catalog(), client, options);
            var result = await new ChatClient(new ModelTransportRegistry([provider])).CompleteAsync(new(Model, Inputs(), 123));
            Check(result.Failure is null && requests == 1 && payloadHooks == 1, "Entry point failed or retried.");
            var actual = bodies.Single().Value.GetProperty("options");
            SameJson(configured ? JsonData.Parse("""{"temperature":0.25,"maxTokens":123.5,"reasoning":"high","cacheRetention":"long","sessionId":"owned-session","toolChoice":false}""").Value :
                JsonData.EmptyObject.Value, actual);
            Check(!handler.Disposed, "Provider disposed the borrowed client.");
        }
        var sends = 0; var hooks = 0;
        using var missingHandler = new Handler((_, _) => { sends++; throw new InvalidOperationException("Unexpected send."); });
        using var missingClient = new HttpClient(missingHandler);
        var missing = new PiMessagesModelProvider(Catalog(), missingClient, new()
        { Environment = JsonData.Parse("""{"API_KEY":"must-not-be-used"}"""),
            EnvironmentLookup = _ => "must-not-be-used", Hooks = new()
            { OnPayload = (_, _, _) => { hooks++; return ValueTask.FromResult<JsonData?>(null); } } });
        var failed = await new ChatClient(new ModelTransportRegistry([missing])).CompleteAsync(new(Model, Inputs(), 123));
        Check(failed.NativeDiagnostic == new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.SourceFailed) &&
            sends == 0 && hooks == 0, "Missing explicit key discovered credentials or admitted effects.");
    }

    private static async Task ReleasedCatalogRows()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Repository(), "artifacts", "released-npm-ai", "pi-ai-0.99.1.tgz"));
        Equal("f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3", Hash(bytes));
        byte[]? shard = null;
        using (var compressed = new MemoryStream(bytes, writable: false))
        using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                if (entry.Name != "package/dist/providers/data/radius.json") continue;
                Check(shard is null && entry.DataStream is not null &&
                    entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile, "Pinned shard differs.");
                using var owned = new MemoryStream(); await entry.DataStream!.CopyToAsync(owned);
                shard = owned.ToArray();
            }
        }
        Check(shard is { Length: 19164 }, "Pinned released radius shard is missing.");
        Equal("8e868af981cc64a39da11b135a96252e3eeccb14b92e80d8fbba899ff96f2616", Hash(shard!));
        var catalog = FrozenModelCatalog.ReadProviderJson("radius", shard!);
        var selected = catalog.Models.Where(row => row.Type == CatalogModelType.Chat && row.DeclaredApi == "pi-messages").ToArray();
        Equal(28, selected.Length);
        var requests = new List<JsonData>();
        using var handler = new Handler(async (request, token) =>
        {
            Equal("https://radius.pi.dev/v1/messages", request.RequestUri!.AbsoluteUri);
            requests.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
            return Response(new OwnedBody(TextWire()));
        });
        using var client = new HttpClient(handler);
        var provider = new PiMessagesModelProvider(catalog, client, new("inert-key"));
        var registry = new ModelTransportRegistry([provider]);
        Equal(28, provider.Models.Count); Equal(28, registry.Models.Length);
        foreach (var row in selected)
        {
            var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
            Check(ReferenceEquals(row, provider.GetCatalogModel(model)) &&
                provider.GetCatalogModel(model).Raw.ToString() == row.Raw.ToString(), "Released raw metadata was narrowed.");
            var result = await new ChatClient(registry).CompleteAsync(new(model, Inputs(), 123));
            Check(result.Failure is null && result.Message.Model == row.Id && result.Message.Provider == row.Provider,
                "Released row routed another model.");
            Equal(row.Id, requests[^1].Value.GetProperty("model").GetString());
        }
        Equal(28, requests.Count); Check(!handler.Disposed, "Released-row selection disposed the borrowed client.");
        // Authored injected responses are not genuine Source/provider execution or full catalog acceptance.
    }

    private static async Task ToolRoundTrip()
    {
        var first = new OwnedBody(ToolWire("toolUse"), held: true);
        var bodies = new List<JsonData>(); var contents = new List<BodyContent>(); var requests = new List<RequestContent>();
        var assistantEntered = Gate(); var releaseAssistant = Gate(); var resultEntered = Gate(); var releaseResult = Gate();
        var adapter = new Adapter(); var policy = new Policy();
        using var handler = new Handler(async (request, token) =>
        {
            bodies.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
            var requestOwner = new RequestContent(request.Content); request.Content = requestOwner; requests.Add(requestOwner);
            var content = new BodyContent(bodies.Count == 1 ? first : new OwnedBody(TextWire()));
            contents.Add(content); return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        var registry = new ModelTransportRegistry([new PiMessagesModelProvider(Catalog(), client, new("inert-key"))]);
        AssistantMessage? committedAssistant = null; ToolResultMessage? committedTool = null;
        await using var agent = new NativeAgent(new(Model, registry, [new("inspect", new ToolInvoker([adapter], policy))]),
            () => 123, new Sink(async (observation, _) =>
            {
                if (observation is AssistantMessageEnded assistant && assistant.Message.StopReason == StopReason.ToolUse)
                {
                    Check(first.Disposed && contents[0].Disposed && requests[0].Disposed, "Assistant commit preceded real HTTP cleanup.");
                    committedAssistant = assistant.Message; assistantEntered.TrySetResult(); await releaseAssistant.Task;
                }
                if (observation is ToolResultMessageEnded tool)
                { committedTool = tool.Message; resultEntered.TrySetResult(); await releaseResult.Task; }
            }), new(StreamCapacity: 1));
        var inputs = Inputs(); var originalInputs = inputs.Select(entry => entry.WireBody.ToString()).ToArray();
        Task? ownedRun = null;
        try
        {
            ownedRun = agent.PromptAsync(inputs);
            await first.CleanupEntered.Task.WaitAsync(DiagnosticDeadline);
            Check(!ownedRun.IsCompleted && !assistantEntered.Task.IsCompleted &&
                adapter.Executions == 0 && policy.Authorizations == 0 && bodies.Count == 1 &&
                !agent.Snapshot.Messages.Any(entry => entry.Role == "assistant"), "Held body leaked commit or authority.");
            first.ReleaseCleanup.TrySetResult();
            await assistantEntered.Task.WaitAsync(DiagnosticDeadline);
            Check(!ownedRun.IsCompleted && adapter.Executions == 0 && policy.Authorizations == 0 && bodies.Count == 1,
                "Held assistant barrier leaked tool authorization.");
            releaseAssistant.TrySetResult();
            await resultEntered.Task.WaitAsync(DiagnosticDeadline);
            Equal(1, policy.Authorizations); Equal(1, adapter.Executions); Equal(1, bodies.Count);
            Check(!ownedRun.IsCompleted, "Held tool-result barrier leaked continuation.");
            releaseResult.TrySetResult();
            await ownedRun;
            Equal(2, bodies.Count);
            var second = bodies[1].Value.GetProperty("context").GetProperty("messages");
            Equal(4, second.GetArrayLength());
            for (var index = 0; index < inputs.Length; index++)
            { Equal(originalInputs[index], inputs[index].WireBody.ToString()); SameJson(inputs[index].WireBody.Value, second[index]); }
            SameJson(PiWireJson.WriteMessage(committedAssistant!).Value, second[2]);
            SameJson(ToolResultMessageMaterializer.ToTranscript(committedTool!, 123).WireBody.Value, second[3]);
            SameJson(JsonData.Parse("""{"value":7,"keep":null}""").Value, adapter.Last!.Arguments.Value);
            SameJson(JsonData.Parse("""{"role":"toolResult","toolCallId":"owned-call","toolName":"inspect","content":[{"type":"text","text":"owned π\u0000"}],"details":null,"usage":{"raw":1,"keep":null},"isError":false,"timestamp":123}""").Value, second[3]);
            Equal("signature", second[2].GetProperty("content")[0].GetProperty("opaqueSignature").GetString());
            SameJson(JsonData.Parse(Usage).Value, second[2].GetProperty("usage"));
            Check(agent.Snapshot.CompletedToolOutcomes.Length == 1 && !agent.Snapshot.IsRunning &&
                contents.All(content => content.Disposed) && requests.All(request => request.Disposed),
                "High-level Agent did not settle its owned turn.");
            agent.FollowUp(new("user", JsonData.Parse("""{"role":"user","content":"Continue once.","timestamp":123,"opaque":{"keep":null}}""")));
            var before = agent.Snapshot.Messages;
            ownedRun = agent.ContinueAsync(); await ownedRun;
            Equal(3, bodies.Count);
            var third = bodies[2].Value.GetProperty("context").GetProperty("messages");
            Equal(before.Length + 1, third.GetArrayLength());
            for (var index = 0; index < before.Length; index++) SameJson(before[index].WireBody.Value, third[index]);
            Equal("Continue once.", third[before.Length].GetProperty("content").GetString());
            Check(!handler.Disposed, "Agent disposed its borrowed HTTP client.");
        }
        finally
        {
            first.ReleaseCleanup.TrySetResult(); releaseAssistant.TrySetResult(); releaseResult.TrySetResult();
            agent.Abort(); if (ownedRun is not null) await Join(ownedRun); await agent.WaitForIdleAsync();
        }
    }

    private static async Task FailureAuthority()
    {
        foreach (var scenario in new[] { "http", "provider", "malformed", "eof", "truncated", "cleanup" })
        {
            var wire = scenario switch
            {
                "provider" => ToolWire("error", terminalType: "error"),
                "malformed" => ToolWire("toolUse", includeTerminal: false) + "data: {bad}\n\n",
                "eof" => ToolWire("toolUse", includeTerminal: false),
                "truncated" => ToolWire("length"),
                _ => ToolWire("toolUse")
            };
            var body = new OwnedBody(scenario == "http" ? """{"error":{"message":"private","code":"owned"}}""" : wire)
            { FailCleanup = scenario == "cleanup" };
            var requests = 0;
            using var handler = new Handler((_, _) =>
            {
                requests++; return Task.FromResult(Response(body, scenario == "http" ? HttpStatusCode.BadRequest : HttpStatusCode.OK));
            });
            using var client = new HttpClient(handler);
            var adapter = new Adapter(); var policy = new Policy();
            var registry = new ModelTransportRegistry([new PiMessagesModelProvider(Catalog(), client, new("inert-key"))]);
            await using var agent = new NativeAgent(new(Model, registry, [new("inspect", new ToolInvoker([adapter], policy))],
                Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))),
                () => 123, new Sink((_, _) => ValueTask.CompletedTask));
            var result = await agent.PromptAsync(Inputs());
            Equal(1, requests); Equal(0, adapter.Executions); Equal(0, policy.Authorizations);
            Check(body.Disposed && !agent.Snapshot.IsRunning, "Failed assistant retained HTTP ownership.");
            if (scenario == "truncated")
                Equal(ToolFailureKind.Truncated, result.Turns.Single().Result.Tools.Outcomes.Single().Result.Failure!.Kind);
            else
            {
                var expected = scenario switch
                { "malformed" => NativeChatFailureCode.MalformedStream, "eof" => NativeChatFailureCode.UnexpectedEof,
                    "cleanup" => NativeChatFailureCode.CleanupFailed, _ => NativeChatFailureCode.ProviderError };
                Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, expected), result.Turns.Single().Result.Chat.NativeDiagnostic);
                if (scenario == "cleanup") Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed),
                    result.Turns.Single().Result.Chat.NativeCleanupDiagnostic);
            }
        }
    }

    private static async Task ReviewedFailuresNoAuthority()
    {
        var scenarios = new (string Name, string? Delta, int Characters, int Depth, NativeChatFailureCode Code)[]
        {
            ("wire", null, 1024, 32, NativeChatFailureCode.ProviderError),
            ("characters", new string(' ', 4097), 4096, 32, NativeChatFailureCode.ResourceLimit),
            ("depth", new string('[', 17), 1024, 16, NativeChatFailureCode.ResourceLimit),
            ("number", """{"value":9007199254740992}""", 1024, 32, NativeChatFailureCode.UnsupportedFeature),
            ("unicode", "{\"value\":\"\\ud800\"}", 1024, 32, NativeChatFailureCode.UnsupportedFeature),
            ("duplicate", """{"value":1,"value":2}""", 1024, 32, NativeChatFailureCode.MalformedStream)
        };
        foreach (var scenario in scenarios)
        foreach (var cleanupFails in new[] { false, true })
        {
            var wire = scenario.Delta is null ?
                ToolWire("error", terminalType: "error").Replace("same-public-text", "provider-primary", StringComparison.Ordinal) :
                Sse("""{"type":"start"}""", """{"type":"toolcall_start","contentIndex":0,"id":"x","toolName":"inspect"}""",
                    JsonSerializer.Serialize(new { type = "toolcall_delta", contentIndex = 0, delta = scenario.Delta }));
            var body = new OwnedBody(wire, held: true) { FailCleanup = cleanupFails };
            BodyContent? response = null; RequestContent? requestOwner = null; var requests = 0; var assistantEntered = Gate();
            StreamTerminalEvent? observedTerminal = null;
            using var handler = new Handler(async (request, token) =>
            {
                requests++; await request.Content!.ReadAsStringAsync(token);
                requestOwner = new(request.Content); request.Content = requestOwner;
                response = new(body); return new HttpResponseMessage(HttpStatusCode.OK) { Content = response };
            });
            using var client = new HttpClient(handler);
            IModelProvider provider;
            if (scenario.Delta is null)
                provider = new PiMessagesModelProvider(Catalog(), client, new("inert-key"));
            else
            {
                // The provider's public call configuration has no limit overrides; use the existing
                // IModelProvider seam to admit this explicitly bounded real Pi transport to the Agent.
                var transport = new PiSharp.AI.Protocols.PiMessages.PiMessagesHttpSseTransport(client, Model,
                    new(Catalog().Models[0].Raw, "inert-key")
                    { MaximumContentCharacters = scenario.Characters, MaximumJsonDepth = scenario.Depth });
                provider = new MutableProvider("authored", [Model], transport);
            }
            var registry = new ModelTransportRegistry([provider]);
            var adapter = new Adapter(); var policy = new Policy();
            await using var agent = new NativeAgent(new(Model, registry, [new("inspect", new ToolInvoker([adapter], policy))]),
                () => 123, new Sink((observation, _) =>
                {
                    if (observation is TurnStreamObserved { Event: StreamTerminalEvent terminalFrame })
                        observedTerminal = terminalFrame;
                    if (observation is AssistantMessageEnded)
                    {
                        Check(body.Disposed && response!.Disposed && requestOwner!.Disposed,
                            "Reviewed failed assistant committed before actual HTTP release.");
                        assistantEntered.TrySetResult();
                    }
                    return ValueTask.CompletedTask;
                }));
            var original = agent.PromptAsync(Inputs());
            try
            {
                await Task.WhenAny(body.ReadEntered.Task, original).WaitAsync(DiagnosticDeadline);
                Check(body.ReadEntered.Task.IsCompleted && requests == 1 && response is not null && requestOwner is not null,
                    "Reviewed failure never admitted the HTTP request/body read: " + scenario.Name);
                await body.CleanupEntered.Task.WaitAsync(DiagnosticDeadline);
                Check(!original.IsCompleted && !assistantEntered.Task.IsCompleted && observedTerminal is null &&
                    !agent.Snapshot.Messages.Any(entry => entry.Role == "assistant") && requests == 1 &&
                    adapter.Executions == 0 && policy.Authorizations == 0,
                    "Reviewed failure escaped held HTTP cleanup: " + scenario.Name);
                body.ReleaseCleanup.TrySetResult();
                var result = await original; var chat = result.Turns.Single().Result.Chat;
                Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, scenario.Code), chat.NativeDiagnostic);
                Equal(chat.NativeDiagnostic, chat.Failure!.NativeDiagnostic);
                Equal(cleanupFails ? new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed) : null,
                    chat.NativeCleanupDiagnostic);
                Equal(AgentLoopStopReason.ChatFailure, result.Reason); Equal(StopReason.Error, chat.Message.StopReason);
                Equal(1, requests); Equal(0, adapter.Executions); Equal(0, policy.Authorizations);
                Check(assistantEntered.Task.IsCompleted && !agent.Snapshot.IsRunning && !handler.Disposed,
                    "Reviewed failure retained ownership or disposed the borrowed client.");
                if (scenario.Delta is null)
                {
                    var properties = chat.Message.ExtraProperties ?? throw new InvalidOperationException("Provider error lost metadata.");
                    Equal("provider-primary", properties.Values["errorMessage"].Value.GetString());
                    Equal(1, chat.Message.Content.Length);
                    SameJson(JsonData.Parse(Usage).Value, PiWireJson.WriteMessage(chat.Message).Value.GetProperty("usage"));
                    Equal("owned-response", properties.Values["responseId"].Value.GetString());
                    Equal("owned-level", properties.Values["providerThinkingLevel"].Value.GetString());
                    Equal("signature", ((ToolCallContent)chat.Message.Content[0]).ExtraProperties!.Values["opaqueSignature"].Value.GetString());
                }
                else Equal(0, chat.Message.Content.Length);
                var terminal = observedTerminal ?? throw new InvalidOperationException("Agent omitted its failed terminal observation.");
                Equal(chat.NativeDiagnostic, terminal.NativeDiagnostic); Equal(chat.NativeCleanupDiagnostic, terminal.NativeCleanupDiagnostic);
            }
            finally { body.ReleaseCleanup.TrySetResult(); agent.Abort(); await Join(original); await agent.WaitForIdleAsync(); }
        }
    }

    private static async Task AbortOwnership()
    {
        var body = new OwnedBody(ToolWire("toolUse"), held: true) { HoldRead = true };
        using var handler = new Handler((_, _) => Task.FromResult(Response(body)));
        using var client = new HttpClient(handler);
        var adapter = new Adapter(); var policy = new Policy(); var assistantEntered = Gate();
        var registry = new ModelTransportRegistry([new PiMessagesModelProvider(Catalog(), client, new("inert-key"))]);
        await using var agent = new NativeAgent(new(Model, registry, [new("inspect", new ToolInvoker([adapter], policy))]),
            () => 123, new Sink((observation, _) =>
            {
                if (observation is AssistantMessageEnded)
                { Check(body.Disposed, "Abort assistant settled before cleanup."); assistantEntered.TrySetResult(); }
                return ValueTask.CompletedTask;
            }));
        var original = agent.PromptAsync(Inputs());
        try
        {
            await body.ReadEntered.Task.WaitAsync(DiagnosticDeadline);
            agent.Abort();
            await body.CleanupEntered.Task.WaitAsync(DiagnosticDeadline);
            Check(!original.IsCompleted && !assistantEntered.Task.IsCompleted && policy.Authorizations == 0 &&
                adapter.Executions == 0, "Abort escaped held real HTTP cleanup.");
            body.ReleaseCleanup.TrySetResult();
            var result = await original;
            Equal(StopReason.Aborted, result.Turns.Single().Result.Chat.Message.StopReason);
            Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.Cancelled),
                result.Turns.Single().Result.Chat.NativeDiagnostic);
            Equal(0, policy.Authorizations); Equal(0, adapter.Executions);
            Check(!handler.Disposed && !agent.Snapshot.IsRunning, "Aborted Agent retained ownership or disposed its client.");
        }
        finally { body.ReleaseCleanup.TrySetResult(); agent.Abort(); await Join(original); await agent.WaitForIdleAsync(); }
    }

    private static FrozenModelCatalog Catalog() => FrozenModelCatalog.ReadProviderJson("authored", Encoding.UTF8.GetBytes(
        """{"pi-messages":{"chat:inert":{"type":"chat","id":"inert","name":"Inert","api":"pi-messages","provider":"authored","baseUrl":"https://pi-messages.invalid/v1","input":["text"],"reasoning":true,"contextWindow":64,"maxTokens":32,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"opaque":{"ordered":["z","a"],"keep":null}}}}"""));
    private static ImmutableArray<TranscriptEntry> Inputs() =>
    [
        new("system", JsonData.Parse("""{"role":"system","content":"Inspect once.","timestamp":123,"toolsAdded":[{"name":"inspect","description":"Inert authored tool.","parameters":{"type":"object","properties":{"value":{"type":"number"},"keep":{"type":"null"}},"required":["value","keep"],"additionalProperties":false}}]}""")),
        new("user", JsonData.Parse("""{"role":"user","content":"Inspect.","timestamp":123,"opaque":{"ordered":[2,1],"keep":null}}"""))
    ];
    private static string TextWire() => Sse("""{"type":"start"}""",
        """{"type":"text_start","contentIndex":0}""",
        """{"type":"text_end","contentIndex":0,"content":"final","contentSignature":"text-signature"}""",
        """{"type":"done","reason":"stop","usage":""" + Usage + """}""");
    private static string ToolWire(string reason, bool includeTerminal = true, string terminalType = "done") =>
        Sse("""{"type":"start"}""", """{"type":"toolcall_start","contentIndex":0,"id":"owned-call","toolName":"inspect"}""",
            """{"type":"toolcall_delta","contentIndex":0,"delta":"{\"value\":1"}""",
            """{"type":"toolcall_end","contentIndex":0,"toolCall":{"type":"toolCall","id":"owned-call","name":"inspect","arguments":{"value":7,"keep":null},"opaqueSignature":"signature"}}""") +
        (includeTerminal ? Sse("{\"type\":\"" + terminalType + "\",\"reason\":\"" + reason + "\",\"usage\":" + Usage +
            ",\"responseId\":\"owned-response\",\"providerThinkingLevel\":\"owned-level\",\"errorMessage\":\"same-public-text\"}") : "");
    private static string Sse(params string[] frames) => string.Concat(frames.Select(frame => "data: " + frame + "\r\n\r\n"));
    private static HttpResponseMessage Response(OwnedBody body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new BodyContent(body) };
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Join(Task original) { try { await original; } catch { } }
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository missing.");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void SameJson(JsonElement expected, JsonElement actual)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var left = expected.EnumerateObject().ToArray(); var right = actual.EnumerateObject().ToArray(); Equal(left.Length, right.Length);
            foreach (var field in left) { Check(actual.TryGetProperty(field.Name, out var value), "Missing property " + field.Name); SameJson(field.Value, value); }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var index = 0; index < expected.GetArrayLength(); index++) SameJson(expected[index], actual[index]); }
        else if (expected.ValueKind == JsonValueKind.Number)
            Equal(BitConverter.DoubleToInt64Bits(expected.GetDouble()), BitConverter.DoubleToInt64Bits(actual.GetDouble()));
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString());
    }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private sealed class MutableProvider(string id, IEnumerable<ModelDescriptor> models, IChatTransport transport) : IModelProvider
    { public string ProviderId => id; public List<ModelDescriptor> List { get; } = [.. models]; public IReadOnlyList<ModelDescriptor> Models => List; public IChatTransport Transport => transport; }
    private sealed class EffectTransport : IChatTransport
    { public int Calls; public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken token = default) { Calls++; throw new InvalidOperationException("Unexpected admission."); } }
    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "inspect"; public int Executions; public PreparedToolAction? Last;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) =>
            ValueTask.FromResult(new PreparedToolAction(Name, "inspect", PreparedToolActionKind.Path, "inert-target",
                invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(action.Arguments.Value.GetProperty("value").GetDouble() == 7 &&
                action.Arguments.Value.GetProperty("keep").ValueKind == JsonValueKind.Null);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Executions++; Last = action;
            return ValueTask.FromResult(ToolResult.FromJson(JsonData.Parse(
                """{"content":[{"type":"text","text":"owned π\u0000"}],"details":null,"usage":{"raw":1.00,"keep":null},"opaque":{"ordered":[2,1]}}""")));
        }
    }
    private sealed class Policy : IToolActionPolicy
    { public int Authorizations; public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Authorizations++; return ValueTask.FromResult(new ToolActionAuthorization(true)); } }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback(observation, token); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { public bool Disposed; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token);
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); } }
    private sealed class BodyContent(OwnedBody body) : HttpContent
    {
        public bool Disposed;
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Unexpected buffering.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
    }
    private sealed class RequestContent(HttpContent inner) : HttpContent
    {
        public bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed |= disposing; if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class OwnedBody(string wire, bool held = false) : MemoryStream(Encoding.UTF8.GetBytes(wire), writable: false)
    {
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public bool HoldRead, FailCleanup, Disposed;
        private Task? _cleanup;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            ReadEntered.TrySetResult();
            if (HoldRead) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return await base.ReadAsync(buffer, token);
        }
        public override ValueTask DisposeAsync() => new(_cleanup ??= Cleanup());
        private async Task Cleanup()
        {
            CleanupEntered.TrySetResult(); if (held) await ReleaseCleanup.Task;
            Disposed = true; base.Dispose(true);
            if (FailCleanup) throw new IOException("private cleanup failure");
        }
    }
}
