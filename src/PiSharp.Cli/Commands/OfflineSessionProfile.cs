using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Tools;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;
using PiSharp.Cli.Extensions;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Compaction;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Settings;
using PiSharp.CodingAgent.Resources;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent.Resources.Skills;

namespace PiSharp.Cli.Commands;

internal sealed record OfflineBashAuthorization(string Executable, string SpillRoot,
    ImmutableHashSet<string> Commands, double? Timeout);

/// <summary>Literal authored wire turns through injected HTTP; only explicit final file targets are authorized.</summary>
internal sealed partial class OfflineSessionProfile : IAsyncDisposable, IRpcExtensionCommandCatalog
{
    public static ModelDescriptor Model { get; } = new("pisharp-offline-session", "openai-responses", "openai");
    public static ModelDescriptor AnthropicModel { get; } = new("pisharp-offline-session", "anthropic-messages", "anthropic");
    public static ModelDescriptor CompletionsModel { get; } = new("pisharp-offline-completions-session", "openai-completions", "openai");
    private const string InertKey = "authored-inert-offline-session-value";
    private static readonly Uri Endpoint = new("https://offline-session.invalid/v1/responses");
    private static readonly Uri AnthropicBase = new("https://offline-session.invalid");
    private static readonly Uri AnthropicEndpoint = new("https://offline-session.invalid/v1/messages?beta=true");
    private static readonly Uri CompletionsEndpoint = new("https://offline-session.invalid/v1/chat/completions");
    private readonly HttpClient? _client;
    private readonly LiveSessionConnection? _live;
    private readonly Handler _handler;
    private readonly FilePolicy _policy;
    private readonly ToolInvokerOptions _profileInvokerOptions;
    private readonly NativeExtensionActivation? _extension;
    private readonly OwnedProcessCleanup? _processCleanup;
    internal ImmutableArray<OwnedProcessCleanupReceipt> ProcessCleanupReceipts => _processCleanup?.Capture() ?? [];
    internal ImmutableArray<Exception> ProcessCleanupFailures => _processCleanup?.CaptureFailures() ?? [];
    private readonly object _disposalGate = new();
    private Task? _disposal;
    private PromptTemplateCliBinding? _promptTemplates;
    internal async Task LoadPromptTemplatesAsync(PromptTemplateCliConfiguration configuration, TextWriter diagnostics, CancellationToken token)
    {
        if (configuration.Selections.IsEmpty) return;
        RequireStartupViewMutable();
        if (_promptTemplates is not null) throw new InvalidOperationException("Prompt templates are already captured.");
        var capture = await PromptTemplateCliBinding.LoadAsync(configuration, _extension?.RawInputHandlers, _extension,
            _extension, PromptTemplateFrontendDecoder.Decode, token: token).ConfigureAwait(false);
        foreach (var diagnostic in capture.Templates.Catalog.Diagnostics)
        {
            await diagnostics.WriteLineAsync(JsonSerializer.Serialize(new { type = "prompt_template_diagnostic",
                severity = diagnostic.Type.ToString().ToLowerInvariant(), message = diagnostic.Message,
                path = diagnostic.Path, collision = diagnostic.Collision }).AsMemory(), token).ConfigureAwait(false);
        }
        await diagnostics.FlushAsync(token).ConfigureAwait(false);
        _promptTemplates = capture;
    }
    internal ReplaceableAgentSession? Sessions { get; private set; }
    internal void AttachOwner(PersistentAgentSession session, PersistentAgentSessionOptions? options = null,
        Func<long>? clock = null, Func<string>? nextId = null, IEnumerable<SessionCatalogStore>? catalogStores = null,
        PersistentSessionLifecycle? lifecycle = null)
    {
        if (mcpRuntime is not null) throw new InvalidOperationException("Admitted MCP runtime requires asynchronous attachment.");
        if (Sessions is not null) throw new InvalidOperationException("Profile already has a session owner.");
        ConfigureRetrySession(session);
        clock ??= () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        nextId ??= () => "replacement-" + Guid.NewGuid().ToString("N");
        var configured = options ?? new();
        var catalog = new SessionCatalog(catalogStores ?? [new("session-directory", Path.GetDirectoryName(session.Path)!)]);
        Sessions = (lifecycle ?? new PersistentSessionLifecycle(Registry, clock, nextId, configured, catalog: catalog)).Attach(session);
        _policy.ActiveSessionPath = () => Sessions.Current.Session.Path;
        AttachRuntimeView(Sessions);
    }
    internal async ValueTask AttachSessionAsync(PersistentAgentSession session, string reason, CancellationToken token)
    { await AttachOwnerAsync(session).ConfigureAwait(false); await StartLifecycleAsync(reason, token).ConfigureAwait(false); }
    private readonly ImmutableArray<string>? _initialActiveTools;
    private readonly ImmutableArray<string>? _deferredCatalogNames;
    internal async Task ApplyInitialToolSelectionAsync(PersistentAgentSession session, CancellationToken token)
    {
        if (mcpRuntime is not null)
        {
            var registry = session.CaptureToolCatalogRegistry();
            var selection = registry.LifetimeToolSelection ?? throw new InvalidOperationException("Admitted catalog requires an explicit lifetime selection.");
            var selectedNames = selection.SelectInitial(registry.RegisteredTools.Select(tool => new PiSharp.CodingAgent.ToolSelection.ToolSelectionDescriptor(
                tool.Adapter.Name, tool.Exposure, tool.DefaultActive, tool.IsExtension)).ToImmutableArray());
            await session.SetActiveToolsAsync(selectedNames, token).ConfigureAwait(false);
        }
        else if (_initialActiveTools is { } names) await session.SetActiveToolsAsync(names, token).ConfigureAwait(false);
        await DrainLoadoutDiagnosticsAsync(token).ConfigureAwait(false);
    }
    private readonly SessionRuntimeRegistry _startupRegistry;
    public SessionRuntimeRegistry Registry => Sessions is null ? _startupRegistry : CaptureRuntimeView().NativeRegistry;
    // Borrow the exact catalog write adapter/queue and policy; no hooks or transforms can redirect export content.
    internal ToolInvoker ExportHtmlWriter { get; }
    public ModelDescriptor SelectedModel { get; }
    internal bool IsLive => _live is not null;
    internal FrozenCatalogModel SelectedModelDefinition { get; }
    internal JsonData SelectedModelWire
    {
        get
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject();
                foreach (var property in SelectedModelDefinition.Raw.Value.EnumerateObject())
                    if (property.Name != "type") property.WriteTo(writer);
                writer.WriteEndObject();
            }
            return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
        }
    }
    // Explicit authored offline profile limit, supplied before any context-based adjustment.
    internal double OriginalDesiredMaxOutput => _live?.MaximumOutputTokens ?? 8192;
    public string Workspace { get; }
    public JsonData InitialSystem { get; private set; }
    public object[] Requests => _handler.Requests.ToArray();
    public object[] Actions => _policy.Actions.ToArray();
    public int UsedTurns => _handler.Requests.Count;
    public int ScriptTurns => _handler.Turns.Length;
    internal ISessionSummaryGenerator SummaryGenerator => new TransportSessionSummaryGenerator(SummaryTransport);
    private static readonly HttpRequestOptionsKey<SessionSummaryRequest> SummaryRequestKey = new("PiSharp.OfflineOwnedSummaryRequest");
    private static HttpRequestMessage MarkSummary(HttpRequestMessage request, SessionSummaryRequest summary)
    { request.Options.Set(SummaryRequestKey, summary); return request; }

    private IChatTransport SummaryTransport(SessionSummaryRequest summary)
    {
        if (summary.Model != SelectedModel || summary.ThinkingLevel is not null || summary.CacheRetention != "none" ||
            summary.MaximumOutputTokens is <= 0 or > 1_000_000 || summary.MaximumOutputTokens != Math.Floor(summary.MaximumOutputTokens))
            throw new SessionCompactionException(SessionCompactionFailure.InvalidSettings);
        if (_live is not null) return _live.CreateTransport((int)summary.MaximumOutputTokens, summary: true);
        if (SelectedModel.Api == "anthropic-messages")
        {
            var factory = new AnthropicMessagesKeyAuthRequestFactory(AnthropicBase, SelectedModel,
                new(MaximumTokens: (int)summary.MaximumOutputTokens, ModelReasoning: false,
                    ModelSupportsImages: SelectedModelDefinition.DeclaresImageInput, CacheRetention: AnthropicCacheRetention.None,
                    MaximumMessages: 512, MaximumEntryCharacters: 1_048_576),
                new(MaxTokens: summary.MaximumOutputTokens, SessionId: summary.SessionId, MaximumPayloadBytes: 1_048_576));
            return new AnthropicMessagesHttpSseTransport(_client!, (request, token) => MarkSummary(factory.Create(request, InertKey, token), summary),
                new(MaximumDataEvents: 256, MaximumTotalDataCharacters: 1_048_576));
        }
        if (SelectedModel.Api == "openai-completions")
        {
            var factory = new CompletionsKeyAuthRequestFactory(CompletionsEndpoint, SelectedModel,
                new(Reasoning: false, MaximumMessages: 512, MaximumEntryCharacters: 1_048_576,
                    ToolDeclarations: new(MaximumMessages: 512, MaximumEntryCharacters: 1_048_576))
                    { ModelSupportsImages = SelectedModelDefinition.DeclaresImageInput },
                new(MaxTokens: summary.MaximumOutputTokens, SupportsReasoningEffort: false, CacheRetention: CompletionsCacheRetention.None,
                    SessionId: summary.SessionId, MaximumPayloadBytes: 1_048_576));
            return new CompletionsHttpSseTransport(_client!, (request, token) => MarkSummary(factory.Create(request, InertKey, token), summary),
                new(MaximumDataEvents: 256, MaximumTotalDataCharacters: 1_048_576));
        }
        var responses = new ResponsesKeyAuthRequestFactory(Endpoint, SelectedModel,
            new(Reasoning: false, MaximumMessages: 512, MaximumEntryCharacters: 1_048_576),
            new(SupportsMaxOutputTokens: true, MaxOutputTokens: (int)summary.MaximumOutputTokens, SessionId: summary.SessionId,
                MaximumPayloadBytes: 1_048_576));
        return new ResponsesHttpSseTransport(_client!, request => MarkSummary(responses.Create(request, InertKey), summary),
            new(MaximumDataEvents: 256, MaximumTotalDataCharacters: 1_048_576));
    }

    private OfflineSessionProfile(string workspace, BuiltinToolCatalog tools, FilePolicy policy, Handler handler, ModelDescriptor model,
        BashTool? bash, NativeExtensionActivation? extension, FrozenCatalogModel modelDefinition, OwnedProcessCleanup? processCleanup, LiveSessionConnection? live = null,
        InitialToolSelection? toolSelection = null, bool deferCatalogValidation = false,
        OriginalSystemPromptAdmission? originalSystemPrompt = null)
    {
        if (live is null) RequireSelectedModelDefinition(model, modelDefinition);
        Workspace = workspace; _policy = policy; _handler = handler; SelectedModel = model; _extension = extension;
        _processCleanup = processCleanup;
        SelectedModelDefinition = modelDefinition;
        _live = live; _client = live is null ? new HttpClient(handler) : null;
        IChatTransport transport;
        if (live is not null) transport = live.CreateTransport();
        else if (model.Api == "anthropic-messages")
        {
            var factory = new AnthropicMessagesKeyAuthRequestFactory(AnthropicBase, model,
                new(MaximumTokens: 8192, ModelReasoning: false, ModelSupportsImages: modelDefinition.DeclaresImageInput, MaximumMessages: 512,
                    MaximumEntryCharacters: bash is null ? 65_536 : 1_048_576),
                new(MaximumPayloadBytes: 1_048_576));
            transport = new AnthropicMessagesHttpSseTransport(_client!, (request, token) => factory.Create(request, InertKey, token),
                new(MaximumDataEvents: 256, MaximumTotalDataCharacters: 1_048_576));
        }
        else if (model.Api == "openai-completions")
        {
            var entryCharacters = bash is null ? 65_536 : 1_048_576;
            var factory = new CompletionsKeyAuthRequestFactory(CompletionsEndpoint, model,
                new(Reasoning: false, MaximumMessages: 512, MaximumEntryCharacters: entryCharacters,
                    ToolDeclarations: new(MaximumMessages: 512, MaximumEntryCharacters: entryCharacters))
                    { ModelSupportsImages = modelDefinition.DeclaresImageInput },
                new(MaxTokens: 8192, SupportsReasoningEffort: false, MaximumPayloadBytes: 1_048_576));
            transport = new CompletionsHttpSseTransport(_client!, (request, token) => factory.Create(request, InertKey, token),
                new(MaximumDataEvents: 256, MaximumTotalDataCharacters: 1_048_576));
        }
        else
        {
            var factory = new ResponsesKeyAuthRequestFactory(Endpoint, model,
                new(Reasoning: false, MaximumMessages: 512, MaximumEntryCharacters: bash is null ? 65_536 : 1_048_576),
                new(MaximumPayloadBytes: 1_048_576));
            transport = new ResponsesHttpSseTransport(_client!, request => factory.Create(request, InertKey),
                new(MaximumDataEvents: 256, MaximumTotalDataCharacters: 1_048_576));
        }
        // Preserve the explicit profile defaults; durable activation can now select edit alongside ls.
        var defaults = bash is null ? tools.Select(["read", "write"]) : tools.Select(["read", "write", "bash"]);
        var registrations = defaults.Select(tool => new SessionRegisteredTool(tool.Declaration, tool.Adapter)).ToImmutableArray();
        registrations = registrations.AddRange(tools.Registered.Where(tool => !defaults.Any(active => active.Name == tool.Name))
            .Select(tool => new SessionRegisteredTool(tool.Declaration, tool.Adapter) { DefaultActive = false }));
        var invokerOptions = bash is null
            ? new ToolInvokerOptions(MaximumArgumentCharacters: 65_536, MaximumActionCharacters: 131_072, MaximumResultCharacters: 131_072)
            : new ToolInvokerOptions(MaximumArgumentCharacters: 96_000, MaximumActionCharacters: 192_000,
                MaximumResultCharacters: 512 * 1024, MaximumActionEntries: 1026) { MaximumStructuredContentCharacters = 8 * 1024 * 1024 };
        _profileInvokerOptions = invokerOptions;
        if (extension is not null)
        {
            policy.ExtensionTargets = extension.Targets;
            extension.Bind(policy, invokerOptions);
            registrations = registrations.AddRange(extension.EnabledAdapters.Select((adapter, index) =>
                new SessionRegisteredTool(extension.EnabledDeclarations[index], adapter, ToolExecutionMode.Sequential)
                { IsExtension = true, Exposure = extension.EnabledRegistrations[index].Exposure, Namespace = extension.EnabledRegistrations[index].Namespace,
                    DefaultActive = extension.EnabledRegistrations[index].DefaultActive,
                    PrepareLoadout = extension.Binding.GetLoadoutPreparation(adapter.Name) }));
        }
        if (toolSelection is not null)
        {
            if (deferCatalogValidation && !toolSelection.UseAvailableDefaults) _deferredCatalogNames = toolSelection.Names;
            var selected = ImmutableArray.CreateBuilder<string>();
            foreach (var name in toolSelection.Names)
            {
                if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
                { _client?.Dispose(); throw new SessionCommandException(SessionCommandFailure.InvalidArguments); }
                if (deferCatalogValidation && !registrations.Any(value => value.Adapter.Name == name)) continue;
                if (toolSelection.UseAvailableDefaults && !registrations.Any(value => value.Adapter.Name == name)) continue;
                if (toolSelection.LifetimePolicy is not null && registrations.Any(value => value.Adapter.Name == name &&
                    value.Exposure is not (ToolExposure.Direct or ToolExposure.ModelOnly))) continue;
                if (name.Length is < 1 or > 64 || !registrations.Any(value => value.Adapter.Name == name &&
                    value.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly))
                {
                    _client?.Dispose();
                    throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
                }
                if (!selected.Contains(name)) selected.Add(name);
            }
            if (toolSelection.IncludeDefaultExtensions && extension is not null)
                foreach (var registration in registrations.Where(value => (toolSelection.LifetimePolicy?.IsAllowed(value.Adapter.Name) ?? true) &&
                    extension.EnabledAdapters.Any(adapter => adapter.Name == value.Adapter.Name) &&
                    ToolExposureSemantics.ActivatesOnRegistration(value.Exposure, value.DefaultActive)))
                    if (!selected.Contains(registration.Adapter.Name)) selected.Add(registration.Adapter.Name);
            _initialActiveTools = selected.ToImmutable();
        }
        ExportHtmlWriter = new([tools.Select(["write"])[0].Adapter], policy, options: invokerOptions);
        var lifetimeSelection = toolSelection?.LifetimePolicy;
        if (deferCatalogValidation && toolSelection is not null && lifetimeSelection is null)
            lifetimeSelection = PiSharp.CodingAgent.ToolSelection.AllowedToolSelection.Create(configuredDefaults: toolSelection.Names);
        var literalSystem = live is not null ? "You are a coding assistant. Use the registered tools only on explicitly authorized targets." :
            bash is null ? "Explicit offline session file tools." : "Explicit offline session file and authorized Bash tools.";
        var initialTools = _initialActiveTools ?? registrations.Where(value => ToolExposureSemantics.ActivatesOnRegistration(value.Exposure, value.DefaultActive))
            .Select(value => value.Adapter.Name).ToImmutableArray();
        startupOriginalPrompt = OriginalSystemPromptBuilder.Capture(originalSystemPrompt ?? new() { CustomPrompt = literalSystem },
            workspace, initialTools, literal: originalSystemPrompt is null);
        _startupRegistry = new([DecorateOriginalPromptBinding(new(model, transport, ExecutionMode: ToolExecutionMode.Sequential, Hooks: extension?.Binding.ContextHooks))], registrations, policy,
            new SessionRuntimeRegistryOptions(ToolInvokerOptions: invokerOptions) { PreparedToolHooks = extension?.Binding.PreparedHooks,
                LifetimeToolSelection = lifetimeSelection, InitialActiveToolNames = _initialActiveTools,
                BindNestedCallsToSessionOwner = true, ReportLoadoutDiagnostic = extension is null ? null : extension.CaptureLoadoutDiagnostic,
                DrainLoadoutDiagnostics = extension is null ? null : extension.DrainLoadoutDiagnosticsAsync,
                PreparePromptSections = PrepareDurablePromptSections });
        // Publish installed metadata into the exact native baseline before any session/model resolution.
        // MCP runtime clones retain this catalog holder through the actual WithToolCatalog pipeline.
        extension?.RegistrationInstallation?.ConfigureModelCatalog(_startupRegistry, _startupRegistry.CaptureModelCatalog().Bindings, DecorateOriginalPromptBinding);
        InitialSystem = JsonData.Parse(JsonSerializer.Serialize(new { role = "system",
            content = literalSystem,
            timestamp = 0, toolsAdded = (_initialActiveTools is { } active ? active.Select(name => registrations.Single(value => value.Adapter.Name == name)) :
                registrations.Where(value => ToolExposureSemantics.ActivatesOnRegistration(value.Exposure,
                value.DefaultActive))).Select(value => value.Declaration.Value).ToArray(), offlineApi = live is null ? model.Api : null }));
        if (originalSystemPrompt is not null)
        {
            var built = System.Text.Json.Nodes.JsonNode.Parse(OriginalSystemPromptBuilder.Message(startupOriginalPrompt, 0, allowForce: false).ToString())!.AsObject();
            foreach (var property in InitialSystem.Value.EnumerateObject())
                if (property.Name is "toolsAdded" or "offlineApi") built[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            InitialSystem = JsonData.Parse(built.ToJsonString());
        }
    }

    internal static ModelDescriptor SelectModel(string? offlineApi) => offlineApi switch
    {
        null or "openai-responses" => Model,
        "anthropic-messages" => AnthropicModel,
        "openai-completions" => CompletionsModel,
        _ => throw new SessionCommandException(SessionCommandFailure.InvalidArguments)
    };

    internal static bool CanConfigureImageInput(string api) => api is "anthropic-messages" or "openai-completions";

    internal static void RequireSelectedModelDefinition(ModelDescriptor expected, FrozenCatalogModel selected)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(selected);
        if (selected.Type != CatalogModelType.Chat || selected.Provider != expected.Provider || selected.Id != expected.Id ||
            selected.DeclaredApi != expected.Api || selected.DeclaresImageInput && !CanConfigureImageInput(expected.Api))
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
    }

    private static FrozenCatalogModel AuthoredModelDefinition(ModelDescriptor model, bool supportsImages)
    {
        if (supportsImages && !CanConfigureImageInput(model.Api))
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        // These identities are explicitly authored offline fixtures, not entries in the released live catalog.
        var raw = new
        {
            type = "chat", id = model.Id, api = model.Api, provider = model.Provider,
            name = "PiSharp authored offline session model",
            baseUrl = model.Api == "anthropic-messages" ? "https://offline-session.invalid" : "https://offline-session.invalid/v1",
            reasoning = false, input = supportsImages ? new[] { "text", "image" } : ["text"], contextWindow = 131_072, maxTokens = 8192,
            cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
            provenance = "authored-offline-profile", liveModelCapabilityClaimed = false
        };
        var catalog = FrozenModelCatalog.ReadProviderJson(model.Provider, JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object> { [model.Api] = new Dictionary<string, object> { ["chat:" + model.Id] = raw } }),
            new(MaximumUtf8Bytes: 4096, MaximumModels: 1, MaximumProperties: 32, MaximumStringCharacters: 128));
        if (!catalog.TryGetModel(CatalogModelType.Chat, model.Id, out var selected))
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        RequireSelectedModelDefinition(model, selected); return selected;
    }

    internal static OfflineBashAuthorization? BashOptions(string? executable, string? spillRoot,
        ImmutableArray<string> commands, string? timeout)
    {
        if (executable is null && spillRoot is null && commands.IsEmpty && timeout is null) return null;
        if (!OperatingSystem.IsWindows()) throw new SessionCommandException(SessionCommandFailure.UnsupportedBashPlatform);
        if (executable is null || spillRoot is null || commands.Length is < 1 or > 16)
            throw new SessionCommandException(SessionCommandFailure.InvalidBashConfiguration);
        var exact = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal); long characters = 0;
        foreach (var command in commands)
        {
            if (command.Length is < 1 or > 12_000 || command.Contains('\0') || !Scalar(command) ||
                (characters += command.Length) > 32_768 || !exact.Add(command))
                throw new SessionCommandException(SessionCommandFailure.InvalidBashConfiguration);
        }
        double? seconds = null;
        if (timeout is not null)
        {
            if (timeout.Length > 128 || !double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) || value <= 0 || value * 1000 > int.MaxValue)
                throw new SessionCommandException(SessionCommandFailure.InvalidBashConfiguration);
            seconds = value;
        }
        return new(SessionCommands.Absolute(executable), SessionCommands.Absolute(spillRoot), exact.ToImmutable(), seconds);
    }

    public static async Task<OfflineSessionProfile> CreateAsync(string workspace, string sessionPath, string? scriptPath,
        ImmutableArray<JsonData> turns, ImmutableArray<string> readTargets, ImmutableArray<string> writeTargets, CancellationToken token,
        Func<CancellationToken, ValueTask>? beforeSendAsync = null, string? offlineApi = null, OfflineBashAuthorization? bash = null,
        NativeExtensionConfiguration? extension = null, IExtensionUiProvider? extensionUi = null,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportInputDiagnostic = null,
        bool modelSupportsImages = false, LiveSessionSelection? liveSelection = null, LiveSessionRuntime? liveRuntime = null,
        InitialToolSelection? toolSelection = null, OfflineGrepHost? grepHost = null, PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission = null,
        AuthenticationResolution? resolvedAnthropicAuthentication = null, HttpMessageHandler? resolvedAnthropicHandler = null,
        PiSharp.Cli.Mcp.McpRegisteredProfileAdmission? registeredMcpAdmission = null,
        NativeExtensionInitializerInstallation? configuredInitializerInstallation = null,
        PiSharp.Cli.Mcp.McpApplicationInitializerAdmission? applicationMcpHost = null,
        Func<ExtensionRegistry, PiSharp.Cli.Extensions.Execution.NativeExtensionExecInstallation>? configuredExecInstallation = null,
        OriginalSystemPromptAdmission? originalSystemPrompt = null)
    {
        if (configuredExecInstallation is not null && (extension is null || configuredExecInstallation.GetInvocationList().Length != 1))
            throw new ArgumentException("One native extension and one explicit execution installation factory required.");
        Func<PiSharp.Cli.Mcp.McpApplicationHostInstallation>? readApplicationHost = null;
        if (applicationMcpHost is not null)
        {
            if (extension is null || configuredInitializerInstallation is null || mcpAdmission is not null || registeredMcpAdmission is not null)
                throw new ArgumentException("Application MCP installation requires one native initializer and no competing MCP profile admission.");
            configuredInitializerInstallation = applicationMcpHost.Bind(configuredInitializerInstallation, out var readInstalled);
            readApplicationHost = readInstalled;
        }
        if (configuredInitializerInstallation is not null)
        {
            if (extension is null) throw new ArgumentException("An admitted initializer installation requires a native extension configuration.");
            configuredInitializerInstallation.Validate();
            token.ThrowIfCancellationRequested();
        }
        if (registeredMcpAdmission is not null && mcpAdmission is not null)
            throw new ArgumentException("Choose one explicit MCP profile admission.");
        // Explicit admitted resolution uses no environment lookup; reject invalid composition before profile effects.
        if (resolvedAnthropicAuthentication is not null &&
            (liveSelection is null || liveSelection.Model.Provider != "anthropic" || liveSelection.Model.Api != "anthropic-messages" ||
             resolvedAnthropicAuthentication.Diagnostic != AuthenticationDiagnostic.Resolved || resolvedAnthropicAuthentication.Authentication is null))
            throw new ArgumentException("Resolved Anthropic authentication requires an admitted Anthropic live selection.");
        if (resolvedAnthropicHandler is not null && resolvedAnthropicAuthentication is null)
            throw new ArgumentException("A resolved Anthropic handler requires explicit admitted authentication.");
        if (resolvedAnthropicAuthentication is not null) token.ThrowIfCancellationRequested();
        if (grepHost is not null)
        {
            ArgumentNullException.ThrowIfNull(grepHost.Executor);
            ArgumentNullException.ThrowIfNull(grepHost.ContextOperations);
            ArgumentNullException.ThrowIfNull(grepHost.SearchAdmission);
            ArgumentNullException.ThrowIfNull(grepHost.ContextAdmission);
        }
        var model = liveSelection?.Model ?? SelectModel(offlineApi);
        var modelDefinition = liveSelection?.Definition ?? AuthoredModelDefinition(model, modelSupportsImages);
        // Reject recognized wire events from the other family before durable session admission.
        foreach (var turn in turns)
            foreach (var observation in turn.Value.GetProperty("events").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (model.Api == "openai-completions")
                {
                    // Literal chunk objects, not normalized events or a manufactured terminal frame.
                    // All shared script ownership/Unicode/depth limits have already been checked.
                    if (observation.TryGetProperty("type", out _) ||
                        !observation.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                        observation.TryGetProperty("object", out var kind) &&
                            (kind.ValueKind != JsonValueKind.String || kind.GetString() != "chat.completion.chunk"))
                        throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                    // The inherited 65,536-character SSE line bound includes the literal "data: " prefix.
                    if (observation.GetRawText().Length > 65_530)
                        throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
                    FiniteChunk(observation, token);
                    continue;
                }
                if (!observation.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                    type.GetString() is not { Length: > 0 } name || name.Any(char.IsControl) || name.Length > 128 ||
                    model.Api == "anthropic-messages" && name.StartsWith("response.", StringComparison.Ordinal) ||
                    model.Api == "openai-responses" && name is ("message_start" or "message_delta" or "message_stop" or
                        "content_block_start" or "content_block_delta" or "content_block_stop"))
                    throw new SessionCommandException(SessionCommandFailure.InvalidScript);
            }
        var files = new LocalFileOperations();
        var canonicalWorkspace = SessionCommands.Absolute(await files.CanonicalizeAsync(workspace, token));
        if (!Directory.Exists(canonicalWorkspace)) throw new SessionCommandException(SessionCommandFailure.WorkspaceMissing);
        if (originalSystemPrompt is not null)
            originalSystemPrompt = OriginalSystemPromptBuilder.Capture(originalSystemPrompt, canonicalWorkspace,
                originalSystemPrompt.SelectedTools.IsDefault ? [] : originalSystemPrompt.SelectedTools).Input;
        var reserved = new HashSet<string>(FilePolicy.Comparer)
        { SessionCommands.Absolute(await files.CanonicalizeAsync(sessionPath, token)) };
        if (scriptPath is not null) reserved.Add(SessionCommands.Absolute(await files.CanonicalizeAsync(scriptPath, token)));
        NativeExtensionPreflight? extensionPreflight = null;
        if (extension is not null)
        {
            extensionPreflight = await extension.PreflightAsync(SessionCommands.Absolute(sessionPath), canonicalWorkspace, token).ConfigureAwait(false);
            reserved.Add(extension.ManifestPath); reserved.Add(extension.ApprovalPath);
        }
        var reads = await Targets(readTargets); var writes = await Targets(writeTargets);
        BashTool? bashTool = null; BashGrant? grant = null;
        OwnedProcessCleanup? processCleanup = null;
        if (bash is not null)
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) throw new SessionCommandException(SessionCommandFailure.UnsupportedBashPlatform);
            var executable = SessionCommands.Absolute(await files.CanonicalizeAsync(bash.Executable, token));
            var spillRoot = SessionCommands.Absolute(await files.CanonicalizeAsync(bash.SpillRoot, token));
            if (!File.Exists(executable) || reserved.Contains(executable) || !Directory.Exists(spillRoot) ||
                !(FilePolicy.Comparer.Equals(spillRoot, canonicalWorkspace) || FilePolicy.Within(canonicalWorkspace, spillRoot)) ||
                reserved.Contains(spillRoot))
                throw new SessionCommandException(SessionCommandFailure.InvalidBashConfiguration);
            var windows = SessionCommands.Absolute(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!Directory.Exists(windows)) throw new SessionCommandException(SessionCommandFailure.InvalidBashConfiguration);
            var spillDirectory = Path.Combine(spillRoot, "pisharp-bash-" + Guid.NewGuid().ToString("N"));
            if (File.Exists(spillDirectory) || Directory.Exists(spillDirectory))
                throw new SessionCommandException(SessionCommandFailure.InvalidBashConfiguration);
            Directory.CreateDirectory(spillDirectory);
            var canonicalSpill = SessionCommands.Absolute(await files.CanonicalizeAsync(spillDirectory, token));
            if (!FilePolicy.Comparer.Equals(spillDirectory, canonicalSpill))
                throw new SessionCommandException(SessionCommandFailure.InvalidBashConfiguration);
            var environment = ImmutableDictionary<string, string>.Empty.Add("SystemRoot", windows)
                .Add("TEMP", canonicalSpill).Add("TMP", canonicalSpill).Add("LANG", "C.UTF-8").Add("LC_ALL", "C.UTF-8");
            grant = new(executable, canonicalWorkspace, canonicalSpill, environment, bash.Commands, bash.Timeout, files);
            processCleanup = new(new NativeProcessRunner());
            bashTool = new(processCleanup, new(executable, canonicalWorkspace, environment, canonicalSpill));
        }
        var policy = new FilePolicy(canonicalWorkspace, reads, writes, reserved, grant, grepHost);
        var grepReader = grepHost is null ? null : new AdmittedGrepContextReader(canonicalWorkspace,
            grepHost.ContextOperations, policy.AuthorizeGrepContextAsync);
        NativeExtensionActivation? activation = null;
        LiveSessionConnection? connection = null;
        Task<LiveSessionConnection>? connectionOriginal = null;
        try
        {
            if (extensionPreflight is not null) activation = await NativeExtensionActivation.LoadAsync(extensionPreflight, token, extensionUi, reportInputDiagnostic,
                configuredInitializerInstallation: configuredInitializerInstallation, configuredExecInstallation: configuredExecInstallation).ConfigureAwait(false);
            if (resolvedAnthropicAuthentication is null) connection = liveSelection?.Connect(liveRuntime);
            else
            {
                connectionOriginal = (liveSelection ?? throw new InvalidOperationException("Validated Anthropic selection is absent."))
                    .ConnectResolvedAnthropicAsync(resolvedAnthropicAuthentication, resolvedAnthropicHandler, token).AsTask();
                connection = await connectionOriginal.ConfigureAwait(false);
            }
            var profile = new OfflineSessionProfile(canonicalWorkspace, new BuiltinToolCatalog(canonicalWorkspace, canonicalWorkspace, files,
                readWriteOptions: new(MaximumReadBytes: 65_536, MaximumWriteBytes: 65_536, MaximumArgumentCharacters: 65_536),
                editOptions: new(MaximumInputBytes: 65_536, MaximumOutputBytes: 65_536, MaximumArgumentCharacters: 65_536,
                    DiffOptions: new(MaximumOutputCharacters: 4096)), bash: bashTool,
                grep: grepHost?.Executor, grepContextReader: grepReader), policy,
                new Handler(turns, beforeSendAsync, model), model, bashTool, activation, modelDefinition, processCleanup, connection, toolSelection,
                deferCatalogValidation: mcpAdmission is not null || registeredMcpAdmission is not null || readApplicationHost is not null,
                originalSystemPrompt: originalSystemPrompt);
            if (readApplicationHost is not null) profile.ConfigureMcpRegistrationRuntime(readApplicationHost().CreateRegisteredAdmission());
            else if (registeredMcpAdmission is not null) profile.ConfigureMcpRegistrationRuntime(registeredMcpAdmission);
            else if (mcpAdmission is not null) profile.ConfigureMcpRuntime(mcpAdmission);
            return profile;
        }
        catch (Exception original)
        {
            var cleanupFailures = new List<Exception>();
            if (connectionOriginal?.Exception is { } connectionFaults) cleanupFailures.Add(connectionFaults);
            Task? connectionCleanup = null;
            try { if (connection is not null) { connectionCleanup = connection.DisposeAsync().AsTask(); await connectionCleanup.ConfigureAwait(false); } }
            catch (Exception cleanup) { cleanupFailures.Add((Exception?)connectionCleanup?.Exception ?? cleanup); }
            if (activation is not null)
                try { await activation.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
            if (cleanupFailures.Count > 0) throw new AggregateException("Profile admission and owned cleanup failed.", new[] { original }.Concat(cleanupFailures));
            throw;
        }

        async Task<HashSet<string>> Targets(ImmutableArray<string> paths)
        {
            var result = new HashSet<string>(FilePolicy.Comparer);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var canonical = SessionCommands.Absolute(await files.CanonicalizeAsync(path, token));
                if (!FilePolicy.Within(canonicalWorkspace, canonical) || reserved.Contains(canonical) ||
                    extension is not null && (FilePolicy.Comparer.Equals(canonical, extension.Package) || FilePolicy.Within(extension.Package, canonical) ||
                        FilePolicy.Comparer.Equals(canonical, extension.SnapshotRoot) || FilePolicy.Within(extension.SnapshotRoot, canonical)))
                    throw new SessionCommandException(SessionCommandFailure.ReservedTarget);
                result.Add(canonical);
            }
            return result;
        }
    }

    public ValueTask DisposeAsync()
    {
        RefuseLifecycleDrainClose();
        _live?.RefuseResolvedCleanupSelfWait();
        RefuseViewCleanupSelfWait();
        // Refuse before installing a settlement or closing Sessions: a reporter must never join its own close.
        RefuseViewReporterDisposal();
        TaskCompletionSource? settlement = null; Task pending;
        lock (lifecycleGate)
        {
            if (lifecycleDraining) throw new InvalidOperationException("Profile close cannot join an active lifecycle drain.");
            lock (_disposalGate)
            {
                if (_disposal is null) { settlement = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = settlement.Task; }
                pending = _disposal;
            }
            lifecycleClosing = true;
        }
        if (settlement is not null) _ = CloseAsync(settlement);
        return new(pending);
    }
    private async Task CloseAsync(TaskCompletionSource settlement)
    {
        var failures = new List<Exception>();
        try { await CloseLifecycleHandoffsAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (Sessions is not null)
            try { await Sessions.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        Task? liveCleanup = null;
        try { if (_live is not null) { liveCleanup = _live.DisposeAsync().AsTask(); await liveCleanup.ConfigureAwait(false); } }
        catch (Exception error) { failures.Add((Exception?)liveCleanup?.Exception ?? error); }
        try { _client?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { await CloseProfileViewsAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) settlement.TrySetException(new NativeExtensionException(NativeExtensionFailure.CleanupFailed, new AggregateException(failures)));
        else settlement.TrySetResult();
    }

    private static void FiniteChunk(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) FiniteChunk(property.Value, token);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) FiniteChunk(item, token);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new SessionCommandException(SessionCommandFailure.InvalidScript);
    }

    private sealed class FilePolicy(string workspace, HashSet<string> reads, HashSet<string> writes,
        HashSet<string> reserved, BashGrant? bash, OfflineGrepHost? grepHost) : IToolActionPolicy
    {
        public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public readonly List<object> Actions = [];
        public ImmutableDictionary<string, string> ExtensionTargets { get; set; } = ImmutableDictionary<string, string>.Empty;
        public Func<string?>? ActiveSessionPath { get; set; }
        public static bool Within(string root, string target) =>
            target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, Comparison);
        private bool IsReserved(string target)
        {
            var activePath = ActiveSessionPath?.Invoke();
            return reserved.Contains(target) || activePath is not null && Comparer.Equals(SessionCommands.Absolute(activePath), target);
        }
        public async ValueTask<bool> AuthorizeGrepContextAsync(string path, int maximumBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var allowed = grepHost is not null && Within(workspace, path) && reads.Contains(path) && !IsReserved(path);
            if (allowed) allowed = await grepHost!.ContextAdmission(path, maximumBytes, token).ConfigureAwait(false);
            // An awaited grant may span a session transition; recheck its reserved target before returning.
            token.ThrowIfCancellationRequested();
            allowed = allowed && !IsReserved(path);
            Actions.Add(new { ToolName = "grep", Operation = "context_read", Target = path, allowed });
            return allowed;
        }
        public async ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (action.Kind == PreparedToolActionKind.Extension)
            {
                var granted = ExtensionTargets.TryGetValue(action.ToolName, out var exactTarget) && action.Target == exactTarget &&
                    action.Operation == "invoke" && action.WorkingDirectory is null && !action.CommandArguments.IsDefault &&
                    action.CommandArguments.IsEmpty && action.Environment.IsEmpty && action.Arguments.Value.ValueKind == JsonValueKind.Object;
                Actions.Add(new { action.ToolName, action.Operation, action.Target, allowed = granted });
                return new(granted);
            }
            if (action.ToolName == "bash")
            {
                var granted = bash is not null && await bash.AuthorizeAsync(action, token).ConfigureAwait(false);
                Actions.Add(new { action.ToolName, action.Operation, action.Target, allowed = granted,
                    action.CommandArguments, action.WorkingDirectory, action.Environment, arguments = action.Arguments.Value });
                return new(granted);
            }
            var isReserved = IsReserved(action.Target);
            if (action.ToolName == "grep")
            {
                var admissible = grepHost is not null && action.Operation == "grep" && action.Kind == PreparedToolActionKind.Path &&
                    action.WorkingDirectory == workspace && !action.CommandArguments.IsDefault && action.CommandArguments.IsEmpty &&
                    action.Environment.IsEmpty && (Comparer.Equals(workspace, action.Target) || Within(workspace, action.Target)) && !isReserved;
                var authorization = admissible
                    ? await grepHost!.SearchAdmission.AuthorizeAsync(invocation, action, token).ConfigureAwait(false)
                    : new ToolActionAuthorization(false);
                token.ThrowIfCancellationRequested();
                if (IsReserved(action.Target)) authorization = new(false, authorization.Terminate);
                Actions.Add(new { action.ToolName, action.Operation, action.Target, allowed = authorization.Allow });
                return authorization;
            }
            var allow = action.Kind == PreparedToolActionKind.Path && action.WorkingDirectory == workspace &&
                action.CommandArguments.IsEmpty && action.Environment.Count == 0 && Within(workspace, action.Target) &&
                !isReserved && (action.ToolName == "read" && action.Operation == "read" && reads.Contains(action.Target) ||
                    action.ToolName == "ls" && action.Operation == "ls" && reads.Contains(action.Target) ||
                    action.ToolName == "edit" && action.Operation == "edit" && reads.Contains(action.Target) && writes.Contains(action.Target) ||
                    action.ToolName == "write" && action.Operation == "write" && writes.Contains(action.Target));
            Actions.Add(new { action.ToolName, action.Operation, action.Target, allowed = allow });
            return new(allow);
        }
    }

    private sealed record BashGrant(string Executable, string Workspace, string SpillDirectory,
        ImmutableDictionary<string, string> Environment, ImmutableHashSet<string> Commands, double? Timeout, IFileOperations Files)
    {
        public async ValueTask<bool> AuthorizeAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (action.ToolName != "bash" || action.Operation != "bash" || action.Kind != PreparedToolActionKind.Command ||
                    action.Target != Executable || action.WorkingDirectory != Workspace || action.CommandArguments.IsDefault ||
                    action.CommandArguments.Length != 2 || action.CommandArguments[0] != "-c" || action.Environment.Count != Environment.Count ||
                    !Environment.All(pair => action.Environment.TryGetValue(pair.Key, out var value) && value == pair.Value) ||
                    action.Arguments.ToString().Length > 96_000) return false;
                var raw = JsonData.Parse(action.Arguments.ToString()).Value;
                if (raw.ValueKind != JsonValueKind.Object || raw.EnumerateObject().Any(pair => pair.Name is not ("command" or "timeout" or "outputPath")) ||
                    !raw.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String ||
                    command.GetString() is not { } text || !Commands.Contains(text) || action.CommandArguments[1] != text ||
                    !raw.TryGetProperty("outputPath", out var path) || path.ValueKind != JsonValueKind.String || path.GetString() is not { } output ||
                    SessionCommands.Absolute(output) != output || Path.GetDirectoryName(output) != SpillDirectory ||
                    !ArtifactName(Path.GetFileName(output)) || File.Exists(output) || Directory.Exists(output)) return false;
                if (Timeout is { } seconds)
                {
                    if (!raw.TryGetProperty("timeout", out var limit) || limit.ValueKind != JsonValueKind.Number ||
                        !limit.TryGetDouble(out var value) || value != seconds) return false;
                }
                else if (raw.TryGetProperty("timeout", out _)) return false;
                if (!File.Exists(Executable) || !Directory.Exists(Workspace) || !Directory.Exists(SpillDirectory) ||
                    !FilePolicy.Comparer.Equals(Executable, SessionCommands.Absolute(await Files.CanonicalizeAsync(Executable, token))) ||
                    !FilePolicy.Comparer.Equals(Workspace, SessionCommands.Absolute(await Files.CanonicalizeAsync(Workspace, token))) ||
                    !FilePolicy.Comparer.Equals(SpillDirectory, SessionCommands.Absolute(await Files.CanonicalizeAsync(SpillDirectory, token)))) return false;
                token.ThrowIfCancellationRequested(); return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { return false; }
        }
        private static bool ArtifactName(string name) => name.Length == 44 && name.StartsWith("pi-bash-", StringComparison.Ordinal) &&
            name.EndsWith(".log", StringComparison.Ordinal) && name.AsSpan(8, 32).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;
    }
    private static bool Scalar(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }

    private sealed class Handler(ImmutableArray<JsonData> turns, Func<CancellationToken, ValueTask>? beforeSendAsync,
        ModelDescriptor model) : HttpMessageHandler
    {
        public ImmutableArray<JsonData> Turns { get; } = turns;
        public readonly List<object> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var anthropic = model.Api == "anthropic-messages";
            var completions = model.Api == "openai-completions";
            var endpoint = anthropic ? AnthropicEndpoint : completions ? CompletionsEndpoint : Endpoint;
            if (request.Method != HttpMethod.Post || request.RequestUri != endpoint || request.Content is null || Requests.Count >= Turns.Length ||
                request.Content.Headers.ContentType?.ToString() != (anthropic || completions ? "application/json" : "application/json; charset=utf-8") ||
                (anthropic ? request.Headers.Contains("Authorization") || !Header(request, "x-api-key", InertKey) ||
                    !Header(request, "anthropic-version", "2023-06-01") : request.Headers.Contains("x-api-key") ||
                    request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization?.Parameter != InertKey))
                throw new SessionCommandException(SessionCommandFailure.InvalidScript);
            var bytes = await request.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (bytes.Length > 1_048_576) throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
            using var body = JsonDocument.Parse(bytes);
            var root = body.RootElement;
            var input = root.GetProperty(anthropic || completions ? "messages" : "input");
            request.Options.TryGetValue(SummaryRequestKey, out var summary);
            var outputBudget = summary is null ? 8192 : (int)summary.MaximumOutputTokens;
            if (summary is not null && (summary.Model != model || summary.CacheRetention != "none" ||
                summary.ThinkingLevel is not null || outputBudget <= 0 || outputBudget > 1_000_000 ||
                root.TryGetProperty("tools", out _) || !anthropic && !completions &&
                (!root.TryGetProperty("max_output_tokens", out var maximum) || maximum.GetInt32() != outputBudget)))
                throw new SessionCommandException(SessionCommandFailure.InvalidScript);
            if (input.ValueKind != JsonValueKind.Array || root.GetProperty("model").GetString() != model.Id || !root.GetProperty("stream").GetBoolean() ||
                anthropic && (root.TryGetProperty("input", out _) || root.TryGetProperty("betas", out _) || root.GetProperty("max_tokens").GetInt32() != outputBudget) ||
                completions && (root.TryGetProperty("input", out _) || root.GetProperty("max_completion_tokens").GetInt32() != outputBudget ||
                    root.GetProperty("store").GetBoolean() || !root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean()) ||
                !anthropic && !completions && root.TryGetProperty("messages", out _))
                throw new SessionCommandException(SessionCommandFailure.InvalidScript);
            var turn = Turns[Requests.Count].Value;
            if (turn.TryGetProperty("expectedRequest", out var expectedBody) && !Same(expectedBody, root))
                throw new SessionCommandException(SessionCommandFailure.InvalidScript);
            var requirements = turn.TryGetProperty("requiredInputTexts", out var required) ? required.EnumerateArray().Select(value => value.GetString()!).ToArray() : [];
            var strings = Strings(input).ToArray();
            if (requirements.Any(expectedText => !strings.Any(actual => actual.Contains(expectedText, StringComparison.Ordinal))))
                throw new SessionCommandException(SessionCommandFailure.InvalidScript);
            // Completions publishes Start only after actual headers/OnResponse. Holding headers
            // would prevent an RPC client waiting for Start from sending its gate-release command.
            // Other families retain their historical send gate; Completions holds the first body read.
            if (!completions && beforeSendAsync is not null) await beforeSendAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Requests.Add(new { sequence = Requests.Count + 1, model = model.Id, api = model.Api, provider = model.Provider,
                requestUri = endpoint.AbsoluteUri, authHeader = anthropic ? "x-api-key" : "authorization",
                authoredInertAuthValidated = true, contentType = request.Content.Headers.ContentType?.ToString(), inputItems = input.GetArrayLength(),
                toolNames = root.TryGetProperty("tools", out var toolArray) ? toolArray.EnumerateArray()
                    .Select(tool => (completions ? tool.GetProperty("function") : tool).GetProperty("name").GetString()).ToArray() : [],
                requiredHistoryChecks = requirements.Length, historyRequirementsSatisfied = true,
                inputSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.GetRawText()))) });
            var wire = new StringBuilder();
            foreach (var observation in turn.GetProperty("events").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (anthropic) wire.Append("event: ").Append(observation.GetProperty("type").GetString()).Append('\n');
                wire.Append("data: ").Append(observation.GetRawText()).Append("\n\n");
            }
            var literal = Encoding.UTF8.GetBytes(wire.ToString());
            HttpContent content;
            if (completions && beforeSendAsync is not null)
            {
                // The admitted source script is at most 1 MiB and 256 events per turn;
                // the only extra wire bytes are fixed data/blank-line framing, not another queue.
                if (literal.Length > 1_048_576 + 256 * 8)
                    throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
                content = new StreamContent(new FirstReadGatedBody(literal, beforeSendAsync));
                content.Headers.ContentLength = literal.Length;
            }
            else content = new ByteArrayContent(literal);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }
        private static bool Header(HttpRequestMessage request, string name, string expected) =>
            request.Headers.TryGetValues(name, out var values) && values.SequenceEqual([expected]);
        private static bool Same(JsonElement expected, JsonElement actual)
        {
            if (expected.ValueKind != actual.ValueKind) return false;
            if (expected.ValueKind == JsonValueKind.Object)
            {
                var left = expected.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                var right = actual.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                return left.Length == right.Length && left.Zip(right).All(pair => pair.First.Name == pair.Second.Name && Same(pair.First.Value, pair.Second.Value));
            }
            if (expected.ValueKind == JsonValueKind.Array)
                return expected.GetArrayLength() == actual.GetArrayLength() && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
            return expected.ValueKind == JsonValueKind.String ? expected.GetString() == actual.GetString() : expected.GetRawText() == actual.GetRawText();
        }
        private static IEnumerable<string> Strings(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var child in value.EnumerateArray()) foreach (var text in Strings(child)) yield return text;
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) foreach (var text in Strings(property.Value)) yield return text;
        }
    }

    /// <summary>Owns literal offline bytes and joins its actual admitted read before physical close.</summary>
    private sealed class FirstReadGatedBody(byte[] literal, Func<CancellationToken, ValueTask> beforeRead) : Stream
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly AsyncLocal<bool> _insideRead = new();
        private TaskCompletionSource<int>? _reading;
        private Task? _disposal;
        private bool _readStarted, _closed;
        private int _position;
        public override bool CanRead { get { lock (_gate) return !_closed; } }
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => literal.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<int> reading; bool first;
            lock (_gate)
            {
                if (_closed) throw new ObjectDisposedException(nameof(FirstReadGatedBody));
                if (destination.IsEmpty) return ValueTask.FromResult(0);
                if (_reading is not null) throw new InvalidOperationException("Offline body has one admitted read.");
                first = !_readStarted; _readStarted = true;
                _reading = reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _ = ReadCoreAsync(destination, cancellationToken, first, reading);
            return new(reading.Task);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private async Task ReadCoreAsync(Memory<byte> destination, CancellationToken token, bool first, TaskCompletionSource<int> reading)
        {
            var count = 0; Exception? failure = null;
            try
            {
                _insideRead.Value = true;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (first) await beforeRead(linked.Token).ConfigureAwait(false);
                // OfflineGate retains its fault check after wake; no canceled/faulted wake supplies bytes.
                linked.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_closed) throw new ObjectDisposedException(nameof(FirstReadGatedBody));
                    count = Math.Min(destination.Length, literal.Length - _position);
                    literal.AsMemory(_position, count).CopyTo(destination); _position += count;
                }
            }
            catch (Exception error) { failure = error; }
            finally { _insideRead.Value = false; }
            lock (_gate)
            {
                if (failure is null) reading.TrySetResult(count); else reading.TrySetException(failure);
                _reading = null;
            }
        }
        public override ValueTask DisposeAsync()
        {
            if (_insideRead.Value) throw new InvalidOperationException("Offline body callback cannot join its own read.");
            TaskCompletionSource? settlement = null; Task<int>? reading = null; Task pending;
            lock (_gate)
            {
                if (_disposal is null)
                {
                    _closed = true; reading = _reading?.Task;
                    settlement = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = settlement.Task;
                }
                pending = _disposal;
            }
            if (settlement is not null) _ = CloseAsync(reading, settlement);
            return new(pending);
        }
        private async Task CloseAsync(Task<int>? reading, TaskCompletionSource settlement)
        {
            Exception? failure = null;
            try { _stop.Cancel(); } catch (Exception error) { failure = error; }
            if (reading is not null)
                try { await reading.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception error) { failure ??= error; }
            try { _stop.Dispose(); } catch (Exception error) { failure ??= error; }
            if (failure is null) settlement.TrySetResult(); else settlement.TrySetException(failure);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
