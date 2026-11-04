using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/data/errors.test.ts. Upstream's wrong pairings are type
// errors (@ts-expect-error); C# has no literal types, so a ChordError refuses them when it is made, and the case checks that refusal. The
// type-level half of "narrowing on code narrows reason" (expectTypeOf) has no C# counterpart; its run-time half is ported. A TypeError is
// an InvalidOperationException here: any exception that is no ChordError.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> DataErrorsCases() =>
    [
        Case("data.errors.chord-error-is-a-tagged-union-a-wrong-pairing-is-refused", () =>
        {
            Accepts(() => new ChordError("invalid", "m", reason: "args"), "invalid with a reason");
            Accepts(() => new ChordError("unavailable", "m"), "unavailable without a reason");
            Accepts(() => new ChordError("unavailable", "m", reason: "locked"), "unavailable with its reason");
            // Upstream's @ts-expect-error lines: refused when they run.
            Refuses(() => new ChordError("invalid", "m"), "reason null not allowed for invalid", "invalid requires a reason");
            Refuses(() => new ChordError("gone", "m", reason: "args"), "reason \"args\" not allowed for gone", "gone carries no reason");
            Refuses(() => new ChordError("unavailable", "m", reason: "part"), "reason \"part\" not allowed for unavailable", "part belongs to too_large");
            Refuses(() => new ChordError("teapot", "m"), "unknown code \"teapot\"", "an unknown code");
        }),
        Case("data.errors.narrowing-on-code-narrows-reason", () =>
        {
            Exception error = new ChordError("too_large", "m", reason: "part");
            Check(error is ChordError, "a ChordError is an Exception (upstream: an Error)");
            Equal("ChordError", error.GetType().Name, "its name");
            Equal("part", ((ChordError)error).Reason, "its reason");
        }),
        Case("data.errors.round-trips-through-json-without-stack-or-cause", () =>
        {
            var details = Parse("""{"path":"/text"}""");
            var error = new ChordError("invalid", "args/text: bad", reason: "args", details: details, cause: new InvalidOperationException("x"));
            var json = JsonNode.Parse(error.ToJson().ToJsonString());
            Json("""{"code":"invalid","reason":"args","message":"args/text: bad","details":{"path":"/text"}}""", json, "JSON.parse(JSON.stringify(error))");
            Sequence(["code", "reason", "message", "details"], Keys(json), "the wire form's keys");
            var back = DataInternal.ChordErrorFromJson(json);
            Check(back is not null, "chordErrorFromJson gives a ChordError");
            Json(json!.ToJsonString(), back!.ToJson(), "back.toJSON()");
            Json("""{"code":"gone","message":"m"}""", new ChordError("gone", "m").ToJson(), "gone's wire form");
            Sequence(["code", "message"], Keys(new ChordError("gone", "m").ToJson()), "no reason or details key when there are none");
        }),
        Case("data.errors.keeps-details-by-reference", () =>
        {
            var raw = Parse("""{"code":"gone","message":"m","details":{"large":[1,2,3]}}""");
            var details = raw["details"];
            Check(ReferenceEquals(details, DataInternal.ChordErrorFromJson(raw).Details), "details is the node given");
        }),
        Case("data.errors.keeps-json-null-details-apart-from-none", () =>
        {
            // Authored from errors.ts: toJSON omits details only when they are undefined, so JSON null details cross a link.
            var error = DataInternal.ChordErrorFromJson(Parse("""{"code":"gone","message":"m","details":null}"""));
            Check(error.HasDetails && error.Details is null, "null details are kept");
            Equal("""{"code":"gone","message":"m","details":null}""", error.ToJson().ToJsonString(), "and cross the link");
        }),
        .. new (string Case, string Raw, string Problem)[]
        {
            ("not-an-object", "\"x\"", "not an object"),
            ("null", "null", "not an object"),
            ("an-array", "[]", "not an object"),
            ("unknown-key", """{"code":"gone","message":"m","stack":"s"}""", "unknown key \"stack\""),
            ("unknown-code", """{"code":"teapot","message":"m"}""", "unknown code"),
            ("inherited-name-as-code", """{"code":"toString","message":"m"}""", "unknown code"),
            ("code-not-a-string", """{"code":1,"message":"m"}""", "unknown code"),
            ("invalid-without-reason", """{"code":"invalid","message":"m"}""", "reason not allowed for invalid"),
            ("reason-not-allowed", """{"code":"gone","reason":"args","message":"m"}""", "reason not allowed for gone"),
            ("reason-of-another-code", """{"code":"unavailable","reason":"part","message":"m"}""", "reason not allowed for unavailable"),
            ("message-not-a-string", """{"code":"gone","message":1}""", "message is not a string"),
        }.Select(row => Case($"data.errors.chord-error-from-json-refuses-{row.Case}", () =>
            Refuses(() => DataInternal.ChordErrorFromJson(JsonNode.Parse(row.Raw)), $"Invalid ChordError JSON: {row.Problem}"))),
        Case("data.errors.protocol-table-is-pinned-together-with-protocol-version", () =>
        {
            const string pinned = """
                {"invalid":["args","params","member","host_address","reentrant","read_after_bulk","cycle"],"contract_mismatch":["updating"],"forbidden":["revoked"],"gone":[],"not_ready":[],"released":["ended","closed","settled","version","discarded"],"too_large":["part"],"unavailable":["locked","park_timeout","busy","storage_failed","undeclared"],"aborted":[]}
                """;
            var table = new JsonObject();
            foreach (var (code, reasons) in DataInternal.ChordErrorReasons) table[code] = new JsonArray([.. reasons.Select(reason => (JsonNode?)reason)]);
            var changed = DataInternal.ProtocolVersion != 1 || ChordJson.Stringify(table) != pinned;
            Check(!changed, "the error table changed: bump PROTOCOL_VERSION and update this pin");
        }),
        Case("data.errors.is-chord-error-checks-the-class-and-optionally-the-code", () =>
        {
            var error = DataInternal.Invalid("member", "no method");
            Check(ChordError.Is(error), "a ChordError");
            Check(ChordError.Is(error, "invalid"), "with its code");
            Check(!ChordError.Is(error, "gone"), "not with another code");
            Check(!ChordError.Is(new InvalidOperationException("x")), "another error");
            Check(!ChordError.Is(Parse("""{"code":"invalid","reason":"member","message":"m"}""")), "its JSON");
        }),
        Case("data.errors.invalid-builds-an-invalid-error-with-its-reason-and-details", () =>
        {
            var error = DataInternal.Invalid("params", "params/a: missing", Parse("""{"path":"/a"}"""));
            Json("""{"code":"invalid","reason":"params","message":"params/a: missing","details":{"path":"/a"}}""", error.ToJson(), "invalid(...).toJSON()");
            Equal("invalid", error.Code, "its code");
        }),
        Case("data.errors.as-chord-error-keeps-a-chord-error-and-wraps-anything-else-as-unavailable", () =>
        {
            var chord = DataInternal.Invalid("cycle", "c");
            Check(ReferenceEquals(chord, DataInternal.AsChordError(chord, "open")), "a ChordError is kept");
            var foreign = new InvalidOperationException("boom");
            var wrapped = DataInternal.AsChordError(foreign, "open notes");
            Json("""{"code":"unavailable","message":"open notes: boom"}""", wrapped.ToJson(), "the wrapped error");
            Check(ReferenceEquals(foreign, wrapped.Cause), "its cause");
            Check(ReferenceEquals(foreign, wrapped.InnerException), "its inner exception");
            Equal("open: text", DataInternal.AsChordError("text", "open").Message, "a thrown string");
        }),
        Case("data.errors.plugin-error-carries-a-plugins-code-message-and-json-details", () =>
        {
            Exception error = new PluginError("quota", "too many", Parse("""{"limit":3}"""));
            Equal("PluginError", error.GetType().Name, "its name");
            var plugin = (PluginError)error;
            Equal("quota", plugin.Code, "its code");
            Equal("too many", plugin.Message, "its message");
            Json("""{"limit":3}""", plugin.Details, "its details");
            Check(new PluginError("c", "m").Details is null, "no details");
        }),
    ];
}
