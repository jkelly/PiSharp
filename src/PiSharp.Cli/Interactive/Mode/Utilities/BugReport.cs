// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/bug-report.ts.
// The /bug flow: consent, optional summary, then upload or export. The report core is PiSharp.CodingAgent.Diagnostics (BugReport,
// BugReportUpload, CrashLog). The overlays upstream builds (ExtensionEditorComponent, ExtensionSelectorComponent, BorderedLoader shown
// in the editor container with focus, restored to the editor afterwards) are behind IBugReportUi; the AgentSession members behind
// IBugReportSession.
using PiSharp.CodingAgent.Diagnostics;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using CoreBugReport = PiSharp.CodingAgent.Diagnostics.BugReport;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>The AgentSession members the /bug flow reads.</summary>
internal interface IBugReportSession
{
    /// <summary><c>session.model?.name</c>.</summary>
    string? ModelName { get; }
    /// <summary><c>session.model?.provider</c>.</summary>
    string? ModelProvider { get; }
    /// <summary><c>session.summarizeForBugReport({ hint, signal })</c>; throws "No model selected" without a model.</summary>
    Task<string> SummarizeForBugReportAsync(string? hint, CancellationToken cancellationToken);
    /// <summary>The <c>collectBugReportMetadata</c> inputs from the session (session id, <c>sessionManager.getCwd()</c>, message count,
    /// model and provider description, thinking level, loaded extensions and errors, global and project settings). The flow sets
    /// <c>Hint</c>, <c>IncludeSession</c> and <c>IncludeSummary</c>.</summary>
    BugReportMetadataOptions GetMetadataOptions();
    string SessionId { get; }
    /// <summary><c>sessionManager.getEntries()</c>.</summary>
    IEnumerable<SessionEntry> GetEntries();
    /// <summary><c>serializeSessionBranch(sessionManager, createShareTrailingEntries)</c>: the transcript JSONL.</summary>
    string SerializeSessionBranch();
    /// <summary>The Radius upload token: <c>getAuthCredential(await modelRuntime.getAuth("radius", { minOAuthValidityMs: 300000 }))</c>
    /// when the radius provider is registered, else null.</summary>
    Task<string?> GetRadiusTokenAsync(CancellationToken cancellationToken);
    /// <summary><c>sessionManager.appendCustomEntry(customType, data)</c>.</summary>
    void AppendCustomEntry(string customType, JsonData data);
}

/// <summary>The bordered loader shown during summary and upload. <see cref="IDisposable.Dispose"/> is the source's
/// <c>restoreEditor(context, loader)</c>; <see cref="Signal"/> is <c>loader.signal</c> (Escape cancels it) and stays readable
/// after disposal.</summary>
internal interface IBugReportLoader : IDisposable
{
    CancellationToken Signal { get; }
}

/// <summary>The interactive surfaces of the /bug flow. Each prompt shows its component in the editor container, focuses it and
/// restores the editor when it finishes.</summary>
internal interface IBugReportUi
{
    /// <summary>An ExtensionEditorComponent prompt (title, description, initial value); null when cancelled.</summary>
    Task<string?> Input(string title, string description, string? initialValue);
    /// <summary>An ExtensionSelectorComponent prompt; null when cancelled.</summary>
    Task<string?> Choose(string title, IReadOnlyList<string> options, string? description);
    /// <summary>A BorderedLoader with <paramref name="message"/> in place of the editor.</summary>
    IBugReportLoader ShowLoader(string message);
    void ShowStatus(string message);
    void ShowError(string message);
}

