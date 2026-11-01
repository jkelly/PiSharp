// Decision 0012: runs a PiSharp native extension (IPiSharpExtension) as a source of durable tools. Its initialization registers into a
// capturing registry; each tool descriptor becomes a ToolRegistration: name, description, schema and its origin, exposure (model-only and
// code-only callers, deferred), sequential execution and output schema map directly; the result's content, isError, details and
// structuredContent become the durable result; nested tool calls go through IToolExecutionApi.ExecuteToolAsync. Tools are not replay-safe
// unless named in ReplaySafe. What durable tools cannot express is reported, not silently dropped: commands, handlers and observations are
// ignored, and PrepareInitialArgumentsAsync is dropped (durable's argument preparation is synchronous).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.CustomAgents.Tools;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Extensions;

namespace PiSharp.CustomAgents.Plugins;

/// <summary>Options of <see cref="PiSharpExtensionAdapter.LoadAsync"/>.</summary>
public sealed record PiSharpExtensionAdapterOptions
{
    /// <summary>The owner generation the extension sees.</summary>
    public long Generation { get; init; } = 1;
    /// <summary>Tools whose calls may run again after a restart (default none: a call a crash interrupted reports that instead).</summary>
    public IReadOnlyList<string> ReplaySafe { get; init; } = [];
    /// <summary>The extension's lifetime (cancelled when it is unloaded).</summary>
    public CancellationToken Lifetime { get; init; }
}

/// <summary>The durable tools a native extension registered, and what could not be mapped.</summary>
public sealed record PiSharpExtensionTools(IReadOnlyList<ToolRegistration> Tools, IReadOnlyList<string> Diagnostics);

/// <summary>Runs <see cref="IPiSharpExtension"/> implementations as durable tool sources.</summary>
public static class PiSharpExtensionAdapter
{
    /// <summary>Initializes <paramref name="extension"/> as <paramref name="ownerId"/> and maps the tools it registers.</summary>
    public static async Task<PiSharpExtensionTools> LoadAsync(IPiSharpExtension extension, string ownerId, PiSharpExtensionAdapterOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        options ??= new PiSharpExtensionAdapterOptions();
        var registry = new CapturingRegistry(ownerId, options);
        await extension.InitializeAsync(registry, cancellationToken).ConfigureAwait(false);
        var tools = new List<ToolRegistration>();
        foreach (var descriptor in registry.Tools)
        {
            if (descriptor.PrepareInitialArgumentsAsync is not null)
                registry.Diagnostics.Add($"Tool '{descriptor.Name}': PrepareInitialArgumentsAsync is not supported by durable tools; dropped.");
            tools.Add(Map(descriptor, ownerId, options, registry.Diagnostics));
        }
        return new PiSharpExtensionTools(tools, [.. registry.Diagnostics]);
    }

    private static ToolRegistration Map(ExtensionToolDescriptor descriptor, string ownerId, PiSharpExtensionAdapterOptions options, List<string> diagnostics)
    {
        ToolNames.Validate(descriptor.Name);
        ImmutableArray<ToolCaller>? callers = descriptor.Exposure switch
        {
            ToolExposure.ModelOnly => [ToolCaller.Model],
            ToolExposure.Codemode or ToolExposure.Hidden => [ToolCaller.Tools],
            _ => null,
        };
        if (descriptor.Exposure == ToolExposure.Hidden)
            diagnostics.Add($"Tool '{descriptor.Name}': hidden exposure maps to callable by other tools only.");
        var parameters = JsonValue.Parse(descriptor.Parameters.ToString()) as JsonObjectValue
            ?? throw new ArgumentException($"Tool '{descriptor.Name}' parameters are not a JSON object.");
        var output = descriptor.OutputSchema is { } schema ? JsonValue.Parse(schema.ToString()) as JsonObjectValue : null;
        return new ToolRegistration
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            Parameters = parameters,
            ParametersOrigin = descriptor.ParametersOrigin,
            ConstrainedSampling = descriptor.ConstrainedSampling is { } sampling ? JsonValue.Parse(sampling.ToString()) : null,
            StructuredOutputSchema = output,
            Replay = options.ReplaySafe.Contains(descriptor.Name, StringComparer.Ordinal) ? ToolReplay.Safe : ToolReplay.Unsafe,
            ExecutionMode = descriptor.SequentialExecution ? ToolExecutionMode.Sequential : null,
            Deferred = descriptor.Exposure == ToolExposure.Deferred || !descriptor.DefaultActive,
            Callers = callers,
            Execute = async (arguments, api, context) =>
            {
                var invocation = new InvocationContext(ownerId, options, api, context);
                JsonData result;
                try
                {
                    result = await descriptor.ExecuteAsync(JsonData.Parse(arguments.ToJson()), invocation, context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !context.IsCancellationRequested)
                {
                    return AgentTool.ToExecution(AgentToolResult.Error($"Tool '{descriptor.Name}' failed: {error.Message}"));
                }
                return ToResult(JsonValue.Parse(result.ToString()), output is not null);
            },
        };
    }

