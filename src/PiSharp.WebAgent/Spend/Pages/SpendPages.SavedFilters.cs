// Decision 0011 (run W3, fix spend-ui): History › Spend's filter chips and saved filters as the page shows them. The chips are the active
// filters, each with the address without it; a saved filter is described by the same chips ("provider: anthropic · status: uncertain"),
// carries the icon of what it filters and is marked when the page shows exactly its filters. The Save current filters modal suggests a name
// from the values of the active filters. Accounts are named as the Account filter names them (provider, kind and masked hint).
using System.Globalization;

namespace PiSharp.WebAgent.Spend;

public sealed partial class SpendPages
{
    /// <summary>The active filters of <paramref name="filter"/> as chips (search, date, provider, model, source, account, status), each with
    /// History › Spend without it. <paramref name="options"/> names the accounts; without it they are named from the account store.</summary>
    public IReadOnlyList<SpendFilterChip> FilterChips(SpendHistoryFilter filter, SpendFilterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var chips = new List<SpendFilterChip>();
        var reset = filter with { Page = 1 };
        if (filter.Search is { } search) chips.Add(new("search", "search: " + search, (reset with { Search = null }).Href));
        if (filter.From is not null || filter.To is not null)
            chips.Add(new("date", "date: " + (Day(filter.From) ?? "…") + " → " + (Day(filter.To) ?? "…"), (reset with { From = null, To = null }).Href));
        if (filter.Provider is { } provider) chips.Add(new("provider", "provider: " + provider, (reset with { Provider = null, Model = null }).Href));
        if (filter.Model is { } model) chips.Add(new("model", "model: " + model, (reset with { Model = null }).Href));
        if (filter.Source is { } source) chips.Add(new("source", "source: " + source.Label(), (reset with { Source = null }).Href));
        if (filter.Account is { } account) chips.Add(new("account", "account: " + AccountOption(account, options), (reset with { Account = null }).Href));
        if (filter.Status is { } status) chips.Add(new("status", "status: " + SpendHistoryFilter.StatusWire(status), (reset with { Status = null }).Href));
        return chips;
    }

    /// <summary>The saved filters as the Saved filters menu and the Manage modal show them; the one whose filters are exactly
    /// <paramref name="current"/>'s is marked.</summary>
    public IReadOnlyList<SpendSavedFilterView> SavedFilters(SpendHistoryFilter? current = null, SpendFilterOptions? options = null)
    {
        var here = current is { Active: true } ? current.QueryString(paging: false).TrimStart('?') : null;
        return [.. (settings.Get<SpendSavedFilters>().Filters ?? []).Select(saved =>
        {
            var filter = SpendSavedFilters.Parse(saved.Query);
            return new SpendSavedFilterView(saved.Name, saved.Query, saved.Href, SavedIcon(filter),
                string.Join(" · ", FilterChips(filter, options).Select(chip => chip.Text)), saved.Query == here);
        })];
    }

    /// <summary>The name the Save current filters modal suggests for <paramref name="filter"/>: the values of its filters, such as
    /// <c>anthropic · Chat · uncertain</c>; empty when nothing filters.</summary>
    public string SuggestedFilterName(SpendHistoryFilter filter, SpendFilterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var parts = new List<string>();
        if (filter.Provider is { } provider) parts.Add(filter.Model is { } model ? provider + "/" + model : provider);
        if (filter.Source is { } source) parts.Add(source.Label());
        if (filter.Account is { } account) parts.Add(AccountOption(account, options));
        if (filter.Status is { } status) parts.Add(SpendHistoryFilter.StatusWire(status));
        if (filter.Search is { } search) parts.Add("“" + search + "”");
        if (filter.From is not null || filter.To is not null)
            parts.Add(filter.From == filter.To ? Day(filter.From)! : (Day(filter.From) ?? "…") + " → " + (Day(filter.To) ?? "…"));
        var name = string.Join(" · ", parts);
        return name.Length <= SpendSavedFilters.MaxNameLength ? name : name[..(SpendSavedFilters.MaxNameLength - 1)].TrimEnd() + "…";
    }

    /// <summary>The icon of a saved filter: uncertain or estimate rows by their status, one source by its own icon, anything else a funnel.</summary>
    internal static string SavedIcon(SpendHistoryFilter filter) => filter switch
    {
        { Status: LedgerStatus.Uncertain } => "bi-question-circle",
        { Status: LedgerStatus.Estimate } => "bi-calculator",
        { Source: { } source } => SourceIcon(source),
        _ => "bi-funnel",
    };

    /// <summary>How the Account filter names <paramref name="account"/>: its option's text, else the account store's name for it.</summary>
    private string AccountOption(string account, SpendFilterOptions? options)
    {
        if (options?.Accounts.SelectMany(group => group.Options).FirstOrDefault(option => option.Value == account) is { } option) return option.Text;
        return accounts?.Get(account) is { } info ? info.Provider + " · " + AccountText(info) : AccountText(account);
    }

    private static string? Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One active filter of History › Spend.</summary>
/// <param name="Name">What it filters (<c>search</c>, <c>date</c>, <c>provider</c>, <c>model</c>, <c>source</c>, <c>account</c>,
/// <c>status</c>), for the remove link's label.</param>
/// <param name="Text">The chip's text, such as <c>provider: anthropic</c>.</param>
/// <param name="Href">History › Spend without this filter.</param>
public sealed record SpendFilterChip(string Name, string Text, string Href);

/// <summary>A saved filter as the Saved filters menu and the Manage modal show it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Query">Its filters as a query string without the leading <c>?</c>.</param>
/// <param name="Href">History › Spend with its filters.</param>
/// <param name="Icon">Its Bootstrap icon class.</param>
/// <param name="Description">Its filters as chips' text, such as <c>provider: anthropic · status: uncertain</c>.</param>
/// <param name="Current">Whether the page shows exactly its filters.</param>
public sealed record SpendSavedFilterView(string Name, string Query, string Href, string Icon, string Description, bool Current);
