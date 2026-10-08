using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

internal static partial class ToolActivationTests
{
    public const string Prefix = "native tool activation ";
    private static readonly ModelDescriptor Model = new("activation", "openai-responses", "fixture");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    ((IEnumerable<(string Name, Func<Task> Run)>)[
        (Prefix + "descriptor defaults distinguish registered declared and callable sets", Defaults),
        (Prefix + "normal native nested pipeline rejects hidden model-only and denied final actions", Reachability),
        (Prefix + "durable ordered activation reaches real requests and survives reopen", Persistence),
        (Prefix + "active request captures old loadout and busy publication is rejected", Boundary),
        (Prefix + "cancelled activation has no durable or live publication", CancelActivation),
        (Prefix + "cancelled callable joins original cleanup and stale capabilities refuse effects", CancelInvocation),
        (Prefix + "invalid exposure rolls back registration and hidden transcript admission fails", InvalidRegistration)
    ]).Concat(LoadoutCases()).Concat(CallbackActivationCases()).Concat(ConfigurationQueueAdmissionCases()).Concat(NamespaceCases());

    private static async Task Defaults()
    {
        await using var fixture = await Fixture.Create();
        Names(["direct", "outer"], fixture.Binding.Tools.Select(tool => tool.Name));
        Check(fixture.Binding.Registrations.Length == 7 && fixture.Binding.Adapters.Length == 7, "Registered tools disappeared.");
        Check(fixture.Binding.RegisteredToolDeclarations.Length == 7, "Registered schema catalog is incomplete.");
        Names(["direct", "outer"], new SessionSystemReplay().Replay([fixture.Binding.CreateDeclarationMessage("", 1)]).Tools
            .Select(tool => tool.Value.GetProperty("name").GetString()!));
        var explicitlyActive = new ExtensionAgentBinding(fixture.Extensions, fixture.Policy, Validate,
            options: new() { InvocationScopes = new(1), ActiveToolNames = ["hidden", "missing", "code", "inactive", "code"] });
        Names(["code", "inactive"], explicitlyActive.Tools.Select(tool => tool.Name));
        foreach (var name in new[] { "inactive", "hidden", "code", "deferred" })
        {
            var result = await fixture.Binding.Tools[0].Executor.ExecuteAsync(Call(name), default);
            Check(result.IsError, "A non-active model tool reached its callback.");
        }
        Check(fixture.Effects.Count == 0, "Inactive/hidden root requests caused effects.");
    }

    private static async Task Reachability()
    {
        await using var fixture = await Fixture.Create();
        fixture.Source.FirstTool = "outer";
        fixture.Outer = async (context, _) =>
        {
            fixture.Retained = context;
            Names(["direct", "code", "deferred", "denied"], context.Tools);
            foreach (var name in new[] { "direct", "code", "deferred", "denied", "inactive", "hidden", "outer" })
                fixture.Outcomes[name] = await context.ExecuteToolAsync(name, JsonData.EmptyObject);
            return Result("outer done");
        };
        var session = await fixture.New("main");
        await using var owner = fixture.Owner(session);
        await session.PromptAsync(Input());
        foreach (var name in new[] { "direct", "code", "deferred" }) Check(!fixture.Outcomes[name].IsError, "Callable tool was rejected.");
        foreach (var name in new[] { "denied", "inactive", "hidden", "outer" }) Check(fixture.Outcomes[name].IsError, "Unreachable/denied tool executed.");
        Names(["direct", "outer"], Declared(fixture.Source.Requests[0]));
        Check(fixture.Policy.SawFinalCodeArguments && !fixture.Effects.Contains("denied"), "Final-action pipeline was bypassed.");
        Check(((IExtensionToolInvocationContext)fixture.Retained!).SessionGeneration == owner.Current.Generation, "Actual owner generation was lost.");
        Check(fixture.Source.Requests.Count == 2, "Nested calls created model requests.");
        var rootResult = session.Snapshot.Context.LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value;
        Check(rootResult.GetProperty("nestedCalls").GetProperty("calls").GetArrayLength() == 7, "Nested ownership records were lost.");
    }

