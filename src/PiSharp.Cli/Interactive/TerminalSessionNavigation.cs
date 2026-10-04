using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Tui.Input;

namespace PiSharp.Cli.Interactive;

public enum TerminalDoubleEscapeAction { Tree, Fork, None }
internal sealed record TerminalSessionNavigationReceipt(long Generation, Guid EditorLifetime, string PreviousText,
    string? EditorText, string Mode, string Disposition)
{
    private int claimed;
    internal bool TryClaim() => Interlocked.Exchange(ref claimed, 1) == 0;
}

internal sealed partial class InteractiveSessionFrontend
{
    private sealed class NavigationRequest(string id, string mode, long generation, Guid editorLifetime, string previousText, CancellationToken token)
    {
        internal string Id = id;
        internal readonly string CaptureId = id;
        internal readonly string Mode = mode, PreviousText = previousText;
        internal readonly long Generation = generation;
        internal readonly Guid EditorLifetime = editorLifetime;
        internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource<bool> Captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int?> Choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<TerminalSessionNavigationReceipt?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? ViewId;
        internal ImmutableArray<string> TargetIds = [], Labels = [];
        internal TerminalSelectListDialogSnapshot? Dialog;
        internal int InitialIndex;
        internal bool Selecting;
    }
    private NavigationRequest? navigation;
    private TaskCompletionSource navigationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal (TerminalSelectListDialogSnapshot? Dialog, Task Changed) CaptureNavigationChange()
    { lock (state) return (CaptureSelectDialog(), navigationChanged.Task); }
    private void NavigationChanged()
    {
        var previous = navigationChanged; navigationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    internal async Task<TerminalSessionNavigationReceipt?> NavigateSessionAsync(TerminalTextEditorPasteController editor,
        TerminalDoubleEscapeAction action, long expectedGeneration, CancellationToken token)
    {
        NavigationRequest request; Func<JsonData, CancellationToken, Task> sender;
        var failed = false;
        lock (state)
        {
            token.ThrowIfCancellationRequested();
            if (!nativePresentation || dialog is not null || navigation is not null || queueRestoration is not null ||
                terminalStreaming || send is null || expectedGeneration != sessionGeneration || action == TerminalDoubleEscapeAction.None ||
                TerminalChatInput.TrimExpandedSubmission(editor.GetExpandedText()).Length != 0) return null;
            request = new(NextNavigationId(), action == TerminalDoubleEscapeAction.Tree ? "tree" : "fork",
                sessionGeneration, editor.LayoutIdentity.EditorLifetimeId, editor.GetExpandedText(), token);
            navigation = request; sender = send;
        }
        try
        {
            await sender(JsonData.Parse(JsonSerializer.Serialize(new { id = request.Id, type = "pisharp_capture_navigation",
                generation = request.Generation, mode = request.Mode })), token).ConfigureAwait(false);
            if (!await request.Captured.Task.WaitAsync(token).ConfigureAwait(false)) return null;
            int? choice;
            while (true)
            {
                choice = await request.Choice.Task.WaitAsync(token).ConfigureAwait(false);
                if (choice is null) return null;
                if (request.Mode == "fork") break;
                lock (state)
                {
                    if (!ReferenceEquals(navigation, request)) return null;
                    request.Choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    request.Dialog = new(Guid.NewGuid(), null, request.Generation, "Navigate without summary?",
                        ["Navigate without summary"], false, HostNavigation: true);
                    NavigationChanged();
                }
                if (selectListRouter is not null) await selectListRouter.SynchronizeAsync(token).ConfigureAwait(false);
                if (await request.Choice.Task.WaitAsync(token).ConfigureAwait(false) is not null) break;
                lock (state)
                {
                    if (!ReferenceEquals(navigation, request) || request.Generation != sessionGeneration || dialog is not null) return null;
                    request.Choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    request.Dialog = NavigationDialog(request, choice.Value);
                    NavigationChanged();
                }
                if (selectListRouter is not null) await selectListRouter.SynchronizeAsync(token).ConfigureAwait(false);
            }
            lock (state)
            {
                if (!ReferenceEquals(navigation, request) || request.Generation != sessionGeneration || dialog is not null) return null;
                request.Id = NextNavigationId(); request.Selecting = true;
                request.Dialog = request.Dialog! with { ResponsePending = true };
                NavigationChanged();
            }
            // Join the original sender, including actual core publication and physical output.
            await sender(JsonData.Parse(JsonSerializer.Serialize(new { id = request.Id, type = "pisharp_select_navigation",
                generation = request.Generation, mode = request.Mode, viewId = request.ViewId,
                targetId = request.TargetIds[choice!.Value] })), token).ConfigureAwait(false);
            var receipt = await request.Completion.Task.WaitAsync(token).ConfigureAwait(false);
            if (receipt is { Disposition: "Selected" })
                await sender(JsonData.Parse(JsonSerializer.Serialize(new { id = request.Id + "-history", type = "get_messages" })), token).ConfigureAwait(false);
            return receipt;
        }
        catch { failed = true; throw; }
        finally
        {
            try
            {
                // Ordinary chooser cancellation retires retained host authority too. Owned
                // shutdown already closes/join-clears the dispatcher, so it needs no new admission.
                if (!failed && !request.Selecting && !token.IsCancellationRequested)
                {
                    lock (state) request.Id = NextNavigationId();
                    await sender(JsonData.Parse(JsonSerializer.Serialize(new { id = request.Id, type = "pisharp_retire_navigation",
                        generation = request.Generation, mode = request.Mode, captureId = request.CaptureId })), token).ConfigureAwait(false);
                    await request.Retired.Task.WaitAsync(token).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (state) if (ReferenceEquals(navigation, request)) { navigation = null; NavigationChanged(); }
                if (selectListRouter is not null) await selectListRouter.SynchronizeAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private string NextNavigationId()
    {
        if (sequence == long.MaxValue) throw new InvalidOperationException("Terminal request identities are exhausted.");
        return "chat-navigation-" + (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private async ValueTask SynchronizeNavigationAsync(CancellationToken outputToken)
    {
        CancellationToken owned;
        lock (state) owned = navigation?.Token ?? outputToken;
        try { await selectListRouter!.SynchronizeAsync(owned).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (owned.IsCancellationRequested && error.CancellationToken == owned) { }
    }
    private static TerminalSelectListDialogSnapshot NavigationDialog(NavigationRequest request, int initial) =>
        new(Guid.NewGuid(), null, request.Generation, request.Mode == "tree" ? "Session tree" : "Fork from user message",
            request.Labels, false, HostNavigation: true, InitialIndex: initial);

    private bool RespondNavigation(TerminalSelectListDialogSnapshot expected, int? index, bool canceled)
    {
        lock (state)
        {
            if (navigation is not { } request || request.Selecting ||
                !TerminalSelectListDialogController.SameLifetime(expected, request.Dialog) || expected.ResponsePending ||
                !canceled && (index is null || (uint)index.Value >= (uint)expected.Options.Length)) return false;
            request.Dialog = request.Dialog! with { ResponsePending = true };
            NavigationChanged();
            return request.Choice.TrySetResult(canceled ? null : index);
        }
    }

    private void RetireNavigationOnSwitch()
    {
        if (navigation is not { Selecting: false } request) return; // Fork awaits its correlated post-switch receipt.
        request.Captured.TrySetResult(false); request.Choice.TrySetResult(null);
        NavigationChanged();
    }

    internal bool ApplySessionNavigation(TerminalSessionNavigationReceipt receipt, TerminalTextEditorPasteController editor)
    {
        lock (state)
        {
            if (!receipt.TryClaim() || receipt.Generation != sessionGeneration ||
                receipt.EditorLifetime != editor.LayoutIdentity.EditorLifetimeId || editor.GetExpandedText() != receipt.PreviousText)
                return false;
            if (receipt.Disposition == "Selected" && receipt.EditorText is { } text &&
                (receipt.Mode == "fork" || TerminalChatInput.TrimExpandedSubmission(editor.GetExpandedText()).Length == 0)) editor.SetText(text);
            return true;
        }
    }

    // Called by ordered RPC output while holding state. No input-owner await here.
    private bool ObserveNavigation(JsonElement body, out string? display)
    {
        display = null;
        var command = body.GetProperty("command").GetString();
        if (command is not ("pisharp_capture_navigation" or "pisharp_select_navigation" or "pisharp_retire_navigation")) return false;
        if (navigation is not { } request || !body.TryGetProperty("id", out var id) || id.GetString() != request.Id) return true;
        if (command == "pisharp_retire_navigation")
        {
            try
            {
                if (body.GetProperty("success").GetBoolean()) request.Retired.TrySetResult();
                else request.Retired.TrySetException(new InvalidOperationException("Navigation authority retirement failed."));
            }
            catch (Exception error) { request.Retired.TrySetException(error); throw; }
            return true;
        }
        if ((command == "pisharp_select_navigation") != request.Selecting) return true;
        try
        {
            if (!body.GetProperty("success").GetBoolean())
            { request.Captured.TrySetResult(false); request.Completion.TrySetResult(null); display = "[navigation] Selection is unavailable or has changed."; return true; }
            var data = body.GetProperty("data");
            if (data.GetProperty("mode").GetString() != request.Mode) throw new InvalidOperationException("Foreign navigation mode.");
            var generation = data.GetProperty("generation").GetInt64();
            if (command == "pisharp_capture_navigation")
            {
                if (request.Captured.Task.IsCompleted) return true;
                if (generation != request.Generation || generation != sessionGeneration) { request.Captured.TrySetResult(false); return true; }
                request.ViewId = data.GetProperty("viewId").GetString()!;
                if (!Guid.TryParseExact(request.ViewId, "N", out _)) throw new InvalidOperationException("Invalid navigation view identity.");
                var choices = data.GetProperty("choices");
                if (choices.GetArrayLength() > 512) throw new InvalidOperationException("Navigation choices exceed their bound.");
                request.TargetIds = choices.EnumerateArray().Select(choice => choice.GetProperty("entryId").GetString()!).ToImmutableArray();
                request.Labels = choices.EnumerateArray().Select(choice => choice.GetProperty("label").GetString()!).ToImmutableArray();
                if (request.TargetIds.Any(target => string.IsNullOrEmpty(target)) || request.TargetIds.Distinct(StringComparer.Ordinal).Count() != request.TargetIds.Length)
                    throw new InvalidOperationException("Invalid navigation targets.");
                if (request.Labels.Sum(label => label.Length) > 65_536) throw new InvalidOperationException("Navigation labels exceed their bound.");
                request.InitialIndex = data.GetProperty("initialIndex").GetInt32();
                if (request.TargetIds.IsEmpty) { request.Captured.TrySetResult(false); return true; }
                if ((uint)request.InitialIndex >= (uint)request.TargetIds.Length) throw new InvalidOperationException("Invalid navigation index.");
                request.Dialog = NavigationDialog(request, request.InitialIndex);
                NavigationChanged();
                request.Captured.TrySetResult(true);
            }
            else
            {
                if (request.Completion.Task.IsCompleted) return true;
                if (!request.Selecting || data.GetProperty("viewId").GetString() != request.ViewId ||
                    generation != sessionGeneration || generation != request.Generation &&
                    (request.Mode != "fork" || generation != checked(request.Generation + 1)))
                { request.Completion.TrySetResult(null); return true; }
                var text = data.GetProperty("editorText").GetString(); if (text is not null) ChatEditor.Validate(text);
                var disposition = data.GetProperty("disposition").GetString()!;
                if (disposition is not ("Selected" or "NoOp" or "Vetoed" or "Aborted")) throw new InvalidOperationException("Invalid navigation disposition.");
                request.Completion.TrySetResult(new(generation, request.EditorLifetime, request.PreviousText, text, request.Mode, disposition));
            }
            return true;
        }
        catch { request.Captured.TrySetResult(false); request.Completion.TrySetResult(null); throw; }
    }
}
