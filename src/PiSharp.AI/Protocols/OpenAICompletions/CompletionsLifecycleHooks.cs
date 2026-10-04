using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>Owned pre-serialization payload, including source own-undefined cache-field presence.</summary>
public sealed record CompletionsPayloadObservation(JsonData Value, ImmutableArray<string> OwnUndefinedPaths);

/// <summary>Owned actual response metadata. It conveys neither the response nor body ownership.</summary>
public sealed record CompletionsResponseObservation(int Status, JsonData Headers);

/// <summary>Invocation-scoped awaited callbacks. A null payload replacement retains the supplied payload.</summary>
public sealed record CompletionsLifecycleHooks
{
    public Func<CompletionsPayloadObservation, ModelDescriptor, CancellationToken, ValueTask<JsonData?>>? OnPayload { get; init; }
    public Func<CompletionsResponseObservation, ModelDescriptor, CancellationToken, ValueTask>? OnResponse { get; init; }
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnProviderStreamEvent { get; init; }
    /// <summary>Awaits an owned raw/presence event and its production ECMAScript JSON view before mapping. Runs after the raw hook when both are supplied.</summary>
    public Func<CompletionsSourceSnapshot, ModelDescriptor, CancellationToken, ValueTask>? OnProviderStreamEventSnapshot { get; init; }
    /// <summary>Observes an actual bounded source publication before delivery. Called outside run locks; a fault ends observation and fails the producer.</summary>
    public Action<CompletionsSourcePublication>? OnSourcePublished { get; init; }
}

// HTTP preparation completes after the actual awaited response hook and before body acquisition.
// Ordinary parsed-chunk enumerators have no preparation phase and retain their existing behavior.
internal interface IPreparedCompletionsEnumerator
{
    void BindStartup(CompletionsStartupHandoff? startup);
    ValueTask PrepareAsync(CancellationToken cancellationToken);
}
