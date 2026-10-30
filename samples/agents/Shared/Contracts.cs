// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Samples;

public sealed record ToolSpec(string Name, string Description, JsonObject Parameters,
    Func<JsonObject, CancellationToken, Task<JsonNode?>> Execute, bool RequiresApproval = false);
public sealed record PlannedCall(string Tool, JsonObject Arguments);
public sealed record WorkflowSpec(string Id, string Title, string Instructions,
    IReadOnlyList<ToolSpec> Tools, IReadOnlyList<PlannedCall> DemoCalls);

public sealed class SampleContext
{
    public SampleContext(string workspace, JsonObject input, bool approved = false, bool live = false)
    {
        Workspace = Path.GetFullPath(workspace); Input = input; Approved = approved; Live = live;
        Directory.CreateDirectory(Workspace);
    }
    public string Workspace { get; }
    public JsonObject Input { get; }
    public bool Approved { get; }
    public bool Live { get; }
    public JsonObject State { get; } = new();
    public string Provider { get; init; } = Environment.GetEnvironmentVariable("PISHARP_SAMPLE_PROVIDER") ?? "openai";
    public string Model { get; init; } = Environment.GetEnvironmentVariable("PISHARP_SAMPLE_MODEL") ?? "";
    private string? api = Environment.GetEnvironmentVariable("PISHARP_SAMPLE_API");
    public string Api { get => api ?? LiveProvider.DefaultApi(Provider); init => api = value; }
    public string BaseUrl { get; init; } = Environment.GetEnvironmentVariable("PISHARP_SAMPLE_BASE_URL") ?? "";
    public string KeyEnvironment { get; init; } = Environment.GetEnvironmentVariable("PISHARP_SAMPLE_KEY_ENV") ?? "OPENAI_API_KEY";
    public string? Prompt { get; init; }
    public string? ResumePath { get; init; }
    public string? ImagePath { get; init; }
    /// <summary>Inject an offline handler for native-provider integration tests.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
    /// <summary>Optional host-chosen directory for run artifacts, independent of the tools' workspace.</summary>
    public string? OutputDirectory { get; init; }
    public string ResolvePath(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new ArgumentException("A workspace-relative path is required.");
        var path = Path.GetFullPath(Path.Combine(Workspace, relative));
        var prefix = Workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Path leaves the sample workspace.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Linked directories are not sample workspaces.");
            if (string.Equals(parent.FullName, Workspace, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Linked files are not sample outputs.");
        return path;
    }
    public async Task WriteJsonAsync(string relative, JsonNode? value, CancellationToken token = default)
    {
        var path = ResolvePath(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, value?.ToJsonString(JsonOptions) ?? "null", token);
    }
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };
}

public static class SampleJson
{
    public static JsonObject Object(string json) => JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Expected object.");
    public static JsonObject Schema(string properties = "{}", params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = JsonNode.Parse(properties),
        ["required"] = new JsonArray(required.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        ["additionalProperties"] = false
    };
    public static string Text(this JsonObject obj, string key, string fallback = "") => obj[key]?.GetValue<string>() ?? fallback;
}
