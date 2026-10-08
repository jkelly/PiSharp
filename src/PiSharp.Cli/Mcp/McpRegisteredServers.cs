// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/mcp-servers.ts (McpServerRegistry),
// packages/coding-agent/src/core/extensions/loader.ts (registerMcpServer: validation, ownership, namespace clashes) and
// packages/coding-agent/src/core/extensions/runner.ts (bind: the change listener emits mcp_servers_change with every server).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Registration;

namespace PiSharp.Cli.Mcp;

/// <summary>The servers native extensions register for the session (<c>pi.registerMcpServer()</c>). The production MCP host connects
/// them next to the <c>mcp.json</c> servers (a configured server of the same name takes precedence): those registered while the
/// extensions load at session start, later ones right away, and unregistered ones are closed. Once the session started, every change
/// is also delivered to native extensions as <c>mcp_servers_change</c> with every registered server.</summary>
public sealed class McpRegisteredServers : IExtensionMcpServerHost
{
    private readonly object gate = new();
    private readonly List<McpRegisteredServer> servers = [];
    private readonly List<Action> listeners = [];
    private Func<JsonData, Task>? dispatch;

    /// <summary>The extension path of an owner (the host's package path); the owner id itself without one.</summary>
    public Func<string, string>? OwnerPath { get; set; }

    public void Register(string ownerId, string name, JsonData configuration)
    {
        ArgumentNullException.ThrowIfNull(ownerId); ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(configuration);
        var path = PathOf(ownerId);
        var validated = McpConfigurationReader.Validate(name, configuration.Value);
        if (validated.Config is not { } config) throw new InvalidOperationException($"Invalid MCP server registered by extension \"{path}\": {validated.Error}");
        lock (gate)
        {
            var existing = servers.FindIndex(server => server.Name == name);
            if (existing >= 0 && servers[existing].ExtensionPath != path)
                throw new InvalidOperationException($"MCP server \"{name}\" is already registered by extension \"{servers[existing].ExtensionPath}\"");
            // Names that differ only in `-` and `_` would share a namespace.
            if (servers.FirstOrDefault(server => server.Name != name && McpCatalogPlanner.Namespace(server.Name) == McpCatalogPlanner.Namespace(name)) is { } clash)
                throw new InvalidOperationException($"MCP server \"{name}\" conflicts with registered server \"{clash.Name}\"");
            var registered = new McpRegisteredServer(name, config, path);
            if (existing >= 0) servers[existing] = registered; else servers.Add(registered);
        }
        Changed();
    }

    /// <summary>Removes a server registered by the owner; servers of other extensions are left alone.</summary>
    public void Unregister(string ownerId, string name)
    {
        ArgumentNullException.ThrowIfNull(ownerId); ArgumentNullException.ThrowIfNull(name);
        var path = PathOf(ownerId);
        lock (gate) { if (servers.RemoveAll(server => server.Name == name && server.ExtensionPath == path) == 0) return; }
        Changed();
    }

    /// <summary>The registered servers, in registration order.</summary>
    public ImmutableArray<McpRegisteredServer> List() { lock (gate) return [.. servers]; }

    /// <summary>Subscribes to changes (the MCP host connects and closes servers); dispose to unsubscribe.</summary>
    internal IDisposable Subscribe(Action listener)
    {
        lock (gate) listeners.Add(listener);
        return new Subscription(() => { lock (gate) listeners.Remove(listener); });
    }

    /// <summary>runner.ts bind: from now on every change reaches native extensions as <c>mcp_servers_change</c>; registrations made while
    /// they loaded are read at session start instead.</summary>
    internal void BindDispatch(Func<JsonData, Task> observe) { lock (gate) dispatch = observe; }

    /// <summary>McpServersChangeEvent: <c>{ type: "mcp_servers_change", servers: [{ name, config, extensionPath }] }</c>.</summary>
    internal static JsonData ChangeEvent(IEnumerable<McpRegisteredServer> registered)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject(); writer.WriteString("type", "mcp_servers_change");
            writer.WritePropertyName("servers"); writer.WriteStartArray();
            foreach (var server in registered)
            {
                writer.WriteStartObject(); writer.WriteString("name", server.Name);
                writer.WritePropertyName("config"); server.Config.Raw.Value.WriteTo(writer);
                writer.WriteString("extensionPath", server.ExtensionPath); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return JsonData.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private void Changed()
    {
        Action[] current; Func<JsonData, Task>? observe;
        lock (gate) { current = [.. listeners]; observe = dispatch; }
        foreach (var listener in current) try { listener(); } catch (Exception) { /* The host reports its own failures. */ }
        if (observe is not null) _ = DispatchAsync(observe, ChangeEvent(List()));
    }

    private static async Task DispatchAsync(Func<JsonData, Task> observe, JsonData value)
    {
        // runner.emit: handler failures are reported by the dispatch; the registration itself never fails.
        try { await observe(value).ConfigureAwait(false); } catch (Exception) { }
    }

    private string PathOf(string ownerId) => OwnerPath?.Invoke(ownerId) ?? ownerId;

    private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
