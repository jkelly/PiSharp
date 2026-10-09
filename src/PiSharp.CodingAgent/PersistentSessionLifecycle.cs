using System.Collections.Immutable;
using System.Globalization;
using PiSharp.Contracts;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Import;

namespace PiSharp.CodingAgent;

public enum AgentSessionCreationKind { New, ForkBefore, ForkAt, Clone }
public sealed record AgentSessionCreationRequest(AgentSessionCreationKind Kind, string? EntryId = null, string? ParentSession = null);

/// <summary>One explicitly configured lifecycle service for local, lazy local or memory sessions.
/// It owns no default home or provider. Destinations are fresh siblings in the attached backend namespace.</summary>
public sealed class PersistentSessionLifecycle
{
    /// <summary>The attachments (session switches) and reload attempts an owner this lifecycle attaches admits. A host following Pi,
    /// whose agent-session.ts switches and reloads any number of times in one process, sets int.MaxValue.</summary>
    public int MaximumOwnerAttachments { get; set; } = ReplaceableAgentSession.MaximumAttachments;
    /// <summary>The owned resources (MCP servers, extension bindings) such an owner admits at once; int.MaxValue follows Pi.</summary>
    public int MaximumOwnedResources { get; set; } = ReplaceableAgentSession.MaximumOwnedResources;
    private readonly SessionRuntimeRegistry registry;
    private readonly Func<long> clock;
    private readonly Func<string> nextEntryId;
    private readonly Func<string> nextSessionId;
    private readonly PersistentAgentSessionOptions options;
    private readonly SessionBranchPlanner planner;
    private readonly SessionBranchPublisher publisher;
    private readonly Func<string, SessionRuntimeRegistry>? registryForWorkingDirectory;
    private readonly Func<string, CancellationToken, ValueTask<SessionRuntimeLease>>? runtimeForWorkingDirectory;
    private readonly Func<string, long, CancellationToken, ValueTask<SessionRuntimeLease>>? runtimeForAttachment;
    public SessionCatalog? Catalog { get; }
    public bool RebindsWorkingDirectory => registryForWorkingDirectory is not null || runtimeForWorkingDirectory is not null || runtimeForAttachment is not null;
    public PersistentAgentSessionOptions Options => options;
    /// <summary>Configures a session a New creation staged (agent-session-runtime.ts newSession recreates the session through
    /// createAgentSession, which picks the default thinking level and records it) before it is attached.</summary>
    public Func<PersistentAgentSession, CancellationToken, ValueTask>? ConfigureNewSession { get; set; }
    /// <summary>The cwd new sessions record when the attached session continues outside its stored cwd (SessionManager cwdOverride).</summary>
    public string? WorkingDirectoryOverride { get; set; }
    public SessionLifecycleReadOnly ReadOnly { get; }
    public PersistentSessionLifecycle(SessionRuntimeRegistry registry, Func<long> clock, Func<string> nextEntryId,
        PersistentAgentSessionOptions? options = null, Func<string>? nextSessionId = null,
        ISessionBranchFileSystem? fileSystem = null, SessionCatalog? catalog = null,
        Func<string, SessionRuntimeRegistry>? registryForWorkingDirectory = null, SessionStorageBackend? backend = null,
        Func<string, CancellationToken, ValueTask<SessionRuntimeLease>>? runtimeForWorkingDirectory = null,
        Func<string, long, CancellationToken, ValueTask<SessionRuntimeLease>>? runtimeForAttachment = null)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(nextEntryId);
        this.registry = registry; this.clock = clock; this.nextEntryId = nextEntryId;
        this.options = (options ?? new()) with { LifetimeToolSelection = registry.LifetimeToolSelection ?? options?.LifetimeToolSelection };
        if (backend is not null)
        {
            if (fileSystem is not null && !ReferenceEquals(fileSystem, backend) ||
                this.options.SessionLogStoreOptions?.StorageFactory is { } configuredFactory && !ReferenceEquals(configuredFactory, backend))
                throw new ArgumentException("Lifecycle backend must consistently supply publication and writer storage.", nameof(backend));
            this.options = this.options with { SessionLogStoreOptions = (this.options.SessionLogStoreOptions ?? new()) with { StorageFactory = backend } };
            fileSystem = backend;
            catalog ??= new SessionCatalog([new("session-backend", backend.Directory)], fileSystem: backend);
        }
        // session-manager.ts newSession/forkFrom: a uuidv7 session id.
        this.nextSessionId = nextSessionId ?? (() => Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(clock())).ToString());
        var bounds = this.options.SessionLogStoreOptions?.ReaderOptions ?? new();
        var graph = this.options.ContextOptions ?? new();
        planner = new(new(Math.Min(100_000, Math.Min(graph.MaximumEntries, Math.Max(1, bounds.MaximumRecords - 1))),
            bounds.MaximumInputBytes, bounds.CodecOptions, graph.MaximumInputCharacters));
        publisher = new(new(bounds, graph), fileSystem);
        Catalog = catalog;
        this.registryForWorkingDirectory = registryForWorkingDirectory;
        if ((registryForWorkingDirectory is null ? 0 : 1) + (runtimeForWorkingDirectory is null ? 0 : 1) +
            (runtimeForAttachment is null ? 0 : 1) > 1)
            throw new ArgumentException("Specify either borrowed registry bindings or an owned runtime factory.");
        this.runtimeForWorkingDirectory = runtimeForWorkingDirectory;
        this.runtimeForAttachment = runtimeForAttachment;
        ReadOnly = new(new(new SessionCopyOptions(bounds, bounds.MaximumInputBytes), graph), backend, catalog);
    }
    /// <summary>Creates an initial session through the same registry factory, storage, identity and bounds
    /// as replacement. The supplied header is fully validated before its cwd is offered to the host factory.</summary>
    public async Task<PersistentAgentSession> CreateAsync(string path, SessionEntry header, ModelDescriptor initialModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header); ArgumentNullException.ThrowIfNull(initialModel);
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("Session creation requires an absolute backend path.", nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        var owned = new SessionEntryCodec(options.SessionLogStoreOptions?.ReaderOptions?.CodecOptions).Read(header.WireBody.Value);
        if (!owned.IsHeader || string.IsNullOrWhiteSpace(owned.WireBody.Value.GetProperty("cwd").GetString()))
            throw new ArgumentException("Session creation requires a valid header.", nameof(header));
        SessionRuntimeLease? runtime = null;
        try
        {
            var acquired = await AcquireRuntimeAsync(owned.WireBody.Value.GetProperty("cwd").GetString()!, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The host did not supply services for the session working directory.");
            acquired.Claim(); runtime = acquired;
            cancellationToken.ThrowIfCancellationRequested();
            var session = await PersistentAgentSession.CreateAsync(path, owned, runtime.Registry, initialModel, clock, nextEntryId,
                options with { UseLatestLeaf = true, SelectedLeafId = null }, cancellationToken).ConfigureAwait(false);
            session.OwnRuntime(runtime);
            return session;
        }
        catch (Exception admission)
        {
            if (runtime is not null) try { await runtime.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Session creation and owned cleanup failed.", admission, cleanup); }
            throw;
        }
    }
    public ReplaceableAgentSession Attach(PersistentAgentSession initial) => ReplaceableAgentSession.WithLifecycle(initial, this);
    public Task<ReplaceableAgentSession> AttachAsync(PersistentAgentSession initial) =>
        ReplaceableAgentSession.WithLifecycleAndRuntimeBindingAsync(initial, this);
    public Task<SessionCatalogPage> ListAsync(SessionCatalogQuery query, CancellationToken cancellationToken = default) =>
        ReadOnly.ListAsync(query, cancellationToken);
    /// <summary>Opens an existing session with the same explicit model/tool registry, identity generators
    /// and persistence bounds used by new/fork/clone. The caller owns the returned coordinator.</summary>
    public Task<PersistentAgentSession> OpenAsync(AgentSessionSwitchRequest request, ModelDescriptor fallbackModel,
        CancellationToken cancellationToken = default)
        => OpenForAttachmentAsync(request, fallbackModel, 1, cancellationToken);

    internal Task<PersistentAgentSession> OpenForAttachmentAsync(AgentSessionSwitchRequest request,
        ModelDescriptor fallbackModel, long generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(fallbackModel);
        if (!System.IO.Path.IsPathFullyQualified(request.Path) || string.IsNullOrWhiteSpace(request.Path) ||
            request.UseLatestLeaf && request.SelectedLeafId is not null)
            throw new ArgumentException("Session open requires an absolute path and valid branch selection.", nameof(request));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        return PersistentAgentSession.OpenWithRuntimeFactoryAsync(request.Path,
            (cwd, token) => AcquireRuntimeAsync(cwd, generation, token), clock, nextEntryId,
            options with { UseLatestLeaf = request.UseLatestLeaf, SelectedLeafId = request.SelectedLeafId }, fallbackModel, cancellationToken);
    }
    private ValueTask<SessionRuntimeLease> AcquireRuntimeAsync(string cwd, CancellationToken token)
        => AcquireRuntimeAsync(cwd, 1, token);

    private ValueTask<SessionRuntimeLease> AcquireRuntimeAsync(string cwd, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return runtimeForAttachment is not null ? runtimeForAttachment(cwd, generation, token) :
            runtimeForWorkingDirectory is not null ? runtimeForWorkingDirectory(cwd, token) :
            ValueTask.FromResult(new SessionRuntimeLease(registryForWorkingDirectory is null ? registry :
                registryForWorkingDirectory(cwd) ?? throw new InvalidOperationException("The host did not supply bindings for the session working directory.")));
    }
    internal async Task<PreparedCreation> PrepareAsync(AgentSessionAttachment previous, AgentSessionCreationRequest request,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var state = previous.Session.Snapshot;
        string? entry = request.EntryId; var position = SessionForkPosition.Before;
        if (request.Kind == AgentSessionCreationKind.Clone)
        { entry = state.Context.LeafId ?? throw new InvalidOperationException("There is no current entry to clone."); position = SessionForkPosition.At; }
        else if (request.Kind == AgentSessionCreationKind.ForkAt) position = SessionForkPosition.At;
        var labels = request.Kind == AgentSessionCreationKind.New ? 0 :
            planner.GetRequiredLabelCount(state.Log.Entries, entry!, position, token);
        token.ThrowIfCancellationRequested();
        var id = nextSessionId(); token.ThrowIfCancellationRequested();
        // Native filenames require safe generated identity components. Historical record IDs retain their
        // broader codec profile, and a malformed generator is rejected rather than normalized.
        if (string.IsNullOrEmpty(id) || id == state.Log.Header.Id || id.Length > 128 || id.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_')))
            throw new InvalidOperationException("New session identity is invalid.");
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(clock()).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        token.ThrowIfCancellationRequested();
        var labelIds = ImmutableArray.CreateBuilder<string>(labels);
        for (var index = 0; index < labels; index++) { token.ThrowIfCancellationRequested(); labelIds.Add(nextEntryId()); token.ThrowIfCancellationRequested(); }
        var plan = request.Kind == AgentSessionCreationKind.New
            ? planner.New(id, timestamp, WorkingDirectoryOverride ?? previous.Session.WorkingDirectory, request.ParentSession, token)
            : planner.Fork(new(state.Log.Header, state.Log.Entries, entry!, position, id, timestamp,
                previous.Session.SessionFile, labelIds.MoveToImmutable()), token);
        var directory = System.IO.Path.GetDirectoryName(previous.Session.Path)!;
        var path = System.IO.Path.Combine(directory, timestamp.Replace(':', '-').Replace('.', '-') + "_" + id + ".jsonl");
        PublishedSessionBranch? file = null; PersistentAgentSession? session = null;
        try
        {
            file = await publisher.PrepareAsync(plan, path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var generation = checked(previous.Generation + 1);
            session = await PersistentAgentSession.OpenWithRuntimeFactoryAsync(path,
                (cwd, cancellation) => AcquireRuntimeAsync(cwd, generation, cancellation), clock, nextEntryId,
                options with { UseLatestLeaf = true, SelectedLeafId = null }, state.Agent.Model, token).ConfigureAwait(false);
            if (request.Kind == AgentSessionCreationKind.New && ConfigureNewSession is { } configure) await configure(session, token).ConfigureAwait(false);
            var prepared = new PreparedCreation(session, file, plan.SelectedText); session = null; file = null;
            return prepared;
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
        }
    }
    internal sealed class PreparedCreation(PersistentAgentSession session, PublishedSessionBranch file, string? selectedText) : IAsyncDisposable
    {
        internal PersistentAgentSession Session { get; } = session;
        internal string? SelectedText { get; } = selectedText;
        private bool committed;
        internal void CommitAttachment() { file.CommitAttachment(); committed = true; }
        public async ValueTask DisposeAsync()
        {
            // Never delete a file while its uncommitted coordinator still owns a writer. Close failure is
            // explicit; the newly created file remains available for inspection rather than being lost.
            if (!committed) await Session.DisposeAsync().ConfigureAwait(false);
            await file.DisposeAsync().ConfigureAwait(false);
        }
    }
}
