// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (showLoadedResources and its
// path/scope formatting helpers, getAutocompleteSourceTag, createBaseAutocompleteProvider, setupAutocompleteProvider,
// getBuiltInCommandConflictDiagnostics) and coding-agent/src/core/slash-commands.ts (BUILTIN_SLASH_COMMANDS).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>A source of a loaded resource (SourceInfo).</summary>
internal sealed record ResourceSourceInfo(string? Path = null, string? Source = null, string? Scope = null, string? BaseDir = null);
internal sealed record LoadedResource(string Path, ResourceSourceInfo? SourceInfo, string? Name = null);
internal sealed record ResourceDiagnostic(string Type, string Message, string? Path = null, string? CollisionName = null, string? WinnerPath = null, string? LoserPath = null);

internal sealed record BuiltinSlashCommand(string Name, string Description, string? ArgumentHint = null);

internal static class SlashCommands
{
    public static IReadOnlyList<BuiltinSlashCommand> Builtin { get; } =
    [
        new("settings", "Open settings menu"),
        new("model", "Select model (opens selector UI)", "<provider/model>"),
        new("tree", "Navigate session tree (switch branches)"),
        new("thinking", "Set thinking level", "<level>"),
        new("scoped-models", "Enable/disable models for Ctrl+P cycling"),
        new("export", "Export session (HTML default, or specify path: .html/.jsonl)"),
        new("import", "Import and resume a session from a JSONL file"),
        new("share", "Share session as a secret GitHub gist"),
        new("bug", "Report a PiSharp bug as a GitHub issue", "<description>"),
        new("copy", "Copy last agent message to clipboard"),
        new("name", "Set session display name"),
        new("session", "Show session info and stats"),
        new("changelog", "Show changelog entries"),
        new("hotkeys", "Show all keyboard shortcuts"),
        new("fork", "Create a new fork from a previous user message"),
        new("clone", "Duplicate the current session at the current position"),
        new("trust", "Save project trust decision for future sessions"),
        new("login", "Configure provider authentication", "<provider>"),
        new("logout", "Remove provider authentication"),
        new("new", "Start a new session"),
        new("compact", "Manually compact the session context"),
        new("resume", "Resume a different session"),
        new("reload", "Reload keybindings, extensions, skills, prompts, themes, and context files"),
        new("quit", "Quit pi")
    ];
}

internal sealed partial class InteractiveMode
{
    private static ResourceSourceInfo? SourceInfoOf(JsonNode? node) => node is JsonObject info
        ? new(S(info["path"]), S(info["source"]), S(info["scope"]), S(info["baseDir"])) : null;

    private string? GetAutocompleteSourceTag(ResourceSourceInfo? sourceInfo)
    {
        if (sourceInfo is null || sourceInfo.Source == "builtin") return null;
        var scopePrefix = sourceInfo.Scope == "user" ? "u" : sourceInfo.Scope == "project" ? "p" : "t";
        var source = TextUtils.JsTrim(sourceInfo.Source ?? "");
        if (source is "auto" or "local" or "cli") return scopePrefix;
        if (source.StartsWith("npm:", StringComparison.Ordinal)) return $"{scopePrefix}:{source}";
        if (InteractiveModeContext.ParseGitUrl(source) is { } git) return $"{scopePrefix}:git:{git.Host}/{git.Path}{(git.Ref is { } gitRef ? "@" + gitRef : "")}";
        return scopePrefix;
    }

    private string? PrefixAutocompleteDescription(string? description, ResourceSourceInfo? sourceInfo)
    {
        var tag = GetAutocompleteSourceTag(sourceInfo);
        if (tag is null) return description;
        return description is { Length: > 0 } ? $"[{tag}] {description}" : $"[{tag}]";
    }

