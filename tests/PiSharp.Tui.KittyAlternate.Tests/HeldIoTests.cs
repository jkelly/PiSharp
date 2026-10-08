using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal sealed record HeldIoResult(int Scenarios, int Failed, bool AllOwnedExecutionsJoined, object Boundary, object[] Rows);
internal sealed record HeldFilePin(string path, long bytes, string sha256);

// These source-only tests require a fresh permitted native boundary. No real console is acquired.
internal static class HeldIoTests
{
    private const string Alternate = "\u001b[32:65;2u";
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);
    internal static async Task<HeldIoResult> Run(JsonElement sourceExpected, HeldIoAdmission admission, HeldIoJournal journal, object earlierEvidence)
    {
        if (!admission.Pass) return new(0, 1, false, admission, []);
        if (sourceExpected.GetProperty("operation").GetProperty("raw").GetString() != Alternate)
            throw new InvalidOperationException("Frozen Shift+Space Source operation differs");
        using var design = JsonDocument.Parse(File.ReadAllText(Fixture("held-io-failure-cancellation-design.json")));
        var rows = new List<object>(); var failed = 0; var joined = true;
        if (!journal.Record("held-io-begin", new { admission, earlierEvidence, incompleteJoin = false, passingReceipt = false }))
            return new(0, 1, false, admission, []);
        var scenarios = design.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        var required = (from route in new[] { "legacy", "acknowledged" }
                        from resource in new[] { "read", "write" }
                        from trigger in new[] { "cancel", "failure" }
                        select $"{route}-held-{resource}-{(trigger == "cancel" ? "cancellation" : trigger)}").ToArray();
        if (!scenarios.Select(s => s.GetProperty("id").GetString()).SequenceEqual(required))
            throw new InvalidOperationException("All eight immutable held-I/O designs must be retained");
        foreach (var scenario in scenarios)
        {
            var id = scenario.GetProperty("id").GetString()!;
            var read = scenario.GetProperty("resource").GetString() == "read";
            var cancel = scenario.GetProperty("trigger").GetString() == "cancel";
            var acknowledged = scenario.GetProperty("route").GetString() == "acknowledged";
            if (scenario.GetProperty("input").GetString() != Alternate) throw new InvalidOperationException("Design operation differs");
            var host = new HeldHost(acknowledged); var held = new IoGate(host.Events);
            var injected = new IOException("Unique held-I/O failure: " + id);
            var assertions = new List<object>(); var failures = new List<string>();
            object? retained = null, sourceObservation = null;
            void Fail(string failure) { lock (failures) failures.Add(failure); }
            string[] FailureSnapshot() { lock (failures) return failures.ToArray(); }
            object[] AssertionSnapshot() { lock (assertions) return assertions.ToArray(); }
            void Check(string criterion, bool pass)
            { lock (assertions) assertions.Add(new { criterion, pass }); if (!pass) Fail(criterion); }
            void PersistDiagnostic(string phase)
            {
                if (!journal.Record("held-io-diagnostic", new { phase, id, admission, earlierEvidence, completedRows = rows.ToArray(),
                    scenario = scenario.Clone(), completeSourceExpectedObject = sourceExpected.Clone(), sourceObservation, retained,
                    primaryFailure = Failure(host.PrimaryFailure, injected), injectedFailure = Failure(injected, injected),
                    originalTasks = host.TaskReceipts(), current = host.Snapshot(held), cleanupErrors = host.CleanupSnapshot(),
                    assertions = AssertionSnapshot(), failures = FailureSnapshot(), causalEvents = host.Events.Snapshot(),
                    allOriginalTasksJoined = host.AllJoined, incompleteJoin = !host.AllJoined, passingReceipt = false }))
                    Fail("diagnostic receipt could not be persisted");
            }
            host.CleanupRecorded = () => PersistDiagnostic("cleanup-error-during-original-join");
            try
            {
                await host.Start();
                // Only the next write is armed. A read gate travels with the packet and is
                // claimed by the following ReadAsync, after the decoder has admitted the key.
                if (!read) host.Sink.ArmWrite(held);
                var admitted = host.Sink.Queue(Alternate, read ? held : null);
                await Observe(admitted);
                await Observe(held.Entered.Task);
                await Observe(held.Bound.Task);
                if (read)
                {
                    await host.ObserveFocus();
                    sourceObservation = CompareSource(host, sourceExpected, includeRenderedFrame: true, Check);
                }
                else
                    sourceObservation = CompareSource(host, sourceExpected, includeRenderedFrame: false, Check);
                if (cancel)
                {
                    host.RequestStop("scenario-cancellation");
                    await Observe(held.CancellationObserved.Task);
                    if (read && acknowledged) await Observe(host.EndNotification.Task);
                    if (!read) await Observe(host.Sink.CooperativeReadCancelledSettled.Task);
                }
                host.BeginClose();
                var checkpointSequence = host.Events.Add("held-retained-checkpoint");
                retained = host.Snapshot(held);
                Check("exact original held I/O remains pending", held.Original is { IsCompleted: false });
                Check("exact original run remains pending", host.Run is { IsCompleted: false });
                Check("owned close remains pending", host.CloseTask is { IsCompleted: false });
                Check("view disposal has not been reported joined before the run", host.ViewDisposeTask is null);
                Check("one held operation entered", held.Entries == 1);
                Check("held task is the original terminal operation", host.Sink.Operations().Any(t => ReferenceEquals(t.Task, held.Original)));
                Check("held operation follows completed startup paint", held.EntrySequence > host.StartupSequence);
                Check("held write retains its full actual attempted output", read || !string.IsNullOrEmpty(held.WriteText) && held.OperationIndex > host.StartupWrites);
                Check("held resource remains active", read ? host.Sink.ActiveReads == 1 : host.Sink.ActiveWrites == 1);
                Check("focus attachment remains current while retained", host.Last?.EditorFocus is { } current && host.Owner.IsCurrent(current));
                if (cancel) Check("borrowed gate observed cancellation without release", held.CancellationObserved.Task.IsCompleted && !held.Released);
                if (!read && cancel) Check("cooperative physical reader settled before held write release", host.Sink.ActiveReads == 0);
                held.Release(cancel ? null : injected);
                Check("manual release follows the retained checkpoint", held.ReleaseSequence > checkpointSequence);
                if (cancel) Check("cancellation was observed before manual release", held.CancellationSequence > held.EntrySequence && held.ReleaseSequence > held.CancellationSequence);
                await Observe(held.Settled.Task);
                // This deadline is a diagnostic watchdog, never a replacement task/join.
                // The finally block still awaits the original close after releasing owned gates.
                await Observe(host.CloseTask!);
            }
            catch (Exception error)
            {
                Fail("scenario control: " + error); host.Events.Add("scenario-control-failed", error.ToString());
                PersistDiagnostic("watchdog-or-control-failure-before-cleanup");
            }
            finally
            {
                if (!held.Released) held.Release(null);
                if (host.Run is { IsCompleted: false }) host.RequestStop("failure-cleanup-cancellation");
                host.Sink.End();
                host.BeginClose();
                PersistDiagnostic("before-final-original-close-join");
                try { await host.CloseTask!; } // Exact original close; diagnostic is durable first.
                catch (Exception error)
                {
                    host.RecordCleanupError("original close fault: " + error); Fail("original close fault: " + error);
                    PersistDiagnostic("original-close-fault-before-recovery-join");
                    try { await host.RecoverCloseFault(); }
                    catch (Exception recovery)
                    {
                        host.RecordCleanupError("original recovery fault: " + recovery); Fail("original recovery fault: " + recovery);
                        PersistDiagnostic("recovery-fault-incomplete-join");
                        // Retain ownership even if the close/recovery orchestration faults.
                        // This last sweep still awaits original admitted tasks, never substitutes a deadline.
                        await host.DrainOriginalTasks();
                    }
                }
            }
            Check("required checkpoint captured", retained is not null && sourceObservation is not null);
            Check("original outcome preserved", cancel ? host.PrimaryFailure is OperationCanceledException : ReferenceEquals(host.PrimaryFailure, injected));
            Check("failure is not a successful EOF", host.PrimaryFailure is not null && host.Exit is null);
            Check("all original tasks explicitly awaited", host.AllJoined);
            Check("balanced physical I/O counters", host.Sink.ActiveReads == 0 && host.Sink.ActiveWrites == 0 && host.Sink.ReadsStarted == host.Sink.ReadsSettled && host.Sink.WritesStarted == host.Sink.WritesSettled);
            Check("borrowed terminal never disposed", host.Sink.Disposals == 0);
            Check("view, stop and receipt owner completed exactly once", host.ViewDisposeCalls == 1 && host.StopDisposals == 1 && host.ReceiptCompletions == 1);
            Check("secondary cleanup errors retained and empty", host.CleanupErrors.Count == 0);
            Check("focus detached after original run join", await host.ProbeDetached());
            Check("exact gate settled before original run observation", held.SettledSequence > held.ReleaseSequence && host.RunSettledSequence > held.SettledSequence);
            Check("explicit physical settlement signal observed", held.Settled.Task.IsCompletedSuccessfully);
            var receipt = host.Receipts.Snapshot;
            Check("acknowledgement lifetime drained", host.Receipts.IsCompleted && receipt.Pending == 0 && receipt.Accepted == 0 && receipt.RetainedUtf16 == 0);
            Check("acknowledged end notification observed with original outcome", !acknowledged || host.Notifications.Count == 1 && ReferenceEquals(host.Notifications[0], host.PrimaryFailure));
            Check("no synthetic submission or interrupt", host.Submits.Count == 0 && host.Interrupts == 0);
            var pass = failures.Count == 0;
            if (!pass) failed++;
            joined &= host.AllJoined;
            rows.Add(new { id, route = acknowledged ? "acknowledged" : "legacy", resource = read ? "read" : "write", trigger = cancel ? "cancel" : "failure",
                design = scenario.Clone(), completeSourceExpectedObject = sourceExpected.Clone(), sourceObservation, retained,
                primaryFailure = Failure(host.PrimaryFailure, injected), injectedFailurePreserved = ReferenceEquals(host.PrimaryFailure, injected),
                host.Exit, final = host.Snapshot(held), originalTasks = host.TaskReceipts(), notifications = host.Notifications.Select(e => Failure(e, injected)).ToArray(),
                cleanupErrors = host.CleanupSnapshot(), assertions, failures, causalEvents = host.Events.Snapshot(), pass });
            journal.Record("held-io-scenario-completed", new { admission, completedRows = rows.ToArray(), allOriginalTasksJoined = host.AllJoined,
                incompleteJoin = !host.AllJoined, passingReceipt = pass && host.AllJoined });
            if (!host.AllJoined || journal.Errors.Count != 0) break; // No later scenario can hide incomplete ownership/evidence.
        }
        return new(rows.Count, failed + journal.Errors.Count, joined, new { admission,
            sourceOracle = FilePin(Fixture("shift-space-source-observations.json")), design = FilePin(Fixture("held-io-failure-cancellation-design.json")),
            physicalTerminal = false, fullNativeGate = false, originalPackageAcceptance = false }, rows.ToArray());
    }
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    private static HeldFilePin FilePin(string file)
    { var data = File.ReadAllBytes(file); return new(Path.GetFullPath(file), data.LongLength, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()); }
    private static object? Failure(Exception? error, Exception injected) => error is null ? null :
        new { type = error.GetType().FullName, error.Message, detail = error.ToString(), sameInjectedInstance = ReferenceEquals(error, injected) };
    private static async Task Observe(Task original) => await original.WaitAsync(Watchdog);
    private static object CompareSource(HeldHost host, JsonElement expected, bool includeRenderedFrame, Action<string, bool> check)
    {
        var draft = host.Last ?? throw new InvalidOperationException("No actual draft callback");
        var frame = host.Frame ?? throw new InvalidOperationException("No actual view observation");
        check("actual alternate draft equals Source", draft.Text == expected.GetProperty("text").GetString() && draft.Text == expected.GetProperty("expandedText").GetString());
        check("actual cursor equals Source", draft.CursorUtf16Offset == expected.GetProperty("cursorUtf16Offset").GetInt32());
        check("actual changes and submissions equal Source", host.Changes.SequenceEqual(expected.GetProperty("changes").EnumerateArray().Select(RequiredSourceString)) && host.Submits.SequenceEqual(expected.GetProperty("submits").EnumerateArray().Select(RequiredSourceString)));
        check("actual focused layout belongs to owner", draft.EditorOwnsFocus == expected.GetProperty("focused").GetBoolean() && draft.EditorFocus is { } focus && host.Owner.IsCurrent(focus) && draft.Layout?.Identity.EditorLifetimeId == focus.EditorLifetimeId);
        if (includeRenderedFrame)
            check("completed actual Source frame and hardware cursor", frame.SourceComponent.Rows.SequenceEqual(expected.GetProperty("rows").EnumerateArray().Select(RequiredSourceString)) && !frame.Frame.Cursor.Visible && ReplayHarness.Position(frame.Frame));
        return new { draft.Text, draft.CursorUtf16Offset, draft.EditorOwnsFocus, changes = host.Changes.ToArray(), submits = host.Submits.ToArray(),
            sourceFrameCompared = includeRenderedFrame, actualObservedRows = frame.SourceComponent.Rows, frameObservationIsPreWrite = !includeRenderedFrame,
            partialWriteVisibilityAccepted = false };
    }

    private static string RequiredSourceString(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? throw new InvalidOperationException("Source expected string is null.")
        : throw new InvalidOperationException("Source expected value must be a non-null string.");

    private sealed class EventLog
    {
        private readonly object sync = new(); private readonly List<object> events = []; private long sequence;
        internal long Add(string name, object? detail = null)
        { lock (sync) { var next = ++sequence; events.Add(new { sequence = next, name, detail }); return next; } }
        internal object[] Snapshot() { lock (sync) return events.ToArray(); }
    }
    private sealed class OriginalTask(string kind, int index, EventLog events)
    {
        internal readonly TaskCompletionSource Bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Task = null!; internal bool Awaited; internal Exception? Error;
        internal void Bind(Task task) { Task = task; Bound.TrySetResult(); }
        internal async Task Join()
        {
            if (Awaited) return;
            await Bound.Task;
            try { await Task; } catch (Exception error) { Error = error; }
            finally { Awaited = true; events.Add("original-task-awaited", new { kind, index, taskId = Task.Id, status = Task.Status.ToString() }); }
        }
        internal object Snapshot() => new { kind, index, taskId = Bound.Task.IsCompleted ? Task.Id : (int?)null,
            status = Bound.Task.IsCompleted ? Task.Status.ToString() : "not-bound", completed = Bound.Task.IsCompleted && Task.IsCompleted, Awaited, error = Error?.ToString() };
    }
    private sealed class IoGate(EventLog events)
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception?> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Original; internal string? WriteText; internal int OperationIndex, Entries;
        internal long EntrySequence, CancellationSequence, ReleaseSequence, SettledSequence;
        private readonly object sync = new(); internal bool Released { get { lock (sync) return release.Task.IsCompleted; } }
        internal async Task Hold(OriginalTask operation, string resource, int index, string? text, CancellationToken token)
        {
            await operation.Bound.Task;
            Original = operation.Task; OperationIndex = index; WriteText = text; Bound.TrySetResult();
            using var registration = token.Register(() => { CancellationSequence = events.Add("held-io-cancellation-observed", resource); CancellationObserved.TrySetResult(); });
            Entries++; EntrySequence = events.Add("held-io-entered", new { resource, index, originalTaskId = Original.Id, text }); Entered.TrySetResult();
            // The token records cancellation but cannot settle this borrowed operation.
            var failure = await release.Task;
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        internal void Release(Exception? failure)
        {
            lock (sync)
            {
                if (release.Task.IsCompleted) return;
                ReleaseSequence = events.Add("held-io-explicit-release", failure?.ToString());
                release.TrySetResult(failure);
            }
        }
        internal void SetSettled(string resource)
        { SettledSequence = events.Add("held-io-settled", resource); Settled.TrySetResult(); }
    }

    private sealed class HeldTerminal(EventLog events) : IConsoleTerminal, ITerminalViewportSource
    {
        private sealed record Packet(string? Text, IoGate? FollowingRead)
        { internal readonly TaskCompletionSource Admitted = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        private readonly Channel<Packet> packets = Channel.CreateUnbounded<Packet>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
        private readonly object sync = new(); private readonly List<OriginalTask> operations = [];
        private Packet? previous; private IoGate? followingRead, nextWrite;
        internal int ReadsStarted, ReadsSettled, ActiveReads, WritesStarted, WritesSettled, ActiveWrites, Disposals;
        internal readonly TaskCompletionSource CooperativeReadCancelledSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TerminalViewport ReadViewport() => new(20, 24, 0, 0, 20, 24);
        public TerminalLeaseSnapshot Snapshot { get { var state = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0); return new(state, state, null, false, false, ActiveReads, ActiveWrites, ReadsStarted, ReadsSettled, WritesStarted, WritesSettled); } }
        internal Task Queue(string text, IoGate? holdFollowingRead)
        { var packet = new Packet(text, holdFollowingRead); if (!packets.Writer.TryWrite(packet)) throw new InvalidOperationException("Test packet admission failed"); events.Add("alternate-packet-queued", text); return packet.Admitted.Task; }
        internal void End() => packets.Writer.TryWrite(new(null, null));
        internal void ArmWrite(IoGate held)
        { if (Interlocked.CompareExchange(ref nextWrite, held, null) is not null) throw new InvalidOperationException("Write gate already armed"); events.Add("next-write-armed"); }
        internal OriginalTask[] Operations() { lock (sync) return operations.ToArray(); }
        private OriginalTask NewOperation(string kind, int index)
        { var operation = new OriginalTask(kind, index, events); lock (sync) operations.Add(operation); return operation; }
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            var index = Interlocked.Increment(ref ReadsStarted); Interlocked.Increment(ref ActiveReads);
            var operation = NewOperation("physical-read", index); var task = ReadCore(operation, index, destination, token); operation.Bind(task); return new(task);
        }
        private async Task<int> ReadCore(OriginalTask operation, int index, Memory<char> destination, CancellationToken token)
        {
            var held = Interlocked.Exchange(ref followingRead, null); var cancelled = false;
            events.Add("physical-read-started", index);
            previous?.Admitted.TrySetResult();
            try
            {
                if (held is not null) { await held.Hold(operation, "read", index, null, token); return 0; }
                var packet = await packets.Reader.ReadAsync(token); previous = packet;
                if (packet.Text is null) return 0;
                packet.Text.AsMemory().CopyTo(destination);
                followingRead = packet.FollowingRead;
                events.Add("alternate-packet-delivered", packet.Text);
                return packet.Text.Length;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = held is null; throw; }
            finally
            {
                Interlocked.Decrement(ref ActiveReads); Interlocked.Increment(ref ReadsSettled);
                events.Add("physical-read-settled", index); held?.SetSettled("read");
                if (cancelled) { events.Add("cooperative-read-cancelled-settled", index); CooperativeReadCancelledSettled.TrySetResult(); }
            }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            var index = Interlocked.Increment(ref WritesStarted); Interlocked.Increment(ref ActiveWrites);
            var operation = NewOperation("physical-write", index); var task = WriteCore(operation, index, text.ToString(), token); operation.Bind(task); return new(task);
        }
        private async Task WriteCore(OriginalTask operation, int index, string text, CancellationToken token)
        {
            var held = Interlocked.Exchange(ref nextWrite, null); events.Add("physical-write-started", new { index, text });
            try { if (held is not null) await held.Hold(operation, "write", index, text, token); else token.ThrowIfCancellationRequested(); }
            finally { Interlocked.Decrement(ref ActiveWrites); Interlocked.Increment(ref WritesSettled); events.Add("physical-write-settled", index); held?.SetSettled("write"); }
        }
        public ValueTask DisposeAsync()
        { Interlocked.Increment(ref Disposals); throw new InvalidOperationException("Borrowed test terminal was disposed"); }
    }

    private sealed class HeldHost
    {
        internal readonly EventLog Events = new(); internal readonly HeldTerminal Sink;
        internal readonly TerminalEditorFocusOwner Owner = new(); internal readonly TerminalSessionView View;
        internal readonly CancellationTokenSource Stop = new(); internal readonly TerminalSubmissionReceipts Receipts = new();
        internal readonly TaskCompletionSource EndNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool acknowledged; private readonly List<OriginalTask> paints = [], focuses = [];
        internal readonly List<string> Changes = [], Submits = [], CleanupErrors = [];
        internal readonly List<Exception?> Notifications = [];
        internal Action? CleanupRecorded;
        internal TerminalDraftSnapshot? Last; internal TerminalEditorRenderedFrame? Frame;
        internal Task<TerminalInputExit>? Run; internal Task? RunObserver, CloseTask, ViewStartTask, ViewDisposeTask, RecoveryTask;
        internal Exception? PrimaryFailure; internal TerminalInputExit? Exit;
        internal long StartupSequence, RunSettledSequence; internal int StartupWrites, ViewDisposeCalls, StopDisposals, ReceiptCompletions, Interrupts;
        private bool viewStartAwaited, runAwaited, runObserverAwaited, viewDisposeAwaited, recoveryAwaited;
        internal HeldHost(bool acknowledged)
        { this.acknowledged = acknowledged; Sink = new(Events); View = new(Sink, Sink, (_, frame) => { Frame = frame; Events.Add("actual-view-frame-observed"); }, Owner); }
        internal async Task Start()
        {
            ViewStartTask = View.StartAsync(CancellationToken.None).AsTask();
            try { await ViewStartTask; } finally { viewStartAwaited = true; Events.Add("original-view-start-awaited"); }
            var input = new TerminalChatInput(Sink, null, Owner);
            Run = acknowledged ? input.RunAcknowledgedAsync((line, _) => { Submits.Add(line.Text); Receipts.FinishLocal(line, true); return Task.FromResult(true); },
                _ => Task.CompletedTask, Paint, Receipts, (editor, line) => editor.AddToHistory(line.Text),
                error => { Notifications.Add(error); Events.Add("input-ended-notification", error?.ToString()); CompleteReceipts(); EndNotification.TrySetResult(); },
                () => Interrupts++, Stop.Token, View.CaptureEditorGeometry)
                : input.RunAsync((line, _) => { Submits.Add(line); return Task.FromResult(true); }, Paint, () => Interrupts++, Stop.Token);
            RunObserver = ObserveRun();
            await Observe(startup.Task);
            StartupWrites = Sink.WritesStarted; StartupSequence = Events.Add("startup-paint-completed");
        }
        private ValueTask Paint(TerminalDraftSnapshot draft, CancellationToken token)
        {
            OriginalTask original;
            lock (paints) { original = new("actual-paint-callback", paints.Count + 1, Events); paints.Add(original); }
            var task = PaintCore(draft, token); original.Bind(task); return new(task);
        }
        private OriginalTask[] PaintTasks() { lock (paints) return paints.ToArray(); }
        private async Task PaintCore(TerminalDraftSnapshot draft, CancellationToken token)
        {
            if (draft.Text != (Last?.Text ?? "")) Changes.Add(draft.Text);
            Last = draft; Events.Add("actual-draft-callback", new { draft.Text, draft.CursorUtf16Offset });
            try { await View.SetDraftAsync(draft, token); startup.TrySetResult(); }
            finally { Events.Add("actual-paint-callback-settled", draft.Text); }
        }
        internal async Task ObserveFocus()
        {
            var original = new OriginalTask("same-value-focus-barrier", focuses.Count + 1, Events); focuses.Add(original);
            original.Bind(Owner.SetEditorFocusAsync(true)); await Observe(original.Task); await original.Join();
        }
        private async Task ObserveRun()
        {
            try { Exit = await Run!; } catch (Exception error) { PrimaryFailure = error; }
            finally { runAwaited = true; RunSettledSequence = Events.Add("original-run-awaited", new { taskId = Run!.Id, status = Run.Status.ToString(), error = PrimaryFailure?.ToString() }); }
        }
        internal void RequestStop(string reason)
        { Events.Add(reason); try { Stop.Cancel(); } catch (Exception error) { RecordCleanupError("stop cancellation: " + error); } }
        internal void RecordCleanupError(string error)
        { lock (CleanupErrors) CleanupErrors.Add(error); CleanupRecorded?.Invoke(); }
        internal string[] CleanupSnapshot() { lock (CleanupErrors) return CleanupErrors.ToArray(); }
        private void CompleteReceipts()
        { if (ReceiptCompletions != 0) return; ReceiptCompletions++; Receipts.Complete(); Events.Add("receipt-owner-completed"); }
        internal void BeginClose() { CloseTask ??= Close(); }
        private async Task Close()
        {
            Events.Add("owned-close-started");
            await DrainOriginalTasks();
            Events.Add("owned-close-settled");
        }
        internal async Task RecoverCloseFault()
        {
            RecoveryTask ??= DrainOriginalTasks();
            try { await RecoveryTask; } finally { recoveryAwaited = RecoveryTask.IsCompleted; }
        }
        internal async Task DrainOriginalTasks()
        {
            if (RunObserver is not null)
            {
                try { await RunObserver; } catch (Exception error) { RecordCleanupError("original run observer: " + error); }
                finally { runObserverAwaited = RunObserver.IsCompleted; }
            }
            try { CompleteReceipts(); } catch (Exception error) { RecordCleanupError("receipt completion: " + error); }
            foreach (var task in PaintTasks().Concat(focuses)) await JoinAndRetain(task);
            foreach (var task in Sink.Operations()) await JoinAndRetain(task);
            if (ViewDisposeTask is null)
                try { ViewDisposeCalls++; ViewDisposeTask = View.DisposeAsync().AsTask(); }
                catch (Exception error) { RecordCleanupError("view disposal admission: " + error); }
            if (ViewDisposeTask is not null)
            {
                try { await ViewDisposeTask; } catch (Exception error) { RecordCleanupError("view disposal: " + error); }
                finally { viewDisposeAwaited = ViewDisposeTask.IsCompleted; Events.Add("original-view-disposal-awaited"); }
            }
            foreach (var task in Sink.Operations()) await JoinAndRetain(task); // Includes actual leave writes.
            if (StopDisposals == 0)
                try { Stop.Dispose(); StopDisposals++; Events.Add("owned-stop-disposed"); }
                catch (Exception error) { RecordCleanupError("stop disposal: " + error); }
        }
        private async Task JoinAndRetain(OriginalTask task)
        {
            if (task.Awaited) return;
            await task.Join();
            if (task.Error is { } error && error is not OperationCanceledException && !ReferenceEquals(error, PrimaryFailure))
                RecordCleanupError("secondary admitted task failure: " + error);
        }
        internal bool AllJoined => viewStartAwaited && runAwaited && runObserverAwaited && viewDisposeAwaited &&
            Run is { IsCompleted: true } && RunObserver is { IsCompleted: true } && CloseTask is { IsCompleted: true } && ViewDisposeTask is { IsCompleted: true } &&
            (RecoveryTask is null || recoveryAwaited && RecoveryTask.IsCompleted) && PaintTasks().Concat(focuses).Concat(Sink.Operations()).All(t => t.Awaited && t.Task.IsCompleted);
        internal async Task<bool> ProbeDetached()
        {
            if (Last?.EditorFocus is not { } focus || Owner.IsCurrent(focus)) return false;
            try { await Owner.SetEditorFocusAsync(true); return false; }
            catch (TerminalEditorFocusException error) when (error.Failure == TerminalEditorFocusFailure.Detached)
            { Events.Add("focus-detached-observed"); return true; }
            catch (Exception error) { RecordCleanupError("focus detachment probe: " + error); return false; }
        }
        internal object[] TaskReceipts() => PaintTasks().Concat(focuses).Concat(Sink.Operations()).Select(t => t.Snapshot()).ToArray();
        internal object Snapshot(IoGate held) => new { Sink.ReadsStarted, Sink.ReadsSettled, Sink.ActiveReads, Sink.WritesStarted, Sink.WritesSettled, Sink.ActiveWrites, Sink.Disposals,
            run = TaskState(Run), runObserver = TaskState(RunObserver), close = TaskState(CloseTask), recovery = TaskState(RecoveryTask), viewStart = TaskState(ViewStartTask), viewDispose = TaskState(ViewDisposeTask),
            heldOriginal = TaskState(held.Original), held.WriteText, held.OperationIndex, held.Entries, held.Released, held.EntrySequence, held.CancellationSequence, held.ReleaseSequence, held.SettledSequence,
            taskReceipts = TaskReceipts(), notifications = Notifications.Count, receiptCompleted = Receipts.IsCompleted,
            receiptSnapshot = new { Receipts.Snapshot.Pending, Receipts.Snapshot.Accepted, Receipts.Snapshot.RetainedUtf16 },
            StartupWrites, StartupSequence, RunSettledSequence, ViewDisposeCalls, StopDisposals, ReceiptCompletions, runAwaited, runObserverAwaited, viewDisposeAwaited };
        private static object TaskState(Task? task) => task is null ? new { started = false, id = (int?)null, status = "not-started", completed = false }
            : new { started = true, id = (int?)task.Id, status = task.Status.ToString(), completed = task.IsCompleted };
    }
}
