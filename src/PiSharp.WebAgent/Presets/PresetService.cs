// Decision 0011 (run W4, R8): the Presets area's operations and views. Saving reads a page's capture (PresetCapture), leaves out the parts
// the user unticked and checks the result (PresetRules); updating keeps the id, the use counts and when it was created. Loading converts a
// preset for the page (PresetLoad) after checking that its models can run here: a model this app does not know, or a provider without
// credentials, makes the preset unavailable (the card is marked and Open is disabled with the reason; never a silent switch to another
// model), and an account id this machine does not have is dropped with a note (the provider's active account is used, and the page says so).
// Every preset the page lists comes from webagent.db; nothing is sample data.
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Settings;
using PiSharp.WebAgent.Shell;

namespace PiSharp.WebAgent.Presets;

/// <summary>A preset's name, description, tags and pin, and the parts to keep.</summary>
/// <param name="Name">The name.</param>
/// <param name="Description">The description, or null.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Pinned">Whether it is pinned to the top of Presets.</param>
/// <param name="Include">The parts to keep (<see cref="PresetParts"/>), or null for every part there is.</param>
public sealed record PresetMeta(string Name, string? Description, IReadOnlyList<string> Tags, bool Pinned, IReadOnlyList<string>? Include = null);

/// <summary>What an import did.</summary>
/// <param name="Imported">The presets saved.</param>
/// <param name="Skipped">The ids of presets this app already has, unchanged.</param>
/// <param name="Renamed">Presets whose id was taken by a different preset, saved under a new id (from, to).</param>
public sealed record PresetImportResult(IReadOnlyList<Preset> Imported, IReadOnlyList<string> Skipped, IReadOnlyList<(string From, string To)> Renamed);

/// <summary>A preset made ready for a page.</summary>
/// <param name="Preset">The preset.</param>
/// <param name="Plan">The page's payload, parts and notes.</param>
/// <param name="Availability">Whether its models can run here.</param>
public sealed record PresetLoadAnswer(Preset Preset, PresetLoadPlan Plan, PresetAvailability Availability);

/// <summary>The Presets area's operations. A singleton.</summary>
public sealed class PresetService(PresetStore store, IServiceProvider services, TimeProvider? time = null)
{
    private const int LongestJson = 20_000;

    private readonly PresetStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IServiceProvider services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>The store.</summary>
    public PresetStore Store => store;

    /// <summary>Every preset, most recently changed first.</summary>
    public IReadOnlyList<Preset> List() => store.List();

    /// <summary>Preset <paramref name="id"/>, or null.</summary>
    public Preset? Get(string id) => store.Get(id);

    /// <summary>Preset <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">There is no such preset.</exception>
    public Preset Require(string id) => store.Get(id) ?? throw new KeyNotFoundException($"There is no preset {id}; it may have been deleted.");

    // ---- the page --------------------------------------------------------------------------------------------------------------

    /// <summary>The Presets page for <paramref name="query"/>.</summary>
    public PresetsPage Page(PresetQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var all = store.List();
        var lookup = new Lookup(services);
        return new PresetsPage
        {
            Query = query,
            Cards = [.. Sort(Filter(all, query), query.Sort).Select(preset => Card(preset, lookup))],
            Total = all.Count,
            CompareCount = all.Count(preset => preset.Kind == PresetPages.Compare),
            BenchCount = all.Count(preset => preset.Kind == PresetPages.Bench),
            Tags = TagCounts(all),
            NewPreset = NewPreset(all),
        };
    }

