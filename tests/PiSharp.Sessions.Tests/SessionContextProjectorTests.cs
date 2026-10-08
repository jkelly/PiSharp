using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

internal static class SessionContextProjectorTests
{
    private const string Time = "2024-01-01T00:00:00.000Z";
    private static readonly SessionEntryCodec Codec = new();
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session selected ancestry excludes siblings and replays branch settings", BranchesAndSettings),
        ("session runtime and model custom context retain owned raw history", CustomAndOpaqueHistory),
        ("session forest diagnostics terminate missing parents duplicates cycles", ForestDiagnostics),
        ("session stored context influences apply only on selected ancestry", SelectedInfluences),
        ("session projection exact bounds cancellation and retained snapshots", LimitsAndSnapshots)
    ];

    private static Task BranchesAndSettings()
    {
        var system = Message("root", null, """{"role":"system","content":"base","timestamp":0,"sections":{"policy":"kept"},"toolsAdded":[{"name":"lookup","description":"Lookup","parameters":{"type":"object"}}]}""");
        var model = Entry("model_change", "model", "root", """{"provider":"base-provider","modelId":"base-model"}""");
        var thinking = Entry("thinking_level_change", "thinking", "model", """{"thinkingLevel":"medium"}""");
        var left = Message("left", "thinking", User("left branch"));
        var answer = Message("answer", "left", Assistant());
        var rightModel = Entry("model_change", "right-model", "model", """{"provider":"right-provider","modelId":"right-model-id"}""");
        var rightThinking = Entry("thinking_level_change", "right-thinking", "right-model", """{"thinkingLevel":"future-level"}""");
        var right = Message("right", "right-thinking", User("right branch"));
        var detached = Message("detached", null, User("another root"));
        ImmutableArray<SessionEntry> entries = [system, model, thinking, left, answer, rightModel, rightThinking, right, detached];
        var projector = new SessionContextProjector(); var projected = projector.Project(entries, "answer");
        Sequence(["root", "model", "thinking", "left", "answer"], projected.Ancestry.Select(entry => entry.Id));
        Sequence(["root", "detached"], projected.RootIds); Equal(entries.Length, projected.ById.Count);
        Equal("answer", projected.LeafId); Equal("medium", projected.ThinkingLevel);
        Equal(new SessionContextModel("assistant-provider", "assistant-model"), projected.Model);
        Sequence(["system", "user", "assistant"], projected.Messages.Select(message => message.Role));
        Equal(system.WireBody.Value.GetProperty("message").GetRawText(), projected.Messages[0].WireBody.ToString());
        Equal("left branch", projected.Messages[1].WireBody.Value.GetProperty("content").GetString());
        var sibling = projector.Project(entries, "right");
        Sequence(["root", "model", "right-model", "right-thinking", "right"], sibling.Ancestry.Select(entry => entry.Id));
        Equal(new SessionContextModel("right-provider", "right-model-id"), sibling.Model); Equal("future-level", sibling.ThinkingLevel);
        Equal(2, sibling.Messages.Length); Equal("right branch", sibling.Messages[1].WireBody.Value.GetProperty("content").GetString());
        var otherRoot = projector.ProjectLatest(entries);
        Equal("detached", otherRoot.LeafId); Equal(1, otherRoot.Ancestry.Length); Equal("off", otherRoot.ThinkingLevel); Equal<SessionContextModel?>(null, otherRoot.Model);
        var emptySelection = projector.Project(entries, null);
        Equal(0, emptySelection.Messages.Length); Equal(0, emptySelection.Ancestry.Length); Equal(entries.Length, emptySelection.ById.Count);
        Equal("off", emptySelection.ThinkingLevel); Equal<SessionContextModel?>(null, emptySelection.Model);
        Equal(0, projector.Project([], null).ById.Count);
        return Task.CompletedTask;
    }

    private static Task CustomAndOpaqueHistory()
    {
        const string raw = """{ "role":"user", "content":"owned", "timestamp":1, "opaque":{"wide":9007199254740993,"scale":1.0,"huge":1e400,"nil":null} }""";
        var user = Message("user", null, raw);
        var assistant = Message("assistant", "user", Assistant());
        var result = Message("result", "assistant", """{"role":"toolResult","toolCallId":"historical-call","toolName":"lookup","content":[{"type":"text","text":"historical result","opaque":null}],"details":null,"isError":false,"timestamp":3}""");
        var custom = Entry("custom_message", "custom", "result", """{"customType":"hidden","content":"custom context","display":false,"details":null}""");
        var images = Entry("custom_message", "images", "custom", """{"customType":"images","content":[{"type":"image","data":"unopened-image","mimeType":"image/png","opaque":{"scale":1.0}}],"display":true}""");
        var state = Entry("custom", "state", "images", """{"customType":"inert-state","data":{"execute":"private-command","nullable":null}}""");
        var unknown = Entry("future_action", "unknown", "state", """{"command":"private-command","opaque":9007199254740993}""");
        var futureMessage = Message("future-message", "unknown", """{"role":"future-role","content":"inert","command":"private-command"}""");
        ImmutableArray<SessionEntry> entries = [user, assistant, result, custom, images, state, unknown, futureMessage];
        var originals = entries.Select(entry => entry.WireBody.ToString()).ToArray();
        var projected = new SessionContextProjector().Project(entries, "future-message");
        Equal(6, projected.Messages.Length); Equal(5, projected.LlmMessages.Length); Equal(entries.Length, projected.Ancestry.Length);
        Equal(raw, projected.Messages[0].WireBody.ToString()); Equal(raw, projected.LlmMessages[0].WireBody.ToString());
        Equal(assistant.WireBody.Value.GetProperty("message").GetRawText(), projected.LlmMessages[1].WireBody.ToString());
        Equal(result.WireBody.Value.GetProperty("message").GetRawText(), projected.LlmMessages[2].WireBody.ToString());
        Equal("custom", projected.Messages[3].Role); Equal("user", projected.LlmMessages[3].Role);
        Equal(1_704_067_200_000L, projected.Messages[3].WireBody.Value.GetProperty("timestamp").GetInt64());
        Check(!projected.Messages[3].WireBody.Value.GetProperty("display").GetBoolean(), "Display flag changed custom context admission.");
        Equal(JsonValueKind.Null, projected.Messages[3].WireBody.Value.GetProperty("details").ValueKind);
        Equal("custom context", projected.LlmMessages[3].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Check(!projected.LlmMessages[3].WireBody.Value.TryGetProperty("details", out _), "Custom state metadata entered model conversion.");
        Check(!projected.Messages[4].WireBody.Value.TryGetProperty("details", out _), "Missing custom details became null.");
        Equal(images.WireBody.Value.GetProperty("content").GetRawText(), projected.LlmMessages[4].WireBody.Value.GetProperty("content").GetRawText());
        Equal("future-role", projected.Messages[5].Role); Equal(unknown.WireBody.ToString(), projected.ById["unknown"].WireBody.ToString());
        for (var index = 0; index < entries.Length; index++) Equal(originals[index], entries[index].WireBody.ToString());
        return Task.CompletedTask;
    }

    private static Task ForestDiagnostics()
    {
        var root = Message("root", null, User("root")); var projector = new SessionContextProjector();
        Fails(SessionContextProjectionFailure.InvalidEntries, () => projector.Project(default, null));
        Fails(SessionContextProjectionFailure.InvalidEntries, () => projector.Project([null!], null));
        Fails(SessionContextProjectionFailure.InvalidEntries, () => projector.ProjectLatest([null!]));
        Fails(SessionContextProjectionFailure.InvalidEntries, () => projector.Project([Message("", null, User("empty"))], null));
        Fails(SessionContextProjectionFailure.InvalidEntries, () => projector.Project([Message("child", "", User("empty parent"))], "child"));
        Fails(SessionContextProjectionFailure.InvalidEntries, () => projector.Project([Codec.Parse("""{"type":"session","version":3,"id":"header","timestamp":"t","cwd":"unopened"}""")], null));
        Fails(SessionContextProjectionFailure.DuplicateId, () => projector.Project([root, Message("root", null, User("duplicate"))], "root"));
        Fails(SessionContextProjectionFailure.MissingParent, () => projector.Project([root, Message("sibling", "private-missing", User("missing parent"))], "root"));
        Fails(SessionContextProjectionFailure.MissingLeaf, () => projector.Project([root], "private-missing"));
        Fails(SessionContextProjectionFailure.MissingLeaf, () => projector.Project([root], ""));
        Fails(SessionContextProjectionFailure.Cycle, () => projector.Project([root, Message("a", "b", User("a")), Message("b", "a", User("b"))], "root"));
        Fails(SessionContextProjectionFailure.Cycle, () => projector.Project([Message("self", "self", User("self"))], "self"));
        return Task.CompletedTask;
    }

    private static Task SelectedInfluences()
    {
        var root = Message("root", null, User("root")); var safe = Message("safe", "root", User("safe"));
        foreach (var influence in new[]
        {
            Entry("compaction", "advanced", "root", """{"summary":"summary","firstKeptEntryId":"root","tokensBefore":100}"""),
            Entry("branch_summary", "advanced", "root", """{"summary":"summary","fromId":"elsewhere"}"""),
            Entry("context_edit", "advanced", "root", """{"targetId":"root","replacement":null}"""),
            Message("advanced", "root", """{"role":"branchSummary","summary":"summary","fromId":null,"timestamp":0}"""),
            Message("advanced", "root", """{"role":"compactionSummary","summary":"summary","tokensBefore":100,"timestamp":0}""")
        })
        {
            ImmutableArray<SessionEntry> entries = [root, influence, safe]; var projector = new SessionContextProjector();
            var selected = projector.Project(entries, "advanced");
            Equal(influence.Kind == SessionEntryKind.ContextEdit ? 0 : 2, selected.Messages.Length);
            Equal(influence.Kind == SessionEntryKind.ContextEdit ? 0 : 2, selected.LlmMessages.Length);
            Equal(2, projector.Project(entries, "safe").Messages.Length);
        }
        var bash = new SessionContextProjector().Project(
            [Message("bash", null, """{"role":"bashExecution","command":"unexecuted","output":"output","cancelled":false,"truncated":false,"timestamp":0}""")], "bash");
        Equal("bashExecution", bash.Messages[0].Role); Equal("user", bash.LlmMessages[0].Role);
        Equal("Ran `unexecuted`\n```\noutput\n```", bash.LlmMessages[0].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        var invalidTime = Entry("custom_message", "bad-time", null, """{"customType":"x","content":"context","display":false}""", "not-a-date");
        Fails(SessionContextProjectionFailure.UnsupportedTimestamp, () => new SessionContextProjector().Project([invalidTime], "bad-time"));
        return Task.CompletedTask;
    }

    private static Task LimitsAndSnapshots()
    {
        ImmutableArray<SessionEntry> entries = [Message("root", null, User("root")), Message("child", "root", User("child"))];
        var input = entries.Sum(entry => entry.WireBody.ToString().Length);
        var output = entries.Sum(entry => entry.WireBody.Value.GetProperty("message").GetRawText().Length);
        var exact = new SessionContextProjectionOptions(MaximumEntries: 2, MaximumAncestorSteps: 2,
            MaximumInputCharacters: input, MaximumOutputMessages: 2, MaximumOutputCharacters: output);
        var snapshot = new SessionContextProjector(exact).Project(entries, "child"); Equal(2, snapshot.Messages.Length);
        foreach (var limits in new[] { exact with { MaximumEntries = 1 }, exact with { MaximumAncestorSteps = 1 },
            exact with { MaximumInputCharacters = input - 1 }, exact with { MaximumOutputMessages = 1 }, exact with { MaximumOutputCharacters = output - 1 } })
            Fails(SessionContextProjectionFailure.ResourceLimit, () => new SessionContextProjector(limits).Project(entries, "child"));
        Throws<ArgumentOutOfRangeException>(() => new SessionContextProjector(new(MaximumAncestorSteps: 0)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => new SessionContextProjector().Project(entries, "child", cancellation.Token));
        var later = entries.Add(Message("later", "child", User("later")));
        Equal(3, new SessionContextProjector().Project(later, "later").Messages.Length);
        Equal(2, snapshot.Messages.Length); Equal(2, snapshot.ById.Count); Equal(2, snapshot.Ancestry.Length);
        Equal("child", snapshot.LeafId); Equal("root", snapshot.Messages[0].WireBody.Value.GetProperty("content").GetString());
        return Task.CompletedTask;
    }

    private static SessionEntry Entry(string type, string id, string? parent, string fields = "{}", string timestamp = Time) => Codec.Parse(
        "{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) +
        ",\"timestamp\":" + JsonSerializer.Serialize(timestamp) + (fields.Length == 2 ? "" : "," + fields[1..^1]) + "}");
    private static SessionEntry Message(string id, string? parent, string message) => Entry("message", id, parent, "{\"message\":" + message + "}");
    private static string User(string text) => JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 1 });
    private static string Assistant() => PiWireJson.WriteMessage(new("test-api", "assistant-provider", "assistant-model", 2,
        [new ThinkingContent("retained", JsonFields.Empty.Set("thinkingSignature", JsonData.Parse("\"opaque-signature\""))),
            new ToolCallContent("historical-call", "lookup", JsonData.Parse("""{"wide":9007199254740993,"scale":1.0}"""))],
        TokenUsage.Zero, StopReason.ToolUse, JsonFields.Empty.Set("opaque", JsonData.Parse("""{"nil":null,"scale":1.0}""")))).ToString();
    private static void Fails(SessionContextProjectionFailure failure, Action action)
    {
        var error = Throws<SessionContextProjectionException>(action); Equal(failure, error.Failure);
        Check(!error.Message.Contains("private", StringComparison.Ordinal), "Session diagnostic leaked record data.");
    }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Selected ancestry differs.");
}
