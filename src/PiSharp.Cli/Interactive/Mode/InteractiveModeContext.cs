using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed record CrashNoticeInfo(string When, string Message);
internal sealed record InstallChange(string Kind, string? Version);
internal sealed record ProcessResult(int ExitCode, string Stdout);
internal sealed record CacheMissNotice(double MissedTokens, double MissedCost, bool ModelChanged, double IdleMs)
{
    /// <summary>cache-stats.ts CACHE_TTL_MS.</summary>
    public const double CacheTtlMs = 5 * 60_000;
    /// <summary>The identity of an assistant message across copies (its timestamp and response id).</summary>
    public static string Key(JsonObject message) => $"{message["timestamp"]?.ToJsonString()}|{message["responseId"]?.ToJsonString()}";
}
internal sealed record CacheWaste(double MissedTokens, double MissedCost, int MissCount);
internal sealed record UsageCostEntry(string Key, double Cost, double Tokens);
internal sealed record CacheWarmingStatusInfo(string Text, bool EconomicsAvailable, double MissCost, double WarmCost);
internal sealed record ExtensionShortcut(string Key, string? Description, string ExtensionPath, Func<Task> Run);
internal sealed record RadiusMcpOffer(string ConfigPath, Action Apply);

/// <summary>The /mcp manager of the current session generation.</summary>
internal sealed class McpBinding
{
    public PiSharp.Cli.Mcp.McpServerManager? Manager { get; set; }
    public Func<string, RadiusMcpOffer?> RadiusServerOffer { get; init; } = _ => null;
}

/// <summary>The services the interactive mode uses beyond the RPC session: terminal, settings, clipboard, catalogs, credentials,
/// extensions and diagnostics. The host fills them with the production implementations; tests substitute them.</summary>
internal sealed record InteractiveModeContext
{
    public required RpcSessionClient Rpc { get; init; }
    public required InteractiveSettings Settings { get; init; }
    public required InteractiveStartup Startup { get; init; }
    public required ITerminal Terminal { get; init; }
    public required UiLoop Loop { get; init; }
    public string Version { get; init; } = "1.1.0";
    public string DocsPath { get; init; } = Path.Join(AppContext.BaseDirectory, "docs");
    public Func<string, string?> GetEnvironment { get; init; } = Environment.GetEnvironmentVariable;
    /// <summary>console.log before the TUI starts (written after the TUI stops, like other startup output).</summary>
    public Action<string> ConsoleLog { get; init; } = _ => { };
    public Func<string, Task> CopyToClipboard { get; init; } = text => Clipboard.CopyToClipboard(text);
    public Func<Task<string?>> ReadClipboardText { get; init; } = () => Clipboard.ReadClipboardText();
    public Func<Task<IReadOnlyList<string>?>> ReadClipboardFilePaths { get; init; } = () => Clipboard.ReadClipboardFilePaths();
    public Func<Task<ClipboardImage?>> ReadClipboardImage { get; init; } = () => ClipboardImageReader.ReadClipboardImage();
    public Func<string, string?> ExtensionForImageMimeType { get; init; } = ClipboardImageReader.ExtensionForImageMimeType;
    public Action<string> OpenUrl { get; init; } = url => BrowserOpener.OpenBrowser(url);
    /// <summary>ensureTool(name, onStatus): the tool's path, downloading it when missing (status type, message).</summary>
    public Func<string, Action<string, string>, Task<string?>> EnsureTool { get; init; } = (_, _) => Task.FromResult<string?>(null);
    public Func<CancellationToken, Task<ModelsRefreshResult>> RefreshModelCatalogs { get; init; } = _ => Task.FromResult(new ModelsRefreshResult(false, []));
    public Func<string, Task<LatestPiRelease?>> CheckForNewVersion { get; init; } = version => VersionCheck.CheckForNewPiVersion(version);
    public Func<string, IReadOnlyList<string>, TimeSpan, Task<ProcessResult>> RunProcess { get; init; } = DefaultRunProcess;
    public Func<CrashNoticeInfo?> TakeUnnotifiedCrash { get; init; } = () => null;
    public Func<string, object?, string?, string, bool> RecordCrashImpl { get; init; } = (_, _, _, _) => false;
    public Func<InstallChange?> DetectInstallChange { get; init; } = () => null;
    public Func<string, bool> HasTrustRequiringProjectResources { get; init; } = _ => false;
    public Func<string, PiSharp.Cli.Pi.ProjectTrustStoreEntry?> GetTrustEntry { get; init; } = _ => null;
    public Action<System.Collections.Immutable.ImmutableArray<PiSharp.Cli.Pi.ProjectTrustUpdate>> SaveTrustDecisions { get; init; } = _ => { };
    public Action<bool, int> RequestExitImpl { get; init; } = (_, _) => { };
    public Action<Action, Action> Suspend { get; init; } = (stop, resume) => { stop(); resume(); };
    public Func<JsonObject, bool> IsRetryableAssistantError { get; init; } = _ => false;
    public Func<Task<bool>> IsAnthropicSubscriptionAuth { get; init; } = () => Task.FromResult(false);
    public Func<string?>? GetModelsJsonError { get; init; }

