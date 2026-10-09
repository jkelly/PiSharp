// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/main.ts (buildSessionOptions: --model, --provider,
// --models/enabledModels scoping and the saved default inside the scope), packages/coding-agent/src/core/model-resolver.ts
// (resolveCliModel, resolveModelScope, findInitialModel) and packages/coding-agent/src/core/settings-manager.ts (enabledModels).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

namespace PiSharp.Cli.Settings;

/// <summary>Preferences select only already admitted native catalog/binding capabilities.</summary>
internal sealed record SettingsModelSelection(string? Provider, string? Model, string? MaximumTokens)
{
    /// <summary>The <c>--models</c> patterns (comma-separated, already split); null reads settings <c>enabledModels</c>.</summary>
    internal ImmutableArray<string>? ModelPatterns { get; init; }
    /// <summary>The explicit <c>--thinking</c> level (it keeps a <c>:level</c> suffix in a fallback id).</summary>
    internal string? CliThinking { get; init; }
    /// <summary>Pi-style entries: without <c>MaximumTokens</c>, requests ask for the model's own <c>maxTokens</c> (simple-options.ts
    /// buildBaseOptions) instead of the explicit verbs' bounded default.</summary>
    internal bool UseModelMaximumTokens { get; init; }

    /// <summary>Exact pinned identities only (no registry, no environment): the CLI identity, else the settings default.</summary>
    internal LiveSessionSelection Resolve(StartupSettingsSnapshot? settings) => LiveSessionSelection.Parse(
        Provider ?? Read(settings, "defaultProvider"), Model ?? Read(settings, "defaultModel"), MaximumTokens);

    /// <summary>
    /// The live session's model, as upstream selects it: <c>--model</c> (with <c>--provider</c>) through resolveCliModel over every chat
    /// model; else the scoped models of <c>--models</c>/<c>enabledModels</c> for a new session (the saved default when it is in scope);
    /// else the settings default; else the first available provider default. Warnings are written as <c>model_diagnostic</c> lines.
    /// </summary>
    internal async Task<LiveSessionSelection> ResolveAsync(StartupSettingsSnapshot? settings, LiveSessionRuntime runtime, TextWriter? diagnostics,
        bool continuing, CancellationToken cancellationToken)
    {
        var registry = await runtime.CreateModelRegistryAsync(cancellationToken).ConfigureAwait(false);
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (registry.GetError() is { } loadError) warnings.Add("errors loading models.json:\n" + loadError);
        var patterns = ModelPatterns ?? ReadPatterns(settings);
        LiveSessionSelection selection;
        try { selection = Select(registry, settings, patterns, continuing, warnings); }
        finally
        {
            if (diagnostics is not null)
            {
                foreach (var warning in warnings)
                    await diagnostics.WriteLineAsync(JsonSerializer.Serialize(new { type = "model_diagnostic", level = "warning", message = warning })
                        .AsMemory(), cancellationToken).ConfigureAwait(false);
                await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return selection;
    }

    private LiveSessionSelection Select(ModelRegistry registry, StartupSettingsSnapshot? settings, ImmutableArray<string> patterns, bool continuing,
        ImmutableArray<string>.Builder warnings)
    {
        // main.ts: the scope (--models, else enabledModels) is resolved and kept for cycling even when --model picks the model.
        var scoped = ImmutableArray<ScopedModel>.Empty;
        if (!patterns.IsDefaultOrEmpty)
        {
            var (models, scopeDiagnostics) = ModelResolver.ResolveModelScope(patterns, registry.GetAvailable());
            scoped = models;
            foreach (var diagnostic in scopeDiagnostics) warnings.Add(diagnostic.Message);
        }
        if (Model is not null)
        {
            var resolved = ModelResolver.ResolveCliModel(Provider, Model, CliThinking, registry.GetAll(), registry.HasConfiguredAuth);
            if (resolved.Warning is not null) warnings.Add(resolved.Warning);
            if (resolved.Error is not null || resolved.Model is null)
                throw new LiveSessionException("UnknownLiveModel", resolved.Error ?? "Select a chat model for the supported live API.");
            return Annotate(Entry(resolved.Model, registry), CliThinking is null ? resolved.ThinkingLevel : null, scoped, warnings);
        }
        var defaultProvider = Provider ?? Read(settings, "defaultProvider"); var defaultModel = Read(settings, "defaultModel");
        if (scoped.Length > 0 && !continuing)
        {
            var saved = defaultProvider is not null && defaultModel is not null ? registry.Find(defaultProvider, defaultModel) : null;
            var pick = saved is null ? scoped[0] : scoped.FirstOrDefault(entry => entry.Model.SameIdentity(saved)) ?? scoped[0];
            return Annotate(Entry(pick.Model, registry), CliThinking is null ? pick.ThinkingLevel : null, scoped, warnings);
        }
        // model-resolver.ts findInitialModel: the saved default when it exists and its provider has configured auth, else the first
        // available model (a known provider's default first).
        var initial = ModelResolver.FindInitialModel(null, null, [], continuing, defaultProvider, defaultModel, null, null, registry);
        if (initial.Model is null) throw new LiveSessionException("NoLiveModel", ModelListing.NoModelsAvailableMessage());
        return Annotate(Entry(initial.Model, registry), null, scoped, warnings);
    }

    private LiveSessionSelection Entry(RegistryModel model, ModelRegistry registry) =>
        UseModelMaximumTokens && MaximumTokens is null ? LiveSessionSelection.FromEntry(model, registry, null, useModelMaximum: true)
            : LiveSessionSelection.FromEntry(model, registry, MaximumTokens);

    private static LiveSessionSelection Annotate(LiveSessionSelection selection, string? thinking, ImmutableArray<ScopedModel> scoped,
        ImmutableArray<string>.Builder warnings)
    {
        selection.PatternThinkingLevel = thinking; selection.ScopedModels = scoped; selection.Warnings = warnings.ToImmutable();
        return selection;
    }

    /// <summary>settings-manager getEnabledModels: an array of pattern strings.</summary>
    private static ImmutableArray<string> ReadPatterns(StartupSettingsSnapshot? settings)
    {
        if (settings is null || !settings.Values.Value.TryGetProperty("enabledModels", out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        return [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)];
    }

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
