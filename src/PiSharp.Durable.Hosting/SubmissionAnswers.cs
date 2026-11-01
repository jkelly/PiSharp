// PiSharp-only (decision 0008). A submission settles with its answer's entry ID (pi-durable SubmissionRecord.answer); a device answering
// one HTTP request needs the text too, as durable-pi's waitMs send returns it. The text is read the way pisharp durable's subagent tool reads
// its child's answer (src/PiSharp.Cli/Durable/DurableSubagent.cs).
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>The text of a settled submission's answer.</summary>
public static class SubmissionAnswers
{
    /// <summary>The answer entry's text blocks, joined; empty when it has none.</summary>
    public static async Task<string> TextAsync(IHarness harness, EntryId answer, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(context);
        var entry = await harness.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), context).ConfigureAwait(false);
        return TextOf(entry);
    }

    /// <summary>The text blocks of an assistant entry's message, joined.</summary>
    public static string TextOf(EntryRecord? entry)
    {
        if (entry?.Model is not { Count: > 0 } messages || messages[0] is not JsonObjectValue message ||
            !message.TryGetValue("content", out var content) || content is not JsonArrayValue blocks) return "";
        return string.Concat(blocks.OfType<JsonObjectValue>().Select(block =>
            block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" } && block.TryGetValue("text", out var text) &&
            text is JsonStringValue value ? value.Value : ""));
    }

    /// <summary>A <see cref="SendResult"/> for a submission record: settled with its answer, or <paramref name="unsettled"/>.</summary>
    internal static async Task<SendResult> ResultAsync(IHarness harness, SubmissionRecord record, SendStatus unsettled, DurableContext context) =>
        record.Status switch
        {
            SubmissionStatus.Done => new SendResult(record.Id, SendStatus.Done)
            {
                AnswerEntryId = record.Answer,
                AnswerText = record.Answer is { } answer ? await TextAsync(harness, answer, context).ConfigureAwait(false) : null,
            },
            SubmissionStatus.Unanswered => new SendResult(record.Id, SendStatus.Unanswered) { Reason = record.Reason },
            _ => new SendResult(record.Id, unsettled),
        };
}
