using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

namespace PiSharp.Cli.Settings;

/// <summary>Preferences select only already admitted native catalog/binding capabilities.</summary>
internal sealed record SettingsModelSelection(string? Provider, string? Model, string? MaximumTokens)
{
    internal LiveSessionSelection Resolve(StartupSettingsSnapshot? settings) => LiveSessionSelection.Parse(
        Provider ?? Read(settings, "defaultProvider"), Model ?? Read(settings, "defaultModel"), MaximumTokens);

    internal static string? Thinking(StartupSettingsSnapshot? settings, ModelDescriptor model, string? cli,
        bool restoring, ImmutableArray<string> available)
    {
        if (cli is null && restoring) return null; // Preserve the acknowledged history restored by the lifecycle.
        string? requested = cli;
        if (requested is null && settings is not null)
        {
            var values = settings.Values.Value;
            if (values.TryGetProperty("modelThinkingLevels", out var overrides) && overrides.ValueKind == JsonValueKind.Object &&
                overrides.TryGetProperty(model.Provider + "/" + model.Id, out var perModel) &&
                perModel.ValueKind == JsonValueKind.String && perModel.GetString() is { Length: > 0 } level)
                requested = level;
            requested ??= Read(settings, "defaultThinkingLevel") ?? "medium";
        }
        return requested is null ? null : Clamp(requested, available);
    }

    internal static string Clamp(string requested, ImmutableArray<string> available)
    {
        // Available levels come from the actual registered transport, never catalog guesses.
        if (available.IsDefaultOrEmpty) throw new ArgumentException("Missing native thinking capabilities.", nameof(available));
        if (available.Contains(requested, StringComparer.Ordinal)) return requested;
        var index = ThinkingLevels.Ordered.IndexOf(requested);
        if (index < 0) return available[0];
        for (var next = index; next < ThinkingLevels.Ordered.Length; next++)
            if (available.Contains(ThinkingLevels.Ordered[next], StringComparer.Ordinal)) return ThinkingLevels.Ordered[next];
        for (var previous = index - 1; previous >= 0; previous--)
            if (available.Contains(ThinkingLevels.Ordered[previous], StringComparer.Ordinal)) return ThinkingLevels.Ordered[previous];
        return available[0];
    }

    private static string? Read(StartupSettingsSnapshot? settings, string name)
    {
        if (settings is null || !settings.Values.Value.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        return value.GetString();
    }
}
