// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-json.test.ts (89c921d8b), ported in the Pi v1.5.0 sync
// (area partial-commits). Upstream's case guards assignJson against reading a missing `__proto__` member through to Object.prototype and
// merging into it. A C# draft is a keyed map with no prototype, so nothing can be polluted; the case checks what upstream's assertions
// leave to check: the `__proto__` member is a plain member of the arguments, assigned whole, and nothing else in the document changed.
// The plan cases after it are PiSharp-authored: they check HarnessJson's AssignPlan (da412b039), which upstream tests only through
// harness-partial-changes.test.ts (HarnessPartialChangesCases.cs).
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

internal static partial class Program
{
    /// <summary>Upstream's assign(): one change that assigns <paramref name="value"/> at <c>message</c>; returns its ops.</summary>
    private static ImmutableArray<Op> HarnessJsonAssign(Tracker tracker, JsonValue value, AssignPlan? plan = null)
    {
        var change = tracker.BeginChange();
        HarnessJson.AssignJson(Root(change), "message", value, plan);
        var prepared = change.Prepare();
        Replays(prepared);
        tracker.Adopt(prepared);
        return prepared.Ops;
    }

    private static AssignPlan.Fields HarnessJsonMembers(string name, AssignPlan plan) =>
        new(Members: new Dictionary<string, AssignPlan>(StringComparer.Ordinal) { [name] = plan });

    private static AssignPlan.Fields HarnessJsonItems(int index, AssignPlan plan) => new(Items: new Dictionary<int, AssignPlan> { [index] = plan });

    private static IEnumerable<(string, Func<Task>)> HarnessJsonCases() =>
    [
        Case("harness.json.does-not-merge-a-new-proto-member-into-object-prototype", () =>
        {
            var tracker = Tracker.Track(J("{}"));
            HarnessJsonAssign(tracker, J("{\"args\":{\"path\":\"x\"}}"));
            var ops = HarnessJsonAssign(tracker, J("{\"args\":{\"path\":\"xy\",\"__proto__\":{\"polluted\":true}}}"));
            // Upstream: the arguments' own `__proto__` member holds the object, and Object.prototype gained nothing.
            Same("{\"polluted\":true}", At(tracker.Value, "message", "args", "__proto__"), "the __proto__ member");
            Same("{\"message\":{\"args\":{\"path\":\"xy\",\"__proto__\":{\"polluted\":true}}}}", tracker.Value, "the document");
            // Chord writes the small object whole here; the ops replay to that document (HarnessJsonAssign's Replays).
            Check(ops.Length > 0, "the change has ops");
        }),
        // PiSharp-authored from here (see the header).
        Case("harness.json.appends-a-planned-text-to-a-string-member-whose-new-length-it-explains", () =>
        {
            var tracker = Tracker.Track(J("{}"));
            HarnessJsonAssign(tracker, J("{\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}"));
            AssignPlan Plan(string text) => HarnessJsonMembers("content", HarnessJsonItems(0, HarnessJsonMembers("text", new AssignPlan.Append(text))));
            var ops = HarnessJsonAssign(tracker, J("{\"content\":[{\"type\":\"text\",\"text\":\"Hello world\"}]}"), Plan(" world"));
            Same("[[\"a\",[\"message\",\"content\",0,\"text\"],\" world\"]]", OpsJson(ops), "an append");
            // Only the lengths are checked, as upstream checks them: the planned text is what lands, so the strings were not compared.
            HarnessJsonAssign(tracker, J("{\"content\":[{\"type\":\"text\",\"text\":\"Hello world!!\"}]}"), Plan("??"));
            Same("{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hello world??\"}]}}", tracker.Value, "the planned text");
            // A plan whose text does not explain the new length is ignored: the strings are compared, as without a plan.
            ops = HarnessJsonAssign(tracker, J("{\"content\":[{\"type\":\"text\",\"text\":\"replaced\"}]}"), Plan(" more"));
            Same("[[\"s\",[\"message\",\"content\",0,\"text\"],\"replaced\"]]", OpsJson(ops), "a set");
        }),
        Case("harness.json.leaves-a-kept-member-to-the-caller-and-assigns-the-rest", () =>
        {
            var tracker = Tracker.Track(J("{}"));
            HarnessJsonAssign(tracker, J("{\"content\":[{\"type\":\"toolCall\",\"id\":\"c1\",\"arguments\":{\"a\":\"x\"}}]}"));
            var plan = HarnessJsonMembers("content", HarnessJsonItems(0, HarnessJsonMembers("arguments", AssignPlan.Keep)));
            var ops = HarnessJsonAssign(tracker, J("{\"content\":[{\"type\":\"toolCall\",\"id\":\"c2\",\"arguments\":{}}]}"), plan);
            Same("[[\"s\",[\"message\",\"content\",0,\"id\"],\"c2\"]]", OpsJson(ops), "ops");
            Same("{\"message\":{\"content\":[{\"type\":\"toolCall\",\"id\":\"c2\",\"arguments\":{\"a\":\"x\"}}]}}", tracker.Value, "the document");
            // A plan applies inside a value the draft has: a new one is set whole, kept members too (PartialChanges.Take clears what
            // it keeps).
            var fresh = Tracker.Track(J("{}"));
            HarnessJsonAssign(fresh, J("{\"note\":\"n\",\"arguments\":{\"a\":1}}"), HarnessJsonMembers("arguments", AssignPlan.Keep));
            Same("{\"message\":{\"note\":\"n\",\"arguments\":{\"a\":1}}}", fresh.Value, "a new message");
        }),
        Case("harness.json.ignores-an-append-plan-for-an-array-item", () =>
        {
            var tracker = Tracker.Track(J("{}"));
            HarnessJsonAssign(tracker, J("{\"list\":[\"ab\"]}"));
            // AppendText takes object members (upstream's appendText() too): the item is compared, so the planned text does not land.
            HarnessJsonAssign(tracker, J("{\"list\":[\"abc\"]}"), HarnessJsonMembers("list", HarnessJsonItems(0, new AssignPlan.Append("X"))));
            Same("{\"message\":{\"list\":[\"abc\"]}}", tracker.Value, "the document");
        }),
    ];
}
