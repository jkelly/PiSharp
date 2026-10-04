using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Tui.Input;

namespace PiSharp.Cli.Interactive;

// Only the input owner applies a correlated removal receipt to its original editor.
internal sealed record TerminalQueueRestoration(long Generation, Guid EditorLifetime,
    string PreviousText, string Text, int Count)
{
    private int claimed;
    internal bool TryClaim() => Interlocked.Exchange(ref claimed, 1) == 0;
}

internal sealed partial class InteractiveSessionFrontend
{
    private sealed record QueueRestorationRequest(string Id, string Command, long Generation, Guid EditorLifetime, string PreviousText)
    {
        internal TaskCompletionSource<TerminalQueueRestoration?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private QueueRestorationRequest? queueRestoration;
    private bool terminalStreaming;
    internal bool IsTerminalStreaming { get { lock (state) return terminalStreaming; } }
    internal long CaptureSessionGeneration() { lock (state) return sessionGeneration; }
    internal (long Generation, bool Restoring) CaptureQueueInputState()
    { lock (state) return (sessionGeneration, queueRestoration is not null || navigation is not null); }

    internal async Task<TerminalQueueRestoration?> RestoreQueuedMessagesAsync(
        TerminalTextEditorPasteController editor, CancellationToken token, bool abort = false, long? expectedGeneration = null)
    {
        QueueRestorationRequest request;
        Func<JsonData, CancellationToken, Task> sender;
        lock (state)
        {
            token.ThrowIfCancellationRequested();
            if (!nativePresentation || dialog is not null || navigation is not null || queueRestoration is not null || send is null) return null;
            if (expectedGeneration is { } expected && expected != sessionGeneration || abort && !terminalStreaming) return null;
            if (sequence == long.MaxValue) throw new InvalidOperationException("Terminal request identities are exhausted.");
            var text = editor.GetExpandedText(); ChatEditor.Validate(text);
            request = new("chat-restore-" + (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
                abort ? "pisharp_interrupt" : "pisharp_restore_queue", sessionGeneration, editor.LayoutIdentity.EditorLifetimeId, text);
            queueRestoration = request; sender = send;
        }
        try
        {
            // Always join the actual send, including late cancellation. A preview snapshot
            // never authorizes restoration; only this command's exact removal response does.
            await sender(JsonData.Parse(JsonSerializer.Serialize(new
            {
                id = request.Id, type = request.Command, generation = request.Generation,
                currentText = request.PreviousText
            })), token).ConfigureAwait(false);
            return await request.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (state) if (ReferenceEquals(queueRestoration, request)) queueRestoration = null;
        }
    }

    internal bool ApplyQueueRestoration(TerminalQueueRestoration result, TerminalTextEditorPasteController editor)
    {
        lock (state)
        {
            if (!result.TryClaim() || result.Generation != sessionGeneration ||
                result.EditorLifetime != editor.LayoutIdentity.EditorLifetimeId || editor.GetExpandedText() != result.PreviousText)
                return false;
            // An unrelated history acknowledgment may advance layout revision while the
            // request is pending. It does not change the captured editor text or lifetime.
            if (result.Count != 0) editor.SetText(result.Text);
            return true;
        }
    }

    // Called under state by the ordered host output observer. Never await the input owner here.
    private bool ObserveQueueRestoration(JsonElement body, out string? display)
    {
        display = null;
        var command = body.GetProperty("command").GetString();
        if (command is not ("pisharp_restore_queue" or "pisharp_interrupt")) return false;
        if (queueRestoration is not { } request || !body.TryGetProperty("id", out var id) || id.GetString() != request.Id)
            return true; // Duplicate, canceled and foreign responses cannot edit an editor.
        if (command != request.Command) return true;
        if (request.Completion.Task.IsCompleted) return true;
        try
        {
            if (!body.GetProperty("success").GetBoolean())
            {
                request.Completion.TrySetResult(null);
                display = "[queue] Unable to restore queued messages.";
                return true;
            }
            var data = body.GetProperty("data");
            var generation = data.GetProperty("generation").GetInt64();
            var count = data.GetProperty("count").GetInt32();
            var text = data.GetProperty("text").GetString() ?? throw new InvalidOperationException("Queue restoration text is null.");
            ChatEditor.Validate(text);
            if (count < 0 || count > 512 || generation != request.Generation || count == 0 && text != request.PreviousText)
                throw new InvalidOperationException("Queue restoration response does not match its request.");
            if (sessionGeneration != generation)
            {
                request.Completion.TrySetResult(null);
                return true;
            }
            request.Completion.TrySetResult(new(generation, request.EditorLifetime, request.PreviousText, text, count));
            // The input owner still has to validate and apply the receipt. Its next
            // actual draft paint is the success indication, not an optimistic status.
            display = count == 0 ? "[queue] No queued messages to restore." : null;
            return true;
        }
        catch
        {
            // Malformed host output fails its observer, but must not strand the input
            // owner's response waiter while the host performs its original cleanup.
            request.Completion.TrySetResult(null);
            throw;
        }
    }
}
