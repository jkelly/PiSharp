using PiSharp.Chord.Registries;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/registries/entries.test.ts. The helpers are pure: every case
// runs as upstream's does, on a hand-built table. Upstream's contribution type is an object with optional `routes` (a record of strings)
// and `items` (an array); here it is a C# record of the same two parts.
internal static partial class Program
{
    /// <summary>Upstream's contribution: optional routes and items.</summary>
    private sealed record RegistriesEntriesValue(IReadOnlyDictionary<string, string>? Routes = null, IReadOnlyList<string>? Items = null);

    private static IReadOnlyList<RegistryEntry<RegistriesEntriesValue>> RegistriesEntriesTable() =>
    [
        new() { Plugin = "@a/one", Version = 1, Value = new(new OrderedDictionary<string, string> { ["/x"] = "one", ["/y"] = "one" }, ["late", "first"]) },
        new() { Plugin = "@b/two", Type = "b.store", Version = 1, Value = new(new OrderedDictionary<string, string> { ["/x"] = "two" }, ["second"]) },
        new() { Plugin = "@c/three", Version = 1, Value = new() },
    ];

    private static IEnumerable<(string, Func<Task>)> RegistriesEntriesCases() =>
    [
        Case("registries.entries.keyed-entries-maps-every-entrys-keys-with-their-plugin-a-duplicate-is-reported-and-the-first-by-plugin-name-keeps-it", () =>
        {
            var conflicts = new List<RegistryConflict>();
            var map = RegistryTable.KeyedEntries(RegistriesEntriesTable(), value => value.Routes, conflicts.Add);
            Sequence(["/x", "/y"], map.Keys, "the keys");
            Equal(new TableItem<string>("one", "@a/one"), map["/x"], "/x");
            Equal(new TableItem<string>("one", "@a/one"), map["/y"], "/y");
            Equal(1, conflicts.Count, "one conflict");
            Equal("\"/x\" is contributed twice", conflicts[0].Message, "the message");
            Sequence(["@a/one", "@b/two"], conflicts[0].Plugins, "the plugins");
        }),
        Case("registries.entries.keyed-entries-reads-an-entrys-keys-in-object-entries-order-pisharp", () =>
        {
            // PiSharp: Object.entries reads array-index keys first, ascending, whatever order the record was built in.
            var table = new[] { new RegistryEntry<IReadOnlyDictionary<string, string>> { Plugin = "@a/one", Version = 1, Value = new OrderedDictionary<string, string> { ["b"] = "x", ["2"] = "x", ["1"] = "x", ["a"] = "x" } } };
            Sequence(["1", "2", "b", "a"], RegistryTable.KeyedEntries(table, value => value, _ => { }).Keys, "the keys");
        }),
        Case("registries.entries.ordered-entries-lists-every-entrys-items-in-table-order-with-their-plugin-and-type", () =>
        {
            Sequence(
                [new TableItem<string>("late", "@a/one"), new TableItem<string>("first", "@a/one"), new TableItem<string>("second", "@b/two", "b.store")],
                RegistryTable.OrderedEntries(RegistriesEntriesTable(), value => value.Items),
                "the items");
        }),
        Case("registries.entries.ordered-entries-sorts-by-order-of-table-order-kept-among-equals", () =>
        {
            static double Order(string item) => item == "first" ? 0 : 1;
            Sequence(["first", "late", "second"], RegistryTable.OrderedEntries(RegistriesEntriesTable(), value => value.Items, Order).Select(item => item.Value), "the items");
        }),
    ];
}
