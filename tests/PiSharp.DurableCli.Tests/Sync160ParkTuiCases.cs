// PiSharp-only (no upstream Pi source): what the durable TUI shows of the Harness states Pi Durable 1.6.0 adds. Upstream's experimental
// durable TUI (packages/coding-agent/src/experimental/durable) did not change in v1.6.0, so these are PiSharp's own lines: a parked tool
// call's status line, a sleeping task's signal keys in the task panel, and a nested call that is parked shown as running in pi's row shape.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Durable;
using PiSharp.Durable;
using static TuiTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160ParkTuiCases() =>
    [
        Case("sync160.tui.describe-task-names-the-signal-keys-a-sleep-waits-for", () =>
        {
            ImmutableArray<SignalKey> keys = [new SignalKey("approvals.decision", "abc"), new SignalKey("example.answer", "answer")];
            // A sleep for signals only has no deadline (Pi Durable 1.6.0).
            Equal("example.ask #7: sleeping answered for approvals.decision/abc, example.answer/answer",
                DurableTuiState.DescribeTask(Node(7, "example.ask", RootId, State(DurableTaskStatus.Sleeping, "answered", signals: keys))), "signals only");
            Equal("pi.tool #8: sleeping parked for example.answer/answer [background]",
                DurableTuiState.DescribeTask(Node(8, "pi.tool", RootId,
                    State(DurableTaskStatus.Sleeping, "parked", until: 61_000, signals: [new SignalKey("example.answer", "answer")]), background: true)),
                "signals and a deadline");
            // A sleep until a time keeps upstream's line, and so does one whose signal list is empty.
            Equal("pi.run #6: sleeping retry", DurableTuiState.DescribeTask(Node(6, "pi.run", RootId, State(DurableTaskStatus.Sleeping, "retry", until: 61_000))),
                "time only");
            Equal("pi.run #6: sleeping retry",
                DurableTuiState.DescribeTask(Node(6, "pi.run", RootId, State(DurableTaskStatus.Sleeping, "retry", until: 61_000, signals: []))), "no keys");
        }),

        Case("sync160.tui.a-parked-call-says-so-in-the-status-line", async () =>
        {
            static string Live(string status) =>
                $$$"""{"run":{"taskId":7,"inputs":[1]},"tools":[{"callId":"c1","name":"run_ci","taskId":12,"status":"{{{status}}}"}]}""";
            var entries = new[] { UserEntry(1, "run ci"), Entry(2, "pi.assistant", AssistantWire("""[{"type":"toolCall","id":"c1","name":"run_ci","arguments":{"ref":"main"}}]""", "toolUse")) };
            await using var run = await TuiRun.Start(View(entries, live: Live("running")));
            await run.WaitFor("Running run_ci... (esc to abort)");
            // Parked: no process holds the call, and the line says which call the run waits for.
            run.Source.Publish(View(entries, live: Live("sleeping")));
            var screen = await run.WaitFor("run_ci is sleeping... (esc to abort)");
            Check(!screen.Contains("Running run_ci...", StringComparison.Ordinal), "not running:\n" + screen);
            run.Source.Publish(View(entries, live: Live("waiting")));
            await run.WaitFor("run_ci is waiting... (esc to abort)");
            // Woken.
            run.Source.Publish(View(entries, live: Live("running")));
            screen = await run.WaitFor("Running run_ci... (esc to abort)");
            Equal(1, screen.Split('\n').Count(line => line.Contains("run_ci", StringComparison.Ordinal) && !line.Contains("esc to abort", StringComparison.Ordinal)),
                "one card for the call, whatever its status was:\n" + screen);
        }),

        Case("sync160.tui.a-parked-nested-call-is-a-running-row", () =>
        {
            // pi's codemode row shape has running, ok, error and cancelled: a parked nested call has not ended, so it is running.
            foreach (var status in new[] { "sleeping", "waiting" })
            {
                var slot = (PiSharp.Durable.Chord.JsonObjectValue)PiSharp.Durable.Chord.JsonValue.Parse(
                    $$$"""{"callId":"c1/a","parentCallId":"c1","parentTaskId":12,"name":"research","taskId":14,"arguments":{"topic":"a"},"status":"{{{status}}}"}""");
                Equal("running", DurableTuiState.NestedRow(new DurableNestedCall(slot))["status"]!.GetValue<string>(), status);
            }
        }),
    ];
}
