// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/bug-report.ts.
// The /bug flow: consent, optional summary, then the report. The report core is PiSharp.CodingAgent.Diagnostics (BugReport,
// CrashLog). The overlays upstream builds (ExtensionEditorComponent, ExtensionSelectorComponent, BorderedLoader shown in the
// editor container with focus, restored to the editor afterwards) are behind IBugReportUi; the AgentSession members behind
// IBugReportSession.
// PiSharp (owner decision 11, docs/decisions/0004-full-parity-owner-decisions.md): upstream's "Upload Report" (uploadBugReport to
// the Radius gateway, Pi's developers) is replaced by "Open GitHub Issue": the same zip is written locally and a prefilled issue at
// github.com/jkelly/PiSharp/issues/new opens with the platform URL opener (utils/open-browser.ts); nothing is uploaded.
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Pi;
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
    /// <summary><c>sessionManager.appendCustomEntry(customType, data)</c>.</summary>
    void AppendCustomEntry(string customType, JsonData data);
}

/// <summary>The bordered loader shown while the summary is written. <see cref="IDisposable.Dispose"/> is the source's
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
    /// <summary>Opens <paramref name="url"/> with the platform URL opener (open-browser.ts <c>openBrowser</c>). False when no
    /// browser the user can see is available (headless, over SSH) or the opener failed.</summary>
    bool OpenUrl(string url);
}

/// <summary>Process-level inputs: environment, working directory for the zip, the crash log path (<c>readCrashLog</c>/
/// <c>clearCrashLog</c>; null means none), the clock and the PiSharp version the issue names.</summary>
internal sealed class BugReportEnvironment
{
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;
    public Func<string> Cwd { get; init; } = Directory.GetCurrentDirectory;
    public string? CrashLogPath { get; init; }
    public TimeProvider? TimeProvider { get; init; }
    public string Version { get; init; } = PiConfig.Version;
}

/// <summary>Source <c>BugReportOptions</c>; <see cref="Delivery"/> is <c>"github-issue"</c> or <c>"zip"</c>.</summary>
internal sealed record BugReportOptions(string? Hint, bool IncludeSession, bool IncludeSummary, string Delivery);

internal static class InteractiveBugReport
{
    public const string Disclaimer =
        "Nothing is uploaded. The report is written to a zip archive in the current directory, and a prefilled issue opens at github.com/jkelly/PiSharp for you to review, attach the zip to and submit. It includes your PiSharp version, operating system, the current model and provider configuration (without API keys), loaded extensions, settings, and provider error diagnostics from this session.";
    public const string TranscriptNote =
        "The transcript contains your messages, model output, tool calls and their results, including file contents and command output read during this session.";
    public const string OpenIssueOption = "Open GitHub Issue";
    public const string GitHubIssueDelivery = "github-issue";

    /// <summary>Run the <c>/bug</c> flow: consent, optional summary, then the zip and (unless only the zip is wanted) the issue.</summary>
    public static async Task ReportBug(IBugReportSession session, IBugReportUi ui, BugReportEnvironment? environment = null, string? initialHint = null)
    {
        environment ??= new();
        var options = await PromptForOptions(session, ui, initialHint).ConfigureAwait(false);
        if (options is null)
        {
            ui.ShowStatus("Bug report cancelled");
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

        var archivePath = await WriteArchive(ui, environment, bundle).ConfigureAwait(false);
        if (archivePath is null) return;
        RecordInSession(session, environment, bundle, options.Delivery, archivePath);
        var exported = $"Bug report exported to: {archivePath}\nReport ID: {bundle.Id}";
        if (options.Delivery != GitHubIssueDelivery)
        {
            ui.ShowStatus(exported);
            return;
        }
        var url = BugReportIssue.CreateIssueUrl(bundle, environment.Version, CoreBugReport.BugReportArchiveFileName(bundle.Id));
        bool opened;
        try { opened = ui.OpenUrl(url); }
        catch (Exception) { opened = false; }
        ui.ShowStatus(opened
            ? $"{exported}\nOpened a prefilled GitHub issue in your browser. Attach the zip to it before submitting. Issue link:\n{url}"
            : $"{exported}\nNo browser could be opened. Open this link to file the issue, then attach the zip:\n{url}");
    }

    private static async Task<BugReportOptions?> PromptForOptions(IBugReportSession session, IBugReportUi ui, string? initialHint)
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
        var delivery = await ui.Choose(
            "Bug report",
            [OpenIssueOption, "Export as Zip", "Cancel"],
            $"Description: {(description.Length > 0 ? description : "none")}\nTranscript: {(includeSession ? "included" : "not included")}\nSummary: {(includeSummary ? $"written by {session.ModelName ?? "the current model"}" : "none")}\n\n{OpenIssueOption} writes a zip archive to the current directory and opens a prefilled issue at github.com/jkelly/PiSharp/issues; attach the zip there. Nothing is uploaded. Export writes only the zip archive.")
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(delivery) || delivery == "Cancel") return null;
        return new(description.Length > 0 ? description : null, includeSession, includeSummary, delivery == OpenIssueOption ? GitHubIssueDelivery : "zip");
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

