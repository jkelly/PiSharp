using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Agent;

/// <summary>Trusted host schema admission. Validate the entire declared schema and final input, or reject it.</summary>
public delegate ValueTask<bool> ExtensionToolArgumentValidator(ExtensionToolRegistrationInfo tool,
    JsonData arguments, CancellationToken cancellationToken);

public sealed record ExtensionAgentBindingOptions(int MaximumTools = 128,
    int MaximumDeclarationCharacters = 1_048_576, int MaximumDeclarationBytes = 4_194_304)
{
    public ToolResultValueOptions ResultValues { get; init; } = new();
    public ExtensionEventDispatchOptions? ToolEventDispatch { get; init; }
    public int MaximumToolEventHandlers { get; init; } = 256;
    public Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? ReportEventDiagnostic { get; init; }
    /// <summary>Explicit generation-bound nested broker; the session owner must retire its cancellation token.</summary>
    public ToolInvocationScopeOptions? InvocationScopes { get; init; }
    /// <summary>Pure pinned system/tool-state replay for conversation-only context replacements.</summary>
    public Func<ImmutableArray<TranscriptEntry>, CancellationToken, TranscriptEntry?>? RestoreSystemMessage { get; init; }
    public Func<ImmutableArray<TranscriptEntry>, CancellationToken, string>? ReadSystemPrompt { get; init; }
    /// <summary>Explicit model-active names. Null uses descriptor defaults; unknown/hidden names are ignored.</summary>
    public ImmutableArray<string>? ActiveToolNames { get; init; }
    public Action<string, Exception>? ReportLoadoutDiagnostic { get; init; }
}

/// <summary>A fixed registry snapshot bound to prepared Agent tools. The caller owns registry/session lifetime.</summary>
public sealed class ExtensionAgentBinding
{
    private readonly ExtensionAgentBindingOptions options;
    private readonly JsonData declarations;
    public ExtensionRegistrySnapshot Snapshot { get; }
    public ImmutableArray<ExtensionToolRegistrationInfo> Registrations => Snapshot.Tools;
    public ImmutableArray<ExtensionToolRegistrationInfo> ActiveRegistrations { get; }
    public ImmutableArray<JsonData> RegisteredToolDeclarations { get; }
    /// <summary>The captured, schema-validating adapters used by Tools. Hosts may compose these before authorization.</summary>
    public ImmutableArray<IPreparedToolAdapter> Adapters { get; }
    /// <summary>The same captured registry hook dispatcher used by Tools; borrowed with the binding's registry lifetime.</summary>
    public IPreparedToolHooks? PreparedHooks { get; }
    public ImmutableArray<ToolDefinition> Tools { get; }
    /// <summary>Prompt preparation and request-context hooks for AgentConfiguration.Hooks; each event captures live registrations.</summary>
    public AgentHooks Hooks { get; }
    /// <summary>Context hooks without fixed binding presentation; session composition prepares its own active loadout.</summary>
    public AgentHooks ContextHooks { get; }
    public ImmutableArray<ToolLoadoutDiagnostic> LoadoutDiagnostics { get; }

    /// <summary>Borrowed leased native preparation callback for production session composition.</summary>
    public Func<ToolLoadout, ToolLoadoutChanges?>? GetLoadoutPreparation(string name) =>
        Registrations.FirstOrDefault(tool => tool.Name == name)?.HasLoadoutPreparation == true
            ? loadout => registry.PrepareToolLoadout(Snapshot, name, loadout, sessionToken: sessionCancellationToken)
            : null;
    private readonly ExtensionRegistry registry;
    private readonly CancellationToken sessionCancellationToken;

