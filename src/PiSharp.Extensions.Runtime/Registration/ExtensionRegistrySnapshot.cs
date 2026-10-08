using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;

namespace PiSharp.Extensions.Runtime;

public sealed record ExtensionRegistrationInfo(
    string OwnerId, long OwnerGeneration, string RegistrationId, string Kind, string Name);

/// <summary>Immutable tool metadata for host bindings; no executable delegate is exposed.</summary>
public sealed record ExtensionToolRegistrationInfo(string OwnerId, long OwnerGeneration,
    string RegistrationId, string Name, string Description, JsonData Parameters)
{
    public bool HasInitialArgumentPreparation { get; init; }
    public ToolExposure Exposure { get; init; } = ToolExposure.Direct;
    public ToolNamespace? Namespace { get; init; }
    public bool DefaultActive { get; init; } = true;
    public bool HasLoadoutPreparation { get; init; }
    public ImmutableArray<string> PromptGuidelines { get; init; } = [];
    /// <summary>The descriptor's constrainedSampling, written into the model-facing declaration.</summary>
    public JsonData? ConstrainedSampling { get; init; }
}

public sealed record ExtensionCommandRegistrationInfo(string OwnerId, long OwnerGeneration,
    string RegistrationId, string Name, string Description, bool HasArgumentCompletions, string? SourcePath);

/// <summary>An immutable view. Dispatch additionally checks that its owner generation is still admitted.</summary>
public sealed class ExtensionRegistrySnapshot
{
    public long Revision { get; }
    public ImmutableArray<ExtensionRegistrationInfo> Registrations { get; }
    public ImmutableArray<ExtensionToolRegistrationInfo> Tools { get; }
    public ImmutableArray<ExtensionCommandRegistrationInfo> Commands { get; }
    public JsonData CommandCatalog { get; }
    public ImmutableArray<ExtensionRegistrationInfo> BeforeAgentStartHandlers { get; }
    public ImmutableArray<ExtensionRegistrationInfo> ContextHandlers { get; }
    public ImmutableArray<ExtensionRegistrationInfo> ContextWithSystemHandlers { get; }
    public ImmutableArray<ExtensionRegistrationInfo> InputHandlers { get; }
    public ImmutableArray<ExtensionRegistrationInfo> ToolCallHandlers { get; }
    public ImmutableArray<ExtensionRegistrationInfo> ToolResultHandlers { get; }
    /// <summary>Tool renderer resolvers in consultation order (extension load order, then registration order).</summary>
    public ImmutableArray<ExtensionRegistrationInfo> ToolRenderers { get; }
    internal object RegistryIdentity { get; }
    internal ImmutableArray<RegistrationEntry> Entries { get; }

