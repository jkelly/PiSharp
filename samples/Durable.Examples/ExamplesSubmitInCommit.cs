// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/34-submit-in-commit.ts (added in cc9c52310), ported in
// the Pi v1.5.0 sync (area admission). Here it is 35-submit-in-commit: PiSharp's examples keep their Pi v1.2.0 numbers, so v1.5.0's
// examples from 04 on are one higher (31-transcript is 32-transcript). ScriptedModels stands in for the faux provider, and the build tool's
// obj({}) is ToolRegistration's default empty parameters. It prints what upstream's console.log prints, with JSON in place of
// util.inspect; the submission ID is the one this port mints. The second message waits until the build tool runs (PiSharp-only): the run
// works on other threads here, so without it the ID would depend on how far the run got. On a file backend the storage is reopened in a
// plain Session and the host's record read back (PiSharp-only, as every example's read-back).
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // The host's own state: who may message this conversation, and what they sent.
    private static readonly ConversationDocToken Visitors = Documents.DefineConversationDoc("app.visitors", 1, DocumentFork.Initial,
        () => (JsonObjectValue)JsonValue.Parse("""{"muted":[],"sent":[]}"""), checkpointWhen: (_, _, _) => false);

    // 35: a host lets people message an agent while it works. A message must join the current run as a steer and never start a run of its
    // own. SubmitAsync(tx, draft) admits it inside the host's own commit, together with the host's check and its own state; WhenIdle.Reject
    // refuses it when no run is working.
    private static async Task SubmitInCommitAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // A tool that waits until released, so messages arrive while the run works.
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = Registry.Create();
        registry.Install(new Extension
        {
            Name = "build",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "build",
                    Description = "Build the project",
                    Execute = async (_, _, _) =>
                    {
                        building.TrySetResult();
                        await working.Task;
                        return new ToolExecutionResult { Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", "built")])]) };
                    },
                }),
            ],
        });
        var models = new ScriptedModels(ScriptedReply.ToolCall("build", JsonObjectValue.Empty), "Built, and I added the test Ana asked for.");
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });
        await root.CommitAsync(async tx => (await tx.DocAsync(Visitors, root.Id)).GetArray("muted").Add("bob"), Context);

        // A visitor's message: checked, admitted as a steer of the current run, and recorded, in one commit or not at all.
        async Task<string> Message(string from, string text, string requestId)
        {
            try
            {
                return await harness.CommitAsync(async tx =>
                {
                    var visitors = await tx.DocAsync(Visitors, root.Id);
                    if (visitors.GetArray("muted").ToValue() is JsonArrayValue muted && muted.Any(name => name is JsonStringValue { Value: var value } && value == from))
                        return $"{from} is muted";
                    // Before the commit's own table writes; document writes may come before or after.
                    var id = await root.SubmitAsync(tx, SubmissionDraft.Input($"{from}: {text}", WhenBusy.Steer, requestId, whenIdle: WhenIdle.Reject));
                    visitors.GetArray("sent").Add(JsonValue.Object([new("from", from), new("text", text)]));
                    return $"{from}'s message is submission {id.Value}";
                }, Context);
            }
            catch (ConversationIdleException)
            {
                return $"{from}: the agent isn't working now";
            }
        }

        await output.WriteLineAsync(await Message("ana", "Add a test for the build.", "ana-1")); // idle: refused, nothing written
        var run = await root.SubmitAsync(SubmissionDraft.Input("Build the project."), Context);
        await building.Task;
        await output.WriteLineAsync(await Message("ana", "Add a test for the build.", "ana-2")); // busy: joins the run as a steer
        await output.WriteLineAsync(await Message("bob", "Delete everything.", "bob-1")); // the host's own check refuses it
        working.TrySetResult();
        await run.WaitAsync(Context);
        await root.WaitForIdleAsync(Context);

        var page = await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 100, null, Context);
        foreach (var entry in page.Items)
        {
            if (entry.Model is not { Count: > 0 } model || model[0] is not JsonObjectValue message) continue;
            var role = message.TryGetValue("role", out var value) && value is JsonStringValue name ? name.Value : null;
            if (role == "user") await output.WriteLineAsync("> " + (message["content"] is JsonStringValue content ? content.Value : Json(message["content"])));
            if (role == "assistant" && message.TryGetValue("stopReason", out var stop) && stop is JsonStringValue { Value: "stop" })
                await output.WriteLineAsync("agent: " + string.Concat(((JsonArrayValue)message["content"]).OfType<JsonObjectValue>()
                    .Where(part => part["type"] is JsonStringValue { Value: "text" }).Select(part => ((JsonStringValue)part["text"]).Value)));
        }
        await output.WriteLineAsync("recorded: " + Json((await harness.SnapshotAsync(Visitors, root.Id, Context))?["sent"]));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("recorded: " + Json((await reopened.SnapshotAsync(Visitors, root.Id, Context))?["sent"])));
    }
}
