using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/data/params.test.ts, and the checkAddress case of
// packages/chord/test/next/contracts/host-kind.test.ts ("check an address's params where it is routed"), kept here with params.ts's other
// checks so HostKindCases.cs stays as area cv2-contracts left it. toBe on params is reference equality on the caller's JsonObject.
// expectTypeOf of the checked params has no C# counterpart (they are a JsonObject). identityKey's @ts-expect-error lines are refused when
// they run: the two-argument overload refuses a remote contract and the three-argument one a local contract.
internal static partial class Program
{
    private static readonly Contract DataParamsSession = Contract.Remote("session", new()
    {
        Version = 1,
        Params = new() { ["id"] = Str(min: 1), ["mode"] = OneOf("chat", "agent"), ["page"] = Int(min: 0), ["open"] = Bool() },
        HostAddress = _ => Server.Address(),
        Variants = new() { ["owner"] = new() },
    });

    /// <summary>A fresh valid params object, edited by <paramref name="edit"/>.</summary>
    private static JsonObject DataParamsValid(Action<JsonObject>? edit = null)
    {
        var value = new JsonObject { ["id"] = "S1", ["mode"] = "chat", ["page"] = 2, ["open"] = true };
        edit?.Invoke(value);
        return value;
    }

    /// <summary>The <c>{ reason, message, details }</c> of the <c>invalid/params</c> error of a refused check.</summary>
    private static JsonObject DataParamsRefusal(JsonNode? raw)
    {
        try
        {
            DataInternal.CheckParams(DataParamsSession, raw, ChordContext.Background);
        }
        catch (ChordError error) when (ChordError.Is(error, "invalid"))
        {
            return new JsonObject { ["reason"] = error.Reason, ["message"] = error.Message, ["details"] = error.Details?.DeepClone() };
        }
        throw new InvalidOperationException("expected a refusal");
    }

