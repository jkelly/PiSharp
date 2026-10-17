// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/harness-setup.ts
// The getters of pi's SettingsManager the Harness settings read (core/settings-manager.ts getProviderRetrySettings, getHttpIdleTimeoutMs,
// getCompactionSettings, getRetrySettings, getSteeringMode, getFollowUpMode) are ported here over PiSettings' merged layers, read at every
// access. The coding registry installs the durable CodingTools (PiSharp.Durable.Tools), as upstream installs pi-durable's own CodingTools
// and not the coding agent's tools; only the tools' prompt contributions come from the coding agent (DurablePrompt).
// PiSharp-only (decision 0005, "PiSharp-only additions"): the registry also installs ask_user_question and exit_plan_mode (user-interaction)
// and plan mode (plan-mode, kept out of the default selection); PlanDirectory picks where plan mode may write.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Cli.Pi;
using PiSharp.Durable;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using PiSharp.Durable.Tools.Images;

namespace PiSharp.Cli.Durable;

/// <summary>The model a new root conversation starts with (upstream <c>InitialModel</c>).</summary>
internal sealed record DurableInitialModel(ModelRef? Model = null, string? ThinkingLevel = null, string? FallbackMessage = null);

/// <summary>Harness settings read at every use from pi's settings as loaded at startup (upstream <c>createHarnessSettings</c>).</summary>
/// <param name="registry">The registry whose default extension selection <see cref="Extensions"/> narrows; null selects every installed
/// extension.</param>
internal sealed class PiHarnessSettings(PiSettings settings, IRegistryReader? registry = null) : HarnessSettings
{
    /// <summary>
    /// Every installed extension but <c>tool-search</c>, which is selected only while an installed tool is deferred (PiSharp-only, durable
    /// work list area l): as the coding agent registers <c>tool_search</c> and activates it only for deferred tools, so the tools the model
    /// is offered stay the same until something defers one. <c>pisharp durable</c> defers none. <c>plan-mode</c> is never selected by
    /// default: it confines write and edit to the plan directory, so only <c>/plan</c> selects it for a conversation (PiSharp-only, decision
    /// 0005). Null without a registry: every installed extension.
    /// </summary>
    public override IReadOnlyList<Extension>? Extensions
    {
        get
        {
            if (registry is null) return null;
            var snapshot = registry.Snapshot();
            var deferred = snapshot.Tools().Any(tool => tool.Tool.Deferred);
            return [.. snapshot.Installed().Where(extension => extension.Name != PlanMode.ExtensionName &&
                (deferred || extension.Name != DurableHarnessSetup.ToolSearchExtensionName))];
        }
    }

    /// <summary>getProviderRetrySettings and getHttpIdleTimeoutMs: <c>timeoutMs</c> falls back to the idle timeout (0, disabled, is
    /// 2147483647).</summary>
    public override ConversationStreamOptions? Stream
    {
        get
        {
            var provider = settings.Merged["retry"] is JsonObject retry ? retry["provider"] as JsonObject : null;
            var idle = PiHttpIdleTimeout.FromSettings(settings.Merged);
            return new ConversationStreamOptions
            {
                TimeoutMs = Number(provider?["timeoutMs"]) ?? (idle == 0 ? 2147483647 : idle),
                MaxRetryDelayMs = Number(provider?["maxRetryDelayMs"]) ?? 60000,
                MaxRetries = Number(provider?["maxRetries"]) is { } retries ? (int)Math.Clamp(retries, int.MinValue, int.MaxValue) : null,
            };
        }
    }

    /// <summary>getCompactionSettings(): enabled, reserveTokens and keepRecentTokens (no model overrides: upstream passes no model).</summary>
    public override CompactionSettings? Compaction => new()
    {
        Enabled = settings.Merged["compaction"] is JsonObject compaction && compaction["enabled"] is { } enabled ? Truthy(enabled) : true,
        ReserveTokens = CompactionTokens("reserveTokens", 16384),
        KeepRecentTokens = CompactionTokens("keepRecentTokens", 20000),
    };

    /// <summary>getRetrySettings().</summary>
    public override RetrySettings? Retry
    {
        get
        {
            var retry = settings.Merged["retry"] as JsonObject;
            return new RetrySettings
            {
                Enabled = retry?["enabled"] is { } enabled ? Truthy(enabled) : true,
                MaxRetries = Number(retry?["maxRetries"]) is { } maximum ? (int)Math.Clamp(maximum, int.MinValue, int.MaxValue) : 3,
                BaseDelayMs = Number(retry?["baseDelayMs"]) ?? 2000,
                MaxAgentDelayMs = Number(retry?["maxAgentDelayMs"]) ?? 60000,
            };
        }
    }

