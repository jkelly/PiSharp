using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.PiMessages;

/// <summary>Owned observable JSON and Source own-undefined presence. Neither transfers resources.</summary>
public sealed record PiMessagesValueObservation(JsonData Value, ImmutableArray<string> OwnUndefinedPaths);

/// <summary>Invocation-scoped awaited hooks. C# null retains payload; JsonData null replaces it literally.</summary>
public sealed record PiMessagesLifecycleHooks
{
    public Func<PiMessagesValueObservation, ModelDescriptor, CancellationToken, ValueTask<JsonData?>>? OnPayload { get; init; }
    public Func<PiMessagesValueObservation, ModelDescriptor, CancellationToken, ValueTask>? OnResponse { get; init; }
    public Func<PiMessagesValueObservation, ModelDescriptor, CancellationToken, ValueTask>? OnProviderStreamEvent { get; init; }
    /// <summary>Actual immutable native publication, including an optional owned Source emission snapshot.</summary>
    /// <remarks>A terminal is observed after owned cleanup. A terminal observer exception propagates
    /// from enumeration without delivering that terminal or fabricating another error event.</remarks>
    public Action<StreamEvent>? OnEventPublished { get; init; }
}
