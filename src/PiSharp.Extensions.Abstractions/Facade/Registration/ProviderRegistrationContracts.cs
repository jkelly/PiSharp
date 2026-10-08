using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions;

public sealed record ExtensionProviderModel(ModelDescriptor Model, JsonData Metadata);
public sealed record ExtensionProviderStreamRequest(ModelDescriptor Model, ImmutableArray<TranscriptEntry> Messages,
    long Timestamp = 0, string? ThinkingLevel = null, JsonData? Options = null);
/// <summary>One admitted native provider implementation. Null context identifies a borrowed built-in seed;
/// extension registrations receive the actual leased native context. No credential or transport lookup occurs.</summary>
public delegate IAsyncEnumerable<StreamEvent> ExtensionProviderStreamCallback(ExtensionProviderStreamRequest request,
    IExtensionContext? context, CancellationToken cancellationToken);
public sealed record ExtensionProviderDefinition(string Name, ImmutableArray<ExtensionProviderModel> Models,
    ExtensionProviderStreamCallback StreamSimple);
/// <summary>Host admission adapter for the original name/config overload. It must validate configuration and
/// return a genuine admitted stream implementation; this contract grants no environment/credential authority.</summary>
public interface IExtensionProviderConfigurationAdapter
{
    ExtensionProviderDefinition Resolve(string name, JsonData configuration);
}
public interface IExtensionProviderRegistrationFacade
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    void RegisterProvider(ExtensionProviderDefinition provider);
    void RegisterProvider(string name, JsonData configuration);
    void UnregisterProvider(string name);
}
/// <summary>Full original callback Task inventory, without flattening or losing a faulted OCE status.</summary>
public sealed class ExtensionProviderOriginalFaultException(string phase, Task? original, Exception evidence)
    : IOException("Admitted extension provider " + phase + " failed.", evidence)
{
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
}
public sealed class ExtensionProviderCanceledOriginalException(Task original, OperationCanceledException evidence)
    : OperationCanceledException("Admitted extension provider original canceled.", evidence, evidence.CancellationToken)
{
    public Task Original { get; } = original;
}
public sealed record ExtensionCustomMessage(string CustomType, JsonData Content, bool Display, JsonData? Details = null);
public enum ExtensionMessageDelivery { Steer, FollowUp, NextTurn }
public sealed record ExtensionMessageOptions(bool? TriggerTurn = null, ExtensionMessageDelivery? DeliverAs = null);
public sealed record ExtensionUserMessageOptions(ExtensionMessageDelivery? DeliverAs = null, bool? ExpandPromptTemplates = null);
/// <summary>Explicit admitted application effects. The implementation owns auth/model selection and real
/// session/agent delivery. No success or message effect can be synthesized by the facade.</summary>
public interface IExtensionRegistrationActionHost
{
    ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken);
    ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options, CancellationToken cancellationToken);
    ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options, CancellationToken cancellationToken);
}
public interface IExtensionRegistrationActions
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    ImmutableArray<ExtensionProviderModel> GetModels();
    ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default);
    ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options = null, CancellationToken cancellationToken = default);
}
public delegate ValueTask ExtensionRegistrationCommandCallback(JsonData arguments, IExtensionRegistrationActions actions,
    IExtensionCommandContext context, CancellationToken cancellationToken);
