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

        Case("send.signals-onabort-notify-and-fit-images-end-to-end", async () =>
        {
            var approval = Signals.DefineSignal(new SignalDefinition
            {
                Name = "test.approval",
                Version = 1,
                Schema = JsonValue.Object([
                    KeyValuePair.Create("type", (JsonValue)"object"),
                    KeyValuePair.Create("properties", (JsonValue)JsonValue.Object([
                        KeyValuePair.Create("approved", (JsonValue)JsonValue.Object([KeyValuePair.Create("type", (JsonValue)"boolean")])),
                    ])),
                    KeyValuePair.Create("required", (JsonValue)JsonValue.Array("approved")),
                    KeyValuePair.Create("additionalProperties", (JsonValue)false),
                ]),
            });
            var waiterTask = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.signal-waiter",
                Version = 1,
                Initial = _ => JsonValue.Object([KeyValuePair.Create("phase", (JsonValue)"wait")]),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["wait"] = async (task, runtime, context) =>
                    {
                        var key = ((JsonStringValue)((JsonObjectValue)task.Input)["key"]).Value;
                        var signalName = ((JsonStringValue)((JsonObjectValue)task.Input)["signal"]).Value;
                        var token = signalName == Signals.SubmissionSettled.Name ? Signals.SubmissionSettled : approval;
                        var read = await runtime.ReadSignalAsync(token, key, context);
                        if (read is { Expired: false, Value: { } value })
                        {
                            await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Terminal(new CompletedTaskOutcome(value)), context);
                            return;
                        }
                        await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Sleeping(
                            JsonValue.Object([KeyValuePair.Create("phase", (JsonValue)"wait")]),
                            until: null,
                            signals: [token.Key(key)]), context);
                    },
                },
            });

            await using var device = await TestDevice.OpenAsync("send-signals", new DeviceSetup
            {
                Registry = context =>
                {
                    var registry = DurableDeviceHost.DefaultRegistry(context);
                    registry.Install(Define.Extension(new Extension { Name = "test-waiters", Tasks = [waiterTask] }));
                    return registry;
                },
                Configure = options => options with { Signals = [approval] },
            });
            Check(device.Host.FitImagesEnabled, "FitImages enabled by default");
            var session = await device.CreateAsync("signals");
            var harness = session.GetHarness();

            // 1. Typed signal validation, delivery, idempotency replay/conflict, closed and not_found.
            var waiterId = await harness.CommitAsync(tx => tx.CreateTaskAsync(waiterTask,
                JsonValue.Object([KeyValuePair.Create("signal", (JsonValue)approval.Name), KeyValuePair.Create("key", (JsonValue)"k1")]),
                new TaskOptions(TaskOwnership.Conversation) { ConversationId = ConversationId.Root }), DurableContext.Background);
            harness.Resume();
            await WaitUntilAsync(async () => (await harness.GetTaskAsync(waiterId, DurableContext.Background))?.State is SleepingTaskState, "waiter sleeping");

            var invalid = await session.DeliverSignalAsync(waiterId.Value, approval.Name, "k1",
                JsonValue.Object([KeyValuePair.Create("approved", (JsonValue)"wrong")]));
            Equal(SignalResult.Invalid, invalid.Result, "schema rejects wrong type");
            Equal("invalid", invalid.Status, "invalid wire status");

            var validPayload = JsonValue.Object([KeyValuePair.Create("approved", (JsonValue)true)]);
            var delivered = await session.DeliverSignalAsync(waiterId.Value, approval.Name, "k1", validPayload, idempotencyKey: "sig-op-1");
            Equal(SignalResult.Delivered, delivered.Result, "delivered");
            Check(!delivered.Replayed, "first delivery is not replayed");

            var replayed = await session.DeliverSignalAsync(waiterId.Value, approval.Name, "k1", validPayload, idempotencyKey: "sig-op-1");
            Equal(SignalResult.Delivered, replayed.Result, "replayed result");
            Check(replayed.Replayed, "same idempotency key replays");

            await ThrowsAsync<HostConflictException>(() => session.DeliverSignalAsync(waiterId.Value, approval.Name, "k1",
                JsonValue.Object([KeyValuePair.Create("approved", (JsonValue)false)]), idempotencyKey: "sig-op-1"), "conflicting idempotency key");

            var settled = await harness.WaitForTaskAsync(waiterId, DurableContext.Background).WaitAsync(Answer);
            Equal(DurableTaskStatus.Terminal, settled.State.Status, "waiter completed");

            var closed = await session.DeliverSignalAsync(waiterId.Value, approval.Name, "k1", validPayload);
            Equal(SignalResult.Closed, closed.Result, "terminal task returns closed");

            var missing = await session.DeliverSignalAsync(999_999, approval.Name, "k1", validPayload);
            Equal(SignalResult.NotFound, missing.Result, "missing task returns not_found");
            Equal("not_found", missing.Status, "not_found snake_case wire status");

            // 2. Submission notify + onAbort forwarding wakes a task waiting on Signals.SubmissionSettled.
            var notifyWatcherId = await harness.CommitAsync(tx => tx.CreateTaskAsync(waiterTask,
                JsonValue.Object([KeyValuePair.Create("signal", (JsonValue)Signals.SubmissionSettled.Name), KeyValuePair.Create("key", (JsonValue)"sub-done")]),
                new TaskOptions(TaskOwnership.Conversation) { ConversationId = ConversationId.Root }), DurableContext.Background);
            await WaitUntilAsync(async () => (await harness.GetTaskAsync(notifyWatcherId, DurableContext.Background))?.State is SleepingTaskState, "notify watcher sleeping");

            var reply = await session.SendAsync(1, new SendRequest("notify me", "notify-1")
            {
                Wait = Answer,
                OnAbort = InputOnAbort.Keep,
                Notify = new SubmissionNotify(notifyWatcherId, "sub-done"),
            });
            Equal(SendStatus.Done, reply.Status, "notified submission settled");
            var notified = await harness.WaitForTaskAsync(notifyWatcherId, DurableContext.Background).WaitAsync(Answer);
            Check(notified.State is TerminalTaskState { Outcome: CompletedTaskOutcome { Result: JsonObjectValue resultObj } } &&
                resultObj.TryGetValue("status", out var statusVal) && statusVal is JsonStringValue { Value: "done" },
                "submission-settled signal woke watcher with settled record");

            // 3. ToolExecutionEndEvent preserves ImageUnsupportedByModel only when set.
            var endWithWarning = new ToolExecutionEndEvent("c1", "read") { ImageUnsupportedByModel = "faux/echo" };
            Equal("faux/echo", ((JsonStringValue)endWithWarning.ToJson()["imageUnsupportedByModel"]).Value, "warning serialized");
            var endWithoutWarning = new ToolExecutionEndEvent("c2", "read");
            Check(!endWithoutWarning.ToJson().ContainsKey("imageUnsupportedByModel"), "null warning omitted");
        }),
    ];
}
