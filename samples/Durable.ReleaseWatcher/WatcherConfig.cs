// The watcher's configuration (watcher.sample.json): the session it works in, its sources, its judgment policy, how often the source check
// runs, which judged labels get researched, and the preference profile its feedback goes to. It holds no secrets: a private source names a
// credential reference that the device's own options resolve, and the device and trigger tokens come from environment variables.
using System.Text.Json.Nodes;

namespace PiSharp.Durable.ReleaseWatcher;

/// <summary>A research angle for a release judged risky.</summary>
public sealed record WatcherAngle(string Title, string? Prompt = null);

/// <summary>The release watcher's configuration.</summary>
public sealed record WatcherConfig
{
    /// <summary>The watcher session's name; with <see cref="Cwd"/> it identifies the session, so setup finds it again.</summary>
    public string Session { get; init; } = "release-watcher";

    /// <summary>The session's working directory, one of the device's start directories.</summary>
    public required string Cwd { get; init; }

    /// <summary>The session's model, <c>{provider, modelId}</c>; null uses the device default.</summary>
    public JsonObject? Model { get; init; }

    /// <summary>How often the source check runs.</summary>
    public int EveryMinutes { get; init; } = 60;

    /// <summary>The preference profile feedback goes to.</summary>
    public string Profile { get; init; } = "default";

    /// <summary>Source specs as <c>POST /v1/sources</c> takes them.</summary>
    public IReadOnlyList<JsonObject> Sources { get; init; } = [];

    /// <summary>The judgment policy's ID.</summary>
    public required string PolicyId { get; init; }

    /// <summary>The policy spec as <c>POST /v1/policies/{id}/versions</c> takes it.</summary>
    public required JsonObject PolicySpec { get; init; }

    /// <summary>Judged labels that start supervised research once the judgment is approved.</summary>
    public IReadOnlyList<string> ResearchLabels { get; init; } = [];

    /// <summary>The research angles; none lets the device plan them.</summary>
    public IReadOnlyList<WatcherAngle> ResearchAngles { get; init; } = [];

    /// <summary>The model-request budget of each research run.</summary>
    public int ResearchRequests { get; init; } = 10;

    /// <summary>Reads a configuration file.</summary>
    /// <exception cref="InvalidDataException">The file is not a watcher configuration.</exception>
    public static WatcherConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads a configuration.</summary>
    /// <exception cref="InvalidDataException">The text is not a watcher configuration.</exception>
    public static WatcherConfig Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("The configuration must be a JSON object.");
        var policy = root["policy"] as JsonObject ?? throw new InvalidDataException("policy is required.");
        var research = root["research"] as JsonObject;
        return new WatcherConfig
        {
            Session = Text(root, "session") ?? "release-watcher",
            Cwd = Text(root, "cwd") ?? throw new InvalidDataException("cwd is required."),
            Model = root["model"]?.DeepClone() as JsonObject,
            EveryMinutes = root["everyMinutes"]?.GetValue<int>() ?? 60,
            Profile = Text(root, "profile") ?? "default",
            Sources = [.. (root["sources"] as JsonArray ?? []).OfType<JsonObject>().Select(source => (JsonObject)source.DeepClone())],
            PolicyId = Text(policy, "id") ?? throw new InvalidDataException("policy.id is required."),
            PolicySpec = policy["spec"]?.DeepClone() as JsonObject ?? throw new InvalidDataException("policy.spec is required."),
            ResearchLabels = [.. (research?["labels"] as JsonArray ?? []).Select(label => label!.GetValue<string>())],
            ResearchAngles = [.. (research?["angles"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(angle => new WatcherAngle(Text(angle, "title") ?? throw new InvalidDataException("An angle needs a title."), Text(angle, "prompt")))],
            ResearchRequests = research?["requests"]?.GetValue<int>() ?? 10,
        };
    }

    private static string? Text(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
