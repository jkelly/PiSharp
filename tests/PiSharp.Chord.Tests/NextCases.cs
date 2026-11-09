using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using PiSharp.Chord;

// Authored for applicability row chord.next-entry-point (Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/
// index.ts and packages/chord/package.json's "./next" export, which upstream does not test): the assembly is the @earendil-works/chord/next
// entry point, experimental, its public surface under PiSharp.Chord namespaces, and it references PiSharp.Contracts and no PiSharp.Durable
// project (decision 0013). Area cv2-data added PiSharp.Telemetry: upstream's chord depends on pi-telemetry, whose span Chord's context
// carries. The legacy worker driver also uses PiSharp.Tools' owned-process facade for process-tree cleanup.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> NextIndexCases() =>
    [
        Case("next.index.is-the-experimental-chord-next-entry-point", () =>
        {
            Equal("@earendil-works/chord/next", ChordNext.Module, "the module");
            Equal("@earendil-works/chord@1.5.0", ChordNext.Package, "the package");
            Equal("f9a26365568127461c47a07a37dcb42c140a3ac5", ChordNext.Commit, "the pinned commit");
            var assembly = typeof(ChordNext).Assembly;
            Equal("PISHARP_CHORD001", assembly.GetCustomAttribute<ExperimentalAttribute>()?.DiagnosticId, "the assembly is experimental");
            var outside = assembly.GetExportedTypes()
                .Where(type => type.Namespace != "PiSharp.Chord" && type.Namespace?.StartsWith("PiSharp.Chord.", StringComparison.Ordinal) != true).ToList();
            Check(outside.Count == 0, "public types outside PiSharp.Chord: " + string.Join(", ", outside));
            Check(assembly.GetExportedTypes().Any(type => type.Namespace == "PiSharp.Chord.Contracts"), "the contracts layer is public");
        }),
        Case("next.index.references-pisharp-contracts-and-no-durable-project", () =>
        {
            var references = typeof(ChordNext).Assembly.GetReferencedAssemblies().Select(name => name.Name!).Where(name => name.StartsWith("PiSharp.", StringComparison.Ordinal));
            Sequence(["PiSharp.Contracts", "PiSharp.Telemetry", "PiSharp.Tools"], references.Order(StringComparer.Ordinal), "the PiSharp assemblies PiSharp.Chord references (no Durable dependency)");
        }),
    ];
}