    private static async Task Persistence()
    {
        await using var fixture = await Fixture.Create();
        var session = await fixture.New("persist");
        await using var owner = fixture.Owner(session);
        try
        {
            await session.SetActiveToolsAsync(["hidden", "missing", "inactive", "code", "inactive"]);
            Names(["inactive", "code"], session.GetActiveTools());
            var checkpoint = session.Snapshot.Log.Entries.Length;
            await session.SetActiveToolsAsync(["inactive", "code"]);
            Check(checkpoint == session.Snapshot.Log.Entries.Length, "No-op activation appended another checkpoint.");
            fixture.Source.FirstTool = "inactive";
            await session.PromptAsync(Input());
            Names(["inactive", "code"], Declared(fixture.Source.Requests[0]));
            Check(fixture.Effects.SequenceEqual(["inactive"]), "Explicit default-inactive activation failed.");
            await session.SetActiveToolsAsync([]);
            Names([], session.GetActiveTools());
        }
        finally { await owner.DisposeAsync(); }
        await using var reopened = await fixture.Open(fixture.PathFor("persist"), default);
        Names([], reopened.GetActiveTools());
        await reopened.SetActiveToolsAsync(["code", "outer"]);
        await reopened.DisposeAsync();
        await using var again = await fixture.Open(fixture.PathFor("persist"), default);
        Names(["code", "outer"], again.GetActiveTools());
        Check(fixture.Binding.Registrations.Length == 7, "Deactivation unregistered callable tools.");
    }

    private static async Task Boundary()
    {
        await using var fixture = await Fixture.Create();
        fixture.Source.HoldFirstRequest = true;
        var session = await fixture.New("boundary");
        await using var owner = fixture.Owner(session);
        var run = session.PromptAsync(Input());
        try
        {
            await Reach(fixture.Source.Entered.Task, run);
            await Throws<InvalidOperationException>(() => session.SetActiveToolsAsync(["inactive"]));
            Names(["direct", "outer"], session.GetActiveTools());
            Names(["direct", "outer"], Declared(fixture.Source.Requests[0]));
        }
        finally { fixture.Source.Release.TrySetResult(); await run; }
        await session.SetActiveToolsAsync(["inactive"]);
        await session.PromptAsync(Input());
        Names(["inactive"], Declared(fixture.Source.Requests[1]));
        session.Steer(Input());
        await Throws<PersistentAgentSessionException>(() => session.SetActiveToolsAsync(["direct"]));
        Names(["inactive"], session.GetActiveTools());
        session.ClearPendingInputQueues();
    }

