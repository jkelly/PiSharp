// Decision 0011 (run W3, SU): what the wizard's view shows (Views/Setup, the mockups' setup.html), built from what the app holds now: the
// providers and accounts of ProviderDirectory (metadata only: a masked hint at most), the environment variables the credentials area read
// at start, the catalog's models of the connected providers, the Defaults and Tools sections, and the app's own versions and address.
// Nothing here is sample data: a count the catalog does not have (Codex models offline, say) is left out instead of made up. The page has
// two states, as the mockup does: default (something connected) and nothing connected (Continue is disabled until a provider connects).
using System.Reflection;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Settings;

namespace PiSharp.WebAgent.Setup;

/// <summary>A step of the wizard, in order. Its name (<see cref="SetupPage.StepName"/>) is the <c>?step=</c> value and the pane's
/// <c>data-setup-step</c>.</summary>
public enum SetupStep
{
    /// <summary>What the app is and where it keeps its data.</summary>
    Welcome,

    /// <summary>Connect a provider: ChatGPT sign-in, an API key, environment variables, a local server.</summary>
    Connect,

    /// <summary>The default model (Provider → Model) and thinking level.</summary>
    Model,

    /// <summary>What was set up.</summary>
    Done,
}

/// <summary>A connected provider in the Connect step's "Connected so far" list.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Account">The account it uses (its active one), or null for a provider that needs no key.</param>
/// <param name="Chip">The kind chip's text: <c>stored key</c>, <c>env · OPENAI_API_KEY</c>, <c>ChatGPT Plus</c>, <c>no key needed</c>.</param>
/// <param name="Icon">The chip's Bootstrap icon.</param>
/// <param name="Tone">The chip's tone (<c>ok</c>, <c>info</c>).</param>
/// <param name="Lead">The detail line's first part: the masked hint, the variable's name, <c>OAuth</c> or <c>No key needed</c>.</param>
/// <param name="LeadMono">Whether <paramref name="Lead"/> is shown in the monospace font (a hint or a variable's name).</param>
/// <param name="Rest">The rest of the detail line (models, expiry, last test), joined with " · "; may be empty.</param>
public sealed record SetupConnection(ProviderEntry Provider, AccountInfo? Account, string Chip, string Icon, string Tone, string Lead, bool LeadMono, string Rest);

/// <summary>An environment chip of the Connect step: a provider key variable, or ambient credentials that were found.</summary>
/// <param name="Name">The variable's name (or what was found, such as <c>AWS credential chain</c>).</param>
/// <param name="Set">Whether the host process has it (the credentials area made a connected environment or ambient account of it).</param>
public sealed record SetupVariable(string Name, bool Set);

/// <summary>The Default model step's starting choice: the saved default when it is a connected provider's model, else the first connected
/// provider's first model, with the saved thinking level (or medium) clamped to the model's levels.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Thinking">The thinking level to check.</param>
public sealed record SetupModelChoice(CatalogProvider Provider, CatalogModel Model, string Thinking);

/// <summary>The versions in the wizard's top bar and footer.</summary>
/// <param name="App">The Web Agent's version.</param>
/// <param name="PiSharp">The PiSharp libraries' version.</param>
/// <param name="DotNet">The .NET runtime's major and minor version, such as <c>10.0</c>.</param>
public sealed record SetupVersions(string App, string PiSharp, string DotNet)
{
    /// <summary>The versions of this process.</summary>
    public static SetupVersions Current { get; } = new(Version(typeof(SetupPage).Assembly), Version(typeof(ThinkingLevels).Assembly),
        $"{Environment.Version.Major}.{Environment.Version.Minor}");

    /// <summary>An assembly's informational version without its source revision (<c>1.1.0.6+abc</c> → <c>1.1.0.6</c>).</summary>
    public static string Version(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }
}

/// <summary>The wizard page (<c>Views/Setup/Index.cshtml</c>).</summary>
public sealed record SetupPage
{
    /// <summary>How many environment chips the Connect step shows before "+ N more" (every variable that is set always shows).</summary>
    public const int VariableChips = 7;

    /// <summary>The thinking level the Default model step starts on when none is saved (clamped to the model's levels).</summary>
    public const string DefaultThinking = "medium";

