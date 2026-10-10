// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/provider-composer.ts (adaptOAuth, composeOAuthAuth,
// getAllModels with modifyModels).
namespace PiSharp.Cli.Models;

/// <summary>The OAuth methods extensions registered with their providers.</summary>
internal interface IExtensionOAuthLayer
{
    /// <summary>Whether an extension registered an OAuth method for the provider.</summary>
    bool Has(string provider);
    /// <summary>The API key of the stored OAuth credential (refreshed when it expires within five minutes), or null.</summary>
    string? ApiKey(string provider);
    /// <summary>modifyModels over the provider's models with the stored OAuth credential; null leaves them unchanged.</summary>
    IReadOnlyList<RegistryModel>? ModifyModels(string provider, IReadOnlyList<RegistryModel> models);
}
