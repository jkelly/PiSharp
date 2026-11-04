using System.Reflection;
using System.Text.RegularExpressions;
using PiSharp.Chord.Contracts;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/contract.test.ts. Adaptations: a spread copy
// ({ ...Notes }) is a MemberwiseClone of the handle, the one copy C# can make of a class with a private constructor. The branding case
// checks contractData here; its evolve and checkAccepts refusals come with area cv2-evolve, and bindProvider and checkParams with area
// cv2-data. The "derived types" cases other than takesNoArgs (Implementation of a remote and of a local contract, ParamsOf) are
// expectTypeOf checks of TypeScript types with no C# counterpart (Contracts/Index.cs). The last case is authored: it pins the link order
// of variants, state members and methods (variantOrder, variantNames) that support.ts's wireNames reads.
internal static partial class Program
{
    private static readonly Contract ContractNotes = Contract.Remote("notes", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str(), ["page"] = Int() },
        HostAddress = _ => Server.Address(),
        Variants = new()
        {
            ["editor"] = new() { State = { ["notes"] = Arr(Str()) }, Methods = { ["add"] = Method(Obj(("text", Str())), Str()), ["clear"] = Method() } },
            ["reader"] = new() { State = { ["notes"] = Arr(Str()) } },
        },
    });

    private static IEnumerable<(string, Func<Task>)> ContractsContractCases() =>
    [
        Case("contracts.contract.handles-carry-kind-name-version-and-fingerprint-only", () =>
        {
            // Object.keys(Notes): the handle's own data.
            Sequence(["Kind", "Name", "Version", "Fingerprint"],
                typeof(Contract).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name), "the handle's properties");
            Check(typeof(Contract).GetFields(BindingFlags.Public | BindingFlags.Instance).Length == 0, "no public fields");
            Equal(ContractKind.Remote, ContractNotes.Kind, "kind");
            Equal("notes", ContractNotes.Name, "name");
            Equal(1L, ContractNotes.Version, "version");
            Check(Regex.IsMatch(ContractNotes.Fingerprint, "^[0-9a-f]{16}\\z"), "fingerprint");
        }),
        Case("contracts.contract.handles-are-branded-spread-and-hand-built-copies-are-refused", () =>
        {
            var copy = (Contract)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ContractNotes, null)!;
            Equal(ContractNotes.Fingerprint, copy.Fingerprint, "the copy carries the handle's data");
            const string refusal = "notes: not a declared contract (spread and hand-built copies are refused)";
            Refuses(() => ContractsInternal.DataOf(copy), refusal);
            Refuses(() => ContractsInternal.CurrentVariants(copy), refusal, "currentVariants");
            Check(ReferenceEquals(ContractsInternal.DataOf(ContractNotes), ContractsInternal.DataOf(ContractNotes)), "the handle's own data");
        }),
        Case("contracts.contract.handles-are-collected-when-dropped-with-their-data", async () =>
        {
            var references = Weakly(() =>
            {
                var contract = Contract.Local("gc", new() { Version = 1, Params = new(), Members = new() { ["s"] = Str() } });
                return [contract, ContractsInternal.DataOf(contract)];
            });
            await ExpectCollected(references);
        }),
        Case("contracts.contract.takes-no-args-exactly-the-methods-whose-argument-schema-is-null", () =>
        {
            var counts = Contract.Local("counts", new()
            {
                Version = 1,
                Params = new(),
                Members = new()
                {
                    ["reset"] = Method(),
                    ["total"] = Method(null, Int()),
                    ["set"] = Method(Int()),
                    ["maybe"] = Method(Union(Nul(), Str())),
                },
            });
            var methods = ContractsInternal.CurrentVariants(counts)[ContractsInternal.Local].Methods
                .Select(entry => $"{entry.Key}:{ContractsInternal.TakesNoArgs(entry.Value)}");
            Sequence(["reset:True", "total:True", "set:False", "maybe:False"], methods, "takesNoArgs per method, in declaration order");
        }),
        Case("contracts.contract.variant-order-and-names-sort-as-links-index-them", () =>
        {
            Sequence(["editor", "reader"], ContractsInternal.VariantOrder(Notes), "variantOrder");
            var (variant, state, methods) = WireNames(Notes, "editor");
            Equal(0, variant, "the editor's index");
            Sequence(["count:0", "notes:1"], state.Select(entry => $"{entry.Key}:{entry.Value}"), "state indexes");
            Sequence(["add:0", "echo:1", "fail:2", "wait:3"], methods.Select(entry => $"{entry.Key}:{entry.Value}"), "method indexes");
            Equal(1, WireNames(Notes, "reader").Variant, "the reader's index");
            // Declaration order stays the data's own order.
            Sequence(["notes", "count"], ContractsInternal.CurrentVariants(Notes)["editor"].State.Keys, "declared state order");
        }),
    ];
}
