// Decision 0011 (run W3, SU): what the first-run wizard and its redirect decide on. "Connected" is the Settings area's word for a provider
// (ProviderDirectory: a usable stored key or sign-in, an environment variable or ambient credentials, or a provider that needs no key such
// as the faux one, or a routed custom provider whose key source is usable: none, a stored key or a set variable; W3 fix), so the wizard,
// Settings › Providers and Settings › Defaults always agree on it. The redirect applies only while nothing is connected and the wizard was
// never finished or skipped, and never with --faux (the faux provider counts as connected anyway, and an offline app must not be forced
// through a wizard it cannot complete online). Finishing saves the default model through ISettingsStore's Defaults section, exactly as
// Settings › Defaults does, and never changes an account or a chat.
using PiSharp.AI;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Settings;

namespace PiSharp.WebAgent.Setup;

/// <summary>The default model the wizard's last step saves.</summary>
/// <param name="Provider">The provider id, or null to leave the defaults as they are.</param>
/// <param name="Model">The model id of <paramref name="Provider"/>.</param>
/// <param name="Thinking">The thinking level (clamped to what the model supports), or null for the model's default.</param>
/// <param name="UseAsDefault">Whether to save the choice as the Defaults section ("Use as default in Chat and Playground").</param>
public sealed record SetupChoice(string? Provider, string? Model, string? Thinking, bool UseAsDefault);

/// <summary>What <see cref="SetupState.FinishAsync"/> stored.</summary>
/// <param name="Setup">The setup section now.</param>
/// <param name="Defaults">The Defaults section now (unchanged when the choice was not used as the default).</param>
/// <param name="SavedDefaults">Whether the Defaults section was written.</param>
public sealed record SetupFinished(SetupSettings Setup, DefaultsSettings Defaults, bool SavedDefaults);

/// <summary>The first-run state: whether anything is connected, whether the wizard ended, and the two ways it ends. A singleton; every
/// answer is read fresh (accounts and settings change while the app runs).</summary>
/// <param name="directory">The Settings area's providers (connected or not).</param>
/// <param name="settings">The settings store (the setup and Defaults sections).</param>
/// <param name="catalog">The model catalog (the default model must be one of its models).</param>
/// <param name="options">The app's options (--faux).</param>
/// <param name="time">The clock of the outcome's time; default <see cref="TimeProvider.System"/>.</param>
public sealed class SetupState(ProviderDirectory directory, ISettingsStore settings, IModelCatalog catalog, WebAgentOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>Whether the app runs offline on faux models (<c>--faux</c>): the wizard is reachable but never forced.</summary>
    public bool Offline => options.Faux;

    /// <summary>The setup section as stored now.</summary>
    public SetupSettings Current => settings.Get<SetupSettings>();

    /// <summary>The connected providers, in directory order.</summary>
    public IReadOnlyList<ProviderEntry> Connected() => [.. directory.List().Where(entry => entry.IsConnected)];

    /// <summary>Whether any provider is connected.</summary>
    public bool HasConnection() => directory.List().Any(entry => entry.IsConnected);

    /// <summary>Whether <c>/</c> and <c>/chat/new</c> send the user to <c>/setup</c> now: not offline, the wizard never finished or skipped,
    /// and nothing connected.</summary>
    public bool RedirectsToSetup() => !Offline && !Current.Done && !HasConnection();

    /// <summary>Records that the user skipped the wizard; the redirect ends whether or not anything is connected.</summary>
    public Task<SetupSettings> SkipAsync(CancellationToken cancellationToken = default) =>
        settings.SaveAsync(SetupSettings.Ended(SetupOutcome.Skipped, clock.GetUtcNow()), cancellationToken);

    /// <summary>Finishes the wizard: saves <paramref name="choice"/> as the default model when it asks to (through the Defaults section, as
    /// Settings › Defaults does), then records the outcome. A chat that already exists keeps its own model and account.</summary>
    /// <exception cref="InvalidOperationException">Nothing is connected yet (the wizard's Connect step comes first).</exception>
    /// <exception cref="ArgumentException">The choice names only one of provider and model, a provider that is not connected, a model the
    /// catalog does not list, or an unknown thinking level.</exception>
    public async Task<SetupFinished> FinishAsync(SetupChoice choice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var connected = Connected();
        if (connected.Count == 0) throw new InvalidOperationException("Connect a provider first: setup finishes once something is connected.");
        var defaults = settings.Get<DefaultsSettings>();
        var saved = false;
        if (choice.UseAsDefault && CheckChoice(choice, connected) is { } wanted)
        {
            defaults = await settings.SaveAsync(wanted, cancellationToken).ConfigureAwait(false);
            saved = true;
        }
        var setup = await settings.SaveAsync(SetupSettings.Ended(SetupOutcome.Finished, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return new SetupFinished(setup, defaults, saved);
    }

    /// <summary>The Defaults section <paramref name="choice"/> asks for, or null when it names no model; the thinking level is clamped to
    /// the model's levels.</summary>
    private DefaultsSettings? CheckChoice(SetupChoice choice, IReadOnlyList<ProviderEntry> connected)
    {
        var asked = SettingsEndpoints.CheckDefaults(new DefaultsBody(choice.Provider, choice.Model, choice.Thinking), catalog);
        if (asked.Provider is not { } provider) return null;
        if (!connected.Any(entry => entry.Id == provider))
            throw new ArgumentException($"{catalog.FindProvider(provider)?.DisplayName ?? provider} is not connected yet: connect it, or choose a connected provider.");
        var model = catalog.FindModel(provider, asked.Model!)!;
        var thinking = asked.Thinking is { } level ? ThinkingLevels.Clamp([.. model.ThinkingLevels], level) : null;
        return asked with { Thinking = thinking };
    }
}
