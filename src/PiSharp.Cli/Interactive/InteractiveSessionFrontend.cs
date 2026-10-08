using System.Globalization;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Rpc.Ui;
using PiSharp.CodingAgent.Resources;

namespace PiSharp.Cli.Interactive;

/// <summary>Bounded cooked editor and presentation; the RPC host alone owns accepted state.</summary>
internal interface IInteractiveSessionPresentation
{
    ValueTask PresentAsync(string displayText, CancellationToken token);
}

internal interface IInteractiveAssistantStreamingPresentation
{
    ValueTask PresentAssistantDeltaAsync(string displayText, CancellationToken token);
}

internal sealed partial class InteractiveSessionFrontend : IDisposable, IRpcExtensionUiPresentationObserver
{
    private readonly IInteractiveSessionPresentation presentation;
    private readonly bool nativePresentation;
    private readonly TerminalSubmissionReceipts? submissionReceipts;
    internal InteractiveSessionFrontend(TextWriter output, bool nativePresentation = false)
        : this(new WriterPresentation(output), nativePresentation) { }
    internal InteractiveSessionFrontend(IInteractiveSessionPresentation presentation, bool nativePresentation = false,
        TerminalSubmissionReceipts? submissionReceipts = null)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        this.presentation = presentation;
        this.nativePresentation = nativePresentation;
        this.submissionReceipts = submissionReceipts;
    }
    private sealed class WriterPresentation(TextWriter output) : IInteractiveSessionPresentation
    {
        public async ValueTask PresentAsync(string displayText, CancellationToken token)
        {
            await output.WriteAsync(displayText.AsMemory(), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
        }
    }
    private readonly object state = new();
    private readonly SemaphoreSlim rendering = new(1, 1);
    private readonly ChatEditor draft = new(), dialogEditor = new();
    private sealed record VisibleDialog(JsonData Record, RpcExtensionUiPresentationIdentity? Identity)
    { internal Guid LifetimeId { get; } = Guid.NewGuid(); }
    private TerminalSelectListInputRouter? selectListRouter;
    private readonly Queue<VisibleDialog> dialogs = new();
    private readonly Dictionary<string, string> widgets = new(StringComparer.Ordinal), statuses = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource commandsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> extensionCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> promptCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> completionRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Generation, string? Cwd)> catalogRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> sessionChoices = [];
    private long sessionGeneration = 1;
    private string? nextSessionCursor, catalogWorkingDirectory, latestCatalogRequest;
    private Func<JsonData, CancellationToken, Task>? send;
    private VisibleDialog? dialog;
    private bool dialogResponsePending;
    private bool startupInputEnded;
    private readonly Dictionary<int, StringBuilder> assistantPresentedBlocks = new();
    private int assistantPresentedCharacters;
    private long sequence, uiCharacters;
    internal Task Ready => ready.Task;
    internal void Bind(Func<JsonData, CancellationToken, Task> sender) => send = sender;
    internal Task StartAsync(CancellationToken token) => Send(new { id = "chat-start", type = "get_state" }, token);
    internal void EndStartupInput()
    {
        // Called by the existing terminal input owner before publishing ordinary EOF to RPC.
        lock (state) startupInputEnded = true;
    }

    internal void BindSelectList(TerminalSelectListInputRouter router)
    {
        if (!nativePresentation || selectListRouter is not null) throw new InvalidOperationException("Native selector may bind once.");
        selectListRouter = router;
    }

    internal TerminalSelectListDialogSnapshot? CaptureSelectDialog()
    {
        lock (state)
        {
            if (dialog is null && navigation?.Dialog is { } hostDialog && hostDialog.SessionGeneration == sessionGeneration) return hostDialog;
            if (dialog?.Identity is not { } identity || identity.SessionGeneration != sessionGeneration ||
                dialog.Record.Value.GetProperty("method").GetString() != "select" ||
                dialog.Record.Value.GetProperty("id").GetString() != identity.RequestId) return null;
            var body = dialog.Record.Value;
            return new(dialog.LifetimeId, identity, sessionGeneration, body.GetProperty("title").GetString()!,
                body.GetProperty("options").EnumerateArray().Select(option => option.GetString()!).ToImmutableArray(), dialogResponsePending);
        }
    }

    internal async Task<bool> RespondSelectAsync(TerminalSelectListDialogSnapshot expected, int? optionIndex,
        bool canceled, CancellationToken token)
    {
        if (expected.HostNavigation) return RespondNavigation(expected, optionIndex, canceled);
        JsonData response; Func<JsonData, CancellationToken, Task> sender;
        lock (state)
        {
            token.ThrowIfCancellationRequested();
            var current = CaptureSelectDialog();
            if (current is null || !TerminalSelectListDialogController.SameLifetime(expected, current) || current.ResponsePending || send is null) return false;
            object packet;
            if (canceled) packet = new { type = "extension_ui_response", id = current.Identity!.RequestId, cancelled = true };
            else
            {
                if (optionIndex is not { } index || (uint)index >= (uint)current.Options.Length || current.Options[index].Length == 0) return false;
                packet = new { type = "extension_ui_response", id = current.Identity!.RequestId, value = current.Options[index] };
            }
            response = JsonData.Parse(JsonSerializer.Serialize(packet)); sender = send; dialogResponsePending = true;
        }
        // Keep the pending owner even if a send fails after admission. Only host retirement
        // releases it; cancellation never detaches an already-entered sender callback.
        await sender(response, token).ConfigureAwait(false); return true;
    }

    internal async ValueTask ObserveAsync(JsonData record, CancellationToken token)
    {
        var body = record.Value; var type = body.GetProperty("type").GetString(); string? display = null; var started = false;
        var assistantDelta = false;
        TerminalPendingQueueSnapshot? pendingQueue = null;
        lock (state)
        {
            switch (type)
            {
                case "response":
                    if (ObserveNavigation(body, out display)) break;
                    if (ObserveQueueRestoration(body, out display)) break;
                    var success = body.GetProperty("success").GetBoolean();
                    var id = body.TryGetProperty("id", out var identity) ? identity.GetString() : null;
                    if (id == "chat-start")
                    {
                        if (!success)
                        {
                            // Only ordinary EOF's exact canceled-before-acceptance response is
                            // expected here. Rejections from a live startup or other failures
                            // still fail the original output callback.
                            if (startupInputEnded && body.GetProperty("command").GetString() == "get_state" &&
                                body.TryGetProperty("error", out var startupError) &&
                                startupError.GetString() == "Command canceled before acceptance.") break;
                            throw new InvalidOperationException("Interactive startup state was rejected.");
                        }
                        terminalStreaming = body.GetProperty("data").GetProperty("isStreaming").GetBoolean();
                        if (body.GetProperty("data").TryGetProperty("pisharpGeneration", out var initialGeneration))
                        {
                            if (!initialGeneration.TryGetInt64(out var startupGeneration) || startupGeneration < 1)
                                throw new InvalidOperationException("Interactive startup generation is invalid.");
                            sessionGeneration = startupGeneration;
                        }
                        // A valid response racing EOF cannot restart history admission. Keep
                        // malformed successful responses on the existing validation path.
                        if (startupInputEnded && body.GetProperty("command").GetString() == "get_state") break;
                        display = "PiSharp interactive cooked/RPC\n/edit starts a multiline draft; /save submits; /cancel discards; /new, /clone, /fork <entryId>, /fork-at <entryId>, /tree, /switch <absolutePath>; /context-omit <entryId>, /context-replace <entryId> <JSON object with content>; /compact [focus], /compact-all, /branch-summary <entryId|root>, /auto-compact on|off; /sessions [cwd], /sessions-next, /resume <number> [leafId]; /complete <command> [prefix] shows completions; /abort, /state, /steer, /follow-up, /clear-queue, /quit.\n[state] " +
                            (body.GetProperty("data").GetProperty("isStreaming").GetBoolean() ? "running" : "idle"); started = true;
                    }
                    else if (success && body.GetProperty("command").GetString() == "get_messages")
                    {
                        var messages = body.GetProperty("data").GetProperty("messages");
                        if (messages.GetArrayLength() > 1024) throw new InvalidOperationException("Interactive history exceeds its profile.");
                        display = "[history]\n" + string.Join('\n', messages.EnumerateArray().Select(message => MessageText(message)));
                    }
                    else if (id == "chat-commands")
                    {
                        if (!success) throw new InvalidOperationException("Interactive command catalog was rejected.");
                        var commands = body.GetProperty("data").GetProperty("commands");
                        if (commands.ValueKind != JsonValueKind.Array || commands.GetArrayLength() > 1024)
                            throw new InvalidOperationException("Interactive command catalog exceeds its profile.");
                        extensionCommands.Clear();
                        promptCommands.Clear();
                        foreach (var command in commands.EnumerateArray())
                        {
                            var name = command.GetProperty("name").GetString()!;
                            if (command.TryGetProperty("source", out var source) && source.GetString() == "prompt")
                            {
                                if (name.Length is < 1 or > 4096) throw new InvalidOperationException("Interactive prompt name is invalid.");
                                ChatEditor.Validate(name);
                                promptCommands.TryAdd(name, command.GetProperty("description").GetString() ?? "");
                                continue;
                            }
                            if (name.Length is < 1 or > 128 || name.Any(value => !(char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-')))
                                throw new InvalidOperationException("Interactive extension command name is invalid.");
                            extensionCommands.Add(name);
                        }
                        commandsReady.TrySetResult();
                    }
                    else if (body.GetProperty("command").GetString() == "pisharp_complete_extension_command")
                    {
                        if (id is null || !completionRequests.Remove(id, out var completedCommand))
                            throw new InvalidOperationException("Interactive completion has no owning request.");
                        if (!success) display = "[response] rejected pisharp_complete_extension_command";
                        else
                        {
                            var completions = body.GetProperty("data").GetProperty("completions");
                            if (completions.ValueKind != JsonValueKind.Null &&
                                (completions.ValueKind != JsonValueKind.Array || completions.GetArrayLength() > 1024))
                                throw new InvalidOperationException("Interactive completion response exceeds its profile.");
                            var original = completions.GetRawText();
                            if (original.Length > 262_144) throw new InvalidOperationException("Interactive completion text exceeds its profile.");
                            display = "[completion " + completedCommand + "] " + original;
                        }
                    }
                    else if (body.GetProperty("command").GetString() == "pisharp_list_sessions")
                    {
                        if (id is null || !catalogRequests.Remove(id, out var request))
                            throw new InvalidOperationException("Session listing has no owning request.");
                        if (id != latestCatalogRequest || request.Generation != sessionGeneration) break;
                        if (!success) { sessionChoices.Clear(); nextSessionCursor = null; display = "[sessions] listing rejected; refresh with /sessions."; break; }
                        var value = body.GetProperty("data");
                        if (value.GetProperty("generation").GetInt64() != sessionGeneration) break;
                        var sessions = value.GetProperty("sessions");
                        if (sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() > 32)
                            throw new InvalidOperationException("Session listing exceeds its bounded page.");
                        var choices = new Dictionary<int, string>(); var keys = new HashSet<string>(StringComparer.Ordinal);
                        var rows = new List<string>(); var number = 0;
                        foreach (var item in sessions.EnumerateArray())
                        {
                            var key = item.GetProperty("key").GetString();
                            if (key is not { Length: 64 } || key.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || !keys.Add(key))
                                throw new InvalidOperationException("Session listing identity is invalid.");
                            choices.Add(++number, key);
                            rows.Add(number.ToString(CultureInfo.InvariantCulture) + ". " + item.GetProperty("fileName").GetString() +
                                " (" + item.GetProperty("sessionId").GetString() + ") " + item.GetProperty("cwd").GetString() +
                                (item.GetProperty("isCurrent").GetBoolean() ? " [current]" : ""));
                        }
                        var cursor = value.GetProperty("nextCursor").GetString();
                        if (cursor is { Length: > 32768 }) throw new InvalidOperationException("Session cursor exceeds its bounded view.");
                        sessionChoices.Clear(); foreach (var choice in choices) sessionChoices.Add(choice.Key, choice.Value);
                        nextSessionCursor = cursor; catalogWorkingDirectory = request.Cwd;
                        display = "[sessions]\n" + (rows.Count == 0 ? "No sessions in this page." : string.Join('\n', rows) + "\n/resume <number> [leafId]") +
                            (nextSessionCursor is null ? "" : "\n/sessions-next for the next page.") +
                            "\nskipped=" + value.GetProperty("skippedFiles").GetRawText() + " unavailable stores=" + value.GetProperty("unavailableStores").GetRawText();
                    }
                    else if (body.GetProperty("command").GetString() == "bash")
                    {
                        if (id is not null && id == pendingBash) pendingBash = null;
                        display = success ? BashStatus(body.GetProperty("data")) : "[error] Bash command failed: " +
                            (body.TryGetProperty("error", out var bashError) ? bashError.GetString() : "Unknown error");
                    }
                    else if (!success) display = "[response] rejected " + body.GetProperty("command").GetString();
                    else if (body.GetProperty("command").GetString() == "get_state")
                    {
                        var value = body.GetProperty("data"); display = "[state] " + StateDescription(value) +
                            " pending=" + value.GetProperty("pendingMessageCount").GetRawText();
                    }
                    else if (body.GetProperty("command").GetString() == "clear_queue")
                    {
                        var value = body.GetProperty("data"); display = "[cleared] steering=" + value.GetProperty("steering").GetRawText() + " followUp=" + value.GetProperty("followUp").GetRawText();
                    }
                    else if (body.GetProperty("command").GetString() == "fork")
                    {
                        var result = body.GetProperty("data");
                        if (result.GetProperty("cancelled").GetBoolean()) display = "[cancelled] fork";
                        else if (result.GetProperty("generation").GetInt64() == sessionGeneration)
                        { draft.Set(result.GetProperty("text").GetString() ?? ""); display = "[fork draft]\n" + draft.Text; }
                    }
                    else if (body.GetProperty("command").GetString() == "get_tree") display = "[tree] " + body.GetProperty("data").GetRawText();
                    else if (body.GetProperty("command").GetString() is "new_session" or "clone" or "pisharp_resume_session")
                        display = (body.GetProperty("data").GetProperty("cancelled").GetBoolean() ? "[cancelled] " : "[accepted] ") + body.GetProperty("command").GetString();
                    else display = "[accepted] " + body.GetProperty("command").GetString() +
                        (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("disposition", out var disposition) ? " " + disposition.GetString() : "");
                    break;
                case "extension_ui_request":
                    if (!nativePresentation || !IsDialog(body)) display = ApplyUi(record);
                    break;
                case "agent_start": terminalStreaming = true; ResetAssistantPresentation(); display = "[running]"; break;
                case "pisharp_recovery_started": display = "[recovering] " + body.GetProperty("reason").GetString(); break;
                case "pisharp_recovery_ended": display = "[recovery] " + body.GetProperty("status").GetString() +
                    (body.GetProperty("willRetry").GetBoolean() ? "; continuing" : ""); break;
                case "pisharp_operation_settled": display = "[operation result] " + body.GetProperty("status").GetString(); break;
                case "message_start":
                    if (body.GetProperty("message").GetProperty("role").GetString() == "assistant")
                    { ResetAssistantPresentation(); display = "[assistant started]"; }
                    break;
                case "message_update":
                    var update = body.GetProperty("assistantMessageEvent");
                    if (update.GetProperty("type").GetString() == "text_delta")
                    {
                        var delta = update.GetProperty("delta").GetString()!;
                        if (nativePresentation && presentation is IInteractiveAssistantStreamingPresentation)
                        {
                            if (delta.Length != 0) RememberAssistantBlock(update, delta, append: true);
                            display = delta; assistantDelta = true;
                        }
                        else display = "[assistant delta] " + delta;
                    }
                    else if (update.GetProperty("type").GetString() == "text_end" && nativePresentation &&
                        presentation is IInteractiveAssistantStreamingPresentation)
                    {
                        var index = AssistantBlockIndex(update); var content = update.GetProperty("content").GetString()!;
                        if (!AssistantBlockMatches(index, content))
                            display = AssistantFinalBlock(index, content);
                        RememberAssistantBlock(update, content, append: false);
                    }
                    break;
                case "message_end":
                    var message = body.GetProperty("message");
                    if (message.GetProperty("role").GetString() == "assistant")
                    {
                        var final = ReconcileAssistantMessage(message);
                        display = (final.Length == 0 ? "" : final + "\n") +
                            "[assistant ended] " + message.GetProperty("stopReason").GetString();
                        if (message.TryGetProperty("errorMessage", out var messageError) && messageError.ValueKind == JsonValueKind.String)
                            display += "\n[assistant error] " + messageError.GetString();
                        ResetAssistantPresentation();
                    }
                    break;
                case "tool_execution_start": display = "[tool start] " + body.GetProperty("toolName").GetString(); break;
                case "tool_execution_update": display = "[tool update] " + ResultText(body.GetProperty("partialResult")); break;
                case "tool_execution_end":
                    display = "[tool end] " + body.GetProperty("toolName").GetString() + " error=" + body.GetProperty("isError").GetRawText() + " " + ResultText(body.GetProperty("result")); break;
                case "agent_settled": terminalStreaming = false; if (!nativePresentation) ClearDialogs(); display = "[settled]"; break;
                case "bash_execution_update":
                    // Deltas are already sanitized by the host executor; the cooked view shows them as streamed.
                    if (body.TryGetProperty("id", out var bashUpdate) && bashUpdate.GetString() == pendingBash)
                        display = body.GetProperty("delta").GetString()!.TrimEnd('\n');
                    break;
                case "queue_update":
                    // The dispatcher serializes attachment changes with queue publication:
                    // a new-session snapshot cannot precede its session_switched record.
                    if (nativePresentation && presentation is ITerminalPendingQueuePresentation)
                        pendingQueue = TerminalPendingQueueSnapshot.Parse(body, sessionGeneration);
                    break;
                case "session_switched":
                    var generation = body.GetProperty("generation").GetInt64();
                    if (generation <= sessionGeneration) break;
                    sessionGeneration = generation; terminalStreaming = false; ResetAssistantPresentation(); sessionChoices.Clear(); nextSessionCursor = null; catalogWorkingDirectory = null;
                    RetireNavigationOnSwitch();
                    statuses.Clear(); widgets.Clear(); draft.Clear();
                    if (nativePresentation && presentation is ITerminalPendingQueuePresentation)
                        pendingQueue = TerminalPendingQueueSnapshot.Empty(generation);
                    display = "[session] " + body.GetProperty("sessionId").GetString();
                    break;
            }
        }
        // Record real admission before any subsequent presentation/output callback can fail.
        if (submissionReceipts is not null) await submissionReceipts.ObserveResponse(record).ConfigureAwait(false);
        if (pendingQueue is not null)
        {
            // RPC's existing ordered output stream owns this snapshot. Count-only state replies
            // and locally submitted command text never populate the pending preview.
            await rendering.WaitAsync(token).ConfigureAwait(false);
            try { await ((ITerminalPendingQueuePresentation)presentation).PresentPendingAsync(pendingQueue, token).ConfigureAwait(false); }
            finally { rendering.Release(); }
        }
        if (display is not null) await RenderAsync(display, token, assistantDelta).ConfigureAwait(false);
        if (selectListRouter is not null && type == "response" && body.GetProperty("command").GetString() == "pisharp_capture_navigation")
            await SynchronizeNavigationAsync(token).ConfigureAwait(false);
        else if (selectListRouter is not null && (started || type == "session_switched"))
            await selectListRouter.SynchronizeAsync(token).ConfigureAwait(false);
        if (started) lock (state) { if (!startupInputEnded) ready.TrySetResult(); }
    }

    public async ValueTask PublishedAsync(RpcExtensionUiPresentation presentation, CancellationToken cancellationToken)
    {
        if (!nativePresentation) return;
        string display;
        lock (state)
        {
            if (selectListRouter is not null && (presentation.Identity.SessionGeneration < sessionGeneration ||
                dialog?.Identity == presentation.Identity || dialogs.Any(queued => queued.Identity == presentation.Identity))) return;
            display = ApplyUi(presentation.Request, presentation.Identity);
            if (dialog is not null && navigation is { Selecting: false } request)
            { request.Dialog = null; request.Captured.TrySetResult(false); request.Choice.TrySetResult(null); NavigationChanged(); }
        }
        if (selectListRouter is not null) await selectListRouter.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        // Adopt and paint the native selector before a generic transcript paint can expose
        // its cooked description as the first title-bearing frame.
        await RenderAsync(display, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask RetiredAsync(RpcExtensionUiRetirement retirement, CancellationToken cancellationToken)
    {
        if (!nativePresentation) return;
        string? display = null;
        lock (state)
        {
            if (dialog?.Identity == retirement.Identity)
            {
                var next = FinishDialog();
                display = "[ui retired] " + retirement.Outcome + (next is null ? "" : "\n" + next);
            }
            else
            {
                var count = dialogs.Count;
                for (var index = 0; index < count; index++)
                {
                    var queued = dialogs.Dequeue();
                    if (queued.Identity == retirement.Identity) uiCharacters -= queued.Record.ToString().Length;
                    else dialogs.Enqueue(queued);
                }
            }
        }
        if (selectListRouter is not null) await selectListRouter.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        if (display is not null) await RenderAsync(display, cancellationToken).ConfigureAwait(false);
    }
    private static bool IsDialog(JsonElement body) => body.GetProperty("method").GetString() is "select" or "confirm" or "input" or "editor";
    private static string StateDescription(JsonElement value)
    {
        if (value.GetProperty("isCompacting").GetBoolean()) return "compacting";
        if (value.GetProperty("isStreaming").GetBoolean()) return "running";
        if (value.TryGetProperty("pisharpOperationActive", out var active) && active.GetBoolean())
            return value.GetProperty("pisharpOperationPhase").GetString() is "BeforeSettlement" or "Settlement" ? "settling" : "recovering";
        return "idle";
    }
    private string ApplyUi(JsonData record, RpcExtensionUiPresentationIdentity? identity = null)
    {
        var body = record.Value; var method = body.GetProperty("method").GetString();
        if (method is "select" or "confirm" or "input" or "editor")
        {
            var length = record.ToString().Length;
            if (dialogs.Count + (dialog is null ? 0 : 1) >= 32 || length > 1_048_576 - uiCharacters)
                throw new InvalidOperationException("Interactive dialogs exceed their bounded profile.");
            uiCharacters += length;
            var visible = new VisibleDialog(record, identity);
            if (dialog is not null) { dialogs.Enqueue(visible); return "[ui queued] " + method; }
            dialog = visible; return DescribeDialog();
        }
        switch (method)
        {
            case "notify": return "[notice] " + body.GetProperty("message").GetString();
            case "setStatus": Set(statuses, body.GetProperty("statusKey").GetString()!, body.TryGetProperty("statusText", out var status) ? status.GetString() : null); return "[status] " + body.GetProperty("statusKey").GetString();
            case "setWidget":
                Set(widgets, body.GetProperty("widgetKey").GetString()!, body.TryGetProperty("widgetLines", out var lines) ? string.Join('\n', lines.EnumerateArray().Select(line => line.GetString())) : null);
                return "[widget] " + body.GetProperty("widgetKey").GetString();
            case "setTitle": return "[title] " + body.GetProperty("title").GetString();
            case "set_editor_text": draft.Set(body.GetProperty("text").GetString()!); return "[draft set]\n" + draft.Text;
            default: throw new InvalidOperationException("Interactive UI method is unavailable.");
        }
    }
    private void Set(Dictionary<string, string> values, string key, string? value)
    {
        if (value is null) values.Remove(key); else values[key] = value;
        if (values.Count > 128 || statuses.Sum(item => item.Key.Length + (long)item.Value.Length) + widgets.Sum(item => item.Key.Length + (long)item.Value.Length) > 1_048_576)
            throw new InvalidOperationException("Interactive widgets exceed their bounded profile.");
    }
    private string DescribeDialog()
    {
        var body = dialog!.Record.Value; var method = body.GetProperty("method").GetString();
        var description = "[ui " + method + "] " + body.GetProperty("title").GetString();
        if (method == "select") description += "\n" + string.Join('\n', body.GetProperty("options").EnumerateArray().Select((choice, index) => (index + 1).ToString(CultureInfo.InvariantCulture) + ": " + choice.GetString()));
        if (method == "confirm") description += "\n" + body.GetProperty("message").GetString() + "\nEnter yes/no or /cancel.";
        if (method == "editor")
        { dialogEditor.Clear(); dialogEditor.Set(body.TryGetProperty("prefill", out var prefill) ? prefill.GetString()! : ""); dialogEditor.Begin(); description += "\n" + dialogEditor.Text + "\nAppend lines; /save returns the draft; /cancel denies."; }
        return description;
    }
    private string? FinishDialog()
    {
        uiCharacters -= dialog!.Record.ToString().Length; dialog = dialogs.TryDequeue(out var next) ? next : null; dialogEditor.Clear(); dialogResponsePending = false;
        return dialog is null ? null : DescribeDialog();
    }
    private string? pendingBash;
    private sealed record UserBashInput(string Command, bool Excluded);
    /// <summary>Source handling: <c>!command</c> runs and records; <c>!!command</c> runs excluded from context. An empty command is ordinary text.</summary>
    private static UserBashInput? UserBashLine(string line)
    {
        if (!line.StartsWith('!')) return null;
        var excluded = line.StartsWith("!!", StringComparison.Ordinal);
        var command = (excluded ? line[2..] : line[1..]).Trim();
        return command.Length == 0 ? null : new(command, excluded);
    }
    /// <summary>Source BashExecutionComponent completion lines (an exit code of 0 adds none).</summary>
    private static string? BashStatus(JsonElement result)
    {
        var parts = new List<string>();
        if (result.GetProperty("cancelled").GetBoolean()) parts.Add("(cancelled)");
        else if (result.TryGetProperty("exitCode", out var exit) && exit.GetInt32() != 0) parts.Add("(exit " + exit.GetRawText() + ")");
        if (result.GetProperty("truncated").GetBoolean() && result.TryGetProperty("fullOutputPath", out var full))
            parts.Add("Output truncated. Full output: " + full.GetString());
        return parts.Count == 0 ? null : string.Join('\n', parts);
    }
    private void ClearDialogs() { dialog = null; dialogs.Clear(); dialogEditor.Clear(); dialogResponsePending = false; uiCharacters = 0; }

    internal Task<bool> LineAsync(string line, CancellationToken token) => LineCoreAsync(line, null, token);
    internal async Task<bool> LineAsync(TerminalSubmittedLine submission, CancellationToken token)
    {
        if (submissionReceipts is null) throw new InvalidOperationException("Terminal frontend has no submission lifetime.");
        try { token.ThrowIfCancellationRequested(); submissionReceipts.ValidatePending(submission); return await LineCoreAsync(submission.Text, submission, token).ConfigureAwait(false); }
        catch { submissionReceipts.RejectUnacknowledged(submission); throw; }
    }
    private async Task<bool> LineCoreAsync(string line, TerminalSubmittedLine? submission, CancellationToken token)
    {
        ChatEditor.Validate(line); object? command = null; string? display = null, completionIdentity = null, catalogIdentity = null, bashIdentity = null;
        bool quit = false, localAcknowledged = false;
        lock (state)
        {
            var id = "chat-" + (++sequence).ToString(CultureInfo.InvariantCulture);
            if (line == "/quit") quit = true;
            // Source Esc precedence: a streaming run is aborted first, otherwise a running user bash command.
            else if (line == "/abort" && !terminalStreaming && pendingBash is not null) command = new { id, type = "abort_bash" };
            else if (dialog is null && !draft.IsEditing && UserBashLine(line) is { } bash)
            {
                if (pendingBash is not null) display = "[warning] A bash command is already running. Use /abort to cancel it first.";
                else
                {
                    pendingBash = bashIdentity = id; display = "$ " + bash.Command;
                    command = new { id, type = "bash", command = bash.Command, excludeFromContext = bash.Excluded };
                }
            }
            else if (line is "/abort" or "/state" or "/clear-queue") command = new { id, type = line switch { "/abort" => "abort", "/state" => "get_state", _ => "clear_queue" } };
            else if (line.StartsWith("/steer ", StringComparison.Ordinal)) command = new { id, type = "steer", message = line[7..] };
            else if (line.StartsWith("/follow-up ", StringComparison.Ordinal)) command = new { id, type = "follow_up", message = line[11..] };
            else if (dialog is null && (line == "/compact" || line.StartsWith("/compact ", StringComparison.Ordinal)))
                command = new { id, type = "pisharp_compact", generation = sessionGeneration, customInstructions = line.Length == 8 ? "" : line[9..] };
            else if (dialog is null && line == "/compact-all")
                command = new { id, type = "pisharp_compact", generation = sessionGeneration, firstKeptEntryId = (string?)null };
            else if (dialog is null && line is "/auto-compact on" or "/auto-compact off")
                command = new { id, type = "pisharp_set_auto_compaction", generation = sessionGeneration, enabled = line == "/auto-compact on" };
            else if (dialog is null && line.StartsWith("/branch-summary ", StringComparison.Ordinal))
                command = new { id, type = "pisharp_branch_summary", generation = sessionGeneration,
                    targetId = line[16..] == "root" ? null : line[16..] };
            else if (dialog is null && (line.StartsWith("/context-omit ", StringComparison.Ordinal) || line.StartsWith("/context-replace ", StringComparison.Ordinal)))
            {
                var omit = line.StartsWith("/context-omit ", StringComparison.Ordinal);
                var arguments = line[(omit ? 14 : 17)..]; var separator = arguments.IndexOf(' ');
                var target = omit ? arguments : separator < 0 ? "" : arguments[..separator];
                JsonData? replacement = null;
                try
                {
                    if (target.Length is < 1 or > 256 || target.Any(char.IsWhiteSpace)) throw new JsonException();
                    replacement = omit ? JsonData.Null : JsonData.Parse(arguments[(separator + 1)..]);
                    if (!omit && (replacement.Value.ValueKind != JsonValueKind.Object ||
                        !replacement.Value.TryGetProperty("content", out var content) || content.ValueKind is not (JsonValueKind.String or JsonValueKind.Array)))
                        throw new JsonException();
                    command = new { id, type = "pisharp_context_edit", generation = sessionGeneration, targetId = target, replacement = replacement.Value };
                }
                catch (JsonException) { display = "[context] use /context-omit <entryId> or /context-replace <entryId> <JSON object with content>."; }
            }
            else if (line.StartsWith("/switch ", StringComparison.Ordinal) && dialog is null)
                command = new { id, type = "switch_session", sessionPath = line[8..] };
            else if (dialog is null && (line == "/sessions" || line.StartsWith("/sessions ", StringComparison.Ordinal) || line == "/sessions-next"))
            {
                if (catalogRequests.Count >= 4) display = "[sessions] outstanding query limit reached.";
                else if (line == "/sessions-next" && nextSessionCursor is null) display = "[sessions] no next page; use /sessions to refresh.";
                else
                {
                    var cwd = line.StartsWith("/sessions ", StringComparison.Ordinal) ? line[10..] : line == "/sessions-next" ? catalogWorkingDirectory : null;
                    var cursor = line == "/sessions-next" ? nextSessionCursor : null;
                    catalogRequests.Add(id, (sessionGeneration, cwd)); latestCatalogRequest = id; catalogIdentity = id;
                    var listing = new Dictionary<string, object> { ["id"] = id, ["type"] = "pisharp_list_sessions", ["pageSize"] = 32 };
                    if (cursor is not null) listing.Add("cursor", cursor);
                    if (cwd is not null) listing.Add("cwd", cwd);
                    command = listing;
                }
            }
            else if (dialog is null && line.StartsWith("/resume ", StringComparison.Ordinal))
            {
                var input = line[8..]; var separator = input.IndexOf(' ');
                var choice = separator < 0 ? input : input[..separator]; var leaf = separator < 0 ? null : input[(separator + 1)..];
                var key = int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && sessionChoices.TryGetValue(number, out var selected) ? selected :
                    choice.Length == 64 && choice.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ? choice : null;
                if (key is null || leaf is { Length: 0 }) display = "[sessions] choose a displayed number, or refresh with /sessions.";
                else
                {
                    var resume = new Dictionary<string, object> { ["id"] = id, ["type"] = "pisharp_resume_session", ["catalogKey"] = key };
                    if (leaf == "@root") resume.Add("root", true);
                    else if (leaf is not null) resume.Add("leafId", leaf);
                    command = resume;
                }
            }
            else if (dialog is null && line is "/new" or "/clone" or "/tree")
                command = new { id, type = line == "/new" ? "new_session" : line == "/clone" ? "clone" : "get_tree" };
            else if (dialog is null && line.StartsWith("/fork ", StringComparison.Ordinal))
                command = new { id, type = "fork", entryId = line[6..] };
            else if (dialog is null && line.StartsWith("/fork-at ", StringComparison.Ordinal))
                command = new { id, type = "fork", entryId = line[9..], position = "at" };
            else if (line.StartsWith("/complete ", StringComparison.Ordinal))
            {
                var request = line[10..]; var separator = request.IndexOf(' ');
                var name = separator < 0 ? request : request[..separator];
                var prefix = separator < 0 ? "" : request[(separator + 1)..];
                if (name.StartsWith('/'))
                {
                    var matches = promptCommands.Where(pair => pair.Key.StartsWith(name[1..], StringComparison.Ordinal))
                        .Select(pair => "/" + pair.Key + " " + pair.Value).ToArray();
                    display = matches.Length == 0 ? "[completion] no matching templates." :
                        "[templates]\n" + ChatEditor.Display(string.Join('\n', matches));
                }
                else if (!extensionCommands.Contains(name) && promptCommands.TryGetValue(name, out var description))
                    display = "[template] " + ChatEditor.Display("/" + name + " " + description);
                else if (!extensionCommands.Contains(name)) display = "[completion] command unavailable.";
                else if (prefix.Length > 65_536) display = "[completion] prefix exceeds its limit.";
                else if (completionRequests.Count >= 16) display = "[completion] outstanding query limit reached.";
                else
                {
                    completionRequests.Add(id, name);
                    completionIdentity = id;
                    command = new { id, type = "pisharp_complete_extension_command", command = name, prefix };
                }
            }
            else if (dialog is not null)
            {
                var body = dialog.Record.Value; var requestId = body.GetProperty("id").GetString()!; var method = body.GetProperty("method").GetString();
                if (dialogResponsePending) display = "[ui] Awaiting host retirement.";
                else if (method == "select" && selectListRouter is not null)
                    display = "[ui] Use the selector's captured keyboard input.";
                else if (line == "/cancel") command = new { type = "extension_ui_response", id = requestId, cancelled = true };
                else if (method == "confirm")
                {
                    if (line.Equals("yes", StringComparison.OrdinalIgnoreCase) || line.Equals("no", StringComparison.OrdinalIgnoreCase))
                        command = new { type = "extension_ui_response", id = requestId, confirmed = line.Equals("yes", StringComparison.OrdinalIgnoreCase) };
                    else display = "[ui] Enter yes/no or /cancel; no approval sent.";
                }
                else if (method == "select")
                {
                    var choices = body.GetProperty("options");
                    if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= choices.GetArrayLength())
                        command = new { type = "extension_ui_response", id = requestId, value = choices[number - 1].GetString() };
                    else display = "[ui] Enter a displayed choice number or /cancel.";
                }
                else if (method == "editor")
                {
                    if (line == "/save") command = new { type = "extension_ui_response", id = requestId, value = nativePresentation ? dialogEditor.Text : dialogEditor.Take() };
                    else { dialogEditor.Append(line); display = "[ui draft]\n" + dialogEditor.Text; }
                }
                else command = new { type = "extension_ui_response", id = requestId, value = line.StartsWith("/answer ", StringComparison.Ordinal) ? line[8..] : line };
                if (command is not null)
                {
                    if (nativePresentation) dialogResponsePending = true;
                    else display = FinishDialog();
                }
            }
            else if (line == "/edit") { draft.Begin(); display = "[draft]\n" + draft.Text; localAcknowledged = true; }
            else if (line == "/cancel") { draft.Clear(); display = "[draft discarded]"; localAcknowledged = true; }
            else if (line == "/show") { display = "[draft]\n" + draft.Text; localAcknowledged = true; }
            else if (line == "/save") command = new { id, type = "prompt", message = draft.Take(), streamingBehavior = "steer" };
            else if (line.StartsWith("/send ", StringComparison.Ordinal)) command = new { id, type = "prompt", message = line[6..], streamingBehavior = "steer" };
            else if (draft.IsEditing) { draft.Append(line); display = "[draft]\n" + draft.Text; }
            else if (line.StartsWith('/'))
            {
                var separator = line.IndexOf(' '); var name = separator < 0 ? line[1..] : line[1..separator];
                var promptName = PromptTemplateExpander.GetInvocationName(line);
                if (extensionCommands.Contains(name) || promptName is not null && promptCommands.ContainsKey(promptName))
                    command = new { id, type = "prompt", message = line };
                else display = "[command] unavailable; use /send for literal leading-slash text.";
            }
            else if (line.Length != 0) command = new { id, type = "prompt", message = line, streamingBehavior = "steer" };
        }
        if (command is not null)
        {
            var record = JsonData.Parse(JsonSerializer.Serialize(command));
            if (submission is not null)
            {
                var type = record.Value.GetProperty("type").GetString()!;
                if (type == "extension_ui_response") submissionReceipts!.FinishLocal(submission, false);
                else submissionReceipts!.Correlate(submission, record.Value.GetProperty("id").GetString()!, type);
            }
            try { await send!(record, token).ConfigureAwait(false); }
            catch
            {
                // An unaccepted send cannot retain a completion slot.
                if (completionIdentity is not null) lock (state) completionRequests.Remove(completionIdentity);
                if (catalogIdentity is not null) lock (state) catalogRequests.Remove(catalogIdentity);
                if (bashIdentity is not null) lock (state) if (pendingBash == bashIdentity) pendingBash = null;
                throw;
            }
        }
        else if (submission is not null) submissionReceipts!.FinishLocal(submission, localAcknowledged, line);
        if (display is not null) await RenderAsync(display, token).ConfigureAwait(false);
        return !quit;
    }
    internal async Task HistoryAsync(CancellationToken token)
    {
        await Send(new { id = "chat-commands", type = "get_commands" }, token).ConfigureAwait(false);
        await commandsReady.Task.WaitAsync(token).ConfigureAwait(false);
        await Send(new { id = "chat-history", type = "get_messages" }, token).ConfigureAwait(false);
    }
    private Task Send(object command, CancellationToken token) => send!(JsonData.Parse(JsonSerializer.Serialize(command)), token);
    private async Task RenderAsync(string text, CancellationToken token, bool assistantDelta = false)
    {
        if (text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Interactive view exceeds its bounded profile.");
        var safe = ChatEditor.Display(text);
        if (Encoding.UTF8.GetByteCount(safe) >= 8 * 1024 * 1024) throw new InvalidOperationException("Interactive UTF-8 view exceeds its bounded profile.");
        await rendering.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (assistantDelta) await ((IInteractiveAssistantStreamingPresentation)presentation).PresentAssistantDeltaAsync(safe, token).ConfigureAwait(false);
            else await presentation.PresentAsync(safe + "\n", token).ConfigureAwait(false);
        }
        finally { rendering.Release(); }
    }
    private static string MessageText(JsonElement message) => "[" + message.GetProperty("role").GetString() + "] " + ResultText(message);
    private void ResetAssistantPresentation()
    { assistantPresentedBlocks.Clear(); assistantPresentedCharacters = 0; }
    private static int AssistantBlockIndex(JsonElement update) =>
        update.TryGetProperty("contentIndex", out var index) ? index.GetInt32() : 0;
    private bool AssistantBlockMatches(int index, string content) =>
        assistantPresentedBlocks.TryGetValue(index, out var shown) && shown.ToString() == content;
    private static string AssistantFinalBlock(int index, string content) =>
        "[assistant final block " + index.ToString(CultureInfo.InvariantCulture) + "] " + (content.Length == 0 ? "[empty]" : content);
    private void RememberAssistantBlock(JsonElement update, string text, bool append)
    {
        var index = AssistantBlockIndex(update);
        assistantPresentedBlocks.TryGetValue(index, out var shown);
        var removed = append ? 0 : shown?.Length ?? 0;
        if (text.Length > 8 * 1024 * 1024 - (assistantPresentedCharacters - removed))
            throw new InvalidOperationException("Interactive assistant presentation exceeds its bounded profile.");
        if (shown is null && assistantPresentedBlocks.Count >= 65_536)
            throw new InvalidOperationException("Interactive assistant block presentation exceeds its bounded profile.");
        if (shown is null) assistantPresentedBlocks.Add(index, shown = new StringBuilder());
        if (!append) shown.Clear();
        shown.Append(text); assistantPresentedCharacters += text.Length - removed;
    }
    private string ReconcileAssistantMessage(JsonElement message)
    {
        if (assistantPresentedBlocks.Count == 0) return MessageText(message);
        if (!message.TryGetProperty("content", out var content)) return MessageText(message);
        if (content.ValueKind == JsonValueKind.String)
            return AssistantBlockMatches(0, content.GetString()!) ? "" : AssistantFinalBlock(0, content.GetString()!);
        if (content.ValueKind != JsonValueKind.Array) return MessageText(message);
        var final = new List<string>(); var index = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                if (!AssistantBlockMatches(index, text.GetString()!)) final.Add(AssistantFinalBlock(index, text.GetString()!));
            }
            else if (block.TryGetProperty("name", out var name)) final.Add(AssistantFinalBlock(index, "tool:" + name.GetString()));
            index++;
        }
        return string.Join('\n', final);
    }
    private static string ResultText(JsonElement value)
    {
        if (!value.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Interactive message content is unavailable.");
        return string.Join('\n', content.EnumerateArray().Select(block => block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() :
            block.TryGetProperty("name", out var name) ? "tool:" + name.GetString() : ""));
    }
    public void Dispose()
    {
        // The command joins its input loop and the host's actual output callbacks before closing this view.
        lock (state) { queueRestoration?.Completion.TrySetCanceled(); queueRestoration = null; ClearDialogs(); draft.Clear(); statuses.Clear(); widgets.Clear(); completionRequests.Clear(); ResetAssistantPresentation(); send = null; }
        ready.TrySetCanceled(); rendering.Dispose();
    }
}