/// <summary>Process-level inputs: environment (<c>PI_OFFLINE</c>, <c>PI_RADIUS_GATEWAY</c>), working directory for the zip, the
/// crash log path (<c>readCrashLog</c>/<c>clearCrashLog</c>; null means none), the upload HTTP client and the clock.</summary>
internal sealed class BugReportEnvironment
{
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;
    public Func<string> Cwd { get; init; } = Directory.GetCurrentDirectory;
    public string? CrashLogPath { get; init; }
    public HttpMessageInvoker? Http { get; init; }
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>Source <c>BugReportOptions</c>.</summary>
internal sealed record BugReportOptions(string? Hint, bool IncludeSession, bool IncludeSummary, string Delivery);

internal static class InteractiveBugReport
{
    public const string Disclaimer =
        "This report goes to the Pi developers (Earendil) and is not shared publicly. It includes your pi version, operating system, the current model and provider configuration (without API keys), loaded extensions, settings, and provider error diagnostics from this session.";
    public const string TranscriptNote =
        "The transcript contains your messages, model output, tool calls and their results, including file contents and command output read during this session.";

    /// <summary>Run the <c>/bug</c> flow: consent, optional summary, then upload or export.</summary>
    public static async Task ReportBug(IBugReportSession session, IBugReportUi ui, BugReportEnvironment? environment = null, string? initialHint = null)
    {
        environment ??= new();
        var options = await PromptForOptions(session, ui, environment, initialHint).ConfigureAwait(false);
        if (options is null)
        {
            ui.ShowStatus("Bug report cancelled");
            return;
        }
        if (options.Delivery == "upload" && !string.IsNullOrEmpty(environment.Env("PI_OFFLINE")))
        {
            ui.ShowError("Uploading bug reports requires online mode. Use Export as Zip instead.");
            return;
        }

        string? summary = null;
        if (options.IncludeSummary)
        {
            var loader = ui.ShowLoader($"Writing summary with {session.ModelName ?? "the current model"}...");
            try
            {
                summary = await session.SummarizeForBugReportAsync(options.Hint, loader.Signal).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                loader.Dispose();
                if (loader.Signal.IsCancellationRequested) ui.ShowStatus("Bug report cancelled");
                else ui.ShowError($"Failed to write bug report summary: {ErrorMessage(error)}");
                return;
            }
            loader.Dispose();
            if (loader.Signal.IsCancellationRequested)
            {
                ui.ShowStatus("Bug report cancelled");
                return;
            }
        }

        BugReportBundle bundle;
        try
        {
            bundle = BuildBundle(session, environment, options, summary);
        }
        catch (Exception error)
        {
            ui.ShowError($"Failed to build bug report: {ErrorMessage(error)}");
            return;
        }

        if (options.Delivery == "upload")
        {
            var failure = await Upload(session, ui, environment, bundle).ConfigureAwait(false);
            if (failure is null) return;
            var fallback = await ui.Choose("Upload failed", ["Export as Zip", "Cancel"], $"{failure}\n\nExport the report as a zip archive instead?")
                .ConfigureAwait(false);
            if (fallback != "Export as Zip")
            {
                ui.ShowStatus("Bug report cancelled");
                return;
            }
        }
        await ExportZip(session, ui, environment, bundle).ConfigureAwait(false);
    }

