using PiSharp.Agent;
using PiSharp.CodingAgent.Resources;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Cli.Prompts;

/// <summary>Startup-owned template capture. No dispatcher, session, plugin or process lifecycle ownership.</summary>
internal sealed class PromptTemplateCliBinding
{
    internal PromptTemplateResourceSet Templates { get; }
    internal IRpcExtensionCommandCatalog CommandCatalog { get; }
    private readonly IPromptInputAdmission? _rawHandlers;
    private readonly IPromptTemplateCommandAdmission? _rawCommands;

    private PromptTemplateCliBinding(PromptTemplateResourceSet templates, IPromptInputAdmission? rawHandlers,
        IPromptTemplateCommandAdmission? rawCommands, IRpcExtensionCommandCatalog? extensionCatalog)
    {
        Templates = templates; _rawHandlers = rawHandlers; _rawCommands = rawCommands;
        CommandCatalog = new PromptTemplateRpcCommandCatalog(templates, extensionCatalog);
    }

    internal static async Task<PromptTemplateCliBinding> LoadAsync(PromptTemplateCliConfiguration configuration,
        IPromptInputAdmission? rawHandlers = null, IPromptTemplateCommandAdmission? rawCommands = null,
        IRpcExtensionCommandCatalog? extensionCatalog = null, Func<string, PromptTemplateMetadata>? decoder = null,
        IPromptTemplateFileSystem? fileSystem = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var templates = await PromptTemplateResourceSet.LoadAsync(configuration.Selections,
            decoder ?? PromptTemplateFrontmatterPolicy.RejectNonemptyYaml, fileSystem, token).ConfigureAwait(false);
        return new(templates, rawHandlers, rawCommands, extensionCatalog);
    }

    internal IPromptInputAdmission AdmissionFor(PromptTemplateInputOperation operation,
        bool? expandTemplates = null, PromptInputAdmissionOptions? limits = null) =>
        Templates.CreateInputAdmission(operation, _rawHandlers, _rawCommands, expandTemplates, limits);

    // Ready for the shared dispatcher's proposed command-type selector; streaming behavior must
    // not change ordinary prompt command handling into explicit-queue handling.
    internal IPromptInputAdmission AdmissionForRpc(string commandType) => AdmissionFor(commandType switch
    {
        "prompt" => PromptTemplateInputOperation.Prompt,
        "steer" => PromptTemplateInputOperation.Steer,
        "follow_up" => PromptTemplateInputOperation.FollowUp,
        _ => throw new ArgumentException("Unsupported prompt input command type.", nameof(commandType))
    });
}
