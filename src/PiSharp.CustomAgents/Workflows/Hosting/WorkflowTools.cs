// Decision 0012: workflows as agent tools. run_workflow_<name> takes the workflow's Data, or an input type mapped to it, as its arguments
// (the schema is exported from that type, and the Harness validates the model's arguments against it), starts a run in its own
// conversation, and waits for the result up to a timeout; past it, or without waiting, it returns the run's task ID for workflow_status. The start is exactly once: the run and a
// receipt on the tool task (pisharp.workflow.start) are written in one commit, and a rerun after a restart finds the receipt and waits for
// the same run. The run is not owned by the call: it keeps going when the call times out or the turn is aborted (abort it with
// WorkflowRun.AbortAsync). workflow_status reports any run on the Session by task ID.
using System.Globalization;
using System.Text;
using PiSharp.Contracts;
using PiSharp.CustomAgents.Internal;
using PiSharp.CustomAgents.Tools;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Workflows.Hosting;

/// <summary>Options of <see cref="WorkflowTools.RunTool{TData}"/>.</summary>
public sealed record WorkflowToolOptions
{
    /// <summary>The tool name; default <c>run_workflow_&lt;name&gt;</c> (with <c>-</c> and <c>.</c> as <c>_</c>).</summary>
    public string? Name { get; init; }
    /// <summary>The tool description; default the workflow's description.</summary>
    public string? Description { get; init; }
    /// <summary>Whether the call waits for the run to end; default true.</summary>
    public bool Wait { get; init; } = true;
    /// <summary>The longest a call waits before it returns the run's task ID; default 10 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Tools that let agents run workflows and follow them.</summary>
public static class WorkflowTools
{
    /// <summary>The status tool's name.</summary>
    public const string StatusToolName = "workflow_status";

    /// <summary><c>pisharp.workflow.start</c> (task scope, on the tool task): <c>{ taskId, conversationId }</c> of the run it started.</summary>
    internal static TaskDocToken Receipt { get; } = Documents.DefineTaskDoc("pisharp.workflow.start", 1, () => JsonObjectValue.Empty);

