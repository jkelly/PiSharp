using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Sessions.Context;

public sealed record SessionSystemReplayResult(TranscriptEntry? CurrentMessage, ImmutableArray<JsonData> Tools,
    string Prompt);

/// <summary>Pure stored system-state replay from pinned transcript.ts. Declarations remain inert JSON;
/// this class never binds or executes tools. Original message history is not flattened or modified.</summary>
public sealed class SessionSystemReplay
{
    private readonly SessionContextProjectionOptions options;
    public SessionSystemReplay(SessionContextProjectionOptions? options = null)
    { this.options = options ?? new(); _ = new SessionContextProjector(this.options); }

    public SessionSystemReplayResult Replay(ImmutableArray<TranscriptEntry> messages, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (messages.IsDefault || messages.Length > options.MaximumOutputMessages) throw Failure();
        var content = new List<string>(); var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var sectionOrder = new List<string>(); var tools = new Dictionary<string, JsonData>(StringComparer.Ordinal);
        var toolOrder = new List<string>(); JsonElement? timestamp = null; long characters = 0;
        foreach (var message in messages)
        {
            token.ThrowIfCancellationRequested();
            if (message is null || message.WireBody is null) throw Failure();
            var body = message.WireBody.Value;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("role", out var role) ||
                role.ValueKind != JsonValueKind.String || role.GetString() != message.Role) throw Failure();
            characters += body.GetRawText().Length;
            if (characters > options.MaximumOutputCharacters) throw new SessionContextProjectionException(SessionContextProjectionFailure.ResourceLimit);
            if (message.Role != "system") continue;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("timestamp", out var time) ||
                time.ValueKind != JsonValueKind.Number || !time.TryGetDouble(out var finiteTime) || !double.IsFinite(finiteTime)) throw Failure();
            timestamp ??= time;
            if (!body.TryGetProperty("content", out var rawContent)) throw Failure();
            var text = Text(rawContent, token); if (text.Length > 0) content.Add(text);
            if (body.TryGetProperty("sections", out var updates) && updates.ValueKind != JsonValueKind.Null)
            {
                if (updates.ValueKind != JsonValueKind.Object) throw Failure();
                foreach (var property in OrderedProperties(updates, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (property.Value.ValueKind == JsonValueKind.Null) { sections.Remove(property.Name); sectionOrder.Remove(property.Name); }
                    else
                    {
                        if (property.Value.ValueKind != JsonValueKind.String) throw Failure();
                        if (!sections.ContainsKey(property.Name)) sectionOrder.Add(property.Name);
                        sections[property.Name] = property.Value.GetString()!;
                    }
                }
            }
            if (body.TryGetProperty("toolsRemoved", out var removed) && removed.ValueKind != JsonValueKind.Null)
                foreach (var reference in Array(removed))
                {
                    token.ThrowIfCancellationRequested(); var name = Name(reference);
                    if (tools.Remove(name)) toolOrder.Remove(name);
                }
            if (body.TryGetProperty("toolsAdded", out var added) && added.ValueKind != JsonValueKind.Null)
                foreach (var declaration in Array(added))
                {
                    token.ThrowIfCancellationRequested(); var name = Name(declaration);
                    if (!tools.ContainsKey(name)) toolOrder.Add(name);
                    tools[name] = JsonData.FromElement(declaration);
                }
        }
        var resolvedTools = toolOrder.Select(name => tools[name]).ToImmutableArray();
        if (timestamp is null && resolvedTools.IsEmpty) return new(null, [], "");
        var orderedSections = EcmaOrder(sectionOrder).ToArray();
        var currentContent = string.Join("\n\n", content);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteString("role", "system"); writer.WriteString("content", currentContent);
            if (orderedSections.Length > 0)
            {
                writer.WritePropertyName("sections"); writer.WriteStartObject();
                foreach (var name in orderedSections) { token.ThrowIfCancellationRequested(); writer.WriteString(name, sections[name]); }
                writer.WriteEndObject();
            }
            if (!resolvedTools.IsEmpty)
            {
                writer.WritePropertyName("toolsAdded"); writer.WriteStartArray();
                foreach (var tool in resolvedTools) { token.ThrowIfCancellationRequested(); writer.WriteRawValue(tool.Value.GetRawText()); }
                writer.WriteEndArray();
            }
            writer.WritePropertyName("timestamp"); if (timestamp is { } value) writer.WriteRawValue(value.GetRawText()); else writer.WriteNumberValue(0);
            writer.WriteEndObject(); writer.Flush();
        }
        var bodyText = Encoding.UTF8.GetString(output.ToArray());
        var prompt = string.Join("\n\n", new[] { currentContent }.Concat(orderedSections.Select(name => sections[name])).Where(part => part.Length > 0));
        if (bodyText.Length > options.MaximumOutputCharacters || prompt.Length > options.MaximumOutputCharacters)
            throw new SessionContextProjectionException(SessionContextProjectionFailure.ResourceLimit);
        token.ThrowIfCancellationRequested();
        return new(new("system", JsonData.Parse(bodyText)), resolvedTools, prompt);
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Failure();
    private static string Name(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : throw Failure();
    private static string Text(JsonElement value, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        var parts = new List<string>();
        foreach (var part in Array(value))
        {
            token.ThrowIfCancellationRequested();
            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw Failure();
            if (type.GetString() != "text") continue;
            if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw Failure();
            parts.Add(text.GetString()!);
        }
        return string.Join("\n", parts);
    }
    private static IEnumerable<JsonProperty> OrderedProperties(JsonElement value, CancellationToken token)
    {
        var properties = value.EnumerateObject().ToArray();
        foreach (var property in properties.Where(property => Index(property.Name) is not null).OrderBy(property => Index(property.Name)))
        { token.ThrowIfCancellationRequested(); yield return property; }
        foreach (var property in properties.Where(property => Index(property.Name) is null))
        { token.ThrowIfCancellationRequested(); yield return property; }
    }
    private static IEnumerable<string> EcmaOrder(IEnumerable<string> names)
    {
        var values = names.ToArray();
        return values.Where(name => Index(name) is not null).OrderBy(Index).Concat(values.Where(name => Index(name) is null));
    }
    private static uint? Index(string name) => name.Length <= 10 && uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value != uint.MaxValue && value.ToString(CultureInfo.InvariantCulture) == name ? value : null;
    private static SessionContextProjectionException Failure() => new(SessionContextProjectionFailure.UnsupportedMessage);
}
