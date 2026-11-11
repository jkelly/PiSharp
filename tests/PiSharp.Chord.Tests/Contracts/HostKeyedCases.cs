// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/contracts/host-kind.ts, packages/chord/src/contracts/contract.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ContractsHostKeyedCases() =>
    [
        Case("contracts.host-keyed.canonical-keys-ignore-cached-key-and-check-exact-types", () =>
        {
            var tenant = new HostKind("tenant", new() { ["name"] = Str(), ["shard"] = Int(), ["active"] = Bool(), ["region"] = OneOf("us", "eu") });
            var address = tenant.Address(("name", "a|b\\c"), ("shard", 4), ("active", true), ("region", "eu"));
            Equal("tenant|a\\|b\\\\c|4|true|eu", tenant.KeyOfAddress(address), "canonical escaped key");
            Json("""{"name":"a|b\\c","shard":4,"active":true,"region":"eu"}""", tenant.AddressOfKey(address.Key).Params, "typed round trip");
            address.Params["name"] = "changed";
            Equal("tenant|changed|4|true|eu", tenant.KeyOfAddress(address), "cached key is ignored");
            address.Params["extra"] = true;
            Check(tenant.KeyOfAddress(address) is null, "extra field rejected");
            address.Params.Remove("extra");
            address.Params["shard"] = 9007199254740992L;
            Check(tenant.KeyOfAddress(address) is null, "unsafe integer rejected");
            address.Params["shard"] = 4;
            address.Params["region"] = "other";
            Check(tenant.KeyOfAddress(address) is null, "literal mismatch rejected");
            address.Params["region"] = "us";
            address.Params["active"] = "true";
            Check(tenant.KeyOfAddress(address) is null, "boolean string rejected");
            Check(tenant.KeyOfAddress(new HostKind("other").Address()) is null, "different kind rejected");
            foreach (var invalid in new[] { "tenant|x|04|true|us", "tenant|x|4|yes|us", "tenant|x|4|true|no", "tenant|x\\q|4|true|us", "tenant|x|4|true" })
                Throws<ArgumentException>(() => tenant.AddressOfKey(invalid), invalid);
        }),
        Case("contracts.host-keyed.pins-upstream-fingerprint-without-changing-string-key-hash", () =>
        {
            var tenant = new HostKind("tenant");
            var strings = Contract.Local("items", new() { Version = 1, Params = new(), Members = new() { ["items"] = Keyed(Str()) } });
            var hosts = Contract.Local("items", new() { Version = 1, Params = new(), Members = new() { ["items"] = Keyed(tenant, Str()) } });
            // Frozen v1.5.2 fingerprint.ts, run directly under Node against the two exact normal forms.
            Equal("3abb26d9a7cda828", strings.Fingerprint, "existing string-keyed fingerprint");
            Equal("a34f82c281b7a441", hosts.Fingerprint, "host-keyed fingerprint");
        }),
        Case("contracts.host-keyed.key-kind-evolution-requires-refuse", () =>
        {
            var tenant = new HostKind("tenant");
            var v1 = Contract.Local("items", new() { Version = 1, Params = new(), Members = new() { ["items"] = Keyed(Str()) } });
            var v2 = Contract.Evolve(v1, editor => editor.Keyed("items", tenant, Str()));
            Refuses(() => ContractsInternal.CheckAccepts(v2, new() { [1] = new() { Keyed = { ["items"] = (Func<object?, object?>)(value => value) } } }), "expected \"refuse\"");
            Accepts(() => ContractsInternal.CheckAccepts(v2, new() { [1] = new() { Keyed = { ["items"] = StateMapping.Refuse } } }), "rekey refusal");
            var v3 = Contract.Evolve(v2, editor => editor.Keyed("items", new HostKind("other"), Str()));
            Refuses(() => ContractsInternal.CheckAccepts(v3, new() { [2] = new() { Keyed = { ["items"] = (Func<object?, object?>)(value => value) } } }), "expected \"refuse\"");
        }),
    ];
}
