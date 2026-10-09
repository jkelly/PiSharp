using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Globalization;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Storage;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Interactive;
using PiSharp.Extensions;
using PiSharp.Rpc.Ui;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent.Resources;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Cli.Settings;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent.Resources.Skills;

namespace PiSharp.Cli.Commands;

/// <summary>Explicit-path durable RPC host with scripted or explicit live provider admission. Stdout contains only the dispatcher's shared JSONL records.</summary>
public static class RpcSessionCommand
{
    public const string Usage = "session rpc --session <existing absolute JSONL> --workspace <existing absolute directory> " +
        "(--offline-script <absolute JSON> | --live [--provider <provider>] [--model <pattern>[:<thinking>]] [--models <patterns>] [--max-output-tokens 1..8192]) [--thinking off|minimal|low|medium|high|xhigh|max] [--offline-api openai-responses|anthropic-messages|openai-completions] [--offline-images true|false (anthropic-messages|openai-completions)] [--leaf <id>|--root] [--allow-read <absolute file>] [--allow-write <absolute file>] " +
        "[[--bash-executable <absolute file>] --bash-spill-root <existing workspace directory> --allow-bash-command <exact command> [--bash-timeout <seconds>]] " + NativeExtensionConfiguration.Flags + " " + SessionCatalogCommand.Flags + " " + CreationFlags + " " + PromptTemplateCliConfiguration.Flags + " " + SettingsStartupConfiguration.Flags + " " + ToolSelectionCliConfiguration.Flags + " " + SkillCliConfiguration.Flags;
    public const string CreationFlags = "[--session-mode open|new-memory|new-lazy]";
    /// <summary>agent-loop.ts runs tool turns until the model stops, with the whole transcript: the Pi entry has no turn or
    /// transcript-message cap; the explicit verbs keep 64 turns and 1024 messages.</summary>
    internal static AgentLoopOptions LoopOptions(bool pi) => pi ? new(MaximumTurns: int.MaxValue, MaximumTranscriptMessages: int.MaxValue)
        : new(MaximumTurns: 64, MaximumTranscriptMessages: 1024);
    /// <summary>agent.ts holds every tool, subscriber, queued message and progress update: the Pi entry has no count bound on them
    /// (the explicit verbs keep the profile defaults).</summary>
    internal static PiSharp.Agent.AgentOptions AgentOptions(bool pi) => pi
        ? PiPayloadBudget.PiAgent(new(Loop: LoopOptions(true))) : PiPayloadBudget.Agent(new(Loop: LoopOptions(false)));
    /// <summary>session-manager.ts loads every line of the file: the Pi entry has no line or record cap (the explicit verbs keep 10,000).</summary>
    internal static PiSharp.Sessions.Storage.SessionLogReaderOptions ReaderOptions(bool pi) =>
        PiPayloadBudget.SessionReader(pi ? new(MaximumLines: int.MaxValue, MaximumRecords: int.MaxValue) : new(MaximumLines: 10_000, MaximumRecords: 10_000));
    /// <summary>buildSessionContext walks every entry of the branch: the Pi entry has no entry, ancestor or message cap.</summary>
    internal static PiSharp.Sessions.Context.SessionContextProjectionOptions ContextOptions(bool pi) => pi
        ? PiPayloadBudget.Context with { MaximumEntries = int.MaxValue, MaximumAncestorSteps = int.MaxValue, MaximumOutputMessages = int.MaxValue }
        : PiPayloadBudget.Context;
    /// <summary>Pi-entry prompt bounds: no text length or image count limit; the images and the message stay within one RPC frame.</summary>
    private static readonly PromptInputAdmissionOptions PiPromptBounds = new(MaximumTextCharacters: int.MaxValue, MaximumImages: int.MaxValue,
        MaximumImageCharacters: PiPayloadBudget.RpcCommandBytes, MaximumImageBytes: PiPayloadBudget.RpcCommandBytes, MaximumJsonDepth: 64,
        MaximumMessageCharacters: PiPayloadBudget.RpcCommandBytes);
    private static readonly JsonlTransportOptions Framing =new(MaximumFrameBytes: PiPayloadBudget.RpcCommandBytes, MaximumJsonDepth: 32, MaximumPendingWrites: 32);
    private sealed record Arguments(string Session, string Workspace, string? Script, bool Latest, string? Leaf,
        ImmutableArray<string> Reads, ImmutableArray<string> Writes, string OfflineApi, OfflineBashAuthorization? Bash,
        NativeExtensionConfiguration? Extension, bool SupportsImages, ImmutableArray<SessionCatalogStore> Stores, string SessionMode, SettingsModelSelection? Live,
        PromptTemplateCliConfiguration Prompts, StartupSettingsRequest? Settings, string? Thinking, ToolSelectionCliOptions Tools, SkillCliConfiguration Skills)
    {
        /// <summary><c>--tool-policy pi|explicit</c> (decision 0004); these verbs default to explicit.</summary>
        internal string? ToolPolicy { get; init; }
    }

    public static Task<int> RunAsync(string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        CancellationToken cancellationToken = default, PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission = null,
        Func<bool, CancellationToken, Task>? persistRetryEnabledOriginal = null, PiSharp.Cli.Reloading.NativeHostReloadAdmission? reloadAdmission = null) =>
        RunCoreAsync(args, stdin, stdout, stderr, null, cancellationToken, mcpAdmission: mcpAdmission, persistRetryEnabledOriginal: persistRetryEnabledOriginal, reloadAdmission: reloadAdmission);

    /// <summary>The production entry: the session's MCP servers come from <paramref name="mcpHost"/> (the global mcp.json).</summary>
    /// <param name="userShutdown">True when the cancellation is a requested shutdown (Pi SIGTERM/SIGHUP), not a failure.</param>
    internal static Task<int> RunHostedAsync(string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        PiSharp.Cli.Mcp.McpSessionHost mcpHost, CancellationToken cancellationToken = default, Func<bool>? userShutdown = null) =>
        RunCoreAsync(args, stdin, stdout, stderr, null, cancellationToken, userShutdown, mcpHost: mcpHost ?? throw new ArgumentNullException(nameof(mcpHost)));

    /// <summary>Same host lifecycle with injected reads for explicitly selected settings files.</summary>
    public static Task<int> RunWithSettingsAsync(string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        IStartupSettingsFileSystem settingsFileSystem, CancellationToken cancellationToken = default,
        PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission = null,
        Func<bool, CancellationToken, Task>? persistRetryEnabledOriginal = null, PiSharp.Cli.Reloading.NativeHostReloadAdmission? reloadAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(settingsFileSystem);
        return RunCoreAsync(args, stdin, stdout, stderr, null, cancellationToken, settingsFileSystem: settingsFileSystem, mcpAdmission: mcpAdmission, persistRetryEnabledOriginal: persistRetryEnabledOriginal, reloadAdmission: reloadAdmission);
    }

    internal static Task<int> RunWithPresentationAsync(string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        IRpcExtensionUiPresentationObserver presentation, CancellationToken cancellationToken, Func<bool>? userShutdown = null,
        Func<RpcSessionShutdownSettlement, ValueTask<RpcTerminalStoppedAcknowledgment>>? stopTerminalAndJoin = null,
        LiveSessionRuntime? liveRuntime = null, PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission = null,
        Func<bool, CancellationToken, Task>? persistRetryEnabledOriginal = null, PiSharp.Cli.Reloading.NativeHostReloadAdmission? reloadAdmission = null,
        TerminalExtensionInputAdmission? terminalInputAdmission = null,
        Func<IExtensionUiProvider, IExtensionUiProvider>? decorateTerminalUi = null, PiSharp.Cli.Mcp.McpSessionHost? mcpHost = null,
        bool javaScriptInput = false) =>
        RunCoreAsync(args, stdin, stdout, stderr, presentation, cancellationToken, userShutdown, stopTerminalAndJoin, liveRuntime, mcpAdmission: mcpAdmission, persistRetryEnabledOriginal: persistRetryEnabledOriginal, reloadAdmission: reloadAdmission, terminalInputAdmission: terminalInputAdmission, decorateTerminalUi: decorateTerminalUi, mcpHost: mcpHost, javaScriptInput: javaScriptInput);

