// Decision 0012: starts and finds workflow runs on a Harness. A run is one durable task in its own ownerless conversation (or a given
// one), created in a single commit with its run document and, when a run key is given, the key that makes the start idempotent.
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.CustomAgents.Workflows.Hosting;

/// <summary>Options for starting a run.</summary>
public sealed record WorkflowStartOptions
{
    /// <summary>Makes the start idempotent: a second start with the same key returns the first run (also after a restart).</summary>
    public string? RunKey { get; init; }
    /// <summary>The conversation the run belongs to; default a new ownerless conversation for the run.</summary>
    public ConversationId? Conversation { get; init; }
    /// <summary>Whether the run is background work of its conversation (excluded from its idle waits and aborts).</summary>
    public bool Background { get; init; }
}

/// <summary>A run as listed by <see cref="WorkflowRunner.ListAsync"/>.</summary>
public sealed record WorkflowRunInfo(string Workflow, TaskId TaskId, ConversationId ConversationId, string? RunKey, double CreatedAt);

/// <summary>Starts, finds and lists workflow runs on one Harness.</summary>
public sealed class WorkflowRunner
{
    /// <summary>A runner for <paramref name="harness"/> running workflows of <paramref name="catalog"/>.</summary>
    public WorkflowRunner(IHarness harness, WorkflowCatalog catalog)
    {
        Harness = harness ?? throw new ArgumentNullException(nameof(harness));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>The Harness.</summary>
    public IHarness Harness { get; }
    /// <summary>The catalog.</summary>
    public WorkflowCatalog Catalog { get; }

    /// <summary>Starts <paramref name="workflow"/> with <paramref name="data"/>; with a known run key, returns that run instead.</summary>
    public async Task<WorkflowRun<TData>> StartAsync<TData>(Workflow<TData> workflow, TData data, WorkflowStartOptions? options = null,
        DurableContext? context = null) where TData : class
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(data);
        options ??= new WorkflowStartOptions();
        context ??= DurableContext.Background;
        var engine = Catalog.Engine(workflow);
        var input = engine.Input(data);
        var (taskId, conversationId, existing) = await Harness.CommitAsync(tx => StartInAsync(tx, Catalog, workflow, input, options), context)
            .ConfigureAwait(false);
        Harness.Resume();
        return new WorkflowRun<TData>(this, workflow, taskId, conversationId) { Existing = existing };
    }

    /// <summary>Starts a run in <paramref name="tx"/> (from <see cref="StartAsync{TData}"/> or a tool's commit): its conversation unless one is
    /// given, its task, its run document, its run key and its index entry. With a known run key, returns that run (<c>Existing</c>).</summary>
    internal static async Task<(TaskId TaskId, ConversationId ConversationId, bool Existing)> StartInAsync(ITx tx, WorkflowCatalog catalog,
        WorkflowDefinition workflow, JsonValue input, WorkflowStartOptions options)
    {
        var engine = catalog.Engine(workflow);
        var key = options.RunKey is { } runKey ? workflow.Name + ":" + runKey : null;
        if (options.Conversation is { } given && await tx.GetConversationAsync(given).ConfigureAwait(false) is null)
            throw new ArgumentException($"Conversation {given.Value} does not exist.", nameof(options));
        DraftObject? keyDoc = null;
        if (key is not null)
        {
            keyDoc = await tx.DocAsync(RunDocs.Keys, key, JsonObjectValue.Empty).ConfigureAwait(false);
            if (keyDoc.Get("taskId") is JsonNumberValue known && keyDoc.Get("conversationId") is JsonNumberValue owner)
                return (new TaskId((long)known.Value), new ConversationId((long)owner.Value), true);
        }
        var conversation = options.Conversation;
        if (conversation is null)
        {
            conversation = (await tx.CreateConversationAsync(ConversationOwnership.Ownerless).ConfigureAwait(false)).Id;
            await Agents.ConfigureAsync(tx, conversation.Value, BaseAgent(catalog, workflow)).ConfigureAwait(false);
        }
        var id = await tx.CreateTaskAsync(engine.Task, input, new TaskOptions(TaskOwnership.Conversation)
        {
            ConversationId = conversation, Background = options.Background,
        }).ConfigureAwait(false);
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var doc = await RunDocs.RunAsync(tx, conversation.Value, id).ConfigureAwait(false);
        doc.Set("workflow", workflow.Name);
        doc.Set("version", workflow.Version);
        doc.Set("status", "pending");
        doc.Set("createdAt", now);
        if (options.RunKey is { } runKeyValue) doc.Set("runKey", runKeyValue);
        if (keyDoc is not null)
        {
            keyDoc.Set("taskId", id.Value);
            keyDoc.Set("conversationId", conversation.Value.Value);
        }
        var index = await tx.DocAsync(RunDocs.Index).ConfigureAwait(false);
        RunDocs.Append(index, "runs", RunDocs.Object(("workflow", workflow.Name), ("taskId", id.Value), ("conversationId", conversation.Value.Value),
            ("runKey", RunDocs.Opt(options.RunKey)), ("createdAt", now)), RunDocs.IndexLimit);
        return (id, conversation.Value, false);
    }

