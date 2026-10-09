// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/config-selector.ts.
// TUI component for managing package resources (enable/disable), shown by `pi config`. The package manager's resolved paths
// (core/package-manager.ts PathMetadata, ResolvedResource, ResolvedPaths) arrive as records; the settings it edits go through
// IConfigSelectorSettings, the SettingsManager members the component calls (settings and package sources as Pi JSON).
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source PathMetadata. <c>Scope</c>: "user" | "project" | "temporary"; <c>Origin</c>: "package" | "top-level".</summary>
internal sealed record PathMetadata(string Source, string Scope, string Origin, string? BaseDir = null, string? PackageRoot = null);

/// <summary>Source ResolvedResource.</summary>
internal sealed record ResolvedResource(string Path, bool Enabled, PathMetadata Metadata);

/// <summary>Source ResolvedPaths.</summary>
internal sealed record ResolvedPaths(IReadOnlyList<ResolvedResource> Extensions, IReadOnlyList<ResolvedResource> Skills,
    IReadOnlyList<ResolvedResource> Prompts, IReadOnlyList<ResolvedResource> Themes)
{
    public static ResolvedPaths Empty { get; } = new([], [], [], []);
}

/// <summary>Source ScopedResolvedPaths: the resources as the global and the project settings resolve them.</summary>
internal sealed record ScopedResolvedPaths(ResolvedPaths Global, ResolvedPaths Project);

/// <summary>
/// The SettingsManager members config-selector.ts uses. Settings are the settings.json objects; their <c>extensions</c>, <c>skills</c>,
/// <c>prompts</c> and <c>themes</c> are string arrays and <c>packages</c> holds PackageSource values (a source string or
/// <c>{ source, autoload?, extensions?, skills?, prompts?, themes? }</c>). The component never mutates the returned objects.
/// </summary>
internal interface IConfigSelectorSettings
{
    JsonObject GetGlobalSettings();
    JsonObject GetProjectSettings();
    void SetExtensionPaths(IReadOnlyList<string> paths);
    void SetSkillPaths(IReadOnlyList<string> paths);
    void SetPromptTemplatePaths(IReadOnlyList<string> paths);
    void SetThemePaths(IReadOnlyList<string> paths);
    void SetProjectExtensionPaths(IReadOnlyList<string> paths);
    void SetProjectSkillPaths(IReadOnlyList<string> paths);
    void SetProjectPromptTemplatePaths(IReadOnlyList<string> paths);
    void SetProjectThemePaths(IReadOnlyList<string> paths);
    void SetPackages(JsonArray packages);
    void SetProjectPackages(JsonArray packages);
}

internal sealed partial class ConfigSelectorComponent
{
    private sealed class ResourceItem
    {
        public required string Path { get; init; }
        public bool Enabled { get; set; }
        public required PathMetadata Metadata { get; init; }
        public required string ResourceType { get; init; }
        public required string DisplayName { get; init; }
        public required string GroupKey { get; init; }
        public required string SubgroupKey { get; init; }
    }

    private sealed class ResourceSubgroup
    {
        public required string Type { get; init; }
        public required string Label { get; init; }
        public List<ResourceItem> Items { get; set; } = [];
    }