    /// <summary>getSteeringMode(): "all", else "one-at-a-time".</summary>
    public override QueueMode? SteeringMode => settings.String("steeringMode") == "all" ? QueueMode.All : QueueMode.OneAtATime;

    /// <summary>getFollowUpMode(): "all", else "one-at-a-time".</summary>
    public override QueueMode? FollowUpMode => settings.String("followUpMode") == "all" ? QueueMode.All : QueueMode.OneAtATime;

    /// <summary>getCompactionTokenSetting without a model: the ordinary setting, else the default; a present value must be a non-negative
    /// safe integer.</summary>
    private double CompactionTokens(string field, double fallback)
    {
        var node = settings.Merged["compaction"] is JsonObject compaction ? compaction[field] : null;
        if (node is null) return fallback;
        if (Number(node) is { } value && value >= 0 && value <= 9007199254740991 && Math.Floor(value) == value) return value;
        throw new InvalidDataException($"Invalid compaction.{field} setting: {(node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : node.ToJsonString())}. Expected a non-negative safe integer.");
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;

    /// <summary>JavaScript truthiness of a JSON value.</summary>
    private static bool Truthy(JsonNode node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.False => false,
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.GetValue<double>() is var number && number != 0 && !double.IsNaN(number),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>().Length > 0,
        _ => true,
    };
}

/// <summary>One execution environment per directory, shared by every conversation in it (upstream <c>ExecutionEnvs</c>).</summary>
internal sealed class DurableExecutionEnvs(string defaultCwd)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, LocalExecutionEnv> envs = new(StringComparer.Ordinal);

    /// <summary>The environment of <paramref name="target"/>'s cwd, the default directory when it has none (<c>HarnessOptions.Env</c>).</summary>
    public Task<IExecutionEnv?> EnvAsync(EnvTarget target, PiSharp.Durable.Chord.DurableContext context)
    {
        var cwd = target.Cwd ?? defaultCwd;
        lock (gate)
        {
            if (!envs.TryGetValue(cwd, out var env)) envs[cwd] = env = new LocalExecutionEnv(cwd);
            return Task.FromResult<IExecutionEnv?>(env);
        }
    }

    /// <summary>Cleans up every environment created so far.</summary>
    public async Task CleanupAsync(PiSharp.Durable.Chord.DurableContext context)
    {
        LocalExecutionEnv[] all;
        lock (gate) { all = [.. envs.Values]; envs.Clear(); }
        foreach (var env in all) await env.CleanupAsync(context).ConfigureAwait(false);
    }
}

