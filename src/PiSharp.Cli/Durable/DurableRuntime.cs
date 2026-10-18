// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/runtime.ts
// openDurable: one process owns the model runtime, the durable Harness on the session's SQLite file and the session lock. The view is the
// shown conversation's viewState() mirror plus the conversations, models, notices and (while the panel is open) the task graph; the
// controller applies the TUI's requests one at a time. Upstream calls listeners with setImmediate after a burst of changes; here a thread-pool
// work item does, so listeners run off the Harness's lines and must marshal to their own.
// PiSharp-only since pi-durable 1.2.0 (decision 0005; durable work list area l): a failed Session (SessionFailedException, ISession.Closed
// settling with SessionEnd.Failed) marks the view failed instead of leaving the TUI waiting, and failed compactions are reported through
// pi.live.compactionFailure instead of from the task outcome. PiSharp-only as well: a lost session lock (removed, or taken over by another
// process) fails the Session the same way; upstream's proper-lockfile onCompromised throws, which ends its process.
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Pi;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;

namespace PiSharp.Cli.Durable;

/// <summary>What <see cref="DurableRuntime.OpenAsync"/> runs in: pi's directories, the environment, the model runtime's inputs and, in tests,
/// a scripted model and short lock timings.</summary>
internal sealed record DurableRuntimeHost
{
    /// <summary>pi's agent directory (upstream <c>getAgentDir()</c>).</summary>
    public required string AgentDir { get; init; }
    /// <summary>The home directory settings paths expand against.</summary>
    public required string Home { get; init; }
    /// <summary>The process working directory, used when <see cref="OpenDurableOptions.Cwd"/> is null.</summary>
    public required string Cwd { get; init; }
    /// <summary>Reads an environment variable.</summary>
    public required Func<string, string?> GetEnvironment { get; init; }
    /// <summary>Sets an environment variable (the global <c>httpProxy</c>).</summary>
    public Action<string, string?> SetEnvironment { get; init; } = (_, _) => { };
    /// <summary>The model registry's and live routes' inputs: auth.json, models.json, the environment, HTTP handlers.</summary>
    public required LiveSessionRuntime LiveRuntime { get; init; }
    /// <summary>The directory of pi's documentation (the <c>docs</c> prompt section); null is the application's.</summary>
    public string? ApplicationDirectory { get; init; }
    /// <summary>Replaces the provider streams (tests: a scripted model); the catalog still comes from the registry.</summary>
    public IDurableModels? Streams { get; init; }
    /// <summary>The session lock's timing; null is upstream's.</summary>
    public PiLockOptions? Lock { get; init; }
    /// <summary>Opens the session's storage from its <c>session.sqlite</c> path; null is
    /// <see cref="SqliteStorage.OpenAsync(string, SqliteStorageOptions?)"/> (tests wrap it to fail on demand).</summary>
    public Func<string, Task<IStorage>>? OpenStorage { get; init; }

    /// <summary>The host of a <c>pisharp durable</c> run.</summary>
    internal static DurableRuntimeHost FromPiHost(PiHost host) => new()
    {
        AgentDir = PiPaths.AgentDirectory(host.GetEnvironment, host.Home),
        Home = host.Home,
        Cwd = host.Cwd,
        GetEnvironment = host.GetEnvironment,
        SetEnvironment = host.SetEnvironment,
        LiveRuntime = host.LiveRuntime,
        ApplicationDirectory = host.ApplicationDirectory ??
            (host.GetEnvironment("PI_PACKAGE_DIR") is { Length: > 0 } packageDir ? PiPaths.NormalizePath(packageDir, host.Home) : null),
    };
}

/// <summary>Opens a durable session (upstream <c>openDurable</c>) and the helpers the TUI shares (<c>agentOf</c>).</summary>
internal static partial class DurableRuntime
{
    internal static readonly DurableContext Context = DurableContext.Background;

    /// <summary>The agent document of a view; empty while the conversation has none (upstream <c>agentOf</c>).</summary>
    internal static JsonObjectValue AgentOf(ConversationView view) => view.Doc(Agents.AgentDoc.Definition.Kind) ?? JsonObjectValue.Empty;

