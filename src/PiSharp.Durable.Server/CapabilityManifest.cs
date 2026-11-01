// PiSharp-only (decision 0010). The capability manifest, contracts/durable-capabilities.json, embedded at build time: every application
// feature with its version and its bindings (routes, client methods, CLI commands, browser pages) or the reason a binding is missing.
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.Server;

/// <summary>The device's capability manifest.</summary>
public static class CapabilityManifest
{
    private static readonly Lazy<JsonObject> Loaded = new(() =>
    {
        using var stream = typeof(CapabilityManifest).Assembly.GetManifestResourceStream("durable-capabilities.json")
            ?? throw new InvalidOperationException("The capability manifest is not embedded.");
        return JsonNode.Parse(stream) as JsonObject ?? throw new InvalidOperationException("The capability manifest is not a JSON object.");
    });

    /// <summary>The manifest as published (callers clone before changing it).</summary>
    public static JsonObject Manifest => Loaded.Value;

    /// <summary>Each feature's ID and version, for <c>GET /v1/device</c>.</summary>
    public static IReadOnlyList<DeviceFeature> Features { get; } = [.. (Loaded.Value["features"] as JsonArray ?? [])
        .OfType<JsonObject>()
        .Select(feature => new DeviceFeature(feature["id"]!.GetValue<string>(), feature["version"]!.GetValue<int>()))];
}
