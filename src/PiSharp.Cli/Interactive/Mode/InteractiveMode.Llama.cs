// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/llama/index.ts (the /llama command:
// configuredClient, syncCatalog, loadModel, unloadModel, downloadModel and the manager loop) and extensions/llama/ui.ts (LlamaView,
// HuggingFaceSearch, showLlamaUi, runWithProgress). Upstream's view is a ctx.ui.custom() component; here it takes the editor's slot.
using System.Text.RegularExpressions;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Llama;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>The services /llama needs from the run: the provider's resolved auth (getProviderAuth), the catalog sync after a router
/// change (setCatalog, then a live <c>refresh</c> of llama.cpp and the session's selectable models), and the Hugging Face token lookup.</summary>
internal sealed record LlamaServices(Func<CancellationToken, Task<LlamaAuth?>> ResolveAuth,
    Func<IReadOnlyList<LlamaModelInfo>, string, CancellationToken, Task> SyncCatalog)
{
    public HttpMessageInvoker? Http { get; init; }
    /// <summary>The provider's catalog refresh after /login (modelRuntime.refresh({ providers: ["llama.cpp"] }): the network unless
    /// PI_OFFLINE), then the session's selectable models.</summary>
    public Func<CancellationToken, Task> Refresh { get; init; } = _ => Task.CompletedTask;
    public Func<Task<string?>> FindHuggingFaceToken { get; init; } = () => HuggingFaceClient.FindTokenAsync(Environment.GetEnvironmentVariable);
    public string HuggingFaceUrl { get; init; } = HuggingFaceClient.DefaultUrl;
}

internal sealed partial class InteractiveMode
{
    private static bool LlamaModelIsLoaded(LlamaModelInfo model) => model.Status is "loaded" or "sleeping";

    private static bool IsLlamaConnectionError(Exception error) => error is LlamaConnectionException ||
        $"{error.GetType().Name} {error.Message}".ToLowerInvariant() is var message &&
        (message.Contains("fetch failed", StringComparison.Ordinal) || message.Contains("timeout", StringComparison.Ordinal) || message.Contains("network", StringComparison.Ordinal));

    private static string LlamaConnectionErrorMessage(Exception error) => IsLlamaConnectionError(error) ? "Could not connect to the server." : error.Message;

    private static (string Repository, string? Quantization) ParseHuggingFaceModel(string value)
    {
        var colon = value.IndexOf(':', value.IndexOf('/', StringComparison.Ordinal) + 1);
        return colon < 0 ? (value, null) : (value[..colon], value[(colon + 1)..]);
    }

    private void LlamaNotify(string message, string? type = null) => context.Loop.Post(() => ShowExtensionNotify(message, type));

    /// <summary>index.ts configuredClient: the router of the provider's resolved auth, or a warning to configure it.</summary>
    private async Task<LlamaClient?> ConfiguredLlamaClientAsync(LlamaServices llama)
    {
        var result = await llama.ResolveAuth(CancellationToken.None);
        if (result is null)
        {
            LlamaNotify($"Configure llama.cpp with /login {LlamaCatalog.ProviderId}", "warning");
            return null;
        }
        return new LlamaClient(result.ServerUrl, result.ApiKey, llama.Http);
    }