    /// <summary>The model of an agent document.</summary>
    internal static ModelRef? ModelOf(JsonObjectValue agent) => agent.TryGetValue("model", out var model) ? ModelRef.FromJson(model) : null;

    /// <summary>The thinking level of an agent document; null when unset.</summary>
    internal static string? ThinkingLevelOf(JsonObjectValue agent) =>
        agent.TryGetValue("thinkingLevel", out var level) && level is JsonStringValue text ? text.Value : null;

    /// <summary>A subagent's task: the oldest user message of its conversation. The main conversation needs no title (upstream
    /// <c>firstInput</c>).</summary>
    internal static async Task<string?> FirstInputAsync(IHarness harness, ConversationId id)
    {
        if (id == ConversationId.Root) return null;
        var conversation = (await harness.ConversationAsync(id, Context).ConfigureAwait(false))!;
        EntryRecord? first = null;
        JsonObjectValue? cursor = null;
        do
        {
            var page = await conversation.EntriesAsync(new EntryQueryBounds(), 256, cursor, Context).ConfigureAwait(false);
            first = page.Items.LastOrDefault(entry => entry.Kind == "pi.user") ?? first;
            cursor = page.Next;
        } while (cursor is not null);
        return TitleOf(first);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The text of a user entry, as a one-line title (upstream <c>titleOf</c>).</summary>
    internal static string? TitleOf(EntryRecord? entry)
    {
        if (entry?.Model is not { Count: > 0 } messages || messages[0] is not JsonObjectValue message ||
            !(message.TryGetValue("role", out var role) && role is JsonStringValue { Value: "user" })) return null;
        var text = message.TryGetValue("content", out var content) ? content switch
        {
            JsonStringValue value => value.Value,
            JsonArrayValue blocks => string.Join(" ", blocks.OfType<JsonObjectValue>().Where(block =>
                block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" }).Select(block =>
                block.TryGetValue("text", out var value) && value is JsonStringValue part ? part.Value : "")),
            _ => "",
        } : "";
        return Whitespace().Replace(text, " ").Trim();
    }

    /// <summary>Opens the newest or a new session for the working directory (upstream <c>openDurable</c>).</summary>
    internal static async Task<IOpenDurable> OpenAsync(OpenDurableOptions options, DurableRuntimeHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        var location = await DurableSessions.SelectAsync(options.Cwd ?? host.Cwd, options.ContinueSession, host.AgentDir, host.Lock,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var envs = new DurableExecutionEnvs(location.Cwd);
        IHarness? harness = null;
        DurableModels? models = null;
        DurableSession session;
        try
        {
            var settings = PiSettings.Load(location.Cwd, host.AgentDir, projectTrusted: true);
            // The durable agent loads no extensions, so the built-in llama.cpp extension's provider is not registered.
            var runtime = DurableHarnessSetup.ConfigureHarnessHttp(settings, host.LiveRuntime with { LlamaProvider = () => false },
                host.GetEnvironment, host.SetEnvironment);
            models = await DurableModels.CreateAsync(runtime, host.Streams, cancellationToken).ConfigureAwait(false);
            var registry = DurableHarnessSetup.CreateCodingRegistry(settings, location.Cwd,
                new DurablePromptResources(host.AgentDir, host.Home, PiSystemPrompt.Documentation(host.ApplicationDirectory)));
            registry.Install(DurableSubagent.Extension);
            // PiSharp-only: image and video generation and embeddings, when a provider with media models is configured.
            if (await DurableMedia.CreateAsync(settings, models, cancellationToken).ConfigureAwait(false) is { } media)
                registry.Install(PiSharp.Durable.Tools.MediaTools.CreateExtension(media));
            var harnessSettings = DurableHarnessSetup.CreateHarnessSettings(settings, registry);

            var reports = new DurableReports();
            // A lost lock means another process may own the session now: nothing more may be committed to it (PiSharp-only; upstream's
            // proper-lockfile onCompromised throws and ends the process). Before the Harness exists the guarded storage refuses commits;
            // once it does, the loss fails its Session at once, which closes it and fails the view as a storage error does.
            location.ThrowIfLockLost();
            var storage = new LockGuardedStorage(host.OpenStorage is { } open
                ? await open(location.Database).ConfigureAwait(false)
                : await SqliteStorage.OpenAsync(location.Database).ConfigureAwait(false), location);
            harness = await Harness.OpenAsync(storage, new HarnessOptions
            {
                Models = models,
                Registry = registry,
                Settings = harnessSettings,
                Env = envs.EnvAsync,
                OnReport = reports.Report,
            }, Context).ConfigureAwait(false);
            location.OnLockLost(((Session)harness).Fail);
            var initial = location.Created ? DurableHarnessSetup.FindInitialAgentModel(settings, models) : null;
            // PiSharp-only (decision 0007): --tier for this run, else the serviceTier setting for a new session.
            var tier = options.ServiceTier ?? (location.Created ? PiSharp.AI.ServiceTiers.Normalize(settings.String("serviceTier")) : null);
            var root = await harness.RootAsync(Context, new AgentChange
            {
                Cwd = location.Cwd,
                Model = initial?.Model is { } model ? model : (Patch<ModelRef>?)null,
                ThinkingLevel = initial?.ThinkingLevel is { } level ? level : (Patch<string>?)null,
                ServiceTier = tier is not null ? tier : (Patch<string>?)null,
            }).ConfigureAwait(false);
            var summaries = new List<DurableConversationSummary>();
            JsonObjectValue? cursor = null;
            do
            {
                var scanFrom = cursor;
                var page = await harness.CommitAsync(tx => tx.ScanConversationsAsync(new ConversationQuery(), 256, scanFrom), Context).ConfigureAwait(false);
                foreach (var record in page.Items)
                    summaries.Add(new(record.Id, DurableSession.Label(record.Id, root.Id), await FirstInputAsync(harness, record.Id).ConfigureAwait(false)));
                cursor = page.Next;
            } while (cursor is not null);
            session = await DurableSession.StartAsync(location, envs, harness, models, settings, root, summaries, initial, reports).ConfigureAwait(false);
        }
        catch
        {
            if (harness is not null) try { await harness.CloseAsync(Context).ConfigureAwait(false); } catch { }
            if (models is not null) try { await models.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await location.ReleaseAsync().ConfigureAwait(false); } catch { }
            // Whatever the lost lock made fail (a refused commit, the failed Session), the loss is what the user is told.
            if (location.LockLost is { } lost) throw lost;
            throw;
        }
        // Lost while the session started: the open fails rather than showing a session that has already failed.
        if (location.LockLost is { } lostAtStart)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            throw lostAtStart;
        }
        return session;
    }
}

/// <summary>The session's storage, refusing every commit once the session lock is lost: the refusal fails the Session as any storage error
/// does. It covers the commits the Harness makes while it opens, before the loss can fail its Session directly.</summary>
internal sealed class LockGuardedStorage(IStorage inner, DurableSessionLocation location) : IStorage
{
    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) =>
        location.LockLost is { } lost ? Task.FromException<Seq>(lost) : inner.CommitAsync(writes, context);
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanConversationsAsync(query, limit, cursor, context);
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        inner.GetEntryAsync(conversationId, id, context);
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanEntriesAsync(query, limit, cursor, context);
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanTasksAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanSubmissionsAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        inner.FindDocumentAsync(address, at, context);
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanDocumentsAsync(query, limit, cursor, context);
    public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
}

