// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/program-status-reporter.ts.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed record BlockedStatus(ProgramBlockedKind Kind, string Message);

/// <summary>Reports interactive-mode state to the terminal (OSC 7501): working during runs and compaction, blocked while a dialog
/// waits, then done, error or idle once the run settles. Messages are limited to the session name, dialog titles and the first
/// line of errors.</summary>
internal sealed class InteractiveProgramStatus(Func<ITerminal> getTerminal, Func<string?> getSessionName, string app = "pi")
{
    private bool runActive, compacting;
    private ProgramStatus runResult = new(ProgramState.Done);
    private ProgramStatus restingStatus = new(ProgramState.Idle);
    private readonly List<(string Source, BlockedStatus Status)> blocked = [];
    private string? lastReport;

    private static string FirstLine(string? text)
    {
        var line = text is null ? "" : PiSharp.Tui.Pi.TextUtils.JsTrim(text.Split('\n')[0].TrimEnd('\r'));
        return line.Length > 0 ? line : "Error";
    }

    public void HandleEvent(JsonObject e)
    {
        switch (SessionEntries.Str(e["type"]))
        {
            case "agent_start":
                runActive = true; runResult = new(ProgramState.Done); break;
            case "message_end":
                if (SessionEntries.Str(e["message"]?["role"]) != "assistant") return;
                runResult = SessionEntries.Str(e["message"]?["stopReason"]) == "error"
                    ? new(ProgramState.Error, Message: FirstLine(SessionEntries.Str(e["message"]?["errorMessage"])))
                    : new(ProgramState.Done);
                break;
            case "compaction_start":
                compacting = true; break;
            case "compaction_end":
            {
                compacting = false;
                var aborted = e["aborted"] is JsonValue a && a.TryGetValue<bool>(out var ab) && ab;
                var error = SessionEntries.Str(e["errorMessage"]);
                if (runActive)
                {
                    if (aborted) runResult = new(ProgramState.Idle);
                    else if (!string.IsNullOrEmpty(error)) runResult = new(ProgramState.Error, Message: FirstLine(error));
                }
                else if (aborted) restingStatus = new(ProgramState.Idle);
                else if (SessionEntries.Str(e["reason"]) == "manual")
                    restingStatus = !string.IsNullOrEmpty(error) ? new(ProgramState.Error, Message: FirstLine(error)) : new(ProgramState.Done);
                break;
            }
            case "agent_settled":
                runActive = false;
                restingStatus = e["aborted"] is JsonValue settled && settled.TryGetValue<bool>(out var wasAborted) && wasAborted ? new(ProgramState.Idle) : runResult;
                break;
            case "session_info_changed":
                break;
            default:
                return;
        }
        Report();
    }

    public void SetBlocked(string source, BlockedStatus? status)
    {
        blocked.RemoveAll(entry => entry.Source == source);
        if (status is not null) blocked.Add((source, status));
        Report();
    }

    public void Reset()
    {
        runActive = false; compacting = false;
        runResult = new(ProgramState.Done); restingStatus = new(ProgramState.Idle);
        Report();
    }

    public void Report()
    {
        var status = CurrentStatus() with { App = app };
        var key = $"{status.State}|{status.App}|{status.Kind}|{status.Message}";
        if (key == lastReport) return;
        lastReport = key;
        getTerminal().SetProgramStatus(status);
    }

    private ProgramStatus CurrentStatus()
    {
        if (blocked.Count > 0) { var last = blocked[^1].Status; return new(ProgramState.Blocked, Kind: last.Kind, Message: last.Message); }
        if (compacting) return new(ProgramState.Working, Message: "Compacting context");
        var status = runActive ? new ProgramStatus(ProgramState.Working) : restingStatus;
        if (status.State is ProgramState.Working or ProgramState.Done) return status with { Message = getSessionName() };
        return status;
    }
}