    public ExtensionAgentBinding(ExtensionRegistry registry, IToolActionPolicy policy,
        ExtensionToolArgumentValidator validateArguments, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null, ToolInvokerOptions? invokerOptions = null,
        ExtensionAgentBindingOptions? options = null, CancellationToken sessionCancellationToken = default,
        ExtensionRegistrySnapshot? capturedSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(validateArguments);
        this.registry = registry; this.sessionCancellationToken = sessionCancellationToken;
        this.options = options ?? new();
        if (this.options.MaximumTools <= 0 || this.options.MaximumDeclarationCharacters < 2 ||
            this.options.MaximumDeclarationBytes < 2 || this.options.MaximumToolEventHandlers is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentNullException.ThrowIfNull(this.options.ResultValues);
        _ = ToolResultValueCodec.Read(JsonData.EmptyObject, this.options.ResultValues);
        sessionCancellationToken.ThrowIfCancellationRequested();
        Snapshot = capturedSnapshot ?? registry.CaptureSnapshot();
        var limits = invokerOptions ?? new();
        if (Snapshot.Tools.Length > this.options.MaximumTools || Snapshot.Tools.Length > limits.MaximumTools)
            throw new InvalidOperationException("Extension Agent binding tool limit exceeded.");
        if (this.options.ActiveToolNames is { } supplied && (supplied.IsDefault || supplied.Length > this.options.MaximumTools))
            throw new ArgumentException("Invalid active tool selection.", nameof(options));
        var names = this.options.ActiveToolNames ?? Snapshot.Tools.Where(tool =>
            ToolExposureSemantics.ActivatesOnRegistration(tool.Exposure, tool.DefaultActive)).Select(tool => tool.Name).ToImmutableArray();
        if (names.Any(name => name is null)) throw new ArgumentException("Active names cannot be null.", nameof(options));
        if (names.Sum(name => (long)name.Length) > this.options.MaximumDeclarationCharacters)
            throw new InvalidOperationException("Active tool selection limit exceeded.");
        var byName = Snapshot.Tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        ActiveRegistrations = names.Distinct(StringComparer.Ordinal).Where(name => byName.TryGetValue(name, out var tool) &&
            tool.Exposure != ToolExposure.Hidden).Select(name => byName[name]).ToImmutableArray();
        declarations = Declarations(ActiveRegistrations, this.options);
        RegisteredToolDeclarations = Declarations(Snapshot.Tools, this.options).Value.EnumerateArray().Select(JsonData.FromElement).ToImmutableArray();
        var activeNames = ActiveRegistrations.Select(tool => tool.Name).ToImmutableHashSet(StringComparer.Ordinal);
        limits = limits with { AllowedRootTools = activeNames,
            AllowedNestedTools = Snapshot.Tools.Where(tool => ToolExposureSemantics.IsCallable(tool.Exposure, activeNames.Contains(tool.Name)))
                .Select(tool => tool.Name).ToImmutableHashSet(StringComparer.Ordinal) };
        Adapters = Snapshot.Tools.Select(tool => (IPreparedToolAdapter)new ExtensionToolAdapter(registry,
            Snapshot, tool, validateArguments, this.options.ResultValues, sessionCancellationToken)).ToImmutableArray();
        PreparedHooks = Snapshot.ToolCallHandlers.IsEmpty && Snapshot.ToolResultHandlers.IsEmpty ? null :
            new RegisteredExtensionToolHooks(registry, Snapshot, this.options.ResultValues, this.options.ToolEventDispatch,
                this.options.MaximumToolEventHandlers, sessionCancellationToken, this.options.ReportEventDiagnostic);
        var contextHooks = new RegisteredExtensionContextHooks(registry, this.options, sessionCancellationToken);
        var loadout = new ToolLoadout(ActiveRegistrations.Select(Metadata).ToImmutableArray(),
            Snapshot.Tools.Where(tool => ToolExposureSemantics.IsCallable(tool.Exposure, activeNames.Contains(tool.Name))).Select(Metadata).ToImmutableArray(),
            Snapshot.Tools.Select(Metadata).ToImmutableArray());
        var presentation = ToolLoadoutPresentation.Prepare(loadout, (name, original) => GetLoadoutPreparation(name)?.Invoke(original),
            this.options.ReportLoadoutDiagnostic, this.options.MaximumDeclarationCharacters, sessionCancellationToken);
        LoadoutDiagnostics = presentation.Diagnostics;
        ContextHooks = new() { TransformRequestMessages = contextHooks.TransformAsync, BeforePrompt = contextHooks.BeforePromptAsync };
        Hooks = ContextHooks with {
            FinalTransformRequestMessages = (messages, token) =>
            { token.ThrowIfCancellationRequested(); sessionCancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(presentation.Project(messages)); } };
        var invoker = this.options.InvocationScopes is { } scopes
            ? ToolInvoker.WithNestedCalls(Adapters, policy, scopes, PreparedHooks, transforms, resultTransforms, limits)
            : PreparedHooks is null
            ? new ToolInvoker(Adapters, policy, transforms, resultTransforms, limits)
            : ToolInvoker.WithPreparedHooks(Adapters, policy, PreparedHooks, transforms, resultTransforms, limits);
        Tools = ActiveRegistrations.Select(tool => new ToolDefinition(tool.Name, invoker)).ToImmutableArray();

        ToolLoadoutTool Metadata(ExtensionToolRegistrationInfo tool) => new(RegisteredToolDeclarations[Snapshot.Tools.IndexOf(tool)], tool.Exposure)
            { Namespace = tool.Namespace };
    }

    /// <summary>Explicit source-shaped declaration input. Publishing a later snapshot requires an idle host transaction.</summary>
    public TranscriptEntry CreateDeclarationMessage(string content, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > options.MaximumDeclarationCharacters || Encoding.UTF8.GetByteCount(content) > options.MaximumDeclarationBytes)
            throw new InvalidOperationException("Extension Agent declaration limit exceeded.");
        for (var index = 0; index < content.Length; index++)
            if (char.IsSurrogate(content[index]) && (!char.IsHighSurrogate(content[index]) ||
                ++index == content.Length || !char.IsLowSurrogate(content[index])))
                throw new ArgumentException("Invalid declaration content Unicode.", nameof(content));
        var text = JsonData.Parse(JsonSerializer.Serialize(content));
        // Charge the final envelope before constructing its buffer. JsonData also checks scalar Unicode.
        var prefix = "{\"role\":\"system\",\"content\":" + text + ",\"toolsAdded\":";
        var suffix = ",\"timestamp\":" + timestamp.ToString(CultureInfo.InvariantCulture) + "}";
        var characters = (long)prefix.Length + declarations.ToString().Length + suffix.Length;
        var bytes = (long)Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(declarations.ToString()) + Encoding.UTF8.GetByteCount(suffix);
        if (characters > options.MaximumDeclarationCharacters || bytes > options.MaximumDeclarationBytes)
            throw new InvalidOperationException("Extension Agent declaration limit exceeded.");
        return new("system", JsonData.Parse(prefix + declarations + suffix));
    }

    private static JsonData Declarations(ImmutableArray<ExtensionToolRegistrationInfo> tools, ExtensionAgentBindingOptions limits)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            long characters = 2, bytes = 2;
            var first = true;
            foreach (var tool in tools)
            {
                var prefix = "{\"name\":" + JsonSerializer.Serialize(tool.Name) + ",\"description\":" +
                    JsonSerializer.Serialize(tool.Description) + ",\"parameters\":";
                var parameters = tool.Parameters.ToString();
                var separator = first ? 0 : 1; first = false;
                characters += prefix.Length + (long)parameters.Length + 1 + separator;
                bytes += Encoding.UTF8.GetByteCount(prefix) + (long)Encoding.UTF8.GetByteCount(parameters) + 1 + separator;
                if (characters > limits.MaximumDeclarationCharacters || bytes > limits.MaximumDeclarationBytes)
                    throw new InvalidOperationException("Extension Agent declaration limit exceeded.");
                writer.WriteRawValue(prefix + parameters + "}");
            }
            writer.WriteEndArray();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)));
    }
}
