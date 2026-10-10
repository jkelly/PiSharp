using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.Configuration;

/// <summary>Two explicitly admitted settings files. No home/environment discovery or writes.</summary>
public interface IStartupSettingsFileSystem
{
    ValueTask<string?> ReadTextAsync(string absolutePath, CancellationToken cancellationToken);
}

public sealed class SystemStartupSettingsFileSystem : IStartupSettingsFileSystem
{
    public async ValueTask<string?> ReadTextAsync(string absolutePath, CancellationToken cancellationToken)
    {
        try
        {
            // Retain the BOM in raw UTF-8 text so the loader removes exactly one, as Pi does.
            var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

public sealed record StartupSettingsRequest(string? UserPath = null, string? ProjectPath = null, JsonData? Overrides = null);
public sealed record StartupSettingsDiagnostic(string Scope, string? Path, string Code);
public sealed record StartupSettingsSnapshot(JsonData Values, AgentPendingInputMode SteeringMode,
    AgentPendingInputMode FollowUpMode, ImmutableArray<StartupSettingsDiagnostic> Diagnostics)
{ public AgentRetryPolicy RetryPolicy { get; init; } = AgentRetryPolicy.Default; }

/// <summary>Captured user -> project -> invocation merge and queue preference projection.
/// Unknown settings are data only; they confer no execution, credential or resource permission.</summary>
public static class StartupSettings
{
    public static async Task<StartupSettingsSnapshot> LoadAsync(StartupSettingsRequest request,
        IStartupSettingsFileSystem? fileSystem = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var path in new[] { request.UserPath, request.ProjectPath })
            if (path is not null && !Path.IsPathFullyQualified(path)) throw new ArgumentException("Settings paths must be absolute.");
        cancellationToken.ThrowIfCancellationRequested();
        var overrides = request.Overrides is null ? new JsonObject() : Parse(RetrySettingsProjection.NormalizeLayer(request.Overrides).ToString());
        fileSystem ??= new SystemStartupSettingsFileSystem();
        var diagnostics = ImmutableArray.CreateBuilder<StartupSettingsDiagnostic>();
        var user = await Read(request.UserPath, "user").ConfigureAwait(false);
        var project = await Read(request.ProjectPath, "project").ConfigureAwait(false);
        var merged = MergeSettings(MergeSettings(user, project), overrides);
        var steering = Mode("steeringMode"); var followUp = Mode("followUpMode");
        cancellationToken.ThrowIfCancellationRequested();
        var values = JsonData.Parse(merged.ToJsonString());
        return new(values, steering, followUp, diagnostics.ToImmutable()) { RetryPolicy = RetrySettingsProjection.ReadEffective(values) };

        async Task<JsonObject> Read(string? path, string scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path is null) return new();
            string? text;
            try { text = await fileSystem.ReadTextAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { cancellationToken.ThrowIfCancellationRequested(); diagnostics.Add(new(scope, path, "ReadFailed")); return new(); }
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(text)) return new();
            try
            {
                var settings = Parse(text[0] == '\uFEFF' ? text[1..] : text);
                // Migrate independently per layer; presence, including null, prevents migration.
                if (!settings.ContainsKey("steeringMode") && settings.TryGetPropertyValue("queueMode", out var queue))
                { settings["steeringMode"] = queue?.DeepClone(); settings.Remove("queueMode"); }
                return Parse(RetrySettingsProjection.NormalizeLayer(JsonData.Parse(settings.ToJsonString())).ToString());
            }
            catch (JsonException) { diagnostics.Add(new(scope, path, "InvalidJson")); return new(); }
        }
        AgentPendingInputMode Mode(string name)
        {
            var node = merged[name];
            if (node is null) return AgentPendingInputMode.OneAtATime;
            if (node is JsonValue value && value.TryGetValue<JsonElement>(out var element))
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var text = element.GetString();
                    if (text == "all") return AgentPendingInputMode.All;
                    if (text is "one-at-a-time" or "") return AgentPendingInputMode.OneAtATime;
                }
                else if (element.ValueKind == JsonValueKind.False || element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && number == 0)
                    return AgentPendingInputMode.OneAtATime;
            }
            diagnostics.Add(new("effective", null, "InvalidQueueMode:" + name));
            return AgentPendingInputMode.OneAtATime;
        }
    }

    private static JsonObject Parse(string text)
    {
        using var document = JsonDocument.Parse(text, PiSharp.Contracts.JsonData.DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Settings require an object.");
        return (JsonObject)Copy(document.RootElement)!;
    }
    private static JsonNode? Copy(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            // JSON.parse's last duplicate wins, before conversion to owned strict native JSON.
            foreach (var property in element.EnumerateObject()) result[property.Name] = Copy(property.Value);
            return result;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray(); foreach (var item in element.EnumerateArray()) result.Add(Copy(item)); return result;
        }
        return element.ValueKind == JsonValueKind.Null ? null : JsonValue.Create(element.Clone());
    }
    private static JsonObject MergeObjects(JsonObject earlier, JsonObject later)
    {
        var result = (JsonObject)earlier.DeepClone();
        foreach (var pair in later)
            result[pair.Key] = result[pair.Key] is JsonObject oldObject && pair.Value is JsonObject newObject
                ? MergeObjects(oldObject, newObject) : pair.Value?.DeepClone();
        return result;
    }
    private static JsonObject MergeSettings(JsonObject earlier, JsonObject later)
    {
        var result = MergeObjects(earlier, later);
        // Pinned top-level defaultTools merge is an exception to ordinary array replacement.
        // Retain it as data; this workflow never enables any of the named tools.
        if (earlier["defaultTools"] is JsonArray oldTools && later["defaultTools"] is JsonArray newTools &&
            newTools.All(node => node is JsonValue value && value.TryGetValue<JsonElement>(out var item) &&
                item.ValueKind == JsonValueKind.String && item.GetString() is { } text && (text.StartsWith('+') || text.StartsWith('-'))))
        {
            var combined = (JsonArray)oldTools.DeepClone(); foreach (var item in newTools) combined.Add(item?.DeepClone()); result["defaultTools"] = combined;
        }
        return result;
    }
}
