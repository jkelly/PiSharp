// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/harness.ts. The Harness extends the Session kernel, as
// upstream's HarnessImpl extends SessionImpl. Every component reads and writes through the Session's Storage guard; safeReport, safeNow,
// documentRetentionMs, report, assertUsable and the open failure's close are v1.2.0's. Upstream's SCHEDULER_INDEX_SIZES symbol getter, for
// tests, is the internal SchedulerIndexSizes property (the test project sees internals).
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable;

/// <summary>Opens a durable agent harness over one storage backend (upstream <c>Harness.open</c>).</summary>
public static class Harness
{
    /// <summary>
    /// Opens a Harness over <paramref name="storage"/>: surviving <c>running</c> tasks become <c>pending</c>, and nothing runs until
    /// <see cref="IHarness.Resume"/> or a call that asks for progress. The registry may keep changing while the Harness runs.
    /// </summary>
    public static async Task<IHarness> OpenAsync(IStorage storage, HarnessOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        var snapshot = options.Registry.Snapshot();
        var missing = Registry.BuiltinTasks.Where(task => snapshot.GetTask(task.Definition.Name) is null).ToList();
        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Select(task => task.Definition.Name));
            throw new InvalidOperationException($"Registry lacks built-in tasks {names}; create it with createRegistry()");
        }
        var harness = new DurableHarness(storage, options, context);
        try
        {
            await harness.OpenTasksAsync(context).ConfigureAwait(false);
        }
        catch
        {
            // The caller's context may be what failed open: close without it, and rethrow the open error. A failing Storage close fails the
            // Session, which reports it.
            try
            {
                await harness.CloseAsync(context.WithoutCancellation()).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reported by the Session.
            }
            throw;
        }
        return harness;
    }
}

/// <summary>The Session kernel extended with conversation handles, a registry and the task scheduler (upstream <c>HarnessImpl</c>).</summary>
internal sealed class DurableHarness : Session, IHarness
{
    private const int ScanPageSize = 256;

    private readonly IStorage storage;
    private readonly HarnessOptions options;
    private readonly Action<Exception> report;
    private readonly DurableTaskScheduler scheduler;
    private readonly Submissions submissions;
    private readonly ConversationViews views;
    private readonly TaskGraphView taskGraph;

    public DurableHarness(IStorage rawStorage, HarnessOptions options, DurableContext context)
        : this(rawStorage, options, context, SafeReport(options.OnReport))
    {
    }

    private DurableHarness(IStorage rawStorage, HarnessOptions options, DurableContext context, Action<Exception> report)
        // Read at each use, as all settings are, but only this field: it is read on every commit.
        : base(rawStorage, SafeNow(Clock(options), options.Now is not null, report), () => options.Settings?.DocumentRetentionMs)
    {
        // Every component reads and writes through the Session's failure guard: rawStorage serves only the base constructor.
        var storage = GuardedStorage;
        this.storage = storage;
        this.options = options;
        this.report = report;
        Now = SafeNow(Clock(options), options.Now is not null, report);
        scheduler = new DurableTaskScheduler(new DurableTaskSchedulerOptions
        {
            Session = this,
            Storage = storage,
            Registry = options.Registry,
            Models = options.Models,
            Agent = (id, snapshot, callContext) => ResolveAgentAsync(id, snapshot, callContext),
            Settings = () => Agents.ResolveSettings(options.Settings),
            Env = BuildEnvAsync,
            Now = Now,
            TimeProvider = options.TimeProvider ?? TimeProvider.System,
            Report = report,
            SettleOutcome = (tx, record, outcome) => Live.SettleSchedulerOutcomeAsync(tx, record, outcome),
            WithdrawInputs = (tx, id) => Inbox.WithdrawQueuedInputsAsync(tx, id),
            Conversation = async (id, binding, callContext) =>
            {
                var record = await ReadOnLineAsync(() => storage.GetConversationAsync(id, callContext)).ConfigureAwait(false);
                return record is null ? null : new BoundConversation(id, binding, scheduler!, submissions!);
            },
            Context = context.WithoutCancellation(),
        });
        submissions = new Submissions(this, storage, Now, () => Agents.ResolveSettings(options.Settings), () => scheduler.Resume());
        views = new ConversationViews(this, storage);
        taskGraph = new TaskGraphView(this, storage);
    }

    /// <summary>The Harness clock.</summary>
    internal Func<double> Now { get; }

    internal IStorage Storage => storage;

    internal DurableTaskScheduler Scheduler => scheduler;

    /// <summary>Internal, for tests: sizes of the scheduler's ownership indexes (upstream <c>[SCHEDULER_INDEX_SIZES]</c>).</summary>
    internal SchedulerIndexSizes SchedulerIndexSizes => scheduler.IndexSizes;

    internal Submissions Submissions => submissions;

