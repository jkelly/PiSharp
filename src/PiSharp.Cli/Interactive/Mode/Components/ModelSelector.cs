// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/model-selector.ts.
// Also coding-agent/src/modes/interactive/model-catalog-refresh.ts (refreshModelCatalogs) and modelsAreEqual from ai/src/models.ts.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source ModelsRefreshResult: whether the refresh was aborted and the catalogs that failed, in insertion order
/// (provider id, error message).</summary>
internal sealed record ModelsRefreshResult(bool Aborted, IReadOnlyList<KeyValuePair<string, string>> Errors);

/// <summary>The ModelRuntime surface the catalog refresh uses (<c>Pick&lt;ModelRuntime, "refresh"&gt;</c>).</summary>
internal interface IModelCatalogRuntime
{
    /// <summary>ModelRuntime.refresh({ signal }): refreshes every catalog; cancellation aborts it.</summary>
    Task<ModelsRefreshResult> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>The ModelRuntime calls the /model selector makes. Models are Pi Model JSON objects (id, name, provider, api, ...).</summary>
internal interface IModelSelectorRuntime : IModelCatalogRuntime
{
    /// <summary>ModelRuntime.getAvailableSnapshot(): models of providers with configured auth.</summary>
    IReadOnlyList<JsonObject> GetAvailableSnapshot();
    /// <summary>ModelRuntime.getModel(provider, id).</summary>
    JsonObject? GetModel(string provider, string modelId);
    /// <summary>ModelRuntime.getError(): the models.json configuration error, if any.</summary>
    string? GetError();
}

/// <summary>Source ScopedModelItem: a model of the session's scoped (--models / enabledModels) set.</summary>
internal sealed record ScopedModelItem(JsonObject Model, string? ThinkingLevel = null);

/// <summary>Source DefaultModelReference (settings defaultProvider/defaultModel).</summary>
internal sealed record DefaultModelReference(string Provider, string Id);

/// <summary>Model JSON accessors shared by the model pickers.</summary>
internal static class ModelJson
{
    public static string Str(JsonObject? model, string key) =>
        model?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    public static string Id(JsonObject model) => Str(model, "id");
    public static string Provider(JsonObject model) => Str(model, "provider");
    public static string Name(JsonObject model) => Str(model, "name");

    /// <summary>Source getModelType: <c>model.type ?? "chat"</c>.</summary>
    public static string Type(JsonObject model) => model["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : "chat";

    /// <summary>Source modelsAreEqual: same type, id and provider; false when either is missing.</summary>
    public static bool ModelsAreEqual(JsonObject? a, JsonObject? b) =>
        a is not null && b is not null && Type(a) == Type(b) && Id(a) == Id(b) && Provider(a) == Provider(b);
}

/// <summary>Source model-catalog-refresh.ts: shares concurrent interactive all-catalog refreshes while keeping each caller's
/// cancellation independent. The shared refresh is aborted once its last waiter leaves.</summary>
internal static class ModelCatalogRefresh
{
    private sealed class ActiveModelCatalogRefresh
    {
        public required CancellationTokenSource Controller;
        public required Task<ModelsRefreshResult> Promise;
        public int Waiters;
    }

    private static readonly ConditionalWeakTable<IModelCatalogRuntime, ActiveModelCatalogRefresh> ActiveByRuntime = new();
    private static readonly Lock Gate = new();

    /// <summary>Source refreshModelCatalogs.</summary>
    public static Task<ModelsRefreshResult> RefreshModelCatalogs(IModelCatalogRuntime modelRuntime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActiveModelCatalogRefresh active;
        TaskCompletionSource<ModelsRefreshResult>? started = null;
        lock (Gate)
        {
            if (!ActiveByRuntime.TryGetValue(modelRuntime, out var existing))
            {
                started = new TaskCompletionSource<ModelsRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                existing = new ActiveModelCatalogRefresh { Controller = new CancellationTokenSource(), Promise = started.Task };
                ActiveByRuntime.AddOrUpdate(modelRuntime, existing);
            }
            active = existing;
            active.Waiters++;
        }
        if (started is not null) _ = RunShared(modelRuntime, active, started);
        return Finally(RaceWithCancellation(active.Promise, cancellationToken), () =>
        {
            bool abort;
            lock (Gate)
            {
                active.Waiters--;
                abort = active.Waiters == 0 && ActiveByRuntime.TryGetValue(modelRuntime, out var current) && ReferenceEquals(current, active);
            }
            if (abort) active.Controller.Cancel();
        });
    }

