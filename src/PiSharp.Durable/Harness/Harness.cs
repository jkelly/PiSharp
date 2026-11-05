// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/harness.ts. The Harness extends the Session kernel, as
// upstream's HarnessImpl extends SessionImpl. Every component reads and writes through the Session's Storage guard; safeReport, safeNow,
// documentRetentionMs, report, assertUsable and the open failure's close are v1.2.0's, as the later areas below changed them. Upstream's
// SCHEDULER_INDEX_SIZES and SCHEDULER_IDLE_STATE symbol getters, for tests, are the internal SchedulerIndexSizes and SchedulerIdleState
// properties (the test project sees internals).
//
// Pi v1.5.0 sync (area harness-options): HarnessOptions.CurrentModels and CurrentSettings, upstream's function forms of models and
// settings, read at each use (7ec1f648e; currentModels and currentSettings), and HarnessOptions.ReadOnly (be14d9732). The calls that
// resume the scheduler are async, so a read-only Harness's ReadOnlySessionException faults their task, as upstream made them async.
// Pi v1.5.0 sync (area model-requests): HarnessOptions.Identity over DEFAULT_HARNESS_IDENTITY, applied by currentModels through
// withClientIdentity (adc5fa9f8, 04a93bad8). Upstream's default attributes requests to pi (PI_ATTRIBUTION); PiSharp's is empty and adds no
// attribution, as PiSharp.Agent's DefaultIdentity: the owner chose no attribution (decision 0013, core.client-identity-attribution).
// Pi v1.5.0 sync (area sleep): HarnessOptions.SetTimer reaches the Session (document sweeps) and the scheduler as hostTimers, each over
// the default timers of HarnessOptions.TimeProvider; wake() and HarnessOptions.OnWake (f75b3240a, ec1be23eb).
// Pi v1.5.0 sync (area reports): OnReport takes one Report, an error with its ReportInfo (a30c2a81d). safeReport passes the info on (its
// pi.durable.reports counter and event are area telemetry's), safeNow reports host_error now, and the scheduler's timers host_error
// setTimer; an agent resolution names the conversation and the extension whose wrapper failed. The task graph reads blocked reasons from
// the scheduler (440a5fcdb). One safeNow serves the Session and the Harness, as upstream's one `now` does, so a broken clock reports once.
// Pi v1.5.0 sync (area telemetry, 2ec55d808): HarnessOptions.Telemetry reaches the scheduler (phase spans, durations, active tasks) and
// submissions (submit spans); without it every span is a no-op. safeReport counts each report in pi.durable.reports by type and records
// pi.durable.report on the reporting context's span with its fixed attributes (type, reason, extension, task.id), never the error; both are
// contained, as the report is the last place an error goes. Reports carry the context of what raised them (ReportSink).
// Pi v1.5.0 sync (area watches, 92ab53602): the state getters Conversation.viewState() and Harness.taskGraph() are removed; their watches
// stay (Conversation.watch(), Harness.watchTaskGraph()).
// Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): Conversation.commit() takes CommitOptions, and the Session reads
// settings.progress.persistWithinMs at each use, as the default window of a deferred commit.
// Pi v1.5.0 sync (area observers, 8b0caefc2): Harness.observeConversation() and observeTaskGraph() check the Harness is usable, then
// attach through the view and graph mounts (ConversationViews.ObserveAsync, TaskGraphView.ObserveAsync); a refusal faults the task.
// Pi v1.5.0 sync (area view, f3255de76): Conversation.watch() and Harness.observeConversation() pass their ConversationWindow on.
// Pi v1.5.0 sync (area restarts, b3a29f111): the scheduler learns which built-in kinds end themselves at settings.restartLimit
// (Live.LimitsRestarts), and its join writes at close through Session.WriteAtCloseAsync; safeReport records task_interrupted's reason,
// extension and task ID, as task_faulted's.
// Pi v1.5.0 sync (area admission, cc9c52310): Conversation.submit(tx, draft) and ConversationHandle.submit(tx, draft) admit inside the
// caller's commit (Submissions.AdmitAsync), as C# overloads of SubmitAsync; the handle's form checks the invocation's abort itself, as the
// caller's commit carries its own context. Upstream's runtime test of the first argument is overload resolution here.
// Pi v1.5.0 sync (area run-end, 65fba65b2): Conversation.entries() builds its scan with HarnessUtil.ConversationEntries, shared with
// TaskRuntime.entries().
// Pi v1.5.0 sync (area group-commit-scheduler, e5c6182ba): the Harness's line jobs overlap storage like a plain Session's (the serialized
// line a Harness kept, LineWaitsForPublication, is gone): every read on the line goes through the line's Storage (ReadOnLineAsync's
// IStorage argument, upstream readOnLine((storage) => ...)), which waits for what an unpublished commit wrote, and a commit's task and
// conversation changes write their ownership memberships from the scheduler's indexes (MembershipKeysAsync, upstream membershipKeys).
// Submissions, the conversation views and the task graph read through it too, so they no longer take the guarded Storage.
// Pi v1.5.0 sync (area scheduler-idle, 35dfa7573): SchedulerIdleState, upstream's SCHEDULER_IDLE_STATE getter. With it the file matches
// harness.ts at v1.5.0 as a whole, apart from the decided empty DefaultIdentity (area model-requests, above). harness.ts has no
// registryReader part; registry-reader.ts is its own file.
using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

