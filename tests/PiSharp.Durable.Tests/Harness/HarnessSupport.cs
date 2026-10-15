// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-support.ts and packages/durable/test/task-support.ts
// (session-support.ts's ControlledStorage, context and helpers are in Session/SessionSupport.cs). The harness.* case groups run on any
// storage: HarnessCases() runs them on MemoryStorage, HarnessBackendCases.cs reruns the task groups on JSONL and SQLite.
//
// Shared adaptations, repeated where they matter:
// - flush() (one macrotask turn) is Flush(): a short real delay. It only ever stands for "let pending work progress"; a positive
//   condition upstream reaches after a flush is awaited with Eventually (a bounded poll) or a deterministic barrier instead
//   (InvocationEnded: the step after a phase ended its invocation; a deferred the handler resolves when it starts).
// - settled(promise) is Settled(task): whether the task completed after a Flush. Used only for negative checks.
// - Deferreds are TaskCompletionSource barriers whose waiters continue asynchronously, as a promise's then-callbacks do.
// - aborted(signal) is Aborted(token): it rejects with OperationCanceledException (upstream's AbortError) once the runtime signal aborts.
// - Handlers may run on different thread-pool threads at once, so lists they append to are SyncList.
// - createModels() is EmptyDurableModels; pi-ai's faux provider is FauxModels in ChatSupport.cs.
// - vi.useFakeTimers is ManualTimeProvider passed as HarnessOptions.TimeProvider (with Now reading it); it counts live timers as
//   vi.getTimerCount() does.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessCases() =>
    [
        .. HarnessTests.RegistryCases("harness.registry."),
        .. HarnessTests.TaskCases("harness.tasks.", () => new PiSharp.Durable.Storage.MemoryStorage()),
    ];
}

/// <summary>The harness.* case groups and their support.</summary>
internal static partial class HarnessTests
{
    internal static readonly DurableContext Context = DurableContext.Background;

    // ─── harness-support.ts ─────────────────────────────────────────────────