    /// <summary>A native result (<c>{content, isError?, details?, structuredContent?}</c>, or any other value as text) as a durable result.</summary>
    internal static ToolExecutionResult ToResult(JsonValue result, bool structured)
    {
        if (result is not JsonObjectValue fields || !fields.TryGetValue("content", out var content) || content is not JsonArrayValue items)
            return new ToolExecutionResult { Output = ToolJson.Text(result is JsonStringValue text ? text.Value : result.ToJson()) };
        var isError = fields.TryGetValue("isError", out var flag) && flag is JsonBooleanValue { Value: true };
        return new ToolExecutionResult
        {
            Output = items,
            IsError = isError ? true : null,
            Details = fields.TryGetValue("details", out var details) ? details : null,
            StructuredOutput = structured && !isError && fields.TryGetValue("structuredContent", out var value) ? value : null,
        };
    }

    private sealed class CapturingRegistry(string ownerId, PiSharpExtensionAdapterOptions options) : IExtensionRegistry
    {
        public List<ExtensionToolDescriptor> Tools { get; } = [];
        public List<string> Diagnostics { get; } = [];

        public string OwnerId => ownerId;
        public long OwnerGeneration => options.Generation;
        public string ContractProfile => ExperimentalExtensionContract.Profile;
        public ImmutableArray<string> Features => ["transactional-registration", "owned-descriptor-callbacks", "tool-invocation-context"];
        public CancellationToken ExtensionLifetimeCancellationToken => options.Lifetime;

        public IExtensionRegistration RegisterTool(ExtensionToolDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            Tools.Add(descriptor);
            return new Registration(this, descriptor.RegistrationId, () => Tools.Remove(descriptor));
        }

        public IExtensionRegistration RegisterCommand(ExtensionCommandDescriptor descriptor) => Ignored("command", descriptor.Name, descriptor.RegistrationId);
        public IExtensionRegistration Observe(ExtensionObservationDescriptor descriptor) => Ignored("observation", descriptor.Topic, descriptor.RegistrationId);
        public IExtensionRegistration RegisterBeforeAgentStartHandler(ExtensionBeforeAgentStartHandlerDescriptor descriptor) =>
            Ignored("before-agent-start handler", null, descriptor.RegistrationId);
        public IExtensionRegistration RegisterContextHandler(ExtensionContextHandlerDescriptor descriptor) => Ignored("context handler", null, descriptor.RegistrationId);
        public IExtensionRegistration RegisterContextWithSystemHandler(ExtensionContextWithSystemHandlerDescriptor descriptor) =>
            Ignored("context handler", null, descriptor.RegistrationId);
        public IExtensionRegistration RegisterInputHandler(ExtensionInputHandlerDescriptor descriptor) => Ignored("input handler", null, descriptor.RegistrationId);
        public IExtensionRegistration RegisterToolCallHandler(ExtensionToolCallHandlerDescriptor descriptor) => Ignored("tool call handler", null, descriptor.RegistrationId);
        public IExtensionRegistration RegisterToolResultHandler(ExtensionToolResultHandlerDescriptor descriptor) =>
            Ignored("tool result handler", null, descriptor.RegistrationId);

        private Registration Ignored(string what, string? name, string id)
        {
            Diagnostics.Add($"The extension's {what}{(name is null ? "" : " '" + name + "'")} is not supported by agents; ignored.");
            return new Registration(this, id, () => { });
        }
    }

    private sealed class Registration(CapturingRegistry registry, string id, Action remove) : IExtensionRegistration
    {
        public string OwnerId => registry.OwnerId;
        public long OwnerGeneration => registry.OwnerGeneration;
        public string RegistrationId => id;
        public void Dispose() => remove();
    }

    /// <summary>A tool call's context: cancellation, nested tool calls through the Harness, and updates as running output.</summary>
    private sealed class InvocationContext(string ownerId, PiSharpExtensionAdapterOptions options, IToolExecutionApi api, DurableContext context)
        : IExtensionToolInvocationContext
    {
        public string OwnerId => ownerId;
        public long OwnerGeneration => options.Generation;
        public CancellationToken OperationCancellationToken => context.CancellationToken;
        public CancellationToken SessionCancellationToken => context.CancellationToken;
        public CancellationToken ExtensionLifetimeCancellationToken => options.Lifetime;
        public string ToolCallId => api.CallId;

        public async ValueTask<ExtensionToolCallOutcome> ExecuteToolAsync(string name, JsonData arguments, ExtensionExecuteToolOptions? options = null)
        {
            if (JsonValue.Parse(arguments.ToString()) is not JsonObjectValue input)
                return new ExtensionToolCallOutcome(null, name, Text("Tool arguments must be a JSON object.", true), true);
            var nested = await api.ExecuteToolAsync(name, input, context).ConfigureAwait(false);
            var value = nested.StructuredOutput switch
            {
                null => string.Join("; ", nested.Diagnostics.Select(diagnostic => diagnostic.Message)),
                JsonStringValue text => text.Value,
                var other => other.ToJson(),
            };
            return new ExtensionToolCallOutcome(nested.TaskId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), name, Text(value, nested.IsError),
                nested.IsError, api.CallId);
        }

        public ValueTask ReportUpdateAsync(JsonData partialResult, CancellationToken cancellationToken = default)
        {
            if (ToResult(JsonValue.Parse(partialResult.ToString()), false).Output is { } output)
                foreach (var item in output.OfType<JsonObjectValue>())
                    if (item.TryGetValue("text", out var text) && text is JsonStringValue value) api.Output(value.Value);
            return ValueTask.CompletedTask;
        }

        private static JsonData Text(string text, bool isError) => JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text } },
            isError,
        }));
    }
}