    /// <summary>The conversation view mounts (upstream <c>host.views</c>).</summary>
    internal ConversationViews Views => views;

    private static Func<double> Clock(HarnessOptions options)
    {
        if (options.Now is { } now) return now;
        var time = options.TimeProvider ?? TimeProvider.System;
        return () => time.GetUtcNow().ToUnixTimeMilliseconds();
    }

    /// <summary><c>OnReport</c> that cannot fail (upstream <c>safeReport</c>): it is the last place an error goes, so its own throw is
    /// dropped.</summary>
    internal static Action<Exception> SafeReport(Action<Exception>? onReport) => error => Contained(() => onReport?.Invoke(error), _ => { });

    /// <summary>
    /// The host clock, falling back to the system clock when it throws (upstream <c>safeNow</c>): a broken clock must not stop commits. The
    /// first throw is reported; the clock is read in every task change, so later ones are not. <paramref name="host"/> is false for the
    /// Harness's own TimeProvider clock, which needs no guard.
    /// </summary>
    internal static Func<double> SafeNow(Func<double> now, bool host, Action<Exception> report)
    {
        if (!host) return now;
        var reported = 0;
        return () =>
        {
            try
            {
                return now();
            }
            catch (Exception error)
            {
                // Set first: a report handler that reads the clock again must not report again.
                if (Interlocked.Exchange(ref reported, 1) == 0) report(error);
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        };
    }

    /// <summary>The Session's failure and listener errors go to <c>OnReport</c>.</summary>
    internal override void Report(Exception error) => report(error);

    /// <summary>Resolves a conversation's committed <c>pi.agent</c> against <paramref name="snapshot"/>, or the current one, and the current
    /// settings.</summary>
    internal async Task<Agent> ResolveAgentAsync(ConversationId id, IRegistrySnapshot? snapshot, DurableContext context)
    {
        var registry = snapshot ?? options.Registry.Snapshot();
        var state = await SnapshotAsync(Agents.AgentDoc, id, context).ConfigureAwait(false);
        return Agents.ResolveAgent(state, registry, Agents.ResolveSettings(options.Settings), report);
    }

    /// <summary>Builds a conversation's environment from its current <c>cwd</c>; null without an <c>Env</c> option.</summary>
    internal async Task<IExecutionEnv?> BuildEnvAsync(ConversationId id, DurableContext context)
    {
        if (options.Env is not { } build) return null;
        var state = await SnapshotAsync(Agents.AgentDoc, id, context).ConfigureAwait(false);
        var cwd = state is not null && state.TryGetValue("cwd", out var value) && value is JsonStringValue text ? text.Value : null;
        return await build(new EnvTarget(id, cwd, this), context).ConfigureAwait(false);
    }

    /// <summary>Reconciles surviving running tasks to pending; part of open.</summary>
    internal Task OpenTasksAsync(DurableContext context) => scheduler.OpenAsync(context);

    public void Resume()
    {
        AssertUsable();
        scheduler.Resume();
    }

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => ReadOnLineAsync(() => storage.GetTaskAsync(id, context));

    public Task<HarnessInspection> InspectAsync(DurableContext context) => ReadOnLineAsync(async () =>
    {
        var (scheduling, tasks) = await scheduler.InspectAsync(options.Registry.Snapshot()).ConfigureAwait(false);
        Task<List<SubmissionRecord>> Scan(SubmissionStatus status) => HarnessUtil.ScanAll(cursor =>
            storage.ScanSubmissionsAsync(new SubmissionQuery { Status = status }, ScanPageSize, cursor, context));
        var submissions = (await Scan(SubmissionStatus.Queued).ConfigureAwait(false))
            .Concat(await Scan(SubmissionStatus.Placed).ConfigureAwait(false))
            .OrderBy(submission => submission.Id.Value).ToImmutableArray();
        return new HarnessInspection(scheduling, tasks, submissions);
    });

    public Task<ISubmission?> SubmissionAsync(SubmissionId id, DurableContext context) => submissions.GetAsync(id, context);

    public Task<SubmissionAbortResult> AbortSubmissionAsync(SubmissionId id, DurableContext context, ConversationId? conversationId = null) =>
        submissions.AbortAsync(id, context, conversationId);

    public Task<TaskAbortResult> AbortTaskAsync(TaskId id, DurableContext context) => scheduler.AbortAsync(id, context);

    public Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context)
    {
        scheduler.Resume();
        return scheduler.WaitForTaskAsync(id, context);
    }

    public Task WaitForIdleAsync(DurableContext context)
    {
        scheduler.Resume();
        return scheduler.WaitForIdleAsync(null, context);
    }