/// <summary>Opens a durable agent harness over one storage backend (upstream <c>Harness.open</c>).</summary>
public static class Harness
{
    /// <summary>
    /// The request identity under every Harness's <see cref="HarnessOptions.Identity"/> (upstream <c>DEFAULT_HARNESS_IDENTITY</c>).
    /// Upstream's attributes requests to Pi (<c>{ attribution: PI_ATTRIBUTION }</c>); PiSharp's is empty, so the Harness adds no attribution
    /// and requests keep PiSharp.AI's defaults (the default attribution is the owner's decision, core.client-identity-attribution).
    /// </summary>
    public static ClientIdentity DefaultIdentity { get; } = new();

    /// <summary>
    /// Opens a Harness over <paramref name="storage"/>: surviving <c>running</c> tasks become <c>pending</c>, and nothing runs until
    /// <see cref="IHarness.Resume"/> or a call that asks for progress. The registry may keep changing while the Harness runs. A read-only
    /// Harness (<see cref="HarnessOptions.ReadOnly"/>) recovers nothing and writes nothing.
    /// </summary>
    public static async Task<IHarness> OpenAsync(IStorage storage, HarnessOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (options.Models is null == options.CurrentModels is null)
            throw new ArgumentException(options.Models is null ? "HarnessOptions needs Models or CurrentModels" : "HarnessOptions takes Models or CurrentModels, not both",
                nameof(options));
        if (options.Settings is not null && options.CurrentSettings is not null)
            throw new ArgumentException("HarnessOptions takes Settings or CurrentSettings, not both", nameof(options));
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

/// <summary>Where the Harness's components send an error with what it is about and, when there is one, the context of the operation it came
/// from, whose span records it (upstream <c>report(error, info, context?)</c>).</summary>
internal delegate void ReportSink(Exception error, ReportInfo info, DurableContext? context = null);

/// <summary>The Session kernel extended with conversation handles, a registry and the task scheduler (upstream <c>HarnessImpl</c>).</summary>
internal sealed class DurableHarness : Session, IHarness
{
    private const int ScanPageSize = 256;

    private readonly IStorage storage;
    private readonly HarnessOptions options;
    private readonly Func<HarnessSettings?> hostSettings;
    private readonly ReportSink report;
    private readonly DurableTaskScheduler scheduler;
    private readonly Submissions submissions;
    private readonly ConversationViews views;
    private readonly TaskGraphView taskGraph;

    public DurableHarness(IStorage rawStorage, HarnessOptions options, DurableContext context)
        : this(rawStorage, options, context, SafeReport(options.OnReport, options.Telemetry), CurrentSettings(options))
    {
    }

    private DurableHarness(IStorage rawStorage, HarnessOptions options, DurableContext context, ReportSink report,
        Func<HarnessSettings?> hostSettings)
        : this(rawStorage, options, context, report, hostSettings, SafeNow(Clock(options), options.Now is not null, (error, info) => report(error, info)))
    {
    }

    private DurableHarness(IStorage rawStorage, HarnessOptions options, DurableContext context, ReportSink report,
        Func<HarnessSettings?> hostSettings, Func<double> now)
        // Read at each use, as all settings are, but only these fields: they are read on every commit.
        : base(rawStorage, now, () => hostSettings()?.DocumentRetentionMs, options.ReadOnly, options.SetTimer, options.TimeProvider,
            () => hostSettings()?.Progress?.PersistWithinMs)
    {
        // Every component reads and writes through the Session's failure guard: rawStorage serves only the base constructor.
        var storage = GuardedStorage;
        this.storage = storage;
        this.options = options;
        this.hostSettings = hostSettings;
        this.report = report;
        Now = now;
        scheduler = new DurableTaskScheduler(new DurableTaskSchedulerOptions
        {
            Session = this,
            Storage = storage,
            Registry = options.Registry,
            Models = CurrentModels(options),
            Agent = (id, snapshot, callContext) => ResolveAgentAsync(id, snapshot, callContext),
            Settings = () => Agents.ResolveSettings(hostSettings()),
            Env = BuildEnvAsync,
            Now = Now,
            OnWake = options.OnWake,
            Timers = new HostTimers(options.SetTimer, NativeTimer.Of(options.TimeProvider ?? TimeProvider.System),
                error => report(error, ReportInfo.HostError(HostCallback.SetTimer))),
            Report = report,
            Telemetry = options.Telemetry,
            SettleOutcome = (tx, record, outcome) => Live.SettleSchedulerOutcomeAsync(tx, record, outcome),
            WithdrawInputs = (tx, id) => Inbox.WithdrawQueuedInputsAsync(tx, id),
            Conversation = async (id, binding, callContext) =>
            {
                var record = await ReadOnLineAsync(line => line.GetConversationAsync(id, callContext)).ConfigureAwait(false);
                return record is null ? null : new BoundConversation(id, binding, scheduler!, submissions!);
            },
            Context = context.WithoutCancellation(),
            ReadOnly = options.ReadOnly,
            LimitsRestarts = Live.LimitsRestarts.Contains,
        });
        submissions = new Submissions(this, Now, () => Agents.ResolveSettings(hostSettings()), () => scheduler.Resume(), options.Telemetry);
        views = new ConversationViews(this);
        taskGraph = new TaskGraphView(this, scheduler.BlockedReasonOf, scheduler.SubscribeBlocked);
    }

    /// <summary>The Harness clock.</summary>
    internal Func<double> Now { get; }

    internal IStorage Storage => storage;

    internal DurableTaskScheduler Scheduler => scheduler;

    /// <summary>Internal, for tests: sizes of the scheduler's ownership indexes (upstream <c>[SCHEDULER_INDEX_SIZES]</c>).</summary>
    internal SchedulerIndexSizes SchedulerIndexSizes => scheduler.IndexSizes;

    /// <summary>Internal, for tests: the kept contexts' idle state and its disagreements with a full recompute (upstream
    /// <c>[SCHEDULER_IDLE_STATE]</c>).</summary>
    internal SchedulerIdleState SchedulerIdleState => scheduler.IdleState;

    internal Submissions Submissions => submissions;

    /// <summary>The conversation view mounts (upstream <c>host.views</c>).</summary>
    internal ConversationViews Views => views;

    /// <summary><c>HarnessOptions.Models</c> as a function with <see cref="HarnessOptions.Identity"/> applied (upstream <c>currentModels</c>):
    /// plain models are wrapped once, so every call returns the same view; a function's result is wrapped at each call.</summary>
    private static Func<IDurableModels> CurrentModels(HarnessOptions options)
    {
        var merged = ClientIdentities.Merge(Harness.DefaultIdentity, options.Identity) ?? Harness.DefaultIdentity;
        if (options.CurrentModels is { } current) return () => DurableModelIdentity.WithClientIdentity(current(), merged);
        var models = options.Models ?? throw new ArgumentException("HarnessOptions needs Models or CurrentModels", nameof(options));
        var wrapped = DurableModelIdentity.WithClientIdentity(models, merged);
        return () => wrapped;
    }

    /// <summary><c>HarnessOptions.Settings</c> as a function (upstream <c>currentSettings</c>): plain settings, or none, are returned at every
    /// call.</summary>
    private static Func<HarnessSettings?> CurrentSettings(HarnessOptions options)
    {
        if (options.CurrentSettings is { } current) return current;
        var settings = options.Settings;
        return () => settings;
    }

    private static Func<double> Clock(HarnessOptions options)
    {
        if (options.Now is { } now) return now;
        var time = options.TimeProvider ?? TimeProvider.System;
        return () => time.GetUtcNow().ToUnixTimeMilliseconds();
    }

    /// <summary><c>OnReport</c> that cannot fail (upstream <c>safeReport</c>): it is the last place an error goes, so its own throw is
    /// dropped. It hears one <see cref="Durable.Report"/>, the error with what it is about. Each report then counts in
    /// <c>pi.durable.reports</c> by type and, with a context, is a <c>pi.durable.report</c> event on its span, with the report's fixed
    /// attributes and never the error; a throwing telemetry adapter is dropped too.</summary>
    internal static ReportSink SafeReport(Action<Report>? onReport, ITelemetryContext? telemetry = null)
    {
        var reports = TelemetrySchema.CreateTypedMetrics((telemetry ?? NoopTelemetry.Context).Metrics ?? NoopTelemetry.Metrics,
            DurableTelemetry.Schema).Counter("pi.durable.reports");
        return (error, info, context) =>
        {
            if (onReport is not null) Contained(() => onReport(new Report(info, error)), _ => { });
            var attributes = DurableTelemetry.ReportAttributes(info);
            try
            {
                reports.Add(1, new SpanAttributes { ["type"] = ReportInfo.KindName(info.Kind) });
                context?.Telemetry.AddEvent("pi.durable.report", attributes);
            }
            catch (Exception)
            {
                // Dropped: see above.
            }
        };
    }

    /// <summary>
    /// The host clock, falling back to the system clock when it throws (upstream <c>safeNow</c>): a broken clock must not stop commits. The
    /// first throw is reported; the clock is read in every task change, so later ones are not. <paramref name="host"/> is false for the
    /// Harness's own TimeProvider clock, which needs no guard.
    /// </summary>
    internal static Func<double> SafeNow(Func<double> now, bool host, Action<Exception, ReportInfo> report)
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
                if (Interlocked.Exchange(ref reported, 1) == 0) report(error, ReportInfo.HostError(HostCallback.Now));
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        };
    }

