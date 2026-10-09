// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/package-manager.ts (DefaultPackageManager and
// its module helpers: resourcePrecedenceRank, getExtensionTempFolder, prefixIgnorePattern, addIgnoreRules, collectFiles,
// collectSkillEntries, collectAncestorAgentsSkillDirs, collectAutoPromptEntries, collectAutoThemeEntries, resolveExtensionEntries,
// collectAutoExtensionEntries, collectResourceFiles, matchesAnyPattern, matchesAnyExactPattern, isEnabledByOverrides,
// applyPatterns, applyAutoloadDisabledPatterns, expandPackageGlob).
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Packages;

/// <summary>Source DefaultPackageManager: resolves extensions, skills, prompt templates and themes from settings packages, settings
/// resource entries and the auto-discovery folders; installs, removes and updates npm and git packages through the user's
/// <c>npm</c> (or <c>npmCommand</c>) and <c>git</c>; edits the <c>packages</c> settings. Project scope requires a trusted project.</summary>
internal sealed class PiPackageManager
{
    internal const string BuiltinPathPrefix = "builtin:";
    private const int NetworkTimeoutMs = 10000;
    private const int UpdateCheckConcurrency = 4;
    private const int GitUpdateConcurrency = 4;
    internal static readonly string[] ResourceTypes = ["extensions", "skills", "prompts", "themes"];
    private static readonly string[] IgnoreFileNames = [".gitignore", ".ignore", ".fdignore"];

    private readonly string cwd;
    private readonly string agentDir;
    private readonly string home;
    private readonly PiSettings settings;
    private readonly Func<string, string?> environment;
    private readonly PiPackageProcesses processes;
    private readonly ImmutableArray<string> builtinExtensions;
    private string? globalNpmRoot;
    private string? globalNpmRootCommandKey;
    private Action<PiPackageProgressEvent>? progressCallback;

    /// <summary>Source new DefaultPackageManager({ cwd, agentDir, settingsManager, builtinExtensions }). <paramref name="settings"/>
    /// carries the project trust (its project layer is empty when untrusted); <paramref name="environment"/> answers
    /// <c>PI_OFFLINE</c>; <paramref name="home"/> expands <c>~</c> and locates <c>~/.agents/skills</c>.</summary>
    internal PiPackageManager(string cwd, string agentDir, string home, PiSettings settings, Func<string, string?> environment,
        PiPackageProcesses? processes = null, IEnumerable<string>? builtinExtensions = null)
    {
        this.home = home;
        this.cwd = PiPaths.ResolvePath(cwd, Directory.GetCurrentDirectory(), home);
        this.agentDir = PiPaths.ResolvePath(agentDir, Directory.GetCurrentDirectory(), home);
        this.settings = settings;
        this.environment = environment;
        this.processes = processes ?? new PiPackageProcesses();
        this.builtinExtensions = [.. builtinExtensions ?? []];
    }

    internal PiSettings Settings => settings;
    internal bool ProjectTrusted => settings.ProjectTrusted;

    /// <summary>Source setProgressCallback.</summary>
    internal void SetProgressCallback(Action<PiPackageProgressEvent>? callback) => progressCallback = callback;

