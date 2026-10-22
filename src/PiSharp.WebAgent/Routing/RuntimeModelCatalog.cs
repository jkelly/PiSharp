// Decision 0011 (run W2, WU6): the Web Agent's real catalog, from PiSharp.ModelRuntime's built-in catalog (no models.json, no persisted or
// remote catalogs, no extension providers, no llama.cpp): every built-in chat model whose API has a live route, which is exactly what
// WebDurableModels.GetModel answers, so a chat started from this list always resolves and a reopened chat is routed to the same model. The
// registry behind it is credential-blind (it reads no environment variable and no key); credentials are the accounts', per request
// (AccountRouter). Providers are listed with the mockups' featured ones first (OpenAI Codex, Anthropic, OpenAI, Google, GitHub Copilot, ...),
// then the rest by name. Anthropic signs in with an API key (or Amazon Bedrock) only: no subscription sign-in is listed for it, nor for any
// provider other than OpenAI Codex (ChatGPT) and GitHub Copilot, the two the credentials area can sign in to.
using System.Collections.Frozen;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Durable;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using AuthKind = PiSharp.WebAgent.Contracts.ProviderAuthKind;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;
using NodeObject = System.Text.Json.Nodes.JsonObject;
using NodeValue = System.Text.Json.Nodes.JsonValue;

namespace PiSharp.WebAgent.Routing;

/// <summary>The providers and chat models of PiSharp.ModelRuntime's built-in catalog that the Web Agent can route. A singleton; immutable
/// once built, so safe from many threads.</summary>
public sealed class RuntimeModelCatalog : IModelCatalog
{
    /// <summary>The providers listed first, in this order (the settings mockup's order); the others follow by display name.</summary>
    public static IReadOnlyList<string> FeaturedProviders { get; } =
    [
        "openai-codex", "anthropic", "openai", "google", "github-copilot", "xai", "amazon-bedrock", "azure", "google-vertex",
        "cloudflare-workers-ai", "cloudflare-ai-gateway", "openrouter", "deepseek", "mistral", "kimi-coding",
    ];

    private static readonly FrozenDictionary<string, int> FeaturedRank =
        FeaturedProviders.Select((id, index) => KeyValuePair.Create(id, index)).ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The providers that sign in (a subscription), and how: OpenAI Codex with a ChatGPT browser sign-in, GitHub Copilot with a
    /// device code. Every other subscription sign-in of the built-in catalog (Anthropic's included) is not offered.</summary>
    private static readonly FrozenDictionary<string, AuthKind> SignIns = new Dictionary<string, AuthKind>(StringComparer.Ordinal)
    {
        ["openai-codex"] = AuthKind.OAuth,
        ["github-copilot"] = AuthKind.DeviceCode,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>One routable model: its registry entry, its catalog row and its durable model.</summary>
    internal sealed record Row(RegistryModel Entry, CatalogModel Model, DurableModel Durable);

    private readonly FrozenDictionary<string, IReadOnlyList<CatalogModel>> models;
    private readonly FrozenDictionary<string, CatalogProvider> providersById;
    private readonly FrozenDictionary<(string Provider, string Id), Row> rows;

    /// <summary>The catalog of the built-in shards (credential-blind: no environment, no key, no file, no network).</summary>
    public RuntimeModelCatalog() : this(ModelRegistry.Create(new ModelRegistryOptions()))
    {
    }

    /// <summary>The catalog of <paramref name="registry"/>'s routable chat models.</summary>
    internal RuntimeModelCatalog(ModelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var byProvider = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        var all = new Dictionary<(string, string), Row>();
        foreach (var entry in registry.GetAll())
        {
            if (!Routable(entry) || all.ContainsKey((entry.Provider, entry.Id))) continue;
            var row = new Row(entry, ToCatalogModel(entry), ToDurableModel(entry));
            all[(entry.Provider, entry.Id)] = row;
            if (!byProvider.TryGetValue(entry.Provider, out var list)) byProvider[entry.Provider] = list = [];
            list.Add(row);
        }
        var listed = byProvider.Keys.Select(Describe)
            .OrderBy(provider => FeaturedRank.GetValueOrDefault(provider.Id, int.MaxValue))
            .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.Id, StringComparer.Ordinal)
            .ToList();
        Providers = listed;
        providersById = listed.ToFrozenDictionary(provider => provider.Id, StringComparer.Ordinal);
        models = byProvider.ToFrozenDictionary(pair => pair.Key, pair => (IReadOnlyList<CatalogModel>)[.. pair.Value.Select(row => row.Model)], StringComparer.Ordinal);
        rows = all.ToFrozenDictionary();
    }

    /// <inheritdoc/>
    public IReadOnlyList<CatalogProvider> Providers { get; }

    /// <inheritdoc/>
    public IReadOnlyList<CatalogModel> Models(string providerId) => providerId is not null && models.TryGetValue(providerId, out var list) ? list : [];

