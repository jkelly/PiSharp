// Decision 0011 (run W2, WU6): the Web Agent's real catalog, from PiSharp.ModelRuntime's built-in catalog (no models.json, no persisted or
// remote catalogs, no extension providers, no llama.cpp): every built-in chat model whose API has a live route, which is exactly what
// WebDurableModels.GetModel answers, so a chat started from this list always resolves and a reopened chat is routed to the same model. The
// registry behind it is credential-blind (it reads no environment variable and no key); credentials are the accounts', per request
// (AccountRouter). Providers are listed with the mockups' featured ones first (OpenAI Codex, Anthropic, OpenAI, Google, GitHub Copilot, ...),
// then the rest by name. Anthropic signs in with an API key (or Amazon Bedrock) only: no subscription sign-in is listed for it, nor for any
// provider other than OpenAI Codex (ChatGPT) and GitHub Copilot, the two the credentials area can sign in to.
//
// W3, CP: with the custom providers area, the catalog also reads the data folder's models.json, through the store's sanitized routes copy
// (ICustomProviderSource): model overrides change built-in models, and each routed custom provider follows the built-in ones, in file
// order, with its models. A custom provider is Free when its base URL is on this machine, Metered when a price is entered, otherwise
// Unknown (a zero price elsewhere means nobody entered one, so its models carry no cost). The catalog is rebuilt, as a new immutable
// snapshot, when the file or a custom provider's vault accounts change. With --faux none of this is registered.
using System.Collections.Frozen;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Durable;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.CustomProviders;
using AuthKind = PiSharp.WebAgent.Contracts.ProviderAuthKind;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;
using NodeObject = System.Text.Json.Nodes.JsonObject;
using NodeValue = System.Text.Json.Nodes.JsonValue;

namespace PiSharp.WebAgent.Routing;

