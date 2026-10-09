// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/scoped-models-selector.ts.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source ModelsConfig. <c>EnabledModelIds</c> null means all enabled (no filter); a list is the explicit ordered set of
/// <c>provider/id</c> keys.</summary>
internal sealed record ModelsConfig(IReadOnlyList<JsonObject> AllModels, IReadOnlyList<string>? EnabledModelIds, string? RefreshStatus = null);

/// <summary>Source ModelsCallbacks.</summary>
internal sealed record ModelsCallbacks(
    /* Called whenever the enabled model set or order changes (session-only, no persist) */
    Action<IReadOnlyList<string>?> OnChange,
    /* Called when user wants to persist current selection to settings */
    Action<IReadOnlyList<string>?> OnPersist,
    Action OnCancel);

/// <summary>Component for enabling/disabling models for Ctrl+P cycling. Changes are session-only until explicitly persisted with
/// Ctrl+S.</summary>
internal sealed class ScopedModelsSelectorComponent : Container, IFocusable, IInputHandler
{
    // EnabledIds: null = all enabled (no filter), list = explicit ordered list

    private static bool IsEnabled(List<string>? enabledIds, string id) => enabledIds is null || enabledIds.Contains(id);

    /// <summary>Collapse an explicit list back to null (= all enabled) when it covers every available model.</summary>
    private static List<string>? NormalizeEnabled(List<string> result, List<string> allIds) =>
        result.Count == allIds.Count && result.All(allIds.Contains) ? null : result;

    private static List<string>? Toggle(List<string>? enabledIds, List<string> allIds, string id)
    {
        if (enabledIds is null) return allIds.Where(modelId => modelId != id).ToList();
        var index = enabledIds.IndexOf(id);
        if (index >= 0) return [.. enabledIds.GetRange(0, index), .. enabledIds.GetRange(index + 1, enabledIds.Count - index - 1)];
        return NormalizeEnabled([.. enabledIds, id], allIds);
    }

    private static List<string>? EnableAll(List<string>? enabledIds, List<string> allIds, List<string>? targetIds = null)
    {
        if (enabledIds is null) return null; // Already all enabled
        var targets = targetIds ?? allIds;
        var result = new List<string>(enabledIds);
        foreach (var id in targets) if (!result.Contains(id)) result.Add(id);
        return NormalizeEnabled(result, allIds);
    }

    private static List<string> ClearAll(List<string>? enabledIds, List<string> allIds, List<string>? targetIds = null)
    {
        if (enabledIds is null) return targetIds is not null ? allIds.Where(id => !targetIds.Contains(id)).ToList() : [];
        var targets = new HashSet<string>(targetIds ?? enabledIds, StringComparer.Ordinal);
        return enabledIds.Where(id => !targets.Contains(id)).ToList();
    }

    private static List<string>? Move(List<string>? enabledIds, string id, int delta)
    {
        if (enabledIds is null) return null;
        var list = new List<string>(enabledIds);
        var index = list.IndexOf(id);
        if (index < 0) return list;
        var newIndex = index + delta;
        if (newIndex < 0 || newIndex >= list.Count) return list;
        var result = new List<string>(list);
        (result[index], result[newIndex]) = (result[newIndex], result[index]);
        return result;
    }

    private static List<string> GetSortedIds(List<string>? enabledIds, List<string> allIds)
    {
        if (enabledIds is null) return allIds;
        var enabledSet = new HashSet<string>(enabledIds, StringComparer.Ordinal);
        return [.. enabledIds, .. allIds.Where(id => !enabledSet.Contains(id))];
    }

    private sealed record ModelItem(string FullId, JsonObject? Model, bool Enabled);

    private readonly Dictionary<string, JsonObject> modelsById = new(StringComparer.Ordinal);
    private List<string> allIds = [];
    private List<string>? enabledIds;
    private List<ModelItem> filteredItems;
    private int selectedIndex;
    private readonly Input searchInput;

    // Focusable implementation - propagate to searchInput for IME cursor positioning
    private bool focused;
    public bool Focused
    {
        get => focused;
        set { focused = value; searchInput.Focused = value; }
    }

    private readonly Container listContainer;
    private readonly Text footerText;
    private readonly ModelsCallbacks callbacks;
    private readonly int maxVisible = 8;
    private bool isDirty;
    private readonly Text? refreshStatusText;

