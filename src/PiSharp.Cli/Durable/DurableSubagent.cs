// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/subagent.ts
// PiSharp-only (decision 0005, "PiSharp-only additions"): the child conversation drops user-interaction as well.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;

namespace PiSharp.Cli.Durable;

/// <summary>
/// A foreground subagent (upstream <c>Subagent</c>): each call creates a child conversation owned by the call's task, runs the task there,
/// and returns the child's answer. Aborting the call aborts the child. The child outlives the call, so the user can switch to it and keep
/// talking.
/// </summary>
internal static class DurableSubagent
{
    /// <summary>TypeBox <c>Type.Object({ task: Type.String({ description }) })</c>, in TypeBox's key order.</summary>
    private static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["task"],"properties":{"task":{"type":"string","description":"What the subagent should do"}}}""");

    // Declared after Parameters: static initializers run in order.
    /// <summary>The <c>subagent</c> extension.</summary>
    internal static Extension Extension { get; } = Create();

    private static Extension Create()
    {
        Extension subagent = null!;
        subagent = Define.Extension(new Extension
        {
            Name = "subagent",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "subagent",
                    Description = "Delegate a self-contained task to a subagent with the same tools and get its answer back. Give it everything " +
                        "it needs to know; it does not see this conversation.",
                    Parameters = Parameters,
                    // A rerun after a crash finds the child it created and the submission it made.
                    Replay = ToolReplay.Safe,
                    Execute = async (arguments, api, context) =>
                    {
                        var task = ((JsonStringValue)((JsonObjectValue)arguments)["task"]).Value;
                        var child = await api.CommitAsync(async tx =>
                        {
                            var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = api.TaskId }, 1).ConfigureAwait(false))
                                .Items.FirstOrDefault();
                            if (existing is not null) return existing.Id;
                            // Starts as a copy of this conversation's agent; without this extension it cannot delegate further. Without
                            // user-interaction (PiSharp-only, decision 0005) it cannot block on a question or a plan review nobody sees: the
                            // TUI shows only the conversation it talks to.
                            var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId)).ConfigureAwait(false);
                            Extension[] removed = api.Registry.GetExtension(UserInteraction.ExtensionName) is { } interaction ? [subagent, interaction] : [subagent];
                            await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Extensions = ExtensionSelection.Edit(remove: removed) })
                                .ConfigureAwait(false);
                            return created.Id;
                        }, context).ConfigureAwait(false);
                        await api.DetailsAsync(Details(child), context).ConfigureAwait(false);
                        var handle = (await api.ConversationAsync(child, context).ConfigureAwait(false))!;
                        var request = SubmissionDraft.Input(task, requestId: $"subagent:{api.TaskId.Value}");
                        var settled = await (await handle.SubmitAsync(request, context).ConfigureAwait(false)).WaitAsync(context).ConfigureAwait(false);
                        if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input || settled.Answer is not { } answer)
                            throw new InvalidOperationException($"Subagent {child.Value} failed: {Lower(settled.Status.ToString())}");
                        var text = await AnswerTextAsync(api, answer, context).ConfigureAwait(false);
                        return new ToolExecutionResult
                        {
                            Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", text)])]),
                            Details = Details(child),
                        };
                    },
                }),
            ],
        });
        return subagent;
    }

    private static JsonObjectValue Details(ConversationId child) => JsonValue.Object([new("conversationId", child.ToJson())]);

    private static string Lower(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>The text of the answer entry: its message's text blocks, joined.</summary>
    private static async Task<string> AnswerTextAsync(IToolExecutionApi api, EntryId answer, DurableContext context)
    {
        var entry = await api.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), context).ConfigureAwait(false);
        if (entry?.Model is not { Count: > 0 } messages || messages[0] is not JsonObjectValue message ||
            !message.TryGetValue("content", out var content) || content is not JsonArrayValue blocks) return "";
        return string.Concat(blocks.OfType<JsonObjectValue>().Select(block =>
            block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" } && block.TryGetValue("text", out var text) &&
            text is JsonStringValue value ? value.Value : ""));
    }
}