    internal ExtensionRegistrySnapshot(object identity, long revision, ImmutableArray<RegistrationEntry> entries)
    {
        RegistryIdentity = identity;
        Revision = revision;
        Entries = entries;
        Registrations = entries.Where(entry => entry.Kind != RegistrationKind.EventBus).Select(entry => new ExtensionRegistrationInfo(entry.OwnerId,
            entry.OwnerGeneration, entry.RegistrationId, entry.Kind.ToString(), entry.Name)).ToImmutableArray();
        BeforeAgentStartHandlers = Registrations.Where(row => row.Kind == nameof(RegistrationKind.BeforeAgentStartHandler)).ToImmutableArray();
        ContextHandlers = Registrations.Where(row => row.Kind == nameof(RegistrationKind.ContextHandler)).ToImmutableArray();
        ContextWithSystemHandlers = Registrations.Where(row => row.Kind == nameof(RegistrationKind.ContextWithSystemHandler)).ToImmutableArray();
        InputHandlers = Registrations.Where(row => row.Kind == nameof(RegistrationKind.InputHandler)).ToImmutableArray();
        ToolCallHandlers = Registrations.Where(row => row.Kind == nameof(RegistrationKind.ToolCallHandler)).ToImmutableArray();
        ToolResultHandlers = Registrations.Where(row => row.Kind == nameof(RegistrationKind.ToolResultHandler)).ToImmutableArray();
        Commands = entries.Where(entry => entry.Kind == RegistrationKind.Command).Select(entry =>
        {
            var descriptor = (ExtensionCommandDescriptor)entry.Descriptor;
            return new ExtensionCommandRegistrationInfo(entry.OwnerId, entry.OwnerGeneration, entry.RegistrationId,
                entry.Name, descriptor.Description, descriptor.GetArgumentCompletionsAsync is not null, descriptor.SourcePath);
        }).ToImmutableArray();
        CommandCatalog = JsonData.Parse(JsonSerializer.Serialize(Commands.Select(command => new
        {
            name = command.Name, description = command.Description, source = "extension",
            sourceInfo = new { source = "extension", path = command.SourcePath },
            ownerId = command.OwnerId, ownerGeneration = command.OwnerGeneration, registrationId = command.RegistrationId
        })));
        Tools = entries.Where(entry => entry.Kind == RegistrationKind.Tool).Select(entry =>
        {
            var descriptor = (ExtensionToolDescriptor)entry.Descriptor;
            return new ExtensionToolRegistrationInfo(entry.OwnerId, entry.OwnerGeneration,
                entry.RegistrationId, entry.Name, descriptor.Description, descriptor.Parameters)
                { HasInitialArgumentPreparation = descriptor.PrepareInitialArgumentsAsync is not null,
                    Exposure = descriptor.Exposure, Namespace = descriptor.Namespace, DefaultActive = descriptor.DefaultActive,
                    HasLoadoutPreparation = descriptor.PrepareLoadout is not null, PromptGuidelines = descriptor.PromptGuidelines,
                    ConstrainedSampling = descriptor.ConstrainedSampling };
        }).ToImmutableArray();
        ToolRenderers = Registrations.Where(row => row.Kind == nameof(RegistrationKind.ToolRenderer)).ToImmutableArray();
    }
}

