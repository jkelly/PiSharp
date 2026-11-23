using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable;

/// <summary>What a running tool reported through its api: output, the last details and diagnostics, behind one lock (upstream
/// <c>Reported</c>, plus the <c>ended</c> flag).</summary>
internal sealed class ToolReport(OutputBuffer output, OutputLimits limits, IEnumerable<ToolDiagnostic>? startDiagnostics = null,
    JsonValue? startDetails = null)
{
    private readonly Lock gate = new();
    // A woken run starts from the diagnostics and details earlier runs left in the slot.
    private readonly List<ToolDiagnostic> diagnostics = [.. startDiagnostics ?? []];
    private JsonValue? details = startDetails;
    private bool ended;

    public OutputLimits Limits { get; } = limits;

    /// <summary>Rejects a report once the call has settled (upstream <c>assertLive</c>).</summary>
    private void AssertLive(string callId)
    {
        if (ended) throw new InvalidOperationException($"Tool call {callId} has settled");
    }

    /// <summary>Runs <paramref name="action"/> while the call is live, atomically with its end: nothing it registers can start after
    /// <see cref="MarkEnded"/>.</summary>
    public T WhileLive<T>(string callId, Func<T> action)
    {
        lock (gate)
        {
            AssertLive(callId);
            return action();
        }
    }

    public bool Push(string callId, string chunk, ShellOutputSkip? skipped)
    {
        lock (gate)
        {
            AssertLive(callId);
            return output.Push(chunk, skipped);
        }
    }

    public bool Push(string callId, ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped)
    {
        lock (gate)
        {
            AssertLive(callId);
            return output.Push(chunk, skipped);
        }
    }

    /// <summary>The output retained so far and whether earlier output was dropped (upstream <c>retainedOutput()</c>).</summary>
    public RetainedOutput Retained(string callId)
    {
        lock (gate)
        {
            AssertLive(callId);
            var retained = output.Snapshot();
            return new RetainedOutput(retained.Text, retained.DroppedBytes > 0);
        }
    }

    public void Diagnostic(string callId, ToolDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        lock (gate)
        {
            AssertLive(callId);
            diagnostics.Add(diagnostic);
        }
    }

    public void Details(string callId, JsonValue value, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            AssertLive(callId);
            context.ThrowIfCancellationRequested();
            details = value;
        }
    }

    /// <summary>The current snapshot, details, and the diagnostics added after the first <paramref name="from"/>.</summary>
    public (BoundedOutput Snapshot, JsonValue? Details, List<ToolDiagnostic> Added, int Count) Capture(int from)
    {
        lock (gate) return (output.Snapshot(), details, diagnostics.GetRange(from, diagnostics.Count - from), diagnostics.Count);
    }

    /// <summary>The retained output (when <paramref name="retain"/>), the last details and every diagnostic, once the call has ended.</summary>
    public (BoundedOutput? Retained, JsonValue? Details, List<ToolDiagnostic> Diagnostics) Final(bool retain)
    {
        lock (gate) return (retain ? output.Snapshot() : null, details, [.. diagnostics]);
    }

    /// <summary>Ends the call: later reports throw.</summary>
    public void MarkEnded()
    {
        lock (gate) ended = true;
    }

    /// <summary>Ends the call and flushes the output's incomplete trailing character.</summary>
    public void End()
    {
        lock (gate)
        {
            ended = true;
            output.End();
        }
    }
}