    /// <summary>The Session's failure and listener errors go to <c>OnReport</c>, with what they are about and the context they came
    /// from.</summary>
    internal override void Report(Exception error, ReportInfo info, DurableContext? context = null) => report(error, info, context);

    /// <summary>Resolves a conversation's committed <c>pi.agent</c> against <paramref name="snapshot"/>, or the current one, and the current
    /// settings.</summary>
    internal async Task<Agent> ResolveAgentAsync(ConversationId id, IRegistrySnapshot? snapshot, DurableContext context)
    {
        var registry = snapshot ?? options.Registry.Snapshot();
        var state = await SnapshotAsync(Agents.AgentDoc, id, context).ConfigureAwait(false);
        return Agents.ResolveAgent(state, registry, Agents.ResolveSettings(hostSettings()),
            (error, extension) => report(error, new ReportInfo(ReportKind.ExtensionError) { ConversationId = id, Extension = extension }, context));
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

    public Wake Wake()
    {
        AssertUsable();
        return scheduler.Wake();
    }

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => ReadOnLineAsync(line => line.GetTaskAsync(id, context));

    public Task<HarnessInspection> InspectAsync(DurableContext context) => ReadOnLineAsync(async line =>
    {
        var (scheduling, tasks) = await scheduler.InspectAsync(options.Registry.Snapshot()).ConfigureAwait(false);
        Task<List<SubmissionRecord>> Scan(SubmissionStatus status) => HarnessUtil.ScanAll(cursor =>
            line.ScanSubmissionsAsync(new SubmissionQuery { Status = status }, ScanPageSize, cursor, context));
        var submissions = (await Scan(SubmissionStatus.Queued).ConfigureAwait(false))
            .Concat(await Scan(SubmissionStatus.Placed).ConfigureAwait(false))
            .OrderBy(submission => submission.Id.Value).ToImmutableArray();
        return new HarnessInspection(scheduling, tasks, submissions);
    });

    public Task<ISubmission?> SubmissionAsync(SubmissionId id, DurableContext context) => submissions.GetAsync(id, context);

    public Task<SubmissionAbortResult> AbortSubmissionAsync(SubmissionId id, DurableContext context, ConversationId? conversationId = null) =>
        submissions.AbortAsync(id, context, conversationId);

    public Task<TaskAbortResult> AbortTaskAsync(TaskId id, DurableContext context) => scheduler.AbortAsync(id, context);

    public async Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context)
    {
        scheduler.Resume();
        return await scheduler.WaitForTaskAsync(id, context).ConfigureAwait(false);
    }

