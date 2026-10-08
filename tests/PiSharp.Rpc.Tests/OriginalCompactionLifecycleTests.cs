using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class OriginalCompactionLifecycleTests
{
    internal const string Prefix = "rpc.original-compaction-lifecycle.";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("actual-compaction", "openai-responses", "fixture");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "native-manual-caller-genuine-result-and-single-wire-pair", Manual),
        (Prefix + "configured-threshold-actual-prompt-lifecycle", Threshold),
        (Prefix + "actual-overflow-recovery-retries-and-native-notifications-survive", Overflow),
        (Prefix + "automatic-no-plan-emits-no-invented-lifecycle", Skip),
        (Prefix + "held-generator-abort-joins-before-terminal", Abort),
        (Prefix + "held-checkpoint-late-abort-acknowledgement-wins", Checkpoint),
        (Prefix + "complete-prospective-event-budget-refuses-before-append", Budget),
        (Prefix + "body-and-all-terminal-callback-faults-preserved", Faults),
        (Prefix + "reentrant-start-abort-and-rpc-self-wait-guard", Reentrant),
        (Prefix + "held-one-sink-multiple-original-faults-and-actual-cancel", HeldMultiFault),
        (Prefix + "held-lone-faulted-OCE-keeps-fault-state-versus-real-cancel", HeldLoneFaultedOce),
        (Prefix + "synchronous-observer-OCE-keeps-fault-state-and-exact-cause", SynchronousObserverOce),
        (Prefix + "observer-provenance-retains-nested-duplicate-empty-inventories", ObserverProvenance)
    ];
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Success(JsonElement value) => Check(value.GetProperty("success").GetBoolean(), value.GetRawText());
    private static Exception[] OriginalCauses(Exception? error)
    {
        var causes = new List<Exception>();
        void Visit(Exception value)
        {
            if (value.GetType().Name == "CompactionObserverTaskFault" && value.InnerException is { } original) Visit(original);
            else if (value is AggregateException { InnerExceptions.Count: > 0 } aggregate)
                foreach (var inner in aggregate.InnerExceptions) Visit(inner);
            else causes.Add(value); // An empty source aggregate is an original fault, not no failure.
        }
        if (error is not null) Visit(error);
        return causes.ToArray();
    }
    private static SessionEntry User(string id, string? parent, string text) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = "2026-10-01T00:00:00.000Z", message = new { role = "user", content = text, timestamp = 123 } }));
    private static JsonElement[] Events(Fixture f, string type) => f.Records.Where(record => record.GetProperty("type").GetString() == type).ToArray();
    private static void ActualResult(Fixture f, SessionSummaryCheckpointReceipt receipt, string reason, bool retry)
    {
        var start = Events(f, "compaction_start").Single(); var end = Events(f, "compaction_end").Single();
        Check(start.GetProperty("reason").GetString() == reason && end.GetProperty("reason").GetString() == reason &&
            !end.GetProperty("aborted").GetBoolean() && end.GetProperty("willRetry").GetBoolean() == retry && !end.TryGetProperty("errorMessage", out _), "Actual lifecycle origin/retry/terminal differs.");
        var result = end.GetProperty("result"); var body = receipt.Entry.WireBody.Value;
        Check(result.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "summary", "firstKeptEntryId", "tokensBefore", "estimatedTokensAfter", "usage", "details" }), "Actual result inventory/order differs.");
        Check(result.GetProperty("summary").GetString() == body.GetProperty("summary").GetString() &&
            result.GetProperty("firstKeptEntryId").GetString() == body.GetProperty("firstKeptEntryId").GetString() &&
            result.GetProperty("tokensBefore").GetDouble() == body.GetProperty("tokensBefore").GetDouble() &&
            result.GetProperty("estimatedTokensAfter").GetDouble() == receipt.Context.Messages.Sum(SessionCompactionTokenEstimator.EstimateTokens) &&
            result.GetProperty("usage").GetRawText() == body.GetProperty("usage").GetRawText() &&
            result.GetProperty("details").GetRawText() == body.GetProperty("details").GetRawText(), "Actual acknowledged entry/context data was lost or fabricated.");
        Check(f.StartActive.Single() && f.EndIdle.Single(), "Lifecycle start/terminal observed wrong compaction state.");
    }
    private static async Task Manual()
    {
        await using (var f = await Fixture.Create())
        {
            var before = f.Session.Snapshot.Log.Sequence;
            var receipt = await f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary);
            Check(receipt is not null && receipt.Append.CheckpointAcknowledged && f.Session.Snapshot.Log.Sequence > before, "Native manual did not append.");
            ActualResult(f, receipt!, "manual", false);
            var result = Events(f, "compaction_end").Single().GetProperty("result");
            Check(result.GetProperty("tokensBefore").GetDouble() == 45001 && result.GetProperty("estimatedTokensAfter").GetDouble() == 22505,
                "Independent manual fixture estimates changed.");
        }
        await using (var f = await Fixture.Create())
        {
            Success(await f.Send(new { id = "manual", type = "compact" }));
            Check(Events(f, "compaction_start").Length == 1 && Events(f, "compaction_end").Length == 1, "Manual centralized wire duplicated.");
            Check(f.StartActive.Single() && f.EndIdle.Single(), "Manual existing lifecycle state changed.");
        }
    }
    private static async Task Threshold()
    {
        await using var f = await Fixture.Create();
        Success(await f.Send(new { id = "auto", type = "set_auto_compaction", enabled = true }));
        Success(await f.Send(new { id = "prompt", type = "prompt", message = "normal completion" }));
        await f.Dispatcher.WaitForIdleAsync().WaitAsync(Bound);
        var end = Events(f, "compaction_end").Single();
        Check(Events(f, "compaction_start").Single().GetProperty("reason").GetString() == "threshold" &&
            end.GetProperty("reason").GetString() == "threshold" && !end.GetProperty("willRetry").GetBoolean() &&
            end.GetProperty("result").GetProperty("summary").GetString() == "offline summary" && f.Transport.Calls == 1 && f.Summary.Calls == 1,
            "Configured actual threshold did not reach original wire projector.");
        Check(f.Observed.Single().Reason == SessionCompactionReason.Threshold && f.StartActive.Single() && f.EndIdle.Single(), "Threshold origin/settlement changed.");
    }
    private static async Task Overflow()
    {
        await using var f = await Fixture.Create(); f.Transport.OverflowFirst = true;
        f.Session.ConfigureAutomaticCompaction(f.Summary, contextWindow: 32768);
        f.Session.ConfigureAutomaticRecovery(16384);
        Success(await f.Send(new { id = "prompt", type = "prompt", message = "overflow completion" }));
        await f.Dispatcher.WaitForIdleAsync().WaitAsync(Bound);
        var end = Events(f, "compaction_end").Single();
        Check(Events(f, "compaction_start").Single().GetProperty("reason").GetString() == "overflow" &&
            end.GetProperty("reason").GetString() == "overflow" && end.GetProperty("willRetry").GetBoolean() &&
            end.TryGetProperty("result", out _) && f.Transport.Calls == 2 && f.Summary.Calls == 1 && f.Observed.Single().Reason == SessionCompactionReason.Overflow,
            "Genuine overflow origin/checkpoint/retry was inferred incorrectly.");
        Check(Events(f, "pisharp_recovery_started").Length == 1 && Events(f, "pisharp_recovery_ended").Length == 1 &&
            Events(f, "pisharp_operation_settled").Length == 1, "Native recovery notification contract disappeared.");
    }
    private static async Task Skip()
    {
        await using var f = await Fixture.Create(compactable: false); f.Transport.LowUsage = true;
        f.Session.ConfigureAutomaticCompaction(f.Summary, contextWindow: 32768);
        Success(await f.Send(new { id = "prompt", type = "prompt", message = "small" }));
        await f.Dispatcher.WaitForIdleAsync().WaitAsync(Bound);
        Check(f.Summary.Calls == 0 && Events(f, "compaction_start").Length == 0 && Events(f, "compaction_end").Length == 0,
            "Skipped threshold fabricated original lifecycle.");
    }
    private static async Task Abort()
    {
        await using var f = await Fixture.Create(); f.Summary.Hold = true; var count = f.Session.Snapshot.Log.Entries.Length;
        var original = f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary);
        Exception? outcome = null;
        try
        {
            await f.Summary.Entered.Task.WaitAsync(Bound); f.Session.Abort(); await f.Summary.Canceled.Task.WaitAsync(Bound);
            Check(!original.IsCompleted && Events(f, "compaction_end").Length == 0 && f.Summary.Active == 1, "Abort abandoned original generator.");
        }
        finally
        {
            f.Summary.Release.TrySetResult();
            try { await original; } catch (Exception error) { outcome = error; }
        }
        Check(outcome is OperationCanceledException, "Canceled original did not preserve cancellation.");
        var end = Events(f, "compaction_end").Single();
        Check(end.GetProperty("aborted").GetBoolean() && !end.GetProperty("willRetry").GetBoolean() &&
            !end.TryGetProperty("result", out _) && !end.TryGetProperty("errorMessage", out _) && f.EndIdle.Single() &&
            f.Session.Snapshot.Log.Entries.Length == count && f.Summary.Active == 0 && f.Summary.Joined.Task.IsCompletedSuccessfully, "Aborted genuine terminal did not join or changed history.");
    }
    private static async Task Checkpoint()
    {
        await using var f = await Fixture.Create(); f.Storage.Hold = true;
        var original = f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary);
        SessionSummaryCheckpointReceipt? receipt = null;
        try
        {
            await f.Storage.Entered.Task.WaitAsync(Bound); f.Session.Abort();
            Check(!original.IsCompleted && Events(f, "compaction_end").Length == 0, "Held checkpoint published terminal early.");
        }
        finally { f.Storage.Release.TrySetResult(); receipt = await original; }
        Check(receipt is not null && f.Storage.Joined.Task.IsCompletedSuccessfully, "Actual checkpoint was abandoned.");
        ActualResult(f, receipt!, "manual", false);
    }
    private static async Task Budget()
    {
        await using var f = await Fixture.Create(options: new(MaximumOutputBytes: 512)); f.Summary.Text = new string('x', 8192);
        var before = f.Session.Snapshot.Log.Entries.Length;
        try { await f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary); throw new InvalidOperationException("Oversized terminal admitted."); }
        catch (Exception error) { Check(OriginalCauses(error).SingleOrDefault() is RpcDispatchException { Failure: RpcDispatchFailure.ResourceLimit }, "Wrong prospective refusal."); }
        Check(f.Session.Snapshot.Log.Entries.Length == before && f.Session.Snapshot.Fault is null && f.Observed.Count == 0,
            "Prospective event validation ran after append.");
        var end = Events(f, "compaction_end").Single(); Check(!end.GetProperty("aborted").GetBoolean() && !end.TryGetProperty("result", out _) &&
            end.TryGetProperty("errorMessage", out _), "Prospective refusal lost actual failure terminal.");
    }
    private static async Task Faults()
    {
        await using var f = await Fixture.Create();
        var first = new IOException("terminal-one"); var second = new IOException("terminal-two");
        using var one = f.Session.SubscribeOperationEvents(new FaultSink(first)); using var two = f.Session.SubscribeOperationEvents(new FaultSink(second));
        f.Summary.Fail = true;
        try { await f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary); throw new InvalidOperationException("Faulted compaction succeeded."); }
        catch (AggregateException errors)
        {
            var leaves = OriginalCauses(errors);
            Check(leaves.Length == 3 && leaves[0] is SessionCompactionException && ReferenceEquals(leaves[1], first) && ReferenceEquals(leaves[2], second), "Body or terminal callback original was masked.");
        }
        Check(!f.Session.Snapshot.IsCompacting && Events(f, "compaction_end").Single().GetProperty("errorMessage").GetString()!.StartsWith("Compaction failed: ", StringComparison.Ordinal), "Fault lifecycle did not settle.");
    }
    private sealed class FaultSink(Exception fault) : ISessionOperationEventSink
    {
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token)
            => observation is SessionCompactionEnded ? ValueTask.FromException(fault) : ValueTask.CompletedTask;
    }
    private static async Task HeldMultiFault()
    {
        foreach (var terminal in new[] { false, true })
        foreach (var style in new[] { "set-exception", "when-all", "cancelled" })
        {
            await using var f = await Fixture.Create(); f.Summary.Fail = terminal;
            var first = new IOException("one-sink-first"); var second = new OperationCanceledException("one-sink-faulted-OCE");
            var sink = new HeldFaultSink(terminal, style); using var lease = f.Session.SubscribeOperationEvents(sink);
            var before = f.Session.Snapshot.Log.Entries.Length;
            var original = f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary);
            Exception? outcome = null;
            try
            {
                await sink.Entered.Task.WaitAsync(Bound);
                Check(!original.IsCompleted && !sink.Original.IsCompleted, "One-sink original was not held.");
                if (!terminal) Check(Events(f, "compaction_end").Length == 0 && f.Summary.Calls == 0, "Start fault was abandoned before terminal/inference.");
            }
            finally
            {
                sink.Release(first, second);
                try { await original; } catch (Exception error) { outcome = error; }
            }
            var leaves = OriginalCauses(outcome);
            var offset = terminal ? 1 : 0;
            if (terminal) Check(leaves.Length > 0 && leaves[0] is SessionCompactionException, "Terminal callback replaced original body failure.");
            if (style == "cancelled")
                Check(sink.Original.IsCanceled && sink.Original.Exception is null && leaves.Length == offset + 1 && leaves[offset] is OperationCanceledException,
                    "Actually cancelled observer was misclassified or lost.");
            else
            {
                var expected = sink.Original.Exception?.Flatten().InnerExceptions.ToArray() ?? [];
                var propagated = leaves.Skip(offset).ToArray();
                string Inventory(Exception[] errors) => string.Join(",", errors.Take(8).Select(error =>
                    (ReferenceEquals(error, first) ? "first" : ReferenceEquals(error, second) ? "second" : "other") +
                    ":" + error.GetType().Name));
                Check(sink.Original.IsFaulted && expected.Length == 2 &&
                    expected.Count(error => ReferenceEquals(error, first)) == 1 &&
                    expected.Count(error => ReferenceEquals(error, second)) == 1 &&
                    propagated.Length == expected.Length &&
                    propagated.Zip(expected).All(pair => ReferenceEquals(pair.First, pair.Second)),
                    "One joined sink's complete original fault inventory or faulted OCE was lost. " +
                    $"terminal={terminal};style={style};status={sink.Original.Status};expectedCount={expected.Length};" +
                    $"observedCount={propagated.Length};expected=[{Inventory(expected)}];observed=[{Inventory(propagated)}]");
            }
            var end = Events(f, "compaction_end").Single();
            Check(!end.GetProperty("aborted").GetBoolean() && !end.GetProperty("willRetry").GetBoolean() && !end.TryGetProperty("result", out _) &&
                end.TryGetProperty("errorMessage", out _) && sink.Terminals == 1 && sink.JoinedBeforeTerminal &&
                f.Session.Snapshot.Log.Entries.Length == before && !f.Session.Snapshot.IsCompacting,
                "Observer cancellation/fault fabricated session abort or failed original settlement.");
        }
    }
    private sealed class HeldFaultSink : ISessionOperationEventSink
    {
        private readonly bool terminal; private readonly string style;
        private readonly TaskCompletionSource one = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource two = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Original { get; }
        internal int Terminals; internal bool JoinedBeforeTerminal = true;
        internal HeldFaultSink(bool terminal, string style)
        { this.terminal = terminal; this.style = style; Original = style == "when-all" ? Task.WhenAll(one.Task, two.Task) : one.Task; }
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token)
        {
            if (observation is SessionCompactionEnded) { Terminals++; if (!terminal) JoinedBeforeTerminal = Original.IsCompleted; }
            if (terminal ? observation is SessionCompactionEnded : observation is SessionCompactionStarted)
            { Entered.TrySetResult(); return new ValueTask(Original); }
            return ValueTask.CompletedTask;
        }
        internal void Release(Exception first, Exception second)
        {
            if (style == "cancelled") one.TrySetCanceled();
            else if (style == "when-all") { one.TrySetException(first); two.TrySetException(second); }
            else one.TrySetException(new[] { first, second });
        }
    }
    private static async Task HeldLoneFaultedOce()
    {
        foreach (var terminal in new[] { false, true })
        foreach (var canceled in new[] { false, true })
        {
            await using var f = await Fixture.Create();
            var fault = new OperationCanceledException("lone-observer-fault-with-no-owned-token-cancellation");
            var sink = new HeldLoneSink(terminal); using var lease = f.Session.SubscribeOperationEvents(sink);
            var before = f.Session.Snapshot.Log.Entries.Length;
            var original = f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary);
            Exception? outcome = null;
            try
            {
                await sink.Entered.Task.WaitAsync(Bound);
                Check(!sink.Original.IsCompleted && !original.IsCompleted, "Lone observer original was abandoned.");
                if (!terminal) Check(Events(f, "compaction_end").Length == 0 && f.Summary.Calls == 0, "Lone start observer was not joined before terminal/inference.");
            }
            finally
            {
                sink.Release(fault, canceled);
                try { await original; } catch (Exception error) { outcome = error; }
            }
            if (canceled)
                Check(sink.Original.IsCanceled && sink.Original.Exception is null && original.IsCanceled && outcome is OperationCanceledException,
                    "Actually canceled observer lost original cancellation state.");
            else
            {
                Check(sink.Original.IsFaulted && !sink.Original.IsCanceled && original.IsFaulted && !original.IsCanceled && outcome is not OperationCanceledException,
                    "A lone faulted observer OCE became a canceled emitter/session task.");
                Check(outcome?.InnerException is AggregateException inventory && inventory.Flatten().InnerExceptions.Count == 1 &&
                    ReferenceEquals(inventory.Flatten().InnerExceptions[0], fault), "Ordinary observer-failure wrapper lost the exact original fault inventory/reference.");
            }
            var end = Events(f, "compaction_end").Single();
            Check(!end.GetProperty("aborted").GetBoolean() && !end.GetProperty("willRetry").GetBoolean() &&
                end.TryGetProperty("result", out _) == terminal && f.Session.Snapshot.Log.Entries.Length == before + (terminal ? 1 : 0) &&
                f.Session.Snapshot.Fault is null && !f.Session.Snapshot.IsCompacting && sink.Terminals == 1 && sink.JoinedBeforeTerminal,
                "Observer task provenance fabricated an owned-source abort or hid an acknowledged checkpoint.");
        }
    }
    private sealed class HeldLoneSink(bool terminal) : ISessionOperationEventSink
    {
        private readonly TaskCompletionSource work = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Original => work.Task;
        internal int Terminals; internal bool JoinedBeforeTerminal = true;
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token)
        {
            if (observation is SessionCompactionEnded) { Terminals++; if (!terminal) JoinedBeforeTerminal = Original.IsCompleted; }
            if (terminal ? observation is SessionCompactionEnded : observation is SessionCompactionStarted)
            { Entered.TrySetResult(); return new ValueTask(Original); }
            return ValueTask.CompletedTask;
        }
        internal void Release(Exception fault, bool canceled)
        { if (canceled) work.TrySetCanceled(); else work.TrySetException(fault); }
    }
    private static async Task SynchronousObserverOce()
    {
        foreach (var terminal in new[] { false, true })
        {
            await using var f = await Fixture.Create(); f.Summary.Hold = terminal;
            var fault = new OperationCanceledException("synchronous-observer-with-no-owned-cancellation", CancellationToken.None);
            var sink = new SynchronousOceSink(terminal, fault); using var lease = f.Session.SubscribeOperationEvents(sink);
            var before = f.Session.Snapshot.Log.Entries.Length;
            var original = f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary, CancellationToken.None);
            Exception? outcome = null;
            try
            {
                if (terminal)
                {
                    await f.Summary.Entered.Task.WaitAsync(Bound);
                    Check(!original.IsCompleted && Events(f, "compaction_end").Length == 0, "Terminal synchronous observer was reached before original generator joined.");
                }
                else Check(original.IsCompleted && f.Summary.Calls == 0, "Synchronous start fault did not settle before inference.");
            }
            finally
            {
                f.Summary.Release.TrySetResult();
                try { await original.WaitAsync(Bound); } catch (Exception error) { outcome = error; }
                // The bounded wait cannot substitute for joining the original when an assertion/deadline fails.
                if (!original.IsCompleted) try { await original; } catch (Exception error) { outcome = error; }
            }
            Check(original.IsFaulted && !original.IsCanceled && outcome is not OperationCanceledException &&
                ReferenceEquals(outcome?.InnerException, fault) && fault.CancellationToken == CancellationToken.None && sink.Throws == 1,
                "Synchronous OCE became canceled or lost its exact original cause.");
            var end = Events(f, "compaction_end").Single();
            Check(!end.GetProperty("aborted").GetBoolean() && !end.GetProperty("willRetry").GetBoolean() &&
                end.TryGetProperty("result", out _) == terminal && f.Session.Snapshot.Log.Entries.Length == before + (terminal ? 1 : 0) &&
                f.Session.Snapshot.Fault is null && !f.Session.Snapshot.IsCompacting && sink.Terminals == 1 &&
                f.Summary.Active == 0 && (!terminal || f.Summary.Joined.Task.IsCompletedSuccessfully),
                "Synchronous observer OCE fabricated owned cancellation or hid acknowledged settlement.");
        }
        // Preserve the actual-cancelled-task path independently of synchronous OCE provenance.
        await using var canceledFixture = await Fixture.Create();
        var canceledSink = new HeldLoneSink(false); using var canceledLease = canceledFixture.Session.SubscribeOperationEvents(canceledSink);
        var canceledOriginal = canceledFixture.Session.CompactAsync(canceledFixture.Session.Snapshot.Log.Header.Id, new(), canceledFixture.Summary);
        Exception? canceledOutcome = null;
        try { await canceledSink.Entered.Task.WaitAsync(Bound); Check(!canceledOriginal.IsCompleted, "Actual canceled-task control was not held."); }
        finally
        {
            canceledSink.Release(new IOException("unused canceled control"), true);
            try { await canceledOriginal; } catch (Exception error) { canceledOutcome = error; }
        }
        Check(canceledSink.Original.IsCanceled && canceledOriginal.IsCanceled && canceledOutcome is OperationCanceledException &&
            !Events(canceledFixture, "compaction_end").Single().GetProperty("aborted").GetBoolean(),
            "Actually canceled observer task was converted to faulted or fabricated session cancellation.");
    }
    private sealed class SynchronousOceSink(bool terminal, Exception fault) : ISessionOperationEventSink
    {
        internal int Throws, Terminals;
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token)
        {
            Check(token == CancellationToken.None, "Observer received an owned cancellation token.");
            if (observation is SessionCompactionEnded) Terminals++;
            if (terminal ? observation is SessionCompactionEnded : observation is SessionCompactionStarted)
            { Throws++; throw fault; }
            return ValueTask.CompletedTask;
        }
    }
    private static async Task ObserverProvenance()
    {
        foreach (var terminal in new[] { false, true })
        foreach (var returnedTask in new[] { false, true })
        foreach (var shape in new[] { "nested", "duplicate", "empty" })
        {
            await using var f = await Fixture.Create(); f.Summary.Hold = terminal && !returnedTask;
            var oce = new OperationCanceledException("original-aggregate-OCE", CancellationToken.None);
            var inventory = shape switch
            {
                "nested" => new AggregateException(new AggregateException(oce)),
                "duplicate" => new AggregateException(oce, oce),
                _ => new AggregateException()
            };
            var sink = new InventorySink(terminal, returnedTask, inventory); using var lease = f.Session.SubscribeOperationEvents(sink);
            var before = f.Session.Snapshot.Log.Entries.Length;
            var original = f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary);
            Exception? outcome = null;
            try
            {
                if (returnedTask)
                {
                    await sink.Entered.Task.WaitAsync(Bound);
                    Check(!sink.Original!.IsCompleted && !original.IsCompleted, "Returned inventory original was abandoned.");
                    if (!terminal) Check(Events(f, "compaction_end").Length == 0 && f.Summary.Calls == 0, "Fault inventory was flattened before original start joined.");
                }
                else if (terminal)
                {
                    await f.Summary.Entered.Task.WaitAsync(Bound);
                    Check(!original.IsCompleted && Events(f, "compaction_end").Length == 0, "Synchronous terminal inventory ran before generator join.");
                }
            }
            finally
            {
                f.Summary.Release.TrySetResult(); sink.Release();
                try { await original; } catch (Exception error) { outcome = error; }
            }
            Check(original.IsFaulted && !original.IsCanceled && outcome is not null && outcome is not OperationCanceledException,
                "Nested/duplicate/empty original observer fault disappeared or became cancellation.");
            if (returnedTask)
                Check(sink.Original is { IsFaulted: true, IsCanceled: false } && outcome!.InnerException is AggregateException taskInventory &&
                    taskInventory.InnerExceptions.Count == 1 && ReferenceEquals(taskInventory.InnerExceptions[0], inventory),
                    "Complete returned task inventory did not retain the exact source aggregate.");
            else Check(ReferenceEquals(outcome!.InnerException, inventory), "Synchronous source aggregate reference was replaced.");
            var causes = OriginalCauses(outcome);
            if (shape == "empty") Check(causes.Length == 1 && ReferenceEquals(causes[0], inventory), "Empty original aggregate was erased.");
            else Check(causes.Length == (shape == "duplicate" ? 2 : 1) && causes.All(cause => ReferenceEquals(cause, oce)),
                "Nested original cause or duplicate identity/multiplicity was erased.");
            var end = Events(f, "compaction_end").Single();
            Check(!end.GetProperty("aborted").GetBoolean() && !end.GetProperty("willRetry").GetBoolean() &&
                end.TryGetProperty("result", out _) == terminal && f.Session.Snapshot.Log.Entries.Length == before + (terminal ? 1 : 0) &&
                f.Session.Snapshot.Fault is null && !f.Session.Snapshot.IsCompacting && sink.Terminals == 1 && sink.JoinedBeforeTerminal,
                "Observer inventory fabricated owned abort or hid acknowledged original settlement.");
        }
        await using var canceledFixture = await Fixture.Create();
        var canceledSink = new HeldLoneSink(false); using var canceledLease = canceledFixture.Session.SubscribeOperationEvents(canceledSink);
        var canceledOriginal = canceledFixture.Session.CompactAsync(canceledFixture.Session.Snapshot.Log.Header.Id, new(), canceledFixture.Summary);
        Exception? canceledOutcome = null;
        try { await canceledSink.Entered.Task.WaitAsync(Bound); Check(!canceledOriginal.IsCompleted, "Actual cancellation control was not held."); }
        finally
        {
            canceledSink.Release(new IOException("unused cancellation control"), true);
            try { await canceledOriginal; } catch (Exception error) { canceledOutcome = error; }
        }
        Check(canceledSink.Original.IsCanceled && canceledOriginal.IsCanceled && canceledOutcome is OperationCanceledException &&
            !Events(canceledFixture, "compaction_end").Single().GetProperty("aborted").GetBoolean(), "Actual task cancellation provenance was lost.");
    }
    private sealed class InventorySink(bool terminal, bool returnedTask, Exception inventory) : ISessionOperationEventSink
    {
        private readonly TaskCompletionSource work = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Original => returnedTask ? work.Task : null;
        internal int Terminals; internal bool JoinedBeforeTerminal = true;
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token)
        {
            Check(token == CancellationToken.None, "Observer acquired an owned cancellation token.");
            if (observation is SessionCompactionEnded) { Terminals++; if (!terminal && returnedTask) JoinedBeforeTerminal = work.Task.IsCompleted; }
            if (terminal ? observation is SessionCompactionEnded : observation is SessionCompactionStarted)
            {
                Entered.TrySetResult();
                if (returnedTask) return new ValueTask(work.Task);
                throw inventory;
            }
            return ValueTask.CompletedTask;
        }
        internal void Release() { if (returnedTask) work.TrySetException(inventory); }
    }
    private static async Task Reentrant()
    {
        await using var f = await Fixture.Create(); var guarded = 0;
        f.FrameObserver = frame =>
        {
            if (frame.GetProperty("type").GetString() is not ("compaction_start" or "compaction_end")) return;
            try { _ = f.Dispatcher.WaitForIdleAsync(); throw new IOException("RPC callback self-wait admitted."); }
            catch (InvalidOperationException) { guarded++; }
        };
        using var lease = f.Session.SubscribeOperationEvents(new AbortAtStart(f.Session));
        try { await f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(), f.Summary); throw new IOException("Reentrant abort did not cancel."); }
        catch (OperationCanceledException) { }
        Check(guarded == 2 && f.Summary.Calls == 0 && Events(f, "compaction_end").Single().GetProperty("aborted").GetBoolean(), "Reentrant abort/self-wait ownership differs.");
    }
    private sealed class AbortAtStart(PersistentAgentSession session) : ISessionOperationEventSink
    {
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token)
        {
            if (observation is SessionCompactionStarted)
            {
                try { _ = session.WaitForIdleAsync(); throw new IOException("Session callback self-wait admitted."); }
                catch (InvalidOperationException) { }
                session.Abort();
            }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PiSharp-original-compaction-lifecycle-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal readonly Summary Summary = new(); internal readonly Transport Transport = new(); internal readonly StorageFactory Storage = new();
        internal readonly List<SessionCompactionObservation> Observed = []; internal readonly List<bool> StartActive = [], EndIdle = [];
        internal Action<JsonElement>? FrameObserver;
        private readonly Capture output = new(); private SessionRuntimeRegistry registry = null!; private int ids;
        internal ReplaceableAgentSession Owner = null!; internal RpcSessionDispatcher Dispatcher = null!; internal PersistentAgentSession Session => Owner.Current.Session;
        internal JsonElement[] Records => Encoding.UTF8.GetString(output.Bytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonData.Parse(line).Value).ToArray();
        private async Task<PersistentAgentSession> Open(string path)
        {
            var session = await PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 123, () => "alias-" + Interlocked.Increment(ref ids),
                new(SessionLogStoreOptions: new(StorageFactory: Storage)), fallbackModel: Model);
            session.ConfigureCompactionObservation(value => { Check(session.Snapshot.Log.ById.ContainsKey(value.CompactionEntry.Id), "Native observation preceded checkpoint."); Observed.Add(value); return ValueTask.CompletedTask; });
            return session;
        }
        internal static async Task<Fixture> Create(bool compactable = true, bool hasGenerator = true, RpcDispatchOptions? options = null)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root); f.registry = new([new(Model, f.Transport)], [], new DenyPolicy());
            foreach (var path in new[] { f.Source, f.Other })
            {
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-01T00:00:00.000Z", cwd = f.Root }));
                await using var store = await SessionLogStore.CreateNewAsync(path, header);
                await store.AppendAsync(path == f.Source && compactable ? [User("old", null, new string('o', 90000)), User("keep", "old", new string('k', 90000)), User("recent", "keep", "four")] : [User("u", null, "four")]);
            }
            var session = await f.Open(f.Source); f.Owner = new(session, (request, _) => f.Open(request.Path));
            f.output.BeforeWrite = record => { if (record.GetProperty("type").GetString() == "compaction_start") f.StartActive.Add(f.Session.Snapshot.IsCompacting);
                if (record.GetProperty("type").GetString() == "compaction_end") f.EndIdle.Add(!f.Session.Snapshot.IsCompacting); f.FrameObserver?.Invoke(record); };
            var wire = JsonData.Parse(JsonSerializer.Serialize(new { id = Model.Id, api = Model.Api, provider = Model.Provider, name = Model.Id, baseUrl = "https://offline.invalid", reasoning = false,
                input = new[] { "text" }, contextWindow = 32768, maxTokens = 16384, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } }));
            try { f.Dispatcher = new(session, new JsonlWriter(f.output, ownership: JsonlStreamOwnership.Borrowed), () => 123, [new(Model, wire)], options,
                RpcSessionOwnership.Borrowed, sessionOwner: f.Owner, summaryGenerator: hasGenerator ? f.Summary : null); return f; }
            catch { await f.Owner.DisposeAsync(); f.output.Dispose(); throw; }
        }
        internal async Task<JsonElement> Send(object command)
        {
            var raw = JsonData.Parse(JsonSerializer.Serialize(command)); await Dispatcher.SubmitAsync(raw); var id = raw.Value.GetProperty("id").GetString();
            return Records.Single(value => value.GetProperty("type").GetString() == "response" && value.TryGetProperty("id", out var identity) && identity.GetString() == id);
        }
        internal async Task JoinRun()
        {
            using var deadline = new CancellationTokenSource(Bound);
            for (var attempt = 0; ; attempt++) { deadline.Token.ThrowIfCancellationRequested(); var state = await Send(new { id = "join-" + attempt, type = "get_state" }); Success(state);
                if (state.GetProperty("data").GetProperty("pisharpRunOwnerSettled").GetBoolean()) return; await Task.Delay(1, deadline.Token); }
        }
        public async ValueTask DisposeAsync()
        {
            Summary.Release.TrySetResult(); Storage.Release.TrySetResult(); Transport.Release.TrySetResult();
            try { await Dispatcher.DisposeAsync(); } finally { await Owner.DisposeAsync(); output.Dispose(); }
            Check(Summary.Active == 0 && Transport.Active == 0, "Original offline summary/turn did not join.");
            // Retain bounded authored durable fixtures; no recursive cleanup or detached original task.
        }
    }
    private sealed class Capture : MemoryStream
    {
        private readonly object gate = new(); internal Action<JsonElement>? BeforeWrite; internal byte[] Bytes() { lock (gate) return ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); BeforeWrite?.Invoke(JsonData.Parse(Encoding.UTF8.GetString(buffer.Span).TrimEnd('\n', '\r')).Value); lock (gate) Write(buffer.Span); return ValueTask.CompletedTask; }
    }
    private sealed class Summary : ISessionSummaryGenerator
    {
        internal bool Hold, Fail; internal string Text = "offline summary"; internal int Calls, Active; internal SessionSummaryRequest? LastRequest;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        {
            Calls++; Active++; LastRequest = request;
            try { Check(request.Kind == SessionSummaryKind.History && request.Model == Model, "Unexpected native summary request.");
                if (Hold) { using var cancellation = token.UnsafeRegister(_ => Canceled.TrySetResult(), null); Entered.TrySetResult(); await Release.Task; }
                token.ThrowIfCancellationRequested(); if (Fail) throw new IOException("actual-generator-failure"); return new(Text, new(4, 3, 0, 0, 7, new(0, 0, 0, 0, 0))); }
            finally { Active--; Joined.TrySetResult(); }
        }
    }
    private sealed class Transport : IChatTransport
    {
        internal bool OverflowFirst, LowUsage; internal int Calls, Active; internal CancellationToken LastToken; internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Calls++; Active++; LastToken = token; var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [], new(LowUsage || Calls > 1 ? 4 : 40000, 0, 0, 0, LowUsage || Calls > 1 ? 4 : 40000, new(0, 0, 0, 0, 0)), OverflowFirst && Calls == 1 ? StopReason.Error : StopReason.Stop, OverflowFirst && Calls == 1 ? JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"prompt is too long\"")) : null);
            try { await Task.Yield(); yield return new StreamStarted(final with { StopReason = StopReason.Pending }); Entered.TrySetResult(); token.ThrowIfCancellationRequested();
                if (final.StopReason == StopReason.Error) yield return new StreamError(final.StopReason, final); else yield return new StreamDone(final.StopReason, final); }
            finally { Active--; Joined.TrySetResult(); }
        }
    }
    private sealed class DenyPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        internal bool Hold; internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), this);
        private sealed class Storage(ISessionLogStorage inner, StorageFactory owner) : ISessionLogStorage
        {
            public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
            public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength); public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
            public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
            public async ValueTask BeforeCheckpointAsync() { try { if (owner.Hold) { owner.Entered.TrySetResult(); await owner.Release.Task; } await inner.BeforeCheckpointAsync(); } finally { owner.Joined.TrySetResult(); } }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
