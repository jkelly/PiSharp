// PiSharp-only (decision 0008). Formatting shared by the views: token counts, costs, times, device icons, providers, tool argument summaries.
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.Web.Hub;

/// <summary>Display formatting.</summary>
public static class Display
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>1234 → 1.2k, 1234567 → 1.23M.</summary>
    public static string Tokens(double value) => value >= 1e6 ? (value / 1e6).ToString("0.00", Invariant) + "M"
        : value >= 1000 ? (value / 1000).ToString("0.#", Invariant) + "k" : value.ToString("0", Invariant);

    /// <summary>A whole number with thousands separators.</summary>
    public static string Count(double value) => value.ToString("#,0", Invariant);

    /// <summary>USD with four decimals.</summary>
    public static string Cost(double value) => "$" + value.ToString("0.0000", Invariant);

    /// <summary>"now", "5m ago", "2h ago", "3d ago".</summary>
    public static string Ago(long? at)
    {
        if (at is not { } ms) return "never";
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return age.TotalSeconds < 60 ? "now" : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago" : age.TotalHours < 48 ? $"{(int)age.TotalHours}h ago" : $"{(int)age.TotalDays}d ago";
    }

    /// <summary>A duration such as 0.4s, 41.0s, 1m 12s.</summary>
    public static string Duration(double milliseconds) => milliseconds < 60_000
        ? (milliseconds / 1000).ToString("0.0", Invariant) + "s"
        : $"{(int)(milliseconds / 60_000)}m {(int)(milliseconds % 60_000 / 1000)}s";

    /// <summary>The Bootstrap icon of a device's platform.</summary>
    public static string PlatformIcon(NetworkDevice device) => device.Kind == "local" && device.Info is null ? "bi-pc-display"
        : (device.Info?.Platform ?? "") switch
        {
            var p when p.StartsWith("win", StringComparison.Ordinal) => "bi-windows",
            var p when p.StartsWith("darwin", StringComparison.Ordinal) => "bi-apple",
            var p when p.StartsWith("linux", StringComparison.Ordinal) => "bi-ubuntu",
            _ => "bi-hdd-rack",
        };

    /// <summary>The platform's name.</summary>
    public static string PlatformName(string? platform) => (platform ?? "") switch
    {
        var p when p.StartsWith("win", StringComparison.Ordinal) => "Windows",
        var p when p.StartsWith("darwin", StringComparison.Ordinal) => "macOS",
        var p when p.StartsWith("linux", StringComparison.Ordinal) => "Linux",
        var p => p,
    };

    /// <summary>The status dot class of a device status.</summary>
    public static string StatusDot(string status) => status switch
    {
        "online" => "ok",
        "checking" => "checking",
        "offline" => "off",
        _ => "danger",
    };

    /// <summary>A device status in words.</summary>
    public static string StatusText(string status) => status switch
    {
        "online" => "Online",
        "checking" => "Checking",
        "offline" => "Offline",
        "unauthorized" => "Token rejected",
        "mismatch" => "Identity changed",
        "incompatible" => "Incompatible",
        _ => status,
    };

    /// <summary>The chip tone of a device status.</summary>
    public static string StatusTone(string status) => status switch
    {
        "online" => "ok",
        "checking" or "offline" => "",
        _ => "danger",
    };

    /// <summary>"…\PiSharp" from a path: its last segment.</summary>
    public static string ShortPath(string path)
    {
        var separator = path.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        var trimmed = path.TrimEnd('\\', '/');
        var last = trimmed.LastIndexOf(separator);
        return last < 0 ? trimmed : "…" + separator + trimmed[(last + 1)..];
    }

    /// <summary>A provider's display name.</summary>
    public static string ProviderName(string provider) => provider switch
    {
        "openai" => "OpenAI",
        "openai-codex" => "OpenAI Codex",
        "anthropic" => "Anthropic",
        "google" => "Google",
        "google-vertex" => "Google Vertex",
        "azure-openai-responses" => "Azure OpenAI",
        "amazon-bedrock" => "Amazon Bedrock",
        "github-copilot" => "GitHub Copilot",
        "openrouter" => "OpenRouter",
        "xai" => "xAI",
        "mistral" => "Mistral",
        "deepseek" => "DeepSeek",
        "lmstudio" => "LM Studio",
        "ollama" => "Ollama",
        "faux" => "Faux (test)",
        _ => provider.Length == 0 ? provider : char.ToUpper(provider[0], Invariant) + provider[1..],
    };

    /// <summary>The Lobe icon of a provider, or null.</summary>
    public static string? ProviderLogo(string provider) => provider switch
    {
        "openai" => "openai",
        "openai-codex" => "codex",
        "anthropic" => "anthropic",
        "google" => "google",
        "google-vertex" => "vertexai",
        "azure-openai-responses" => "azure",
        "amazon-bedrock" => "bedrock",
        "github-copilot" => "githubcopilot",
        "openrouter" => "openrouter",
        "xai" => "xai",
        "mistral" => "mistral",
        "deepseek" => "deepseek",
        "lmstudio" => "lmstudio",
        "ollama" => "ollama",
        "cloudflare-workers-ai" => "cloudflare",
        "kimi-coding" or "moonshot" => "kimi",
        _ => null,
    } is { } name ? $"/img/providers/{name}.svg" : null;

    /// <summary>A model's context and price line.</summary>
    public static string ModelLine(ModelInfo model)
    {
        var parts = new List<string> { ProviderName(model.Provider), Tokens(model.ContextWindow).ToUpperInvariant() + " context" };
        if (model.Reasoning) parts.Add("reasoning");
        if (model.Cost.Input > 0 || model.Cost.Output > 0)
            parts.Add($"${model.Cost.Input.ToString("0.##", Invariant)} / ${model.Cost.Output.ToString("0.##", Invariant)} per 1M");
        return string.Join(" · ", parts);
    }

    /// <summary>A one-line summary of tool arguments: compact JSON, cut to 140 characters.</summary>
    public static string ArgsSummary(JsonNode? args)
    {
        if (args is null) return "";
        if (args is JsonObject json && json.Count == 1 && json.First().Value is JsonValue only && only.TryGetValue<string>(out var text)) return Cut(text.ReplaceLineEndings(" "), 140);
        return Cut(args.ToJsonString(), 140);
    }

    /// <summary>Indented JSON.</summary>
    public static string Pretty(JsonNode? node) => node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "";

    public static string Cut(string text, int length) => text.Length > length ? text[..(length - 1)] + "…" : text;

    public static string? Str(JsonObject? json, string key) => json is null ? null : json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static long? Long(JsonObject? json, string key) => json?[key] is JsonValue value && value.TryGetValue<double>(out var number) ? (long)number : null;

    public static double Number(JsonObject? json, string key) => json?[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    public static bool Bool(JsonObject? json, string key) => json?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static IEnumerable<JsonObject> Objects(JsonObject? json, string key) => json?[key] is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>The human words of a job or phase status.</summary>
    public static (string Text, string Tone) JobStatus(string? status) => status switch
    {
        "completed" => ("Completed", "ok"),
        "running" => ("Running", "ok"),
        "waiting" => ("Waiting for you", "warn"),
        "declined" => ("Declined", ""),
        "aborted" => ("Stopped", ""),
        "failed" => ("Failed", "danger"),
        _ => (status ?? "", ""),
    };

    /// <summary>A task state in words.</summary>
    public static (string Text, string Tone) TaskStatus(TaskNode task) => task.Aborting == true ? ("Stopping", "warn") : task.State.Status switch
    {
        "running" => ("Running", "ok"),
        "waiting" => ("Waiting", "warn"),
        "completing" => ("Finishing", "ok"),
        "pending" or "ready" => ("Queued", ""),
        var status => (status, ""),
    };

    /// <summary>A task's name: its label, or its kind.</summary>
    public static string TaskName(TaskNode task) => task.Label ?? task.Kind switch
    {
        "pi.generation" => "Model response",
        "pi.compaction" => "Compaction",
        "pi.tool" => "Tool call",
        "pisharp.job-approval" => "Approval",
        var kind => kind,
    };

    /// <summary>A task's icon.</summary>
    public static string TaskIcon(TaskNode task) => task.Kind switch
    {
        "pi.generation" => "bi-cpu",
        "pi.tool" => "bi-wrench",
        "pi.compaction" => "bi-layers",
        "pisharp.job-approval" => "bi-shield-check",
        var kind when kind.StartsWith("pisharp.job.", StringComparison.Ordinal) => "bi-diagram-3",
        _ => "bi-activity",
    };
}