    private sealed class ResourceGroup
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string Scope { get; init; }
        public required string Origin { get; init; }
        public required string Source { get; init; }
        public List<ResourceSubgroup> Subgroups { get; set; } = [];
    }

    /// <summary>Source FlatEntry: a group header, a subgroup header or an item.</summary>
    private sealed record FlatEntry(string Type, ResourceGroup? Group = null, ResourceSubgroup? Subgroup = null, ResourceItem? Item = null);

    private static class ConfigResources
    {
        public const string BuiltinPathPrefix = "builtin:";
        public static readonly string[] ResourceTypes = ["extensions", "skills", "prompts", "themes"];
        public static readonly Dictionary<string, string> ResourceTypeLabels = new(StringComparer.Ordinal)
        {
            ["extensions"] = "Extensions",
            ["skills"] = "Skills",
            ["prompts"] = "Prompts",
            ["themes"] = "Themes",
        };

        public static readonly StringComparer Locale = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None);

        /// <summary>node:path relative: "" for the same path.</summary>
        public static string Relative(string from, string to)
        {
            var relative = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(from), System.IO.Path.GetFullPath(to));
            return relative == "." ? "" : relative;
        }

        public static string Basename(string path) => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        public static string Dirname(string path) => System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(path)) ?? path;

        public static string FormatBaseDir(string baseDir, string homeDir)
        {
            string displayPath;

            if (baseDir == homeDir) displayPath = "~";
            else if (baseDir.StartsWith(homeDir, StringComparison.Ordinal))
            {
                // Replace home prefix with ~, normalize separators for display
                var rest = baseDir[homeDir.Length..];
                displayPath = $"~{rest.Replace('\\', '/')}";
            }
            else displayPath = baseDir.Replace('\\', '/');

            return displayPath.EndsWith('/') ? displayPath : $"{displayPath}/";
        }

        public static string GetGroupLabel(PathMetadata metadata, string agentDir, string homeDir)
        {
            if (metadata.Origin == "package") return $"{metadata.Source} ({metadata.Scope})";
            if (metadata.Source == "builtin") return metadata.Scope == "user" ? "Built-in" : "Built-in (project override)";
            // Top-level resources
            if (metadata.Source == "auto")
            {
                if (!string.IsNullOrEmpty(metadata.BaseDir))
                    return metadata.Scope == "user"
                        ? $"User ({FormatBaseDir(metadata.BaseDir, homeDir)})"
                        : $"Project ({FormatBaseDir(metadata.BaseDir, homeDir)})";
                return metadata.Scope == "user" ? $"User ({FormatBaseDir(agentDir, homeDir)})" : $"Project ({PiConfig.ConfigDirName}/)";
            }
            return metadata.Scope == "user" ? "User settings" : "Project settings";
        }

        public static List<ResourceGroup> BuildGroups(ResolvedPaths resolved, string agentDir, string homeDir)
        {
            var groupMap = new Dictionary<string, ResourceGroup>(StringComparer.Ordinal);
            var groupOrder = new List<ResourceGroup>();

            void AddToGroup(IReadOnlyList<ResolvedResource> resources, string resourceType)
            {
                foreach (var (path, enabled, metadata) in resources)
                {
                    var groupKey = $"{metadata.Origin}:{metadata.Scope}:{metadata.Source}:{metadata.BaseDir ?? ""}";

                    if (!groupMap.TryGetValue(groupKey, out var group))
                    {
                        group = new ResourceGroup
                        {
                            Key = groupKey,
                            Label = GetGroupLabel(metadata, agentDir, homeDir),
                            Scope = metadata.Scope,
                            Origin = metadata.Origin,
                            Source = metadata.Source,
                        };
                        groupMap[groupKey] = group;
                        groupOrder.Add(group);
                    }

                    var subgroupKey = $"{groupKey}:{resourceType}";

                    var subgroup = group.Subgroups.Find(sg => sg.Type == resourceType);
                    if (subgroup is null)
                    {
                        subgroup = new ResourceSubgroup { Type = resourceType, Label = ResourceTypeLabels[resourceType] };
                        group.Subgroups.Add(subgroup);
                    }

                    var fileName = Basename(path);
                    var parentFolder = Basename(Dirname(path));
                    string displayName;
                    if (metadata.Source == "builtin") displayName = path[Math.Min(BuiltinPathPrefix.Length, path.Length)..];
                    else if (resourceType == "extensions" && parentFolder != "extensions") displayName = $"{parentFolder}/{fileName}";
                    else if (resourceType == "skills" && fileName == "SKILL.md") displayName = parentFolder;
                    else displayName = fileName;
                    subgroup.Items.Add(new ResourceItem
                    {
                        Path = path,
                        Enabled = enabled,
                        Metadata = metadata,
                        ResourceType = resourceType,
                        DisplayName = displayName,
                        GroupKey = groupKey,
                        SubgroupKey = subgroupKey,
                    });
                }
            }

            AddToGroup(resolved.Extensions, "extensions");
            AddToGroup(resolved.Skills, "skills");
            AddToGroup(resolved.Prompts, "prompts");
            AddToGroup(resolved.Themes, "themes");

            // Sort groups: packages first, then top-level; user before project
            var groups = groupOrder.OrderBy(group => group, Comparer<ResourceGroup>.Create((a, b) =>
            {
                if (a.Origin != b.Origin) return a.Origin == "package" ? -1 : 1;
                if (a.Scope != b.Scope) return a.Scope == "user" ? -1 : 1;
                return Locale.Compare(a.Source, b.Source);
            })).ToList();

            // Sort subgroups within each group by type order, and items by name
            foreach (var group in groups)
            {
                group.Subgroups = [.. group.Subgroups.OrderBy(subgroup => Array.IndexOf(ResourceTypes, subgroup.Type))];
                foreach (var subgroup in group.Subgroups)
                    subgroup.Items = [.. subgroup.Items.OrderBy(item => item.DisplayName, Locale)];
            }

            return groups;
        }

        public static List<string> StringArray(JsonNode? node) =>
            node is JsonArray array ? [.. array.Select(entry => entry is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>()] : [];

        public static string PackageSourceString(JsonNode? pkg) =>
            pkg is JsonObject obj ? obj["source"]?.GetValue<string>() ?? "" : pkg is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

        public static JsonArray ToJsonArray(IEnumerable<string> values) => [.. values.Select(value => (JsonNode)JsonValue.Create(value))];
    }

    private sealed class ConfigSelectorHeader(string writeScope, bool projectModeAvailable) : IComponent
    {
        private string writeScope = writeScope;

        public void SetWriteScope(string value) => writeScope = value;

        public void Invalidate() { }

        public List<string> Render(int width)
        {
            var title = theme.Bold(writeScope == "project" ? "Project Local Resources" : "Global Resources");
            var sep = theme.Fg("muted", " · ");
            var switchHint = projectModeAvailable ? KeybindingHints.KeyHint("tui.input.tab", "switch mode") + sep : "";
            var actionHint = writeScope == "project" ? KeybindingHints.RawKeyHint("space", "cycle inherit/+/-") : KeybindingHints.RawKeyHint("space", "toggle");
            var hint = switchHint + actionHint + sep + KeybindingHints.RawKeyHint("esc", "close");
            var spacing = Math.Max(1, width - TextUtils.VisibleWidth(title) - TextUtils.VisibleWidth(hint));
            var scopeHint = writeScope == "project"
                ? theme.Fg("muted", $"{PiConfig.ConfigDirName}/settings.json · inherited global resources are dimmed")
                : theme.Fg("muted", $"~/{PiConfig.ConfigDirName}/agent/settings.json");

            return
            [
                TextUtils.TruncateToWidth($"{title}{new string(' ', spacing)}{hint}", width, ""),
                TextUtils.TruncateToWidth(scopeHint, width, ""),
            ];
        }
    }

    private sealed class ResourceList : IComponent, IFocusable, IInputHandler
    {
        private readonly Dictionary<string, List<ResourceGroup>> groupsByScope;
        private List<FlatEntry> flatItems = [];
        private List<FlatEntry> filteredItems = [];
        private int selectedIndex;
        private readonly Input searchInput;
        private readonly int maxVisible;
        private readonly IConfigSelectorSettings settingsManager;
        private readonly string cwd;
        private readonly string agentDir;
        private readonly string homeDir;
        private string writeScope;
        private readonly Dictionary<string, bool> inheritedEnabledByKey;

        public Action? OnCancel { get; set; }
        public Action? OnExit { get; set; }
        public Action<ResourceItem, bool>? OnToggle { get; set; }
        public Action? OnSwitchMode { get; set; }

        private bool focused;
        public bool Focused
        {
            get => focused;
            set { focused = value; searchInput.Focused = value; }
        }

        public ResourceList(Dictionary<string, List<ResourceGroup>> groupsByScope, IConfigSelectorSettings settingsManager, string cwd, string agentDir,
            string homeDir, int? terminalHeight = null, string writeScope = "global")
        {
            this.groupsByScope = groupsByScope;
            this.settingsManager = settingsManager;
            this.cwd = cwd;
            this.agentDir = agentDir;
            this.homeDir = homeDir;
            this.writeScope = writeScope;
            inheritedEnabledByKey = BuildInheritedEnabledMap(groupsByScope["global"]);
            searchInput = new Input();
            // 8 lines of chrome: top spacer + top border + spacer + header (2 lines) + spacer + bottom spacer + bottom border
            const int chrome = 8;
            maxVisible = Math.Max(5, (terminalHeight ?? 24) - chrome);
            BuildFlatList();
            filteredItems = [.. flatItems];
        }

        public void SetWriteScope(string value)
        {
            writeScope = value;
            BuildFlatList();
            FilterItems(searchInput.GetValue());
        }

        private List<ResourceGroup> Groups => groupsByScope[writeScope];

        private Dictionary<string, bool> BuildInheritedEnabledMap(List<ResourceGroup> groups)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var group in groups)
                foreach (var subgroup in group.Subgroups)
                    foreach (var item in subgroup.Items)
                        result[GetResourceItemKey(item)] = item.Enabled;
            return result;
        }

        private void BuildFlatList()
        {
            flatItems = [];
            foreach (var group in Groups)
            {
                flatItems.Add(new("group", group));
                foreach (var subgroup in group.Subgroups)
                {
                    flatItems.Add(new("subgroup", group, subgroup));
                    foreach (var item in subgroup.Items) flatItems.Add(new("item", Item: item));
                }
            }
            // Start selection on first item (not header)
            selectedIndex = flatItems.FindIndex(e => e.Type == "item");
            if (selectedIndex < 0) selectedIndex = 0;
        }

        private int FindNextItem(int fromIndex, int direction)
        {
            var idx = fromIndex + direction;
            while (idx >= 0 && idx < filteredItems.Count)
            {
                if (filteredItems[idx].Type == "item") return idx;
                idx += direction;
            }
            return fromIndex; // Stay at current if no item found
        }

        private void FilterItems(string query)
        {
            if (TextUtils.JsTrim(query).Length == 0)
            {
                filteredItems = [.. flatItems];
                SelectFirstItem();
                return;
            }

            var lowerQuery = query.ToLowerInvariant();
            var matchingItems = new HashSet<ResourceItem>();
            var matchingSubgroups = new HashSet<ResourceSubgroup>();
            var matchingGroups = new HashSet<ResourceGroup>();

            foreach (var entry in flatItems)
            {
                if (entry is { Type: "item", Item: { } item } &&
                    (item.DisplayName.ToLowerInvariant().Contains(lowerQuery, StringComparison.Ordinal) ||
                     item.ResourceType.ToLowerInvariant().Contains(lowerQuery, StringComparison.Ordinal) ||
                     item.Path.ToLowerInvariant().Contains(lowerQuery, StringComparison.Ordinal)))
                    matchingItems.Add(item);
            }

            // Find which subgroups and groups contain matching items
            foreach (var group in Groups)
                foreach (var subgroup in group.Subgroups)
                    foreach (var item in subgroup.Items)
                        if (matchingItems.Contains(item))
                        {
                            matchingSubgroups.Add(subgroup);
                            matchingGroups.Add(group);
                        }

            filteredItems = [];
            foreach (var entry in flatItems)
            {
                if (entry.Type == "group" && matchingGroups.Contains(entry.Group!)) filteredItems.Add(entry);
                else if (entry.Type == "subgroup" && matchingSubgroups.Contains(entry.Subgroup!)) filteredItems.Add(entry);
                else if (entry.Type == "item" && matchingItems.Contains(entry.Item!)) filteredItems.Add(entry);
            }

            SelectFirstItem();
        }

        private void SelectFirstItem()
        {
            var firstItemIndex = filteredItems.FindIndex(e => e.Type == "item");
            selectedIndex = firstItemIndex >= 0 ? firstItemIndex : 0;
        }

        public void UpdateItem(ResourceItem item, bool enabled)
        {
            item.Enabled = enabled;
            // Update in groups too
            foreach (var group in Groups)
                foreach (var subgroup in group.Subgroups)
                {
                    var found = subgroup.Items.Find(i => i.Path == item.Path && i.ResourceType == item.ResourceType);
                    if (found is not null)
                    {
                        found.Enabled = enabled;
                        return;
                    }
                }
        }

        public void Invalidate() { }

        public List<string> Render(int width)
        {
            var lines = new List<string>();

            // Search input
            lines.AddRange(searchInput.Render(width));
            lines.Add("");

            if (filteredItems.Count == 0)
            {
                lines.Add(theme.Fg("muted", "  No resources found"));
                return lines;
            }

            // Calculate visible range
            var startIndex = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, filteredItems.Count - maxVisible));
            var endIndex = Math.Min(startIndex + maxVisible, filteredItems.Count);

            for (var i = startIndex; i < endIndex; i++)
            {
                var entry = filteredItems[i];
                var isSelected = i == selectedIndex;

                if (entry.Type == "group")
                {
                    // Main group header (no cursor)
                    var inherited = writeScope == "project" && entry.Group!.Scope == "user";
                    var label = theme.Bold($"{entry.Group!.Label}{(inherited ? " · inherited global" : "")}");
                    var groupLine = theme.Fg(inherited ? "dim" : "accent", label);
                    lines.Add(TextUtils.TruncateToWidth($"  {groupLine}", width, ""));
                }
                else if (entry.Type == "subgroup")
                {
                    // Subgroup header (indented, no cursor)
                    var color = writeScope == "project" && entry.Group!.Scope == "user" ? "dim" : "muted";
                    var subgroupLine = theme.Fg(color, entry.Subgroup!.Label);
                    lines.Add(TextUtils.TruncateToWidth($"    {subgroupLine}", width, ""));
                }
                else
                {
                    // Resource item (cursor only on items)
                    var item = entry.Item!;
                    var cursor = isSelected ? "> " : "  ";
                    var dimmed = IsDimmedItem(item);
                    var nameText = isSelected && !dimmed ? theme.Bold(item.DisplayName) : item.DisplayName;
                    var name = dimmed ? theme.Fg("dim", nameText) : nameText;
                    lines.Add(TextUtils.TruncateToWidth($"{cursor}    {RenderCheckbox(item)} {name}{GetItemSuffix(item)}", width, "..."));
                }
            }

            // Scroll indicator
            if (startIndex > 0 || endIndex < filteredItems.Count)
            {
                var itemCount = filteredItems.Count(e => e.Type == "item");
                var currentItemIndex = filteredItems.Take(selectedIndex).Count(e => e.Type == "item") + 1;
                lines.Add(theme.Fg("dim", $"  ({currentItemIndex.ToString(CultureInfo.InvariantCulture)}/{itemCount.ToString(CultureInfo.InvariantCulture)})"));
            }

            return lines;
        }

        public void HandleInput(string data)
        {
            var kb = KeybindingsManager.Global;

            if (kb.Matches(data, "tui.select.up"))
            {
                selectedIndex = FindNextItem(selectedIndex, -1);
                return;
            }
            if (kb.Matches(data, "tui.select.down"))
            {
                selectedIndex = FindNextItem(selectedIndex, 1);
                return;
            }
            if (kb.Matches(data, "tui.select.pageUp"))
            {
                // Jump up by maxVisible, then find nearest item
                var target = Math.Max(0, selectedIndex - maxVisible);
                while (target < filteredItems.Count && filteredItems[target].Type != "item") target++;
                if (target < filteredItems.Count) selectedIndex = target;
                return;
            }
            if (kb.Matches(data, "tui.select.pageDown"))
            {
                // Jump down by maxVisible, then find nearest item
                var target = Math.Min(filteredItems.Count - 1, selectedIndex + maxVisible);
                while (target >= 0 && filteredItems[target].Type != "item") target--;
                if (target >= 0) selectedIndex = target;
                return;
            }
            if (kb.Matches(data, "tui.select.cancel"))
            {
                OnCancel?.Invoke();
                return;
            }
            if (Keys.Matches(data, "ctrl+c"))
            {
                OnExit?.Invoke();
                return;
            }
            if (kb.Matches(data, "tui.input.tab"))
            {
                OnSwitchMode?.Invoke();
                return;
            }
            if (data == " " || kb.Matches(data, "tui.select.confirm"))
            {
                var entry = selectedIndex >= 0 && selectedIndex < filteredItems.Count ? filteredItems[selectedIndex] : null;
                if (entry is { Type: "item", Item: { } item } && (writeScope == "project" || GetItemScope(item) == "user"))
                {
                    if (ToggleResource(item) is { } newEnabled)
                    {
                        UpdateItem(item, newEnabled);
                        OnToggle?.Invoke(item, newEnabled);
                    }
                }
                return;
            }

            // Pass to search input
            searchInput.HandleInput(data);
            FilterItems(searchInput.GetValue());
        }

        private bool? ToggleResource(ResourceItem item)
        {
            if (writeScope == "project")
            {
                var state = GetNextOverrideState(item);
                if (!SetProjectResourceOverride(item, state)) return null;
                return state == "inherit" ? GetInheritedEnabled(item) : state == "load";
            }

            var enabled = !item.Enabled;
            if (item.Metadata.Origin == "top-level") ToggleTopLevelResource(item, enabled);
            else TogglePackageResource(item, enabled);
            return enabled;
        }

        private static string StripPatternPrefix(string pattern) =>
            pattern.StartsWith('!') || pattern.StartsWith('+') || pattern.StartsWith('-') ? pattern[1..] : pattern;

        private void ToggleTopLevelResource(ResourceItem item, bool enabled)
        {
            var scope = item.Metadata.Scope;
            var settings = scope == "project" ? settingsManager.GetProjectSettings() : settingsManager.GetGlobalSettings();

            var arrayKey = item.ResourceType;
            var current = ConfigResources.StringArray(settings[arrayKey]);

            // Generate pattern for this resource
            var pattern = GetResourcePattern(item);
            var disablePattern = $"-{pattern}";
            var enablePattern = $"+{pattern}";

            // Filter out existing patterns for this resource
            var updated = current.Where(p => StripPatternPrefix(p) != pattern).ToList();

            updated.Add(enabled ? enablePattern : disablePattern);

            if (scope == "project")
            {
                if (arrayKey == "extensions") settingsManager.SetProjectExtensionPaths(updated);
                else if (arrayKey == "skills") settingsManager.SetProjectSkillPaths(updated);
                else if (arrayKey == "prompts") settingsManager.SetProjectPromptTemplatePaths(updated);
                else if (arrayKey == "themes") settingsManager.SetProjectThemePaths(updated);
            }
            else
            {
                if (arrayKey == "extensions") settingsManager.SetExtensionPaths(updated);
                else if (arrayKey == "skills") settingsManager.SetSkillPaths(updated);
                else if (arrayKey == "prompts") settingsManager.SetPromptTemplatePaths(updated);
                else if (arrayKey == "themes") settingsManager.SetThemePaths(updated);
            }
        }

        private static List<JsonNode?> ClonePackages(JsonObject settings) =>
            settings["packages"] is JsonArray array ? [.. array.Select(pkg => pkg?.DeepClone())] : [];

        private static bool HasFilters(JsonObject pkg) => ConfigResources.ResourceTypes.Any(key => pkg.ContainsKey(key));

        private void TogglePackageResource(ResourceItem item, bool enabled)
        {
            var scope = item.Metadata.Scope;
            var settings = scope == "project" ? settingsManager.GetProjectSettings() : settingsManager.GetGlobalSettings();

            var packages = ClonePackages(settings);
            var pkgIndex = packages.FindIndex(pkg => ConfigResources.PackageSourceString(pkg) == item.Metadata.Source);

            if (pkgIndex == -1) return;

            // Convert string to object form if needed
            if (packages[pkgIndex] is not JsonObject pkg)
            {
                pkg = new JsonObject { ["source"] = ConfigResources.PackageSourceString(packages[pkgIndex]) };
                packages[pkgIndex] = pkg;
            }

            // Get the resource array for this type
            var arrayKey = item.ResourceType;
            var current = ConfigResources.StringArray(pkg[arrayKey]);

            // Generate pattern relative to package root
            var pattern = GetPackageResourcePattern(item);
            var disablePattern = $"-{pattern}";
            var enablePattern = $"+{pattern}";

            // Filter out existing patterns for this resource
            var updated = current.Where(p => StripPatternPrefix(p) != pattern).ToList();

            updated.Add(enabled ? enablePattern : disablePattern);

            if (updated.Count > 0) pkg[arrayKey] = ConfigResources.ToJsonArray(updated);
            else pkg.Remove(arrayKey);

            // Clean up empty filter object
            if (!HasFilters(pkg)) packages[pkgIndex] = JsonValue.Create(ConfigResources.PackageSourceString(pkg));

            var array = new JsonArray([.. packages]);
            if (scope == "project") settingsManager.SetProjectPackages(array);
            else settingsManager.SetPackages(array);
        }

        private string RenderCheckbox(ResourceItem item)
        {
            if (writeScope == "project")
            {
                var state = GetProjectOverrideState(item);
                if (state == "load") return theme.Fg("success", "[+]");
                if (state == "unload") return theme.Fg("warning", "[-]");
                return theme.Fg("dim", item.Enabled ? "[x]" : "[ ]");
            }
            return item.Enabled ? theme.Fg("success", "[x]") : theme.Fg("dim", "[ ]");
        }

        private string GetItemSuffix(ResourceItem item)
        {
            if (writeScope != "project") return "";
            var state = GetProjectOverrideState(item);
            if (state == "load") return theme.Fg("muted", "  project load");
            if (state == "unload") return theme.Fg("muted", "  project unload");
            return IsInheritedGlobalItem(item) ? theme.Fg("dim", "  inherited global") : "";
        }

        private bool IsDimmedItem(ResourceItem item) =>
            writeScope == "project" && IsInheritedGlobalItem(item) && GetProjectOverrideState(item) == "inherit";

        private bool SetProjectResourceOverride(ResourceItem item, string state) =>
            item.Metadata.Origin == "top-level" ? SetProjectTopLevelOverride(item, state) : SetProjectPackageOverride(item, state);

        private bool SetProjectTopLevelOverride(ResourceItem item, string state)
        {
            var current = ConfigResources.StringArray(settingsManager.GetProjectSettings()[item.ResourceType]);
            var pattern = IsInheritedGlobalItem(item) ? item.Path : GetResourcePatternForScope(item, "project");
            var patterns = GetTopLevelOverridePatterns(item, "project");
            var updated = current.Where(entry =>
            {
                var target = GetPatternEntryTarget(entry);
                if ((entry.StartsWith('!') || entry.StartsWith('+') || entry.StartsWith('-')) && patterns.Contains(target)) return false;
                return !(state == "inherit" && IsInheritedGlobalItem(item) && target == pattern);
            }).ToList();
            if (state != "inherit")
            {
                // Project entries name inherited files to override them. Built-in paths need no entry.
                if (IsInheritedGlobalItem(item) && item.Metadata.Source != "builtin" && !updated.Contains(pattern)) updated.Add(pattern);
                updated.Add($"{(state == "load" ? "+" : "-")}{pattern}");
            }
            SetProjectTopLevelPaths(item.ResourceType, updated);
            return true;
        }

        private void SetProjectTopLevelPaths(string key, List<string> paths)
        {
            if (key == "extensions") settingsManager.SetProjectExtensionPaths(paths);
            else if (key == "skills") settingsManager.SetProjectSkillPaths(paths);
            else if (key == "prompts") settingsManager.SetProjectPromptTemplatePaths(paths);
            else settingsManager.SetProjectThemePaths(paths);
        }

        private bool SetProjectPackageOverride(ResourceItem item, string state)
        {
            var packages = ClonePackages(settingsManager.GetProjectSettings());
            var pkgIndex = packages.FindIndex(pkg =>
                PackageSourceStringMatches(item.Metadata.Source, GetItemScope(item), ConfigResources.PackageSourceString(pkg), "project"));
            if (pkgIndex == -1)
            {
                if (state == "inherit") return false;
                packages.Add(CreatePackageOverrideSource(item));
                pkgIndex = packages.Count - 1;
            }
            if (packages[pkgIndex] is not { } node) return false;
            if (node is not JsonObject pkg)
            {
                pkg = new JsonObject { ["source"] = ConfigResources.PackageSourceString(node) };
                packages[pkgIndex] = pkg;
            }
            var pattern = GetPackageResourcePattern(item);
            var updated = ConfigResources.StringArray(pkg[item.ResourceType]).Where(entry => GetPatternEntryTarget(entry) != pattern).ToList();
            if (state != "inherit") updated.Add($"{(state == "load" ? "+" : "-")}{pattern}");
            if (updated.Count > 0) pkg[item.ResourceType] = ConfigResources.ToJsonArray(updated);
            else pkg.Remove(item.ResourceType);
            if (!HasFilters(pkg))
            {
                if (pkg["autoload"] is JsonValue autoload && autoload.TryGetValue<bool>(out var flag) && !flag) packages.RemoveAt(pkgIndex);
                else packages[pkgIndex] = JsonValue.Create(ConfigResources.PackageSourceString(pkg));
            }
            settingsManager.SetProjectPackages(new JsonArray([.. packages]));
            return true;
        }

        private string GetNextOverrideState(ResourceItem item)
        {
            var state = GetProjectOverrideState(item);
            var inheritedEnabled = GetInheritedEnabled(item);
            if (state == "inherit") return inheritedEnabled ? "unload" : "load";
            if (state == "unload") return inheritedEnabled ? "load" : "inherit";
            return inheritedEnabled ? "inherit" : "unload";
        }

        private string GetProjectOverrideState(ResourceItem item)
        {
            if (writeScope != "project") return "inherit";
            if (item.Metadata.Origin == "top-level")
                return GetOverrideStateFromEntries(ConfigResources.StringArray(settingsManager.GetProjectSettings()[item.ResourceType]),
                    GetTopLevelOverridePatterns(item, "project"), false);
            if (FindMatchingPackageSource(item, "project") is not JsonObject pkg) return "inherit";
            if (!pkg.ContainsKey(item.ResourceType)) return "inherit";
            var autoloadDisabled = pkg["autoload"] is JsonValue autoload && autoload.TryGetValue<bool>(out var flag) && !flag;
            return GetOverrideStateFromEntries(ConfigResources.StringArray(pkg[item.ResourceType]), [GetPackageResourcePattern(item)], !autoloadDisabled);
        }

        private string GetOverrideStateFromEntries(List<string> entries, HashSet<string> patterns, bool emptyArrayIsUnload)
        {
            if (entries.Count == 0 && emptyArrayIsUnload) return "unload";
            var state = "inherit";
            foreach (var entry in entries)
            {
                if (!patterns.Contains(GetPatternEntryTarget(entry))) continue;
                state = entry.StartsWith('!') || entry.StartsWith('-') ? "unload" : "load";
            }
            return state;
        }

        private bool GetInheritedEnabled(ResourceItem item) =>
            inheritedEnabledByKey.TryGetValue(GetResourceItemKey(item), out var enabled) ? enabled : GetItemScope(item) == "user" ? item.Enabled : true;

        private bool IsInheritedGlobalItem(ResourceItem item) =>
            GetItemScope(item) == "user" || inheritedEnabledByKey.ContainsKey(GetResourceItemKey(item));

        private HashSet<string> GetTopLevelOverridePatterns(ResourceItem item, string scope)
        {
            var baseDir = GetTopLevelBaseDir(scope);
            var patterns = new HashSet<string>(StringComparer.Ordinal)
            {
                GetResourcePatternForScope(item, scope),
                item.Path,
                ConfigResources.Relative(baseDir, item.Path),
            };
            if (!string.IsNullOrEmpty(item.Metadata.BaseDir)) patterns.Add(ConfigResources.Relative(item.Metadata.BaseDir, item.Path));
            return patterns;
        }

        private string GetResourcePatternForScope(ResourceItem item, string scope)
        {
            var sourceScope = GetItemScope(item);
            if (scope != sourceScope || item.Metadata.Source == "builtin") return item.Path;
            var baseDir = item.Metadata.BaseDir ?? GetTopLevelBaseDir(sourceScope);
            return ConfigResources.Relative(baseDir, item.Path);
        }

        private JsonNode CreatePackageOverrideSource(ResourceItem item)
        {
            var source = item.Metadata.Source;
            if (!PiPaths.IsLocalPath(source)) return new JsonObject { ["source"] = source, ["autoload"] = false };
            var sourcePath = PiPaths.ResolvePath(source, GetTopLevelBaseDir(GetItemScope(item)), homeDir, trim: true);
            var relative = ConfigResources.Relative(GetTopLevelBaseDir("project"), sourcePath);
            return new JsonObject { ["source"] = relative.Length > 0 ? relative : ".", ["autoload"] = false };
        }

        private bool PackageSourceStringMatches(string leftSource, string leftScope, string rightSource, string rightScope)
        {
            if (leftSource == rightSource) return true;
            if (!PiPaths.IsLocalPath(leftSource) || !PiPaths.IsLocalPath(rightSource)) return false;
            var left = PiPaths.ResolvePath(leftSource, GetTopLevelBaseDir(leftScope), homeDir, trim: true);
            var right = PiPaths.ResolvePath(rightSource, GetTopLevelBaseDir(rightScope), homeDir, trim: true);
            return left == right;
        }

        private JsonNode? FindMatchingPackageSource(ResourceItem item, string targetScope)
        {
            var settings = targetScope == "project" ? settingsManager.GetProjectSettings() : settingsManager.GetGlobalSettings();
            if (settings["packages"] is not JsonArray packages) return null;
            return packages.FirstOrDefault(pkg =>
                PackageSourceStringMatches(item.Metadata.Source, GetItemScope(item), ConfigResources.PackageSourceString(pkg), targetScope));
        }

        private static string GetPatternEntryTarget(string entry) => StripPatternPrefix(entry);

        private static string GetResourceItemKey(ResourceItem item) => $"{item.ResourceType}:{PiPaths.Canonicalize(item.Path)}";

        private static string GetItemScope(ResourceItem item) => item.Metadata.Scope == "project" ? "project" : "user";

        private string GetTopLevelBaseDir(string scope) => scope == "project" ? Path.Join(cwd, PiConfig.ConfigDirName) : agentDir;

        private string GetResourcePattern(ResourceItem item)
        {
            if (item.Metadata.Source == "builtin") return item.Path;
            var scope = item.Metadata.Scope;
            var baseDir = item.Metadata.BaseDir ?? GetTopLevelBaseDir(scope);
            return ConfigResources.Relative(baseDir, item.Path);
        }

        private static string GetPackageResourcePattern(ResourceItem item)
        {
            var baseDir = item.Metadata.BaseDir ?? ConfigResources.Dirname(item.Path);
            return ConfigResources.Relative(baseDir, item.Path);
        }
    }
}