    /// <summary>The presets of <paramref name="all"/> that <paramref name="query"/> keeps.</summary>
    public static IEnumerable<Preset> Filter(IEnumerable<Preset> all, PresetQuery query)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(query);
        foreach (var preset in all)
        {
            if (query.UsedIn is { } page && preset.Kind != page && !preset.UsedIn.Contains(page)) continue;
            if (query.Tags.Any(tag => !preset.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))) continue;
            if (query.Search is { } text && !Matches(preset, text)) continue;
            yield return preset;
        }
    }

    /// <summary><paramref name="presets"/> in <paramref name="sort"/>'s order, pinned first.</summary>
    public static IEnumerable<Preset> Sort(IEnumerable<Preset> presets, string sort)
    {
        var pinned = presets.OrderByDescending(preset => preset.Pinned);
        return sort switch
        {
            "uses" => pinned.ThenByDescending(preset => preset.Uses).ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase),
            "name" => pinned.ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ThenBy(preset => preset.Id, StringComparer.Ordinal),
            "created" => pinned.ThenByDescending(preset => preset.Created).ThenBy(preset => preset.Id, StringComparer.Ordinal),
            _ => pinned.ThenByDescending(preset => preset.LastUsed ?? DateTimeOffset.MinValue).ThenByDescending(preset => preset.Updated),
        };
    }

    /// <summary>Preset <paramref name="preset"/>'s card.</summary>
    public PresetCardView Card(Preset preset) => Card(preset, new Lookup(services));

    /// <summary>Whether <paramref name="preset"/>'s models can run here.</summary>
    public PresetAvailability Availability(Preset preset) => new Lookup(services).Availability(preset);

    // ---- the save modal ----------------------------------------------------------------------------------------------------------

    /// <summary>The save modal's new pane for what <paramref name="page"/> posted (<paramref name="capture"/>); the preset it loaded,
    /// <paramref name="originId"/>, is offered to update.</summary>
    public SavePresetView Preview(string? page, JsonNode? capture, string? originId)
    {
        var all = store.List();
        var target = PresetPages.Known(page) ? page! : PresetPages.Playground;
        // Update is offered for a preset of this page only (updating it from another page would change what kind of preset it is).
        var origin = string.IsNullOrWhiteSpace(originId) ? null : store.Get(originId.Trim()) is { } loaded && loaded.Kind == target ? loaded : null;
        Preset draft;
        try
        {
            draft = PresetRules.Normalize(PresetCapture.From(page, capture));
        }
        catch (ArgumentException error)
        {
            return new SavePresetView { Mode = SavePresetView.NewMode, Page = target, Error = error.Message, TagsInUse = TagNames(all) };
        }
        var lookup = new Lookup(services);
        return new SavePresetView
        {
            Mode = SavePresetView.NewMode,
            Page = target,
            Draft = draft,
            Origin = origin,
            TagsInUse = TagNames(all),
            Captured = Captured(draft, lookup),
            Parts = Parts(draft, edit: false),
            Model = draft.Models.Count > 0 ? lookup.Model(draft.Models[0].Provider, draft.Models[0].Model) : null,
        };
    }

    /// <summary>The Presets page's New preset modal.</summary>
    public SavePresetView NewPreset() => NewPreset(store.List());

    /// <summary>The save modal's edit pane for preset <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">There is no such preset.</exception>
    public SavePresetView Edit(string id)
    {
        var preset = Require(id);
        var lookup = new Lookup(services);
        var json = PresetJson.Export(preset);
        return new SavePresetView
        {
            Mode = SavePresetView.EditMode,
            Page = preset.Kind,
            Draft = preset,
            TagsInUse = TagNames(store.List()),
            Captured = Captured(preset, lookup),
            Parts = Parts(preset, edit: true),
            Json = json.Length > LongestJson ? json[..LongestJson] + "\n… (" + (json.Length / 1024).ToString("N0", CultureInfo.InvariantCulture) + " KB in all; Export JSON has the whole preset)" : json,
            Model = preset.Models.Count > 0 ? lookup.Model(preset.Models[0].Provider, preset.Models[0].Model) : null,
        };
    }

    // ---- saving ------------------------------------------------------------------------------------------------------------------

    /// <summary>Saves what <paramref name="page"/> posted as a new preset, or as preset <paramref name="update"/> (keeping its id, use counts
    /// and creation time).</summary>
    /// <exception cref="ArgumentException">It is not valid.</exception>
    /// <exception cref="KeyNotFoundException">There is no preset <paramref name="update"/>.</exception>
    public Preset Save(string? page, JsonNode? capture, PresetMeta meta, string? update = null)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var draft = PresetCapture.From(page, capture);
        if (meta.Include is { } include)
            foreach (var part in PresetParts.All.Where(part => !include.Contains(part))) draft = draft.Without(part);
        draft = draft with { Name = meta.Name, Description = meta.Description, Tags = meta.Tags, Pinned = meta.Pinned };
        if (!string.IsNullOrWhiteSpace(update))
        {
            return store.Update(update.Trim(), existing => existing.Kind != draft.Kind
                ? throw new ArgumentException($"“{existing.Name}” is a {PresetPages.Title(existing.Kind)} preset; save this {PresetPages.Title(draft.Kind)} setup as a new preset.")
                : PresetRules.Normalize(draft with
            {
                Id = existing.Id,
                Uses = existing.Uses,
                LastUsed = existing.LastUsed,
                UsedIn = existing.UsedIn,
                Created = existing.Created,
            })) ?? throw new KeyNotFoundException($"There is no preset {update.Trim()}; it may have been deleted. Save it as a new preset.");
        }
        return store.Add(PresetRules.Normalize(draft with { Id = "" }));
    }

    /// <summary>Changes preset <paramref name="id"/>'s name, description, tags and pin, and leaves out the parts not in the include list.</summary>
    /// <exception cref="ArgumentException">It is not valid.</exception>
    /// <exception cref="KeyNotFoundException">There is no such preset.</exception>
    public Preset UpdateMeta(string id, PresetMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return store.Update(id, preset =>
        {
            var next = preset with { Name = meta.Name, Description = meta.Description, Tags = meta.Tags, Pinned = meta.Pinned };
            if (meta.Include is { } include)
                foreach (var part in preset.Parts.Where(part => !include.Contains(part))) next = next.Without(part);
            return PresetRules.Normalize(next);
        }) ?? throw new KeyNotFoundException($"There is no preset {id}; it may have been deleted.");
    }

    /// <summary>A copy of preset <paramref name="id"/>, "Name (copy)", under a new id, never used.</summary>
    /// <exception cref="KeyNotFoundException">There is no such preset.</exception>
    public Preset Duplicate(string id)
    {
        var preset = Require(id);
        var name = preset.Name + " (copy)";
        if (name.Length > Preset.MaxName) name = preset.Name[..(Preset.MaxName - 7)].TrimEnd() + " (copy)";
        return store.Add(preset with { Id = "", Name = name, Pinned = false, Uses = 0, LastUsed = null, UsedIn = [], Created = default });
    }

    /// <summary>Removes preset <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">There is no such preset.</exception>
    public void Delete(string id)
    {
        if (!store.Delete(id)) throw new KeyNotFoundException($"There is no preset {id}; it may have been deleted already.");
    }

    /// <summary>Counts a load of preset <paramref name="id"/> into <paramref name="page"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="page"/> is not a page presets load into.</exception>
    /// <exception cref="KeyNotFoundException">There is no such preset.</exception>
    public Preset Used(string id, string? page)
    {
        if (!PresetPages.Known(page)) throw new ArgumentException("A preset loads into the Playground, Compare or the Tool Bench.");
        return store.Update(id, preset => preset with
        {
            Uses = preset.Uses + 1,
            LastUsed = clock.GetUtcNow(),
            UsedIn = preset.UsedIn.Contains(page!) ? preset.UsedIn : [.. preset.UsedIn, page!],
        }, touch: false) ?? throw new KeyNotFoundException($"There is no preset {id}; it may have been deleted.");
    }

    // ---- loading -----------------------------------------------------------------------------------------------------------------

    /// <summary>Preset <paramref name="id"/> made ready for <paramref name="page"/> (nothing is counted: see <see cref="Used"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="page"/> is not a page presets load into.</exception>
    /// <exception cref="KeyNotFoundException">There is no such preset.</exception>
    public async Task<PresetLoadAnswer> LoadAsync(string id, string? page, CancellationToken cancellationToken = default)
    {
        if (!PresetPages.Known(page)) throw new ArgumentException("A preset loads into the Playground, Compare or the Tool Bench.");
        var preset = Require(id);
        var availability = new Lookup(services).Availability(preset);
        var notes = new List<string>();
        preset = await AccountsAsync(preset, notes, cancellationToken).ConfigureAwait(false);
        var plan = PresetLoad.For(preset, page!);
        return new PresetLoadAnswer(preset, plan with { Notes = [.. notes, .. plan.Notes] }, availability);
    }

    /// <summary>The load modal for <paramref name="page"/>.</summary>
    public LoadPresetView Picker(string? page)
    {
        var target = PresetPages.Known(page) ? page! : PresetPages.Playground;
        var all = store.List();
        var lookup = new Lookup(services);
        var now = clock.GetUtcNow();
        return new LoadPresetView
        {
            Page = target,
            Tags = TagNames(all),
            Items = [.. Sort(all, "recent").Select(preset => new PresetLoadItem
            {
                Preset = preset,
                Availability = lookup.Availability(preset),
                Model = preset.Models.Count > 0 ? lookup.Model(preset.Models[0].Provider, preset.Models[0].Model) : null,
                ModelCount = preset.Models.Count,
                Parameters = Parameters(preset),
                Age = Age(preset.LastUsed ?? preset.Updated, now),
            })],
        };
    }

    // ---- JSON --------------------------------------------------------------------------------------------------------------------

    /// <summary>Preset <paramref name="id"/>'s file: its name and text.</summary>
    /// <exception cref="KeyNotFoundException">There is no such preset.</exception>
    public (string FileName, string Json) Export(string id)
    {
        var preset = Require(id);
        return (PresetJson.FileName(preset), PresetJson.Export(preset));
    }

    /// <summary>The file of every preset: its name and text.</summary>
    public (string FileName, string Json) ExportAll()
    {
        var now = clock.GetUtcNow();
        var all = Sort(store.List(), "name").ToList();
        return (PresetJson.AllFileName(now), PresetJson.ExportAll(all, now));
    }

    /// <summary>Imports a presets file: every preset in it is checked first, and none is saved when one is not valid. A preset this app
    /// already has unchanged is skipped; one whose id another preset has is saved under a new id (an import never replaces a preset).</summary>
    /// <exception cref="ArgumentException">The file is not valid JSON, not a presets file, or holds a preset that is not valid.</exception>
    public PresetImportResult Import(string? text)
    {
        var incoming = PresetJson.Parse(text);
        var adding = new List<Preset>();
        var skipped = new List<string>();
        foreach (var preset in incoming)
        {
            if (preset.Id.Length > 0 && store.Get(preset.Id) is { } existing && PresetJson.Same(existing, preset with { Pinned = existing.Pinned }))
            {
                skipped.Add(preset.Id);
                continue;
            }
            if (adding.Any(other => other.Id.Length > 0 && other.Id == preset.Id && PresetJson.Same(other, preset)))
            {
                skipped.Add(preset.Id);
                continue;
            }
            adding.Add(preset);
        }
        var stored = store.AddAll(adding);
        var renamed = adding.Zip(stored).Where(pair => pair.First.Id.Length > 0 && pair.First.Id != pair.Second.Id)
            .Select(pair => (pair.First.Id, pair.Second.Id)).ToList();
        return new PresetImportResult(stored, skipped, renamed);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------

    /// <summary>"now", "5m", "2h", "3d", "2w", "4mo", "1y": how long ago <paramref name="at"/> was.</summary>
    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}m";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}h";
        if (span < TimeSpan.FromDays(7)) return $"{(int)span.TotalDays}d";
        if (span < TimeSpan.FromDays(30)) return $"{(int)(span.TotalDays / 7)}w";
        if (span < TimeSpan.FromDays(365)) return $"{(int)(span.TotalDays / 30)}mo";
        return $"{(int)(span.TotalDays / 365)}y";
    }

    /// <summary>A preset's parameters as key/values (the load modal's preview).</summary>
    public static IReadOnlyList<(string Key, string Value)> Parameters(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var list = new List<(string, string)>();
        foreach (var (name, text) in preset.Options?.Entries() ?? []) list.Add((name, text));
        foreach (var (index, column) in (preset.Columns ?? []).Index().Where(pair => pair.Item.Overrides))
            list.Add(($"column {index + 1}", string.Join(" · ", new[] { column.Thinking is { } t ? "thinking " + t : null, column.Temperature is { } temperature ? "temperature " + PresetOptions.Number(temperature) : null }.OfType<string>())));
        if (preset.ToolChoice is { } choice) list.Add(("toolChoice", choice));
        if (preset.Parallel is { } parallel) list.Add(("parallel calls", parallel ? "on" : "off"));
        if (preset.Rules is { } rules) list.Add(("auto-respond", rules.AutoRespond ? "on" : "off"));
        if (preset.Headers is { Count: > 0 } headers) list.Add(("headers", string.Join(", ", headers.Keys)));
        return list;
    }

    private SavePresetView NewPreset(IReadOnlyList<Preset> all) => new()
    {
        Mode = SavePresetView.NewMode,
        Page = PresetCapture.PresetsPage,
        TagsInUse = TagNames(all),
        Picker = new ProviderModelSelect("sp") { Small = false },
        Parts = [],
    };

    private static bool Matches(Preset preset, string text) =>
        preset.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || preset.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
        || preset.Models.Any(model => (model.Provider + "/" + model.Model).Contains(text, StringComparison.OrdinalIgnoreCase))
        || preset.Tags.Any(tag => tag.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<(string Tag, int Count)> TagCounts(IEnumerable<Preset> all) =>
        [.. all.SelectMany(preset => preset.Tags).GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.First(), group.Count())).OrderByDescending(pair => pair.Item2).ThenBy(pair => pair.Item1, StringComparer.OrdinalIgnoreCase)];

    private static IReadOnlyList<string> TagNames(IEnumerable<Preset> all) => [.. TagCounts(all).Select(pair => pair.Tag)];

    private PresetCardView Card(Preset preset, Lookup lookup)
    {
        var used = preset.Uses == 0 ? "never used" : $"used {preset.Uses}× · last used {SettingsText.Ago(preset.LastUsed, clock.GetUtcNow())}";
        var pages = new[] { preset.Kind }.Concat(preset.UsedIn).Distinct(StringComparer.Ordinal).Select(PresetPages.Title);
        var (primary, secondary, other) = preset.Kind switch
        {
            PresetPages.Compare => (PresetPages.Compare, PresetPages.Playground, PresetPages.Bench),
            PresetPages.Bench => (PresetPages.Bench, PresetPages.Playground, PresetPages.Compare),
            _ => (PresetPages.Playground, PresetPages.Compare, PresetPages.Bench),
        };
        return new PresetCardView
        {
            Preset = preset,
            Availability = lookup.Availability(preset),
            Models = [.. preset.Models.Select(model => lookup.Model(model.Provider, model.Model))],
            Caps = Caps(preset),
            UsedText = used,
            UsedInText = string.Join(" · ", pages),
            Primary = Action(preset, primary),
            Secondary = Action(preset, secondary),
            Other = Action(preset, other),
        };
    }

    private static PresetAction Action(Preset preset, string page)
    {
        var title = (preset.Kind, page) switch
        {
            (PresetPages.Compare, PresetPages.Playground or PresetPages.Bench) when preset.Columns is { Count: > 1 } => "Loads the first column's model",
            (PresetPages.Bench, PresetPages.Playground) when preset.Tools is { Count: > 0 } => "Loads the tools with one mock response each",
            (not PresetPages.Compare, PresetPages.Compare) when preset.Has(PresetParts.Model) => "Loads the model into the first column",
            _ => null,
        };
        var url = PresetPages.Url(page, preset.Id);
        if (page == PresetPages.Playground && preset.Models.Count > 0)
            url += "&provider=" + Uri.EscapeDataString(preset.Models[0].Provider) + "&model=" + Uri.EscapeDataString(preset.Models[0].Model);
        return new PresetAction(page, url, title);
    }

    private static IReadOnlyList<PresetCap> Caps(Preset preset)
    {
        var caps = new List<PresetCap>();
        foreach (var (name, text) in preset.Options?.Entries() ?? [])
        {
            caps.Add(name switch
            {
                "thinking" => new PresetCap("thinking: " + text, Reasoning: true),
                "temperature" => new PresetCap("temp " + text),
                "providerOptions" => new PresetCap("provider options"),
                "stop" => new PresetCap("stop: " + text),
                _ => new PresetCap(name + " " + text),
            });
        }
        if (preset.Columns?.Any(column => column.Overrides) == true) caps.Add(new PresetCap("column overrides"));
        if (preset.ToolChoice is { } choice) caps.Add(new PresetCap("toolChoice " + choice));
        if (preset.Tools is { Count: > 0 } tools)
        {
            caps.Add(new PresetCap(tools.Count == 1 ? "1 tool" : $"{tools.Count} tools"));
            if (tools.Any(tool => tool.Mocks.Count > 0)) caps.Add(new PresetCap("mock results"));
        }
        if (preset.Parallel is false && preset.Tools is { Count: > 0 }) caps.Add(new PresetCap("sequential calls"));
        if (preset.Rules is { AutoRespond: true }) caps.Add(new PresetCap("auto-respond"));
        if (preset.Has(PresetParts.SystemPrompt)) caps.Add(new PresetCap("system prompt"));
        if (preset.Messages is { Count: > 0 } messages)
            caps.Add(new PresetCap(preset.Kind == PresetPages.Compare ? "shared prompt" : messages.Count == 1 ? "1 message" : $"{messages.Count} messages"));
        if (preset.Headers is { Count: > 0 } headers) caps.Add(new PresetCap(headers.Count == 1 ? "1 header" : $"{headers.Count} headers"));
        return caps;
    }

    private static IReadOnlyList<(string Key, string Value)> Captured(Preset preset, Lookup lookup)
    {
        var rows = new List<(string, string)>();
        if (preset.Columns is { Count: > 0 } columns)
        {
            foreach (var (index, column) in columns.Index())
            {
                var overrides = string.Join(" · ", new[] { column.Thinking is { } t ? "thinking " + t : null, column.Temperature is { } temperature ? "temp " + PresetOptions.Number(temperature) : null }.OfType<string>());
                rows.Add(($"Column {index + 1}", column.Provider + "/" + column.Model + (overrides.Length > 0 ? " · " + overrides : "")));
            }
        }
        else if (preset.Models.Count > 0)
        {
            var model = lookup.Model(preset.Models[0].Provider, preset.Models[0].Model);
            rows.Add(("Model", model.Id));
            if (model.Api is { } api) rows.Add(("API", api));
        }
        else
        {
            rows.Add(("Model", "not saved"));
        }
        if (preset.Has(PresetParts.Model)) rows.Add(("Account", preset.Account is { } account ? SettingsText.ShortId(account) : preset.Columns?.Any(column => column.Account is not null) == true ? "per column" : "the provider's active account"));
        rows.Add(("Thinking", preset.Options?.Thinking ?? "off"));
        foreach (var (name, text) in preset.Options?.Entries() ?? [])
            if (name != "thinking") rows.Add((name, text));
        if (preset.Rules is { } rules)
            rows.Add(("Auto-respond", rules.AutoRespond ? $"on · latency {rules.Latency}" : "off"));
        rows.Add(("System prompt", preset.SystemPrompt is { } system ? Chars(system.Length) : "none"));
        rows.Add((preset.Kind == PresetPages.Compare ? "Prompt" : "Messages", preset.Messages is { Count: > 0 } messages
            ? preset.Kind == PresetPages.Compare ? Chars(messages[0].Text?.Length ?? 0) : messages.Count.ToString(CultureInfo.InvariantCulture) : "none"));
        if (preset.Kind != PresetPages.Compare) rows.Add(("Tools", ToolsText(preset) ?? "none"));
        if (preset.Headers is { Count: > 0 } headers) rows.Add(("Headers", string.Join(", ", headers.Keys)));
        return rows;
    }

    private static IReadOnlyList<PresetPartView> Parts(Preset preset, bool edit)
    {
        var list = new List<PresetPartView>();
        foreach (var part in PresetParts.All)
        {
            var has = preset.Has(part);
            var detail = part switch
            {
                PresetParts.Model => preset.Columns is { Count: > 0 } columns ? (columns.Count == 1 ? "1 model" : $"{columns.Count} models") + " · " + string.Join(", ", columns.Select(column => column.Model))
                    : preset.ModelId ?? "No model",
                PresetParts.Parameters => has ? string.Join(", ", (preset.Options?.Entries() ?? []).Select(entry => entry.Name).Concat(preset.Rules is null ? [] : ["auto-respond rules"])) : "No parameters set",
                PresetParts.SystemPrompt => preset.SystemPrompt is { } system ? Chars(system.Length) : "No system prompt",
                PresetParts.Messages => preset.Messages is { Count: > 0 } messages
                    ? preset.Kind == PresetPages.Compare ? "the shared prompt" : messages.Count == 1 ? "1 message" : $"{messages.Count} messages"
                    : "No messages",
                PresetParts.Tools => ToolsText(preset) ?? "No tools",
                PresetParts.Headers => preset.Headers is { Count: > 0 } headers ? (headers.Count == 1 ? "1 custom header" : $"{headers.Count} custom headers") + " · saved as typed" : "No custom headers",
                _ => "",
            };
            var on = has && (edit || part != PresetParts.Headers);
            list.Add(new PresetPartView(part, PresetParts.Label(part), detail, has, on));
        }
        return list;
    }

    private static string? ToolsText(Preset preset)
    {
        if (preset.Tools is not { Count: > 0 } tools) return preset.ToolChoice is { } choice ? "toolChoice " + choice : null;
        var on = tools.Count(tool => tool.Enabled);
        var mocks = tools.Sum(tool => tool.Mocks.Count);
        var text = $"{tools.Count} {(tools.Count == 1 ? "tool" : "tools")} ({on} on)";
        if (mocks > 0) text += $" · {mocks} {(mocks == 1 ? "mock" : "mocks")}";
        if (preset.ToolChoice is { } kind) text += " · toolChoice " + kind;
        if (preset.Parallel is { } parallel) text += parallel ? " · parallel" : " · sequential";
        return text;
    }

    private static string Chars(int count) => count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " char" : " chars");

    private async Task<Preset> AccountsAsync(Preset preset, List<string> notes, CancellationToken cancellationToken)
    {
        if (services.GetService<IChatAccounts>() is not { } accounts) return preset;
        async Task<bool> KnownAsync(string provider, string account)
        {
            try { return await accounts.FindAsync(provider, account, cancellationToken).ConfigureAwait(false) is not null; }
            catch (Exception error) when (error is not OperationCanceledException) { return false; }
        }
        if (preset.Account is { } id && preset.Provider is { } provider && !await KnownAsync(provider, id).ConfigureAwait(false))
        {
            notes.Add($"The preset's {provider} account ({SettingsText.ShortId(id)}) is not on this machine, so {provider}'s active account is used.");
            preset = preset with { Account = null };
        }
        if (preset.Columns is { Count: > 0 } columns)
        {
            var list = new List<CompareColumnDraft>();
            foreach (var column in columns)
            {
                if (column.Account is { } account && !await KnownAsync(column.Provider, account).ConfigureAwait(false))
                {
                    notes.Add($"The {column.Provider}/{column.Model} column's account ({SettingsText.ShortId(account)}) is not on this machine, so {column.Provider}'s active account is used.");
                    list.Add(column with { Account = null });
                }
                else list.Add(column);
            }
            preset = preset with { Columns = list };
        }
        return preset;
    }

    // The catalog, the providers and the run engine, read once per page.
    private sealed class Lookup(IServiceProvider services)
    {
        private readonly RunEngine? engine = services.GetService<RunEngine>();
        private readonly IModelCatalog? catalog = services.GetService<IModelCatalog>();
        private readonly Lazy<IReadOnlyDictionary<string, ProviderEntry>> providers = new(() =>
        {
            try
            {
                return services.GetService<ProviderDirectory>()?.List().GroupBy(entry => entry.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
                    ?? new Dictionary<string, ProviderEntry>(StringComparer.Ordinal);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger<PresetService>().LogWarning(error, "The providers could not be listed for Presets.");
                return new Dictionary<string, ProviderEntry>(StringComparer.Ordinal);
            }
        });
        private readonly Dictionary<(string, string), RunCapabilities?> capabilities = [];

        public PresetModelView Model(string provider, string model)
        {
            var caps = Capabilities(provider, model);
            var entry = providers.Value.GetValueOrDefault(provider);
            var name = catalog?.FindModel(provider, model)?.Name ?? caps?.Name ?? model;
            var providerName = entry?.Name ?? catalog?.FindProvider(provider)?.DisplayName ?? provider;
            return new PresetModelView(provider, model, name, providerName, entry?.LogoUrl ?? ProviderLogos.Url(provider), caps?.Api, Problem(provider, model) is null);
        }

        public PresetAvailability Availability(Preset preset)
        {
            foreach (var (provider, model) in preset.Models)
                if (Problem(provider, model) is { } problem) return problem;
            return PresetAvailability.Available;
        }

        private PresetAvailability? Problem(string provider, string model)
        {
            if (engine is null) return null;
            var caps = Capabilities(provider, model);
            if (caps is null)
                return new PresetAvailability(false, "model not found", $"{provider}/{model} is not a model this app knows (a custom provider may have been removed).", "Model not found");
            if (caps.Offline) return null;
            if (providers.Value.GetValueOrDefault(provider) is { NoCredentials: false, IsConnected: false } entry)
                return new PresetAvailability(false, "provider not configured", $"{entry.Name} has no credentials.", $"Connect {entry.Name} first", entry.Href, "Connect " + entry.Name);
            return null;
        }

        private RunCapabilities? Capabilities(string provider, string model)
        {
            if (engine is null) return null;
            if (capabilities.TryGetValue((provider, model), out var known)) return known;
            RunCapabilities? found;
            try { found = engine.Capabilities(provider, model); }
            catch (Exception error) when (error is not OutOfMemoryException) { found = null; }
            capabilities[(provider, model)] = found;
            return found;
        }
    }
}
