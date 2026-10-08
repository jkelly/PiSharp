using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
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
using PiSharp.Sessions.Storage;

internal static class McpPreOpenCaptureTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("MCP pre-open held actual discovery precedes registry transfer", HeldDiscovery),
        ("MCP pre-open historical schema is restored by name with the actual discovery schema", RestoredHistoricalSchema),
        ("MCP pre-open calls require exact actual attachment and native final policy", Binding),
        ("MCP pre-open rejects substituted adapter and unreserved attachment generation", RejectedBinding),
        ("MCP pre-open transferred runtime release joins owning shutdown without close reentry", OwningRuntimeRelease),
        ("MCP pre-open owning close stops held call then joins cleanup and withdraws", HeldCallClose),
        ("MCP pre-open bound close reports each stop and withdrawal original once", BoundFaults),
        ("MCP pre-open owning shutdown reports each stop and withdrawal original once", ShutdownFaults),
        ("MCP pre-open partial binding runtime release retains each owned cleanup fault once", PartialBindingFaults),
        ("MCP pre-open public close after automatic retirement retains physical stop original", RetiredStopFailure),
        ("MCP pre-open repeated close after automatic retirement retains complete stop and withdrawal originals", RetiredStopAndWithdrawalFailures)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("MCP pre-open capture invariant failed."); }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original failure."); }
    private static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(Leaves) : [error];
    private static ReplaceableAgentSession.OwnedResourceStopException PhysicalStopWitness(
        Exception failure, Exception stop, Exception? withdrawal)
    {
        // The resource receipt is either the known stop wrapper or [stop wrapper, body failure].
        // Do not normalize arbitrary exception trees into an apparently matching outcome.
        Exception stopFailure = failure;
        if (withdrawal is not null)
        {
            Check(failure.GetType() == typeof(AggregateException));
            var composite = (AggregateException)failure;
            Check(composite.InnerExceptions.Count == 2 && ReferenceEquals(composite.InnerExceptions[1], withdrawal));
            stopFailure = composite.InnerExceptions[0];
        }
        Check(stopFailure is ReplaceableAgentSession.OwnedResourceStopException);
        var witness = (ReplaceableAgentSession.OwnedResourceStopException)stopFailure;
        Check(ReferenceEquals(witness.ObservedException, stop) && witness.OriginalTask is { IsFaulted: true, IsCanceled: false });
        // BindOwner registers runtime.CloseAsync: this task is the runtime close original,
        // not the nested fake channel close task. Its complete inventory still contains stop.
        var actualFaults = witness.OriginalTask!.Exception!;
        Check(actualFaults.InnerExceptions.Count == 1 && ReferenceEquals(actualFaults.InnerExceptions[0], stop));
        Check(witness.OriginalTaskException is { InnerExceptions.Count: 1 } recorded && ReferenceEquals(recorded.InnerExceptions[0], stop));
        Check(witness.InnerExceptions.Count == 1 && ReferenceEquals(witness.InnerExceptions[0], stop));
        return witness;
    }
    private static Task BoundFaults() => Faults(shutdown: false);
    private static Task ShutdownFaults() => Faults(shutdown: true);
    private static Task RetiredStopFailure() => RetiredPublicClose(failWithdrawal: false);
    private static Task RetiredStopAndWithdrawalFailures() => RetiredPublicClose(failWithdrawal: true);
    private static async Task RetiredPublicClose(bool failWithdrawal)
    {
        var f = await Fixture.Create(); var capture = await f.Acquire(); var session = await f.Open(capture);
        var owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement admission.")));
        capture.BindOwner(owner, owner.Current);
        var stop = new IOException("automatic retirement physical stop original");
        var withdrawal = new IOException("automatic retirement withdrawal original");
        f.Channel.CloseFailure = stop; f.Channel.HoldClose = true;
        if (failWithdrawal) f.ComposeFailure = withdrawal;
        var shutdown = owner.DisposeAsync().AsTask();
        Exception? assertion = null;
        try { await f.Channel.CloseEntered.Task; Check(!shutdown.IsCompleted && f.Channel.Closes == 1); }
        catch (Exception error) { assertion = error; }
        finally { f.Channel.CloseRelease.TrySetResult(); }
        try
        {
            var ownerFailure = await Failure(shutdown);
            if (assertion is not null) throw assertion;
            var ownerLeaves = Leaves(ownerFailure).ToArray();
            Check(ownerLeaves.Count(error => ReferenceEquals(error, stop)) == 1 &&
                ownerLeaves.Count(error => ReferenceEquals(error, withdrawal)) == (failWithdrawal ? 1 : 0));
            // Owning shutdown retires this resource and disposes the session. Only an actual
            // replacement reservation's RetireWriterAsync marks the session itself retired.
            Check(session.Snapshot.IsDisposed);
            Check(!session.Snapshot.IsRetired);
            Check(ReferenceEquals(owner.Current.Session, session) && owner.Current.LifetimeToken.IsCancellationRequested);
            Check(f.Extensions.CaptureSnapshot().Tools.IsEmpty == !failWithdrawal);
            // First public close occurs only after automatic retirement and transferred runtime cleanup.
            var original = capture.CloseAsync();
            Check(ReferenceEquals(original, capture.CloseAsync()));
            var failure = await Failure(original); var repeated = await Failure(capture.CloseAsync());
            var leaves = Leaves(failure).ToArray();
            Check(ReferenceEquals(failure, repeated) && f.Channel.Closes == 1 &&
                leaves.Count(error => ReferenceEquals(error, stop)) == 1 &&
                leaves.Count(error => ReferenceEquals(error, withdrawal)) == (failWithdrawal ? 1 : 0));
            var witness = PhysicalStopWitness(failure, stop, failWithdrawal ? withdrawal : null);
            var repeatedWitness = PhysicalStopWitness(repeated, stop, failWithdrawal ? withdrawal : null);
            Check(ReferenceEquals(witness, repeatedWitness) && ReferenceEquals(witness.OriginalTask, repeatedWitness.OriginalTask) &&
                ReferenceEquals(witness.OriginalTaskException, repeatedWitness.OriginalTaskException));
        }
        finally
        {
            try { await owner.DisposeAsync(); } catch (Exception) { /* Original owning settlement was inspected above. */ }
            await f.Extensions.DisposeAsync();
        }
    }
    private static async Task Faults(bool shutdown)
    {
        foreach (var failStop in new[] { false, true })
        foreach (var failWithdrawal in new[] { false, true })
        {
            if (!failStop && !failWithdrawal) continue;
            var f = await Fixture.Create(); var capture = await f.Acquire(); var session = await f.Open(capture);
            var owner = new ReplaceableAgentSession(session, (request, token) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement admission.")));
            capture.BindOwner(owner, owner.Current);
            var stop = new IOException("physical close original"); var withdrawal = new IOException("withdrawal original");
            f.Channel.CloseFailure = failStop ? stop : null; f.Channel.HoldClose = true;
            if (failWithdrawal) f.ComposeFailure = withdrawal;
            var work = shutdown ? owner.DisposeAsync().AsTask() : capture.CloseAsync();
            Exception? assertion = null;
            try { await f.Channel.CloseEntered.Task; Check(!work.IsCompleted && f.Channel.Closes == 1); }
            catch (Exception error) { assertion = error; }
            finally { f.Channel.CloseRelease.TrySetResult(); }
            try
            {
                var failure = await Failure(work); var leaves = Leaves(failure).ToArray();
                if (assertion is not null) throw assertion;
                Check(leaves.Count(error => ReferenceEquals(error, stop)) == (failStop ? 1 : 0) &&
                    leaves.Count(error => ReferenceEquals(error, withdrawal)) == (failWithdrawal ? 1 : 0));
                if (failStop)
                {
                    var publicClose = capture.CloseAsync();
                    if (!shutdown) Check(ReferenceEquals(work, publicClose));
                    var publicFailure = await Failure(publicClose);
                    var witness = PhysicalStopWitness(publicFailure, stop, failWithdrawal ? withdrawal : null);
                    Check(ReferenceEquals(publicClose, capture.CloseAsync()) && ReferenceEquals(publicFailure, await Failure(capture.CloseAsync())));
                    var repeatedWitness = PhysicalStopWitness(await Failure(capture.CloseAsync()), stop, failWithdrawal ? withdrawal : null);
                    Check(ReferenceEquals(witness, repeatedWitness) && ReferenceEquals(witness.OriginalTask, repeatedWitness.OriginalTask));
                }
                Check(f.Extensions.CaptureSnapshot().Tools.IsEmpty == !failWithdrawal);
            }
            finally
            {
                try { await owner.DisposeAsync(); } catch (Exception) { /* Original expected settlement was inspected above. */ }
                await f.Extensions.DisposeAsync();
            }
        }
    }
    private static async Task PartialBindingFaults()
    {
        foreach (var failStop in new[] { false, true })
        {
            var f = await Fixture.Create(); var capture = await f.Acquire();
            var binding = new IOException("actual owner binding original");
            var stop = new IOException("physical close original"); var withdrawal = new IOException("withdrawal original");
            var session = await f.Open(capture, bindOwner: (bindingOwner, bindingAttachment) =>
            {
                capture.BindOwner(bindingOwner, bindingAttachment); f.ComposeFailure = withdrawal;
                f.Channel.CloseFailure = failStop ? stop : null; f.Channel.HoldClose = true;
                throw binding;
            });
            var lifecycle = new PersistentSessionLifecycle(capture.Registry, () => 1000, () => "unused-binding-entry");
            var work = lifecycle.AttachAsync(session);
            Exception? assertion = null;
            try { await f.Channel.CloseEntered.Task; Check(!work.IsCompleted && f.Channel.Closes == 1); }
            catch (Exception error) { assertion = error; }
            finally { f.Channel.CloseRelease.TrySetResult(); }
            try
            {
                var failed = await Failure(work); var leaves = Leaves(failed).ToArray();
                if (assertion is not null) throw assertion;
                Check(leaves.Count(error => ReferenceEquals(error, binding)) == 1 &&
                    leaves.Count(error => ReferenceEquals(error, stop)) == (failStop ? 1 : 0) &&
                    leaves.Count(error => ReferenceEquals(error, withdrawal)) == 1 && session.Snapshot.IsRetired);
            }
            finally
            {
                try { await session.DisposeAsync(); } catch (Exception) { /* Actual initial attach already retained expected cleanup faults. */ }
                await f.Extensions.DisposeAsync();
            }
        }
    }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Unexpected admission."); }
    private static async Task Join(Exception? primary, params Func<Task>[] operations)
    {
        var errors = new List<Exception>(); if (primary is not null) errors.Add(primary);
        foreach (var operation in operations)
            try { await operation(); } catch (Exception error) { if (!errors.Any(value => ReferenceEquals(value, error))) errors.Add(error); }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }
    private static async Task HeldDiscovery()
    {
        var f = await Fixture.Create(); f.Channel.HoldList = true;
        var acquire = f.Acquire(); McpPreOpenServerCapture? capture = null; Exception? primary = null;
        try
        {
            await f.Channel.ListEntered.Task;
            Check(!acquire.IsCompleted && f.Channel.Acquires == 1 && f.Channel.Calls == 0);
            f.Channel.ListRelease.TrySetResult(); capture = await acquire;
            Check(capture.CatalogSnapshot.Tools.Single().InputSchema.ToString() == Channel.Schema.ToString() &&
                capture.Registry.RegisteredTools.Single().Declaration.Value.GetProperty("parameters").GetProperty("required")[0].GetString() == "value");
            _ = capture.TransferRuntimeOwnership(); Reject(() => capture.TransferRuntimeOwnership());
            var close = capture.CloseAsync(); Check(ReferenceEquals(close, capture.CloseAsync())); await close;
            Check(f.Channel.Closes == 1 && f.Extensions.CaptureSnapshot().Tools.IsEmpty);
            _ = await Failure(f.Acquire()); Check(f.Channel.Acquires == 1);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            f.Channel.ListRelease.TrySetResult(); f.Channel.CloseRelease.TrySetResult();
            await Join(primary, async () => { capture ??= await acquire; }, () => capture?.CloseAsync() ?? Task.CompletedTask, () => f.Extensions.DisposeAsync().AsTask());
        }
    }
    // Pi 1.1.0 _restoreToolsFromTranscript restores by name with the current binding: the historical schema is replaced by the
    // actual discovery schema (recorded before use) instead of failing the open.
    private static async Task RestoredHistoricalSchema()
    {
        var f = await Fixture.Create(); var capture = await f.Acquire(); var session = await f.Open(capture, wrongSchema: true);
        var owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement acquisition granted.")));
        Exception? primary = null;
        try
        {
            Check(session.GetActiveTools().SequenceEqual(["mcp__demo__a"]) && f.Channel.Calls == 0 && f.Channel.Closes == 0);
            var recorded = session.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("message").GetProperty("toolsAdded")[0];
            Check(recorded.GetProperty("parameters").GetProperty("properties").GetProperty("value").GetProperty("type").GetString() == "string");        }
        catch (Exception error) { primary = error; }
        finally { await Join(primary, capture.CloseAsync, () => owner.DisposeAsync().AsTask(), () => f.Extensions.DisposeAsync().AsTask()); }
    }
    private static async Task Binding()
    {
        var f = await Fixture.Create(); var capture = await f.Acquire(); var session = await f.Open(capture);
        var owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement acquisition granted.")));
        var executor = session.CaptureToolCatalogRegistry().Resolve(session.Snapshot.Context, Fixture.Model).Configuration.Tools.Single().Executor;
        Exception? primary = null;
        try
        {
            var before = await executor.ExecuteAsync(Call(), default); Check(before.IsError && f.Channel.Calls == 0);
            capture.BindOwner(owner, owner.Current); Reject(() => capture.BindOwner(owner, owner.Current));
            var result = await executor.ExecuteAsync(Call(), default);
            Check(!result.IsError && f.Channel.Calls == 1 && f.Policy.Calls == 2 &&
                f.Channel.Identity is { OwnerId: "pre-open-scope", SessionGeneration: 1, ToolCallId: "captured-call" });
            f.Policy.Allow = false; Check((await executor.ExecuteAsync(Call(), default)).IsError && f.Channel.Calls == 1);
            var close = capture.CloseAsync(); Check(ReferenceEquals(close, capture.CloseAsync())); await close;
            Check(session.CaptureToolCatalogRegistry().RegisteredTools.IsEmpty && f.Extensions.CaptureSnapshot().Tools.IsEmpty);
            Check((await executor.ExecuteAsync(Call(), default)).IsError && f.Channel.Calls == 1);
        }
        catch (Exception error) { primary = error; }
        finally { await Join(primary, capture.CloseAsync, () => owner.DisposeAsync().AsTask(), () => f.Extensions.DisposeAsync().AsTask()); }
    }
    private static async Task HeldCallClose()
    {
        var f = await Fixture.Create(); var capture = await f.Acquire(); var session = await f.Open(capture);
        var owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement acquisition granted.")));
        capture.BindOwner(owner, owner.Current); f.Channel.HoldCall = true; f.Channel.HoldClose = true;
        var executor = session.CaptureToolCatalogRegistry().Resolve(session.Snapshot.Context, Fixture.Model).Configuration.Tools.Single().Executor;
        var call = executor.ExecuteAsync(Call(), default).AsTask(); Task? close = null; Exception? primary = null;
        try
        {
            await f.Channel.CallEntered.Task; close = capture.CloseAsync(); await f.Channel.CloseEntered.Task;
            Check(f.Channel.CallToken.IsCancellationRequested && !call.IsCompleted && !close.IsCompleted && f.Channel.Closes == 1);
            f.Channel.CallRelease.TrySetResult(); await call; Check(!close.IsCompleted);
            f.Channel.CloseRelease.TrySetResult(); await close;
            Check(session.CaptureToolCatalogRegistry().RegisteredTools.IsEmpty && ReferenceEquals(close, capture.CloseAsync()));
        }
        catch (Exception error) { primary = error; }
        finally
        {
            f.Channel.CallRelease.TrySetResult(); f.Channel.CloseRelease.TrySetResult();
            await Join(primary, async () => { await call; }, () => close ?? capture.CloseAsync(), () => owner.DisposeAsync().AsTask(), () => f.Extensions.DisposeAsync().AsTask());
        }
    }
    private static async Task RejectedBinding()
    {
        foreach (var substitute in new[] { false, true })
        {
            var f = await Fixture.Create(); var capture = await f.Acquire(substitute ? 1 : 2);
            var registered = capture.Registry.RegisteredTools.Single();
            var overrideRegistry = substitute ? capture.Registry.WithToolCatalog([registered with { Adapter = new SubstitutedAdapter(registered.Adapter.Name) }],
                capture.Registry.PreparedToolHooks) : capture.Registry;
            var session = await f.Open(capture, overrideRegistry: overrideRegistry);
            var owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement acquisition granted.")));
            Exception? primary = null;
            try { Reject(() => capture.BindOwner(owner, owner.Current)); Check(f.Channel.Calls == 0); }
            catch (Exception error) { primary = error; }
            finally { await Join(primary, capture.CloseAsync, () => owner.DisposeAsync().AsTask(), () => f.Extensions.DisposeAsync().AsTask()); }
        }
    }
    private static async Task OwningRuntimeRelease()
    {
        var f = await Fixture.Create(); var capture = await f.Acquire(); var session = await f.Open(capture);
        var owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement acquisition granted.")));
        capture.BindOwner(owner, owner.Current); f.Channel.HoldClose = true;
        var shutdown = owner.DisposeAsync().AsTask(); Exception? primary = null;
        try
        {
            await f.Channel.CloseEntered.Task; Check(!shutdown.IsCompleted && f.Channel.Closes == 1);
            f.Channel.CloseRelease.TrySetResult(); await shutdown;
            Check(f.Extensions.CaptureSnapshot().Tools.IsEmpty && f.Channel.Closes == 1);
            var original = capture.CloseAsync(); Check(ReferenceEquals(original, capture.CloseAsync())); await original;
        }
        catch (Exception error) { primary = error; }
        finally { f.Channel.CloseRelease.TrySetResult(); await Join(primary, () => shutdown, capture.CloseAsync, () => f.Extensions.DisposeAsync().AsTask()); }
    }
    private sealed class SubstitutedAdapter(string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromException<PreparedToolAction>(new InvalidOperationException("Fabricated adapter must remain unreachable."));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromException<ToolResult>(new InvalidOperationException("Fabricated adapter must remain unreachable."));
    }
    private static ToolInvocation Call()
    {
        var call = new ToolCallContent("captured-call", "mcp__demo__a", JsonData.Parse("{\"value\":\"actual\"}"));
        var assistant = new AssistantMessage(Fixture.Model.Api, Fixture.Model.Provider, Fixture.Model.Id, 1000, [call], TokenUsage.Zero, StopReason.ToolUse);
        return new(assistant, call, 0);
    }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal bool Allow = true; internal int Calls;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new ToolActionAuthorization(Allow)); }
    }
    private sealed class Fixture
    {
        internal static readonly ModelDescriptor Model = new("pre-open-authored", "openai-responses", "authored");
        internal readonly ExtensionRegistry Extensions = new(); internal readonly Policy Policy = new(); internal readonly Channel Channel = new();
        internal RegistrationScope Scope = null!; internal SessionRuntimeRegistry Base = null!;
        internal Exception? ComposeFailure;
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); f.Scope = await f.Extensions.ActivateAsync("pre-open-scope", new EmptyExtension());
            f.Base = new([new(Model, new Transport())], [], f.Policy, new() { BindNestedCallsToSessionOwner = true }); return f;
        }
        internal Task<McpPreOpenServerCapture> Acquire(long generation = 1) => McpPreOpenServerCapture.AcquireAsync(
            new("demo", McpConfigurationReader.Validate("demo", JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"direct\"}").Value).Config!, "authored", McpConfigurationScope.Extension),
            Extensions, Scope, Base, Policy, (_, arguments, _) => ValueTask.FromResult(arguments.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String),
            (_, _) => { Channel.Acquires++; return ValueTask.FromResult<IMcpAdmittedRequestChannel>(Channel); }, new(generation, "0.99.1"),
            (current, binding) => ComposeFailure is { } failure ? throw failure : binding.PreparedHooks ?? current.PreparedToolHooks);
        internal Task<PersistentAgentSession> Open(McpPreOpenServerCapture capture, bool wrongSchema = false, SessionRuntimeRegistry? overrideRegistry = null,
            Action<ReplaceableAgentSession, AgentSessionAttachment>? bindOwner = null)
        {
            var cwd = Path.GetFullPath(Path.GetTempPath());
            var declaration = capture.Registry.RegisteredTools.Single().Declaration.Value;
            if (wrongSchema) declaration = JsonSerializer.SerializeToElement(new { name = "mcp__demo__a", description = "actual discovery", parameters = new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } } });
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "session", version = 3, id = "pre-open", timestamp = "2026-10-05T00:00:00.000Z", cwd }) + "\n" +
                JsonSerializer.Serialize(new { type = "message", id = "historical", parentId = (string?)null, timestamp = "2026-10-05T00:00:00.001Z",
                    message = new { role = "system", content = "historical declarations", toolsAdded = new[] { declaration }, timestamp = 1 } }) + "\n");
            var storage = new MemoryStorage(bytes); var identity = 0;
            return PersistentAgentSession.OpenWithRuntimeFactoryAsync(Path.Combine(cwd, "authored-pre-open-no-disk.jsonl"),
                (_, _) => ValueTask.FromResult(new SessionRuntimeLease(overrideRegistry ?? capture.Registry, capture.TransferRuntimeOwnership(), bindOwner)),
                () => 1000, () => "admitted-" + ++identity, new(UseLatestLeaf: true, SessionLogStoreOptions: new(StorageFactory: new MemoryFactory(storage))), Model);
        }
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal static readonly JsonData Schema = JsonData.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}");
        internal bool HoldList, HoldCall, HoldClose; internal int Acquires, Calls, Closes; internal McpInvocationIdentity? Identity; internal CancellationToken CallToken;
        internal readonly TaskCompletionSource ListEntered = Gate(), ListRelease = Gate(), CallEntered = Gate(), CallRelease = Gate(), CloseEntered = Gate(), CloseRelease = Gate();
        private readonly Lazy<Task> close;
        internal Exception? CloseFailure;
        internal Channel() { close = new(CloseCore, LazyThreadSafetyMode.ExecutionAndPublication); }
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize") return JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"authored\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}");
            if (method == "tools/list")
            {
                ListEntered.TrySetResult(); if (HoldList) await ListRelease.Task;
                return JsonData.Parse(JsonSerializer.Serialize(new { tools = new[] { new { name = "a", description = "actual discovery", inputSchema = Schema.Value } } }));
            }
            Check(method == "tools/call"); Calls++; Identity = options.InvocationIdentity; CallToken = token;
            CallEntered.TrySetResult(); if (HoldCall) await CallRelease.Task;
            return JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"actual admitted result\"}]}");
        }
        public Task CloseAsync() => close.Value;
        private async Task CloseCore() { Closes++; CloseEntered.TrySetResult(); if (HoldClose) await CloseRelease.Task; if (CloseFailure is { } failure) throw failure; }
    }
    private sealed class MemoryFactory(MemoryStorage storage) : ISessionLogStorageFactory
    { public ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => ValueTask.FromResult<ISessionLogStorage>(storage); }
    private sealed class MemoryStorage : ISessionLogStorage
    {
        private readonly MemoryStream bytes = new();
        internal MemoryStorage(byte[] initial) { bytes.Write(initial); bytes.Position = 0; }
        public Stream ReadStream => bytes; public long Length => bytes.Length;
        public SessionLogStorageDurability Durability => SessionLogStorageDurability.VolatileMemory;
        public void PositionForAppend(long length) { Check(bytes.Length == length); bytes.Position = length; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> value) => bytes.WriteAsync(value);
        public ValueTask FlushAsync() => ValueTask.CompletedTask; public void FlushToDisk() { }
        public ValueTask BeforeCheckpointAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => bytes.DisposeAsync();
    }
    private sealed class Transport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; if (request is not null) throw new InvalidOperationException("No provider operation admitted by this fixture."); yield break; }
    }
}
