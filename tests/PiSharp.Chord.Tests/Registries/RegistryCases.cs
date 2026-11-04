using PiSharp.Chord.Registries;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/registries/registry.test.ts. The tokens case runs as
// upstream's does, without its expectTypeOf (a token's types are its type parameters: Registry<C, V>). The providers cases build their
// hosts through createHost (phase 6); here, on the hand-built host (RegistryHost.cs):
// - "a provider without compose" checks checkProvider's refusal, which upstream's staging prefixes with the plugin ('plugin "@app/a": ')
//   and turns into a load failure that leaves the plugin out: that part waits for phase 6.
// - "heals a host.registry made before it" waits on the value before the provider is served, and the publication's commit wakes it; the
//   wait for the publication itself (host.registry before load) is the host's.
// - "one per host" (a second serve leaves its plugin out at load) and "are served only while the setup runs" are the host's staging and
//   wait for phase 6.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RegistriesRegistryCases() =>
    [
        Case("registries.registry.tokens-carry-a-name-a-version-and-the-types-only-names-and-versions-are-checked-at-declaration", () =>
        {
            var http = Registry.Define<RegistriesPath, string[]>("chord.http", new() { Version = 2 });
            Equal("registry", http.Kind, "the kind");
            Equal("chord.http", http.Name, "the name");
            Equal(2L, http.Version, "the version");
            Refuses(() => Registry.Define<int, int>("Chord.Http", new() { Version = 1 }), "registry \"Chord.Http\": expected lowercase");
            Refuses(() => Registry.Define<int, int>("chord.http", new() { Version = 0 }), "chord.http.version: expected a positive integer");
        }),
        Case("registries.registry.providers-a-provider-without-compose-is-refused-when-its-setup-serves-it-plugin-code-is-not-type-checked", () =>
        {
            var names = Registry.Define<string, double>("app.names", new() { Version = 1 });
            var host = new RegistryHost();
            Refuses(() => host.Load(("@app/a", facet => facet.Serve(names, new RegistryProvider<string, double> { Compose = null! }))),
                "registry app.names: compose is a function");
            Refuses(() => RegistriesInternal.CheckProvider("app.names", null), "registry app.names: compose is a function");
        }),
        Case("registries.registry.providers-a-registry-whose-provider-comes-with-load-heals-a-host-registry-made-before-it", async () =>
        {
            var names = Registry.Define<string, double>("app.names", new() { Version = 1 });
            var host = new RegistryHost();
            var waiting = host.RegistryAsync(names);
            Check(!waiting.IsCompleted, "it waits for a provider");
            host.Load(("@app/a", facet => facet.Serve(names, new RegistryProvider<string, double> { Compose = (table, _) => table.Count })));
            host.Delivered();
            var value = await waiting;
            Equal(0.0, value.Value, "the value");
            host.Close();
        }),
    ];

    /// <summary>Upstream's <c>{ readonly path: string }</c>.</summary>
    private sealed record RegistriesPath(string Path);
}
