using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Extensions;
using PiSharp.Sessions.Lifecycle;
using System.Globalization;
using PiSharp.Sessions.Storage;

namespace PiSharp.Cli.Extensions;

/// <summary>One explicit host attachment. Exposes acknowledged ancestry, with no session mutation authority.</summary>
internal sealed partial class NativeSessionSnapshotProvider : IExtensionSessionCatalogProvider, IExtensionSessionOpaqueViewProvider,
    IExtensionSessionContextEditProvider, IExtensionSessionCompactionProvider, IExtensionSessionToolActivationProvider, IExtensionSessionTreeProvider
{
    private ReplaceableAgentSession? owner;
    internal Func<AgentSessionAttachment, PersistentAgentSession, CancellationToken, ValueTask<bool>>? BeforeSwitch { get; set; }
    internal Func<AgentSessionReplacement, ValueTask>? AfterSwitch { get; set; }
    internal void Attach(ReplaceableAgentSession value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Interlocked.CompareExchange(ref owner, value, null) is not null)
            throw new InvalidOperationException("Published generation already has an explicit session attachment.");
    }
    public ExtensionSessionSnapshot? Capture(IExtensionContext context)
    {
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        var host = Volatile.Read(ref owner);
        if (host is null) return null;
        return Snapshot(host.Current);
    }
    private static ExtensionSessionSnapshot Snapshot(AgentSessionAttachment attached)
        => Snapshot(attached.Session, attached.Generation);
    private static ExtensionSessionSnapshot Snapshot(PersistentAgentSession session, long generation)
    {
        var state = session.Snapshot;
        if (state.IsDisposed || state.IsRetired || state.Fault is not null) throw new InvalidOperationException("Published session view is no longer available.");
        // The registry applies its independent view budgets before delivering this immutable capture.
        return new(state.Log.Header.Id, generation, state.Context.LeafId,
            state.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray())
            { Persistence = Persistence(state.Log.StorageDurability) };
    }
    private static ExtensionSessionPersistence Persistence(SessionLogStorageDurability durability) => durability switch
    {
        SessionLogStorageDurability.LocalFileFlush => ExtensionSessionPersistence.DurableLocalFile,
        SessionLogStorageDurability.VolatileMemory => ExtensionSessionPersistence.VolatileMemory,
        SessionLogStorageDurability.DeferredLocalFile => ExtensionSessionPersistence.DeferredLocalFile,
        _ => throw new InvalidOperationException("Session checkpoint storage is unsupported.")
    };

    public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot)
    {
        var host = Volatile.Read(ref owner) ?? throw new InvalidOperationException("No active session owner.");
        var attached = host.Current;
        if (snapshot.Generation != attached.Generation || snapshot.SessionId != attached.Session.Snapshot.Log.Header.Id)
            throw new InvalidOperationException("Session changed during callback admission.");
        return new Scope(this, host, attached, context, snapshot, context.SessionCancellationToken);
    }

    private sealed partial class Scope : IExtensionSessionCatalogScope, IExtensionSessionContextEditScope, IExtensionSessionCompactionScope, IExtensionSessionToolActivationScope, IExtensionSessionTreeScope
    {
        private readonly NativeSessionSnapshotProvider provider;
        private readonly ReplaceableAgentSession host;
        private readonly AgentSessionAttachment attachment;
        private readonly IExtensionContext context;
        private readonly CancellationToken baseSessionToken;
        private readonly CancellationTokenSource lifetime;
        private readonly object gate = new();
        private readonly List<Task> pending = [];
        private Task? disposal;
        private bool closed;
        public ExtensionSessionSnapshot Snapshot { get; }
        public CancellationToken SessionCancellationToken { get; }
        internal Scope(NativeSessionSnapshotProvider provider, ReplaceableAgentSession host, AgentSessionAttachment attachment,
            IExtensionContext context, ExtensionSessionSnapshot snapshot, CancellationToken baseSessionToken)
        {
            this.provider = provider; this.host = host; this.attachment = attachment; this.context = context;
            this.baseSessionToken = baseSessionToken; Snapshot = snapshot;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(baseSessionToken, attachment.LifetimeToken);
            SessionCancellationToken = lifetime.Token;
        }
        private Task<T> Admit<T>(Func<Task<T>> work)
        {
            TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (closed) throw new InvalidOperationException("Session callback scope is closed.");
                if (pending.Count >= 128) throw new InvalidOperationException("Session callback action limit reached.");
                host.ValidateAttachment(attachment);
                context.OperationCancellationToken.ThrowIfCancellationRequested();
                context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
                SessionCancellationToken.ThrowIfCancellationRequested();
                pending.Add(done.Task);
            }
            _ = CompleteAsync(work, done);
            return done.Task;
        }
        private static async Task CompleteAsync<T>(Func<Task<T>> work, TaskCompletionSource<T> done)
        {
            try { done.TrySetResult(await work().ConfigureAwait(false)); }
            catch (OperationCanceledException error) { done.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { done.TrySetException(error); }
        }
        private T AdmitSync<T>(Func<T> work)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (closed || pending.Count >= 128) throw new InvalidOperationException("Session callback scope is closed or exhausted.");
                host.ValidateAttachment(attachment);
                context.OperationCancellationToken.ThrowIfCancellationRequested(); context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
                SessionCancellationToken.ThrowIfCancellationRequested(); pending.Add(done.Task);
            }
            try { return work(); } finally { done.TrySetResult(); }
        }
        public ImmutableArray<string> GetActiveTools() => AdmitSync(() =>
        { host.ValidateAttachment(attachment); return attachment.Session.GetToolActivationSelection().Names; });
        public ExtensionToolActivationSelection SetActiveTools(ImmutableArray<string> names) => AdmitSync<ExtensionToolActivationSelection>(() =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.OperationCancellationToken,
                context.ExtensionLifetimeCancellationToken, SessionCancellationToken);
            host.ValidateAttachment(attachment);
            var selection = attachment.Session.ScheduleToolActivation(names, linked.Token);
            return new(selection.Revision, selection.Names);
        });
        public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string entryKind, int schemaVersion,
            PiSharp.Contracts.JsonData data, CancellationToken cancellationToken) => new(Admit(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                context.OperationCancellationToken, context.ExtensionLifetimeCancellationToken, SessionCancellationToken);
            var receipt = await host.AppendExtensionEntryAsync(attachment,
                new(context.OwnerId, entryKind, schemaVersion, data), linked.Token).ConfigureAwait(false);
            return new ExtensionSessionEntryAcknowledgment(Snapshot.SessionId, Snapshot.Generation, receipt.Entry.WireBody,
                receipt.Append.Sequence, receipt.Append.ByteOffset, receipt.Append.ByteLength, receipt.Context.LeafId)
                { Persistence = Persistence(receipt.Append.Snapshot.StorageDurability) };
        }));
        public ValueTask<ExtensionSessionContextEditAcknowledgment> AppendContextEditAsync(string targetId,
            PiSharp.Contracts.JsonData? replacement,
            Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateProspectiveSnapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(validateProspectiveSnapshot);
            return new(Admit(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                    context.OperationCancellationToken, context.ExtensionLifetimeCancellationToken, SessionCancellationToken);
                var receipt = await host.AppendContextEditAsync(attachment, new(targetId, replacement), linked.Token,
                    async (preview, token) =>
                    {
                        var prospective = new ExtensionSessionSnapshot(Snapshot.SessionId, Snapshot.Generation,
                            preview.Context.LeafId, preview.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray())
                            { Persistence = Persistence(preview.PreviousLog.StorageDurability) };
                        await validateProspectiveSnapshot(prospective, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                var checkpoint = new ExtensionSessionEntryAcknowledgment(Snapshot.SessionId, Snapshot.Generation,
                    receipt.Entry.WireBody, receipt.Append.Sequence, receipt.Append.ByteOffset, receipt.Append.ByteLength, receipt.Context.LeafId)
                    { Persistence = Persistence(receipt.Append.Snapshot.StorageDurability) };
                var after = new ExtensionSessionSnapshot(Snapshot.SessionId, Snapshot.Generation, receipt.Context.LeafId,
                    receipt.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray()) { Persistence = checkpoint.Persistence };
                return new ExtensionSessionContextEditAcknowledgment(checkpoint, after);
            }));
        }
        public ValueTask<ExtensionSessionCompactionAcknowledgment?> AppendCompactionSummaryAsync(ExtensionSessionCompactionSummary summary,
            Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateProspectiveSnapshot, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(summary); ArgumentNullException.ThrowIfNull(validateProspectiveSnapshot);
            return new(Admit<ExtensionSessionCompactionAcknowledgment?>(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                    context.OperationCancellationToken, context.ExtensionLifetimeCancellationToken, SessionCancellationToken);
                var receipt = await host.CompactAsync(attachment, new(ExtensionSummary: new(summary.Text, summary.Usage, summary.Details),
                    OverrideRetainedBoundary: true, FirstKeptEntryId: summary.FirstKeptEntryId), NeverGenerateSummary.Instance, linked.Token,
                    async (preview, token) =>
                    {
                        var prospective = new ExtensionSessionSnapshot(Snapshot.SessionId, Snapshot.Generation, preview.Context.LeafId,
                            preview.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray())
                            { Persistence = Persistence(preview.PreviousLog.StorageDurability) };
                        await validateProspectiveSnapshot(prospective, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                if (receipt is null) return null;
                var checkpoint = new ExtensionSessionEntryAcknowledgment(Snapshot.SessionId, Snapshot.Generation,
                    receipt.Entry.WireBody, receipt.Append.Sequence, receipt.Append.ByteOffset, receipt.Append.ByteLength, receipt.Context.LeafId)
                    { Persistence = Persistence(receipt.Append.Snapshot.StorageDurability) };
                return new(checkpoint, new ExtensionSessionSnapshot(Snapshot.SessionId, Snapshot.Generation, receipt.Context.LeafId,
                    receipt.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray()) { Persistence = checkpoint.Persistence });
            }));
        }
        private sealed class NeverGenerateSummary : ISessionSummaryGenerator
        {
            internal static NeverGenerateSummary Instance { get; } = new();
            public ValueTask<SessionGeneratedSummary> GenerateAsync(PiSharp.Sessions.Compaction.SessionSummaryRequest request, CancellationToken token)
                => throw new InvalidOperationException("An extension-provided summary must not start a provider request.");
        }
        public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string absolutePath, bool useLatestLeaf,
            string? selectedLeafId, CancellationToken cancellationToken)
            => SwitchCoreAsync(absolutePath, useLatestLeaf, selectedLeafId, null, cancellationToken);
        public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string absolutePath, bool useLatestLeaf,
            string? selectedLeafId, Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateStagedSnapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(validateStagedSnapshot);
            return SwitchCoreAsync(absolutePath, useLatestLeaf, selectedLeafId, validateStagedSnapshot, cancellationToken);
        }
        private ValueTask<IExtensionSessionActionScope?> SwitchCoreAsync(string absolutePath, bool useLatestLeaf,
            string? selectedLeafId, Func<ExtensionSessionSnapshot, CancellationToken, ValueTask>? validateStagedSnapshot,
            CancellationToken cancellationToken)
        {
            if (context is not IExtensionCommandContext) throw new InvalidOperationException("Only command contexts can replace sessions.");
            return new(Admit<IExtensionSessionActionScope?>(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                    context.OperationCancellationToken, context.ExtensionLifetimeCancellationToken, SessionCancellationToken);
                var replaced = await host.SwitchAsync(attachment, new(absolutePath, useLatestLeaf, selectedLeafId),
                    async (previous, target, token) =>
                    {
                        if (validateStagedSnapshot is not null)
                            await validateStagedSnapshot(NativeSessionSnapshotProvider.Snapshot(target, checked(attachment.Generation + 1)), token).ConfigureAwait(false);
                        var veto = provider.BeforeSwitch ?? host.BeforeReplacement;
                        return veto is null || await veto(previous, target, token).ConfigureAwait(false);
                    }, provider.AfterSwitch, linked.Token).ConfigureAwait(false);
                if (replaced is null) return null;
                return new Scope(provider, host, replaced.Current, context, NativeSessionSnapshotProvider.Snapshot(replaced.Current), baseSessionToken);
            }));
        }
        public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request,
            CancellationToken cancellationToken)
            => CreateCoreAsync(request, null, cancellationToken);
        public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request,
            Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateStagedSnapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(validateStagedSnapshot);
            return CreateCoreAsync(request, validateStagedSnapshot, cancellationToken);
        }
        private ValueTask<ExtensionSessionCreationScopeResult?> CreateCoreAsync(ExtensionSessionCreationRequest request,
            Func<ExtensionSessionSnapshot, CancellationToken, ValueTask>? validateStagedSnapshot, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (context is not IExtensionCommandContext) throw new InvalidOperationException("Only command contexts can create sessions.");
            if (!Enum.IsDefined(request.Kind)) throw new ArgumentException("Session creation kind is invalid.", nameof(request));
            if (request.Setup?.GetInvocationList().Length > 1) throw new ArgumentException("One portable setup callback original required.");
            return new(Admit<ExtensionSessionCreationScopeResult?>(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                    context.OperationCancellationToken, context.ExtensionLifetimeCancellationToken, SessionCancellationToken);
                var nativeRequest = new AgentSessionCreationRequest((AgentSessionCreationKind)request.Kind, request.EntryId, request.ParentSession);
                var replaced = request.Setup is { } setup
                    ? await host.CreateWithSetupAsync(attachment, nativeRequest,
                        (manager, setupToken) => setup(new NativeSessionSetupManagerAdapter(manager), setupToken), linked.Token,
                        validateStagedSnapshot is null ? null : (target, validationToken) =>
                            validateStagedSnapshot(NativeSessionSnapshotProvider.Snapshot(target, checked(attachment.Generation + 1)), validationToken)).ConfigureAwait(false)
                    : await host.CreateAsync(attachment, nativeRequest,
                        validateStagedSnapshot is null ? null : (target, _, preflightToken) =>
                            validateStagedSnapshot(NativeSessionSnapshotProvider.Snapshot(target, checked(attachment.Generation + 1)), preflightToken),
                        linked.Token).ConfigureAwait(false);
                if (replaced is null) return null;
                return new(new Scope(provider, host, replaced.Current, context,
                    NativeSessionSnapshotProvider.Snapshot(replaced.Current), baseSessionToken), replaced.SelectedText);
            }));
        }
        public ValueTask<ExtensionSessionCatalogPage> ListAsync(ExtensionSessionCatalogQuery query,
            CancellationToken cancellationToken) => new(Admit(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                context.OperationCancellationToken, context.ExtensionLifetimeCancellationToken, SessionCancellationToken);
            var page = await host.ListSessionsAsync(attachment, new(query.PageSize, query.Cursor, query.WorkingDirectory), linked.Token).ConfigureAwait(false);
            return new ExtensionSessionCatalogPage(page.Items.Select(item => new ExtensionSessionCatalogItem(item.Key,
                item.StoreId, item.FileName, item.Path, item.SessionId, item.CreatedTimestamp, item.WorkingDirectory,
                item.CwdGroup, item.ParentSessionPath, item.FileBytes, new DateTime(item.ModifiedUtcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
                string.Equals(item.Path, attachment.Session.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))).ToImmutableArray(),
                page.NextCursor, page.SkippedFiles, page.UnavailableStores);
        }));
        public ValueTask<IExtensionSessionCatalogScope?> ResumeAsync(string catalogKey, bool useLatestLeaf,
            string? selectedLeafId, CancellationToken cancellationToken)
            => ResumeCoreAsync(catalogKey, useLatestLeaf, selectedLeafId, null, cancellationToken);
        public ValueTask<IExtensionSessionCatalogScope?> ResumeAsync(string catalogKey, bool useLatestLeaf,
            string? selectedLeafId, Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateStagedSnapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(validateStagedSnapshot);
            return ResumeCoreAsync(catalogKey, useLatestLeaf, selectedLeafId, validateStagedSnapshot, cancellationToken);
        }
        private ValueTask<IExtensionSessionCatalogScope?> ResumeCoreAsync(string catalogKey, bool useLatestLeaf,
            string? selectedLeafId, Func<ExtensionSessionSnapshot, CancellationToken, ValueTask>? validateStagedSnapshot,
            CancellationToken cancellationToken)
        {
            if (context is not IExtensionCommandContext) throw new InvalidOperationException("Only command contexts can resume sessions.");
            return new(Admit<IExtensionSessionCatalogScope?>(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                    context.OperationCancellationToken, context.ExtensionLifetimeCancellationToken, SessionCancellationToken);
                var replaced = await host.ResumeAsync(attachment, new(catalogKey, useLatestLeaf, selectedLeafId),
                    validateStagedSnapshot is null ? null : (target, preflightToken) =>
                        validateStagedSnapshot(NativeSessionSnapshotProvider.Snapshot(target, checked(attachment.Generation + 1)), preflightToken),
                    cancellationToken: linked.Token, beforeResume: provider.BeforeSwitch, afterResume: provider.AfterSwitch).ConfigureAwait(false);
                if (replaced is null) return null;
                return new Scope(provider, host, replaced.Current, context, NativeSessionSnapshotProvider.Snapshot(replaced.Current), baseSessionToken);
            }));
        }
        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? completion = null; Task[] work = [];
            lock (gate)
            {
                if (disposal is null)
                { closed = true; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); disposal = completion.Task; work = pending.ToArray(); }
            }
            if (completion is not null) _ = CloseAsync(work, completion);
            return new(disposal);
        }
        private async Task CloseAsync(Task[] work, TaskCompletionSource completion)
        {
            Exception? failure = null;
            try { lifetime.Cancel(); } catch (Exception error) { failure = error; }
            // Action failures are delivered to the caller; this join owns their physical cleanup.
            foreach (var task in work) try { await task.ConfigureAwait(false); } catch (Exception) { }
            lifetime.Dispose();
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
    }
}