/// <summary>Harness reports: buffered until the session can show them as notices.</summary>
internal sealed class DurableReports
{
    private readonly Lock gate = new();
    private readonly List<Exception> pending = [];
    private Action<Exception>? sink;

    /// <summary><c>HarnessOptions.OnReport</c>.</summary>
    public void Report(Exception error)
    {
        Action<Exception>? target;
        lock (gate)
        {
            target = sink;
            if (target is null) { pending.Add(error); return; }
        }
        target(error);
    }

    /// <summary>Sends the buffered reports and every later one to <paramref name="target"/>.</summary>
    public void Attach(Action<Exception> target)
    {
        Exception[] buffered;
        lock (gate) { sink = target; buffered = [.. pending]; pending.Clear(); }
        foreach (var error in buffered) target(error);
    }
}

/// <summary>An open durable session: the view source, the controller and their state (upstream <c>openDurable</c>'s closure).</summary>
internal sealed class DurableSession : IOpenDurable, IDurableViewSource, IDurableController
{
    private static DurableContext Context => DurableRuntime.Context;

    private readonly DurableSessionLocation location;
    private readonly DurableExecutionEnvs envs;
    private readonly IHarness harness;
    private readonly DurableModels models;
    private readonly ConversationId rootId;
    private readonly Lock gate = new();
    private readonly List<Action> listeners = [];
    private DurableView state;
    private bool notifying;
    private long nextNotice = 1;
    private volatile IConversation current;
    private AttachedReplicatedState<ConversationView> conversation;
    private IDisposable unsubscribe;
    private IDisposable? unsubscribeCommits;
    private IDisposable? unsubscribeClose;
    /// <summary>Set when the Harness begins to close: ours, or a failed Session's.</summary>
    private volatile bool ended;
    private AttachedReplicatedState<TaskGraph>? tasks;
    private IDisposable? unsubscribeTasks;
    private Task queue = Task.CompletedTask;
    private Task? closing;

