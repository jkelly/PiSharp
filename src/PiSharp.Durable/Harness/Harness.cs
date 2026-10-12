// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/harness.ts. The Harness extends the Session kernel, as
// upstream's HarnessImpl extends SessionImpl. Placeholders for later phases fail with NotImplementedException naming their phase:
// submissions (P8), reset, views and usage totals (P10), compaction and the task graph (P11).
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
            // The caller's context may be what failed open: close without it, and rethrow the open error.
            try
            {
                await harness.CloseAsync(context.WithoutCancellation()).ConfigureAwait(false);
            }
            catch (Exception closeError)
            {
                options.OnReport?.Invoke(closeError);
            }
            throw;
        }
        return harness;
    }

    internal static NotImplementedException NotPorted(string what, string phase) => new($"{what} is not ported yet ({phase})");
}

/// <summary>The Session kernel extended with conversation handles, a registry and the task scheduler (upstream <c>HarnessImpl</c>).</summary>
internal sealed class DurableHarness : Session, IHarness
{
    private const int ScanPageSize = 256;

    private readonly IStorage storage;
    private readonly HarnessOptions options;
    private readonly Action<Exception> report;
    private readonly DurableTaskScheduler scheduler;
    private volatile bool closed;

    public DurableHarness(IStorage storage, HarnessOptions options, DurableContext context)
        : base(storage, Clock(options))
    {
        this.storage = storage;
        this.options = options;
        report = options.OnReport ?? (_ => { });
        Now = Clock(options);
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
                return record is null ? null : new BoundConversation(id, binding, scheduler!);
            },
            Context = context.WithoutCancellation(),
        });
    }

    /// <summary>The Harness clock.</summary>
    internal Func<double> Now { get; }

    internal IStorage Storage => storage;

    internal DurableTaskScheduler Scheduler => scheduler;

    private static Func<double> Clock(HarnessOptions options)
    {
        if (options.Now is { } now) return now;
        var time = options.TimeProvider ?? TimeProvider.System;
        return () => time.GetUtcNow().ToUnixTimeMilliseconds();
    }

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
        AssertOpen();
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

    /// <summary>TODO(P8): submissions.</summary>
    public Task<ISubmission?> SubmissionAsync(SubmissionId id, DurableContext context) =>
        Task.FromException<ISubmission?>(Harness.NotPorted("Harness.SubmissionAsync", "P8"));

    /// <summary>TODO(P8): submissions.</summary>
    public Task<SubmissionAbortResult> AbortSubmissionAsync(SubmissionId id, DurableContext context, ConversationId? conversationId = null) =>
        Task.FromException<SubmissionAbortResult>(Harness.NotPorted("Harness.AbortSubmissionAsync", "P8"));

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

    /// <summary>TODO(P10): the Session usage total.</summary>
    public Task<JsonObjectValue> UsageAsync(DurableContext context) =>
        Task.FromException<JsonObjectValue>(Harness.NotPorted("Harness.UsageAsync", "P10"));

    /// <summary>TODO(P11): the task graph.</summary>
    public Task<AttachedReplicatedState<JsonObjectValue>> TaskGraphAsync(DurableContext context) =>
        Task.FromException<AttachedReplicatedState<JsonObjectValue>>(Harness.NotPorted("Harness.TaskGraphAsync", "P11"));

    /// <summary>TODO(P11): the task graph.</summary>
    public Task<IWatchHandle<JsonObjectValue>> WatchTaskGraphAsync(DurableContext context) =>
        Task.FromException<IWatchHandle<JsonObjectValue>>(Harness.NotPorted("Harness.WatchTaskGraphAsync", "P11"));

    public Task<IConversation> RootAsync(DurableContext context, AgentChange? agent = null, ConversationInit? init = null) =>
        CreateAsync(CreateTarget.Root, agent, init, context);

    public async Task<IConversation?> ConversationAsync(ConversationId id, DurableContext context)
    {
        AssertOpen();
        var record = await ReadOnLineAsync(() => storage.GetConversationAsync(id, context)).ConfigureAwait(false);
        return record is null ? null : new ConversationImpl(record.Id, this);
    }

    public Task<IConversation> CreateConversationAsync(ConversationCreateOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateAsync(CreateTarget.Independent(options.Ownership), options.Agent, options.Init, context);
    }

    public override Task CloseAsync(DurableContext context)
    {
        closed = true;
        return base.CloseAsync(context);
    }

    /// <summary>Joins task invocations after admission is sealed and before Storage closes; writes no task outcome.</summary>
    internal override Task BeforeCloseAsync() => scheduler.JoinAsync();

    internal async Task<IConversation> CreateAsync(CreateTarget target, AgentChange? agent, ConversationInit? init, DurableContext context)
    {
        AssertOpen();
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

    private void AssertOpen()
    {
        if (closed) throw HarnessUtil.ClosedError();
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

    /// <summary>TODO(P8): admission joins with the submissions port.</summary>
    public Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context) =>
        Task.FromException<ISubmission>(Harness.NotPorted("Conversation.SubmitAsync", "P8"));

    /// <summary>TODO(P10): a reset is a write submission placed through the inbox.</summary>
    public Task ResetAsync(string? handoff, DurableContext context) => Task.FromException(Harness.NotPorted("Conversation.ResetAsync", "P10"));

    /// <summary>TODO(P11): manual compaction.</summary>
    public Task<TaskId> CompactAsync(string? instructions, DurableContext context) =>
        Task.FromException<TaskId>(Harness.NotPorted("Conversation.CompactAsync", "P11"));

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

    /// <summary>TODO(P10): the structural view.</summary>
    public Task<AttachedReplicatedState<JsonObjectValue>> ViewStateAsync(DurableContext context) =>
        Task.FromException<AttachedReplicatedState<JsonObjectValue>>(Harness.NotPorted("Conversation.ViewStateAsync", "P10"));

    /// <summary>TODO(P10): the structural view.</summary>
    public Task<IWatchHandle<JsonObjectValue>> WatchAsync(DurableContext context) =>
        Task.FromException<IWatchHandle<JsonObjectValue>>(Harness.NotPorted("Conversation.WatchAsync", "P10"));

    public override string ToString() => $"Conversation {Id}";
}

/// <summary>
/// Invocation-bound handle for tasks and tools (upstream <c>boundConversation</c>). Every operation first checks the invocation and runs under
/// its signal, so it rejects once the invocation ends; admitted work stays durable.
/// </summary>
internal sealed class BoundConversation(ConversationId id, InvocationBinding binding, DurableTaskScheduler scheduler) : IConversationHandle
{
    public ConversationId Id { get; } = id;

    /// <summary>TODO(P8): admission joins with the submissions port; its submissions are bound to the invocation too.</summary>
    public async Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context)
    {
        binding.Check();
        await Task.CompletedTask.ConfigureAwait(false);
        throw Harness.NotPorted("ConversationHandle.SubmitAsync", "P8");
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
