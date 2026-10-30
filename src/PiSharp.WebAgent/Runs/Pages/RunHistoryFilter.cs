// Decision 0011 (run W4, R3): History › Runs' filters (the mockups' _RunFilters): search (prompt text, output, error, model, run id, op or
// session id), a date range (the app's local days, as the Time column shows them), the source, Provider → Model and the status (a stop
// reason: stop, length, toolUse; or an outcome: error, aborted, interrupted, running), with the page and its size. They live in the query
// string, so every view is a link; values that are not allowed are dropped, never guessed. A model needs its provider. Saved filters are
// their own settings section ("history-filters"), each a name and the normalised query of its filters (paging is never saved).
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Runs;

/// <summary>One History › Runs filter, as the query string carries it.</summary>
public sealed record RunHistoryFilter
{
    /// <summary>The page sizes the page offers.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [25, 50, 100];

    /// <summary>The status filter's values, in the order the select lists them: stop reasons, then outcomes.</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["stop", "length", "toolUse", "error", "aborted", "interrupted", "running"];

    /// <summary>The stop reasons among <see cref="Statuses"/> (matched against a run's stop reason; the rest match its status).</summary>
    public static IReadOnlyList<string> StopReasons { get; } = ["stop", "length", "toolUse"];

    /// <summary>The sources the Source select lists (the mockups' five).</summary>
    public static IReadOnlyList<SpendSource> Sources { get; } =
        [SpendSource.Chat, SpendSource.Playground, SpendSource.Compare, SpendSource.AgentCompare, SpendSource.Bench];

    /// <summary>The longest search text kept.</summary>
    public const int MaxSearch = 200;

    /// <summary>Text to find, or null.</summary>
    public string? Search { get; init; }

    /// <summary>The first day (local), or null.</summary>
    public DateOnly? From { get; init; }

    /// <summary>The last day (local, inclusive), or null.</summary>
    public DateOnly? To { get; init; }

    /// <summary>The source's wire name, or null.</summary>
    public string? Source { get; init; }

    /// <summary>The provider id, or null.</summary>
    public string? Provider { get; init; }

    /// <summary>The model id (only with a provider), or null.</summary>
    public string? Model { get; init; }

    /// <summary>One of <see cref="Statuses"/>, or null.</summary>
    public string? Status { get; init; }

    /// <summary>The page, from 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page (<see cref="Sizes"/>).</summary>
    public int Size { get; init; } = 25;

    /// <summary>Whether any filter is set (paging aside).</summary>
    public bool Active => Search is not null || From is not null || To is not null || Source is not null || Provider is not null || Status is not null;

    /// <summary>The filter <paramref name="value"/> reads (query string names q, from, to, source, provider, model, status, page, size).</summary>
    public static RunHistoryFilter Parse(Func<string, string?> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string? Text(string name) => value(name) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        DateOnly? Day(string name) => Text(name) is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
        var search = Text("q");
        if (search is { Length: > MaxSearch }) search = search[..MaxSearch];
        var source = Text("source") is { } wire && SpendSources.TryParse(wire, out var parsed) ? parsed.Wire() : null;
        var provider = Text("provider");
        if (provider is { Length: > 200 }) provider = null;
        var model = provider is null ? null : Text("model");
        if (model is { Length: > 300 }) model = null;
        var status = Text("status") is { } given ? Statuses.FirstOrDefault(item => string.Equals(item, given, StringComparison.OrdinalIgnoreCase)) : null;
        var page = Text("page") is { } pageText && int.TryParse(pageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1;
        var size = Text("size") is { } sizeText && int.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) && Sizes.Contains(rows) ? rows : 25;
        return new RunHistoryFilter
        {
            Search = search, From = Day("from"), To = Day("to"), Source = source, Provider = provider, Model = model, Status = status, Page = page, Size = size,
        };
    }

    /// <summary>The filter of a request's query string.</summary>
    public static RunHistoryFilter Parse(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Parse(name => query.TryGetValue(name, out var value) ? value.ToString() : null);
    }

    /// <summary>The filter of <paramref name="query"/> (with or without its leading <c>?</c>), on the first page at the default size.</summary>
    public static RunHistoryFilter ParseQuery(string? query)
    {
        var values = QueryHelpers.ParseQuery(query);
        return Parse(name => values.TryGetValue(name, out var value) ? value.ToString() : null) with { Page = 1, Size = 25 };
    }

