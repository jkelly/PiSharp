// Decision 0011 (run W4, R8): what the Presets views render: the page (GET /presets: _PresetFilters, _PresetGrid of _PresetCards, or
// _PresetsEmpty), the shared _SavePresetModal (new and edit panes) and the shared _LoadPresetModal (search, tags, the list and each preset's
// preview). Built by PresetService from the stored presets and the catalog; nothing here is sample data.
using System.Globalization;
using PiSharp.WebAgent.Shell;

namespace PiSharp.WebAgent.Presets;

/// <summary>Whether a preset's models can run here.</summary>
/// <param name="Ok">Whether every model is in the catalog and its provider is set up.</param>
/// <param name="Chip">The card's warning chip ("provider not configured", "model not found"), or null.</param>
/// <param name="Problem">Why it cannot load, as a sentence, or null.</param>
/// <param name="Title">The disabled Open button's title ("Connect xAI first"), or null.</param>
/// <param name="ConnectUrl">Where to set the provider up, or null.</param>
/// <param name="ConnectLabel">The link's text ("Connect xAI"), or null.</param>
public sealed record PresetAvailability(bool Ok, string? Chip = null, string? Problem = null, string? Title = null, string? ConnectUrl = null, string? ConnectLabel = null)
{
    /// <summary>Everything is available.</summary>
    public static PresetAvailability Available { get; } = new(true);
}

/// <summary>A model a preset names, as the views show it.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Name">The model's name.</param>
/// <param name="ProviderName">The provider's name.</param>
/// <param name="LogoUrl">The provider's mark, or null.</param>
/// <param name="Api">The model's API, or null when it is not in the catalog.</param>
/// <param name="Ok">Whether it can run here.</param>
public sealed record PresetModelView(string Provider, string Model, string Name, string ProviderName, string? LogoUrl, string? Api, bool Ok)
{
    /// <summary><c>provider/model</c>.</summary>
    public string Id => Provider + "/" + Model;
}

/// <summary>A capability chip of a card (<c>cap</c>; <c>cap-reasoning</c> for thinking).</summary>
public sealed record PresetCap(string Text, bool Reasoning = false);

/// <summary>An Open button of a card.</summary>
/// <param name="Page">The page it opens.</param>
/// <param name="Url">Its address.</param>
/// <param name="Title">Its title (what it loads), or null.</param>
public sealed record PresetAction(string Page, string Url, string? Title = null)
{
    /// <summary>"Open in Playground".</summary>
    public string Label => "Open in " + PresetPages.Title(Page);

    /// <summary>The page's icon.</summary>
    public string Icon => PresetPages.Icon(Page);
}

/// <summary>One preset card.</summary>
public sealed record PresetCardView
{
    /// <summary>The preset.</summary>
    public required Preset Preset { get; init; }

    /// <summary>Whether it can load here.</summary>
    public required PresetAvailability Availability { get; init; }

    /// <summary>Its models (one; Compare: the columns').</summary>
    public required IReadOnlyList<PresetModelView> Models { get; init; }

    /// <summary>Its capability chips.</summary>
    public required IReadOnlyList<PresetCap> Caps { get; init; }

    /// <summary>"used 23× · last used 2h ago", or "never used".</summary>
    public required string UsedText { get; init; }

    /// <summary>The pages it belongs to ("Playground", "Compare · Playground").</summary>
    public required string UsedInText { get; init; }

    /// <summary>The primary Open button.</summary>
    public required PresetAction Primary { get; init; }

    /// <summary>The second Open button.</summary>
    public required PresetAction Secondary { get; init; }

    /// <summary>The third page, in the actions menu.</summary>
    public required PresetAction Other { get; init; }

    /// <summary>The first model, or null when the model was left out.</summary>
    public PresetModelView? Model => Models.Count > 0 ? Models[0] : null;
}

/// <summary>The Presets page's filters (query string <c>q</c>, <c>in</c>, <c>sort</c>, <c>tag</c>).</summary>
/// <param name="Search">The search text, or null.</param>
/// <param name="UsedIn">The page filter (<see cref="PresetPages"/>), or null for anywhere.</param>
/// <param name="Sort">recent, uses, name or created.</param>
/// <param name="Tags">The tags every preset shown has.</param>
public sealed record PresetQuery(string? Search, string? UsedIn, string Sort, IReadOnlyList<string> Tags)
{
    /// <summary>The sorts, with their labels.</summary>
    public static IReadOnlyList<(string Value, string Label)> Sorts { get; } =
        [("recent", "Recently used"), ("uses", "Most used"), ("name", "Name A–Z"), ("created", "Recently created")];

