using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Tree;
using PiSharp.Sessions.Context;

// Authored controls; deliberately not added to the shared runner by this source lane.
internal static class ContextSessionFacadeTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("context-facade.live-and-stale", LiveAndStale);
        yield return ("context-facade.held-ignored-original", HeldIgnoredOriginal);
        yield return ("context-facade.native-request-and-fresh-context", NativeRequestAndFreshContext);
        yield return ("context-facade.original-fault-graph", OriginalFaultGraph);
        yield return ("context-facade.unsupported-authority", UnsupportedAuthority);
        yield return ("context-facade.canceled-session", CanceledSession);
        yield return ("context-facade.registry-close-joins-callback", RegistryCloseJoinsCallback);
        yield return ("context-facade.main-and-owned-failure", MainAndOwnedFailure);
        yield return ("context-facade.callback-original-full-shared-graph", CallbackOriginalGraph);
        yield return ("context-facade.callback-faulted-oce-and-canceled-status", CallbackFaultedOceAndCanceled);
        yield return ("context-facade.action-faulted-oce-and-canceled-status", ActionFaultedOceAndCanceled);
        yield return ("context-facade.multiple-canceled-original-inventory", MultipleCanceledOriginalInventory);
        yield return ("context-facade.admitted-host-full-engine-forest", AdmittedHostFullEngineForest);
        yield return ("context-facade.host-action-ignored-original-join", HostActionIgnoredOriginalJoin);
        yield return ("context-facade.no-admitted-host-refuses-before-callback", NoAdmittedHost);
        yield return ("context-facade.read-host-exact-frame-and-no-action-escape", ReadHostExactFrame);
        yield return ("context-facade.admitted-host-action-owner-close-drains", AdmittedActionOwnerClose);
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Facade control failed."); }
    private static JsonData Arguments => JsonData.Parse("{}");
    private static async Task LiveAndStale()
    {
        var host = new Host();
        var native = new Native();
        IExtensionCommandFacade? retained = null;
        var command = ExtensionCommandFacade.CreateCommand("read", "read", "read", host, (_, view, _) =>
        {
            retained = view;
            Require(view.IsIdle && view.SessionId == "source" && view.LeafId == "leaf");
            host.Idle = false;
            Require(!view.IsIdle && view.GetBranch().Length == 1);
            return ValueTask.CompletedTask;
        });
        await command.ExecuteAsync(Arguments, native, default);
        try { _ = retained!.Cwd; throw new Exception("Retained facade remained active."); }
        catch (InvalidOperationException) { }
    }
    private static async Task HeldIgnoredOriginal()
    {
        var original = new TaskCompletionSource<ExtensionSessionCreationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new Native { Create = _ => new(original.Task) };
        var command = ExtensionCommandFacade.CreateCommand("hold", "hold", "hold", new Host(), (_, view, _) =>
        { _ = view.NewSessionAsync(); return ValueTask.CompletedTask; });
        var admittedOriginal = command.ExecuteAsync(Arguments, native, default).AsTask();
        try { Require(!admittedOriginal.IsCompleted && native.Calls == 1); }
        finally { original.TrySetResult(null); await admittedOriginal; }
        Require(original.Task.IsCompletedSuccessfully);
    }
    private static async Task NativeRequestAndFreshContext()
    {
        var fresh = new Native { SessionSnapshot = new("target", 2, null, []) };
        var native = new Native { Create = request =>
        {
            Require(request.Kind == ExtensionSessionCreationKind.ForkAt && request.EntryId == "entry");
            return ValueTask.FromResult<ExtensionSessionCreationResult?>(new(fresh, "selected"));
        }};
        var command = ExtensionCommandFacade.CreateCommand("fork", "fork", "fork", new Host(), async (_, view, _) =>
        { var result = await view.ForkAsync("entry", before: false); Require(result?.SessionId == "target"); });
        await command.ExecuteAsync(Arguments, native, default);
        Require(native.Calls == 1);
    }
    private static async Task OriginalFaultGraph()
    {
        var first = new Exception("first" ); var second = new Exception("second");
        var original = new TaskCompletionSource<ExtensionSessionCreationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException([first, second]);
        var native = new Native { Create = _ => new(original.Task) };
        var command = ExtensionCommandFacade.CreateCommand("fault", "fault", "fault", new Host(), (_, view, _) =>
        { _ = view.NewSessionAsync(); return ValueTask.CompletedTask; });
        try { await command.ExecuteAsync(Arguments, native, default); throw new Exception("Missing failure."); }
        catch (AggregateException error)
        { Require(error.InnerExceptions.Count == 2 && ReferenceEquals(error.InnerExceptions[0], first) && ReferenceEquals(error.InnerExceptions[1], second)); }
    }
    private static async Task UnsupportedAuthority()
    {
        var command = ExtensionCommandFacade.CreateCommand("none", "none", "none", new Host(), (_, view, _) =>
        {
            try { _ = view.NewSessionAsync(); throw new Exception("Invented authority."); }
            catch (NotSupportedException) { }
            return ValueTask.CompletedTask;
        });
        await command.ExecuteAsync(Arguments, new Bare(), default);
    }
    private static async Task CanceledSession()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var command = ExtensionCommandFacade.CreateCommand("cancel", "cancel", "cancel", new Host(), (_, view, _) =>
        {
            try { _ = view.Model; throw new Exception("Canceled read succeeded."); }
            catch (OperationCanceledException error) { Require(error.CancellationToken == canceled.Token); }
            return ValueTask.CompletedTask;
        });
        await command.ExecuteAsync(Arguments, new Bare { SessionCancellationToken = canceled.Token }, default);
    }
    private static async Task RegistryCloseJoinsCallback()
    {
        await using var registry = new ExtensionRegistry();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IExtensionCommandFacade? retained = null;
        var descriptor = ExtensionCommandFacade.CreateCommand("native", "native", "native", new Host(), async (_, view, _) =>
        { retained = view; Require(view.Cwd == "host-cwd"); entered.SetResult(); await release.Task; });
        var owner = await registry.ActivateAsync("facade-owner", new Plugin(descriptor));
        var dispatchOriginal = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "native", Arguments).AsTask();
        await entered.Task;
        var closeOriginal = owner.DisposeAsync().AsTask();
        try { Require(!closeOriginal.IsCompleted && !dispatchOriginal.IsCompleted); }
        finally { release.TrySetResult(); await dispatchOriginal; await closeOriginal; }
        try { _ = retained!.IsIdle; throw new Exception("Retired facade admitted a read."); }
        catch (InvalidOperationException) { }
    }
    private static async Task MainAndOwnedFailure()
    {
        var main = new Exception("main"); var owned = new Exception("owned");
        var native = new Native { Create = _ => ValueTask.FromException<ExtensionSessionCreationResult?>(owned) };
        var command = ExtensionCommandFacade.CreateCommand("both", "both", "both", new Host(), (_, view, _) =>
        { _ = view.NewSessionAsync(); throw main; });
        try { await command.ExecuteAsync(Arguments, native, default); throw new Exception("Faults lost."); }
        catch (AggregateException error)
        { Require(error.InnerExceptions.Count == 2 && ReferenceEquals(error.InnerExceptions[0], main) && ReferenceEquals(error.InnerExceptions[1], owned)); }
    }
    private sealed class Plugin(ExtensionCommandDescriptor descriptor) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
        { registry.RegisterCommand(descriptor); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task AdmittedHostFullEngineForest()
    {
        var codec = new SessionEntryCodec();
        SessionEntry Entry(string id, string? parent) => codec.Parse("{\"type\":\"custom\",\"id\":\"" + id +
            "\",\"parentId\":" + (parent is null ? "null" : "\"" + parent + "\"") +
            ",\"timestamp\":\"2026-01-01T00:00:00Z\",\"customType\":\"state\",\"data\":{\"opaque\":1e400}}");
        var entries = ImmutableArray.Create(Entry("root", null), Entry("leaf", "root"), Entry("other", "root"));
        var host = new GraphHost(new SessionTreeQueries().Build(entries));
        var captured = new ExtensionSessionSnapshot("source", 1, "leaf", entries.Take(2).Select(entry => entry.WireBody).ToImmutableArray());
        await using var registry = new ExtensionRegistry(null, null, new ViewProvider(captured), host);
        var calls = 0;
        var descriptor = ExtensionCommandFacade.CreateCommand("forest", "forest", "forest", (_, facade, _) =>
        {
            calls++;
            var graph = (IExtensionSessionGraphFacade)facade;
            Require(graph.GetEntries().Length == 3 && graph.GetEntry("other")!.ToString().Contains("1e400", StringComparison.Ordinal));
            Require(graph.GetBranch("other").Length == 2 && graph.GetLeafEntry()!.ToString().Contains("other", StringComparison.Ordinal));
            Require(facade.LeafId == "other" && facade.GetBranch().Length == 2);
            Require(graph.BuildContextEntries().Length == 2 && graph.BuildSessionProjection().Messages.IsEmpty);
            return ValueTask.CompletedTask;
        });
        var owner = await registry.ActivateAsync("forest-owner", new Plugin(descriptor));
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "forest", Arguments);
        Require(calls == 1); await owner.DisposeAsync();
    }
    private static async Task HostActionIgnoredOriginalJoin()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new ActionHost(actual.Task);
        var descriptor = ExtensionCommandFacade.CreateCommand("idle", "idle", "idle", host, (_, facade, _) =>
        { _ = ((IExtensionHostActionFacade)facade).WaitForIdleAsync(); return ValueTask.CompletedTask; });
        var original = descriptor.ExecuteAsync(Arguments, new Bare(), default).AsTask();
        try { Require(!original.IsCompleted && host.Calls == 1); }
        finally { actual.TrySetResult(); await original; }
        Require(actual.Task.IsCompletedSuccessfully);
    }
    private static async Task NoAdmittedHost()
    {
        var calls = 0;
        var descriptor = ExtensionCommandFacade.CreateCommand("unbound", "unbound", "unbound", (_, _, _) =>
        { calls++; return ValueTask.CompletedTask; });
        await using var registry = new ExtensionRegistry();
        var owner = await registry.ActivateAsync("unbound-owner", new Plugin(descriptor));
        try { await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "unbound", Arguments); throw new Exception("Unbound facade admitted."); }
        catch (NotSupportedException) { Require(calls == 0); }
        await owner.DisposeAsync();
    }
    private sealed class ViewProvider(ExtensionSessionSnapshot snapshot) : IExtensionSessionViewProvider
    { public ExtensionSessionSnapshot? Capture(IExtensionContext context) => snapshot; }
    private static async Task ReadHostExactFrame()
    {
        var host = new ActionHost(Task.CompletedTask);
        await using var registry = new ExtensionRegistry(null, null, null, host);
        IExtensionContextReadHost? retainedHost = null;
        IExtensionCommandContext? retainedContext = null;
        var calls = 0;
        var descriptor = new ExtensionCommandDescriptor("exact", "exact", "exact", (_, context, _) =>
        {
            var exposed = ((IExtensionFacadeHostContext)context).FacadeHost;
            Require(!ReferenceEquals(exposed, host) && exposed is not IExtensionContextActionHost && exposed.GetCwd(context) == "host-cwd");
            try { _ = exposed.GetCwd(new Bare()); throw new Exception("Invented context acquired read authority."); }
            catch (InvalidOperationException) { }
            if (retainedHost is not null)
            {
                try { _ = retainedHost.GetCwd(retainedContext!); throw new Exception("Prior callback context reentered host."); }
                catch (InvalidOperationException) { }
            }
            retainedHost = exposed; retainedContext = context; calls++;
            return ValueTask.CompletedTask;
        });
        var owner = await registry.ActivateAsync("exact-owner", new Plugin(descriptor));
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "exact", Arguments);
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "exact", Arguments);
        Require(calls == 2); await owner.DisposeAsync();
    }
    private static async Task AdmittedActionOwnerClose()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new ActionHost(actual.Task);
        await using var registry = new ExtensionRegistry(null, null, null, host);
        var descriptor = ExtensionCommandFacade.CreateCommand("owned-idle", "owned-idle", "owned-idle", (_, facade, _) =>
        { _ = ((IExtensionHostActionFacade)facade).WaitForIdleAsync(); return ValueTask.CompletedTask; });
        var owner = await registry.ActivateAsync("owned-idle-owner", new Plugin(descriptor));
        var dispatch = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "owned-idle", Arguments).AsTask();
        var close = owner.DisposeAsync().AsTask();
        try { Require(host.Calls == 1 && !dispatch.IsCompleted && !close.IsCompleted); }
        finally
        {
            actual.TrySetResult();
            try { await dispatch; } catch (OperationCanceledException) { }
            await close;
        }
        Require(actual.Task.IsCompletedSuccessfully);
    }
    private sealed class ActionHost(Task original) : Host, IExtensionContextActionHost
    {
        internal int Calls;
        public Task WaitForIdleAsync(IExtensionCommandContext context, CancellationToken cancellationToken) { Calls++; return original; }
        public Task ReloadAsync(IExtensionCommandContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompactAsync(IExtensionCommandContext context, string? customInstructions, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class GraphHost(SessionTreeSnapshot tree) : Host, IExtensionSessionGraphReadHost
    {
        private static void Validate(IExtensionSessionContext context)
        { if (context.SessionSnapshot is not { SessionId: "source", Generation: 1 }) throw new InvalidOperationException("Stale engine view."); }
        public string? GetLeafId(IExtensionSessionContext context) { Validate(context); return "other"; }
        public JsonData? GetEntry(IExtensionSessionContext context, string entryId) { Validate(context); return tree.GetEntry(entryId)?.WireBody; }
        public JsonData? GetLeafEntry(IExtensionSessionContext context) => GetEntry(context, "other");
        public ImmutableArray<JsonData> GetEntries(IExtensionSessionContext context) { Validate(context); return tree.Entries.Select(entry => entry.WireBody).ToImmutableArray(); }
        public ImmutableArray<JsonData> GetBranch(IExtensionSessionContext context, string? entryId) { Validate(context); return tree.GetBranch(entryId ?? "other").Select(entry => entry.WireBody).ToImmutableArray(); }
        public JsonData GetTree(IExtensionSessionContext context) => throw new NotSupportedException("This control exercises actual forest queries, not export serialization.");
        public JsonData GetHeader(IExtensionSessionContext context) => throw new NotSupportedException();
        public string? GetSessionFile(IExtensionSessionContext context) => null;
        public string? GetSessionName(IExtensionSessionContext context) => tree.SessionName;
        public string? GetLabel(IExtensionSessionContext context, string entryId) => tree.Labels.TryGetValue(entryId, out var label) ? label.Label : null;
        public string GetSessionCwd(IExtensionSessionContext context) => "engine-cwd";
        public string? GetSessionDirectory(IExtensionSessionContext context) => null;
        public ExtensionFacadeSessionProjection BuildSessionProjection(IExtensionSessionContext context)
        {
            Validate(context);
            var projected = new SessionContextProjector().Project(tree.Entries, "other");
            return new(projected.ContextEntries.Select(entry => entry.SourceEntry.WireBody).ToImmutableArray(),
                projected.ContextEntries.Select(entry => new ExtensionFacadeSessionContribution(entry.SourceEntry.WireBody,
                    entry.Messages.Select(message => message.WireBody).ToImmutableArray())).ToImmutableArray(),
                projected.Messages.Select(message => message.WireBody).ToImmutableArray(),
                projected.LlmMessages.Select(message => message.WireBody).ToImmutableArray(), projected.ThinkingLevel,
                projected.Model?.Provider, projected.Model?.ModelId);
        }
    }
    private static async Task CallbackOriginalGraph()
    {
        var shared = new Exception("shared callback leaf");
        var nested = new AggregateException(shared, shared);
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetException([nested, shared, nested]);
        var command = ExtensionCommandFacade.CreateCommand("graph", "graph", "graph", new Host(), (_, _, _) => new(source.Task));
        var wrapper = command.ExecuteAsync(Arguments, new Bare(), default).AsTask();
        try { await wrapper; throw new Exception("Missing callback graph."); }
        catch (AggregateException error)
        {
            Require(wrapper.IsFaulted && source.Task.IsFaulted && error.InnerExceptions.Count == 3);
            Require(ReferenceEquals(error.InnerExceptions[0], nested) && ReferenceEquals(error.InnerExceptions[1], shared)
                && ReferenceEquals(error.InnerExceptions[2], nested));
        }
    }
    private static async Task CallbackFaultedOceAndCanceled()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var fault = new OperationCanceledException("faulted callback", cancellation.Token);
        var faulted = Task.FromException(fault);
        var command = ExtensionCommandFacade.CreateCommand("oce", "oce", "oce", new Host(), (_, _, _) => new(faulted));
        var wrapper = command.ExecuteAsync(Arguments, new Bare(), default).AsTask();
        try { await wrapper; throw new Exception("Missing callback OCE fault."); }
        catch (ExtensionFacadeOriginalFaultException error)
        { Require(wrapper.IsFaulted && !wrapper.IsCanceled && ReferenceEquals(error.OriginalTask, faulted)
            && ReferenceEquals(error.OriginalException.InnerExceptions[0], fault)); }
        var canceled = Task.FromCanceled(cancellation.Token);
        command = ExtensionCommandFacade.CreateCommand("canceled", "canceled", "canceled", new Host(), (_, _, _) => new(canceled));
        wrapper = command.ExecuteAsync(Arguments, new Bare(), default).AsTask();
        try { await wrapper; throw new Exception("Missing callback cancellation."); }
        catch (OperationCanceledException error) { Require(wrapper.IsCanceled && error.CancellationToken == cancellation.Token); }
    }
    private static async Task ActionFaultedOceAndCanceled()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var fault = new OperationCanceledException("faulted action", cancellation.Token);
        var faulted = Task.FromException<ExtensionSessionCreationResult?>(fault);
        var native = new Native { Create = _ => new(faulted) };
        Task<IExtensionCommandFacade?>? mapped = null;
        var command = ExtensionCommandFacade.CreateCommand("action-oce", "action-oce", "action-oce", new Host(), (_, view, _) =>
        { mapped = view.NewSessionAsync().AsTask(); return ValueTask.CompletedTask; });
        var wrapper = command.ExecuteAsync(Arguments, native, default).AsTask();
        try { await wrapper; throw new Exception("Missing action OCE fault."); }
        catch (ExtensionFacadeOriginalFaultException error)
        { Require(wrapper.IsFaulted && mapped!.IsFaulted && ReferenceEquals(error.OriginalTask, faulted)
            && ReferenceEquals(error.OriginalException.InnerExceptions[0], fault)); }
        try { await mapped!; } catch (ExtensionFacadeOriginalFaultException) { }
        var canceled = Task.FromCanceled<ExtensionSessionCreationResult?>(cancellation.Token);
        native = new Native { Create = _ => new(canceled) };
        wrapper = command.ExecuteAsync(Arguments, native, default).AsTask();
        try { await wrapper; throw new Exception("Missing action cancellation."); }
        catch (OperationCanceledException error) { Require(wrapper.IsCanceled && mapped!.IsCanceled && error.CancellationToken == cancellation.Token); }
        try { await mapped!; } catch (OperationCanceledException error) { Require(error.CancellationToken == cancellation.Token); }
    }
    private static async Task MultipleCanceledOriginalInventory()
    {
        using var firstCancellation = new CancellationTokenSource(); firstCancellation.Cancel();
        using var secondCancellation = new CancellationTokenSource(); secondCancellation.Cancel();
        using var callbackCancellation = new CancellationTokenSource(); callbackCancellation.Cancel();
        var first = Task.FromCanceled<ExtensionSessionCreationResult?>(firstCancellation.Token);
        var second = Task.FromCanceled<ExtensionSessionCreationResult?>(secondCancellation.Token);
        var callbackOriginal = Task.FromCanceled(callbackCancellation.Token);
        var call = 0;
        var native = new Native { Create = _ => new(++call == 1 ? first : second) };
        var command = ExtensionCommandFacade.CreateCommand("many-canceled", "many-canceled", "many-canceled", new Host(), (_, view, _) =>
        {
            // Both mapped results are deliberately ignored: the facade must directly join them.
            _ = view.NewSessionAsync(); _ = view.NewSessionAsync();
            return new(callbackOriginal);
        });
        var wrapper = command.ExecuteAsync(Arguments, native, default).AsTask();
        try { await wrapper; throw new Exception("Canceled originals were lost."); }
        catch (AggregateException error)
        {
            Require(wrapper.IsFaulted && call == 2 && error.InnerExceptions.Count == 3);
            var inventory = error.InnerExceptions.Cast<ExtensionFacadeCanceledOriginalException>().ToArray();
            Require(ReferenceEquals(inventory[0].OriginalTask, callbackOriginal) && inventory[0].CancellationToken == callbackCancellation.Token);
            Require(ReferenceEquals(inventory[1].OriginalTask, first) && inventory[1].CancellationToken == firstCancellation.Token);
            Require(ReferenceEquals(inventory[2].OriginalTask, second) && inventory[2].CancellationToken == secondCancellation.Token);
            Require(inventory.All(item => item.OriginalTask.IsCanceled && item.InnerException is OperationCanceledException));
        }
    }
    private class Host : IExtensionContextReadHost
    {
        internal bool Idle = true;
        public string GetCwd(IExtensionContext context) => "host-cwd";
        public JsonData? GetModel(IExtensionContext context) => null;
        public bool IsIdle(IExtensionContext context) => Idle;
        public bool HasPendingMessages(IExtensionContext context) => !Idle;
        public string GetSystemPrompt(IExtensionContext context) => "host-prompt";
    }
    private class Bare : IExtensionCommandContext
    {
        public string OwnerId => "owner";
        public long OwnerGeneration => 7;
        public CancellationToken OperationCancellationToken => default;
        public CancellationToken SessionCancellationToken { get; init; }
        public CancellationToken ExtensionLifetimeCancellationToken => default;
    }
    private sealed class Native : Bare, IExtensionSessionCreationCommandContext
    {
        internal int Calls;
        internal Func<ExtensionSessionCreationRequest, ValueTask<ExtensionSessionCreationResult?>> Create = _ => ValueTask.FromResult<ExtensionSessionCreationResult?>(null);
        public ExtensionSessionSnapshot? SessionSnapshot { get; init; } = new("source", 1, "leaf", ImmutableArray.Create(JsonData.Parse("{\"id\":\"leaf\"}")));
        public IExtensionUi Ui => throw new NotSupportedException("This read/action facade does not use UI.");
        public ValueTask<ExtensionSessionCreationResult?> CreateSessionAsync(ExtensionSessionCreationRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Create(request); }
        public ValueTask<IExtensionSessionCommandContext?> SwitchSessionAsync(string absolutePath, bool useLatestLeaf = true, string? selectedLeafId = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IExtensionSessionCommandContext?>(null);
        public ValueTask<ExtensionSessionEntryAcknowledgment> AppendSessionEntryAsync(string entryKind, int schemaVersion, JsonData data, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
