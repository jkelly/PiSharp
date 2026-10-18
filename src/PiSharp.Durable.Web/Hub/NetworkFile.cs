// PiSharp-only (decision 0008). Reads durable-pi's network file (dpi-network.yaml, apps/dpi-sdk/src/node/config.ts, MIT): `defaults.port` and
// `devices.<id>: {name?, kind?, url?, ssh?, port?}`. A device needs a URL, or an ssh host the default port completes. Tokens never come
// from the file. Provisioning ssh devices is durable-pi's `net` tooling and not part of this hub: they are added by their URL.
using YamlDotNet.RepresentationModel;

namespace PiSharp.Durable.Web.Hub;

/// <summary>A device a network file declares.</summary>
public sealed record NetworkFileDevice(string Id, string Name, string Kind, string Url);

/// <summary>dpi-network.yaml.</summary>
public static class NetworkFile
{
    /// <summary>The devices of a network file.</summary>
    /// <exception cref="FormatException">Not a network file, or a device without an address.</exception>
    public static IReadOnlyList<NetworkFileDevice> Parse(string yaml)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (YamlDotNet.Core.YamlException failure) { throw new FormatException("The YAML is not valid: " + failure.Message, failure); }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) throw new FormatException("The file is empty.");
        var port = 4780;
        if (Child(root, "defaults") is YamlMappingNode defaults && Text(defaults, "port") is { } text && int.TryParse(text, out var parsed)) port = parsed;
        if (Child(root, "devices") is not YamlMappingNode devices) throw new FormatException("The file has no devices section.");
        var result = new List<NetworkFileDevice>();
        foreach (var (key, value) in devices.Children)
        {
            var id = ((YamlScalarNode)key).Value ?? "";
            if (!DeviceRegistry.IsValidId(id)) throw new FormatException($"{id} is not a valid device name: use lower-case letters, digits and dashes.");
            var device = value as YamlMappingNode ?? new YamlMappingNode();
            var kind = Text(device, "kind") ?? "url";
            var devicePort = int.TryParse(Text(device, "port"), out var own) ? own : port;
            var url = Text(device, "url");
            if (url is null && Text(device, "ssh") is { } ssh) url = $"http://{ssh[(ssh.IndexOf('@') + 1)..]}:{devicePort}";
            if (url is null && kind == "local") url = $"http://127.0.0.1:{devicePort}";
            if (url is null) throw new FormatException($"{id} has no url.");
            result.Add(new NetworkFileDevice(id, Text(device, "name") ?? id, kind is "local" or "ssh" or "exe-dev" or "url" ? kind : "url", DeviceRegistry.NormalizeUrl(url)));
        }
        return result;
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Text(YamlMappingNode node, string key) => Child(node, key) is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;
}