    public async Task WaitForIdleAsync(DurableContext context)
    {
        scheduler.Resume();
        await scheduler.WaitForIdleAsync(null, context).ConfigureAwait(false);
    }

    /// <summary>Sums every conversation's committed <c>pi.usage</c>. Each document is read at its own point; totals only grow.</summary>
    public async Task<JsonObjectValue> UsageAsync(DurableContext context)
    {
        var conversations = await ReadOnLineAsync(line => HarnessUtil.ScanAll(cursor =>
            line.ScanConversationsAsync(new ConversationQuery(), ScanPageSize, cursor, context))).ConfigureAwait(false);
        var total = UsageLedger.UsageDoc.Definition.Initial();
        foreach (var conversation in conversations)
        {
            var state = await SnapshotAsync(UsageLedger.UsageDoc, conversation.Id, context).ConfigureAwait(false);
            if (state is not null) total = UsageLedger.AddUsageState(total, state);
        }
        return total;
    }

    public Task<IWatchHandle<TaskGraph>> WatchTaskGraphAsync(DurableContext context) => taskGraph.WatchAsync(context);

    public async Task<Observation<TaskGraph>> ObserveTaskGraphAsync(Observer<TaskGraph> observer, DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(context);
        return await taskGraph.ObserveAsync(observer, context).ConfigureAwait(false);
    }

