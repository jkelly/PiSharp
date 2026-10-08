using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

internal static class SessionHistoryProjectorTests
{
    private static readonly SessionEntryCodec Codec = new();
    private const string Time = "2026-01-01T00:00:00.000Z";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session-history separates compacted model input original display and whole accounting", IndependentViews),
        ("session-history replays selected system and model state without sibling effects", SelectedState),
        ("session-history named section and tool deltas replay removal replacement and insertion order", SystemDeltas),
        ("session-history retains usage tool summary failed assistant and opaque contributions", UsageSources),
        ("session-history rejects unsupported arithmetic without publishing partial totals", UnsupportedArithmetic),
        ("session-history bounds original history independently of reduced context", HistoryBounds),
        ("session-history generated forests preserve ancestry all leaves and reopen order", GeneratedForests)
    ];
    private static SessionEntry Entry(string id, string? parent, string type, object fields)
    {
        var extra = JsonSerializer.Serialize(fields)[1..^1];
        return Codec.Parse("{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) +
            ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Time + "\"" +
            (extra.Length == 0 ? "" : "," + extra) + "}");
    }
    private static SessionEntry Message(string id, string? parent, object message) => Entry(id, parent, "message", new { message });
    private static object Usage(int count, double cost = 0.25) => new { input = count, output = count * 2,
        cacheRead = count * 3, cacheWrite = count * 4, totalTokens = 999,
        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = cost },
        future = new { opaque = true, nullValue = (string?)null } };
    private static object Assistant(string content, object? usage = null, string model = "model", string stop = "stop") =>
        new { role = "assistant", content = new object[] { new { type = "text", text = content } },
            api = "fixture", provider = "fixture", model, usage = usage ?? Usage(1), stopReason = stop, timestamp = 1 };
    private static ImmutableArray<SessionEntry> Forest() =>
    [
        Message("system", null, new { role = "system", content = "original system", timestamp = 1,
            toolDeclarations = new[] { new { name = "original", parameters = new { opaque = true } } } }),
        Message("user", "system", new { role = "user", content = "original user", timestamp = 2 }),
        Message("assistant", "user", Assistant("billed left", Usage(2))),
        Entry("hidden", "assistant", "custom_message", new { customType = "hidden", content = "hidden model text", display = false }),
        Entry("state", "hidden", "custom", new { customType = "ext/state", data = new { schemaVersion = 1, value = "left" } }),
        Entry("compact", "state", "compaction", new { summary = "compact summary", firstKeptEntryId = "absent", tokensBefore = 100,
            usage = Usage(3), systemMessage = new { role = "system", content = "checkpoint system", timestamp = 3,
                toolDeclarations = new[] { new { name = "checkpoint", parameters = new { opaque = true } } } } }),
        Message("after", "compact", Assistant("after compact", Usage(4))),
        Message("right", "system", Assistant("billed right", Usage(5), model: "right-model")),
        Entry("label", "right", "label", new { targetId = "assistant", label = "global left" }),
        Entry("name", "label", "session_info", new { name = "  Global name  " }),
        Entry("unknown", null, "future_inert", new { shell = "must never execute", usage = new { imaginary = true } })
    ];
    private static Task IndependentViews()
    {
        var entries = Forest(); var raw = entries.Select(entry => entry.WireBody.ToString()).ToArray();
        var projected = new SessionHistoryProjector().Project(entries, "after");
        Equal(11, projected.FullHistory.Length); Equal(7, projected.BranchHistory.Length);
        Equal(3, projected.Context.Messages.Length); Equal(7, projected.HistoryMessages.Length);
        Check(projected.Context.Messages.All(message => !message.WireBody.ToString().Contains("billed left", StringComparison.Ordinal)), "Compacted message leaked into model context.");
        Check(projected.HistoryMessages.Any(message => message.WireBody.ToString().Contains("billed left", StringComparison.Ordinal)), "Compaction removed raw visible history.");
        Check(projected.HistoryMessages.Any(message => message.Role == "custom") && projected.DisplayMessages.All(message => message.Role != "custom"), "Hidden display flag changed model/history ownership.");
        Equal(4, projected.DisplayMessages.Length); Equal(2, projected.SystemHistory.Length);
        Equal("checkpoint system", projected.EffectiveSystemMessage!.WireBody.Value.GetProperty("content").GetString());
        Equal(140d, projected.SessionStatistics.Totals!.Total); Equal(90d, projected.BranchStatistics.Totals!.Total);
        Equal("Global name", projected.Tree.SessionName); Equal("global left", projected.Tree.GetLabel("assistant")!.Label);
        Check(raw.SequenceEqual(entries.Select(entry => entry.WireBody.ToString())), "Projection mutated original record bodies.");
        return Task.CompletedTask;
    }
    private static Task SelectedState()
    {
        var common = Message("root", null, new { role = "system", content = "common", timestamp = 0 });
        var leftModel = Entry("lm", "root", "model_change", new { provider = "left", modelId = "one" });
        var leftThink = Entry("lt", "lm", "thinking_level_change", new { thinkingLevel = "high" });
        var leftSystem = Message("ls", "lt", new { role = "system", content = "left", timestamp = 1,
            toolDeclarations = new[] { new { name = "left-tool" } } });
        var rightSystem = Message("rs", "root", new { role = "system", content = "right", timestamp = 2,
            toolDeclarations = new[] { new { name = "right-tool" } } });
        var right = Message("ra", "rs", Assistant("right", model: "right-final"));
        ImmutableArray<SessionEntry> entries = [common, leftModel, leftThink, leftSystem, rightSystem, right];
        var left = new SessionHistoryProjector().Project(entries, "ls"); var other = new SessionHistoryProjector().Project(entries, "ra");
        Equal("left", left.Context.Model!.Provider); Equal("one", left.Context.Model.ModelId); Equal("high", left.Context.ThinkingLevel);
        Equal("common\n\nleft", left.EffectiveSystemMessage!.WireBody.Value.GetProperty("content").GetString());
        Equal("common\n\nright", other.EffectiveSystemMessage!.WireBody.Value.GetProperty("content").GetString());
        Equal("right-final", other.Context.Model!.ModelId); Equal("off", other.Context.ThinkingLevel);
        Check(left.SystemHistory.All(message => !message.WireBody.ToString().Contains("right-tool", StringComparison.Ordinal)), "Sibling tool state leaked.");
        var root = new SessionHistoryProjector().Project(entries, null);
        Check(root.BranchHistory.IsEmpty && root.HistoryMessages.IsEmpty && root.EffectiveSystemMessage is null && root.Context.Model is null, "Explicit root acquired history/state.");
        Equal(10d, root.SessionStatistics.Totals!.Total); Equal(0d, root.BranchStatistics.Totals!.Total);
        return Task.CompletedTask;
    }
    private static Task UsageSources()
    {
        ImmutableArray<SessionEntry> entries =
        [
            Message("a", null, new { role = "assistant", content = new object[] { new { type = "toolCall", id = "call", name = "tool", arguments = new { } } },
                api = "fixture", provider = "fixture", model = "model", usage = Usage(1), stopReason = "error", timestamp = 0 }),
            Message("t", "a", new { role = "toolResult", toolCallId = "call", toolName = "tool", content = new[] { new { type = "text", text = "completed" } },
                isError = false, timestamp = 1, usage = Usage(2) }),
            Entry("u", "t", "usage", new { kind = "future_kind", provider = "fixture", model = "model", usage = Usage(3) }),
            Entry("b", "u", "branch_summary", new { fromId = "a", summary = "summary", usage = Usage(4) }),
            Entry("c", "b", "compaction", new { summary = "compact", firstKeptEntryId = "none", tokensBefore = 1, usage = Usage(5) }),
            Message("aborted", "c", Assistant("aborted", Usage(6), stop: "aborted"))
        ];
        var result = new SessionHistoryProjector().ProjectLatest(entries); var statistics = result.SessionStatistics;
        Equal(3, statistics.TotalMessages); Equal(2, statistics.AssistantMessages); Equal(1, statistics.ToolCalls); Equal(1, statistics.ToolResults);
        Equal(6, statistics.Contributions.Length); Equal(210d, statistics.Totals!.Total); Equal(1.5d, statistics.Totals.Cost);
        Check(statistics.Contributions.All(value => value.Usage.Value.GetProperty("future").GetProperty("opaque").GetBoolean()), "Opaque usage metadata was lost.");
        Equal(21d, statistics.Totals.Input); // totalTokens999 is not substituted for the pinned four category sum.
        Equal(210d, result.BranchStatistics.Totals!.Total);
        return Task.CompletedTask;
    }
    private static Task SystemDeltas()
    {
        object Tool(string name, string description) => new { name, description, parameters = new { type = "object", opaque = new { retained = true } } };
        ImmutableArray<SessionEntry> entries =
        [
            Message("s1", null, new { role = "system", content = "base", timestamp = 7,
                sections = new Dictionary<string, string?> { ["10"] = "ten", ["2"] = "two", ["a"] = "A" },
                toolsAdded = new[] { Tool("A", "initial A"), Tool("B", "initial B") } }),
            Message("s2", "s1", new { role = "system", content = new[] { new { type = "text", text = "first" }, new { type = "text", text = "second" } }, timestamp = 8,
                sections = new Dictionary<string, string?> { ["a"] = "updated A", ["b"] = "B" },
                toolsRemoved = new[] { new { name = "A" } }, toolsAdded = new[] { Tool("B", "updated B"), Tool("C", "new C") } }),
            Message("s3", "s2", new { role = "system", content = "", timestamp = 9,
                sections = new Dictionary<string, string?> { ["2"] = null, ["a"] = null },
                toolsRemoved = new[] { new { name = "B" } }, toolsAdded = new[] { Tool("A", "re-added A") } }),
            Message("s4", "s3", new { role = "system", content = "last", timestamp = 10, sections = new { a = "re-added A" } })
        ];
        var projected = new SessionHistoryProjector().ProjectLatest(entries); var current = projected.EffectiveSystemMessage!.WireBody.Value;
        Equal("base\n\nfirst\nsecond\n\nlast", current.GetProperty("content").GetString()); Equal(7, current.GetProperty("timestamp").GetInt32());
        Check(current.GetProperty("sections").EnumerateObject().Select(property => property.Name).SequenceEqual(["10", "b", "a"]), "Section deletion/reinsertion or numeric enumeration changed.");
        Check(projected.SystemState.Tools.Select(tool => tool.Value.GetProperty("name").GetString()).SequenceEqual(["C", "A"]), "Tool replacement/removal/re-add changed insertion order.");
        Equal("base\n\nfirst\nsecond\n\nlast\n\nten\n\nB\n\nre-added A", projected.SystemState.Prompt);
        Equal(4, projected.SystemHistory.Length); Equal(4, projected.Context.Messages.Length);
        var middle = new SessionHistoryProjector().Project(entries, "s2");
        Check(middle.SystemState.Tools.Select(tool => tool.Value.GetProperty("description").GetString()).SequenceEqual(["updated B", "new C"]), "Tool definition replacement did not retain latest complete declaration.");
        var bad = Message("bad", null, new { role = "system", content = "retained", timestamp = 0, sections = new { secret = 42 } });
        Throws(() => new SessionHistoryProjector().ProjectLatest([bad]), SessionContextProjectionFailure.UnsupportedMessage);
        return Task.CompletedTask;
    }
    private static Task UnsupportedArithmetic()
    {
        var invalid = Entry("invalid", null, "branch_summary", new { fromId = "missing", summary = "retained", usage = new { opaque = "unrecognized" } });
        var result = new SessionHistoryProjector().ProjectLatest([invalid]);
        Equal(SessionAccountingStatus.UnsupportedUsage, result.SessionStatistics.Status);
        Check(result.SessionStatistics.Totals is null && result.SessionStatistics.Contributions.Length == 1 &&
            result.SessionStatistics.UnsupportedRecordIndexes.SequenceEqual([0]), "Unsupported raw usage became partial authoritative totals.");
        var finite1 = Message("one", null, Assistant("one", Usage(1, 1e308)));
        var finite2 = Message("two", "one", Assistant("two", Usage(1, 1e308)));
        result = new SessionHistoryProjector().ProjectLatest([finite1, finite2]);
        Check(result.SessionStatistics.Totals is null && result.SessionStatistics.Contributions.Length == 2 &&
            result.SessionStatistics.UnsupportedRecordIndexes.SequenceEqual([1]), "Overflow published nonfinite or partial sums.");
        var rawHuge = Codec.Parse(invalid.WireBody.ToString().Replace("{\"opaque\":\"unrecognized\"}",
            "{\"input\":1,\"output\":2,\"cacheRead\":3,\"cacheWrite\":4,\"cost\":{\"total\":1e400},\"future\":null}", StringComparison.Ordinal));
        result = new SessionHistoryProjector().ProjectLatest([rawHuge]);
        Check(result.SessionStatistics.Totals is null && result.SessionStatistics.Contributions[0].Usage.ToString().Contains("1e400", StringComparison.Ordinal), "Huge opaque numeric token was changed or accepted as finite billing.");
        return Task.CompletedTask;
    }
    private static Task HistoryBounds()
    {
        ImmutableArray<SessionEntry> entries = [Message("one", null, new { role = "user", content = new string('a', 500), timestamp = 1 }),
            Message("two", "one", new { role = "user", content = new string('b', 500), timestamp = 2 }),
            Entry("compact", "two", "compaction", new { summary = "small", firstKeptEntryId = "none", tokensBefore = 2 })];
        var exact = new SessionHistoryProjector(new(MaximumOutputMessages: 3)).ProjectLatest(entries);
        Equal(3, exact.HistoryMessages.Length); Equal(1, exact.Context.Messages.Length);
        Throws(() => new SessionHistoryProjector(new(MaximumOutputMessages: 2)).ProjectLatest(entries), SessionContextProjectionFailure.ResourceLimit);
        var rawLength = exact.HistoryMessages.Sum(message => message.WireBody.Value.GetRawText().Length);
        Equal(3, new SessionHistoryProjector(new(MaximumOutputCharacters: rawLength)).ProjectLatest(entries).HistoryMessages.Length);
        Throws(() => new SessionHistoryProjector(new(MaximumOutputCharacters: rawLength - 1)).ProjectLatest(entries), SessionContextProjectionFailure.ResourceLimit);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { new SessionHistoryProjector().ProjectLatest(entries, canceled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        Equal(3, exact.HistoryMessages.Length); return Task.CompletedTask;
    }
    private static Task GeneratedForests()
    {
        // Reproducible exhaustive selections over many forests, with forward physical references and
        // disconnected roots. The oracle follows the authored parent relation, independently of indexes.
        for (var seed = 0; seed < 32; seed++)
        {
            var records = Enumerable.Range(0, 32).Select(index => Entry("id-" + index,
                index == 0 || (index + seed) % 7 == 0 ? null : "id-" + ((index * 11 + seed) % index),
                "future_inert", new { seed, index, data = new { execution = "none" } })).ToArray();
            var physical = records.Where((_, index) => index % 2 != 0).Concat(records.Where((_, index) => index % 2 == 0)).ToImmutableArray();
            var reopened = physical.Select(entry => Codec.Parse(entry.WireBody.ToString())).ToImmutableArray();
            foreach (var record in records)
            {
                var expected = new List<string>(); SessionEntry? cursor = record;
                while (cursor is not null) { expected.Add(cursor.Id); cursor = cursor.ParentId is null ? null : records.Single(entry => entry.Id == cursor.ParentId); }
                expected.Reverse(); var actual = new SessionHistoryProjector().Project(physical, record.Id);
                var again = new SessionHistoryProjector().Project(reopened, record.Id);
                Check(actual.BranchHistory.Select(entry => entry.Id).SequenceEqual(expected) &&
                    again.BranchHistory.Select(entry => entry.Id).SequenceEqual(expected), "Generated ancestry/reopen order changed.");
                Check(actual.HistoryMessages.IsEmpty && actual.Context.LlmMessages.IsEmpty && actual.SessionStatistics.TotalMessages == 0, "Unknown records gained effects.");
            }
        }
        Throws(() => new SessionHistoryProjector().ProjectLatest([Entry("secret-a", "secret-b", "future", new { }),
            Entry("secret-b", "secret-a", "future", new { })]), SessionContextProjectionFailure.Cycle);
        Throws(() => new SessionHistoryProjector().ProjectLatest([Entry("secret", "missing", "future", new { })]), SessionContextProjectionFailure.MissingParent);
        Throws(() => new SessionHistoryProjector().ProjectLatest([Record("secret"), Record("secret")]), SessionContextProjectionFailure.DuplicateId);
        return Task.CompletedTask;
        static SessionEntry Record(string id) => Entry(id, null, "future", new { });
    }
    private static void Throws(Action action, SessionContextProjectionFailure expected)
    {
        try { action(); throw new InvalidOperationException("Expected graph/history failure."); }
        catch (SessionContextProjectionException error) { Equal(expected, error.Failure); Check(!error.Message.Contains("secret", StringComparison.Ordinal), "Diagnostic leaked an identity."); }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
