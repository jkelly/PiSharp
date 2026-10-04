using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI;

public sealed record ChatRequest(ModelDescriptor Model, ImmutableArray<TranscriptEntry> Messages, long Timestamp = 0);

/// <summary>Transport emits normalized immutable progress and a terminal event; it never invokes tools.</summary>
public interface IChatTransport
{
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Provider identity and catalog are separate from wire transport.</summary>
public interface IModelProvider
{
    string ProviderId { get; }
    IReadOnlyList<ModelDescriptor> Models { get; }
    IChatTransport Transport { get; }
}
