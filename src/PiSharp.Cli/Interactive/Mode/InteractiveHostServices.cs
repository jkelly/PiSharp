// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (the session services the
// mode reads: modelRuntime refresh/getError/checkAuth/isUsingSubscription, collectCacheMisses/detectCacheMiss/computeCacheWaste,
// getUsageCostBreakdown, cacheWarmingStatus, formatCacheWarmingUsage, reportBug) over PiSharp's live runtime and session.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Cli.Pi;
using PiSharp.CodingAgent.Usage;
using PiSharp.CodingAgent.Diagnostics;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Cli.Interactive.Mode;

internal static class InteractiveHostServices
{
    /// <summary>The production services over the RPC host's live session (published through <see cref="InteractiveStartup.Host"/>)
    /// and the run's live runtime.</summary>
    public static InteractiveModeContext Configure(InteractiveModeContext context, PiEntryOptions options, Func<SessionState?>? getState = null)
    {
        var startup = context.Startup;
        var runtime = options.LiveRuntime;
        PiSharp.Cli.Models.ModelRegistry? registry = null;
        var registryLoad = Task.Run(async () =>
        {
            try { registry = await runtime.CreateModelRegistryAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Model services stay unavailable; the mode works without them. */ }
        });
        IReadOnlyList<SessionEntry> Entries() => startup.Host.CurrentSession?.Snapshot.Log.Entries is { IsDefault: false } entries ? entries : [];
        var prices = new DelegateModelPriceSource((provider, id) => registry?.Find(provider, id) is { } model ? ModelCostOf(model.CloneJson()) : null);
        PiSharp.Cli.Commands.OfflineSessionProfile? Profile() => startup.Host.Profile as PiSharp.Cli.Commands.OfflineSessionProfile;
        bool UsingOAuth(string provider) => registry?.CheckAuth(provider) == "OAuth";
        // runner.ts getMessageRenderer/getEntryRenderer/getMarkdownTransformers and the loaded extensions, from the Node extension host.
        var extensions = options.Extensions is { } piExtensions ? new PiInteractiveExtensionHost(piExtensions) : null;
        var resourceDiagnostics = context.GetResourceDiagnostics;
        if (extensions is not null)
            context = context with
            {
                Extensions = extensions,
                MarkdownTransformers = () => extensions.HasMarkdownTransformers
                    ? [(markdown, transform) => extensions.TransformMarkdown(markdown, new JsonObject
                        { ["messageType"] = transform.MessageType, ["isStreaming"] = transform.IsStreaming, ["availableWidth"] = transform.AvailableWidth })]
                    : [],
                GetMessageRenderer = customType => extensions.RendersMessage(customType)
                    ? (message, renderOptions, _) => new ExtensionRowsComponent(width => extensions.RenderMessage(customType, message, width, renderOptions.Expanded) ?? [])
                    : null,
                GetEntryRenderer = customType => extensions.RendersEntry(customType)
                    ? (entry, renderOptions, _) => new ExtensionRowsComponent(width => extensions.RenderEntry(customType, entry, width, renderOptions.Expanded) ?? [])
                    : null,
                GetLoadedExtensions = extensions.LoadedExtensions,
                // runner.ts getShortcuts: the handlers run with a fresh extension context (IMPL-E's PiExtensionHost.CreateContext).
                GetExtensionShortcuts = keybindings => [.. options.Extensions!.ResolveShortcuts(keybindings).Shortcuts.Select(shortcut =>
                    new ExtensionShortcut(shortcut.Key, shortcut.Description, shortcut.ExtensionPath,
                        () => options.Extensions!.RunShortcutAsync(shortcut.Extension, shortcut.Shortcut, CancellationToken.None)))],
                GetResourceDiagnostics = () =>
                {
                    var diagnostics = resourceDiagnostics?.Invoke() ?? new ResourceDiagnostics([], [], [], []);
                    return diagnostics with { Extensions = [.. diagnostics.Extensions, .. extensions.Diagnostics()] };
                }
            };
        return context with
        {
            RefreshModelCatalogs = async token =>
            {
                await registryLoad.ConfigureAwait(false);
                // model-runtime.ts refresh(): models.json and the providers are re-read, and the network is used unless PI_OFFLINE
                // is set (allowNetwork ?? modelNetworkEnabled).
                try { registry = await runtime.CreateModelRegistryAsync(token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { /* The registry of the last load stays. */ }
                catch (OperationCanceledException) { return new ModelsRefreshResult(true, []); }
                if (registry is null) return new ModelsRefreshResult(false, []);
                try
                {
                    var errors = await registry.RefreshAsync(allowNetwork: runtime.ReadEnvironment("PI_OFFLINE") is null, force: null, providers: null, token).ConfigureAwait(false);
                    if (Profile()?.LiveModels is { } models) await models.RefreshAsync(token).ConfigureAwait(false);
                    return new ModelsRefreshResult(false, [.. errors.Select(error => KeyValuePair.Create(error.Key, error.Value.Message))]);
                }
                catch (OperationCanceledException) { return new ModelsRefreshResult(true, []); }
            },
            GetModelsJsonError = () => registry?.GetError(),
            // agent-session.ts reload(): extensions, skills, prompt templates and context files (IMPL-E's in-place Pi reload).
            ReloadSession = _ => Profile()?.SupportsPiReload == true ? Profile()!.PiReloadAsync(CancellationToken.None) : Task.CompletedTask,
            // model-runtime.ts: credentials or catalogs changed, so the available snapshot (and the session's selectable models) is re-read.
            OnCredentialsChanged = async _ =>
            {
                if (Profile()?.LiveModels is { } models) await models.RefreshAsync().ConfigureAwait(false);
                try { registry = await runtime.CreateModelRegistryAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            },
            ResolveToolRenderers = toolName =>
            {
                try { return Profile()?.ResolveExtensionToolRenderers(toolName) is { } extension ? BuiltInToolRenderers.Resolve(toolName, null, extension) : null; }
                catch { return null; }
            },
            // tools-manager.ts ensureTool through the run's tools manager (the one grep and find use), downloading into <agentDir>/bin.
            EnsureTool = (tool, status) => (options.ToolPolicy.Search ?? new PiToolsManager(Path.Join(startup.AgentDir, "bin"), context.GetEnvironment))
                .EnsureToolAsync(tool, report => status(report.Type, report.Message), CancellationToken.None),
            CheckForPackageUpdates = async () =>
            {
                var settings = PiSettings.Load(startup.Cwd, startup.AgentDir, startup.ProjectTrusted);
                var manager = new PiSharp.Cli.Packages.PiPackageManager(startup.Cwd, startup.AgentDir, startup.Home, settings, context.GetEnvironment,
                    new() { Output = TextWriter.Null, ErrorOutput = TextWriter.Null });
                var updates = await manager.CheckForAvailableUpdatesAsync().ConfigureAwait(false);
                return [.. updates.Select(update => update.DisplayName)];
            },
            IsAnthropicSubscriptionAuth = async () =>
            {
                await registryLoad.ConfigureAwait(false);
                if (UsingOAuth("anthropic")) return true;
                var key = context.GetEnvironment("ANTHROPIC_API_KEY") ?? context.GetEnvironment("ANTHROPIC_OAUTH_TOKEN");
                return key?.StartsWith("sk-ant-oat", StringComparison.Ordinal) == true;
            },
            UsingSubscription = UsingOAuth,
            ComputeCacheWaste = _ =>
            {
                var totals = CacheStats.ComputeCacheWaste(Entries(), prices);
                return new CacheWaste(totals.MissedTokens, totals.MissedCost, totals.MissCount);
            },
            CollectCacheMisses = _ => CacheStats.CollectCacheMisses(Entries(), prices).ToDictionary(pair => KeyOf(pair.Key),
                pair => new CacheMissNotice(pair.Value.MissedTokens, pair.Value.MissedCost, pair.Value.ModelChanged, pair.Value.IdleMs), StringComparer.Ordinal),
            DetectCacheMissForEntry = entryId =>
            {
                var entries = Entries();
                var entry = entries.FirstOrDefault(candidate => candidate.Id == entryId);
                if (entry is null) return null;
                return CacheStats.CollectCacheMisses(entries, prices).TryGetValue(entry, out var miss)
                    ? new CacheMissNotice(miss.MissedTokens, miss.MissedCost, miss.ModelChanged, miss.IdleMs) : null;
            },
            GetUsageCostBreakdown = _ => [.. UsageTotalsCalculator.GetUsageCostBreakdown(Entries()).Select(entry => new UsageCostEntry(entry.Key, entry.Cost, entry.Tokens))],
            FormatCacheWarmingUsage = entryJson =>
            {
                var id = SessionEntries.Id(entryJson);
                var entry = Entries().FirstOrDefault(candidate => candidate.Id == id);
                return entry is null ? "" : CacheWarmer.FormatCacheWarmingUsage(entry);
            },
            CacheWarmingStatus = () => Profile()?.CurrentCacheWarmer?.Status is { } status
                ? new CacheWarmingStatusInfo(CacheWarmer.FormatCacheWarmingStatus(status), status.Decision?.EconomicsAvailable == true,
                    status.Decision?.MissCost ?? 0, status.Decision?.WarmCost ?? 0)
                : null,
            SetCacheWarmingMode = _ => Profile()?.CurrentCacheWarmer?.OnModeChanged(),
            AppendLabelChange = (entryId, label) => (startup.Host.CurrentSession ?? throw new InvalidOperationException("The session is not ready."))
                .AppendLabelChangeAsync(entryId, label),
            ReportBug = (ui, hint) =>
            {
                var session = startup.Host.CurrentSession ?? throw new InvalidOperationException("The session is not ready.");
                return InteractiveBugReport.ReportBug(new BugReportSession(session, context, getState?.Invoke(), Profile()?.SummaryGenerator), ui,
                    new BugReportEnvironment { Env = context.GetEnvironment, Cwd = () => startup.Cwd,
                        CrashLogPath = PiSharp.Cli.Diagnostics.CrashReporting.DefaultCrashLogPath() }, hint);
            }
        };
    }

    /// <summary>The /bug flow's view of the session: model, metadata, entries, the transcript (serializeSessionBranch with the
    /// pi.share trailing entry), the summary (generateBugReportSummary through the host's summarization transport) and the
    /// bug report's custom entry.</summary>
    private sealed class BugReportSession(PiSharp.CodingAgent.PersistentAgentSession session, InteractiveModeContext context, SessionState? state,
        PiSharp.CodingAgent.ISessionSummaryGenerator? summaries) : IBugReportSession
    {
        public string? ModelName => SessionEntries.Str(state?.Model?["name"]);
        public string? ModelProvider => SessionEntries.Str(state?.Model?["provider"]);
        public string SessionId => session.Snapshot.Log.Header.Id;
        public Task<string> SummarizeForBugReportAsync(string? hint, CancellationToken cancellationToken)
        {
            if (state?.Model is not JsonObject model) throw new InvalidOperationException("No model selected");
            var generator = summaries ?? throw new InvalidOperationException("Bug report summaries are not available in this session host.");
            static double Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
            var descriptor = session.Snapshot.Agent.Model;
            var options = new BugReportSummaryOptions(session.Snapshot.Context.Messages, Number(model["contextWindow"]), Number(model["maxTokens"]),
                model["reasoning"] is JsonValue reasoning && reasoning.TryGetValue<bool>(out var reasons) && reasons)
            { Hint = hint, ThinkingLevel = state.ThinkingLevel, SessionId = SessionId };
            // completeSummarization through the host's summary transport (the session model, cacheRetention none), with
            // bug-report.ts's reasoning (the session level on a reasoning model) and the session id.
            return BugReport.GenerateBugReportSummaryAsync(options, async (request, token) =>
            {
                var maximum = double.IsFinite(request.MaxTokens) ? Math.Floor(request.MaxTokens) : 4096;
                try
                {
                    var summary = await generator.GenerateAsync(new(PiSharp.Sessions.Compaction.SessionSummaryKind.History, descriptor,
                        request.SystemPrompt, request.Prompt, maximum, request.Reasoning, request.SessionId), token).ConfigureAwait(false);
                    return new PiSharp.Contracts.AssistantMessage(descriptor.Api, descriptor.Provider, descriptor.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        [new PiSharp.Contracts.TextContent(summary.Text)], summary.Usage ?? PiSharp.Contracts.TokenUsage.Zero, PiSharp.Contracts.StopReason.Stop);
                }
                catch (PiSharp.Sessions.Compaction.SessionCompactionException error) when (error.ProviderAborted || token.IsCancellationRequested)
                { throw new InvalidOperationException("Bug report summary was cancelled", error); }
                catch (PiSharp.Sessions.Compaction.SessionCompactionException error) when (error.ProviderErrorMessage is not null)
                { throw new InvalidOperationException("Bug report summary failed: " + (error.ProviderErrorMessage.Length == 0 ? "Unknown error" : error.ProviderErrorMessage), error); }
            }, cancellationToken);
        }
        public BugReportMetadataOptions GetMetadataOptions()
        {
            static PiSharp.Contracts.JsonData Read(string path)
            {
                try { return PiSharp.Contracts.JsonData.Parse(File.Exists(path) ? File.ReadAllText(path) : "{}"); }
                catch { return PiSharp.Contracts.JsonData.Parse("{}"); }
            }
            return new(SessionId, context.Startup.Cwd, false, false, session.Snapshot.Context.Messages.Length, state?.ThinkingLevel ?? "off",
                Read(Path.Join(context.Startup.AgentDir, "settings.json")), Read(Path.Join(context.Startup.Cwd, PiConfig.ConfigDirName, "settings.json")))
            {
                Model = state?.Model is { } model ? PiSharp.Contracts.JsonData.Parse(model.ToJsonString()) : null
            };
        }
        public IEnumerable<SessionEntry> GetEntries() => session.Snapshot.Log.Entries;
        public string SerializeSessionBranch() => PiSharp.CodingAgent.Export.SessionJsonlExport.SerializeSessionBranch(
            PiSharp.CodingAgent.Export.AgentSessionExport.Source(session),
            (parentId, timestamp) => new PiSharp.CodingAgent.Export.SessionShare().CreateShareTrailingEntries(
                PiSharp.CodingAgent.Export.AgentSessionExport.State(session), parentId, timestamp));
        public void AppendCustomEntry(string customType, PiSharp.Contracts.JsonData data) =>
            session.AppendCustomEntryAsync(customType, data).GetAwaiter().GetResult();
    }

    private static string KeyOf(SessionEntry entry)
    {
        try
        {
            return JsonNode.Parse(entry.WireBody.ToString()) is JsonObject body && body["message"] is JsonObject message ? CacheMissNotice.Key(message) : entry.Id;
        }
        catch (JsonException) { return entry.Id; }
    }

    private static PiSharp.AI.ModelOperations.ModelCost? ModelCostOf(JsonObject model)
    {
        if (model["cost"] is not JsonObject cost) return null;
        static double N(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
        return new(N(cost["input"]), N(cost["output"]), N(cost["cacheRead"]), N(cost["cacheWrite"]));
    }
}
