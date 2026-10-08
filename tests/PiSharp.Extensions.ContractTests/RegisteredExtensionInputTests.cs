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
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RegisteredExtensionInputTests
{
    private static readonly ModelDescriptor Model = new("input-model", "openai-responses", "offline-authored");
    private static readonly JsonData Images = JsonData.Parse("[ {\"type\":\"image\",\"data\":\"YWJj\",\"mimeType\":\"image/png\",\"opaque\":{\"number\":1.0,\"nil\":null}} ]");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("registered input transforms reach actual durable checkpoint, provider history and reopen", TransformedDurability),
        ("handled input preserves both real queues, history, clocks and physical storage", HandledNoEffects),
        ("registered input owner order, errors and removal retain admitted callbacks only", OrderingAndRemoval),
        ("registered active inputs use real steering/follow-up and settled barrier queues", ActualQueues),
        ("held input cancellation, self-wait and concurrent disposal join owner and storage cleanup", CleanupAndReporter),
        ("input and final envelope bounds fail before Agent effects and uncertain append blocks provider", AdmissionAndCheckpoint)
    ];

    private static async Task TransformedDurability()
    {
        using var files = new Files(); using var deadline = Deadline(); await using var registry = new ExtensionRegistry();
        var trace = new List<string>(); var diagnostics = new List<ExtensionEventDiagnostic>();
        await registry.ActivateAsync("A", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(new("transform", (input, _, _) =>
            {
                trace.Add("A"); Equal(ExtensionInputSource.Rpc, input.Source); Check(input.StreamingBehavior is null, "Idle source input retained a streaming mode.");
                return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, input.Text + "|A", JsonData.Null));
            }));
            entries.RegisterInputHandler(new("error", (_, _, _) => { trace.Add("error"); throw new IOException("SECRET input failure"); }));
            return ValueTask.CompletedTask;
        }));
        await registry.ActivateAsync("B", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(new("transform", (input, _, _) =>
            { trace.Add("B"); Equal(Images.ToString(), input.Images!.ToString()); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, input.Text + "|B")); }));
            return ValueTask.CompletedTask;
        }));
        var binding = Binding(registry); var admission = Admission(registry, binding, (d, _) => { diagnostics.Add(d); return ValueTask.CompletedTask; });
        var source = new Source(); var storage = new StorageFactory(); var session = await Create(files, binding, source, storage, deadline.Token);
        source.BeforeSend = async (request, token) =>
        {
            await Acknowledged(session, token); Equal("user", request.Messages[^1].Role);
            Equal("start|A|B", Text(request.Messages[^1]));
            Equal("1.0", request.Messages[^1].WireBody.Value.GetProperty("content")[1].GetProperty("opaque").GetProperty("number").GetRawText());
        };
        var initialBytes = session.Snapshot.Log.CommittedByteLength; var barrier = storage.Storage!.Arm();
        var run = session.SubmitInputAsync(new("start", PromptInputSource.Rpc, Images, PromptInputStreamingBehavior.Steer), admission, cancellationToken: deadline.Token);
        ImmutableArray<TranscriptEntry> retained = []; long acknowledged = 0;
        try
        {
            await Stage(barrier.Entered.Task, run, deadline.Token);
            Equal(0, source.Requests.Count); Equal(initialBytes, session.Snapshot.Log.CommittedByteLength); Equal(0, session.Snapshot.Context.LlmMessages.Length);
            Equal("A,error,B", string.Join(',', trace)); Equal(ExtensionEventFailure.HandlerFailed, diagnostics.Single().Failure);
            False(diagnostics.Single().ToString().Contains("SECRET", StringComparison.Ordinal));
            barrier.Release.TrySetResult(); var submitted = await run.WaitAsync(deadline.Token);
            Equal(SubmittedInputDisposition.Started, submitted.Disposition); Equal(AgentLoopStopReason.Completed, submitted.Run!.Reason);
            Equal(1, source.Requests.Count); Equal(1, source.Cleanups); False(session.Snapshot.IsAdmittingInput);
            retained = session.Snapshot.Context.LlmMessages; acknowledged = session.Snapshot.Log.CommittedByteLength;
            Equal(source.Requests[0].Messages[^1].WireBody.ToString(), retained[0].WireBody.ToString());
            Equal("start|A|B", Text(retained[0])); Equal("[ {\"type\":\"image\",\"data\":\"YWJj\",\"mimeType\":\"image/png\",\"opaque\":{\"number\":1.0,\"nil\":null}} ]", Images.ToString());
            await Acknowledged(session, deadline.Token);
        }
        finally { barrier.Release.TrySetResult(); session.Abort(); await Join(run); await session.DisposeAsync(); }
        var resumed = new Source();
        await using (var opened = await PersistentAgentSession.OpenAsync(files.Path, Configuration(binding, resumed), files.Clock, files.NextId, cancellationToken: deadline.Token))
        {
            Equal(acknowledged, opened.Snapshot.Log.CommittedByteLength); Prefix(retained, opened.Snapshot.Context.LlmMessages);
            await opened.SubmitInputAsync(new("again", PromptInputSource.Rpc, Images), admission, cancellationToken: deadline.Token);
            Prefix(retained, resumed.Requests.Single().Messages); Equal("again|A|B", Text(resumed.Requests.Single().Messages[^1]));
            await Acknowledged(opened, deadline.Token);
        }
        Equal(1, storage.Storage!.Disposals);
    }

    private static async Task HandledNoEffects()
    {
        using var files = new Files(); using var deadline = Deadline(); await using var registry = new ExtensionRegistry();
        var trace = new List<string>();
        await registry.ActivateAsync("owner", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(new("transform", (input, _, _) =>
            { trace.Add("transform"); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, input.Text + " changed")); }));
            entries.RegisterInputHandler(new("handled", (_, _, _) =>
            { trace.Add("handled"); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Handled)); }));
            entries.RegisterInputHandler(new("tail", (_, _, _) => { trace.Add("tail"); return ValueTask.FromResult<ExtensionInputPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        var binding = Binding(registry); var source = new Source(); var storage = new StorageFactory();
        await using var session = await Create(files, binding, source, storage, deadline.Token);
        session.Steer(User("retained steering")); session.FollowUp(User("retained follow-up"));
        var queues = session.GetPendingInputQueueSnapshot(); var before = await Bytes(files.Path); var snapshot = session.Snapshot;
        var clocks = files.Clocks; var ids = files.Ids; var writes = storage.Storage!.Writes;
        var result = await session.SubmitInputAsync(new("handled", PromptInputSource.Extension, Images), Admission(registry, binding), cancellationToken: deadline.Token);
        Equal(SubmittedInputDisposition.Handled, result.Disposition); Check(result.Run is null, "Handled input manufactured a run.");
        Equal("transform,handled", string.Join(',', trace)); Equal(0, source.Requests.Count);
        Equal(clocks, files.Clocks); Equal(ids, files.Ids); Equal(writes, storage.Storage!.Writes);
        Equal(snapshot.Agent.Generation, session.Snapshot.Agent.Generation); Equal(snapshot.Log.CommittedByteLength, session.Snapshot.Log.CommittedByteLength);
        Equal(0, session.Snapshot.Agent.Messages.Length); False(session.Snapshot.IsAdmittingInput);
        Queues(queues, session.GetPendingInputQueueSnapshot()); EqualBytes(before, await Bytes(files.Path));
        session.ClearPendingInputQueues();
        // Historical canonical API remains unchanged; it does not masquerade as source-input admission.
        await session.PromptAsync(User("canonical"), deadline.Token); Equal(1, source.Requests.Count); Equal(2, trace.Count);
        var noHandler = await session.SubmitInputAsync(new("ordinary"), cancellationToken: deadline.Token);
        Equal(SubmittedInputDisposition.Started, noHandler.Disposition); Check(noHandler.Run is not null, "No-handler input skipped the actual Agent.");
        Equal(2, source.Requests.Count); await Acknowledged(session, deadline.Token);
    }

    private static async Task OrderingAndRemoval()
    {
        using var files = new Files(); using var deadline = Deadline(); await using var registry = new ExtensionRegistry();
        RegistrationScope? ownerB = null; IExtensionRegistration? removed = null; var first = true; var trace = new List<string>(); var errors = new List<ExtensionEventDiagnostic>();
        await registry.ActivateAsync("A", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(new("replace", (input, _, _) =>
            {
                trace.Add("A");
                if (first)
                {
                    first = false; removed!.Dispose();
                    ownerB!.RegisterInputHandler(new("added", (_, _, _) =>
                    { trace.Add("new-handled"); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Handled)); }));
                }
                return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, input.Text + "|A"));
            }));
            entries.RegisterInputHandler(new("error", (_, _, _) => { trace.Add("error"); throw new IOException("SECRET callback"); }));
            return ValueTask.CompletedTask;
        }));
        ownerB = await registry.ActivateAsync("B", new Extension((entries, _) =>
        {
            removed = entries.RegisterInputHandler(new("replace", (input, _, _) =>
            { trace.Add("B"); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, input.Text + "|B")); }));
            entries.RegisterInputHandler(new("tail", (_, _, _) => { trace.Add("tail"); return ValueTask.FromResult<ExtensionInputPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        var binding = Binding(registry); var old = Admission(registry, binding, (d, _) => { errors.Add(d); return ValueTask.CompletedTask; });
        var source = new Source(); await using var session = await Create(files, binding, source, null, deadline.Token);
        await session.SubmitInputAsync(new("first"), old, cancellationToken: deadline.Token);
        Equal("A,error,B,tail", string.Join(',', trace)); Equal("first|A|B", Text(source.Requests.Single().Messages[^1]));
        Equal(1, errors.Count); var bytes = await Bytes(files.Path); var generation = session.Snapshot.Agent.Generation;
        var stale = await ThrowsAsync<PromptInputAdmissionException>(() => session.SubmitInputAsync(new("stale"), old, cancellationToken: deadline.Token));
        Equal(PromptInputAdmissionFailure.HandlerFailed, stale.Failure); False(stale.ToString().Contains("SECRET", StringComparison.Ordinal));
        Equal(4, trace.Count); Equal(generation, session.Snapshot.Agent.Generation); Check(session.Snapshot.Fault is null, "Rejected input poisoned acknowledged history.");
        EqualBytes(bytes, await Bytes(files.Path));
        var current = Binding(registry); Check(current.Snapshot.Revision > binding.Snapshot.Revision, "Removal did not publish a new revision.");
        var handled = await session.SubmitInputAsync(new("next"), Admission(registry, current), cancellationToken: deadline.Token);
        Equal(SubmittedInputDisposition.Handled, handled.Disposition); Equal("A,error,B,tail,A,error,tail,new-handled", string.Join(',', trace));
        Equal(1, source.Requests.Count); EqualBytes(bytes, await Bytes(files.Path));
    }

    private static async Task ActualQueues()
    {
        using var files = new Files(); using var deadline = Deadline(); await using var registry = new ExtensionRegistry(); var modes = new List<string?>();
        await registry.ActivateAsync("owner", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(new("transform", (input, _, _) =>
            {
                modes.Add(input.StreamingBehavior);
                return ValueTask.FromResult<ExtensionInputPatch?>(input.Text == "handled" ? new(ExtensionInputAction.Handled) : new(ExtensionInputAction.Transform, input.Text + "|hook"));
            }));
            return ValueTask.CompletedTask;
        }));
        var binding = Binding(registry); var admission = Admission(registry, binding); var source = new Source();
        var provider = source.Arm(); await using var session = await Create(files, binding, source, null, deadline.Token);
        source.BeforeSend = (request, token) => Acknowledged(session, token);
        var ended = Gate(); var endRelease = Gate(); var holdEnd = true;
        using var listener = session.Subscribe(new Sink(async (observation, _) =>
        { if (observation is AgentLoopEnded && holdEnd) { holdEnd = false; ended.TrySetResult(); await endRelease.Task; } }));
        var run = session.PromptAsync(User("running"), deadline.Token);
        try
        {
            await Stage(provider.Entered.Task, run, deadline.Token); var stored = await Bytes(files.Path);
            var steer = await session.SubmitInputAsync(new("steer", PromptInputSource.Rpc, StreamingBehavior: PromptInputStreamingBehavior.Steer), admission, cancellationToken: deadline.Token);
            var follow = await session.SubmitInputAsync(new("follow", PromptInputSource.Rpc, StreamingBehavior: PromptInputStreamingBehavior.FollowUp), admission, cancellationToken: deadline.Token);
            Equal(SubmittedInputDisposition.Queued, steer.Disposition); Equal(SubmittedInputDisposition.Queued, follow.Disposition);
            Check(steer.Run is null && follow.Run is null, "Queued input manufactured a generation.");
            var queued = session.GetPendingInputQueueSnapshot(); Equal("steer|hook", Text(queued.SteeringMessages.Single())); Equal("follow|hook", Text(queued.FollowUpMessages.Single()));
            EqualBytes(stored, await Bytes(files.Path)); var clocks = files.Clocks; var ids = files.Ids;
            var handled = await session.SubmitInputAsync(new("handled"), admission, cancellationToken: deadline.Token);
            Equal(SubmittedInputDisposition.Handled, handled.Disposition); Equal(clocks, files.Clocks); Equal(ids, files.Ids); Queues(queued, session.GetPendingInputQueueSnapshot());
            await ThrowsAsync<InvalidOperationException>(() => session.SubmitInputAsync(new("missing mode"), admission, cancellationToken: deadline.Token));
            Queues(queued, session.GetPendingInputQueueSnapshot()); EqualBytes(stored, await Bytes(files.Path));
            provider.Release.TrySetResult(); await Stage(ended.Task, run, deadline.Token);
            Equal(3, source.Requests.Count); Equal("steer|hook", Text(source.Requests[1].Messages[^1])); Equal("follow|hook", Text(source.Requests[2].Messages[^1]));
            var late = await session.SubmitInputAsync(new("late", StreamingBehavior: PromptInputStreamingBehavior.FollowUp), admission, cancellationToken: deadline.Token);
            Equal(SubmittedInputDisposition.Queued, late.Disposition); False(run.IsCompleted); Equal(3, source.Requests.Count);
            Equal("late|hook", Text(session.GetPendingInputQueueSnapshot().FollowUpMessages.Single()));
            endRelease.TrySetResult(); await run.WaitAsync(deadline.Token);
            Equal(3, source.Requests.Count); Equal(1, session.GetPendingInputQueueSnapshot().FollowUpMessages.Length);
            await session.ContinueAsync(deadline.Token); Equal(4, source.Requests.Count); Equal("late|hook", Text(source.Requests[3].Messages[^1]));
            Equal("steer,followUp,,,followUp", string.Join(',', modes)); await Acknowledged(session, deadline.Token);
        }
        finally { provider.Release.TrySetResult(); endRelease.TrySetResult(); session.Abort(); await Join(run); }
        Equal(source.Requests.Count, source.Cleanups);
    }

    private static async Task CleanupAndReporter()
    {
        using (var files = new Files())
        using (var deadline = Deadline())
        await using (var registry = new ExtensionRegistry())
        {
            PersistentAgentSession? session = null; var entered = Gate(); var cleanup = Gate(); var release = Gate(); var ownerCleanups = 0; var callbackCleanups = 0;
            var cancellationFailure = new IOException("SECRET cancellation callback");
            var scope = await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                entries.RegisterInputHandler(new("held", async (_, _, token) =>
                {
                    Throws<InvalidOperationException>(() => session!.WaitForIdleAsync());
                    Throws<InvalidOperationException>(() => session!.DisposeAsync());
                    Throws<InvalidOperationException>(() => session!.SubmitInputAsync(new("recursive")));
                    using var registration = token.Register(() => throw cancellationFailure);
                    entered.TrySetResult();
                    try { await Gate().Task.WaitAsync(token); return null; }
                    finally { cleanup.TrySetResult(); await release.Task; callbackCleanups++; }
                }));
                return ValueTask.CompletedTask;
            }, () => { ownerCleanups++; return ValueTask.CompletedTask; }));
            var binding = Binding(registry); var source = new Source(); var storage = new StorageFactory();
            var runtime = new SessionRuntimeRegistry([new(Model, source)], [], new Policy());
            session = await PersistentAgentSession.CreateAsync(files.Path, files.Header, runtime, Model, files.Clock, files.NextId,
                new(SessionLogStoreOptions: new(StorageFactory: storage)), deadline.Token);
            var clocks = files.Clocks; var ids = files.Ids;
            var run = session.SubmitInputAsync(new("held"), Admission(registry, binding), cancellationToken: deadline.Token);
            Task? close = null; Task? ownerClose = null;
            try
            {
                await Stage(entered.Task, run, deadline.Token); True(session.Snapshot.IsAdmittingInput); False(session.Snapshot.Agent.IsRunning);
                False(session.WaitForIdleAsync().IsCompleted);
                Throws<InvalidOperationException>(() => session.PromptAsync(User("busy")));
                Throws<InvalidOperationException>(() => session.ContinueAsync());
                Throws<InvalidOperationException>(() => session.ConfigureAsync(new(ThinkingLevel: "off")));
                True(session.Abort()); close = session.DisposeAsync().AsTask(); Check(ReferenceEquals(close, session.DisposeAsync().AsTask()), "Input cleanup did not share coordinator settlement.");
                ownerClose = scope.DisposeAsync().AsTask(); await Stage(cleanup.Task, run, deadline.Token);
                False(close.IsCompleted); False(ownerClose.IsCompleted); Equal(0, ownerCleanups); Equal(0, callbackCleanups); Equal(0, storage.Storage!.Disposals);
                release.TrySetResult(); await ThrowsAsync<OperationCanceledException>(() => run);
                var retained = await ThrowsAsync<PersistentAgentSessionException>(() => close);
                Equal(PersistentAgentSessionFailure.CleanupFailed, retained.Fault.Failure);
                Check(OnlyOriginalCancellationCause(retained, cancellationFailure), "Joined disposal lost or masked the original cancellation failure.");
                await ownerClose;
                Equal(1, callbackCleanups); Equal(1, ownerCleanups); Equal(1, storage.Storage!.Disposals);
                True(session.Snapshot.InputCancellationCallbackFailed); False(session.Snapshot.IsAdmittingInput);
                Equal(0, source.Requests.Count); Equal(0L, session.Snapshot.Agent.Generation); Equal(clocks, files.Clocks); Equal(ids, files.Ids);
                await using var lease = await SessionLogStore.OpenAsync(files.Path, cancellationToken: deadline.Token);
                Equal(new FileInfo(files.Path).Length, lease.Snapshot.CommittedByteLength);
            }
            finally
            {
                release.TrySetResult(); session.Abort();
                try
                {
                    try { await run; } catch (OperationCanceledException) { }
                    var retained = await ThrowsAsync<PersistentAgentSessionException>(() => session.DisposeAsync().AsTask());
                    Equal(PersistentAgentSessionFailure.CleanupFailed, retained.Fault.Failure);
                    Check(OnlyOriginalCancellationCause(retained, cancellationFailure), "Repeated disposal lost or masked the original cancellation failure.");
                }
                finally { if (ownerClose is not null) await ownerClose; }
            }
        }
        using var faultFiles = new Files(); using var faultDeadline = Deadline(); await using var faultRegistry = new ExtensionRegistry();
        await faultRegistry.ActivateAsync("owner", new Extension((entries, _) =>
        { entries.RegisterInputHandler(new("error", (_, _, _) => throw new IOException("SECRET handler"))); return ValueTask.CompletedTask; }));
        var faultBinding = Binding(faultRegistry); var faultSource = new Source(); await using var faultSession = await Create(faultFiles, faultBinding, faultSource, null, faultDeadline.Token);
        var reported = Gate(); var reporterRelease = Gate(); var original = await Bytes(faultFiles.Path); var originalClock = faultFiles.Clocks;
        var faulty = Admission(faultRegistry, faultBinding, async (diagnostic, _) =>
        { False(diagnostic.ToString().Contains("SECRET", StringComparison.Ordinal)); reported.TrySetResult(); await reporterRelease.Task; throw new IOException("SECRET observer"); });
        var observerRun = faultSession.SubmitInputAsync(new("error"), faulty, cancellationToken: faultDeadline.Token);
        try
        {
            await Stage(reported.Task, observerRun, faultDeadline.Token); False(observerRun.IsCompleted); False(faultSession.WaitForIdleAsync().IsCompleted);
            reporterRelease.TrySetResult(); var error = await ThrowsAsync<PromptInputAdmissionException>(() => observerRun);
            Equal(PromptInputAdmissionFailure.HandlerFailed, error.Failure); False(error.ToString().Contains("SECRET", StringComparison.Ordinal));
            Equal(0, faultSource.Requests.Count); Equal(originalClock, faultFiles.Clocks); EqualBytes(original, await Bytes(faultFiles.Path));
            Check(faultSession.Snapshot.Fault is null, "Pre-effect observer failure poisoned durable history."); await faultSession.WaitForIdleAsync(faultDeadline.Token);
        }
        finally { reporterRelease.TrySetResult(); faultSession.Abort(); await Join(observerRun); }
    }

    private static async Task AdmissionAndCheckpoint()
    {
        using var files = new Files(); using var deadline = Deadline(); await using var registry = new ExtensionRegistry(); var callbacks = 0;
        await registry.ActivateAsync("owner", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(new("replace", (input, _, _) =>
            {
                callbacks++;
                return ValueTask.FromResult<ExtensionInputPatch?>(input.Text == "bad final" ? new(ExtensionInputAction.Transform, new string('z', 20)) : null);
            }));
            return ValueTask.CompletedTask;
        }));
        var binding = Binding(registry); var source = new Source(); var storage = new StorageFactory(); var session = await Create(files, binding, source, storage, deadline.Token);
        var admission = Admission(registry, binding); var before = await Bytes(files.Path); var clock = files.Clocks; var ids = files.Ids;
        var invalid = new (PromptInput Input, PromptInputAdmissionOptions Limits)[]
        {
            (new(new string('x', 9)), new(MaximumTextCharacters: 8)),
            (new("\ud800"), new()),
            (new("x", (PromptInputSource)999), new()),
            (new("x", Images: JsonData.Parse("[{},{}]")), new(MaximumImages: 1)),
            (new("x", Images: JsonData.Parse("[{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"m\",\"n\":1e999}]")), new()),
            (new("x", Images: Images), new(MaximumImageCharacters: 1))
        };
        foreach (var test in invalid) Throws<PromptInputAdmissionException>(() => session.SubmitInputAsync(test.Input, admission, test.Limits));
        Equal(0, callbacks); Equal(clock, files.Clocks); Equal(ids, files.Ids);
        var changed = await ThrowsAsync<PromptInputAdmissionException>(() => session.SubmitInputAsync(new("bad final"), admission, new(MaximumTextCharacters: 10), deadline.Token));
        Equal(PromptInputAdmissionFailure.ResourceLimit, changed.Failure); Equal(1, callbacks); Equal(clock, files.Clocks); Equal(ids, files.Ids);
        await ThrowsAsync<PromptInputAdmissionException>(() => session.SubmitInputAsync(new("x"), admission, new(MaximumMessageCharacters: 1), deadline.Token));
        await ThrowsAsync<PromptInputAdmissionException>(() => session.SubmitInputAsync(new("x", Images: JsonData.Parse("[{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"image/png\"}]")),
            admission, new(MaximumJsonDepth: 2), deadline.Token));
        Equal(0, source.Requests.Count); Equal(ids, files.Ids); EqualBytes(before, await Bytes(files.Path));
        const string envelope = "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"timestamp\":123}";
        var exact = await session.SubmitInputAsync(new("x"), admission, new(MaximumTextCharacters: 1, MaximumMessageCharacters: envelope.Length, MaximumMessageBytes: envelope.Length), deadline.Token);
        Equal(SubmittedInputDisposition.Started, exact.Disposition); Equal(envelope, source.Requests.Single().Messages[^1].WireBody.ToString());
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var count = callbacks;
        Throws<OperationCanceledException>(() => session.SubmitInputAsync(new("cancelled"), admission, cancellationToken: canceled.Token)); Equal(count, callbacks);
        await session.SubmitInputAsync(new("data\0\u6587"), admission, cancellationToken: deadline.Token);
        Equal("data\0\u6587", Text(source.Requests[^1].Messages[^1])); await Acknowledged(session, deadline.Token);
        var barrier = storage.Storage!.Arm(fail: true); var requests = source.Requests.Count;
        var failure = session.SubmitInputAsync(new("checkpoint failure"), admission, cancellationToken: deadline.Token);
        try
        {
            await Stage(barrier.Entered.Task, failure, deadline.Token); Equal(requests, source.Requests.Count);
            barrier.Release.TrySetResult(); await ThrowsAsync<PersistentAgentSessionException>(() => failure);
            Check(session.Snapshot.Fault is { Failure: PersistentAgentSessionFailure.AppendFailed, MayHaveWritten: true }, "Uncertain input append failure was not retained.");
            Equal(requests, source.Requests.Count); False(session.Snapshot.IsAdmittingInput);
            await session.WaitForIdleAsync(deadline.Token);
        }
        finally { barrier.Release.TrySetResult(); session.Abort(); await Join(failure); await session.DisposeAsync(); }
        Equal(1, storage.Storage!.Disposals);
        var inspected = await new SessionLogReader().ReadFileAsync(files.Path, deadline.Token);
        Equal(SessionLogReadStatus.Complete, inspected.Status); Equal(new FileInfo(files.Path).Length, (long)inspected.OriginalBytes.Length);
    }

    private static ExtensionAgentBinding Binding(ExtensionRegistry registry) => new(registry, new Policy(), static (_, _, _) => ValueTask.FromResult(false));
    private static RegisteredExtensionInputAdmission Admission(ExtensionRegistry registry, ExtensionAgentBinding binding,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? diagnostics = null) => new(registry, binding.Snapshot, reportDiagnostic: diagnostics);
    private static AgentConfiguration Configuration(ExtensionAgentBinding binding, Source source) => new(Model, source, binding.Tools);
    private static Task<PersistentAgentSession> Create(Files files, ExtensionAgentBinding binding, Source source, StorageFactory? storage, CancellationToken token) =>
        PersistentAgentSession.CreateAsync(files.Path, files.Header, Configuration(binding, source), files.Clock, files.NextId,
            new(SessionLogStoreOptions: storage is null ? null : new(StorageFactory: storage)), token);
    private static TranscriptEntry User(string text) => PromptInputValue.Message(new(text), 123);
    private static string Text(TranscriptEntry message) => message.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
    private static async Task<byte[]> Bytes(string path)
    { await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); return bytes.ToArray(); }
    private static async ValueTask Acknowledged(PersistentAgentSession session, CancellationToken token)
    {
        await using var file = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = await new SessionLogReader().ReadAsync(file, cancellationToken: token);
        Equal(SessionLogReadStatus.Complete, read.Status); Equal((long)read.OriginalBytes.Length, session.Snapshot.Log.CommittedByteLength);
    }
    private sealed class Files : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-registered-input-" + Guid.NewGuid().ToString("N"));
        public int Clocks, Ids;
        public string Path => System.IO.Path.Combine(root, "session.jsonl");
        public SessionEntry Header => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "header", timestamp = "2026-10-01T00:00:00.000Z", cwd = root }));
        public Files() => Directory.CreateDirectory(root);
        public long Clock() { Interlocked.Increment(ref Clocks); return 123; }
        public string NextId() => "entry-" + Interlocked.Increment(ref Ids);
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(root); var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(target) != parent || !System.IO.Path.GetFileName(target).StartsWith("PiSharp-registered-input-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing cleanup outside owned input test directory.");
            Directory.Delete(target, recursive: true);
        }
    }
    private sealed class Source : IChatTransport
    {
        public readonly List<ChatRequest> Requests = []; public int Cleanups; private Barrier? barrier;
        public Func<ChatRequest, CancellationToken, ValueTask>? BeforeSend;
        public Barrier Arm() => barrier = new(false);
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); if (BeforeSend is not null) await BeforeSend(request, token);
            var index = Requests.Count; Requests.Add(request); var selected = barrier; barrier = null;
            try
            {
                if (selected is not null) await selected.WaitAsync(token);
                var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [new TextContent("answer-" + index)], TokenUsage.Zero, StopReason.Stop);
                yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
                yield return new TextStarted(0, new("")); yield return new TextEnded(0, ((TextContent)message.Content[0]).Text); yield return new StreamDone(StopReason.Stop, message);
            }
            finally { Cleanups++; }
        }
    }
    private sealed class Barrier(bool fail)
    {
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async ValueTask WaitAsync(CancellationToken token = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(token); if (fail) throw new IOException("SECRET checkpoint failure"); }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        public Storage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class Storage(ISessionLogStorage inner) : ISessionLogStorage
    {
        private readonly object gate = new(); private Barrier? barrier; public int Disposals, Writes;
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) { Writes++; return inner.WriteAsync(bytes); }
        public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
        public Barrier Arm(bool fail = false) { lock (gate) return barrier = new(fail); }
        public async ValueTask BeforeCheckpointAsync()
        { Barrier? selected; lock (gate) { selected = barrier; barrier = null; } await inner.BeforeCheckpointAsync(); if (selected is not null) await selected.WaitAsync(); }
        public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class Extension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask; }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private static void Queues(AgentPendingInputQueueSnapshot expected, AgentPendingInputQueueSnapshot actual)
    { Prefix(expected.SteeringMessages, actual.SteeringMessages); Equal(expected.SteeringMessages.Length, actual.SteeringMessages.Length); Prefix(expected.FollowUpMessages, actual.FollowUpMessages); Equal(expected.FollowUpMessages.Length, actual.FollowUpMessages.Length); }
    private static void Prefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { True(actual.Length >= expected.Length); for (var index = 0; index < expected.Length; index++) Equal(expected[index].WireBody.ToString(), actual[index].WireBody.ToString()); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool OnlyOriginalCancellationCause(Exception error, Exception original) => ReferenceEquals(error, original) ||
        error is AggregateException { InnerExceptions.Count: > 0 } aggregate && aggregate.InnerExceptions.All(inner => OnlyOriginalCancellationCause(inner, original)) ||
        error is PersistentAgentSessionException { InnerException: { } cause } && OnlyOriginalCancellationCause(cause, original);
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(12));
    private static async Task Stage(Task witness, Task run, CancellationToken token)
    { await Task.WhenAny(witness, run).WaitAsync(token); if (!witness.IsCompleted) { await run; throw new InvalidOperationException("Execution settled before input gate."); } await witness; }
    private static async Task Join(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(12)); } catch (Exception) when (task.IsCompleted) { } }
    private static T Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void EqualBytes(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), "Physical session bytes changed unexpectedly.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void True(bool value) => Check(value, "Input admission assertion failed.");
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
