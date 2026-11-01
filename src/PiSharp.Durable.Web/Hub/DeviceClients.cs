using PiSharp.Durable.Client;

namespace PiSharp.Durable.Web.Hub;

/// <summary>Clients for the hub's own calls to devices (page and panel rendering), with each device's token.</summary>
public sealed class DeviceClients(DeviceRegistry registry)
{
    /// <summary>A client for <paramref name="deviceId"/>; dispose it.</summary>
    /// <exception cref="DurableApiException">No such device, or no token.</exception>
    public DurableDeviceClient For(string deviceId)
    {
        var device = registry.Get(deviceId) ?? throw new DurableApiException(DurableErrorCodes.NotFound, $"No device {deviceId}.", 404);
        var token = registry.Token(deviceId) ?? throw new DurableApiException(DurableErrorCodes.Unauthorized, $"No token for {deviceId}.", 401);
        return new DurableDeviceClient(new Uri(device.Url), token);
    }

    /// <summary>A client for an address and token that are not stored yet (adding a device).</summary>
    public static DurableDeviceClient For(string url, string token) => new(new Uri(url), token);
}
