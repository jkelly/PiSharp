// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/33-turn-limit-and-continue.ts (added in f6a9db1d8,
// #11043), ported in the Pi v1.5.0 sync (area live-turns). Here it is 34-turn-limit-and-continue: PiSharp's examples keep their Pi v1.2.0
// numbers, so v1.5.0's examples from 04 on are one higher (31-transcript is 32-transcript, 34-submit-in-commit is 35-submit-in-commit).
// ScriptedModels stands in for the faux provider, and the step tool declares pi-schema's obj({ n: num() }) as JSON Schema. It prints what
// upstream's console.log prints (its arguments joined with spaces); ConversationBusyException prints as upstream's ConversationBusy. On a
// file backend the storage is reopened in a plain Session and the transcript's kinds read back (PiSharp-only, as every example's
// read-back).
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // pi-schema's obj({ n: num() }).
    private static readonly JsonObjectValue StepParameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","properties":{"n":{"type":"number"}},"required":["n"],"additionalProperties":false}""");

    // console.log(...args): its arguments joined with spaces.
    private static Task LogAsync(TextWriter output, params string?[] parts) => output.WriteLineAsync(string.Join(" ", parts.Select(part => part ?? "undefined")));

    // 34: a per-conversation turn limit stops a long run after a number of tool rounds; a continue resumes it without a user message, with a
    // fresh budget. A host learns that a run stopped from pi.live.runFailure.
    private static async Task TurnLimitAndContinueAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var step = Define.Tool(new ToolRegistration
        {
            Name = "step",
            Description = "Do one step of the work",
            Parameters = StepParameters,
            Execute = (arguments, _, _) => Task.FromResult(new ToolExecutionResult
            {
                Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", $"step {((JsonObjectValue)arguments)["n"].ToJson()} done")])]),
            }),
        });
        static ScriptedReply Call(int n) => ScriptedReply.ToolCall("step", JsonValue.Object([new("n", n)]));
        // Four steps, then the answer. Two tool rounds per run are allowed.
        var models = new ScriptedModels(Call(1), Call(2), Call(3), Call(4), "All four steps are done.");

        var registry = Registry.Create();
        registry.Install(new Extension { Name = "steps", Tools = [step] });
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        // Stored on the conversation, like its model: children and forks inherit it.
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux, MaxTurns = 2 });

        var first = await (await root.SubmitAsync(SubmissionDraft.Input("Do the four steps."), Context)).WaitAsync(Context);
        var failure = Live.RunFailureOf(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context));
        await LogAsync(output, "first run:", StatusName(first.Status), first.Reason, $"({failure?.Detail ?? "undefined"})");

        // An idle conversation runs a continue on the context as it is, the round's results last, with a fresh budget.
        var resumed = await root.SubmitAsync(SubmissionDraft.Continue(), Context);
        // It never queues: while that run works, another continue is refused.
        try
        {
            await root.SubmitAsync(SubmissionDraft.Continue(), Context);
        }
        catch (ConversationBusyException)
        {
            await LogAsync(output, "second continue while busy: ConversationBusy");
        }
        var second = await resumed.WaitAsync(Context);
        await LogAsync(output, "first continue:", StatusName(second.Status), second.Status == SubmissionStatus.Done ? "" : second.Reason);
        var third = await (await root.SubmitAsync(SubmissionDraft.Continue(), Context)).WaitAsync(Context);
        var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
        await LogAsync(output, "second continue:", StatusName(third.Status), Live.RunFailureOf(live) is null ? "(runFailure cleared)" : "");

        var page = await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 100, null, Context);
        foreach (var entry in page.Items)
        {
            if (entry.Model is not { Count: > 0 } model || model[0] is not JsonObjectValue message) continue;
            var role = message.TryGetValue("role", out var value) && value is JsonStringValue name ? name.Value : null;
            if (role == "user") await LogAsync(output, ">", message["content"] is JsonStringValue content ? content.Value : Json(message["content"]));
            if (role == "toolResult")
                await LogAsync(output, "  tool:", ((JsonArrayValue)message["content"]).FirstOrDefault() is JsonObjectValue { } part &&
                    part["type"] is JsonStringValue { Value: "text" } ? ((JsonStringValue)part["text"]).Value : "");
            if (role == "assistant" && message.TryGetValue("stopReason", out var stop) && stop is JsonStringValue { Value: "stop" })
                await LogAsync(output, "agent:", string.Concat(((JsonArrayValue)message["content"]).OfType<JsonObjectValue>()
                    .Where(part => part["type"] is JsonStringValue { Value: "text" }).Select(part => ((JsonStringValue)part["text"]).Value)));
        }
        // A continue after an answer has nothing to answer: it ends without a model request.
        var after = await (await root.SubmitAsync(SubmissionDraft.Continue(), Context)).WaitAsync(Context);
        await LogAsync(output, "continue after the answer:", StatusName(after.Status), after.Reason);
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20))));
    }
}
