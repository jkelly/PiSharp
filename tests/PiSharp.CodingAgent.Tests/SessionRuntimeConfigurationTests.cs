using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using NativeAgent = PiSharp.Agent.Agent;

internal static class SessionRuntimeConfigurationTests
{
    private static readonly ModelDescriptor ModelA = new("model-a", "openai-responses", "fixture-provider");
    private static readonly ModelDescriptor ModelB = new("model-b", "openai-responses", "fixture-provider");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("Runtime binding replays removal/replacement/readdition in source order with strict raw declarations", OrderedBinding),
        ("Agent atomic idle config/history rejects invalid publication and busy mutation", AtomicAgentState),
        ("Durable runtime switch holds admission then supplies model/tools/history to next generation and reopen", DurableSwitch),
        ("Runtime bound tool final-target denial precedes effects after configuration restore", MandatoryPolicy),
        ("Runtime configuration uncertain append retains old live state and blocks continuation", UncertainUpdate),
        ("Runtime configuration validates model/declaration/thinking/cancellation before file mutation", Admission),
        ("Runtime configuration disposal waits accepted checkpoint and shares settlement", DisposalBarrier),
        ("Native thinking durable selection reaches tool continuations and survives reopen", ThinkingDurable),
        ("Native thinking seeds an admitted default and owns hook/cancellation request controls", ThinkingDefaultAndAbort),
        ("Native thinking without off survives restore and activation with rejected-off byte stability", ThinkingWithoutOffRestore)
    ];

    private static Task OrderedBinding()
    {
        var transport = new Script();
        var read = new Adapter("read"); var write = new Adapter("write"); var edit = new Adapter("edit");
        var registry = Registry(transport, new Script(), [read, write, edit]);
        var initial = SystemDelta([Decl("read"), Decl("write")]);
        var changed = SystemDelta([Decl("edit")], ["read"]);
        var readded = SystemDelta([Decl("read")]);
        var replacement = SystemDelta([Decl("write")]);
        Names(["read", "write"], registry.Resolve(ModelA, [initial]).Configuration.Tools);
        Names(["write", "edit"], registry.Resolve(ModelA, [initial, changed]).Configuration.Tools);
        Names(["write", "edit", "read"], registry.Resolve(ModelA, [initial, changed, readded, replacement]).Configuration.Tools);
        var oldWrite = JsonData.Parse("""{"name":"write","description":"older interface","parameters":{"type":"object"}}""");
        Names(["write", "edit", "read"], registry.Resolve(ModelA,
            [SystemDelta([Decl("read"), oldWrite]), changed, readded, replacement]).Configuration.Tools);
        Names(["edit", "read", "write"], registry.Resolve(ModelA,
            [initial, changed, readded, SystemDelta([Decl("write")], ["write"])]).Configuration.Tools);
        var reordered = JsonData.Parse("""{"parameters":{"future":null,"n":1.0,"type":"object"},"description":"read tool","name":"read"}""");
        Names(["read"], registry.Resolve(ModelA, [SystemDelta([reordered])]).Configuration.Tools);
        var numericMutation = JsonData.Parse("""{"name":"read","description":"read tool","parameters":{"type":"object","n":1,"future":null}}""");
        var mismatch = Throws<SessionRuntimeRegistryException>(() => registry.Resolve(ModelA, [SystemDelta([numericMutation])]));
        Equal(SessionRuntimeRegistryFailure.DeclarationMismatch, mismatch.Failure);
        var omitted = JsonData.Parse("""{"name":"read","description":"read tool","parameters":{"type":"object","n":1.0}}""");
        Equal(SessionRuntimeRegistryFailure.DeclarationMismatch,
            Throws<SessionRuntimeRegistryException>(() => registry.Resolve(ModelA, [SystemDelta([omitted])])).Failure);
        Equal(SessionRuntimeRegistryFailure.UnknownTool,
            Throws<SessionRuntimeRegistryException>(() => registry.Resolve(ModelA, [SystemDelta([Decl("unknown")])])).Failure);
        Equal(SessionRuntimeRegistryFailure.UnknownModel,
            Throws<SessionRuntimeRegistryException>(() => registry.Resolve(ModelA with { Api = "other" }, [])).Failure);
        Throws<ArgumentNullException>(() => new SessionRuntimeRegistry([new(ModelA, transport)], [], null!));
        var narrow = new SessionRuntimeRegistry([new(ModelA, transport)], [new(Decl("read"), read)], new Policy(),
            new(MaximumDeclarations: 1));
        Equal(SessionRuntimeRegistryFailure.ResourceLimit,
            Throws<SessionRuntimeRegistryException>(() => narrow.Resolve(ModelA, [initial])).Failure);
        var depth = new SessionRuntimeRegistry([new(ModelA, transport)], [], new Policy(), new(MaximumJsonDepth: 2));
        Equal(SessionRuntimeRegistryFailure.ResourceLimit,
            Throws<SessionRuntimeRegistryException>(() => depth.Resolve(ModelA, [User("x")])).Failure);
        var badUnicode = new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"\ud800","timestamp":123}"""));
        Equal(SessionRuntimeRegistryFailure.InvalidTranscript,
            Throws<SessionRuntimeRegistryException>(() => registry.Resolve(ModelA, [badUnicode])).Failure);
        var custom = JsonData.Parse("""{"type":"custom","name":"read","description":"read tool","parameters":{}}""");
        Equal(SessionRuntimeRegistryFailure.UnsupportedDeclaration,
            Throws<SessionRuntimeRegistryException>(() => registry.Resolve(ModelA, [SystemDelta([custom])])).Failure);
        return Task.CompletedTask;
    }

    private static async Task AtomicAgentState()
    {
        var transport = new Script(block: true);
        var sink = new Sink();
        await using var agent = new NativeAgent(new(ModelA, transport, []), () => 123, sink);
        var old = User("old");
        agent.ReplaceMessages([old]);
        var wrong = new TranscriptEntry("user", JsonData.Parse("""{"role":"system","content":"invalid","timestamp":123}"""));
        Throws<ArgumentException>(() => agent.ConfigureAndReplaceMessages(new(ModelB, transport, []), [wrong]));
        Equal(ModelA, agent.Snapshot.Model);
        Equal(old.WireBody.ToString(), agent.Snapshot.Messages.Single().WireBody.ToString());
        agent.ConfigureAndReplaceMessages(new(ModelB, transport, []), [User("replacement")]);
        Equal(ModelB, agent.Snapshot.Model);
        var run = agent.PromptAsync(User("run"));
        try
        {
            await Within(transport.Entered.Task);
            Throws<InvalidOperationException>(() => agent.ConfigureAndReplaceMessages(new(ModelA, transport, []), [old]));
            Equal(ModelB, agent.Snapshot.Model);
            transport.Release.TrySetResult();
            await Within(run);
        }
        finally { transport.Release.TrySetResult(); await Ignore(run); }
    }

    private static async Task DurableSwitch()
    {
        using var files = new Files();
        var transportA = new Script("read"); var transportB = new Script("write");
        var read = new Adapter("read"); var write = new Adapter("write");
        var registry = Registry(transportA, transportB, [read, write]);
        var storage = new StorageFactory(); var ids = new Ids();
        var options = new PersistentAgentSessionOptions(SessionLogStoreOptions: new(StorageFactory: storage));
        string oldLeaf;
        await using (var session = await Create(files, registry, ids, options))
        {
            await session.ConfigureAsync(new(SystemMessage: SystemDelta([Decl("read")], content: "Base")));
            var endEntered = Gate(); var endRelease = Gate();
            using (var listener = session.Subscribe(new Sink(async (observation, _) =>
            {
                if (observation is AgentLoopEnded) { endEntered.TrySetResult(); await endRelease.Task; }
            })))
            {
                var first = session.PromptAsync(User("first"));
                try
                {
                    await Within(endEntered.Task);
                    Throws<InvalidOperationException>(() => session.ConfigureAsync(new(ModelB)));
                    endRelease.TrySetResult(); await Within(first);
                }
                finally { endRelease.TrySetResult(); await Ignore(first); }
            }
            Equal(1, read.Effects); Equal(0, write.Effects);
            oldLeaf = session.Snapshot.Context.LeafId!;
            var before = session.Snapshot;
            var gate = storage.Storage!.Arm();
            var update = session.ConfigureAsync(new(ModelB, "off", SystemDelta([Decl("write")], ["read"], "Later")));
            try
            {
                await Within(gate.Entered.Task);
                Equal(ModelA, session.Snapshot.Agent.Model);
                Check(session.Snapshot.IsConfiguring && !session.Snapshot.Agent.IsRunning, "Configuration reservation was not exposed separately.");
                Names(["read"], session.Snapshot.Agent.Tools);
                Equal(before.Log.CommittedByteLength, session.Snapshot.Log.CommittedByteLength);
                Equal(before.Context.LeafId, session.Snapshot.Context.LeafId);
                Throws<InvalidOperationException>(() => session.PromptAsync(User("racing prompt")));
                Throws<InvalidOperationException>(() => session.ConfigureAsync(new(ModelA)));
                Check(!session.WaitForIdleAsync().IsCompleted, "Configuration lease was omitted from idle.");
                Equal(0, transportB.Requests.Count);
                gate.Release.TrySetResult();
                var acknowledged = await Within(update);
                Equal(ModelB, acknowledged.Agent.Model);
                Check(!acknowledged.IsConfiguring && !session.Snapshot.IsConfiguring, "Settled update retained its busy reservation.");
                Names(["write"], acknowledged.Agent.Tools);
                Check(acknowledged.Log.CommittedByteLength > before.Log.CommittedByteLength, "Update did not durably append.");
            }
            finally { gate.Release.TrySetResult(); await Ignore(update); }
            var canonical = session.Snapshot.Context.LlmMessages;
            await session.PromptAsync(User("second"));
            Equal(1, write.Effects); Equal(1, read.Effects);
            Equal(ModelB, transportB.Requests[0].Model);
            Prefix(canonical, transportB.Requests[0].Messages);
            Check(transportB.Requests[0].Messages.Any(message => message.WireBody.ToString().Contains("Base", StringComparison.Ordinal)) &&
                transportB.Requests[0].Messages.Any(message => message.WireBody.ToString().Contains("Later", StringComparison.Ordinal)),
                "System history was flattened or dropped.");
        }
        await using (var selected = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, registry, () => 123, ids.Next,
            new(UseLatestLeaf: false, SelectedLeafId: oldLeaf)))
        {
            Equal(ModelA, selected.Snapshot.Agent.Model);
            Names(["read"], selected.Snapshot.Agent.Tools);
            Check(selected.Snapshot.Context.LlmMessages.All(message => !message.WireBody.ToString().Contains("Later", StringComparison.Ordinal)), "Sibling configuration leaked into old branch.");
            Equal(1, read.Effects); Equal(1, write.Effects);
        }
        await using var latest = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, registry, () => 123, ids.Next);
        Equal(ModelB, latest.Snapshot.Agent.Model);
        Names(["write"], latest.Snapshot.Agent.Tools);
        var history = latest.Snapshot.Context.LlmMessages;
        await latest.PromptAsync(User("resumed"));
        Prefix(history, transportB.Requests[^1].Messages);
        Equal(1, write.Effects); Equal(1, read.Effects);
    }

    private static async Task MandatoryPolicy()
    {
        using var files = new Files();
        var transport = new Script("write");
        var adapter = new Adapter("write", target: "/outside-authorized-root");
        var policy = new Policy(deny: true);
        var registry = new SessionRuntimeRegistry([new(ModelA, transport)], [new(Decl("write"), adapter)], policy);
        var ids = new Ids();
        await using (var created = await Create(files, registry, ids))
            await created.ConfigureAsync(new(SystemMessage: SystemDelta([Decl("write")])));
        await using var reopened = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, registry, () => 123, ids.Next);
        var result = await reopened.PromptAsync(User("denied final action"));
        Equal(AgentLoopStopReason.Completed, result.Reason);
        Equal(1, policy.Calls); Equal("/outside-authorized-root", policy.Last!.Target);
        Equal(0, adapter.Effects);
        var tool = reopened.Snapshot.Context.LlmMessages.Single(message => message.Role == "toolResult");
        Check(tool.WireBody.Value.GetProperty("isError").GetBoolean(), "Policy denial did not become a finalized error result.");
        Equal(2, transport.Requests.Count);
    }

    private static async Task UncertainUpdate()
    {
        using var files = new Files();
        var a = new Script(); var b = new Script();
        var registry = Registry(a, b, [new Adapter("read"), new Adapter("write")]);
        var storage = new StorageFactory(); var ids = new Ids();
        var session = await Create(files, registry, ids, new(SessionLogStoreOptions: new(StorageFactory: storage)));
        var before = session.Snapshot;
        var barrier = storage.Storage!.Arm(fail: true);
        var update = session.ConfigureAsync(new(ModelB, SystemMessage: SystemDelta([Decl("write")])));
        try
        {
            await Within(barrier.Entered.Task);
            barrier.Release.TrySetResult();
            var error = await ThrowsAsync<PersistentAgentSessionException>(() => update);
            Check(error.Fault.MayHaveWritten && error.Fault.DurableFlushCompleted, "Update uncertainty was hidden.");
            Equal(SessionLogStoreFailure.CheckpointFailed, error.Fault.StorageFailure);
            Equal(ModelA, session.Snapshot.Agent.Model);
            Check(session.Snapshot.Agent.Tools.IsEmpty, "Unacknowledged tool loadout became live.");
            Equal(before.Log.CommittedByteLength, session.Snapshot.Log.CommittedByteLength);
            Equal(before.Context.LeafId, session.Snapshot.Context.LeafId);
            Throws<PersistentAgentSessionException>(() => session.PromptAsync(User("retry")));
            Throws<PersistentAgentSessionException>(() => session.ConfigureAsync(new(ModelA)));
            Equal(0, a.Requests.Count); Equal(0, b.Requests.Count);
        }
        finally { barrier.Release.TrySetResult(); await Ignore(update); await session.DisposeAsync(); }
        await using var inspected = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, registry, () => 123, ids.Next);
        Equal(ModelB, inspected.Snapshot.Agent.Model);
        Names(["write"], inspected.Snapshot.Agent.Tools);
        Equal(0, b.Requests.Count);
    }

    private static async Task Admission()
    {
        using var files = new Files();
        var a = new Script(); var b = new Script();
        var registry = Registry(a, b, [new Adapter("read")]);
        var ids = new Ids();
        await using var session = await Create(files, registry, ids);
        var before = await InspectBytes(files.Path);
        Equal(SessionRuntimeRegistryFailure.UnknownModel,
            (await ThrowsAsync<SessionRuntimeRegistryException>(() => session.ConfigureAsync(new(ModelA with { Id = "unknown" })))).Failure);
        Equal(SessionRuntimeRegistryFailure.UnsupportedThinkingLevel,
            (await ThrowsAsync<SessionRuntimeRegistryException>(() => session.ConfigureAsync(new(ThinkingLevel: "high")))).Failure);
        Equal(SessionRuntimeRegistryFailure.UnknownTool,
            (await ThrowsAsync<SessionRuntimeRegistryException>(() => session.ConfigureAsync(new(SystemMessage: SystemDelta([Decl("unknown")]))))).Failure);
        var stale = JsonData.Parse("""{"name":"read","description":"stale","parameters":{"type":"object","n":1.0,"future":null}}""");
        Equal(SessionRuntimeRegistryFailure.DeclarationMismatch,
            (await ThrowsAsync<SessionRuntimeRegistryException>(() => session.ConfigureAsync(new(SystemMessage: SystemDelta([stale]))))).Failure);
        await ThrowsAsync<PersistentAgentSessionException>(() => session.ConfigureAsync(new(SystemMessage: User("wrong role"))));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Throws<OperationCanceledException>(() => session.ConfigureAsync(new(ModelB), canceled.Token));
        var after = await InspectBytes(files.Path);
        Check(before.SequenceEqual(after), "Rejected update changed durable bytes.");
        Check(session.Snapshot.Fault is null, "Pre-write admission rejection poisoned a usable session.");
        await ThrowsAsync<PersistentAgentSessionException>(() => session.PromptAsync(SystemDelta([Decl("read")])));
        Throws<PersistentAgentSessionException>(() => session.Steer(SystemDelta([Decl("read")])));
        Throws<PersistentAgentSessionException>(() => session.FollowUp(SystemDelta([Decl("read")])));
        Equal(0, a.Requests.Count); Equal(0, session.Snapshot.Agent.Messages.Length);
        await session.ConfigureAsync(new(SystemMessage: SystemDelta([Decl("read")])));
        await session.PromptAsync(User("valid after rejected update"));
        Equal(1, a.Requests.Count);
        Check(session.Snapshot.Fault is null, "Valid update/run did not survive rejected admission.");
        // Trusted configuration callbacks may not self-wait or close the lease they are preparing.
        using var callbackFiles = new Files();
        PersistentAgentSession? callbackSession = null; var checks = 0;
        long Clock()
        {
            if (callbackSession is not null)
            {
                Throws<InvalidOperationException>(() => callbackSession.WaitForIdleAsync());
                Throws<InvalidOperationException>(() => callbackSession.DisposeAsync());
                checks++;
            }
            return 123;
        }
        callbackSession = await PersistentAgentSession.CreateAsync(callbackFiles.Path, Header(callbackFiles),
            registry, ModelA, Clock, new Ids().Next);
        await using (callbackSession)
            await callbackSession.ConfigureAsync(new(ModelB));
        Equal(1, checks);
    }

    private static async Task DisposalBarrier()
    {
        using var files = new Files();
        var registry = Registry(new Script(), new Script(), []);
        var storage = new StorageFactory();
        var session = await Create(files, registry, new(), new(SessionLogStoreOptions: new(StorageFactory: storage)));
        var barrier = storage.Storage!.Arm();
        var update = session.ConfigureAsync(new(ModelB));
        try
        {
            await Within(barrier.Entered.Task);
            var first = session.DisposeAsync().AsTask(); var second = session.DisposeAsync().AsTask();
            Check(!first.IsCompleted && !second.IsCompleted && !update.IsCompleted, "Dispose abandoned admitted metadata append.");
            Equal(ModelA, session.Snapshot.Agent.Model);
            barrier.Release.TrySetResult();
            await Within(update); await Within(Task.WhenAll(first, second));
            Equal(ModelB, session.Snapshot.Agent.Model);
            Equal(ModelB.Id, session.Snapshot.Context.Model!.ModelId);
            Equal(1, storage.Storage.Disposals);
            Equal(new FileInfo(files.Path).Length, session.Snapshot.Log.CommittedByteLength);
        }
        finally { barrier.Release.TrySetResult(); await Ignore(update); await session.DisposeAsync(); }
    }

    private static async Task ThinkingDurable()
    {
        using var files = new Files(); var ids = new Ids();
        var transport = new Script("read", levels: ["off", "low", "high"]);
        var registry = Registry(transport, new Script(), [new Adapter("read")]);
        await using (var session = await Create(files, registry, ids))
        {
            Check(session.GetSupportedThinkingLevels().SequenceEqual(["off", "low", "high"]), "Native capabilities were lost.");
            await session.ConfigureAsync(new(ThinkingLevel: "high", SystemMessage: SystemDelta([Decl("read")])));
            await session.PromptAsync(User("thinking with a tool"));
            Equal(2, transport.Requests.Count);
            Check(transport.Requests.All(request => request.ThinkingLevel == "high"), "Tool continuation reset thinking.");
            Check(session.Snapshot.Agent.Messages.Where(entry => entry.Role == "assistant").All(entry =>
                entry.WireBody.Value.GetProperty("thinkingLevel").GetString() == "high"), "Assistant stamp lost requested thinking.");
            var bytes = await InspectBytes(files.Path); var leaf = session.Snapshot.Context.LeafId;
            var error = await ThrowsAsync<SessionRuntimeRegistryException>(() => session.ConfigureAsync(new(ThinkingLevel: "max")));
            Equal(SessionRuntimeRegistryFailure.UnsupportedThinkingLevel, error.Failure);
            var afterRejectedThinking = await InspectBytes(files.Path);
            Check(bytes.SequenceEqual(afterRejectedThinking), "Rejected thinking changed durable bytes.");
            Equal(leaf, session.Snapshot.Context.LeafId); Equal("high", session.Snapshot.Context.ThinkingLevel);
        }
        await using var restored = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, registry, () => 123, ids.Next);
        Equal("high", restored.Snapshot.Context.ThinkingLevel);
        await restored.PromptAsync(User("restored")); Equal("high", transport.Requests.Last().ThinkingLevel);
        await restored.ConfigureAsync(new(ThinkingLevel: "low"));
        restored.ScheduleToolActivation([]);
        await restored.PromptAsync(User("changed")); Equal("low", transport.Requests.Last().ThinkingLevel);
        Equal("low", restored.Snapshot.Context.ThinkingLevel);
        Check(restored.Snapshot.Agent.Tools.IsEmpty, "Scheduled activation failed to reach the next request boundary.");
    }

    private static async Task ThinkingWithoutOffRestore()
    {
        using var files = new Files(); var ids = new Ids();
        var transport = new Script("read", levels: ["low", "high"]);
        var registry = Registry(transport, new Script(), [new Adapter("read")]);
        await using (var session = await Create(files, registry, ids))
        {
            Equal("low", session.Snapshot.Context.ThinkingLevel);
            Check(session.GetSupportedThinkingLevels().SequenceEqual(["low", "high"]), "Default capability query inserted off.");
            await session.ConfigureAsync(new(ThinkingLevel: "high", SystemMessage: SystemDelta([Decl("read")])));
            await session.PromptAsync(User("no-off tool continuation"));
            Equal(2, transport.Requests.Count);
            Check(transport.Requests.All(request => request.ThinkingLevel == "high"), "No-off tool continuation reset thinking.");
            await RejectOff(session);
        }
        await using var restored = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, registry, () => 123, ids.Next);
        Equal("high", restored.Snapshot.Context.ThinkingLevel);
        Check(restored.GetSupportedThinkingLevels().SequenceEqual(["low", "high"]), "Restore inserted off into capabilities.");
        await restored.PromptAsync(User("no-off restored")); Equal("high", transport.Requests.Last().ThinkingLevel);
        await restored.ConfigureAsync(new(ThinkingLevel: "low"));
        restored.ScheduleToolActivation([]);
        await restored.PromptAsync(User("no-off activation"));
        Equal("low", transport.Requests.Last().ThinkingLevel); Equal("low", restored.Snapshot.Context.ThinkingLevel);
        Check(restored.Snapshot.Agent.Tools.IsEmpty && restored.Snapshot.Context.Messages.Last(entry => entry.Role == "assistant")
            .WireBody.Value.GetProperty("thinkingLevel").GetString() == "low", "Activation lost admitted thinking or tool removal.");
        await RejectOff(restored);

        async Task RejectOff(PersistentAgentSession selected)
        {
            var before = selected.Snapshot; var requests = transport.Requests.Count;
            var bytes = await ReadIdleAcknowledgedBytes(selected);
            try
            {
                await selected.ConfigureAsync(new(ThinkingLevel: "off"));
                throw new InvalidOperationException("Explicit unsupported off was admitted.");
            }
            catch (SessionRuntimeRegistryException error) when (error.Failure == SessionRuntimeRegistryFailure.UnsupportedThinkingLevel) { }
            var afterBytes = await ReadIdleAcknowledgedBytes(selected); var after = selected.Snapshot;
            Check(bytes.SequenceEqual(afterBytes) && after.Log.Sequence == before.Log.Sequence && after.Log.LeafId == before.Log.LeafId &&
                after.Context.ThinkingLevel == before.Context.ThinkingLevel && after.Agent.Model == before.Agent.Model &&
                after.Fault is null && transport.Requests.Count == requests, "Rejected off changed durable or live selection, faulted, or sent.");
        }
    }
    private static async Task<byte[]> ReadIdleAcknowledgedBytes(PersistentAgentSession session)
    {
        var before = session.Snapshot;
        bool Idle(PersistentAgentSessionSnapshot snapshot) => !snapshot.IsProcessingOperation && !snapshot.Agent.IsRunning &&
            !snapshot.IsConfiguring && !snapshot.IsEditingContext && !snapshot.IsCompacting && snapshot.Fault is null;
        Check(Idle(before) && before.Log.StorageDurability == SessionLogStorageDurability.LocalFileFlush &&
            before.Log.CommittedByteLength is >= 0 and <= 8_388_608, "Read requires a bounded idle durable checkpoint.");
        await using var reader = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(reader.Length == before.Log.CommittedByteLength, "Physical and acknowledged lengths differ.");
        var bytes = new byte[checked((int)before.Log.CommittedByteLength)]; await reader.ReadExactlyAsync(bytes);
        var after = session.Snapshot;
        Check(Idle(after) && reader.Position == bytes.Length && reader.Length == bytes.Length &&
            after.Log.Sequence == before.Log.Sequence && after.Log.LeafId == before.Log.LeafId &&
            after.Log.CommittedByteLength == before.Log.CommittedByteLength && after.OperationGeneration == before.OperationGeneration &&
            after.OperationPhase == before.OperationPhase && after.Agent.Generation == before.Agent.Generation,
            "Checkpoint changed during the owned shared read.");
        return bytes;
    }
    private static async Task ThinkingDefaultAndAbort()
    {
        var hookTransport = new Script(levels: ["low", "high"]);
        await using (var agent = new NativeAgent(new(ModelA, hookTransport, [], Hooks: new(PrepareRequest: (snapshot, _) =>
            ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript) { ThinkingLevel = "off" })))
            { ThinkingLevel = "high" }, () => 123, new Sink()))
        {
            await agent.PromptAsync(User("hook"));
            Equal("high", hookTransport.Requests.Single().ThinkingLevel);
            Equal("high", agent.Snapshot.Messages.Last().WireBody.Value.GetProperty("thinkingLevel").GetString());
        }
        using var files = new Files(); var ids = new Ids();
        var transport = new Script(block: true, levels: ["low", "high"]);
        var registry = Registry(transport, new Script(), []);
        await using var session = await Create(files, registry, ids);
        Equal("low", session.Snapshot.Context.ThinkingLevel);
        using var cancellation = new CancellationTokenSource();
        var run = session.PromptAsync(User("cancel"), cancellation.Token);
        try
        {
            await Within(Task.WhenAny(transport.Entered.Task, run));
            if (!transport.Entered.Task.IsCompleted)
            {
                await run; // Surface pre-transport admission failure instead of waiting for an unreachable signal.
                throw new InvalidOperationException("Prompt completed without entering the transport.");
            }
            await transport.Entered.Task;
            cancellation.Cancel(); await run;
            Equal("low", transport.Requests.Single().ThinkingLevel);
            Equal("low", session.Snapshot.Agent.Messages.Last().WireBody.Value.GetProperty("thinkingLevel").GetString());
            Equal("low", session.Snapshot.Context.ThinkingLevel);
        }
        finally { cancellation.Cancel(); transport.Release.TrySetResult(); await run; }
    }

    private static SessionRuntimeRegistry Registry(Script a, Script b, ImmutableArray<Adapter> adapters) =>
        new([new(ModelA, a), new(ModelB, b)], adapters.Select(adapter => new SessionRegisteredTool(Decl(adapter.Name), adapter)).ToImmutableArray(), new Policy());
    private static Task<PersistentAgentSession> Create(Files files, SessionRuntimeRegistry registry, Ids ids, PersistentAgentSessionOptions? options = null) =>
        PersistentAgentSession.CreateAsync(files.Path, Header(files), registry, ModelA, () => 123, ids.Next, options);
    private static SessionEntry Header(Files files) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    { type = "session", version = 3, id = "header", timestamp = "2026-10-01T00:00:00.000Z", cwd = files.Directory }));
    private static JsonData Decl(string name) => JsonData.Parse("""{"name":""" + JsonSerializer.Serialize(name) +
        ""","description":""" + JsonSerializer.Serialize(name + " tool") + ""","parameters":{"type":"object","n":1.0,"future":null}}""");
    private static TranscriptEntry SystemDelta(ImmutableArray<JsonData> added, ImmutableArray<string> removed = default, string content = "") =>
        new("system", JsonData.Parse(JsonSerializer.Serialize(new { role = "system", content, timestamp = 123,
            toolsAdded = added.Select(value => value.Value), toolsRemoved = removed.IsDefault ? [] : removed.Select(name => new { name }).ToArray() })));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(
        """{"role":"user","content":""" + JsonSerializer.Serialize(text) + ""","timestamp":123,"future":{"deep":{"null":null}}}"""));
    private sealed class Ids { private int _next; public string Next() => "configuration-" + Interlocked.Increment(ref _next); }
    private sealed class Files : IDisposable
    {
        private readonly string _parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        public string Directory { get; }
        public string Path => System.IO.Path.Combine(Directory, "session.jsonl");
        public Files()
        { Directory = System.IO.Path.Combine(_parent, "PiSharp-runtime-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(Directory); }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Directory);
            if (System.IO.Path.GetDirectoryName(resolved) != _parent.TrimEnd(System.IO.Path.DirectorySeparatorChar) ||
                !System.IO.Path.GetFileName(resolved).StartsWith("PiSharp-runtime-", StringComparison.Ordinal)) throw new InvalidOperationException("Unowned test path.");
            System.IO.Directory.Delete(resolved, recursive: true);
        }
    }
    private sealed class Script(string? firstTool = null, bool block = false, ImmutableArray<string> levels = default) : IChatTransport, IThinkingLevelTransport
    {
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) => levels.IsDefault ? ["off"] : levels;
        public readonly List<ChatRequest> Requests = [];
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var tool = Requests.Count == 0 ? firstTool : null;
            Requests.Add(request);
            var final = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 123,
                tool is null ? [new TextContent("done")] : [new ToolCallContent("call-" + tool, tool, JsonData.EmptyObject)],
                TokenUsage.Zero, tool is null ? StopReason.Stop : StopReason.ToolUse);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            Entered.TrySetResult();
            if (block) await Release.Task.WaitAsync(token);
            if (tool is null)
            { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            else
            {
                var call = (ToolCallContent)final.Content[0];
                yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call);
            }
            yield return new StreamDone(final.StopReason, final);
        }
    }
    private sealed class Adapter(string name, string target = "/trusted/final") : IPreparedToolAdapter
    {
        public string Name => name;
        public int Effects;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) =>
            ValueTask.FromResult(new PreparedToolAction(name, "fake", PreparedToolActionKind.Path, target, invocation.Call.Arguments,
                [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Effects++; return ValueTask.FromResult(ToolResult.Success("effect")); }
    }
    private sealed class Policy(bool deny = false) : IToolActionPolicy
    {
        public int Calls;
        public PreparedToolAction? Last;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Last = action; return ValueTask.FromResult(new ToolActionAuthorization(!deny)); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask>? callback = null) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) => callback?.Invoke(observation, cancellationToken) ?? ValueTask.CompletedTask; }
    private sealed class Barrier(bool fail)
    {
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async ValueTask WaitAsync()
        { Entered.TrySetResult(); await Release.Task; if (fail) throw new IOException("private metadata storage failure"); }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        public Storage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class Storage(ISessionLogStorage inner) : ISessionLogStorage
    {
        private readonly object _gate = new();
        private Barrier? _barrier;
        public int Disposals;
        public Stream ReadStream => inner.ReadStream;
        public SessionLogStorageDurability Durability => inner.Durability;
        public long Length => inner.Length;
        public void PositionForAppend(long value) => inner.PositionForAppend(value);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> value) => inner.WriteAsync(value);
        public ValueTask FlushAsync() => inner.FlushAsync();
        public void FlushToDisk() => inner.FlushToDisk();
        public Barrier Arm(bool fail = false) { lock (_gate) return _barrier = new(fail); }
        public async ValueTask BeforeCheckpointAsync()
        {
            Barrier? barrier; lock (_gate) { barrier = _barrier; _barrier = null; }
            await inner.BeforeCheckpointAsync(); if (barrier is not null) await barrier.WaitAsync();
        }
        public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<byte[]> InspectBytes(string path)
    {
        // The durable writer owns ReadWrite access with FileShare.Read. An inspection reader
        // must also share that existing writer's access; File.ReadAllBytesAsync uses Share.Read.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }
    private static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Ignore(Task task) { try { await Within(task); } catch (Exception) { } }
    private static void Names(IEnumerable<string> expected, ImmutableArray<ToolDefinition> actual) => Check(expected.SequenceEqual(actual.Select(tool => tool.Name)), "Tool order differs.");
    private static void Prefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { Check(actual.Length >= expected.Length, "History disappeared."); for (var i = 0; i < expected.Length; i++) Equal(expected[i].WireBody.ToString(), actual[i].WireBody.ToString()); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static T Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await Within(run()); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
