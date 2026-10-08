using System.Collections.Immutable;
using PiSharp.Agent;

namespace PiSharp.CodingAgent.Resources;

/// <summary>A loaded template capture for embedding. No session generation, reload task or command authority.</summary>
public sealed class PromptTemplateResourceSet
{
    public PromptTemplateCatalogSnapshot Catalog { get; }
    public ImmutableArray<PromptTemplateCommandEntry> Commands { get; }

    private PromptTemplateResourceSet(PromptTemplateCatalogSnapshot catalog)
    {
        Catalog = catalog;
        Commands = catalog.Resources.Select(resource => new PromptTemplateCommandEntry(resource.Template.Name,
            resource.Template.Description, resource.Template.ArgumentHint, resource.SourceInfo)).ToImmutableArray();
    }

    public static async Task<PromptTemplateResourceSet> LoadAsync(IEnumerable<PromptTemplatePathSelection> selections,
        Func<string, PromptTemplateMetadata> decodeFrontmatter, IPromptTemplateFileSystem? fileSystem = null,
        CancellationToken cancellationToken = default) =>
        new(await PromptTemplateDiscovery.LoadAsync(selections, decodeFrontmatter, fileSystem, cancellationToken).ConfigureAwait(false));

    public IPromptInputAdmission CreateInputAdmission(PromptTemplateInputOperation operation,
        IPromptInputAdmission? inputHandlers = null, IPromptTemplateCommandAdmission? commands = null,
        bool? expandTemplates = null, PromptInputAdmissionOptions? limits = null) =>
        new PromptTemplateInputAdmission(Catalog, operation, inputHandlers, commands, expandTemplates, limits);
}