    /// <summary>index.ts registerCommand("llama") handler (interactive mode).</summary>
    private async Task HandleLlamaCommandAsync()
    {
        if (context.Llama is not { } llama) { LlamaNotify("/llama is available in interactive mode", "warning"); return; }
        LlamaClient? client;
        try { client = await ConfiguredLlamaClientAsync(llama); }
        catch (Exception error) { ShowError(error.Message); return; }
        if (client is null) return;

        async Task<IReadOnlyList<LlamaModelInfo>> SyncCatalogAsync(IReadOnlyList<LlamaModelInfo>? catalog = null)
        {
            using var timeout = new CancellationTokenSource(LlamaClient.RequestTimeout);
            var current = catalog ?? await client.ListAsync(token: timeout.Token);
            // /llama already contacted the configured llama.cpp server, so this refresh stays live even with PI_OFFLINE.
            try { await llama.SyncCatalog(current, client.ServerUrl, timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw new InvalidOperationException("Model catalog refresh timed out."); }
            await RefreshAvailableModelsAsync();
            UpdateAvailableProviderCount();
            return current;
        }

        async Task LoadModelAsync(LlamaView ui, IReadOnlyList<LlamaModelInfo> catalog, LlamaModelInfo target)
        {
            var loaded = catalog.Where(model => model.Id != target.Id && LlamaModelIsLoaded(model)).ToList();
            var replace = false;
            if (loaded.Count > 0)
            {
                var choice = await ui.SelectAsync($"{loaded.Count} model{(loaded.Count == 1 ? " is" : "s are")} loaded",
                    ["Unload all and load", "Keep loaded and load", "Cancel"]);
                if (choice is null or "Cancel") return;
                replace = choice == "Unload all and load";
            }
            async Task RestoreLoadedAsync()
            {
                LlamaNotify("Restoring previously loaded models");
                foreach (var model in loaded) await client.LoadAndWaitAsync(model.Id, _ => { });
                await SyncCatalogAsync();
            }
            if (replace) foreach (var model in loaded) await client.UnloadAndWaitAsync(model.Id);
            try
            {
                var result = await RunLlamaProgressAsync(ui, "Loading model", target.Id, "Starting…", "Stop loading?", target.Id,
                    async (token, update) => { await client.LoadAndWaitAsync(target.Id, update, token); return true; }, () => client.UnloadAsync(target.Id));
                if (result.Cancelled)
                {
                    if (replace) await RestoreLoadedAsync();
                    return;
                }
                var refreshed = await SyncCatalogAsync();
                var loadedModel = refreshed.FirstOrDefault(model => model.Id == target.Id);
                LlamaNotify(loadedModel?.Status == "loaded" ? $"Loaded {target.Id}" : $"Load started for {target.Id}");
            }
            catch (Exception)
            {
                if (replace)
                {
                    try { await RestoreLoadedAsync(); } catch (Exception) { /* Preserve the original load error. */ }
                }
                throw;
            }
        }

        async Task UnloadModelAsync(LlamaView ui, LlamaModelInfo model)
        {
            if (!await ui.ConfirmAsync("Unload model?", model.Id)) return;
            await client.UnloadAndWaitAsync(model.Id);
            await SyncCatalogAsync();
            LlamaNotify($"Unloaded {model.Id}");
        }

        async Task DownloadModelAsync(LlamaView ui)
        {
            var huggingFace = new HuggingFaceClient(await llama.FindHuggingFaceToken(), llama.HuggingFaceUrl, llama.Http);
            var selected = await ui.SearchModelsAsync((query, token) => huggingFace.SearchAsync(query, token));
            if (selected is null) return;
            var parsed = ParseHuggingFaceModel(selected);
            ui.ShowStatus("Loading model details", parsed.Repository);
            var details = await huggingFace.DetailsAsync(parsed.Repository);
            if (details.Gated is { } gated)
            {
                var approval = gated == "manual" ? "Manual approval is required" : "Accept the access terms";
                var choice = await ui.SelectAsync($"Hugging Face access required\n{details.Id}\n\n{approval} at:\nhttps://huggingface.co/{details.Id}\n\nThe llama.cpp server needs HF_TOKEN with access.",
                    ["Continue", "Back"]);
                if (choice != "Continue") return;
            }
            var quantization = parsed.Quantization;
            if (quantization is null && details.Quantizations.Count > 0)
            {
                var options = details.Quantizations.Select(entry =>
                {
                    var detail = string.Join(" · ", new[] { entry.Size is { } size ? LlamaClient.FormatBytes(size) : null, entry.Name == "Q4_K_M" ? "recommended" : null }
                        .Where(value => !string.IsNullOrEmpty(value)));
                    return detail.Length > 0 ? $"{entry.Name} · {detail}" : entry.Name;
                }).ToList();
                var choice = await ui.SelectAsync($"Select quantization\n{details.Id}", options);
                if (choice is null) return;
                quantization = details.Quantizations.ElementAtOrDefault(options.IndexOf(choice))?.Name;
                if (quantization is null) return;
            }
            var model = quantization is not null ? $"{details.Id}:{quantization}" : details.Id;
            var result = await RunLlamaProgressAsync(ui, "Downloading model", model, "Starting…", "Stop download?", model,
                (token, update) => client.DownloadAndWaitAsync(model, update, token), () => client.UnloadAsync(model));
            if (result.Cancelled) return;
            await SyncCatalogAsync(result.Value);
            LlamaNotify($"Downloaded {model}");
        }

        await ShowLlamaUiAsync(async ui =>
        {
            async Task<IReadOnlyList<LlamaModelInfo>?> ReadCatalogAsync()
            {
                while (true)
                {
                    try { return await SyncCatalogAsync(); }
                    catch (Exception error)
                    {
                        if (await ui.ConnectionErrorAsync(client.ServerUrl, LlamaConnectionErrorMessage(error)) == "close") return null;
                    }
                }
            }
            var catalog = await ReadCatalogAsync();
            if (catalog is null) return;
            while (true)
            {
                var action = await ui.ShowModelsAsync(client.ServerUrl, catalog);
                if (action.Kind == LlamaManagerActionKind.Close) return;
                Exception? actionError = null;
                try
                {
                    if (action.Kind == LlamaManagerActionKind.Download) await DownloadModelAsync(ui);
                    else if (LlamaModelIsLoaded(action.Model!)) await UnloadModelAsync(ui, action.Model!);
                    else if (action.Model!.Status == "unloaded") await LoadModelAsync(ui, catalog, action.Model);
                    else LlamaNotify($"{action.Model.Id} is {action.Model.Status}", "warning");
                }
                catch (Exception error) { actionError = error; }
                var refreshed = await ReadCatalogAsync();
                if (refreshed is null) return;
                catalog = refreshed;
                if (actionError is not null && !IsLlamaConnectionError(actionError)) LlamaNotify(actionError.Message, "error");
            }
        });
    }

    /// <summary>ui.ts showLlamaUi: the view takes the editor's place until <paramref name="run"/> settles; a failure is notified.</summary>
    private async Task ShowLlamaUiAsync(Func<LlamaView, Task> run)
    {
        var view = new LlamaView(this);
        await context.Loop.InvokeAsync(() =>
        {
            DisposeActiveSelector();
            editorContainer.Clear();
            editorContainer.AddChild(view);
            ui.SetFocus(view);
            ui.RequestRender();
        });
        try { await run(view); }
        catch (Exception error) { LlamaNotify(error.Message, "error"); }
        finally
        {
            await context.Loop.InvokeAsync(() =>
            {
                view.Close();
                editorContainer.Clear();
                editorContainer.AddChild(editor);
                ui.SetFocus(editor);
                ui.RequestRender();
            });
        }
    }

    private sealed record LlamaProgressResult<T>(bool Cancelled, T Value);

    /// <summary>ui.ts runWithProgress: the operation runs while the progress screen shows; escape asks to stop it, and stopping cancels it
    /// on the server and aborts the wait.</summary>
    private static async Task<LlamaProgressResult<T>> RunLlamaProgressAsync<T>(LlamaView ui, string title, string model, string initialMessage,
        string cancelTitle, string cancelMessage, Func<CancellationToken, Action<LlamaProgress>, Task<T>> run, Func<Task> cancel)
    {
        using var controller = new CancellationTokenSource();
        var state = new LlamaView.ProgressState(title, model, initialMessage);
        var settled = Task.Run(() => run(controller.Token, progress =>
        {
            ui.ApplyProgress(state, progress);
            ui.UpdateProgress(state);
        }));
        while (!settled.IsCompleted)
        {
            var stop = ui.ProgressAsync(state);
            if (await Task.WhenAny(settled, stop) == settled) break;
            var confirmed = await ui.ConfirmAsync(cancelTitle, cancelMessage);
            if (!confirmed || settled.IsCompleted) continue;
            try { await cancel(); }
            finally { controller.Cancel(); }
            try { await settled; } catch (Exception) { }
            return new(true, default!);
        }
        return new(false, await settled);
    }

    internal enum LlamaManagerActionKind { Model, Download, Close }
    internal sealed record LlamaManagerAction(LlamaManagerActionKind Kind, LlamaModelInfo? Model = null);

    /// <summary>ui.ts LlamaView: one framed screen at a time (the model list, a select, a status, the Hugging Face search or progress).</summary>
    internal sealed class LlamaView(InteractiveMode mode) : IComponent, IInputHandler, IFocusable
    {
        internal sealed class ProgressState(string title, string model, string message)
        {
            public string Title { get; } = title;
            public string Model { get; } = model;
            public string Message { get; set; } = message;
            public double? Ratio { get; set; }
            public string? Detail { get; set; }
        }

        private const string DownloadValue = "\0download";
        private readonly Dictionary<string, IReadOnlyList<HuggingFaceModel>> searchCache = new(StringComparer.Ordinal);
        private Container content = Frame("llama.cpp models", [new Text(theme.Fg("muted", "Loading…"), 1, 1)]);
        private IInputHandler? inputHandler;
        private IFocusable? inputTarget;
        private TaskCompletionSource? progressPromise;
        private bool showingProgress;
        private bool focused;
        private HuggingFaceSearch? search;

        public bool Focused
        {
            get => focused;
            set { focused = value; if (inputTarget is not null) inputTarget.Focused = value; }
        }

        private UiLoop Loop => mode.context.Loop;

        internal void Close() { search?.Close(null); progressPromise = null; }

        private static SelectListTheme SelectTheme() => new(text => theme.Fg("accent", text), text => theme.Fg("accent", text), text => theme.Fg("muted", text),
            text => theme.Fg("dim", text), text => theme.Fg("warning", text));

        private static Container Frame(string title, IEnumerable<IComponent> body, string? footer = null)
        {
            var container = new Container();
            container.AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
            container.AddChild(new Text(theme.Fg("accent", theme.Bold(title)), 1, 0));
            foreach (var child in body) container.AddChild(child);
            if (footer is not null)
            {
                container.AddChild(new Spacer(1));
                container.AddChild(new Text(theme.Fg("dim", footer), 1, 0));
            }
            container.AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
            return container;
        }

        private void SetContent(Container next, IInputHandler? handler = null, IFocusable? target = null)
        {
            if (inputTarget is not null) inputTarget.Focused = false;
            progressPromise = null;
            showingProgress = false;
            content = next;
            inputHandler = handler;
            inputTarget = target;
            if (inputTarget is not null) inputTarget.Focused = focused;
            mode.ui.RequestRender();
        }

        private static string ContextLabel(LlamaModelInfo model)
        {
            static string Label(double value) => value >= 1000 ? $"{Math.Round(value / 1000, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture)}k"
                : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if ((model.Meta("n_ctx") ?? model.Meta("n_ctx_train")) is { } context) return Label(context);
            var args = model.Args;
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (args[index] is not ("--ctx-size" or "-c" or "-ctx")) continue;
                if (LlamaCatalog.JsNumber(args[index + 1]) is { } value && double.IsFinite(value) && value > 0) return Label(value);
            }
            return "";
        }

        private static string ModelDescription(LlamaModelInfo model)
        {
            var details = new List<string>();
            var loaded = model.Status is "loaded" or "sleeping";
            if (loaded) details.Add("loaded");
            else if (model.Status != "unloaded") details.Add(model.Status);
            var context = loaded ? ContextLabel(model) : "";
            if (context.Length > 0) details.Add($"{context} context");
            return string.Join(" · ", details);
        }

        internal Task<LlamaManagerAction> ShowModelsAsync(string serverUrl, IReadOnlyList<LlamaModelInfo> models)
        {
            var result = new TaskCompletionSource<LlamaManagerAction>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sorted = models.OrderBy(model => model.Status == "loaded" ? 0 : 1)
                .ThenBy(model => model.Id, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, false)).ToList();
            var byId = new Dictionary<string, LlamaModelInfo>(StringComparer.Ordinal);
            foreach (var model in sorted) byId[model.Id] = model;
            var items = sorted.Select(model => new SelectItem(model.Id, model.Id, ModelDescription(model))).ToList();
            items.Add(new(DownloadValue, "Download model…", "Hugging Face owner/repository[:quant]"));
            Loop.Post(() =>
            {
                var list = new SelectList(items, Math.Min(items.Count, 12), SelectTheme(), new SelectListLayoutOptions(36, 56))
                {
                    OnSelect = item =>
                    {
                        if (item.Value == DownloadValue) result.TrySetResult(new(LlamaManagerActionKind.Download));
                        else if (byId.TryGetValue(item.Value, out var model)) result.TrySetResult(new(LlamaManagerActionKind.Model, model));
                    },
                    OnCancel = () => result.TrySetResult(new(LlamaManagerActionKind.Close))
                };
                SetContent(Frame("llama.cpp models", [new Text(theme.Fg("dim", serverUrl), 1, 0), new Spacer(1), list],
                    $"{KeybindingHints.KeyHint("tui.select.confirm", "load/unload/download")} • {KeybindingHints.KeyHint("tui.select.cancel", "close")}"), list);
            });
            return result.Task;
        }

