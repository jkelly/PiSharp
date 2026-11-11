// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): core/radius-mcp.ts.
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Cli.Mcp;

/// <summary>The same global configuration change is offered by /login and applied by shell login.</summary>
internal sealed record RadiusMcpServerPlan(string ConfigPath, string Name, JsonObject Config)
{
    internal bool Apply()
    {
        var replaced = false;
        McpCommand.EditMcpServers(ConfigPath, (servers, root) =>
        {
            servers ??= new JsonObject();
            replaced = servers.ContainsKey(Name);
            servers[Name] = Config.DeepClone();
            root["mcpServers"] = servers;
            return true;
        });
        return replaced;
    }
}

internal static class RadiusMcpServer
{
    internal const string Url = "https://radius.pi.dev/mcp";

    internal static RadiusMcpServerPlan? Plan(string providerId, string agentDirectory, string cwd)
    {
        var path = Path.Combine(agentDirectory, "mcp.json");
        var loaded = McpConfigurationReader.Load(File.Exists(path) ? new(path, File.ReadAllText(path)) : null, null, false);
        var existing = loaded.Servers.FirstOrDefault(server => server.Config.Transport == McpTransportKind.Http &&
            server.Config.Raw.Value.GetProperty("url").GetString()?.TrimEnd('/') == Url);
        if (existing?.Config.AuthProvider == providerId) return null;
        var name = existing?.Name ?? "radius";
        if (existing is null)
        {
            var namespaces = loaded.Servers.Select(server => McpCatalogPlanner.Namespace(server.Name)).ToHashSet(StringComparer.Ordinal);
            for (var suffix = 1; namespaces.Contains(McpCatalogPlanner.Namespace(name)); suffix++)
                name = suffix == 1 ? "radius-mcp" : $"radius-mcp-{suffix}";
        }
        var config = existing is null ? new JsonObject { ["url"] = Url } : JsonNode.Parse(existing.Config.Raw.Value.GetRawText())!.AsObject();
        config["auth"] = new JsonObject { ["provider"] = providerId };
        config.Remove("oauth");
        return new(path, name, config);
    }
}