    private DurableSession(DurableSessionLocation location, DurableExecutionEnvs envs, IHarness harness, DurableModels models, PiSettings settings,
        IConversation root, AttachedReplicatedState<ConversationView> conversation, IReadOnlyList<DurableConversationSummary> summaries)
    {
        this.location = location; this.envs = envs; this.harness = harness; this.models = models; Settings = settings;
        rootId = root.Id;
        current = root;
        this.conversation = conversation;
        state = new DurableView(new DurableSessionInfo(location.Id, location.Directory, location.Cwd), conversation.Value, [.. summaries],
            ModelSummaries(), []);
        state = WithShown(state, conversation.Value);
        unsubscribe = conversation.Subscribe((value, _, _) => Update(view => WithShown(view, value)));
    }

    /// <summary>The view showing <paramref name="value"/>, a revision of the shown conversation, with its nested calls gathered; another
    /// conversation's revision starts them afresh.</summary>
    private static DurableView WithShown(DurableView view, ConversationView value)
    {
        var none = ImmutableSortedDictionary<long, DurableNestedCall>.Empty;
        var calls = view.Conversation.Conversation.Id == value.Conversation.Id ? view.NestedCalls ?? none : none;
        return view with { Conversation = value, NestedCalls = DurableNestedCall.Merge(calls, value) };
    }

    /// <summary>The label of a conversation in the switcher.</summary>
    internal static string Label(ConversationId id, ConversationId root) => id == root ? "main" : $"subagent {id.Value}";

    internal static async Task<DurableSession> StartAsync(DurableSessionLocation location, DurableExecutionEnvs envs, IHarness harness,
        DurableModels models, PiSettings settings, IConversation root, IReadOnlyList<DurableConversationSummary> summaries,
        DurableInitialModel? initial, DurableReports reports)
    {
        var view = await root.ViewStateAsync(Context).ConfigureAwait(false);
        var session = new DurableSession(location, envs, harness, models, settings, root, view, summaries);
        // Since pi-durable 1.2.0 a storage error fails and closes the Harness (SessionFailed); the view then says so (durable work list area l).
        // Close listeners run as the close begins, before the failed call's caller sees its error and before the failure is reported, so
        // neither becomes a notice besides the failure.
        session.unsubscribeClose = harness.SubscribeClose(() => session.ended = true);
        _ = session.WatchClosedAsync();
        reports.Attach(error =>
        {
            if (!session.ended) session.Notice(DurableNoticeLevel.Warning, error.Message);
        });
        // Subagents appear as their conversations are created. A commit listener only records; it calls no Session API.
        session.unsubscribeCommits = harness.SubscribeCommits((publication, _) => session.OnCommit(publication));
        var saved = DurableRuntime.ModelOf(DurableRuntime.AgentOf(session.Current.Conversation));
        if (saved is null) session.Notice(DurableNoticeLevel.Warning, "No model configured; select one with /model.");
        else if (models.GetModel(saved.Provider, saved.ModelId) is null)
            session.Notice(DurableNoticeLevel.Warning, $"Saved model is unavailable: {saved.Provider}/{saved.ModelId}");
        if (initial?.FallbackMessage is { } fallback) session.Notice(DurableNoticeLevel.Info, fallback);
        // The task panel starts open; /tasks hides it.
        await session.ToggleTasksAsync().ConfigureAwait(false);
        // Recovered work from an interrupted turn continues now.
        harness.Resume();
        return session;
    }