internal class ExtensionContext : IExtensionUiContext, IExtensionSessionActionsContext, IExtensionSessionCatalogContext,
    IExtensionSessionContextEditContext, IExtensionSessionCompactionContext, IExtensionToolActivationContext
{
    public IExtensionUi Ui { get; private set; } = UnavailableExtensionUiScope.Default;
    internal void SetUi(IExtensionUi ui) => Ui = ui;
    public ExtensionSessionSnapshot? SessionSnapshot { get; private set; }
    internal void SetSessionSnapshot(ExtensionSessionSnapshot snapshot) => SessionSnapshot = snapshot;
    public string OwnerId { get; }
    public long OwnerGeneration { get; }
    public CancellationToken OperationCancellationToken { get; }
    public CancellationToken SessionCancellationToken { get; private set; }
    internal CancellationToken BaseSessionCancellationToken { get; }
    internal RegistrationScope Scope { get; }
    internal IExtensionSessionActionScope? SessionActions { get; private set; }
    internal void SetSessionActions(IExtensionSessionActionScope actions)
    { SessionActions = actions; SessionCancellationToken = actions.SessionCancellationToken; }
    public ImmutableArray<string> GetActiveTools()
    {
        CheckCatalogCancellation(default);
        return SessionActions is IExtensionSessionToolActivationScope activation ? activation.GetActiveTools()
            : throw new InvalidOperationException("Callback tool activation is unavailable from this host.");
    }
    public ExtensionToolActivationSelection SetActiveTools(ImmutableArray<string> names)
    {
        CheckCatalogCancellation(default);
        (validateActivationInput ?? throw new InvalidOperationException("Callback tool activation is unavailable from this host."))(names);
        return SessionActions is IExtensionSessionToolActivationScope activation ? activation.SetActiveTools(names)
            : throw new InvalidOperationException("Callback tool activation is unavailable from this host.");
    }
    private Action<ImmutableArray<string>>? validateActivationInput;
    internal void ConfigureToolActivation(Action<ImmutableArray<string>> validate) => validateActivationInput = validate;
    public ValueTask<ExtensionSessionEntryAcknowledgment> AppendSessionEntryAsync(string entryKind, int schemaVersion,
        JsonData data, CancellationToken cancellationToken = default) => SessionActions?.AppendAsync(entryKind, schemaVersion, data,
            cancellationToken) ?? throw new InvalidOperationException("Durable session entries are unavailable from this host.");
    public CancellationToken ExtensionLifetimeCancellationToken { get; }
    private Action<string, JsonData?>? validateContextEditInput;
    private Func<ExtensionSessionSnapshot, CancellationToken, ExtensionSessionSnapshot>? ownContextEditSnapshot;
    private Action<ExtensionSessionCompactionSummary>? validateCompactionInput;
    private Func<ExtensionSessionSnapshot, CancellationToken, ExtensionSessionSnapshot>? ownCompactionSnapshot;
    internal void ConfigureCompaction(Action<ExtensionSessionCompactionSummary> validate,
        Func<ExtensionSessionSnapshot, CancellationToken, ExtensionSessionSnapshot> own)
    { validateCompactionInput = validate; ownCompactionSnapshot = own; }
    public async ValueTask<ExtensionSessionCompactionAcknowledgment?> AppendCompactionSummaryAsync(
        ExtensionSessionCompactionSummary summary, CancellationToken cancellationToken = default)
    {
        if (SessionActions is not IExtensionSessionCompactionScope summaries || validateCompactionInput is null || ownCompactionSnapshot is null)
            throw new InvalidOperationException("Extension summaries are unavailable from this host.");
        CheckCatalogCancellation(cancellationToken); validateCompactionInput(summary);
        ExtensionSessionSnapshot? admitted = null;
        var receipt = await summaries.AppendCompactionSummaryAsync(summary, (staged, token) =>
        { admitted = ownCompactionSnapshot(staged, token); return ValueTask.CompletedTask; }, cancellationToken).ConfigureAwait(false);
        if (receipt is null) return null;
        try
        {
            if (admitted is null || receipt.Checkpoint is null || receipt.Snapshot is null ||
                admitted.SessionId != receipt.Checkpoint.SessionId || admitted.Generation != receipt.Checkpoint.Generation ||
                admitted.SelectedLeafId != receipt.Checkpoint.SelectedLeafId || admitted.BranchEntries.IsDefaultOrEmpty ||
                admitted.BranchEntries[^1].ToString() != receipt.Checkpoint.Entry.ToString() ||
                receipt.Snapshot.SessionId != admitted.SessionId || receipt.Snapshot.Generation != admitted.Generation ||
                receipt.Snapshot.SelectedLeafId != admitted.SelectedLeafId || receipt.Snapshot.BranchEntries.IsDefault ||
                !receipt.Snapshot.BranchEntries.Select(row => row.ToString()).SequenceEqual(admitted.BranchEntries.Select(row => row.ToString())) ||
                receipt.Snapshot.Persistence != receipt.Checkpoint.Persistence)
                throw new InvalidOperationException("Summary returned an inconsistent checkpoint/view.");
            return new(receipt.Checkpoint, admitted with { Persistence = receipt.Checkpoint.Persistence });
        }
        catch (Exception error) when (receipt.Checkpoint is not null)
        { throw new ExtensionSessionCompactionCommittedException(receipt.Checkpoint, error); }
    }
    internal void ConfigureContextEdits(Action<string, JsonData?> validate,
        Func<ExtensionSessionSnapshot, CancellationToken, ExtensionSessionSnapshot> own)
    { validateContextEditInput = validate; ownContextEditSnapshot = own; }
    public async ValueTask<ExtensionSessionContextEditAcknowledgment> AppendContextEditAsync(string targetId,
        JsonData? replacement, CancellationToken cancellationToken = default)
    {
        if (SessionActions is not IExtensionSessionContextEditScope edits || validateContextEditInput is null || ownContextEditSnapshot is null)
            throw new InvalidOperationException("Context edits are unavailable from this host.");
        CheckCatalogCancellation(cancellationToken);
        validateContextEditInput(targetId, replacement);
        ExtensionSessionSnapshot? admitted = null;
        var receipt = await edits.AppendContextEditAsync(targetId, replacement, (staged, token) =>
        {
            admitted = ownContextEditSnapshot(staged, token);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            if (receipt is null || receipt.Checkpoint is null || admitted is null || receipt.Snapshot is null ||
                admitted.SessionId != receipt.Checkpoint.SessionId || admitted.Generation != receipt.Checkpoint.Generation ||
                admitted.SelectedLeafId != receipt.Checkpoint.SelectedLeafId || admitted.BranchEntries.IsDefaultOrEmpty ||
                receipt.Checkpoint.Entry is null || admitted.BranchEntries[^1].ToString() != receipt.Checkpoint.Entry.ToString() ||
                receipt.Snapshot.SessionId != admitted.SessionId || receipt.Snapshot.Generation != admitted.Generation ||
                receipt.Snapshot.SelectedLeafId != admitted.SelectedLeafId || receipt.Snapshot.BranchEntries.IsDefault ||
                !receipt.Snapshot.BranchEntries.Select(row => row.ToString()).SequenceEqual(admitted.BranchEntries.Select(row => row.ToString())) ||
                receipt.Snapshot.Persistence != receipt.Checkpoint.Persistence)
                throw new InvalidOperationException("Context edit returned an inconsistent checkpoint/view.");
            // No post-effect cancellation rejection. The actual acknowledged checkpoint remains observable.
            return new(receipt.Checkpoint, admitted with { Persistence = receipt.Checkpoint.Persistence });
        }
        catch (Exception error) when (receipt?.Checkpoint is not null)
        { throw new ExtensionSessionContextEditCommittedException(receipt.Checkpoint, error); }
    }
    public async ValueTask<ExtensionSessionCatalogPage> ListSessionsAsync(ExtensionSessionCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageSize is < 1 or > 128 || query.Cursor is { Length: > 32768 } ||
            query.WorkingDirectory is { Length: > 4096 } || query.Cursor is { } cursor && !RegistrationPolicy.Scalars(cursor) ||
            query.WorkingDirectory is { } cwd && !RegistrationPolicy.Scalars(cwd))
            throw new ArgumentException("Session catalog query is invalid.", nameof(query));
        if (SessionActions is not IExtensionSessionCatalogScope catalog)
            throw new InvalidOperationException("Session discovery is unavailable from this host.");
        CheckCatalogCancellation(cancellationToken);
        var page = await catalog.ListAsync(query, cancellationToken).ConfigureAwait(false);
        CheckCatalogCancellation(cancellationToken);
        if (page is null || page.Items.IsDefault || page.Items.Length > query.PageSize || page.SkippedFiles < 0 || page.UnavailableStores < 0 ||
            page.NextCursor is { Length: > 32768 }) throw new InvalidOperationException("Session catalog exceeded its bounded view.");
        long characters = page.NextCursor?.Length ?? 0, bytes = page.NextCursor is null ? 0 : Encoding.UTF8.GetByteCount(page.NextCursor);
        var items = ImmutableArray.CreateBuilder<ExtensionSessionCatalogItem>(page.Items.Length);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in page.Items)
        {
            if (item is null || item.Key is not { Length: 64 } || item.Key.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                item.FileBytes < 0 || !keys.Add(item.Key) || string.IsNullOrEmpty(item.StoreId) || string.IsNullOrEmpty(item.FileName) ||
                string.IsNullOrEmpty(item.Path) || string.IsNullOrEmpty(item.SessionId) || item.CreatedTimestamp is null ||
                item.WorkingDirectory is null || item.CwdGroup is null || item.ModifiedUtc is null)
                throw new InvalidOperationException("Session catalog item is invalid.");
            foreach (var text in new[] { item.Key, item.StoreId, item.FileName, item.Path, item.SessionId, item.CreatedTimestamp,
                item.WorkingDirectory, item.CwdGroup, item.ParentSessionPath, item.ModifiedUtc })
            {
                if (text is null) continue;
                if (text.Length > 8192 || !RegistrationPolicy.Scalars(text)) throw new InvalidOperationException("Session catalog item exceeded its bounded view.");
                characters += text.Length;
                bytes += Encoding.UTF8.GetByteCount(text);
            }
            if (characters > 1_048_576 || bytes > 1_048_576) throw new InvalidOperationException("Session catalog exceeded its bounded view.");
            items.Add(item);
        }
        if (page.NextCursor is { } next && !RegistrationPolicy.Scalars(next)) throw new InvalidOperationException("Session catalog cursor is invalid.");
        CheckCatalogCancellation(cancellationToken);
        return page with { Items = items.MoveToImmutable() };
    }
    private void CheckCatalogCancellation(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); OperationCancellationToken.ThrowIfCancellationRequested();
        SessionCancellationToken.ThrowIfCancellationRequested(); ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
    }

    internal ExtensionContext(RegistrationScope scope, CancellationToken operation, CancellationToken session)
    {
        OwnerId = scope.OwnerId;
        Scope = scope;
        OwnerGeneration = scope.OwnerGeneration;
        OperationCancellationToken = operation;
        SessionCancellationToken = session;
        BaseSessionCancellationToken = session;
        ExtensionLifetimeCancellationToken = scope.ExtensionLifetimeCancellationToken;
    }
}

