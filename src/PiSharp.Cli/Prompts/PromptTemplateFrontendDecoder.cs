using PiSharp.CodingAgent.Resources;

namespace PiSharp.Cli.Prompts;

internal static class PromptTemplateFrontendDecoder
{
    internal static (int SyntaxNodes, long WeightVisits, long ReferenceVisits) InspectAliasAccounting(string yaml) =>
#if PISHARP_PROMPT_YAML
        PiSharp.PromptTemplates.Yaml.PromptTemplateYamlDecoder.InspectAliasAccounting(yaml);
#else
        throw new InvalidOperationException("Standard YAML alias accounting is disabled.");
#endif

    internal static PromptTemplateMetadata Decode(string yaml) =>
#if PISHARP_PROMPT_YAML
        PiSharp.PromptTemplates.Yaml.PromptTemplateYamlDecoder.Decode(yaml);
#else
        PromptTemplateFrontmatterPolicy.RejectNonemptyYaml(yaml);
#endif
}
