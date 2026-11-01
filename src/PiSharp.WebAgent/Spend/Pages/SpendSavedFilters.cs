// Decision 0011 (run W3, fix spend-ui): History › Spend's saved filters, the mockups' "Saved filters" menu of _SpendFilters (the user's
// named filters, Save current filters… and Manage saved filters). They are their own settings section ("spend-filters" in webagent.db), not
// part of the Spend Guard's "spend" section: they are a view of the ledger that the guard never reads, so saving one never raises a change
// of the guard's settings. Each is a name and the query string of its filters (q, from, to, provider, model, source, account, status),
// normalised through SpendHistoryFilter, so the page number, the page size and ?resolve= are never saved. Dates are saved as they are (a
// saved range does not move with the calendar). A new data folder starts with the three filters that need no date (Uncertain only, Settled
// at an estimate, Chats only), which the user can rename or remove. Nothing here is a secret: an account is its id.
using Microsoft.AspNetCore.WebUtilities;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Spend;

/// <summary>One saved filter of History › Spend.</summary>
/// <param name="Name">What the Saved filters menu shows: 1 to <see cref="SpendSavedFilters.MaxNameLength"/> characters, unique ignoring
/// case.</param>
/// <param name="Query">The filters as a query string without its leading <c>?</c>, such as <c>provider=anthropic&amp;status=uncertain</c>.</param>
public sealed record SpendSavedFilter(string Name, string Query)
{
    /// <summary>History › Spend with this filter.</summary>
    public string Href => "/history/spend?" + Query;
}

/// <summary>History › Spend's saved filters (the <c>spend-filters</c> section), in the order the menu shows them.</summary>
/// <param name="Filters">The saved filters.</param>
public sealed record SpendSavedFilters(IReadOnlyList<SpendSavedFilter>? Filters = null) : ISettingsSection<SpendSavedFilters>
{
    /// <summary>The most saved filters kept.</summary>
    public const int MaxFilters = 50;

    /// <summary>The longest name accepted.</summary>
    public const int MaxNameLength = 80;

    /// <inheritdoc/>
    public static string SectionName => "spend-filters";

    /// <inheritdoc/>
    public static SpendSavedFilters CreateDefault() => new(
    [
        new SpendSavedFilter("Uncertain only", "status=uncertain"),
        new SpendSavedFilter("Settled at an estimate", "status=estimate"),
        new SpendSavedFilter("Chats only", "source=chat"),
    ]);

    /// <summary>The saved filter called <paramref name="name"/> (ignoring case), or null.</summary>
    public SpendSavedFilter? Find(string name) =>
        Filters?.FirstOrDefault(filter => string.Equals(filter.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The filters of <paramref name="query"/> (with or without its leading <c>?</c>), on the first page at the default size and
    /// without a Resolve modal to open.</summary>
    public static SpendHistoryFilter Parse(string? query)
    {
        var values = QueryHelpers.ParseQuery(query);
        return SpendHistoryFilter.Parse(key => values.TryGetValue(key, out var value) ? value.ToString() : null) with { Page = 1, Size = 25, Resolve = null };
    }

    /// <summary>How <paramref name="query"/> is saved: its filters as a query string without the leading <c>?</c>, in a fixed order, values
    /// that are not allowed, paging, the page size and <c>resolve</c> left out.</summary>
    /// <exception cref="ArgumentException">No filter is set, so there is nothing to save.</exception>
    public static string NormalizeQuery(string? query)
    {
        var filter = Parse(query);
        if (!filter.Active) throw new ArgumentException("Choose at least one filter to save.");
        return filter.QueryString(paging: false).TrimStart('?');
    }

    /// <summary>A saved filter's name, trimmed, with runs of white space as one space.</summary>
    /// <exception cref="ArgumentException">The name is empty or longer than <see cref="MaxNameLength"/> characters.</exception>
    public static string NormalizeName(string? name)
    {
        var words = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = string.Join(' ', words);
        if (normalized.Length == 0) throw new ArgumentException("Give the filter a name.");
        if (normalized.Length > MaxNameLength) throw new ArgumentException($"A filter's name is at most {MaxNameLength} characters.");
        return normalized;
    }

    /// <inheritdoc/>
    public SpendSavedFilters Normalize()
    {
        // A later filter with the same name replaces an earlier one in its place, so a page can append a change.
        var filters = new List<SpendSavedFilter>();
        foreach (var filter in Filters ?? [])
        {
            if (filter is null) throw new ArgumentException("A saved filter is missing.");
            var name = NormalizeName(filter.Name);
            var query = NormalizeQuery(filter.Query);
            var at = filters.FindIndex(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) filters[at] = new SpendSavedFilter(name, query);
            else filters.Add(new SpendSavedFilter(name, query));
        }
        if (filters.Count > MaxFilters) throw new ArgumentException($"Keep at most {MaxFilters} saved filters; remove one first.");
        return new SpendSavedFilters(filters);
    }
}