        internal Task<string?> SelectAsync(string title, IReadOnlyList<string> options)
        {
            var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Post(() =>
            {
                var list = new SelectList(options.Select(option => new SelectItem(option, option)), Math.Min(options.Count, 12), SelectTheme())
                {
                    OnSelect = item => result.TrySetResult(item.Value),
                    OnCancel = () => result.TrySetResult(null)
                };
                SetContent(Frame(title, [new Spacer(1), list],
                    $"{KeybindingHints.KeyHint("tui.select.confirm", "select")} • {KeybindingHints.KeyHint("tui.select.cancel", "cancel")}"), list);
            });
            return result.Task;
        }

        internal async Task<bool> ConfirmAsync(string title, string message) => await SelectAsync($"{title}\n{message}", ["Yes", "No"]) == "Yes";

        internal async Task<string> ConnectionErrorAsync(string serverUrl, string message) =>
            await SelectAsync($"llama.cpp unavailable\n{serverUrl}\n\n{message}", ["Retry", "Close"]) == "Retry" ? "retry" : "close";

        internal Task<string?> SearchModelsAsync(Func<string, CancellationToken, Task<IReadOnlyList<HuggingFaceModel>>> searchModels)
        {
            var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Post(() =>
            {
                var component = new HuggingFaceSearch(mode, searchModels, searchCache, value => result.TrySetResult(value));
                search = component;
                SetContent(Frame("Download model", [new Spacer(1), component],
                    $"{KeybindingHints.KeyHint("tui.select.confirm", "select")} • {KeybindingHints.KeyHint("tui.select.cancel", "back")}"), component, component);
            });
            return result.Task;
        }