/// <summary>The Harness pieces of <c>pisharp durable</c> (upstream <c>harness-setup.ts</c>).</summary>
internal static class DurableHarnessSetup
{
    /// <summary>pi's HTTP setup (upstream <c>configureHarnessHttp</c>): the global <c>httpProxy</c> fills HTTP_PROXY and HTTPS_PROXY when
    /// they are unset (applyHttpProxySettings), and provider requests time out when headers or body data stall for
    /// <c>httpIdleTimeoutMs</c> (configureHttpDispatcher), through the live routes' HTTP handlers.</summary>
    /// <exception cref="InvalidDataException">An invalid <c>httpIdleTimeoutMs</c> setting.</exception>
    internal static LiveSessionRuntime ConfigureHarnessHttp(PiSettings settings, LiveSessionRuntime runtime, Func<string, string?> getEnvironment,
        Action<string, string?> setEnvironment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);
        if (settings.Global["httpProxy"] is JsonValue proxyValue && proxyValue.TryGetValue<string>(out var proxy) && PiArgs.JsTrim(proxy) is { Length: > 0 } trimmed)
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY" })
                if (getEnvironment(name) is null) setEnvironment(name, trimmed);
        var idle = PiHttpIdleTimeout.FromSettings(settings.Merged);
        return runtime with { CreateHttpHandler = PiHttpIdleTimeout.Wrap(runtime.CreateHttpHandler, idle) };
    }

    /// <summary>The image processor of <c>pisharp durable</c>'s read tool: one worker thread per process, started on the first image.</summary>
    private static readonly Lazy<IImageProcessor> CodingImages = new(() => ImageProcessors.CreateThreaded(PiSharp.Tools.Skia.SkiaImageCodec.Instance));

    /// <summary>The name of <see cref="ToolSearch.CreateExtension"/>'s extension.</summary>
    internal const string ToolSearchExtensionName = "tool-search";

    /// <summary>Harness settings read at every use (upstream <c>createHarnessSettings</c>); with <paramref name="registry"/>, its default
    /// extension selection leaves <c>tool_search</c> off until a tool is deferred (<see cref="PiHarnessSettings.Extensions"/>).</summary>
    internal static HarnessSettings CreateHarnessSettings(PiSettings settings, IRegistryReader? registry = null) => new PiHarnessSettings(settings, registry);

    /// <summary>
    /// A registry with the durable coding tools and pi's system prompt (upstream <c>createCodingRegistry</c>). Since pi-durable 1.2.0 the
    /// tools' read returns images; <c>pisharp durable</c> gives it an image processor on the coding agent's Skia codec, on a worker thread
    /// (durable work list area e; upstream's equivalent is <c>createCodingTools({ images: await createNodePhotonImages() })</c>). It also
    /// installs <c>tool_search</c> (pi-durable 1.2.0's <c>tool-search</c> extension), which the settings of
    /// <see cref="CreateHarnessSettings"/> leave unselected until a tool is deferred (durable work list area l). PiSharp-only (decision
    /// 0005): it installs <c>user-interaction</c> (<c>ask_user_question</c>, <c>exit_plan_mode</c>) and <c>plan-mode</c>, which the settings
    /// leave unselected until <c>/plan</c> selects it for a conversation.
    /// </summary>
    internal static IRegistry CreateCodingRegistry(PiSettings settings, string cwd, DurablePromptResources resources)
    {
        var registry = Registry.Create();
        registry.Install(CodingTools.CreateCodingTools(new ReadToolOptions { Images = CodingImages.Value }));
        registry.Install(DurablePrompt.Create(settings, cwd, resources));
        registry.Install(ToolSearch.CreateExtension());
        registry.Install(UserInteraction.CreateExtension());
        registry.Install(PlanMode.CreateExtension());
        return registry;
    }

    /// <summary>
    /// Where plan mode lets <paramref name="conversation"/> write (PiSharp-only, decision 0005): <c>&lt;session dir&gt;/plans/&lt;conversation
    /// id&gt;</c>, outside the repository; or, with the setting <c>planMode.directory</c> (absolute, <c>~</c>, or relative to the session's
    /// working directory), <c>&lt;planMode.directory&gt;/&lt;session id&gt;-&lt;conversation id&gt;</c>.
    /// </summary>
    internal static string PlanDirectory(DurableSessionLocation location, ConversationId conversation, PiSettings settings, string home)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(settings);
        var id = conversation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (settings.Merged["planMode"] is JsonObject planMode && planMode["directory"] is JsonValue value && value.TryGetValue<string>(out var configured) &&
            PiArgs.JsTrim(configured).Length > 0)
            return Path.Join(PiPaths.ResolvePath(configured, location.Cwd, home, trim: true), $"{location.Id}-{id}");
        return Path.Join(location.Directory, "plans", id);
    }

    /// <summary>The model a new root conversation starts with: an explicit provider and model, or pi's default resolution (upstream
    /// <c>findInitialAgentModel</c>).</summary>
    /// <exception cref="InvalidOperationException">"Could not resolve model: …" for an explicit model that does not resolve.</exception>
    internal static DurableInitialModel FindInitialAgentModel(PiSettings settings, DurableModels models, (string? Provider, string Model)? cli = null)
    {
        var registry = models.Registry;
        if (cli is { } explicitModel)
        {
            var resolved = ModelResolver.ResolveCliModel(explicitModel.Provider, explicitModel.Model, null, registry.GetAll(), registry.HasConfiguredAuth);
            if (resolved.Error is not null || resolved.Model is null) throw new InvalidOperationException($"Could not resolve model: {resolved.Error ?? explicitModel.Model}");
            return new(new ModelRef(resolved.Model.Provider, resolved.Model.Id), resolved.ThinkingLevel);
        }
        var initial = ModelResolver.FindInitialModel(null, null, [], isContinuing: false, settings.String("defaultProvider"), settings.String("defaultModel"),
            settings.String("defaultThinkingLevel"), null, registry);
        return initial.Model is null
            ? new(FallbackMessage: initial.FallbackMessage)
            : new(new ModelRef(initial.Model.Provider, initial.Model.Id), initial.ThinkingLevel, initial.FallbackMessage);
    }
}