    /// <summary>Starts <paramref name="workflow"/> and waits for its result.</summary>
    public async Task<WorkflowResult<TData>> RunAsync<TData>(Workflow<TData> workflow, TData data, WorkflowStartOptions? options = null,
        DurableContext? context = null) where TData : class
    {
        var run = await StartAsync(workflow, data, options, context).ConfigureAwait(false);
        return await run.WaitAsync(context).ConfigureAwait(false);
    }

    /// <summary>A handle to an existing run of <paramref name="workflow"/>.</summary>
    public WorkflowRun<TData> Attach<TData>(Workflow<TData> workflow, TaskId taskId, ConversationId conversationId) where TData : class =>
        new(this, workflow ?? throw new ArgumentNullException(nameof(workflow)), taskId, conversationId);

    /// <summary>The run started with <paramref name="runKey"/>; null when none was.</summary>
    public async Task<WorkflowRun<TData>?> FindAsync<TData>(Workflow<TData> workflow, string runKey, DurableContext? context = null) where TData : class
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        var doc = await Harness.SnapshotAsync(RunDocs.Keys, workflow.Name + ":" + runKey, context ?? DurableContext.Background).ConfigureAwait(false);
        return doc is not null && doc.TryGetValue("taskId", out var task) && task is JsonNumberValue id &&
            doc.TryGetValue("conversationId", out var owner) && owner is JsonNumberValue conversation
            ? new WorkflowRun<TData>(this, workflow, new TaskId((long)id.Value), new ConversationId((long)conversation.Value)) { Existing = true }
            : null;
    }

    /// <summary>The runs started on this Session, oldest first (the newest 1000).</summary>
    public async Task<IReadOnlyList<WorkflowRunInfo>> ListAsync(DurableContext? context = null)
    {
        var index = await Harness.SnapshotAsync(RunDocs.Index, context ?? DurableContext.Background).ConfigureAwait(false);
        if (index is null || !index.TryGetValue("runs", out var runs) || runs is not JsonArrayValue list) return [];
        return [.. list.OfType<JsonObjectValue>().Select(run => new WorkflowRunInfo(
            ((JsonStringValue)run["workflow"]).Value, new TaskId((long)((JsonNumberValue)run["taskId"]).Value),
            new ConversationId((long)((JsonNumberValue)run["conversationId"]).Value),
            run.TryGetValue("runKey", out var key) && key is JsonStringValue text ? text.Value : null,
            run.TryGetValue("createdAt", out var at) && at is JsonNumberValue time ? time.Value : 0))];
    }

    /// <summary>The run's conversation agent: no extensions or tools, the workflow's model and instructions.</summary>
    internal static AgentChange BaseAgent(WorkflowCatalog catalog, WorkflowDefinition workflow)
    {
        var model = (workflow as IHasPromptDefaults)?.Model ?? catalog.Options.DefaultModel;
        var thinking = (workflow as IHasPromptDefaults)?.Thinking ?? catalog.Options.DefaultThinking;
        return new AgentChange
        {
            Model = model is null ? (Patch<ModelRef>?)null : Patch<ModelRef>.Set(model),
            ThinkingLevel = thinking is null ? (Patch<string>?)null : Patch<string>.Set(thinking),
            Extensions = Patch<ExtensionSelection>.Set(ExtensionSelection.Only([])),
            ModelTools = Patch<ModelToolSelection>.Set(ModelToolSelection.Only([])),
        };
    }
}

/// <summary>The prompt defaults a workflow carries.</summary>
internal interface IHasPromptDefaults
{
    ModelRef? Model { get; }
    string? Thinking { get; }
    string? Instructions { get; }
}
