// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts (setCustomEditorComponent:
// the custom editor gets the default editor's submit and change handlers, its text, and a CustomEditor its app actions;
// setupAutocompleteProvider: each addAutocompleteProvider factory wraps the provider so far).
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>An editor an extension made with ctx.ui.setEditorComponent: it lives in the Node extension host (it renders and handles
/// keys there); its submit, change and app-action events come back to the mode's loop.</summary>
internal sealed class NodeEditorComponent : IEditorComponent
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly PiExtensionHost host;
    private readonly string id;
    private readonly Action<Action> post;
    private string text = "";

    internal NodeEditorComponent(PiExtensionHost host, string id, Action<Action> post)
    {
        this.host = host; this.id = id; this.post = post;
    }

    internal string Id => id;
    public Action<string>? OnSubmit { get; set; }
    public Action<string>? OnChange { get; set; }
    /// <summary>The mode's app actions (CustomEditor actionHandlers, onEscape, onCtrlD, onPasteImage) by action name.</summary>
    internal Func<string, Action?>? Action { get; set; }

    /// <summary>An event of this editor from Node (on the host channel's thread).</summary>
    internal void Event(JsonElement value)
    {
        var kind = value.TryGetProperty("event", out var type) ? type.GetString() : null;
        var eventText = value.TryGetProperty("text", out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : null;
        switch (kind)
        {
            case "change": if (eventText is not null) { text = eventText; post(() => OnChange?.Invoke(eventText)); } return;
            case "submit": if (eventText is not null) { text = ""; post(() => OnSubmit?.Invoke(eventText)); } return;
            case "action":
                var action = value.TryGetProperty("action", out var name) ? name.GetString() : null;
                if (action is not null) post(() => Action?.Invoke(action)?.Invoke());
                return;
        }
    }

    private JsonElement? Call(string method, params JsonNode?[] args)
    {
        using var timeout = new CancellationTokenSource(Wait);
        try { return host.CallComponentAsync(id, method, new JsonArray(args), timeout.Token).WaitAsync(Wait).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException or IOException or
            PiSharp.Compatibility.Node.Pi.PiNodeHostException) { return null; }
    }
    private void Send(string method, params JsonNode?[] args) =>
        _ = host.CallComponentAsync(id, method, new JsonArray(args), CancellationToken.None).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);

    public List<string> Render(int width)
    {
        using var timeout = new CancellationTokenSource(Wait);
        try { return [.. host.RenderComponentAsync(id, width, timeout.Token).WaitAsync(Wait).GetAwaiter().GetResult()]; }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException or IOException or
            PiSharp.Compatibility.Node.Pi.PiNodeHostException) { return []; }
    }
    public void Invalidate() { }
    public void HandleInput(string data) =>
        _ = host.InputComponentAsync(id, data, CancellationToken.None).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);

    public string GetText() => Call("getText") is { ValueKind: JsonValueKind.String } value ? text = value.GetString()! : text;
    public void SetText(string value) { text = value; Send("setText", value); }
    public void AddToHistory(string value) => Send("addToHistory", value);
    public void InsertTextAtCursor(string value) => Send("insertTextAtCursor", value);
    public string GetExpandedText() => Call("getExpandedText") is { ValueKind: JsonValueKind.String } value ? value.GetString()! : GetText();
    public void SetPaddingX(int padding) => Send("setPaddingX", padding);
    public void SetAutocompleteMaxVisible(int maxVisible) => Send("setAutocompleteMaxVisible", maxVisible);
    public void SetAutocompleteProvider(IAutocompleteProvider provider) =>
        _ = host.SetEditorAutocompleteAsync(id, host.RegisterAutocompleteBase(provider), CancellationToken.None).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
}

/// <summary>A provider an extension's addAutocompleteProvider factory made around the mode's provider so far (<paramref name="inner"/>):
/// it runs in Node and calls back into <paramref name="inner"/> for what it delegates.</summary>
internal sealed class NodeAutocompleteProvider(PiExtensionHost host, string wrapper, IAutocompleteProvider inner) : IAutocompleteProvider
{
    private readonly string token = host.RegisterAutocompleteBase(inner);
    public IReadOnlyList<string> TriggerCharacters => host.AutocompleteTriggers(wrapper, token);
    public async Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken)
    {
        try { return await host.AutocompleteSuggestAsync(wrapper, token, lines, cursorLine, cursorCol, force, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or IOException or PiSharp.Compatibility.Node.Pi.PiNodeHostException)
        { return await inner.GetSuggestionsAsync(lines, cursorLine, cursorCol, force, cancellationToken).ConfigureAwait(false); }
    }
    public CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix) =>
        host.AutocompleteApply(wrapper, token, lines, cursorLine, cursorCol, item, prefix) ?? inner.ApplyCompletion(lines, cursorLine, cursorCol, item, prefix);
    public bool ShouldTriggerFileCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol) => host.AutocompleteFile(wrapper, token, lines, cursorLine, cursorCol);
}
