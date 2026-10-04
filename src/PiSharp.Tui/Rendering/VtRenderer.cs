using System.Globalization;
using System.Text;

namespace PiSharp.Tui.Rendering;

/// <summary>
/// Serialized full/changed-row VT writes over a borrowed console lease. The host owns terminal acquisition,
/// dimensions and exclusive output access. Successful physical completion is the frame-cache commit boundary.
/// </summary>
public sealed class VtRenderer : IAsyncDisposable
{
    private readonly IConsoleTerminal terminal;
    private readonly TerminalRenderLimits limits;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly TaskCompletionSource drained = NewGate();
    private TerminalFrame? cached;
    private Task tail = Task.CompletedTask;
    private Task? disposal;
    private long epoch, started, settled;
    private int pending;

    public VtRenderer(IConsoleTerminal terminal, TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(terminal); this.terminal = terminal;
        this.limits = limits ?? new(); this.limits.Validate();
    }

    public TerminalRendererSnapshot Snapshot
    {
        get { lock (gate) return new(pending, cached is not null, disposal is not null, started, settled); }
    }

    /// <summary>Call on resize or any host output outside this renderer, even when dimensions stay equal.</summary>
    public void Invalidate()
    {
        lock (gate) { cached = null; epoch++; }
    }

    public ValueTask<TerminalRenderResult> RenderAsync(TerminalFrame frame, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (token.IsCancellationRequested) { Invalidate(); token.ThrowIfCancellationRequested(); }
        limits.ValidateViewport(frame.Rows.Length, frame.Columns);
        if (frame.TextCharacters > limits.MaximumFrameCharacters || frame.LargestGraphemeCharacters > limits.MaximumGraphemeCharacters ||
            frame.Rows.Any(value => value.Text.Length > limits.MaximumFrameCharacters) ||
            frame.IsSourcePresentation && FullFrameCharacterCount(frame) > limits.MaximumFrameCharacters)
            throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
        Task predecessor; TaskCompletionSource turn;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (pending >= limits.MaximumPendingRenders) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
            pending++; predecessor = tail; turn = NewGate(); tail = turn.Task;
        }
        return new(RenderOwnedAsync(frame, token, predecessor, turn));
    }

    private async Task<TerminalRenderResult> RenderOwnedAsync(TerminalFrame frame, CancellationToken caller,
        Task predecessor, TaskCompletionSource turn)
    {
        CancellationToken ownedCancellation = default;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, stop.Token);
            ownedCancellation = linked.Token;
            // Owned completion gates preserve admission order, including cancelled waiting calls.
            // Waiting cancellation settles after its predecessor, so it cannot let a later write overlap.
            await predecessor.ConfigureAwait(false);
            string output; TerminalRenderKind kind; long version;
            lock (gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                (output, kind) = Encode(frame, cached); version = epoch;
                if (kind == TerminalRenderKind.Unchanged) return new(kind, 0, true);
                cached = null; started++;
            }
            try
            {
                // Do not detach a write with WaitAsync: the actual transport must settle before releasing this turn.
                await terminal.WriteAsync(output.AsMemory(), linked.Token).ConfigureAwait(false);
            }
            finally { lock (gate) settled++; }
            lock (gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                var committed = epoch == version && disposal is null;
                if (committed) cached = frame;
                return new(kind, output.Length, committed);
            }
        }
        catch (OperationCanceledException canceled) when (caller.IsCancellationRequested &&
            ownedCancellation.CanBeCanceled && canceled.CancellationToken == ownedCancellation)
        { Invalidate(); throw new OperationCanceledException(caller); }
        catch (OperationCanceledException canceled) when (stop.IsCancellationRequested &&
            ownedCancellation.CanBeCanceled && canceled.CancellationToken == ownedCancellation)
        { Invalidate(); throw new ObjectDisposedException(nameof(VtRenderer)); }
        catch { Invalidate(); throw; }
        finally
        {
            lock (gate) { pending--; if (disposal is not null && pending == 0) drained.TrySetResult(); }
            turn.TrySetResult();
        }
    }

    private (string Output, TerminalRenderKind Kind) Encode(TerminalFrame frame, TerminalFrame? previous)
    {
        var full = previous is null || frame.Columns != previous.Columns || frame.Rows.Length != previous.Rows.Length ||
            !string.Equals(frame.WidthPolicyId, previous.WidthPolicyId, StringComparison.Ordinal);
        var different = full || frame.Cursor != previous!.Cursor || frame.PositionCursor != previous.PositionCursor ||
            frame.InverseSpan != previous.InverseSpan || !frame.Rows.SequenceEqual(previous.Rows);
        if (!different) return ("", TerminalRenderKind.Unchanged);
        var output = new StringBuilder(); Append(output, "\u001b[0m");
        if (full) Append(output, "\u001b[2J");
        for (var row = 0; row < frame.Rows.Length; row++)
        {
            if (!full && frame.Rows[row] == previous!.Rows[row] && frame.StyleForRow(row) == previous.StyleForRow(row)) continue;
            Move(output, row, 0); Append(output, "\u001b[2K");
            var text = frame.Rows[row].Text;
            if (frame.StyleForRow(row) is { } span)
            {
                Append(output, text[..span.StartUtf16]); Append(output, "\u001b[7m");
                Append(output, text.Substring(span.StartUtf16, span.LengthUtf16)); Append(output, "\u001b[0m");
                Append(output, text[(span.StartUtf16 + span.LengthUtf16)..]);
            }
            else Append(output, text);
        }
        // CUP clears delayed auto-wrap after a row ending at the right edge; no data newline is emitted.
        if (frame.PositionCursor) Move(output, frame.Cursor.Row, frame.Cursor.Column);
        Append(output, frame.Cursor.Visible ? "\u001b[?25h" : "\u001b[?25l");
        return (output.ToString(), full ? TerminalRenderKind.Full : TerminalRenderKind.Diff);
    }

    private void Move(StringBuilder output, int row, int column)
    {
        Append(output, "\u001b["); Append(output, (row + 1).ToString(CultureInfo.InvariantCulture));
        Append(output, ";"); Append(output, (column + 1).ToString(CultureInfo.InvariantCulture)); Append(output, "H");
    }

    // The factory and renderer use the same exact full-write admission budget, including fixed styles.
    internal static long FullFrameCharacterCount(TerminalFrame frame)
    {
        long count = 8 + 6; // initial SGR reset, ED and final cursor visibility
        for (var row = 0; row < frame.Rows.Length; row++)
            count += MoveCharacters(row, 0) + 4 + frame.Rows[row].Text.Length + (frame.StyleForRow(row) is null ? 0 : 8);
        if (frame.PositionCursor) count += MoveCharacters(frame.Cursor.Row, frame.Cursor.Column);
        return count;
    }
    private static int MoveCharacters(int row, int column) => 4 + (row + 1).ToString(CultureInfo.InvariantCulture).Length +
        (column + 1).ToString(CultureInfo.InvariantCulture).Length;

    private void Append(StringBuilder output, string value)
    {
        if (output.Length > limits.MaximumFrameCharacters - value.Length)
            throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
        output.Append(value);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task closing;
        lock (gate)
        {
            if (disposal is null)
            {
                completion = NewGate(); disposal = completion.Task; cached = null; epoch++;
                if (pending == 0) drained.TrySetResult();
            }
            closing = disposal;
        }
        if (completion is not null) _ = CloseAsync(completion);
        return new(closing);
    }

    private async Task CloseAsync(TaskCompletionSource completion)
    {
        try
        {
            Exception? failure = null;
            try { stop.Cancel(); } catch (Exception error) { failure = error; }
            await drained.Task.ConfigureAwait(false); stop.Dispose();
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
        catch (Exception error) { completion.TrySetException(error); }
        // The terminal is borrowed. Only its owning host may dispose the lease and restore console state.
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
