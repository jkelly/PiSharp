using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.Extensions;

namespace PiSharp.Compatibility.Node;

/// <summary>Native invocation for source dialog arguments. The existing registry/worker callback
/// owns the supplied scoped UI, cancellation and this returned Task until settlement.</summary>
public static class OriginalUiDialogBroker
{
    public static async Task<WorkerValue> InvokeAsync(string method, JsonData suppliedArguments,
        bool optionsPresent, IExtensionUi? scopedUi, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(suppliedArguments); token.ThrowIfCancellationRequested();
        if (method is not ("ui.select" or "ui.confirm" or "ui.input" or "ui.editor"))
            throw new InvalidOperationException("Original dialog method differs.");
        if (Encoding.UTF8.GetByteCount(suppliedArguments.ToString()) > 262_144)
            throw new IOException("Original dialog argument byte limit.");
        var args = suppliedArguments.Value;
        var minimum = method is "ui.select" or "ui.confirm" ? 2 : 1;
        var maximum = method == "ui.editor" ? 2 : 3;
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < minimum || args.GetArrayLength() > maximum)
            throw new InvalidOperationException("Original dialog argument count differs.");
        var title = Text(args[0]); var secondary = args.GetArrayLength() > 1 ? args[1] : default;
        ExtensionUiDialogOptions? options = null;
        if (optionsPresent)
        {
            if (method == "ui.editor" || args.GetArrayLength() != 3 || args[2].ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Original dialog options differ.");
            var properties = args[2].EnumerateObject().ToArray();
            if (properties.Any(property => property.Name != "timeout") || properties.Length > 1)
                throw new NotSupportedException("Dialog AbortSignal/unknown options require a separately admitted source cancellation hook.");
            if (args[2].TryGetProperty("timeout", out var timeout))
            {
                if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetDouble(out var milliseconds) || !double.IsFinite(milliseconds))
                    throw new InvalidOperationException("Finite original dialog timeout required.");
                options = new(milliseconds);
            }
            else options = new();
        }
        else if (args.GetArrayLength() == 3 && args[2].ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("Absent original options carry a value.");

        if (method == "ui.select")
        {
            if (secondary.ValueKind != JsonValueKind.Array || secondary.GetArrayLength() > 256)
                throw new InvalidOperationException("Original select choices differ.");
            var choices = secondary.EnumerateArray().Select(Text).ToImmutableArray();
            return await Invoke("ui.select", () => scopedUi is null ? ValueTask.FromResult(ExtensionUiOutcome<string>.Unavailable(ExtensionUiUnavailableReason.NoUi)) :
                scopedUi.SelectAsync(title, choices, options, token), token).ConfigureAwait(false);
        }
        if (method == "ui.confirm")
        {
            var message = Text(secondary);
            return await Invoke("ui.confirm", () => scopedUi is null ? ValueTask.FromResult(ExtensionUiOutcome<bool>.Unavailable(ExtensionUiUnavailableReason.NoUi)) :
                scopedUi.ConfirmAsync(title, message, options, token), token).ConfigureAwait(false);
        }
        var text = secondary.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : Text(secondary);
        return method == "ui.input"
            ? await Invoke(method, () => scopedUi is null ? ValueTask.FromResult(ExtensionUiOutcome<string>.Unavailable(ExtensionUiUnavailableReason.NoUi)) :
                scopedUi.InputAsync(title, text, options, token), token).ConfigureAwait(false)
            : await Invoke(method, () => scopedUi is null ? ValueTask.FromResult(ExtensionUiOutcome<string>.Unavailable(ExtensionUiUnavailableReason.NoUi)) :
                scopedUi.EditorAsync(title, text, token), token).ConfigureAwait(false);
    }
    private static async Task<WorkerValue> Invoke<T>(string method, Func<ValueTask<ExtensionUiOutcome<T>>> invoke, CancellationToken token)
    {
        Task<ExtensionUiOutcome<T>>? original = null; ExtensionUiOutcome<T> outcome;
        try { original = invoke().AsTask(); outcome = await original.ConfigureAwait(false); }
        catch (OperationCanceledException) when (original is { IsCanceled: true }) { throw; }
        catch (Exception direct)
        { throw new OriginalUiDialogOriginalException(method, original, original is { IsFaulted: true } ? original.Exception! : direct, direct); }
        token.ThrowIfCancellationRequested();
        var value = new Dictionary<string, object?>
        {
            ["outcome"] = outcome.Kind switch { ExtensionUiOutcomeKind.Value => "value", ExtensionUiOutcomeKind.Cancelled => "cancelled",
                ExtensionUiOutcomeKind.TimedOut => "timedOut", _ => "unavailable" },
            ["presence"] = outcome.Kind == ExtensionUiOutcomeKind.Value ? "json" : "undefined"
        };
        if (outcome.Kind == ExtensionUiOutcomeKind.Value) value.Add("value", outcome.Value);
        if (outcome.UnavailableReason is { } reason) value.Add("reason", reason.ToString());
        // Keep the strict outer worker codec unchanged. JavaScript strings may contain lone
        // UTF16 code units, so all string dialogs carry one bounded ASCII JSON literal inside
        // valueJson. The admitted JS caller validates this shape and parses it exactly once.
        if (outcome.Kind == ExtensionUiOutcomeKind.Value && outcome.Value is string text)
        {
            value.Remove("value"); value.Add("valueJson", StringCodeUnits(text));
        }
        return WorkerValue.FromJson(JsonData.Parse(JsonSerializer.Serialize(value)));
    }
    private static string StringCodeUnits(string text)
    {
        if (text.Length > 65_536) throw new IOException("Dialog result text limit.");
        var json = new StringBuilder(text.Length + 2).Append('"');
        foreach (var unit in text)
            if (unit is '"' or '\\') json.Append('\\').Append(unit);
            else if (unit < 32 || unit > 126) json.Append("\\u").Append(((int)unit).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else json.Append(unit);
        return json.Append('"').ToString();
    }
    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Original dialog string required.");
        // Parsed JSON syntax is validated; preserve source UTF16 code units, including escaped lone surrogates.
        var raw = value.GetRawText(); if (raw.Length > 393_218) throw new IOException("Dialog text limit.");
        var decoded = new StringBuilder();
        for (var index = 1; index < raw.Length - 1; index++)
        {
            if (raw[index] != '\\') { decoded.Append(raw[index]); continue; }
            var escape = raw[++index];
            if (escape == 'u')
            {
                var code = 0;
                for (var digit = 0; digit < 4; digit++)
                { var c = raw[++index]; code = (code << 4) | (c <= '9' ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10); }
                decoded.Append((char)code);
            }
            else decoded.Append(escape switch { 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escape });
        }
        if (decoded.Length > 65_536) throw new IOException("Dialog text limit.");
        return decoded.ToString();
    }
}

public sealed class OriginalUiDialogOriginalException(string method, Task? original, Exception evidence, Exception direct)
    : IOException("Original native dialog callback failed.", evidence)
{
    public string Method { get; } = method;
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
    public Exception Direct { get; } = direct;
}
