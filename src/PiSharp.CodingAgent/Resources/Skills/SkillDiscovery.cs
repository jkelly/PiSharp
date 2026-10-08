using System.Collections.Immutable;

namespace PiSharp.CodingAgent.Resources.Skills;

/// <summary>Explicit host grant for the derived skills subtree only; no ambient lookup or project authority upgrade.</summary>
public sealed record SkillRootGrant(string Path, bool Trusted);
public sealed record SkillDiscoveryRequest(SkillRootGrant? UserAgentDirectory, SkillRootGrant? ProjectWorkingDirectory,
    ImmutableArray<SkillPathSelection> ExplicitPaths, bool IncludeDefaults = true);
public sealed record SkillReloadDescriptor(string Key, string Fingerprint, SkillResource Skill);

public static class SkillDiscovery
{
    public static ImmutableArray<SkillPathSelection> BuildSelections(SkillDiscoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExplicitPaths.IsDefault || request.ExplicitPaths.Length > 128) throw new ArgumentException("Invalid explicit skills selection.");
        var result = ImmutableArray.CreateBuilder<SkillPathSelection>();
        var user = Root(request.UserAgentDirectory, "skills"); var project = Root(request.ProjectWorkingDirectory, ".pi", "skills");
        if (request.IncludeDefaults)
        {
            if (user is not null) result.Add(new(user, request.UserAgentDirectory!.Trusted) { Scope = PromptTemplateSourceScope.User, ReportMissingPath = false });
            if (project is not null) result.Add(new(project, request.ProjectWorkingDirectory!.Trusted) { Scope = PromptTemplateSourceScope.Project, ReportMissingPath = false });
        }
        foreach (var selection in request.ExplicitPaths)
        {
            if (selection is null || !System.IO.Path.IsPathFullyQualified(selection.Path)) throw new ArgumentException("Explicit skill path must be absolute.");
            var scope = selection.Scope;
            if (!request.IncludeDefaults)
            {
                if (user is not null && Under(selection.Path, user)) scope = PromptTemplateSourceScope.User;
                else if (project is not null && Under(selection.Path, project)) scope = PromptTemplateSourceScope.Project;
            }
            result.Add(selection with { Scope = scope });
        }
        if (result.Count > 128) throw new ArgumentException("Too many skill selections.");
        return result.ToImmutable();
    }
    public static Task<SkillResourceSet> LoadAsync(SkillDiscoveryRequest request, Func<string, SkillMetadata> decode,
        ISkillResourceFileSystem? files = null, SkillResourceOptions? options = null, CancellationToken cancellationToken = default) =>
        SkillResourceSet.LoadAsync(BuildSelections(request), decode, files, options, cancellationToken);
    private static string? Root(SkillRootGrant? grant, params string[] suffix)
    {
        if (grant is null) return null;
        if (!System.IO.Path.IsPathFullyQualified(grant.Path) || grant.Path.Length > 4096) throw new ArgumentException("Skill root grant must be absolute.");
        string[] parts = [System.IO.Path.GetFullPath(grant.Path), .. suffix]; return System.IO.Path.Combine(parts);
    }
    private static bool Under(string path, string root)
    {
        var target = System.IO.Path.GetFullPath(path); var canonical = System.IO.Path.TrimEndingDirectorySeparator(root);
        var compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return target.Equals(canonical, compare) || target.StartsWith(canonical + System.IO.Path.DirectorySeparatorChar, compare);
    }
}
