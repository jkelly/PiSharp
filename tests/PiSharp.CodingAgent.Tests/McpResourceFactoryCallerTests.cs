using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
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
using PiSharp.Sessions.Storage;

// Authored actual factory/lifecycle controls, not registered or executed in this source slice.
internal static class McpResourceFactoryCallerTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-resource-factory.actual-admitted-factory-registers-before-discovery-and-binds-native-calls", Pipeline),
        ("mcp-resource-factory.absent-resource-dependency-preserves-existing-catalog", Absent),
        ("mcp-resource-factory.held-output-joins-actual-attachment-shutdown", HeldOutput),
        ("mcp-resource-factory.multicast-registration-callbacks-refuse-before-effects", Multicast)
    ];
    private static readonly ModelDescriptor Model = new("resource-factory", "openai-responses", "fixture");
    private static JsonData Json(string value) => JsonData.Parse(value);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("Resource factory control failed."); }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal int Calls;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal readonly List<string> Methods = []; internal McpInvocationIdentity? Identity;
        internal readonly TaskCompletionSource Closed = Gate(); internal int Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize") return ValueTask.FromResult(Json("""{"protocolVersion":"2025-11-25","capabilities":{"resources":{}},"serverInfo":{"name":"synthetic","version":"1"}}"""));
            Methods.Add(method); Identity = options.InvocationIdentity;
            return ValueTask.FromResult(Json(method switch
            {
                "resources/list" => """{"resources":[{"uri":"data://a","name":"a"}]}""",
                "resources/templates/list" => """{"resourceTemplates":[{"uriTemplate":"data://{x}","name":"a"}]}""",
                "resources/read" => """{"contents":[{"uri":"data://a.bin","blob":"AQI="}]}""",
                _ => throw new IOException("Unexpected resource method")
            }));
        }
        public Task CloseAsync() { Closes++; Closed.TrySetResult(); return Task.CompletedTask; }
    }
    private sealed class Transport : IChatTransport
    {
        private int calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var first = Interlocked.Increment(ref calls) == 1;
            AssistantContent content = first ? new ToolCallContent("factory-resource", McpResourceTools.ReadResource, Json("""{"server":"demo","uri":"data://a.bin"}""")) : new TextContent("done");
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [content], TokenUsage.Zero, first ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (content is ToolCallContent tool) { yield return new ToolCallStarted(0, tool with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, tool); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            await Task.CompletedTask; yield return new StreamDone(final.StopReason, final);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ExtensionRegistry ServerRegistry = new(), ResourceRegistry = new();
        internal readonly Policy Policy = new(); internal readonly Channel Channel = new();
        internal ReplaceableAgentSession Owner = null!; internal McpAdmittedResourceRegistration? Registration;
        internal int DiscoveryTools, Saves; internal McpResourceOutputSaver? SaveOverride;
        internal static async Task<Fixture> Create(bool optedIn)
        {
            var f = new Fixture();
            try
            {
                var serverScope = await f.ServerRegistry.ActivateAsync("factory-server", new EmptyExtension());
                var resourceScope = await f.ResourceRegistry.ActivateAsync("factory-resources", new EmptyExtension());
                if (optedIn) f.Registration = new(f.ResourceRegistry, resourceScope, f.Policy, (_, _, _) => ValueTask.FromResult(true),
                    (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks,
                    (data, extension, identity, token) =>
                    {
                        f.Saves++;
                        if (f.SaveOverride?.GetInvocationList().Length > 1) throw new IOException("One synthetic saver required.");
                        return f.SaveOverride is { } saver ? saver(data, extension, identity, token) : ValueTask.FromResult("synthetic/resource" + extension);
                    });
                var baseRegistry = new SessionRuntimeRegistry([new(Model, new Transport())], [], f.Policy, new() { BindNestedCallsToSessionOwner = true });
                var entry = new McpServerEntry("demo", McpConfigurationReader.Validate("demo", Json("""{"command":"inert","exposure":"direct"}""").Value).Config!, "fixture", McpConfigurationScope.Extension);
                var factory = new McpSessionRuntimeFactory((cwd, generation, token) => ValueTask.FromResult(new McpSessionRuntimeAdmission(
                    baseRegistry, f.ResourceRegistry, f.ServerRegistry, f.Policy, new([entry], []),
                    [new("demo", actual => Check(ReferenceEquals(actual, entry)), (actual, current, cancellation) =>
                        McpPreOpenServerCapture.AcquireAsync(actual, f.ServerRegistry, serverScope, current, f.Policy,
                            (_, _, _) => ValueTask.FromResult(true), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(f.Channel),
                            new(generation, "0.99.1"), (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation))],
                    false, (plan, current) =>
                    {
                        f.DiscoveryTools = current.RegisteredTools.Length;
                        Check(plan.ResourceToolsExposure == McpExposure.Direct && f.DiscoveryTools == (optedIn ? 3 : 0));
                        return new(current, []);
                    }) { ResourceRegistration = f.Registration }));
                var backend = new SessionStorageBackend(Path.Combine(Path.GetTempPath(), "resource-factory-" + Guid.NewGuid().ToString("N")), SessionStorageMode.InMemory);
                var ids = 0; var lifecycle = new PersistentSessionLifecycle(baseRegistry, () => 1, () => "resource-" + ++ids, backend: backend, runtimeForAttachment: factory.AcquireAsync);
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "resources", timestamp = "2026-10-05T00:00:00.000Z", cwd = backend.Directory }));
                var session = await lifecycle.CreateAsync(Path.Combine(backend.Directory, "resources.jsonl"), header, Model);
                try { f.Owner = await lifecycle.AttachAsync(session); }
                catch { await session.DisposeAsync(); throw; }
                if (optedIn)
                {
                    // Discovery supplies the available catalog; this caller explicitly
                    // selects its three admitted tools through the owning session.
                    var before = session.Snapshot.Log.Entries;
                    await session.SetActiveToolsAsync([McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource]);
                    var active = session.GetActiveTools();
                    var expected = new[] { McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource };
                    if (!active.SequenceEqual(expected))
                        throw new InvalidOperationException($"Explicit resource selection mismatch; activeCount={active.Length}; activeNames={string.Join(",", active.Take(8).Select(name => expected.Contains(name) ? name : "other"))}.");
                    Check(session.CaptureToolCatalogRegistry().UsesFinalActionPolicy(f.Policy));
                    var after = session.Snapshot.Log.Entries;
                    Check(after.Length == before.Length + 1 && after.Take(before.Length).Select(entry => entry.WireBody.ToString())
                        .SequenceEqual(before.Select(entry => entry.WireBody.ToString())));
                    var declared = after[^1].WireBody.Value.GetProperty("message");
                    Check(declared.GetProperty("toolsRemoved").GetArrayLength() == 0 &&
                        declared.GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(expected));
                }
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Owner is not null) await Owner.DisposeAsync(); }
            finally { try { await ServerRegistry.DisposeAsync(); } finally { await ResourceRegistry.DisposeAsync(); } }
        }
    }
    private static async Task Pipeline()
    {
        await using var f = await Fixture.Create(true); var session = f.Owner.Current.Session;
        var configuration = session.CaptureToolCatalogRegistry().Resolve(session.Snapshot.Context, Model).Configuration;
        foreach (var name in new[] { McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource })
        {
            var call = new ToolCallContent("factory-resource", name, name == McpResourceTools.ReadResource ? Json("""{"server":"demo","uri":"data://a.bin"}""") : Json("{}"));
            var invocation = new ToolInvocation(new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
            var result = await configuration.Tools.Single(tool => tool.Name == name).Executor.ExecuteAsync(invocation, default);
            Check(!result.IsError && f.Channel.Identity is { OwnerId: "factory-resources", ToolCallId: "factory-resource" });
            Check(f.Channel.Identity!.SessionGeneration == f.Owner.Current.Generation);
        }
        Check(f.DiscoveryTools == 3 && f.Policy.Calls == 3 && f.Saves == 1 && f.Channel.Methods.SequenceEqual(new[] { "resources/list", "resources/templates/list", "resources/read" }));
    }
    private static async Task Absent()
    {
        await using var f = await Fixture.Create(false);
        Check(f.DiscoveryTools == 0 && f.Owner.Current.Session.CaptureToolCatalogRegistry().RegisteredTools.IsEmpty && f.Channel.Methods.Count == 0);
    }
    private static async Task HeldOutput()
    {
        Console.Error.WriteLine("PHASE mcp-resource-factory.held-output fixture-create-start");
        var f = await Fixture.Create(true); var entered = Gate(); var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failures = new List<Exception>();
        Task? prompt = null; Task? closing = null;
        void Phase(string phase)
        {
            try { Console.Error.WriteLine($"PHASE mcp-resource-factory.held-output {phase}; saves={f.Saves}; channelCloses={f.Channel.Closes}; promptStatus={prompt?.Status.ToString() ?? "null"}; closeStatus={closing?.Status.ToString() ?? "null"}"); }
            catch (Exception error) { failures.Add(error); }
        }
        Phase("fixture-created-active-selection-checked");
        f.SaveOverride = (_, _, _, _) => { entered.TrySetResult(); return new(saved.Task); };
        try
        {
            prompt = f.Owner.Current.Session.PromptAsync(new TranscriptEntry("user", Json("""{"role":"user","content":"read","timestamp":1}""")), deadline.Token);
            Phase("prompt-started");
            var first = await Task.WhenAny(entered.Task, prompt).WaitAsync(deadline.Token);
            Phase("saver-entry-observed");
            if (!ReferenceEquals(first, entered.Task))
                throw new InvalidOperationException($"Saver entry not reached before prompt completion; promptStatus={prompt.Status}; saves={f.Saves}.");
            Phase("saver-entered"); closing = f.Owner.DisposeAsync().AsTask(); Phase("close-started");
            var stopped = await Task.WhenAny(f.Channel.Closed.Task, closing, prompt).WaitAsync(deadline.Token);
            Phase("channel-stop-observed");
            if (!ReferenceEquals(stopped, f.Channel.Closed.Task))
                throw new InvalidOperationException($"Channel stop not reached while output held; promptStatus={prompt.Status}; closeStatus={closing.Status}.");
            Phase("channel-closed"); Check(!prompt.IsCompleted && !closing.IsCompleted && f.Saves == 1);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            saved.TrySetResult("synthetic/held.bin"); Phase("saver-released");
            // Also stop on failed preconditions. No timed wait substitutes for joining
            // either original, and faulted OCE is not treated as task cancellation.
            try { closing ??= f.Owner.DisposeAsync().AsTask(); } catch (Exception error) { failures.Add(error); }
            if (prompt is not null) try { await prompt; }
            catch (Exception error)
            {
                if (!prompt.IsCanceled || !f.Owner.Current.LifetimeToken.IsCancellationRequested)
                    failures.Add((Exception?)prompt.Exception ?? error);
            }
            Phase("prompt-joined");
            if (closing is not null) try { await closing; } catch (Exception error) { failures.Add((Exception?)closing.Exception ?? error); }
            Phase("close-joined");
            Task? cleanup = null;
            try { cleanup = f.DisposeAsync().AsTask(); await cleanup; }
            catch (Exception error) { failures.Add((Exception?)cleanup?.Exception ?? error); }
        }
        if (failures.Count != 0) throw new AggregateException("Held output precondition or original failed.", failures);
        Check(f.Channel.Closes == 1);
    }
    private static async Task Multicast()
    {
        await using var registry = new ExtensionRegistry(); var scope = await registry.ActivateAsync("multicast", new EmptyExtension()); var effects = 0;
        ExtensionToolArgumentValidator validator = (_, _, _) => { effects++; return ValueTask.FromResult(true); };
        McpPreparedHookComposer composer = (current, _) => { effects++; return current.PreparedToolHooks; };
        McpResourceOutputSaver saver = (_, _, _, _) => { effects++; return ValueTask.FromResult("synthetic"); };
        for (var index = 0; index < 3; index++)
        {
            try { _ = new McpAdmittedResourceRegistration(registry, scope, new Policy(), index == 0 ? validator + validator : validator,
                index == 1 ? composer + composer : composer, index == 2 ? saver + saver : saver); throw new IOException("Multicast admitted"); }
            catch (ArgumentException) { }
        }
        Check(effects == 0 && registry.CaptureSnapshot().Tools.IsEmpty);
    }
}
