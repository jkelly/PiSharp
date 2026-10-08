using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Contracts;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Facade.Execution;

namespace PiSharp.Extensions.Runtime.Facade.Context;

/// <summary>Composes with native command admission; it never creates an independent host owner.</summary>
public static partial class ExtensionCommandFacade
{
    /// <summary>Uses only bindings carried by the actual admitted native command context.</summary>
    public static ExtensionCommandDescriptor CreateCommand(string registrationId, string name,
        string description, ExtensionFacadeCommandCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new(registrationId, name, description, (arguments, context, token) =>
        {
            var host = context is ExtensionCommandContext native ? native.GetFacadeHostForAdapter() :
                context is IExtensionFacadeHostContext admitted ? admitted.FacadeHost :
                throw new NotSupportedException("The admitted command has no facade host binding.");
            var adapted = CreateCommand(registrationId, name, description, host, callback);
            return adapted.ExecuteAsync(arguments, context, token);
        });
    }
    public static ExtensionCommandDescriptor CreateCommand(string registrationId, string name,
        string description, IExtensionContextReadHost host, ExtensionFacadeCommandCallback callback)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(callback);
        return new(registrationId, name, description, async (arguments, context, token) =>
        {
            var lease = new Lease();
            var previous = Lease.Current.Value;
            Lease.Current.Value = lease;
            Exception? callbackFailure = null;
            Task? callbackOriginal = null;
            try
            {
                var original = callback(arguments, new View(context, host, lease), token);
                callbackOriginal = original.AsTask();
                await callbackOriginal.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                callbackFailure = callbackOriginal is null
                    ? error is OperationCanceledException
                        ? new InvalidOperationException("The facade callback threw synchronously before returning an original task.", error)
                        : error
                    : PreserveOriginalFailure(callbackOriginal, error);
            }
            finally { Lease.Current.Value = previous; }
            var failures = await lease.CloseAsync().ConfigureAwait(false);
            if (callbackFailure is not null && !failures.Any(error => SameFailure(error, callbackFailure)))
                failures.Insert(0, callbackFailure);
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("Facade command and owned session actions failed.", failures);
        });
    }

    private static bool SameFailure(Exception left, Exception right) => ReferenceEquals(left, right) ||
        left is ExtensionFacadeOriginalFaultException a && right is ExtensionFacadeOriginalFaultException b &&
        ReferenceEquals(a.OriginalTask, b.OriginalTask);

    private static Exception PreserveOriginalFailure(Task original, Exception awaitedFailure)
    {
        if (original.IsCanceled && awaitedFailure is OperationCanceledException canceled)
            return new ExtensionFacadeCanceledOriginalException(original, canceled);
        if (!original.IsFaulted) return awaitedFailure;
        var graph = original.Exception!;
        if (awaitedFailure is OperationCanceledException)
            return new ExtensionFacadeOriginalFaultException(original, graph);
        return graph.InnerExceptions.Count > 1 ? graph : awaitedFailure;
    }

    private sealed class Lease(Action? originatingCheck = null)
    {
        internal static readonly AsyncLocal<Lease?> Current = new();
        private readonly object gate = new();
        private readonly List<TaskCompletionSource<(Task Original, Task Mapped)>> work = [];
        private readonly Dictionary<object, ExtensionSessionBehaviorOperationOwner> behaviorOwners = new(ReferenceEqualityComparer.Instance);
        private bool closing;
        private Task<List<Exception>>? settlement;
        internal void Check()
        {
            lock (gate)
                if (closing || originatingCheck is null && !ReferenceEquals(Current.Value, this))
                    throw new InvalidOperationException("The facade belongs to an active originating command callback.");
            originatingCheck?.Invoke();
        }
        internal ExtensionSessionBehaviorOperationOwner BehaviorOwner(object view, IExtensionContext context, Action validate)
        {
            lock(gate)
            {
                Check();
                if(behaviorOwners.TryGetValue(view,out var found))return found;
                if(behaviorOwners.Count==128)throw new InvalidOperationException("Facade behavior owner bound exceeded.");
                var created=new ExtensionSessionBehaviorOperationOwner(context,validate);behaviorOwners.Add(view,created);return created;
            }
        }
        internal ValueTask<T> Start<TSource, T>(Func<ValueTask<TSource>> invoke, Func<TSource, T> map)
        {
            var slot = new TaskCompletionSource<(Task Original, Task Mapped)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                Check();
                work.Add(slot);
            }
            try
            {
                // Invoke once and convert once, including custom IValueTaskSource originals.
                var original = invoke();
                var task = original.AsTask();
                var mapped = MapAsync(task, map);
                slot.SetResult((task, mapped));
                return new(mapped);
            }
            catch (Exception error)
            {
                var failure = Task.FromException<T>(error);
                slot.SetResult((failure, failure));
                throw;
            }
        }
        private static async Task<T> MapAsync<TSource, T>(Task<TSource> original, Func<TSource, T> map)
        {
            TSource value;
            try { value = await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                ExceptionDispatchInfo.Capture(PreserveOriginalFailure(original, error)).Throw();
                throw; // Unreachable, retaining definite assignment for the mapper.
            }
            return map(value);
        }
        internal ValueTask StartTask(Func<Task> invoke)
        {
            var slot = new TaskCompletionSource<(Task Original, Task Mapped)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { Check(); work.Add(slot); }
            try
            {
                var original = invoke() ?? throw new InvalidOperationException("The host action returned no original Task.");
                slot.SetResult((original, original));
                return new(original);
            }
            catch (Exception error)
            {
                var failure = Task.FromException(error);
                slot.SetResult((failure, failure));
                throw;
            }
        }
        internal Task<List<Exception>> CloseAsync()
        {
            TaskCompletionSource<(Task Original, Task Mapped)>[] slots;
            ExtensionSessionBehaviorOperationOwner[] owners;
            TaskCompletionSource<Task<List<Exception>>> started;
            lock (gate)
            {
                if(settlement is not null)return settlement;
                closing = true; slots = work.ToArray(); owners=behaviorOwners.Values.ToArray();
                started=new(TaskCreationOptions.RunContinuationsAsynchronously);settlement=JoinClose(started.Task);
            }
            started.SetResult(SettleAsync(slots,owners));return settlement!;
        }
        private static async Task<List<Exception>> JoinClose(Task<Task<List<Exception>>> started)
        {var original=await started.ConfigureAwait(false);return await original.ConfigureAwait(false);}
        private static async Task<List<Exception>> SettleAsync(TaskCompletionSource<(Task Original, Task Mapped)>[] slots,
            ExtensionSessionBehaviorOperationOwner[] owners)
        {
            var failures = new List<Exception>();
            var closes=new List<(ExtensionSessionBehaviorOperationOwner Owner,Task Original)>();
            foreach(var owner in owners)
            {
                try{closes.Add((owner,owner.CloseAsync()));}catch(Exception error){failures.Add(error);}
            }
            foreach (var slot in slots)
            {
                var pair = await slot.Task.ConfigureAwait(false);
                var original = pair.Original;
                Exception? originalFailure = null;
                try { await original.ConfigureAwait(false); }
                catch (Exception error)
                {
                    originalFailure = error;
                    failures.Add(PreserveOriginalFailure(original, error));
                }
                if (ReferenceEquals(pair.Original, pair.Mapped)) continue;
                try { await pair.Mapped.ConfigureAwait(false); }
                catch (Exception error)
                {
                    if (originalFailure is not null) continue; // Mapper cannot run after original failure.
                    if (!ReferenceEquals(error, originalFailure) && !failures.Any(failure => SameFailure(failure, error))) failures.Add(error);
                }
            }
            foreach(var item in closes)
            {
                Exception? direct=null;
                try{await item.Original.ConfigureAwait(false);}catch(Exception error){direct=error;}
                if(direct is not null)failures.Add(new ExtensionFacadeBehaviorSettlementException(item.Original,
                    item.Original.IsFaulted?item.Original.Exception:null,direct,item.Owner.CaptureOriginals()));
            }
            return failures.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToList();
        }
    }

    private sealed partial class View(IExtensionCommandContext context, IExtensionContextReadHost host, Lease lease)
        : IExtensionCommandFacade, IExtensionSessionGraphFacade, IExtensionHostActionFacade, IExtensionSettingsThinkingReadFacade, IExtensionExecCommandFacade, IExtensionTreeCommandFacade, IExtensionSessionSetupCommandFacade, IExtensionLifecycleHandoffFacade, IExtensionSystemPromptOptionsReadFacade, IExtensionSessionBehaviorFacade
    {
        public void RequestShutdown(CancellationToken admissionCancellation = default)
            => RequestLifecycle(ExtensionLifecycleHandoffKind.Shutdown, admissionCancellation);
        public void RequestReload(CancellationToken admissionCancellation = default)
            => RequestLifecycle(ExtensionLifecycleHandoffKind.Reload, admissionCancellation);
        private void RequestLifecycle(ExtensionLifecycleHandoffKind kind, CancellationToken token)
        {
            Check(); token.ThrowIfCancellationRequested();
            var admitted = host as IExtensionLifecycleHandoffHost ??
                throw new NotSupportedException("No actual post-origin lifecycle host.");
            admitted.RequestLifecycleHandoff(context, kind, token);
        }
        private T Read<T>(Func<T> read)
        {
            Check();
            var result = read();
            Check();
            return result;
        }
        private void Check()
        {
            lease.Check();
            context.OperationCancellationToken.ThrowIfCancellationRequested();
            context.SessionCancellationToken.ThrowIfCancellationRequested();
            context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        }
        public string OwnerId => Read(() => context.OwnerId);
        public long OwnerGeneration => Read(() => context.OwnerGeneration);
        public string Cwd => Read(() => host.GetCwd(context));
        public JsonData? Model => Read(() => host.GetModel(context));
        public bool IsIdle => Read(() => host.IsIdle(context));
        public bool HasPendingMessages => Read(() => host.HasPendingMessages(context));
        public string SystemPrompt => Read(() => host.GetSystemPrompt(context));
        public JsonData GetSystemPromptOptions() => Read(() => (host as IExtensionSystemPromptOptionsReadHost ??
            throw new NotSupportedException("No actual system prompt construction input binding.")).GetSystemPromptOptions(context));
        private IExtensionSettingsThinkingReadHost SettingsThinking => host as IExtensionSettingsThinkingReadHost ??
            throw new NotSupportedException("The admitted host has no settings/thinking read binding.");
        public JsonData GetSettings() => Read(() => SettingsThinking.GetSettings(context));
        public string GetThinkingLevel() => Read(() => SettingsThinking.GetThinkingLevel(context));
        private ExtensionSessionSnapshot? Snapshot => (context as IExtensionSessionContext)?.SessionSnapshot;
        public string? SessionId => Read(() => Snapshot?.SessionId);
        public string? LeafId => Read(() => host is IExtensionSessionGraphReadHost graph
            ? graph.GetLeafId(Session) : Snapshot?.SelectedLeafId);
        public ImmutableArray<JsonData> GetBranch() => Read(() => host is IExtensionSessionGraphReadHost graph
            ? graph.GetBranch(Session, null) : Snapshot?.BranchEntries ?? []);
        private IExtensionSessionContext Session => context as IExtensionSessionContext ??
            throw new NotSupportedException("The admitted context has no session view.");
        private IExtensionSessionGraphReadHost Graph => host as IExtensionSessionGraphReadHost ??
            throw new NotSupportedException("The admitted host has no full session graph binding.");
        public JsonData? GetEntry(string entryId) => Read(() => Graph.GetEntry(Session, entryId));
        public JsonData? GetLeafEntry() => Read(() => Graph.GetLeafEntry(Session));
        public ImmutableArray<JsonData> GetEntries() => Read(() => Graph.GetEntries(Session));
        public ImmutableArray<JsonData> GetBranch(string? entryId) => Read(() => Graph.GetBranch(Session, entryId));
        public JsonData GetTree() => Read(() => Graph.GetTree(Session));
        public JsonData GetHeader() => Read(() => Graph.GetHeader(Session));
        public string? SessionFile => Read(() => Graph.GetSessionFile(Session));
        public string? SessionName => Read(() => Graph.GetSessionName(Session));
        public string? GetLabel(string entryId) => Read(() => Graph.GetLabel(Session, entryId));
        public string SessionCwd => Read(() => Graph.GetSessionCwd(Session));
        public string? SessionDirectory => Read(() => Graph.GetSessionDirectory(Session));
        public ExtensionFacadeSessionProjection BuildSessionProjection() => Read(() => Graph.BuildSessionProjection(Session));
        public ImmutableArray<JsonData> BuildContextEntries() => BuildSessionProjection().ContextEntries;
        private IExtensionContextActionHost Actions => host as IExtensionContextActionHost ??
            throw new NotSupportedException("The admitted host has no command action binding.");
        public ValueTask<ExtensionExecResult> ExecAsync(string command, ImmutableArray<string> arguments, ExtensionExecOptions? options = null)
        {
            Check();
            var execution = host as IExtensionContextExecHost ?? throw new NotSupportedException("No admitted execution context host.");
            return lease.Start<ExtensionExecResult, ExtensionExecResult>(
                () => new ValueTask<ExtensionExecResult>(execution.ExecAsync(context, command, arguments, options)), static value => value);
        }
        public ValueTask WaitForIdleAsync(CancellationToken cancellationToken = default)
        { Check(); return lease.StartTask(() => Actions.WaitForIdleAsync(context, cancellationToken)); }
        public ValueTask ReloadAsync(CancellationToken cancellationToken = default)
        { Check(); return lease.StartTask(() => Actions.ReloadAsync(context, cancellationToken)); }
        public ValueTask CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default)
        { Check(); return lease.StartTask(() => Actions.CompactAsync(context, customInstructions, cancellationToken)); }
        public ValueTask<IExtensionCommandFacade?> NewSessionAsync(string? parentSession = null,
            CancellationToken cancellationToken = default) => CreateAsync(
                new(ExtensionSessionCreationKind.New, ParentSession: parentSession), cancellationToken);
        public ValueTask<IExtensionCommandFacade?> ForkAsync(string entryId, bool before = true,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
            return CreateAsync(new(before ? ExtensionSessionCreationKind.ForkBefore : ExtensionSessionCreationKind.ForkAt,
                EntryId: entryId), cancellationToken);
        }
        private ValueTask<IExtensionCommandFacade?> CreateAsync(ExtensionSessionCreationRequest request,
            CancellationToken token)
        {
            Check();
            if (context is not IExtensionSessionCreationCommandContext creation)
                throw new NotSupportedException("The admitted host context has no native session creation broker.");
            return lease.Start<ExtensionSessionCreationResult?, IExtensionCommandFacade?>(
                () => creation.CreateSessionAsync(request, token),
                result => result is null ? null : new View(result.Context, host, lease));
        }
        public ValueTask<IExtensionCommandFacade?> SwitchSessionAsync(string absolutePath,
            CancellationToken cancellationToken = default)
        {
            Check();
            if (context is not IExtensionSessionCommandContext sessions)
                throw new NotSupportedException("The admitted host context has no native session switch broker.");
            return lease.Start<IExtensionSessionCommandContext?, IExtensionCommandFacade?>(
                () => sessions.SwitchSessionAsync(absolutePath, cancellationToken: cancellationToken),
                result => result is null ? null : new View(result, host, lease));
        }
    }
}

