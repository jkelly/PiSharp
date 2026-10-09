// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/resource-loader.ts (loadProjectContextFiles,
// loadContextFileFromDir, findShadowedContextFile, resolvePromptInput, discoverSystemPromptFile, discoverAppendSystemPromptFile,
// mergePaths), packages/coding-agent/src/core/package-manager.ts (resolve, addAutoDiscoveredResources, collectAncestorAgentsSkillDirs,
// collectAutoPromptEntries, collectAutoThemeEntries) and packages/coding-agent/src/core/footer-data-provider.ts (findGitPaths).
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Pi;

internal sealed record PiContextFile(string Path, string Content);
internal sealed record PiResourcePath(string Path, string Scope, string Source, bool ReportMissing);
internal sealed record PiTheme(string Name, string Path);

/// <summary>The run's resource inputs, found as upstream finds them. Paths only: nothing here grants tool or read authority.</summary>
internal sealed class PiResources
{
    internal ImmutableArray<PiContextFile> ContextFiles { get; init; } = [];
    /// <summary>The custom system prompt text (resolved <c>--system-prompt</c> or a discovered SYSTEM.md), or null for Pi's default.</summary>
    internal string? SystemPrompt { get; init; }
    internal string? SystemPromptSource { get; init; }
    /// <summary>Resolved <c>--append-system-prompt</c> values or the discovered APPEND_SYSTEM.md (source getAppendSystemPrompt).</summary>
    internal ImmutableArray<string> AppendSystemPrompt { get; init; } = [];
    internal ImmutableArray<PiResourcePath> SkillPaths { get; init; } = [];
    internal ImmutableArray<PiResourcePath> PromptPaths { get; init; } = [];
    internal ImmutableArray<PiResourcePath> ThemePaths { get; init; } = [];
    internal ImmutableArray<PiTheme> Themes { get; init; } = [];
    internal ImmutableArray<PiDiagnostic> Diagnostics { get; init; } = [];

