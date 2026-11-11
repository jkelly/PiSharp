// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/host-kind.ts
//
// Host kinds and addresses (§3): a kind of addressable host with params like a contract's, and the address of one host of a kind, which a
// remote contract's hostAddress computes from its params. Upstream's kind is a function with a `kind` property; here it is a HostKind,
// whose Address methods are that function's calls (`Session({ id })` is `Session.Address(("id", id))`). The kind's params data is held
// per kind (a weak table, as upstream's WeakMap), so it lives while the kind is referenced and a copy of a kind is refused. KindShape,
// ParamsOfKind and the typed Address<K> are TypeScript types: an address's params are a JsonObject.
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Contracts;

/// <summary>A kind of addressable host (upstream <c>hostKind(name, params?)</c>): called with its params through <see cref="Address()"/>,
/// it makes an address. Its params follow the params policy of contracts (a flat record of required <c>Str()</c>, <c>Int()</c>,
/// <c>Bool()</c> and <c>OneOf(...)</c>). Making an address computes its key only: its params are checked where it is routed.</summary>
public sealed class HostKind
{
    /// <summary>Declares a host kind named <paramref name="name"/> with <paramref name="params"/> (none when omitted).</summary>
    public HostKind(string name, OrderedDictionary<string, Property>? @params = null)
    {
        ContractsInternal.CheckName(name, "A host kind's name");
        // `client` is the host kind of a host without an address in store names (owner strings, table names).
        if (name == "client") throw new ArgumentException("A host kind's name \"client\" is reserved for hosts without an address");
        var data = ContractsInternal.ReadParams(@params ?? Enumerable.Empty<KeyValuePair<string, Property>>(), $"{name}.params");
        Kind = name;
        ContractsInternal.Kinds.Add(this, data);
    }

    /// <summary>The kind's name.</summary>
    public string Kind { get; }

    /// <summary>Checks the address's kind and exact parameter types, returning its canonical key; ignores its cached key.</summary>
    public string? KeyOfAddress(Address? address) => ContractsInternal.KeyOfAddress(this, address);

    /// <summary>Decodes a canonical stored address key, preserving integer and boolean parameter types.</summary>
    public Address AddressOfKey(string key) => ContractsInternal.AddressOfKey(this, key);

    /// <summary>The address of the one host of a kind without params (<c>Server()</c>).</summary>
    public Address Address() => Address(new JsonObject());

    /// <summary>The address of the host with these params; the object is the address's params, as given.</summary>
    public Address Address(JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new Address(Kind, values, ContractsInternal.AddressKey(Kind, ContractsInternal.KindParams(this), values));
    }

    /// <summary>The address of the host with these params, field by field (a value that belongs to another node is copied).</summary>
    public Address Address(params (string Field, JsonNode? Value)[] values)
    {
        var @params = new JsonObject();
        foreach (var (field, value) in values) @params[field] = value?.Parent is null ? value : value.DeepClone();
        return Address(@params);
    }
}

/// <summary>One host of a kind: the kind's name, its params and its key (<c>session|S1</c>); two addresses are equal when their keys are.</summary>
public sealed class Address : IEquatable<Address>
{
    internal Address(string kind, JsonObject @params, string key)
    {
        Kind = kind;
        Params = @params;
        Key = key;
    }

    /// <summary>The kind's name.</summary>
    public string Kind { get; }

    /// <summary>The params of the host.</summary>
    public JsonObject Params { get; }

    /// <summary>The address's key: the kind's name and each field value in declared order, joined like an identity key.</summary>
    public string Key { get; }

    /// <inheritdoc/>
    public bool Equals(Address? other) => other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Address);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    /// <inheritdoc/>
    public override string ToString() => Key;

    /// <summary>The portable JSON representation of an address.</summary>
    public JsonObject ToJson() => new() { ["kind"] = Kind, ["params"] = Params.DeepClone(), ["key"] = Key };
}

internal static partial class ContractsInternal
{
    internal static readonly ConditionalWeakTable<HostKind, ParamsData> Kinds = new();

