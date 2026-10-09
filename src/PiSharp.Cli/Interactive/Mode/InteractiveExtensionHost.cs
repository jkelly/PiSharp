// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (the extension UI context
// the mode gives extensions: widgets, header and footer factories, working indicator, hidden thinking label, tools expansion,
// terminal input listeners) and core/extensions/runner.ts (getMessageRenderer, getEntryRenderer, getMarkdownTransformers). PiSharp's
// TypeScript extensions live in the Node extension host: their components render there and the mode draws the rows.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>The interactive mode's view of the run's extension host.</summary>
internal interface IInteractiveExtensionHost
{
    /// <summary>Connects ctx.ui publications, component redraw requests and the reads the extensions make of the terminal.</summary>
    void Attach(Action<string, JsonElement> publications, Action<string> invalidated, InteractiveExtensionReads reads);
    /// <summary>A Node component's rows at a width (empty when it cannot render).</summary>
    IReadOnlyList<string> RenderComponent(string componentId, int width);
    void InputComponent(string componentId, string data);
    /// <summary>An onTerminalInput handler's answer for one input chunk.</summary>
    TuiInputListenerResult? TerminalInput(string callbackId, string data);
    bool RendersMessage(string customType);
    bool RendersEntry(string customType);
    IReadOnlyList<string>? RenderMessage(string customType, JsonObject message, int width, bool expanded);
    IReadOnlyList<string>? RenderEntry(string customType, JsonObject entry, int width, bool expanded);
    bool HasMarkdownTransformers { get; }
    string TransformMarkdown(string markdown, JsonObject context);
    IReadOnlyList<LoadedResource> LoadedExtensions();
    IReadOnlyList<ResourceDiagnostic> Diagnostics();
    /// <summary>ctx.ui.setEditorComponent: the extension editor with this component id (null when the host has none). Its events run
    /// through <paramref name="post"/> on the mode's loop; <paramref name="action"/> resolves the mode's app actions by name.</summary>
    IEditorComponent? CreateEditor(string componentId, Action<Action> post, Func<string, Action?> action) => null;
    /// <summary>ctx.ui.addAutocompleteProvider: the wrapper the extension's factory makes around the provider so far.</summary>
    Func<IAutocompleteProvider, IAutocompleteProvider>? AutocompleteWrapper(string wrapperId) => null;
    /// <summary>The mode's keybindings, its editor's app actions and its extension shortcuts, for extension editors.</summary>
    void ConfigureEditor(IReadOnlyDictionary<string, IReadOnlyList<string>> keybindings, IEnumerable<string> actions, Func<string, bool> shortcut) { }
}

/// <summary>What the extensions read from the mode without entering its loop (ctx.ui.getEditorText, the footer data, tui.terminal).</summary>
internal sealed record InteractiveExtensionReads(Func<string> EditorText, Func<(int Columns, int Rows)> TerminalSize, Func<bool> ToolsExpanded,
    Func<string?> GitBranch, Func<int> AvailableProviderCount);