internal class ExtensionToolContext(RegistrationScope scope, CancellationToken operation, CancellationToken session)
    : ExtensionContext(scope, operation, session), IExtensionToolContext { }

/// <summary>One leased native invocation and one retained actual update-delivery receipt.</summary>
internal sealed class ExtensionToolInvocationContext(RegistrationScope scope, CancellationToken operation,
    CancellationToken session, string toolCallId, ExtensionToolUpdateCallback update, Func<JsonData, bool> validateUpdate,
    ExtensionToolBroker? broker = null)
    : ExtensionToolContext(scope, operation, session), IExtensionToolInvocationContext, IExtensionToolContext
{
    private readonly object gate = new();
    private ExtensionToolUpdateCallback? publish = update;
    private Func<JsonData, bool>? validate = validateUpdate;
    private TaskCompletionSource? lastDelivery;
    private Exception? failure;
    private bool active, closed;
    public string ToolCallId { get; } = toolCallId;
    public string? ParentToolCallId => broker?.ParentToolCallId;
    public long SessionGeneration => broker?.SessionGeneration ?? 0;
    public int CallDepth => broker?.CallDepth ?? 0;
    public ImmutableArray<string> Tools => broker?.Tools ?? [];
    private readonly List<NestedDelivery> nested = [];
    private sealed class NestedDelivery
    {
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ExtensionToolCallOutcome>? Work { get; set; }
    }
    public ValueTask<ExtensionToolCallOutcome> ExecuteToolAsync(string name, JsonData arguments,
        ExtensionExecuteToolOptions? options = null)
    {
        NestedDelivery delivery;
        lock (gate)
        {
            if (closed || broker is null || nested.Count >= 4096 ||
                options?.OnUpdate is { } onUpdate && onUpdate.GetInvocationList().Length != 1)
                return ValueTask.FromResult(ExtensionToolCallOutcome.Unavailable(name));
            nested.Add(delivery = new());
        }
        delivery.Work = DispatchNestedAsync(name, arguments, options ?? new());
        delivery.Published.TrySetResult();
        return new(delivery.Work!);
    }
    private async Task<ExtensionToolCallOutcome> DispatchNestedAsync(string name, JsonData arguments,
        ExtensionExecuteToolOptions options)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(OperationCancellationToken,
                SessionCancellationToken, ExtensionLifetimeCancellationToken, options.CancellationToken);
            return await broker!.ExecuteToolAsync(name, arguments, options with { CancellationToken = linked.Token }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { return new(null, name, JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"Nested tool invocation was cancelled.\"}],\"isError\":true}"), true, ToolCallId); }
        catch (Exception)
        { return ExtensionToolCallOutcome.Unavailable(name); }
    }
    internal void StopInvocationAdmission() { lock (gate) closed = true; }

    public ValueTask ReportUpdateAsync(JsonData partialResult, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource delivery;
        ExtensionToolUpdateCallback target;
        lock (gate)
        {
            if (closed) throw new ExtensionRegistrationException(ExtensionRegistrationFailure.InactiveScope, OwnerId, "tool-update");
            OperationCancellationToken.ThrowIfCancellationRequested(); SessionCancellationToken.ThrowIfCancellationRequested();
            ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (active || failure is not null)
                    throw new ExtensionRegistrationException(ExtensionRegistrationFailure.LimitExceeded, OwnerId, "tool-update");
                if (!validate!(partialResult))
                    throw new ExtensionRegistrationException(ExtensionRegistrationFailure.InvalidDescriptor, OwnerId, "tool-update");
            }
            catch (Exception error) { failure ??= error; throw; }
            active = true; lastDelivery = delivery = new(TaskCreationOptions.RunContinuationsAsynchronously); target = publish!;
        }
        _ = DeliverAsync(delivery, target, partialResult, cancellationToken);
        return new(delivery.Task);
    }

    private async Task DeliverAsync(TaskCompletionSource delivery, ExtensionToolUpdateCallback target,
        JsonData partialResult, CancellationToken token)
    {
        Exception? error = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                OperationCancellationToken, SessionCancellationToken, ExtensionLifetimeCancellationToken, token);
            await target(partialResult, linked.Token).ConfigureAwait(false);
        }
        catch (Exception caught) { error = caught; lock (gate) failure ??= caught; }
        lock (gate) active = false;
        if (error is null) delivery.TrySetResult(); else delivery.TrySetException(error);
    }

    internal async ValueTask CloseUpdatesAsync()
    {
        Task? joined;
        NestedDelivery[] children;
        lock (gate) { closed = true; joined = lastDelivery?.Task; children = nested.ToArray(); }
        await Task.WhenAll(children.Select(JoinNested)).ConfigureAwait(false);
        if (joined is not null)
            try { await joined.ConfigureAwait(false); } catch (Exception error) { lock (gate) failure ??= error; }
        Exception? errorToThrow;
        lock (gate) { publish = null; validate = null; lastDelivery = null; nested.Clear(); errorToThrow = failure; }
        if (errorToThrow is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errorToThrow).Throw();
        static async Task JoinNested(NestedDelivery child)
        { await child.Published.Task.ConfigureAwait(false); await child.Work!.ConfigureAwait(false); }
    }
}

