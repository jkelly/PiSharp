// Decision 0011 (run W2, WU6): the chat area's account seam (IChatAccounts, WU7) over the credentials area's account store (WU5). A new chat
// binds its provider's active account; a chat's own account is found by id and must belong to the chat's provider. Only ids and labels
// cross: never a secret. Registered in every mode (with --faux the faux provider has no accounts, so chats bind none).
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.Routing;

/// <summary>The accounts chats bind, from <see cref="IAccountStore"/>.</summary>
public sealed class AccountChatAccounts(IAccountStore accounts) : IChatAccounts
{
    private readonly IAccountStore accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    /// <inheritdoc/>
    public Task<ChatAccount?> BindAsync(string provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ChatAccount?>(accounts.Active(provider) is { } active ? new ChatAccount(active.Id, active.Label) : null);
    }

    /// <inheritdoc/>
    public Task<ChatAccount?> FindAsync(string provider, string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(accountId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ChatAccount?>(accounts.Get(accountId) is { } account && account.Provider == provider ? new ChatAccount(account.Id, account.Label) : null);
    }
}
