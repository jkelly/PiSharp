// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/program-status-reporter.ts.
using System.Text.Json;
using PiSharp.Tui.Rendering;

namespace PiSharp.Cli.Interactive;

/// <summary>A dialog the user must answer, reported as <c>blocked</c>.</summary>
internal sealed record ProgramBlockedStatus(TerminalProgramBlockedKind Kind, string Message);

/// <summary>Reports interactive-mode state to the terminal (OSC 7501): <c>working</c> during agent runs and compaction,
/// <c>blocked</c> while a dialog waits for the user, then <c>done</c>, <c>error</c> or <c>idle</c> once the run settles.
/// Messages are limited to the session name, dialog titles and the first line of errors; prompts and assistant output
/// are never reported. Each method returns the status to send, or null when it equals the last report; the owner
/// sends it in call order. Events are the RPC records the interactive frontend observes, and the session name comes
/// from <c>get_state</c> and <c>session_info_changed</c> instead of a session manager.</summary>
internal sealed class ProgramStatusReporter(string app = "pi")
{
    private readonly object gate = new();
    private bool runActive, compacting;
    private string? sessionName;
    /// <summary>Outcome of the current run, reported once it settles.</summary>
    private TerminalProgramStatus runResult = new(TerminalProgramState.Done);
    /// <summary>Status while no run is active.</summary>
    private TerminalProgramStatus restingStatus = new(TerminalProgramState.Idle);
    /// <summary>Open dialogs by source, in the order they opened. The most recent one is reported.</summary>
    private readonly List<KeyValuePair<string, ProgramBlockedStatus>> blocked = [];
    private TerminalProgramStatus? lastReport;

    /// <summary>Whether <see cref="HandleEvent"/> can change the report for this record.</summary>
    internal static bool Observes(JsonElement record) => record.ValueKind == JsonValueKind.Object &&
        record.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
        type.GetString() is "agent_start" or "message_end" or "compaction_start" or "compaction_end" or "agent_settled" or
            "session_info_changed" or "response";

    private static string FirstLine(string? text)
    {
        var first = text is null ? "" : text.Split('\n', 2)[0];
        if (first.EndsWith('\r')) first = first[..^1];
        first = first.Trim();
        return first.Length == 0 ? "Error" : first;
    }

    internal TerminalProgramStatus? HandleEvent(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object || String(record, "type") is not { } type) return null;
        lock (gate)
        {
            switch (type)
            {
                case "agent_start": runActive = true; runResult = new(TerminalProgramState.Done); break;
                case "message_end":
                    // The latest response decides the outcome, so a retried error is replaced by its successful retry.
                    if (!record.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                        String(message, "role") != "assistant") return null;
                    runResult = String(message, "stopReason") == "error"
                        ? new(TerminalProgramState.Error, Message: FirstLine(String(message, "errorMessage")))
                        : new(TerminalProgramState.Done);
                    break;
                case "compaction_start": compacting = true; break;
                case "compaction_end":
                    compacting = false;
                    var aborted = Boolean(record, "aborted"); var error = String(record, "errorMessage");
                    if (runActive)
                    {
                        // A failed recovery compaction ends the run unless a later response succeeds.
                        if (aborted) runResult = new(TerminalProgramState.Idle);
                        else if (!string.IsNullOrEmpty(error)) runResult = new(TerminalProgramState.Error, Message: FirstLine(error));
                    }
                    else if (aborted) restingStatus = new(TerminalProgramState.Idle);
                    else if (String(record, "reason") == "manual")
                        restingStatus = !string.IsNullOrEmpty(error) ? new(TerminalProgramState.Error, Message: FirstLine(error)) : new(TerminalProgramState.Done);
                    break;
                case "agent_settled":
                    runActive = false;
                    restingStatus = Boolean(record, "aborted") ? new(TerminalProgramState.Idle) : runResult;
                    break;
                case "session_info_changed": sessionName = String(record, "name"); break; // Part of working and done reports.
                case "response":
                    if (String(record, "command") != "get_state" || !Boolean(record, "success") ||
                        !record.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
                    sessionName = String(data, "sessionName");
                    break;
                default: return null;
            }
            return ReportLocked();
        }
    }

    /// <summary>Report <c>blocked</c> for a dialog until it is cleared with null. Reopening a source replaces it.</summary>
    internal TerminalProgramStatus? SetBlocked(string source, ProgramBlockedStatus? status)
    {
        lock (gate)
        {
            blocked.RemoveAll(entry => entry.Key == source);
            if (status is not null) blocked.Add(KeyValuePair.Create(source, status));
            return ReportLocked();
        }
    }

    /// <summary>Forget the previous session's run, for example after switching sessions.</summary>
    internal TerminalProgramStatus? Reset()
    {
        lock (gate)
        {
            runActive = false; compacting = false;
            runResult = new(TerminalProgramState.Done); restingStatus = new(TerminalProgramState.Idle);
            return ReportLocked();
        }
    }

    internal TerminalProgramStatus? Report() { lock (gate) return ReportLocked(); }

    private TerminalProgramStatus? ReportLocked()
    {
        var status = CurrentStatus() with { App = app };
        if (status == lastReport) return null;
        return lastReport = status;
    }

    private TerminalProgramStatus CurrentStatus()
    {
        if (blocked.Count != 0) { var last = blocked[^1].Value; return new(TerminalProgramState.Blocked, Kind: last.Kind, Message: last.Message); }
        if (compacting) return new(TerminalProgramState.Working, Message: "Compacting context");
        var status = runActive ? new TerminalProgramStatus(TerminalProgramState.Working) : restingStatus;
        return status.State is TerminalProgramState.Working or TerminalProgramState.Done ? status with { Message = sessionName } : status;
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool Boolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
