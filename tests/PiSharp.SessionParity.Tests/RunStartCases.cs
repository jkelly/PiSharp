// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/agent-session.ts (sendUserMessage -> prompt with
// streamingBehavior: deliverAs, _startRun), by reading. Authored expectations.
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using static EventFixture;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RunStartCases() =>
    [
        Case("run-start.native-send-user-message-needs-deliver-as-while-busy", NativeUserMessageWhileBusy),
    ];

    private sealed class PassThroughInput : IPromptInputAdmission
    {
        public ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PromptInputDecision(PromptInputAction.Continue));
    }

    // The native sendUserMessage action is prompt({ streamingBehavior: deliverAs }): while the session is busy a message without
    // deliverAs fails with prompt()'s error (it used to steer), a steered one is queued, and while idle it starts a run.
    private static async Task NativeUserMessageWhileBusy()
    {
        await using var f = await CreateAsync(Response(text: "first"), Response(text: "second"), Response(text: "third"));
        var reads = new NativeExtensionContextFacadeHost(); reads.Attach(f.Owner);
        var actions = new NativeExistingSessionRegistrationActions(reads, () => new PassThroughInput());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport.DuringStream = async () => { if (f.Transport.Calls == 1) { entered.TrySetResult(); await release.Task; } };
        var run = f.Session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"go","timestamp":20}""")));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Exception? failure = null;
            try { await actions.SendUserMessageAsync(JsonData.Parse("\"no mode\""), null, CancellationToken.None); }
            catch (Exception error) { failure = error; }
            var messages = new List<string>();
            for (var current = failure; current is not null; current = current.InnerException)
            {
                messages.Add(current.Message);
                if (current is AggregateException aggregate) messages.AddRange(aggregate.InnerExceptions.Select(inner => inner.Message));
            }
            Check(messages.Contains(PersistentAgentSession.BusyInputMessage), "busy input without deliverAs: " + string.Join(" | ", messages));
            await actions.SendUserMessageAsync(JsonData.Parse("\"steered\""), new(ExtensionMessageDelivery.Steer), CancellationToken.None);
            Equal(1, f.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length, "steered while busy");
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        await f.Session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var calls = f.Transport.Calls;
        await actions.SendUserMessageAsync(JsonData.Parse("\"idle\""), null, CancellationToken.None);
        Equal(calls + 1, f.Transport.Calls, "an idle message without deliverAs starts a run");
    }
}
