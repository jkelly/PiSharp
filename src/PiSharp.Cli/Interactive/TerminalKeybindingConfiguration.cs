using PiSharp.Tui.Input;

namespace PiSharp.Cli.Interactive;

/// <summary>One immutable startup configuration. Reload returns a new owner configuration.</summary>
public sealed class TerminalKeybindingConfiguration
{
    private readonly KeyValuePair<string, TerminalKeybindingDefinition>[] definitions;
    private readonly KeyValuePair<string, TerminalKeybindingValue?>[] bindings;
    private readonly Func<TerminalKeybindingConfiguration> reload;
    internal TerminalKeybindingConfiguration(string agentDirectory, string configPath, string platform, string loadStatus,
        bool migrated, IEnumerable<KeyValuePair<string, TerminalKeybindingDefinition>> definitions,
        IEnumerable<KeyValuePair<string, TerminalKeybindingValue?>> bindings, Func<TerminalKeybindingConfiguration> reload)
    {
        AgentDirectory = agentDirectory; ConfigPath = configPath; Platform = platform; LoadStatus = loadStatus; Migrated = migrated;
        this.definitions = definitions.ToArray(); this.bindings = bindings.ToArray(); this.reload = reload;
    }
    public string AgentDirectory { get; }
    public string ConfigPath { get; }
    public string Platform { get; }
    public string LoadStatus { get; }
    public bool Migrated { get; }
    private TerminalDoubleEscapeAction? doubleEscapeOverride;
    public TerminalNavigationSettings? NavigationSettings { get; private init; }
    public string DoubleEscapeSource => doubleEscapeOverride is not null ? "explicit" : NavigationSettings?.Source ?? "default";
    public TerminalDoubleEscapeAction DoubleEscapeAction
    {
        get => doubleEscapeOverride ?? NavigationSettings?.Action ?? TerminalDoubleEscapeAction.Tree;
        init
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            doubleEscapeOverride = value;
        }
    }
    internal TerminalKeybindingConfiguration WithNavigationSettings(TerminalNavigationSettings settings) =>
        Copy(this, settings);
    private TerminalKeybindingConfiguration Copy(TerminalKeybindingConfiguration bindings, TerminalNavigationSettings? settings) =>
        new(bindings.AgentDirectory, bindings.ConfigPath, bindings.Platform, bindings.LoadStatus, bindings.Migrated,
            bindings.definitions, bindings.bindings, bindings.reload)
        { doubleEscapeOverride = this.doubleEscapeOverride, NavigationSettings = settings };
    public TerminalKeybindings CreateBindings(bool kittyProtocolActive = false) => new(definitions,
        (raw, key) => TerminalInputDecoder.MatchesRawKey(raw, key, kittyActive: kittyProtocolActive), bindings);
    public TerminalKeybindingConfiguration Reload()
    {
        var next = reload();
        return Copy(next, NavigationSettings?.Reload());
    }
}
