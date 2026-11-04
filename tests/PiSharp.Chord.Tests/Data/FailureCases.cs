using PiSharp.Chord.Data;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/data/failure.test.ts. toEqual on a failure cause is the
// record's equality: the same kind and the same ChordError instance.
internal static partial class Program
{
    private static readonly ChordError FailureRoot = new("unavailable", "app.catalog is not served here");

    private static IEnumerable<(string, Func<Task>)> DataFailureCases() =>
    [
        Case("data.failure.a-value-thrown-by-an-open-is-transient-unless-marked-broken", () =>
        {
            var error = new InvalidOperationException("network down");
            var transient = DataInternal.OpenFailure(error, "open notes");
            Equal(FailureKind.Transient, transient.Kind, "the kind");
            Json("""{"code":"unavailable","message":"open notes: network down"}""", transient.Error.ToJson(), "the error");
            Check(ReferenceEquals(error, transient.Error.Cause), "its cause");
        }),
        Case("data.failure.broken-marks-an-opens-failure-as-broken-its-message-is-the-causes", () =>
        {
            var marked = Failure.Broken(new InvalidOperationException("bad config"));
            Equal("bad config", marked.Message, "broken()'s message");
            var cause = DataInternal.OpenFailure(marked, "open notes");
            Equal(FailureKind.Broken, cause.Kind, "the kind");
            Equal("open notes: bad config", cause.Error.Message, "the message");
        }),
        Case("data.failure.the-kind-comes-from-the-stage-never-from-the-error-code", () =>
        {
            var chord = DataInternal.Invalid("host_address", "wrong host");
            Equal(new FailureCause(FailureKind.Transient, chord), DataInternal.OpenFailure(chord, "open"), "a thrown ChordError");
            Equal(new FailureCause(FailureKind.Broken, chord), DataInternal.OpenFailure(Failure.Broken(chord), "open"), "a broken ChordError");
        }),
        Case("data.failure.names-the-nearest-blocker-and-the-root-cause-its-blockers-error-as-the-cause", () =>
        {
            var cause = DataInternal.WaitingFor("app.chat(S1)", FailureRoot);
            Equal(FailureKind.Waiting, cause.Kind, "the kind");
            Json("""{"code":"unavailable","message":"waiting for app.chat(S1): app.catalog is not served here"}""", cause.Error.ToJson(), "the error");
            Check(ReferenceEquals(FailureRoot, cause.Error.Cause), "its cause");
            // A chain names its nearest blocker and its root only (fix round 1, Opus F1).
            var chain = DataInternal.WaitingFor("app.panel", cause.Error);
            Equal("waiting for app.panel: app.catalog is not served here", chain.Error.Message, "a chain");
        }),
        Case("data.failure.reaches-a-fixed-point-when-a-ring-of-waiting-attempts-passes-its-causes-round", () =>
        {
            var b = DataInternal.WaitingFor("X", new ChordError("unavailable", "X is not served here")).Error;
            var rounds = 0;
            for (; rounds < 10; rounds++)
            {
                var next = DataInternal.WaitingFor("A", DataInternal.WaitingFor("B", b).Error).Error;
                if (next.Message == b.Message) break;
                b = next;
            }
            Check(rounds <= 1, $"at most one round, actual {rounds}");
            Equal("waiting for A: X is not served here", b.Message, "the fixed point");
        }),
        Case("data.failure.keeps-the-root-cause-within-500-characters-a-long-label-is-clipped-first", () =>
        {
            var original = new ChordError("unavailable", "ORIGINAL: missing provider");
            var message = DataInternal.WaitingFor($"sol.item({new string('x', 600)})", original).Error.Message;
            Check(message.Length <= 500, $"within 500 characters, actual {message.Length}");
            Check(message.Contains(original.Message, StringComparison.Ordinal), "the root cause is kept");
            Check(message.StartsWith("waiting for sol.item(xxx", StringComparison.Ordinal), "the label is clipped: " + message[..40]);
        }),
        Case("data.failure.keeps-a-long-chains-root-cause-never-repeated-ellipses", () =>
        {
            var error = new ChordError("unavailable", "ORIGINAL: missing provider");
            for (var i = 0; i < 2000; i++) error = DataInternal.WaitingFor($"level-{i}", error).Error;
            Equal("waiting for level-1999: ORIGINAL: missing provider", error.Message, "the chain's message");
        }),
        Case("data.failure.clips-a-long-root-past-its-share-and-both-when-both-are-long", () =>
        {
            var longRoot = new ChordError("unavailable", new string('y', 600));
            var shortLabel = DataInternal.WaitingFor("app.a", longRoot).Error.Message;
            Equal(500, shortLabel.Length, "a long root's length");
            Check(shortLabel.StartsWith("waiting for app.a: yyy", StringComparison.Ordinal), "the label is kept");
            var both = DataInternal.WaitingFor(new string('z', 600), longRoot).Error.Message;
            Equal(500, both.Length, "both long: the length");
            Check(both.EndsWith("y…", StringComparison.Ordinal), "both long: the root is clipped");
        }),
        Case("data.failure.is-compared-by-value-kind-code-reason-and-message", () =>
        {
            Check(DataInternal.SameCause(DataInternal.WaitingFor("a", FailureRoot), DataInternal.WaitingFor("a", FailureRoot)), "the same blocker");
            Check(!DataInternal.SameCause(DataInternal.WaitingFor("a", FailureRoot), DataInternal.WaitingFor("b", FailureRoot)), "another blocker");
            Check(!DataInternal.SameCause(new FailureCause(FailureKind.Broken, FailureRoot), new FailureCause(FailureKind.Transient, FailureRoot)), "another kind");
            var other = new ChordError("unavailable", FailureRoot.Message, reason: "undeclared");
            Check(!DataInternal.SameCause(new FailureCause(FailureKind.Waiting, FailureRoot), new FailureCause(FailureKind.Waiting, other)), "another reason");
        }),
    ];
}