    /// <summary>Sums every conversation's committed <c>pi.usage</c>. Each document is read at its own point; totals only grow.</summary>
    public async Task<JsonObjectValue> UsageAsync(DurableContext context)
    {
        var conversations = await ReadOnLineAsync(() => HarnessUtil.ScanAll(cursor =>
            storage.ScanConversationsAsync(new ConversationQuery(), ScanPageSize, cursor, context))).ConfigureAwait(false);
        var total = UsageLedger.UsageDoc.Definition.Initial();
        foreach (var conversation in conversations)
        {
            var state = await SnapshotAsync(UsageLedger.UsageDoc, conversation.Id, context).ConfigureAwait(false);
            if (state is not null) total = UsageLedger.AddUsageState(total, state);
        }
        return total;
    }

    public Task<AttachedReplicatedState<TaskGraph>> TaskGraphAsync(DurableContext context) => taskGraph.StateAsync(context);

    public Task<IWatchHandle<TaskGraph>> WatchTaskGraphAsync(DurableContext context) => taskGraph.WatchAsync(context);

    public Task<IConversation> RootAsync(DurableContext context, AgentChange? agent = null, ConversationInit? init = null) =>
        CreateAsync(CreateTarget.Root, agent, init, context);

    public async Task<IConversation?> ConversationAsync(ConversationId id, DurableContext context)
    {
        AssertUsable();
        var record = await ReadOnLineAsync(() => storage.GetConversationAsync(id, context)).ConfigureAwait(false);
        return record is null ? null : new ConversationImpl(record.Id, this);
    }

    public Task<IConversation> CreateConversationAsync(ConversationCreateOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateAsync(CreateTarget.Independent(options.Ownership), options.Agent, options.Init, context);
    }

    /// <summary>Signals and joins task invocations after the close listeners and before Storage closes; writes no task outcome.</summary>
    internal override Task BeforeCloseAsync() => scheduler.JoinAsync();

    internal async Task<IConversation> CreateAsync(CreateTarget target, AgentChange? agent, ConversationInit? init, DurableContext context)
    {
        AssertUsable();
        var id = await CommitWithAsync(async tx =>
        {
            if (target.Kind == CreateKind.Root && await tx.GetConversationAsync(ConversationId.Root).ConfigureAwait(false) is not null)
                return ConversationId.Root;
            var record = target.Kind switch
            {
                CreateKind.Root => await tx.CreateRootConversationAsync().ConfigureAwait(false),
                CreateKind.Fork => await tx.ForkConversationAsync(target.ParentId, target.At, target.Ownership!).ConfigureAwait(false),
                _ => await tx.CreateConversationAsync(target.Ownership!).ConfigureAwait(false),
            };
            if (agent is not null) await Agents.ConfigureAsync(tx, record.Id, agent).ConfigureAwait(false);
            if (init is not null) await init(tx, record.Id).ConfigureAwait(false);
            return record.Id;
        }, context).ConfigureAwait(false);
        return new ConversationImpl(id, this);
    }

    /// <summary>
    /// The built-in creation hook, in every commit that creates or forks a conversation: empty <c>pi.live</c>, <c>pi.inbox</c> and
    /// <c>pi.usage</c>, a fresh <c>pi.provider</c>, the conversation's <c>pi.agent</c> (see <see cref="Agents.CreateAgentAsync"/>), then
    /// <c>HarnessOptions.ConversationCreated</c>.
    /// </summary>
    internal override async Task ConversationCreatedAsync(Transaction tx, ConversationRecord record)
    {
        await tx.DocAsync(Live.LiveDoc, record.Id).ConfigureAwait(false);
        await tx.DocAsync(Inbox.InboxDoc, record.Id).ConfigureAwait(false);
        await tx.DocAsync(UsageLedger.UsageDoc, record.Id).ConfigureAwait(false);
        await tx.DocAsync(ProviderIdentity.ProviderDoc, record.Id).ConfigureAwait(false);
        await Agents.CreateAgentAsync(tx, record).ConfigureAwait(false);
        if (options.ConversationCreated is { } created) await created(tx, record).ConfigureAwait(false);
    }

    internal enum CreateKind { Root, Independent, Fork }

    internal sealed record CreateTarget(CreateKind Kind, ConversationOwnership? Ownership, ConversationId ParentId, EntryId At)
    {
        public static CreateTarget Root { get; } = new(CreateKind.Root, null, default, default);
        public static CreateTarget Independent(ConversationOwnership ownership) => new(CreateKind.Independent, ownership, default, default);
        public static CreateTarget Fork(ConversationId parentId, EntryId at, ConversationOwnership ownership) => new(CreateKind.Fork, ownership, parentId, at);
    }
}

/// <summary>A stateless handle for one conversation, bound to the Harness that returned it (upstream <c>ConversationImpl</c>).</summary>
internal sealed class ConversationImpl(ConversationId id, DurableHarness harness) : IConversation
{
    public ConversationId Id { get; } = id;