        internal void ShowStatus(string title, string message) =>
            Loop.Post(() => SetContent(Frame(title, [new Spacer(1), new Text(theme.Fg("muted", message), 1, 0)])));

        /// <summary>Object.assign(state, progress): the fields the update carries replace the state's.</summary>
        internal void ApplyProgress(ProgressState state, LlamaProgress progress)
        {
            lock (state)
            {
                state.Message = progress.Message;
                if (progress.HasRatio) state.Ratio = progress.Ratio;
                if (progress.HasDetail) state.Detail = progress.Detail;
            }
        }

        /// <summary>Shows the progress screen; the task completes when escape asks to stop.</summary>
        internal Task ProgressAsync(ProgressState state)
        {
            var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Post(() =>
            {
                progressPromise ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var current = progressPromise;
                _ = current.Task.ContinueWith(_ => result.TrySetResult(), TaskScheduler.Default);
                showingProgress = true;
                RenderProgress(state);
            });
            return result.Task;
        }

        internal void UpdateProgress(ProgressState state) => Loop.Post(() => RenderProgress(state));

        private void RenderProgress(ProgressState state)
        {
            if (!showingProgress) return;
            string message; double? ratio; string? detail;
            lock (state) { message = state.Message; ratio = state.Ratio; detail = state.Detail; }
            var body = new List<IComponent> { new Text(theme.Fg("text", state.Model), 1, 0), new Spacer(1), new Text(theme.Fg("muted", message), 1, 0) };
            if (ratio is { } value)
            {
                const int available = 40;
                var filled = (int)Math.Round(Math.Max(0, Math.Min(1, value)) * available, MidpointRounding.AwayFromZero);
                body.Add(new Text(theme.Fg("accent", $"{new string('█', filled)}{new string('─', available - filled)} {Math.Round(value * 100, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture)}%"), 1, 0));
            }
            if (!string.IsNullOrEmpty(detail)) body.Add(new Text(theme.Fg("dim", detail), 1, 0));
            content = Frame(state.Title, body, KeybindingHints.KeyHint("tui.select.cancel", "stop"));
            inputHandler = null;
            mode.ui.RequestRender();
        }

