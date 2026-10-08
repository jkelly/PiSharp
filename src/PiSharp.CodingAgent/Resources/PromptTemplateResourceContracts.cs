using System.Collections.Immutable;

namespace PiSharp.CodingAgent.Resources;

public enum PromptTemplateSourceScope { User, Project, Temporary }
public enum PromptTemplateSourceOrigin { Package, TopLevel }

/// <summary>Caller-supplied provenance; none of these fields grants read or project authority.</summary>
public sealed record PromptTemplateSourceInfo(string Path, string Source, PromptTemplateSourceScope Scope,
    PromptTemplateSourceOrigin Origin, string? BaseDir = null);
public sealed record PromptTemplateResourceIdentity(string FilePath, string FileName, PromptTemplateSourceInfo SourceInfo);
public sealed record PromptTemplateResource(PromptTemplate Template, string FilePath, PromptTemplateSourceInfo SourceInfo);

/// <summary>An outcome supplied by the read owner. The catalog never accesses its path.</summary>
public abstract record PromptTemplateReadOutcome(PromptTemplateResourceIdentity Identity);
public sealed record PromptTemplateReadText(PromptTemplateResourceIdentity Identity, string Content)
    : PromptTemplateReadOutcome(Identity);
public sealed record PromptTemplateReadFailure(PromptTemplateResourceIdentity Identity, string Message)
    : PromptTemplateReadOutcome(Identity);

public enum PromptTemplateResourceDiagnosticType { Warning, Error, Collision }
public sealed record PromptTemplateResourceCollision(string Name, string WinnerPath, string LoserPath)
{
    public string ResourceType => "prompt";
}
public sealed record PromptTemplateResourceDiagnostic(PromptTemplateResourceDiagnosticType Type, string Message,
    string? Path = null, PromptTemplateResourceCollision? Collision = null);
public sealed record PromptTemplateCatalogSnapshot(ImmutableArray<PromptTemplateResource> Resources,
    ImmutableArray<PromptTemplateResourceDiagnostic> Diagnostics);