    /// <summary>No filter, most recently used first.</summary>
    public static PresetQuery Default { get; } = new(null, null, "recent", []);

    /// <summary>The query from the page's parameters (<c>type</c> is another name for <c>in</c>).</summary>
    public static PresetQuery From(string? search, string? usedIn, string? sort, IEnumerable<string>? tags)
    {
        var page = string.IsNullOrWhiteSpace(usedIn) ? null : usedIn.Trim().ToLowerInvariant();
        return new PresetQuery(
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            PresetPages.Known(page) ? page : null,
            Sorts.Any(item => item.Value == sort) ? sort! : "recent",
            [.. (tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Whether anything narrows the list.</summary>
    public bool Active => Search is not null || UsedIn is not null || Tags.Count > 0;

    /// <summary>The query string (without '?').</summary>
    public string QueryString()
    {
        var parts = new List<string>();
        if (Search is not null) parts.Add("q=" + Uri.EscapeDataString(Search));
        if (UsedIn is not null) parts.Add("in=" + UsedIn);
        if (Sort != "recent") parts.Add("sort=" + Sort);
        parts.AddRange(Tags.Select(tag => "tag=" + Uri.EscapeDataString(tag)));
        return string.Join("&", parts);
    }
}

/// <summary>The Presets page (GET /presets).</summary>
public sealed record PresetsPage
{
    /// <summary>The filters.</summary>
    public required PresetQuery Query { get; init; }

    /// <summary>The cards shown.</summary>
    public required IReadOnlyList<PresetCardView> Cards { get; init; }

    /// <summary>How many presets there are.</summary>
    public required int Total { get; init; }

    /// <summary>How many are Compare presets.</summary>
    public int CompareCount { get; init; }

    /// <summary>How many are Tool Bench presets.</summary>
    public int BenchCount { get; init; }

    /// <summary>Every tag with how many presets have it, most used first.</summary>
    public IReadOnlyList<(string Tag, int Count)> Tags { get; init; } = [];

    /// <summary>The New preset modal.</summary>
    public required SavePresetView NewPreset { get; init; }

    /// <summary>Whether there is no preset at all.</summary>
    public bool Empty => Total == 0;

    /// <summary>"9 presets · 1 compare · 1 bench", or "2 of 9 presets" when filtered.</summary>
    public string CountText
    {
        get
        {
            var noun = Total == 1 ? "preset" : "presets";
            if (Query.Active) return $"{Cards.Count} of {Total} {noun}";
            var parts = new List<string> { $"{Total} {noun}" };
            if (CompareCount > 0) parts.Add($"{CompareCount} compare");
            if (BenchCount > 0) parts.Add($"{BenchCount} bench");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>One part's Include checkbox in the save modal.</summary>
/// <param name="Key">The part (<see cref="PresetParts"/>).</param>
/// <param name="Label">Its label.</param>
/// <param name="Detail">What it holds ("thinking, maxTokens"; "2 tools (1 on) · 3 mocks"), or why it is off.</param>
/// <param name="Available">Whether there is something to include.</param>
/// <param name="Checked">Whether it is ticked.</param>
public sealed record PresetPartView(string Key, string Label, string Detail, bool Available, bool Checked);

/// <summary>The shared save modal (<c>_SavePresetModal</c>): the new pane (saving what a page has, or the Presets page's New preset) or the
/// edit pane (a stored preset).</summary>
public sealed record SavePresetView
{
    /// <summary>The new pane.</summary>
    public const string NewMode = "new";

    /// <summary>The edit pane.</summary>
    public const string EditMode = "edit";

    /// <summary>The host shell a page renders (its content comes from POST /api/presets/preview when it opens).</summary>
    public const string HostMode = "host";

    /// <summary><see cref="NewMode"/>, <see cref="EditMode"/> or <see cref="HostMode"/>.</summary>
    public required string Mode { get; init; }

    /// <summary>The page saving (<see cref="PresetPages"/>, or <c>presets</c>).</summary>
    public required string Page { get; init; }

    /// <summary>What is being saved (all parts), or the stored preset being edited; null in the host shell or when the capture failed.</summary>
    public Preset? Draft { get; init; }

    /// <summary>Why the page's request cannot be saved, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The preset the page loaded, offered to update, or null.</summary>
    public Preset? Origin { get; init; }

    /// <summary>The tags in use, most used first.</summary>
    public IReadOnlyList<string> TagsInUse { get; init; } = [];

    /// <summary>The captured settings (read-only key/values).</summary>
    public IReadOnlyList<(string Key, string Value)> Captured { get; init; } = [];

    /// <summary>The Include checkboxes.</summary>
    public IReadOnlyList<PresetPartView> Parts { get; init; } = [];

    /// <summary>The preset's JSON (edit pane), or null.</summary>
    public string? Json { get; init; }

    /// <summary>The Provider → Model dropdowns of the Presets page's New preset, or null.</summary>
    public ProviderModelSelect? Picker { get; init; }

    /// <summary>The model's name and API, for the subtitle and the captured settings, or null.</summary>
    public PresetModelView? Model { get; init; }

    /// <summary>The host shell for <paramref name="page"/>.</summary>
    public static SavePresetView Host(string page) => new() { Mode = HostMode, Page = page };

    /// <summary>Whether this is the Presets page's New preset (a model and a system prompt chosen here).</summary>
    public bool Blank => Mode == NewMode && Page == PresetCapture.PresetsPage;

    /// <summary>The modal's title.</summary>
    public string Title => Mode == EditMode ? "Edit preset" : Blank ? "New preset" : "Save preset";

    /// <summary>The subtitle.</summary>
    public string Subtitle => Mode switch
    {
        EditMode when Draft is { } preset => $"Created {preset.Created.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture)} · updated {Settings.SettingsText.Ago(preset.Updated)} · used {preset.Uses}×",
        HostMode => "Reading the page…",
        _ when Blank => "A model and a system prompt to start from; save more from the Playground, Compare or Tool Bench.",
        _ => Page switch
        {
            PresetPages.Bench => "Tool Bench · reopen it later from Presets",
            PresetPages.Compare => "Compare · reuse this setup from Compare, the Playground or the Presets page.",
            _ => "Reuse this setup from Playground, Compare or the Presets page.",
        },
    };
}

/// <summary>One preset in the load modal.</summary>
public sealed record PresetLoadItem
{
    /// <summary>The preset.</summary>
    public required Preset Preset { get; init; }

    /// <summary>Whether it can load here.</summary>
    public required PresetAvailability Availability { get; init; }

    /// <summary>Its first model, or null.</summary>
    public PresetModelView? Model { get; init; }

    /// <summary>How many models (Compare).</summary>
    public int ModelCount { get; init; }

    /// <summary>Its parameters as key/values.</summary>
    public IReadOnlyList<(string Key, string Value)> Parameters { get; init; } = [];

    /// <summary>"2d", "5h", "now": when it was last used or changed.</summary>
    public required string Age { get; init; }
}

/// <summary>The shared load modal (<c>_LoadPresetModal</c>).</summary>
public sealed record LoadPresetView
{
    /// <summary>The page loading (<see cref="PresetPages"/>).</summary>
    public required string Page { get; init; }

    /// <summary>The presets, pinned first, then the most recently used.</summary>
    public IReadOnlyList<PresetLoadItem> Items { get; init; } = [];

    /// <summary>Every tag, most used first.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Whether this is the host shell (its content comes from GET /presets/picker when it opens).</summary>
    public bool Hosted { get; init; }

    /// <summary>The host shell for <paramref name="page"/>.</summary>
    public static LoadPresetView Host(string page) => new() { Page = page, Hosted = true };

    /// <summary>The modal's subtitle.</summary>
    public string Subtitle => Page switch
    {
        PresetPages.Compare => "Loading replaces the prompt, the shared options and the columns. Unsaved changes are lost.",
        PresetPages.Bench => "Loading opens a new bench session with the preset's model, tools and messages; the current session is kept.",
        _ => "Loading replaces the current model, parameters and messages. Unsaved changes are lost.",
    };
}