    /// <summary>Source <c>exportZip</c>'s write: the archive in the working directory; null (after the error) when it fails.</summary>
    private static async Task<string?> WriteArchive(IBugReportUi ui, BugReportEnvironment environment, BugReportBundle bundle)
    {
        var archivePath = Path.Combine(environment.Cwd(), CoreBugReport.BugReportArchiveFileName(bundle.Id));
        try
        {
            await CoreBugReport.WriteBugReportArchiveAsync(bundle, archivePath, environment.TimeProvider).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ui.ShowError($"Failed to write bug report: {ErrorMessage(error)}");
            return null;
        }
        return archivePath;
    }

    private static void RecordInSession(IBugReportSession session, BugReportEnvironment environment, BugReportBundle bundle, string delivery, string? path)
    {
        session.AppendCustomEntry(CoreBugReport.CustomEntryType, CoreBugReport.BugReportSessionEntryData(bundle, delivery, path));
        if (bundle.CrashCount > 0 && environment.CrashLogPath is { Length: > 0 } crashLogPath) CrashLog.ClearCrashLog(crashLogPath);
    }

    private static string ErrorMessage(Exception error) => error.Message;
}

/// <summary>PiSharp: the prefilled GitHub issue for a bug report. The title and body carry the description, the generated summary
/// and the environment from <c>report.json</c> (PiSharp version, operating system, runtime, model and provider); no settings,
/// transcript, paths or credentials. GitHub cannot attach files from a link, so the body names the zip to attach.</summary>
internal static class BugReportIssue
{
    public const string NewIssueUrl = "https://github.com/jkelly/PiSharp/issues/new";
    /// <summary>GitHub rejects longer issue links (about 8 KB), so the body is cut to keep the whole link within this length.</summary>
    public const int MaxUrlLength = 8000;
    public const int MaxTitleLength = 100;
    public const string SummaryTruncatedNote = "_[Summary truncated to fit in the issue link. The full summary is `summary.md` in the attached report.]_";
    public const string DescriptionTruncatedNote = "_[Description truncated to fit in the issue link. The full description is `hint` in `report.json` in the attached report.]_";

