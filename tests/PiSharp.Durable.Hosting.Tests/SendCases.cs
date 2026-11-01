using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SendCases() =>
    [
        Case("send.waits-for-the-answer-and-admits-a-request-id-once", async () =>
        {
            await using var device = await TestDevice.OpenAsync("send-dedupe");
            var session = await device.CreateAsync("send");
            var queued = await session.SendAsync(1, new SendRequest("first", "req-1"));
            Check(queued.Status is SendStatus.Queued or SendStatus.Done, "without a wait: " + queued.Status);
            var waited = await session.SendAsync(1, new SendRequest("first", "req-1") { Wait = Answer });
            Equal(queued.SubmissionId, waited.SubmissionId, "the same request ID is the same submission");
            Equal(SendStatus.Done, waited.Status, "status");
            Equal("Echo: first", waited.AnswerText, "answer");
            var repeated = await session.SendAsync(1, new SendRequest("first", "req-1"));
            Equal(SendStatus.Done, repeated.Status, "a settled submission is reported settled, not queued");
            Equal(waited.AnswerEntryId, repeated.AnswerEntryId, "its answer");
        }),

        Case("send.tool-turns-answer-with-the-result", async () =>
        {
            await using var device = await TestDevice.OpenAsync("send-tool", new DeviceSetup { Registry = RegistryWith(new TestGate()) });
            var session = await device.CreateAsync("tool");
            var reply = await session.SendAsync(1, new SendRequest("tool: probe {}", "tool-1") { Wait = Answer });
            Equal("Tool finished: probe: ok", reply.AnswerText, "answer");
        }),

        Case("send.reject-pending-and-follow-up-while-busy", async () =>
        {
            var gate = new TestGate();
            await using var device = await TestDevice.OpenAsync("send-busy", new DeviceSetup { Registry = RegistryWith(gate) });
            var session = await device.CreateAsync("busy");
            await session.SendAsync(1, new SendRequest("tool: hang {}", "busy-1"));
            await gate.Started.Task.WaitAsync(Answer);

            var rejected = await ThrowsAsync<HostBusyException>(
                () => session.SendAsync(1, new SendRequest("not now", "busy-2") { WhenBusy = WhenBusy.Reject }), "reject while busy");
            Equal("busy", rejected.Code, "code");
            var pending = await session.SendAsync(1, new SendRequest("after that", "busy-3") { Wait = TimeSpan.FromMilliseconds(200) });
            Equal(SendStatus.Pending, pending.Status, "a follow-up waited on while the run is busy");

            gate.Release.TrySetResult();
            var first = await session.SendAsync(1, new SendRequest("tool: hang {}", "busy-1") { Wait = Answer });
            Equal("Tool finished: hang: released", first.AnswerText, "the first run's answer");
            var followUp = await session.SendAsync(1, new SendRequest("after that", "busy-3") { Wait = Answer });
            Equal("Echo: after that", followUp.AnswerText, "the follow-up ran next");
            var rejectedAgain = await session.SendAsync(1, new SendRequest("idle now", "busy-4") { WhenBusy = WhenBusy.Reject, Wait = Answer });
            Equal("Echo: idle now", rejectedAgain.AnswerText, "reject admits a message while idle");
        }),

        Case("send.bad-requests-are-invalid", async () =>
        {
            await using var device = await TestDevice.OpenAsync("send-invalid");
            var session = await device.CreateAsync("invalid");
            await ThrowsAsync<HostInvalidException>(() => session.SendAsync(1, new SendRequest("x", "")), "an empty request ID");
            await ThrowsAsync<HostInvalidException>(() => session.SendAsync(1, new SendRequest("x", new string('r', 129))), "a long request ID");
            await ThrowsAsync<HostInvalidException>(() => session.SendAsync(1, new SendRequest(new string('x', 100_001), "long")), "a long message");
            await ThrowsAsync<HostNotFoundException>(() => session.SendAsync(99999, new SendRequest("x", "missing")), "an unknown agent");
        }),

        Case("send.reset-and-compact-are-admitted", async () =>
        {
            await using var device = await TestDevice.OpenAsync("send-reset");
            var session = await device.CreateAsync("reset");
            await session.SendAsync(1, new SendRequest("before", "reset-1") { Wait = Answer });
            await session.ResetAsync(1, "We were testing resets.");
            var harness = session.GetHarness();
            var conversation = (await harness.ConversationAsync(ConversationId.Root, DurableContext.Background))!;
            await WaitUntilAsync(async () => (await conversation.ContextAsync(DurableContext.Background)).Head?.Kind == "pi.reset",
                "the active context starts at the reset");
            var task = await session.CompactAsync(1);
            await harness.WaitForTaskAsync(task, DurableContext.Background).WaitAsync(Answer);
        }),
    ];
}
