using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Configuration;

/// <summary>Metadata supplied by the owning connection. No client, transport or executable callback crosses this leaf.</summary>
public sealed record McpOfferedTool(string Name, JsonData InputSchema, string? Description = null,
    string? Title = null, string? AnnotationTitle = null);
public sealed record McpServerToolSnapshot(McpServerEntry Entry, ImmutableArray<McpOfferedTool> Tools,
    string? Instructions = null, bool Connected = true, bool HasResources = false);
public sealed record McpPlannedTool(string Server, string OriginalName, string Name, string Label,
    string Description, JsonData Parameters, ToolNamespace Namespace, McpExposure McpExposure, ToolExposure Exposure,
    double TimeoutMilliseconds);
public sealed record McpToolCatalogPlan(ImmutableArray<McpPlannedTool> Tools,
    ImmutableDictionary<string, string> NameOwners, bool NeedsCodemode, bool NeedsToolSearch,
    bool AutoEnableCodemode, McpExposure? ResourceToolsExposure);

/// <summary>Pure configuration/borrowed-catalog planning. Plans neither activate tools nor open connections.</summary>
public static class McpCatalogPlanner
{
    public static McpServerCatalog ComposeServers(McpLoadedConfiguration configured, IEnumerable<McpRegisteredServer> registered)
    {
        ArgumentNullException.ThrowIfNull(configured); ArgumentNullException.ThrowIfNull(registered);
        var result = configured.Servers.ToBuilder(); var overridden = ImmutableArray.CreateBuilder<string>();
        var rows = new List<McpRegisteredServer>(); var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in registered)
        {
            var validated = McpConfigurationReader.Validate(entry.Name, entry.Config.Raw.Value);
            if (!validated.IsValid) throw new ArgumentException(validated.Error, nameof(registered));
            if (positions.TryGetValue(entry.Name, out var index)) rows[index] = entry;
            else { positions.Add(entry.Name, rows.Count); rows.Add(entry); }
        }
        foreach (var entry in rows)
        {
            var config = configured.Servers.FirstOrDefault(server => server.Name == entry.Name);
            if (config is not null)
                overridden.Add($"\"{entry.Name}\" registered by {entry.ExtensionPath} is overridden by {config.Source}");
            else result.Add(new(entry.Name, entry.Config, entry.ExtensionPath, McpConfigurationScope.Extension));
        }
        return new(result.ToImmutable(), overridden.ToImmutable());
    }

    public static ToolExposure ToToolExposure(McpExposure exposure) => exposure switch
    {
        McpExposure.Codemode => ToolExposure.Codemode,
        McpExposure.CodemodeDeferred or McpExposure.Deferred => ToolExposure.Deferred,
        McpExposure.Direct => ToolExposure.Direct,
        McpExposure.Hidden => ToolExposure.Hidden,
        _ => throw new ArgumentOutOfRangeException(nameof(exposure))
    };

    public static string CreateToolName(string server, string tool, Func<string, bool>? isTaken = null)
    {
        ArgumentNullException.ThrowIfNull(server); ArgumentNullException.ThrowIfNull(tool);
        var raw = $"mcp__{server}__{tool}";
        var name = new string(raw.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        if (name.Length <= 64 && !(isTaken?.Invoke(name) ?? false)) return name;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\0" + tool))).ToLowerInvariant()[..8];
        return name[..Math.Min(name.Length, 55)] + "_" + hash;
    }

    public static McpToolCatalogPlan Plan(IEnumerable<McpServerToolSnapshot> snapshots, bool autoEnableCodemode = true,
        ImmutableDictionary<string, string>? previousNameOwners = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var owners = (previousNameOwners ?? ImmutableDictionary<string, string>.Empty).WithComparers(StringComparer.Ordinal).ToBuilder();
        var tools = ImmutableArray.CreateBuilder<McpPlannedTool>();
        var exposures = new HashSet<McpExposure>(); var resourceExposures = new HashSet<McpExposure>();
        foreach (var snapshot in snapshots)
        {
            var entry = snapshot.Entry;
            if (!snapshot.Connected || !entry.Config.Enabled) continue;
            if (snapshot.Tools.IsDefault) throw new ArgumentException("An initialized borrowed tool snapshot is required.", nameof(snapshots));
            var namespaceName = "mcp__" + entry.Name;
            var group = new ToolNamespace(namespaceName, snapshot.Instructions ?? $"Tools in the {namespaceName} namespace.");
            var current = new HashSet<string>(StringComparer.Ordinal);
            if (snapshot.HasResources) { exposures.Add(entry.Config.Exposure); resourceExposures.Add(entry.Config.Exposure); }
            foreach (var tool in snapshot.Tools)
            {
                var owner = entry.Name + "\0" + tool.Name;
                var name = CreateToolName(entry.Name, tool.Name, candidate =>
                    (owners.TryGetValue(candidate, out var existing) && existing != owner) || current.Contains(candidate));
                owners[name] = owner; current.Add(name);
                var exposure = McpConfigurationReader.GetToolExposure(entry.Config, tool.Name); exposures.Add(exposure);
                var description = tool.Description?.Trim(' ', '\t', '\n', '\r', '\v', '\f', '\u00a0', '\u1680',
                    '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a',
                    '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff');
                if (string.IsNullOrEmpty(description)) description = tool.Title ?? tool.AnnotationTitle;
                if (string.IsNullOrEmpty(description)) description = $"MCP tool {tool.Name} from server {entry.Name}";
                tools.Add(new(entry.Name, tool.Name, name, entry.Name + "/" + tool.Name, description,
                    Parameters(tool.InputSchema), group, exposure, ToToolExposure(exposure), entry.Config.TimeoutSeconds * 1000));
            }
        }
        McpExposure? resources = null;
        foreach (var candidate in new[] { McpExposure.Direct, McpExposure.Codemode, McpExposure.CodemodeDeferred, McpExposure.Deferred })
            if (resourceExposures.Contains(candidate)) { resources = candidate; break; }
        return new(tools.ToImmutable(), owners.ToImmutable(), exposures.Contains(McpExposure.Codemode) || exposures.Contains(McpExposure.CodemodeDeferred),
            exposures.Contains(McpExposure.Deferred), autoEnableCodemode, resources);
    }

    private static JsonData Parameters(JsonData schema)
    {
        if (schema.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("MCP input schema must be an object.", nameof(schema));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in schema.Value.EnumerateObject())
            {
                if (property.Name == "type" && property.Value.ValueKind == JsonValueKind.Null) writer.WriteString("type", "object");
                else property.WriteTo(writer);
            }
            if (!schema.Value.TryGetProperty("type", out _)) writer.WriteString("type", "object");
            if (!schema.Value.TryGetProperty("properties", out _)) { writer.WritePropertyName("properties"); writer.WriteStartObject(); writer.WriteEndObject(); }
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