    private bool OfflineModeEnabled => environment("PI_OFFLINE") is { Length: > 0 } value &&
        (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    // ---- settings -------------------------------------------------------------------------------------------------------------

    private List<PiPackageEntry> Packages(string scope) => PiPackageEntry.List(scope == "project" ? settings.Project : settings.Global);
    private JsonObject Layer(string scope) => scope == "project" ? settings.Project : settings.Global;

    private void SetPackages(string scope, IEnumerable<JsonNode> packages) =>
        settings.SetField(scope == "project" ? "project" : "global", "packages", new JsonArray([.. packages.Select(node => node.DeepClone())]));

    /// <summary>Source addSourceToSettings: false when the same source (same normalized form) is already present; a matching entry with
    /// another ref or path form is replaced in place (object entries keep their filters).</summary>
    internal bool AddSourceToSettings(string source, bool local = false)
    {
        var scope = local ? "project" : "user";
        var current = Packages(scope);
        var normalized = NormalizePackageSourceForSettings(source, scope);
        var index = current.FindIndex(existing => PackageSourcesMatch(existing, source, scope));
        if (index != -1)
        {
            var existing = current[index];
            if (existing.Source == normalized) return false;
            var next = current.Select(entry => entry.Node).ToList();
            if (existing.Node is JsonObject filter)
            {
                var replaced = (JsonObject)filter.DeepClone();
                replaced["source"] = normalized;
                next[index] = replaced;
            }
            else next[index] = JsonValue.Create(normalized);
            SetPackages(scope, next);
            return true;
        }
        SetPackages(scope, [.. current.Select(entry => entry.Node), JsonValue.Create(normalized)]);
        return true;
    }

    /// <summary>Source removeSourceFromSettings.</summary>
    internal bool RemoveSourceFromSettings(string source, bool local = false)
    {
        var scope = local ? "project" : "user";
        var current = Packages(scope);
        var next = current.Where(existing => !PackageSourcesMatch(existing, source, scope)).ToList();
        if (next.Count == current.Count) return false;
        SetPackages(scope, next.Select(entry => entry.Node));
        return true;
    }

    /// <summary>Source getInstalledPath.</summary>
    internal string? GetInstalledPath(string source, string scope)
    {
        var parsed = PiPackageSource.Parse(source);
        var path = parsed switch
        {
            PiNpmSource npm => GetNpmInstallPath(npm, scope),
            PiGitSource git => GetGitInstallPath(git, scope),
            PiLocalSource localSource => ResolvePathFromBase(localSource.Path, GetBaseDirForScope(scope)),
            _ => null
        };
        return path is not null && Path.Exists(path) ? path : null;
    }

    private void EmitProgress(PiPackageProgressEvent progress) => progressCallback?.Invoke(progress);

    private async Task WithProgress(string action, string source, string message, Func<Task> operation)
    {
        EmitProgress(new("start", action, source, message));
        try
        {
            await operation().ConfigureAwait(false);
            EmitProgress(new("complete", action, source));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            EmitProgress(new("error", action, source, error.Message));
            throw;
        }
    }

    // ---- resolve ---------------------------------------------------------------------------------------------------------------

    private sealed class Accumulator
    {
        internal readonly Dictionary<string, OrderedDictionary<string, (PiPathMetadata Metadata, bool Enabled)>> Maps = ResourceTypes.ToDictionary(
            type => type, _ => new OrderedDictionary<string, (PiPathMetadata, bool)>(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>Source resolve: settings packages (project first, deduplicated by identity), settings resource entries (project, then
    /// user), auto-discovered resources and built-in extensions, ranked by precedence and deduplicated by canonical path. Missing npm
    /// and git packages are installed (or not, per <paramref name="onMissing"/>; never when offline).</summary>
    internal async Task<PiResolvedPaths> ResolveAsync(Func<string, Task<PiMissingSourceAction>>? onMissing = null, CancellationToken cancellationToken = default)
    {
        var accumulator = new Accumulator();
        var all = new List<(PiPackageEntry Package, string Scope)>();
        all.AddRange(Packages("project").Select(package => (package, "project")));
        all.AddRange(Packages("user").Select(package => (package, "user")));
        var packageSources = DedupePackages(all);
        await ResolvePackageSources(packageSources, accumulator, onMissing, cancellationToken).ConfigureAwait(false);

        var globalBaseDir = agentDir;
        var projectBaseDir = Path.Join(cwd, PiConfig.ConfigDirName);
        foreach (var resourceType in ResourceTypes)
        {
            var target = accumulator.Maps[resourceType];
            ResolveLocalEntries(StringsOf(settings.Project, resourceType), resourceType, target, new() { Source = "local", Scope = "project", Origin = "top-level" }, projectBaseDir);
            ResolveLocalEntries(StringsOf(settings.Global, resourceType), resourceType, target, new() { Source = "local", Scope = "user", Origin = "top-level" }, globalBaseDir);
        }
        AddAutoDiscoveredResources(accumulator, globalBaseDir, projectBaseDir);

        // Built-in extensions are enabled unless the user `extensions` setting excludes them; a matching project override wins.
        foreach (var name in builtinExtensions)
        {
            var path = BuiltinPathPrefix + name;
            var projectEnabled = ApplyAutoloadDisabledPatterns([path], OverridePatterns(StringsOf(settings.Project, "extensions")), projectBaseDir)
                .TryGetValue(path, out var enabled) ? enabled : (bool?)null;
            AddResource(accumulator.Maps["extensions"], path, new() { Source = "builtin", Scope = projectEnabled is null ? "user" : "project", Origin = "top-level" },
                projectEnabled ?? IsEnabledByOverrides(path, StringsOf(settings.Global, "extensions"), globalBaseDir));
        }
        return ToResolvedPaths(accumulator);
    }

    /// <summary>Source resolveExtensionSources (<c>-e</c>): <c>builtin:</c> names and package sources resolved in the temporary, project
    /// or user scope; missing npm and git sources are installed (temporary installs under <c>&lt;agentDir&gt;/tmp/extensions</c>).</summary>
    internal async Task<PiResolvedPaths> ResolveExtensionSourcesAsync(IEnumerable<string> sources, bool local = false, bool temporary = false,
        CancellationToken cancellationToken = default)
    {
        var accumulator = new Accumulator();
        var scope = temporary ? "temporary" : local ? "project" : "user";
        var list = sources.ToList();
        foreach (var source in list.Where(source => source.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal)))
            AddResource(accumulator.Maps["extensions"], source, new() { Source = "builtin", Scope = scope, Origin = "top-level" }, true);
        var packageSources = list.Where(source => !source.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal))
            .Select(source => (new PiPackageEntry(source, JsonValue.Create(source)), scope)).ToList();
        await ResolvePackageSources(packageSources, accumulator, null, cancellationToken).ConfigureAwait(false);
        return ToResolvedPaths(accumulator);
    }

    /// <summary>Source listConfiguredPackages: user entries, then project entries.</summary>
    internal ImmutableArray<PiConfiguredPackage> ListConfiguredPackages()
    {
        var result = ImmutableArray.CreateBuilder<PiConfiguredPackage>();
        foreach (var package in Packages("user")) result.Add(new(package.Source, "user", package.IsFilter, GetInstalledPath(package.Source, "user")));
        foreach (var package in Packages("project")) result.Add(new(package.Source, "project", package.IsFilter, GetInstalledPath(package.Source, "project")));
        return result.ToImmutable();
    }

    // ---- install / remove / update -------------------------------------------------------------------------------------------

    /// <summary>Source install.</summary>
    internal async Task InstallAsync(string source, bool local = false, CancellationToken cancellationToken = default)
    {
        var parsed = PiPackageSource.Parse(source);
        var scope = local ? "project" : "user";
        AssertProjectTrustedForScope(scope);
        await WithProgress("install", source, $"Installing {source}...", async () =>
        {
            switch (parsed)
            {
                case PiNpmSource npm: await InstallNpm(npm, scope, false, cancellationToken).ConfigureAwait(false); return;
                case PiGitSource git: await InstallGit(git, scope, cancellationToken).ConfigureAwait(false); return;
                case PiLocalSource localSource:
                    var resolved = ResolvePath(localSource.Path);
                    if (!Path.Exists(resolved)) throw new PiPackageException($"Path does not exist: {resolved}");
                    return;
            }
            throw new PiPackageException($"Unsupported install source: {source}");
        }).ConfigureAwait(false);
    }

    /// <summary>Source installAndPersist.</summary>
    internal async Task InstallAndPersistAsync(string source, bool local = false, CancellationToken cancellationToken = default)
    {
        await InstallAsync(source, local, cancellationToken).ConfigureAwait(false);
        AddSourceToSettings(source, local);
    }

    /// <summary>Source remove.</summary>
    internal async Task RemoveAsync(string source, bool local = false, CancellationToken cancellationToken = default)
    {
        var parsed = PiPackageSource.Parse(source);
        var scope = local ? "project" : "user";
        AssertProjectTrustedForScope(scope);
        await WithProgress("remove", source, $"Removing {source}...", async () =>
        {
            switch (parsed)
            {
                case PiNpmSource npm: await UninstallNpm(npm, scope, cancellationToken).ConfigureAwait(false); return;
                case PiGitSource git: RemoveGit(git, scope); return;
                case PiLocalSource: return;
            }
            throw new PiPackageException($"Unsupported remove source: {source}");
        }).ConfigureAwait(false);
    }

    /// <summary>Source removeAndPersist: whether a settings entry was removed.</summary>
    internal async Task<bool> RemoveAndPersistAsync(string source, bool local = false, CancellationToken cancellationToken = default)
    {
        await RemoveAsync(source, local, cancellationToken).ConfigureAwait(false);
        return RemoveSourceFromSettings(source, local);
    }

    /// <summary>Source update: every configured package, or those matching <paramref name="source"/>'s identity (an unknown source fails
    /// with a suggestion). Pinned npm versions stay; git sources are fetched and reset; nothing happens offline.</summary>
    internal async Task UpdateAsync(string? source = null, CancellationToken cancellationToken = default)
    {
        var identity = source is null ? null : GetPackageIdentity(source);
        var matched = false;
        var updateSources = new List<(string Source, string Scope)>();
        foreach (var (scope, packages) in new[] { ("user", Packages("user")), ("project", Packages("project")) })
            foreach (var package in packages)
            {
                if (identity is not null && GetPackageIdentity(package.Source, scope) != identity) continue;
                matched = true;
                updateSources.Add((package.Source, scope));
            }
        if (source is not null && !matched)
            throw new PiPackageException(BuildNoMatchingPackageMessage(source, [.. Packages("user"), .. Packages("project")]));
        await UpdateConfiguredSources(updateSources, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateConfiguredSources(List<(string Source, string Scope)> sources, CancellationToken cancellationToken)
    {
        if (OfflineModeEnabled || sources.Count == 0) return;
        var npmCandidates = new List<(string Source, string Scope, PiNpmSource Parsed)>();
        var gitCandidates = new List<(string Source, string Scope, PiGitSource Parsed)>();
        foreach (var (source, scope) in sources)
        {
            // Pinned npm versions are fixed; pinned git refs are checkout targets, reconciled when the configured ref changes.
            switch (PiPackageSource.Parse(source))
            {
                case PiNpmSource npm when !npm.Pinned: npmCandidates.Add((source, scope, npm)); break;
                case PiGitSource git: gitCandidates.Add((source, scope, git)); break;
            }
        }
        var checks = await RunWithConcurrency(npmCandidates.Select(entry => (Func<Task<bool>>)(() => ShouldUpdateNpmSource(entry.Parsed, entry.Scope, cancellationToken))).ToList(),
            UpdateCheckConcurrency).ConfigureAwait(false);
        var userUpdates = new List<(string Source, PiNpmSource Parsed)>(); var projectUpdates = new List<(string Source, PiNpmSource Parsed)>();
        for (var index = 0; index < npmCandidates.Count; index++)
        {
            if (!checks[index]) continue;
            (npmCandidates[index].Scope == "user" ? userUpdates : projectUpdates).Add((npmCandidates[index].Source, npmCandidates[index].Parsed));
        }
        var tasks = new List<Task>();
        if (userUpdates.Count > 0) tasks.Add(UpdateNpmBatch(userUpdates, "user", cancellationToken));
        if (projectUpdates.Count > 0) tasks.Add(UpdateNpmBatch(projectUpdates, "project", cancellationToken));
        if (gitCandidates.Count > 0)
            tasks.Add(RunWithConcurrency(gitCandidates.Select(entry => (Func<Task<bool>>)(async () =>
            {
                await WithProgress("update", entry.Source, $"Updating {entry.Source}...", () => UpdateGit(entry.Parsed, entry.Scope, cancellationToken)).ConfigureAwait(false);
                return true;
            })).ToList(), GitUpdateConcurrency));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<bool> ShouldUpdateNpmSource(PiNpmSource source, string scope, CancellationToken cancellationToken)
    {
        var installedPath = GetManagedNpmInstallPath(source, scope);
        var installedVersion = Path.Exists(installedPath) ? GetInstalledNpmVersion(installedPath) : null;
        if (installedVersion is null) return true;
        try
        {
            var target = await GetLatestNpmVersion(source.Version is not null ? source.Spec : source.Name, source.Range, cancellationToken).ConfigureAwait(false);
            return PiSemver.Gt(target, installedVersion);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return true; }
    }

    private async Task UpdateNpmBatch(List<(string Source, PiNpmSource Parsed)> sources, string scope, CancellationToken cancellationToken)
    {
        if (sources.Count == 0) return;
        var label = sources.Count == 1 ? sources[0].Source : $"{scope} npm packages";
        var message = sources.Count == 1 ? $"Updating {sources[0].Source}..." : $"Updating {scope} npm packages...";
        var specs = sources.Select(entry => entry.Parsed.Version is not null ? entry.Parsed.Spec : $"{entry.Parsed.Name}@latest").ToList();
        await WithProgress("update", label, message, async () =>
        {
            var installRoot = GetNpmInstallRoot(scope, false);
            EnsureNpmProject(installRoot);
            await RunNpmCommand(GetNpmInstallArgs(specs, installRoot), null, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Source checkForAvailableUpdates: unpinned installed npm packages with a newer version and git checkouts behind their
    /// remote; nothing offline.</summary>
    internal async Task<ImmutableArray<PiPackageUpdate>> CheckForAvailableUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (OfflineModeEnabled) return [];
        var all = new List<(PiPackageEntry Package, string Scope)>();
        all.AddRange(Packages("project").Select(package => (package, "project")));
        all.AddRange(Packages("user").Select(package => (package, "user")));
        var checks = DedupePackages(all).Where(entry => entry.Scope != "temporary").Select(entry => (Func<Task<PiPackageUpdate?>>)(async () =>
        {
            var source = entry.Package.Source;
            switch (PiPackageSource.Parse(source))
            {
                case PiNpmSource npm when !npm.Pinned:
                {
                    var installedPath = GetNpmInstallPath(npm, entry.Scope);
                    if (!Path.Exists(installedPath) || !await NpmHasAvailableUpdate(npm, installedPath, cancellationToken).ConfigureAwait(false)) return null;
                    return new(source, npm.Name, "npm", entry.Scope);
                }
                case PiGitSource git when !git.Pinned:
                {
                    var installedPath = GetGitInstallPath(git, entry.Scope);
                    if (!Path.Exists(installedPath) || !await GitHasAvailableUpdate(installedPath, cancellationToken).ConfigureAwait(false)) return null;
                    return new(source, $"{git.Host}/{git.Path}", "git", entry.Scope);
                }
                default: return null;
            }
        })).ToList();
        var results = await RunWithConcurrency(checks, UpdateCheckConcurrency).ConfigureAwait(false);
        return [.. results.OfType<PiPackageUpdate>()];
    }

    private async Task ResolvePackageSources(List<(PiPackageEntry Package, string Scope)> sources, Accumulator accumulator,
        Func<string, Task<PiMissingSourceAction>>? onMissing, CancellationToken cancellationToken)
    {
        foreach (var (package, scope) in sources)
        {
            var sourceText = package.Source;
            var filter = package.IsFilter ? package : null;
            var deltaBase = FindAutoloadDeltaBase(package, scope, sources);
            var resolvedSource = deltaBase?.Source ?? sourceText;
            var resolvedScope = deltaBase?.Scope ?? scope;
            var parsed = PiPackageSource.Parse(resolvedSource);
            var metadata = new PiPathMetadata { Source = sourceText, Scope = scope, Origin = "package" };
            if (parsed is PiLocalSource localSource)
            {
                ResolveLocalExtensionSource(localSource, accumulator, filter, metadata, GetBaseDirForScope(resolvedScope));
                continue;
            }
            async Task<bool> InstallMissing()
            {
                if (OfflineModeEnabled) return false;
                if (onMissing is not null)
                {
                    var action = await onMissing(resolvedSource).ConfigureAwait(false);
                    if (action == PiMissingSourceAction.Skip) return false;
                    if (action == PiMissingSourceAction.Error) throw new PiPackageException($"Missing source: {resolvedSource}");
                }
                await InstallParsedSource(parsed, resolvedScope, cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (parsed is PiNpmSource npm)
            {
                var installedPath = GetNpmInstallPath(npm, resolvedScope);
                if (!Path.Exists(installedPath) || !InstalledNpmMatchesConfiguredVersion(npm, installedPath))
                {
                    if (!await InstallMissing().ConfigureAwait(false)) continue;
                    installedPath = GetNpmInstallPath(npm, resolvedScope);
                }
                metadata.BaseDir = installedPath; metadata.PackageRoot = installedPath;
                CollectPackageResources(installedPath, accumulator, filter, metadata);
                continue;
            }
            if (parsed is PiGitSource git)
            {
                var installedPath = GetGitInstallPath(git, resolvedScope);
                if (!Path.Exists(installedPath))
                {
                    if (!await InstallMissing().ConfigureAwait(false)) continue;
                }
                else if (resolvedScope == "temporary" && !git.Pinned && !OfflineModeEnabled)
                    await RefreshTemporaryGitSource(git, resolvedSource, cancellationToken).ConfigureAwait(false);
                metadata.BaseDir = installedPath; metadata.PackageRoot = installedPath;
                CollectPackageResources(installedPath, accumulator, filter, metadata);
            }
        }
    }

    private (string Source, string Scope)? FindAutoloadDeltaBase(PiPackageEntry package, string scope, List<(PiPackageEntry Package, string Scope)> sources)
    {
        if (scope != "project" || !package.IsFilter || package.Autoload != false) return null;
        var identity = GetPackageIdentity(package.Source, scope);
        foreach (var entry in sources)
            if (entry.Scope == "user" && GetPackageIdentity(entry.Package.Source, "user") == identity) return (entry.Package.Source, "user");
        return null;
    }

    private void ResolveLocalExtensionSource(PiLocalSource source, Accumulator accumulator, PiPackageEntry? filter, PiPathMetadata metadata, string baseDir)
    {
        var resolved = ResolvePathFromBase(source.Path, baseDir);
        if (!Path.Exists(resolved)) return;
        try
        {
            if (IsFile(resolved))
            {
                metadata.BaseDir = Path.GetDirectoryName(resolved);
                AddResource(accumulator.Maps["extensions"], resolved, metadata, true);
                return;
            }
            if (Directory.Exists(resolved))
            {
                metadata.BaseDir = resolved; metadata.PackageRoot = resolved;
                if (!CollectPackageResources(resolved, accumulator, filter, metadata)) AddResource(accumulator.Maps["extensions"], resolved, metadata, true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private Task InstallParsedSource(PiPackageSource parsed, string scope, CancellationToken cancellationToken) => parsed switch
    {
        PiNpmSource npm => InstallNpm(npm, scope, scope == "temporary", cancellationToken),
        PiGitSource git => InstallGit(git, scope, cancellationToken),
        _ => Task.CompletedTask
    };

    // ---- identity ----------------------------------------------------------------------------------------------------------

    private string GetSourceMatchKeyForInput(string source) => PiPackageSource.Parse(source) switch
    {
        PiNpmSource npm => $"npm:{npm.Name}",
        PiGitSource git => $"git:{git.Host}/{git.Path}",
        PiLocalSource local => $"local:{ResolvePath(local.Path)}",
        _ => source
    };

    private string GetSourceMatchKeyForSettings(string source, string scope) => PiPackageSource.Parse(source) switch
    {
        PiNpmSource npm => $"npm:{npm.Name}",
        PiGitSource git => $"git:{git.Host}/{git.Path}",
        PiLocalSource local => $"local:{ResolvePathFromBase(local.Path, GetBaseDirForScope(scope))}",
        _ => source
    };

    private string BuildNoMatchingPackageMessage(string source, List<PiPackageEntry> configured)
    {
        var suggestion = FindSuggestedConfiguredSource(source, configured);
        return suggestion is null ? $"No matching package found for {source}" : $"No matching package found for {source}. Did you mean {suggestion}?";
    }

    private static string? FindSuggestedConfiguredSource(string source, List<PiPackageEntry> configured)
    {
        var trimmed = PiArgs.JsTrim(source);
        foreach (var package in configured)
        {
            switch (PiPackageSource.Parse(package.Source))
            {
                case PiNpmSource npm when trimmed == npm.Name || trimmed == npm.Spec: return package.Source;
                case PiGitSource git:
                    var shorthand = $"{git.Host}/{git.Path}";
                    if (trimmed == shorthand || git.Ref is not null && trimmed == $"{shorthand}@{git.Ref}") return package.Source;
                    break;
            }
        }
        return null;
    }

    private bool PackageSourcesMatch(PiPackageEntry existing, string inputSource, string scope) =>
        GetSourceMatchKeyForSettings(existing.Source, scope) == GetSourceMatchKeyForInput(inputSource);

    /// <summary>Source normalizePackageSourceForSettings: local paths are stored relative to the scope's settings directory.</summary>
    internal string NormalizePackageSourceForSettings(string source, string scope)
    {
        if (PiPackageSource.Parse(source) is not PiLocalSource local) return source;
        var baseDir = GetBaseDirForScope(scope);
        var relative = Relative(baseDir, ResolvePath(local.Path));
        return relative.Length > 0 ? relative : ".";
    }

    /// <summary>Source getPackageIdentity: npm name, git host/path (SSH and HTTPS forms match), or the resolved local path.</summary>
    internal string GetPackageIdentity(string source, string? scope = null) => PiPackageSource.Parse(source) switch
    {
        PiNpmSource npm => $"npm:{npm.Name}",
        PiGitSource git => $"git:{git.Host}/{git.Path}",
        PiLocalSource local => scope is not null ? $"local:{ResolvePathFromBase(local.Path, GetBaseDirForScope(scope))}" : $"local:{ResolvePath(local.Path)}",
        _ => source
    };

    /// <summary>Source dedupePackages: a project entry wins over a user entry of the same identity, except a project <c>autoload: false</c>
    /// entry, which is a delta over the user entry (both kept, delta first).</summary>
    private List<(PiPackageEntry Package, string Scope)> DedupePackages(List<(PiPackageEntry Package, string Scope)> packages)
    {
        var result = new List<(PiPackageEntry Package, string Scope)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in packages)
        {
            var identity = GetPackageIdentity(entry.Package.Source, entry.Scope);
            if (!seen.TryGetValue(identity, out var index)) { seen[identity] = result.Count; result.Add(entry); continue; }
            var existing = result[index];
            if (existing.Scope == "project" && entry.Scope == "user")
            {
                if (existing.Package.IsFilter && existing.Package.Autoload == false) result.Add(entry);
            }
            else if (entry.Scope == "project") result[index] = entry;
        }
        return result;
    }

    private void AssertProjectTrustedForScope(string scope)
    {
        if (scope == "project" && !settings.ProjectTrusted) throw new PiPackageException("Project is not trusted; refusing to access project package storage");
    }

    // ---- npm ------------------------------------------------------------------------------------------------------------------

    /// <summary>Source getNpmCommand: the <c>npmCommand</c> argv, else <c>npm</c>.</summary>
    internal (string Command, ImmutableArray<string> Args) GetNpmCommand()
    {
        var configured = settings.Merged["npmCommand"] is JsonArray array
            ? array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : "").ToList() : null;
        if (configured is null || configured.Count == 0) return ("npm", []);
        if (configured[0].Length == 0) throw new PiPackageException("Invalid npmCommand: first array entry must be a non-empty command");
        return (configured[0], [.. configured.Skip(1)]);
    }

    /// <summary>Source getPackageManagerName: npm, pnpm or bun, including wrapped forms (<c>mise exec … -- pnpm</c>, <c>corepack pnpm</c>).</summary>
    internal string GetPackageManagerName()
    {
        var (command, args) = GetNpmCommand();
        static string Normalize(string value) => System.Text.RegularExpressions.Regex.Replace(Basename(value), @"\.(cmd|exe)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var supported = new HashSet<string>(StringComparer.Ordinal) { "npm", "pnpm", "bun" };
        var direct = Normalize(command);
        var separator = args.LastIndexOf("--");
        if (separator >= 0) return separator + 1 < args.Length && args[separator + 1].Length > 0 ? Normalize(args[separator + 1]) : direct;
        if (supported.Contains(direct)) return direct;
        var wrapped = args.Select(Normalize).Where(supported.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (wrapped.Count > 1) throw new PiPackageException($"Ambiguous npmCommand package managers: {string.Join(", ", wrapped)}");
        return wrapped.Count > 0 ? wrapped[0] : direct;
    }

    private Task RunNpmCommand(IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken)
    {
        var (command, prefix) = GetNpmCommand();
        return processes.RunAsync(command, [.. prefix, .. args], workingDirectory, cancellationToken);
    }

    /// <summary>Source getGitDependencyInstallArgs.</summary>
    internal ImmutableArray<string> GetGitDependencyInstallArgs() => GetPackageManagerName() switch
    {
        "bun" => ["install", "--omit=dev", "--omit=peer"],
        "pnpm" => ["install", "--prod", "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"],
        "npm" => ["install", "--omit=dev", "--legacy-peer-deps"],
        _ => ["install"]
    };

    private string RunNpmCommandSync(IReadOnlyList<string> args)
    {
        var (command, prefix) = GetNpmCommand();
        return processes.RunSync(command, [.. prefix, .. args]);
    }

    /// <summary>Source getNpmInstallArgs: peer resolution disabled for managed installs.</summary>
    internal ImmutableArray<string> GetNpmInstallArgs(IReadOnlyList<string> specs, string installRoot) => GetPackageManagerName() switch
    {
        "bun" => ["install", .. specs, "--cwd", installRoot, "--omit=peer"],
        "pnpm" => ["install", .. specs, "--prefix", installRoot, "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"],
        _ => ["install", .. specs, "--prefix", installRoot, "--legacy-peer-deps"]
    };

    private async Task InstallNpm(PiNpmSource source, string scope, bool temporary, CancellationToken cancellationToken)
    {
        var installRoot = GetNpmInstallRoot(scope, temporary);
        EnsureNpmProject(installRoot);
        await RunNpmCommand(GetNpmInstallArgs([source.Spec], installRoot), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task UninstallNpm(PiNpmSource source, string scope, CancellationToken cancellationToken)
    {
        var installRoot = GetNpmInstallRoot(scope, false);
        if (!Path.Exists(installRoot)) return;
        var name = GetPackageManagerName();
        if (name == "bun") { await RunNpmCommand(["uninstall", source.Name, "--cwd", installRoot], null, cancellationToken).ConfigureAwait(false); return; }
        List<string> args = ["uninstall", source.Name, "--prefix", installRoot];
        if (name != "pnpm") args.Add("--legacy-peer-deps");
        await RunNpmCommand(args, null, cancellationToken).ConfigureAwait(false);
    }

    private bool InstalledNpmMatchesConfiguredVersion(PiNpmSource source, string installedPath)
    {
        var installed = GetInstalledNpmVersion(installedPath);
        if (installed is null) return false;
        return source.Range is null || PiSemver.Satisfies(installed, source.Range);
    }

    private async Task<bool> NpmHasAvailableUpdate(PiNpmSource source, string installedPath, CancellationToken cancellationToken)
    {
        if (OfflineModeEnabled) return false;
        var installed = GetInstalledNpmVersion(installedPath);
        if (installed is null) return false;
        try
        {
            var target = await GetLatestNpmVersion(source.Version is not null ? source.Spec : source.Name, source.Range, cancellationToken).ConfigureAwait(false);
            return PiSemver.Gt(target, installed);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return false; }
    }

    private static string? GetInstalledNpmVersion(string installedPath)
    {
        var packageJson = Path.Join(installedPath, "package.json");
        if (!File.Exists(packageJson)) return null;
        try { return JsonNode.Parse(PiPaths.ReadText(packageJson))?["version"] is JsonValue value && value.TryGetValue<string>(out var version) ? version : null; }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private async Task<string> GetLatestNpmVersion(string packageSpec, string? range, CancellationToken cancellationToken)
    {
        var (command, prefix) = GetNpmCommand();
        var stdout = await processes.CaptureAsync(command, [.. prefix, "view", packageSpec, "version", "--json"], cwd, NetworkTimeoutMs, null, cancellationToken).ConfigureAwait(false);
        var raw = PiArgs.JsTrim(stdout);
        if (raw.Length == 0) throw new PiPackageException("Empty response from npm view");
        var parsed = JsonNode.Parse(raw);
        if (parsed is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (parsed is JsonArray array)
        {
            var versions = array.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var entry) ? entry : null).OfType<string>().Where(entry => entry.Length > 0).ToList();
            var latest = range is not null ? PiSemver.MaxSatisfying(versions, range) : versions.Order(Comparer<string>.Create(PiSemver.RCompare)).FirstOrDefault();
            if (latest is not null) return latest;
        }
        throw new PiPackageException("Unexpected response from npm view");
    }

    private string GetNpmInstallRoot(string scope, bool temporary)
    {
        if (temporary) return GetTemporaryDir("npm");
        if (scope == "project")
        {
            AssertProjectTrustedForScope(scope);
            return Path.Join(cwd, PiConfig.ConfigDirName, "npm");
        }
        return Path.Join(agentDir, "npm");
    }

    private string GetGlobalNpmRoot()
    {
        var (command, args) = GetNpmCommand();
        var key = string.Join('\0', [command, .. args]);
        if (globalNpmRoot is not null && globalNpmRootCommandKey == key) return globalNpmRoot;
        if (GetPackageManagerName() == "bun")
        {
            var binDir = PiArgs.JsTrim(RunNpmCommandSync(["pm", "bin", "-g"]));
            globalNpmRoot = NodeJoin(Path.GetDirectoryName(binDir) ?? ".", "install", "global", "node_modules");
        }
        else globalNpmRoot = PiArgs.JsTrim(RunNpmCommandSync(["root", "-g"]));
        globalNpmRootCommandKey = key;
        return globalNpmRoot;
    }

    private string? GetPnpmGlobalPackagePath(string packageName)
    {
        if (GetPackageManagerName() != "pnpm") return null;
        var output = RunNpmCommandSync(["list", "-g", "--depth", "0", "--json"]);
        if (JsonNode.Parse(output) is not JsonArray entries) throw new PiPackageException("Unexpected pnpm list output");
        foreach (var entry in entries)
            if (entry?["dependencies"]?[packageName]?["path"] is JsonValue path && path.TryGetValue<string>(out var text) && text.Length > 0) return text;
        return null;
    }

    private string GetManagedNpmInstallPath(PiNpmSource source, string scope)
    {
        if (scope == "temporary") return NodeJoin(GetTemporaryDir("npm"), "node_modules", source.Name);
        if (scope == "project")
        {
            AssertProjectTrustedForScope(scope);
            return NodeJoin(cwd, PiConfig.ConfigDirName, "npm", "node_modules", source.Name);
        }
        return NodeJoin(agentDir, "npm", "node_modules", source.Name);
    }

    private string? GetLegacyGlobalNpmInstallPath(PiNpmSource source)
    {
        try { return GetPnpmGlobalPackagePath(source.Name) ?? NodeJoin(GetGlobalNpmRoot(), source.Name); }
        catch (Exception error) when (error is PiPackageException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Source getNpmInstallPath: the managed path, or for user scope a legacy global install that exists.</summary>
    internal string GetNpmInstallPath(PiNpmSource source, string scope)
    {
        var managed = GetManagedNpmInstallPath(source, scope);
        if (scope != "user" || Path.Exists(managed)) return managed;
        var legacy = GetLegacyGlobalNpmInstallPath(source);
        return legacy is not null && Path.Exists(legacy) ? legacy : managed;
    }

    private void EnsureNpmProject(string installRoot)
    {
        Directory.CreateDirectory(installRoot);
        MarkPathIgnoredByCloudSync(installRoot);
        EnsureGitIgnore(installRoot);
        var packageJson = Path.Join(installRoot, "package.json");
        if (!File.Exists(packageJson)) File.WriteAllText(packageJson, "{\n  \"name\": \"pi-extensions\",\n  \"private\": true\n}", new UTF8Encoding(false));
    }

    /// <summary>Source markPathIgnoredByCloudSync (utils/paths.ts): Dropbox and File Provider ignore attributes on macOS (xattr) and
    /// Linux (setfattr), best effort; nothing on Windows.</summary>
    private static void MarkPathIgnoredByCloudSync(string path)
    {
        (string Tool, string[] Args)[] commands = OperatingSystem.IsMacOS()
            ? [("xattr", ["-w", "com.dropbox.ignored", "1", path]), ("xattr", ["-w", "com.apple.fileprovider.ignore#P", "1", path])]
            : OperatingSystem.IsLinux() ? [("setfattr", ["-n", "user.com.dropbox.ignored", "-v", "1", path])] : [];
        foreach (var (tool, args) in commands)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in args) info.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(info);
                process?.StandardOutput.ReadToEnd(); process?.StandardError.ReadToEnd(); process?.WaitForExit();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        }
    }

    private static void EnsureGitIgnore(string directory)
    {
        Directory.CreateDirectory(directory);
        var ignorePath = Path.Join(directory, ".gitignore");
        if (!File.Exists(ignorePath)) File.WriteAllText(ignorePath, "*\n!.gitignore\n", new UTF8Encoding(false));
    }

    // ---- git ----------------------------------------------------------------------------------------------------------------

    private async Task InstallGit(PiGitSource source, string scope, CancellationToken cancellationToken)
    {
        var targetDir = GetGitInstallPath(source, scope);
        if (Path.Exists(targetDir))
        {
            if (source.Ref is not null) { await EnsureGitRef(targetDir, ["fetch", "origin", source.Ref], "FETCH_HEAD", cancellationToken).ConfigureAwait(false); return; }
            var target = await GetLocalGitUpdateTarget(targetDir, cancellationToken).ConfigureAwait(false);
            await EnsureGitRef(targetDir, target.FetchArgs, target.Ref, cancellationToken).ConfigureAwait(false);
            return;
        }
        var gitRoot = GetGitInstallRoot(scope);
        if (gitRoot is not null) EnsureGitIgnore(gitRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        DeleteFile(GetGitUpdateMarkerPath(targetDir));
        try
        {
            await processes.RunAsync("git", ["clone", source.Repo, targetDir], null, cancellationToken).ConfigureAwait(false);
            if (source.Ref is not null) await processes.RunAsync("git", ["checkout", source.Ref], targetDir, cancellationToken).ConfigureAwait(false);
            if (File.Exists(Path.Join(targetDir, "package.json"))) await RunNpmCommand(GetGitDependencyInstallArgs(), targetDir, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteTree(targetDir);
            PruneEmptyGitParents(targetDir, gitRoot);
            throw;
        }
    }

    private async Task UpdateGit(PiGitSource source, string scope, CancellationToken cancellationToken)
    {
        var targetDir = GetGitInstallPath(source, scope);
        if (!Path.Exists(targetDir)) { await InstallGit(source, scope, cancellationToken).ConfigureAwait(false); return; }
        if (source.Ref is not null) { await EnsureGitRef(targetDir, ["fetch", "origin", source.Ref], "FETCH_HEAD", cancellationToken).ConfigureAwait(false); return; }
        var target = await GetLocalGitUpdateTarget(targetDir, cancellationToken).ConfigureAwait(false);
        await EnsureGitRef(targetDir, target.FetchArgs, target.Ref, cancellationToken).ConfigureAwait(false);
    }

    private Task<string> Capture(string command, IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? env = null) => processes.CaptureAsync(command, args, workingDirectory, NetworkTimeoutMs, env, cancellationToken);

    private async Task<bool> GitHasAvailableUpdate(string installedPath, CancellationToken cancellationToken)
    {
        if (OfflineModeEnabled) return false;
        try
        {
            var localHead = await Capture("git", ["rev-parse", "HEAD"], installedPath, cancellationToken).ConfigureAwait(false);
            var remoteHead = await GetRemoteGitHead(installedPath, cancellationToken).ConfigureAwait(false);
            return PiArgs.JsTrim(localHead) != PiArgs.JsTrim(remoteHead);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return false; }
    }

    private async Task<string> GetRemoteGitHead(string installedPath, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        if (await GetGitUpstreamRef(installedPath, cancellationToken).ConfigureAwait(false) is { } upstreamRef)
        {
            var heads = await Capture("git", ["ls-remote", "origin", upstreamRef], installedPath, cancellationToken, environment).ConfigureAwait(false);
            var match = System.Text.RegularExpressions.Regex.Match(heads, @"^([0-9a-f]{40})\s+", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (match.Success) return match.Groups[1].Value;
        }
        var remote = await Capture("git", ["ls-remote", "origin", "HEAD"], installedPath, cancellationToken, environment).ConfigureAwait(false);
        var head = System.Text.RegularExpressions.Regex.Match(remote.Replace("\r\n", "\n"), @"^([0-9a-f]{40})\s+HEAD$", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!head.Success) throw new PiPackageException("Failed to determine remote HEAD");
        return head.Groups[1].Value;
    }

    /// <summary>Source getLocalGitUpdateTarget: the upstream branch (or origin's HEAD branch) and the narrow fetch for it.</summary>
    internal async Task<(string Ref, string Head, ImmutableArray<string> FetchArgs)> GetLocalGitUpdateTarget(string installedPath, CancellationToken cancellationToken)
    {
        try
        {
            var upstream = PiArgs.JsTrim(await Capture("git", ["rev-parse", "--abbrev-ref", "@{upstream}"], installedPath, cancellationToken).ConfigureAwait(false));
            if (!upstream.StartsWith("origin/", StringComparison.Ordinal)) throw new PiPackageException($"Unsupported upstream remote: {upstream}");
            var branch = upstream["origin/".Length..];
            if (branch.Length == 0) throw new PiPackageException("Missing upstream branch name");
            var head = await Capture("git", ["rev-parse", "@{upstream}"], installedPath, cancellationToken).ConfigureAwait(false);
            return ("@{upstream}", head, ["fetch", "--prune", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"]);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try { await processes.RunAsync("git", ["remote", "set-head", "origin", "-a"], installedPath, cancellationToken).ConfigureAwait(false); }
            catch (Exception ignored) when (ignored is not OperationCanceledException) { }
            var head = await Capture("git", ["rev-parse", "origin/HEAD"], installedPath, cancellationToken).ConfigureAwait(false);
            string originHeadRef;
            try { originHeadRef = await Capture("git", ["symbolic-ref", "refs/remotes/origin/HEAD"], installedPath, cancellationToken).ConfigureAwait(false); }
            catch (Exception ignored) when (ignored is not OperationCanceledException) { originHeadRef = ""; }
            var branch = PiArgs.JsTrim(originHeadRef);
            if (branch.StartsWith("refs/remotes/origin/", StringComparison.Ordinal)) branch = branch["refs/remotes/origin/".Length..];
            if (branch.Length > 0) return ("origin/HEAD", head, ["fetch", "--prune", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"]);
            return ("origin/HEAD", head, ["fetch", "--prune", "--no-tags", "origin", "+HEAD:refs/remotes/origin/HEAD"]);
        }
    }

    private async Task<string?> GetGitUpstreamRef(string installedPath, CancellationToken cancellationToken)
    {
        try
        {
            var upstream = PiArgs.JsTrim(await Capture("git", ["rev-parse", "--abbrev-ref", "@{upstream}"], installedPath, cancellationToken).ConfigureAwait(false));
            if (!upstream.StartsWith("origin/", StringComparison.Ordinal)) return null;
            var branch = upstream["origin/".Length..];
            return branch.Length > 0 ? $"refs/heads/{branch}" : null;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private static async Task<T[]> RunWithConcurrency<T>(List<Func<Task<T>>> tasks, int limit)
    {
        if (tasks.Count == 0) return [];
        var results = new T[tasks.Count];
        var next = -1;
        async Task Worker()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= tasks.Count) return;
                results[index] = await tasks[index]().ConfigureAwait(false);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, Math.Min(limit, tasks.Count))).Select(_ => Worker())).ConfigureAwait(false);
        return results;
    }

    private bool HasMissingGitDependencies(string targetDir)
    {
        var packageJson = Path.Join(targetDir, "package.json");
        if (!File.Exists(packageJson)) return false;
        try
        {
            if (JsonNode.Parse(PiPaths.ReadText(packageJson))?["dependencies"] is not JsonObject dependencies) return false;
            var nodeModules = Path.GetFullPath(Path.Join(targetDir, "node_modules"));
            return dependencies.Select(pair => pair.Key).Any(name =>
            {
                var dependencyPath = Path.GetFullPath(Path.Combine(nodeModules, name));
                if (!dependencyPath.StartsWith(nodeModules + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
                return !Path.Exists(dependencyPath);
            });
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    private async Task RepairMissingGitDependencies(string targetDir, CancellationToken cancellationToken)
    {
        if (!HasMissingGitDependencies(targetDir)) return;
        await RunNpmCommand(GetGitDependencyInstallArgs(), targetDir, cancellationToken).ConfigureAwait(false);
    }

    private static string GetGitUpdateMarkerPath(string targetDir) =>
        Path.Join(Path.GetDirectoryName(targetDir), $".{Path.GetFileName(targetDir)}.pi-update-incomplete");

    private async Task CleanAndInstallGitDependencies(string targetDir, string markerPath, CancellationToken cancellationToken)
    {
        // Extensions should be pristine; if cleaning fails after deleting dependencies, repair them so the extension still loads.
        try { await processes.RunAsync("git", ["clean", "-fdx"], targetDir, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try { await RepairMissingGitDependencies(targetDir, cancellationToken).ConfigureAwait(false); }
            catch (Exception ignored) when (ignored is not OperationCanceledException) { }
            throw;
        }
        if (File.Exists(Path.Join(targetDir, "package.json"))) await RunNpmCommand(GetGitDependencyInstallArgs(), targetDir, cancellationToken).ConfigureAwait(false);
        DeleteFile(markerPath);
    }

    private async Task EnsureGitRef(string targetDir, IReadOnlyList<string> fetchArgs, string reference, CancellationToken cancellationToken)
    {
        // Fetch only the ref the checkout resets to.
        await processes.RunAsync("git", fetchArgs, targetDir, cancellationToken).ConfigureAwait(false);
        var localHead = await Capture("git", ["rev-parse", "HEAD"], targetDir, cancellationToken).ConfigureAwait(false);
        var commitRef = $"{reference}^{{commit}}";
        var targetHead = await Capture("git", ["rev-parse", commitRef], targetDir, cancellationToken).ConfigureAwait(false);
        var markerPath = GetGitUpdateMarkerPath(targetDir);
        if (PiArgs.JsTrim(localHead) == PiArgs.JsTrim(targetHead))
        {
            if (File.Exists(markerPath)) await CleanAndInstallGitDependencies(targetDir, markerPath, cancellationToken).ConfigureAwait(false);
            else await RepairMissingGitDependencies(targetDir, cancellationToken).ConfigureAwait(false);
            return;
        }
        File.WriteAllText(markerPath, "", new UTF8Encoding(false));
        await processes.RunAsync("git", ["reset", "--hard", commitRef], targetDir, cancellationToken).ConfigureAwait(false);
        await CleanAndInstallGitDependencies(targetDir, markerPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshTemporaryGitSource(PiGitSource source, string sourceText, CancellationToken cancellationToken)
    {
        if (OfflineModeEnabled) return;
        try { await WithProgress("pull", sourceText, $"Refreshing {sourceText}...", () => UpdateGit(source, "temporary", cancellationToken)).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    private void RemoveGit(PiGitSource source, string scope)
    {
        var targetDir = GetGitInstallPath(source, scope);
        DeleteTree(targetDir);
        DeleteFile(GetGitUpdateMarkerPath(targetDir));
        PruneEmptyGitParents(targetDir, GetGitInstallRoot(scope));
    }

    private static void PruneEmptyGitParents(string targetDir, string? installRoot)
    {
        if (installRoot is null) return;
        var root = Path.GetFullPath(installRoot);
        var current = Path.GetDirectoryName(targetDir);
        while (current is not null && current.StartsWith(root, StringComparison.Ordinal) && current != root)
        {
            if (!Path.Exists(current)) { current = Path.GetDirectoryName(current); continue; }
            if (Directory.EnumerateFileSystemEntries(current).Any()) break;
            try { Directory.Delete(current, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { break; }
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>Source getGitInstallPath: <c>&lt;root&gt;/&lt;host&gt;/&lt;path&gt;</c>, or a ref-hashed temporary checkout.</summary>
    internal string GetGitInstallPath(PiGitSource source, string scope)
    {
        if (scope == "temporary") return GetTemporaryDir($"git-{source.Host}", source.Path, source.Ref);
        var installRoot = GetGitInstallRoot(scope) ?? throw new PiPackageException("Missing git install root");
        return ResolveManagedPath(installRoot, source.Host, source.Path);
    }

    private string? GetGitInstallRoot(string scope)
    {
        if (scope == "temporary") return null;
        if (scope == "project")
        {
            AssertProjectTrustedForScope(scope);
            return Path.Join(cwd, PiConfig.ConfigDirName, "git");
        }
        return Path.Join(agentDir, "git");
    }

    /// <summary>Source getExtensionTempFolder: <c>&lt;agentDir&gt;/tmp/extensions</c>, private to the user.</summary>
    internal static string GetExtensionTempFolder(string agentDir)
    {
        var folder = Path.Join(agentDir, "tmp", "extensions");
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    private string GetTemporaryDir(string prefix, string? suffix = null, string? reference = null)
    {
        var root = ResolveManagedPath(GetExtensionTempFolder(agentDir), prefix);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{prefix}-{suffix ?? ""}{(reference is not null ? "@" + reference : "")}")))[..8];
        return ResolveManagedPath(root, hash, suffix ?? "");
    }

    private static string ResolveManagedPath(string root, params string[] parts)
    {
        var resolvedRoot = Path.GetFullPath(root);
        var resolvedPath = Path.GetFullPath(Path.Combine([resolvedRoot, .. parts.Where(part => part.Length > 0)]));
        resolvedPath = Path.TrimEndingDirectorySeparator(resolvedPath);
        if (resolvedPath != resolvedRoot && !resolvedPath.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PiPackageException($"Refusing to use path outside package install root: {resolvedPath}");
        return resolvedPath;
    }

    private string GetBaseDirForScope(string scope)
    {
        if (scope == "project")
        {
            AssertProjectTrustedForScope(scope);
            return Path.Join(cwd, PiConfig.ConfigDirName);
        }
        return scope == "user" ? agentDir : cwd;
    }

    // Source resolvePath (node path.resolve drops trailing separators).
    private string ResolvePath(string input) => Path.TrimEndingDirectorySeparator(PiPaths.ResolvePath(input, cwd, home, trim: true));
    private string ResolvePathFromBase(string input, string baseDir) => Path.TrimEndingDirectorySeparator(PiPaths.ResolvePath(input, baseDir, home, trim: true));

    /// <summary>Node path.join: segments joined and normalized (separators, <c>.</c> and <c>..</c>).</summary>
    private static string NodeJoin(params string[] parts) => Path.GetFullPath(Path.Join(parts));

    // ---- resource collection --------------------------------------------------------------------------------------------------

    private bool CollectPackageResources(string packageRoot, Accumulator accumulator, PiPackageEntry? filter, PiPathMetadata metadata)
    {
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
        var manifest = PiManifest.Read(Path.Join(packageRoot, "package.json"));
        if (manifest is not null)
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
            ? CollectAncestorAgentsSkillDirs(cwd).Where(directory => Path.GetFullPath(directory) != Path.GetFullPath(userAgentsSkillsDir)).ToList() : [];

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
                .Where(resource => seen.Add(resource.Path.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal) ? resource.Path : PiPaths.Canonicalize(resource.Path)))];
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
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
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

    private static bool MatchesAnyPattern(string filePath, IEnumerable<string> patterns, string baseDir)
    {
        var rel = ToPosixPath(Relative(baseDir, filePath));
        var name = Path.GetFileName(filePath);
        var filePathPosix = ToPosixPath(filePath);
        var isSkillFile = name == "SKILL.md";
        var parentDir = isSkillFile ? Path.GetDirectoryName(filePath)! : null;
        return patterns.Any(pattern =>
        {
            var normalized = ToPosixPath(pattern);
            if (PiMinimatch.Match(rel, normalized) || PiMinimatch.Match(name, normalized) || PiMinimatch.Match(filePathPosix, normalized)) return true;
            if (parentDir is null) return false;
            return PiMinimatch.Match(ToPosixPath(Relative(baseDir, parentDir)), normalized) || PiMinimatch.Match(Path.GetFileName(parentDir), normalized) ||
                PiMinimatch.Match(ToPosixPath(parentDir), normalized);
        });
    }

    private static string NormalizeExactPattern(string pattern) =>
        ToPosixPath(pattern.StartsWith("./", StringComparison.Ordinal) || pattern.StartsWith(".\\", StringComparison.Ordinal) ? pattern[2..] : pattern);

    private static bool MatchesAnyExactPattern(string filePath, IReadOnlyCollection<string> patterns, string baseDir)
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

    private static bool IsEnabledByOverrides(string filePath, List<string> patterns, string baseDir)
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
    private static HashSet<string> ApplyPatterns(List<string> allPaths, IEnumerable<string> patterns, string baseDir)
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

    private static Dictionary<string, bool> ApplyAutoloadDisabledPatterns(List<string> allPaths, IEnumerable<string> patterns, string baseDir)
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
}
