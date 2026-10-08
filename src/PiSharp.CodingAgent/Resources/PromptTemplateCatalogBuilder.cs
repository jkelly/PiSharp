using System.Collections.Immutable;

namespace PiSharp.CodingAgent.Resources;

/// <summary>Pi v0.99.1 file-outcome parsing and first-name-wins collisions over supplied values only.</summary>
public static class PromptTemplateCatalogBuilder
{
    public static PromptTemplateCatalogSnapshot Build(IEnumerable<PromptTemplateReadOutcome> outcomes,
        Func<string, PromptTemplateMetadata> decodeFrontmatter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(decodeFrontmatter);
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = new List<PromptTemplateResource>();
        var diagnostics = ImmutableArray.CreateBuilder<PromptTemplateResourceDiagnostic>();
        foreach (var outcome in outcomes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(outcome);
            ArgumentNullException.ThrowIfNull(outcome.Identity);
            if (outcome is PromptTemplateReadFailure failure)
            {
                diagnostics.Add(new(PromptTemplateResourceDiagnosticType.Warning, failure.Message, failure.Identity.FilePath));
                continue;
            }
            if (outcome is not PromptTemplateReadText text)
                throw new ArgumentException("Unknown prompt template read outcome.", nameof(outcomes));
            try
            {
                var template = PromptTemplateParser.Parse(text.Identity.FileName, text.Content, decodeFrontmatter);
                cancellationToken.ThrowIfCancellationRequested();
                parsed.Add(new(template, text.Identity.FilePath, text.Identity.SourceInfo));
            }
            // Original cancellation is never converted into a successful partial catalog.
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                diagnostics.Add(new(PromptTemplateResourceDiagnosticType.Warning, error.Message, text.Identity.FilePath));
            }
        }
        return FromParsedResources(parsed, diagnostics, cancellationToken);
    }

    internal static PromptTemplateCatalogSnapshot FromParsedResources(IEnumerable<PromptTemplateResource> parsed,
        IEnumerable<PromptTemplateResourceDiagnostic> suppliedDiagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<PromptTemplateResourceDiagnostic>();
        diagnostics.AddRange(suppliedDiagnostics);
        var resources = ImmutableArray.CreateBuilder<PromptTemplateResource>();
        var seen = new Dictionary<string, PromptTemplateResource>(StringComparer.Ordinal);
        foreach (var resource in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.TryGetValue(resource.Template.Name, out var winner))
                diagnostics.Add(new(PromptTemplateResourceDiagnosticType.Collision,
                    $"name \"/{resource.Template.Name}\" collision", resource.FilePath,
                    new(resource.Template.Name, winner.FilePath, resource.FilePath)));
            else
            {
                seen.Add(resource.Template.Name, resource); resources.Add(resource);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(resources.ToImmutable(), diagnostics.ToImmutable());
    }
}
