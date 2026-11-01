// Decision 0011 (run W3, SU): the wizard's wording that depends on data: the connected count, a model's thinking range (setup.js words it
// the same way when the model changes), the "Adds N Codex models" sentence and the API key modal's subtitle (as _CredentialButton words it).
using PiSharp.AI;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Settings;

namespace PiSharp.WebAgent.Setup;

/// <summary>Text for the wizard's views.</summary>
public static class SetupText
{
    /// <summary>"0 connected", "1 provider connected", "3 providers connected".</summary>
    public static string ConnectedCount(int count) => count == 0 ? "0 connected" : SettingsText.Plural(count, "provider") + " connected";

    /// <summary>What thinking levels <paramref name="model"/> takes, for the note under the levels: <c>GPT-X supports minimal to max; off
    /// isn't available.</c>, or that it does not think.</summary>
    public static string ThinkingNote(CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var levels = ThinkingLevels.Ordered.Where(level => model.ThinkingLevels.Contains(level, StringComparer.Ordinal)).ToList();
        if (levels.Count == 0 || levels is ["off"]) return $"{model.Name} doesn't reason, so only off is available.";
        var range = levels.Count == 1 ? $"{model.Name} supports {levels[0]} only" : $"{model.Name} supports {levels[0]} to {levels[^1]}";
        var gaps = ThinkingLevels.Ordered.SkipWhile(level => level != levels[0]).TakeWhile(level => level != levels[^1]).Where(level => !levels.Contains(level)).ToList();
        if (!levels.Contains("off")) gaps.Insert(0, "off");
        var missing = gaps.Count == 0 ? "" : "; " + string.Join(", ", gaps) + (gaps.Count == 1 ? " isn't" : " aren't") + " available";
        return range + missing + ". The level is clamped when you switch to a model with fewer levels.";
    }

    /// <summary>"Adds 9 Codex models, including GPT-6.1 Sol. " from the catalog, or nothing when the catalog lists no Codex model (offline).</summary>
    public static string CodexModels(ProviderEntry? codex) => codex is { Models: [var first, ..] } entry
        ? $"Adds {SettingsText.Plural(entry.Models.Count, "Codex model")}, including {first.Name}. " : "";

    /// <summary>The API key modal's subtitle for <paramref name="provider"/>: its id, APIs and base URL.</summary>
    public static string KeySubtitle(ProviderEntry provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return string.Join(" · ", new[] { provider.Id, provider.Apis.Count > 0 ? string.Join(", ", provider.Apis) : null, provider.BaseUrl }
            .Where(part => !string.IsNullOrEmpty(part)));
    }
}