    /// <summary>The shared operation: modelRuntime.refresh raced with its own controller; it leaves the table once it settles.</summary>
    private static async Task RunShared(IModelCatalogRuntime modelRuntime, ActiveModelCatalogRefresh active, TaskCompletionSource<ModelsRefreshResult> promise)
    {
        ModelsRefreshResult? result = null; Exception? failure = null;
        try { result = await RaceWithCancellation(StartRefresh(modelRuntime, active.Controller.Token), active.Controller.Token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        lock (Gate)
        {
            if (ActiveByRuntime.TryGetValue(modelRuntime, out var current) && ReferenceEquals(current, active)) ActiveByRuntime.Remove(modelRuntime);
        }
        if (result is not null) promise.TrySetResult(result);
        else if (failure is OperationCanceledException canceled) promise.TrySetCanceled(canceled.CancellationToken);
        else promise.TrySetException(failure!);
    }

    private static Task<ModelsRefreshResult> StartRefresh(IModelCatalogRuntime runtime, CancellationToken token)
    {
        try { return runtime.RefreshAsync(token); }
        catch (Exception error) { return Task.FromException<ModelsRefreshResult>(error); }
    }

    /// <summary>Source raceWithAbortSignal: stop waiting on cancellation while the abandoned operation settles on its own.</summary>
    private static Task<ModelsRefreshResult> RaceWithCancellation(Task<ModelsRefreshResult> operation, CancellationToken token)
    {
        if (!token.CanBeCanceled) return operation;
        if (token.IsCancellationRequested)
        {
            _ = operation.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            return Task.FromCanceled<ModelsRefreshResult>(token);
        }
        return operation.WaitAsync(token);
    }

    private static async Task<ModelsRefreshResult> Finally(Task<ModelsRefreshResult> task, Action onSettled)
    {
        try { return await task.ConfigureAwait(false); }
        finally { onSettled(); }
    }
}

/// <summary>Component that renders a model selector with search.</summary>
internal sealed class ModelSelectorComponent : Container, IFocusable, IInputHandler
{
    private sealed record ModelItem(string Provider, string Id, JsonObject Model);

    private enum ModelScope { All, Scoped }

    private readonly Input searchInput;

    // Focusable implementation - propagate to searchInput for IME cursor positioning
    private bool focused;
    public bool Focused
    {
        get => focused;
        set { focused = value; searchInput.Focused = value; }
    }

    private readonly Container listContainer;
    private List<ModelItem> allModels = [];
    private List<ModelItem> scopedModelItems = [];
    private List<ModelItem> activeModels = [];
    private List<ModelItem> filteredModels = [];
    private int selectedIndex;
    private readonly JsonObject? currentModel;
    private readonly IModelSelectorRuntime modelRuntime;
    private readonly Action<JsonObject> onSelectCallback;
    private readonly Action<JsonObject>? onSelectAsDefaultCallback;
    private readonly Action onCancelCallback;
    private string? errorMessage;
    private string refreshStatusMessage = "Refreshing model catalogs…";
    private bool refreshStatusSuccess;
    private readonly ITui tui;
    private IReadOnlyList<ScopedModelItem> scopedModels;
    private readonly DefaultModelReference? defaultModel;
    private ModelScope scope;
    private readonly Text? scopeText;
    private readonly Text? scopeHintText;
    private readonly CancellationTokenSource refreshAbortController = new();
    private IDisposable? refreshTimeout;
    private bool closed;

    /// <param name="tui">Render requests, the refresh timeout timer and the UI loop the refresh result is applied on.</param>
    public ModelSelectorComponent(
        ITui tui,
        JsonObject? currentModel,
        IModelSelectorRuntime modelRuntime,
        IReadOnlyList<ScopedModelItem> scopedModels,
        Action<JsonObject> onSelect,
        Action onCancel,
        string? initialSearchInput = null,
        Action<JsonObject>? onSelectAsDefault = null,
        DefaultModelReference? defaultModel = null)
    {
        this.tui = tui;
        this.currentModel = currentModel;
        this.modelRuntime = modelRuntime;
        this.scopedModels = scopedModels;
        this.defaultModel = defaultModel;
        scope = scopedModels.Count > 0 ? ModelScope.Scoped : ModelScope.All;
        onSelectCallback = onSelect;
        onSelectAsDefaultCallback = onSelectAsDefault;
        onCancelCallback = onCancel;

        // Add top border
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        // Add hint about model filtering
        if (scopedModels.Count > 0)
        {
            scopeText = new Text(GetScopeText(), 0, 0);
            AddChild(scopeText);
            scopeHintText = new Text(GetScopeHintText(), 0, 0);
            AddChild(scopeHintText);
        }
        else
        {
            const string hintText = "Only showing models from configured providers. Use /login to add providers.";
            AddChild(new Text(theme.Fg("warning", hintText), 0, 0));
        }
        AddChild(new Spacer(1));

        // Create search input
        searchInput = new Input();
        if (!string.IsNullOrEmpty(initialSearchInput)) searchInput.SetValue(initialSearchInput);
        searchInput.OnSubmit = _ =>
        {
            // Enter on search input selects the first filtered item
            if (selectedIndex < filteredModels.Count) HandleSelect(filteredModels[selectedIndex].Model);
        };
        AddChild(searchInput);

        AddChild(new Spacer(1));

        // Create list container
        listContainer = new Container();
        AddChild(listContainer);

        AddChild(new Spacer(1));

        // Hint
        if (onSelectAsDefaultCallback is not null)
        {
            AddChild(new Text(theme.Fg("dim",
                $"  {KeybindingHints.KeyDisplayText("tui.select.confirm")} to select · {KeybindingHints.KeyDisplayText("app.models.save")} to set as default · {KeybindingHints.KeyDisplayText("tui.select.cancel")} to cancel"), 0, 0));
        }

        // Add bottom border
        AddChild(new DynamicBorder());

        // Render the current snapshot immediately, then refresh in the background.
        LoadModelsFromSnapshot();
        if (!string.IsNullOrEmpty(initialSearchInput)) FilterModels(initialSearchInput);
        else UpdateList();
        this.tui.RequestRender();
        _ = RefreshModels();
    }

    private void LoadModelsFromSnapshot()
    {
        var models = modelRuntime.GetAvailableSnapshot().Select(model => new ModelItem(ModelJson.Provider(model), ModelJson.Id(model), model)).ToList();
        allModels = SortModels(models);
        scopedModels = scopedModels.Select(scoped =>
        {
            var refreshed = modelRuntime.GetModel(ModelJson.Provider(scoped.Model), ModelJson.Id(scoped.Model));
            return refreshed is not null ? scoped with { Model = refreshed } : scoped;
        }).ToList();
        scopedModelItems = scopedModels.Select(scoped => new ModelItem(ModelJson.Provider(scoped.Model), ModelJson.Id(scoped.Model), scoped.Model)).ToList();
        activeModels = scope == ModelScope.Scoped ? scopedModelItems : allModels;
        filteredModels = activeModels;
        var currentIndex = filteredModels.FindIndex(item => ModelJson.ModelsAreEqual(currentModel, item.Model));
        selectedIndex = currentIndex >= 0 ? currentIndex : Math.Min(selectedIndex, Math.Max(0, filteredModels.Count - 1));
    }

    private async Task RefreshModels()
    {
        const int timeoutMs = 15_000;
        var timedOut = false;
        refreshTimeout = tui.Loop.SetTimeout(() =>
        {
            timedOut = true;
            refreshAbortController.Cancel();
        }, timeoutMs);
        ModelsRefreshResult? result = null;
        Exception? failure = null;
        try { result = await ModelCatalogRefresh.RefreshModelCatalogs(modelRuntime, refreshAbortController.Token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        // Upstream continues on the event loop; apply the outcome on the TUI's loop.
        tui.Loop.Post(() =>
        {
            try
            {
                if (closed) return;
                if (result is not null)
                {
                    refreshStatusMessage = "";
                    if (result.Aborted && timedOut) errorMessage = "Model refresh timed out; showing cached models.";
                    else if (result.Errors.Count == 1) errorMessage = $"Could not refresh {result.Errors[0].Key}; showing cached models.";
                    else if (result.Errors.Count > 1)
                        errorMessage = $"Could not refresh {result.Errors.Count} model catalogs ({string.Join(", ", result.Errors.Select(e => e.Key))}); showing cached models.";
                    else
                    {
                        errorMessage = modelRuntime.GetError();
                        if (string.IsNullOrEmpty(errorMessage))
                        {
                            refreshStatusMessage = "Model catalogs refreshed.";
                            refreshStatusSuccess = true;
                        }
                    }
                    LoadModelsFromSnapshot();
                    FilterModels(searchInput.GetValue());
                    tui.RequestRender();
                }
                else
                {
                    refreshStatusMessage = "";
                    errorMessage = timedOut
                        ? "Model refresh timed out; showing cached models."
                        : $"Could not refresh model catalogs: {(failure is OperationCanceledException ? "This operation was aborted" : failure?.Message)}";
                    UpdateList();
                    tui.RequestRender();
                }
            }
            finally
            {
                refreshTimeout?.Dispose();
            }
        });
    }

    /// <summary>Stops the background refresh; selecting or cancelling disposes the selector.</summary>
    public void Dispose()
    {
        if (closed) return;
        closed = true;
        refreshTimeout?.Dispose();
        refreshAbortController.Cancel();
    }

    private List<ModelItem> SortModels(List<ModelItem> models)
    {
        // Sort: current model first, default model second, then by provider (a stable sort, as Array.prototype.sort is).
        var providerCompare = StringComparer.Create(CultureInfo.InvariantCulture, false);
        return models.Select((item, index) => (item, index)).OrderBy(entry => entry, Comparer<(ModelItem Item, int Index)>.Create((a, b) =>
        {
            var aIsCurrent = ModelJson.ModelsAreEqual(currentModel, a.Item.Model);
            var bIsCurrent = ModelJson.ModelsAreEqual(currentModel, b.Item.Model);
            if (aIsCurrent && !bIsCurrent) return -1;
            if (!aIsCurrent && bIsCurrent) return 1;
            var aIsDefault = IsDefaultModel(a.Item.Model);
            var bIsDefault = IsDefaultModel(b.Item.Model);
            if (aIsDefault && !bIsDefault) return -1;
            if (!aIsDefault && bIsDefault) return 1;
            var byProvider = providerCompare.Compare(a.Item.Provider, b.Item.Provider);
            return byProvider != 0 ? byProvider : a.Index.CompareTo(b.Index);
        })).Select(entry => entry.item).ToList();
    }

    private string GetScopeText()
    {
        var allText = scope == ModelScope.All ? theme.Fg("accent", "all") : theme.Fg("muted", "all");
        var scopedText = scope == ModelScope.Scoped ? theme.Fg("accent", "scoped") : theme.Fg("muted", "scoped");
        return $"{theme.Fg("muted", "Scope: ")}{allText}{theme.Fg("muted", " | ")}{scopedText}";
    }

    private static string GetScopeHintText() => KeybindingHints.KeyHint("tui.input.tab", "scope") + theme.Fg("muted", " (all/scoped)");

    private bool IsDefaultModel(JsonObject model) =>
        defaultModel is not null && defaultModel.Provider == ModelJson.Provider(model) && defaultModel.Id == ModelJson.Id(model);

    private static bool IsDefaultSearch(string query)
    {
        var normalized = TextUtils.JsTrim(query).ToLowerInvariant();
        return normalized.Length > 0 && "default".StartsWith(normalized, StringComparison.Ordinal);
    }

    private void SetScope(ModelScope next)
    {
        if (scope == next) return;
        scope = next;
        activeModels = scope == ModelScope.Scoped ? scopedModelItems : allModels;
        var currentIndex = activeModels.FindIndex(item => ModelJson.ModelsAreEqual(currentModel, item.Model));
        selectedIndex = currentIndex >= 0 ? currentIndex : 0;
        FilterModels(searchInput.GetValue());
        scopeText?.SetText(GetScopeText());
    }

    private void FilterModels(string query)
    {
        if (query.Length > 0)
        {
            var filtered = Fuzzy.Filter(activeModels, query, item =>
            {
                var defaultText = IsDefaultModel(item.Model) ? " default" : "";
                return $"{ModelSearch.GetModelSelectorSearchText(new ModelSearchItem(item.Id, item.Provider, ModelJson.Str(item.Model, "name")))}{defaultText}";
            });
            if (IsDefaultSearch(query))
            {
                var defaultItems = activeModels.Where(item => IsDefaultModel(item.Model)).ToList();
                var defaultKeys = defaultItems.Select(item => $"{item.Provider}\0{item.Id}").ToHashSet(StringComparer.Ordinal);
                filteredModels = [.. defaultItems, .. filtered.Where(item => !defaultKeys.Contains($"{item.Provider}\0{item.Id}"))];
            }
            else filteredModels = filtered;
        }
        else filteredModels = activeModels;
        // When filtering by a query, move the selector to the top row so the best
        // match is highlighted. When the query is cleared, keep the current position
        // clamped to the (restored) list length.
        selectedIndex = query.Length > 0 ? 0 : Math.Min(selectedIndex, Math.Max(0, filteredModels.Count - 1));
        UpdateList();
    }

    private void UpdateList()
    {
        listContainer.Clear();

        const int maxVisible = 10;
        var startIndex = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, filteredModels.Count - maxVisible));
        var endIndex = Math.Min(startIndex + maxVisible, filteredModels.Count);

        // Show visible slice of filtered models
        for (var i = startIndex; i < endIndex; i++)
        {
            var item = filteredModels[i];
            var isSelected = i == selectedIndex;
            var isCurrent = ModelJson.ModelsAreEqual(currentModel, item.Model);
            var isDefault = IsDefaultModel(item.Model);
            var defaultBadge = isDefault ? theme.Fg("muted", " · default") : "";

            var cursor = isSelected ? theme.Fg("accent", "→ ") : "  ";
            var currentMarker = isCurrent ? theme.Fg("accent", "✓ ") : "  ";
            var modelText = isSelected ? theme.Fg("accent", item.Id) : item.Id;
            var providerBadge = theme.Fg("muted", $"[{item.Provider}]");
            var line = $"{cursor}{currentMarker}{modelText} {providerBadge}{defaultBadge}";

            listContainer.AddChild(new Text(line, 0, 0));
        }

        // Add scroll indicator if needed
        if (startIndex > 0 || endIndex < filteredModels.Count)
        {
            var scrollInfo = theme.Fg("muted", $"  ({selectedIndex + 1}/{filteredModels.Count})");
            listContainer.AddChild(new Text(scrollInfo, 0, 0));
        }

        // Show error message or "no results" if empty
        if (!string.IsNullOrEmpty(errorMessage))
        {
            // Show error in red
            foreach (var line in errorMessage.Split('\n')) listContainer.AddChild(new Text(theme.Fg("error", line), 0, 0));
        }
        else if (filteredModels.Count == 0)
        {
            listContainer.AddChild(new Text(theme.Fg("muted", "  No matching models"), 0, 0));
        }
        else
        {
            var selected = filteredModels[selectedIndex];
            listContainer.AddChild(new Spacer(1));
            listContainer.AddChild(new Text(theme.Fg("muted", $"  Model Name: {JsString(selected.Model["name"])}"), 0, 0));
        }
        if (refreshStatusMessage.Length > 0)
        {
            listContainer.AddChild(new Spacer(1));
            listContainer.AddChild(new Text(theme.Fg(refreshStatusSuccess ? "success" : "muted", $"  {refreshStatusMessage}"), 0, 0));
        }
    }

    /// <summary>A JS template-literal rendering of a JSON value (missing renders "undefined").</summary>
    private static string JsString(JsonNode? node) => node switch
    {
        null => "undefined",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.input.tab"))
        {
            if (scopedModelItems.Count > 0)
            {
                SetScope(scope == ModelScope.All ? ModelScope.Scoped : ModelScope.All);
                scopeHintText?.SetText(GetScopeHintText());
            }
            return;
        }
        // Up arrow - wrap to bottom when at top
        if (kb.Matches(keyData, "tui.select.up"))
        {
            if (filteredModels.Count == 0) return;
            selectedIndex = selectedIndex == 0 ? filteredModels.Count - 1 : selectedIndex - 1;
            UpdateList();
        }
        // Down arrow - wrap to top when at bottom
        else if (kb.Matches(keyData, "tui.select.down"))
        {
            if (filteredModels.Count == 0) return;
            selectedIndex = selectedIndex == filteredModels.Count - 1 ? 0 : selectedIndex + 1;
            UpdateList();
        }
        // Enter
        else if (kb.Matches(keyData, "tui.select.confirm"))
        {
            if (selectedIndex < filteredModels.Count) HandleSelect(filteredModels[selectedIndex].Model);
        }
        // Escape or Ctrl+C
        else if (kb.Matches(keyData, "tui.select.cancel"))
        {
            Dispose();
            onCancelCallback();
        }
        // Select and save as default
        else if (kb.Matches(keyData, "app.models.save") && onSelectAsDefaultCallback is not null)
        {
            if (selectedIndex < filteredModels.Count)
            {
                var selectedModel = filteredModels[selectedIndex];
                Dispose();
                onSelectAsDefaultCallback(selectedModel.Model);
            }
        }
        // Pass everything else to search input
        else
        {
            searchInput.HandleInput(keyData);
            FilterModels(searchInput.GetValue());
        }
    }

    private void HandleSelect(JsonObject model)
    {
        Dispose();
        onSelectCallback(model);
    }

    public Input GetSearchInput() => searchInput;
}