    // Changelog and telemetry
    public Func<IReadOnlyList<ChangelogEntry>> ParseChangelogEntries { get; init; } = () => Changelog.ParseChangelog(Changelog.GetChangelogPath());
    public Action<string, InteractiveSettings> ReportInstallTelemetry { get; init; } = (_, _) => { };

    // Extensions
    public Func<string, ToolRenderers?>? ResolveToolRenderers { get; init; }
    public Func<IReadOnlyList<MarkdownTransformer>>? MarkdownTransformers { get; init; }
    public Func<string, EntryRenderer?>? GetEntryRenderer { get; init; }
    public Func<string, MessageRenderer?>? GetMessageRenderer { get; init; }
    public Func<IReadOnlyDictionary<string, IReadOnlyList<string>>, IReadOnlyList<ExtensionShortcut>>? GetExtensionShortcuts { get; init; }
    public Func<IReadOnlyList<LoadedResource>>? GetLoadedExtensions { get; init; }
    public Func<ResourceDiagnostics>? GetResourceDiagnostics { get; init; }

    // Cache statistics
    public Func<IReadOnlyList<JsonObject>, IReadOnlyDictionary<string, CacheMissNotice>>? CollectCacheMisses { get; init; }
    public Func<IReadOnlyList<JsonObject>, JsonObject, CacheMissNotice?>? DetectCacheMiss { get; init; }
    /// <summary>detectCacheMiss for a persisted assistant entry (by id).</summary>
    public Func<string, CacheMissNotice?>? DetectCacheMissForEntry { get; init; }
    /// <summary>modelRuntime.isUsingSubscription(provider).</summary>
    public Func<string, bool> UsingSubscription { get; init; } = _ => false;
    public Func<JsonObject, string>? FormatCacheWarmingUsage { get; init; }
    public Func<IReadOnlyList<JsonObject>, CacheWaste>? ComputeCacheWaste { get; init; }
    public Func<IReadOnlyList<JsonObject>, IReadOnlyList<UsageCostEntry>>? GetUsageCostBreakdown { get; init; }
    public Func<CacheWarmingStatusInfo?>? CacheWarmingStatus { get; init; }
    public Action<string>? SetCacheWarmingMode { get; init; }

    // Session operations outside the RPC protocol
    public Func<RpcSessionClient, Task> ReloadSession { get; init; } = _ => Task.CompletedTask;
    public Func<string?, string, Task<string>> ExportToJsonl { get; init; } = (_, _) => throw new InvalidOperationException("JSONL export is unavailable.");
    public Func<RpcSessionClient, string?, string?, Task<string>> ExportToHtml { get; init; } = DefaultExportHtml;
    public Func<string, string> ImportSessionFile { get; init; } = path => path;
    public Func<PiSharp.CodingAgent.Export.SessionShareUi, string?, CancellationToken, Task> ShareSession { get; init; } =
        (_, _, _) => throw new InvalidOperationException("Sharing is unavailable in this host.");
    /// <summary>Source sessionManager.appendLabelChange (the /tree label editor): a <c>label</c> entry for an entry; null clears it.</summary>
    public Func<string, string?, Task> AppendLabelChange { get; init; } = (_, _) => throw new InvalidOperationException("Tree labels are not supported by this session host.");
    public Func<IBugReportUi, string?, Task> ReportBug { get; init; } = (_, _) => throw new InvalidOperationException("Bug reports are unavailable in this host.");
    public Func<string, string, RpcSessionClient?, Task> RenameSessionFile { get; init; } = (_, _, _) => Task.CompletedTask;

    // Credentials and MCP
    public PiSharp.Cli.Authentication.ProviderLoginHost? Login { get; init; }
    public Func<string, Task> OnCredentialsChanged { get; init; } = _ => Task.CompletedTask;
    public McpBinding? Mcp { get; init; }

    public bool RecordCrash(string kind, object? error, string? sessionFile, string cwd) => RecordCrashImpl(kind, error, sessionFile, cwd);
    public void RequestExit(bool fromSignal, int code = 0) => RequestExitImpl(fromSignal, code);

    public IReadOnlyList<string> ParseChangelog() =>
        ParseChangelogEntries().Select(entry => Changelog.NormalizeChangelogLinks(entry.Content, entry)).ToList();

    public IReadOnlyList<string> GetNewChangelogEntries(IReadOnlyList<string> _, string lastVersion) =>
        Changelog.GetNewEntries(ParseChangelogEntries(), lastVersion).Select(entry => Changelog.NormalizeChangelogLinks(entry.Content, entry)).ToList();

    public static GitSource? ParseGitUrl(string source) => Git.ParseGitUrl(source);
    public static string? GetCwdRelativePath(string filePath, string cwd) => Paths.GetCwdRelativePath(filePath, cwd);

    private static async Task<string> DefaultExportHtml(RpcSessionClient rpc, string? outputPath, string? themeName)
    {
        var command = new JsonObject { ["type"] = "export_html" };
        if (outputPath is not null) command["outputPath"] = outputPath;
        var data = await rpc.RequestAsync(command) as JsonObject;
        return SessionEntries.Str(data?["path"]) ?? outputPath ?? "";
    }

    private static async Task<ProcessResult> DefaultRunProcess(string file, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Process did not start.");
        process.StandardInput.Close();
        using var cancellation = new CancellationTokenSource(timeout);
        var output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
        return new(process.ExitCode, await output);
    }
}