    public ScopedModelsSelectorComponent(ModelsConfig config, ModelsCallbacks callbacks)
    {
        this.callbacks = callbacks;

        foreach (var model in config.AllModels)
        {
            var fullId = $"{ModelJson.Provider(model)}/{ModelJson.Id(model)}";
            modelsById[fullId] = model;
            allIds.Add(fullId);
        }

        enabledIds = config.EnabledModelIds is null ? null : [.. config.EnabledModelIds];
        filteredItems = BuildItems();

        // Header
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold("Model Configuration")), 0, 0));
        AddChild(new Text(theme.Fg("muted", $"Session-only. {KeybindingHints.KeyDisplayText("app.models.save")} to save to settings."), 0, 0));
        AddChild(new Spacer(1));

        // Search input
        searchInput = new Input();
        AddChild(searchInput);
        AddChild(new Spacer(1));

        // List container
        listContainer = new Container();
        AddChild(listContainer);

        // Footer hint
        AddChild(new Spacer(1));
        if (!string.IsNullOrEmpty(config.RefreshStatus))
        {
            refreshStatusText = new Text(theme.Fg("muted", $"  {config.RefreshStatus}"), 0, 0);
            AddChild(refreshStatusText);
        }
        footerText = new Text(GetFooterText(), 0, 0);
        AddChild(footerText);

        AddChild(new DynamicBorder());
        UpdateList();
    }

    /// <summary>Source updateModels: replaces the available models; <paramref name="updateEnabled"/> false keeps the enabled set
    /// (upstream's omitted second argument).</summary>
    public void UpdateModels(IReadOnlyList<JsonObject> models, IReadOnlyList<string>? enabledModelIds = null, bool updateEnabled = false)
    {
        var selectedId = selectedIndex < filteredItems.Count ? filteredItems[selectedIndex].FullId : null;
        if (updateEnabled) enabledIds = enabledModelIds is null ? null : [.. enabledModelIds];
        modelsById.Clear();
        allIds = [];
        foreach (var model in models)
        {
            var fullId = $"{ModelJson.Provider(model)}/{ModelJson.Id(model)}";
            modelsById[fullId] = model;
            allIds.Add(fullId);
        }
        Refresh();
        var refreshedIndex = selectedId is not null ? filteredItems.FindIndex(item => item.FullId == selectedId) : -1;
        if (refreshedIndex >= 0)
        {
            selectedIndex = refreshedIndex;
            UpdateList();
        }
    }

    /// <summary>Source setRefreshStatus; <paramref name="kind"/> is "muted", "success" or "warning".</summary>
    public void SetRefreshStatus(string message, string kind) => refreshStatusText?.SetText(theme.Fg(kind, $"  {message}"));

    private List<ModelItem> BuildItems() =>
        GetSortedIds(enabledIds, allIds).Select(id => new ModelItem(id, modelsById.GetValueOrDefault(id), IsEnabled(enabledIds, id))).ToList();

    private string GetFooterText()
    {
        var enabledCount = enabledIds?.Count(modelsById.ContainsKey) ?? allIds.Count;
        var unavailableCount = enabledIds?.Count(id => !modelsById.ContainsKey(id)) ?? 0;
        var allEnabled = enabledIds is null;
        var countText = allEnabled
            ? "all enabled"
            : $"{enabledCount}/{allIds.Count} enabled{(unavailableCount != 0 ? $" · {unavailableCount} unavailable" : "")}";
        string[] parts =
        [
            $"{KeybindingHints.KeyDisplayText("tui.select.confirm")} toggle",
            $"{KeybindingHints.KeyDisplayText("app.models.enableAll")} all",
            $"{KeybindingHints.KeyDisplayText("app.models.clearAll")} clear",
            $"{KeybindingHints.KeyDisplayText("app.models.toggleProvider")} provider",
            $"{KeybindingHints.KeyDisplayText("app.models.reorderUp")}/{KeybindingHints.KeyDisplayText("app.models.reorderDown")} reorder",
            $"{KeybindingHints.KeyDisplayText("app.models.save")} save",
            countText,
        ];
        return isDirty
            ? theme.Fg("dim", $"  {string.Join(" · ", parts)} ") + theme.Fg("warning", "(unsaved)")
            : theme.Fg("dim", $"  {string.Join(" · ", parts)}");
    }

    private void Refresh()
    {
        var query = searchInput.GetValue();
        var items = BuildItems();
        filteredItems = query.Length > 0
            ? Fuzzy.Filter(items, query, item => item.Model is { } model
                ? ModelSearch.GetModelSearchText(new ModelSearchItem(ModelJson.Id(model), ModelJson.Provider(model), ModelJson.Str(model, "name")))
                : item.FullId)
            : items;
        selectedIndex = Math.Min(selectedIndex, Math.Max(0, filteredItems.Count - 1));
        UpdateList();
        footerText.SetText(GetFooterText());
    }

    private void NotifyChange() => callbacks.OnChange(enabledIds is null ? null : [.. enabledIds]);

    private void UpdateList()
    {
        listContainer.Clear();

        if (filteredItems.Count == 0)
        {
            listContainer.AddChild(new Text(theme.Fg("muted", "  No matching models"), 0, 0));
            return;
        }

        var startIndex = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, filteredItems.Count - maxVisible));
        var endIndex = Math.Min(startIndex + maxVisible, filteredItems.Count);
        for (var i = startIndex; i < endIndex; i++)
        {
            var item = filteredItems[i];
            var isSelected = i == selectedIndex;
            var prefix = isSelected ? theme.Fg("accent", "→ ") : "  ";
            var id = item.Model is { } model ? ModelJson.Id(model) : item.FullId;
            var styledId = item.Model is not null ? id : theme.Strikethrough(id);
            var modelText = isSelected ? theme.Fg("accent", styledId) : styledId;
            var providerBadge = theme.Fg("muted", item.Model is not null ? $" [{ModelJson.Provider(item.Model)}]" : " [unavailable]");
            var status = item.Model is not null && item.Enabled ? theme.Fg("accent", "✓ ") : "  ";
            listContainer.AddChild(new Text($"{prefix}{status}{modelText}{providerBadge}", 0, 0));
        }

        // Add scroll indicator if needed
        if (startIndex > 0 || endIndex < filteredItems.Count)
            listContainer.AddChild(new Text(theme.Fg("muted", $"  ({selectedIndex + 1}/{filteredItems.Count})"), 0, 0));

        if (filteredItems.Count > 0)
        {
            var selected = filteredItems[selectedIndex];
            listContainer.AddChild(new Spacer(1));
            listContainer.AddChild(new Text(theme.Fg("muted",
                $"  {(selected.Model is not null ? $"Model Name: {JsString(selected.Model["name"])}" : "Model unavailable")}"), 0, 0));
        }
    }

    private static string JsString(JsonNode? node) => node switch
    {
        null => "undefined",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    public void HandleInput(string data)
    {
        var kb = KeybindingsManager.Global;

        // Navigation
        if (kb.Matches(data, "tui.select.up"))
        {
            if (filteredItems.Count == 0) return;
            selectedIndex = selectedIndex == 0 ? filteredItems.Count - 1 : selectedIndex - 1;
            UpdateList();
            return;
        }
        if (kb.Matches(data, "tui.select.down"))
        {
            if (filteredItems.Count == 0) return;
            selectedIndex = selectedIndex == filteredItems.Count - 1 ? 0 : selectedIndex + 1;
            UpdateList();
            return;
        }

        // Reorder enabled models
        var reorderUp = kb.Matches(data, "app.models.reorderUp");
        var reorderDown = kb.Matches(data, "app.models.reorderDown");
        if (reorderUp || reorderDown)
        {
            if (enabledIds is null) return;
            var item = selectedIndex < filteredItems.Count ? filteredItems[selectedIndex] : null;
            if (item is not null && IsEnabled(enabledIds, item.FullId))
            {
                var delta = reorderUp ? -1 : 1;
                var currentIndex = enabledIds.IndexOf(item.FullId);
                var newIndex = currentIndex + delta;
                // Only move if within bounds
                if (newIndex >= 0 && newIndex < enabledIds.Count)
                {
                    enabledIds = Move(enabledIds, item.FullId, delta);
                    isDirty = true;
                    selectedIndex += delta;
                    Refresh();
                    NotifyChange();
                }
            }
            return;
        }

        // Toggle on Enter
        if (kb.Matches(data, "tui.select.confirm"))
        {
            if (selectedIndex < filteredItems.Count)
            {
                enabledIds = Toggle(enabledIds, allIds, filteredItems[selectedIndex].FullId);
                isDirty = true;
                Refresh();
                NotifyChange();
            }
            return;
        }

        // Enable all (filtered if search active, otherwise all)
        if (kb.Matches(data, "app.models.enableAll"))
        {
            var targetIds = searchInput.GetValue().Length > 0 ? filteredItems.Select(i => i.FullId).ToList() : null;
            enabledIds = EnableAll(enabledIds, allIds, targetIds);
            isDirty = true;
            Refresh();
            NotifyChange();
            return;
        }

        // Clear all (filtered if search active, otherwise all)
        if (kb.Matches(data, "app.models.clearAll"))
        {
            var targetIds = searchInput.GetValue().Length > 0 ? filteredItems.Select(i => i.FullId).ToList() : null;
            enabledIds = ClearAll(enabledIds, allIds, targetIds);
            isDirty = true;
            Refresh();
            NotifyChange();
            return;
        }

        // Toggle provider of current item
        if (kb.Matches(data, "app.models.toggleProvider"))
        {
            var item = selectedIndex < filteredItems.Count ? filteredItems[selectedIndex] : null;
            if (item?.Model is { } model)
            {
                var provider = ModelJson.Provider(model);
                var providerIds = allIds.Where(id => ModelJson.Provider(modelsById[id]) == provider).ToList();
                var allEnabled = providerIds.All(id => IsEnabled(enabledIds, id));
                enabledIds = allEnabled ? ClearAll(enabledIds, allIds, providerIds) : EnableAll(enabledIds, allIds, providerIds);
                isDirty = true;
                Refresh();
                NotifyChange();
            }
            return;
        }

        // Save/persist to settings
        if (kb.Matches(data, "app.models.save"))
        {
            callbacks.OnPersist(enabledIds is null ? null : [.. enabledIds]);
            isDirty = false;
            footerText.SetText(GetFooterText());
            return;
        }

        // Ctrl+C - clear search or cancel if empty
        if (Keys.Matches(data, "ctrl+c"))
        {
            if (searchInput.GetValue().Length > 0)
            {
                searchInput.SetValue("");
                Refresh();
            }
            else callbacks.OnCancel();
            return;
        }

        // Escape - cancel
        if (Keys.Matches(data, "escape"))
        {
            callbacks.OnCancel();
            return;
        }

        // Pass everything else to search input
        searchInput.HandleInput(data);
        Refresh();
    }

    public Input GetSearchInput() => searchInput;
}