    private static async Task CancelActivation()
    {
        await using var fixture = await Fixture.Create();
        var session = await fixture.New("cancel-activation");
        await using var owner = fixture.Owner(session);
        var before = await ReadShared(fixture.PathFor("cancel-activation"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => session.SetActiveToolsAsync(["inactive"], cancelled.Token));
        using var during = new CancellationTokenSource();
        fixture.Clock = () => { during.Cancel(); return 1; };
        await Throws<OperationCanceledException>(() => session.SetActiveToolsAsync(["code"], during.Token));
        fixture.Clock = () => 1;
        var afterCancelledActivation = await ReadShared(fixture.PathFor("cancel-activation"));
        Check(before.SequenceEqual(afterCancelledActivation), "Cancelled activation changed the durable file.");
        Names(["direct", "outer"], session.GetActiveTools());
        await Throws<SessionRuntimeRegistryException>(() => session.SetActiveToolsAsync(default));
        await Throws<ArgumentException>(() => session.ConfigureAsync(new(SystemMessage: fixture.Binding.CreateDeclarationMessage("", 1))
            { ActiveToolNames = [] }));
        Names(["direct", "outer"], session.GetActiveTools());
    }

    private static async Task CancelInvocation()
    {
        await using var fixture = await Fixture.Create();
        var entered = Gate(); var cleanup = Gate(); var release = Gate();
        fixture.Source.FirstTool = "outer";
        fixture.Outer = async (context, token) =>
        {
            fixture.Retained = context;
            await context.ExecuteToolAsync("code", JsonData.EmptyObject, new(token));
            return Result("handled cancellation");
        };
        fixture.Code = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cleanup.TrySetResult(); await release.Task; }
            return Result("unreachable");
        };
        var session = await fixture.New("cancel-call");
        await using var owner = fixture.Owner(session);
        using var cancellation = new CancellationTokenSource();
        var run = session.PromptAsync(Input(), cancellation.Token);
        try
        {
            await Reach(entered.Task, run);
            cancellation.Cancel();
            await Reach(cleanup.Task, run);
            Check(!run.IsCompleted, "Cancellation detached original native cleanup.");
            await Throws<InvalidOperationException>(() => session.SetActiveToolsAsync(["inactive"]));
        }
        finally { cancellation.Cancel(); release.TrySetResult(); await run; }
        await session.SetActiveToolsAsync(["inactive"]);
        var effects = fixture.Effects.Count;
        var retained = fixture.Retained ?? throw new InvalidOperationException("Native callback context was not captured.");
        Check((await retained.ExecuteToolAsync("code", JsonData.EmptyObject)).IsError, "Ended context reached callable tools.");
        await owner.DisposeAsync();
        await Throws<PersistentAgentSessionException>(() => session.SetActiveToolsAsync(["direct"]));
        Check((await retained.ExecuteToolAsync("code", JsonData.EmptyObject)).IsError, "Retired generation reached callable tools.");
        Check(effects == fixture.Effects.Count, "Stale capability caused effects.");
    }

    private static async Task InvalidRegistration()
    {
        await using var registry = new ExtensionRegistry();
        await Throws<ExtensionRegistrationException>(() => registry.ActivateAsync("invalid", new Extension(host =>
        {
            host.RegisterTool(Tool("valid", ToolExposure.Direct));
            host.RegisterTool(Tool("invalid", (ToolExposure)999));
        })));
        Check(registry.CaptureSnapshot().Tools.IsEmpty, "Invalid exposure left staged residue.");
        await using var fixture = await Fixture.Create();
        var session = await fixture.New("hidden");
        await using var owner = fixture.Owner(session);
        var hidden = fixture.Binding.RegisteredToolDeclarations.Single(tool => tool.Value.GetProperty("name").GetString() == "hidden");
        var forged = new TranscriptEntry("system", JsonData.Parse(JsonSerializer.Serialize(new
            { role = "system", content = "", timestamp = 1, toolsAdded = new[] { hidden.Value } })));
        await Throws<SessionRuntimeRegistryException>(() => session.ConfigureAsync(new(SystemMessage: forged)));
        Names(["direct", "outer"], session.GetActiveTools());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "PiSharp-activation-" + Guid.NewGuid().ToString("N"));
        private int sequence;
        public ExtensionRegistry Extensions { get; }
        private readonly IExtensionSessionViewProvider? views;
        private Fixture(IExtensionSessionViewProvider? views = null)
        { this.views = views; Extensions = new(null, null, views); }
        public ExtensionAgentBinding Binding { get; private set; } = null!;
        public SessionRuntimeRegistry Runtime { get; private set; } = null!;
        public Source Source { get; } = new();
        public Policy Policy { get; } = new();
        public List<string> Effects { get; } = [];
        public List<string> LoadoutErrors { get; } = [];
        public Dictionary<string, ExtensionToolCallOutcome> Outcomes { get; } = new(StringComparer.Ordinal);
        public IExtensionToolContext? Retained;
        public Func<long> Clock = () => 1;
        public Func<IExtensionToolContext, CancellationToken, ValueTask<JsonData>>? Outer;
        public Func<IExtensionToolContext, CancellationToken, ValueTask<JsonData>>? Code;
        public static async Task<Fixture> Create(Func<string, ToolLoadout, ToolLoadoutChanges?>? prepare = null,
            IExtensionSessionViewProvider? views = null, Func<string, ToolNamespace?>? namespaceForTool = null)
        {
            var fixture = new Fixture(views); Directory.CreateDirectory(fixture.directory);
            try
            {
                await fixture.Extensions.ActivateAsync("owner", new Extension(host =>
                {
                    foreach (var (name, exposure, active) in new[] {
                        ("direct", ToolExposure.Direct, true), ("outer", ToolExposure.ModelOnly, true),
                        ("inactive", ToolExposure.Direct, false), ("code", ToolExposure.Codemode, true),
                        ("deferred", ToolExposure.Deferred, true), ("denied", ToolExposure.Deferred, true),
                        ("hidden", ToolExposure.Hidden, true) })
                        host.RegisterTool(Tool(name, exposure, async (_, context, token) =>
                        {
                            fixture.Effects.Add(name);
                            if (name == "outer" && fixture.Outer is { } outer) return await outer(context, token);
                            if (name == "code" && fixture.Code is { } code) return await code(context, token);
                            return Result(name);
                        }) with { Namespace = namespaceForTool?.Invoke(name), DefaultActive = active,
                            PrepareLoadout = prepare is null ? null : loadout => prepare(name, loadout) });
                    host.RegisterToolCallHandler(new("final-arguments", (value, _, _) => ValueTask.FromResult<ExtensionToolCallPatch?>(
                        value.ToolName == "code" ? new(JsonData.Parse("{\"final\":true}")) : null)));
                }));
                fixture.Binding = new(fixture.Extensions, fixture.Policy, Validate,
                    options: new() { ReportLoadoutDiagnostic = (name, _) => fixture.LoadoutErrors.Add(name) });
                var tools = fixture.Binding.Registrations.Select((tool, index) => new SessionRegisteredTool(
                    fixture.Binding.RegisteredToolDeclarations[index], fixture.Binding.Adapters[index])
                    { Exposure = tool.Exposure, Namespace = tool.Namespace, DefaultActive = tool.DefaultActive,
                        PrepareLoadout = fixture.Binding.GetLoadoutPreparation(tool.Name) }).ToImmutableArray();
                fixture.Runtime = new([new(Model, fixture.Source, Hooks: fixture.Binding.ContextHooks)], tools, fixture.Policy,
                    new() { PreparedToolHooks = fixture.Binding.PreparedHooks, BindNestedCallsToSessionOwner = true,
                        ReportLoadoutDiagnostic = (name, _) => fixture.LoadoutErrors.Add(name) });
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public string PathFor(string name) => Path.Combine(directory, name + ".jsonl");
        private string Next() => "activation-" + Interlocked.Increment(ref sequence);
        public async Task<PersistentAgentSession> New(string name)
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = name, timestamp = "2026-10-03T00:00:00.000Z", cwd = directory }));
            var session = await PersistentAgentSession.CreateAsync(PathFor(name), header, Runtime, Model, () => Clock(), Next);
            try { await session.ConfigureAsync(new(SystemMessage: Binding.CreateDeclarationMessage("", 1))); return session; }
            catch { await session.DisposeAsync(); throw; }
        }
        public Task<PersistentAgentSession> Open(string path, CancellationToken token) => PersistentAgentSession.OpenWithRegistryAsync(
            path, Runtime, () => Clock(), Next, fallbackModel: Model, cancellationToken: token);
        public ReplaceableAgentSession Owner(PersistentAgentSession initial)
        {
            var owner = new ReplaceableAgentSession(initial, (request, token) => Open(request.Path, token));
            if (views is ActivationProvider provider) provider.Owner = owner;
            return owner;
        }
        public async ValueTask DisposeAsync()
        {
            await Extensions.DisposeAsync();
            var full = Path.GetFullPath(directory);
            Check(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(full).StartsWith("PiSharp-activation-", StringComparison.Ordinal), "Unowned cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private sealed class Policy : IToolActionPolicy
    {
        public bool SawFinalCodeArguments;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (action.ToolName == "code") SawFinalCodeArguments = action.Arguments.Value.GetProperty("final").GetBoolean();
            return ValueTask.FromResult(new ToolActionAuthorization(action.ToolName != "denied"));
        }
    }
    private sealed class Extension(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Source : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = [];
        public string? FirstTool;
        public Func<int, string?>? SelectTool;
        public bool HoldFirstRequest;
        public TaskCompletionSource Entered { get; } = Gate();
        public TaskCompletionSource Release { get; } = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var first = Requests.Count == 0; Requests.Add(request);
            if (first && HoldFirstRequest) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            token.ThrowIfCancellationRequested();
            var selected = SelectTool is { } select ? select(Requests.Count - 1) : first ? FirstTool : null;
            var call = selected is { } name ? new ToolCallContent(SelectTool is null ? "root" : "root-" + Requests.Count, name, JsonData.EmptyObject) : null;
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1,
                call is null ? [new TextContent("done")] : [call], TokenUsage.Zero, call is null ? StopReason.Stop : StopReason.ToolUse);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            if (call is not null) { yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            yield return new StreamDone(message.StopReason, message);
        }
    }
    private static ExtensionToolDescriptor Tool(string name, ToolExposure exposure, ExtensionToolCallback? execute = null) =>
        new(name, name, "activation fixture", JsonData.Parse("{\"type\":\"object\"}"), execute ?? ((_, _, _) => ValueTask.FromResult(Result(name))))
        { Exposure = exposure };
    private static ValueTask<bool> Validate(ExtensionToolRegistrationInfo _, JsonData arguments, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(arguments.Value.ValueKind == JsonValueKind.Object); }
    private static JsonData Result(string text) => ToolResult.Success(text).ToJson();
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"activation\",\"timestamp\":1}"));
    private static ToolInvocation Call(string name)
    { var call = new ToolCallContent("root", name, JsonData.EmptyObject); return new(new(Model.Api, Model.Provider, Model.Id, 1, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0); }
    private static IEnumerable<string> Declared(ChatRequest request) => new SessionSystemReplay().Replay(request.Messages).Tools.Select(tool => tool.Value.GetProperty("name").GetString()!);
    private static async Task<byte[]> ReadShared(string path)
    { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous); using var result = new MemoryStream(); await file.CopyToAsync(result); return result.ToArray(); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Reach(Task boundary, Task original)
    { if (await Task.WhenAny(boundary, original) == original && !boundary.IsCompleted) { await original; throw new InvalidOperationException("Original work settled before boundary."); } await boundary; }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Tool order/set differs.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
