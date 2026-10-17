using PiSharp.Durable.Hosting;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> RecoveryCases() =>
    [
        Case("recovery.a-tool-cut-off-by-a-restart-is-interrupted-not-rerun", async () =>
        {
            var gate = new TestGate();
            await using var device = await TestDevice.OpenAsync("recovery-tool", new DeviceSetup { Registry = RegistryWith(gate) });
            var session = await device.CreateAsync("recovery");
            await session.SendAsync(1, new SendRequest("tool: hang {}", "rec-1"));
            await gate.Started.Task.WaitAsync(Answer);

            // The device restarts while the tool runs; after it, the same tool would finish at once.
            var after = new TestGate();
            after.Release.TrySetResult();
            await device.ReopenAsync(new DeviceSetup { Registry = RegistryWith(after) });
            var reopened = device.Host.ResolveSession(session.Id);
            var done = await reopened.SendAsync(1, new SendRequest("tool: hang {}", "rec-1") { Wait = Answer });
            Equal(SendStatus.Done, done.Status, "the run finished after the restart");
            Check(done.AnswerText?.Contains("interrupted") == true, "the agent was told the call was interrupted: " + done.AnswerText);
            Equal(0, after.Runs, "an unsafe tool is not run again");
            Equal(1, gate.Runs, "it ran once");
        }),

        Case("recovery.queued-messages-survive-a-restart", async () =>
        {
            var gate = new TestGate();
            await using var device = await TestDevice.OpenAsync("recovery-queue", new DeviceSetup { Registry = RegistryWith(gate) });
            var session = await device.CreateAsync("queue");
            await session.SendAsync(1, new SendRequest("tool: hang {}", "q-1"));
            await gate.Started.Task.WaitAsync(Answer);
            var queued = await session.SendAsync(1, new SendRequest("next please", "q-2"));
            Equal(SendStatus.Queued, queued.Status, "queued behind the run");

            var after = new TestGate();
            after.Release.TrySetResult();
            await device.ReopenAsync(new DeviceSetup { Registry = RegistryWith(after) });
            var reply = await device.Host.ResolveSession(session.Id).SendAsync(1, new SendRequest("next please", "q-2") { Wait = Answer });
            Equal(queued.SubmissionId, reply.SubmissionId, "the same submission");
            Equal("Echo: next please", reply.AnswerText, "answered after the restart");
        }),
    ];
}
