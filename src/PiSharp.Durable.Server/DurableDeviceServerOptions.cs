namespace PiSharp.Durable.Server;

/// <summary>How a device serves its API.</summary>
public sealed record DurableDeviceServerOptions
{
    /// <summary>The device token; every <c>/v1</c> request must present it as <c>Authorization: Bearer</c> or <c>X-DPI-Token</c>.</summary>
    public required string Token { get; init; }

    /// <summary>The largest JSON body.</summary>
    public int MaxJsonBytes { get; init; } = 256 * 1024;

    /// <summary>How often an idle stream sends <c>: ping</c>.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Unsent bytes after which a slow viewer's stream is closed; it reconnects to a fresh snapshot.</summary>
    public long MaxStreamBacklogBytes { get; init; } = 2_000_000;

    /// <summary>How long device and session streams gather changes before sending.</summary>
    public TimeSpan Coalesce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long agent streams gather document changes before sending.</summary>
    public TimeSpan DocumentCoalesce { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The longest wait a request may ask for (<c>waitMs</c>).</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Hears failures of stream updates, which a stream survives.</summary>
    public Action<Exception>? OnError { get; init; }
}
