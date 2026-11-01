// Decision 0011 (run W2, WU4): the sidebar's chat list before the Chat area registers its ChatService: no chats.
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Faux;

/// <summary>An <see cref="IRecentChats"/> with no chats.</summary>
public sealed class EmptyRecentChats : IRecentChats
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<RecentChat>> ListAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecentChat>>([]);
}
