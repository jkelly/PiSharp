using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;

namespace PiSharp.AI;

/// <summary>Offline normalized-event replay. This is not an HTTP/provider protocol adapter.</summary>
public sealed class RecordedChatTransport : IChatTransport
{
    private readonly ImmutableArray<StreamEvent> _events;
    public RecordedChatTransport(IEnumerable<StreamEvent> events) => _events = events.ToImmutableArray();

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var value in _events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }
}
