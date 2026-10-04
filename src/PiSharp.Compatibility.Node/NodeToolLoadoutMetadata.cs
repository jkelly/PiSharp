using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Compatibility.Node;

/// <summary>Bounded read-only projection; no adapter, delegate or execution context crosses the worker.</summary>
public static class NodeToolLoadoutMetadata
{
    public static JsonData Write(ToolLoadout loadout, string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        if (string.IsNullOrEmpty(snapshotId) || snapshotId.Length > 128 || loadout.Registered.IsDefault ||
            loadout.Declared.IsDefault || loadout.Callable.IsDefault || loadout.Registered.Length > 256)
            throw new InvalidOperationException("Node loadout snapshot shape/count differs.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = loadout.Registered.Select(tool =>
        {
            if (!names.Add(tool.Name) || tool.Name.Length is 0 or > 128 || tool.Description.Length > 65536 ||
                tool.Declaration.Value.EnumerateObject().Any(p => p.Name is not ("name" or "label" or "description" or "parameters")) ||
                !tool.Declaration.Value.TryGetProperty("parameters", out var schema) || schema.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Unsupported Node loadout declaration.");
            declarations.Add(tool.Name, tool.Declaration.ToString());
            var row = new Dictionary<string, object?> { ["declaration"] = tool.Declaration.Value, ["exposure"] = Exposure(tool.Exposure) };
            if (tool.Namespace is { } group)
            {
                if (group.Name is null || group.Name.Length > 65536 || group.Description?.Length > 65536)
                    throw new InvalidOperationException("Node tool namespace text limit.");
                var metadata = new Dictionary<string, object?> { ["name"] = group.Name };
                if (group.Description is not null) metadata.Add("description", group.Description);
                row.Add("namespace", metadata);
            }
            return row;
        }).ToArray();
        string[] Ordered(ImmutableArray<ToolLoadoutTool> tools)
        {
            var result = tools.Select(tool => tool.Name).ToArray();
            if (result.Length > 256 || result.Distinct(StringComparer.Ordinal).Count() != result.Length ||
                result.Any(name => !names.Contains(name)) ||
                tools.Any(tool => declarations[tool.Name] != tool.Declaration.ToString()))
                throw new InvalidOperationException("Node loadout ordered member differs.");
            return result;
        }
        var json = JsonSerializer.Serialize(new { snapshotId, declared = Ordered(loadout.Declared), callable = Ordered(loadout.Callable), registered = rows });
        if (Encoding.UTF8.GetByteCount(json) > 262144) throw new IOException("Node loadout snapshot byte budget.");
        return JsonData.Parse(json);
    }
    public static ToolLoadoutChanges? ReadChanges(JsonData? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (Encoding.UTF8.GetByteCount(value.ToString()) > 262144 || value.Value.ValueKind != JsonValueKind.Object ||
            value.Value.EnumerateObject().Any(p => p.Name is not ("descriptions" or "hiddenDeclarations")))
            throw new InvalidOperationException("Unsupported Node loadout changes.");
        var descriptions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var hidden = ImmutableArray.CreateBuilder<string>();
        if (value.Value.TryGetProperty("descriptions", out var supplied))
        {
            if (supplied.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Node loadout descriptions object required.");
            foreach (var p in supplied.EnumerateObject())
            {
                if (descriptions.Count >= 256 || p.Name.Length > 128 || p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > 65536)
                    throw new InvalidOperationException("Node loadout description limit.");
                descriptions.Add(p.Name, p.Value.GetString()!);
            }
        }
        if (value.Value.TryGetProperty("hiddenDeclarations", out var suppliedHidden))
        {
            if (suppliedHidden.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Node hidden declaration array required.");
            foreach (var item in suppliedHidden.EnumerateArray())
            {
                if (hidden.Count >= 256 || item.ValueKind != JsonValueKind.String || item.GetString()!.Length > 128)
                    throw new InvalidOperationException("Node hidden declaration limit.");
                hidden.Add(item.GetString()!);
            }
        }
        return new() { Descriptions = descriptions.ToImmutable(), HiddenDeclarations = hidden.ToImmutable() };
    }
    internal static ToolNamespace? ReadNamespace(JsonElement row)
    {
        if (!row.TryGetProperty("namespace", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not ("name" or "description")) ||
            !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString()!.Length > 65536)
            throw new InvalidOperationException("Node namespace metadata differs.");
        string? description = null;
        if (value.TryGetProperty("description", out var text))
        {
            if (text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 65536) throw new InvalidOperationException("Node namespace description differs.");
            description = text.GetString();
        }
        return new(name.GetString()!, description);
    }
    private static string Exposure(ToolExposure value) => value switch
    {
        ToolExposure.Direct => "direct", ToolExposure.ModelOnly => "model-only", ToolExposure.Codemode => "codemode",
        ToolExposure.Deferred => "deferred", ToolExposure.Hidden => "hidden",
        _ => throw new InvalidOperationException("Unknown Node loadout exposure.")
    };
}
