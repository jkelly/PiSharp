using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

internal static class SessionBranchPlannerTests
{
    private static readonly SessionEntryCodec Codec = new();
    private const string Time = "2026-10-02T12:00:00.000Z";
    private static readonly SessionEntry Header = Codec.Parse("{\"type\":\"session\",\"version\":3,\"id\":\"original\",\"timestamp\":\"" + Time +
        "\",\"cwd\":\"C:/fixture\",\"parentSession\":\"older.jsonl\",\"opaqueHeader\":1e400}");
    private static SessionEntry Entry(string id, string? parent = null, string type = "custom", string fields = "\"customType\":\"state\",\"data\":{\"number\":1.00e400,\"nil\":null}") =>
        Codec.Parse("{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" +
            JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Time + "\"," + fields + "}");
    private static SessionEntry Label(string id, string parent, string target, string? label, string time = Time) =>
        Codec.Parse("{\"type\":\"label\",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) +
            ",\"timestamp\":" + JsonSerializer.Serialize(time) + ",\"targetId\":" + JsonSerializer.Serialize(target) + ",\"label\":" + JsonSerializer.Serialize(label) + "}");
    private static SessionEntry User(string id, string? parent, string content) => Entry(id, parent, "message",
        "\"message\":{\"role\":\"user\",\"timestamp\":1,\"content\":" + content + "}");
    private static SessionForkPlanRequest Request(ImmutableArray<SessionEntry> source, string leaf,
        SessionForkPosition position = SessionForkPosition.At, ImmutableArray<string> labels = default) =>
        new(Header, source, leaf, position, "fresh", Time, "source.jsonl", labels);
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session fork reconnects ancestry and retains exact opaque numeric tokens", OpaqueAncestry),
        ("session fork resolves labels globally in pinned insertion order", GlobalLabels),
        ("session fork repairs compaction references to removed labels including self", CompactionReferences),
        ("session fork JSONL reload preserves label-remapped compaction context and physical leaf", CompactionJsonlReload),
        ("session fork before extracts concatenated user text and creates empty root branch", Before),
        ("session fork admission rejects invalid graph selection metadata and identity reuse", Admission),
        ("session branch bounds cancellation and eager bytes retain immutable source", BoundsAndBytes)
    ];
    private static Task OpaqueAncestry()
    {
        var source = ImmutableArray.Create(Entry("root"), Label("old-label", "root", "root", "title"),
            Entry("future", "old-label", "future_kind", "\"opaque\":{\"large\":1.00e400,\"empty\":[],\"null\":null}"), Entry("sibling", "root"));
        var originals = source.Select(entry => entry.WireBody.ToString()).ToArray();
        var plan = new SessionBranchPlanner().Fork(Request(source, "future", labels: ["fresh-label"]));
        Ids(["root", "future", "fresh-label"], plan.Entries); Equal(null, plan.Entries[0].ParentId);
        Equal("root", plan.Entries[1].ParentId); Equal("future", plan.Entries[2].ParentId);
        Equal("1.00e400", plan.Entries[1].WireBody.Value.GetProperty("opaque").GetProperty("large").GetRawText());
        Equal("source.jsonl", plan.Header.WireBody.Value.GetProperty("parentSession").GetString());
        Check(!plan.Header.WireBody.Value.TryGetProperty("opaqueHeader", out _) && plan.Header.Id == "fresh", "Fork copied historical header fields or ID.");
        Equal("fresh-label", plan.LeafId); Equal("future", plan.SourceLeafId); Equal(null, plan.SelectedText);
        Check(!plan.HasConversation && originals.SequenceEqual(source.Select(entry => entry.WireBody.ToString())), "Fork mutated source or invented conversation.");
        return Task.CompletedTask;
    }
    private static Task GlobalLabels()
    {
        var source = ImmutableArray.Create(Entry("root"), Entry("left", "root"), Entry("right", "root"),
            Label("first-root", "right", "root", "first"), Label("first-left", "left", "left", "left"),
            Label("update-root", "first-root", "root", "updated", "opaque-original-label-time"),
            Label("clear-root", "update-root", "root", null), Label("readd-root", "clear-root", "root", "again"),
            Label("outside", "readd-root", "right", "outside"));
        var plan = new SessionBranchPlanner().Fork(Request(source, "left", labels: ["new-left", "new-root"]));
        Ids(["root", "left", "new-left", "new-root"], plan.Entries);
        Equal("left", plan.Entries[2].WireBody.Value.GetProperty("targetId").GetString());
        Equal("root", plan.Entries[3].WireBody.Value.GetProperty("targetId").GetString());
        Equal("again", plan.Entries[3].WireBody.Value.GetProperty("label").GetString());
        var updated = new SessionBranchPlanner().Fork(Request(source.RemoveRange(6, 3), "left", labels: ["new-root", "new-left"]));
        Equal("opaque-original-label-time", updated.Entries[2].Timestamp);
        var collisionTarget = ImmutableArray.Create(Entry("root"), Label("label", "root", "root", "root title"),
            Label("outside", "label", "fresh-label", "outside title"));
        var isolated = new SessionBranchPlanner().Fork(Request(collisionTarget, "root", labels: ["fresh-label"]));
        Ids(["root", "fresh-label"], isolated.Entries);
        Equal("root title", isolated.Entries[1].WireBody.Value.GetProperty("label").GetString());
        return Task.CompletedTask;
    }
    private static Task CompactionReferences()
    {
        var source = ImmutableArray.Create(User("root", null, "\"prompt\""), Label("l1", "root", "root", "one"),
            Label("l2", "l1", "root", "two"), Entry("kept", "l2"),
            Entry("compact", "kept", "compaction", "\"summary\":\"s\",\"firstKeptEntryId\":\"l1\",\"tokensBefore\":1"));
        var plan = new SessionBranchPlanner().Fork(Request(source, "compact", labels: ["new-label"]));
        Equal("root", plan.Entries[1].ParentId);
        Equal("kept", plan.Entries[2].WireBody.Value.GetProperty("firstKeptEntryId").GetString()); Check(plan.HasConversation, "User conversation lost.");
        var self = source.SetItem(4, Entry("compact", "kept", "compaction", "\"summary\":\"s\",\"firstKeptEntryId\":\"compact\",\"tokensBefore\":1"));
        Equal("compact", new SessionBranchPlanner().Fork(Request(self, "compact", labels: ["new-label"]))
            .Entries[2].WireBody.Value.GetProperty("firstKeptEntryId").GetString());
        return Task.CompletedTask;
    }
    private static async Task CompactionJsonlReload()
    {
        // Authored regression, not a captured upstream result. Pinned session-manager.ts
        // createBranchedSession remaps removed labels to the next retained entry;
        // _buildIndex resumes at the last physical entry; buildContextEntries uses that branch.
        foreach (var boundary in new[] { "l1", "l2", "compact" })
        {
            var source = ImmutableArray.Create(User("root", null, "\"summarized prompt\""),
                Entry("model", "root", "model_change", "\"provider\":\"stored\",\"modelId\":\"fixture\""),
                Label("l1", "model", "root", "first"), Label("l2", "l1", "root", "latest"),
                User("kept", "l2", "\"kept \\u03c0 \\ud83d\\ude00\""),
                Entry("compact", "kept", "compaction", "\"summary\":\"summary\\nline\",\"firstKeptEntryId\":" +
                    JsonSerializer.Serialize(boundary) + ",\"tokensBefore\":7,\"details\":{\"opaque\":1.00e400}"),
                User("sibling", "root", "\"unselected sibling\""),
                Label("outside-label", "sibling", "root", "global title"));
            var originalWire = source.Select(entry => entry.WireBody.ToString()).ToArray();
            var plan = new SessionBranchPlanner().Fork(Request(source, "compact", labels: ["fresh-label"]));
            var read = await new SessionLogReader().ReadAsync(new MemoryStream(plan.JsonlBytes.ToArray(), false), leaveOpen: false);
            Equal(SessionLogReadStatus.Complete, read.Status);
            var reloaded = read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
            Ids(["root", "model", "kept", "compact", "fresh-label"], reloaded);
            Equal("compact", plan.SourceLeafId); Equal("fresh-label", plan.LeafId);
            var tree = new SessionTreeQueries().Build(reloaded);
            Equal(plan.LeafId, tree.PhysicalLeafId); Equal("global title", tree.GetLabel("root")!.Label);
            Equal("compact", reloaded[^1].ParentId);
            Equal(boundary == "compact" ? "compact" : "kept",
                reloaded[3].WireBody.Value.GetProperty("firstKeptEntryId").GetString());
            Equal("1.00e400", reloaded[3].WireBody.Value.GetProperty("details").GetProperty("opaque").GetRawText());
            var context = new SessionContextProjector().Project(reloaded, tree.PhysicalLeafId);
            Equal(new SessionContextModel("stored", "fixture"), context.Model);
            Check((boundary == "compact" ? new[] { "compactionSummary" } : new[] { "compactionSummary", "user" })
                .SequenceEqual(context.Messages.Select(message => message.Role)), "Reloaded compaction retained the wrong messages.");
            Equal("summary\nline", context.Messages[0].WireBody.Value.GetProperty("summary").GetString());
            if (boundary != "compact") Equal("kept \u03c0 \U0001f600", context.Messages[1].WireBody.Value.GetProperty("content").GetString());
            Check(originalWire.SequenceEqual(source.Select(entry => entry.WireBody.ToString())), "JSONL fork mutated source wire data.");
        }
    }
    private static Task Before()
    {
        var source = ImmutableArray.Create(Entry("metadata"), User("user", "metadata",
            "[{\"type\":\"text\",\"text\":\"first\"},{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"},{\"type\":\"text\",\"text\":\"second\"}]"));
        var plan = new SessionBranchPlanner().Fork(Request(source, "user", SessionForkPosition.Before));
        Equal("firstsecond", plan.SelectedText); Ids(["metadata"], plan.Entries); Equal("metadata", plan.SourceLeafId);
        var empty = new SessionBranchPlanner().Fork(Request([User("root", null, "\"exact\\ntext\""), Label("label", "root", "root", "title")], "root", SessionForkPosition.Before));
        Equal("exact\ntext", empty.SelectedText); Check(empty.Entries.IsEmpty && empty.LeafId is null, "Fork before root retained labels/history.");
        var at = new SessionBranchPlanner().Fork(Request(source, "user")); Equal(null, at.SelectedText); Check(at.HasConversation, "At fork dropped user entry.");
        return Task.CompletedTask;
    }
    private static Task Admission()
    {
        var planner = new SessionBranchPlanner(); var source = ImmutableArray.Create(Entry("root"));
        Fails(() => planner.Fork(Request(source, "absent")), SessionBranchPlanFailure.MissingEntry);
        Fails(() => planner.Fork(Request(source, "root", SessionForkPosition.Before)), SessionBranchPlanFailure.RequiresUserMessage);
        Fails(() => planner.Fork(Request([Entry("bad", "absent")], "bad")), SessionBranchPlanFailure.InvalidGraph);
        Fails(() => planner.Fork(Request([Entry("root"), Entry("root")], "root")), SessionBranchPlanFailure.InvalidGraph);
        Fails(() => planner.Fork(Request(source, "root") with { NewSessionId = "original" }), SessionBranchPlanFailure.InvalidRequest);
        Fails(() => planner.Fork(Request(source, "root") with { NewSessionId = "root" }), SessionBranchPlanFailure.InvalidRequest);
        var labelled = source.Add(Label("label", "root", "root", "title"));
        Fails(() => planner.Fork(Request(labelled, "root")), SessionBranchPlanFailure.InvalidRequest);
        Fails(() => planner.Fork(Request(labelled, "root", labels: ["root"])), SessionBranchPlanFailure.InvalidRequest);
        var malformed = source.Add(Entry("label", "root", "label", "\"targetId\":\"root\",\"label\":true"));
        Fails(() => planner.Fork(Request(malformed, "root", labels: ["new"])), SessionBranchPlanFailure.MetadataUnavailable);
        foreach (var invalid in new[] { "\ud800", "\udfff", "x\ud800z" })
        {
            Fails(() => planner.New(invalid, Time, "C:/fixture"), SessionBranchPlanFailure.InvalidRequest);
            Fails(() => planner.New("new", invalid, "C:/fixture"), SessionBranchPlanFailure.InvalidRequest);
            Fails(() => planner.New("new", Time, invalid), SessionBranchPlanFailure.InvalidRequest);
            Fails(() => planner.New("new", Time, "C:/fixture", invalid), SessionBranchPlanFailure.InvalidRequest);
            Fails(() => planner.Fork(Request(labelled, "root", labels: [invalid])), SessionBranchPlanFailure.InvalidRequest);
        }
        Equal("\U0001f4dd", planner.New("\U0001f4dd", Time, "C:/fixture").Header.Id);
        return Task.CompletedTask;
    }
    private static async Task BoundsAndBytes()
    {
        var source = ImmutableArray.Create(User("root", null, "\"p\""), Entry("tail", "root"));
        var plan = new SessionBranchPlanner().Fork(Request(source, "tail"));
        var bytes = plan.JsonlBytes.ToArray();
        var read = await new SessionLogReader().ReadAsync(new MemoryStream(bytes, false), leaveOpen: false);
        Equal(SessionLogReadStatus.Complete, read.Status); Check(read.SourceComplete && bytes[^1] == '\n', "Plan bytes are incomplete.");
        var exact = new SessionBranchPlanner(new(MaximumOutputBytes: bytes.Length)).Fork(Request(source, "tail"));
        Check(exact.JsonlBytes.SequenceEqual(plan.JsonlBytes), "Exact byte boundary changed bytes.");
        Fails(() => new SessionBranchPlanner(new(MaximumOutputBytes: bytes.Length - 1)).Fork(Request(source, "tail")), SessionBranchPlanFailure.ResourceLimit);
        Fails(() => new SessionBranchPlanner(new(MaximumEntries: 1)).Fork(Request(source, "tail")), SessionBranchPlanFailure.ResourceLimit);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { new SessionBranchPlanner().Fork(Request(source, "tail"), canceled.Token); throw new InvalidOperationException("Canceled fork completed."); }
        catch (OperationCanceledException error) { Equal(canceled.Token, error.CancellationToken); }
        var fresh = new SessionBranchPlanner().New("fresh", Time, "C:/fixture");
        Check(fresh.Entries.IsEmpty && !fresh.HasConversation && fresh.LeafId is null &&
            !fresh.Header.WireBody.Value.TryGetProperty("parentSession", out _), "New session inherited parent/history.");
        Equal("fresh", fresh.Header.Id); Check(Encoding.UTF8.GetString(fresh.JsonlBytes.AsSpan()).EndsWith('\n'), "New session lacks header terminator.");
    }
    private static void Fails(Action action, SessionBranchPlanFailure failure)
    { try { action(); throw new InvalidOperationException("Expected branch rejection."); } catch (SessionBranchPlanException error) { Equal(failure, error.Failure); } }
    private static void Ids(string[] expected, IEnumerable<SessionEntry> entries) => Check(expected.SequenceEqual(entries.Select(entry => entry.Id)), "Branch IDs/order changed.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