/// <summary>The Node extension host behind <see cref="IInteractiveExtensionHost"/>. Renders are answered synchronously (the mode renders
/// on its loop) with a bounded wait; a render or transform that fails or times out draws nothing or keeps the Markdown.</summary>
internal sealed class PiInteractiveExtensionHost(PiExtensionHost host) : IInteractiveExtensionHost
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<(string Markdown, string Context), string> transforms = new();

    public void Attach(Action<string, JsonElement> publications, Action<string> invalidated, InteractiveExtensionReads reads)
    {
        host.InteractiveUi = publications;
        host.ComponentInvalidated = invalidated;
        host.EditorText = reads.EditorText;
        host.TerminalSize = reads.TerminalSize;
        host.ToolsExpanded = reads.ToolsExpanded;
        host.GitBranch = reads.GitBranch;
        host.AvailableProviderCount = reads.AvailableProviderCount;
    }

    private static T? Bounded<T>(Func<CancellationToken, Task<T>> request, T? fallback)
    {
        using var timeout = new CancellationTokenSource(Wait);
        try { return request(timeout.Token).WaitAsync(Wait).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or InvalidOperationException or IOException or
            NotSupportedException or JsonException or KeyNotFoundException) { return fallback; }
    }

    public IReadOnlyList<string> RenderComponent(string componentId, int width) =>
        Bounded<ImmutableArray<string>>(token => host.RenderComponentAsync(componentId, width, token), []) is { IsDefault: false } rows ? rows : [];

    public void InputComponent(string componentId, string data) =>
        _ = host.InputComponentAsync(componentId, data, CancellationToken.None).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);

    public TuiInputListenerResult? TerminalInput(string callbackId, string data)
    {
        var result = Bounded(async token => await host.InvokeTerminalInputAsync(callbackId, data, token).ConfigureAwait(false), null);
        if (result is not { ValueKind: JsonValueKind.Object } value) return null;
        var consume = value.TryGetProperty("consume", out var flag) && flag.ValueKind == JsonValueKind.True;
        var replaced = value.TryGetProperty("data", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        return consume || replaced is not null ? new TuiInputListenerResult(consume, replaced) : null;
    }

    private bool Registers(string field, string customType) => host.Extensions.Any(extension =>
        (extension.Descriptor[field] as JsonArray ?? []).Any(item => item?.GetValue<string>() == customType));

    public bool RendersMessage(string customType) => Registers("messageRenderers", customType);
    public bool RendersEntry(string customType) => Registers("entryRenderers", customType);

    public IReadOnlyList<string>? RenderMessage(string customType, JsonObject message, int width, bool expanded) =>
        Bounded(token => host.RenderMessageAsync(customType, message, width, expanded, token), null) is { } rows ? rows : null;

    public IReadOnlyList<string>? RenderEntry(string customType, JsonObject entry, int width, bool expanded) =>
        Bounded(token => host.RenderEntryAsync(customType, entry, width, expanded, token), null) is { } rows ? rows : null;

    public bool HasMarkdownTransformers => host.Extensions.Any(extension => extension.Descriptor["markdownTransformer"]?.GetValue<bool>() == true);

    public string TransformMarkdown(string markdown, JsonObject context)
    {
        var key = (markdown, context.ToJsonString());
        if (transforms.TryGetValue(key, out var cached)) return cached;
        var transformed = Bounded(token => host.TransformMarkdownAsync(markdown, context, token), null);
        if (transformed is null) return markdown;
        if (transforms.Count > 512) transforms.Clear();
        transforms[key] = transformed;
        return transformed;
    }

    public IReadOnlyList<LoadedResource> LoadedExtensions() => [.. host.Extensions.Select(extension => new LoadedResource(extension.Path, null))];

    public IReadOnlyList<ResourceDiagnostic> Diagnostics() => [.. host.Errors.Select(error => new ResourceDiagnostic("error", error.Error, error.Path))];

    private readonly ConcurrentDictionary<string, NodeEditorComponent> editors = new(StringComparer.Ordinal);

    public IEditorComponent? CreateEditor(string componentId, Action<Action> post, Func<string, Action?> action)
    {
        var editor = new NodeEditorComponent(host, componentId, post) { Action = action };
        editors[componentId] = editor;
        host.ComponentEvent = (id, value) => { if (editors.TryGetValue(id, out var target)) target.Event(value); };
        return editor;
    }

    public Func<IAutocompleteProvider, IAutocompleteProvider>? AutocompleteWrapper(string wrapperId) => inner => new NodeAutocompleteProvider(host, wrapperId, inner);

    public void ConfigureEditor(IReadOnlyDictionary<string, IReadOnlyList<string>> keybindings, IEnumerable<string> actions, Func<string, bool> shortcut)
    {
        host.EditorShortcut = shortcut;
        _ = host.ConfigureEditorAsync(keybindings, [.. actions], CancellationToken.None).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
    }
}

/// <summary>A component that lives in the extension host: its rows are fetched per width until it asks to be redrawn.</summary>
internal sealed class ExtensionRowsComponent(Func<int, IReadOnlyList<string>> render, Action<string>? input = null) : IComponent, IInputHandler
{
    private readonly Dictionary<int, List<string>> cache = [];
    public List<string> Render(int width)
    {
        if (cache.TryGetValue(width, out var rows)) return [.. rows];
        rows = [.. render(width).Select(row => TextUtils.VisibleWidth(row) > width ? TextUtils.TruncateToWidth(row, width, "") : row)];
        cache[width] = rows;
        return [.. rows];
    }
    public void Invalidate() => cache.Clear();
    public void HandleInput(string data) => input?.Invoke(data);
}
