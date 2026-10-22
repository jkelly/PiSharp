// Decision 0011 (run W2, WU4): the catalog of --faux mode, and of every mode until the Routing area registers the real one. It lists exactly
// the models the registered FauxDurableModels answers (provider "faux"), so a chat started from it always resolves through
// IDurableModels.GetModel. Echo thinking reasons, so the thinking-level control has levels to show offline. Nothing here calls a provider.
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Faux;

/// <summary>The faux provider and its models, from a <see cref="FauxDurableModels"/>.</summary>
public sealed class FauxModelCatalog(FauxDurableModels models) : IModelCatalog
{
    /// <summary><c>faux/echo-thinking</c>: a reasoning faux model with a 200000-token window.</summary>
    public static FauxModel EchoThinking { get; } = new("echo-thinking", "Echo thinking", 200_000, 32_000, Reasoning: true);

    /// <summary>The thinking levels <see cref="EchoThinking"/> lists.</summary>
    public static IReadOnlyList<string> ReasoningLevels { get; } = ["off", "minimal", "low", "medium", "high"];

    /// <summary>The faux provider: no sign-in, free.</summary>
    public static CatalogProvider Provider { get; } = new(FauxDurableModels.ProviderId, "Faux (offline)", null, [ProviderAuthKind.None], BillingKind.Free);

    /// <summary>The Web Agent's faux models: <c>faux/echo</c>, <c>faux/echo-small</c> and <c>faux/echo-thinking</c>.</summary>
    public static FauxDurableModels CreateModels() => new([FauxDurableModels.Echo, FauxDurableModels.EchoSmall, EchoThinking]);

    private readonly IReadOnlyList<CatalogModel> catalog = [.. models.Models.Select(model => new CatalogModel(FauxDurableModels.ProviderId, model.Id, model.Name)
    {
        Reasoning = model.Reasoning,
        ThinkingLevels = model.Reasoning ? ReasoningLevels : ["off"],
        ContextWindow = (int)model.ContextWindow,
        MaxTokens = (int)model.MaxTokens,
        Input = ["text"],
        Cost = new CatalogCost(0, 0),
    })];

    /// <inheritdoc/>
    public IReadOnlyList<CatalogProvider> Providers { get; } = [Provider];

    /// <inheritdoc/>
    public IReadOnlyList<CatalogModel> Models(string providerId) => providerId == FauxDurableModels.ProviderId ? catalog : [];
}
