using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Packages;

internal sealed partial class PiPackageManager
{
    // ---- resource collection --------------------------------------------------------------------------------------------------

    private bool CollectPackageResources(string packageRoot, Accumulator accumulator, PiPackageEntry? filter, PiPathMetadata metadata)
    {
        var manifest = PiManifest.Read(Path.Join(packageRoot, "package.json"));
        metadata.ChangelogPath = ResolvePackageChangelogPath(packageRoot, manifest);
        if (filter is not null)
        {
            foreach (var resourceType in ResourceTypes)
            {
                var patterns = filter.Patterns(resourceType);
                var target = accumulator.Maps[resourceType];
                if (filter.Autoload == false) ApplyPackageDeltaFilter(packageRoot, patterns ?? [], resourceType, target, metadata);
                else if (patterns is { } given) ApplyPackageFilter(packageRoot, given, resourceType, target, metadata);
                else CollectDefaultResources(packageRoot, resourceType, target, metadata);
            }
            return true;
        }
        if (manifest is { HasResourceFields: true })
        {
            foreach (var resourceType in ResourceTypes) AddManifestEntries(manifest.Get(resourceType), packageRoot, resourceType, accumulator.Maps[resourceType], metadata);
            return true;
        }
        var hasAnyDir = false;
        foreach (var resourceType in ResourceTypes)
        {
            var directory = Path.Join(packageRoot, resourceType);
            if (!Path.Exists(directory)) continue;
            foreach (var file in CollectResourceFiles(directory, resourceType)) AddResource(accumulator.Maps[resourceType], file, metadata, true);
            hasAnyDir = true;
        }
        return hasAnyDir;
    }

