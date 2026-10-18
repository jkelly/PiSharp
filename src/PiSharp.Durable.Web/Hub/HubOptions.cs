namespace PiSharp.Durable.Web.Hub;

/// <summary>Where the hub keeps its registry and which devices it seeds (configuration section <c>Hub</c>).</summary>
public sealed class HubOptions
{
    /// <summary>The registry, the Data Protection keys and the UI state; default <c>%LOCALAPPDATA%\PiSharpDurableWeb</c>.</summary>
    public string DataDirectory { get; set; } = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiSharpDurableWeb");

    /// <summary>A <c>dpi-network.yaml</c> whose devices are added at start, read-only in the UI.</summary>
    public string? ConfigFile { get; set; }

    /// <summary>This computer's device directory (<c>pisharp durable serve</c>); its device appears as <c>local</c> when it has a token.
    /// Default <c>~/.pi/agent/experimental/durable-device</c>; empty turns it off.</summary>
    public string? LocalDeviceDirectory { get; set; }

    /// <summary>Host names the hub answers besides <c>localhost</c>, <c>127.0.0.1</c> and <c>[::1]</c>.</summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>How long a proxied device request may take.</summary>
    public TimeSpan ProxyTimeout { get; set; } = TimeSpan.FromSeconds(330);

    /// <summary>The local device directory to use: the configured one, or the default under the user's home.</summary>
    public string? ResolvedLocalDeviceDirectory => LocalDeviceDirectory switch
    {
        null => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "experimental", "durable-device"),
        "" => null,
        var directory => directory,
    };
}
