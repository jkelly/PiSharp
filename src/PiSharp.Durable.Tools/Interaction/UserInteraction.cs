// PiSharp-only (decision 0005): no upstream counterpart. Upstream pi-durable has neither ask_user_question nor plan mode; this file
// follows the spec's §9.2 pattern for a tool that waits for a person (a task-scoped document the host answers into, pending state in the
// tool slot's details, a document watch), with a replay-safe tool so a close or crash only reruns the wait.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable.Tools;

/// <summary>
/// The <c>user-interaction</c> extension: <c>ask_user_question</c> (<see cref="UserQuestions"/>) and <c>exit_plan_mode</c>
/// (<see cref="PlanMode"/>), the two tools that wait for the user. Both keep their call open durably: the tool commits a task-scoped
/// <see cref="InteractionDoc"/>, publishes its pending state in its <c>pi.live</c> tool slot details (which every conversation view mounts),
/// and waits for the host to commit a response into the document. Both are replay-safe and callable only by the model, so after a close or
/// crash the call reruns, finds its document, and either waits again or returns the stored response; the result entry is appended once.
/// </summary>
public static class UserInteraction
{
    /// <summary>The extension name.</summary>
    public const string ExtensionName = "user-interaction";

    /// <summary>
    /// The interaction document of one waiting tool call (kind <c>pisharp.interaction</c>, version 1, task-scoped, so it retires with the
    /// call). Questions: <c>{kind: "questions", questions, createdAt, response?: {status: "answered"|"declined", answers?, at}}</c>. Plan
    /// approval: <c>{kind: "plan-approval", plan, planFile?, directory, createdAt, response?: {status: "approved"|"rejected"|"cancelled",
    /// stop?, feedback?, at}}</c>. A document without <c>kind</c> was never created by the tool.
    /// </summary>
    public static TaskDocToken InteractionDoc { get; } = Documents.DefineTaskDoc("pisharp.interaction", 1, () => JsonObjectValue.Empty);

    /// <summary>The <c>user-interaction</c> extension. Hosts that cannot show the user a question (a subagent's child conversation, a
    /// headless run) should not select it.</summary>
    public static Extension CreateExtension() => Define.Extension(new Extension
    {
        Name = ExtensionName,
        Tools = [UserQuestions.CreateTool(), PlanMode.CreateExitTool()],
    });

    /// <summary>
    /// The interaction a running model-issued tool slot of <c>pi.live.tools</c> waits for, or null: the slot is <c>running</c> and its details
    /// are <c>{kind: "pisharp.questions" | "pisharp.plan-approval", state: "pending", ...}</c>.
    /// </summary>
    public static PendingInteraction? PendingOf(ConversationId conversationId, JsonObjectValue toolSlot)
    {
        ArgumentNullException.ThrowIfNull(toolSlot);
        if (!(toolSlot.TryGetValue("status", out var status) && status is JsonStringValue { Value: "running" })) return null;
        if (!(toolSlot.TryGetValue("details", out var value) && value is JsonObjectValue details)) return null;
        if (!(details.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: UserQuestions.DetailsKind or PlanMode.DetailsKind } named)) return null;
        if (!(details.TryGetValue("state", out var state) && state is JsonStringValue { Value: "pending" })) return null;
        if (!(toolSlot.TryGetValue("taskId", out var taskId) && taskId is JsonNumberValue id)) return null;
        var callId = toolSlot.TryGetValue("callId", out var call) && call is JsonStringValue text ? text.Value : "";
        return new PendingInteraction(conversationId, new TaskId((long)id.Value), callId, named.Value, details);
    }

    /// <summary>Every pending interaction of a conversation view's <c>pi.live.tools</c>, lowest task ID first. A host shows the first.</summary>
    public static IReadOnlyList<PendingInteraction> PendingIn(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return PendingIn(view.Conversation.Id, view.Doc(Live.LiveDoc));
    }

    /// <summary>Every pending interaction of a <c>pi.live</c> document value, lowest task ID first.</summary>
    public static IReadOnlyList<PendingInteraction> PendingIn(ConversationId conversationId, JsonObjectValue? live)
    {
        if (live is null || !live.TryGetValue("tools", out var tools) || tools is not JsonArrayValue slots) return [];
        return [.. slots.OfType<JsonObjectValue>().Select(slot => PendingOf(conversationId, slot)).OfType<PendingInteraction>()
            .OrderBy(pending => pending.TaskId.Value)];
    }

    /// <summary>The pending interactions of <paramref name="conversation"/>, from its committed view, lowest task ID first.</summary>
    public static async Task<IReadOnlyList<PendingInteraction>> PendingAsync(IConversation conversation, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(context);
        using var state = await conversation.ViewStateAsync(context).ConfigureAwait(false);
        return PendingIn(state.Value);
    }

    /// <summary>
    /// Inside a host's conversation commit: the interaction document of tool task <paramref name="toolTask"/>, checked to belong to a live
    /// model-issued tool call of <paramref name="conversationId"/> and to be of <paramref name="kind"/>. Reads only through
    /// <paramref name="tx"/>. Throws <see cref="InteractionNotPendingException"/> once the call has ended or is being aborted, and
    /// <see cref="ArgumentException"/> for a task that is not such a call; either throw discards the commit.
    /// </summary>
    internal static async Task<DraftObject> OpenAsync(ITx tx, ConversationId conversationId, TaskId toolTask, string kind)
    {
        var task = await tx.GetTaskAsync(toolTask).ConfigureAwait(false);
        if (task is null || task.Kind != HarnessTasks.ToolTask.Definition.Name || task.ConversationId != conversationId)
            throw new ArgumentException($"Task {toolTask.Value} is not a tool call of conversation {conversationId.Value}", nameof(toolTask));
        if (task.State.Status is DurableTaskStatus.Terminal or DurableTaskStatus.Completing || task.AbortRequested)
            throw new InteractionNotPendingException(toolTask, "the tool call has ended");
        var doc = await tx.DocAsync(InteractionDoc, toolTask).ConfigureAwait(false);
        if (doc.Get("kind") is not JsonStringValue { } stored || stored.Value != kind)
            throw new ArgumentException($"Task {toolTask.Value} does not wait for a {kind} interaction", nameof(toolTask));
        return doc;
    }

    /// <summary>The <c>response</c> of an interaction document value, or null.</summary>
    internal static JsonObjectValue? ResponseOf(JsonObjectValue? doc) =>
        doc is not null && doc.TryGetValue("response", out var response) && response is JsonObjectValue value ? value : null;

    /// <summary>Wall-clock milliseconds. Neither tools nor host commits can read the Harness clock; the times are informational only.</summary>
    internal static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>An interaction a running tool call waits for (a question form or a plan review), as its tool slot publishes it.</summary>