    /// <summary>A tool without parameters whose execution returns no content (upstream <c>tool</c>).</summary>
    internal static ToolRegistration Tool(string name, string? description = null) => Define.Tool(new ToolRegistration
    {
        Name = name,
        Description = description ?? $"{name} tool",
        Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty }),
    });

    /// <summary>A Harness and the registry it reads.</summary>
    internal sealed record OpenedHarness(IHarness Harness, IRegistry Registry);

    /// <summary>Opens a Harness with a fresh registry holding the named tools (upstream <c>openHarness</c>).</summary>
    internal static async Task<OpenedHarness> OpenHarness(IStorage storage, IReadOnlyList<string>? toolNames = null, IRegistry? registry = null,
        Action<Exception>? onReport = null, HarnessSettings? settings = null, IDurableModels? models = null)
    {
        registry ??= Registry.Create();
        if (toolNames is { Count: > 0 }) registry.Install(Define.Extension(new Extension { Name = "tools", Tools = [.. toolNames.Select(name => Tool(name))] }));
        var harness = await Harness.OpenAsync(storage, new HarnessOptions
        {
            Models = models ?? EmptyDurableModels.Instance,
            Registry = registry,
            OnReport = onReport,
            Settings = settings,
        }, Context);
        return new OpenedHarness(harness, registry);
    }

    /// <summary>A pi-ai user message with text content (upstream <c>user</c>).</summary>
    internal static JsonObjectValue User(string text) => Obj(("role", "user"), ("content", text), ("timestamp", 1));

    /// <summary>A pi-ai assistant message with a text block and tool calls named <c>tool-{id}</c> (upstream <c>assistant</c>).</summary>
    internal static JsonObjectValue Assistant(string text, IReadOnlyList<string>? calls = null, string? stopReason = null)
    {
        var content = new List<JsonValue> { Obj(("type", "text"), ("text", text)) };
        foreach (var id in calls ?? []) content.Add(Obj(("type", "toolCall"), ("id", id), ("name", $"tool-{id}"), ("arguments", JsonObjectValue.Empty)));
        return Obj(("role", "assistant"), ("content", JsonValue.Array(content)), ("api", "faux"), ("provider", "faux"), ("model", "faux"),
            ("usage", O("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""")),
            ("stopReason", stopReason ?? (calls is { Count: > 0 } ? "toolUse" : "stop")), ("timestamp", 2));
    }

    /// <summary>A pi-ai tool result message (upstream <c>toolResult</c>).</summary>
    internal static JsonObjectValue ToolResult(string id, string? text = null) => Obj(("role", "toolResult"), ("toolCallId", id), ("toolName", $"tool-{id}"),
        ("content", JsonValue.Array(Obj(("type", "text"), ("text", text ?? $"result {id}")))), ("isError", false), ("timestamp", 3));

    /// <summary>A pi-ai system message with sections; a null section value removes it (upstream <c>system</c>).</summary>
    internal static JsonObjectValue SystemMessage(params (string Key, string? Text)[] sections) => Obj(("role", "system"), ("content", ""),
        ("sections", JsonValue.Object(sections.Select(section => KeyValuePair.Create(section.Key, section.Text is null ? JsonValue.Null : (JsonValue)section.Text)))),
        ("timestamp", 4));

    /// <summary>Compact message rendering for assertions (upstream <c>describeMessage</c>).</summary>
    internal static string DescribeMessage(JsonValue message)
    {
        var value = (JsonObjectValue)message;
        string? FirstText() => value["content"] is JsonArrayValue content
            ? content.OfType<JsonObjectValue>().FirstOrDefault(item => item["type"] is JsonStringValue { Value: "text" }) is { } text ? S(text["text"]) : null
            : null;
        return S(value["role"]) switch
        {
            "user" => $"user:{(value["content"] is JsonStringValue content ? content.Value : value["content"].ToJson())}",
            "assistant" => $"assistant:{FirstText() ?? ""}",
            "toolResult" => $"result:{S(value["toolCallId"])}:{(value["isError"] is JsonBooleanValue { Value: true } ? "error" : FirstText() ?? "")}",
            "system" => $"system:{string.Join(",", value.TryGetValue("sections", out var sections) && sections is JsonObjectValue keyed ? keyed.Keys : [])}",
            var role => role,
        };
    }

    /// <summary>Uninstalls what one of the helpers below installed (upstream <c>Installed</c>).</summary>
    internal sealed class Installed(IRegistry registry, Extension extension) : IDisposable
    {
        public void Dispose() => registry.Uninstall(extension);
    }

    private static Installed InstallOne(IRegistry registry, Extension extension)
    {
        registry.Install(extension);
        return new Installed(registry, extension);
    }

    /// <summary>Installs a one-tool extension named after the tool (upstream <c>addTool</c>).</summary>
    internal static Installed AddTool(IRegistry registry, ToolRegistration tool, string? name = null) =>
        InstallOne(registry, new Extension { Name = name ?? $"tool:{tool.Name}", Tools = [tool] });

    /// <summary>Installs a one-task extension named after the task (upstream <c>addTask</c>).</summary>
    internal static Installed AddTask(IRegistry registry, DurableTask task, string? name = null) =>
        InstallOne(registry, new Extension { Name = name ?? $"task:{task.Definition.Name}", Tasks = [task] });

    private static int hookExtensions;

    /// <summary>Installs an extension with one hook registration for <paramref name="task"/> (upstream <c>addHooks</c>).</summary>
    internal static Installed AddHooks(IRegistry registry, DurableTask task, object handlers, string? name = null) =>
        InstallOne(registry, new Extension { Name = name ?? $"hooks:{Interlocked.Increment(ref hookExtensions)}", Hooks = [Define.Hook(task, handlers)] });

    /// <summary>Installs a one-section extension named after the section (upstream <c>addSection</c>).</summary>
    internal static Installed AddSection(IRegistry registry, string key, PromptRender render, bool? tag = null, string? name = null) =>
        InstallOne(registry, new Extension { Name = name ?? $"section:{key}", Sections = [Define.Section(key, render, tag)] });

    // ─── task-support.ts ────────────────────────────────────────────────────

    /// <summary>A deferred (upstream <c>deferred()</c>); waiters continue asynchronously.</summary>
    internal sealed class Deferred<T>
    {
        private readonly TaskCompletionSource<T> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> Task => source.Task;
        public void Resolve(T value) => source.TrySetResult(value);
        public void Reject(Exception error) => source.TrySetException(error);
    }

    /// <summary>A deferred without a value.</summary>
    internal sealed class Deferred
    {
        private readonly TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => source.Task;
        public void Resolve() => source.TrySetResult();
        public void Reject(Exception error) => source.TrySetException(error);
    }

    /// <summary>Rejects once <paramref name="signal"/> aborts; for handlers that block until cancelled (upstream <c>aborted</c>).</summary>
    internal static Task Aborted(CancellationToken signal)
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (signal.IsCancellationRequested) aborted.TrySetException(new OperationCanceledException(signal));
        else signal.Register(() => aborted.TrySetException(new OperationCanceledException(signal)));
        return aborted.Task;
    }

    /// <summary>Upstream <c>flush()</c>: lets pending work progress (see the file header).</summary>
    internal static Task Flush() => Task.Delay(10);

    /// <summary>Flushes until <paramref name="check"/> holds (upstream <c>eventually</c>), for at most about 20 seconds.</summary>
    internal static async Task Eventually(Func<Task<bool>> check)
    {
        for (var attempt = 0; attempt < 2000; attempt++)
        {
            if (await check()) return;
            await Flush();
        }
        throw new InvalidOperationException("Condition was not reached");
    }

    /// <inheritdoc cref="Eventually(Func{Task{bool}})"/>
    internal static Task Eventually(Func<bool> check) => Eventually(() => Task.FromResult(check()));

    /// <summary>Whether <paramref name="task"/> has settled after pending work flushes (upstream <c>settled</c>).</summary>
    internal static async Task<bool> Settled(Task task)
    {
        await Flush();
        return task.IsCompleted;
    }

    /// <summary>
    /// A deterministic barrier for upstream's "the step after the phase ends the invocation; await flush()": completes once the task has no
    /// live invocation, or its invocation has fully ended.
    /// </summary>
    internal static Task InvocationEnded(IHarness harness, TaskId id) => ((DurableHarness)harness).Scheduler.InvocationDone(id);

    /// <summary>A Harness, the registry it reads, and every failure passed to <c>OnReport</c>.</summary>
    internal sealed record OpenedTasks(IHarness Harness, IRegistry Registry, SyncList<Exception> Reports);

    /// <summary>Opens a Harness whose registry holds <paramref name="tasks"/>; failures passed to <c>OnReport</c> are collected (upstream
    /// <c>openTasks</c>).</summary>
    internal static async Task<OpenedTasks> OpenTasks(IStorage storage, IReadOnlyList<DurableTask> tasks, IRegistry? registry = null,
        Func<double>? now = null, HarnessSettings? settings = null, TimeProvider? timeProvider = null, IDurableModels? models = null)
    {
        registry ??= Registry.Create();
        if (tasks.Count > 0) registry.Install(Define.Extension(new Extension { Name = "tasks", Tasks = [.. tasks] }));
        var reports = new SyncList<Exception>();
        var harness = await Harness.OpenAsync(storage, new HarnessOptions
        {
            Models = models ?? EmptyDurableModels.Instance,
            Registry = registry,
            OnReport = reports.Add,
            Now = now,
            Settings = settings,
            TimeProvider = timeProvider,
        }, Context);
        return new OpenedTasks(harness, registry, reports);
    }

    /// <summary>Next state that completes a task with <paramref name="result"/> (upstream <c>completed</c>).</summary>
    internal static TaskState Completed(JsonValue? result) => NextTaskState.Completed(result ?? JsonValue.Null);

    /// <summary>Next state that aborts a task (upstream <c>abortedWith</c>).</summary>
    internal static TaskState AbortedWith(string reason) => NextTaskState.Aborted(reason);

    /// <summary>A registry reader that counts live subscriptions (upstream <c>countingReader</c>).</summary>
    internal sealed class CountingReader(IRegistry registry) : IRegistryReader
    {
        private int count;
        public int Subscriptions => Volatile.Read(ref count);
        public IRegistrySnapshot Snapshot() => registry.Snapshot();
        public IDisposable Subscribe(Action listener)
        {
            Interlocked.Increment(ref count);
            var unsubscribe = registry.Subscribe(listener);
            var active = 1;
            return new Disposer(() =>
            {
                if (Interlocked.Exchange(ref active, 0) == 0) return;
                Interlocked.Decrement(ref count);
                unsubscribe.Dispose();
            });
        }
    }

    internal sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>A document reader with no documents.</summary>
    internal sealed class NullReader : IDocumentReader
    {
        public static NullReader Instance { get; } = new();
        public Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
            Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at, DurableContext context) =>
            Task.FromResult<JsonObjectValue?>(null);
        public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key, EntryId at,
            DurableContext context) => Task.FromResult<JsonObjectValue?>(null);
    }
}