    public Task<Agent> AgentAsync(DurableContext context) => harness.ResolveAgentAsync(Id, null, context);

    public Task ConfigureAsync(AgentChange change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => Agents.ConfigureAsync(tx, Id, change), context);
    }

    public Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context) => harness.Submissions.SubmitAsync(Id, submission, context);

    /// <summary>Admits a write of a <c>pi.reset</c> entry with <c>head: "self"</c>, carrying <paramref name="handoff"/> as a user message when
    /// given; a busy conversation places it at its next boundary.</summary>
    public async Task ResetAsync(string? handoff, DurableContext context)
    {
        var model = handoff is null ? null : JsonValue.Array(JsonValue.Object([
            KeyValuePair.Create("role", (JsonValue)"user"),
            KeyValuePair.Create("content", (JsonValue)handoff),
            KeyValuePair.Create("timestamp", (JsonValue)harness.Now()),
        ]));
        var entry = new EntryDraft { Kind = Entries.ResetEntry.Kind, Head = EntryHead.Self, Model = model };
        await harness.Submissions.SubmitAsync(Id, SubmissionDraft.Write(entry), context).ConfigureAwait(false);
    }

    /// <summary>Creates a manual compaction task with its live status in one commit (upstream <c>compact</c>).</summary>
    public Task<TaskId> CompactAsync(string? instructions, DurableContext context)
    {
        harness.Scheduler.Resume();
        return harness.CommitWithAsync(tx => Compaction.CreateCompactionAsync(tx, Id, "manual", instructions), context);
    }

    public Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => change(tx), context, new TransactionScope(Id));
    }

    public Task CommitAsync(Func<ITx, Task> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => change(tx), context, new TransactionScope(Id));
    }

    public Task<T> CommitAsync<T>(Func<ITx, T> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => Task.FromResult(change(tx)), context, new TransactionScope(Id));
    }

    public Task CommitAsync(Action<ITx> change, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => change(tx), context, new TransactionScope(Id));
    }

    public Task<ContextView> ContextAsync(DurableContext context, EntryId? at = null) =>
        ConversationContext.ReadContextAsync(harness, harness.Storage, Id, context, at);

    public Task<Page<EntryRecord>> EntriesAsync(EntryQueryBounds query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        var bounded = new EntryQuery(Id) { MinEntryId = query.MinEntryId, MaxEntryId = query.MaxEntryId, Order = query.Order };
        return harness.ReadOnLineAsync(() => harness.Storage.ScanEntriesAsync(bounded, limit, cursor, context));
    }

    public Task<IConversation> ForkAsync(EntryId at, ConversationCreateOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        return harness.CreateAsync(DurableHarness.CreateTarget.Fork(Id, at, options.Ownership), options.Agent, options.Init, context);
    }

    public Task AbortAsync(DurableContext context, ConversationAbortOptions? options = null)
    {
        harness.Scheduler.Resume();
        return harness.Scheduler.AbortConversationAsync(Id, options?.Background == true, context);
    }

    public Task WaitForIdleAsync(DurableContext context)
    {
        harness.Scheduler.Resume();
        return harness.Scheduler.WaitForIdleAsync(Id, context);
    }

    public Task<AttachedReplicatedState<ConversationView>> ViewStateAsync(DurableContext context) => harness.Views.StateAsync(Id, context);

    public Task<IWatchHandle<ConversationView>> WatchAsync(DurableContext context) => harness.Views.WatchAsync(Id, context);

    public override string ToString() => $"Conversation {Id}";
}

/// <summary>
/// Invocation-bound handle for tasks and tools (upstream <c>boundConversation</c>). Every operation first checks the invocation and runs under
/// its signal, so it rejects once the invocation ends; admitted work stays durable.
/// </summary>
internal sealed class BoundConversation(ConversationId id, InvocationBinding binding, DurableTaskScheduler scheduler, Submissions submissions)
    : IConversationHandle
{
    public ConversationId Id { get; } = id;

    /// <summary>Admits user input; the returned submission's operations are bound to the invocation too.</summary>
    public async Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context)
    {
        binding.Check();
        var admitted = await submissions.SubmitAsync(Id, submission, context.WithSignal(binding.Signal)).ConfigureAwait(false);
        return new BoundSubmission(admitted, binding);
    }

    public async Task AbortAsync(DurableContext context, ConversationAbortOptions? options = null)
    {
        binding.Check();
        await scheduler.AbortConversationAsync(Id, options?.Background == true, context.WithSignal(binding.Signal)).ConfigureAwait(false);
    }

    public async Task WaitForIdleAsync(DurableContext context)
    {
        binding.Check();
        await scheduler.WaitForIdleAsync(Id, context.WithSignal(binding.Signal)).ConfigureAwait(false);
    }
}