/// <param name="ConversationId">The conversation of the call.</param>
/// <param name="TaskId">The tool task, which the host passes to <see cref="UserQuestions.AnswerAsync"/> or <see cref="PlanMode.ApproveAsync"/>.</param>
/// <param name="CallId">The model's tool call ID.</param>
/// <param name="Kind"><see cref="UserQuestions.DetailsKind"/> or <see cref="PlanMode.DetailsKind"/>.</param>
/// <param name="Details">The slot's details: questions, or the plan with its file and directory.</param>
public sealed record PendingInteraction(ConversationId ConversationId, TaskId TaskId, string CallId, string Kind, JsonObjectValue Details);

/// <summary>The tool call no longer waits for this interaction: it ended, is being aborted, or got another response first.</summary>
public sealed class InteractionNotPendingException(TaskId taskId, string reason)
    : InvalidOperationException($"Tool call {taskId.Value} no longer waits for a response: {reason}")
{
    /// <summary>The tool task.</summary>
    public TaskId TaskId { get; } = taskId;
}

/// <summary>The wait shared by <c>ask_user_question</c> and <c>exit_plan_mode</c>.</summary>
internal static class InteractionWait
{
    /// <summary>
    /// Waits until the call's interaction document has a response and returns the document. Checks the watch's acquisition revision first;
    /// later revisions are delivered in order once the listener starts. Cancellation of <paramref name="context"/> (an abort, a close) ends
    /// the wait with an <see cref="OperationCanceledException"/>; a watch that ends otherwise fails it.
    /// </summary>
    public static async Task<JsonObjectValue> WaitForResponseAsync(IToolExecutionApi api, DurableContext context)
    {
        var watch = await api.WatchDocAsync(UserInteraction.InteractionDoc, api.TaskId, context).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tool call {api.CallId} has no interaction document");
        try
        {
            if (UserInteraction.ResponseOf(watch.Value) is not null) return watch.Value!;
            var answered = new TaskCompletionSource<JsonObjectValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            watch.Start((value, _, _) =>
            {
                if (UserInteraction.ResponseOf(value) is not null) answered.TrySetResult(value!);
                return Task.CompletedTask;
            });
            _ = watch.Closed.ContinueWith(closed => answered.TrySetException(
                    new InvalidOperationException($"The interaction watch of tool call {api.CallId} ended: {closed.Result.Reason}", closed.Result.Error)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await context.WaitAsync(answered.Task).ConfigureAwait(false);
        }
        finally
        {
            await watch.StopAsync().ConfigureAwait(false);
        }
    }
}