        public void HandleInput(string data)
        {
            if (progressPromise is { } progress && KeybindingsManager.Global.Matches(data, "tui.select.cancel"))
            {
                progressPromise = null;
                progress.TrySetResult();
                return;
            }
            inputHandler?.HandleInput(data);
            mode.ui.RequestRender();
        }

        public List<string> Render(int width) =>
            [.. content.Render(width).Select(line => TextUtils.VisibleWidth(line) > width ? TextUtils.TruncateToWidth(line, width, "") : line)];

        public void Invalidate() => content.Invalidate();
    }

    /// <summary>ui.ts HuggingFaceSearch: an input with debounced Hugging Face search (cached per query), fuzzy-filtered results, and an
    /// exact <c>owner/repository[:quant]</c> accepted as typed.</summary>
    internal sealed partial class HuggingFaceSearch : Container, IInputHandler, IFocusable
    {
        private readonly InteractiveMode mode;
        private readonly Func<string, CancellationToken, Task<IReadOnlyList<HuggingFaceModel>>> searchModels;
        private readonly Dictionary<string, IReadOnlyList<HuggingFaceModel>> cache;
        private readonly Action<string?> onSelectModel;
        private readonly Input input = new();
        private readonly Container resultsContainer = new();
        private IReadOnlyList<HuggingFaceModel> results = [];
        private List<HuggingFaceModel> filteredResults = [];
        private int selectedIndex;
        private string query = "";
        private string status = "Type at least 2 characters";
        private CancellationTokenSource? debounce;
        private CancellationTokenSource? request;
        private bool closed;
        private bool focused;

        [GeneratedRegex(@"^[^/\s]+/[^:\s]+(?::[^\s:]+)?$", RegexOptions.ECMAScript)]
        private static partial Regex ExactModel();

        internal HuggingFaceSearch(InteractiveMode mode, Func<string, CancellationToken, Task<IReadOnlyList<HuggingFaceModel>>> searchModels,
            Dictionary<string, IReadOnlyList<HuggingFaceModel>> cache, Action<string?> onSelectModel)
        {
            this.mode = mode; this.searchModels = searchModels; this.cache = cache; this.onSelectModel = onSelectModel;
            AddChild(new Text(theme.Fg("dim", "Model name or owner/repository[:quant]"), 1, 0));
            AddChild(input);
            AddChild(new Spacer(1));
            AddChild(resultsContainer);
            UpdateResults();
        }

        public bool Focused { get => focused; set { focused = value; input.Focused = value; } }

        private static string CompactCount(double value)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (value >= 1_000_000) return (value / 1_000_000).ToString(value >= 10_000_000 ? "F0" : "F1", culture) + "M";
            if (value >= 1_000) return (value / 1_000).ToString(value >= 100_000 ? "F0" : "F1", culture) + "k";
            return value.ToString("R", culture);
        }

        private void UpdateResults()
        {
            resultsContainer.Clear();
            const int maxVisible = 10;
            var start = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, filteredResults.Count - maxVisible));
            var end = Math.Min(start + maxVisible, filteredResults.Count);
            for (var index = start; index < end; index++)
            {
                var model = filteredResults[index];
                var prefix = index == selectedIndex ? "→ " : "  ";
                var details = $"{CompactCount(model.Downloads)} downloads";
                resultsContainer.AddChild(new Text(index == selectedIndex
                    ? theme.Fg("accent", $"{prefix}{model.Id}  {details}")
                    : $"{prefix}{model.Id}{theme.Fg("muted", $"  {details}")}", 0, 0));
            }
            if (start > 0 || end < filteredResults.Count)
                resultsContainer.AddChild(new Text(theme.Fg("dim", $"  ({selectedIndex + 1}/{filteredResults.Count})"), 0, 0));
            if (filteredResults.Count == 0) resultsContainer.AddChild(new Text(theme.Fg("dim", $"  {status}"), 0, 0));
            else if (status == "Searching Hugging Face…") resultsContainer.AddChild(new Text(theme.Fg("dim", $"  {status}"), 0, 0));
            mode.ui.RequestRender();
        }

        private void FilterResults()
        {
            if (query.Length > 0)
            {
                var matches = Fuzzy.Filter(results, query, model => model.Id).Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
                filteredResults = [.. results.Where(model => matches.Contains(model.Id))];
            }
            else filteredResults = [.. results];
            selectedIndex = Math.Min(selectedIndex, Math.Max(0, filteredResults.Count - 1));
            UpdateResults();
        }

        private void ScheduleSearch()
        {
            debounce?.Cancel(); debounce = null;
            request?.Cancel(); request = null;
            if (query.Length < 2)
            {
                status = "Type at least 2 characters";
                FilterResults();
                return;
            }
            if (cache.TryGetValue(query.ToLowerInvariant(), out var cached))
            {
                results = cached;
                status = cached.Count == 0 ? "No GGUF models found" : "";
                FilterResults();
                return;
            }
            status = "Searching Hugging Face…";
            FilterResults();
            var timer = debounce = new CancellationTokenSource();
            var pending = query;
            _ = Task.Delay(500, timer.Token).ContinueWith(task => { if (!task.IsCanceled) mode.context.Loop.Post(() => _ = RunSearchAsync(pending)); }, TaskScheduler.Default);
        }

        private async Task RunSearchAsync(string text)
        {
            var current = new CancellationTokenSource();
            request = current;
            try
            {
                var found = await searchModels(text, current.Token);
                cache[text.ToLowerInvariant()] = found;
                if (closed || current.IsCancellationRequested || query != text) return;
                results = found;
                selectedIndex = 0;
                status = found.Count == 0 ? "No GGUF models found" : "";
                FilterResults();
            }
            catch (Exception error)
            {
                if (closed || current.IsCancellationRequested || query != text) return;
                results = [];
                status = error.Message;
                FilterResults();
            }
            finally { if (ReferenceEquals(request, current)) request = null; }
        }

        internal void Close(string? model)
        {
            if (closed) return;
            closed = true;
            debounce?.Cancel();
            request?.Cancel();
            onSelectModel(model);
        }

        public void HandleInput(string data)
        {
            var keys = KeybindingsManager.Global;
            if (keys.Matches(data, "tui.select.up"))
            {
                if (filteredResults.Count > 0) { selectedIndex = selectedIndex == 0 ? filteredResults.Count - 1 : selectedIndex - 1; UpdateResults(); }
                return;
            }
            if (keys.Matches(data, "tui.select.down"))
            {
                if (filteredResults.Count > 0) { selectedIndex = selectedIndex == filteredResults.Count - 1 ? 0 : selectedIndex + 1; UpdateResults(); }
                return;
            }
            if (keys.Matches(data, "tui.select.confirm"))
            {
                var exact = ExactModel().IsMatch(query) ? query : null;
                var selected = exact ?? (selectedIndex < filteredResults.Count ? filteredResults[selectedIndex].Id : null);
                if (!string.IsNullOrEmpty(selected)) Close(selected);
                return;
            }
            if (keys.Matches(data, "tui.select.cancel")) { Close(null); return; }
            input.HandleInput(data);
            var next = TextUtils.JsTrim(input.GetValue());
            if (next == query) return;
            query = next;
            ScheduleSearch();
        }
    }
}
