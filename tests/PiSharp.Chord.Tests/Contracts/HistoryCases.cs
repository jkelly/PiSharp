using PiSharp.Chord.Contracts;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/history.test.ts. Upstream's cases are type-level
// only (expectTypeOf of Increment, CurrentStep, OlderVersions, StepAt and ChangesAfter over a literal History type), which C# has no
// counterpart for: a contract's history is its ContractData.Versions at run time (History.cs). Each case checks the same facts on a run-time
// history instead: the version numbers evolve gives (upstream's Increment on decimal digits), and the current step, the older versions,
// the step of a number and the changes after a version of a history with versions 9, 10 and 11 whose steps change ["a", "x", "state"]
// and ["a", "m", "args"], as upstream's History does. Upstream's History has empty variants; a run-time edit needs the members it edits,
// so variant a declares x and m.
internal static partial class Program
{
    /// <summary>The history of upstream's case: versions 9, 10 (changes a.x's state) and 11 (changes m's arguments).</summary>
    private static ContractData HistoryOf()
    {
        var v9 = Contract.Remote("h", new()
        {
            Version = 9,
            Params = new(),
            HostAddress = _ => new HostKind("server").Address(),
            Variants = new() { ["a"] = new() { State = { ["x"] = Str() }, Methods = { ["m"] = Method() } } },
        });
        var v10 = Contract.Evolve(v9, e => e.State("a", "x", Int()));
        return ContractsInternal.DataOf(Contract.Evolve(v10, e => e.Args("m", Str())));
    }

    private static IEnumerable<string> HistoryChanges(IEnumerable<Change> changes) =>
        changes.Select(change => $"{change.Variant},{change.Member},{change.Side.ToString().ToLowerInvariant()}");

    private static IEnumerable<(string, Func<Task>)> ContractsHistoryCases() =>
    [
        Case("contracts.history.type-level-history-increments-decimal-versions", () =>
        {
            foreach (var (from, to) in new[] { (1L, 2L), (9L, 10L), (19L, 20L), (99L, 100L), (1099L, 1100L) })
            {
                var @base = Contract.Local("i", new() { Version = from, Params = new(), Members = new() { ["s"] = Str() } });
                Equal(to, Contract.Evolve(@base, e => e).Version, $"Increment<\"{from}\">");
            }
        }),
        Case("contracts.history.type-level-history-finds-the-current-step-older-versions-and-steps-by-version", () =>
        {
            var history = HistoryOf();
            Equal(11L, history.Versions[^1].Version, "CurrentStep's version");
            Sequence([9L, 10L], ContractsInternal.OlderVersions(history).Select(step => step.Version), "OlderVersions");
            Sequence(["a,x,state"], HistoryChanges(history.Versions.Single(step => step.Version == 10).Changes), "StepAt<\"10\">'s changes");
            Sequence([], HistoryChanges(history.Versions.Single(step => step.Version == 9).Changes), "a base has no changes");
        }),
        Case("contracts.history.type-level-history-collects-every-change-after-a-version", () =>
        {
            var history = HistoryOf();
            Sequence(["a,x,state", "a,m,args"], HistoryChanges(ContractsInternal.ChangesAfter(history, 9)), "ChangesAfter<\"9\">");
            Sequence(["a,m,args"], HistoryChanges(ContractsInternal.ChangesAfter(history, 10)), "ChangesAfter<\"10\">");
            Sequence([], HistoryChanges(ContractsInternal.ChangesAfter(history, 11)), "ChangesAfter<\"11\">");
        }),
    ];
}
