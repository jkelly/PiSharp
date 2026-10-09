// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/runner.ts (emitProjectTrustEvent,
// emitResourcesDiscover), packages/coding-agent/src/core/project-trust.ts (resolveProjectTrusted: a decision with remember is saved)
// and packages/coding-agent/src/core/agent-session.ts (extendResourcesFromExtensions).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Pi;

/// <summary>A project_trust handler's decision: yes or no, and whether the store should remember it.</summary>
internal sealed record PiProjectTrustDecision(bool Trusted, bool Remember);

/// <summary>Resource paths returned by resources_discover handlers, in handler order.</summary>
internal sealed record PiDiscoveredResources(ImmutableArray<string> SkillPaths, ImmutableArray<string> PromptPaths, ImmutableArray<string> ThemePaths)
{
    internal static PiDiscoveredResources Empty { get; } = new([], [], []);
    internal bool IsEmpty => SkillPaths.IsEmpty && PromptPaths.IsEmpty && ThemePaths.IsEmpty;
}

/// <summary>The two extension events the CLI's own flows raise. The loaded extensions (IMPL-E) arrive as a registry and a captured
/// snapshot; both events run through <see cref="ExtensionRegistry.ReduceEventAsync"/>.</summary>
internal static class PiExtensionEvents
{
    internal const string ProjectTrust = "project_trust";
    internal const string ResourcesDiscover = "resources_discover";

    /// <summary>Source emitProjectTrustEvent: handlers in registration order; the first result that is not
    /// <c>trusted: "undecided"</c> decides (<c>trusted === "yes"</c>); later handlers' results are ignored. A failing handler is
    /// reported as <c>Extension "&lt;path&gt;" project_trust error: &lt;message&gt;</c> (resolveProjectTrusted's onExtensionError) and the
    /// next one runs. Null when no handler decided.</summary>
    internal static async Task<PiProjectTrustDecision?> ProjectTrustAsync(ExtensionRegistry registry, ExtensionRegistrySnapshot snapshot, string cwd,
        Func<string, ValueTask>? onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(snapshot);
        if (!registry.HasEventHandlers(snapshot, ProjectTrust)) return null;
        PiProjectTrustDecision? decided = null;
        var initial = JsonData.Parse(JsonSerializer.Serialize(new { type = ProjectTrust, cwd }));
        await registry.ReduceEventAsync(snapshot, ProjectTrust, initial, (_, result) =>
        {
            if (decided is null && result.Value.ValueKind == JsonValueKind.Object)
            {
                var trusted = result.Value.TryGetProperty("trusted", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (trusted != "undecided")
                    decided = new(trusted == "yes", result.Value.TryGetProperty("remember", out var remember) && remember.ValueKind == JsonValueKind.True);
            }
            return null; // Every handler sees the unchanged { type, cwd } event.
        }, async (diagnostic, _) =>
        {
            if (onError is not null) await onError($"Extension \"{diagnostic.OwnerId}\" project_trust error: {diagnostic.Message ?? diagnostic.Failure.ToString()}").ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return decided;
    }

    /// <summary>Source emitResourcesDiscover: every handler sees <c>{ type, cwd, reason }</c> (reason "startup" or "reload"); the
    /// <c>skillPaths</c>, <c>promptPaths</c> and <c>themePaths</c> string arrays of the results are collected in order. A failing handler
    /// is reported as an extension error (<c>onError(extensionPath, message)</c>) and the next one runs.</summary>
    internal static async Task<PiDiscoveredResources> ResourcesDiscoverAsync(ExtensionRegistry registry, ExtensionRegistrySnapshot snapshot, string cwd,
        string reason, Func<string, string, ValueTask>? onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(snapshot);
        if (reason is not ("startup" or "reload")) throw new ArgumentException("resources_discover reason is startup or reload.", nameof(reason));
        if (!registry.HasEventHandlers(snapshot, ResourcesDiscover)) return PiDiscoveredResources.Empty;
        var skills = ImmutableArray.CreateBuilder<string>(); var prompts = ImmutableArray.CreateBuilder<string>(); var themes = ImmutableArray.CreateBuilder<string>();
        var initial = JsonData.Parse(JsonSerializer.Serialize(new { type = ResourcesDiscover, cwd, reason }));
        await registry.ReduceEventAsync(snapshot, ResourcesDiscover, initial, (_, result) =>
        {
            if (result.Value.ValueKind == JsonValueKind.Object)
            {
                Collect(result.Value, "skillPaths", skills); Collect(result.Value, "promptPaths", prompts); Collect(result.Value, "themePaths", themes);
            }
            return null;
        }, async (diagnostic, _) =>
        {
            if (onError is not null) await onError(diagnostic.OwnerId, diagnostic.Message ?? diagnostic.Failure.ToString()).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return new(skills.ToImmutable(), prompts.ToImmutable(), themes.ToImmutable());

        static void Collect(JsonElement result, string name, ImmutableArray<string>.Builder target)
        {
            if (result.TryGetProperty(name, out var paths) && paths.ValueKind == JsonValueKind.Array)
                foreach (var path in paths.EnumerateArray()) if (path.ValueKind == JsonValueKind.String) target.Add(path.GetString()!);
        }
    }
}

/// <summary>The extensions a Pi-style run has loaded before its session starts (IMPL-E supplies them): the registry and the snapshot the
/// CLI's own events dispatch over.</summary>
internal sealed record PiLoadedExtensions(ExtensionRegistry Registry, ExtensionRegistrySnapshot Snapshot)
{
    /// <summary>The Node extension host that loaded the extensions (IMPL-E), which the session binds after project trust.</summary>
    internal PiSharp.Cli.Extensions.Pi.PiExtensionHost? Host { get; init; }
}