    private static readonly string[] ContextCandidates = ["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];

    /// <summary>Source loadContextFileFromDir: the first candidate that is a file.</summary>
    internal static PiContextFile? LoadContextFileFromDir(string directory, List<PiDiagnostic>? warnings = null)
    {
        foreach (var name in ContextCandidates)
        {
            var path = Path.Join(directory, name);
            if (!Path.Exists(path)) continue;
            try
            {
                if (!File.Exists(path)) continue;
                // On case-insensitive file systems AGENTS.MD finds AGENTS.md: report the candidate's own spelling, as Node does.
                return new(path, PiPaths.ReadText(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { warnings?.Add(new("warning", $"Warning: Could not read {path}: {error.Message}")); }
        }
        return null;
    }

    internal sealed record GitPaths(string RepoDir, string CommonGitDir, string HeadPath);

    /// <summary>Source findGitPaths: the nearest <c>.git</c> directory or <c>gitdir:</c> file and its common git directory.</summary>
    internal static GitPaths? FindGitPaths(string cwd)
    {
        var directory = cwd;
        while (true)
        {
            var gitPath = Path.Join(directory, ".git");
            if (Path.Exists(gitPath))
            {
                try
                {
                    if (File.Exists(gitPath))
                    {
                        var content = File.ReadAllText(gitPath).Trim();
                        if (content.StartsWith("gitdir: ", StringComparison.Ordinal))
                        {
                            var gitDir = Path.GetFullPath(Path.Combine(directory, content[8..].Trim()));
                            var headPath = Path.Join(gitDir, "HEAD");
                            if (!File.Exists(headPath)) return null;
                            var commonDirPath = Path.Join(gitDir, "commondir");
                            var common = File.Exists(commonDirPath) ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirPath).Trim())) : gitDir;
                            return new(directory, common, headPath);
                        }
                    }
                    else if (Directory.Exists(gitPath))
                    {
                        var headPath = Path.Join(gitPath, "HEAD");
                        return File.Exists(headPath) ? new(directory, gitPath, headPath) : null;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            }
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || parent == directory) return null;
            directory = parent;
        }
    }

    /// <summary>Source findShadowedContextFile: the main repository's context file that a nested linked worktree's own copy shadows.</summary>
    internal static string? FindShadowedContextFile(string cwd)
    {
        var git = FindGitPaths(cwd);
        if (git is null) return null;
        var commonGitDir = PiPaths.Canonicalize(git.CommonGitDir);
        var worktreeRoot = PiPaths.Canonicalize(git.RepoDir);
        var mainRepoRoot = Path.GetDirectoryName(commonGitDir);
        if (mainRepoRoot is null || !worktreeRoot.StartsWith(mainRepoRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        if (PiPaths.Canonicalize(Path.Join(mainRepoRoot, ".git")) != commonGitDir) return null;
        var worktreeContext = LoadContextFileFromDir(worktreeRoot);
        return worktreeContext is null ? null : Path.Join(mainRepoRoot, Path.GetFileName(worktreeContext.Path));
    }

    /// <summary>Source loadProjectContextFiles: the agent directory's file, then cwd and its ancestors from the root down.</summary>
    internal static ImmutableArray<PiContextFile> LoadProjectContextFiles(string cwd, string agentDir, List<PiDiagnostic>? warnings = null)
    {
        var resolvedCwd = Path.GetFullPath(cwd); var resolvedAgentDir = Path.GetFullPath(agentDir);
        var files = new List<PiContextFile>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var global = LoadContextFileFromDir(resolvedAgentDir, warnings);
        if (global is not null) { files.Add(global); seen.Add(global.Path); }
        var ancestors = new List<PiContextFile>();
        var shadowed = FindShadowedContextFile(resolvedCwd);
        var current = resolvedCwd;
        while (true)
        {
            var context = LoadContextFileFromDir(current, warnings);
            var isShadowed = shadowed is not null && PiPaths.Canonicalize(context?.Path ?? "") == shadowed;
            if (context is not null && !isShadowed && seen.Add(context.Path)) ancestors.Insert(0, context);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        files.AddRange(ancestors);
        return [.. files];
    }

    /// <summary>Source resolvePromptInput: an existing file's contents, else the text itself.</summary>
    internal static string? ResolvePromptInput(string? input, string description, List<PiDiagnostic> warnings)
    {
        if (string.IsNullOrEmpty(input)) return null;
        if (Path.Exists(input))
        {
            try { return PiPaths.ReadText(input); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new("warning", $"Warning: Could not read {description} file {input}: {error.Message}"));
                return input;
            }
        }
        return input;
    }

    /// <summary>Source discoverSystemPromptFile / discoverAppendSystemPromptFile: the trusted project's file, else the global one.</summary>
    internal static string? DiscoverPromptFile(string cwd, string agentDir, bool projectTrusted, string name)
    {
        var project = Path.Join(cwd, PiConfig.ConfigDirName, name);
        if (projectTrusted && Path.Exists(project)) return project;
        var global = Path.Join(agentDir, name);
        return Path.Exists(global) ? global : null;
    }

    /// <summary>Source collectAncestorAgentsSkillDirs: <c>.agents/skills</c> of cwd and its ancestors up to the git repository root.</summary>
    internal static ImmutableArray<string> AncestorAgentsSkillDirs(string cwd)
    {
        var start = Path.GetFullPath(cwd);
        string? gitRoot = null;
        for (var probe = start; probe is not null; probe = Path.GetDirectoryName(probe) is { } up && up != probe ? up : null)
            if (Path.Exists(Path.Join(probe, ".git"))) { gitRoot = probe; break; }
        var result = ImmutableArray.CreateBuilder<string>();
        var directory = start;
        while (true)
        {
            result.Add(Path.Join(directory, ".agents", "skills"));
            if (gitRoot is not null && directory == gitRoot) break;
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || parent == directory) break;
            directory = parent;
        }
        return result.ToImmutable();
    }

    /// <summary>Source collectAutoPromptEntries / collectAutoThemeEntries: top-level files with the extension, not hidden.</summary>
    internal static ImmutableArray<string> CollectTopLevelFiles(string directory, string extension)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            return [.. new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Where(entry => !entry.Name.StartsWith('.') && entry.Name != "node_modules" && entry.Name.EndsWith(extension, StringComparison.Ordinal) &&
                    (entry is FileInfo || entry.LinkTarget is not null && File.Exists(entry.FullName)))
                .Select(entry => entry.FullName)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Discovers every resource input of a run (source DefaultResourceLoader.reload over package-manager resolve, without
    /// packages or extensions). Settings entries that are <c>+</c>/<c>-</c>/<c>!</c> override patterns are not paths and are skipped.</summary>
    internal static PiResources Discover(PiResourceRequest request)
    {
        var warnings = new List<PiDiagnostic>();
        var cwd = request.Cwd; var agentDir = request.AgentDir; var home = request.Home;
        var projectBase = Path.Join(cwd, PiConfig.ConfigDirName);
        var userAgentsSkills = Path.Join(home, ".agents", "skills");

        // Source reload over the package manager (IMPL-E): enabled -e source resources lead, enabled settings-package resources (rank 4)
        // follow the run's top-level ones.
        static IEnumerable<PiResourcePath> Resolved(PiSharp.Cli.Packages.PiResolvedPaths? resolved, string type, bool packagesOnly) =>
            resolved is null ? [] : resolved.Get(type).Where(resource => resource.Enabled && (!packagesOnly || resource.Metadata.Origin == "package"))
                .Select(resource => new PiResourcePath(resource.Path, resource.Metadata.Scope, resource.Metadata.Source, false));
        ImmutableArray<PiResourcePath> Collect(string type, ImmutableArray<string> cliPaths, bool disabled)
        {
            var list = new List<PiResourcePath>(Resolved(request.ExtensionSources, type, false));
            void Settings(JsonObject layer, string baseDir, string scope)
            {
                foreach (var entry in Strings(layer, type))
                    if (!(entry.StartsWith('+') || entry.StartsWith('-') || entry.StartsWith('!')) && PiPaths.IsLocalPath(entry))
                        list.Add(new(PiPaths.ResolvePath(entry, baseDir, home, trim: true), scope, "local", true));
            }
            if (!disabled)
            {
                Settings(request.Settings.Project, projectBase, "project");
                Settings(request.Settings.Global, agentDir, "user");
                if (request.ProjectTrusted)
                {
                    if (type == "skills") list.Add(new(Path.Join(projectBase, "skills"), "project", "auto", false));
                }
                if (type == "skills" && request.ProjectTrusted)
                    foreach (var directory in AncestorAgentsSkillDirs(cwd).Where(directory => !PiPaths.Comparer.Equals(Path.GetFullPath(directory), Path.GetFullPath(userAgentsSkills))))
                        list.Add(new(directory, "project", "auto", false));
                if (request.ProjectTrusted && type is "prompts" or "themes")
                    list.AddRange(CollectTopLevelFiles(Path.Join(projectBase, type), type == "prompts" ? ".md" : ".json").Select(path => new PiResourcePath(path, "project", "auto", false)));
                if (type == "skills")
                {
                    list.Add(new(Path.Join(agentDir, "skills"), "user", "auto", false));
                    list.Add(new(userAgentsSkills, "user", "auto", false));
                }
                else list.AddRange(CollectTopLevelFiles(Path.Join(agentDir, type), type == "prompts" ? ".md" : ".json").Select(path => new PiResourcePath(path, "user", "auto", false)));
                list.AddRange(Resolved(request.Packages, type, true));
            }
            // Source mergePaths(primary, additional): CLI paths last, duplicates (by canonical path) dropped.
            list.AddRange(cliPaths.Select(path => new PiResourcePath(PiPaths.IsLocalPath(path) ? PiPaths.ResolvePath(path, cwd, home, trim: true) : path, "temporary", "cli", true)));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return [.. list.Where(item => seen.Add(PiPaths.Canonicalize(item.Path)))];
        }

        var skills = Collect("skills", request.CliSkills, request.NoSkills);
        var prompts = Collect("prompts", request.CliPromptTemplates, request.NoPromptTemplates);
        var themes = Collect("themes", request.CliThemes, request.NoThemes);
        foreach (var path in request.CliSkills.Where(PiPaths.IsLocalPath).Select(path => PiPaths.ResolvePath(path, cwd, home, trim: true)).Where(path => !Path.Exists(path)))
            warnings.Add(new("error", $"Skill path does not exist: {path}"));
        foreach (var path in request.CliPromptTemplates.Where(PiPaths.IsLocalPath).Select(path => PiPaths.ResolvePath(path, cwd, home, trim: true)).Where(path => !Path.Exists(path)))
            warnings.Add(new("error", $"Prompt template path does not exist: {path}"));
        foreach (var path in request.CliThemes.Select(path => PiPaths.ResolvePath(path, cwd, home, trim: true)).Where(path => !Path.Exists(path)))
            warnings.Add(new("error", $"Theme path does not exist: {path}"));

        var contextFiles = request.NoContextFiles ? [] : LoadProjectContextFiles(cwd, agentDir, warnings);
        var systemSource = request.SystemPrompt ?? DiscoverPromptFile(cwd, agentDir, request.ProjectTrusted, "SYSTEM.md");
        var system = ResolvePromptInput(systemSource, "system prompt", warnings);
        var appendSources = request.AppendSystemPrompt is { IsDefault: false } explicitAppend ? explicitAppend :
            DiscoverPromptFile(cwd, agentDir, request.ProjectTrusted, "APPEND_SYSTEM.md") is { } discovered ? [discovered] : [];
        var append = appendSources.Select(source => ResolvePromptInput(source, "append system prompt", warnings)).OfType<string>().ToImmutableArray();
        var (loadedThemes, themeDiagnostics) = LoadThemes(themes);
        warnings.AddRange(themeDiagnostics);
        return new()
        {
            ContextFiles = contextFiles, SystemPrompt = system, SystemPromptSource = systemSource is not null && Path.Exists(systemSource) ? Path.GetFullPath(systemSource) : null,
            AppendSystemPrompt = append, SkillPaths = skills, PromptPaths = prompts, ThemePaths = themes, Themes = loadedThemes, Diagnostics = [.. warnings]
        };
    }

    /// <summary>Source extendResources: paths that resources_discover handlers returned, resolved against the cwd and appended after
    /// the run's own (duplicates dropped); new theme files are loaded as data.</summary>
    internal static PiResources WithDiscovered(PiResources resources, PiDiscoveredResources discovered, string cwd, string home)
    {
        if (discovered.IsEmpty) return resources;
        ImmutableArray<PiResourcePath> Append(ImmutableArray<PiResourcePath> existing, ImmutableArray<string> added)
        {
            var seen = new HashSet<string>(existing.Select(item => PiPaths.Canonicalize(item.Path)), StringComparer.Ordinal);
            return [.. existing, .. added.Select(path => new PiResourcePath(PiPaths.ResolvePath(path, cwd, home, trim: true), "temporary", "extension", true))
                .Where(item => seen.Add(PiPaths.Canonicalize(item.Path)))];
        }
        var themePaths = Append(resources.ThemePaths, discovered.ThemePaths);
        var (themes, themeDiagnostics) = LoadThemes(themePaths);
        return new()
        {
            ContextFiles = resources.ContextFiles, SystemPrompt = resources.SystemPrompt, SystemPromptSource = resources.SystemPromptSource,
            AppendSystemPrompt = resources.AppendSystemPrompt, SkillPaths = Append(resources.SkillPaths, discovered.SkillPaths),
            PromptPaths = Append(resources.PromptPaths, discovered.PromptPaths), ThemePaths = themePaths, Themes = themes,
            Diagnostics = [.. resources.Diagnostics.Where(diagnostic => !diagnostic.Message.Contains("collision", StringComparison.Ordinal) && !diagnostic.Message.StartsWith("Theme file", StringComparison.Ordinal) && !diagnostic.Message.StartsWith("Failed to load theme", StringComparison.Ordinal)), .. themeDiagnostics]
        };
    }

    private static IEnumerable<string> Strings(JsonObject layer, string name) =>
        layer[name] is JsonArray array ? array.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var text) ? text : null).OfType<string>() : [];

    internal static readonly ImmutableArray<string> BuiltinThemes = ["dark", "light"];

    /// <summary>Theme files as data (IMPL-I renders them): a file or a directory's top-level <c>.json</c> files, named by their
    /// <c>name</c> field. The first theme of a name wins; later ones are reported as collisions, as upstream does.</summary>
    internal static (ImmutableArray<PiTheme> Themes, ImmutableArray<PiDiagnostic> Diagnostics) LoadThemes(IEnumerable<PiResourcePath> paths)
    {
        var themes = new List<PiTheme>(); var diagnostics = new List<PiDiagnostic>();
        var names = new Dictionary<string, PiTheme>(StringComparer.Ordinal);
        foreach (var source in paths)
        {
            var files = Directory.Exists(source.Path) ? CollectTopLevelFiles(source.Path, ".json") : File.Exists(source.Path) ? [source.Path] : [];
            foreach (var file in files)
            {
                try
                {
                    var name = JsonNode.Parse(PiPaths.ReadText(file))?["name"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name)) { diagnostics.Add(new("warning", $"Theme file has no name: {file}")); continue; }
                    if (BuiltinThemes.Contains(name) || names.ContainsKey(name)) { diagnostics.Add(new("warning", $"name \"{name}\" collision: {file}")); continue; }
                    var theme = new PiTheme(name, file); names[name] = theme; themes.Add(theme);
                }
                catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
                { diagnostics.Add(new("warning", $"Failed to load theme {file}: {error.Message}")); }
            }
        }
        return ([.. themes], [.. diagnostics]);
    }
}

internal sealed record PiResourceRequest(string Cwd, string AgentDir, string Home, PiSettings Settings, bool ProjectTrusted)
{
    internal ImmutableArray<string> CliSkills { get; init; } = [];
    internal ImmutableArray<string> CliPromptTemplates { get; init; } = [];
    internal ImmutableArray<string> CliThemes { get; init; } = [];
    internal bool NoSkills { get; init; }
    internal bool NoPromptTemplates { get; init; }
    internal bool NoThemes { get; init; }
    internal bool NoContextFiles { get; init; }
    internal string? SystemPrompt { get; init; }
    internal ImmutableArray<string> AppendSystemPrompt { get; init; }
    /// <summary>The package manager's resolve() result (IMPL-E); its enabled package resources join the run's skills, prompts and themes.</summary>
    internal PiSharp.Cli.Packages.PiResolvedPaths? Packages { get; init; }
    /// <summary>The package manager's resolveExtensionSources() result for <c>-e</c> (IMPL-E); its enabled resources come first.</summary>
    internal PiSharp.Cli.Packages.PiResolvedPaths? ExtensionSources { get; init; }
}
