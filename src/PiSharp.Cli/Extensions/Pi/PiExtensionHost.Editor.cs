// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts (setCustomEditorComponent,
// addAutocompleteProvider/setupAutocompleteProvider) and packages/coding-agent/src/modes/interactive/components/custom-editor.ts.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>The interactive mode's side of extension editors (ctx.ui.setEditorComponent) and autocomplete providers
/// (ctx.ui.addAutocompleteProvider): the components live in Node; their events, the mode's keybindings and the mode's own autocomplete
/// provider cross the host channel.</summary>
internal sealed partial class PiExtensionHost
{
    /// <summary>An extension component's event (an editor's submit, change or app action): component id and the event fields.</summary>
    internal Action<string, JsonElement>? ComponentEvent { get; set; }

    /// <summary>CustomEditor.onExtensionShortcut of an extension editor: the mode's extension shortcuts.</summary>
    internal Func<string, bool>? EditorShortcut { get; set; }

    private readonly ConcurrentDictionary<string, IAutocompleteProvider> _autocompleteBases = new(StringComparer.Ordinal);
    private long _nextAutocomplete;

    /// <summary>The mode's effective keybindings and its editor's app actions, for editors extending CustomEditor.</summary>
    internal Task ConfigureEditorAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> keybindings, IEnumerable<string> actions, CancellationToken token) =>
        IsRunning ? Node.RequestAsync("editor.configure", new JsonObject
        {
            ["keybindings"] = new JsonObject([.. keybindings.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)new JsonArray([.. pair.Value.Select(key => (JsonNode)key)])))]),
            ["actions"] = new JsonArray([.. actions.Select(action => (JsonNode)action)])
        }, token) : Task.CompletedTask;

    /// <summary>Calls a method of a Node component (an editor's getText, setText, addToHistory...).</summary>
    internal Task<JsonElement?> CallComponentAsync(string id, string method, JsonArray? args, CancellationToken token) =>
        Node.RequestAsync("component.method", new JsonObject { ["id"] = id, ["method"] = method, ["args"] = args ?? [] }, token);

    /// <summary>A host autocomplete provider the Node side can call (an extension editor's provider, a wrapper's base).</summary>
    internal string RegisterAutocompleteBase(IAutocompleteProvider provider)
    {
        var token = "b" + Interlocked.Increment(ref _nextAutocomplete).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _autocompleteBases[token] = provider;
        return token;
    }

    internal Task SetEditorAutocompleteAsync(string id, string token, CancellationToken cancellationToken) =>
        Node.RequestAsync("editor.autocomplete", new JsonObject { ["id"] = id, ["token"] = token }, cancellationToken);

    private static JsonArray Lines(IReadOnlyList<string> lines) => new([.. lines.Select(line => (JsonNode)line)]);

    internal async Task<AutocompleteSuggestions?> AutocompleteSuggestAsync(string wrapper, string token, IReadOnlyList<string> lines, int cursorLine, int cursorCol,
        bool force, CancellationToken cancellationToken)
    {
        var result = await Node.RequestAsync("autocomplete.suggest", new JsonObject
        {
            ["wrapper"] = wrapper, ["token"] = token, ["lines"] = Lines(lines), ["cursorLine"] = cursorLine, ["cursorCol"] = cursorCol, ["force"] = force
        }, cancellationToken).ConfigureAwait(false);
        return result is { ValueKind: JsonValueKind.Object } value ? ReadSuggestions(value) : null;
    }

    internal CompletionResult? AutocompleteApply(string wrapper, string token, IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix)
    {
        var result = Bounded("autocomplete.apply", new JsonObject
        {
            ["wrapper"] = wrapper, ["token"] = token, ["lines"] = Lines(lines), ["cursorLine"] = cursorLine, ["cursorCol"] = cursorCol, ["prefix"] = prefix,
            ["item"] = new JsonObject { ["value"] = item.Value, ["label"] = item.Label, ["description"] = item.Description }
        });
        return result is { ValueKind: JsonValueKind.Object } value ? ReadCompletion(value) : null;
    }

    internal IReadOnlyList<string> AutocompleteTriggers(string wrapper, string token) =>
        Bounded("autocomplete.triggers", new JsonObject { ["wrapper"] = wrapper, ["token"] = token }) is { ValueKind: JsonValueKind.Array } value
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)] : [];

    internal bool AutocompleteFile(string wrapper, string token, IReadOnlyList<string> lines, int cursorLine, int cursorCol) =>
        Bounded("autocomplete.file", new JsonObject { ["wrapper"] = wrapper, ["token"] = token, ["lines"] = Lines(lines), ["cursorLine"] = cursorLine, ["cursorCol"] = cursorCol })
            is not { ValueKind: JsonValueKind.False };

    private JsonElement? Bounded(string method, JsonObject parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return Node.RequestAsync(method, parameters, timeout.Token).WaitAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException or PiSharp.Compatibility.Node.Pi.PiNodeHostException or IOException)
        { return null; }
    }

    private static AutocompleteSuggestions? ReadSuggestions(JsonElement value)
    {
        if (!value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        return new([.. items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new AutocompleteItem(Text(item, "value") ?? "", Text(item, "label") ?? Text(item, "value") ?? "", Text(item, "description")))],
            value.TryGetProperty("prefix", out var prefix) && prefix.ValueKind == JsonValueKind.String ? prefix.GetString()! : "");
    }

    private static CompletionResult? ReadCompletion(JsonElement value) =>
        value.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array
            ? new([.. lines.EnumerateArray().Select(line => line.GetString() ?? "")], value.GetProperty("cursorLine").GetInt32(), value.GetProperty("cursorCol").GetInt32())
            : null;

    /// <summary>A Node provider's call to the host provider it wraps (autocomplete.base).</summary>
    private async Task<JsonNode?> AutocompleteBaseAsync(JsonElement p, CancellationToken token)
    {
        if (!_autocompleteBases.TryGetValue(p.GetProperty("token").GetString()!, out var provider)) return null;
        IReadOnlyList<string> LinesOf() => p.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array
            ? [.. lines.EnumerateArray().Select(line => line.GetString() ?? "")] : [];
        int Number(string name) => p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        switch (p.GetProperty("op").GetString())
        {
            case "suggest":
            {
                var suggestions = await provider.GetSuggestionsAsync(LinesOf(), Number("cursorLine"), Number("cursorCol"),
                    p.TryGetProperty("force", out var force) && force.ValueKind == JsonValueKind.True, token).ConfigureAwait(false);
                return suggestions is null ? null : new JsonObject
                {
                    ["items"] = new JsonArray([.. suggestions.Items.Select(item => (JsonNode)new JsonObject { ["value"] = item.Value, ["label"] = item.Label, ["description"] = item.Description })]),
                    ["prefix"] = suggestions.Prefix
                };
            }
            case "apply":
            {
                var item = p.GetProperty("item");
                var result = provider.ApplyCompletion(LinesOf(), Number("cursorLine"), Number("cursorCol"),
                    new(item.GetProperty("value").GetString() ?? "", item.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString()! : "",
                        item.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null),
                    p.TryGetProperty("prefix", out var prefix) ? prefix.GetString() ?? "" : "");
                return new JsonObject { ["lines"] = new JsonArray([.. result.Lines.Select(line => (JsonNode)line)]), ["cursorLine"] = result.CursorLine, ["cursorCol"] = result.CursorCol };
            }
            case "triggers": return new JsonArray([.. provider.TriggerCharacters.Select(trigger => (JsonNode)trigger)]);
            case "file": return provider.ShouldTriggerFileCompletion(LinesOf(), Number("cursorLine"), Number("cursorCol"));
            default: return null;
        }
    }
}
