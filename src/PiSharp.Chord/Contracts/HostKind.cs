// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/host-kind.ts
//
// Host kinds and addresses (§3): a kind of addressable host with params like a contract's, and the address of one host of a kind, which a
// remote contract's hostAddress computes from its params. Upstream's kind is a function with a `kind` property; here it is a HostKind,
// whose Address methods are that function's calls (`Session({ id })` is `Session.Address(("id", id))`). The kind's params data is held
// per kind (a weak table, as upstream's WeakMap), so it lives while the kind is referenced and a copy of a kind is refused. KindShape,
// ParamsOfKind and the typed Address<K> are TypeScript types: an address's params are a JsonObject.
using System.Runtime.CompilerServices;
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
}
