using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class McpHostShutdownTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mcp-host-shutdown. normal close joins separately held cancellation request progress and physical stop", () => Held(false)),
        ("mcp-host-shutdown. owner shutdown joins separately held cancellation request progress and physical stop", () => Held(true)),
        ("mcp-host-shutdown. normal close retains exact callback and stop originals", () => Faults(false, false)),
        ("mcp-host-shutdown. owner shutdown retains exact callback and stop originals", () => Faults(true, false)),
        ("mcp-host-shutdown. synchronous stop initiation failure still joins cancellation and native call", SynchronousStop),
        ("mcp-host-shutdown. late startup acquisition joins actual physical cleanup before either owner original", LateAcquisition),
        ("mcp-host-shutdown. busy admission and idle veto leave actual server stop untouched", BusyAndVeto),
        ("mcp-host-shutdown. successful replacement acknowledges old withdrawal before publishing target", Replacement)
    ];
    private static readonly ModelDescriptor Model = new("host-shutdown", "openai-responses", "synthetic");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("MCP host shutdown ordering assertion failed."); }
    private static TranscriptEntry User() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"invoke\",\"timestamp\":1}"));
    private static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(Leaves) : [error];
    private static async Task<Exception?> Observe(Task original)
    { try { await original; return null; } catch (Exception error) { return error; } }
    private static void ThrowAssertions(Exception? assertion, params Exception?[] unexpected)
    {
        var failures = unexpected.Where(error => error is not null).Cast<Exception>().ToList();
        if (assertion is not null) failures.Insert(0, assertion);
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task Held(bool shutdown)
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync();
        var run = f.Session.PromptAsync(User()); Task? closing = null; Exception? assertion = null;
        try
        {
            await Task.WhenAll(f.Channel.RequestEntered.Task, f.ProgressEntered.Task);
            closing = shutdown ? f.Owner.StopAdmissionAndJoinAsync() : f.Server.CloseAsync();
            await Task.WhenAll(f.Channel.StopEntered.Task, f.Channel.CallbackEntered.Task);
            Check(!run.IsCompleted && !closing.IsCompleted && f.Channel.Closes == 1 && f.Policy.Calls == 1);
            Check(f.Channel.Token.IsCancellationRequested && !f.Channel.CallbackExited.Task.IsCompleted &&
                f.Channel.RequestOriginal is { IsCompleted: false } && f.Channel.ProgressOriginal is { IsCompleted: false } &&
                f.Channel.PhysicalOriginal is { IsCompleted: false });
            Check(ReferenceEquals(closing, shutdown ? f.Owner.StopAdmissionAndJoinAsync() : f.Server.CloseAsync()));
            f.Channel.CallbackRelease.TrySetResult(); await f.Channel.CallbackExited.Task;
            Check(!closing.IsCompleted && !run.IsCompleted);
            f.Channel.RequestRelease.TrySetResult(); await f.Channel.RequestReleased.Task;
            Check(!closing.IsCompleted && !run.IsCompleted && !f.Channel.RequestOriginal!.IsCompleted);
            f.ProgressRelease.TrySetResult(); await f.Channel.RequestFinished.Task;
            Check(!closing.IsCompleted && !f.Channel.PhysicalOriginal!.IsCompleted);
            f.Channel.StopRelease.TrySetResult();
        }
        catch (Exception error) { assertion = error; }
        finally { f.ReleaseAll(); }
        var runFailure = await Observe(run);
        var closeFailure = closing is null ? null : await Observe(closing);
        if (runFailure is OperationCanceledException) runFailure = null;
        ThrowAssertions(assertion, runFailure, closeFailure);
        Check(f.Channel.CallbackExited.Task.IsCompleted && f.Channel.RequestFinished.Task.IsCompleted &&
            f.Channel.PhysicalOriginal!.IsCompleted && f.Channel.ProgressOriginal!.IsCompleted &&
            f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.IsEmpty);
        Check(f.Owner.Current.LifetimeToken.IsCancellationRequested == shutdown);
        Check(f.Channel.Identity is { OwnerId: "host-scope", SessionGeneration: 1, ToolCallId: "host-call" });
    }

    private static async Task SynchronousStop()
    { await Faults(false, true); await Faults(true, true); }
    private static async Task Faults(bool shutdown, bool synchronous)
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync();
        var callback = new IOException("cancellation callback original"); var stop = new IOException("physical stop original");
        f.Channel.CallbackFailure = callback; f.Channel.StopFailure = stop; f.Channel.SynchronousStop = synchronous;
        var run = f.Session.PromptAsync(User()); Task? closing = null; Exception? assertion = null;
        try
        {
            await Task.WhenAll(f.Channel.RequestEntered.Task, f.ProgressEntered.Task);
            closing = shutdown ? f.Owner.StopAdmissionAndJoinAsync() : f.Server.CloseAsync();
            await Task.WhenAll(f.Channel.CallbackEntered.Task, f.Channel.StopEntered.Task);
            Check(!closing.IsCompleted && !run.IsCompleted);
            f.Channel.CallbackRelease.TrySetResult(); await f.Channel.CallbackExited.Task;
            Check(!closing.IsCompleted);
            f.Channel.RequestRelease.TrySetResult(); await f.Channel.RequestReleased.Task;
            Check(!closing.IsCompleted && !run.IsCompleted);
            f.ProgressRelease.TrySetResult(); await f.Channel.RequestFinished.Task;
            if (!synchronous) Check(!closing.IsCompleted);
        }
        catch (Exception error) { assertion = error; }
        finally { f.ReleaseAll(); }
        var runFailure = await Observe(run); var closeFailure = closing is null ? null : await Observe(closing);
        if (closeFailure is not null) f.Expected.AddRange(Leaves(closeFailure));
        if (runFailure is OperationCanceledException) runFailure = null;
        ThrowAssertions(assertion, runFailure);
        Check(closeFailure is not null);
        var leaves = Leaves(closeFailure!).ToArray();
        Check(leaves.Length == 2 && leaves.Count(error => ReferenceEquals(error, callback)) == 1 &&
            leaves.Count(error => ReferenceEquals(error, stop)) == 1 && f.Channel.Closes == 1);
        var repeated = shutdown ? f.Owner.StopAdmissionAndJoinAsync() : f.Server.CloseAsync();
        Check(ReferenceEquals(closing, repeated) && ReferenceEquals(await Observe(repeated), closeFailure));
        Check(f.Channel.RequestFinished.Task.IsCompleted && f.Channel.ProgressOriginal!.IsCompleted && f.Server.CatalogWithdrawalAcknowledged);
    }

    private static async Task LateAcquisition()
    {
        await using var f = await Fixture.Create(holdAcquisition: true);
        var connecting = f.Server.ConnectAsync(); await f.AcquireEntered.Task;
        var closing = f.Server.CloseAsync(); Exception? assertion = null;
        try
        {
            Check(!closing.IsCompleted && !connecting.IsCompleted && f.Channel.Closes == 0);
            f.AcquireRelease.TrySetResult(); await f.Channel.StopEntered.Task;
            Check(!closing.IsCompleted && !connecting.IsCompleted && f.Channel.Closes == 1 && f.Channel.Requests == 0);
            f.Channel.StopRelease.TrySetResult();
        }
        catch (Exception error) { assertion = error; }
        finally { f.ReleaseAll(); }
        var connectionFailure = await Observe(connecting); var closeFailure = await Observe(closing);
        if (connectionFailure is OperationCanceledException) connectionFailure = null;
        ThrowAssertions(assertion, connectionFailure, closeFailure);
        Check(f.Channel.PhysicalOriginal!.IsCompleted && f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.IsEmpty);
    }

    private static async Task BusyAndVeto()
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync(); await f.SeedTarget();
        var attachment = f.Owner.Current; var run = f.Session.PromptAsync(User()); Exception? assertion = null;
        try
        {
            await Task.WhenAll(f.Channel.RequestEntered.Task, f.ProgressEntered.Task);
            var rejected = await Observe(f.Owner.SwitchAsync(attachment, new(f.TargetPath)));
            Check(rejected is InvalidOperationException && f.Channel.Closes == 0 && ReferenceEquals(f.Owner.Current, attachment));
        }
        catch (Exception error) { assertion = error; }
        finally { f.Channel.RequestRelease.TrySetResult(); f.ProgressRelease.TrySetResult(); }
        var runFailure = await Observe(run); ThrowAssertions(assertion, runFailure);
        var vetoed = await f.Owner.SwitchAsync(attachment, new(f.TargetPath), beforeSwitch: (oldAttachment, target, token) => ValueTask.FromResult(false));
        Check(vetoed is null && f.Channel.Closes == 0 && !attachment.LifetimeToken.IsCancellationRequested &&
            ReferenceEquals(f.Owner.Current, attachment) && !f.Server.CatalogWithdrawalAcknowledged);
    }
    private static async Task Replacement()
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync(); await f.SeedTarget();
        var old = f.Owner.Current; var notified = false;
        f.Owner.AttachmentChanged = replacement =>
        {
            Check(f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.IsEmpty &&
                old.Session.Snapshot.Context.LlmMessages.Any(message => message.WireBody.Value.TryGetProperty("toolsRemoved", out var removed) &&
                    removed.EnumerateArray().Any(row => row.GetProperty("name").GetString() == "mcp__demo__a")));
            notified = true; return ValueTask.CompletedTask;
        };
        var switching = f.Owner.SwitchAsync(old, new(f.TargetPath)); Exception? assertion = null;
        try
        {
            await f.Channel.StopEntered.Task;
            Check(!switching.IsCompleted && ReferenceEquals(f.Owner.Current, old) && !notified);
            f.Channel.StopRelease.TrySetResult();
        }
        catch (Exception error) { assertion = error; }
        finally { f.ReleaseAll(); }
        var switchedFailure = await Observe(switching); ThrowAssertions(assertion, switchedFailure);
        Check(notified && f.Owner.Current.Generation == 2 && f.Channel.Closes == 1 && old.Session.Snapshot.IsRetired);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly SessionStorageBackend Backend = new(Path.Combine(Path.GetTempPath(), "mcp-host-" + Guid.NewGuid().ToString("N")), SessionStorageMode.InMemory);
        internal readonly Channel Channel = new(); internal readonly Policy Policy = new(); internal readonly ExtensionRegistry Extensions = new();
        internal readonly TaskCompletionSource ProgressEntered = Gate(), ProgressRelease = Gate(), AcquireEntered = Gate(), AcquireRelease = Gate();
        internal readonly List<Exception> Expected = [];
        internal PersistentAgentSession Session = null!; internal ReplaceableAgentSession Owner = null!; internal McpPreparedServer Server = null!;
        private SessionRuntimeRegistry registry = null!; private int ids; private IDisposable? subscription;
        internal string TargetPath => Path.Combine(Backend.Directory, "target.jsonl");
        internal SessionEntry Header(string id) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id, timestamp = "2026-10-05T00:00:00.000Z", cwd = Backend.Directory }));
        internal static async Task<Fixture> Create(bool holdAcquisition = false)
        {
            var f = new Fixture();
            try
            {
                f.registry = new([new(Model, new Transport())], [], f.Policy, new() { BindNestedCallsToSessionOwner = true });
                var lifecycle = new PersistentSessionLifecycle(f.registry, () => 1, () => "host-" + ++f.ids, backend: f.Backend,
                    options: new(AgentOptions: new(ProgressDelivery: new(Mode: ToolProgressDeliveryMode.NativeAwaited))));
                f.Session = await lifecycle.CreateAsync(Path.Combine(f.Backend.Directory, "initial.jsonl"), f.Header("initial"), Model);
                f.Owner = lifecycle.Attach(f.Session);
                f.subscription = f.Session.Subscribe(new Sink(async observation =>
                { if (observation is ToolExecutionUpdated) { f.ProgressEntered.TrySetResult(); await f.ProgressRelease.Task; } }));
                var scope = await f.Extensions.ActivateAsync("host-scope", new EmptyExtension());
                var entry = new McpServerEntry("demo", McpConfigurationReader.Validate("demo",
                    JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"direct\"}").Value).Config!, "fixture", McpConfigurationScope.Extension);
                f.Server = new(entry, f.Extensions, scope, f.Owner, f.Policy,
                    (tool, arguments, token) => ValueTask.FromResult(arguments.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String),
                    async (configured, token) => { f.AcquireEntered.TrySetResult(); if (holdAcquisition) await f.AcquireRelease.Task; return f.Channel; },
                    new(1, "0.99.1"), (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task SeedTarget()
        { await using var store = await SessionLogStore.CreateNewAsync(TargetPath, Header("target"), new(StorageFactory: Backend)); }
        internal void ReleaseAll()
        { AcquireRelease.TrySetResult(); ProgressRelease.TrySetResult(); Channel.CallbackRelease.TrySetResult(); Channel.RequestRelease.TrySetResult(); Channel.StopRelease.TrySetResult(); }
        public async ValueTask DisposeAsync()
        {
            ReleaseAll(); var unexpected = new List<Exception>();
            async Task Join(Func<Task> original)
            {
                try { await original(); }
                catch (Exception error) { if (!Leaves(error).All(leaf => Expected.Any(expected => ReferenceEquals(expected, leaf)))) unexpected.Add(error); }
            }
            if (Server is not null) await Join(Server.CloseAsync);
            if (Owner is not null) await Join(() => Owner.DisposeAsync().AsTask());
            else if (Session is not null) await Join(() => Session.DisposeAsync().AsTask());
            subscription?.Dispose(); await Join(() => Extensions.DisposeAsync().AsTask());
            if (unexpected.Count != 0) throw new AggregateException(unexpected);
        }
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal readonly TaskCompletionSource RequestEntered = Gate(), RequestRelease = Gate(), RequestReleased = Gate(), RequestFinished = Gate();
        internal readonly TaskCompletionSource CallbackEntered = Gate(), CallbackRelease = Gate(), CallbackExited = Gate(), StopEntered = Gate(), StopRelease = Gate();
        internal Task<JsonData>? RequestOriginal; internal Task? ProgressOriginal, PhysicalOriginal;
        internal CancellationToken Token; internal McpInvocationIdentity? Identity;
        internal Exception? CallbackFailure, StopFailure; internal bool SynchronousStop; internal int Calls, Closes, Requests;
        private readonly Lazy<Task> close;
        internal Channel() { close = new(BeginClose, LazyThreadSafetyMode.ExecutionAndPublication); }
        public ValueTask StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            Requests++;
            if (method == "initialize") return ValueTask.FromResult(JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"));
            if (method == "tools/list") return ValueTask.FromResult(JsonData.Parse("{\"tools\":[{\"name\":\"a\",\"description\":\"actual discovery\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}}]}"));
            Check(method == "tools/call" && options.OnProgress is not null); Calls++; Token = token; Identity = options.InvocationIdentity;
            RequestOriginal = RequestCore(options, token); return new(RequestOriginal);
        }
        private async Task<JsonData> RequestCore(McpRequestOptions options, CancellationToken token)
        {
            using var registration = token.Register(() =>
            {
                CallbackEntered.TrySetResult();
                try
                {
                    StopEntered.Task.GetAwaiter().GetResult(); CallbackRelease.Task.GetAwaiter().GetResult();
                    if (CallbackFailure is { } failure) throw failure;
                }
                finally { CallbackExited.TrySetResult(); }
            });
            ProgressOriginal = options.OnProgress!(new(1, 2, "held native MCP progress"), default).AsTask();
            RequestEntered.TrySetResult();
            try
            {
                await RequestRelease.Task; RequestReleased.TrySetResult(); await ProgressOriginal;
                return JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"actual channel result\"}]}");
            }
            finally { RequestFinished.TrySetResult(); }
        }
        public Task CloseAsync() => close.Value;
        private Task BeginClose()
        {
            Closes++;
            if (SynchronousStop)
            { StopEntered.TrySetResult(); throw StopFailure ?? new IOException("Synchronous stop not configured."); }
            PhysicalOriginal = CloseCore(); StopEntered.TrySetResult(); return PhysicalOriginal;
        }
        private async Task CloseCore()
        {
            await StopRelease.Task;
            if (RequestOriginal is { } original) { try { await original; } catch (OperationCanceledException) { } }
            if (StopFailure is { } failure) throw failure;
        }
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal int Calls;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Sink(Func<AgentEvent, Task> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => new(emit(observation)); }
    private sealed class Transport : IChatTransport
    {
        private int requests;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var tool = Interlocked.Increment(ref requests) == 1;
            AssistantContent content = tool ? new ToolCallContent("host-call", "mcp__demo__a", JsonData.Parse("{\"value\":\"actual\"}")) : new TextContent("done");
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [content], TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (content is ToolCallContent call) { yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            await Task.CompletedTask; yield return new StreamDone(final.StopReason, final);
        }
    }
}
