using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/data/validator.test.ts. A Uint8Array is a JsonValue holding a
// byte[]. "The check function too" is the compiled pi-schema check a Validator holds (upstream's validator.check).
internal static partial class Program
{
    private static readonly Snapshot ValidatorArgs = ContractsInternal.SnapshotArguments(
        Obj(("text", Str()), ("items", Arr(Obj(("n", Int())))), ("tags", Map(Str())), ("blob", Bytes(max: 2))), "m.args");

    /// <summary>A fresh valid value (a JsonNode has one parent), edited by <paramref name="edit"/>.</summary>
    private static JsonObject ValidatorValid(Action<JsonObject>? edit = null)
    {
        var value = new JsonObject
        {
            ["text"] = "t",
            ["items"] = new JsonArray(new JsonObject { ["n"] = 1 }),
            ["tags"] = new JsonObject(),
            ["blob"] = JsonValue.Create(new byte[1]),
        };
        edit?.Invoke(value);
        return value;
    }

    /// <summary>The <c>{ reason, message, details }</c> of the <c>invalid/args</c> error a failed check throws, or "passed".</summary>
    private static JsonNode ValidatorFailure(JsonNode? value)
    {
        try
        {
            DataInternal.Check(DataInternal.ValidatorFor(ValidatorArgs, ChordContext.Background), value, "args", ChordContext.Background);
        }
        catch (ChordError error) when (ChordError.Is(error, "invalid"))
        {
            return new JsonObject { ["reason"] = error.Reason, ["message"] = error.Message, ["details"] = error.Details?.DeepClone() };
        }
        return "passed";
    }

    private static IEnumerable<(string, Func<Task>)> DataValidatorCases() =>
    [
        Case("data.validator.failed-checks-pass-a-valid-value", () => Json("\"passed\"", ValidatorFailure(ValidatorValid()), "a valid value")),
        Case("data.validator.failed-checks-report-the-first-error-with-its-json-pointer-path", () =>
        {
            Json("""{"reason":"args","message":"args/text: expected string, got integer","details":{"path":"/text"}}""",
                ValidatorFailure(ValidatorValid(value => value["text"] = 1)), "a wrong field");
            Json("""{"path":"/items/0/n"}""", ValidatorFailure(ValidatorValid(value => value["items"] = new JsonArray(new JsonObject { ["n"] = "x" })))["details"],
                "a nested wrong field");
            Json("""{"reason":"args","message":"args: expected object, got null","details":{"path":"/"}}""", ValidatorFailure(null), "the value itself");
        }),
        Case("data.validator.failed-checks-report-a-missing-field-at-its-own-path", () =>
        {
            var missing = ValidatorFailure(ValidatorValid(value => value.Remove("text")));
            Equal("args/text: missing", missing["message"]!.GetValue<string>(), "the message");
            Json("""{"path":"/text"}""", missing["details"], "the path");
            Json("""{"path":"/items/0/n"}""", ValidatorFailure(ValidatorValid(value => value["items"] = new JsonArray(new JsonObject())))["details"],
                "a missing nested field");
        }),
        Case("data.validator.failed-checks-report-an-unknown-field-at-its-own-path-nested-too", () =>
        {
            Json("""{"reason":"args","message":"args/extra: unknown field","details":{"path":"/extra"}}""",
                ValidatorFailure(ValidatorValid(value => value["extra"] = 1)), "an unknown field");
            Json("""{"path":"/items/0/m"}""",
                ValidatorFailure(ValidatorValid(value => value["items"] = new JsonArray(new JsonObject { ["n"] = 1, ["m"] = 2 })))["details"], "a nested unknown field");
        }),
        Case("data.validator.failed-checks-check-bytes-by-instanceof-and-length", () =>
        {
            Equal("args/blob: expected bytes, got array", ValidatorFailure(ValidatorValid(value => value["blob"] = new JsonArray(1)))["message"]!.GetValue<string>(),
                "an array of numbers");
            var tooLong = ValidatorFailure(ValidatorValid(value => value["blob"] = JsonValue.Create(new byte[3])));
            Equal("args/blob: expected at most 2 bytes", tooLong["message"]!.GetValue<string>(), "3 bytes: the message");
            Json("""{"path":"/blob"}""", tooLong["details"], "3 bytes: the path");
        }),
        Case("data.validator.failed-checks-treat-format-as-an-annotation-no-format-is-checked", () =>
        {
            var owner = ContractsInternal.SnapshotArguments(Obj(("mail", Parse("""{"type":"string","format":"email"}"""))), "m");
            var validator = DataInternal.ValidatorFor(owner, ChordContext.Background);
            Check(validator.Check(Parse("""{"mail":"a@b.c"}""")), "an email");
            Check(validator.Check(Parse("""{"mail":"nope"}""")), "no email");
        }),
        Case("data.validator.failed-checks-escape-tilde-and-slash-in-keys-so-a-path-names-one-place", () =>
            Json("""{"path":"/tags/a~1b"}""", ValidatorFailure(ValidatorValid(value => value["tags"] = new JsonObject { ["a/b"] = 1, ["c~d"] = "x" }))["details"],
                "the escaped path")),
        Case("data.validator.validators-compile-once-per-schema-owner-counted-then-validate-counted-a-failure-adds-one-walk", async () =>
        {
            var owner = ContractsInternal.SnapshotArguments(Obj(("a", Str())), "m");
            var first = await Counted(context => DataInternal.Check(DataInternal.ValidatorFor(owner, context), Parse("""{"a":"x"}"""), "args", context));
            var second = await Counted(context => DataInternal.Check(DataInternal.ValidatorFor(owner, context), Parse("""{"a":"y"}"""), "args", context));
            var failed = await Counted(context =>
                Throws<ChordError>(() => DataInternal.Check(DataInternal.ValidatorFor(owner, context), new JsonObject(), "args", context), "a failed check"));
            Sequence([new Counts(0, 1, 1), new Counts(0, 1, 0), new Counts(1, 1, 0)], [first, second, failed], "the counts");
        }),
        Case("data.validator.validators-are-collected-with-their-schema-owner-the-check-function-too", async () => await ExpectCollected(Weakly(() =>
        {
            var owner = ContractsInternal.SnapshotArguments(Obj(("blob", Bytes()), ("n", Int())), "m");
            var validator = DataInternal.ValidatorFor(owner, ChordContext.Background);
            return [owner, validator, validator.Compiled];
        }))),
    ];
}
