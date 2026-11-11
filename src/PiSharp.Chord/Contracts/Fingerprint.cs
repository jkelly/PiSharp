// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/fingerprint.ts
//
// Fingerprints: the normal form of a contract version and its portable 64-bit hash. Not a security boundary. The hash input is
// JavaScript's JSON.stringify of the normal form (ChordJson.Stringify), so a contract declared here has the fingerprint the same
// declaration has in TypeScript, and a C# host and a TypeScript peer agree on it. Area cv2-contracts added this file because every
// declaration is materialized with its fingerprint; area cv2-evolve, which owns fingerprint.ts, ported fingerprint.test.ts (FingerprintCases.cs).
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Contracts;

internal static partial class ContractsInternal
{
    /// <summary>Version of the normal form; changing the normal form changes it and is a link-protocol change.</summary>
    internal const int NormalFormVersion = 2;

    /// <summary>Materializes a version with its fingerprint: <see cref="Hash64"/> of <c>JSON.stringify({ normalFormVersion, value })</c> over
    /// the normal form <c>{ kind: "remote", params, variants }</c> or <c>{ kind: "local", params, members }</c>.</summary>
    internal static VersionData Materialize(ContractKind kind, ParamsData @params, long version, IReadOnlyDictionary<string, VariantData> variants,
        IReadOnlyList<Change> changes)
    {
        var normal = new JsonObject { ["kind"] = WireName(kind), ["params"] = @params.Normal.DeepClone() };
        if (kind == ContractKind.Local) normal["members"] = VariantNormal(variants[Local]);
        else normal["variants"] = SortedDict(variants.Select(entry => (entry.Key, (JsonNode?)VariantNormal(entry.Value))));
        return new VersionData(version, FingerprintOf(normal), variants, changes);
    }

    /// <summary><see cref="Hash64"/> of <c>JSON.stringify({ normalFormVersion, value: normal })</c>: the fingerprint of a normal form.</summary>
    internal static string FingerprintOf(JsonNode normal)
    {
        var text = new StringBuilder("{\"normalFormVersion\":").Append(NormalFormVersion.ToString(CultureInfo.InvariantCulture)).Append(",\"value\":");
        ChordJson.Stringify(text, normal);
        return Hash64(text.Append('}').ToString());
    }

    /// <summary>Members as a sorted dictionary of <c>{ state }</c>, <c>{ args, result }</c> or <c>{ keyed }</c> (slice 6a §12: a node of
    /// its own, so contracts without keyed members keep their fingerprints and <see cref="NormalFormVersion"/> stays).</summary>
    private static JsonObject VariantNormal(VariantData variant) => SortedDict(
        variant.State.Select(entry => (entry.Key, (JsonNode?)new JsonObject { ["state"] = entry.Value.Normal.DeepClone() }))
            .Concat(variant.Keyed.Select(entry => (entry.Key, (JsonNode?)KeyedNormal(entry.Value))))
            .Concat(variant.Methods.Select(entry => (entry.Key, (JsonNode?)new JsonObject
            {
                ["args"] = entry.Value.Args.Normal.DeepClone(),
                ["result"] = entry.Value.Result.Normal.DeepClone(),
            }))));

    private static JsonObject KeyedNormal(Snapshot member)
    {
        var normal = new JsonObject { ["keyed"] = member.Normal.DeepClone() };
        if (member.KeyHost is { } host) normal["key"] = host.Kind;
        return normal;
    }

    private static JsonObject SortedDict(IEnumerable<(string Key, JsonNode? Value)> entries)
    {
        var result = new JsonObject();
        foreach (var (key, value) in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)) result[key] = value;
        return result;
    }

    /// <summary>16 hex characters from two 32-bit FNV-1a lanes (different offset bases and primes) over the UTF-8 bytes.</summary>
    internal static string Hash64(string text)
    {
        var low = 0x811c9dc5u;
        var high = 0xcbf29ce4u;
        foreach (var octet in Encoding.UTF8.GetBytes(text))
        {
            low = unchecked((low ^ octet) * 0x01000193u);
            high = unchecked((high ^ octet) * 0x01000197u);
        }
        return high.ToString("x8", CultureInfo.InvariantCulture) + low.ToString("x8", CultureInfo.InvariantCulture);
    }
}
