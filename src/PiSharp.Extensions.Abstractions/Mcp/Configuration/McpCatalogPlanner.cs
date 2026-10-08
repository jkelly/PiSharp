// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/mcp-servers.ts, packages/coding-agent/src/extensions/mcp/tools.ts and packages/coding-agent/src/extensions/mcp/index.ts.
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
/// <summary><paramref name="Namespace"/> carries the configured server description. The server's instructions are
/// longer usage guidance kept out of tool listings, carried as <paramref name="NamespaceInstructions"/>.</summary>
public sealed record McpPlannedTool(string Server, string OriginalName, string Name, string Label,
    string Description, JsonData Parameters, ToolNamespace Namespace, McpExposure McpExposure, ToolExposure Exposure,
    double TimeoutMilliseconds, string? NamespaceInstructions = null);
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
            else
            {
                // Names that differ only in `-` and `_` would share a namespace; registration rejects them.
                if (rows.FirstOrDefault(row => Namespace(row.Name) == Namespace(entry.Name)) is { } clash)
                    throw new ArgumentException($"MCP server \"{entry.Name}\" conflicts with registered server \"{clash.Name}\"", nameof(registered));
                positions.Add(entry.Name, rows.Count); rows.Add(entry);
            }
        }
        foreach (var entry in rows)
        {
            var config = configured.Servers.FirstOrDefault(server => Namespace(server.Name) == Namespace(entry.Name));
            if (config is not null)
                overridden.Add($"\"{entry.Name}\" registered by {entry.ExtensionPath} is overridden by \"{config.Name}\" in {config.Source}");
            else result.Add(new(entry.Name, entry.Config, entry.ExtensionPath, McpConfigurationScope.Extension));
        }
        return new(result.ToImmutable(), overridden.ToImmutable());
    }

    /// <summary>Namespace of a server's tools: `mcp__&lt;server&gt;` with `-` replaced by `_`, like the tool names.</summary>
    public static string Namespace(string server)
    { ArgumentNullException.ThrowIfNull(server); return "mcp__" + server.Replace('-', '_'); }

    /// <summary>`codemode` and `deferred` both leave tools out of the codemode description; they differ only in
    /// which discovery tool reaches them.</summary>
    public static ToolExposure ToToolExposure(McpExposure exposure) => exposure switch
    {
        McpExposure.Codemode or McpExposure.Deferred => ToolExposure.Deferred,
        McpExposure.Direct => ToolExposure.Direct,
        McpExposure.Hidden => ToolExposure.Hidden,
        _ => throw new ArgumentOutOfRangeException(nameof(exposure))
    };

    /// <summary>`mcp__&lt;server&gt;__&lt;tool&gt;` with everything but `[A-Za-z0-9_]` replaced by `_`, so the name is also a
    /// script identifier; shortened with a hash of the raw names when too long or when <paramref name="isTaken"/>.</summary>
    public static string CreateToolName(string server, string tool, Func<string, bool>? isTaken = null)
    {
        ArgumentNullException.ThrowIfNull(server); ArgumentNullException.ThrowIfNull(tool);
        var raw = $"mcp__{server}__{tool}";
        var name = new string(raw.Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
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
            var configured = entry.Config.Description is { } text ? McpJson.JsTrim(text) : null;
            // Like upstream registerTools: the configured description and the server's connection instructions.
            var group = new ToolNamespace(Namespace(entry.Name), string.IsNullOrEmpty(configured) ? null : configured)
                { Instructions = string.IsNullOrEmpty(snapshot.Instructions) ? null : snapshot.Instructions };
            var current = new HashSet<string>(StringComparer.Ordinal);
            // Like Codex, every tool whose name sanitizes to a shared name gets the hash suffix, so which one
            // would keep the plain name does not depend on the order of the list.
            var shared = snapshot.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal)
                .Select(tool => CreateToolName(entry.Name, tool)).GroupBy(name => name, StringComparer.Ordinal)
                .Where(names => names.Count() > 1).Select(names => names.Key).ToHashSet(StringComparer.Ordinal);
            if (snapshot.HasResources) { exposures.Add(entry.Config.Exposure); resourceExposures.Add(entry.Config.Exposure); }
            foreach (var tool in snapshot.Tools)
            {
                var owner = entry.Name + "\0" + tool.Name;
                var name = CreateToolName(entry.Name, tool.Name, candidate =>
                    (owners.TryGetValue(candidate, out var existing) && existing != owner) || current.Contains(candidate) || shared.Contains(candidate));
                owners[name] = owner; current.Add(name);
                var exposure = McpConfigurationReader.GetToolExposure(entry.Config, tool.Name); exposures.Add(exposure);
                var description = tool.Description is { } offered ? McpJson.JsTrim(offered) : null;
                if (string.IsNullOrEmpty(description)) description = tool.Title ?? tool.AnnotationTitle;
                if (string.IsNullOrEmpty(description)) description = $"MCP tool {tool.Name} from server {entry.Name}";
                tools.Add(new(entry.Name, tool.Name, name, entry.Name + "/" + tool.Name, description,
                    Parameters(tool.InputSchema), group, exposure, ToToolExposure(exposure), entry.Config.TimeoutSeconds * 1000,
                    snapshot.Instructions));
            }
        }
        McpExposure? resources = null;
        foreach (var candidate in new[] { McpExposure.Direct, McpExposure.Codemode, McpExposure.Deferred })
            if (resourceExposures.Contains(candidate)) { resources = candidate; break; }
        return new(tools.ToImmutable(), owners.ToImmutable(), exposures.Contains(McpExposure.Codemode),
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