    // ─── IOpenDurable ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IDurableViewSource View => this;

    /// <inheritdoc/>
    public IDurableController Controller => this;

    /// <inheritdoc/>
    public PiSettings Settings { get; }

    /// <summary>The Harness (tests).</summary>
    internal IHarness Harness => harness;

    /// <summary>The session directory and its lock (tests).</summary>
    internal DurableSessionLocation Location => location;

    /// <summary>The model runtime (tests).</summary>
    internal DurableModels Models => models;

    /// <summary>The conversation shown and talked to.</summary>
    internal IConversation Shown => current;

    // ─── View source ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public DurableView Current { get { lock (gate) return state; } }

    /// <inheritdoc/>
    public IDisposable Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate) listeners.Add(listener);
        return new Unsubscriber(this, listener);
    }

    private sealed class Unsubscriber(DurableSession owner, Action listener) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate) owner.listeners.Remove(listener);
        }
    }

    /// <summary>Commit listeners and Chord frames call this on the Session line; listeners run afterwards, once per burst.</summary>
    private void Update(Func<DurableView, DurableView> patch)
    {
        lock (gate)
        {
            state = patch(state);
            if (notifying) return;
            notifying = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            Action[] targets;
            lock (gate)
            {
                notifying = false;
                targets = [.. listeners];
            }
            foreach (var listener in targets)
                try { listener(); }
                catch { /* A listener's failure is its own; the others still run. */ }
        }, null);
    }

    private ImmutableArray<DurableModelSummary> ModelSummaries() =>
        [.. models.GetAvailableSnapshot().Select(model => new DurableModelSummary(model.Provider, model.Id, model.Name, model.ContextWindow))];

    private void Notice(DurableNoticeLevel level, string message)
    {
        Update(view =>
        {
            var notices = view.Notices.Add(new DurableNotice(nextNotice++, level, message));
            return view with { Notices = notices.Length > 20 ? notices.RemoveRange(0, notices.Length - 20) : notices };
        });
    }

    /// <summary>A failed request becomes an error notice; one the failed session rejected marks the session failed instead, which the view
    /// shows once.</summary>
    private void Fail(Exception error)
    {
        if (error is SessionFailedException failed) MarkFailed(failed.Cause);
        else if (!ended) Notice(DurableNoticeLevel.Error, error.Message);
    }

    private void MarkFailed(Exception cause) => Update(view => view.Failure is null ? view with { Failure = cause.Message } : view);

    /// <summary>Marks the view failed when the Harness closes because its Session failed (<c>ISession.Closed</c> settles with
    /// <see cref="SessionEndReason.Failed"/>). Our own close settles it with <see cref="SessionEndReason.Closed"/>.</summary>
    private async Task WatchClosedAsync()
    {
        var end = await harness.Closed.ConfigureAwait(false);
        if (end.Reason == SessionEndReason.Failed) MarkFailed(end.Error ?? new InvalidOperationException("unknown error"));
    }

    private void OnCommit(CommitPublication publication)
    {
        Update(view =>
        {
            var conversations = view.Conversations;
            foreach (var change in publication.Changes)
            {
                if (change is ConversationWrite created)
                    conversations = conversations.Add(new(created.Value.Id, Label(created.Value.Id, rootId)));
                else if (change is EntryWrite { Value.Kind: "pi.user" } entry)
                {
                    var id = entry.Value.ConversationId;
                    conversations = [.. conversations.Select(summary => summary.Id == id && summary.Title is null
                        ? summary with { Title = DurableRuntime.TitleOf(entry.Value) } : summary)];
                }
            }
            return conversations == view.Conversations ? view : view with { Conversations = conversations };
        });
    }

    // ─── Controller ─────────────────────────────────────────────────────────

    /// <summary>One at a time, so toggles, switches, and key presses apply in order; a failure becomes an error notice.</summary>
    private Task Command(Func<Task> operation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (gate)
        {
            previous = queue;
            queue = done.Task;
        }
        _ = RunAfterAsync(previous, operation, done);
        return done.Task;
    }

    /// <summary>Runs <paramref name="operation"/> on the thread pool after <paramref name="previous"/>, so the caller (the TUI's loop) gets
    /// control back at once.</summary>
    private async Task RunAfterAsync(Task previous, Func<Task> operation, TaskCompletionSource done)
    {
        try
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            await operation().ConfigureAwait(false);
        }
        catch (Exception error) { Fail(error); }
        finally { done.TrySetResult(); }
    }

    private void WatchAnswer(ISubmission submission) => _ = WatchAnswerAsync(submission);

    private async Task WatchAnswerAsync(ISubmission submission)
    {
        try
        {
            var settled = await submission.WaitAsync(Context).ConfigureAwait(false);
            if (settled.Status == SubmissionStatus.Unanswered && settled.Reason != "aborted")
                Notice(DurableNoticeLevel.Error, $"No answer: {settled.Reason}{(settled.Detail is null ? "" : " " + settled.Detail.ToJson())}");
        }
        catch (Exception error) { Fail(error); }
    }

    private DurableModel AgentModel()
    {
        var reference = DurableRuntime.ModelOf(DurableRuntime.AgentOf(Current.Conversation));
        var model = reference is null ? null : models.GetModel(reference.Provider, reference.ModelId);
        return model ?? throw new InvalidOperationException(reference is null ? "No model selected" : "Current model is unavailable");
    }

    /// <inheritdoc/>
    public Task SubmitAsync(string text, WhenBusy whenBusy) => Command(async () =>
        WatchAnswer(await current.SubmitAsync(SubmissionDraft.Input(text, whenBusy), Context).ConfigureAwait(false)));

    /// <inheritdoc/>
    public Task CompactAsync(string? instructions) => Command(async () =>
    {
        var id = await current.CompactAsync(instructions, Context).ConfigureAwait(false);
        // Report the outcome once it is known; the status line shows the compaction meanwhile.
        _ = ReportCompactionAsync(id);
    });

    private async Task ReportCompactionAsync(TaskId id)
    {
        try
        {
            var receipt = await harness.WaitForTaskAsync(id, Context).ConfigureAwait(false);
            var outcome = ((SettlingTaskState)receipt.State).Outcome;
            switch (outcome)
            {
                case CompletedTaskOutcome completed:
                {
                    var result = completed.Result is { } json ? CompactionResult.FromJson(json) : new CompactionResult();
                    // A summary written while busy is a submission: placed now, queued, or dropped as stale.
                    SubmissionStatus? status = null;
                    if (result.SubmissionId is { } submissionId && await harness.SubmissionAsync(submissionId, Context).ConfigureAwait(false) is { } submission)
                        status = (await submission.StatusAsync(Context).ConfigureAwait(false)).Status;
                    Notice(DurableNoticeLevel.Info, result.EntryId is not null || status == SubmissionStatus.Done
                        ? "Compacted."
                        : status == SubmissionStatus.Queued
                            ? "Compaction summary queued; it is placed at the next turn boundary."
                            : status == SubmissionStatus.Unanswered
                                ? "Compaction summary dropped: the context changed under it."
                                : "Nothing to compact: the context fits in the recent window that is kept verbatim.");
                    break;
                }
                case AbortedTaskOutcome:
                    Notice(DurableNoticeLevel.Info, "Compaction aborted.");
                    break;
                // Failed, faulted and orphaned compactions, manual or automatic, are recorded in pi.live.compactionFailure (pi-durable 1.2.0),
                // which the TUI shows until a compaction produces a summary.
            }
        }
        catch (Exception error) { Fail(error); }
    }

    /// <inheritdoc/>
    public async Task AbortAsync()
    {
        // Not queued: it waits until the conversation is idle.
        try { var shown = current; await Task.Run(() => shown.AbortAsync(Context)).ConfigureAwait(false); }
        catch (Exception error) { Fail(error); }
    }

    /// <inheritdoc/>
    public Task CycleThinkingAsync() => Command(async () =>
    {
        var model = AgentModel();
        if (!model.Reasoning) throw new InvalidOperationException("Current model does not support thinking");
        var levels = DurableModels.SupportedThinkingLevels(model);
        var level = DurableRuntime.ThinkingLevelOf(DurableRuntime.AgentOf(Current.Conversation)) ?? "off";
        var index = levels.IndexOf(level);
        var next = levels.Length == 0 ? "off" : levels[(index + 1) % levels.Length];
        await current.ConfigureAsync(new AgentChange { ThinkingLevel = next }, Context).ConfigureAwait(false);
    });

    /// <inheritdoc/>
    public Task SetModelAsync(ModelRef model) => Command(async () =>
    {
        ArgumentNullException.ThrowIfNull(model);
        var found = models.GetModel(model.Provider, model.ModelId) ?? throw new InvalidOperationException($"Unknown model: {model.Provider}/{model.ModelId}");
        var thinking = DurableRuntime.ThinkingLevelOf(DurableRuntime.AgentOf(Current.Conversation)) ?? "off";
        await current.ConfigureAsync(new AgentChange { Model = model, ThinkingLevel = DurableModels.ClampThinkingLevel(found, thinking) }, Context)
            .ConfigureAwait(false);
    });

    /// <inheritdoc/>
    public Task SetServiceTierAsync(string? tier) => Command(async () =>
    {
        var normalized = tier is null ? null : PiSharp.AI.ServiceTiers.Normalize(tier)
            ?? throw new InvalidOperationException($"Unknown service tier \"{tier}\". Valid tiers: {string.Join(", ", PiSharp.AI.ServiceTiers.All)}");
        await current.ConfigureAsync(new AgentChange { ServiceTier = normalized is null ? Patch<string>.Clear : normalized }, Context).ConfigureAwait(false);
    });

    /// <inheritdoc/>
    public Task ToggleTasksAsync() => Command(async () =>
    {
        if (tasks is not null)
        {
            CloseTasks();
            Update(view => view with { Tasks = null });
            return;
        }
        var graph = await harness.TaskGraphAsync(Context).ConfigureAwait(false);
        tasks = graph;
        unsubscribeTasks = graph.Subscribe((value, _, _) => Update(view => view with { Tasks = value }));
    });

    private void CloseTasks()
    {
        unsubscribeTasks?.Dispose();
        unsubscribeTasks = null;
        tasks?.Dispose();
        tasks = null;
    }

    /// <inheritdoc/>
    public Task SwitchConversationAsync(ConversationId id) => Command(async () =>
    {
        var next = await harness.ConversationAsync(id, Context).ConfigureAwait(false) ?? throw new InvalidOperationException($"Conversation {id.Value} does not exist");
        var nextState = await next.ViewStateAsync(Context).ConfigureAwait(false);
        unsubscribe.Dispose();
        conversation.Dispose();
        current = next;
        conversation = nextState;
        unsubscribe = nextState.Subscribe((value, _, _) => Update(view => WithShown(view, value)));
    });

    // ─── Close ──────────────────────────────────────────────────────────────

    /// <summary>Closes the Harness (writing no outcome: a running turn resumes with <c>--continue</c>), cleans up the environments and
    /// releases the lock (upstream <c>close()</c>); idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        Task task;
        lock (gate) task = closing ??= CloseAsync();
        await task.ConfigureAwait(false);
    }

    private async Task CloseAsync()
    {
        unsubscribe.Dispose();
        unsubscribeCommits?.Dispose();
        unsubscribeClose?.Dispose();
        conversation.Dispose();
        CloseTasks();
        try
        {
            // Close writes no outcome: a running turn resumes with --continue. A failed session closed itself already; what its close
            // rethrows is the failure the view shows.
            try { await harness.CloseAsync(Context).ConfigureAwait(false); }
            catch (Exception) when (Current.Failure is not null || harness.Closed is { IsCompleted: true, Result.Reason: SessionEndReason.Failed })
            {
            }
            await envs.CleanupAsync(Context).ConfigureAwait(false);
            await models.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await location.ReleaseAsync().ConfigureAwait(false);
        }
    }
}
