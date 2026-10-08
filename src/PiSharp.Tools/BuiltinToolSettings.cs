// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/settings-manager.ts (getShellPath,
// getShellCommandPrefix, getImageAutoResize, getBlockImages).
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Tools.Processes;

namespace PiSharp.Tools;

/// <summary>
/// The tool settings of settings.json: <c>shellPath</c> (~ expanded), <c>shellCommandPrefix</c>, <c>images.autoResize</c>
/// (default true) and <c>images.blockImages</c> (default false). Values are data; they grant no tool or path permission.
/// </summary>
public sealed record BuiltinToolSettings(string? ShellPath = null, string? ShellCommandPrefix = null,
    bool AutoResizeImages = true, bool BlockImages = false)
{
    public static BuiltinToolSettings Default { get; } = new();

    /// <summary>Reads the merged (user, project, invocation) settings object.</summary>
    public static BuiltinToolSettings FromSettings(JsonData? settings, ShellHost? host = null)
    {
        if (settings is null || settings.Value.ValueKind != JsonValueKind.Object) return Default;
        var root = settings.Value;
        // Source getShellPath: a falsy value is returned as is (no custom shell); a string is normalized.
        var shellPath = root.TryGetProperty("shellPath", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } text
            ? ShellDiscovery.NormalizeShellPath(text, host) : null;
        // Source bash.ts: `commandPrefix ? `${commandPrefix}\n${command}` : command`, so an empty prefix is no prefix.
        var prefix = root.TryGetProperty("shellCommandPrefix", out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } prefixText ? prefixText : null;
        var images = root.TryGetProperty("images", out var section) && section.ValueKind == JsonValueKind.Object ? section : default;
        return new(shellPath, prefix, Flag(images, "autoResize", true), Flag(images, "blockImages", false));
    }

    // Source `images?.name ?? fallback`, used for its JavaScript truthiness.
    private static bool Flag(JsonElement section, string name, bool fallback)
    {
        if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0 && !double.IsNaN(number),
            JsonValueKind.String => value.GetString()!.Length != 0,
            _ => true
        };
    }
}