    private static List<AutocompleteItem>? CreateFuzzyAutocompleteItems<T>(IReadOnlyList<T> items, string prefix, Func<T, string> getSearchText, Func<T, AutocompleteItem> toItem)
    {
        var filtered = Fuzzy.Filter(items, prefix, getSearchText);
        return filtered.Count == 0 ? null : [.. filtered.Select(toItem)];
    }

    private IAutocompleteProvider CreateBaseAutocompleteProvider()
    {
        var slashCommands = SlashCommands.Builtin.Select(command => new SlashCommand(command.Name, command.Description, command.ArgumentHint)).ToList();
        for (var i = 0; i < slashCommands.Count; i++)
        {
            var command = slashCommands[i];
            if (command.Name == "model")
                slashCommands[i] = command with
                {
                    GetArgumentCompletions = prefix =>
                    {
                        var models = state.ScopedModels.Count > 0 ? state.ScopedModels.Select(scoped => scoped.Model).ToList() : state.AvailableModels;
                        if (models.Count == 0) return Task.FromResult<List<AutocompleteItem>?>(null);
                        var items = models.Select(model => new ModelSearchItem(S(model["id"]) ?? "", S(model["provider"]) ?? "", S(model["name"]))).ToList();
                        return Task.FromResult(CreateFuzzyAutocompleteItems(items, prefix, ModelSearch.GetModelSearchText,
                            item => new AutocompleteItem($"{item.Provider}/{item.Id}", item.Id, item.Provider)));
                    }
                };
            else if (command.Name == "thinking")
                slashCommands[i] = command with
                {
                    GetArgumentCompletions = prefix => Task.FromResult(CreateFuzzyAutocompleteItems(state.AvailableThinkingLevels, prefix, level => level, level => new AutocompleteItem(level, level)))
                };
            else if (command.Name == "login")
                slashCommands[i] = command with
                {
                    GetArgumentCompletions = prefix =>
                    {
                        var providers = GetLoginProviderCompletionOptions(GetLoginProviderOptions());
                        return Task.FromResult(CreateFuzzyAutocompleteItems(providers, prefix, GetLoginProviderSearchText,
                            provider => new AutocompleteItem(provider.Id, provider.Id, FormatLoginProviderCompletionDescription(provider))));
                    }
                };
        }
        if (context.Mcp is not null) slashCommands.Add(new SlashCommand("mcp", "Manage MCP servers", null, prefix => McpArgumentCompletions(prefix)));
        var builtinNames = new HashSet<string>(slashCommands.Select(command => command.Name), StringComparer.Ordinal);
        var templateCommands = new List<SlashCommand>();
        var extensionCommands = new List<SlashCommand>();
        var skillCommandList = new List<SlashCommand>();
        skillCommands.Clear();
        foreach (var command in state.Commands.OfType<JsonObject>())
        {
            var name = S(command["name"]) ?? "";
            var sourceInfo = SourceInfoOf(command["sourceInfo"]);
            var description = PrefixAutocompleteDescription(S(command["description"]), sourceInfo);
            switch (S(command["source"]))
            {
                case "prompt":
                    templateCommands.Add(new SlashCommand(name, description, S(command["argumentHint"])));
                    break;
                case "skill":
                    if (!settings.EnableSkillCommands) break;
                    skillCommands[name] = sourceInfo?.Path ?? "";
                    skillCommandList.Add(new SlashCommand(name, description));
                    break;
                default:
                    if (builtinNames.Contains(name)) break;
                    var invocation = name;
                    extensionCommands.Add(new SlashCommand(invocation, description, null, prefix => CompleteExtensionCommandAsync(invocation, prefix)));
                    break;
            }
        }
        return new CombinedAutocompleteProvider([.. slashCommands, .. templateCommands, .. extensionCommands, .. skillCommandList], Cwd, fdPath);
    }

