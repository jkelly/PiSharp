using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions;

/// <summary>The entry point specified by the native extension plan. The surrounding API is experimental.</summary>
public interface IPiSharpExtension : IAsyncDisposable
{
    ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken);
}

/// <summary>Owner-bound registrations. Initialization changes become visible together on successful completion.</summary>
public interface IExtensionRegistry
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    string ContractProfile { get; }
    ImmutableArray<string> Features { get; }
    CancellationToken ExtensionLifetimeCancellationToken { get; }
    IExtensionRegistration RegisterTool(ExtensionToolDescriptor descriptor);
    IExtensionRegistration RegisterCommand(ExtensionCommandDescriptor descriptor);
    IExtensionRegistration Observe(ExtensionObservationDescriptor descriptor);
    IExtensionRegistration RegisterBeforeAgentStartHandler(ExtensionBeforeAgentStartHandlerDescriptor descriptor);
    IExtensionRegistration RegisterContextHandler(ExtensionContextHandlerDescriptor descriptor);
    IExtensionRegistration RegisterContextWithSystemHandler(ExtensionContextWithSystemHandlerDescriptor descriptor);
    IExtensionRegistration RegisterInputHandler(ExtensionInputHandlerDescriptor descriptor);
    IExtensionRegistration RegisterToolCallHandler(ExtensionToolCallHandlerDescriptor descriptor);
    IExtensionRegistration RegisterToolResultHandler(ExtensionToolResultHandlerDescriptor descriptor);
}

public interface IExtensionRegistration : IDisposable
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    string RegistrationId { get; }
}

/// <summary>Cancellation ownership only. Session views and broker actions are later integration prerequisites.</summary>
public interface IExtensionContext
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    CancellationToken OperationCancellationToken { get; }
    CancellationToken SessionCancellationToken { get; }
    CancellationToken ExtensionLifetimeCancellationToken { get; }
}

public interface IExtensionToolContext : IExtensionContext
{
    /// <summary>Callable names captured by the host's prepared invocation pipeline; empty without a broker.</summary>
    ImmutableArray<string> Tools => [];
    /// <summary>Tool failures are outcomes. Legacy direct dispatch has no broker and grants no effects.</summary>
    ValueTask<ExtensionToolCallOutcome> ExecuteToolAsync(string name, JsonData arguments,
        ExtensionExecuteToolOptions? options = null) => ValueTask.FromResult(ExtensionToolCallOutcome.Unavailable(name));
}
public interface IExtensionCommandContext : IExtensionContext { }
/// <summary>The immutable host catalog belonging to this admitted command revision.</summary>
public interface IExtensionCommandCatalogContext : IExtensionCommandContext
{
    long CommandCatalogRevision { get; }
    JsonData CommandCatalog { get; }
}

// These callbacks exercise registration ownership. They are not the future Agent tool adapter or hook reducers.
public delegate ValueTask<JsonData> ExtensionToolCallback(
    JsonData arguments, IExtensionToolContext context, CancellationToken cancellationToken);
/// <summary>Pure trusted preparation, with cancellation but no host operation or UI context.</summary>
public delegate ValueTask<JsonData> ExtensionToolArgumentPreparationCallback(
    JsonData arguments, CancellationToken cancellationToken);
public delegate ValueTask ExtensionCommandCallback(
    JsonData arguments, IExtensionCommandContext context, CancellationToken cancellationToken);
public delegate ValueTask<JsonData> ExtensionCommandCompletionCallback(
    string prefix, CancellationToken cancellationToken);
public delegate ValueTask ExtensionObservationCallback(
    JsonData observation, IExtensionContext context, CancellationToken cancellationToken);

public sealed record ExtensionToolDescriptor(
    string RegistrationId, string Name, string Description, JsonData Parameters, ExtensionToolCallback ExecuteAsync)
{
    public ExtensionToolArgumentPreparationCallback? PrepareInitialArgumentsAsync { get; init; }
    public ToolExposure Exposure { get; init; } = ToolExposure.Direct;
    public ToolNamespace? Namespace { get; init; }
    /// <summary>Only direct/model-only tools activate by default. Other exposures require explicit activation.</summary>
    public bool DefaultActive { get; init; } = true;
    /// <summary>Pure synchronous presentation preparation; called only while this tool is active.</summary>
    public Func<ToolLoadout, ToolLoadoutChanges?>? PrepareLoadout { get; init; }
    /// <summary>Source promptGuidelines, reported to loadout preparation by ToolLoadout.GetPromptGuidelines.</summary>
    public ImmutableArray<string> PromptGuidelines { get; init; } = [];
    /// <summary>Source constrainedSampling: provider sampling constraints carried on the model-facing declaration, for example
    /// <c>{ "type": "grammar", "variants": { "openai_lark": "..." } }</c>. Null declares none.</summary>
    public JsonData? ConstrainedSampling { get; init; }
    /// <summary>The tool's own renderers, consulted after every registered tool renderer resolver.</summary>
    public ExtensionToolRenderers? Renderers { get; init; }
}

public sealed record ExtensionCommandDescriptor(
    string RegistrationId, string Name, string Description, ExtensionCommandCallback ExecuteAsync)
{
    public ExtensionCommandCompletionCallback? GetArgumentCompletionsAsync { get; init; }
    /// <summary>Source provenance declared by the approved owner, never executable authority.</summary>
    public string? SourcePath { get; init; }
}

/// <summary>An observation has no replacement, cancellation decision, or state-changing result.</summary>
public sealed record ExtensionObservationDescriptor(
    string RegistrationId, string Topic, ExtensionObservationCallback ObserveAsync);

/// <summary>Owner-registered typed callbacks. Initialization publishes these with the owner's tools.</summary>
public sealed record ExtensionBeforeAgentStartHandlerDescriptor(string RegistrationId,
    ExtensionReducerCallback<ExtensionBeforeAgentStartEvent, ExtensionBeforeAgentStartPatch> HandleAsync);
public sealed record ExtensionContextHandlerDescriptor(string RegistrationId,
    ExtensionReducerCallback<ExtensionContextEvent, ExtensionContextMessagesPatch> HandleAsync);
public sealed record ExtensionContextWithSystemHandlerDescriptor(string RegistrationId,
    ExtensionReducerCallback<ExtensionContextWithSystemEvent, ExtensionContextMessagesPatch> HandleAsync);
public sealed record ExtensionInputHandlerDescriptor(string RegistrationId,
    ExtensionReducerCallback<ExtensionInputEvent, ExtensionInputPatch> HandleAsync);
public sealed record ExtensionToolCallHandlerDescriptor(string RegistrationId,
    ExtensionReducerCallback<ExtensionToolCallEvent, ExtensionToolCallPatch> HandleAsync);
public sealed record ExtensionToolResultHandlerDescriptor(string RegistrationId,
    ExtensionReducerCallback<ExtensionToolResultEvent, ExtensionToolResultPatch> HandleAsync);

public static class ExperimentalExtensionContract
{
    public const string Profile = "experimental-registration-0";
    public static ImmutableArray<string> Features { get; } =
        ["before-agent-start", "context-request-transform", "context-with-system-request-transform", "transactional-registration", "owned-descriptor-callbacks", "awaited-observations", "registered-input-tool-reducers", "tool-invocation-context", "session-branch-snapshot", "session-durable-entries", "session-replacement", "session-creation", "session-opaque-records", "session-catalog", "session-context-edits"];
}
