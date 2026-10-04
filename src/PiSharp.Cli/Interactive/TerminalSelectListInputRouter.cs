using PiSharp.Tui;
using PiSharp.Tui.Input;

namespace PiSharp.Cli.Interactive;

/// <summary>
/// Tags the existing focus admission with a selector transition. No additional reader,
/// render loop, timer or unjoined cancellation surrogate is introduced. The eight-slot
/// focus owner bounds transitions and acknowledges only after the actual paint settles.
/// </summary>
internal sealed class TerminalSelectListInputRouter(InteractiveSessionFrontend frontend,
    TerminalSelectListDialogController controller, TerminalEditorFocusOwner focusOwner)
{
    internal sealed record Change(TerminalSelectListInputRouter Owner, CancellationToken AdmissionToken);
    private readonly AsyncLocal<Change?> scheduling = new();
    private readonly object gate = new();
    private TerminalEditorFocusOwner.Attachment? attachment;
    private TerminalSelectListDialogSnapshot? scheduled;
    private Task lastTransition = Task.CompletedTask;
    private bool attached, closed, leased, restoreEditorFocus;

    internal TerminalSelectListDialogSnapshot? CaptureInputTarget() => frontend.CaptureSelectDialog();
    internal Change? CaptureChange() => scheduling.Value;

    internal void AttachInput(TerminalEditorFocusOwner.Attachment value)
    {
        lock (gate)
        {
            if (attached || closed) throw new InvalidOperationException("Selector input may attach once.");
            attachment = value; attached = true;
        }
    }

    internal void DetachInput()
    {
        lock (gate) { closed = true; attachment = null; }
        // The input owner disposes its focus attachment, settling every admitted request,
        // before waiting for RPC receipt/output cleanup which can itself retire a dialog.
    }

    internal ValueTask SynchronizeAsync(CancellationToken token)
    {
        TerminalEditorFocusOwner.Attachment? current;
        lock (gate) { if (closed) return ValueTask.CompletedTask; current = attachment; }
        if (current is null) throw new InvalidOperationException("Selector input must attach before starting the RPC host.");
        TerminalEditorFocusSnapshot focus;
        try { focus = current.Snapshot; }
        catch (TerminalEditorFocusException error) when (error.Failure == TerminalEditorFocusFailure.Detached)
        { lock (gate) { if (closed) return ValueTask.CompletedTask; } throw; }
        TaskCompletionSource completion; bool target;
        lock (gate)
        {
            if (closed) return ValueTask.CompletedTask;
            // Capture current authority and admit the original focus request under one
            // router turn. Concurrent publications cannot enqueue a stale restore last.
            var next = frontend.CaptureSelectDialog();
            if (TerminalSelectListDialogController.SameLifetime(scheduled, next) && scheduled?.ResponsePending == next?.ResponsePending)
                return new(lastTransition);
            if (next is not null && !leased) { restoreEditorFocus = focus.EditorOwnsFocus; leased = true; }
            target = next is null ? (leased ? restoreEditorFocus : focus.EditorOwnsFocus) : false;
            if (next is null) leased = false;
            scheduled = next;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lastTransition = completion.Task;
            // The focus admission reads only this call's AsyncLocal tag; it takes no
            // router gate. This preserves transition admission order without awaiting here.
            _ = JoinTransition(target, token, completion);
        }
        // SetEditorFocusAsync synchronously admits its original Request. AsyncLocal tags
        // only this call, including when another producer concurrently requests focus.
        return new(completion.Task);
    }

    private async Task JoinTransition(bool target, CancellationToken token, TaskCompletionSource completion)
    {
        try
        {
            var previous = scheduling.Value; Task original;
            scheduling.Value = new(this, token);
            try { original = focusOwner.SetEditorFocusAsync(target, token); }
            finally { scheduling.Value = previous; }
            await original.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (TerminalEditorFocusException error) when (error.Failure == TerminalEditorFocusFailure.Detached)
        {
            // Input EOF has already joined its entered callbacks and read before detaching.
            // An unentered transition is then retired with the RPC owner, without a new paint.
            lock (gate)
            { if (closed) completion.TrySetResult(); else completion.TrySetException(error); }
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal async ValueTask ApplyAsync(Change change, CancellationToken token)
    {
        if (!ReferenceEquals(change.Owner, this)) throw new InvalidOperationException("Foreign selector transition.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(change.AdmissionToken, token);
        try { await controller.ReconcileAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (error.CancellationToken == linked.Token && change.AdmissionToken.IsCancellationRequested)
        { throw new OperationCanceledException(change.AdmissionToken); }
    }

    internal ValueTask HandleInputAsync(TerminalInputEvent input, TerminalSelectListDialogSnapshot target, CancellationToken token)
    {
        TerminalEditorFocusOwner.Attachment? current;
        lock (gate) current = closed ? null : attachment;
        return current is null || current.Snapshot.EditorOwnsFocus ? ValueTask.CompletedTask :
            controller.HandleInputAsync(input, target, token);
    }
}
