// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/examples/20-inbox.ts. Upstream stands in for a model with pi-ai's
// faux provider; here ScriptedModels does, with a held first reply, so the example runs offline. It prints what upstream's console.log
// prints, as JSON. On a file backend the storage is then reopened in a plain Session and the transcript and inbox are read back.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 20: what happens to submissions while a conversation is busy.
    private static async Task InboxAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // The first answer waits until we let it go, so the conversation stays busy while we submit more.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ScriptedModels(ScriptedReply.Held("Answer to the first question.", held.Task), "Answer to the follow-up and the steer.");

        // Settings apply to every conversation: place every queued follow-up at once instead of one per run.
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models,
            Registry = Registry.Create(),
            Settings = new HarnessSettings { FollowUpMode = QueueMode.All },
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        var first = await root.SubmitAsync(SubmissionDraft.Input("First question"), Context);

        // While busy, input queues as a follow-up (the default) or a steer, and writes queue too.
        var followUp = await root.SubmitAsync(SubmissionDraft.Input("A follow-up"), Context);
        var steer = await root.SubmitAsync(SubmissionDraft.Input("A steer", WhenBusy.Steer), Context);
        var note = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "app.note", Data = "noted while busy" }), Context);
        var withdrawn = await root.SubmitAsync(SubmissionDraft.Input("Never mind"), Context);
        // WhenBusy.Reject refuses instead of queueing.
        try
        {
            await root.SubmitAsync(SubmissionDraft.Input("Now or never", WhenBusy.Reject), Context);
        }
        catch (ConversationBusyException error)
        {
            await output.WriteLineAsync("rejected: " + error.Message);
        }
        // A queued submission can be withdrawn until a boundary places it.
        var withdrawal = await withdrawn.AbortAsync(Context);
        await output.WriteLineAsync("withdraw: " + withdrawal switch
        {
            SubmissionAbortResult.Aborted => "aborted",
            SubmissionAbortResult.AlreadyPlaced => "already_placed",
            SubmissionAbortResult.Settled => "settled",
            _ => "not_found",
        });

        var inbox = await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Context);
        await output.WriteLineAsync("inbox: " + InboxItems(inbox));

        // The first answer ends the run at a final boundary: the write is placed first, then the steer and the follow-up, which start the
        // next run together.
        held.SetResult();
        async Task Status(string name, ISubmission submission)
        {
            var record = await submission.WaitAsync(Context);
            var status = record.Status == SubmissionStatus.Done ? "done" : "unanswered";
            await output.WriteLineAsync($"{name}: {status} {(record.Status == SubmissionStatus.Unanswered ? record.Reason : "")}");
        }
        await Status("first", first);
        await Status("follow-up", followUp);
        await Status("steer", steer);
        await Status("note", note);
        await Status("withdrawn", withdrawn);

        var page = await root.EntriesAsync(new EntryQueryBounds(), 20, null, Context);
        await output.WriteLineAsync("transcript: " + Kinds(page.Items.Reverse()));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20)));
            await output.WriteLineAsync("inbox: " + InboxItems(await reopened.SnapshotAsync(Inbox.InboxDoc, root.Id, Context)));
        });
    }

    // Upstream prints `${item.id} ${item.mode}` for each item.
    private static string InboxItems(JsonObjectValue? inbox) => Json(inbox is null ? null : JsonValue.Array(((JsonArrayValue)inbox["items"])
        .Select(item => (JsonValue)$"{((JsonObjectValue)item)["id"].ToJson()} {((JsonStringValue)((JsonObjectValue)item)["mode"]).Value}")));
}