    private static async Task<BugReportOptions?> PromptForOptions(IBugReportSession session, IBugReportUi ui, BugReportEnvironment environment,
        string? initialHint)
    {
        var hint = await ui.Input("Report a bug", $"{Disclaimer}\n\nWhat went wrong? (optional)", initialHint).ConfigureAwait(false);
        if (hint is null) return null;
        var transcript = await ui.Choose("Include the session transcript?", ["Yes, include the transcript", "No"], TranscriptNote).ConfigureAwait(false);
        if (string.IsNullOrEmpty(transcript)) return null;
        var includeSession = transcript != "No";
        var includeSummary = false;
        if (!includeSession)
        {
            var summary = await ui.Choose(
                $"Attach a summary written by {session.ModelName ?? "the current model"} instead?",
                ["Yes, generate a summary", "No"],
                $"The transcript is sent to {session.ModelProvider ?? "your provider"} with your credentials and tokens. Only the generated summary is attached; the transcript stays on your machine.")
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(summary)) return null;
            includeSummary = summary != "No";
        }
        var description = PiSharp.Tui.Pi.TextUtils.JsTrim(hint);
        var gatewayHost = new Uri(BugReportUpload.GetRadiusGatewayUrl(environment.Env)).Authority;
        var delivery = await ui.Choose(
            "Bug report",
            ["Upload Report", "Export as Zip", "Cancel"],
            $"Description: {(description.Length > 0 ? description : "none")}\nTranscript: {(includeSession ? "included" : "not included")}\nSummary: {(includeSummary ? $"written by {session.ModelName ?? "the current model"}" : "none")}\n\nUpload sends the report to {gatewayHost}. Export writes a zip archive to the current directory instead.")
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(delivery) || delivery == "Cancel") return null;
        return new(description.Length > 0 ? description : null, includeSession, includeSummary, delivery == "Upload Report" ? "upload" : "zip");
    }

    private static BugReportBundle BuildBundle(IBugReportSession session, BugReportEnvironment environment, BugReportOptions options, string? summary)
    {
        var baseOptions = session.GetMetadataOptions();
        var metadataOptions = baseOptions with
        {
            Hint = options.Hint,
            IncludeSession = options.IncludeSession,
            IncludeSummary = summary is not null,
            TimeProvider = environment.TimeProvider ?? baseOptions.TimeProvider,
        };
        var crashes = environment.CrashLogPath is { Length: > 0 } path ? CrashLog.ReadCrashLog(path) : [];
        return new(
            CoreBugReport.CollectBugReportMetadata(metadataOptions),
            CoreBugReport.CollectBugReportDiagnostics(session.SessionId, session.GetEntries(), crashes),
            options.IncludeSession ? session.SerializeSessionBranch() : null,
            summary);
    }

    private static async Task<string?> Upload(IBugReportSession session, IBugReportUi ui, BugReportEnvironment environment, BugReportBundle bundle)
    {
        var loader = ui.ShowLoader("Uploading bug report...");
        try
        {
            var token = await session.GetRadiusTokenAsync(loader.Signal).ConfigureAwait(false);
            var id = await BugReportUpload.UploadBugReportAsync(bundle, new(token), environment.Http, environment.Env, loader.Signal).ConfigureAwait(false);
            loader.Dispose();
            RecordInSession(session, environment, bundle, "upload", null);
            ui.ShowStatus($"Bug report uploaded. Report ID: {id}");
            return null;
        }
        catch (Exception error)
        {
            loader.Dispose();
            if (loader.Signal.IsCancellationRequested)
            {
                ui.ShowStatus("Bug report cancelled");
                return null;
            }
            return ErrorMessage(error);
        }
    }

    private static async Task ExportZip(IBugReportSession session, IBugReportUi ui, BugReportEnvironment environment, BugReportBundle bundle)
    {
        var archivePath = Path.Combine(environment.Cwd(), CoreBugReport.BugReportArchiveFileName(bundle.Id));
        try
        {
            await CoreBugReport.WriteBugReportArchiveAsync(bundle, archivePath, environment.TimeProvider).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ui.ShowError($"Failed to write bug report: {ErrorMessage(error)}");
            return;
        }
        RecordInSession(session, environment, bundle, "zip", archivePath);
        ui.ShowStatus($"Bug report exported to: {archivePath}\nReport ID: {bundle.Id}");
    }

    private static void RecordInSession(IBugReportSession session, BugReportEnvironment environment, BugReportBundle bundle, string delivery, string? path)
    {
        session.AppendCustomEntry(CoreBugReport.CustomEntryType, CoreBugReport.BugReportSessionEntryData(bundle, delivery, path));
        if (bundle.CrashCount > 0 && environment.CrashLogPath is { Length: > 0 } crashLogPath) CrashLog.ClearCrashLog(crashLogPath);
    }

    private static string ErrorMessage(Exception error) => error.Message;
}
