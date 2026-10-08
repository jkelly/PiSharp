using System.Threading.Channels;
using System.Collections.Immutable;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Extensions;

namespace PiSharp.Cli.Interactive;

internal sealed record TerminalInputFailureFacts(Exception? Primary, ImmutableArray<Exception> Secondary);

internal sealed record TerminalDraftSnapshot(string Text, int CursorUtf16Offset)
{
    internal TerminalEditorLayoutInput? Layout { get; init; }
    internal bool EditorOwnsFocus { get; init; } = true;
    internal TerminalEditorFocusSnapshot? EditorFocus { get; init; }
}
internal enum TerminalInputExit { Eof, Quit }

/// <summary>One owned read/decoder and bounded event consumer; the console lease is borrowed.</summary>
internal sealed class TerminalChatInput
{
    private readonly IConsoleTerminal terminal;
    private readonly TimeProvider? clock;
    private readonly TerminalEditorFocusOwner? focusOwner;
    private readonly TerminalKeybindings? keybindings;
    private readonly bool kittyProtocolActive;
    private readonly TerminalSelectListInputRouter? selectListRouter;
    private readonly InteractiveSessionFrontend? queueRestoration;
    private readonly TerminalDoubleEscapeAction doubleEscapeAction;
    private readonly TerminalExtensionInputAdmission? terminalInputAdmission;
    private readonly Func<long>? captureTerminalSessionGeneration;
    internal TerminalChatInput(IConsoleTerminal terminal, TimeProvider? clock = null) : this(terminal, clock, null) { }
    internal TerminalChatInput(IConsoleTerminal terminal, TimeProvider? clock, TerminalEditorFocusOwner? focusOwner)
            : this(terminal, clock, focusOwner, null) { }
    internal TerminalChatInput(IConsoleTerminal terminal, TimeProvider? clock, TerminalEditorFocusOwner? focusOwner,
        TerminalKeybindings? keybindings, bool kittyProtocolActive = false, TerminalSelectListInputRouter? selectListRouter = null,
        InteractiveSessionFrontend? queueRestoration = null, TerminalDoubleEscapeAction doubleEscapeAction = TerminalDoubleEscapeAction.Tree, TerminalExtensionInputAdmission? terminalInputAdmission = null, Func<long>? captureTerminalSessionGeneration = null)
    { this.terminal = terminal; this.clock = clock; this.focusOwner = focusOwner; this.keybindings = keybindings?.CreateSnapshot(); this.kittyProtocolActive = kittyProtocolActive; this.selectListRouter = selectListRouter; this.queueRestoration = queueRestoration; this.doubleEscapeAction = doubleEscapeAction; if ((terminalInputAdmission is null) != (captureTerminalSessionGeneration is null)) throw new ArgumentException("Terminal input admission and generation capture must be supplied together."); this.terminalInputAdmission = terminalInputAdmission; this.captureTerminalSessionGeneration = captureTerminalSessionGeneration; }
    /// <summary>The caller completes receipts only after the actual RPC host/output callbacks settle.</summary>
    internal async Task<TerminalInputExit> RunAcknowledgedAsync(
        Func<TerminalSubmittedLine, CancellationToken, Task<bool>> submitLine,
        Func<CancellationToken, Task> cancelDraft,
        Func<TerminalDraftSnapshot, CancellationToken, ValueTask> renderDraft,
        TerminalSubmissionReceipts receipts,
        Action<TerminalTextEditorPasteController, TerminalAcceptedLine> applyAccepted,
        Action<Exception?> inputEnded, Action interrupt, CancellationToken token,
        Func<TerminalEditorGeometrySnapshot>? captureGeometry = null,
        Action<TerminalInputEvent>? observeInputCompletion = null, Task? beginPhysicalRead = null, bool gracefulInterrupt = false,
        Action<TerminalInputFailureFacts>? observeFailures = null)
    {
        ArgumentNullException.ThrowIfNull(receipts); ArgumentNullException.ThrowIfNull(applyAccepted);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var events = new InputAdmissions(selectListRouter, queueRestoration);
        var draft = new TerminalTextEditorPasteController(keybindings: keybindings);
        var interruptKeys = keybindings?.GetKeys(keybindings.GetDefinition("app.clear") is null ? "tui.input.copy" : "app.clear");
        using var focus = focusOwner?.AttachEditor(draft.CaptureLayoutInput().Identity.EditorLifetimeId, events.AdmitFocus);
        if (selectListRouter is not null) selectListRouter.AttachInput(focus ?? throw new InvalidOperationException("Native selector requires the editor focus owner."));
        Exception? producerFailure = null, failure = null;
        var failureGate = new object(); var observedFailures = new List<Exception>();
        var producerCanceledStop = 0;
        var userInterrupt = 0;
        var endGate = new object(); var notifiedEnd = false; var notifiedFailure = false;
        var reader = ReadOwnedAsync();
        var exit = TerminalInputExit.Eof;
        Task<bool>? nextInput = null; Task? nextReceipt = null;
        Task<TerminalQueueRestoration?>? restoration = null;
        Task<TerminalSessionNavigationReceipt?>? navigation = null;
        var doubleEscape = new TerminalDoubleEscapeGesture(clock ?? TimeProvider.System);
        TerminalInputEvent? restorationInput = null;
        var deferredInputs = new Queue<Admission>();
        try
        {
            // The actual terminal caller supplies geometry. Its first editor paint belongs to
            // this controller, before any decoded event is consumed; the existing owner joins
            // the admitted reader/render/receipt work if startup faults or is canceled.
            if (captureGeometry is not null || focus is not null)
                await AwaitOwned(renderDraft(CaptureDraft(captureGeometry is not null), stop.Token).AsTask()).ConfigureAwait(false);
            while (true)
            {
                Drain();
                if (navigation?.IsCompleted == true)
                {
                    var receipt = await navigation.ConfigureAwait(false); navigation = null;
                    if (receipt is not null) queueRestoration!.ApplySessionNavigation(receipt, draft);
                    await AwaitOwned(renderDraft(CaptureDraft(captureGeometry is not null), stop.Token).AsTask()).ConfigureAwait(false);
                    observeInputCompletion?.Invoke(restorationInput!); restorationInput = null;
                }
                if (restoration?.IsCompleted == true)
                {
                    var restored = await restoration.ConfigureAwait(false); restoration = null;
                    if (restored is not null) queueRestoration!.ApplyQueueRestoration(restored, draft);
                    await AwaitOwned(renderDraft(CaptureDraft(captureGeometry is not null), stop.Token).AsTask()).ConfigureAwait(false);
                    observeInputCompletion?.Invoke(restorationInput!); restorationInput = null;
                }
                if (receipts.IsCompleted) break;
                Admission admitted; var retainedInput = false;
                if (restoration is null && navigation is null && deferredInputs.TryDequeue(out admitted!))
                {
                    events.ReleaseRetainedInput();
                    if (admitted.SessionGeneration != queueRestoration!.CaptureSessionGeneration())
                    { observeInputCompletion?.Invoke(admitted.Input!); continue; }
                }
                else if (!events.TryRead(out admitted!, retainInput: restoration is not null || navigation is not null))
                {
                    nextInput ??= events.Reader.WaitToReadAsync(stop.Token).AsTask();
                    nextReceipt ??= receipts.WaitForChangeAsync(stop.Token, token).AsTask();
                    Task? pending = (Task?)restoration ?? navigation;
                    var completed = pending is null ? await Task.WhenAny(nextInput, nextReceipt).ConfigureAwait(false) :
                        await Task.WhenAny(nextInput, nextReceipt, pending).ConfigureAwait(false);
                    if (ReferenceEquals(completed, pending)) continue;
                    if (completed == nextReceipt)
                    { await nextReceipt.ConfigureAwait(false); nextReceipt = null; continue; }
                    var available = await nextInput.ConfigureAwait(false); nextInput = null;
                    if (!available) break;
                    continue;
                }
                else retainedInput = (restoration is not null || navigation is not null) && admitted.Focus is null;
                if (admitted.DuringRestoration && admitted.SessionGeneration != queueRestoration!.CaptureSessionGeneration())
                {
                    if (retainedInput) events.ReleaseRetainedInput();
                    observeInputCompletion?.Invoke(admitted.Input!); continue;
                }
                if (retainedInput)
                {
                    if (admitted.SelectTarget is null && selectListRouter?.CaptureInputTarget() is null &&
                        (focus is null || focus.Snapshot.EditorOwnsFocus))
                    {
                        // Preserve ordinary input in its original order while the bounded take
                        // is pending. These entries still count toward the 32-input admission cap.
                        // Focus/selector events keep flowing, so host output never waits on a
                        // frozen input owner before it can deliver the restoration response.
                        deferredInputs.Enqueue(admitted); continue;
                    }
                    events.ReleaseRetainedInput();
                }
                if (admitted.Focus is { } change)
                {
                    if (!change.TryApply()) continue;
                    try
                    {
                        if (admitted.SelectorChange is { } selectorChange)
                            await AwaitOwned(selectListRouter!.ApplyAsync(selectorChange, stop.Token).AsTask(), selectorChange.AdmissionToken).ConfigureAwait(false);
                        await AwaitOwned(renderDraft(CaptureDraft(captureGeometry is not null), stop.Token).AsTask()).ConfigureAwait(false); change.Complete();
                    }
                    catch (OperationCanceledException error) when (admitted.SelectorChange is { } canceledChange &&
                        canceledChange.AdmissionToken.IsCancellationRequested && !stop.IsCancellationRequested)
                    { change.Complete(error); } // Dialog cancellation retires this paint, preserving the input lifetime.
                    catch (Exception error) { change.Complete(error); throw; }
                    continue;
                }
                if (admitted.SelectTarget is { } selectTarget)
                {
                    await AwaitOwned(selectListRouter!.HandleInputAsync(admitted.Input!, selectTarget, stop.Token).AsTask()).ConfigureAwait(false);
                    observeInputCompletion?.Invoke(admitted.Input!);
                    continue;
                }
                if (selectListRouter?.CaptureInputTarget() is not null)
                {
                    // A draft key admitted before publication has no selector identity.
                    // It cannot submit/reset that draft against the newly visible dialog.
                    observeInputCompletion?.Invoke(admitted.Input!); continue;
                }
                if (focus is not null && !focus.Snapshot.EditorOwnsFocus) { observeInputCompletion?.Invoke(admitted.Input!); continue; }
                var input = admitted.Input!;
                var editorInput = keybindings is null ? TerminalInputDecoder.ForEditorInput(input, draft.IsCharacterJumpPending) : input;
                if (queueRestoration is not null && IsClear(editorInput))
                {
                    if (admitted.SessionGeneration != queueRestoration.CaptureSessionGeneration())
                    { observeInputCompletion?.Invoke(input); continue; }
                    draft.Reset(); await AwaitOwned(cancelDraft(stop.Token)).ConfigureAwait(false);
                }
                else if (WantsRestore(draft, editorInput))
                {
                    restoration = queueRestoration!.RestoreQueuedMessagesAsync(draft, stop.Token, expectedGeneration: admitted.SessionGeneration);
                    restorationInput = input;
                    continue;
                }
                else if (WantsSubmit(draft, editorInput))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var submission = receipts.Reserve(TrimExpandedSubmission(draft.GetExpandedText()));
                    bool keepReading;
                    try
                    {
                        var submitting = submitLine(submission, stop.Token);
                        await AwaitOwned(submitting).ConfigureAwait(false);
                        keepReading = await submitting.ConfigureAwait(false);
                    }
                    catch { receipts.RejectUnacknowledged(submission); throw; }
                    Drain();
                    if (!keepReading) { receipts.RejectUnacknowledged(submission); exit = TerminalInputExit.Quit; break; }
                    draft.Reset();
                }
                else if (WantsCancel(draft, editorInput))
                {
                    if (queueRestoration is null)
                    { draft.Reset(); await AwaitOwned(cancelDraft(stop.Token)).ConfigureAwait(false); }
                    else if (input is not TerminalKey { Action: TerminalKeyAction.Repeat } &&
                        admitted.SessionGeneration == queueRestoration.CaptureSessionGeneration() && queueRestoration.IsTerminalStreaming)
                    {
                        restoration = queueRestoration.RestoreQueuedMessagesAsync(draft, stop.Token, abort: true,
                            expectedGeneration: admitted.SessionGeneration);
                        restorationInput = input; continue;
                    }
                    else if (queueRestoration is not null && input is not TerminalKey { Action: TerminalKeyAction.Repeat or TerminalKeyAction.Release } &&
                        admitted.SessionGeneration is { } navigationGeneration &&
                        !queueRestoration.IsTerminalStreaming && TrimExpandedSubmission(draft.GetExpandedText()).Length == 0 &&
                        doubleEscapeAction != TerminalDoubleEscapeAction.None &&
                        doubleEscape.Press(navigationGeneration))
                    {
                        navigation = queueRestoration.NavigateSessionAsync(draft, doubleEscapeAction, navigationGeneration, stop.Token);
                        restorationInput = input; continue;
                    }
                }
                else if (WantsExit(draft, editorInput) && draft.Snapshot.Text.Length == 0)
                { exit = TerminalInputExit.Quit; break; }
                else Handle(editorInput);
                await AwaitOwned(renderDraft(CaptureDraft(captureGeometry is not null), stop.Token).AsTask()).ConfigureAwait(false);
                observeInputCompletion?.Invoke(input);
            }
        }
        catch (Exception error) { RecordFailure(error); if (Volatile.Read(ref producerFailure) is { } admissionError) RecordFailure(admissionError); }
        finally
        {
            selectListRouter?.DetachInput();
            try { stop.Cancel(); } catch (Exception error) { RecordFailure(error); }
            Notify(failure);
            // A restoration sender can be inside a host UI publication. Retire its
            // unconsumed focus admissions before joining that original sender.
            if ((restoration is not null || navigation is not null) && selectListRouter is not null) focus?.Dispose();
            if (navigation is not null)
                try { await navigation.ConfigureAwait(false); } catch (OperationCanceledException error) when (IsOwnedCancellation(error)) { }
                catch (Exception error) { RecordFailure(error); Notify(failure); }
            if (restoration is not null)
                try { await restoration.ConfigureAwait(false); } catch (OperationCanceledException error) when (IsOwnedCancellation(error)) { }
                catch (Exception error) { RecordFailure(error); Notify(failure); }
            try { await reader.ConfigureAwait(false); } catch (OperationCanceledException error) when (IsOwnedCancellation(error)) { }
            catch (Exception error) { RecordFailure(error); Notify(failure); }
            if (nextInput is not null)
                try { await nextInput.ConfigureAwait(false); } catch (OperationCanceledException error) when (IsOwnedCancellation(error)) { }
                catch (Exception error) { RecordFailure(error); Notify(failure); }
            if (nextReceipt is not null)
                try { await nextReceipt.ConfigureAwait(false); } catch (OperationCanceledException error) when (IsOwnedCancellation(error)) { }
                catch (Exception error) { RecordFailure(error); Notify(failure); }
            // A retiring RPC callback cannot wait on focus admission after this consumer ended.
            // Actual reads and admitted input/render callbacks have joined before detachment.
            if (selectListRouter is not null) focus?.Dispose();
            // The same owner retains the editor across EOF/failure until late admissions are joined.
            while (true)
            {
                while (receipts.TryClaim(out var accepted))
                    try { Apply(accepted!); } catch (Exception error) { RecordFailure(error); Notify(failure); }
                if (receipts.IsCompleted) break;
                await receipts.WaitForChangeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            draft.Reset();
        }
        if (Volatile.Read(ref producerFailure) is { } producerError) RecordFailure(producerError);
        if (IsUserCancellation(failure)) { failure = null; exit = TerminalInputExit.Quit; }
        TerminalInputFailureFacts facts;
        lock (failureGate) facts = new(failure, observedFailures.Where(error => !ReferenceEquals(error, failure)).ToImmutableArray());
        // The read, its physical original and all admitted callbacks have joined. Publish
        // secondary causes without replacing the caller's chosen cancellation outcome.
        observeFailures?.Invoke(facts);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return exit;

        async Task ReadOwnedAsync()
        {
            // Command startup attaches focus before RPC extensions can publish, while
            // physical input admission retains the original get_state-before-read order.
            if (beginPhysicalRead is not null) await beginPhysicalRead.WaitAsync(stop.Token).ConfigureAwait(false);
            await Read(events, stop.Token, () => { Volatile.Write(ref userInterrupt, 1); interrupt(); }, interruptKeys, (error, _) =>
            {
                Interlocked.CompareExchange(ref producerFailure, error, null);
                if (!stop.IsCancellationRequested) Volatile.Write(ref producerCanceledStop, 1);
                stop.Cancel();
            }, () =>
            {
                // Selector EOF closes RPC admission during an entered paint/send. Its
                // cancellation still joins that original before releasing either owner.
                if (selectListRouter?.CaptureInputTarget() is not null) Notify(null);
            }, observeFailure: ObserveReadFailure).ConfigureAwait(false);
        }

        bool IsOwnedCancellation(Exception? error) => error is OperationCanceledException canceled &&
            canceled.CancellationToken == stop.Token && stop.IsCancellationRequested &&
            (canceled is not RpcCommandAdmissionCanceledException admission ||
                admission.CallerToken == stop.Token && admission.CallerRequested && !admission.ConnectionRequested);
        bool IsUserCancellation(Exception? error) => gracefulInterrupt && Volatile.Read(ref userInterrupt) != 0 && IsOwnedCancellation(error);
        void ObserveReadFailure(Exception error)
        {
            // The producer can fail while an earlier callback is still held. Retain
            // its cause now, but choose primary authority only after that callback joins.
            lock (failureGate)
                if (!IsOwnedCancellation(error) && !observedFailures.Any(existing => ReferenceEquals(existing, error)))
                    observedFailures.Add(error);
        }
        void RecordFailure(Exception error)
        {
            // Only the exact producer failure may replace the internal cancellation it caused.
            // Caller cancellation and a callback's foreign token retain their precedence.
            // A proven connection-close cancellation is never an input-owner cancellation.
            lock (failureGate)
            {
                if (!IsOwnedCancellation(error) && !observedFailures.Any(existing => ReferenceEquals(existing, error)))
                    observedFailures.Add(error);
                if (failure is null || IsUserCancellation(failure) ||
                    Volatile.Read(ref producerCanceledStop) != 0 && !token.IsCancellationRequested && IsOwnedCancellation(failure) &&
                    ReferenceEquals(error, Volatile.Read(ref producerFailure))) failure = error;
            }
        }

        TerminalDraftSnapshot CaptureDraft(bool layout)
        {
            var snapshot = draft.Snapshot; var ownership = focus?.Snapshot;
            return new(snapshot.Text, snapshot.CursorUtf16Offset)
            { Layout = layout || focus is not null ? draft.CaptureLayoutInput() : null,
                EditorOwnsFocus = ownership?.EditorOwnsFocus ?? true, EditorFocus = ownership };
        }
        void Apply(TerminalAcceptedLine accepted)
        {
            try { applyAccepted(draft, accepted); accepted.FinishApplying(null); }
            catch (Exception error) { accepted.FinishApplying(error); throw; }
        }
        void Drain() { while (receipts.TryClaim(out var accepted)) Apply(accepted!); }
        void Handle(TerminalInputEvent input)
        {
            if (captureGeometry is null || !draft.RequiresConfiguredLayout(input))
            { draft.HandleInput(input); return; }
            // No await or retained map crosses this editor operation. Repeated resize observations
            // are bounded, and a stale map never changes draft, history, undo or paste ownership.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var geometry = captureGeometry();
                TerminalEditorVisualMap map;
                try { map = new TerminalEditorVisualMapBuilder().BuildForTerminal(draft.CaptureLayoutInput(), geometry); }
                catch (TerminalEditorLayoutException error) when (geometry.ContentColumns <= 2 &&
                    error.Failure == TerminalEditorLayoutFailure.ResourceLimit)
                {
                    // An indivisible wide source segment cannot fit this physical viewport.
                    // Leave every controller value intact; the actual view shows its bounded
                    // fallback and a later resize rebuilds from this same owner.
                    return;
                }
                var current = captureGeometry();
                if (current.Identity != geometry.Identity) continue;
                draft.HandleInput(input, map, current.Identity); return;
            }
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.StaleGeometry);
        }
        async Task AwaitOwned(Task action, CancellationToken localCancellation = default)
        {
            try
            {
                while (!action.IsCompleted && !receipts.IsCompleted)
                {
                    nextReceipt ??= receipts.WaitForChangeAsync(stop.Token, token).AsTask();
                    if (await Task.WhenAny(action, nextReceipt).ConfigureAwait(false) != nextReceipt) break;
                    await nextReceipt.ConfigureAwait(false); nextReceipt = null; Drain();
                }
                await action.ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (localCancellation.IsCancellationRequested && !stop.IsCancellationRequested)
            {
                // Only this dialog's original operation was canceled. Join it without
                // canceling the session's input owner; retirement will restore its focus.
                try { await action.ConfigureAwait(false); }
                catch (Exception actionError)
                {
                    if (!ReferenceEquals(actionError, error) &&
                        !(actionError is OperationCanceledException canceled && canceled.CancellationToken == localCancellation) &&
                        !IsOwnedCancellation(actionError)) RecordFailure(actionError);
                }
                throw;
            }
            catch (Exception error)
            {
                // A receipt/observer failure cannot detach an already admitted send/render callback.
                Exception retainedError = error;
                try { stop.Cancel(); } catch (Exception cleanupError) { RecordFailure(cleanupError); }
                try { await action.ConfigureAwait(false); }
                catch (Exception actionError)
                {
                    // Receipt wake cancellation can win the same stop race. Preserve the
                    // actual sender's local proof/cause after joining its original task.
                    if (IsOwnedCancellation(error) && actionError is RpcCommandAdmissionCanceledException && IsOwnedCancellation(actionError))
                        retainedError = actionError;
                    else if (!IsOwnedCancellation(actionError)) RecordFailure(actionError);
                }
                RecordFailure(retainedError);
                if (!ReferenceEquals(retainedError, error)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(retainedError).Throw();
                throw;
            }
        }
        void Notify(Exception? error)
        {
            lock (endGate)
            {
                if (notifiedEnd && (error is null || notifiedFailure)) return;
                notifiedEnd = true; if (error is not null) notifiedFailure = true;
                try { inputEnded(error); } catch (Exception callbackError) { RecordFailure(callbackError); }
            }
        }
    }

    internal async Task<TerminalInputExit> RunAsync(Func<string, CancellationToken, Task<bool>> submitLine,
        Func<TerminalDraftSnapshot, CancellationToken, ValueTask> renderDraft, Action interrupt, CancellationToken token,
        Func<TerminalEditorGeometrySnapshot>? captureGeometry = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var events = new InputAdmissions();
        var draft = new TerminalTextEditorPasteController(keybindings: keybindings);
        var interruptKeys = keybindings?.GetKeys(keybindings.GetDefinition("app.clear") is null ? "tui.input.copy" : "app.clear");
        using var focus = focusOwner?.AttachEditor(draft.CaptureLayoutInput().Identity.EditorLifetimeId, events.AdmitFocus);
        Exception? producerFailure = null;
        var reader = Read(events, stop.Token, interrupt, interruptKeys, (error, _) =>
        {
            Interlocked.CompareExchange(ref producerFailure, error, null);
            stop.Cancel();
        });
        Exception? failure = null; var exit = TerminalInputExit.Eof;
        try
        {
            if (focus is not null || captureGeometry is not null) await renderDraft(CaptureDraft(), stop.Token).ConfigureAwait(false);
            while (await events.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false))
            {
                if (!events.TryRead(out var admitted)) continue;
                if (admitted.Focus is { } change)
                {
                    if (!change.TryApply()) continue;
                    try { await renderDraft(CaptureDraft(), stop.Token).ConfigureAwait(false); change.Complete(); }
                    catch (Exception error) { change.Complete(error); throw; }
                    continue;
                }
                if (focus is not null && !focus.Snapshot.EditorOwnsFocus) continue;
                var input = admitted.Input!;
                var editorInput = keybindings is null ? TerminalInputDecoder.ForEditorInput(input, draft.IsCharacterJumpPending) : input;
                if (WantsSubmit(draft, editorInput))
                {
                    var line = TrimExpandedSubmission(draft.GetExpandedText());
                    if (!await submitLine(line, stop.Token).ConfigureAwait(false)) { exit = TerminalInputExit.Quit; break; }
                    draft.Reset();
                }
                else if (WantsCancel(draft, editorInput))
                { draft.Reset(); if (!await submitLine("/cancel", stop.Token).ConfigureAwait(false)) { exit = TerminalInputExit.Quit; break; } }
                else if (WantsExit(draft, editorInput) && draft.Snapshot.Text.Length == 0)
                { exit = TerminalInputExit.Quit; break; }
                else HandleLegacy(editorInput);
                await renderDraft(CaptureDraft(), stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) { failure = Volatile.Read(ref producerFailure) ?? error; }
        finally
        {
            try { stop.Cancel(); } catch (Exception error) { failure ??= error; }
            try { await reader.ConfigureAwait(false); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { failure ??= error; }
            draft.Reset();
        }
        failure = Volatile.Read(ref producerFailure) ?? failure;
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return exit;

        void HandleLegacy(TerminalInputEvent input)
        {
            if (captureGeometry is null || !draft.RequiresConfiguredLayout(input)) { draft.HandleInput(input); return; }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var geometry = captureGeometry();
                TerminalEditorVisualMap map;
                try { map = new TerminalEditorVisualMapBuilder().BuildForTerminal(draft.CaptureLayoutInput(), geometry); }
                catch (TerminalEditorLayoutException error) when (geometry.ContentColumns <= 2 &&
                    error.Failure == TerminalEditorLayoutFailure.ResourceLimit) { return; }
                var current = captureGeometry(); if (current.Identity != geometry.Identity) continue;
                draft.HandleInput(input, map, current.Identity); return;
            }
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.StaleGeometry);
        }

        TerminalDraftSnapshot CaptureDraft()
        {
            var snapshot = draft.Snapshot; var ownership = focus?.Snapshot;
            return new(snapshot.Text, snapshot.CursorUtf16Offset)
            { Layout = focus is null && captureGeometry is null ? null : draft.CaptureLayoutInput(),
                EditorOwnsFocus = ownership?.EditorOwnsFocus ?? true, EditorFocus = ownership };
        }
    }

    private bool WantsRestore(TerminalTextEditorPasteController draft, TerminalInputEvent input) =>
        queueRestoration is not null && keybindings is not null && !draft.HandlesPendingCharacterJump(input) &&
        input is not TerminalKey { Action: TerminalKeyAction.Release or TerminalKeyAction.Repeat } &&
        keybindings.GetKeys("app.message.dequeue").Any(key => TerminalInputDecoder.MatchesKey(input, key));

    private bool IsClear(TerminalInputEvent input) => input is not TerminalKey
        { Action: TerminalKeyAction.Release or TerminalKeyAction.Repeat } && (keybindings is null
        ? TerminalInputDecoder.MatchesKey(input, "ctrl+c")
        : keybindings.GetKeys(keybindings.GetDefinition("app.clear") is null ? "tui.input.copy" : "app.clear")
            .Any(key => TerminalInputDecoder.MatchesKey(input, key)));

    private bool WantsSubmit(TerminalTextEditorPasteController draft, TerminalInputEvent input) => keybindings is null
        ? input is TerminalKey { Key: "Enter", Modifiers: TerminalModifiers.None, Action: not TerminalKeyAction.Release }
        : input is not TerminalKey { Action: TerminalKeyAction.Release } && draft.TryPrepareConfiguredSubmit(input);
    private bool WantsCancel(TerminalTextEditorPasteController draft, TerminalInputEvent input) => keybindings is null
        ? input is TerminalKey { Key: "Escape", Modifiers: TerminalModifiers.None, Action: not TerminalKeyAction.Release }
        : !draft.HandlesPendingCharacterJump(input) && input is not TerminalKey { Action: TerminalKeyAction.Release } &&
            keybindings.GetKeys(keybindings.GetDefinition("app.interrupt") is null ? "tui.select.cancel" : "app.interrupt")
                .Any(key => TerminalInputDecoder.MatchesKey(input, key));
    private bool WantsExit(TerminalTextEditorPasteController draft, TerminalInputEvent input) => keybindings is null
        ? input is TerminalKey { Key: "d", Modifiers: TerminalModifiers.Control, Action: not TerminalKeyAction.Release }
        : !draft.HandlesPendingCharacterJump(input) && input is not TerminalKey { Action: TerminalKeyAction.Release } &&
            (keybindings.GetDefinition("app.exit") is null ? TerminalInputDecoder.MatchesKey(input, "ctrl+d") :
                keybindings.GetKeys("app.exit").Any(key => TerminalInputDecoder.MatchesKey(input, key)));

    // Editor.submitValue() expands registered paste markers before ECMAScript trim().
    // This set excludes U+0085 and includes U+FEFF, unlike String.Trim().
    internal static string TrimExpandedSubmission(string text)
    {
        var first = 0; var end = text.Length;
        while (first < end && IsSubmissionWhitespace(text[first])) first++;
        while (end > first && IsSubmissionWhitespace(text[end - 1])) end--;
        return first == 0 && end == text.Length ? text : text[first..end];
    }

    private static bool IsSubmissionWhitespace(char value) => value is
        >= '\u0009' and <= '\u000D' or '\u0020' or '\u00A0' or '\u1680' or
        >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or
        '\u205F' or '\u3000' or '\uFEFF';

    private async Task Read(InputAdmissions output, CancellationToken token, Action interrupt, string[]? interruptKeys,
        Action<Exception, bool> producerFailed, Action? observeEof = null, Action<Exception>? observeFailure = null)
    {
        using var readStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var readToken = readStop.Token;
        var provider = clock ?? TimeProvider.System; var decoder = new TerminalInputDecoder(timeProvider: provider, kittyProtocolActive: kittyProtocolActive);
        var clearGesture = new TerminalInterruptGesture(provider);
        var buffer = new char[4096]; Task<int>? physical = null; long physicalSessionGeneration = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(35), provider);
        Task<bool>? tick = null; Exception? failure = null, inputOverflow = null;
        try
        {
            while (true)
            {
                if (physical is null)
                {
                    physicalSessionGeneration = captureTerminalSessionGeneration?.Invoke() ?? 0;
                    physical = terminal.ReadAsync(buffer.AsMemory(), readToken).AsTask();
                }
                tick ??= timer.WaitForNextTickAsync(readToken).AsTask();
                if (await Task.WhenAny(physical, tick).ConfigureAwait(false) == tick)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    tick = null; Publish(decoder.FlushTimeouts()); continue;
                }
                var count = await physical.ConfigureAwait(false); physical = null;
                if (count < 0 || count > buffer.Length) throw new IOException("Terminal read returned an invalid character count.");
                if (count == 0) { Publish(decoder.Complete()); observeEof?.Invoke(); break; }
                if (terminalInputAdmission is null) Publish(decoder.Feed(buffer.AsSpan(0, count)));
                else
                {
                    if (physicalSessionGeneration != captureTerminalSessionGeneration!()) continue;
                    var dispatchOriginal = terminalInputAdmission.DispatchAsync(new string(buffer, 0, count), physicalSessionGeneration, readToken);
                    ExtensionTerminalInputOutcome outcome;
                    try { outcome = await dispatchOriginal.ConfigureAwait(false); }
                    catch when (dispatchOriginal.IsFaulted)
                    { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(dispatchOriginal.Exception!).Throw(); throw; }
                    readToken.ThrowIfCancellationRequested();
                    if (physicalSessionGeneration != captureTerminalSessionGeneration!()) continue;
                    if (outcome.Disposition == ExtensionTerminalInputDisposition.Forward)
                        Publish(decoder.Feed(outcome.Data.AsSpan()));
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            if (!OwnReadCancellation(error)) observeFailure?.Invoke(error);
            if (!OwnReadCancellation(error) || !token.IsCancellationRequested)
                try { producerFailed(error, ReferenceEquals(error, inputOverflow)); }
                catch (Exception callbackError) { RecordReadFailure(callbackError); }
        }
        finally
        {
            try { readStop.Cancel(); } catch (Exception error) { RecordReadFailure(error); }
            timer.Dispose();
            if (physical is not null)
                try { await physical.ConfigureAwait(false); } catch (Exception error) { RecordReadFailure(error); }
            if (tick is not null)
                try { await tick.ConfigureAwait(false); } catch (OperationCanceledException error) when (OwnReadCancellation(error)) { }
                catch (Exception error) { RecordReadFailure(error); }
            // Preserve exact cancellation provenance across this owned linked-token layer.
            if (OwnReadCancellation(failure) && token.IsCancellationRequested) failure = new OperationCanceledException(token);
            output.TryComplete(failure);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        bool OwnReadCancellation(Exception? error) => error is OperationCanceledException canceled &&
            canceled.CancellationToken == readToken && readToken.IsCancellationRequested;
        void RecordReadFailure(Exception error)
        {
            if (!OwnReadCancellation(error)) observeFailure?.Invoke(error);
            if (failure is null || OwnReadCancellation(failure)) failure = error;
        }

        void Publish(IEnumerable<TerminalInputEvent> values)
        {
            foreach (var value in values)
            {
                // Decide the interrupt binding using Source wire semantics before this producer shortcut.
                // Keep the original event for the editor's pending-jump and record/provenance handling.
                var selectTarget = selectListRouter?.CaptureInputTarget();
                if (selectTarget is null && value is not TerminalKey { Action: TerminalKeyAction.Release } && (interruptKeys is null
                    ? TerminalInputDecoder.ForEditorInput(value, false) is TerminalKey { Key: "c", Modifiers: TerminalModifiers.Control }
                    : interruptKeys.Any(key => TerminalInputDecoder.MatchesKey(value, key))))
                {
                    if (queueRestoration is null)
                    { interrupt(); readToken.ThrowIfCancellationRequested(); continue; }
                    if (value is TerminalKey { Action: TerminalKeyAction.Repeat }) continue;
                    if (clearGesture.Press(queueRestoration.CaptureSessionGeneration()))
                    { interrupt(); readToken.ThrowIfCancellationRequested(); continue; }
                    // First press remains an ordered editor action, including while
                    // a restoration is pending. Only shutdown bypasses the consumer.
                }
                if (!output.TryWrite(value, selectTarget))
                {
                    inputOverflow = new InvalidOperationException("Terminal input event admission exceeds its bounded profile.");
                    throw inputOverflow;
                }
            }
        }
    }
    // One FIFO admits keys and focus changes under the same gate. The original32 key slots
    // and eight independent focus slots are bounded before effects. During restoration only,
    // ordinary keys remain charged while focus/selector admissions can complete.
    private sealed record Admission(TerminalInputEvent? Input, TerminalEditorFocusOwner.Request? Focus,
        TerminalSelectListInputRouter.Change? SelectorChange = null, TerminalSelectListDialogSnapshot? SelectTarget = null,
        long? SessionGeneration = null, bool DuringRestoration = false);
    private sealed class InputAdmissions(TerminalSelectListInputRouter? selector = null, InteractiveSessionFrontend? frontend = null)
    {
        private readonly object gate = new();
        private readonly Channel<Admission> channel = Channel.CreateUnbounded<Admission>(new UnboundedChannelOptions
        { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        private int inputs, changes;
        private bool completed;
        internal ChannelReader<Admission> Reader => channel.Reader;
        internal bool TryWrite(TerminalInputEvent input, TerminalSelectListDialogSnapshot? selectTarget = null)
        {
            var inputState = frontend?.CaptureQueueInputState();
            lock (gate)
            {
                if (completed || inputs == 32) return false;
                inputs++; return channel.Writer.TryWrite(new(input, null, SelectTarget: selectTarget,
                    SessionGeneration: inputState?.Generation, DuringRestoration: inputState?.Restoring ?? false));
            }
        }
        internal void AdmitFocus(TerminalEditorFocusOwner.Request request)
        {
            lock (gate)
            {
                if (completed) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.Detached);
                if (changes == 8) throw new TerminalEditorFocusException(TerminalEditorFocusFailure.AdmissionLimit);
                changes++; channel.Writer.TryWrite(new(null, request, selector?.CaptureChange()));
            }
        }
        internal bool TryRead(out Admission value, bool retainInput = false)
        {
            lock (gate)
            {
                if (!channel.Reader.TryRead(out value!)) return false;
                if (value.Focus is null) { if (!retainInput) inputs--; } else changes--;
                return true;
            }
        }
        internal void ReleaseRetainedInput() { lock (gate) inputs--; }
        internal void TryComplete(Exception? error)
        { lock (gate) { completed = true; channel.Writer.TryComplete(error); } }
    }

}