/// <summary>
/// The nested calls of one tool invocation (upstream <c>run</c>'s <c>admissions</c>, <c>sequence</c> and <c>madeNestedMemo</c>): default
/// keys by call order, the attempt's <c>madeNestedCalls</c> memo, and every admission it started. Upstream chains each admission on the
/// memo's promise, so the admissions of one synchronous stretch of the tool (such as a <c>Promise.all</c> of calls) enqueue their commits
/// back to back once the memo committed, before the scheduler reacts to any of them. Here the calls a tool makes before <c>Execute</c> first
/// returns its task (its synchronous prefix, on the invoking thread) are held and admitted together when it returns; admissions waiting
/// for the memo are started together, in call order, under one lock, which also orders them before any admission made later.
/// </summary>
internal sealed class NestedCalls(ITaskRuntime runtime, ToolReport reported, string callId, bool resumes, bool keyed = false)
{
    private readonly Lock gate = new();
    private readonly List<Task> admissions = [];
    private readonly List<Admission> waiting = [];
    private List<Admission>? held;
    private int sequence;
    [ThreadStatic] private static NestedCalls? prefix;
    /// <summary>The attempt's memo commit; null before the first call and after a failed one.</summary>
    private Task? madeNestedMemo;
    private bool memoCommitted;

    public string CallId { get; } = callId;

    /// <summary>Whether the caller is replay-safe: it reattaches to its nested calls and child tasks after a restart (upstream
    /// <c>resumes</c>).</summary>
    public bool Resumes { get; } = resumes;

    /// <summary>One nested call waiting for its admission commit.</summary>
    private sealed class Admission(string name, JsonValue args, string key, bool? progress, DurableContext context)
    {
        public TaskCompletionSource<TaskId> Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name { get; } = name;
        public JsonValue Args { get; } = args;
        public string Key { get; } = key;
        public bool? Progress { get; } = progress;
        public DurableContext Context { get; } = context;
    }

    /// <summary>Admits a nested call (upstream <c>executeTool</c> up to its admission): throws once the call settled, picks the key, and
    /// registers the admission so cleanup waits for it.</summary>
    public (string Key, Task<TaskId> Admission) Admit(string name, JsonValue args, NestedCallOptions? options, DurableContext context) =>
        reported.WhileLive(CallId, () =>
        {
            // The next position, for a nested call made without a key; after a wake a position may mean another call.
            if (options?.Key is null && keyed) throw NeedsKeys(CallId);
            var key = options?.Key ?? (++sequence).ToString(CultureInfo.InvariantCulture);
            if (options?.Key is not null) ToolPhases.CheckKey(options.Key);
            var admission = new Admission(name, args, key, options?.Progress, context);
            lock (gate)
            {
                admissions.Add(admission.Admitted.Task);
                if (ReferenceEquals(prefix, this))
                {
                    (held ??= []).Add(admission);
                    return (key, admission.Admitted.Task);
                }
            }
            Admit([admission]);
            return (key, admission.Admitted.Task);
        });

    /// <summary>What a call without a key is refused with after a wake.</summary>
    internal static InvalidOperationException NeedsKeys(string callId) => new($"Tool call {callId} needs explicit keys after a wake");

    /// <summary>Marks the start of the tool's synchronous prefix on this thread (see the class summary).</summary>
    public void BeginPrefix() => prefix = this;

    /// <summary>Ends the prefix and admits the calls it made, in order.</summary>
    public void EndPrefix()
    {
        prefix = null;
        List<Admission>? calls;
        lock (gate)
        {
            calls = held;
            held = null;
        }
        if (calls is not null) Admit(calls);
    }