    /// <summary>agent-session.ts _getThinkingLevelForModelSwitch: the settings' per-model level (modelThinkingLevels), else
    /// defaultThinkingLevel, else null (the current level stays).</summary>
    internal static string? ModelSwitchThinkingLevel(StartupSettingsSnapshot? settings, ModelDescriptor model)
    {
        if (settings?.Values.Value is not { ValueKind: JsonValueKind.Object } values) return null;
        if (values.TryGetProperty("modelThinkingLevels", out var overrides) && overrides.ValueKind == JsonValueKind.Object &&
            overrides.TryGetProperty(model.Provider + "/" + model.Id, out var perModel) && perModel.ValueKind == JsonValueKind.String &&
            perModel.GetString() is { Length: > 0 } level) return level;
        return values.TryGetProperty("defaultThinkingLevel", out var fallback) && fallback.ValueKind == JsonValueKind.String &&
            fallback.GetString() is { Length: > 0 } defaultLevel ? defaultLevel : null;
    }

    /// <summary>settings-manager.ts getCompactionSettings(model): compaction.enabled, and reserveTokens/keepRecentTokens from
    /// compaction.modelOverrides["provider/id"], then compaction, then the defaults; a token value that is not a non-negative safe
    /// integer is refused with upstream's message.</summary>
    internal static PiSharp.Sessions.Compaction.SessionCompactionSettings CompactionSettings(StartupSettingsSnapshot? settings, ModelDescriptor model)
    {
        var defaults = new PiSharp.Sessions.Compaction.SessionCompactionSettings();
        if (settings?.Values.Value is not { ValueKind: JsonValueKind.Object } values || !values.TryGetProperty("compaction", out var compaction) ||
            compaction.ValueKind != JsonValueKind.Object) return defaults;
        var key = model.Provider + "/" + model.Id;
        JsonElement? overrides = null;
        if (compaction.TryGetProperty("modelOverrides", out var all) && all.ValueKind == JsonValueKind.Object && all.TryGetProperty(key, out var entry))
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Invalid compaction.modelOverrides[\"{key}\"] setting: {entry.GetRawText()}. Expected an object.");
            overrides = entry;
        }
        double Token(string field, double fallback)
        {
            static bool Safe(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
                number >= 0 && number == Math.Floor(number) && number <= 9007199254740991;
            if (compaction.TryGetProperty(field, out var ordinary) && !Safe(ordinary))
                throw new InvalidOperationException($"Invalid compaction.{field} setting: {ordinary.GetRawText().Trim('"')}. Expected a non-negative safe integer.");
            if (overrides is { } modelEntry && modelEntry.TryGetProperty(field, out var overridden))
            {
                if (!Safe(overridden))
                    throw new InvalidOperationException($"Invalid compaction.modelOverrides[\"{key}\"].{field} setting: {overridden.GetRawText().Trim('"')}. Expected a non-negative safe integer.");
                return overridden.GetDouble();
            }
            return compaction.TryGetProperty(field, out var value) ? value.GetDouble() : fallback;
        }
        var enabled = !(compaction.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.False);
        return new(enabled, Token("reserveTokens", defaults.ReserveTokens), Token("keepRecentTokens", defaults.KeepRecentTokens));
    }

    private static async Task<int> RunCoreAsync(string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        IRpcExtensionUiPresentationObserver? presentation, CancellationToken cancellationToken, Func<bool>? userShutdown = null,
        Func<RpcSessionShutdownSettlement, ValueTask<RpcTerminalStoppedAcknowledgment>>? stopTerminalAndJoin = null,
        LiveSessionRuntime? liveRuntime = null, IStartupSettingsFileSystem? settingsFileSystem = null,
        PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission = null,
        Func<bool, CancellationToken, Task>? persistRetryEnabledOriginal = null, PiSharp.Cli.Reloading.NativeHostReloadAdmission? reloadAdmission = null,
        TerminalExtensionInputAdmission? terminalInputAdmission = null,
        Func<IExtensionUiProvider, IExtensionUiProvider>? decorateTerminalUi = null, PiSharp.Cli.Mcp.McpSessionHost? mcpHost = null,
        bool javaScriptInput = false)
    {
        ArgumentNullException.ThrowIfNull(stdin); ArgumentNullException.ThrowIfNull(stdout); ArgumentNullException.ThrowIfNull(stderr);
        OfflineSessionProfile? profile = null; PersistentAgentSession? session = null;
        RpcExtensionUiCoordinator? ui = null; RpcSessionDispatcher? dispatcher = null; JsonlReader? reader = null; JsonlWriter? writer = null;
        InputObservation? observedInput = null; OutputObservation? observedOutput = null; OfflineGate? gate = null;
        SessionStorageBackend? backend = null; Exception? operationFailure = null;
        var cleanupFailures = new List<Exception>();
        using var lifecycleStop = new CancellationTokenSource();
        using var lifecycleRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifecycleStop.Token);
        try
        {
            var parsed = Parse(args);
            cancellationToken.ThrowIfCancellationRequested();
            if (!stdin.CanRead || !stdout.CanWrite) throw Invalid();
            if (!Directory.Exists(parsed.Workspace)) throw new SessionCommandException(SessionCommandFailure.WorkspaceMissing);
            // A Pi-style entry (plain pisharp, -p, --mode json|rpc) already resolved settings, model, prompt and tool policy.
            var pi = PiSharp.Cli.Pi.PiEntryOptions.Current;
            // Pi has no prompt length or image count limit; template, skill and extension input admission use these bounds too.
            using var piPromptBounds = pi is null ? null : PromptInputAdmissionOptions.UseAsDefault(PiPromptBounds);
            liveRuntime ??= pi?.LiveRuntime;
            var settings = pi?.Settings ?? await SettingsStartupConfiguration.LoadAsync(parsed.Settings, stderr, settingsFileSystem, cancellationToken).ConfigureAwait(false);
            var liveSelection = pi?.Selection ?? (parsed.Live is null ? null : await parsed.Live.ResolveAsync(settings, liveRuntime ?? LiveSessionRuntime.Default, stderr,
                parsed.SessionMode == "open", cancellationToken).ConfigureAwait(false));
            // Production sessions read the global mcp.json once at start (--no-mcp connects nothing); every session gets the built-in codemode.
            var hostAdmission = mcpAdmission is null && mcpHost is not null;
            if (hostAdmission) mcpAdmission = mcpHost!.CreateAdmission(parsed.Workspace, stderr, settings?.Values, parsed.Tools.NoMcp);
            backend = parsed.SessionMode == "open" ? null : new SessionStorageBackend(Path.GetDirectoryName(parsed.Session)!,
                parsed.SessionMode == "new-memory" ? SessionStorageMode.InMemory : SessionStorageMode.LazyLocal,
                new(MaximumFileBytes: PiPayloadBudget.SessionFileBytes, MaximumResidentBytes: PiPayloadBudget.SessionFileBytes));
            var turns = parsed.Script is null ? ImmutableArray<JsonData>.Empty :
                await SessionCommands.ScriptAsync(parsed.Script, cancellationToken).ConfigureAwait(false);
            gate = OfflineGate.From(turns);
            // Pending input commands may carry Pi-sized images; retain two maximal commands while a dialog is open.
            // Pi extensions (IMPL-E) get a UI in the modes upstream gives one (tui and rpc); print and json run without (hasUI false).
            var piExtensions = pi?.Extensions;
            ui = parsed.Extension is null && (piExtensions is null || pi!.ExtensionMode is not ("rpc" or "tui")) ? null
                : new(pi is null ? new RpcExtensionUiOptions(MaximumRetainedOrdinaryBytes: 2 * PiPayloadBudget.RpcCommandBytes)
                    // rpc-mode.ts createExtensionUIContext: dialogs, their texts, choices and responses have no limits of their own;
                    // requests and responses stay within one frame and a bounded number of open dialogs.
                    : new RpcExtensionUiOptions(MaximumOutstandingRequests: 4096, MaximumRequestBytes: PiPayloadBudget.OutputRecordBytes,
                        MaximumResponseBytes: PiPayloadBudget.RpcCommandBytes, MaximumRetainedBytes: 4 * PiPayloadBudget.RpcCommandBytes,
                        MaximumTextCharacters: int.MaxValue, MaximumChoices: int.MaxValue, MaximumJsonDepth: 64, MaximumIdCharacters: int.MaxValue,
                        MaximumRetainedOrdinaryBytes: 2 * PiPayloadBudget.RpcCommandBytes),
                    presentationObserver: presentation);
            if (piExtensions is not null)
            {
                // Source emitError: print and json modes write "Extension error (<path>): <error>"; RPC publishes an extension_error record.
                piExtensions.ReportError = async (path, eventName, error) =>
                {
                    if (dispatcher is { } rpc) await rpc.PublishExtensionErrorAsync(path, eventName, error).ConfigureAwait(false);
                    else { await stderr.WriteAsync(($"Extension error ({path}): {error}\n").AsMemory()).ConfigureAwait(false); await stderr.FlushAsync().ConfigureAwait(false); }
                };
                piExtensions.Settings = () => settings?.Values is { } values ? System.Text.Json.Nodes.JsonNode.Parse(values.ToString()) : null;
            }
            IExtensionUiProvider? activationUi = ui is null ? null : decorateTerminalUi?.Invoke(ui) ?? ui;
            if (activationUi is not null && terminalInputAdmission is not null) activationUi = terminalInputAdmission.Decorate(activationUi);
            if (piExtensions is not null) piExtensions.UiProvider = activationUi;
            profile = await OfflineSessionProfile.CreateAsync(parsed.Workspace, parsed.Session, parsed.Script,
                turns, parsed.Reads, parsed.Writes, cancellationToken, gate.BeforeSendAsync, parsed.OfflineApi, parsed.Bash, parsed.Extension, activationUi,
                async (diagnostic, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (pi?.Extensions is { } nodeExtensions && diagnostic.OwnerId.StartsWith("pi-extension-", StringComparison.Ordinal))
                    {
                        await nodeExtensions.ReportAsync(nodeExtensions.PathOfOwner(diagnostic.OwnerId), diagnostic.EventName, diagnostic.ErrorText).ConfigureAwait(false);
                        return;
                    }
                    await stderr.WriteAsync((JsonSerializer.Serialize(new { schemaVersion = 1, type = "extension_input_diagnostic",
                        eventName = diagnostic.EventName, ownerId = diagnostic.OwnerId, ownerGeneration = diagnostic.OwnerGeneration,
                        registrationId = diagnostic.RegistrationId, failure = diagnostic.Failure.ToString() }) + "\n").AsMemory(), token).ConfigureAwait(false);
                    await stderr.FlushAsync(token).ConfigureAwait(false);
                    // Source RPC onError: an extension_error record on stdout once the dispatcher owns the output.
                    if (dispatcher is { } rpc) await rpc.PublishExtensionErrorAsync(parsed.Extension?.Package ?? diagnostic.OwnerId,
                        diagnostic.EventName, diagnostic.ErrorText).ConfigureAwait(false);
                }, modelSupportsImages: parsed.SupportsImages, liveSelection: liveSelection, liveRuntime: liveRuntime,
                toolSelection: ToolSelectionCliConfiguration.ResolveOptions(parsed.Tools, settings) ??
                    (pi is null ? null : new InitialToolSelection(PiSharp.Tools.BuiltinToolPrompts.DefaultToolNames, true)),
                mcpAdmission: parsed.Tools.NoMcp && !hostAdmission ? null : mcpAdmission,
                toolSettings: PiSharp.Tools.BuiltinToolSettings.FromSettings(settings?.Values),
                originalSystemPrompt: pi?.SystemPrompt, toolPolicy: pi?.ToolPolicy ?? (parsed.ToolPolicy == "pi"
                    ? new PiSharp.Cli.Pi.PiToolPolicy(PiSharp.Cli.Pi.PiToolPolicyMode.Pi) { ProtectedDirectories = [Path.GetDirectoryName(parsed.Session)!] } : null),
                // With --no-mcp extensions still register servers; nothing connects them, which is reported (reportUnhandledMcpServers).
                mcpRegistrations: hostAdmission ? mcpHost!.Registrations : null, piExtensions: pi?.Extensions,
                deferMissingCredentials: pi is not null, piEntry: pi is not null).ConfigureAwait(false);
            // A virtual selection's router reads this profile's session branch and records its state there.
            if (liveSelection is { IsVirtual: true } virtualSelection) virtualSelection.VirtualSession = profile.CurrentVirtualModelSession;
            if (pi?.Extensions is { } modelsHost) await PiSharp.Cli.Extensions.Pi.PiExtensionModels.CreateAsync(modelsHost, liveRuntime ?? LiveSessionRuntime.Default, cancellationToken).ConfigureAwait(false);
            profile.ConfigureRetrySettings(settings, persistRetryEnabledOriginal);
            profile.ConfigureEffectiveSettings(settings);
            profile.BindSettingsThinkingReads();
            if (pi?.Skills is { } piSkills) profile.AdoptOriginalPromptSkills(piSkills);
            // /reload and ctx.reload() (agent-session.ts reload): the Pi entry reloads extensions and resources in place.
            if (pi is not null) { profile.PiReloadResources = pi.ReloadResources; if (pi.Extensions is { } reloading) { var reloader = profile; reloading.Reload = reloader.PiReloadAsync; } }
            else await profile.LoadSkillsAsync(parsed.Skills, stderr, cancellationToken).ConfigureAwait(false);
            long ticks = 0; var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long Clock() => started + Interlocked.Increment(ref ticks);
            var options = new PersistentAgentSessionOptions(UseLatestLeaf: parsed.Latest, SelectedLeafId: parsed.Leaf,
                AgentOptions: AgentOptions(pi is not null),
                SessionLogStoreOptions: new(ReaderOptions: ReaderOptions(pi is not null), JavaScriptSerialization: pi is not null),
                ContextOptions: ContextOptions(pi is not null));
            // agent-session.ts prompt: Pi entries validate the model and its provider auth before each idle prompt.
            if (pi is not null)
                options = options with { PromptPreflight = PromptPreflight(liveRuntime ?? LiveSessionRuntime.Default), UnselectedModel = LiveSessionSelection.UnselectedModel,
                    // main.ts buildSessionOptions: --model is the model of every session the run creates or opens.
                    SelectedModel = liveSelection is { FromCliModel: true } fromCli ? fromCli.Model : null,
                    // sdk.ts: a session without messages starts with the CLI level, else the per-model or default setting, clamped.
                    // main.ts createRuntime for a session without messages: the scoped pick, else findInitialModel (no model: none).
                    NewSessionModel = async token =>
                    {
                        var current = pi.ReloadSettings is { } reloadModel ? await reloadModel(token).ConfigureAwait(false) : settings;
                        try
                        {
                            return (await new SettingsModelSelection(null, null, null) { ModelPatterns = pi.ModelPatterns, UseModelMaximumTokens = true }
                                .ResolveAsync(current, liveRuntime ?? LiveSessionRuntime.Default, null, false, token).ConfigureAwait(false)).Model;
                        }
                        catch (LiveSessionException error) when (error.Code == "NoLiveModel") { return LiveSessionSelection.UnselectedModel; }
                        catch (Exception error) when (error is LiveSessionException or SessionCommandException) { return null; }
                    },
                    NewSessionThinkingLevel = (model, levels) => SettingsModelSelection.Thinking(pi.ReloadSettings is { } reloadThinking
                        ? reloadThinking(CancellationToken.None).GetAwaiter().GetResult() : settings, model, parsed.Thinking ?? liveSelection?.PatternThinkingLevel, false, levels) };
            // session-manager.ts generateId: randomUUID().slice(0, 8); PersistentAgentSession redraws an id already in use.
            static string NextId() => Guid.NewGuid().ToString("N")[..8];
            var catalog = new SessionCatalog(parsed.Stores.IsEmpty ? [new("session-directory", Path.GetDirectoryName(parsed.Session)!)] : parsed.Stores,
                fileSystem: backend);
            if (pi is not null)
            {
                // sdk.ts createAgentSession restore: a session whose branch model is not available runs on the fallback model. At startup
                // the entry already chose it (the profile's model); a session switched to later (/resume) gets findInitialModel's pick
                // over the current available snapshot as a continued session.
                var restoring = profile;
                restoring.RestoreFallback = (_, fallback) =>
                {
                    if (restoring.LiveModels is not { CurrentRegistry: { } registry } models) return fallback;
                    var current = pi.ReloadSettings is { } reload ? reload(CancellationToken.None).GetAwaiter().GetResult() : settings;
                    return SettingsModelSelection.ContinuingInitialModel(registry, current) is { } initial &&
                        models.Available.FirstOrDefault(definition => definition.Model.Provider == initial.Provider && definition.Model.Id == initial.Id) is { } bound
                        ? bound.Model : fallback;
                };
            }
            var lifecycle = profile.CreateLifecycle(Clock, NextId, options, catalog: catalog, backend: backend);
            // agent-session.ts switches sessions, reloads and holds MCP servers any number of times in one process.
            if (pi is not null) { lifecycle.MaximumOwnerAttachments = int.MaxValue; lifecycle.MaximumOwnedResources = int.MaxValue; }
            // sdk.ts createAgentSession for a new session (/new, new_session): the CLI level, else the per-model or global default
            // the settings files hold now, clamped to the model.
            lifecycle.ConfigureNewSession = async (created, token) =>
            {
                var current = pi?.ReloadSettings is { } reload ? await reload(token).ConfigureAwait(false) : settings;
                var level = SettingsModelSelection.Thinking(current, created.Snapshot.Agent.Model, parsed.Thinking ?? liveSelection?.PatternThinkingLevel,
                    false, created.GetSupportedThinkingLevels());
                if (level is not null && level != created.Snapshot.Context.ThinkingLevel)
                    await created.ConfigureAsync(new(ThinkingLevel: level), token).ConfigureAwait(false);
            };
            // sdk.ts createAgentSession for a new session (/new, new_session): the CLI level, else the per-model or global default
            // the settings files hold now, clamped to the model.
            lifecycle.ConfigureNewSession = async (created, token) =>
            {
                var current = pi?.ReloadSettings is { } reload ? await reload(token).ConfigureAwait(false) : settings;
                var level = SettingsModelSelection.Thinking(current, created.Snapshot.Agent.Model, parsed.Thinking ?? liveSelection?.PatternThinkingLevel,
                    false, created.GetSupportedThinkingLevels());
                if (level is not null && level != created.Snapshot.Context.ThinkingLevel)
                    await created.ConfigureAsync(new(ThinkingLevel: level), token).ConfigureAwait(false);
            };
            session = parsed.SessionMode == "open"
                ? await lifecycle.OpenAsync(new(parsed.Session, parsed.Latest, parsed.Leaf), profile.SelectedModel, cancellationToken).ConfigureAwait(false)
                : await lifecycle.CreateAsync(parsed.Session, new PiSharp.Sessions.Serialization.SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                    { type = "session", version = 3, id = pi?.HeaderId ?? NextId(), timestamp = pi?.HeaderTimestamp ?? DateTimeOffset.FromUnixTimeMilliseconds(Clock()).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), cwd = profile.Workspace })),
                    profile.SelectedModel, cancellationToken).ConfigureAwait(false);
            // sdk.ts createAgentSession writes only model_change and thinking_level_change for a new session; its prompt sections and
            // tool loadout are recorded by the first request (agent-session.ts _preparePromptAndToolLoadout, agent-loop.ts
            // declareToolChanges: one system message before the prompt). The Pi entry applies the loadout in memory until then.
            if (parsed.SessionMode != "open" && pi is null) await session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem)), cancellationToken).ConfigureAwait(false);
            else if (parsed.SessionMode != "open") await session.ApplyInitialToolsAsync(profile.InitialToolNames, cancellationToken).ConfigureAwait(false);
            // A session whose stored cwd no longer exists continues in the cwd the user chose (main.ts promptForMissingSessionCwd).
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var continuesElsewhere = pi?.SessionCwdOverride is { } cwdOverride && string.Equals(SessionCommands.Absolute(cwdOverride), profile.Workspace, pathComparison) &&
                !Directory.Exists(session.WorkingDirectory);
            if (!continuesElsewhere && !string.Equals(SessionCommands.Absolute(session.WorkingDirectory), profile.Workspace, pathComparison))
                throw new SessionCommandException(SessionCommandFailure.WorkspaceMismatch);
            if (continuesElsewhere) lifecycle.WorkingDirectoryOverride = profile.Workspace;
            var thinking = SettingsModelSelection.Thinking(settings, session.Snapshot.Agent.Model, parsed.Thinking ?? liveSelection?.PatternThinkingLevel,
                parsed.SessionMode == "open", session.GetSupportedThinkingLevels());
            // sdk.ts: an existing session takes the CLI level in memory (no thinking_level_change; the Pi entry), a new one recorded it.
            if (thinking is not null && thinking != session.Snapshot.Context.ThinkingLevel)
            {
                if (pi is not null && parsed.SessionMode == "open") await session.ApplyInitialThinkingLevelAsync(thinking, cancellationToken).ConfigureAwait(false);
                else await session.ConfigureAsync(new(ThinkingLevel: thinking), cancellationToken).ConfigureAwait(false);
            }
            await profile.LoadPromptTemplatesAsync(pi?.PromptTemplates ?? parsed.Prompts, pi is null ? stderr : TextWriter.Null, cancellationToken).ConfigureAwait(false);
            if (pi?.SessionName is { } sessionName) await session.SetSessionNameAsync(session.Snapshot.Log.Header.Id, sessionName, cancellationToken).ConfigureAwait(false);
            if (parsed.SessionMode == "open") await profile.ApplySkillsAsync(session, cancellationToken).ConfigureAwait(false);
            if (settings is not null) { session.SteeringMode = settings.SteeringMode; session.FollowUpMode = settings.FollowUpMode; }
            await profile.AttachOwnerAsync(session, options, Clock, NextId, parsed.Stores.IsEmpty ? null : parsed.Stores, lifecycle).ConfigureAwait(false);
            if (reloadAdmission is not null) profile.ConfigureReload(reloadAdmission);
            await profile.ApplyInitialToolSelectionAsync(session, cancellationToken, resumed: parsed.SessionMode == "open" || pi is not null).ConfigureAwait(false);
            observedInput = new InputObservation(stdin, gate);
            // Events and responses carry tool results with Pi-sized images (owner decision 0004).
            // pi --mode rpc reads each line as rpc-mode.ts handleInputLine does (StringDecoder + JSON.parse). JSON.parse and
            // JSON.stringify have no depth limit; 64 levels is what an owned JsonData holds.
            // The in-process print, json and interactive connections carry the same values (a custom entry or message of any depth).
            var framing = javaScriptInput ? Framing with { MaximumJsonDepth = 64, JavaScriptInput = true } : pi is not null ? Framing with { MaximumJsonDepth = 64 } : Framing;
            // Every frame Pi writes is serializeJsonLine, JSON.stringify(value) + "\n".
            var outputFraming = framing with { MaximumFrameBytes = PiPayloadBudget.OutputRecordBytes, JavaScriptInput = false, JavaScriptOutput = javaScriptInput };
            observedOutput = new OutputObservation(stdout, gate, outputFraming.MaximumFrameBytes);
            reader = new JsonlReader(observedInput, framing);
            writer = new JsonlWriter(observedOutput, outputFraming);
            // The profile retains resource ownership across the terminal-stopped boundary. Dispatcher cleanup
            // already fences RPC/UI admission and joins its original run, reader, callbacks and writer.
            // Pi entries switch among the registry's available models (set_model, cycle_model and the --models scope; /model and Ctrl+P
            // in interactive mode), as agent-session.ts setModel/cycleModel do over modelRuntime.getAvailableSnapshot.
            var liveModels = pi is not null && profile.IsLive
                ? await profile.EnableModelSwitchingAsync(liveRuntime ?? LiveSessionRuntime.Default, cancellationToken).ConfigureAwait(false) : null;
            // model-runtime.ts registerProvider/unregisterProvider/unregisterVirtualModel after startup: the selectable models follow.
            if (pi?.Extensions is { } providerHost && liveModels is not null)
                providerHost.ProvidersChanged = () => _ = liveModels.RefreshAsync().ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            var scope = liveSelection?.ScopedModels ?? [];
            var modelRuntime = liveModels is null ? null : new PiSharp.Rpc.Protocol.RpcModelRuntime(() => liveModels.Available)
            {
                Scoped = scope.IsDefaultOrEmpty ? null : () => [.. scope.Select(scoped =>
                    new PiSharp.Rpc.Protocol.RpcScopedModel(new(scoped.Model.Id, scoped.Model.Api, scoped.Model.Provider), scoped.ThinkingLevel))],
                SwitchThinkingLevel = model => ModelSwitchThinkingLevel(pi?.ReloadSettings is { } reload ? reload(CancellationToken.None).GetAwaiter().GetResult() : settings, model)
            };
            dispatcher = new(session, writer, Clock, [new(profile.SelectedModel, profile.SelectedModelWire)],
                options: new PiSharp.Rpc.Protocol.RpcDispatchOptions(MaximumCommandBytes: PiPayloadBudget.RpcCommandBytes, MaximumOutputBytes: outputFraming.MaximumFrameBytes,
                    MaximumModels: 4096, MaximumModelDefinitionBytes: 16 * 1024 * 1024, MaximumJsonDepth: framing.MaximumJsonDepth,
                    // rpc-mode.ts has no id or type length limit.
                    MaximumIdCharacters: javaScriptInput ? int.MaxValue : 256, MaximumCommandTypeCharacters: javaScriptInput ? int.MaxValue : 128,
                    // Pi has no prompt, steer, follow_up or bash text limit and no image count limit (the frame bound stays).
                    MaximumPromptCharacters: pi is null ? 65_536 : int.MaxValue, MaximumImages: pi is null ? 16 : int.MaxValue)
                    // rpc-mode.ts answers get_messages/get_entries with the whole session, handles every command as it arrives, reports every
                    // started tool and continues while input is queued: the Pi entry has no count bound on them (frames stay the memory bound).
                    with { MaximumModels = pi is null ? 4096 : int.MaxValue, MaximumReturnedMessages = pi is null ? 1024 : int.MaxValue,
                        MaximumReturnedEntries = pi is null ? 4096 : int.MaxValue, MaximumPendingToolMessages = pi is null ? 128 : int.MaxValue,
                        MaximumContinuationRuns = pi is null ? 16 : int.MaxValue, MaximumConcurrentCommands = pi is null ? 8 : int.MaxValue },
                sessionOwnership: RpcSessionOwnership.Borrowed, inputAdmission: profile.InputAdmission, extensionUi: ui,
                extensionCommandCatalog: profile, sessionOwner: profile.Sessions,
                // main.ts: the initial runtime's session starts with reason "startup" in the Pi entry (new, continued or resumed alike).
                sessionStartup: token => profile.StartLifecycleAsync(pi is not null ? "startup" : parsed.SessionMode == "open" ? "resume" : "new", token),
                summaryGenerator: profile.SummaryGenerator, recoveryDesiredMaxOutput: profile.OriginalDesiredMaxOutput,
                inputAdmissionSelector: profile.PromptInputSelector, exportHtmlWriter: profile.ExportHtmlWriter,
                selectedTreePublisher: profile.PublishSelectedTreeAsync,
                postInputSettlement: profile.DrainLifecycleHandoffsAsync,
                postRunSettlement: profile.DrainLifecycleHandoffsAsync, userBash: profile.UserBash, modelRuntime: modelRuntime,
                compactionSettings: pi is null ? null : model => CompactionSettings(pi.ReloadSettings is { } reloadCompaction
                    ? reloadCompaction(CancellationToken.None).GetAwaiter().GetResult() : settings, model),
                // agent-session-runtime.ts switchSession: SessionManager.open(sessionPath) for the Pi entry.
                prepareSessionPath: pi is null ? null : (path, token) => PrepareSessionPathAsync(path, backend, profile.Workspace, token));
            profile.ConfigureLifecycleModeStop(lifecycleStop.CancelAsync);
            if (pi?.Extensions is { } compactingExtensions)
            {
                var compactor = dispatcher;
                compactingExtensions.Compact = async (instructions, token) =>
                    System.Text.Json.Nodes.JsonNode.Parse((await compactor.CompactForExtensionAsync(profile.ManualCompactionRequest(instructions), token).ConfigureAwait(false)).ToString());
            }
            // IMPL-I: the interactive mode reads the live session for features the RPC protocol does not carry.
            var publishedProfile = profile; var publishedSession = session;
            pi?.Interactive?.Host.Publish(() => publishedProfile.Sessions?.Current.Session ?? publishedSession, publishedProfile);
            // The dispatcher took ownership of the idle session: background MCP servers may publish their tools from now on.
            if (mcpHost is not null && mcpAdmission is not null) mcpHost.HostStarted(mcpAdmission);
            await dispatcher.RunAsync(reader, lifecycleRun.Token).ConfigureAwait(false);
        }
        catch (Exception error) { operationFailure = error; }
        // A run/subscriber failure may poison session authority even when resource
        // cleanup succeeds. Retain that original fault separately from later cleanup.
        var operationFault = (profile?.Sessions?.Current.Session ?? session)?.Snapshot.Fault;
        // Phase one runs even after startup failure. Cleanup has no caller token and joins each original.
        gate?.Release();
        if (dispatcher is not null)
        {
            // RunAsync and DisposeAsync join the same original task. Its retained operation
            // failure is not a new cleanup failure merely because disposal observes it again.
            try
            {
                var dispatcherSettlement = await dispatcher.SettleAsync().ConfigureAwait(false);
                if (ReferenceEquals(operationFailure, dispatcherSettlement.CompletionFailure))
                    operationFailure = dispatcherSettlement.OperationFailure;
                else if (dispatcherSettlement.OperationFailure is { } dispatchFailure)
                    operationFailure = operationFailure is null ? dispatchFailure : new AggregateException(operationFailure, dispatchFailure);
                cleanupFailures.AddRange(dispatcherSettlement.CleanupFailures);
            }
            catch (Exception error) { cleanupFailures.Add(error); }
        }
        else if (writer is not null) await Cleanup(() => writer.DisposeAsync()).ConfigureAwait(false);
        if (reader is not null) await Cleanup(() => reader.DisposeAsync()).ConfigureAwait(false);
        ExtensionSessionSnapshot? shutdownSnapshot = null;
        // Rejected initial acquisition can already have retired the startup view.
        // Only an attached session supplies a shutdown notification target; all
        // acquired resources are still disposed below, including unattached ones.
        var shutdownProfile = profile?.Sessions is not null ? profile : null;
        // Dispatcher work has joined; capture the acknowledged final attachment before phase one
        // cancels its lifetime. Retention grants read authority only, never a new action scope.
        try { shutdownSnapshot = shutdownProfile?.CaptureShutdownSessionSnapshot(); }
        catch (Exception error) { cleanupFailures.Add(error); }
        if (profile?.Sessions is { } owner) await Cleanup(() => new(owner.StopAdmissionAndJoinAsync())).ConfigureAwait(false);
        else if (session is not null) await Cleanup(() => new(session.StopAdmissionAndJoinAsync())).ConfigureAwait(false);
        if (terminalInputAdmission is not null) await Cleanup(() => terminalInputAdmission.StopAdmissionAndJoinAsync()).ConfigureAwait(false);
        if (ui is not null) await Cleanup(() => ui.DisposeAsync()).ConfigureAwait(false);
        if (observedInput is not null) await Cleanup(() => observedInput.DisposeAsync()).ConfigureAwait(false);
        if (observedOutput is not null) await Cleanup(() => observedOutput.DisposeAsync()).ConfigureAwait(false);
        if (profile is not null) cleanupFailures.AddRange(profile.ProcessCleanupFailures);

        RpcSessionShutdownSettlement? settlement = null;
        if (stopTerminalAndJoin is not null)
        {
            settlement = new((operationFailure is null ? cleanupFailures : cleanupFailures.Prepend(operationFailure)).ToImmutableArray(),
                (profile?.Sessions?.Current.Session ?? session)?.Snapshot, profile?.ProcessCleanupReceipts ?? []);
            try
            {
                var acknowledgment = await stopTerminalAndJoin(settlement).ConfigureAwait(false);
                _ = settlement.Validate(acknowledgment);
            }
            catch (Exception error) { cleanupFailures.Add(error); }
            finally { cleanupFailures.AddRange(settlement.CaptureAcceptedTerminalFailures()); }
        }
        // Phase two: every owned resource close is attempted and joined, including an unattached session.
        // Retired UI scopes cannot repaint or admit input while lifecycle disposal is running.
        var runtimeFailures = new List<Exception>();
        if (shutdownProfile is not null)
            await RuntimeCleanup(async () => { _ = await shutdownProfile.DispatchSessionShutdownAsync(shutdownSnapshot).ConfigureAwait(false); }).ConfigureAwait(false);
        if (profile is not null) await RuntimeCleanup(() => profile.DisposeAsync()).ConfigureAwait(false);
        if (session is not null) await RuntimeCleanup(() => session.DisposeAsync()).ConfigureAwait(false);
        cleanupFailures.AddRange(runtimeFailures);
        settlement?.CompleteRuntimeCleanup(runtimeFailures.ToImmutableArray());
        try
        {
            var finalSession = profile?.Sessions?.Current.Session ?? session;
            if (finalSession is not null)
            {
                var state = finalSession.Snapshot;
                if (state.Fault is not null && Environment.GetEnvironmentVariable("PISHARP_DEBUG") == "1") await stderr.WriteAsync("session fault: " + state.Fault + Environment.NewLine).ConfigureAwait(false);
                var bytes = backend is null ? new FileInfo(finalSession.Path).Length : backend.GetMetadata(finalSession.Path).Bytes;
                var newFault = state.Fault is not null &&
                    (operationFailure is null || !ReferenceEquals(state.Fault, operationFault));
                if (newFault || !state.IsDisposed || bytes != state.Log.CommittedByteLength || backend?.ActiveWriterCount > 0)
                    throw new SessionCommandException(SessionCommandFailure.CommandFailed);
            }
        }
        catch (Exception error) { cleanupFailures.Add(error); }
        settlement?.Complete((operationFailure is null ? cleanupFailures : cleanupFailures.Prepend(operationFailure)).ToImmutableArray());
        // rpc-mode.ts: a null command line is an unhandled TypeError; Node prints it and the process exits 1.
        if (operationFailure is RpcDispatchException { InnerException: RpcInputTypeError typeError })
        {
            await stderr.WriteAsync("TypeError: " + typeError.Message + "\n").ConfigureAwait(false);
            await stderr.FlushAsync().ConfigureAwait(false);
            return 1;
        }
        if (cleanupFailures.Count > 0)
        {
            if (Environment.GetEnvironmentVariable("PISHARP_DEBUG") == "1") await stderr.WriteAsync(string.Join(Environment.NewLine, cleanupFailures) + Environment.NewLine).ConfigureAwait(false);
            return await Fail("CleanupFailed", "RPC host cleanup failed after joining owned work; inspect durable state.", 1).ConfigureAwait(false);
        }
        if (operationFailure is null)
        {
            if (cancellationToken.IsCancellationRequested && userShutdown?.Invoke() != true)
                return await Fail("Canceled", "RPC host canceled after settling owned work; inspect durable state.", 1).ConfigureAwait(false);
            return 0;
        }
        // A user shutdown never suppresses a foreign OCE or an actual startup/run/cleanup error.
        if (operationFailure is OperationCanceledException canceled && cancellationToken.IsCancellationRequested && canceled.CancellationToken == cancellationToken)
            return await Fail("Canceled", "RPC host canceled after settling owned work; inspect durable state.", 1).ConfigureAwait(false);
        if (operationFailure is SessionRuntimeRegistryException registryError && registryError.Failure == SessionRuntimeRegistryFailure.UnknownModel)
        {
            if (profile?.IsLive == true) return await Fail("LiveSessionModelMismatch", "The selected live model does not match this session history; use a matching model or a new session.", 2).ConfigureAwait(false);
            var error = new SessionCommandException(SessionCommandFailure.OfflineProviderMismatch);
            return await Fail(error.Failure.ToString(), error.Message, 2).ConfigureAwait(false);
        }
        if (operationFailure is SessionCommandException commandError)
            return await Fail(commandError.Failure.ToString(), commandError.Message, commandError.Failure == SessionCommandFailure.CommandFailed ? 1 : 2).ConfigureAwait(false);
        if (operationFailure is LiveSessionException liveError)
            return await Fail(liveError.Code, liveError.Message, 2).ConfigureAwait(false);
        if (operationFailure is NativeExtensionException extensionError)
            return await Fail(extensionError.Failure.ToString(), extensionError.Message, extensionError.Failure == NativeExtensionFailure.CleanupFailed ? 1 : 2).ConfigureAwait(false);
        if (Environment.GetEnvironmentVariable("PISHARP_DEBUG") == "1") await stderr.WriteAsync(operationFailure + Environment.NewLine).ConfigureAwait(false);
        return await Fail("RpcHostFailed", "RPC host failed after owned cleanup; inspect durable state before retrying.", 1).ConfigureAwait(false);

        async ValueTask Cleanup(Func<ValueTask> close)
        { try { await close().ConfigureAwait(false); } catch (Exception error) { cleanupFailures.Add(error); } }
        async ValueTask RuntimeCleanup(Func<ValueTask> close)
        { try { await close().ConfigureAwait(false); } catch (Exception error) { runtimeFailures.Add(error); } }
        async Task<int> Fail(string code, string message, int result)
        {
            await stderr.WriteAsync(JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code, message,
                effectsMayHaveCompleted = true, cleanupFailureCount = cleanupFailures.Count }) + "\n").ConfigureAwait(false);
            await stderr.FlushAsync().ConfigureAwait(false); return result;
        }
    }

    /// <summary>agent-session.ts prompt: a model whose provider has no configured auth (the registry read now: auth.json, models.json and
    /// the environment, so a credential added since startup counts) refuses with the OAuth re-login text or formatNoApiKeyFoundMessage.
    /// A session without a model runs with Agent's DEFAULT_MODEL (provider "unknown"), refused as "the selected model"; the
    /// formatNoModelSelectedMessage branch is unreachable upstream (session.model is never undefined).</summary>
    internal static Func<ModelDescriptor, CancellationToken, ValueTask> PromptPreflight(LiveSessionRuntime runtime) => async (model, token) =>
    {
        var registry = await runtime.CreateModelRegistryAsync(token).ConfigureAwait(false);
        if (registry.HasConfiguredAuth(model.Provider)) return;
        throw new SessionPromptRejectedException(registry.IsUsingOAuth(model.Provider)
            ? PiSharp.Cli.Models.ModelListing.OAuthAuthenticationFailedMessage(model.Provider)
            : PiSharp.Cli.Models.ModelListing.NoApiKeyFoundMessage(model.Provider));
    };

    /// <summary>session-manager.ts SessionManager.open and _setSessionFile before a switch: a session file opens wherever it is; an
    /// empty file is initialized with a session header; a non-empty file that does not parse as a session is refused (unchanged); a
    /// missing file is a new session at that path, written once it has a conversation (the lazy store holds its header until then).
    /// The header's cwd is the host's (process.cwd()). Upstream opens the path only after the session_before_switch veto, so the
    /// returned undo puts the path back as it was (opening a session without messages also records its model and level).</summary>
    internal static async ValueTask<Func<ValueTask>?> PrepareSessionPathAsync(string path, SessionStorageBackend? backend, string cwd, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        var full = Path.GetFullPath(path);
        // The backend (lazy local, or memory under --no-session) serves a file outside its namespace as a lazy local file.
        var lazy = backend is { Mode: SessionStorageMode.LazyLocal } || backend is not null &&
            !string.Equals(Path.GetDirectoryName(full), backend.Directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        // A session of this run that is not written yet: its pending bytes come back on undo.
        if (backend is not null && !File.Exists(full) && backend.FileExists(full))
        {
            byte[] pending;
            var read = await backend.OpenReadAsync(full, token).ConfigureAwait(false);
            await using (read.ConfigureAwait(false))
            {
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy, token).ConfigureAwait(false);
                pending = copy.ToArray();
            }
            return async () =>
            {
                if (File.Exists(full)) return; // Written since: a conversation is no longer the prepared state.
                await backend.DeleteOwnedAsync(full).ConfigureAwait(false);
                await StageAsync(backend, full, pending, CancellationToken.None).ConfigureAwait(false);
            };
        }
        if (Directory.Exists(full)) return null;
        if (File.Exists(full))
        {
            var length = new FileInfo(full).Length;
            if (length > 0)
            {
                if (PiSharp.Cli.Pi.PiSessions.ReadHeader(full) is null)
                    throw new InvalidDataException($"Session file is not a valid {PiSharp.Cli.Pi.PiConfig.AppName} session: {full}");
            }
            else await File.WriteAllTextAsync(full, NewHeader() + "\n", new UTF8Encoding(false), token).ConfigureAwait(false);
            return () =>
            {
                if (File.Exists(full))
                {
                    using var stream = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > length) stream.SetLength(length);
                }
                return ValueTask.CompletedTask;
            };
        }
        if (lazy)
        {
            await StageAsync(backend!, full, Encoding.UTF8.GetBytes(NewHeader() + "\n"), token).ConfigureAwait(false);
            return async () => { if (!File.Exists(full)) await backend!.DeleteOwnedAsync(full).ConfigureAwait(false); };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, NewHeader() + "\n", new UTF8Encoding(false), token).ConfigureAwait(false);
        return () => { if (File.Exists(full)) File.Delete(full); return ValueTask.CompletedTask; };

        string NewHeader()
        {
            var (_, id, timestamp) = PiSharp.Cli.Pi.PiSessions.NewSessionFile(Path.GetDirectoryName(full)!, null, DateTimeOffset.UtcNow);
            return PiSharp.Cli.Pi.PiJson.Stringify(new System.Text.Json.Nodes.JsonObject
            { ["type"] = "session", ["version"] = PiSharp.Cli.Pi.PiSessions.CurrentSessionVersion, ["id"] = id, ["timestamp"] = timestamp, ["cwd"] = cwd });
        }
        static async ValueTask StageAsync(SessionStorageBackend backend, string path, byte[] bytes, CancellationToken token)
        {
            var storage = await backend.OpenAsync(path, true, token).ConfigureAwait(false);
            await using (storage.ConfigureAwait(false))
            {
                await storage.WriteAsync(bytes).ConfigureAwait(false);
                await storage.BeforeCheckpointAsync().ConfigureAwait(false);
            }
        }
    }

    // Startup UI settings must use the same validated workspace as the actual RPC composition.
    internal static string ResolveStartupWorkspace(string[] args) => Parse(args).Workspace;

    private static Arguments Parse(string[] args)
    {
        if (args is null || args.Length is < 8 or > 264 || args[0] != "session" || args[1] != "rpc") throw Invalid();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var tools = new ToolSelectionCliOptions();
        var skills = ImmutableArray.CreateBuilder<SkillPathSelection>();
        var reads = ImmutableArray.CreateBuilder<string>(); var writes = ImmutableArray.CreateBuilder<string>(); var root = false; var live = false;
        var bashCommands = ImmutableArray.CreateBuilder<string>();
        var extensionTools = ImmutableArray.CreateBuilder<string>();
        var deniedExtensionTools = ImmutableArray.CreateBuilder<string>();
        var extensionCommands = ImmutableArray.CreateBuilder<string>();
        var stores = ImmutableArray.CreateBuilder<SessionCatalogStore>();
        var prompts = ImmutableArray.CreateBuilder<PromptTemplatePathSelection>();
        for (var index = 2; index < args.Length; index++)
        {
            if (SkillCliConfiguration.TryConsume(args, ref index, skills)) continue;
            if (PromptTemplateCliConfiguration.TryConsume(args, ref index, prompts)) continue;
            if (ToolSelectionCliConfiguration.TryConsume(args, ref index, ref tools)) continue;
            var key = args[index];
            if (key == "--live") { if (live) throw Invalid(); live = true; continue; }
            if (key == "--root") { if (root) throw Invalid(); root = true; continue; }
            if (key is not ("--session" or "--workspace" or "--offline-script" or "--offline-api" or "--offline-images" or "--provider" or "--model" or "--models" or "--max-output-tokens" or "--leaf" or "--allow-read" or "--allow-write" or
                "--bash-executable" or "--bash-spill-root" or "--allow-bash-command" or "--bash-timeout" or "--session-store" or "--session-mode" or
                "--extension-package" or "--extension-manifest" or "--extension-approval" or "--extension-snapshot-root" or "--enable-extension-tool" or "--deny-extension-tool" or "--enable-extension-command" or
                "--user-settings" or "--project-settings" or "--steering-mode" or "--follow-up-mode" or "--thinking" or "--tool-policy") ||
                ++index >= args.Length) throw Invalid();
            var value = args[index];
            if (key == "--allow-read") reads.Add(SessionCommands.Absolute(value));
            else if (key == "--allow-write") writes.Add(SessionCommands.Absolute(value));
            else if (key == "--allow-bash-command") bashCommands.Add(value);
            else if (key == "--enable-extension-tool") extensionTools.Add(value);
            else if (key == "--deny-extension-tool") deniedExtensionTools.Add(value);
            else if (key == "--enable-extension-command") extensionCommands.Add(value);
            else if (key == "--session-store") { if (stores.Count >= 32) throw Invalid(); stores.Add(SessionCatalogCommand.ParseStore(value)); }
            else if (!options.TryAdd(key, value)) throw Invalid();
        }
        if (!options.TryGetValue("--session", out var session) || !options.TryGetValue("--workspace", out var workspace) ||
            reads.Count > 128 || writes.Count > 128) throw Invalid();
        options.TryGetValue("--offline-script", out var script);
        options.TryGetValue("--provider", out var provider); options.TryGetValue("--model", out var liveModel);
        options.TryGetValue("--max-output-tokens", out var maximumTokens);
        // Pi 1.0.0 main.ts: a provider only scopes the --model search; settings defaultModel never completes it.
        if (provider is not null && liveModel is null)
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments,
                $"--provider requires --model (for example: --provider {provider} --model <pattern>)");
        options.TryGetValue("--models", out var modelPatterns);
        if (live ? script is not null || options.ContainsKey("--offline-api") || options.ContainsKey("--offline-images") :
            script is null || provider is not null || liveModel is not null || maximumTokens is not null || modelPatterns is not null) throw Invalid();
        options.TryGetValue("--thinking", out var thinking);
        if (thinking is not null && !PiSharp.AI.ThinkingLevels.Ordered.Contains(thinking, StringComparer.Ordinal)) throw Invalid();
        // Capture preferences without settings IO. Resolve through the model registry after the one settings read.
        var liveSelection = live ? new SettingsModelSelection(provider, liveModel, maximumTokens)
            { ModelPatterns = modelPatterns is null ? null : PiSharp.Cli.Models.ModelResolver.SplitPatterns(modelPatterns), CliThinking = thinking } : null;
        options.TryGetValue("--leaf", out var leaf);
        options.TryGetValue("--session-mode", out var sessionMode); sessionMode ??= "open";
        if (sessionMode is not ("open" or "new-memory" or "new-lazy") || sessionMode != "open" && (root || leaf is not null)) throw Invalid();
        options.TryGetValue("--offline-api", out var offlineApi);
        options.TryGetValue("--bash-executable", out var bashExecutable); options.TryGetValue("--bash-spill-root", out var bashSpillRoot);
        options.TryGetValue("--bash-timeout", out var bashTimeout);
        var bash = OfflineSessionProfile.BashOptions(bashExecutable, bashSpillRoot, bashCommands.ToImmutable(), bashTimeout);
        var model = OfflineSessionProfile.SelectModel(offlineApi);
        options.TryGetValue("--offline-images", out var imageInput);
        if (imageInput is not (null or "true" or "false") || imageInput == "true" && !OfflineSessionProfile.CanConfigureImageInput(model.Api)) throw Invalid();
        options.TryGetValue("--extension-package", out var extensionPackage); options.TryGetValue("--extension-manifest", out var extensionManifest);
        options.TryGetValue("--extension-approval", out var extensionApproval); options.TryGetValue("--extension-snapshot-root", out var extensionSnapshots);
        var extension = NativeExtensionConfiguration.Optional(extensionPackage, extensionManifest, extensionApproval, extensionSnapshots, extensionTools.ToImmutable(), deniedExtensionTools.ToImmutable(), extensionCommands.ToImmutable());
        if (root && leaf is not null || leaf is { Length: 0 } || leaf?.Length > 4096 ||
            leaf is not null && (!Unicode(leaf) || leaf.Any(char.IsControl))) throw Invalid();
        try { if (stores.Count > 0) _ = new SessionCatalog(stores); } // Validate before acquiring providers/plugins.
        catch (ArgumentException) { throw Invalid(); }
        var sessionPath = SessionCommands.Absolute(session);
        if (sessionMode != "open" && stores.Any(store => !string.Equals(Path.GetFullPath(store.Directory), Path.GetDirectoryName(sessionPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) throw Invalid();
        return new(SessionCommands.Absolute(session), SessionCommands.Absolute(workspace), script is null ? null : SessionCommands.Absolute(script),
            !root && leaf is null, leaf, reads.ToImmutable(), writes.ToImmutable(), model.Api, bash, extension, imageInput == "true", stores.ToImmutable(), sessionMode, liveSelection,
            new(prompts.ToImmutable()), SettingsStartupConfiguration.FromOptions(options), thinking, tools, new(skills.ToImmutable()))
        { ToolPolicy = options.TryGetValue("--tool-policy", out var policy) ? policy is "pi" or "explicit" ? policy : throw Invalid() : null };
    }
    private static SessionCommandException Invalid() => new(SessionCommandFailure.InvalidArguments);
    private static bool Unicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }

    /// <summary>Authored offline pacing only. No gate text becomes code, authorization or an admitted command.</summary>
    private sealed class OfflineGate(string? releaseId)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        private int _faulted;
        public static OfflineGate From(ImmutableArray<JsonData> turns)
        {
            string? id = null;
            for (var index = 0; index < turns.Length; index++)
                if (turns[index].Value.TryGetProperty("rpcGate", out var spec))
                {
                    if (index != 0 || spec.ValueKind != JsonValueKind.Object || spec.EnumerateObject().Count() != 1 ||
                        !spec.TryGetProperty("releaseOnGetStateId", out var field) || field.ValueKind != JsonValueKind.String)
                        throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                    id = field.GetString();
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !Unicode(id) || id.Any(char.IsControl))
                        throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                }
            return new(id);
        }
        public async ValueTask BeforeSendAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFaulted();
            if (releaseId is not null && Interlocked.Exchange(ref _entered, 1) == 0)
            {
                // Actual HTTP work cancellation releases and joins this trusted callback before response acquisition.
                using var canceled = cancellationToken.UnsafeRegister(_ => Release(), null);
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            // A failed write/flush wakes cleanup, but cannot authorize acquiring the scripted response.
            ThrowIfFaulted();
        }
        public void Release() => _release.TrySetResult();
        public void Fault()
        {
            Interlocked.Exchange(ref _faulted, 1);
            Release();
        }
        private void ThrowIfFaulted()
        {
            if (Volatile.Read(ref _faulted) != 0) throw new SessionCommandException(SessionCommandFailure.CommandFailed);
        }
        public void Flushed(JsonData record)
        {
            var body = record.Value;
            if (releaseId is not null && body.TryGetProperty("type", out var type) && type.GetString() == "response" &&
                body.TryGetProperty("command", out var command) && command.GetString() == "get_state" &&
                body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == releaseId &&
                body.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True) Release();
        }
    }

    private abstract class BorrowedObservation(Stream inner, OfflineGate gate) : Stream
    {
        protected Stream Inner { get; } = inner;
        protected OfflineGate Gate { get; } = gate;
        public override bool CanRead => Inner.CanRead;
        public override bool CanWrite => Inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Gate.Release(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class InputObservation(Stream inner, OfflineGate gate) : BorrowedObservation(inner, gate)
    {
        // Authoritative input is read/parsed only by JsonlReader. Abort, EOF and read failure cause dispatcher
        // shutdown to cancel the real handler token; observing them here must not release work into success.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(buffer, cancellationToken);
    }

    private sealed class OutputObservation(Stream inner, OfflineGate gate, int maximumFrameBytes) : BorrowedObservation(inner, gate)
    {
        private byte[]? _written;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                if (buffer.Length > maximumFrameBytes + 1) throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
                await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                _written = buffer.ToArray();
            }
            catch { Gate.Fault(); throw; }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                var record = _written; _written = null;
                if (record is not null) Gate.Flushed(JsonData.Parse(Encoding.UTF8.GetString(record)));
            }
            catch { Gate.Fault(); throw; }
        }
    }
}
