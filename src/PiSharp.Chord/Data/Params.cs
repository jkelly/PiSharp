// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/data/params.ts
//
// Params and identity: the checked params of a get, their key, and the in-process identity of an object. Params are the caller's own
// object, never copied, frozen or defaulted. identityKey's principal key is required for a remote contract and absent for a local one by
// type upstream; here the two overloads refuse the other kind when they run (ArgumentException), as declarations are checked when they
// run. ParamsOf is a TypeScript type: checked params are a JsonObject.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Data;

internal static partial class DataInternal
{
    /// <summary>Checks <paramref name="raw"/> against the contract's params with one compiled closed-object check (<c>invalid/params</c>
    /// on failure) and returns the caller's own object with its key: the field values in declared order (as JavaScript's <c>String</c>
    /// writes them), each escaped, joined with <c>|</c> (upstream <c>checkParams</c>).</summary>
    internal static (JsonObject Params, string Key) CheckParams(Contract contract, JsonNode? raw, ChordContext context)
    {
        var @params = ContractsInternal.DataOf(contract).Params;
        Check(ValidatorFor(@params, context), raw, "params", context);
        // The check above establishes the params type.
        var values = raw as JsonObject ?? ChordJson.AsObject(raw)!;
        return (values, ContractsInternal.JoinKey(@params.Fields.Select(field => ChordJson.ToJavaScriptString(values[field]))));
    }

    /// <summary>The in-process identity of a local object: <c>name|key</c>, each part escaped (upstream <c>identityKey</c>; a local
    /// contract has no principal).</summary>
    internal static string IdentityKey(Contract contract, string key)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Kind != ContractKind.Local) throw new ArgumentException($"{contract.Name}: a remote object's identity needs a principal key");
        return ContractsInternal.JoinKey([contract.Name, key]);
    }

    /// <summary>The in-process identity of a remote object: <c>name|key|principalKey</c>, each part escaped (upstream
    /// <c>identityKey</c>).</summary>
    internal static string IdentityKey(Contract contract, string key, string principalKey)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Kind != ContractKind.Remote) throw new ArgumentException($"{contract.Name}: a local object's identity has no principal key");
        return ContractsInternal.JoinKey([contract.Name, key, principalKey]);
    }

    /// <summary>An address from a kind's name and params as a hold names it or a <c>hostAddress</c> returned it: the params checked against
    /// the kind's with one compiled closed-object check (<c>invalid/host_address</c> on failure), the key computed (upstream
    /// <c>checkAddress</c>).</summary>
    internal static Address CheckAddress(string kind, ParamsData @params, JsonNode? raw, ChordContext context)
    {
        Check(ValidatorFor(@params, context), raw, "host_address", context);
        // The check above establishes a flat record of the kind's scalar fields.
        var values = raw as JsonObject ?? ChordJson.AsObject(raw)!;
        return new Address(kind, values, ContractsInternal.AddressKey(kind, @params, values));
    }
}
