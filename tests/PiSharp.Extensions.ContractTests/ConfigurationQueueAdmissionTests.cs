using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;

internal static partial class ToolActivationTests
{
    private static IEnumerable<(string Name, Func<Task> Run)> ConfigurationQueueAdmissionCases() =>
    [
        (Prefix + "host configuration rejects steering and followup queues without mutation", ConfigurationRejectsQueuedInputs),
        (Prefix + "held configuration preparation rejects late queues and preserves active-run steering", ConfigurationRejectsLateQueueAdmission)
    ];

    private static TranscriptEntry QueuedInput(string text) => new("user",
        JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 1 })));

    private static void SameQueues(AgentPendingInputQueueSnapshot expected, AgentPendingInputQueueSnapshot actual)
    {
        Check(expected.SteeringMessages.SequenceEqual(actual.SteeringMessages) &&
            expected.FollowUpMessages.SequenceEqual(actual.FollowUpMessages) &&
            expected.SteeringMode == actual.SteeringMode && expected.FollowUpMode == actual.FollowUpMode,
            "Configuration admission changed queued contents, FIFO order or drain modes.");
    }

    private static async Task ConfigurationRejectsQueuedInputs()
    {
        foreach (var steering in new[] { true, false })
        {
            await using var fixture = await Fixture.Create();
            var name = steering ? "configuration-steering" : "configuration-followup";
            var session = await fixture.New(name); await using var owner = fixture.Owner(session);
            var first = QueuedInput("first"); var second = QueuedInput("second");
            if (steering) { session.Steer(first); session.Steer(second); }
            else { session.FollowUp(first); session.FollowUp(second); }
            var queues = session.GetPendingInputQueueSnapshot();
            Names(["first", "second"], (steering ? queues.SteeringMessages : queues.FollowUpMessages)
                .Select(message => message.WireBody.Value.GetProperty("content").GetString() ??
                    throw new InvalidOperationException("Queued fixture input lost its text.")));
            var snapshot = session.Snapshot; var selection = session.GetToolActivationSelection();
            var bytes = await ReadShared(fixture.PathFor(name));
            await Throws<PersistentAgentSessionException>(() => session.SetActiveToolsAsync(["inactive"]));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            // Queue admission remains ahead of caller cancellation, matching pending-input precedence.
            await Throws<PersistentAgentSessionException>(() => session.SetActiveToolsAsync(["code"], cancelled.Token));
            SameQueues(queues, session.GetPendingInputQueueSnapshot());
            var afterBytes = await ReadShared(fixture.PathFor(name));
            Check(bytes.SequenceEqual(afterBytes), "Rejected configuration changed durable bytes.");
            var after = session.Snapshot; var afterSelection = session.GetToolActivationSelection();
            Check(ReferenceEquals(snapshot.Context, after.Context) && ReferenceEquals(snapshot.Log, after.Log) &&
                snapshot.Agent.Generation == after.Agent.Generation && !after.IsConfiguring && !after.IsProcessingOperation,
                "Rejected configuration reserved work or changed context/history.");
            Names(selection.Names, afterSelection.Names); Names(snapshot.Agent.Tools.Select(tool => tool.Name), after.Agent.Tools.Select(tool => tool.Name));
            Check(selection.Revision == afterSelection.Revision, "Rejected configuration advanced loadout revision.");
            Check(session.TryClearPendingInputQueues(queues, out var removed) && removed is not null,
                "Rejected configuration changed the authoritative queue revision.");
            SameQueues(queues, removed ?? throw new InvalidOperationException("Queue removal lost its actual snapshot."));
        }
    }

    private static async Task ConfigurationRejectsLateQueueAdmission()
    {
        var entered = Gate(); var release = Gate(); var hold = false;
        await using var fixture = await Fixture.Create((name, _) =>
        {
            if (hold && name == "inactive") { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
            return null;
        });
        var session = await fixture.New("configuration-late-queues"); await using var owner = fixture.Owner(session);
        var snapshot = session.Snapshot; var selection = session.GetToolActivationSelection();
        var queues = session.GetPendingInputQueueSnapshot(); var bytes = await ReadShared(fixture.PathFor("configuration-late-queues"));
        hold = true;
        var original = Task.Run(() => session.SetActiveToolsAsync(["inactive"]));
        try
        {
            await Reach(entered.Task, original);
            Check(session.Snapshot.IsConfiguring && !original.IsCompleted, "Preparation did not retain configuration ownership.");
            await Throws<InvalidOperationException>(() => { session.Steer(QueuedInput("late steering")); return Task.CompletedTask; });
            await Throws<InvalidOperationException>(() => { session.FollowUp(QueuedInput("late followup")); return Task.CompletedTask; });
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<InvalidOperationException>(() => { session.Steer(QueuedInput("cancelled steering"), cancelled.Token); return Task.CompletedTask; });
            await Throws<InvalidOperationException>(() => { session.FollowUp(QueuedInput("cancelled followup"), cancelled.Token); return Task.CompletedTask; });
            SameQueues(queues, session.GetPendingInputQueueSnapshot());
            var heldBytes = await ReadShared(fixture.PathFor("configuration-late-queues"));
            Check(bytes.SequenceEqual(heldBytes), "Late enqueue changed durable bytes during preparation.");
            var held = session.Snapshot; var heldSelection = session.GetToolActivationSelection();
            Check(ReferenceEquals(snapshot.Context, held.Context) && ReferenceEquals(snapshot.Log, held.Log) &&
                snapshot.Agent.Generation == held.Agent.Generation && selection.Revision == heldSelection.Revision,
                "Late enqueue changed context, history or loadout revision.");
            Names(selection.Names, heldSelection.Names); Names(snapshot.Agent.Tools.Select(tool => tool.Name), held.Agent.Tools.Select(tool => tool.Name));
        }
        finally { release.TrySetResult(); await original; }
        Check(!session.Snapshot.IsConfiguring, "Configuration reservation survived the original join.");
        Names(["inactive"], session.GetActiveTools()); SameQueues(queues, session.GetPendingInputQueueSnapshot());

        // An ordinary running turn uses its own request boundary and must still accept both queues.
        fixture.Source.HoldFirstRequest = true;
        var run = session.PromptAsync(Input());
        try
        {
            await Reach(fixture.Source.Entered.Task, run);
            session.Steer(QueuedInput("running steering")); session.FollowUp(QueuedInput("running followup"));
            var activeQueues = session.GetPendingInputQueueSnapshot();
            Check(activeQueues.SteeringMessages.Length == 1 && activeQueues.FollowUpMessages.Length == 1,
                "Configuration queue guard rejected ordinary in-flight steering/followup.");
        }
        finally { fixture.Source.Release.TrySetResult(); await run; }
    }
}
