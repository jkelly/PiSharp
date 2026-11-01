// Decision 0011 (run W3, S0): the priced faux switch. --faux-metered keeps --faux fully offline (FauxDurableModels, no provider code path)
// but makes the faux provider Metered at a list price ($3 input, $15 output, $0.30 cache read, $3.75 cache write per million tokens) and
// makes its answers report usage, with tokens worked out from text length (a token per 4 characters, rounded up: the request's messages
// as JSON for input, the answer's text, thinking and tool calls for output). So the Spend Guard, its ledger and its pages can be exercised
// and tested without a provider. Plain --faux is unchanged: free, zero usage.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Faux;

/// <summary>The priced faux provider's list price, its token counting, and its registration.</summary>
public static class FauxPricing
{
    /// <summary>The list price of every faux model with <c>--faux-metered</c>, per million tokens.</summary>
    public static CatalogCost Price { get; } = new(3, 15, 0.30, 3.75);

    /// <summary>The tokens of <paramref name="text"/>: one per 4 characters, rounded up; 0 for none.</summary>
    public static int Tokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Min(int.MaxValue, (text.Length + 3L) / 4);

    /// <summary>pi-ai usage of <paramref name="input"/> input and <paramref name="output"/> output tokens at <paramref name="price"/>, with
    /// the cost of each part and the total (US dollars).</summary>
    public static TokenUsage Usage(int input, int output, CatalogCost price)
    {
        ArgumentNullException.ThrowIfNull(price);
        var inputCost = (decimal)input * (decimal)price.Input / 1_000_000m;
        var outputCost = (decimal)output * (decimal)price.Output / 1_000_000m;
        return new TokenUsage(input, output, 0, 0, input + output, new UsageCost(inputCost, outputCost, 0, 0, inputCost + outputCost));
    }

    /// <summary>With <see cref="WebAgentOptions.FauxMetered"/>, replaces the faux models and catalog the core's fallbacks registered with
    /// priced ones (<see cref="MeteredFauxDurableModels"/>, a Metered <see cref="FauxModelCatalog"/>); otherwise changes nothing. Called by
    /// the Spend area before its guard decorates <see cref="IDurableModels"/>.</summary>
    public static IServiceCollection AddMeteredFaux(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.FauxMetered) return services;
        services.TryAddSingleton(_ => FauxModelCatalog.CreateModels());
        services.RemoveAll<IDurableModels>();
        services.RemoveAll<IModelCatalog>();
        services.TryAddSingleton(provider => new MeteredFauxDurableModels(provider.GetRequiredService<FauxDurableModels>(), Price));
        services.AddSingleton<IDurableModels>(provider => provider.GetRequiredService<MeteredFauxDurableModels>());
        services.AddSingleton<IModelCatalog>(provider => new FauxModelCatalog(provider.GetRequiredService<FauxDurableModels>(), Price));
        return services;
    }
}