    private static readonly JsonObjectValue StatusParameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["task_id"],"properties":{"task_id":{"type":"integer","description":"The run's task ID"}}}""");

    /// <summary>A tool that runs <paramref name="workflow"/> (added to <paramref name="catalog"/>) with the Data the model passes.</summary>
    public static ToolRegistration RunTool<TData>(WorkflowCatalog catalog, Workflow<TData> workflow, WorkflowToolOptions? options = null) where TData : class =>
        RunTool<TData, TData>(catalog, workflow, data => data, options);

    /// <summary>A tool that runs <paramref name="workflow"/> (added to <paramref name="catalog"/>) with the Data <paramref name="input"/> makes of
    /// the <typeparamref name="TInput"/> the model passes: the tool's parameters are <typeparamref name="TInput"/>'s schema, so the model fills
    /// only what a run starts from, not the fields the workflow's actions write.</summary>
    public static ToolRegistration RunTool<TData, TInput>(WorkflowCatalog catalog, Workflow<TData> workflow, Func<TInput, TData> input,
        WorkflowToolOptions? options = null) where TData : class where TInput : class
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(input);
        options ??= new WorkflowToolOptions();
        if (options.Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "The timeout must be positive.");
        var name = options.Name ?? "run_workflow_" + workflow.Name.Replace('-', '_').Replace('.', '_');
        ToolNames.Validate(name);
        var schema = Schemas.Export(typeof(TInput), workflow.JsonOptions);
        if (schema.Wrapped) throw new ArgumentException($"The input of workflow '{workflow.Name}' is not an object type.", nameof(workflow));
        return new ToolRegistration
        {
            Name = name,
            Description = options.Description ?? workflow.Description ?? $"Runs the '{workflow.Name}' workflow on the given data and returns its final data.",
            Parameters = schema.Schema,
            ParametersOrigin = ToolSchemaOrigin.JsonSchema,
            Replay = ToolReplay.Safe,
            Execute = async (arguments, api, context) =>
            {
                var engine = catalog.Engine(workflow);
                JsonValue data;
                try { data = engine.Input(input(engine.Codec.Deserialize<TInput>(arguments, "arguments"))); }
                catch (WorkflowDataException error) { return Text(error.Message, error: true); }
                var (taskId, _) = await api.CommitAsync(async tx =>
                {
                    var receipt = await tx.DocAsync(Receipt, api.TaskId).ConfigureAwait(false);
                    if (receipt.Get("taskId") is JsonNumberValue known && receipt.Get("conversationId") is JsonNumberValue owner)
                        return (new TaskId((long)known.Value), new ConversationId((long)owner.Value));
                    var started = await WorkflowRunner.StartInAsync(tx, catalog, workflow, data, new WorkflowStartOptions()).ConfigureAwait(false);
                    receipt.Set("taskId", started.TaskId.Value);
                    receipt.Set("conversationId", started.ConversationId.Value);
                    return (started.TaskId, started.ConversationId);
                }, context).ConfigureAwait(false);
                var id = taskId.Value.ToString(CultureInfo.InvariantCulture);
                var follow = $"call {StatusToolName} with task_id {id} to follow it.";
                if (!options.Wait) return Text($"Started workflow '{workflow.Name}' as run {id}; {follow}");
                var waiting = api.WaitForTaskAsync(taskId, context);
                if (await Task.WhenAny(waiting, Task.Delay(options.Timeout, context.CancellationToken)).ConfigureAwait(false) != waiting)
                {
                    _ = waiting.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    context.CancellationToken.ThrowIfCancellationRequested();
                    return Text($"Workflow '{workflow.Name}' run {id} is still running; {follow}");
                }
                return Finished(workflow.Name, await waiting.ConfigureAwait(false));
            },
        };
    }

    /// <summary><c>workflow_status(task_id)</c>: a run's status, current action, recent history and log, and its Data once it ended.</summary>
    public static ToolRegistration StatusTool() => new()
    {
        Name = StatusToolName,
        Description = "Reports a workflow run's status, current action, recent steps and log, and its final data once it ended.",
        Parameters = StatusParameters,
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var taskId = new TaskId((long)((JsonNumberValue)((JsonObjectValue)arguments)["task_id"]).Value);
            var record = await api.GetTaskAsync(taskId, context).ConfigureAwait(false);
            if (record is null || !record.Kind.StartsWith(Workflow.TaskKind(""), StringComparison.Ordinal))
                return Text($"Task {taskId.Value} is not a workflow run.", error: true);
            var doc = await api.SnapshotAsync(RunDocs.Run, record.ConversationId, RunDocs.Key(taskId), context).ConfigureAwait(false);
            var state = WorkflowRun<object>.State(record, doc);
            var text = new StringBuilder()
                .Append("Run ").Append(taskId.Value).Append(" of '").Append(record.Kind[Workflow.TaskKind("").Length..]).Append("': ")
                .Append(state.Status.ToString().ToLowerInvariant());
            if (state.CurrentAction is { } current && record.State is not SettlingTaskState) text.Append(" at '").Append(current).Append('\'');
            if (state.Error is { } error) text.Append(" (").Append(error).Append(')');
            text.Append('.');
            foreach (var line in state.History.TakeLast(5))
                text.Append("\n- ").Append(line.Action).Append(": ").Append(line.Outcome).Append(line.Next is null ? "" : " -> " + line.Next)
                    .Append(line.Error is null ? "" : " (" + line.Error + ")");
            foreach (var line in state.Log.TakeLast(5)) text.Append("\nlog: ").Append(line);
            if (record.State is SettlingTaskState { Outcome.Result: JsonObjectValue result } && result.TryGetValue("data", out var data))
                text.Append("\nData: ").Append(data.ToJson());
            return Text(text.ToString());
        },
    };

    private static ToolExecutionResult Finished(string workflow, TaskRecord record)
    {
        var outcome = ((SettlingTaskState)record.State).Outcome;
        var result = outcome.Result as JsonObjectValue;
        var data = result is not null && result.TryGetValue("data", out var value) ? value : null;
        string? Field(string key) => result is not null && result.TryGetValue(key, out var item) && item is JsonStringValue text ? text.Value : null;
        var steps = result is not null && result.TryGetValue("steps", out var count) && count is JsonNumberValue number ? (int)number.Value : 0;
        var id = record.Id.Value.ToString(CultureInfo.InvariantCulture);
        return outcome switch
        {
            CompletedTaskOutcome => Text($"Workflow '{workflow}' run {id} completed after {steps} steps.\nData: {data?.ToJson() ?? "null"}"),
            FailedTaskOutcome failed => Text($"Workflow '{workflow}' run {id} failed at '{Field("failedAt")}': {failed.Error.Message}", error: true),
            AbortedTaskOutcome aborted => Text($"Workflow '{workflow}' run {id} was aborted: {aborted.Reason ?? "aborted"}", error: true),
            _ => Text($"Workflow '{workflow}' run {id} ended {outcome.Status.ToString().ToLowerInvariant()}.", error: true),
        };
    }

    private static ToolExecutionResult Text(string text, bool error = false) => new()
    {
        Output = ToolJson.Text(text),
        IsError = error ? true : null,
    };
}
