using System.Collections.Immutable;
using System.Text;

namespace PiSharp.CodingAgent.Resources;

/// <summary>Pi v0.99.1 low-level explicit-file/directory loading. No default paths, trust or package resolution.</summary>
public static class PromptTemplateDiscovery
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static async Task<PromptTemplateCatalogSnapshot> LoadAsync(IEnumerable<PromptTemplatePathSelection> selections,
        Func<string, PromptTemplateMetadata> decodeFrontmatter, IPromptTemplateFileSystem? fileSystem = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections); ArgumentNullException.ThrowIfNull(decodeFrontmatter);
        cancellationToken.ThrowIfCancellationRequested();
        var paths = selections.ToImmutableArray();
        foreach (var selection in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selection is null || !Path.IsPathFullyQualified(selection.Path) || selection.SourceInfo is null)
                throw new ArgumentException("Prompt selections must have caller-resolved absolute paths and provenance.", nameof(selections));
        }
        fileSystem ??= new SystemPromptTemplateFileSystem();
        var resources = new List<PromptTemplateResource>();
        var diagnostics = new List<PromptTemplateResourceDiagnostic>();
        var missing = new List<PromptTemplateResourceDiagnostic>();
        foreach (var selection in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var kind = fileSystem.Stat(selection.Path);
                cancellationToken.ThrowIfCancellationRequested();
                if (kind == PromptTemplateFileKind.Missing)
                {
                    if (selection.ReportMissingPath) missing.Add(new(PromptTemplateResourceDiagnosticType.Error,
                        "Prompt template path does not exist", selection.Path));
                }
                else if (kind == PromptTemplateFileKind.Directory) await LoadDirectoryAsync(selection).ConfigureAwait(false);
                else if (kind == PromptTemplateFileKind.File && selection.Path.EndsWith(".md", StringComparison.Ordinal))
                    await LoadFileAsync(selection.Path, Path.GetFileName(selection.Path), selection.SourceInfo).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                diagnostics.Add(new(PromptTemplateResourceDiagnosticType.Warning, error.Message, selection.Path));
            }
        }
        var catalog = PromptTemplateCatalogBuilder.FromParsedResources(resources, diagnostics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnosticPaths = new HashSet<string?>(catalog.Diagnostics.Select(diagnostic => diagnostic.Path), StringComparer.Ordinal);
        var allDiagnostics = catalog.Diagnostics.ToBuilder();
        foreach (var diagnostic in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The outer pinned resource loader adds a missing-path error only when no diagnostic
            // already mentions that exact path, including an earlier appended missing-path error.
            if (diagnosticPaths.Add(diagnostic.Path)) allDiagnostics.Add(diagnostic);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return catalog with { Diagnostics = allDiagnostics.ToImmutable() };

        async Task LoadDirectoryAsync(PromptTemplatePathSelection selection)
        {
            ImmutableArray<PromptTemplateDirectoryEntry> entries;
            try { entries = fileSystem.ReadDirectory(selection.Path); }
            catch (OperationCanceledException) { throw; }
            catch { cancellationToken.ThrowIfCancellationRequested(); return; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isFile = entry.Kind == PromptTemplateFileKind.File;
                if (entry.Kind == PromptTemplateFileKind.SymbolicLink)
                {
                    try { isFile = fileSystem.Stat(entry.Path) == PromptTemplateFileKind.File; }
                    catch (OperationCanceledException) { throw; }
                    catch { cancellationToken.ThrowIfCancellationRequested(); continue; }
                }
                if (isFile && entry.Name.EndsWith(".md", StringComparison.Ordinal))
                    await LoadFileAsync(entry.Path, entry.Name, selection.SourceInfo).ConfigureAwait(false);
            }
        }

        async Task LoadFileAsync(string path, string fileName, PromptTemplateSourceInfo sourceInfo)
        {
            var identity = new PromptTemplateResourceIdentity(path, fileName, sourceInfo with { Path = path });
            PromptTemplateReadOutcome outcome;
            try
            {
                var bytes = await fileSystem.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                outcome = new PromptTemplateReadText(identity, Utf8.GetString(bytes));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcome = new PromptTemplateReadFailure(identity, error.Message);
            }
            var loaded = PromptTemplateCatalogBuilder.Build([outcome], decodeFrontmatter, cancellationToken);
            resources.AddRange(loaded.Resources); diagnostics.AddRange(loaded.Diagnostics);
        }
    }
}
