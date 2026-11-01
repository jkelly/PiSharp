// Decision 0011 (run W3, CP): what Settings › Custom providers renders, built from the store's snapshot, the vault (which providers keep a
// stored key: a masked hint at most), the catalog (how many custom models the model picker has now) and the tests the user ran since the
// app started. Nothing is checked over the network to render it: reachability is shown only after a test the user clicked.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Routing;

namespace PiSharp.WebAgent.CustomProviders;

/// <summary>The model of <c>Views/Settings/CustomProviders.cshtml</c>.</summary>
/// <param name="Snapshot">The file.</param>
/// <param name="Rows">One row per custom provider, in file order.</param>
/// <param name="Overrides">One row per model override, in file order.</param>
/// <param name="Preview">The file as the preview shows it (redacted), or null when there is none.</param>
/// <param name="OverrideProviders">The built-in providers the override picker offers (none with --faux).</param>
/// <param name="Offline">The app runs with --faux: nothing is tested or routed.</param>
public sealed record CustomProvidersPage(CustomProviderSnapshot Snapshot, IReadOnlyList<CustomProviderRow> Rows, IReadOnlyList<OverrideRow> Overrides, string? Preview,
    IReadOnlyList<string> OverrideProviders, bool Offline)
{
    /// <summary>The empty state: no custom provider.</summary>
    public bool Empty => Rows.Count == 0;

    /// <summary>How many custom models the model picker lists now.</summary>
    public int InPicker => Rows.Sum(row => row.InPicker);

    /// <summary>How many providers answered their last test, and how many failed.</summary>
    public (int Ok, int Failed) Tested => (Rows.Count(row => row.LastTest is { Ok: true }), Rows.Count(row => row.LastTest is { Ok: false }));

    /// <summary>When the last test ran, or null.</summary>
    public DateTimeOffset? LastCheck => Rows.Select(row => row.LastTest?.TestedAt).Where(time => time is not null).Max();

    /// <summary>The page of <paramref name="service"/>'s current state.</summary>
    public static CustomProvidersPage Build(CustomProviderService service, IModelCatalog catalog, bool offline)
    {
        ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(catalog);
        var snapshot = service.Current;
        var rows = snapshot.Providers.Select(provider => new CustomProviderRow(provider, service.KeySource(provider), service.StoredKey(provider.Id)?.Hint,
            provider.KeyVariable is { } variable ? service.IsSet(variable) : null, service.LastTest(provider.Id),
            catalog is RuntimeModelCatalog ? catalog.Models(provider.Id).Count : 0)).ToList();
        var builtIn = RuntimeModelCatalog.BuiltInOnly;
        var overrides = snapshot.Overrides.Select(item => OverrideRow.Of(item, builtIn)).ToList();
        IReadOnlyList<string> offered = catalog is RuntimeModelCatalog
            ? [.. catalog.Providers.Select(provider => provider.Id).Where(CustomProviderJson.IsBuiltin)]
            : [];
        return new CustomProvidersPage(snapshot, rows, overrides, service.PreviewFile(), offered, offline);
    }

    /// <summary>"saved today 10:41", "saved 2026-10-08 10:41" (UTC).</summary>
    public static string SavedText(DateTimeOffset saved, DateTimeOffset now) =>
        saved.UtcDateTime.Date == now.UtcDateTime.Date ? "saved today " + saved.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC"
            : "saved " + saved.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>One custom provider on the page.</summary>
/// <param name="Provider">Its metadata.</param>
/// <param name="Key">Where its key comes from.</param>
/// <param name="Hint">A stored key's masked hint, or null.</param>
/// <param name="VariableSet">Whether its key's variable is set on the web host (null: no variable).</param>
/// <param name="LastTest">Its last test since the app started, or null.</param>
/// <param name="InPicker">How many of its models the model picker lists now (0 with --faux, or when it is not routed).</param>
public sealed record CustomProviderRow(CustomProviderInfo Provider, CustomKeySource Key, string? Hint, bool? VariableSet, CustomProviderTest? LastTest, int InPicker)
{
    /// <summary>The logo URL (LM Studio and Ollama have marks), or null for the network icon.</summary>
    public string? LogoUrl => ProviderLogos.Url(Provider.Id);

    /// <summary>The key chip's text.</summary>
    public string KeyText => Key switch
    {
        CustomKeySource.Stored => "stored key" + (Hint is { Length: > 0 } ? " · " + Hint : ""),
        CustomKeySource.Environment => "env · $" + Provider.KeyVariable + (VariableSet == false ? " · not set" : ""),
        CustomKeySource.Unsupported => "key not used",
        _ => "no key",
    };

    /// <summary>The key chip's icon.</summary>
    public string KeyIcon => Key switch
    {
        CustomKeySource.Stored => "bi-safe",
        CustomKeySource.Environment => "bi-terminal",
        CustomKeySource.Unsupported => "bi-exclamation-triangle",
        _ => "bi-unlock",
    };

    /// <summary>The key chip's tone (colour only for status): info for a reference, warn for a problem.</summary>
    public string? KeyTone => Key switch
    {
        CustomKeySource.Environment when VariableSet == false => "warn",
        CustomKeySource.Environment => "info",
        CustomKeySource.Unsupported => "warn",
        _ => null,
    };

    /// <summary>"1 model · qwen3-coder-30b-a3b" (the first ids).</summary>
    public string ModelsText
    {
        get
        {
            var count = Provider.Models.Count;
            var names = string.Join(", ", Provider.Models.Take(4).Select(model => model.Id)) + (count > 4 ? ", …" : "");
            return $"{count} model{(count == 1 ? "" : "s")}" + (count > 0 ? " · " + names : "");
        }
    }
}

/// <summary>One model override on the page.</summary>
/// <param name="Provider">The built-in provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Name">The model's name (its id when the catalog does not list it).</param>
/// <param name="Chips">What changes: a field with its catalog value and the new one.</param>
/// <param name="Json">The override as JSON (redacted), for its editor.</param>
public sealed record OverrideRow(string Provider, string Model, string Name, IReadOnlyList<OverrideChip> Chips, string Json)
{
    /// <summary>The provider's logo URL, or null.</summary>
    public string? LogoUrl => ProviderLogos.Url(Provider);

    /// <summary>A unique element id fragment.</summary>
    public string Key => "ovr-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Provider + "/" + Model)))[..12].ToLowerInvariant();

    /// <summary>The row of <paramref name="item"/>, with the catalog values of <paramref name="builtIn"/>.</summary>
    internal static OverrideRow Of(ModelOverrideInfo item, RuntimeModelCatalog builtIn)
    {
        var row = builtIn.Find(item.Provider, item.Model);
        var base_ = row?.Entry.CloneJson();
        var chips = new List<OverrideChip>();
        foreach (var (field, value) in item.Fields)
        {
            switch (field)
            {
                case "cost" when value is JsonObject cost:
                    foreach (var (part, price) in cost)
                        chips.Add(new OverrideChip("cost." + part, base_?["cost"]?[part] is { } old ? CustomProviderJson.Short(old, money: true) : null, CustomProviderJson.Short(price, money: true)));
                    break;
                case "headers" when value is JsonObject headers:
                    foreach (var (name, _) in headers) chips.Add(new OverrideChip("headers + " + name, null, null));
                    break;
                case "compat" when value is JsonObject compat:
                    foreach (var (name, setting) in compat) chips.Add(new OverrideChip("compat." + name, null, CustomProviderJson.Short(setting)));
                    break;
                default:
                    var before = base_?[field];
                    chips.Add(new OverrideChip(field, before is JsonValue ? CustomProviderJson.Short(before) : null,
                        value is JsonValue or null ? CustomProviderJson.Short(value) : "changed"));
                    break;
            }
        }
        return new OverrideRow(item.Provider, item.Model, row?.Model.Name ?? item.Model, chips, CustomProviderJson.Pretty(item.Fields));
    }
}

/// <summary>One changed field of a model override: <c>contextWindow 1,000,000 → 200,000</c>.</summary>
/// <param name="Field">The field (<c>cost.input</c>, <c>headers + anthropic-beta</c>).</param>
/// <param name="Old">The catalog value, or null when not shown.</param>
/// <param name="New">The new value, or null when the field alone says it.</param>
public sealed record OverrideChip(string Field, string? Old, string? New);