    // The API key list, the environment chips and the connections start with the catalog's featured providers (RuntimeModelCatalog's
    // order, the mockups' order); the rest follow in directory order, so the lists start the same with or without a catalog (--faux).
    private static readonly Dictionary<string, int> FeaturedOrder =
        RuntimeModelCatalog.FeaturedProviders.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);

    /// <summary>The step shown first (the <c>?step=</c> asked for, unless it is not reachable yet).</summary>
    public required SetupStep Step { get; init; }

    /// <summary>Whether the app runs offline (--faux): no sign-in, no connection test.</summary>
    public required bool Offline { get; init; }

    /// <summary>The connected providers.</summary>
    public required IReadOnlyList<SetupConnection> Connected { get; init; }

    /// <summary>The ChatGPT (openai-codex) provider, for the sign-in card; null when nothing knows it.</summary>
    public ProviderEntry? Codex { get; init; }

    /// <summary>The providers that take an API key, for the Connect step's provider list: one-key providers first, then the cloud accounts
    /// that need more than a key (Azure, Bedrock, Vertex, Cloudflare), each in directory order. Sign-in providers are not listed.</summary>
    public required IReadOnlyList<ProviderEntry> KeyProviders { get; init; }

    /// <summary>The environment chips shown: every variable (or ambient credential) that is set, then unset ones up to <see cref="VariableChips"/>.</summary>
    public required IReadOnlyList<SetupVariable> Variables { get; init; }

    /// <summary>How many unset variables are not shown ("+ N more").</summary>
    public required int MoreVariables { get; init; }

    /// <summary>The Defaults section as saved.</summary>
    public required DefaultsSettings Defaults { get; init; }

    /// <summary>The setup section as saved.</summary>
    public required SetupSettings Setup { get; init; }

    /// <summary>The Default model step's starting choice, or null when no connected provider has a catalog model.</summary>
    public SetupModelChoice? Choice { get; init; }

    /// <summary>The catalog models of the connected providers, less the hidden ones.</summary>
    public required int ModelsAvailable { get; init; }

    /// <summary>The folder new chats work in (the Tools section).</summary>
    public required string Workspace { get; init; }

    /// <summary>The app's data folder.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>The address the browser reached the app at, such as <c>http://127.0.0.1:5180</c>.</summary>
    public required string Origin { get; init; }

    /// <summary>The versions in the top bar and footer.</summary>
    public required SetupVersions Versions { get; init; }

    /// <summary>How many providers PiSharp has built in.</summary>
    public required int BuiltinProviderCount { get; init; }

    /// <summary>The credential modals (the Settings area's API key and ChatGPT sign-in modals).</summary>
    public required CredentialModals Modals { get; init; }

    /// <summary>Whether nothing is connected yet (the mockup's "nothing" state).</summary>
    public bool NothingConnected => Connected.Count == 0;

    /// <summary>The address without its scheme, such as <c>127.0.0.1:5180</c>.</summary>
    public string Host => Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ? uri.Authority : Origin;

    /// <summary>The ids of the connected providers (the Default model step offers only these).</summary>
    public IReadOnlyList<string> ConnectedIds => [.. Connected.Select(connection => connection.Provider.Id)];

    /// <summary>Whether ChatGPT (openai-codex) is connected.</summary>
    public bool CodexConnected => Codex is { IsConnected: true };

    /// <summary>A step's name: <c>welcome</c>, <c>connect</c>, <c>model</c>, <c>done</c>.</summary>
    public static string StepName(SetupStep step) => step.ToString().ToLowerInvariant();

    /// <summary>The step named <paramref name="name"/> (case-insensitive), or null.</summary>
    public static SetupStep? ParseStep(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "welcome" => SetupStep.Welcome,
        "connect" => SetupStep.Connect,
        "model" => SetupStep.Model,
        "done" => SetupStep.Done,
        _ => null,
    };

    /// <summary>The page as it stands now.</summary>
    /// <param name="directory">The providers and their accounts.</param>
    /// <param name="settings">The settings store (Defaults, Tools, Models and setup).</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="modals">The credential modals' settings.</param>
    /// <param name="origin">The address the browser reached the app at.</param>
    /// <param name="dataDirectory">The app's data folder.</param>
    /// <param name="step">The <c>?step=</c> asked for: the Default model step needs a connected provider and the Done step a finished
    /// wizard, so an unreachable step falls back to the one before it.</param>
    /// <param name="now">The time expiries are counted from; default now.</param>
    public static SetupPage Build(ProviderDirectory directory, ISettingsStore settings, IModelCatalog catalog, CredentialModals modals, string origin,
        string dataDirectory, string? step, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(modals);
        var at = now ?? DateTimeOffset.UtcNow;
        var providers = Featured(directory.List());
        var connected = providers.Where(entry => entry.IsConnected).ToList();
        var setup = settings.Get<SetupSettings>();
        var defaults = settings.Get<DefaultsSettings>();
        var hidden = settings.Get<ModelsSettings>();

        var shown = ParseStep(step) ?? SetupStep.Welcome;
        if (shown == SetupStep.Done && !setup.Finished) shown = SetupStep.Model;
        if (shown == SetupStep.Model && connected.Count == 0) shown = SetupStep.Connect;

        var keyProviders = providers.Where(entry => entry.TakesApiKey && !entry.SignsIn && !entry.NoCredentials).ToList();
        var (variables, more) = Chips(providers);
        return new SetupPage
        {
            Step = shown,
            Offline = directory.Offline,
            Connected = [.. connected.Select(entry => Connection(entry, at))],
            Codex = directory.Find(SignInFlows.CodexProvider),
            KeyProviders = [.. keyProviders.Where(entry => entry.KeyForm == ApiKeyForm.Single), .. keyProviders.Where(entry => entry.KeyForm != ApiKeyForm.Single)],
            Variables = variables,
            MoreVariables = more,
            Defaults = defaults,
            Setup = setup,
            Choice = Choose(catalog, connected.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal), defaults),
            ModelsAvailable = connected.Sum(entry => entry.Models.Count(model => !hidden.IsHidden(entry.Id, model.Id))),
            Workspace = settings.Get<ToolsSettings>().EffectiveWorkingDirectory,
            DataDirectory = dataDirectory,
            Origin = origin,
            Versions = SetupVersions.Current,
            BuiltinProviderCount = BuiltinProviders.All.Length,
            Modals = modals,
        };
    }

    /// <summary>The "Connected so far" row of <paramref name="entry"/>.</summary>
    public static SetupConnection Connection(ProviderEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var models = entry.Models.Count > 0 ? SettingsText.Plural(entry.Models.Count, "model") : null;
        // W3 fix: a custom provider says where its key comes from (no key, stored key, env · VARIABLE) and leads with its base URL.
        if (entry.Custom is { } custom)
            return new SetupConnection(entry, entry.Active, custom.Chip, custom.Icon, custom.Tone, custom.BaseUrl.Length > 0 ? custom.BaseUrl : "custom provider",
                custom.BaseUrl.Length > 0, models ?? "");
        if (entry.NoCredentials || entry.Active is not { } account)
            return new SetupConnection(entry, null, "no key needed", "bi-plug", "ok", "No key needed", false, models ?? "");
        var rest = new List<string?> { models };
        string lead;
        var mono = false;
        switch (account.Kind)
        {
            case AccountKind.OAuth:
                lead = "OAuth";
                if (account.ExpiresAt is { } expires)
                    rest.Add(expires > now ? "expires " + SettingsText.Until(expires, now) + ", refreshes automatically" : "refreshes on the next request");
                break;
            case AccountKind.ApiKey:
                lead = account.Hint.Length > 0 ? account.Hint : "Stored settings";
                mono = account.Hint.Length > 0;
                if (account.LastTest is { Ok: true, LatencyMs: { } latency }) rest.Add($"tested {latency} ms");
                break;
            case AccountKind.Environment:
                lead = account.Source ?? account.Label;
                mono = true;
                break;
            default:
                lead = account.Source ?? account.Label;
                break;
        }
        return new SetupConnection(entry, account, SettingsText.KindChip(account), SettingsText.KindIcon(account), SettingsText.KindTone(account), lead, mono,
            string.Join(" · ", rest.Where(part => !string.IsNullOrEmpty(part))));
    }

    // The featured providers first (in that order), then the rest as they came; OrderBy is stable.
    private static List<ProviderEntry> Featured(IEnumerable<ProviderEntry> providers) =>
        [.. providers.OrderBy(entry => FeaturedOrder.TryGetValue(entry.Id, out var at) ? at : int.MaxValue)];

    // Every provider key variable once (featured, then directory order), set when a connected environment account reads it, plus ambient credentials that
    // were found. The set ones all show; unset ones fill up to VariableChips and the rest are counted.
    private static (IReadOnlyList<SetupVariable> Shown, int More) Chips(IReadOnlyList<ProviderEntry> providers)
    {
        var accounts = providers.SelectMany(entry => entry.Accounts).ToList();
        var set = accounts.Where(account => account.Kind == AccountKind.Environment && account.Status == AccountStatus.Connected && account.Source is not null)
            .Select(account => account.Source!).ToHashSet(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var variable in providers.SelectMany(entry => entry.Variables))
            if (variable != CredentialVault.ExcludedVariable && !names.Contains(variable, StringComparer.Ordinal)) names.Add(variable);
        var all = names.Select(name => new SetupVariable(name, set.Contains(name))).ToList();
        foreach (var ambient in accounts.Where(account => account.Kind == AccountKind.Ambient && account.Status == AccountStatus.Connected))
            all.Add(new SetupVariable(ambient.Source ?? ambient.Label, true));
        var shown = all.Where(variable => variable.Set).ToList();
        var unset = all.Where(variable => !variable.Set).ToList();
        var room = Math.Max(0, VariableChips - shown.Count);
        shown.AddRange(unset.Take(room));
        return (shown, Math.Max(0, unset.Count - room));
    }

    // The same choice _ProviderModelSelect starts on (connected providers with catalog models, the saved default when offered, else the
    // first), so the thinking levels the view marks match the model the dropdowns show before any script runs.
    private static SetupModelChoice? Choose(IModelCatalog catalog, IReadOnlySet<string> connected, DefaultsSettings defaults)
    {
        var groups = catalog.Providers.Where(provider => connected.Contains(provider.Id) && catalog.Models(provider.Id).Count > 0).ToList();
        if (groups.Count == 0) return null;
        var provider = groups.FirstOrDefault(group => group.Id == defaults.Provider) ?? groups[0];
        var models = catalog.Models(provider.Id);
        var model = (provider.Id == defaults.Provider ? models.FirstOrDefault(item => item.Id == defaults.Model) : null) ?? models[0];
        return new SetupModelChoice(provider, model, ThinkingLevels.Clamp([.. model.ThinkingLevels], defaults.Thinking ?? DefaultThinking));
    }
}