    /// <summary>Extension argument completions: the host runs the command's getArgumentCompletions.</summary>
    private async Task<List<AutocompleteItem>?> CompleteExtensionCommandAsync(string name, string prefix)
    {
        try
        {
            var data = await rpc.RequestAsync(new JsonObject { ["type"] = "pisharp_complete_extension_command", ["command"] = name, ["prefix"] = prefix });
            return data is JsonObject result && result["completions"] is JsonArray completions
                ? completions.OfType<JsonObject>().Select(item => new AutocompleteItem(S(item["value"]) ?? "", S(item["label"]) ?? S(item["value"]) ?? "", S(item["description"]))).ToList()
                : null;
        }
        catch { return null; }
    }

    /// <summary>A provider with the merged trigger characters of the wrappers (provider.triggerCharacters = ...).</summary>
    private sealed class TriggerCharactersProvider(IAutocompleteProvider inner, IReadOnlyList<string> triggers) : IAutocompleteProvider
    {
        public IReadOnlyList<string> TriggerCharacters => triggers;
        public Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken) =>
            inner.GetSuggestionsAsync(lines, cursorLine, cursorCol, force, cancellationToken);
        public CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix) =>
            inner.ApplyCompletion(lines, cursorLine, cursorCol, item, prefix);
        public bool ShouldTriggerFileCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol) => inner.ShouldTriggerFileCompletion(lines, cursorLine, cursorCol);
    }
    private void SetupAutocompleteProvider()
    {
        var provider = CreateBaseAutocompleteProvider();
        var triggers = new List<string>();
        foreach (var wrap in autocompleteProviderWrappers)
        {
            provider = wrap(provider);
            triggers.AddRange(provider.TriggerCharacters ?? []);
        }
        if (triggers.Count > 0) provider = new TriggerCharactersProvider(provider, [.. triggers.Distinct(StringComparer.Ordinal)]);
        autocompleteProvider = provider;
        defaultEditor.SetAutocompleteProvider(provider);
        if (!ReferenceEquals(editor, defaultEditor)) editor.SetAutocompleteProvider(provider);
    }

    // =========================================================================
    // Loaded resources
    // =========================================================================

    private string FormatDisplayPath(string p)
    {
        var home = context.Startup.Home;
        return p.StartsWith(home, StringComparison.Ordinal) ? "~" + p[home.Length..] : p;
    }

    private string FormatExtensionDisplayPath(string path) =>
        Regex.Replace(Regex.Replace(FormatDisplayPath(path), @"/index\.ts$", ""), @"/index\.js$", "");

    private string FormatContextPath(string p)
    {
        var cwd = Path.GetFullPath(Cwd);
        var absolute = Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Join(cwd, p));
        return InteractiveModeContext.GetCwdRelativePath(absolute, cwd) ?? FormatDisplayPath(absolute);
    }

    private static bool IsPackageSource(ResourceSourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "";
        return source.StartsWith("npm:", StringComparison.Ordinal) || source.StartsWith("git:", StringComparison.Ordinal);
    }

    private string GetShortPath(string fullPath, ResourceSourceInfo? sourceInfo)
    {
        var normalizedFullPath = fullPath.Replace('\\', '/');
        if (sourceInfo?.BaseDir is { } baseDir && IsPackageSource(sourceInfo))
        {
            var normalizedBaseDir = baseDir.Replace('\\', '/');
            var npmRoot = Regex.Match(normalizedBaseDir, @"^(.*/node_modules)/(@?[^/]+(?:/[^/]+)?)$");
            if (npmRoot.Success && normalizedFullPath.StartsWith(npmRoot.Groups[1].Value + "/", StringComparison.Ordinal))
                return Path.GetRelativePath(normalizedBaseDir, normalizedFullPath).Replace('\\', '/');
            var relative = Path.GetRelativePath(Path.GetFullPath(baseDir), Path.GetFullPath(fullPath));
            if (relative.Length > 0 && relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return relative.Replace('\\', '/');
        }
        var source = sourceInfo?.Source ?? "";
        var npm = Regex.Match(normalizedFullPath, @"node_modules/(@?[^/]+(?:/[^/]+)?)/(.*)");
        if (npm.Success && source.StartsWith("npm:", StringComparison.Ordinal)) return npm.Groups[2].Value;
        var git = Regex.Match(normalizedFullPath, @"git/[^/]+/[^/]+/(.*)");
        if (git.Success && source.StartsWith("git:", StringComparison.Ordinal)) return git.Groups[1].Value;
        return FormatDisplayPath(fullPath);
    }

    private string GetCompactPathLabel(string resourcePath, ResourceSourceInfo? sourceInfo = null)
    {
        var shortPath = GetShortPath(resourcePath, sourceInfo);
        var segments = shortPath.Replace('\\', '/').Split('/').Where(segment => segment.Length > 0 && segment != "~").ToList();
        return segments.Count > 0 ? segments[^1] : shortPath;
    }

    private string GetCompactPackageSourceLabel(ResourceSourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "";
        if (source.StartsWith("npm:", StringComparison.Ordinal)) return source["npm:".Length..] is { Length: > 0 } npm ? npm : source;
        if (InteractiveModeContext.ParseGitUrl(source) is { } git) return git.Path is { Length: > 0 } path ? path : source;
        return source;
    }

    private string GetCompactExtensionLabel(string resourcePath, ResourceSourceInfo? sourceInfo)
    {
        if (!IsPackageSource(sourceInfo)) return GetCompactPathLabel(resourcePath, sourceInfo);
        var sourceLabel = GetCompactPackageSourceLabel(sourceInfo);
        if (sourceLabel.Length == 0) return GetCompactPathLabel(resourcePath, sourceInfo);
        var shortPath = GetShortPath(resourcePath, sourceInfo).Replace('\\', '/');
        var packagePath = shortPath.StartsWith("extensions/", StringComparison.Ordinal) ? shortPath["extensions/".Length..] : shortPath;
        var dir = packagePath.Contains('/') ? packagePath[..packagePath.LastIndexOf('/')] : "";
        var name = Path.GetFileNameWithoutExtension(packagePath);
        if (name == "index") return dir.Length == 0 || dir == "." ? sourceLabel : $"{sourceLabel}:{dir}";
        return $"{sourceLabel}:{packagePath}";
    }

    private List<string> GetCompactDisplayPathSegments(string resourcePath) =>
        [.. FormatDisplayPath(resourcePath).Replace('\\', '/').Split('/').Where(segment => segment.Length > 0 && segment != "~")];

    private List<string> GetCompactExtensionLabels(IReadOnlyList<LoadedResource> extensions)
    {
        var nonPackage = extensions.Where(extension => !IsPackageSource(extension.SourceInfo)).Select(extension =>
        {
            var segments = GetCompactDisplayPathSegments(extension.Path);
            if (segments.Count > 1 && segments[^1] is "index.ts" or "index.js") segments.RemoveAt(segments.Count - 1);
            return (extension.Path, Segments: segments);
        }).ToList();
        return extensions.Select(extension =>
        {
            if (IsPackageSource(extension.SourceInfo)) return GetCompactExtensionLabel(extension.Path, extension.SourceInfo);
            var index = nonPackage.FindIndex(item => item.Path == extension.Path);
            if (index == -1) return GetCompactPathLabel(extension.Path, extension.SourceInfo);
            var segments = nonPackage[index].Segments;
            if (segments.Count == 0) return GetCompactPathLabel(extension.Path);
            for (var count = 1; count <= segments.Count; count++)
            {
                var candidate = string.Join("/", segments.Skip(segments.Count - count));
                var unique = nonPackage.Select((item, itemIndex) => (item, itemIndex)).All(pair => pair.itemIndex == index ||
                    string.Join("/", pair.item.Segments.Skip(Math.Max(0, pair.item.Segments.Count - count))) != candidate);
                if (unique) return candidate;
            }
            return string.Join("/", segments);
        }).ToList();
    }

    private static (string Label, string? ScopeLabel, string Color) GetDisplaySourceInfo(ResourceSourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "local";
        var scope = sourceInfo?.Scope ?? "project";
        if (source == "local")
            return scope switch
            {
                "user" => ("user", null, "muted"),
                "project" => ("project", null, "muted"),
                "temporary" => ("path", "temp", "muted"),
                _ => ("path", null, "muted")
            };
        if (source == "cli") return ("path", scope == "temporary" ? "temp" : null, "muted");
        var scopeLabel = scope switch { "user" => "user", "project" => "project", "temporary" => "temp", _ => null };
        return (source, scopeLabel, "accent");
    }

    private static string GetScopeGroup(ResourceSourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "local";
        var scope = sourceInfo?.Scope ?? "project";
        if (source == "cli" || scope == "temporary") return "path";
        return scope is "user" or "project" ? scope : "path";
    }

    private sealed record ScopeGroup(string Scope, List<LoadedResource> Paths, SortedDictionary<string, List<LoadedResource>> Packages);

    private static List<ScopeGroup> BuildScopeGroups(IEnumerable<LoadedResource> items)
    {
        var groups = new Dictionary<string, ScopeGroup>(StringComparer.Ordinal)
        {
            ["user"] = new("user", [], new(StringComparer.Ordinal)), ["project"] = new("project", [], new(StringComparer.Ordinal)),
            ["path"] = new("path", [], new(StringComparer.Ordinal))
        };
        foreach (var item in items)
        {
            var group = groups[GetScopeGroup(item.SourceInfo)];
            var source = item.SourceInfo?.Source ?? "local";
            if (IsPackageSource(item.SourceInfo))
            {
                if (!group.Packages.TryGetValue(source, out var list)) group.Packages[source] = list = [];
                list.Add(item);
            }
            else group.Paths.Add(item);
        }
        return new[] { groups["project"], groups["user"], groups["path"] }.Where(group => group.Paths.Count > 0 || group.Packages.Count > 0).ToList();
    }

    private static readonly StringComparer LocaleCompare = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, false);

    private static string FormatScopeGroups(List<ScopeGroup> groups, Func<LoadedResource, string> formatPath, Func<LoadedResource, string, string> formatPackagePath)
    {
        var lines = new List<string>();
        foreach (var group in groups)
        {
            lines.Add($"  {theme.Fg("accent", group.Scope)}");
            foreach (var item in group.Paths.OrderBy(item => item.Path, LocaleCompare)) lines.Add(theme.Fg("dim", $"    {formatPath(item)}"));
            foreach (var (source, items) in group.Packages.OrderBy(pair => pair.Key, LocaleCompare))
            {
                lines.Add($"    {theme.Fg("mdLink", source)}");
                foreach (var item in items.OrderBy(item => item.Path, LocaleCompare)) lines.Add(theme.Fg("dim", $"      {formatPackagePath(item, source)}"));
            }
        }
        return string.Join("\n", lines);
    }

    private static ResourceSourceInfo? FindSourceInfoForPath(string p, Dictionary<string, ResourceSourceInfo> sourceInfos)
    {
        if (sourceInfos.TryGetValue(p, out var exact)) return exact;
        var current = p;
        while (current.Contains('/'))
        {
            current = current[..current.LastIndexOf('/')];
            if (sourceInfos.TryGetValue(current, out var parent)) return parent;
        }
        return null;
    }

    private string FormatPathWithSource(string p, ResourceSourceInfo? sourceInfo)
    {
        if (sourceInfo is null) return FormatDisplayPath(p);
        var shortPath = GetShortPath(p, sourceInfo);
        var (label, scopeLabel, _) = GetDisplaySourceInfo(sourceInfo);
        return $"{(scopeLabel is not null ? $"{label} ({scopeLabel})" : label)} {shortPath}";
    }

    private string FormatDiagnostics(IReadOnlyList<ResourceDiagnostic> diagnostics, Dictionary<string, ResourceSourceInfo> sourceInfos)
    {
        var lines = new List<string>();
        var collisions = new Dictionary<string, List<ResourceDiagnostic>>(StringComparer.Ordinal);
        var others = new List<ResourceDiagnostic>();
        foreach (var d in diagnostics)
        {
            if (d.Type == "collision" && d.CollisionName is { } name)
            {
                if (!collisions.TryGetValue(name, out var list)) collisions[name] = list = [];
                list.Add(d);
            }
            else others.Add(d);
        }
        foreach (var (name, list) in collisions)
        {
            var first = list[0];
            lines.Add(theme.Fg("warning", $"  \"{name}\" collision:"));
            lines.Add(theme.Fg("dim", $"    {theme.Fg("success", "✓")} {FormatPathWithSource(first.WinnerPath ?? "", FindSourceInfoForPath(first.WinnerPath ?? "", sourceInfos))}"));
            foreach (var d in list)
                lines.Add(theme.Fg("dim", $"    {theme.Fg("warning", "✗")} {FormatPathWithSource(d.LoserPath ?? "", FindSourceInfoForPath(d.LoserPath ?? "", sourceInfos))} (skipped)"));
        }
        foreach (var d in others)
        {
            var color = d.Type == "error" ? "error" : "warning";
            if (d.Path is { } path)
            {
                lines.Add(theme.Fg(color, $"  {FormatPathWithSource(path, FindSourceInfoForPath(path, sourceInfos))}"));
                lines.Add(theme.Fg(color, $"    {d.Message}"));
            }
            else lines.Add(theme.Fg(color, $"  {d.Message}"));
        }
        return string.Join("\n", lines);
    }

    private void ShowLoadedResources(bool force = false, bool showDiagnosticsWhenQuiet = false, IReadOnlyList<LoadedResource>? extensionsOverride = null)
    {
        loadedResourcesContainer.Clear();
        var showListing = force || ShouldShowStartupDetails();
        var showDiagnostics = showListing || showDiagnosticsWhenQuiet;
        if (!showListing && !showDiagnostics) return;
        string SectionHeader(string name, string color = "mdHeading") => theme.Fg(color, $"[{name}]");
        string FormatCompactList(IEnumerable<string> items, bool sort = true)
        {
            var labels = items.Select(item => TextUtils.JsTrim(item)).Where(item => item.Length > 0).ToList();
            if (sort) labels.Sort(LocaleCompare);
            return theme.Fg("dim", $"  {string.Join(", ", labels)}");
        }
        void AddLoadedSection(string name, Func<string> collapsedBody, Func<string>? expandedBody = null, string color = "mdHeading")
        {
            expandedBody ??= collapsedBody;
            loadedResourcesContainer.AddChild(new ExpandableText(() => $"{SectionHeader(name, color)}\n{collapsedBody()}",
                () => $"{SectionHeader(name, color)}\n{expandedBody()}", GetStartupExpansionState(), 0, 0));
            loadedResourcesContainer.AddChild(new Spacer(1));
        }

        var commands = state.Commands.OfType<JsonObject>().ToList();
        var skills = commands.Where(command => S(command["source"]) == "skill")
            .Select(command => new LoadedResource(SourceInfoOf(command["sourceInfo"])?.Path ?? "", SourceInfoOf(command["sourceInfo"]), (S(command["name"]) ?? "").Replace("skill:", "", StringComparison.Ordinal))).ToList();
        var templates = commands.Where(command => S(command["source"]) == "prompt")
            .Select(command => new LoadedResource(SourceInfoOf(command["sourceInfo"])?.Path ?? "", SourceInfoOf(command["sourceInfo"]), S(command["name"]))).ToList();
        var extensions = extensionsOverride ?? context.GetLoadedExtensions?.Invoke() ?? [];
        var sourceInfos = new Dictionary<string, ResourceSourceInfo>(StringComparer.Ordinal);
        foreach (var item in extensions.Concat(skills).Concat(templates)) if (item.SourceInfo is not null) sourceInfos[item.Path] = item.SourceInfo;

        if (showListing)
        {
            var resources = context.Startup.Resources;
            var contextFiles = new List<string>();
            if (resources.SystemPromptSource is { } systemPromptSource && File.Exists(systemPromptSource)) contextFiles.Add(systemPromptSource);
            contextFiles.AddRange(resources.ContextFiles.Select(file => file.Path));
            if (contextFiles.Count > 0)
            {
                loadedResourcesContainer.AddChild(new Spacer(1));
                AddLoadedSection("Context", () => FormatCompactList(contextFiles.Select(FormatContextPath), sort: false),
                    () => string.Join("\n", contextFiles.Select(file => theme.Fg("dim", $"  {FormatDisplayPath(file)}"))));
            }
            if (skills.Count > 0)
            {
                var groups = BuildScopeGroups(skills);
                AddLoadedSection("Skills", () => FormatCompactList(skills.Select(skill => skill.Name ?? "")),
                    () => FormatScopeGroups(groups, item => FormatDisplayPath(item.Path), (item, _) => GetShortPath(item.Path, item.SourceInfo)));
            }
            if (templates.Count > 0)
            {
                var groups = BuildScopeGroups(templates);
                AddLoadedSection("Prompts", () => FormatCompactList(templates.Select(template => "/" + template.Name)),
                    () => FormatScopeGroups(groups, item => "/" + item.Name, (item, _) => "/" + item.Name));
            }
            if (extensions.Count > 0)
            {
                var groups = BuildScopeGroups(extensions);
                var labels = GetCompactExtensionLabels(extensions);
                AddLoadedSection("Extensions", () => FormatCompactList(labels),
                    () => FormatScopeGroups(groups, item => FormatExtensionDisplayPath(item.Path), (item, _) => FormatExtensionDisplayPath(GetShortPath(item.Path, item.SourceInfo))));
            }
        }

        if (showDiagnostics)
        {
            var diagnostics = context.GetResourceDiagnostics?.Invoke() ?? new ResourceDiagnostics([], [], [], []);
            void AddDiagnostics(string title, IReadOnlyList<ResourceDiagnostic> list)
            {
                if (list.Count == 0) return;
                loadedResourcesContainer.AddChild(new ThemedText(() => $"{theme.Fg("warning", $"[{title}]")}\n{FormatDiagnostics(list, sourceInfos)}", 0, 0));
                loadedResourcesContainer.AddChild(new Spacer(1));
            }
            AddDiagnostics("Skill conflicts", diagnostics.Skills);
            AddDiagnostics("Prompt conflicts", diagnostics.Prompts);
            var extensionDiagnostics = diagnostics.Extensions.ToList();
            var builtinNames = new HashSet<string>(SlashCommands.Builtin.Select(command => command.Name), StringComparer.Ordinal);
            foreach (var command in commands.Where(command => S(command["source"]) == "extension" && builtinNames.Contains(S(command["name"]) ?? "")))
                extensionDiagnostics.Add(new("warning", $"Extension command '/{S(command["name"])}' conflicts with built-in interactive command. Skipping in autocomplete.",
                    SourceInfoOf(command["sourceInfo"])?.Path));
            AddDiagnostics("Extension issues", extensionDiagnostics);
            AddDiagnostics("Theme conflicts", diagnostics.Themes);
        }
    }
}

internal sealed record ResourceDiagnostics(IReadOnlyList<ResourceDiagnostic> Skills, IReadOnlyList<ResourceDiagnostic> Prompts,
    IReadOnlyList<ResourceDiagnostic> Extensions, IReadOnlyList<ResourceDiagnostic> Themes);
