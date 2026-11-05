using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/definition.test.ts. Upstream's expectTypeOf
// checks of StateDefinition<T, K> are type-level, with no C# counterpart (a document is a JsonObject); the keyed flags they come with are
// checked. `defineState(42 as never, ...)` is the untyped door, a null name here. vi.fn's call count is a counter in initial(). A
// migrate's `old.count * from` is NaN for a string count, as JavaScript's is. Errors at use are InvalidOperationExceptions with upstream's
// messages (Definition.cs). The `pisharp-` cases check what upstream's types rule out (persist and checkpointWhen, refused when Define
// runs), the checkpoint rule's default, and CommitPersistence.
internal static partial class Program
{
    private static StateOptions DefinitionOptions() => new() { Schema = Obj(("n", Int())), Version = 1, Initial = () => DocumentsN(0) };

    /// <summary>expect(run).toThrow(message) for an error at use: an InvalidOperationException whose message contains
    /// <paramref name="message"/>.</summary>
    private static void DocumentsFails(Action run, string message)
    {
        var error = Throws<InvalidOperationException>(run, message);
        Check(error.Message.Contains(message, StringComparison.Ordinal), $"expected a message containing <{message}>, actual <{error.Message}>.");
    }

    private static IEnumerable<(string, Func<Task>)> DocumentsDefinitionCases() =>
    [
        Case("documents.definition.definestate-types-the-value-and-whether-it-is-keyed", () =>
        {
            var notes = StateDefinition.Define("notes", new() { Schema = Obj(("list", Arr(Str()))), Version = 1, Initial = () => new() { ["list"] = new JsonArray() } });
            var drafts = StateDefinition.Define("drafts", new() { Schema = Obj(("n", Int())), Version = 1, Initial = () => DocumentsN(0), Keyed = true });
            Sequence([false, true], [notes.Keyed, drafts.Keyed], "the keyed flags");
        }),
        Case("documents.definition.definestate-takes-a-restricted-name-only-lowercase-a-z0-9-decision-restricted-names", () =>
        {
            foreach (var name in new[] { "@app/notes.v_1~x-y", "n" }) Equal(name, StateDefinition.Define(name, DefinitionOptions()).Name, name);
            foreach (var (name, quoted) in new[]
            {
                ("", "\"\""), ("Notes", "\"Notes\""), ("app:notes", "\"app:notes\""), ("app notes", "\"app notes\""), ("a\0b", "\"a\\u0000b\""),
                ("a\uD800", "\"a\\ud800\""), ("é", "\"é\""),
            })
            {
                Refuses(() => StateDefinition.Define(name, DefinitionOptions()), $"A state name {quoted}: expected lowercase [a-z0-9@/._~-]", quoted);
            }
            // Plugin code, not type-checked.
            Refuses(() => StateDefinition.Define(null!, DefinitionOptions()), "A state name null");
        }),
        Case("documents.definition.initial-r8-is-checked-against-the-schema-once-the-first-time-it-is-used", async () =>
        {
            var metered = await Metered();
            var calls = 0;
            var doc = StateDefinition.Define("doc", new()
            {
                Schema = Obj(("n", Int())),
                Version = 1,
                Initial = () =>
                {
                    calls++;
                    return DocumentsN(0);
                },
            });
            Json("""{"n":0}""", doc.Initial(metered.Context), "the first value");
            Json("""{"n":0}""", doc.Initial(metered.Context), "the second value");
            Equal(2, calls, "initial()'s calls");
            Sequence(["chord.data.compilations=1", "chord.data.validations=1"],
                metered.Counts().OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}"), "the counts");
        }),
        Case("documents.definition.initial-r8-refuses-a-value-that-does-not-match-every-time-until-one-does", () =>
        {
            JsonObject value = new() { ["n"] = "x" };
            var doc = StateDefinition.Define("doc", new() { Schema = Obj(("n", Int())), Version = 1, Initial = () => value });
            DocumentsFails(() => doc.Initial(ChordContext.Background), "doc: initial() does not match the schema");
            DocumentsFails(() => doc.Initial(ChordContext.Background), "doc: initial() does not match the schema");
            value = DocumentsN(1);
            Json("""{"n":1}""", doc.Initial(ChordContext.Background), "a matching value");
        }),
        Case("documents.definition.fingerprints-are-computed-once-from-the-schemas-structure-descriptions-ignored-a-structural-change-seen", () =>
        {
            static string Make(JsonNode schema) => StateDefinition.Define("doc", new() { Schema = schema, Version = 1, Initial = () => DocumentsN(0) }).Fingerprint;
            var plain = Make(Obj(("n", Int())));
            Check(Regex.IsMatch(plain, "^[0-9a-f]{16}\\z"), $"a fingerprint of 16 hex digits: {plain}");
            var described = Int();
            described["description"] = "a count";
            Equal(plain, Make(Obj(("n", described))), "a description");
            Check(Make(Obj(("n", Str()))) != plain, "a structural change");
        }),
        Case("documents.definition.migrate-r8-maps-an-older-value-and-checks-the-result-against-the-schema-each-time-it-produces-one", async () =>
        {
            var metered = await Metered();
            var doc = StateDefinition.Define("doc", new()
            {
                Schema = Obj(("n", Int())),
                Version = 2,
                Initial = () => DocumentsN(0),
                Migrate = (old, from) => new() { ["n"] = (ChordJson.AsNumber(old["count"]) ?? double.NaN) * from },
            });
            Json("""{"n":3}""", doc.Migrate(new() { ["count"] = 3 }, 1, metered.Context), "the migrated value");
            DocumentsFails(() => doc.Migrate(new() { ["count"] = "x" }, 1, metered.Context), "doc: migrate() from version 1 does not match the schema");
            Equal(2.0, metered.Counts()["chord.data.validations"], "the validations");
        }),
        Case("documents.definition.migrate-r8-refuses-an-older-value-without-a-migrate", () =>
        {
            var doc = StateDefinition.Define("doc", new() { Schema = Obj(("n", Int())), Version = 2, Initial = () => DocumentsN(0) });
            DocumentsFails(() => doc.Migrate(DocumentsN(1), 1, ChordContext.Background), "doc: no migrate from version 1 to 2");
        }),
        Case("documents.definition.pisharp-definestate-refuses-persist-without-checkpointwhen-and-checkpointwhen-without-persist", () =>
        {
            Refuses(() => StateDefinition.Define("doc", new() { Schema = Obj(("n", Int())), Version = 1, Initial = () => DocumentsN(0), Persist = true }),
                "doc: persist: true requires checkpointWhen");
            Refuses(() => StateDefinition.Define("doc", new()
            {
                Schema = Obj(("n", Int())),
                Version = 1,
                Initial = () => DocumentsN(0),
                CheckpointWhen = (_, _, _) => true,
            }), "doc: checkpointWhen requires persist: true");
            Refuses(() => StateDefinition.Define("doc", null!), "options");
            Refuses(() => StateDefinition.Define("doc", new() { Schema = Obj(("n", Int())), Version = 1, Initial = null! }), "Initial");
        }),
        Case("documents.definition.pisharp-a-persisted-definition-asks-its-checkpoint-rule-an-in-memory-one-never-checkpoints", () =>
        {
            var asked = new List<string>();
            var persisted = StateDefinition.Define("doc", new()
            {
                Schema = Obj(("n", Int())),
                Version = 1,
                Initial = () => DocumentsN(0),
                Persist = true,
                CheckpointWhen = (value, ops, info) =>
                {
                    asked.Add($"{ChordJson.Stringify(value)} {OpsJson(ops).ToJsonString()} {info.DeltasSinceBase}");
                    return info.DeltasSinceBase >= 2;
                },
            });
            ImmutableArray<Op> ops = [DocumentsSetN(1)];
            Sequence([true, false, true], [persisted.Persist, persisted.CheckpointWhen(DocumentsN(1), ops, new(1)), persisted.CheckpointWhen(DocumentsN(1), ops, new(2))],
                "the flag and the rule's answers");
            Sequence(["""{"n":1} [["s",["n"],1]] 1""", """{"n":1} [["s",["n"],1]] 2"""], asked, "what the rule was asked");
            var memory = StateDefinition.Define("doc", DefinitionOptions());
            Sequence([false, false], [memory.Persist, memory.CheckpointWhen(DocumentsN(1), ops, new(100))], "an in-memory definition");
        }),
        Case("documents.definition.pisharp-commit-persistence-is-immediate-or-deferred-within-an-optional-delay", () =>
        {
            Sequence(["immediate", "deferred", "deferred"], [CommitPersistence.Immediate.Kind, CommitPersistence.Deferred().Kind, CommitPersistence.Deferred(250).Kind], "the kinds");
            Sequence<double?>([null, null, 250.0], [CommitPersistence.Immediate.WithinMs, CommitPersistence.Deferred().WithinMs, CommitPersistence.Deferred(250).WithinMs], "the delays");
            Equal(CommitPersistence.Deferred(250), CommitPersistence.Deferred(250), "a record's equality");
        }),
    ];
}
