// PiSharp-only (decision 0008). This computer's device: when `pisharp durable serve` has made its token, the hub lists it as `local` at the
// address device.json gives (default http://127.0.0.1:4780), reading the token from the user-only file each time it checks, and seeds the
// devices of a configured dpi-network.yaml. Both are read-only in the UI.
using System.Text.Json;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.Web.Hub;

/// <summary>Seeds the local device and the config file's devices.</summary>
public sealed class LocalDevice(HubOptions options, DeviceRegistry registry, ILogger<LocalDevice> logger)
{
    /// <summary>The local device's id.</summary>
    public const string Id = "local";

    /// <summary>Adds or refreshes <c>local</c> and the config file's devices.</summary>
    public void Refresh()
    {
        if (options.ResolvedLocalDeviceDirectory is { } directory && File.Exists(Path.Join(directory, "token")))
        {
            try
            {
                var token = File.ReadAllText(Path.Join(directory, "token")).Trim();
                var (host, port) = ("127.0.0.1", DurableWire.DefaultPort);
                var config = Path.Join(directory, "device.json");
                if (File.Exists(config))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(config));
                    if (json.RootElement.TryGetProperty("port", out var portValue) && portValue.TryGetInt32(out var configured) && configured > 0) port = configured;
                    if (json.RootElement.TryGetProperty("host", out var hostValue) && hostValue.GetString() is { Length: > 0 } bound && bound is not ("0.0.0.0" or "::"))
                        host = bound;
                }
                if (registry.Get(Id) is { Source: not "local" }) return;
                registry.Upsert(Id, Id, "local", $"http://{(host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host)}:{port}", "local", token);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                logger.LogWarning(failure, "Could not read the local device in {Directory}.", directory);
            }
        }
        if (options.ConfigFile is { Length: > 0 } file && File.Exists(file))
        {
            try
            {
                var declared = NetworkFile.Parse(File.ReadAllText(file));
                foreach (var device in declared)
                    if (registry.Get(device.Id) is null or { Source: "config" }) registry.Upsert(device.Id, device.Name, device.Kind, device.Url, "config", null);
            }
            catch (Exception failure) when (failure is IOException or FormatException or ArgumentException)
            {
                logger.LogWarning(failure, "Could not read {File}.", file);
            }
        }
    }
}