    private void Admit(List<Admission> calls)
    {
        Task? memo = null;
        lock (gate)
        {
            if (memoCommitted)
            {
                StartAll(calls);
                return;
            }
            waiting.AddRange(calls);
            // The memo first, once per attempt; a failed one is tried again by the next call.
            if (madeNestedMemo is null) madeNestedMemo = memo = ToolPhases.CommitMadeNestedCallsAsync(runtime, StartWaiting, calls[0].Context);
        }
        memo?.ContinueWith(MemoSettled, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Starts the admissions that waited for the memo, in call order. Runs inside the memo's commit, on the Session line: their commits queue
    /// right behind it, before the scheduler can react to the memo's publication, as upstream's run once the memo resolves. Each admission
    /// checks the memo is committed before it creates anything.
    /// </summary>
    private void StartWaiting()
    {
        lock (gate)
        {
            memoCommitted = true;
            var queued = waiting.ToList();
            waiting.Clear();
            StartAll(queued);
        }
    }

    /// <summary>A memo commit that failed fails the admissions still waiting for it; the next call tries the memo again.</summary>
    private void MemoSettled(Task memo)
    {
        if (memo.IsCompletedSuccessfully) return;
        lock (gate)
        {
            memoCommitted = false;
            madeNestedMemo = null;
            var queued = waiting.ToList();
            waiting.Clear();
            var error = memo.Exception?.InnerException ?? new OperationCanceledException();
            foreach (var admission in queued) admission.Admitted.TrySetException(error);
        }
    }

    /// <summary>
    /// Enqueues the admissions' commits (upstream <c>startNestedCall</c>), in call order, back to back on the Session line
    /// (<see cref="SessionLine.Together"/>): an idle line would otherwise run the first at once and let the scheduler's reaction to it in
    /// before the next, and a job another thread enqueues meanwhile (a reservation pass the thread pool started, say) could queue between
    /// them, so the second call's documents were created a commit later than upstream's. Called under the lock.
    /// </summary>
    private void StartAll(List<Admission> calls)
    {
        if (calls.Count == 0) return;
        SessionLine.Together(() =>
        {
            foreach (var admission in calls) Start(admission);
        });
    }

    private void Start(Admission admission)
    {
        Task<TaskId> started;
        try
        {
            started = ToolPhases.StartNestedCallAsync(runtime, CallId, admission.Name, admission.Args, admission.Key, admission.Progress,
                abandonOnRestart: !Resumes, admission.Context);
        }
        catch (Exception error)
        {
            started = Task.FromException<TaskId>(error);
        }
        started.ContinueWith(static (task, state) =>
        {
            var admitted = ((Admission)state!).Admitted;
            if (task.IsCompletedSuccessfully) admitted.TrySetResult(task.GetAwaiter().GetResult());
            else if (task.IsCanceled) admitted.TrySetCanceled();
            else admitted.TrySetException(task.Exception!.InnerExceptions);
        }, admission, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Waits for every admission started, whatever its outcome (upstream <c>Promise.allSettled(admissions)</c>).</summary>
    public async Task AdmissionsSettledAsync()
    {
        Task[] started;
        lock (gate) started = [.. admissions];
        foreach (var admission in started)
            await admission.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .ConfigureAwait(false);
    }
}

/// <summary>The tool execution api of one call (upstream's <c>api</c> object built by <c>run</c>).</summary>
internal sealed class ToolExecutionApi : IToolExecutionApi
{
    private readonly ITaskRuntime runtime;
    private readonly ToolReport reported;
    private readonly Progress progress;
    private readonly NestedCalls nested;
    private readonly ToolPhases.Resumed resumed;

    public ToolExecutionApi(ITaskRuntime runtime, string callId, ToolReport reported, Progress progress, NestedCalls nested,
        ToolPhases.Resumed resumed)
    {
        this.runtime = runtime;
        this.reported = reported;
        this.progress = progress;
        this.nested = nested;
        this.resumed = resumed;
        CallId = callId;
        Registry = runtime.Registry;
        OutputWindow = reported.Limits.Retain == ToolOutputRetention.Tail
            ? new ShellOutputWindow(reported.Limits.MaxBytes, reported.Limits.MaxLines, runtime.Settings.Progress.OutputIntervalMs,
                ToolOutput.ProgressBytesPerSecond)
            : null;
    }

    public TaskId TaskId => runtime.TaskId;
    public ConversationId ConversationId => runtime.ConversationId;
    public string CallId { get; }
    public WakeReason? Wake => resumed.Wake;
    public JsonValue? Checkpoint => resumed.Checkpoint;
    public IRegistrySnapshot Registry { get; }
    public IDurableModels Models => runtime.Models;
    /// <summary>Built for this run once the attempt exists (upstream's <c>attempt.env</c>, read by the api's getter).</summary>
    public IExecutionEnv? Env { get; internal set; }
    public ShellOutputWindow? OutputWindow { get; }

    /// <summary>The parks the wrapped base returned in this run (upstream's <c>BASE_PARKS</c> entry of the api); compared by reference and
    /// guarded by itself.</summary>
    internal HashSet<ToolPark> BaseParks { get; } = new(ReferenceEqualityComparer.Instance);

    public double Now() => runtime.Now();

    public Task<Agent> AgentAsync(DurableContext context) => runtime.AgentAsync(context);

    public void Output(string chunk, ShellOutputSkip? skipped = null)
    {
        if (reported.Push(CallId, chunk, skipped)) progress.Mark();
    }

    public void Output(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null)
    {
        if (reported.Push(CallId, chunk, skipped)) progress.Mark();
    }

    public RetainedOutput RetainedOutput() => reported.Retained(CallId);

    public void Diagnostic(ToolDiagnostic diagnostic)
    {
        reported.Diagnostic(CallId, diagnostic);
        progress.Mark();
    }

    public Task DetailsAsync(JsonValue value, DurableContext context)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(context);
            reported.Details(CallId, value, context);
            var committed = progress.MarkAndWait();
            // Cancelling the wait leaves the update in place; the commit's own outcome stays observed.
            _ = committed.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return context.WaitAsync(committed);
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    public async Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        T result = default!;
        await runtime.CommitAsync(async (tx, _) => { result = await change(tx).ConfigureAwait(false); }, context, options).ConfigureAwait(false);
        return result;
    }

    public Task CommitAsync(Func<ITx, Task> change, DurableContext context, CommitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return runtime.CommitAsync(async (tx, _) => await change(tx).ConfigureAwait(false), context, options);
    }

    public Task<JsonValue?> MemoAsync(string name, DurableContext context) => runtime.MemoAsync(name, context);
    public Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context) => runtime.MemoAsync(name, candidate, context);
    public Task<SignalState?> ReadSignalAsync(SignalToken token, string key, DurableContext context) => runtime.ReadSignalAsync(token, key, context);

    /// <summary>
    /// Pi v1.2.0's default for <c>api.createTask()</c> (tool.ts): a child task of a tool that is not replay-safe gets
    /// <see cref="TaskOptions.AbandonOnRestart"/>, as such a caller never resumes after a restart; an explicit value wins (upstream
    /// <c>{ abandonOnRestart: true, ...options }</c>).
    /// </summary>
    internal static TaskOptions ChildTaskOptions(TaskOptions options, bool resumes) =>
        options.Ownership.TaskId is not null && !resumes && options.AbandonOnRestart is null ? options with { AbandonOnRestart = true } : options;

    public async Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (resumed.Wake is not null) throw NestedCalls.NeedsKeys(CallId);
        var child = ChildTaskOptions(options, nested.Resumes);
        TaskId id = default;
        await runtime.CommitAsync(async (tx, _) => { id = await tx.CreateTaskAsync(task, input, child).ConfigureAwait(false); }, context)
            .ConfigureAwait(false);
        return id;
    }

    public async Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, string key, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(key);
        ToolPhases.CheckKey(key);
        var child = ChildTaskOptions(options, nested.Resumes);
        TaskId id = default;
        await runtime.CommitAsync(async (tx, _) =>
        {
            // Opened, or created empty for a key not used yet.
            var member = await tx.DocAsync(HarnessTasks.NestedCallDoc, runtime.TaskId, key, JsonValue.Null).ConfigureAwait(false);
            if (member.Get("taskId") is JsonNumberValue used)
            {
                var existingId = new TaskId((long)used.Value);
                var existing = member.Get("kind") is JsonStringValue { Value: "task" } ? await tx.GetTaskAsync(existingId).ConfigureAwait(false) : null;
                if (existing?.Kind != task.Definition.Name || !JsonValue.DeepEquals(existing.Input, input))
                    throw new InvalidOperationException($"Key {key} of tool call {CallId} was already used for another call or task");
                id = existingId;
                return;
            }
            id = await tx.CreateTaskAsync(task, input, child).ConfigureAwait(false);
            member.Set("kind", "task");
            member.Set("taskId", id.ToJson());
        }, context).ConfigureAwait(false);
        return id;
    }

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => runtime.GetTaskAsync(id, context);
    public Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context) => runtime.WaitForTaskAsync(id, context);
    public Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context) => runtime.ConversationAsync(id, context);

    /// <summary>
    /// Runs <paramref name="calls"/>, which makes nested calls of <paramref name="api"/>'s tool call on this thread, and admits those calls
    /// together, as the calls a tool makes in one synchronous stretch: upstream's <c>executeTool()</c> calls of one stretch chain on the
    /// attempt's memo promise, so their admission commits enqueue back to back, in call order, before the scheduler reacts to any of them.
    /// For a tool whose calls come after an await, such as code mode launching the calls its journal commit covered (NestedCalls admits
    /// such calls one at a time otherwise). An <see cref="IToolExecutionApi"/> that is not the Harness's own just runs
    /// <paramref name="calls"/>.
    /// </summary>
    internal static void AdmitTogether(IToolExecutionApi api, Action calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (api is not ToolExecutionApi own)
        {
            calls();
            return;
        }
        own.nested.BeginPrefix();
        try
        {
            calls();
        }
        finally
        {
            own.nested.EndPrefix();
        }
    }

    public Task<NestedToolExecutionResult> ExecuteToolAsync(string name, JsonObjectValue arguments, DurableContext context,
        NestedCallOptions? options = null) => ExecuteToolValueAsync(name, arguments, context, options);

    public Task<NestedToolCall> StartToolAsync(string name, JsonObjectValue arguments, DurableContext context, NestedCallOptions? options = null) =>
        StartToolValueAsync(name, arguments, context, options);

    /// <summary>
    /// <see cref="IToolExecutionApi.ExecuteToolAsync"/> with arguments of any JSON kind, as upstream's <c>executeTool()</c> passes whatever
    /// its caller gives: a code mode script's <c>tools.x("text")</c> reaches the nested call as is, where the tool's <c>prepareArguments</c>
    /// may repair it and validation reports what is not an object. An <see cref="IToolExecutionApi"/> that is not the Harness's own takes
    /// only objects.
    /// </summary>
    internal static Task<NestedToolExecutionResult> ExecuteToolAsync(IToolExecutionApi api, string name, JsonValue arguments, DurableContext context,
        NestedCallOptions? options = null) => api switch
        {
            ToolExecutionApi own => own.ExecuteToolValueAsync(name, arguments, context, options),
            _ when arguments is JsonObjectValue keyed => api.ExecuteToolAsync(name, keyed, context, options),
            _ => Task.FromException<NestedToolExecutionResult>(new ArgumentException("Nested call arguments must be a JSON object", nameof(arguments))),
        };

    private async Task<NestedToolExecutionResult> ExecuteToolValueAsync(string name, JsonValue arguments, DurableContext context,
        NestedCallOptions? options) =>
        await (await StartToolValueAsync(name, arguments, context, options).ConfigureAwait(false)).ResultAsync(context).ConfigureAwait(false);

    /// <summary>Starts or finds the nested call and returns its handle (upstream <c>executeTool</c> up to its <c>nested</c> object).</summary>
    private async Task<NestedToolCall> StartToolValueAsync(string name, JsonValue arguments, DurableContext context, NestedCallOptions? options)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        var (key, admission) = nested.Admit(name, arguments, options, context);
        var id = await admission.ConfigureAwait(false);
        return new NestedToolCall(id, async resultContext =>
        {
            var settled = await runtime.WaitForTaskAsync(id, resultContext).ConfigureAwait(false);
            var stored = await runtime.SnapshotAsync(HarnessTasks.NestedCallDoc, runtime.TaskId, key, resultContext).ConfigureAwait(false);
            // A nested call the scheduler faulted or orphaned stored no result.
            return stored is not null && stored.TryGetValue("result", out var result)
                ? NestedToolExecutionResult.FromJson(result)
                : ToolPhases.FallbackResult(id, name, ((SettlingTaskState)settled.State).Outcome);
        });
    }

    public Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => runtime.SnapshotAsync(token, context);
    public Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        runtime.SnapshotAsync(token, conversationId, context);
    public Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) => runtime.SnapshotAsync(token, taskId, context);
    public Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) => runtime.SnapshotAsync(token, key, context);
    public Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
        runtime.SnapshotAsync(token, conversationId, key, context);
    public Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        runtime.SnapshotAsync(token, taskId, key, context);
    public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at, DurableContext context) =>
        runtime.SnapshotAsOfAsync(token, conversationId, at, context);
    public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key, EntryId at,
        DurableContext context) => runtime.SnapshotAsOfAsync(token, conversationId, key, at, context);

    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocToken token, DurableContext context) => runtime.WatchDocAsync(token, context);
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        runtime.WatchDocAsync(token, conversationId, context);
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
        runtime.WatchDocAsync(token, taskId, context);
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
        runtime.WatchDocAsync(token, key, context);
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        DurableContext context) => runtime.WatchDocAsync(token, conversationId, key, context);
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        runtime.WatchDocAsync(token, taskId, key, context);
}