    /// <inheritdoc/>
    public CatalogProvider? FindProvider(string providerId) => providerId is not null && providersById.TryGetValue(providerId, out var provider) ? provider : null;

    /// <inheritdoc/>
    public CatalogModel? FindModel(string providerId, string modelId) => Find(providerId, modelId)?.Model;

    /// <summary>Every routable model, in provider then catalog order.</summary>
    internal IEnumerable<Row> Rows => Providers.SelectMany(provider => Models(provider.Id).Select(model => rows[(provider.Id, model.Id)]));

    /// <summary>The routable model <paramref name="modelId"/> of <paramref name="providerId"/>, or null.</summary>
    internal Row? Find(string? providerId, string? modelId) =>
        providerId is not null && modelId is not null && rows.TryGetValue((providerId, modelId), out var row) ? row : null;

    /// <summary>The name shown for <paramref name="providerId"/> (its id when unknown).</summary>
    internal string DisplayName(string providerId) => FindProvider(providerId)?.DisplayName ?? DefaultName(providerId);

    /// <summary>Whether <paramref name="entry"/> is a chat model the Web Agent routes: not virtual, with a live route for its API.</summary>
    internal static bool Routable(RegistryModel entry) =>
        entry.Type == CatalogModelType.Chat && !VirtualModels.IsVirtual(entry) && LiveSessionSelection.SupportedApi(entry.Provider, entry.Api);

    /// <summary>A registry entry as a pi-ai model carrying its JSON (PiSharp.Cli's DurableModels.ToModel).</summary>
    internal static DurableModel ToDurableModel(RegistryModel entry)
    {
        var wire = ChordJson.Parse(entry.ToJsonString()) as ChordObject;
        return new DurableModel
        {
            Id = entry.Id, Name = entry.Name ?? entry.Id, Api = entry.Api, Provider = entry.Provider, BaseUrl = entry.BaseUrl,
            Reasoning = entry.Reasoning, Input = entry.Input.IsDefaultOrEmpty ? ["text"] : entry.Input,
            ContextWindow = entry.ContextWindow, MaxTokens = entry.MaxTokens,
            InputLimits = DurableModelInputLimits.FromJson(wire is not null && wire.TryGetValue("inputLimits", out var limits) ? limits : null),
            Wire = wire,
        };
    }

    private static CatalogModel ToCatalogModel(RegistryModel entry)
    {
        var json = entry.CloneJson();
        var levels = VirtualModels.SupportedThinkingLevels(entry);
        return new CatalogModel(entry.Provider, entry.Id, entry.Name ?? entry.Id)
        {
            Reasoning = entry.Reasoning,
            ThinkingLevels = levels.IsDefaultOrEmpty ? ["off"] : [.. levels],
            ContextWindow = Tokens(entry.ContextWindow),
            MaxTokens = Tokens(entry.MaxTokens),
            Input = entry.Input.IsDefaultOrEmpty ? ["text"] : [.. entry.Input],
            Cost = json["cost"] is NodeObject cost
                ? new CatalogCost(Number(cost, "input"), Number(cost, "output"), Number(cost, "cacheRead"), Number(cost, "cacheWrite"))
                : null,
        };
    }

    private static int Tokens(double value) => double.IsFinite(value) ? (int)Math.Clamp(value, 0, int.MaxValue) : 0;

    private static double Number(NodeObject value, string name) =>
        value[name] is NodeValue scalar && scalar.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : 0;

    private static CatalogProvider Describe(string id) => new(id, DefaultName(id), ProviderLogos.Key(id), AuthKinds(id),
        SignIns.ContainsKey(id) ? BillingKind.Subscription : BillingKind.Metered);

    private static string DefaultName(string id) => id == "openai-codex" ? "OpenAI Codex"
        : BuiltinProviders.All.FirstOrDefault(provider => provider.Id == id)?.Name ?? id;

    /// <summary>How <paramref name="id"/> takes credentials here, preferred first: its sign-in (OpenAI Codex, GitHub Copilot), an API key
    /// (not for a sign-in-only provider, nor Copilot, whose key is a GitHub token from its sign-in), its environment variables (never
    /// <c>ANTHROPIC_OAUTH_TOKEN</c>) and ambient credentials (Amazon Bedrock, Google Vertex AI).</summary>
    private static IReadOnlyList<AuthKind> AuthKinds(string id)
    {
        var kinds = new List<AuthKind>();
        if (SignIns.TryGetValue(id, out var signIn)) kinds.Add(signIn);
        if (!SignIns.ContainsKey(id) && !CredentialVault.SignInOnly(id)) kinds.Add(AuthKind.ApiKey);
        if ((ProviderEnvironmentKeys.GetApiKeyVariables(id) ?? []).Any(variable => variable != CredentialVault.ExcludedVariable)) kinds.Add(AuthKind.Environment);
        if (id is "amazon-bedrock" or "google-vertex") kinds.Add(AuthKind.Ambient);
        return kinds;
    }
}
