using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Tree;

internal static class SessionTreeQueriesTests
{
    private static readonly SessionEntryCodec Codec = new();
    private const string Time = "2026-01-01T00:00:00.000Z";
    private static SessionEntry Entry(string id, string? parent = null, string timestamp = Time, string type = "custom",
        string fields = "\"customType\":\"state\",\"data\":{\"number\":1e400,\"null\":null}") =>
        Codec.Parse("{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) +
            ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":" + JsonSerializer.Serialize(timestamp) + "," + fields + "}");
    private static SessionEntry Label(string id, string parent, string target, string? rawLabel, string timestamp = Time) =>
        Entry(id, parent, timestamp, "label", "\"targetId\":" + JsonSerializer.Serialize(target) + (rawLabel is null ? "" : ",\"label\":" + rawLabel));
    private static SessionEntry Info(string id, string? parent, string? rawName) =>
        Entry(id, parent, type: "session_info", fields: "\"opaque\":1e400" + (rawName is null ? "" : ",\"name\":" + rawName));
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session tree immutable forest indexes roots structural leaves and physical branch points", Topology),
        ("session tree ancestry follows parents while children preserve physical order and stable date ties", BranchAndOrdering),
        ("session tree global labels replay across branches and missing null empty values clear", Labels),
        ("session tree latest global name applies exact trimming clearing and metadata capabilities", Names),
        ("session tree opaque timestamps remain navigable with explicit chronological diagnostics", OpaqueTimestamps),
        ("session tree reuses full projector graph admission and sanitized missing selection failures", GraphFailures),
        ("session tree exact admission query diagnostics and cancellation limits preserve snapshots", LimitsAndCancellation),
        ("session tree deep iterative branches retain unknown records without source mutation", DeepAndOwnership)
    ];

    private static Task Topology()
    {
        var child = Entry("child", "root"); var root = Entry("root"); var sibling = Entry("sibling", "root"); var detached = Entry("detached");
        var tree = new SessionTreeQueries().Build([child, root, sibling, detached]);
        Ids(["root", "detached"], tree.RootIds); Ids(["child", "sibling", "detached"], tree.StructuralLeafIds);
        Ids(["root"], tree.BranchPointIds); Equal("detached", tree.PhysicalLeafId);
        Ids(["child", "sibling"], tree.ById["root"].ChildIds);
        Check(ReferenceEquals(root, tree.GetEntry("root")), "Validated immutable entry ownership changed.");
        Check(tree.GetEntry("absent") is null && tree.GetChildren("absent").IsEmpty, "Missing lookup did not return an empty result.");
        Ids(["root", "child"], tree.GetBranch("child").Select(entry => entry.Id));
        Check(tree.GetBranch(null).IsEmpty, "Explicit null selected the physical leaf.");
        var forward = new SessionTreeQueries().Build([child, root]);
        Equal("root", forward.PhysicalLeafId); Ids(["child"], forward.StructuralLeafIds);
        Ids(["root"], forward.GetLatestBranch().Select(entry => entry.Id));
        var empty = new SessionTreeQueries().Build([]);
        Check(empty.ById.IsEmpty && empty.RootIds.IsEmpty && empty.GetLatestBranch().IsEmpty && empty.PhysicalLeafId is null, "Empty forest was not empty.");
        return Task.CompletedTask;
    }

    private static Task BranchAndOrdering()
    {
        var tree = new SessionTreeQueries().Build([
            Entry("root"), Entry("late", "root", "2026-01-01T00:00:02Z"), Entry("early", "root", "2026-01-01T00:00:01Z"),
            Entry("tie-offset", "root", "2026-01-01T01:00:01+01:00"), Entry("submillisecond", "root", "2026-01-01T00:00:01.0000001Z"),
            Entry("grandchild", "early")]);
        Ids(["late", "early", "tie-offset", "submillisecond"], tree.GetChildren("root").Select(entry => entry.Id));
        var sorted = tree.GetChronologicalChildren("root"); Equal(SessionTreeOrderStatus.Completed, sorted.Status);
        Check(sorted.Diagnostics.IsEmpty, "Supported chronology emitted a diagnostic.");
        Ids(["early", "tie-offset", "submillisecond", "late"], sorted.Entries.Select(entry => entry.Id));
        Ids(["root", "early", "grandchild"], tree.GetBranch("grandchild").Select(entry => entry.Id));
        Ids(["root", "late"], tree.GetBranch("late").Select(entry => entry.Id));
        Ids(["late", "early", "tie-offset", "submillisecond"], tree.ById["root"].ChildIds);
        return Task.CompletedTask;
    }

    private static Task Labels()
    {
        var source = ImmutableArray.Create(Entry("root"), Entry("left", "root"), Entry("right", "root"),
            Label("first", "left", "root", "\"first\""), Label("second", "right", "root", "\"latest\"", "opaque-label-time"));
        var tree = new SessionTreeQueries().Build(source);
        Equal("latest", tree.GetLabel("root")!.Label); Equal("opaque-label-time", tree.GetLabel("root")!.Timestamp);
        Equal(tree.GetLabel("root"), tree.ById["root"].ResolvedLabel); Ids(["root", "left"], tree.GetBranch("left").Select(entry => entry.Id));
        foreach (var clear in new string?[] { null, "null", "\"\"" })
        {
            var cleared = new SessionTreeQueries().Build(source.Add(Label("clear", "second", "root", clear)));
            Check(cleared.LabelsAvailable && cleared.GetLabel("root") is null && cleared.ById["root"].ResolvedLabel is null, "Label clear kept value/timestamp.");
            Equal(tree.GetLabel("root"), tree.ById["root"].ResolvedLabel);
            var raw = cleared.GetEntry("clear")!.WireBody.Value;
            Equal(clear is not null, raw.TryGetProperty("label", out var value)); if (clear == "null") Equal(JsonValueKind.Null, value.ValueKind);
        }
        var unknownTarget = new SessionTreeQueries().Build(source.Add(Label("unknown-target", "second", "not-a-tree-id", "\"kept\"")));
        Equal("kept", unknownTarget.GetLabel("not-a-tree-id")!.Label); Check(unknownTarget.GetEntry("not-a-tree-id") is null, "Label synthesized its target.");
        var whitespace = new SessionTreeQueries().Build(source.Add(Label("space", "second", "root", "\" \"")));
        Equal(" ", whitespace.GetLabel("root")!.Label);
        var bad = new SessionTreeQueries().Build(source.Add(Label("bad", "second", "root", "true")));
        Check(!bad.LabelsAvailable && bad.Labels.IsEmpty, "Malformed optional label pretended to resolve.");
        Equal(SessionTreeDiagnosticCode.InvalidLabel, bad.MetadataDiagnostics.Single().Code);
        Throws(() => bad.GetLabel("root"), SessionTreeQueryFailure.MetadataUnavailable);
        Ids(["root", "left"], bad.GetBranch("left").Select(entry => entry.Id));
        var repairedMetadata = new SessionTreeQueries().Build(source.Add(Label("bad", "second", "root", "true")).Add(Label("clear", "bad", "root", null)));
        Check(repairedMetadata.LabelsAvailable && repairedMetadata.MetadataDiagnostics.IsEmpty, "A superseded invalid label still blocked current metadata.");
        return Task.CompletedTask;
    }

    private static Task Names()
    {
        var entries = ImmutableArray.Create(Entry("root"), Info("name", "root", "\"\\uFEFF\\u00A0 title \\u3000\""), Entry("sibling", "root"));
        var tree = new SessionTreeQueries().Build(entries); Equal("title", tree.SessionName); Check(tree.SessionNameAvailable, "Known name unavailable.");
        foreach (var clear in new string?[] { null, "null", "\" \\t\\r\\n\"" })
        {
            var cleared = new SessionTreeQueries().Build(entries.Add(Info("clear", "sibling", clear)));
            Check(cleared.SessionNameAvailable && cleared.SessionName is null, "Latest empty name did not clear an older name.");
            Equal("title", tree.SessionName);
        }
        var nonEcma = new SessionTreeQueries().Build(entries.Add(Info("nel", "sibling", "\"\\u0085keep\\u0085\"")));
        Equal("\u0085keep\u0085", nonEcma.SessionName);
        var invalid = new SessionTreeQueries().Build(entries.Add(Info("bad", "sibling", "17")));
        Check(!invalid.SessionNameAvailable && invalid.SessionName is null, "Malformed name pretended to clear title.");
        Equal(SessionTreeDiagnosticCode.InvalidSessionName, invalid.MetadataDiagnostics.Single().Code);
        Ids(["root", "sibling"], invalid.GetBranch("sibling").Select(entry => entry.Id));
        var overwritten = new SessionTreeQueries().Build(entries.Add(Info("bad", "sibling", "17")).Add(Info("valid", "bad", "\"new\"")));
        Check(overwritten.SessionNameAvailable && overwritten.MetadataDiagnostics.IsEmpty, "Earlier invalid name blocked the latest source-defined name.");
        Equal("new", overwritten.SessionName);
        return Task.CompletedTask;
    }

    private static Task OpaqueTimestamps()
    {
        var tree = new SessionTreeQueries().Build([Entry("root", timestamp: "opaque root"), Entry("bad", "root", "opaque child"), Entry("good", "root")]);
        Ids(["root", "bad"], tree.GetBranch("bad").Select(entry => entry.Id));
        Ids(["bad", "good"], tree.GetChildren("root").Select(entry => entry.Id));
        var unsupported = tree.GetChronologicalChildren("root");
        Equal(SessionTreeOrderStatus.UnsupportedTimestamp, unsupported.Status); Check(unsupported.Entries.IsEmpty, "Unsupported sort presented partial chronology.");
        Equal(SessionTreeDiagnosticCode.UnsupportedTimestamp, unsupported.Diagnostics.Single().Code); Equal(1, unsupported.Diagnostics[0].RecordIndex);
        Check(!unsupported.Diagnostics[0].Message.Contains("opaque child", StringComparison.Ordinal), "Date diagnostic leaked raw text.");
        var single = new SessionTreeQueries().Build([Entry("root"), Entry("bad", "root", "opaque child")]);
        Ids(["bad"], single.GetChronologicalChildren("root").Entries.Select(entry => entry.Id));
        Check(single.GetChronologicalChildren("absent").Entries.IsEmpty, "Missing chronological lookup was nonempty.");
        return Task.CompletedTask;
    }

    private static Task GraphFailures()
    {
        GraphThrows(() => new SessionTreeQueries().Build(default), SessionContextProjectionFailure.InvalidEntries);
        GraphThrows(() => new SessionTreeQueries().Build([null!]), SessionContextProjectionFailure.InvalidEntries);
        var header = Codec.Parse("""{"type":"session","version":3,"id":"session","timestamp":"time","cwd":"path"}""");
        GraphThrows(() => new SessionTreeQueries().Build([header]), SessionContextProjectionFailure.InvalidEntries);
        GraphThrows(() => new SessionTreeQueries().Build([Entry("")]), SessionContextProjectionFailure.InvalidEntries);
        GraphThrows(() => new SessionTreeQueries().Build([Entry("root"), Entry("child", "")]), SessionContextProjectionFailure.InvalidEntries);
        GraphThrows(() => new SessionTreeQueries().Build([Entry("same"), Entry("same")]), SessionContextProjectionFailure.DuplicateId);
        GraphThrows(() => new SessionTreeQueries().Build([Entry("good"), Entry("bad", "secret-missing-parent")]), SessionContextProjectionFailure.MissingParent);
        GraphThrows(() => new SessionTreeQueries().Build([Entry("good"), Entry("one", "two"), Entry("two", "one")]), SessionContextProjectionFailure.Cycle);
        GraphThrows(() => new SessionTreeQueries().Build([Entry("self", "self")]), SessionContextProjectionFailure.Cycle);
        var tree = new SessionTreeQueries().Build([Entry("root")]);
        Throws(() => tree.GetBranch("secret-absent-entry"), SessionTreeQueryFailure.MissingEntry);
        return Task.CompletedTask;
    }

    private static Task LimitsAndCancellation()
    {
        var entries = ImmutableArray.Create(Entry("root"), Entry("one", "root"), Entry("two", "root"));
        var characters = entries.Sum(entry => entry.WireBody.ToString().Length);
        var tree = new SessionTreeQueries(new(new(MaximumEntries: 3, MaximumInputCharacters: characters), MaximumQueryEntries: 2)).Build(entries);
        Ids(["one", "two"], tree.GetChildren("root").Select(entry => entry.Id)); Equal(2, tree.GetBranch("one").Length);
        GraphThrows(() => new SessionTreeQueries(new(new(MaximumEntries: 2))).Build(entries), SessionContextProjectionFailure.ResourceLimit);
        GraphThrows(() => new SessionTreeQueries(new(new(MaximumInputCharacters: characters - 1))).Build(entries), SessionContextProjectionFailure.ResourceLimit);
        var bounded = new SessionTreeQueries(new(MaximumQueryEntries: 1)).Build(entries);
        Throws(() => bounded.GetChildren("root"), SessionTreeQueryFailure.ResourceLimit);
        Throws(() => bounded.GetBranch("one"), SessionTreeQueryFailure.ResourceLimit);
        Equal(1, bounded.GetBranch("root").Length);
        var invalidMetadata = entries.Add(Label("bad1", "one", "root", "true")).Add(Label("bad2", "two", "one", "true"));
        Equal(1, new SessionTreeQueries(new(MaximumMetadataDiagnostics: 1)).Build(invalidMetadata.RemoveAt(invalidMetadata.Length - 1)).MetadataDiagnostics.Length);
        Throws(() => new SessionTreeQueries(new(MaximumMetadataDiagnostics: 1)).Build(invalidMetadata), SessionTreeQueryFailure.ResourceLimit);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Cancelled(() => new SessionTreeQueries().Build(entries, cancellation.Token), cancellation.Token);
        Cancelled(() => tree.GetBranch(null, cancellation.Token), cancellation.Token);
        Cancelled(() => tree.GetChildren("absent", cancellation.Token), cancellation.Token);
        Cancelled(() => tree.GetChronologicalChildren("root", cancellation.Token), cancellation.Token);
        Equal(3, tree.Entries.Length); Ids(["one", "two"], tree.ById["root"].ChildIds);
        return Task.CompletedTask;
    }

    private static Task DeepAndOwnership()
    {
        var entries = ImmutableArray.CreateBuilder<SessionEntry>(512);
        for (var index = 0; index < 512; index++) entries.Add(Entry("e" + index, index == 0 ? null : "e" + (index - 1),
            timestamp: "opaque time", type: "future-entry", fields: "\"data\":{\"number\":1e400,\"type\":\"System.Action\",\"null\":null}"));
        var owned = entries.MoveToImmutable(); var before = owned.Select(entry => entry.WireBody.ToString()).ToArray();
        var tree = new SessionTreeQueries(new(MaximumQueryEntries: 512)).Build(owned);
        Equal(512, tree.GetLatestBranch().Length); Ids(["e0"], tree.RootIds); Ids(["e511"], tree.StructuralLeafIds);
        Check(tree.BranchPointIds.IsEmpty && tree.Entries.All(entry => entry.Kind == SessionEntryKind.Unknown), "Unknown history acquired tree behavior.");
        for (var index = 0; index < owned.Length; index++) Equal(before[index], tree.Entries[index].WireBody.ToString());
        return Task.CompletedTask;
    }

    private static void Ids(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Identity order differs.");
    private static void Throws(Action action, SessionTreeQueryFailure failure)
    {
        try { action(); } catch (SessionTreeQueryException error) { Equal(failure, error.Failure); Check(error.InnerException is null && !error.Message.Contains("secret", StringComparison.Ordinal), "Query failure exposed identity."); return; }
        throw new InvalidOperationException("Expected tree query failure.");
    }
    private static void GraphThrows(Action action, SessionContextProjectionFailure failure)
    {
        try { action(); } catch (SessionContextProjectionException error) { Equal(failure, error.Failure); Check(!error.Message.Contains("secret", StringComparison.Ordinal), "Graph failure exposed identity."); return; }
        throw new InvalidOperationException("Expected shared graph admission failure.");
    }
    private static void Cancelled(Action action, CancellationToken token)
    { try { action(); } catch (OperationCanceledException error) { Equal(token, error.CancellationToken); return; } throw new InvalidOperationException("Expected cancellation."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