    public async Task<Observation<ConversationView>> ObserveConversationAsync(ConversationId id, Observer<ConversationView> observer,
        DurableContext context, ConversationWindow? window = null)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(context);
        return await views.ObserveAsync(id, observer, context, window).ConfigureAwait(false);
    }

    public Task<IConversation> RootAsync(DurableContext context, AgentChange? agent = null, ConversationInit? init = null) =>
        CreateAsync(CreateTarget.Root, agent, init, context);

    public async Task<IConversation?> ConversationAsync(ConversationId id, DurableContext context)
    {
        AssertUsable();
        var record = await ReadOnLineAsync(line => line.GetConversationAsync(id, context)).ConfigureAwait(false);
        return record is null ? null : new ConversationImpl(record.Id, this);
    }

    public Task<IConversation> CreateConversationAsync(ConversationCreateOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateAsync(CreateTarget.Independent(options.Ownership), options.Agent, options.Init, context);
    }

    /// <summary>Signals and joins task invocations after the close listeners and before Storage closes; writes no task outcome.</summary>
    internal override Task BeforeCloseAsync() => scheduler.JoinAsync();

    /// <summary>The ownership memberships a commit's task changes write, from the scheduler's indexes (upstream <c>membershipKeys</c>).</summary>
    internal override ValueTask<IReadOnlyList<string>> MembershipKeysAsync(Transaction tx) => scheduler.MembershipKeysAsync(tx);

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

    public Task<SubmissionId> SubmitAsync(ITx tx, SubmissionDraft submission) => harness.Submissions.AdmitAsync(tx, Id, submission);

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
    public async Task<TaskId> CompactAsync(string? instructions, DurableContext context)
    {
        harness.Scheduler.Resume();
        return await harness.CommitWithAsync(tx => Compaction.CreateCompactionAsync(tx, Id, "manual", instructions), context).ConfigureAwait(false);
    }

    public Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => change(tx), context, new TransactionScope(Id), options);
    }

    public Task CommitAsync(Func<ITx, Task> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => change(tx), context, new TransactionScope(Id), options);
    }

    public Task<T> CommitAsync<T>(Func<ITx, T> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => Task.FromResult(change(tx)), context, new TransactionScope(Id), options);
    }

    public Task CommitAsync(Action<ITx> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return harness.CommitWithAsync(tx => change(tx), context, new TransactionScope(Id), options);
    }

    public Task<ContextView> ContextAsync(DurableContext context, EntryId? at = null) =>
        ConversationContext.ReadContextAsync(harness, harness.Storage, Id, context, at);

    public Task<Page<EntryRecord>> EntriesAsync(EntryQueryBounds query, int limit, JsonObjectValue? cursor, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        var scan = HarnessUtil.ConversationEntries(Id, query);
        return harness.ReadOnLineAsync(line => line.ScanEntriesAsync(scan, limit, cursor, context));
    }

    public Task<IConversation> ForkAsync(EntryId at, ConversationCreateOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        return harness.CreateAsync(DurableHarness.CreateTarget.Fork(Id, at, options.Ownership), options.Agent, options.Init, context);
    }

    public async Task AbortAsync(DurableContext context, ConversationAbortOptions? options = null)
    {
        harness.Scheduler.Resume();
        await harness.Scheduler.AbortConversationAsync(Id, options?.Background == true, context).ConfigureAwait(false);
    }

    public async Task WaitForIdleAsync(DurableContext context)
    {
        harness.Scheduler.Resume();
        await harness.Scheduler.WaitForIdleAsync(Id, context).ConfigureAwait(false);
    }

    public Task<IWatchHandle<ConversationView>> WatchAsync(DurableContext context, ConversationWindow? window = null) =>
        harness.Views.WatchAsync(Id, context, window);

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

    /// <summary>Admits a submission; the returned submission's operations are bound to the invocation too.</summary>
    public async Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context)
    {
        binding.Check();
        var admitted = await submissions.SubmitAsync(Id, submission, context.WithSignal(binding.Signal)).ConfigureAwait(false);
        return new BoundSubmission(admitted, binding);
    }

    /// <summary>Admits a submission inside the caller's commit; rejects once the invocation is aborted or ended.</summary>
    public Task<SubmissionId> SubmitAsync(ITx tx, SubmissionDraft submission)
    {
        try
        {
            binding.Check();
            // The caller's commit carries its own context, so the invocation's abort is checked here.
            if (binding.Signal.Token.IsCancellationRequested) throw binding.Signal.Error();
        }
        catch (Exception error) { return Task.FromException<SubmissionId>(error); }
        return submissions.AdmitAsync(tx, Id, submission);
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
