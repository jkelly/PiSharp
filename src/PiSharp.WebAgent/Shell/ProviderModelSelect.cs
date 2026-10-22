// Decision 0011 (run W2, WU4): the model for _ProviderModelSelect, the two linked dropdowns every model choice uses (Provider -> Model; never one
// combined list). The partial reads IModelCatalog and Models' hidden list; the caller names the fields, the selection and, when it knows
// them, the providers to offer (for example the connected ones). Without script the form still posts the right pair: only the selected
// provider's model list is enabled. CatalogText formats a model's one-line summary, as the mockups word it.
using System.Globalization;
using System.Text;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Shell;

/// <summary>What <c>_ProviderModelSelect</c> renders.</summary>
/// <param name="IdPrefix">A prefix for element ids, unique on the page (such as <c>mp</c> in the model picker, <c>dm</c> on Defaults).</param>
public sealed record ProviderModelSelect(string IdPrefix)
{
    /// <summary>The selected provider id; null (or one not offered) selects the first provider.</summary>
    public string? Provider { get; init; }

    /// <summary>The selected model id of <see cref="Provider"/>; null (or one not offered) selects the provider's first model.</summary>
    public string? Model { get; init; }

    /// <summary>The provider field's <c>name</c>.</summary>
    public string ProviderField { get; init; } = "provider";

    /// <summary>The model field's <c>name</c>.</summary>
    public string ModelField { get; init; } = "model";

    /// <summary>Small controls (<c>form-select-sm</c>), as on the settings pages.</summary>
    public bool Small { get; init; }

    /// <summary>The provider ids to offer, in catalog order; null offers every catalog provider.</summary>
    public IReadOnlyCollection<string>? Providers { get; init; }

    /// <summary>Whether models hidden in Settings › Models are listed too (the selected model always is).</summary>
    public bool IncludeHidden { get; init; }
}

/// <summary>Text for catalog entries.</summary>
public static class CatalogText
{
    /// <summary>A model's one-line summary, such as <c>272K context · reasoning · images · $2 / $10 per 1M tokens</c>.</summary>
    public static string Summary(CatalogProvider provider, CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        var parts = new List<string>();
        if (model.ContextWindow > 0) parts.Add(Tokens(model.ContextWindow) + " context");
        if (model.Reasoning) parts.Add("reasoning");
        if (model.Input.Contains("image", StringComparer.Ordinal)) parts.Add("images");
        parts.Add(provider.Billing switch
        {
            BillingKind.Subscription => "included in your subscription",
            BillingKind.Free => "free",
            BillingKind.Metered when model.Cost is { } cost => $"{Dollars(cost.Input)} / {Dollars(cost.Output)} per 1M tokens",
            _ => "price unknown",
        });
        return string.Join(" · ", parts);
    }

    /// <summary>A token count in the mockups' short form: <c>272K</c>, <c>1M</c>, <c>1.5M</c>.</summary>
    public static string Tokens(long count) => count >= 1_000_000
        ? (count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : count >= 1_000 ? Math.Round(count / 1_000d).ToString(CultureInfo.InvariantCulture) + "K" : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>A price in dollars: whole amounts without cents (<c>$2</c>), others with at least two decimals (<c>$0.75</c>, <c>$0.075</c>).</summary>
    public static string Dollars(double amount) =>
        "$" + (amount == Math.Floor(amount) ? amount.ToString("0", CultureInfo.InvariantCulture) : amount.ToString("0.00##", CultureInfo.InvariantCulture));

    /// <summary><paramref name="value"/> as a fragment of an element id: letters, digits, <c>-</c> and <c>_</c>; anything else becomes <c>-</c>.</summary>
    public static string IdPart(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var c in value) builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return builder.ToString();
    }
}
