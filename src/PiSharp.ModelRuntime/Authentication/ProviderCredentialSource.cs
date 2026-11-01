// Web Agent W2 (WU2, plan "Architecture" item 2): the one credential seam of PiSharp.ModelRuntime. It replaces the concrete
// AuthJsonCredentialStore (Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/auth-storage.ts,
// AuthStorage.read/list), which stays in PiSharp.Cli and implements it; PiSharp.WebAgent implements it over its own vault, per account.
using PiSharp.AI.Authentication.OAuth;

namespace PiSharp.Cli.Authentication;

/// <summary>One stored credential entry: <c>oauth</c>, <c>api_key</c> (with its configured, unresolved key and <c>env</c>) or another type.</summary>
internal sealed record StoredCredentialEntry(string Type, string? Key, IReadOnlyDictionary<string, string>? Environment)
{
    public override string ToString() => $"StoredCredentialEntry ({Type}) [redacted]";
}

/// <summary>
/// The stored credentials the registry and the live provider routes read: the OAuth entries <see cref="StoredOAuthLifecycle"/> reads and
/// refreshes under the source's lock (<see cref="IAdmittedOAuthCredentialSource"/>), plus each provider's entry type and configured
/// <c>api_key</c> value. The CLI's source is <c>auth.json</c>; a source never resolves a configured value (<c>!command</c>, env names).
/// </summary>
internal interface IProviderCredentialSource : IAdmittedOAuthCredentialSource
{
    /// <summary>Source AuthStorage.read: the entry's type tag, and for <c>api_key</c> its configured key value (unresolved) and
    /// <c>env</c>. Null for a missing entry, or when the source holds nothing yet.</summary>
    Task<StoredCredentialEntry?> ReadEntryAsync(string provider, CancellationToken cancellationToken);

    /// <summary>Source AuthStorage.list: provider ids and credential types, without resolving key values. Empty when the source holds
    /// nothing yet.</summary>
    Task<IReadOnlyList<(string Provider, string Type)>> ListAsync(CancellationToken cancellationToken);
}