    /// <summary>The query string (with its <c>?</c>, or empty): the filters in a fixed order, then the page and size when
    /// <paramref name="paging"/> and they are not the defaults.</summary>
    public string QueryString(bool paging = true)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (value is not null) parts.Add(name + "=" + Uri.EscapeDataString(value));
        }
        Add("q", Search);
        Add("from", From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("to", To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("source", Source);
        Add("provider", Provider);
        Add("model", Model);
        Add("status", Status);
        if (paging && Page > 1) Add("page", Page.ToString(CultureInfo.InvariantCulture));
        if (paging && Size != 25) Add("size", Size.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    /// <summary>History › Runs with this filter on <paramref name="page"/> (this page when null).</summary>
    public string Href(int? page = null) => "/history" + (this with { Page = page ?? Page }).QueryString();

    /// <summary>The active filter chips, each with the address that removes it.</summary>
    public IReadOnlyList<RunFilterChip> Chips
    {
        get
        {
            var chips = new List<RunFilterChip>();
            var first = this with { Page = 1 };
            if (Search is not null) chips.Add(new RunFilterChip("search", "search: " + Search, (first with { Search = null }).Href()));
            if (From is not null || To is not null)
                chips.Add(new RunFilterChip("date", "date: " + (From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "…") + " → " +
                    (To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "…"), (first with { From = null, To = null }).Href()));
            if (Source is not null)
                chips.Add(new RunFilterChip("source", "source: " + (SpendSources.TryParse(Source, out var source) ? source.Label() : Source), (first with { Source = null }).Href()));
            if (Provider is not null) chips.Add(new RunFilterChip("provider", "provider: " + Provider, (first with { Provider = null, Model = null }).Href()));
            if (Model is not null) chips.Add(new RunFilterChip("model", "model: " + Model, (first with { Model = null }).Href()));
            if (Status is not null) chips.Add(new RunFilterChip("status", "status: " + Status, (first with { Status = null }).Href()));
            return chips;
        }
    }

    /// <summary>The first instant of <see cref="From"/> in the app's time zone, or null.</summary>
    public DateTimeOffset? FromTime => From is { } day ? LocalStart(day) : null;

    /// <summary>The first instant after <see cref="To"/> in the app's time zone, or null.</summary>
    public DateTimeOffset? ToTime => To is { } day ? LocalStart(day.AddDays(1)) : null;

    private static DateTimeOffset LocalStart(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}

/// <summary>One active filter chip.</summary>
/// <param name="Name">The filter (search, date, source, provider, model, status).</param>
/// <param name="Text">What the chip says.</param>
/// <param name="Href">History › Runs without this filter.</param>
public sealed record RunFilterChip(string Name, string Text, string Href);

/// <summary>One saved filter of History › Runs.</summary>
/// <param name="Name">What the Saved filters menu shows (1 to <see cref="RunSavedFilters.MaxNameLength"/> characters, unique ignoring case).</param>
/// <param name="Query">The filters as a query string without its <c>?</c>.</param>
public sealed record RunSavedFilter(string Name, string Query)
{
    /// <summary>History › Runs with this filter.</summary>
    public string Href => "/history?" + Query;
}

/// <summary>History › Runs' saved filters (the <c>history-filters</c> section), in menu order. A new data folder starts with three that need
/// no date, which the user can rename or remove.</summary>
/// <param name="Filters">The saved filters.</param>
public sealed record RunSavedFilters(IReadOnlyList<RunSavedFilter>? Filters = null) : ISettingsSection<RunSavedFilters>
{
    /// <summary>The most saved filters kept.</summary>
    public const int MaxFilters = 50;

    /// <summary>The longest name accepted.</summary>
    public const int MaxNameLength = 80;

    /// <inheritdoc/>
    public static string SectionName => "history-filters";

    /// <inheritdoc/>
    public static RunSavedFilters CreateDefault() => new(
    [
        new RunSavedFilter("Errors only", "status=error"),
        new RunSavedFilter("Tool calls only", "status=toolUse"),
        new RunSavedFilter("Playground only", "source=playground"),
    ]);

    /// <summary>How <paramref name="query"/> is saved: its filters only, in a fixed order.</summary>
    /// <exception cref="ArgumentException">No filter is set.</exception>
    public static string NormalizeQuery(string? query)
    {
        var filter = RunHistoryFilter.ParseQuery(query);
        if (!filter.Active) throw new ArgumentException("Choose at least one filter to save.");
        return filter.QueryString(paging: false).TrimStart('?');
    }

    /// <summary>A name, trimmed, with runs of white space as one space.</summary>
    /// <exception cref="ArgumentException">Empty or too long.</exception>
    public static string NormalizeName(string? name)
    {
        var normalized = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length == 0) throw new ArgumentException("Give the filter a name.");
        if (normalized.Length > MaxNameLength) throw new ArgumentException($"A filter's name is at most {MaxNameLength} characters.");
        return normalized;
    }

    /// <inheritdoc/>
    public RunSavedFilters Normalize()
    {
        var filters = new List<RunSavedFilter>();
        foreach (var filter in Filters ?? [])
        {
            if (filter is null) throw new ArgumentException("A saved filter is missing.");
            var name = NormalizeName(filter.Name);
            var query = NormalizeQuery(filter.Query);
            var at = filters.FindIndex(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) filters[at] = new RunSavedFilter(name, query);
            else filters.Add(new RunSavedFilter(name, query));
        }
        if (filters.Count > MaxFilters) throw new ArgumentException($"Keep at most {MaxFilters} saved filters; remove one first.");
        return new RunSavedFilters(filters);
    }

    /// <summary>A short description of a saved query, for the menu's tooltip.</summary>
    public static string Describe(string query)
    {
        var chips = RunHistoryFilter.ParseQuery(query).Chips;
        var text = new StringBuilder();
        foreach (var chip in chips) text.Append(text.Length == 0 ? "" : " · ").Append(chip.Text);
        return text.ToString();
    }
}
