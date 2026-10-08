using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class FocusOwnershipTests
{
    internal static async Task<object> Run(JsonElement source, Action<object>? observeFailure = null)
    {
        var rows = new List<object>(); var controls = new List<object>(); var joined = new List<object>();
        var failed = 0; var owned = new List<Harness>(); Exception? primaryFailure = null; var context = "source-and-focus-controls";
        try
        {
        foreach (var acknowledged in new[] { false, true })
        foreach (var schedule in source.GetProperty("cases").EnumerateArray())
        foreach (var editorName in new[] { "first", "second" })
        {
            var owner = new TerminalEditorFocusOwner(); var host = await Harness.Start(owned, acknowledged, owner); var index = 0;
            Exception? scenarioFailure = null;
            try
            {
                foreach (var step in schedule.GetProperty("steps").EnumerateArray())
                {
                    var op = step.GetProperty("operation"); var kind = op.GetProperty("kind").GetString();
                    if (kind == "focus") await owner.SetEditorFocusAsync(op.GetProperty("target").GetString() == editorName).WaitAsync(TimeSpan.FromSeconds(5));
                    else if (kind == "recreate" && op.GetProperty("target").GetString() == editorName)
                    { var pastChanges = host.Changes.ToArray(); var pastSubmits = host.Submits.ToArray();
                        joined.Add(await host.Close()); host = await Harness.Start(owned, acknowledged, owner);
                        host.Changes.AddRange(pastChanges); host.Submits.AddRange(pastSubmits); await owner.SetEditorFocusAsync(false); }
                    else if (kind == "input")
                    {
                        await host.Sink.Send(op.GetProperty("raw").GetString()!);
                        // Same-value focus is a serialized observation barrier after physical admission;
                        // it changes no Source state and allows inactive/protocol events to be observed.
                        await owner.SetEditorFocusAsync(step.GetProperty("editors").GetProperty(editorName).GetProperty("focused").GetBoolean());
                    }
                    var expected = step.GetProperty("editors").GetProperty(editorName);
                    var text = expected.GetProperty("text").GetString()!; var cur = expected.GetProperty("cursor");
                    var cursor = cur.GetProperty("col").GetInt32() + text.Split('\n').Take(cur.GetProperty("line").GetInt32()).Sum(l => l.Length + 1);
                    var expectedRows = expected.GetProperty("rows").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    var focused = expected.GetProperty("focused").GetBoolean(); var snapshot = host.Last!; var frame = host.Frame!;
                    var expectedChanges = expected.GetProperty("changes").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    var expectedSubmits = expected.GetProperty("submits").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    var positions = Position(frame.Frame);
                    var pass = snapshot.Text == text && snapshot.CursorUtf16Offset == cursor && snapshot.EditorOwnsFocus == focused &&
                        frame.SourceComponent.Rows.SequenceEqual(expectedRows) && positions == focused && !frame.Frame.Cursor.Visible &&
                        host.Submits.SequenceEqual(expectedSubmits) && host.Changes.SequenceEqual(expectedChanges) && snapshot.Text == expected.GetProperty("expandedText").GetString() && snapshot.EditorFocus is { } applied && owner.IsCurrent(applied) &&
                        snapshot.Layout!.Identity.EditorLifetimeId == applied.EditorLifetimeId;
                    if (!pass) failed++;
                    rows.Add(new { id = schedule.GetProperty("id").GetString(), editorName, step = index++, route = acknowledged ? "acknowledged" : "legacy",
                        operation = op.Clone(), expectedText = text, actualText = snapshot.Text, expectedCursor = cursor, actualCursor = snapshot.CursorUtf16Offset,
                        expectedFocus = focused, actualFocus = snapshot.EditorOwnsFocus, expectedRows, actualRows = frame.SourceComponent.Rows,
                        expectedSubmits, actualSubmits = host.Submits.ToArray(), expectedChanges, actualChanges = host.Changes.ToArray(), globalApplicationInterrupts = host.Interrupts,
                        snapshot.EditorFocus, positionCursor = positions, hardwareCursorVisible = frame.Frame.Cursor.Visible, pass });
                }
            }
            catch (Exception error) { scenarioFailure = error; throw; }
            finally { if (scenarioFailure is null) joined.Add(await host.Close()); else await host.Cleanup(scenarioFailure); }
        }
        foreach (var acknowledged in new[] { false, true })
        {
            // Focus-only work wakes while the sole borrowed read deliberately ignores cancellation.
            var owner = new TerminalEditorFocusOwner(); var h = await Harness.Start(owned, acknowledged, owner, nonCooperative: true);
            await owner.SetEditorFocusAsync(false); var awake = h.Last!.EditorOwnsFocus == false && h.Sink.ActiveReads == 1;
            h.Stop.Cancel(); await Task.Delay(35); var retained = !h.Run.IsCompleted;
            var receipt = await h.Close(); var detached = Failure(() => owner.SetEditorFocusAsync(true), TerminalEditorFocusFailure.Detached);
            Add(new { id = "held-read-focus-wakeup-and-joined-cancel", route = Route(acknowledged), awake, retained, detached, receipt, pass = awake && retained && detached });

            // Earlier key admission and its held paint precede later focus; focus ack joins its own paint.
            owner = new(); var paintHeld = NewSignal(); var releasePaint = NewSignal(); var focusPaintHeld = NewSignal(); var releaseFocusPaint = NewSignal();
            h = await Harness.Start(owned, acknowledged, owner, beforePaint: async snapshot =>
            {
                if (snapshot.Text == "a" && snapshot.EditorOwnsFocus) { paintHeld.TrySetResult(); await releasePaint.Task; }
                if (!snapshot.EditorOwnsFocus) { focusPaintHeld.TrySetResult(); await releaseFocusPaint.Task; }
            }, releaseOnClose: [releasePaint, releaseFocusPaint]);
            await h.Sink.Send("a"); await paintHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var change = owner.SetEditorFocusAsync(false); await h.Sink.Send("b");
            var early = !change.IsCompleted; releasePaint.TrySetResult(); await focusPaintHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var beforeAck = !change.IsCompleted; releaseFocusPaint.TrySetResult(); await change;
            await owner.SetEditorFocusAsync(false); var preserved = h.Last!.Text == "a" && !h.Last.EditorOwnsFocus;
            receipt = await h.Close(); Add(new { id = "held-render-causal-key-focus-key", route = Route(acknowledged), early, beforeAck, preserved, receipt, pass = early && beforeAck && preserved });

            // Queued cancellation prevents effects, and admission has eight focus slots independently of32keys.
            owner = new(); var firstPaint = NewSignal(); var releaseFirst = NewSignal();
            h = await Harness.Start(owned, acknowledged, owner, beforePaint: async _ => { firstPaint.TrySetResult(); await releaseFirst.Task; }, releaseOnClose: [releaseFirst]);
            await firstPaint.Task.WaitAsync(TimeSpan.FromSeconds(5)); using var requestStop = new CancellationTokenSource();
            var canceled = owner.SetEditorFocusAsync(false, requestStop.Token); requestStop.Cancel();
            var requests = Enumerable.Range(0, 8).Select(_ => owner.SetEditorFocusAsync(true)).ToArray();
            // The canceled message still occupies a bounded queue slot until consumed.
            var limited = requests.Count(t => t.IsFaulted) == 1 && Failure(() => owner.SetEditorFocusAsync(false).GetAwaiter().GetResult(), TerminalEditorFocusFailure.AdmissionLimit);
            releaseFirst.TrySetResult(); try { await canceled; } catch (OperationCanceledException) { }
            foreach (var request in requests) try { await request; } catch (TerminalEditorFocusException) { }
            var noEffect = h.Last!.EditorOwnsFocus; receipt = await h.Close();
            Add(new { id = "bounded-focus-admission-and-preapply-cancel", route = Route(acknowledged), limited, canceled = canceled.IsCanceled, noEffect, receipt, pass = limited && canceled.IsCanceled && noEffect });

            // Canceling an applied operation does not detach its already admitted paint.
            owner = new(); var appliedPaint = NewSignal(); var releaseApplied = NewSignal();
            h = await Harness.Start(owned, acknowledged, owner, beforePaint: async snapshot => { if (!snapshot.EditorOwnsFocus) { appliedPaint.TrySetResult(); await releaseApplied.Task; } }, releaseOnClose: [releaseApplied]);
            using var appliedStop = new CancellationTokenSource(); change = owner.SetEditorFocusAsync(false, appliedStop.Token);
            await appliedPaint.Task.WaitAsync(TimeSpan.FromSeconds(5)); appliedStop.Cancel();
            var retainedPaint = !change.IsCompleted; releaseApplied.TrySetResult(); await change;
            var appliedFocus = !h.Last!.EditorOwnsFocus; receipt = await h.Close();
            Add(new { id = "postapply-cancel-joins-paint", route = Route(acknowledged), retainedPaint, appliedFocus, receipt, pass = retainedPaint && appliedFocus });

            // Run cancellation joins the already admitted focus paint before faulting its acknowledgement.
            owner = new(); var cancelPaint = NewSignal(); var releaseCanceledPaint = NewSignal();
            h = await Harness.Start(owned, acknowledged, owner, beforePaint: async snapshot => { if (!snapshot.EditorOwnsFocus) { cancelPaint.TrySetResult(); await releaseCanceledPaint.Task; } }, releaseOnClose: [releaseCanceledPaint]);
            change = owner.SetEditorFocusAsync(false); await cancelPaint.Task.WaitAsync(TimeSpan.FromSeconds(5)); h.Stop.Cancel(); await Task.Delay(35);
            var callbackRetained = !change.IsCompleted && !h.Run.IsCompleted; releaseCanceledPaint.TrySetResult();
            var focusCanceled = false; try { await change; } catch (OperationCanceledException) { focusCanceled = true; }
            receipt = await h.Close(); Add(new { id = "run-cancel-joins-focus-paint", route = Route(acknowledged), callbackRetained, focusCanceled, receipt, pass = callbackRetained && focusCanceled });

            // Tiny-view fallback does not mutate the canonical owned editor or invent cursor authority.
            owner = new(); h = await Harness.Start(owned, acknowledged, owner); await h.Sink.Send("\U0001f642"); await owner.SetEditorFocusAsync(true);
            var wideSnapshot = h.Last!; h.Sink.Columns = 1; await h.View.RefreshViewportAsync(CancellationToken.None);
            await owner.SetEditorFocusAsync(false); var fallbackPreserved = h.Last!.Text == wideSnapshot.Text && h.Last.CursorUtf16Offset == wideSnapshot.CursorUtf16Offset;
            h.Sink.Columns = 20; await h.View.RefreshViewportAsync(CancellationToken.None);
            var anchorWithheld = !Position(h.Frame!.Frame) && !h.Frame.Frame.Cursor.Visible;
            receipt = await h.Close(); Add(new { id = "tiny-window-unowned-fallback-resize", route = Route(acknowledged), fallbackPreserved, anchorWithheld, receipt, pass = fallbackPreserved && anchorWithheld });

            // Stale snapshots, mismatched layout lifetimes and old attachments cannot publish a replacement.
            owner = new(); h = await Harness.Start(owned, acknowledged, owner); var old = h.Last!;
            await h.Sink.Send("n"); await Until(() => h.Last!.Text == "n"); await h.View.SetDraftAsync(h.Last!, CancellationToken.None);
            var olderLayoutRejected = await Stale(() => h.View.SetDraftAsync(old, CancellationToken.None));
            await owner.SetEditorFocusAsync(false); var staleRejected = await Stale(() => h.View.SetDraftAsync(old, CancellationToken.None));
            var layoutOwner = new TerminalTextEditorPasteController(); var wrong = h.Last! with { Layout = layoutOwner.CaptureLayoutInput() };
            wrong = wrong with { Text = wrong.Layout!.Snapshot.Text, CursorUtf16Offset = wrong.Layout.Snapshot.CursorUtf16Offset };
            var layoutRejected = await Stale(() => h.View.SetDraftAsync(wrong, CancellationToken.None));
            receipt = await h.Close(); var next = await Harness.Start(owned, acknowledged, owner);
            var recreationInactive = !next.Last!.EditorOwnsFocus && next.Last.Text == "";
            var replacedRejected = await Stale(() => next.View.SetDraftAsync(old, CancellationToken.None));
            var nextReceipt = await next.Close(); Add(new { id = "stale-owner-layout-recreation", route = Route(acknowledged), staleRejected, layoutRejected, recreationInactive, replacedRejected,
                receipt, nextReceipt, olderLayoutRejected, pass = staleRejected && layoutRejected && recreationInactive && replacedRejected && olderLayoutRejected });

            // The original32decoded-key budget remains independent of focus admission.
            owner = new(); var boundedPaint = NewSignal(); var releaseBounded = NewSignal();
            h = await Harness.Start(owned, acknowledged, owner, beforePaint: async _ => { boundedPaint.TrySetResult(); await releaseBounded.Task; }, releaseOnClose: [releaseBounded]);
            Exception? boundedFailure = null;
            try
            {
                await boundedPaint.Task; change = owner.SetEditorFocusAsync(false); h.Sink.Queue(string.Concat(Enumerable.Repeat("\u001b[A", 33)));
                await h.PaintCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var overloadRetained = h.Sink.ActiveReads == 0 && !h.Run.IsCompleted && !change.IsCompleted;
                releaseBounded.TrySetResult(); receipt = await h.Close(typeof(InvalidOperationException));
                var overloadDetached = false; try { await change; } catch (TerminalEditorFocusException error) { overloadDetached = error.Failure == TerminalEditorFocusFailure.Detached; }
                Add(new { id = "original32key-budget-with-pending-focus", route = Route(acknowledged), overloadRetained, overloadDetached, receipt, pass = overloadRetained && overloadDetached });
            }
            catch (Exception error) { boundedFailure = error; throw; }
            finally { releaseBounded.TrySetResult(); if (boundedFailure is null) await h.Close(); else await h.Cleanup(boundedFailure); }

            // Existing global interrupts survive null component ownership, including accepted MOK filtering.
            owner = new(); h = await Harness.Start(owned, acknowledged, owner); await owner.SetEditorFocusAsync(false);
            foreach (var raw in new[] { "\u0003", "\u001b[99;69u", "\u001b[27;69;99~", "\u001b[27;5;99~", "\r", "\u001b", "\u0004", "\u001b[200~hidden\u001b[201~" })
            { await h.Sink.Send(raw); await owner.SetEditorFocusAsync(false); }
            var inactive = h.Last!.Text == "" && h.Submits.Count == 0 && h.Interrupts == 3;
            receipt = await h.Close(); Add(new { id = "inactive-editor-bindings-global-interrupts", route = Route(acknowledged), h.Interrupts, submits = h.Submits.ToArray(), inactive, receipt, pass = inactive });

            // Pending jump, registered paste, undo and accepted history survive loss/reacquire.
            owner = new(); h = await Harness.Start(owned, acknowledged, owner);
            await h.Sink.Send("\u001b[200~" + new string('z', 1001) + "\u001b[201~"); await owner.SetEditorFocusAsync(true);
            var atom = h.Last!; await owner.SetEditorFocusAsync(false); await h.Sink.Send("ignored"); await owner.SetEditorFocusAsync(true);
            var atomPreserved = h.Last!.Text == atom.Text && h.Last.Layout!.AtomicRanges.SequenceEqual(atom.Layout!.AtomicRanges);
            await h.Sink.Send("q"); await owner.SetEditorFocusAsync(false); await owner.SetEditorFocusAsync(true);
            await h.Sink.Send("\u001f"); await owner.SetEditorFocusAsync(true);
            var undoPreserved = h.Last!.Text == atom.Text;
            receipt = await h.Close(); Add(new { id = "registered-atom-and-undo-preserved", route = Route(acknowledged), atomPreserved, undoPreserved, atomCount = atom.Layout!.AtomicRanges.Length,
                receipt, pass = atomPreserved && undoPreserved && atom.Layout.AtomicRanges.Length == 1 });
        }
        // Only cancellation caused by the exact producer failure may yield to that failure.
        foreach (var cancellation in new[] { "internal", "caller", "foreign", "read-failure", "read-caller", "read-foreign", "read-earlier-failure" })
        {
            context = cancellation;
            var held = NewSignal(); var release = NewSignal(); using var foreign = new CancellationTokenSource();
            foreign.Cancel(); var foreignError = new OperationCanceledException(foreign.Token);
            var readFailure = new IOException("distinct physical read failure");
            var earlierFailure = new InvalidDataException("distinct earlier render failure");
            var h = await Harness.Start(owned, true, new(), beforePaint: async _ =>
            {
                held.TrySetResult(); await release.Task;
                if (cancellation is "foreign" or "read-foreign") throw foreignError;
                if (cancellation == "read-earlier-failure") throw earlierFailure;
            }, releaseOnClose: [release]);
            Exception? precedenceFailure = null;
            try
            {
                await held.Task;
                if (cancellation.StartsWith("read-", StringComparison.Ordinal)) h.Sink.QueueFault(readFailure);
                else h.Sink.Queue(string.Concat(Enumerable.Repeat("\u001b[A", 33)));
                await h.PaintCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (cancellation is "caller" or "read-caller") h.Stop.Cancel();
                var retained = !h.Run.IsCompleted; release.TrySetResult();
                var expectedFailure = cancellation switch
                {
                    "internal" => typeof(InvalidOperationException), "read-failure" => typeof(IOException),
                    "read-earlier-failure" => typeof(InvalidDataException), _ => typeof(OperationCanceledException)
                };
                var receipt = await h.Close(expectedFailure, expectedCancellation: expectedFailure == typeof(OperationCanceledException)
                    ? cancellation is "foreign" or "read-foreign" ? foreign.Token : h.PaintToken : null,
                    expectedContributor: expectedFailure == typeof(OperationCanceledException)
                    ? cancellation is "foreign" or "read-foreign" ? foreign.Token : h.CallerToken : null);
                var exact = cancellation switch
                {
                    "foreign" or "read-foreign" => ReferenceEquals(h.Failure, foreignError),
                    "read-failure" => ReferenceEquals(h.Failure, readFailure),
                    "read-earlier-failure" => ReferenceEquals(h.Failure, earlierFailure), _ => true
                };
                var physicalFailureRetained = !cancellation.StartsWith("read-", StringComparison.Ordinal) || ReferenceEquals(h.Sink.ReadFailure, readFailure);
                var causes = h.Causes;
                var producer = cancellation.StartsWith("read-", StringComparison.Ordinal) ? readFailure :
                    causes?.Primary is InvalidOperationException ? causes.Primary : causes?.Secondary.FirstOrDefault(error => error is InvalidOperationException);
                var producerRetained = producer is not null && causes is not null && ReferenceEquals(causes.Primary, h.Failure) &&
                    (ReferenceEquals(causes.Primary, producer) || causes.Secondary.Any(error => ReferenceEquals(error, producer)));
                Add(new { id = "acknowledged-producer-cancellation-precedence", cancellation, retained, exact, physicalFailureRetained, producerRetained, receipt,
                    pass = retained && exact && physicalFailureRetained && producerRetained });
            }
            catch (Exception error) { precedenceFailure = error; throw; }
            finally { release.TrySetResult(); if (precedenceFailure is null) await h.Close(); else await h.Cleanup(precedenceFailure); }
        }
        context = "custom-cancellation-proof-controls";
        using (var stopped = new CancellationTokenSource())
        using (var foreignStop = new CancellationTokenSource())
        using (var live = new CancellationTokenSource())
        {
            stopped.Cancel(); foreignStop.Cancel();
            var cause = new CustomCancellation(stopped.Token); var foreignCause = new CustomCancellation(foreignStop.Token);
            var liveCause = new CustomCancellation(live.Token);
            var original = Task.FromCanceled(stopped.Token);
            var accepted = ProvenCancellation(cause, original, cause, stopped.Token, stopped.Token);
            var wrongTokenRejected = !ProvenCancellation(foreignCause, original, foreignCause, stopped.Token, stopped.Token);
            var liveOwnerRejected = !ProvenCancellation(cause, original, cause, stopped.Token, live.Token);
            var foreignPrimaryRejected = !ProvenCancellation(cause, original, foreignCause, stopped.Token, stopped.Token);
            var unrequestedWaitRejected = !ProvenCancellation(liveCause, original, liveCause, live.Token, stopped.Token);
            var uncanceledOriginalRejected = !ProvenCancellation(cause, Task.CompletedTask, cause, stopped.Token, stopped.Token);
            var lateContributorRejected = !ProvenCancellation(cause, original, cause, stopped.Token, stopped.Token, contributorRequested: false);
            Add(new { id = context, accepted, wrongTokenRejected, liveOwnerRejected, foreignPrimaryRejected, unrequestedWaitRejected, uncanceledOriginalRejected, lateContributorRejected,
                pass = accepted && wrongTokenRejected && liveOwnerRejected && foreignPrimaryRejected && unrequestedWaitRejected && uncanceledOriginalRejected && lateContributorRejected });
        }
        // A diagnostic timeout must release the held callback and join Run before disposing its view.
        foreach (var acknowledged in new[] { false, true })
        {
            context = "close-timeout-directly-joins-original-run/" + Route(acknowledged);
            var release = NewSignal(); var callbackJoined = false;
            var h = await Harness.Start(owned, acknowledged, new(), beforePaint: async _ => { await release.Task; callbackJoined = true; }, releaseOnClose: [release]);
            var receipt = await h.Close(typeof(TimeoutException), TimeSpan.Zero);
            Add(new { id = "close-timeout-directly-joins-original-run", route = Route(acknowledged), callbackJoined, receipt,
                pass = callbackJoined && h.Run.IsCompleted });
        }
        // A held submission and accepted receipt are owned before a later focus change on each route.
        foreach (var acknowledged in new[] { false, true })
        {
            context = "held-submit-receipt-before-focus/" + Route(acknowledged);
            var owner = new TerminalEditorFocusOwner(); var submitHeld = NewSignal(); var releaseSubmit = NewSignal(); var order = new List<string>();
            var h = await Harness.Start(owned, acknowledged, owner, submit: async () => { order.Add("submit-enter"); submitHeld.TrySetResult(); await releaseSubmit.Task; order.Add("submit-join"); },
                beforePaint: snapshot => { if (!snapshot.EditorOwnsFocus) order.Add("focus-paint"); return Task.CompletedTask; }, accepted: () => order.Add("receipt-applied"), releaseOnClose: [releaseSubmit]);
            await h.Sink.Send("kept\r"); await submitHeld.Task.WaitAsync(TimeSpan.FromSeconds(5)); var change = owner.SetEditorFocusAsync(false);
            var waits = !change.IsCompleted; releaseSubmit.TrySetResult(); await change;
            var properOrder = order.IndexOf("submit-join") < order.IndexOf("focus-paint") && (!acknowledged || order.IndexOf("receipt-applied") < order.IndexOf("focus-paint"));
            var receipt = await h.Close(); Add(new { id = "held-submit-receipt-before-focus", route = Route(acknowledged), waits, properOrder, order, receipt, pass = waits && properOrder });
        }
        // Fail before Close (including startup) and let only unconditional ownership cleanup retire the run.
        foreach (var acknowledged in new[] { false, true })
        foreach (var phase in new[] { "pre-close", "startup", "pre-close-cleanup-fault" })
        {
            context = "unconditional-cleanup-before-close-and-failed-startup/" + Route(acknowledged) + "/" + phase;
            var scope = new List<Harness>(); var release = NewSignal(); var callbackJoined = false;
            var expected = new IOException("injected primary " + phase); Exception? primary = null; Harness? h = null;
            try
            {
                h = await Harness.Start(scope, acknowledged, new(), beforePaint: async _ =>
                {
                    await release.Task; callbackJoined = true;
                    if (phase == "pre-close-cleanup-fault") throw new IOException("injected cleanup fault");
                }, releaseOnClose: [release], startupProbe: started =>
                {
                    h = started;
                    if (phase == "startup") throw expected;
                });
                throw expected;
            }
            catch (Exception error) { primary = error; }
            finally { await CleanupAll(scope, primary); }
            var joinedRun = h is not null && h.Run.IsCompleted && h.Sink.ActiveReads == 0 && h.Sink.ActiveWrites == 0 &&
                h.Sink.ReadsStarted == h.Sink.ReadsSettled && h.Sink.WritesStarted == h.Sink.WritesSettled && h.Sink.Disposals == 0;
            var primaryPreserved = ReferenceEquals(primary, expected);
            var cleanupFaultRetained = phase != "pre-close-cleanup-fault" || expected.Data.Count > 0;
            Add(new { id = "unconditional-cleanup-before-close-and-failed-startup", route = Route(acknowledged), phase,
                barrierReleased = release.Task.IsCompleted, callbackJoined, joinedRun, primaryPreserved, cleanupFaultRetained,
                pass = release.Task.IsCompleted && callbackJoined && joinedRun && primaryPreserved && cleanupFaultRetained });
        }
        context = "exclusive-attachment-stale-request";
        var duplicateOwner = new TerminalEditorFocusOwner(false); TerminalEditorFocusOwner.Request? oldRequest = null;
        var oldAttachment = duplicateOwner.AttachEditor(Guid.NewGuid(), r => oldRequest = r);
        var duplicate = Failure(() => duplicateOwner.AttachEditor(Guid.NewGuid(), _ => { }), TerminalEditorFocusFailure.AlreadyAttached);
        var oldTask = duplicateOwner.SetEditorFocusAsync(true); oldAttachment.Dispose();
        var after = duplicateOwner.AttachEditor(Guid.NewGuid(), _ => { }); var staleRequestSkipped = !oldRequest!.TryApply() && !after.Snapshot.EditorOwnsFocus;
        try { await oldTask; } catch (TerminalEditorFocusException) { }
        after.Dispose(); Add(new { id = "exclusive-attachment-stale-request", duplicate, staleRequestSkipped, oldTask.IsFaulted, pass = duplicate && staleRequestSkipped && oldTask.IsFaulted });
        return new { sourceSchedules = 5, sourceCheckpoints = 37, actualRouteEditorProjections = rows.Count, rows, controls, joined, failed,
            boundary = "Single editor ownership, with Source other editor represented as external owner. Actual input and actual view on both routes; same-state serialized observation barriers leave original expected objects intact. Complete secondary widget focus and physical OS/IME gates remain open.", allOwnedExecutionsJoined = true };
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try { await CleanupAll(owned, primaryFailure); }
            finally
            {
                if (primaryFailure is not null) observeFailure?.Invoke(new { context, failed, projections = rows.Count,
                    completedControls = controls.Count, ownedCount = owned.Count,
                    originals = owned.TakeLast(8).Select(h => h.JoinFacts()).ToArray() });
            }
        }
        void Add(object row) { controls.Add(row); if (!(bool)row.GetType().GetProperty("pass")!.GetValue(row)!) failed++; }
    }
    private static async Task Until(Func<bool> predicate)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(1, deadline.Token); }
    private static bool Position(TerminalFrame frame) => (bool)typeof(TerminalFrame).GetProperty("PositionCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame)!;
    private static string Route(bool ack) => ack ? "acknowledged" : "legacy";
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Failure(Action action, TerminalEditorFocusFailure expected) { try { action(); return false; } catch (TerminalEditorFocusException e) { return e.Failure == expected; } }
    private static async Task<bool> Stale(Func<ValueTask> action) { try { await action(); return false; } catch (TerminalEditorFocusException e) { return e.Failure == TerminalEditorFocusFailure.StaleIdentity; } }
    private static async Task CleanupAll(IEnumerable<Harness> owned, Exception? primaryFailure)
    {
        List<Exception> failures = [];
        foreach (var h in owned.Reverse())
            try { await h.Cleanup(primaryFailure); } catch (Exception error) { failures.Add(error); }
        if (failures.Count == 0) return;
        var cleanupFailure = new AggregateException("Focus harness cleanup failed after joining owners.", failures);
        if (primaryFailure is null) throw cleanupFailure;
        primaryFailure.Data["focus-cleanup-failures"] = cleanupFailure;
    }

    private sealed class CustomCancellation(CancellationToken token) : OperationCanceledException(token) { }
    private static bool ProvenCancellation(Exception? cause, Task original, Exception? primary,
        CancellationToken? expectedWait, CancellationToken? contributor, TerminalSubmissionReceipts? receiptOwner = null,
        CancellationToken caller = default, bool contributorRequested = true) =>
        cause is OperationCanceledException canceled && original.IsCanceled && ReferenceEquals(primary, cause) &&
        expectedWait is { IsCancellationRequested: true } wait && canceled.CancellationToken == wait &&
        contributor is { IsCancellationRequested: true } && contributorRequested &&
        (cause is not TerminalReceiptWaitCanceledException receipt ||
            ReferenceEquals(receipt.Owner, receiptOwner) && receipt.WaitToken == wait && receipt.Original.CancellationToken == wait && receipt.InputOwnerToken == caller);
    private sealed class Harness
    {
        internal readonly ControlledTerminal Sink;
        internal readonly TerminalSessionView View;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TerminalSubmissionReceipts Receipts = new();
        internal TerminalDraftSnapshot? Last; internal TerminalEditorRenderedFrame? Frame;
        internal readonly List<string> Submits = [], Changes = [];
        internal int Interrupts, Ended; internal Task<TerminalInputExit> Run = Task.FromResult(TerminalInputExit.Eof);
        internal Exception? Failure;
        internal TerminalInputFailureFacts? Causes;
        internal CancellationToken PaintToken;
        private readonly CancellationToken callerToken;
        internal CancellationToken CallerToken => callerToken;
        private Type? expectedFailure;
        private CancellationToken? expectedCancellation;
        private CancellationToken? expectedContributor;
        private bool contributorRequestedBeforeClose;
        private object? closePredicateFacts;
        internal readonly TaskCompletionSource PaintCanceled = NewSignal();
        private TaskCompletionSource[] releases = [];
        private bool closed;
        private Harness(TerminalEditorFocusOwner owner, bool nonCooperative)
        { callerToken = Stop.Token; Sink = new(nonCooperative); View = new(Sink, Sink, (_, frame) => Frame = frame, owner); }
        internal static async Task<Harness> Start(List<Harness> owned, bool acknowledged, TerminalEditorFocusOwner owner, bool nonCooperative = false,
            Func<TerminalDraftSnapshot, Task>? beforePaint = null, Func<Task>? submit = null, Action? accepted = null,
            TaskCompletionSource[]? releaseOnClose = null, Action<Harness>? startupProbe = null)
        {
            var h = new Harness(owner, nonCooperative) { releases = releaseOnClose ?? [] }; var entered = NewSignal();
            owned.Add(h);
            try
            {
            await h.View.StartAsync(CancellationToken.None);
            async ValueTask Paint(TerminalDraftSnapshot snapshot, CancellationToken token)
            { h.PaintToken = token; using var registration = token.Register(() => h.PaintCanceled.TrySetResult()); if (snapshot.Text != (h.Last?.Text ?? "")) h.Changes.Add(snapshot.Text); h.Last = snapshot; entered.TrySetResult(); if (beforePaint is not null) await beforePaint(snapshot); await h.View.SetDraftAsync(snapshot, token); }
            async Task<bool> Submit(string text, CancellationToken token)
            { h.Submits.Add(text); if (submit is not null) await submit(); return true; }
            var input = new TerminalChatInput(h.Sink, null, owner);
            h.Run = acknowledged ? input.RunAcknowledgedAsync(async (line, token) =>
            { var keep = await Submit(line.Text, token); h.Receipts.FinishLocal(line, true); return keep; }, _ => Task.CompletedTask, Paint, h.Receipts,
                (editor, line) => { editor.AddToHistory(line.Text); accepted?.Invoke(); }, _ => { h.Ended++; h.Receipts.Complete(); }, () => h.Interrupts++, h.Stop.Token, h.View.CaptureEditorGeometry,
                observeFailures: facts => h.Causes = facts)
                : input.RunAsync(Submit, Paint, () => h.Interrupts++, h.Stop.Token);
            startupProbe?.Invoke(h);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); return h;
            }
            catch (Exception error) { await h.Cleanup(error); throw; }
        }
        internal async Task<object> Close(Type? expectedFailure = null, TimeSpan? diagnosticBound = null, CancellationToken? expectedCancellation = null,
            CancellationToken? expectedContributor = null)
        {
            if (closed) return new { alreadyClosed = true, pass = true };
            this.expectedFailure = expectedFailure;
            this.expectedCancellation = expectedCancellation;
            this.expectedContributor = expectedContributor;
            contributorRequestedBeforeClose = expectedContributor?.IsCancellationRequested ?? false;
            Sink.End(); Exception? error = null;
            try { await Run.WaitAsync(diagnosticBound ?? TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException e) when (Stop.IsCancellationRequested) { if (expectedFailure is not null) error = e; }
            catch (Exception e) { error = e; }
            finally
            {
                try
                {
                    foreach (var release in releases) release.TrySetResult();
                    Receipts.Complete(); Sink.End(); Stop.Cancel();
                }
                finally
                {
                    try { await Run; }
                    catch (OperationCanceledException e) when (Stop.IsCancellationRequested) { if (expectedFailure is not null) error ??= e; }
                    catch (Exception e) { error ??= e; }
                    finally { try { await View.DisposeAsync(); } finally { Stop.Dispose(); closed = true; } }
                }
            }
            Failure = error;
            var pass = ExpectedFailureMatches() && Run.IsCompleted && Sink.ActiveReads == 0 && Sink.ActiveWrites == 0 && Sink.ReadsStarted == Sink.ReadsSettled && Sink.WritesStarted == Sink.WritesSettled && Sink.Disposals == 0;
            // Snapshot actual operands before throwing this fixture's assertion. No messages, stacks, paths or token values.
            var receiptCancellation = error as TerminalReceiptWaitCanceledException;
            closePredicateFacts = new { expectedType = TypeName(expectedFailure), actualType = TypeName(error?.GetType()),
                primaryType = TypeName(Causes?.Primary?.GetType()), isOperationCanceled = error is OperationCanceledException,
                exactOperationCanceled = error?.GetType() == typeof(OperationCanceledException),
                exactTaskCanceled = error?.GetType() == typeof(TaskCanceledException),
                expectedIsOperationCanceled = expectedFailure == typeof(OperationCanceledException),
                expectedTypeMatches = error?.GetType() == expectedFailure, Run.IsCanceled,
                primaryInstanceMatches = ReferenceEquals(Causes?.Primary, error), expectedTokenPresent = expectedCancellation.HasValue,
                expectedTokenRequested = expectedCancellation?.IsCancellationRequested ?? false,
                contributorPresent = expectedContributor.HasValue, contributorRequestedBeforeClose,
                cancellationTokenMatches = CancellationTokenMatches(), expectedFailureMatches = ExpectedFailureMatches(),
                receiptWaitCause = receiptCancellation is not null, receiptOwnerMatches = ReferenceEquals(receiptCancellation?.Owner, Receipts),
                receiptWaitTokenMatches = receiptCancellation is not null && receiptCancellation.WaitToken == PaintToken,
                receiptInputOwnerTokenMatches = receiptCancellation is not null && receiptCancellation.InputOwnerToken == callerToken,
                receiptInputOwnerRequested = receiptCancellation?.InputOwnerRequested,
                receiptOriginalType = TypeName(receiptCancellation?.Original.GetType()), pass };
            if (!pass) throw new InvalidOperationException("Focus execution failure authority or original join check failed.");
            return new { Sink.ReadsStarted, Sink.ReadsSettled, Sink.ActiveReads, Sink.WritesStarted, Sink.WritesSettled, Sink.ActiveWrites, Sink.Disposals, Ended, failure = error?.GetType().Name,
                cancellationTokenMatches = CancellationTokenMatches(), primaryInstanceMatches = ReferenceEquals(Causes?.Primary, Failure), closePredicateFacts, pass };
        }
        private bool CancellationTokenMatches() => expectedCancellation is { } token && token.IsCancellationRequested &&
            Failure is OperationCanceledException canceled && canceled.CancellationToken == token;
        private bool ExpectedFailureMatches() => expectedFailure == typeof(OperationCanceledException)
            // Concrete subtype does not replace cause, token, contributor and original-task proof.
            ? ProvenCancellation(Failure, Run, Causes?.Primary, expectedCancellation, expectedContributor, Receipts, callerToken, contributorRequestedBeforeClose)
            : expectedFailure is null ? Failure is null : Failure?.GetType() == expectedFailure;
        internal object JoinFacts() => new { expectedFailure = FailureKind(expectedFailure), actualFailure = FailureKind(Failure?.GetType()),
            closePredicateFacts,
            expectedTypeMatches = expectedFailure is null ? Failure is null : Failure?.GetType() == expectedFailure,
            expectedFailureMatches = ExpectedFailureMatches(), cancellationTokenMatches = CancellationTokenMatches(),
            primaryInstanceMatches = ReferenceEquals(Causes?.Primary, Failure),
            reportedPrimary = FailureKind(Causes?.Primary?.GetType()),
            secondaryKinds = Causes?.Secondary.Take(8).Select(error => FailureKind(error.GetType())).ToArray(),
            Run.IsCompleted, Run.IsCanceled, Run.IsFaulted, Sink.ActiveReads, Sink.ActiveWrites,
            Sink.ReadsStarted, Sink.ReadsSettled, Sink.WritesStarted, Sink.WritesSettled, Sink.Disposals, closed };
        private static string? TypeName(Type? type)
        {
            const int maximum = 160;
            var name = type?.FullName;
            return name is null || name.Length <= maximum ? name : name[..maximum];
        }
        private static string FailureKind(Type? type) => type == typeof(TaskCanceledException) ? "task-canceled" : type == typeof(OperationCanceledException) ? "canceled" :
            type == typeof(InvalidOperationException) ? "invalid-operation" : type == typeof(IOException) ? "io" :
            type == typeof(InvalidDataException) ? "invalid-data" : type == typeof(TimeoutException) ? "timeout" : type is null ? "none" : "other";
        internal async Task Cleanup(Exception? primaryFailure)
        {
            if (closed) return;
            try
            {
                // Retire barriers before the diagnostic wait, even when the scenario never reached Close.
                try
                {
                    foreach (var release in releases) release.TrySetResult();
                    Receipts.Complete(); Sink.End(); Stop.Cancel();
                }
                finally { await Close(); }
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            { primaryFailure.Data["focus-cleanup-" + primaryFailure.Data.Count] = cleanupFailure; }
        }
    }
    private sealed class ControlledTerminal(bool nonCooperative) : IConsoleTerminal, ITerminalViewportSource
    {
        private sealed record Packet(string? Text, Exception? Failure = null) { internal readonly TaskCompletionSource Admitted = NewSignal(); }
        private readonly Channel<Packet> packets = Channel.CreateUnbounded<Packet>(); private Packet? previous;
        internal int ReadsStarted, ReadsSettled, ActiveReads, WritesStarted, WritesSettled, ActiveWrites, Disposals;
        internal int Columns = 20;
        internal Exception? ReadFailure;
        public TerminalViewport ReadViewport() => new(Columns, 24, 0, 0, Columns, 24);
        public TerminalLeaseSnapshot Snapshot { get { var state = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0); return new(state, state, null, false, false, ActiveReads, ActiveWrites, ReadsStarted, ReadsSettled, WritesStarted, WritesSettled); } }
        internal async Task Send(string text) { var packet = new Packet(text); packets.Writer.TryWrite(packet); await packet.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        internal void End() => packets.Writer.TryWrite(new(null));
        internal void Queue(string text) => packets.Writer.TryWrite(new(text));
        internal void QueueFault(Exception error) => packets.Writer.TryWrite(new(null, error));
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            ReadsStarted++; ActiveReads++; previous?.Admitted.TrySetResult();
            try { var packet = await packets.Reader.ReadAsync(nonCooperative ? CancellationToken.None : token); previous = packet;
                if (packet.Failure is { } failure) { ReadFailure = failure; throw failure; }
                if (packet.Text is null) return 0; packet.Text.AsMemory().CopyTo(destination); return packet.Text.Length; }
            finally { ActiveReads--; ReadsSettled++; }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        { WritesStarted++; ActiveWrites++; try { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; } finally { ActiveWrites--; WritesSettled++; } }
        public ValueTask DisposeAsync() { Disposals++; throw new InvalidOperationException("Borrowed terminal disposed"); }
    }
}