/// <summary>The providers and chat models of PiSharp.ModelRuntime's built-in catalog (and the custom providers of models.json) that the Web
/// Agent can route. A singleton; each build is immutable and replaced whole, so safe from many threads.</summary>
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

    /// <summary>The built-in catalog without models.json (the base values model overrides change), built once.</summary>
    private static readonly Lazy<RuntimeModelCatalog> BuiltIn = new(() => new RuntimeModelCatalog(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ICustomProviderSource? custom;
    private readonly IAccountStore? accounts;
    private readonly object rebuild = new();
    private long accountChanges;
    private volatile Built current;

    /// <summary>The catalog of the built-in shards (credential-blind: no environment, no key, no file, no network).</summary>
    public RuntimeModelCatalog() : this(ModelRegistry.Create(new ModelRegistryOptions()))
    {
    }

    /// <summary>The catalog of <paramref name="registry"/>'s routable chat models.</summary>
    internal RuntimeModelCatalog(ModelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        current = new Built(registry, null, null, 0, 0);
    }

    /// <summary>
    /// The built-in catalog with the custom providers and model overrides of <paramref name="custom"/> (W3, CP): read from its sanitized
    /// routes copy, rebuilt when the file changes. <paramref name="accounts"/> tells which custom providers keep a key in the vault. Without
    /// <paramref name="custom"/> this is the built-in catalog.
    /// </summary>
    public RuntimeModelCatalog(ICustomProviderSource? custom, IAccountStore? accounts = null)
    {
        this.custom = custom;
        this.accounts = accounts;
        if (accounts is not null) accounts.Changed += OnAccountChanged;
        current = custom is null ? BuiltIn.Value.current : Build(custom.Current, 0);
    }

    /// <summary>The built-in catalog shared by every instance (no models.json): the values a model override changes.</summary>
    internal static RuntimeModelCatalog BuiltInOnly => BuiltIn.Value;

    private void OnAccountChanged(object? sender, AccountChangedEventArgs change)
    {
        if (!CustomProviderJson.IsBuiltin(change.Provider)) Interlocked.Increment(ref accountChanges);
    }

    private Built Snapshot
    {
        get
        {
            var built = current;
            if (custom is null) return built;
            var snapshot = custom.Current;
            var changes = Interlocked.Read(ref accountChanges);
            if (built.Version == snapshot.Version && built.AccountChanges == changes) return built;
            lock (rebuild)
            {
                built = current;
                if (built.Version == snapshot.Version && built.AccountChanges == changes) return built;
                current = built = Build(snapshot, changes);
                return built;
            }
        }
    }

    private Built Build(CustomProviderSnapshot snapshot, long changes)
    {
        ModelRegistry? registry = null;
        if (snapshot.RoutesPath is { } path)
        {
            registry = ModelRegistry.Create(new ModelRegistryOptions { ModelsPath = path, RunCommand = _ => null });
            if (registry.GetError() is not null) registry = null;
        }
        return new Built(registry ?? ModelRegistry.Create(new ModelRegistryOptions()), registry is null ? null : snapshot, accounts, snapshot.Version, changes);
    }

    /// <inheritdoc/>
    public IReadOnlyList<CatalogProvider> Providers => Snapshot.Providers;

    /// <inheritdoc/>
    public IReadOnlyList<CatalogModel> Models(string providerId) => providerId is not null && Snapshot.ModelsBy.TryGetValue(providerId, out var list) ? list : [];

    /// <inheritdoc/>
    public CatalogProvider? FindProvider(string providerId) => providerId is not null && Snapshot.ProvidersById.TryGetValue(providerId, out var provider) ? provider : null;

    /// <inheritdoc/>
    public CatalogModel? FindModel(string providerId, string modelId) => Find(providerId, modelId)?.Model;

    /// <summary>The routed custom provider <paramref name="providerId"/> as the catalog lists it, or null for a built-in or unknown one.</summary>
    internal CustomProviderInfo? Custom(string? providerId) =>
        providerId is not null && Snapshot.Customs.TryGetValue(providerId, out var provider) ? provider : null;

    /// <summary>The version of the custom providers' file this catalog was last built from (0: built-ins only).</summary>
    internal long CustomVersion => Snapshot.Version;

    /// <summary>Every routable model, in provider then catalog order.</summary>
    internal IEnumerable<Row> Rows
    {
        get
        {
            var built = Snapshot;
            return built.Providers.SelectMany(provider => built.ModelsBy[provider.Id].Select(model => built.Rows[(provider.Id, model.Id)]));
        }
    }

    /// <summary>The routable model <paramref name="modelId"/> of <paramref name="providerId"/>, or null.</summary>
    internal Row? Find(string? providerId, string? modelId) =>
        providerId is not null && modelId is not null && Snapshot.Rows.TryGetValue((providerId, modelId), out var row) ? row : null;

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

    private static CatalogModel ToCatalogModel(RegistryModel entry, CustomProviderInfo? custom = null)
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
            // A custom model's zero price is a price only on this machine; elsewhere it means nobody entered one (unknown).
            Cost = json["cost"] is NodeObject cost && (custom is null || custom.Loopback || Priced(cost))
                ? new CatalogCost(Number(cost, "input"), Number(cost, "output"), Number(cost, "cacheRead"), Number(cost, "cacheWrite"))
                : null,
        };
    }

    private static bool Priced(NodeObject cost) => Number(cost, "input") > 0 || Number(cost, "output") > 0 || Number(cost, "cacheRead") > 0 || Number(cost, "cacheWrite") > 0;

    private static int Tokens(double value) => double.IsFinite(value) ? (int)Math.Clamp(value, 0, int.MaxValue) : 0;

    private static double Number(NodeObject value, string name) =>
        value[name] is NodeValue scalar && scalar.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : 0;

    private static CatalogProvider Describe(string id) => new(id, DefaultName(id), ProviderLogos.Key(id), AuthKinds(id),
        SignIns.ContainsKey(id) ? BillingKind.Subscription : BillingKind.Metered);

    /// <summary>A custom provider as the catalog lists it: its name and logo, an API key when the vault holds one (else no credentials:
    /// an environment reference or none), and Free on this machine, Metered with a price entered, otherwise Unknown.</summary>
    private static CatalogProvider DescribeCustom(CustomProviderInfo provider, IReadOnlyList<Row> rows, IAccountStore? accounts)
    {
        var stored = provider.FileKey == CustomKeySource.None && accounts?.Active(provider.Id) is { Kind: AccountKind.ApiKey };
        var billing = provider.Loopback ? BillingKind.Free : rows.Any(row => row.Model.Cost is not null) ? BillingKind.Metered : BillingKind.Unknown;
        return new CatalogProvider(provider.Id, provider.Name, ProviderLogos.Key(provider.Id), [stored ? AuthKind.ApiKey : AuthKind.None], billing);
    }

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
    /// <summary>One immutable build: the built-in providers (featured first, then by name), then the routed custom ones in file order.</summary>
    private sealed class Built
    {
        public Built(ModelRegistry registry, CustomProviderSnapshot? custom, IAccountStore? accounts, long version, long accountChanges)
        {
            Version = version;
            AccountChanges = accountChanges;
            var customs = (custom?.Providers ?? []).Where(provider => provider.Routed).ToDictionary(provider => provider.Id, StringComparer.Ordinal);
            var byProvider = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            var all = new Dictionary<(string, string), Row>();
            foreach (var entry in registry.GetAll())
            {
                var isCustom = customs.TryGetValue(entry.Provider, out var owner);
                if (!isCustom && custom?.Find(entry.Provider) is not null) continue;
                if (!Routable(entry) || all.ContainsKey((entry.Provider, entry.Id))) continue;
                var row = new Row(entry, ToCatalogModel(entry, owner), ToDurableModel(entry));
                all[(entry.Provider, entry.Id)] = row;
                if (!byProvider.TryGetValue(entry.Provider, out var list)) byProvider[entry.Provider] = list = [];
                list.Add(row);
            }
            var listed = byProvider.Keys.Where(id => !customs.ContainsKey(id)).Select(Describe)
                .OrderBy(provider => FeaturedRank.GetValueOrDefault(provider.Id, int.MaxValue))
                .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(provider => provider.Id, StringComparer.Ordinal)
                .ToList();
            foreach (var provider in custom?.Providers ?? [])
                if (customs.ContainsKey(provider.Id) && byProvider.TryGetValue(provider.Id, out var rows)) listed.Add(DescribeCustom(provider, rows, accounts));
            Providers = listed;
            ProvidersById = listed.ToFrozenDictionary(provider => provider.Id, StringComparer.Ordinal);
            ModelsBy = byProvider.ToFrozenDictionary(pair => pair.Key, pair => (IReadOnlyList<CatalogModel>)[.. pair.Value.Select(row => row.Model)], StringComparer.Ordinal);
            Rows = all.ToFrozenDictionary();
            Customs = customs.Where(pair => byProvider.ContainsKey(pair.Key)).ToFrozenDictionary(StringComparer.Ordinal);
        }

        public long Version { get; }

        public long AccountChanges { get; }

        public IReadOnlyList<CatalogProvider> Providers { get; }

        public FrozenDictionary<string, CatalogProvider> ProvidersById { get; }

        public FrozenDictionary<string, IReadOnlyList<CatalogModel>> ModelsBy { get; }

        public FrozenDictionary<(string Provider, string Id), Row> Rows { get; }

        public FrozenDictionary<string, CustomProviderInfo> Customs { get; }
    }
}
