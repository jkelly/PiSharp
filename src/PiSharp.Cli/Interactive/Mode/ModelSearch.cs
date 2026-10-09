// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/model-search.ts.
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

    /// <summary>Source getModelSelectorSearchText: the /model selector search should rank exact provider-prefixed queries before
    /// proxy-provider IDs like openrouter/openai/gpt-5, so keep the bare model ID out of the leading position.</summary>
    public static string GetModelSelectorSearchText(ModelSearchItem item)
    {
        var (id, provider) = (item.Id, item.Provider);
        var name = string.IsNullOrEmpty(item.Name) ? "" : $" {item.Name}";
        return $"{provider} {provider}/{id} {provider} {id}{name}";
    }
}
