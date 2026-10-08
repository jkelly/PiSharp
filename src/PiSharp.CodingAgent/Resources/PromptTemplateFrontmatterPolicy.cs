namespace PiSharp.CodingAgent.Resources;

/// <summary>Explicit plain-template policy, not a YAML parser. The existing parser bypasses this
/// for absent or empty frontmatter. Supply a qualified YAML decoder to enable nonempty frontmatter.</summary>
public static class PromptTemplateFrontmatterPolicy
{
    public static PromptTemplateMetadata RejectNonemptyYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        throw new NotSupportedException("YAML prompt frontmatter is unavailable in this build; use a plain template or supply a qualified YAML decoder.");
    }
}