internal sealed partial class ConfigSelectorComponent : Container, IFocusable
{
    private readonly ConfigSelectorHeader header;
    private readonly ResourceList resourceList;
    private string writeScope;

    private bool focused;
    public bool Focused
    {
        get => focused;
        set { focused = value; resourceList.Focused = value; }
    }

    /// <param name="writeScope">"global" or "project".</param>
    /// <param name="homeDir">os.homedir() for "~" display and path resolution; defaults to the user profile.</param>
    public ConfigSelectorComponent(ScopedResolvedPaths resolvedPaths, IConfigSelectorSettings settingsManager, string cwd, string agentDir,
        Action onClose, Action onExit, Action requestRender, int? terminalHeight = null, string writeScope = "global", bool projectModeAvailable = true,
        string? homeDir = null)
    {
        this.writeScope = writeScope;
        var home = homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var groupsByScope = new Dictionary<string, List<ResourceGroup>>(StringComparer.Ordinal)
        {
            ["global"] = ConfigResources.BuildGroups(resolvedPaths.Global, agentDir, home),
            ["project"] = ConfigResources.BuildGroups(resolvedPaths.Project, agentDir, home),
        };

        // Add header
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        header = new ConfigSelectorHeader(this.writeScope, projectModeAvailable);
        AddChild(header);
        AddChild(new Spacer(1));

        // Resource list
        resourceList = new ResourceList(groupsByScope, settingsManager, cwd, agentDir, home, terminalHeight, this.writeScope)
        {
            OnCancel = onClose,
            OnExit = onExit,
            OnToggle = (_, _) => requestRender(),
        };
        if (projectModeAvailable)
        {
            resourceList.OnSwitchMode = () =>
            {
                SwitchWriteScope();
                requestRender();
            };
        }
        AddChild(resourceList);

        // Bottom border
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    private void SwitchWriteScope()
    {
        writeScope = writeScope == "global" ? "project" : "global";
        header.SetWriteScope(writeScope);
        resourceList.SetWriteScope(writeScope);
    }

    /// <summary>Source getResourceList(): the focus target (an IInputHandler and IFocusable component).</summary>
    public IComponent GetResourceList() => resourceList;
}