/// <summary>
/// A tool execution api that forwards every member to another (upstream's <c>{ ...api, output, outputWindow }</c>). A tool wrapper derives
/// from it and overrides what it changes; one that replaces <see cref="Output(string, ShellOutputSkip?)"/> and transforms text must also
/// return null from <see cref="OutputWindow"/>.
/// </summary>
public class DelegatingToolExecutionApi(IToolExecutionApi inner) : IToolExecutionApi
{
    /// <summary>The api this one forwards to.</summary>
    protected IToolExecutionApi Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The api this one forwards to, for the Harness: the parks a wrapped base returns are recorded in its run's own api
    /// (<see cref="Parks.CarrierOf"/>).</summary>
    internal IToolExecutionApi InnerApi => Inner;

    /// <inheritdoc/>
    public virtual TaskId TaskId => Inner.TaskId;
    /// <inheritdoc/>
    public virtual ConversationId ConversationId => Inner.ConversationId;
    /// <inheritdoc/>
    public virtual string CallId => Inner.CallId;
    /// <inheritdoc/>
    public virtual WakeReason? Wake => Inner.Wake;
    /// <inheritdoc/>
    public virtual JsonValue? Checkpoint => Inner.Checkpoint;
    /// <inheritdoc/>
    public virtual IRegistrySnapshot Registry => Inner.Registry;
    /// <inheritdoc/>
    public virtual IDurableModels Models => Inner.Models;
    /// <inheritdoc/>
    public virtual IExecutionEnv? Env => Inner.Env;
    /// <inheritdoc/>
    public virtual double Now() => Inner.Now();
    /// <inheritdoc/>
    public virtual ShellOutputWindow? OutputWindow => Inner.OutputWindow;
    /// <inheritdoc/>
    public virtual RetainedOutput RetainedOutput() => Inner.RetainedOutput();
    /// <inheritdoc/>
    public virtual Task<Agent> AgentAsync(DurableContext context) => Inner.AgentAsync(context);
    /// <inheritdoc/>
    public virtual void Output(string chunk, ShellOutputSkip? skipped = null) => Inner.Output(chunk, skipped);
    /// <inheritdoc/>
    public virtual void Output(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null) => Inner.Output(chunk, skipped);
    /// <inheritdoc/>
    public virtual void Diagnostic(ToolDiagnostic diagnostic) => Inner.Diagnostic(diagnostic);
    /// <inheritdoc/>
    public virtual Task DetailsAsync(JsonValue value, DurableContext context) => Inner.DetailsAsync(value, context);
    /// <inheritdoc/>
    public virtual Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context, CommitOptions? options = null) =>
        Inner.CommitAsync(change, context, options);
    /// <inheritdoc/>
    public virtual Task CommitAsync(Func<ITx, Task> change, DurableContext context, CommitOptions? options = null) =>
        Inner.CommitAsync(change, context, options);
    /// <inheritdoc/>
    public virtual Task<JsonValue?> MemoAsync(string name, DurableContext context) => Inner.MemoAsync(name, context);
    /// <inheritdoc/>
    public virtual Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context) => Inner.MemoAsync(name, candidate, context);
    /// <inheritdoc/>
    public virtual Task<SignalState?> ReadSignalAsync(SignalToken token, string key, DurableContext context) => Inner.ReadSignalAsync(token, key, context);
    /// <inheritdoc/>
    public virtual Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, DurableContext context) =>
        Inner.CreateTaskAsync(task, input, options, context);
    /// <inheritdoc/>
    public virtual Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, string key, DurableContext context) =>
        Inner.CreateTaskAsync(task, input, options, key, context);
    /// <inheritdoc/>
    public virtual Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => Inner.GetTaskAsync(id, context);
    /// <inheritdoc/>
    public virtual Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context) => Inner.WaitForTaskAsync(id, context);
    /// <inheritdoc/>
    public virtual Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context) => Inner.ConversationAsync(id, context);
    /// <inheritdoc/>
    public virtual Task<NestedToolExecutionResult> ExecuteToolAsync(string name, JsonObjectValue arguments, DurableContext context,
        NestedCallOptions? options = null) => Inner.ExecuteToolAsync(name, arguments, context, options);
    /// <inheritdoc/>
    public virtual Task<NestedToolCall> StartToolAsync(string name, JsonObjectValue arguments, DurableContext context,
        NestedCallOptions? options = null) => Inner.StartToolAsync(name, arguments, context, options);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => Inner.SnapshotAsync(token, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        Inner.SnapshotAsync(token, conversationId, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) => Inner.SnapshotAsync(token, taskId, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) => Inner.SnapshotAsync(token, key, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
        Inner.SnapshotAsync(token, conversationId, key, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        Inner.SnapshotAsync(token, taskId, key, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at,
        DurableContext context) => Inner.SnapshotAsOfAsync(token, conversationId, at, context);
    /// <inheritdoc/>
    public virtual Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key,
        EntryId at, DurableContext context) => Inner.SnapshotAsOfAsync(token, conversationId, key, at, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocToken token, DurableContext context) => Inner.WatchDocAsync(token, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        Inner.WatchDocAsync(token, conversationId, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
        Inner.WatchDocAsync(token, taskId, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
        Inner.WatchDocAsync(token, key, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        DurableContext context) => Inner.WatchDocAsync(token, conversationId, key, context);
    /// <inheritdoc/>
    public virtual Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        Inner.WatchDocAsync(token, taskId, key, context);
}

/// <summary>Tool result helpers (upstream <c>tool.ts</c>).</summary>
internal static class ToolResults
{
    /// <summary>An error result the Harness writes itself: no content and one <c>error</c> diagnostic with <paramref name="code"/> (upstream
    /// <c>harnessError</c>).</summary>
    public static ToolExecutionResult HarnessError(string code, string message) => new()
    {
        Output = JsonValue.Array(),
        IsError = true,
        Diagnostics = [Diagnostic(code, message)],
    };

    /// <summary>An <c>error</c> diagnostic (upstream <c>toolDiagnostic</c>).</summary>
    public static ToolDiagnostic Diagnostic(string code, string message) => new(ToolDiagnosticSeverity.Error, message, code);

    /// <summary>
    /// Appends a <c>pi.tool-result</c> entry (upstream <c>appendToolResult</c>). The content ends with the rendered diagnostics, so the
    /// stored message is exactly what the model sees; <c>data</c> keeps the structured list. A result's usage is added to <c>pi.usage</c> in
    /// the same commit. <paramref name="call"/> is the pi-ai <c>ToolCall</c>.
    /// </summary>
    public static async Task<TypedEntry<JsonObjectValue>> AppendToolResultAsync(ITx tx, ConversationId conversationId, JsonObjectValue call,
        ToolExecutionResult result, double timestamp, double? durationMs = null)
    {
        var diagnostics = (result.Diagnostics ?? []).Select(DiagnosticJson).ToList();
        var content = (result.Output ?? JsonArrayValue.Empty).ToList();
        if (diagnostics.Count > 0)
            content.Add(new RecordJson.Writer().Add("type", "text").Add("text", RenderDiagnostics(result.Diagnostics!.Value)).Build());
        var name = ((JsonStringValue)call["name"]).Value;
        var message = new RecordJson.Writer()
            .Add("role", "toolResult")
            .Add("toolCallId", call["id"])
            .Add("toolName", name)
            .Add("content", JsonValue.Array(content))
            .Add("details", result.Details)
            .Add("usage", result.Usage)
            .Add("isError", result.IsError ?? false)
            .Number("durationMs", durationMs)
            .Number("timestamp", timestamp)
            .Build();
        if (result.Usage is { } usage) await UsageLedger.RecordUsageAsync(tx, conversationId, "tools", name, usage).ConfigureAwait(false);
        var data = JsonValue.Object([KeyValuePair.Create("diagnostics", (JsonValue)JsonValue.Array(diagnostics))]);
        return await tx.AppendEntryAsync(Entries.ToolResultEntry, conversationId,
            new TypedEntryDraft<JsonObjectValue> { Model = JsonValue.Array(message), Data = data }).ConfigureAwait(false);
    }

    /// <summary>A diagnostic's JSON in upstream <c>toolDiagnostic</c>'s key order: <c>{severity, code?, message}</c>.</summary>
    public static JsonValue DiagnosticJson(ToolDiagnostic diagnostic) => new RecordJson.Writer()
        .Add("severity", WireName.Of(diagnostic.Severity)).Add("code", diagnostic.Code).Add("message", diagnostic.Message).Build();

    /// <summary>Reads a diagnostic a slot stored as <see cref="DiagnosticJson"/>.</summary>
    public static ToolDiagnostic ParseDiagnostic(JsonValue json)
    {
        var value = (JsonObjectValue)json;
        return new ToolDiagnostic(
            WireName.Parse<ToolDiagnosticSeverity>(((JsonStringValue)value["severity"]).Value, "tool diagnostic"),
            ((JsonStringValue)value["message"]).Value,
            value.TryGetValue("code", out var code) && code is JsonStringValue text ? text.Value : null);
    }

    /// <summary>A control's JSON: <c>{addTools?, terminate?, handoff?}</c>, an unset field omitted.</summary>
    public static JsonObjectValue ControlJson(ToolControl control)
    {
        var writer = new RecordJson.Writer();
        if (control.AddTools is { } names)
            writer.Add("addTools", JsonValue.Array(names.Select(name => (JsonValue)JsonValue.String(name ??
                throw new ArgumentException("Tool control addTools contains null and is not strict JSON")))));
        if (control.Terminate is { } terminate) writer.Add("terminate", terminate);
        return writer.Add("handoff", control.Handoff).Build();
    }

    private static string RenderDiagnostics(IEnumerable<ToolDiagnostic> diagnostics) =>
        $"<harness>\n{string.Join("\n", diagnostics.Select(diagnostic => $"[{WireName.Of(diagnostic.Severity)}] {diagnostic.Message}"))}\n</harness>";
}
