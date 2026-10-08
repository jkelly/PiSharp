using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Resources;
using PiSharp.Sessions.Serialization;

// Authored synthetic prepared-pipeline controls. No registration/execution is authorized in this leaf.
internal static class McpOwnedResourceDispatchTests
{
    internal const string Prefix = "mcp-owned-resource-dispatch.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "actual-prepared-catalog-list-template-read-and-final-policy", Pipeline),
        (Prefix + "schema-and-final-denial-refuse-before-resource-effects", Denial),
        (Prefix + "unbound-owner-and-session-generation-refuse-before-capture", Generation),
        (Prefix + "held-output-normal-close-joins-native-prepared-original", () => HeldOutput(false)),
        (Prefix + "held-output-owning-shutdown-joins-native-prepared-original", () => HeldOutput(true)),
        (Prefix + "native-fault-evidence-survives-prepared-json-projection-and-transfer", Evidence),
        (Prefix + "named-list-terminal-multifault-evidence-survives-prepared-error", () => TerminalEvidence(false, false)),
        (Prefix + "read-terminal-multifault-evidence-survives-prepared-error", () => TerminalEvidence(true, false)),
        (Prefix + "read-faulted-oce-retains-fault-original-through-prepared-error", () => TerminalEvidence(true, true)),
        (Prefix + "synchronous-capture-oce-retains-fault-evidence-with-live-owner", CaptureEvidence),
        (Prefix + "multicast-provider-saver-refuse-before-callback-effects", Multicast)
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Owned MCP resource dispatch control failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonData Json(string value) => JsonData.Parse(value);
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal bool Allowed = true; internal int Calls;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new ToolActionAuthorization(Allowed)); }
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal readonly List<string> Requests = []; internal McpInvocationIdentity? Identity;
        internal Func<string, Task<JsonData>>? Handler; internal int Closes;
        internal readonly TaskCompletionSource StopEntered = Gate();
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize") return ValueTask.FromResult(Json("""{"protocolVersion":"2025-11-25","capabilities":{"resources":{}},"serverInfo":{"name":"synthetic","version":"1"}}"""));
            Requests.Add(method); Identity = options.InvocationIdentity;
            if (Handler?.GetInvocationList().Length > 1) throw new InvalidOperationException("One synthetic handler required.");
            return new(Handler is { } handler ? handler(method) : Task.FromResult(method switch
            {
                "resources/list" => Json("""{"resources":[{"uri":"data://a","name":"a"}]}"""),
                "resources/templates/list" => Json("""{"resourceTemplates":[{"uriTemplate":"data://{x}","name":"template"}]}"""),
                "resources/read" => Json("""{"contents":[{"uri":"data://a.bin","blob":"AQI="}]}"""),
                _ => throw new InvalidOperationException("Unexpected method")
            }));
        }
        public Task CloseAsync() { Closes++; StopEntered.TrySetResult(); return Task.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly StartupSettingsTests.Fixture Files = new();
        internal readonly ExtensionRegistry Registry = new(); internal readonly Policy Policy = new(); internal readonly Channel Channel = new();
        internal OfflineSessionProfile Profile = null!; internal PersistentAgentSession Session = null!; internal ReplaceableAgentSession Owner = null!;
        internal RegistrationScope ResourceScope = null!; internal McpPreparedServer Server = null!; internal McpOwnedResourceDispatch Dispatch = null!;
        internal int Captures, Saves; internal McpResourceOutputSaver? SaveOverride; internal ScriptedTransport? Transport;
        internal ImmutableArray<SessionEntry> InitialEntries, ConnectedEntries;
        internal Func<IExtensionToolInvocationContext, IReadOnlyList<McpResourceServer>>? CaptureOverride;
        internal static async Task<Fixture> Create(bool scripted = false)
        {
            var f = new Fixture();
            try
            {
                var path = Path.Combine(f.Files.Root, "resource-catalog.jsonl");
                f.Profile = await OfflineSessionProfile.CreateAsync(f.Files.Root, path, null, [], [], [], default);
                IChatTransport transport = scripted ? f.Transport = new(f.Profile.SelectedModel) : f.Profile.Registry.Resolve(f.Profile.SelectedModel, []).Configuration.Transport;
                var baseRegistry = new SessionRuntimeRegistry([new(f.Profile.SelectedModel, transport)], [], f.Policy, new() { BindNestedCallsToSessionOwner = true });
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "resources", timestamp = "2026-10-05T00:00:00.000Z", cwd = f.Files.Root }));
                var ids = 0; f.Session = await PersistentAgentSession.CreateAsync(path, header, baseRegistry, f.Profile.SelectedModel, () => 1, () => "resource-" + ++ids);
                f.InitialEntries = f.Session.Snapshot.Log.Entries;
                f.Owner = new(f.Session, (_, _) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement open admitted")));
                var serverScope = await f.Registry.ActivateAsync("server-tool-scope", new EmptyExtension());
                f.ResourceScope = await f.Registry.ActivateAsync("resource-scope", new EmptyExtension());
                var config = McpConfigurationReader.Validate("demo", Json("""{"command":"inert","exposure":"direct"}""").Value).Config!;
                f.Server = new(new("demo", config, "synthetic", McpConfigurationScope.Extension), f.Registry, serverScope, f.Owner, f.Policy,
                    ValidateArguments, (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(f.Channel), new(f.Owner.Current.Generation, "0.99.1"),
                    (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks);
                await f.Server.ConnectAsync();
                f.ConnectedEntries = f.Session.Snapshot.Log.Entries;
                f.Dispatch = new(invocation =>
                {
                    f.Captures++;
                    if (f.CaptureOverride?.GetInvocationList().Length > 1) throw new InvalidOperationException("One synthetic capture required.");
                    return f.CaptureOverride is { } capture ? capture(invocation) : [f.Server.CaptureResourceServer(invocation)];
                },
                    (data, extension, identity, token) =>
                    {
                        f.Saves++;
                        if (f.SaveOverride?.GetInvocationList().Length > 1) throw new InvalidOperationException("One synthetic saver required.");
                        return f.SaveOverride is { } saver ? saver(data, extension, identity, token) : ValueTask.FromResult("synthetic/saved" + extension);
                    });
                f.Dispatch.Bind(f.Owner, f.Owner.Current, f.ResourceScope);
                var descriptors = f.Dispatch.CreateDescriptors("resources");
                var plan = f.Registry.PrepareToolCatalogReplacement(f.ResourceScope, [], descriptors, f.Registry.CaptureSnapshot());
                await f.Owner.PrepareAndPublishToolCatalogAsync(f.Owner.Current, (expected, token) =>
                {
                    var binding = new ExtensionAgentBinding(f.Registry, f.Policy, ValidateArguments, sessionCancellationToken: f.Owner.Current.LifetimeToken, capturedSnapshot: plan.PreviewSnapshot);
                    var added = binding.Registrations.Select(tool =>
                    {
                        var index = binding.Registrations.IndexOf(tool);
                        return new SessionRegisteredTool(binding.RegisteredToolDeclarations[index], binding.Adapters[index])
                        { Exposure = tool.Exposure, Namespace = tool.Namespace, DefaultActive = tool.DefaultActive, IsExtension = true };
                    }).ToImmutableArray();
                    return ValueTask.FromResult(new PreparedSessionToolCatalog(expected.WithToolCatalog(added, binding.PreparedHooks), descriptors.Select(tool => tool.Name).ToImmutableArray(), () => { plan.Commit(); }));
                });
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal static ValueTask<bool> ValidateArguments(ExtensionToolRegistrationInfo tool, JsonData arguments, CancellationToken token)
        {
            var value = arguments.Value;
            if (value.ValueKind != JsonValueKind.Object) return ValueTask.FromResult(false);
            var read = tool.Name == McpResourceTools.ReadResource;
            var valid = value.EnumerateObject().All(property => (read ? property.Name is "server" or "uri" : property.Name is "server" or "cursor") && property.Value.ValueKind == JsonValueKind.String);
            if (read) valid &= value.TryGetProperty("server", out _) && value.TryGetProperty("uri", out _);
            return ValueTask.FromResult(valid);
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Owner is not null) await Owner.DisposeAsync(); else if (Session is not null) await Session.DisposeAsync(); }
            finally { try { await Registry.DisposeAsync(); } finally { try { if (Profile is not null) await Profile.DisposeAsync(); } finally { Files.Dispose(); } } }
        }
    }
    private static ToolInvocation Call(string name, JsonData arguments)
    {
        var call = new ToolCallContent("actual-resource-call", name, arguments);
        return new(new AssistantMessage("openai-responses", "fixture", "model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
    }
    private static IToolExecutor Executor(Fixture fixture, string name) => fixture.Session.CaptureToolCatalogRegistry()
        .Resolve(fixture.Session.Snapshot.Context, fixture.Profile.SelectedModel).Configuration.Tools.Single(tool => tool.Name == name).Executor;
    private static async Task Pipeline()
    {
        await using var f = await Fixture.Create();
        foreach (var tool in new[] { McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource })
        {
            var result = await Executor(f, tool).ExecuteAsync(Call(tool, tool == McpResourceTools.ReadResource ? Json("""{"server":"demo","uri":"data://a.bin"}""") : Json("{}")), default);
            Check(!result.IsError && f.Channel.Identity is { OwnerId: "resource-scope", ToolCallId: "actual-resource-call" });
            Check(f.Channel.Identity!.SessionGeneration == f.Owner.Current.Generation && f.Channel.Identity.OwnerGeneration == f.ResourceScope.OwnerGeneration);
        }
        var entries = f.Session.Snapshot.Log.Entries;
        Console.Error.WriteLine("DIAGNOSTIC " + JsonSerializer.Serialize(new
        { source = "mcp-owned-resource-dispatch.pipeline", variant = "catalog-boundaries",
            initialCount = f.InitialEntries.Length, connectedCount = f.ConnectedEntries.Length, finalCount = entries.Length,
            policyCalls = f.Policy.Calls, captures = f.Captures, saves = f.Saves, activeCount = f.Session.GetActiveTools().Length }));
        Check(f.Policy.Calls == 3 && f.Captures == 3 && f.Saves == 1 && f.Session.GetActiveTools().Length == 3);
        Check(f.InitialEntries.Select(entry => entry.WireBody.Value.GetProperty("type").GetString())
            .SequenceEqual(new[] { "model_change", "thinking_level_change" }));
        // Even a resources-only server acknowledges an empty tool catalog at connect.
        // The separately admitted resource descriptors have their own durable boundary.
        Check(f.ConnectedEntries.Length == f.InitialEntries.Length + 1 && f.ConnectedEntries.Take(f.InitialEntries.Length)
             .Select(entry => entry.WireBody.ToString()).SequenceEqual(f.InitialEntries.Select(entry => entry.WireBody.ToString())));
        var connected = f.ConnectedEntries[^1].WireBody.Value;
        Check(connected.GetProperty("type").GetString() == "message");
        var emptyCatalog = connected.GetProperty("message");
        Check(emptyCatalog.GetProperty("role").GetString() == "system" &&
            emptyCatalog.GetProperty("toolsAdded").GetArrayLength() == 0 && emptyCatalog.GetProperty("toolsRemoved").GetArrayLength() == 0);
        Check(entries.Length == f.ConnectedEntries.Length + 1 && entries.Take(f.ConnectedEntries.Length)
            .Select(entry => entry.WireBody.ToString()).SequenceEqual(f.ConnectedEntries.Select(entry => entry.WireBody.ToString())));
        var catalog = entries[^1].WireBody.Value;
        Check(catalog.GetProperty("type").GetString() == "message");
        var message = catalog.GetProperty("message");
        Check(message.GetProperty("role").GetString() == "system" && message.GetProperty("toolsRemoved").GetArrayLength() == 0 &&
            message.GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString())
                .SequenceEqual(new[] { McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource }));
    }
    private static async Task Denial()
    {
        await using var f = await Fixture.Create();
        var malformed = await Executor(f, McpResourceTools.ReadResource).ExecuteAsync(Call(McpResourceTools.ReadResource, Json("{}")), default);
        Check(malformed.IsError && f.Policy.Calls == 0 && f.Captures == 0 && f.Channel.Requests.Count == 0);
        f.Policy.Allowed = false;
        var denied = await Executor(f, McpResourceTools.ListResources).ExecuteAsync(Call(McpResourceTools.ListResources, Json("{}")), default);
        Check(denied.Failure?.Kind == ToolFailureKind.Blocked && f.Policy.Calls == 1 && f.Captures == 0 && f.Channel.Requests.Count == 0);
    }
    private sealed class Context : IExtensionToolInvocationContext
    {
        public string OwnerId { get; init; } = "impostor";
        public long OwnerGeneration { get; init; } = 99;
        public long SessionGeneration { get; init; } = 99;
        public string ToolCallId => "native-test";
        public CancellationToken OperationCancellationToken => default;
        public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
        public ValueTask ReportUpdateAsync(JsonData result, CancellationToken token) => ValueTask.CompletedTask;
    }
    private static async Task Generation()
    {
        await using var f = await Fixture.Create();
        foreach (var context in new[] { new Context(), new Context { OwnerId = f.ResourceScope.OwnerId, OwnerGeneration = f.ResourceScope.OwnerGeneration } })
        {
            try { await f.Dispatch.ExecuteAsync(McpResourceTools.ListResources, Json("{}"), context); throw new InvalidOperationException("Foreign resource invocation admitted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("stale owner/session")); }
        }
        Check(f.Captures == 0 && f.Channel.Requests.Count == 0);
        var unbound = new McpOwnedResourceDispatch(_ => [], (_, _, _, _) => ValueTask.FromResult("synthetic"));
        try { await unbound.ExecuteAsync(McpResourceTools.ListResources, Json("{}"), new Context()); throw new InvalidOperationException("Unbound resource invocation admitted"); }
        catch (InvalidOperationException error) { Check(error.Message.Contains("unbound")); }
    }
    private sealed class ScriptedTransport(ModelDescriptor model) : IChatTransport
    {
        private int requests;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var useTool = Interlocked.Increment(ref requests) == 1;
            AssistantContent content = useTool ? new ToolCallContent("actual-resource-call", McpResourceTools.ReadResource, Json("""{"server":"demo","uri":"data://a.bin"}""")) : new TextContent("done");
            var final = new AssistantMessage(model.Api, model.Provider, model.Id, 1, [content], TokenUsage.Zero, useTool ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (content is ToolCallContent call) { yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            await Task.CompletedTask; yield return new StreamDone(final.StopReason, final);
        }
    }
    private static async Task HeldOutput(bool shutdown)
    {
        await using var f = await Fixture.Create(scripted: true); var entered = Gate(); var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SaveOverride = (_, _, _, _) =>
        {
            try { f.Dispatch.TakeOriginalCallbackFailures(); throw new InvalidOperationException("Output evidence self-transfer admitted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("cannot transfer")); }
            entered.SetResult(); return new(saved.Task);
        };
        var run = f.Session.PromptAsync(new TranscriptEntry("user", Json("""{"role":"user","content":"resource","timestamp":1}""")));
        await entered.Task; var closing = shutdown ? f.Owner.StopAdmissionAndJoinAsync() : f.Server.CloseAsync(); await f.Channel.StopEntered.Task;
        Check(!run.IsCompleted && !closing.IsCompleted && f.Saves == 1);
        saved.SetResult("synthetic/held.bin");
        try { await run; } catch (OperationCanceledException) when (shutdown) { }
        await closing; Check(f.Channel.Closes == 1);
    }
    private static async Task Evidence()
    {
        await using var f = await Fixture.Create(); var first = new IOException("resource first"); var second = new IOException("resource cleanup");
        var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); original.SetException(new[] { first, second });
        f.Channel.Handler = _ => original.Task;
        var result = await Executor(f, McpResourceTools.ListResources).ExecuteAsync(Call(McpResourceTools.ListResources, Json("{}")), default);
        Check(!result.IsError); var evidence = f.Dispatch.TakeOriginalCallbackFailures(); Check(evidence.Length == 1 && evidence[0].Invocation.ToolCallId == "actual-resource-call");
        static IEnumerable<Exception> Flatten(Exception error) => error is AggregateException aggregate ? aggregate.InnerExceptions.SelectMany(Flatten) :
            error is McpResourceCallbackException { InnerException: { } inner } ? Flatten(inner) : [error];
        var faults = evidence[0].Failures.SelectMany(Flatten).ToArray(); Check(faults.Contains(first) && faults.Contains(second) && f.Dispatch.TakeOriginalCallbackFailures().IsEmpty);
    }
    private static async Task CaptureEvidence()
    {
        await using var f = await Fixture.Create(); var failure = new OperationCanceledException("synchronous capture fault");
        f.CaptureOverride = invocation =>
        {
            Check(!invocation.OperationCancellationToken.IsCancellationRequested && !invocation.SessionCancellationToken.IsCancellationRequested &&
                !invocation.ExtensionLifetimeCancellationToken.IsCancellationRequested);
            throw failure;
        };
        var result = await Executor(f, McpResourceTools.ListResources).ExecuteAsync(Call(McpResourceTools.ListResources, Json("{}")), default);
        Check(result.IsError && f.Captures == 1 && f.Channel.Requests.Count == 0);
        var evidence = f.Dispatch.TakeOriginalCallbackFailures(); Check(evidence.Length == 1);
        McpResourceCallbackException? capture = null;
        void Visit(Exception error)
        {
            if (error is McpResourceCallbackException { Callback: "server capture" } callback) capture = callback;
            if (error is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) Visit(inner);
            else if (error.InnerException is { } inner) Visit(inner);
        }
        foreach (var error in evidence[0].Failures) Visit(error);
        Check(capture is { Original: null } && ReferenceEquals(capture.InnerException, failure) && f.Dispatch.TakeOriginalCallbackFailures().IsEmpty);
    }
    private static async Task TerminalEvidence(bool read, bool faultedCancellation)
    {
        await using var f = await Fixture.Create();
        Exception first = faultedCancellation ? new OperationCanceledException("faulted channel OCE") : new IOException("terminal first");
        var second = new IOException("terminal sibling");
        var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException(new[] { first, second }); f.Channel.Handler = _ => original.Task;
        var tool = read ? McpResourceTools.ReadResource : McpResourceTools.ListResources;
        var result = await Executor(f, tool).ExecuteAsync(Call(tool, read ? Json("""{"server":"demo","uri":"data://a"}""") : Json("""{"server":"demo"}""")), default);
        Check(result.IsError && original.Task.IsFaulted && !original.Task.IsCanceled);
        var evidence = f.Dispatch.TakeOriginalCallbackFailures();
        Check(evidence.Length == 1 && evidence[0].Invocation.ToolCallId == "actual-resource-call");
        var found = new List<Exception>(); var originals = new List<Task>();
        void Visit(Exception failure)
        {
            found.Add(failure);
            if (failure is McpResourceCallbackException { Original: { } task }) originals.Add(task);
            if (failure is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) Visit(inner);
            else if (failure.InnerException is { } inner) Visit(inner);
        }
        foreach (var failure in evidence[0].Failures) Visit(failure);
        Check(found.Contains(first) && found.Contains(second) && originals.Contains(original.Task) && f.Dispatch.TakeOriginalCallbackFailures().IsEmpty);
    }
    private static async Task Multicast()
    {
        var effects = 0; Func<IExtensionToolInvocationContext, IReadOnlyList<McpResourceServer>> capture = _ => { effects++; return []; };
        McpResourceOutputSaver saver = (_, _, _, _) => { effects++; return ValueTask.FromResult("synthetic"); };
        foreach (var multipleCapture in new[] { true, false })
        {
            try { _ = new McpOwnedResourceDispatch(multipleCapture ? capture + capture : capture, multipleCapture ? saver : saver + saver); throw new InvalidOperationException("Multicast dispatch admitted"); }
            catch (ArgumentException) { }
        }
        Check(effects == 0); await Task.CompletedTask;
    }
}