    /// <summary>The issue link: <c>{NewIssueUrl}?title=…&amp;body=…</c>, both percent-encoded (<see cref="Uri.EscapeDataString"/>).
    /// When the link would exceed <see cref="MaxUrlLength"/>, the summary is cut first, then the description, each with a note.</summary>
    public static string CreateIssueUrl(BugReportBundle bundle, string version, string archiveFileName)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var title = Title(bundle.Hint, version);
        var description = bundle.Hint ?? "";
        var summary = bundle.Summary is null ? null : PiSharp.Tui.Pi.TextUtils.JsTrim(bundle.Summary);
        string Url(string? cutDescription, string? cutSummary) =>
            Link(title, Body(bundle, version, archiveFileName, cutDescription ?? description, cutDescription is not null, cutSummary ?? summary, cutSummary is not null));
        var full = Url(null, null);
        if (full.Length <= MaxUrlLength) return full;
        if (summary is { Length: > 0 })
        {
            var kept = Fit(summary.Length, length => Url(null, Cut(summary, length)).Length <= MaxUrlLength);
            if (kept >= 0) return Url(null, Cut(summary, kept));
        }
        var emptySummary = summary is { Length: > 0 } ? "" : null;
        var keptDescription = Fit(description.Length, length => Url(Cut(description, length), emptySummary).Length <= MaxUrlLength);
        return Url(Cut(description, Math.Max(0, keptDescription)), emptySummary);
    }

    /// <summary>The first line of the description (or <c>Bug report from PiSharp {version}</c>), at most <see cref="MaxTitleLength"/>
    /// characters.</summary>
    public static string Title(string? hint, string version)
    {
        var line = hint is null ? "" : PiSharp.Tui.Pi.TextUtils.JsTrim(hint.Split('\n')[0]);
        if (line.Length == 0) return $"Bug report from PiSharp {version}";
        var title = "Bug report: " + line;
        return title.Length <= MaxTitleLength ? title : Cut(title, MaxTitleLength - 3).TrimEnd() + "...";
    }

    /// <summary>The Markdown body: description, summary (when attached), environment and the report to attach.</summary>
    public static string Body(BugReportBundle bundle, string version, string archiveFileName, string description, bool descriptionCut,
        string? summary, bool summaryCut)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var metadata = bundle.Metadata.Value;
        var body = new StringBuilder();
        body.Append("## Description\n\n").Append(description.Length > 0 || descriptionCut ? description : "_No description given._");
        if (descriptionCut) body.Append(description.Length > 0 ? "\n\n" : "").Append(DescriptionTruncatedNote);
        body.Append("\n\n");
        if (summary is not null)
        {
            body.Append("## Summary\n\n").Append(summary);
            if (summaryCut) body.Append(summary.Length > 0 ? "\n\n" : "").Append(SummaryTruncatedNote);
            body.Append("\n\n");
        }
        var environment = Object(metadata, "environment");
        var model = Object(metadata, "model");
        var provider = Object(metadata, "provider");
        body.Append("## Environment\n\n");
        body.Append("- PiSharp: ").Append(version).Append('\n');
        body.Append("- Pi baseline: ").Append(Text(environment, "version") ?? CoreBugReport.PiVersion).Append('\n');
        body.Append("- OS: ").Append(Text(environment, "osVersion") ?? "unknown")
            .Append(" (").Append(Text(environment, "platform") ?? "unknown").Append(' ').Append(Text(environment, "arch") ?? "unknown").Append(")\n");
        body.Append("- Runtime: ").Append(Text(environment, "runtime") ?? "unknown").Append('\n');
        if (model is { } described)
        {
            var id = $"{Text(described, "provider") ?? "unknown"}/{Text(described, "id") ?? "unknown"}";
            var name = Text(described, "name");
            body.Append("- Model: ").Append(id).Append(name is { Length: > 0 } && name != Text(described, "id") ? $" ({name})" : "").Append('\n');
            if (Text(described, "api") is { } api) body.Append("- API: ").Append(api).Append('\n');
            if (provider is { } providerInfo && Text(providerInfo, "name") is { } providerName) body.Append("- Provider: ").Append(providerName).Append('\n');
        }
        else body.Append("- Model: none\n");
        body.Append("- Thinking level: ").Append(Text(metadata, "thinkingLevel") ?? "off").Append('\n');
        body.Append("- Extensions: ").Append(Count(metadata, "extensions")).Append(" loaded, ").Append(Count(metadata, "extensionErrors")).Append(" failed\n");
        var diagnostics = bundle.Diagnostics.Value;
        body.Append("- Failed or diagnosed turns: ").Append(Count(diagnostics, "assistant")).Append('\n');
        body.Append("- Crashes: ").Append(Count(diagnostics, "crashes")).Append("\n\n");
        body.Append("## Report\n\nReport ID: `").Append(bundle.Id).Append("`. The full report is `").Append(archiveFileName).Append("` (")
            .Append(string.Join(", ", CoreBugReport.BugReportFiles(bundle).Select(file => file.Name)))
            .Append(") on the reporter's machine. Attach it to this issue: GitHub cannot attach files from a link.");
        if (bundle.SessionIncluded) body.Append(" It contains the session transcript; review it before attaching.");
        body.Append('\n');
        return body.ToString();
    }

    private static string Link(string title, string body) =>
        $"{NewIssueUrl}?title={Uri.EscapeDataString(WellFormed(title))}&body={Uri.EscapeDataString(WellFormed(body))}";

    /// <summary>The largest length in [0, <paramref name="max"/>] that <paramref name="fits"/>; -1 when none does.</summary>
    private static int Fit(int max, Func<int, bool> fits)
    {
        int low = 0, high = max, best = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (fits(middle)) { best = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return best;
    }

    /// <summary>The first <paramref name="length"/> characters, never splitting a surrogate pair.</summary>
    private static string Cut(string text, int length)
    {
        if (length >= text.Length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    /// <summary>Lone surrogates become U+FFFD so the text percent-encodes as UTF-8.</summary>
    private static string WellFormed(string text)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var paired = char.IsHighSurrogate(character) ? index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                : !char.IsLowSurrogate(character) || index > 0 && char.IsHighSurrogate(text[index - 1]);
            if (paired && builder is null) continue;
            builder ??= new StringBuilder(text, 0, index, text.Length);
            builder.Append(paired ? character : '�');
        }
        return builder?.ToString() ?? text;
    }

    private static JsonElement? Object(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object ? field : null;

    private static string? Text(JsonElement? value, string name) =>
        value is { } element && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 } text ? text : null;

    private static int Count(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Array ? field.GetArrayLength() : 0;
}
