using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RegisteredInputRpcTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("registered-rpc", "openai-responses", "offline-authored");
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"registered-rpc","api":"openai-responses","provider":"offline-authored","name":"Offline registered input","baseUrl":"https://offline.invalid","reasoning":false,"input":["text","image"],"contextWindow":8192,"maxTokens":1024,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    private static readonly JsonData Images = JsonData.Parse("""[{"type":"image","data":"YWJj","mimeType":"image/png","opaque":{"fraction":1.0,"nil":null}}]""");
    private static readonly JsonData ReplacedImages = JsonData.Parse("""[{"type":"image","data":"ZA==","mimeType":"image/webp","opaque":{"fraction":2.0}}]""");

    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("rpc.registered-input-real-two-hook-transform-images-continue-durability-and-reopen", TransformsAndDurability),
        ("rpc.registered-input-handled-and-rejected-admission-preserve-history-queues-and-effects", HandledAndFailures),
        ("rpc.registered-input-started-ack-waits-real-entry-and-flush-before-events-and-provider", ActualStartBarrier),
        ("rpc.registered-input-idle-busy-modes-use-the-one-real-queue-and-durable-drain", ActualQueues),
        ("rpc.registered-input-final-bounds-and-concurrent-clear-mode-growth-preflight", BoundsAndQueueRaces),
        ("rpc.registered-input-cancellation-eof-output-failure-join-admission-owner-and-output", CancellationAndCleanup)
    ];

    private static async Task TransformsAndDurability()
    {
        var seen = new List<string>();
        await using var fixture = await Fixture.Create((registry, _) =>
        {
            registry.RegisterInputHandler(new("first", (input, _, _) =>
            {
                Equal(ExtensionInputSource.Rpc, input.Source); Check(input.StreamingBehavior is null, "Idle prompt exposed a mode.");
                seen.Add("first:" + input.Text);
                return Patch(input.Text == "plain" ? null : new(ExtensionInputAction.Transform, input.Text + "|A", JsonData.Null));
            }));
            registry.RegisterInputHandler(new("second", (input, _, _) =>
            {
                seen.Add("second:" + input.Text);
                if (input.Text == "plain") return Patch(null);
                Equal(Images.ToString(), input.Images!.ToString());
                return Patch(new(ExtensionInputAction.Transform, input.Text + "|B", ReplacedImages));
            }));
            return ValueTask.CompletedTask;
        });
        await fixture.Send("prompt", "transform", "original", Images, "steer");
        await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Equal("started", fixture.Disposition("transform")); Equal("first:original|second:original|A", string.Join('|', seen));
        var user = fixture.Session.Snapshot.Context.Messages[0]; Equal("original|A|B", Text(user));
        Equal("2.0", user.WireBody.Value.GetProperty("content")[1].GetProperty("opaque").GetProperty("fraction").GetRawText());
        Equal(ReplacedImages.Value[0].GetRawText(), user.WireBody.Value.GetProperty("content")[1].GetRawText());
        Equal(user.WireBody.ToString(), fixture.Transport.Requests()[0].Messages[0].WireBody.ToString());
        Equal(user.WireBody.ToString(), fixture.Output.Records().Single(value => Type(value) == "message_end" &&
            value.Value.GetProperty("message").GetProperty("role").GetString() == "user").Value.GetProperty("message").GetRawText());
        await Acknowledged(fixture.Session);
        var prior = fixture.Session.Snapshot.Context.Messages;
        await fixture.Send("prompt", "continue", "plain", Images);
        await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Equal("plain", Text(fixture.Transport.Requests()[1].Messages[^1]));
        Equal("1.0", fixture.Transport.Requests()[1].Messages[^1].WireBody.Value.GetProperty("content")[1].GetProperty("opaque").GetProperty("fraction").GetRawText());
        Prefix(prior, fixture.Transport.Requests()[1].Messages);
        var committed = fixture.Session.Snapshot.Context.Messages;
        await fixture.Dispatcher.DisposeAsync();
        await using var reopened = await PersistentAgentSession.OpenAsync(fixture.Session.Path,
            new(Model, new Script(), fixture.Binding.Tools), fixture.Clock, fixture.NextId);
        Prefix(committed, reopened.Snapshot.Context.Messages); Equal(committed.Length, reopened.Snapshot.Context.Messages.Length);
        await Acknowledged(reopened);
    }

    private static async Task HandledAndFailures()
    {
        var calls = new List<string>(); var diagnostics = new List<ExtensionEventDiagnostic>();
        await using var fixture = await Fixture.Create((registry, _) =>
        {
            registry.RegisterInputHandler(new("transform", (input, _, _) =>
            { calls.Add("transform"); return Patch(new(ExtensionInputAction.Transform, input.Text + "|changed")); }));
            registry.RegisterInputHandler(new("decision", (input, _, _) =>
            {
                calls.Add("decision");
                if (input.Text.StartsWith("failure", StringComparison.Ordinal)) throw new IOException("SECRET admission failure");
                return Patch(new(ExtensionInputAction.Handled));
            }));
            registry.RegisterInputHandler(new("tail", (_, _, _) => { calls.Add("tail"); return Patch(null); }));
            return ValueTask.CompletedTask;
        }, diagnostics: (value, _) => { diagnostics.Add(value); throw new IOException("SECRET diagnostic observer"); });
        fixture.Session.Steer(User("existing steering")); fixture.Session.FollowUp(User("existing follow-up"));
        var queue = fixture.Session.GetPendingInputQueueSnapshot(); var bytes = await Bytes(fixture.Session.Path);
        var snapshot = fixture.Session.Snapshot; var clocks = fixture.Clocks; var ids = fixture.Ids;
        await fixture.Send("prompt", "handled", "handled", Images);
        Equal("handled", fixture.Disposition("handled")); Equal("transform|decision", string.Join('|', calls));
        Equal(clocks, fixture.Clocks); Equal(ids, fixture.Ids); Equal(0, fixture.Transport.Requests().Length);
        Equal(snapshot.Agent.Generation, fixture.Session.Snapshot.Agent.Generation); Equal(0, fixture.Session.Snapshot.Context.Messages.Length);
        Queues(queue, fixture.Session.GetPendingInputQueueSnapshot()); EqualBytes(bytes, await Bytes(fixture.Session.Path));
        Check(!fixture.Output.Records().Any(value => Type(value) is "agent_start" or "agent_end" or "agent_settled" or "queue_update"), "Handled input invented lifecycle or queue output.");
        await fixture.Send("steer", "queue-handled", "handled"); Equal("handled", fixture.Disposition("queue-handled"));
        await fixture.Send("prompt", "failure", "failure");
        Check(!fixture.Response("failure").Value.GetProperty("success").GetBoolean(), "Failed awaited diagnostic was accepted.");
        Equal("Prompt input admission failed.", fixture.Response("failure").Value.GetProperty("error").GetString());
        Equal(ExtensionEventFailure.HandlerFailed, diagnostics.Single().Failure);
        Check(!fixture.Output.Records().Any(value => value.ToString().Contains("SECRET", StringComparison.Ordinal)), "Private failure entered the wire.");
        Equal(clocks, fixture.Clocks); Equal(ids, fixture.Ids); Queues(queue, fixture.Session.GetPendingInputQueueSnapshot());
        EqualBytes(bytes, await Bytes(fixture.Session.Path)); Check(fixture.Session.Snapshot.Fault is null, "Rejected hook poisoned durable state.");
        var addedCalls = 0; var capturedRevision = fixture.Binding.Snapshot.Revision;
        await fixture.Registry.ActivateAsync("revision", new Extension((registry, _) =>
        { registry.RegisterInputHandler(new("new", (_, _, _) => { addedCalls++; return Patch(null); })); return ValueTask.CompletedTask; }));
        var count = calls.Count;
        await fixture.Send("follow_up", "addition", "addition");
        Equal("handled", fixture.Disposition("addition")); Equal("transform|decision", string.Join('|', calls.Skip(count))); Equal(0, addedCalls);
        Equal(capturedRevision, fixture.Binding.Snapshot.Revision); Check(fixture.Registry.CaptureSnapshot().Revision > capturedRevision, "Owner addition did not publish a new snapshot.");
        Equal(3, fixture.Binding.Snapshot.InputHandlers.Length); Equal(4, fixture.Registry.CaptureSnapshot().InputHandlers.Length);
        Equal(clocks, fixture.Clocks); Equal(ids, fixture.Ids); Equal(0, fixture.Transport.Requests().Length);
        Equal(snapshot.Agent.Generation, fixture.Session.Snapshot.Agent.Generation); Equal(0, fixture.Session.Snapshot.Context.Messages.Length);
        EqualBytes(bytes, await Bytes(fixture.Session.Path)); Queues(queue, fixture.Session.GetPendingInputQueueSnapshot());
        Equal("registered", fixture.Scope.OwnerId); await fixture.Scope.DisposeAsync(); count = calls.Count;
        await fixture.Send("follow_up", "stale", "stale");
        Check(!fixture.Response("stale").Value.GetProperty("success").GetBoolean(), "Removed owner remained admissible through its captured binding.");
        Equal("Prompt input admission failed.", fixture.Response("stale").Value.GetProperty("error").GetString());
        Equal(count, calls.Count); EqualBytes(bytes, await Bytes(fixture.Session.Path)); Queues(queue, fixture.Session.GetPendingInputQueueSnapshot());
        Equal(0, addedCalls); Equal(clocks, fixture.Clocks); Equal(ids, fixture.Ids); Equal(0, fixture.Transport.Requests().Length);
        Equal(snapshot.Agent.Generation, fixture.Session.Snapshot.Agent.Generation); Equal(0, fixture.Session.Snapshot.Context.Messages.Length);
        Check(!fixture.Session.Snapshot.IsAdmittingInput && fixture.Session.Snapshot.Fault is null, "Owner revocation leaked or poisoned input admission.");
        Check(!fixture.Output.Records().Any(value => Type(value) is "agent_start" or "agent_end" or "agent_settled" or "queue_update"), "Addition or revocation invented lifecycle or queue output.");
    }

    private static async Task ActualStartBarrier()
    {
        var hookEntered = Gate(); var hookRelease = Gate(); var output = new Capture(); var storage = new StorageFactory();
        var provider = new Step(Pause: true);
        await using var fixture = await Fixture.Create(async (registry, _) =>
        {
            registry.RegisterInputHandler(new("held", async (input, _, token) =>
            { hookEntered.TrySetResult(); await hookRelease.Task.WaitAsync(token); return new(ExtensionInputAction.Transform, input.Text + "|hook"); }));
            await ValueTask.CompletedTask;
        }, new Script(provider), output, storage);
        var checkpoint = storage.Storage!.Arm(); var ackEntered = Gate(); var ackRelease = Gate();
        output.BeforeFlush = async (record, token) =>
        { if (IsResponse(record, "start")) { ackEntered.TrySetResult(); await ackRelease.Task.WaitAsync(token); } };
        var generation = fixture.Session.Snapshot.Agent.Generation; var prompt = fixture.Send("prompt", "start", "start");
        try
        {
            await hookEntered.Task.WaitAsync(Deadline);
            Check(!prompt.IsCompleted && !fixture.Session.Snapshot.Agent.IsRunning, "Hook preflight fabricated actual admission.");
            Equal(0, output.Records().Length); Equal(0, fixture.Transport.Requests().Length);
            await fixture.Send("get_state", "during-hook");
            Check(!fixture.Response("during-hook").Value.GetProperty("data").GetProperty("isStreaming").GetBoolean(), "Hook reservation became streaming.");
            hookRelease.TrySetResult(); await ackEntered.Task.WaitAsync(Deadline);
            Check(fixture.Session.Snapshot.Agent.IsRunning && fixture.Session.Snapshot.Agent.Generation > generation, "Started ack preceded actual Agent entry.");
            Check(!prompt.IsCompleted && !output.Records().Any(value => IsResponse(value, "start") || Type(value) == "agent_start"), "Unflushed ack released events.");
            Equal(0, fixture.Transport.Requests().Length); Check(!checkpoint.Entered.Task.IsCompleted, "Actual input commit crossed the ack barrier.");
            ackRelease.TrySetResult(); await prompt.WaitAsync(Deadline); Equal("started", fixture.Disposition("start"));
            await checkpoint.Entered.Task.WaitAsync(Deadline); Equal(0, fixture.Transport.Requests().Length);
            await fixture.Send("get_messages", "before-checkpoint"); Equal(0, fixture.Response("before-checkpoint").Value.GetProperty("data").GetProperty("messages").GetArrayLength());
            checkpoint.Release.TrySetResult(); await provider.Entered.Task.WaitAsync(Deadline);
            Check(!fixture.Dispatcher.WaitForIdleAsync().IsCompleted, "Started response meant provider settlement.");
            var records = output.Records(); Check(Array.FindIndex(records, value => IsResponse(value, "start")) < Array.FindIndex(records, value => Type(value) == "agent_start"), "Event preceded ack.");
            provider.Release.TrySetResult(); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            Equal(1, output.Records().Count(value => IsResponse(value, "start"))); Equal(1, output.Records().Count(value => Type(value) == "agent_settled"));
            await Acknowledged(fixture.Session);
        }
        finally { hookRelease.TrySetResult(); ackRelease.TrySetResult(); checkpoint.Release.TrySetResult(); provider.Release.TrySetResult(); }
    }

    private static async Task ActualQueues()
    {
        foreach (var mode in new[] { "all", "one-at-a-time" })
        {
            var observed = new List<(string Text, string? Mode)>(); var first = new Step(Pause: true);
            await using var fixture = await Fixture.Create((registry, _) =>
            {
                registry.RegisterInputHandler(new("queue", (input, _, _) =>
                {
                    Equal(ExtensionInputSource.Rpc, input.Source); observed.Add((input.Text, input.StreamingBehavior));
                    return Patch(input.Text == "handled" ? new(ExtensionInputAction.Handled) : new(ExtensionInputAction.Transform, input.Text + "|hook"));
                }));
                return ValueTask.CompletedTask;
            }, new Script(first));
            await fixture.Send("steer", "idle-steer", "idle steer", Images);
            await fixture.Send("follow_up", "idle-follow", "idle follow", Images);
            Equal("queued", fixture.Disposition("idle-steer")); Equal("queued", fixture.Disposition("idle-follow"));
            Equal("steer", observed[0].Mode); Equal("followUp", observed[1].Mode); Equal(0L, fixture.Session.Snapshot.Agent.Generation);
            var idle = fixture.Session.GetPendingInputQueueSnapshot(); Equal("idle steer|hook", Text(idle.SteeringMessages.Single()));
            Equal(Images.Value[0].GetRawText(), idle.FollowUpMessages.Single().WireBody.Value.GetProperty("content")[1].GetRawText());
            Equal(0, fixture.Transport.Requests().Length); Equal(0, fixture.Session.Snapshot.Context.Messages.Length);
            await fixture.Send("clear_queue", "clear-idle");
            Equal("idle steer|hook", fixture.Response("clear-idle").Value.GetProperty("data").GetProperty("steering")[0].GetString());
            await fixture.Send("set_steering_mode", "sm", mode: mode); await fixture.Send("set_follow_up_mode", "fm", mode: mode);
            await fixture.Send("prompt", "initial", "begin"); await first.Entered.Task.WaitAsync(Deadline);
            await fixture.Send("prompt", "busy-handled", "handled"); Equal("handled", fixture.Disposition("busy-handled"));
            await fixture.Send("prompt", "busy-rejected", "reject"); Check(!fixture.Response("busy-rejected").Value.GetProperty("success").GetBoolean(), "Unspecified busy delivery entered a queue.");
            await fixture.Send("steer", "s1", "steer one", Images); await fixture.Send("prompt", "s2", "steer two", behavior: "steer");
            await fixture.Send("follow_up", "f1", "follow one"); await fixture.Send("prompt", "f2", "follow two", behavior: "followUp");
            foreach (var id in new[] { "s1", "s2", "f1", "f2" }) Equal("queued", fixture.Disposition(id));
            var queued = fixture.Session.GetPendingInputQueueSnapshot(); Equal(2, queued.SteeringMessages.Length); Equal(2, queued.FollowUpMessages.Length);
            var wire = fixture.Output.Records().Last(value => Type(value) == "queue_update").Value;
            Equal(string.Join('|', queued.SteeringMessages.Select(Text)), string.Join('|', wire.GetProperty("steering").EnumerateArray().Select(value => value.GetString())));
            Equal(string.Join('|', queued.FollowUpMessages.Select(Text)), string.Join('|', wire.GetProperty("followUp").EnumerateArray().Select(value => value.GetString())));
            first.Release.TrySetResult(); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            var users = fixture.Session.Snapshot.Context.Messages.Where(value => value.Role == "user").ToArray();
            Equal("begin|hook|steer one|hook|steer two|hook|follow one|hook|follow two|hook", string.Join('|', users.Select(Text)));
            Equal(mode == "all" ? 3 : 5, fixture.Transport.Requests().Length);
            Equal(queued.SteeringMessages[0].WireBody.ToString(), users[1].WireBody.ToString());
            Equal(queued.FollowUpMessages[1].WireBody.ToString(), users[4].WireBody.ToString());
            Equal(0, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length); Equal(0, fixture.Session.GetPendingInputQueueSnapshot().FollowUpMessages.Length);
            await Acknowledged(fixture.Session);
        }
        await HeldInputAcrossSettlement();
    }

    private static async Task HeldInputAcrossSettlement()
    {
        var hookEntered = Gate(); var hookRelease = Gate(); var first = new Step(Pause: true); var second = new Step(Pause: true);
        await using var fixture = await Fixture.Create((registry, _) =>
        {
            registry.RegisterInputHandler(new("late", async (input, _, token) =>
            {
                if (input.Text == "late")
                {
                    Equal("steer", input.StreamingBehavior); hookEntered.TrySetResult(); await hookRelease.Task.WaitAsync(token);
                }
                return new(ExtensionInputAction.Transform, input.Text + "|hook");
            }));
            return ValueTask.CompletedTask;
        }, new Script(first, second));
        await fixture.Send("prompt", "first", "first"); await first.Entered.Task.WaitAsync(Deadline);
        // Capture the old coordinator join before admitting the held input, so it excludes that reservation.
        var oldIdle = fixture.Session.WaitForIdleAsync(); var late = fixture.Send("prompt", "late", "late", behavior: "steer");
        try
        {
            await hookEntered.Task.WaitAsync(Deadline); first.Release.TrySetResult(); await oldIdle.WaitAsync(Deadline);
            Check(!fixture.Session.Snapshot.Agent.IsRunning && fixture.Session.Snapshot.IsAdmittingInput, "Held hook did not span actual generation settlement.");
            Check(!fixture.Output.Records().Any(value => Type(value) == "agent_settled"), "Old monitor escaped the outstanding input reservation.");
            hookRelease.TrySetResult(); await late.WaitAsync(Deadline); await second.Entered.Task.WaitAsync(Deadline);
            Equal("started", fixture.Disposition("late")); Equal(0, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length);
            Equal("late|hook", Text(fixture.Transport.Requests()[1].Messages[^1]));
            second.Release.TrySetResult(); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            Equal(1, fixture.Output.Records().Count(value => Type(value) == "agent_settled"));
            var endings = fixture.Output.Records().Where(value => Type(value) == "agent_end").ToArray(); Equal(2, endings.Length);
            Equal(2, endings[0].Value.GetProperty("messages").GetArrayLength()); Equal(2, endings[1].Value.GetProperty("messages").GetArrayLength());
            Equal("late|hook", endings[1].Value.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
            await Acknowledged(fixture.Session);
        }
        finally { hookRelease.TrySetResult(); first.Release.TrySetResult(); second.Release.TrySetResult(); }
    }

    private static async Task BoundsAndQueueRaces()
    {
        await using (var fixture = await Fixture.Create((registry, _) =>
        {
            registry.RegisterInputHandler(new("big", (_, _, _) => Patch(new(ExtensionInputAction.Transform, new string('x', 300)))));
            return ValueTask.CompletedTask;
        }, options: new(MaximumOutputBytes: 256)))
        {
            var bytes = await Bytes(fixture.Session.Path); var ids = fixture.Ids;
            await fixture.Send("steer", "too-big", "small");
            Check(!fixture.Response("too-big").Value.GetProperty("success").GetBoolean(), "Oversized transformed queue receipt was accepted.");
            Equal(0, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length); Equal(0, fixture.Transport.Requests().Length);
            Equal(ids, fixture.Ids); EqualBytes(bytes, await Bytes(fixture.Session.Path));
            Check(!fixture.Output.Records().Any(value => Type(value) == "queue_update"), "Rejected transformed input changed queue output.");
        }
        await using (var fixture = await Fixture.Create((registry, _) =>
        {
            registry.RegisterInputHandler(new("images", (input, _, _) => Patch(new(ExtensionInputAction.Transform, input.Text, Images))));
            return ValueTask.CompletedTask;
        }, options: new(MaximumImages: 0)))
        {
            var bytes = await Bytes(fixture.Session.Path); await fixture.Send("follow_up", "no-images", "small");
            Check(!fixture.Response("no-images").Value.GetProperty("success").GetBoolean(), "Transformed images bypassed the RPC image limit.");
            Equal(0, fixture.Session.GetPendingInputQueueSnapshot().FollowUpMessages.Length); EqualBytes(bytes, await Bytes(fixture.Session.Path));
        }
        var entered = Gate(); var release = Gate();
        await using var race = await Fixture.Create((registry, _) =>
        {
            registry.RegisterInputHandler(new("held", async (input, _, token) =>
            { if (input.Text == "held") { entered.TrySetResult(); await release.Task.WaitAsync(token); } return new(ExtensionInputAction.Transform, input.Text + "|hook"); }));
            return ValueTask.CompletedTask;
        });
        await race.Send("steer", "old", "old"); var held = race.Send("follow_up", "held", "held");
        try
        {
            await entered.Task.WaitAsync(Deadline); await race.Send("clear_queue", "clear"); await race.Send("set_follow_up_mode", "mode", mode: "all");
            release.TrySetResult(); await held.WaitAsync(Deadline);
            var queue = race.Session.GetPendingInputQueueSnapshot(); Equal(0, queue.SteeringMessages.Length); Equal("held|hook", Text(queue.FollowUpMessages.Single()));
            Equal(AgentPendingInputMode.All, queue.FollowUpMode);
        }
        finally { release.TrySetResult(); }
        race.Session.ClearPendingInputQueues();
        var captured = Gate(); var commitRelease = Gate(); var validations = 0;
        var submission = Task.Run(() => race.Session.SubmitInputAsync(new("new", PromptInputSource.Rpc, StreamingBehavior: PromptInputStreamingBehavior.Steer),
            options: new() { QueueOnly = true, BeforeQueueCommit = (_, queue, _) =>
            {
                if (Interlocked.Increment(ref validations) == 1)
                {
                    Check(Throws<InvalidOperationException>(() => race.Session.Steer(User("reentrant"))) is not null, "Preflight allowed reentrant mutation.");
                    Throws<InvalidOperationException>(() => race.Session.ClearPendingInputQueues());
                    Throws<InvalidOperationException>(() => race.Session.SteeringMode = AgentPendingInputMode.All);
                    Throws<InvalidOperationException>(() => race.Session.WaitForIdleAsync());
                    Throws<InvalidOperationException>(() => race.Session.DisposeAsync());
                    captured.TrySetResult(); commitRelease.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
                }
                else if (!queue.SteeringMessages.IsEmpty) throw new PromptInputAdmissionException(PromptInputAdmissionFailure.ResourceLimit);
            } }));
        try
        {
            await captured.Task.WaitAsync(Deadline); race.Session.Steer(User("external growth")); commitRelease.TrySetResult();
            await ThrowsAsync<PromptInputAdmissionException>(() => submission); Equal(2, validations);
            Equal("external growth", Text(race.Session.GetPendingInputQueueSnapshot().SteeringMessages.Single()));
            Check(!race.Session.Snapshot.IsAdmittingInput && race.Session.Snapshot.Fault is null, "Preflight race leaked or poisoned a reservation.");
        }
        finally { commitRelease.TrySetResult(); }
        race.Session.ClearPendingInputQueues();
        var attempts = Enumerable.Range(0, 8).Select(_ => (Entered: Gate(), Release: Gate())).ToArray(); validations = 0;
        var contested = Task.Run(() => race.Session.SubmitInputAsync(new("never queued", StreamingBehavior: PromptInputStreamingBehavior.Steer),
            options: new() { QueueOnly = true, BeforeQueueCommit = (_, _, _) =>
            {
                var attempt = attempts[Interlocked.Increment(ref validations) - 1]; attempt.Entered.TrySetResult();
                attempt.Release.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
            } }));
        try
        {
            for (var index = 0; index < attempts.Length; index++)
            {
                await attempts[index].Entered.Task.WaitAsync(Deadline); race.Session.Steer(User("growth-" + index)); attempts[index].Release.TrySetResult();
            }
            Equal(PromptInputAdmissionFailure.ResourceLimit, (await ThrowsAsync<PromptInputAdmissionException>(() => contested)).Failure);
            Equal(8, validations); Equal(8, race.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length);
            Check(!race.Session.GetPendingInputQueueSnapshot().SteeringMessages.Any(value => Text(value) == "never queued"), "Contended input was appended without validated receipt.");
            Check(!race.Session.Snapshot.IsAdmittingInput && race.Session.Snapshot.Fault is null, "Retry exhaustion leaked or poisoned admission.");
        }
        finally { foreach (var attempt in attempts) attempt.Release.TrySetResult(); }
    }

    private static async Task CancellationAndCleanup()
    {
        await CallerCancellationAndReentrancy();
        var entered = Gate(); var cleanup = Gate(); var cleanupRelease = Gate(); var output = new Capture { HoldDispose = true };
        await using (var fixture = await Fixture.Create((registry, _) =>
        {
            registry.RegisterInputHandler(new("held", async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); return null; }
                finally { cleanup.TrySetResult(); await cleanupRelease.Task.WaitAsync(Deadline); }
            }));
            return ValueTask.CompletedTask;
        }, output: output))
        {
            var bytes = await Bytes(fixture.Session.Path);
            await using var input = new EofInput(Encoding.UTF8.GetBytes("{\"type\":\"prompt\",\"id\":\"eof\",\"message\":\"held\"}\n"));
            var run = fixture.Dispatcher.RunAsync(new JsonlReader(input, ownership: JsonlStreamOwnership.Borrowed));
            try
            {
                await entered.Task.WaitAsync(Deadline); var idle = fixture.Dispatcher.WaitForIdleAsync(); Check(!idle.IsCompleted, "Held hook escaped idle join.");
                input.Eof.TrySetResult(); await cleanup.Task.WaitAsync(Deadline);
                Check(!run.IsCompleted && !fixture.Dispatcher.Completion.IsCompleted && !output.DisposeEntered.Task.IsCompleted, "EOF skipped admitted callback cleanup.");
                cleanupRelease.TrySetResult(); await output.DisposeEntered.Task.WaitAsync(Deadline);
                Check(!run.IsCompleted && !fixture.Dispatcher.Completion.IsCompleted, "EOF skipped output disposal join.");
                var repeated = fixture.Dispatcher.DisposeAsync().AsTask(); output.DisposeRelease.TrySetResult();
                await Task.WhenAll(run, repeated, idle).WaitAsync(Deadline); await fixture.Dispatcher.Completion.WaitAsync(Deadline);
                Equal(1, output.Disposals); Equal(0, fixture.Transport.Requests().Length); EqualBytes(bytes, await Bytes(fixture.Session.Path));
                Check(!fixture.Session.Snapshot.IsAdmittingInput, "EOF leaked actual input admission.");
                Check(fixture.Response("eof").Value.GetProperty("success").GetBoolean() == false, "Cancelled held input became successful.");
                // The dispatcher borrows admission/registry lifetime; the composing host closes it after RPC cleanup.
                await fixture.Registry.DisposeAsync();
            }
            finally { input.Eof.TrySetResult(); cleanupRelease.TrySetResult(); output.DisposeRelease.TrySetResult(); }
        }
        var provider = new Step(HoldCleanup: true); var broken = new Capture { FailOnType = "message_update" };
        await using var failed = await Fixture.Create((registry, _) =>
        { registry.RegisterInputHandler(new("continue", (_, _, _) => Patch(null))); return ValueTask.CompletedTask; }, new Script(provider), broken);
        try
        {
            await failed.Send("prompt", "accepted", "start"); await provider.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!failed.Dispatcher.Completion.IsCompleted, "Broken output skipped provider cleanup.");
            provider.CleanupRelease.TrySetResult();
            Equal(RpcDispatchFailure.OutputFailed, (await ThrowsAsync<RpcDispatchException>(() => failed.Dispatcher.Completion)).Failure);
            Equal(1, broken.Records().Count(value => IsResponse(value, "accepted")));
            Check(!broken.Records().Any(value => Type(value) == "agent_settled"), "Broken output emitted normal settlement.");
            Equal(1, provider.Cleanups); Equal(1, broken.Disposals);
        }
        finally { provider.CleanupRelease.TrySetResult(); }
    }

    private static async Task CallerCancellationAndReentrancy()
    {
        var entered = Gate(); var cleanup = Gate(); var cleanupRelease = Gate(); var provider = new Step(Pause: true);
        RpcSessionDispatcher? dispatcher = null; PersistentAgentSession? session = null;
        await using var fixture = await Fixture.Create((registry, _) =>
        {
            registry.RegisterInputHandler(new("caller", async (input, _, token) =>
            {
                if (input.Text != "held") return null;
                Throws<InvalidOperationException>(() => session!.WaitForIdleAsync());
                Throws<InvalidOperationException>(() => dispatcher!.WaitForIdleAsync());
                Throws<InvalidOperationException>(() => dispatcher!.DisposeAsync());
                Throws<InvalidOperationException>(() => dispatcher!.SubmitAsync(JsonData.Parse("""{"type":"steer","message":"recursive"}""")));
                await dispatcher!.SubmitAsync(JsonData.Parse("""{"type":"get_state","id":"hook-read"}"""));
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); return null; }
                finally { cleanup.TrySetResult(); await cleanupRelease.Task.WaitAsync(Deadline); }
            }));
            return ValueTask.CompletedTask;
        }, new Script(provider));
        dispatcher = fixture.Dispatcher; session = fixture.Session;
        var bytes = await Bytes(session.Path); var clocks = fixture.Clocks; var ids = fixture.Ids;
        using var caller = new CancellationTokenSource();
        var pending = dispatcher.SubmitAsync(JsonData.Parse("""{"type":"prompt","id":"cancelled","message":"held"}"""), caller.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline); caller.Cancel(); await cleanup.Task.WaitAsync(Deadline);
            Check(!pending.IsCompleted && session.Snapshot.IsAdmittingInput, "Caller cancellation skipped registered cleanup.");
            cleanupRelease.TrySetResult(); await pending.WaitAsync(Deadline);
            Check(!fixture.Response("cancelled").Value.GetProperty("success").GetBoolean(), "Cancelled preflight was accepted.");
            Equal(clocks, fixture.Clocks); Equal(ids, fixture.Ids); Equal(0, fixture.Transport.Requests().Length);
            EqualBytes(bytes, await Bytes(session.Path)); Check(session.Snapshot.Fault is null, "Cancelled preflight poisoned the session.");
            using var acceptedCaller = new CancellationTokenSource();
            await dispatcher.SubmitAsync(JsonData.Parse("""{"type":"prompt","id":"accepted-caller","message":"start"}"""), acceptedCaller.Token);
            await provider.Entered.Task.WaitAsync(Deadline); acceptedCaller.Cancel();
            Equal("started", fixture.Disposition("accepted-caller")); Check(!session.Snapshot.Agent.CancellationRequested, "Returned caller cancellation aborted an accepted generation.");
            provider.Release.TrySetResult(); await dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            Equal(StopReason.Stop.ToString().ToLowerInvariant(), session.Snapshot.Context.Messages[^1].WireBody.Value.GetProperty("stopReason").GetString());
            await Acknowledged(session);
        }
        finally { cleanupRelease.TrySetResult(); provider.Release.TrySetResult(); }
    }

    private static ValueTask<ExtensionInputPatch?> Patch(ExtensionInputPatch? value) => ValueTask.FromResult(value);
    private static TranscriptEntry User(string text) => PromptInputValue.Message(new(text), 123);
    private static string Text(TranscriptEntry message) => message.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
    private static string Type(JsonData value) => value.Value.GetProperty("type").GetString()!;
    private static bool IsResponse(JsonData value, string id) => Type(value) == "response" && value.Value.TryGetProperty("id", out var identity) && identity.GetString() == id;
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void EqualBytes(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), "Physical durable bytes changed.");
    private static void Prefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { Check(actual.Length >= expected.Length, "Prior history disappeared."); for (var i = 0; i < expected.Length; i++) Equal(expected[i].WireBody.ToString(), actual[i].WireBody.ToString()); }
    private static void Queues(AgentPendingInputQueueSnapshot expected, AgentPendingInputQueueSnapshot actual)
    { Equal(expected.SteeringMessages.Length, actual.SteeringMessages.Length); Equal(expected.FollowUpMessages.Length, actual.FollowUpMessages.Length); Prefix(expected.SteeringMessages, actual.SteeringMessages); Prefix(expected.FollowUpMessages, actual.FollowUpMessages); }
    private static T Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T value) { return value; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run().WaitAsync(Deadline); } catch (T value) { return value; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<byte[]> Bytes(string path)
    { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); return bytes.ToArray(); }
    private static async Task Acknowledged(PersistentAgentSession session)
    {
        await using var input = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = await new SessionLogReader().ReadAsync(input); Equal(SessionLogReadStatus.Complete, read.Status);
        Equal((long)read.OriginalBytes.Length, session.Snapshot.Log.CommittedByteLength);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory; public int Clocks, Ids;
        public ExtensionRegistry Registry { get; } = new(); public ExtensionAgentBinding Binding { get; private set; } = null!;
        public RegistrationScope Scope { get; private set; } = null!;
        public PersistentAgentSession Session { get; private set; } = null!; public RpcSessionDispatcher Dispatcher { get; private set; } = null!;
        public Script Transport { get; private set; } = null!; public Capture Output { get; private set; } = null!;
        private StorageFactory? storage;
        private Fixture() { directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-registered-rpc-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); }
        public long Clock() { Interlocked.Increment(ref Clocks); return 123; }
        public string NextId() => "entry-" + Interlocked.Increment(ref Ids);
        public static async Task<Fixture> Create(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize,
            Script? transport = null, Capture? output = null, StorageFactory? storage = null, RpcDispatchOptions? options = null,
            Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? diagnostics = null)
        {
            var value = new Fixture(); value.Transport = transport ?? new Script(); value.Output = output ?? new Capture(); value.storage = storage;
            value.Scope = await value.Registry.ActivateAsync("registered", new Extension(initialize));
            value.Binding = new(value.Registry, new Policy(), static (_, _, _) => ValueTask.FromResult(false));
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "registered-rpc-header",
                timestamp = "2026-10-01T00:00:00.000Z", cwd = value.directory }));
            value.Session = await PersistentAgentSession.CreateAsync(System.IO.Path.Combine(value.directory, "session.jsonl"), header,
                new(Model, value.Transport, value.Binding.Tools), value.Clock, value.NextId,
                new(SessionLogStoreOptions: storage is null ? null : new(StorageFactory: storage)));
            value.Dispatcher = new(value.Session, new JsonlWriter(value.Output, ownership: JsonlStreamOwnership.Owned), value.Clock,
                [new(Model, ModelWire)], options, inputAdmission: new RegisteredExtensionInputAdmission(value.Registry, value.Binding.Snapshot, reportDiagnostic: diagnostics));
            return value;
        }
        public Task Send(string type, string id, string? message = null, JsonData? images = null, string? behavior = null, string? mode = null)
        {
            var fields = new Dictionary<string, object?> { ["type"] = type, ["id"] = id };
            if (message is not null) fields["message"] = message; if (images is not null) fields["images"] = images.Value;
            if (behavior is not null) fields["streamingBehavior"] = behavior; if (mode is not null) fields["mode"] = mode;
            return Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(fields)));
        }
        public JsonData Response(string id) => Output.Records().Single(value => IsResponse(value, id));
        public string Disposition(string id) => Response(id).Value.GetProperty("data").GetProperty("disposition").GetString()!;
        public async ValueTask DisposeAsync()
        {
            Transport.ReleaseAll(); storage?.Storage?.ReleaseAll(); Output.DisposeRelease.TrySetResult();
            try { await Dispatcher.DisposeAsync().AsTask().WaitAsync(Deadline); } catch (RpcDispatchException) { }
            await Session.DisposeAsync(); await Registry.DisposeAsync();
            var target = System.IO.Path.GetFullPath(directory); var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(target) != parent || !System.IO.Path.GetFileName(target).StartsWith("PiSharp-registered-rpc-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned RPC test directory.");
            Directory.Delete(target, recursive: true);
        }
    }
    private sealed class Extension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed record Step(bool Pause = false, bool HoldCleanup = false)
    { public readonly TaskCompletionSource Entered = Gate(), Release = Gate(), CleanupEntered = Gate(), CleanupRelease = Gate(); public int Cleanups; }
    private sealed class Script(params Step[] steps) : IChatTransport
    {
        private readonly object gate = new(); private readonly List<ChatRequest> requests = []; private readonly List<Step> admitted = [];
        public ChatRequest[] Requests() { lock (gate) return requests.ToArray(); }
        public void ReleaseAll() { lock (gate) foreach (var step in steps.Concat(admitted)) { step.Release.TrySetResult(); step.CleanupRelease.TrySetResult(); } }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Step step; int index; lock (gate) { index = requests.Count; step = index < steps.Length ? steps[index] : new(); requests.Add(request); admitted.Add(step); }
            try
            {
                step.Entered.TrySetResult(); if (step.Pause) await step.Release.Task.WaitAsync(token);
                var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [new TextContent("answer-" + index)], TokenUsage.Zero, StopReason.Stop);
                yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
                yield return new TextStarted(0, new("")); yield return new TextDelta(0, "answer-" + index); yield return new TextEnded(0, "answer-" + index);
                yield return new StreamDone(StopReason.Stop, message);
            }
            finally { step.CleanupEntered.TrySetResult(); if (step.HoldCleanup) await step.CleanupRelease.Task.WaitAsync(Deadline); Interlocked.Increment(ref step.Cleanups); }
        }
    }
    private sealed class Capture : Stream
    {
        private readonly object gate = new(); private readonly List<JsonData> records = []; private JsonData? written;
        public bool HoldDispose; public string? FailOnType; public int Disposals;
        public Func<JsonData, CancellationToken, ValueTask>? BeforeFlush;
        public readonly TaskCompletionSource DisposeEntered = Gate(), DisposeRelease = Gate();
        public JsonData[] Records() { lock (gate) return records.ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); var value = JsonData.Parse(Encoding.UTF8.GetString(bytes.Span)); if (Type(value) == FailOnType) throw new IOException("SECRET output failure"); written = value; return ValueTask.CompletedTask; }
        public override async Task FlushAsync(CancellationToken token)
        { var value = written!; written = null; if (BeforeFlush is { } before) await before(value, token); lock (gate) records.Add(value); }
        public override async ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); DisposeEntered.TrySetResult(); if (HoldDispose) await DisposeRelease.Task.WaitAsync(Deadline); }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class Barrier
    { public readonly TaskCompletionSource Entered = Gate(), Release = Gate(); public async ValueTask WaitAsync() { Entered.TrySetResult(); await Release.Task.WaitAsync(Deadline); } }
    private sealed class StorageFactory : ISessionLogStorageFactory
    { public Storage? Storage; public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token)); }
    private sealed class Storage(ISessionLogStorage inner) : ISessionLogStorage
    {
        private Barrier? barrier; private readonly List<Barrier> barriers = []; public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public Barrier Arm() { var value = new Barrier(); barriers.Add(value); barrier = value; return value; }
        public void ReleaseAll() { foreach (var value in barriers) value.Release.TrySetResult(); }
        public void PositionForAppend(long value) => inner.PositionForAppend(value); public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes); public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
        public async ValueTask BeforeCheckpointAsync() { var value = barrier; barrier = null; await inner.BeforeCheckpointAsync(); if (value is not null) await value.WaitAsync(); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class EofInput(byte[] frame) : Stream
    {
        private int offset; public readonly TaskCompletionSource Eof = Gate();
        public override async ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken token = default)
        { if (offset < frame.Length) { var count = Math.Min(bytes.Length, frame.Length - offset); frame.AsMemory(offset, count).CopyTo(bytes); offset += count; return count; } await Eof.Task.WaitAsync(token); return 0; }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