    private static IEnumerable<(string, Func<Task>)> DataParamsCases() =>
    [
        Case("data.params.check-params-returns-the-callers-own-object-and-the-key-of-the-field-values-in-declared-order", () =>
        {
            var valid = DataParamsValid();
            var (@params, key) = DataInternal.CheckParams(DataParamsSession, valid, ChordContext.Background);
            Check(ReferenceEquals(valid, @params), "the caller's own object");
            Equal("S1|chat|2|true", key, "the key");
        }),
        Case("data.params.check-params-gives-the-same-key-whatever-the-property-order", () =>
            Equal("S1|chat|2|true", DataInternal.CheckParams(DataParamsSession,
                new JsonObject { ["open"] = true, ["page"] = 2, ["mode"] = "chat", ["id"] = "S1" }, ChordContext.Background).Key, "the key")),
        Case("data.params.check-params-refuses-missing-extra-and-wrong-fields-at-their-paths", () =>
        {
            var missing = DataParamsRefusal(DataParamsValid(value => value.Remove("id")));
            Equal("params", missing["reason"]!.GetValue<string>(), "missing: the reason");
            Check(Regex.IsMatch(missing["message"]!.GetValue<string>(), "^params/id: "), "missing: the message " + missing["message"]);
            Json("""{"path":"/id"}""", missing["details"], "missing: the path");
            var extra = DataParamsRefusal(DataParamsValid(value => value["extra"] = 1));
            Check(Regex.IsMatch(extra["message"]!.GetValue<string>(), "^params/extra: "), "extra: the message " + extra["message"]);
            Json("""{"path":"/extra"}""", extra["details"], "extra: the path");
            var wrong = DataParamsRefusal(DataParamsValid(value => value["page"] = "2"));
            Equal("params/page: expected integer, got string", wrong["message"]!.GetValue<string>(), "wrong: the message");
            Json("""{"path":"/page"}""", wrong["details"], "wrong: the path");
            var scalar = DataParamsRefusal("S1");
            Equal("params: expected object, got string", scalar["message"]!.GetValue<string>(), "a string: the message");
            Json("""{"path":"/"}""", scalar["details"], "a string: the path");
        }),
        Case("data.params.check-params-enforces-literal-membership-and-constraints", () =>
        {
            Json("""{"path":"/mode"}""", DataParamsRefusal(DataParamsValid(value => value["mode"] = "other"))["details"], "a literal outside the set");
            Json("""{"path":"/id"}""", DataParamsRefusal(DataParamsValid(value => value["id"] = ""))["details"], "below the minimum length");
            Json("""{"path":"/page"}""", DataParamsRefusal(DataParamsValid(value => value["page"] = -1))["details"], "below the minimum");
            Json("""{"path":"/page"}""", DataParamsRefusal(DataParamsValid(value => value["page"] = 1.5))["details"], "not an integer");
        }),
        Case("data.params.check-params-escapes-the-separator-no-two-params-join-to-one-key", () =>
        {
            var pair = Contract.Local("pair", new() { Version = 1, Params = new() { ["a"] = Str(), ["b"] = Str() }, Members = new() });
            var keys = new[] { ("x|y", "z"), ("x", "y|z"), ("x\\", "|z"), ("x\\|", "z") }
                .Select(row => DataInternal.CheckParams(pair, new JsonObject { ["a"] = row.Item1, ["b"] = row.Item2 }, ChordContext.Background).Key).ToList();
            Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count(), "distinct keys: " + string.Join(" ", keys));
            Equal("x\\|y|z", keys[0], "the first key");
        }),
        Case("data.params.check-params-counts-one-compilation-per-contract-then-one-validation-per-get-a-failure-adds-one-walk", async () =>
        {
            var fresh = Contract.Local("fresh", new() { Version = 1, Params = new() { ["id"] = Str() }, Members = new() });
            Equal(new Counts(0, 1, 1), await Counted(context => DataInternal.CheckParams(fresh, new JsonObject { ["id"] = "a" }, context)), "the first get");
            Equal(new Counts(0, 1, 0), await Counted(context => DataInternal.CheckParams(fresh, new JsonObject { ["id"] = "b" }, context)), "the second get");
            var failed = await Counted(context => Throws<ChordError>(() => DataInternal.CheckParams(fresh, new JsonObject { ["id"] = 1 }, context), "a failed get"));
            Equal(new Counts(1, 1, 0), failed, "a failed get");
        }),
        Case("data.params.check-params-does-not-retain-a-dropped-contracts-params-validator", async () => await ExpectCollected(Weakly(() =>
        {
            var contract = Contract.Local("gc", new() { Version = 1, Params = new() { ["id"] = Str() }, Members = new() });
            DataInternal.CheckParams(contract, new JsonObject { ["id"] = "a" }, ChordContext.Background);
            var validator = DataInternal.ValidatorFor(ContractsInternal.DataOf(contract).Params, ChordContext.Background);
            return [contract, validator];
        }))),
        Case("data.params.identity-key-joins-name-key-and-principal-key-each-escaped", () =>
            Equal("session|S1\\|chat\\|2\\|true|{\"kind\":\"user\"}", DataInternal.IdentityKey(DataParamsSession, "S1|chat|2|true", """{"kind":"user"}"""),
                "the identity key")),
        Case("data.params.identity-key-local-objects-take-no-principal-key", () =>
        {
            var info = Contract.Local("info", new() { Version = 1, Params = new(), Members = new() });
            Equal("info|", DataInternal.IdentityKey(info, ""), "a local identity key");
            // Upstream's @ts-expect-error lines, refused when they run.
            Refuses(() => DataInternal.IdentityKey(info, "", "p"), "info: a local object's identity has no principal key");
            Refuses(() => DataInternal.IdentityKey(DataParamsSession, "k"), "session: a remote object's identity needs a principal key");
        }),
    ];

    private static IEnumerable<(string, Func<Task>)> ContractsHostKindCheckAddressCases() =>
    [
        Case("contracts.host-kind.check-an-addresss-params-where-it-is-routed-with-the-kinds-compiled-check-invalid-host-address", () =>
        {
            var @params = ContractsInternal.KindParams(Session);
            var address = DataInternal.CheckAddress("session", @params, new JsonObject { ["id"] = "S1" }, ChordContext.Background);
            Equal("session|S1", address.Key, "the key");
            foreach (var bad in new[] { """{"id":1}""", "{}", """{"id":"S1","extra":1}""", "null" })
            {
                var thrown = Throws<ChordError>(() => DataInternal.CheckAddress("session", @params, JsonNode.Parse(bad), ChordContext.Background), bad);
                Check(ChordError.Is(thrown, "invalid") && thrown.Reason == "host_address", $"{bad}: invalid/host_address, actual {thrown.Code}/{thrown.Reason}");
            }
        }),
    ];
}