internal sealed partial class ExtensionCommandContext(RegistrationScope scope, CancellationToken operation, CancellationToken session,
    ExtensionRegistrySnapshot captured)
    : ExtensionContext(scope, operation, session), IExtensionCommandCatalogContext, IExtensionSessionCatalogCommandContext, IExtensionFacadeHostContext, IExtensionSessionTreeCommandContext
{
    private readonly CallbackFrame? facadeCallback = CallbackFrame.Capture(scope);
    private IExtensionContextReadHost? facadeReadHost;
    public IExtensionContextReadHost FacadeHost
    {
        get
        {
            var configured = GetFacadeHostForAdapter();
            return facadeReadHost ??= new AdmittedContextReadHost(this, configured, ValidateFacadeAccess);
        }
    }
    internal IExtensionContextReadHost GetFacadeHostForAdapter()
    {
        ValidateFacadeAccess();
        return Scope.Registry.FacadeHost ?? throw new NotSupportedException("This native host has not supplied facade bindings.");
    }
    internal void ValidateFacadeInvocation() => ValidateFacadeAccess();
    private void ValidateFacadeAccess()
    {
        if (facadeCallback is null || !CallbackFrame.IsExecuting(facadeCallback))
            throw new InvalidOperationException("Facade host access belongs to the exact active originating native callback.");
        OperationCancellationToken.ThrowIfCancellationRequested();
        SessionCancellationToken.ThrowIfCancellationRequested();
        ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
    }
    internal ExtensionRegistrySnapshot Captured { get; } = captured;
    internal Func<IExtensionSessionActionScope, ValueTask<IExtensionSessionCommandContext>>? CreateFreshContext { get; set; }
    internal Func<ExtensionSessionSnapshot, CancellationToken, ValueTask>? ValidateCreatedSnapshot { get; set; }
    public async ValueTask<IExtensionSessionCommandContext?> SwitchSessionAsync(string absolutePath, bool useLatestLeaf = true,
        string? selectedLeafId = null, CancellationToken cancellationToken = default)
    {
        if (SessionActions is null || CreateFreshContext is null || ValidateCreatedSnapshot is null)
            throw new InvalidOperationException("Session replacement is unavailable from this host.");
        var fresh = await SessionActions.SwitchAsync(absolutePath, useLatestLeaf, selectedLeafId, ValidateCreatedSnapshot, cancellationToken).ConfigureAwait(false);
        if (fresh is null) return null;
        try { return await CreateFreshContext(fresh).ConfigureAwait(false); }
        catch (Exception error)
        {
            var snapshot = fresh.Snapshot;
            throw new ExtensionSessionSwitchCommittedException(new(snapshot.SessionId, snapshot.Generation, snapshot.SelectedLeafId), error);
        }
    }
    public long CommandCatalogRevision => Captured.Revision;
    public async ValueTask<ExtensionSessionCreationResult?> CreateSessionAsync(ExtensionSessionCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SessionActions is not IExtensionSessionCreationScope creation || CreateFreshContext is null || ValidateCreatedSnapshot is null)
            throw new InvalidOperationException("Session creation is unavailable from this host.");
        var result = await creation.CreateAsync(request, ValidateCreatedSnapshot, cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        try
        {
            var fresh = await CreateFreshContext(result.Scope).ConfigureAwait(false);
            return new((IExtensionSessionCreationCommandContext)fresh, result.SelectedText);
        }
        catch (Exception error)
        {
            var snapshot = result.Scope.Snapshot;
            throw new ExtensionSessionCreationCommittedException(new(snapshot.SessionId, snapshot.Generation, snapshot.SelectedLeafId), error);
        }
    }
    public JsonData CommandCatalog => Captured.CommandCatalog;
    public async ValueTask<IExtensionSessionCatalogCommandContext?> ResumeSessionAsync(string catalogKey,
        bool useLatestLeaf = true, string? selectedLeafId = null, CancellationToken cancellationToken = default)
    {
        if (catalogKey is null || catalogKey.Length != 64 || catalogKey.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            useLatestLeaf && selectedLeafId is not null || selectedLeafId is { Length: > 128 } ||
            selectedLeafId is { } leaf && (leaf.Length == 0 || !RegistrationPolicy.Scalars(leaf)))
            throw new ArgumentException("Session resume request is invalid.", nameof(catalogKey));
        if (SessionActions is not IExtensionSessionCatalogScope catalog || CreateFreshContext is null || ValidateCreatedSnapshot is null)
            throw new InvalidOperationException("Session resume is unavailable from this host.");
        var result = await catalog.ResumeAsync(catalogKey, useLatestLeaf, selectedLeafId, ValidateCreatedSnapshot, cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        try { return (IExtensionSessionCatalogCommandContext)await CreateFreshContext(result).ConfigureAwait(false); }
        catch (Exception error)
        {
            var snapshot = result.Snapshot;
            throw new ExtensionSessionResumeCommittedException(new(snapshot.SessionId, snapshot.Generation, snapshot.SelectedLeafId), error);
        }
    }
}

internal sealed class CallbackFrame : IDisposable
{
    private static readonly AsyncLocal<CallbackFrame?> current = new();
    private readonly CallbackFrame? parent;
    private readonly ImmutableArray<RegistrationScope> scopes;
    private bool active = true;

    internal CallbackFrame(RegistrationScope scope)
        : this([scope]) { }

    internal CallbackFrame(ImmutableArray<RegistrationScope> scopes)
    {
        this.scopes = scopes;
        parent = current.Value;
        current.Value = this;
    }

    internal static bool IsExecuting(RegistrationScope scope) =>
        TerminalInputRegistryCleanupFrame.IsExecuting(scope) || Frames().Any(frame => frame.scopes.Any(candidate => ReferenceEquals(candidate, scope)));
    internal static bool IsExecuting(ExtensionRegistry registry) =>
        TerminalInputRegistryCleanupFrame.IsExecuting(registry) || Frames().Any(frame => frame.scopes.Any(scope => ReferenceEquals(scope.Registry, registry)));

    internal static CallbackFrame? Capture(RegistrationScope scope) =>
        Frames().FirstOrDefault(frame => frame.scopes.Any(candidate => ReferenceEquals(candidate, scope)));
    internal static bool IsExecuting(CallbackFrame captured) => Frames().Any(frame => ReferenceEquals(frame, captured));

    private static IEnumerable<CallbackFrame> Frames()
    {
        for (var frame = current.Value; frame is not null; frame = frame.parent)
            if (Volatile.Read(ref frame.active)) yield return frame;
    }

    public void Dispose()
    {
        Volatile.Write(ref active, false);
        current.Value = parent;
    }
}
