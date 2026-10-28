// Decision 0012: what an action can see and do besides its Data: where the run stands, replay-safe helpers, model calls, agents and
// messages, services, and the raw durable runtime as an escape hatch.
using System.Globalization;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Workflows;

/// <summary>The context of one attempt of one action.</summary>
public sealed partial class ActionContext
{
    private readonly IStepScope scope;

    internal ActionContext(IStepScope scope, string action, RunningTask task, ITaskRuntime runtime, DurableContext durable, Checkpoint checkpoint,
        bool isReplay)
    {
        this.scope = scope;
        Action = action;
        Task = task;
        Runtime = runtime;
        Durable = durable;
        Checkpoint = checkpoint;
        IsReplay = isReplay;
    }

    internal RunningTask Task { get; }
    internal Checkpoint Checkpoint { get; }
    internal IStepScope Scope => scope;

    /// <summary>The workflow's name.</summary>
    public string Workflow => scope.WorkflowName;
    /// <summary>This action's name.</summary>
    public string Action { get; }
    /// <summary>The run's durable task.</summary>
    public TaskId TaskId => Task.Id;
    /// <summary>The run's conversation.</summary>
    public ConversationId ConversationId => Task.ConversationId;
    /// <summary>The step number of this visit: stable across the visit's retries, new on every visit (loops included).</summary>
    public long Step => Checkpoint.Visit;
    /// <summary>The 1-based attempt of this visit.</summary>
    public int Attempt => Checkpoint.Attempt;
    /// <summary>How many times this run has entered this action, this visit included.</summary>
    public int Visit => Checkpoint.Visits.GetValueOrDefault(Action, 1);
    /// <summary>True when an earlier attempt at this exact point started and did not commit (a crash or restart): side effects it made may
    /// already have happened.</summary>
    public bool IsReplay { get; }
    /// <summary>The Harness clock.</summary>
    public DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds((long)Runtime.Now());
    /// <summary>The durable task runtime of the run (escape hatch: memos, documents, child tasks).</summary>
    public ITaskRuntime Runtime { get; }
    /// <summary>The durable context of the invocation.</summary>
    public DurableContext Durable { get; }

    /// <summary>A key unique to this visit and <paramref name="key"/>, the same on every attempt and replay: pass it to external systems so
    /// a repeated call is recognised (<c>wf:&lt;task&gt;:&lt;step&gt;:&lt;key&gt;</c>).</summary>
    public string IdempotencyKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return "wf:" + TaskId.Value.ToString(CultureInfo.InvariantCulture) + ":" + Step.ToString(CultureInfo.InvariantCulture) + ":" + key;
    }

    /// <summary>
    /// Runs <paramref name="work"/> unless this visit already recorded a result for <paramref name="key"/>, and returns the recorded result.
    /// The first recorded result wins: work that crashed before recording runs again, so it is at least once; its result is recorded once
    /// and kept across the visit's retries. Results are stored with the run (keep them small).
    /// </summary>
    public async Task<T> OnceAsync<T>(string key, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);
        var name = "once:" + Step.ToString(CultureInfo.InvariantCulture) + ":" + key;
        var context = Durable.WithCancellation(cancellationToken);
        if (await Runtime.MemoAsync(name, context).ConfigureAwait(false) is JsonObjectValue recorded) return Read<T>(recorded);
        var result = await work(cancellationToken).ConfigureAwait(false);
        var candidate = JsonValue.Object([KeyValuePair.Create("value", scope.Codec.Serialize(result, "result of '" + key + "'"))]);
        return Read<T>((JsonObjectValue)await Runtime.MemoAsync(name, candidate, context).ConfigureAwait(false));
    }

    /// <summary><see cref="OnceAsync{T}(string, Func{CancellationToken, Task{T}}, CancellationToken)"/> for work without a result.</summary>
    public Task OnceAsync(string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return OnceAsync(key, async token =>
        {
            await work(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>A service from the catalog's <see cref="IServiceProvider"/>; null when absent or no provider was given.</summary>
    public T? GetService<T>() where T : class => scope.Services?.GetService(typeof(T)) as T;

    /// <summary>A required service; throws when absent.</summary>
    public T GetRequiredService<T>() where T : class =>
        GetService<T>() ?? throw new InvalidOperationException($"No service of type {typeof(T).Name} is registered with the workflow catalog.");

    /// <summary>Appends a line to the run's log (its run document; the newest lines are kept).</summary>
    public Task LogAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Runtime.CommitAsync(async (tx, _) =>
        {
            var doc = await RunDocs.RunAsync(tx, ConversationId, TaskId).ConfigureAwait(false);
            RunDocs.Append(doc, "log", RunDocs.Object(("at", Runtime.Now()), ("step", Step), ("action", Action), ("text", message)), RunDocs.LogLimit);
        }, Durable.WithCancellation(cancellationToken));
    }

    private T Read<T>(JsonObjectValue recorded) => scope.Codec.Deserialize<T>(recorded["value"], "recorded result");
}