    /// <summary>The params of a declared host kind.</summary>
    internal static ParamsData KindParams(HostKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return Kinds.TryGetValue(kind, out var data) ? data : throw new ArgumentException($"{kind.Kind}: not a declared host kind");
    }

    /// <summary>An address's key: the kind's name and each field value in declared order (as JavaScript's <c>String</c> writes it), joined
    /// (<see cref="JoinKey"/>).</summary>
    internal static string AddressKey(string kind, ParamsData @params, JsonObject values) =>
        JoinKey([kind, .. @params.Fields.Select(field => values.TryGetPropertyValue(field, out var value) ? ChordJson.ToJavaScriptString(value) : "undefined")]);

    internal static string? KeyOfAddress(HostKind kind, object? value)
    {
        var data = KindParams(kind);
        if (value is JsonValue wrapped && wrapped.TryGetValue<Address>(out var unwrapped)) value = unwrapped;
        var (name, parameters) = value switch
        {
            Address address => (address.Kind, address.Params),
            JsonObject json => (ChordJson.AsString(json["kind"]), json["params"] as JsonObject),
            _ => (null, null),
        };
        if (name != kind.Kind || parameters is null || parameters.Count != data.Fields.Count) return null;
        foreach (var pair in data.Normal.OfType<JsonArray>())
        {
            var field = ChordJson.AsString(pair[0])!;
            if (!parameters.TryGetPropertyValue(field, out var each) || pair[1] is not JsonObject schema) return null;
            if (schema["anyOf"] is JsonArray choices)
            {
                var text = ChordJson.AsString(each);
                if (text is null || !choices.OfType<JsonObject>().Any(choice => ChordJson.AsString(choice["const"]) == text)) return null;
            }
            else switch (ChordJson.AsString(schema["type"]))
            {
                case "integer":
                    if (ChordJson.AsNumber(each) is not { } number || !double.IsFinite(number) || !double.IsInteger(number) || Math.Abs(number) > 9007199254740991) return null;
                    break;
                case "boolean":
                    if (!ChordJson.IsBoolean(each, true) && !ChordJson.IsBoolean(each, false)) return null;
                    break;
                case "string":
                    if (ChordJson.AsString(each) is null) return null;
                    break;
                default: return null;
            }
        }
        return AddressKey(kind.Kind, data, parameters);
    }

    internal static Address AddressOfKey(HostKind kind, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var data = KindParams(kind);
        var parts = new List<string>();
        var part = new StringBuilder();
        for (var index = 0; index < key.Length; index++)
        {
            var character = key[index];
            if (character == '\\')
            {
                if (++index >= key.Length || key[index] is not ('\\' or '|')) throw BadKey();
                part.Append(key[index]);
            }
            else if (character == '|') { parts.Add(part.ToString()); part.Clear(); }
            else part.Append(character);
        }
        parts.Add(part.ToString());
        if (parts[0] != kind.Kind || parts.Count != data.Fields.Count + 1) throw BadKey();
        var parameters = new JsonObject();
        for (var index = 0; index < data.Normal.Count; index++)
        {
            var pair = (JsonArray)data.Normal[index]!;
            var field = ChordJson.AsString(pair[0])!;
            var schema = (JsonObject)pair[1]!;
            var text = parts[index + 1];
            switch (ChordJson.AsString(schema["type"]))
            {
                case "integer":
                    if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) throw BadKey();
                    parameters[field] = integer;
                    break;
                case "boolean":
                    if (text is not ("true" or "false")) throw BadKey();
                    parameters[field] = text == "true";
                    break;
                default: parameters[field] = text; break;
            }
        }
        var address = new Address(kind.Kind, parameters, key);
        if (KeyOfAddress(kind, address) != key) throw BadKey();
        return address;

        ArgumentException BadKey() => new($"\"{key}\": not an address key of {kind.Kind}");
    }

    internal static string KindOfKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var end = key.IndexOf('|');
        return end < 0 ? key : key[..end];
    }
}