    /// <summary>Pi 1.5.0 resolvePackageChangelogPath: <c>pi.changelogPath</c>, else <c>CHANGELOG.md</c>, resolved against the package root;
    /// only an existing file whose real path is inside the package's real root counts. An empty <c>changelogPath</c> turns it off.</summary>
    internal static string? ResolvePackageChangelogPath(string packageRoot, PiManifest? manifest)
    {
        var changelogPath = manifest?.ChangelogPath ?? "CHANGELOG.md";
        if (changelogPath.Length == 0) return null;
        var resolvedPath = NodeResolve(packageRoot, changelogPath);
        if (!Path.Exists(resolvedPath)) return null;
        if (!IsPathInside(PiPaths.Canonicalize(packageRoot), PiPaths.Canonicalize(resolvedPath))) return null;
        try { return IsFile(resolvedPath) ? resolvedPath : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Pi 1.5.0 isPathInside: <paramref name="child"/> lies below <paramref name="parent"/> (node path.relative).</summary>
    private static bool IsPathInside(string parent, string child)
    {
        var relativePath = Path.GetRelativePath(parent, child);
        return relativePath != "." && relativePath != ".." && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relativePath);
    }

    private void CollectDefaultResources(string packageRoot, string resourceType, OrderedDictionary<string, (PiPathMetadata, bool)> target, PiPathMetadata metadata)
    {
        var entries = PiManifest.Read(Path.Join(packageRoot, "package.json"))?.Get(resourceType);
        if (entries is not null) { AddManifestEntries(entries, packageRoot, resourceType, target, metadata); return; }
        var directory = Path.Join(packageRoot, resourceType);
        if (Path.Exists(directory)) foreach (var file in CollectResourceFiles(directory, resourceType)) AddResource(target, file, metadata, true);
    }

    private void ApplyPackageFilter(string packageRoot, ImmutableArray<string> userPatterns, string resourceType,
        OrderedDictionary<string, (PiPathMetadata, bool)> target, PiPathMetadata metadata)
    {
        var allFiles = CollectManifestFiles(packageRoot, resourceType);
        if (userPatterns.IsEmpty)
        {
            // An empty array disables every resource of the type.
            foreach (var file in allFiles) AddResource(target, file, metadata, false);
            return;
        }
        var enabled = ApplyPatterns(allFiles, userPatterns, packageRoot);
        foreach (var file in allFiles) AddResource(target, file, metadata, enabled.Contains(file));
    }

    private void ApplyPackageDeltaFilter(string packageRoot, ImmutableArray<string> userPatterns, string resourceType,
        OrderedDictionary<string, (PiPathMetadata, bool)> target, PiPathMetadata metadata)
    {
        if (userPatterns.IsEmpty) return;
        var allFiles = CollectManifestFiles(packageRoot, resourceType);
        foreach (var (file, enabled) in ApplyAutoloadDisabledPatterns(allFiles, userPatterns, packageRoot)) AddResource(target, file, metadata, enabled);
    }

    /// <summary>Source collectManifestFiles: the files of a type after the manifest's own override patterns.</summary>
    private List<string> CollectManifestFiles(string packageRoot, string resourceType)
    {
        var entries = PiManifest.Read(Path.Join(packageRoot, "package.json"))?.Get(resourceType);
        if (entries is { Length: > 0 } manifestEntries)
        {
            var allFiles = CollectFilesFromManifestEntries(manifestEntries, packageRoot, resourceType);
            var manifestPatterns = manifestEntries.Where(IsOverridePattern).ToList();
            var enabled = manifestPatterns.Count > 0 ? ApplyPatterns(allFiles, manifestPatterns, packageRoot) : [.. allFiles];
            return [.. enabled];
        }
        var conventionDir = Path.Join(packageRoot, resourceType);
        return Path.Exists(conventionDir) ? CollectResourceFiles(conventionDir, resourceType) : [];
    }

    private void AddManifestEntries(ImmutableArray<string>? entries, string root, string resourceType, OrderedDictionary<string, (PiPathMetadata, bool)> target, PiPathMetadata metadata)
    {
        if (entries is not { } list) return;
        var allFiles = CollectFilesFromManifestEntries(list, root, resourceType);
        var enabled = ApplyPatterns(allFiles, [.. list.Where(IsOverridePattern)], root);
        foreach (var file in allFiles) if (enabled.Contains(file)) AddResource(target, file, metadata, true);
    }

    private static List<string> CollectFilesFromManifestEntries(ImmutableArray<string> entries, string root, string resourceType)
    {
        var resolved = entries.Where(entry => !IsOverridePattern(entry))
            .SelectMany(entry => HasGlobPattern(entry) ? ExpandPackageGlob(entry, root) : [NodeResolve(root, entry)]).ToList();
        return CollectFilesFromPaths(resolved, resourceType);
    }

    private void ResolveLocalEntries(List<string> entries, string resourceType, OrderedDictionary<string, (PiPathMetadata, bool)> target, PiPathMetadata metadata, string baseDir)
    {
        if (entries.Count == 0) return;
        var plain = entries.Where(entry => !IsPattern(entry)).ToList();
        var patterns = entries.Where(IsPattern).ToList();
        if (resourceType == "extensions")
        {
            var builtinPatterns = patterns.Where(IsBuiltinPattern).ToList();
            patterns = patterns.Where(pattern => !IsBuiltinPattern(pattern)).ToList();
            var builtins = plain.Where(entry => entry.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal)).ToList();
            if (builtins.Count > 0)
            {
                var enabledBuiltins = ApplyBuiltinPatterns(builtins, builtinPatterns);
                var projectOverrides = metadata.Scope == "user" ? OverridePatterns(StringsOf(settings.Project, "extensions")) : null;
                foreach (var builtin in builtins)
                {
                    var builtinEnabled = enabledBuiltins.Contains(builtin);
                    var scope = metadata.Scope;
                    if (projectOverrides is { Count: > 0 } && TryApplyBuiltinOverrides(builtin, projectOverrides, out var projectEnabled))
                    {
                        builtinEnabled = projectEnabled;
                        scope = "project";
                    }
                    AddResource(target, builtin, new() { Source = "builtin", Scope = scope, Origin = "top-level" }, builtinEnabled);
                }
                plain = plain.Where(entry => !entry.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal)).ToList();
            }
        }
        var allFiles = CollectFilesFromPaths([.. plain.Select(entry => ResolvePathFromBase(entry, baseDir))], resourceType);
        var enabled = ApplyPatterns(allFiles, patterns, baseDir);
        foreach (var file in allFiles) AddResource(target, file, metadata, enabled.Contains(file));
    }

    private void AddAutoDiscoveredResources(Accumulator accumulator, string globalBaseDir, string projectBaseDir)
    {
        var userMetadata = new PiPathMetadata { Source = "auto", Scope = "user", Origin = "top-level", BaseDir = globalBaseDir };
        var projectMetadata = new PiPathMetadata { Source = "auto", Scope = "project", Origin = "top-level", BaseDir = projectBaseDir };
        var userAgentsSkillsDir = Path.Join(home, ".agents", "skills");
        var projectTrusted = settings.ProjectTrusted;
        var projectAgentsSkillDirs = projectTrusted
            ? CollectAncestorAgentsSkillDirs(cwd).Where(directory => !PiPaths.PathsEqual(directory, userAgentsSkillsDir)).ToList() : [];

        void Add(string resourceType, List<string> paths, PiPathMetadata metadata, List<string> overrides, string baseDir)
        {
            var target = accumulator.Maps[resourceType];
            foreach (var path in paths) AddResource(target, path, metadata, IsEnabledByOverrides(path, overrides, baseDir));
        }
        List<string> UserOverrides(string type) => StringsOf(settings.Global, type);
        List<string> ProjectOverrides(string type) => StringsOf(settings.Project, type);

        if (projectTrusted)
        {
            Add("extensions", CollectAutoExtensionEntries(Path.Join(projectBaseDir, "extensions")), projectMetadata, ProjectOverrides("extensions"), projectBaseDir);
            Add("skills", CollectSkillEntries(Path.Join(projectBaseDir, "skills"), "pi"), projectMetadata, ProjectOverrides("skills"), projectBaseDir);
        }
        foreach (var agentsSkillsDir in projectAgentsSkillDirs)
        {
            var agentsBaseDir = Path.GetDirectoryName(agentsSkillsDir)!;
            Add("skills", CollectSkillEntries(agentsSkillsDir, "agents"), projectMetadata.With(agentsBaseDir), ProjectOverrides("skills"), agentsBaseDir);
        }
        if (projectTrusted)
        {
            Add("prompts", CollectTopLevelEntries(Path.Join(projectBaseDir, "prompts"), ".md"), projectMetadata, ProjectOverrides("prompts"), projectBaseDir);
            Add("themes", CollectTopLevelEntries(Path.Join(projectBaseDir, "themes"), ".json"), projectMetadata, ProjectOverrides("themes"), projectBaseDir);
        }
        Add("extensions", CollectAutoExtensionEntries(Path.Join(globalBaseDir, "extensions")), userMetadata, UserOverrides("extensions"), globalBaseDir);
        Add("skills", CollectSkillEntries(Path.Join(globalBaseDir, "skills"), "pi"), userMetadata, UserOverrides("skills"), globalBaseDir);
        var userAgentsBaseDir = Path.GetDirectoryName(userAgentsSkillsDir)!;
        Add("skills", CollectSkillEntries(userAgentsSkillsDir, "agents"), userMetadata.With(userAgentsBaseDir), UserOverrides("skills"), userAgentsBaseDir);
        Add("prompts", CollectTopLevelEntries(Path.Join(globalBaseDir, "prompts"), ".md"), userMetadata, UserOverrides("prompts"), globalBaseDir);
        Add("themes", CollectTopLevelEntries(Path.Join(globalBaseDir, "themes"), ".json"), userMetadata, UserOverrides("themes"), globalBaseDir);
    }

    private static List<string> CollectFilesFromPaths(List<string> paths, string resourceType)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (!Path.Exists(path)) continue;
            try
            {
                if (IsFile(path)) files.Add(path);
                else if (Directory.Exists(path)) files.AddRange(CollectResourceFiles(path, resourceType));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }

    private static void AddResource(OrderedDictionary<string, (PiPathMetadata, bool)> map, string path, PiPathMetadata metadata, bool enabled)
    {
        if (path.Length == 0) return;
        map.TryAdd(path, (metadata, enabled));
    }

    /// <summary>Source resourcePrecedenceRank: project settings entries, project auto-discovery, user settings entries, user
    /// auto-discovery, packages, then built-in extensions.</summary>
    internal static int ResourcePrecedenceRank(PiPathMetadata metadata)
    {
        if (metadata.Source == "builtin") return 5;
        if (metadata.Origin == "package") return 4;
        return (metadata.Scope == "project" ? 0 : 2) + (metadata.Source == "local" ? 0 : 1);
    }

    private static PiResolvedPaths ToResolvedPaths(Accumulator accumulator)
    {
        ImmutableArray<PiResolvedResource> Map(string type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return [.. accumulator.Maps[type].Select(pair => new PiResolvedResource(pair.Key, pair.Value.Enabled, pair.Value.Metadata))
                .OrderBy(resource => ResourcePrecedenceRank(resource.Metadata))
                .Where(resource => seen.Add(PiSharp.Cli.Extensions.Pi.PiSourceInfo.ResourcePathKey(resource.Path)))];
        }
        return new(Map("extensions"), Map("skills"), Map("prompts"), Map("themes"));
    }

    // ---- module helpers ---------------------------------------------------------------------------------------------------------

    private static List<string> StringsOf(JsonObject layer, string name) =>
        layer[name] is JsonArray array ? [.. array.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var text) ? text : null).OfType<string>()] : [];

    private static string ToPosixPath(string path) => Path.DirectorySeparatorChar == '\\' ? path.Replace('\\', '/') : path;
    private static string Basename(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    /// <summary>Node path.relative: "" for the same path.</summary>
    internal static string Relative(string from, string to)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(from), Path.GetFullPath(to));
        return relative == "." ? "" : relative;
    }

    /// <summary>Node path.resolve(root, entry).</summary>
    private static string NodeResolve(string root, string entry) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, entry))) is var full && full.Length > 0 ? full : root;

    private static bool IsFile(string path) => File.Exists(path) && !Directory.Exists(path);
    private static void DeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }

    /// <summary>rmSync(path, { recursive: true, force: true }): read-only files (git objects) are made writable first.</summary>
    internal static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) { DeleteFile(path); return; }
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null) { info.Delete(); return; }
        foreach (var file in info.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly)) file.Attributes &= ~FileAttributes.ReadOnly;
        info.Delete(true);
    }

    private static string? PrefixIgnorePattern(string line, string prefix)
    {
        var trimmed = PiArgs.JsTrim(line);
        if (trimmed.Length == 0) return null;
        if (trimmed.StartsWith('#') && !trimmed.StartsWith("\\#", StringComparison.Ordinal)) return null;
        var pattern = line; var negated = false;
        if (pattern.StartsWith('!')) { negated = true; pattern = pattern[1..]; }
        else if (pattern.StartsWith("\\!", StringComparison.Ordinal)) pattern = pattern[1..];
        if (pattern.StartsWith('/')) pattern = pattern[1..];
        var prefixed = prefix.Length > 0 ? prefix + pattern : pattern;
        return negated ? "!" + prefixed : prefixed;
    }

    private static void AddIgnoreRules(PiIgnore ignore, string directory, string rootDir)
    {
        var relativeDir = Relative(rootDir, directory);
        var prefix = relativeDir.Length > 0 ? ToPosixPath(relativeDir) + "/" : "";
        foreach (var name in IgnoreFileNames)
        {
            var ignorePath = Path.Join(directory, name);
            if (!File.Exists(ignorePath)) continue;
            try
            {
                var patterns = System.Text.RegularExpressions.Regex.Split(File.ReadAllText(ignorePath), "\r?\n")
                    .Select(line => PrefixIgnorePattern(line, prefix)).OfType<string>().ToList();
                if (patterns.Count > 0) ignore.Add(patterns);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsPattern(string value) => value.StartsWith('!') || value.StartsWith('+') || value.StartsWith('-') || value.Contains('*') || value.Contains('?');
    private static bool IsOverridePattern(string value) => value.StartsWith('!') || value.StartsWith('+') || value.StartsWith('-');
    private static bool HasGlobPattern(string value) => value.Contains('*') || value.Contains('?');

    /// <summary>Source expandPackageGlob: glob entries list visible paths only (no dot segments), sorted.</summary>
    private static List<string> ExpandPackageGlob(string pattern, string root) =>
        [.. PiGlobExpand.Expand(pattern, root).Select(match => NodeResolve(root, match))
            .Where(path => Relative(root, path).Split(Path.DirectorySeparatorChar).All(segment => segment == ".." || !segment.StartsWith('.')))
            .Order(StringComparer.Ordinal)];

    private sealed record Entry(string Name, string FullPath, bool IsDirectory, bool IsFile);

    /// <summary>readdirSync(withFileTypes) with symbolic links stat-ed (broken links skipped).</summary>
    private static List<Entry> ReadDirectory(string directory)
    {
        var result = new List<Entry>();
        foreach (var info in PiSharp.Contracts.Compatibility.NodeDirectoryOrder.Order(new DirectoryInfo(directory).EnumerateFileSystemInfos()))
        {
            if (info.LinkTarget is null) { result.Add(new(info.Name, info.FullName, info is DirectoryInfo, info is FileInfo)); continue; }
            var isDirectory = Directory.Exists(info.FullName); var isFile = !isDirectory && File.Exists(info.FullName);
            if (isFile && info is FileInfo file) { try { _ = file.ResolveLinkTarget(true); } catch (IOException) { continue; } }
            if (!isDirectory && !isFile) continue;
            result.Add(new(info.Name, info.FullName, isDirectory, isFile));
        }
        return result;
    }

    private static List<string> CollectFiles(string directory, string extension, PiIgnore? ignore = null, string? rootDir = null)
    {
        var files = new List<string>();
        if (!Path.Exists(directory)) return files;
        var root = rootDir ?? directory;
        ignore ??= new PiIgnore();
        AddIgnoreRules(ignore, directory, root);
        try
        {
            foreach (var entry in ReadDirectory(directory))
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                var relPath = ToPosixPath(Relative(root, entry.FullPath));
                if (ignore.Ignores(entry.IsDirectory ? relPath + "/" : relPath)) continue;
                if (entry.IsDirectory) files.AddRange(CollectFiles(entry.FullPath, extension, ignore, root));
                else if (entry.IsFile && entry.Name.EndsWith(extension, StringComparison.Ordinal)) files.Add(entry.FullPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>Source collectSkillEntries: a directory with SKILL.md is one skill (no recursion); otherwise root <c>.md</c> files (pi
    /// mode) or nested <c>.md</c> files (agents mode) and subdirectories.</summary>
    internal static List<string> CollectSkillEntries(string directory, string mode, PiIgnore? ignore = null, string? rootDir = null)
    {
        var entries = new List<string>();
        if (!Path.Exists(directory)) return entries;
        var root = rootDir ?? directory;
        ignore ??= new PiIgnore();
        AddIgnoreRules(ignore, directory, root);
        try
        {
            var items = ReadDirectory(directory);
            foreach (var entry in items)
            {
                if (entry.Name != "SKILL.md") continue;
                if (entry.IsFile && !ignore.Ignores(ToPosixPath(Relative(root, entry.FullPath)))) { entries.Add(entry.FullPath); return entries; }
            }
            foreach (var entry in items)
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                var relPath = ToPosixPath(Relative(root, entry.FullPath));
                if (entry.IsFile && entry.Name.EndsWith(".md", StringComparison.Ordinal) && !ignore.Ignores(relPath) &&
                    (mode == "pi" && directory == root || mode == "agents" && directory != root)) { entries.Add(entry.FullPath); continue; }
                if (!entry.IsDirectory || ignore.Ignores(relPath + "/")) continue;
                entries.AddRange(CollectSkillEntries(entry.FullPath, mode, ignore, root));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return entries;
    }

    /// <summary>Source collectAncestorAgentsSkillDirs.</summary>
    private static List<string> CollectAncestorAgentsSkillDirs(string startDir) => [.. PiResources.AncestorAgentsSkillDirs(startDir)];

    /// <summary>Source collectAutoPromptEntries / collectAutoThemeEntries: top-level files of the extension, ignore files honoured.</summary>
    private static List<string> CollectTopLevelEntries(string directory, string extension)
    {
        var entries = new List<string>();
        if (!Path.Exists(directory)) return entries;
        var ignore = new PiIgnore();
        AddIgnoreRules(ignore, directory, directory);
        try
        {
            foreach (var entry in ReadDirectory(directory))
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                if (ignore.Ignores(ToPosixPath(Relative(directory, entry.FullPath)))) continue;
                if (entry.IsFile && entry.Name.EndsWith(extension, StringComparison.Ordinal)) entries.Add(entry.FullPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return entries;
    }

    /// <summary>Source resolveExtensionEntries: package.json <c>pi.extensions</c> entries that exist, else index.ts, else index.js.</summary>
    internal static List<string>? ResolveExtensionEntries(string directory)
    {
        var packageJson = Path.Join(directory, "package.json");
        if (File.Exists(packageJson) && PiManifest.Read(packageJson)?.Extensions is { Length: > 0 } extensions)
        {
            var entries = extensions.Select(path => NodeResolve(directory, path)).Where(Path.Exists).ToList();
            if (entries.Count > 0) return entries;
        }
        var indexTs = Path.Join(directory, "index.ts"); var indexJs = Path.Join(directory, "index.js");
        if (Path.Exists(indexTs)) return [indexTs];
        if (Path.Exists(indexJs)) return [indexJs];
        // PiSharp: a native C# extension's directory holds its pisharp-extension.json manifest (owner decision 10).
        var native = Path.Join(directory, PiSharp.Cli.Extensions.Pi.PiNativeExtension.ManifestName);
        if (File.Exists(native)) return [native];
        return null;
    }

    /// <summary>Source collectAutoExtensionEntries: the directory's own entries, else top-level <c>.ts</c>/<c>.js</c> files and one level of
    /// extension directories.</summary>
    internal static List<string> CollectAutoExtensionEntries(string directory)
    {
        var entries = new List<string>();
        if (!Path.Exists(directory)) return entries;
        if (ResolveExtensionEntries(directory) is { } rootEntries) return rootEntries;
        var ignore = new PiIgnore();
        AddIgnoreRules(ignore, directory, directory);
        try
        {
            foreach (var entry in ReadDirectory(directory))
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                var relPath = ToPosixPath(Relative(directory, entry.FullPath));
                if (ignore.Ignores(entry.IsDirectory ? relPath + "/" : relPath)) continue;
                if (entry.IsFile && (entry.Name.EndsWith(".ts", StringComparison.Ordinal) || entry.Name.EndsWith(".js", StringComparison.Ordinal))) entries.Add(entry.FullPath);
                else if (entry.IsDirectory && ResolveExtensionEntries(entry.FullPath) is { } resolved) entries.AddRange(resolved);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return entries;
    }

    private static List<string> CollectResourceFiles(string directory, string resourceType) => resourceType switch
    {
        "skills" => CollectSkillEntries(directory, "pi"),
        "extensions" => CollectAutoExtensionEntries(directory),
        "themes" => CollectFiles(directory, ".json"),
        _ => CollectFiles(directory, ".md")
    };

    private bool MatchesAnyPattern(string filePath, IEnumerable<string> patterns, string baseDir)
    {
        var rel = ToPosixPath(Relative(baseDir, filePath));
        var name = Path.GetFileName(filePath);
        var filePathPosix = ToPosixPath(filePath);
        var isSkillFile = name == "SKILL.md";
        var parentDir = isSkillFile ? Path.GetDirectoryName(filePath)! : null;
        return patterns.Any(pattern =>
        {
            // Pi 1.2.0 matchesAnyPattern: ~ is expanded (normalizePath) before matching.
            var normalized = ToPosixPath(PiPaths.NormalizePath(pattern, home));
            if (PiMinimatch.Match(rel, normalized) || PiMinimatch.Match(name, normalized) || PiMinimatch.Match(filePathPosix, normalized)) return true;
            if (parentDir is null) return false;
            return PiMinimatch.Match(ToPosixPath(Relative(baseDir, parentDir)), normalized) || PiMinimatch.Match(Path.GetFileName(parentDir), normalized) ||
                PiMinimatch.Match(ToPosixPath(parentDir), normalized);
        });
    }

    /// <summary>Source normalizeExactPattern: a leading <c>./</c> dropped and (Pi 1.2.0) <c>~</c> expanded.</summary>
    private string NormalizeExactPattern(string pattern) =>
        ToPosixPath(PiPaths.NormalizePath(pattern.StartsWith("./", StringComparison.Ordinal) || pattern.StartsWith(".\\", StringComparison.Ordinal) ? pattern[2..] : pattern, home));

    private bool MatchesAnyExactPattern(string filePath, IReadOnlyCollection<string> patterns, string baseDir)
    {
        if (patterns.Count == 0) return false;
        var rel = ToPosixPath(Relative(baseDir, filePath));
        var filePathPosix = ToPosixPath(filePath);
        var isSkillFile = Path.GetFileName(filePath) == "SKILL.md";
        var parentDir = isSkillFile ? Path.GetDirectoryName(filePath)! : null;
        return patterns.Any(pattern =>
        {
            var normalized = NormalizeExactPattern(pattern);
            if (normalized == rel || normalized == filePathPosix) return true;
            return parentDir is not null && (normalized == ToPosixPath(Relative(baseDir, parentDir)) || normalized == ToPosixPath(parentDir));
        });
    }

    private static List<string> OverridePatterns(IEnumerable<string> entries) => [.. entries.Where(IsOverridePattern)];

    private bool IsEnabledByOverrides(string filePath, List<string> patterns, string baseDir)
    {
        var overrides = OverridePatterns(patterns);
        var excludes = overrides.Where(pattern => pattern.StartsWith('!')).Select(pattern => pattern[1..]).ToList();
        var forceIncludes = overrides.Where(pattern => pattern.StartsWith('+')).Select(pattern => pattern[1..]).ToList();
        var forceExcludes = overrides.Where(pattern => pattern.StartsWith('-')).Select(pattern => pattern[1..]).ToList();
        var enabled = true;
        if (excludes.Count > 0 && MatchesAnyPattern(filePath, excludes, baseDir)) enabled = false;
        if (forceIncludes.Count > 0 && MatchesAnyExactPattern(filePath, forceIncludes, baseDir)) enabled = true;
        if (forceExcludes.Count > 0 && MatchesAnyExactPattern(filePath, forceExcludes, baseDir)) enabled = false;
        return enabled;
    }

    /// <summary>Source applyPatterns: includes (or everything), minus <c>!</c> excludes, plus exact <c>+</c> force-includes, minus exact
    /// <c>-</c> force-excludes.</summary>
    private HashSet<string> ApplyPatterns(List<string> allPaths, IEnumerable<string> patterns, string baseDir)
    {
        List<string> includes = [], excludes = [], forceIncludes = [], forceExcludes = [];
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('+')) forceIncludes.Add(pattern[1..]);
            else if (pattern.StartsWith('-')) forceExcludes.Add(pattern[1..]);
            else if (pattern.StartsWith('!')) excludes.Add(pattern[1..]);
            else includes.Add(pattern);
        }
        var result = includes.Count == 0 ? [.. allPaths] : allPaths.Where(path => MatchesAnyPattern(path, includes, baseDir)).ToList();
        if (excludes.Count > 0) result = [.. result.Where(path => !MatchesAnyPattern(path, excludes, baseDir))];
        if (forceIncludes.Count > 0)
            foreach (var path in allPaths)
                if (!result.Contains(path) && MatchesAnyExactPattern(path, forceIncludes, baseDir)) result.Add(path);
        if (forceExcludes.Count > 0) result = [.. result.Where(path => !MatchesAnyExactPattern(path, forceExcludes, baseDir))];
        return new HashSet<string>(result, StringComparer.Ordinal);
    }

    private Dictionary<string, bool> ApplyAutoloadDisabledPatterns(List<string> allPaths, IEnumerable<string> patterns, string baseDir)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            var marked = pattern.StartsWith('+') || pattern.StartsWith('-') || pattern.StartsWith('!');
            var target = marked ? pattern[1..] : pattern;
            var enabled = !pattern.StartsWith('-') && !pattern.StartsWith('!');
            var exact = pattern.StartsWith('+') || pattern.StartsWith('-');
            foreach (var path in allPaths)
                if (exact ? MatchesAnyExactPattern(path, [target], baseDir) : MatchesAnyPattern(path, [target], baseDir)) result[path] = enabled;
        }
        return result;
    }

    private static bool IsBuiltinPattern(string pattern)
    {
        var target = pattern.Length > 0 && pattern[0] is '+' or '-' or '!' ? pattern[1..] : pattern;
        return target.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal);
    }

    private static HashSet<string> ApplyBuiltinPatterns(List<string> builtins, List<string> patterns)
    {
        List<string> includes = [], excludes = [], forceIncludes = [], forceExcludes = [];
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('+')) { var t = pattern[1..]; if (t.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal)) forceIncludes.Add(t); }
            else if (pattern.StartsWith('-')) { var t = pattern[1..]; if (t.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal)) forceExcludes.Add(t); }
            else if (pattern.StartsWith('!')) { var t = pattern[1..]; if (t.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal)) excludes.Add(t); }
            else if (pattern.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal)) includes.Add(pattern);
        }
        var result = includes.Count == 0 ? [.. builtins] : builtins.Where(b => includes.Any(p => PiMinimatch.Match(b, p))).ToList();
        if (excludes.Count > 0) result = [.. result.Where(b => !excludes.Any(p => PiMinimatch.Match(b, p)))];
        if (forceIncludes.Count > 0)
            foreach (var b in builtins)
                if (!result.Contains(b) && forceIncludes.Contains(b, StringComparer.Ordinal)) result.Add(b);
        if (forceExcludes.Count > 0) result = [.. result.Where(b => !forceExcludes.Contains(b, StringComparer.Ordinal))];
        return new HashSet<string>(result, StringComparer.Ordinal);
    }

    private static bool TryApplyBuiltinOverrides(string builtin, List<string> overrides, out bool enabled)
    {
        var matched = false;
        enabled = true;
        foreach (var pattern in overrides)
        {
            var marked = pattern.StartsWith('+') || pattern.StartsWith('-') || pattern.StartsWith('!');
            var target = marked ? pattern[1..] : pattern;
            if (!target.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal)) continue;
            var exact = pattern.StartsWith('+') || pattern.StartsWith('-');
            if (exact ? string.Equals(builtin, target, StringComparison.Ordinal) : PiMinimatch.Match(builtin, target))
            {
                matched = true;
                enabled = !pattern.StartsWith('-') && !pattern.StartsWith('!');
            }
        }
        return matched;
    }
}
