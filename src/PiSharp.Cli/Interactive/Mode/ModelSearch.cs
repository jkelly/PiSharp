// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): coding-agent/src/modes/interactive/model-search.ts.
using System.Text;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>Source ModelSearchItem.</summary>
internal sealed record ModelSearchItem(string Id, string Provider, string? Name = null);

/// <summary>The text the model pickers fuzzy-match against.</summary>
internal static class ModelSearch
{
    /// <summary>Source getModelSearchText.</summary>
    public static string GetModelSearchText(ModelSearchItem item)
    {
        var (id, provider) = (item.Id, item.Provider);
        var name = string.IsNullOrEmpty(item.Name) ? "" : $" {item.Name}";
        return $"{id} {provider} {provider}/{id} {provider} {id}{name}";
    }

    /// <summary>Source splitModelLabel: the lower-cased words of runs of letters and numbers (<c>/[^\p{L}\p{N}]+/u</c>, by code point).</summary>
    private static List<string> SplitModelLabel(string value)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in value.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) { word.Append(rune.ToString()); continue; }
            if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>Source getDistinctModelName (Pi 1.5.0): the model name when it has words the ID does not, e.g. a renamed override like
    /// "MiniMax M3 [rasmus@openrouter, parasail]" for "minimax/minimax-m3". Names that only restate the ID with different casing,
    /// separators, or a dropped namespace/version suffix ("Claude Sonnet 4.5 (EU)" for "eu.anthropic.claude-sonnet-4-5-20250929-v1:0")
    /// return null.</summary>
    public static string? GetDistinctModelName(string id, string? name)
    {
        name = name is null ? null : PiSharp.Tui.Pi.TextUtils.JsTrim(name);
        if (string.IsNullOrEmpty(name)) return null;
        var idWords = SplitModelLabel(id);
        var idTokens = new HashSet<string>(idWords, StringComparer.Ordinal);
        var compactId = string.Concat(idWords);
        // Single-character words must match a whole ID token: "4" is not covered by "mistral-small-2603".
        bool IsCovered(string word) => idTokens.Contains(word) || (word.Length > 1 && compactId.Contains(word, StringComparison.Ordinal));
        return SplitModelLabel(name).All(IsCovered) ? null : name;
    }

    /// <summary>Source getModelSelectorSearchText: the /model selector search should rank exact provider-prefixed queries before
    /// proxy-provider IDs like openrouter/openai/gpt-5, so keep the bare model ID out of the leading position.</summary>
    public static string GetModelSelectorSearchText(ModelSearchItem item)
    {
        var (id, provider) = (item.Id, item.Provider);
        var name = string.IsNullOrEmpty(item.Name) ? "" : $" {item.Name}";
        return $"{provider} {provider}/{id} {provider} {id}{name}";
    }
}