/// <summary>A list that handlers on different threads append to.</summary>
internal sealed class SyncList<T>
{
    private readonly List<T> items = [];
    public void Add(T item) { lock (items) items.Add(item); }
    public int Count { get { lock (items) return items.Count; } }
    public List<T> Snapshot() { lock (items) return [.. items]; }
    public void Clear() { lock (items) items.Clear(); }
    public T this[int index] { get { lock (items) return items[index]; } }
}

/// <summary>
/// A manual clock and timer source (upstream's vi.useFakeTimers). Timers fire only from <see cref="Advance"/>; <see cref="TimerCount"/>
/// counts scheduled timers that have neither fired nor been disposed, as vi.getTimerCount() does.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public ManualTimeProvider() : this(DateTimeOffset.FromUnixTimeMilliseconds(0)) { }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return now;
    }

    /// <summary>The clock in milliseconds, for <c>HarnessOptions.Now</c>.</summary>
    public double NowMs() => GetUtcNow().ToUnixTimeMilliseconds();

    public int TimerCount
    {
        get { lock (gate) return timers.Count(timer => timer.Due is not null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (gate) timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock forward, firing due timers in due order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (gate) target = now + by;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(timer => timer.Due is { } due && due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    return;
                }
                now = next.Due!.Value;
                next.Due = next.Period is { } period ? now + period : null;
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                Due = null;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
