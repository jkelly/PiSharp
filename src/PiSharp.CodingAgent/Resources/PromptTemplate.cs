namespace PiSharp.CodingAgent.Resources;

/// <summary>Prompt text and completion metadata, independent of discovery and command registration.</summary>
public sealed record PromptTemplate(string Name, string Description, string Content, string? ArgumentHint = null);

/// <summary>The two string-valued fields selected from decoded Pi YAML frontmatter.</summary>
public sealed record PromptTemplateMetadata(string? Description = null, string? ArgumentHint = null);

/// <summary>Normalized body and the extracted YAML text; null means no frontmatter decoder is needed.</summary>
public sealed record PromptTemplateDocument(string? FrontmatterYaml, string Body);