/// <summary>Public context exposure is read-only and bound to the exact admitted context/frame.
/// It deliberately does not implement action/provider mutation capabilities on the configured host.</summary>
internal sealed class AdmittedContextReadHost(IExtensionContext context, IExtensionContextReadHost host, Action validate)
    : IExtensionContextReadHost, IExtensionSettingsThinkingReadHost, IExtensionSystemPromptOptionsReadHost
{
    private T Read<T>(IExtensionContext supplied, Func<T> read)
    {
        if (!ReferenceEquals(context, supplied)) throw new InvalidOperationException("Facade host reads require the exact admitted context.");
        validate(); var result = read(); validate(); return result;
    }
    public string GetCwd(IExtensionContext supplied) => Read(supplied, () => host.GetCwd(context));
    public JsonData? GetModel(IExtensionContext supplied) => Read(supplied, () => host.GetModel(context));
    public bool IsIdle(IExtensionContext supplied) => Read(supplied, () => host.IsIdle(context));
    public bool HasPendingMessages(IExtensionContext supplied) => Read(supplied, () => host.HasPendingMessages(context));
    public string GetSystemPrompt(IExtensionContext supplied) => Read(supplied, () => host.GetSystemPrompt(context));
    public JsonData GetSystemPromptOptions(IExtensionContext supplied) => Read(supplied, () =>
        (host as IExtensionSystemPromptOptionsReadHost ?? throw new NotSupportedException("No actual prompt construction input binding."))
            .GetSystemPromptOptions(context));
    private IExtensionSettingsThinkingReadHost SettingsThinking => host as IExtensionSettingsThinkingReadHost ??
        throw new NotSupportedException("The admitted host has no settings/thinking read binding.");
    public JsonData GetSettings(IExtensionContext supplied) => Read(supplied, () => SettingsThinking.GetSettings(context));
    public string GetThinkingLevel(IExtensionContext supplied) => Read(supplied, () => SettingsThinking.GetThinkingLevel(context));
}
