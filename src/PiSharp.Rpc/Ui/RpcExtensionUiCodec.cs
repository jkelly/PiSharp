using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;
using PiSharp.Extensions;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Rpc.Ui;

internal sealed record UiReply(string? Id, bool Cancelled, string? Text, bool? Confirmed, bool Invalid);
internal static class RpcExtensionUiCodec
{
    internal static bool IsResponse(JsonData raw) => raw.Value.ValueKind == JsonValueKind.Object &&
        raw.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "extension_ui_response";

    internal static UiReply Decode(JsonData raw, RpcExtensionUiOptions options)
    {
        // Hashless live records still undergo the existing strict duplicate/Unicode/depth admission.
        if (Encoding.UTF8.GetByteCount(raw.ToString()) > options.MaximumResponseBytes) throw Limit();
        _ = JsonlRecordCodec.Encode(raw, new(MaximumFrameBytes: options.MaximumResponseBytes, MaximumJsonDepth: options.MaximumJsonDepth));
        var body = raw.Value;
        string? id = null;
        if (body.TryGetProperty("id", out var field) && field.ValueKind == JsonValueKind.String &&
            field.GetString()!.Length <= options.MaximumIdCharacters) id = field.GetString();
        var cancelled = body.TryGetProperty("cancelled", out field) && field.ValueKind == JsonValueKind.True;
        string? text = null; bool? confirmed = null; var invalid = false;
        if (body.TryGetProperty("value", out field))
        {
            if (field.ValueKind == JsonValueKind.String)
            { text = field.GetString(); if (text!.Length > options.MaximumTextCharacters) throw Limit(); }
            else invalid = true;
        }
        if (body.TryGetProperty("confirmed", out field))
        {
            if (field.ValueKind is JsonValueKind.True or JsonValueKind.False) confirmed = field.GetBoolean();
            else invalid = true;
        }
        if (body.TryGetProperty("cancelled", out field) && field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) invalid = true;
        return new(id, cancelled, text, confirmed, invalid);
    }

    internal static JsonData Dialog(string id, ExtensionUiFeature method, string title, string? extra,
        ImmutableArray<string> choices, ExtensionUiDialogOptions? timing, RpcExtensionUiOptions limits)
    {
        Text(title, limits); if (extra is not null) Text(extra, limits);
        if (timing?.TimeoutMilliseconds is { } timeout && !double.IsFinite(timeout)) throw new ArgumentException("UI timeout must be finite.");
        if (method == ExtensionUiFeature.Select)
        {
            if (choices.IsDefault || choices.Length > limits.MaximumChoices) throw Limit();
            foreach (var choice in choices) Text(choice, limits);
        }
        return RpcCommandCodec.Build(writer =>
        {
            Header(writer, id, method switch { ExtensionUiFeature.Select => "select", ExtensionUiFeature.Confirm => "confirm",
                ExtensionUiFeature.Input => "input", ExtensionUiFeature.Editor => "editor", _ => throw new ArgumentException("Invalid dialog.") });
            writer.WriteString("title", title);
            if (method == ExtensionUiFeature.Select)
            { writer.WritePropertyName("options"); writer.WriteStartArray(); foreach (var choice in choices) writer.WriteStringValue(choice); writer.WriteEndArray(); }
            if (extra is not null) writer.WriteString(method switch { ExtensionUiFeature.Confirm => "message", ExtensionUiFeature.Input => "placeholder", _ => "prefill" }, extra);
            if (method == ExtensionUiFeature.Confirm && extra is null) throw new ArgumentNullException(nameof(extra));
            if (method != ExtensionUiFeature.Editor && timing?.TimeoutMilliseconds is { } value)
            {
                writer.WritePropertyName("timeout");
                writer.WriteRawValue(EcmaScriptJsonProjection.Project(value.ToString("R", CultureInfo.InvariantCulture)));
            }
        }, limits.MaximumRequestBytes);
    }

    internal static (ExtensionUiFeature Feature, JsonData Record) Notification(string id, ExtensionUiNotification notification, RpcExtensionUiOptions limits)
    {
        var feature = notification switch { ExtensionUiNotify => ExtensionUiFeature.Notify, ExtensionUiStatus => ExtensionUiFeature.Status,
            ExtensionUiTextWidget => ExtensionUiFeature.TextWidget, ExtensionUiTitle => ExtensionUiFeature.Title,
            ExtensionUiEditorText => ExtensionUiFeature.EditorText, _ => throw new ArgumentException("Unknown UI notification.") };
        var record = RpcCommandCodec.Build(writer =>
        {
            switch (notification)
            {
                case ExtensionUiNotify notice:
                    Text(notice.Message, limits); Header(writer, id, "notify"); writer.WriteString("message", notice.Message);
                    if (notice.Kind is { } kind) { if (!Enum.IsDefined(kind)) throw new ArgumentException("Invalid notification kind."); writer.WriteString("notifyType", kind.ToString().ToLowerInvariant()); } break;
                case ExtensionUiStatus status:
                    Text(status.Key, limits); if (status.Text is not null) Text(status.Text, limits);
                    Header(writer, id, "setStatus"); writer.WriteString("statusKey", status.Key); if (status.Text is not null) writer.WriteString("statusText", status.Text); break;
                case ExtensionUiTextWidget widget:
                    Text(widget.Key, limits); Header(writer, id, "setWidget"); writer.WriteString("widgetKey", widget.Key);
                    if (widget.Lines is { } lines)
                    {
                        if (lines.IsDefault || lines.Length > limits.MaximumChoices) throw Limit();
                        writer.WritePropertyName("widgetLines"); writer.WriteStartArray(); foreach (var line in lines) { Text(line, limits); writer.WriteStringValue(line); } writer.WriteEndArray();
                    }
                    if (widget.Placement is { } placement) { if (!Enum.IsDefined(placement)) throw new ArgumentException("Invalid widget placement."); writer.WriteString("widgetPlacement", placement == ExtensionUiWidgetPlacement.AboveEditor ? "aboveEditor" : "belowEditor"); } break;
                case ExtensionUiTitle title: Text(title.Title, limits); Header(writer, id, "setTitle"); writer.WriteString("title", title.Title); break;
                case ExtensionUiEditorText editor: Text(editor.Text, limits); Header(writer, id, "set_editor_text"); writer.WriteString("text", editor.Text); break;
            }
        }, limits.MaximumRequestBytes);
        return (feature, record);
    }
    private static void Header(Utf8JsonWriter writer, string id, string method)
    { writer.WriteString("type", "extension_ui_request"); writer.WriteString("id", id); writer.WriteString("method", method); }
    private static void Text(string value, RpcExtensionUiOptions limits)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > limits.MaximumTextCharacters) throw Limit();
        for (var i = 0; i < value.Length; i++)
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new ArgumentException("Invalid UI text Unicode."); }
            else if (char.IsLowSurrogate(value[i])) throw new ArgumentException("Invalid UI text Unicode.");
    }
    private static RpcDispatchException Limit() => new(RpcDispatchFailure.ResourceLimit);
}
