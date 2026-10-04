using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;

namespace PiSharp.Compatibility.Node;

/// <summary>Explicit unchanged source selection. Approval, package policy and lifecycle remain host-owned.</summary>
public static class NodeTierAAdmission
{
    public const string SourceCommit = "d86654abb8862e201933517d6f1fce9f88dd117f";
    public const string PirateSource = "packages/coding-agent/examples/extensions/pirate.ts";
    public const string HelloSource = "packages/coding-agent/examples/extensions/hello.ts";
    public const string CommandsSource = "packages/coding-agent/examples/extensions/commands.ts";
    public const string InputSource = "packages/coding-agent/examples/extensions/input-transform.ts";
    private static readonly Dictionary<string, (int Bytes, string Hash)> Pins = new(StringComparer.Ordinal)
    {
        [CommandsSource] = (2594, "36716b53da169936c7e1360a4fde1e2fc0c3356a6505f177235f09c5538c6e4f"),
        [InputSource] = (1444, "cf0f65d610631ca75d18aae1c8f1408139702f674cb2835f8c009b943776474b"),
        [PirateSource] = (1461, "dd6ce684bbe7630e4a749fe133ef8b8b875adbc7e31284b17fe0b8157d51e013"),
        [HelloSource] = (640, "0aa4e9800c2526914d4c1edb00b2cfa9bd9dd6da5218289994cccd5f5bfa4934")
    };
    public static void ValidateSelection(ImmutableArray<string> sources, int inputInstances = 1)
    {
        if (sources.IsDefaultOrEmpty || sources.Length > 8 || inputInstances != 1 || sources.Any(path => path is null || !Pins.ContainsKey(path)))
            throw new InvalidOperationException("Node source admission requires 1–8 explicitly pinned sources and no duplicate legacy profile.");
    }
    internal static void ValidateReceipt(JsonElement source, ImmutableArray<string> selected, int inputInstances)
    {
        var legacy = selected.IsDefault;
        if (legacy) selected = inputInstances == 2 ? [CommandsSource, InputSource, InputSource] : [CommandsSource, InputSource];
        else ValidateSelection(selected, inputInstances);
        if (source.GetProperty("sourceCommit").GetString() != SourceCommit || !source.GetProperty("factoryAwaited").GetBoolean() ||
            !source.GetProperty("sourceFunctionsRemainInNode").GetBoolean() || source.GetProperty("successfulSourceFactoryInvocations").GetInt32() != selected.Length ||
            source.GetProperty("sourceFactoryCount").GetInt32() != selected.Distinct(StringComparer.Ordinal).Count() ||
            source.GetProperty("sourceReference").GetProperty("expectedSha256").GetString() != NodeCommandInputWorkerLaunch.SourceReferenceExpectedSha256 ||
            source.GetProperty("admissionProfile").GetString() != (legacy ? "legacy-command-input" : "bounded-tier-a"))
            throw new InvalidOperationException("Node source provenance differs.");
        var pins = source.GetProperty("sourcePins");
        if (pins.GetArrayLength() != selected.Length) throw new InvalidOperationException("Node source pin count differs.");
        for (var i = 0; i < selected.Length; i++)
        {
            var row = pins[i]; var expected = Pins[selected[i]];
            if (row.GetProperty("path").GetString() != selected[i] || row.GetProperty("bytes").GetInt32() != expected.Bytes || row.GetProperty("sha256").GetString() != expected.Hash)
                throw new InvalidOperationException("Node source pin differs.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "commands", "inputHandlers", "beforeAgentStartHandlers", "tools" })
        {
            var rows = source.GetProperty(kind);
            if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Node registration array required.");
            foreach (var row in rows.EnumerateArray())
            {
                var id = Text(row, "callbackId", 128); Text(row, "sourcePath", 4096);
                if (!ids.Add(id) || ids.Count > 64) throw new InvalidOperationException("Node registration identity/count differs.");
                if (kind is "commands" or "tools")
                {
                    if (!names.Add(kind + ":" + Text(row, "name", 128))) throw new InvalidOperationException("Duplicate Node registration name.");
                    Text(row, "description", 65536, allowEmpty: true);
                    if (kind == "commands") _ = row.GetProperty("hasCompletion").GetBoolean();
                    else
                    {
                        if (row.GetProperty("initialPreparation").GetString() != "original-validateToolArguments" || !row.GetProperty("originalSchemaRetained").GetBoolean())
                            throw new InvalidOperationException("Node original tool preparation/schema identity differs.");
                        _ = row.GetProperty("hasLoadoutPreparation").GetBoolean();
                        _ = NodeToolLoadoutMetadata.ReadNamespace(row);
                        var json = Text(row, "parametersJson", 65536);
                        if (Encoding.UTF8.GetByteCount(json) > 65536 || JsonData.Parse(json).Value.ValueKind != JsonValueKind.Object)
                            throw new InvalidOperationException("Node tool schema differs.");
                    }
                }
            }
        }
        if (legacy && (source.GetProperty("commands").GetArrayLength() != 1 || source.GetProperty("inputHandlers").GetArrayLength() != inputInstances ||
            source.GetProperty("beforeAgentStartHandlers").GetArrayLength() != 0 || source.GetProperty("tools").GetArrayLength() != 0 ||
            source.GetProperty("commands")[0].GetProperty("callbackId").GetString() != "commands-1" ||
            source.GetProperty("commands")[0].GetProperty("name").GetString() != "commands"))
            throw new InvalidOperationException("Legacy Commands/Input registration differs.");
    }
    private static string Text(JsonElement row, string key, int maximum, bool allowEmpty = false)
    {
        var value = row.GetProperty(key).GetString();
        if (value is null || (!allowEmpty && value.Length == 0) || value.Length > maximum) throw new InvalidOperationException("Node registration text limit: " + key);
        return value;
    }
}
