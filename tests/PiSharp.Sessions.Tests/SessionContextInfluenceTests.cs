using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

internal static class SessionContextInfluenceTests
{
    private const string Time = "2024-01-01T00:00:00.000Z";
    private static readonly SessionEntryCodec Codec = new();
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("session newest checkpoint retains selected range and full ancestry settings", Checkpoints);
        yield return ("session surviving edits replace content omit contributions and preserve raw metadata", Edits);
        yield return ("session stored summaries and recorded bash convert without effects", SummariesAndBash);
        yield return ("session converted influences obey output budgets and retain snapshots", LimitsAndOwnership);
        yield return ("session complete native context matches four genuine whole-module captures", GenuineWholeModule);
    }

    private static Task Checkpoints()
    {
        var root = Message("root", null, """{"role":"system","content":"original system","timestamp":0,"toolsAdded":[{"name":"old","parameters":{"type":"object"}}]}""");
        var settings = Entry("thinking_level_change", "settings", "root", """{"thinkingLevel":"high"}""");
        var oldAnswer = Message("old-answer", "settings", Assistant());
        var discardedEdit = Edit("discarded-edit", "old-answer", "kept", """{"content":"must not survive"}""");
        var kept = Message("kept", "discarded-edit", User("kept original"));
        var oldCheckpoint = Entry("compaction", "old-checkpoint", "kept", """{"summary":"OLD SUMMARY","firstKeptEntryId":"kept","tokensBefore":10}""");
        var retainedSystem = Message("retained-system", "old-checkpoint", """{"role":"system","content":"must not survive as system","timestamp":0}""");
        var state = Entry("future_state", "state", "retained-system", """{"opaque":{"wide":9007199254740993,"nil":null}}""");
        const string checkpointSystem = """{ "role":"system", "content":"checkpoint", "timestamp":42, "toolsAdded":[{"name":"new","parameters":{"type":"object","minimum":0.5}}], "opaque":{"wide":9007199254740993,"scaled":1.0,"nil":null} }""";
        var checkpoint = Entry("compaction", "checkpoint", "state", "{\"summary\":\"NEW SUMMARY\",\"firstKeptEntryId\":\"kept\",\"tokensBefore\":9007199254740993,\"systemMessage\":" + checkpointSystem + "}");
        var tail = Message("tail", "checkpoint", User("tail"));
        var sibling = Entry("compaction", "sibling", "root", """{"summary":"SIBLING SUMMARY","firstKeptEntryId":"root","tokensBefore":99}""");
        ImmutableArray<SessionEntry> entries = [root, settings, oldAnswer, discardedEdit, kept, oldCheckpoint, retainedSystem, state, checkpoint, tail, sibling];
        var projected = new SessionContextProjector().Project(entries, "tail");
        Sequence(["checkpoint", "kept", "old-checkpoint", "state", "tail"], projected.ContextEntries.Select(item => item.SourceEntry.Id));
        Sequence(["system", "compactionSummary", "user", "user"], projected.Messages.Select(message => message.Role));
        Equal(checkpointSystem, projected.Messages[0].WireBody.ToString());
        Equal("NEW SUMMARY", projected.Messages[1].WireBody.Value.GetProperty("summary").GetString());
        Equal("9007199254740993", projected.Messages[1].WireBody.Value.GetProperty("tokensBefore").GetRawText());
        Equal("kept original", projected.Messages[2].WireBody.Value.GetProperty("content").GetString());
        Equal(0, projected.ContextEntries[2].Messages.Length); Equal(0, projected.ContextEntries[3].Messages.Length);
        Equal("high", projected.ThinkingLevel); Equal(new SessionContextModel("stored-provider", "stored-model"), projected.Model);
        Equal(10, projected.Ancestry.Length); Equal(entries.Length, projected.ById.Count);

        // Missing/sibling/current/later boundaries retain no earlier entry in the public source.
        foreach (var boundary in new[] { "missing", "sibling", "checkpoint", "tail" })
        {
            var retainNone = Entry("compaction", "checkpoint", "state", JsonSerializer.Serialize(new { summary = "none", firstKeptEntryId = boundary, tokensBefore = 1 }));
            var none = new SessionContextProjector().Project(entries.Replace(checkpoint, retainNone), "tail");
            Sequence(["checkpoint", "tail"], none.ContextEntries.Select(item => item.SourceEntry.Id));
            Sequence(["compactionSummary", "user"], none.Messages.Select(message => message.Role));
            Equal("high", none.ThinkingLevel); Equal(projected.Model, none.Model);
        }
        return Task.CompletedTask;
    }

    private static Task Edits()
    {
        var root = Message("root", null, """{"role":"system","content":"system","timestamp":0}""");
        var user = Message("user", "root", User("original user"));
        var assistant = Message("assistant", "user", Assistant());
        var result = Message("result", "assistant", """{"role":"toolResult","toolCallId":"stored-call","toolName":"lookup","content":[{"type":"text","text":"original result"}],"details":null,"isError":true,"timestamp":2,"opaque":{"huge":1e400}}""");
        var custom = Entry("custom_message", "custom", "result", """{"customType":"hidden","content":"original custom","display":false,"details":null}""");
        var summary = Entry("branch_summary", "summary", "custom", """{"summary":"retained summary","fromId":"sibling"}""");
        var first = Edit("first", "summary", "user", """{"content":"first"}""");
        var second = Edit("second", "first", "user", """{"content":"last","unknown":{"nil":null}}""");
        var assistantEdit = Edit("assistant-edit", "second", "assistant", """{"content":"replacement assistant"}""");
        var resultEdit = Edit("result-edit", "assistant-edit", "result", """{"content":"replacement result"}""");
        var customEdit = Edit("custom-edit", "result-edit", "custom", "null");
        var systemEdit = Edit("system-edit", "custom-edit", "root", """{"content":"ignored system replacement"}""");
        var summaryEdit = Edit("summary-edit", "system-edit", "summary", """{"content":"ignored summary replacement"}""");
        var editOfEdit = Edit("edit-of-edit", "summary-edit", "second", "null");
        var nonexistent = Edit("nonexistent", "edit-of-edit", "absent-target", "null");
        ImmutableArray<SessionEntry> entries = [root, user, assistant, result, custom, summary, first, second, assistantEdit, resultEdit, customEdit, systemEdit, summaryEdit, editOfEdit, nonexistent];
        var originals = entries.Select(entry => entry.WireBody.ToString()).ToArray();
        var projected = new SessionContextProjector().Project(entries, "nonexistent");
        Sequence(["system", "user", "assistant", "toolResult", "branchSummary"], projected.Messages.Select(message => message.Role));
        Equal("system", projected.Messages[0].WireBody.Value.GetProperty("content").GetString());
        Equal("last", projected.Messages[1].WireBody.Value.GetProperty("content").GetString());
        Equal("replacement assistant", Text(projected.Messages[2])); Equal("replacement result", Text(projected.Messages[3]));
        Equal("retained summary", projected.Messages[4].WireBody.Value.GetProperty("summary").GetString());
        Equal(0, projected.ContextEntries.Single(item => item.SourceEntry.Id == "custom").Messages.Length);
        Equal(0, projected.ContextEntries.Single(item => item.SourceEntry.Id == "second").Messages.Length);
        foreach (var property in assistant.WireBody.Value.GetProperty("message").EnumerateObject().Where(property => property.Name != "content"))
            Equal(property.Value.GetRawText(), projected.Messages[2].WireBody.Value.GetProperty(property.Name).GetRawText());
        foreach (var property in result.WireBody.Value.GetProperty("message").EnumerateObject().Where(property => property.Name != "content"))
            Equal(property.Value.GetRawText(), projected.Messages[3].WireBody.Value.GetProperty(property.Name).GetRawText());
        for (var index = 0; index < entries.Length; index++) Equal(originals[index], entries[index].WireBody.ToString());

        const string replacementArray = """[{"type":"image","data":"unopened-image","mimeType":"image/png","opaque":{"wide":9007199254740993,"scaled":0.5,"nil":null}},{"type":"text","text":"ordered text"}]""";
        var arrayEdit = Edit("array-edit", "nonexistent", "user", "{\"content\":" + replacementArray + "}");
        var withArray = new SessionContextProjector().Project(entries.Add(arrayEdit), "array-edit");
        Equal(replacementArray, withArray.Messages[1].WireBody.Value.GetProperty("content").GetRawText());
        Equal(replacementArray, withArray.LlmMessages[1].WireBody.Value.GetProperty("content").GetRawText());
        Equal(user.WireBody.Value.GetProperty("message").GetRawText(), projected.ContextEntries[1].SourceEntry.WireBody.Value.GetProperty("message").GetRawText());

        // Null applies to the whole contribution, including a checkpoint's system and summary.
        var checkpoint = Entry("compaction", "checkpoint", "nonexistent", """{"summary":"checkpoint","firstKeptEntryId":"user","tokensBefore":1,"systemMessage":{"role":"system","content":"checkpoint system","timestamp":0}}""");
        var omit = Edit("omit", "checkpoint", "checkpoint", "null");
        var omitted = new SessionContextProjector().Project(entries.Add(checkpoint).Add(omit), "omit");
        Equal(0, omitted.ContextEntries[0].Messages.Length);
        Check(omitted.Messages.All(message => message.Role != "compactionSummary"), "Omitted checkpoint summary survived.");
        return Task.CompletedTask;
    }

    private static Task SummariesAndBash()
    {
        var branch = Entry("branch_summary", "branch", null, """{"summary":"branch\r\nsummary","fromId":"other","details":{"notSent":null}}""");
        var empty = Entry("branch_summary", "empty", "branch", """{"summary":"","fromId":"other"}""", "not-a-date");
        var stored = Message("stored", "empty", """{"role":"branchSummary","summary":"stored","fromId":null,"timestamp":11,"opaque":{"nil":null}}""");
        var compaction = Message("stored-compaction", "stored", """{"role":"compactionSummary","summary":"compacted","tokensBefore":1,"timestamp":12}""");
        var bash = Message("bash", "stored-compaction", """{"role":"bashExecution","command":"authored command","output":"line\r\n","exitCode":7,"cancelled":false,"truncated":true,"fullOutputPath":"unopened/private/output","excludeFromContext":false,"timestamp":13,"opaque":null}""");
        var cancelled = Message("cancelled", "bash", """{"role":"bashExecution","command":"cancelled","output":"","exitCode":9,"cancelled":true,"truncated":true,"fullOutputPath":"","timestamp":14}""");
        var hidden = Message("hidden", "cancelled", """{"role":"bashExecution","command":"never execute","output":"secret context","exitCode":null,"cancelled":false,"truncated":false,"excludeFromContext":true,"timestamp":15}""");
        ImmutableArray<SessionEntry> entries = [branch, empty, stored, compaction, bash, cancelled, hidden];
        var projected = new SessionContextProjector().Project(entries, "hidden");
        Equal(6, projected.Messages.Length); Equal(5, projected.LlmMessages.Length); Equal(0, projected.ContextEntries[1].Messages.Length);
        Equal("The following is a summary of a branch that this conversation came back from:\n\n<summary>\nbranch\r\nsummary</summary>", Text(projected.LlmMessages[0]));
        Equal("The following is a summary of a branch that this conversation came back from:\n\n<summary>\nstored</summary>", Text(projected.LlmMessages[1]));
        Equal("The conversation history before this point was compacted into the following summary:\n\n<summary>\ncompacted\n</summary>", Text(projected.LlmMessages[2]));
        Equal("Ran `authored command`\n```\nline\r\n\n```\n\nCommand exited with code 7\n\n[Output truncated. Full output: unopened/private/output]", Text(projected.LlmMessages[3]));
        Equal("Ran `cancelled`\n(no output)\n\n(command cancelled)", Text(projected.LlmMessages[4]));
        Equal(1_704_067_200_000L, projected.Messages[0].WireBody.Value.GetProperty("timestamp").GetInt64());
        Equal(JsonValueKind.Null, projected.Messages[1].WireBody.Value.GetProperty("fromId").ValueKind);
        Equal(bash.WireBody.Value.GetProperty("message").GetRawText(), projected.Messages[3].WireBody.ToString());
        foreach (var extra in new[] { "", ",\"exitCode\":null", ",\"exitCode\":0", ",\"exitCode\":-4", ",\"excludeFromContext\":null" })
        {
            var ordinary = Message("ordinary", null, "{\"role\":\"bashExecution\",\"command\":\"authored\",\"output\":\"\",\"cancelled\":false,\"truncated\":false,\"timestamp\":0" + extra + "}");
            var text = Text(new SessionContextProjector().Project([ordinary], "ordinary").LlmMessages[0]);
            Equal("Ran `authored`\n(no output)" + (extra.Contains("-4", StringComparison.Ordinal) ? "\n\nCommand exited with code -4" : ""), text);
        }
        foreach (var extra in new[] { ",\"excludeFromContext\":\"unchecked\"", ",\"fullOutputPath\":42", ",\"exitCode\":9007199254740993" })
        {
            var unsupported = Message("unsupported", null, "{\"role\":\"bashExecution\",\"command\":\"authored\",\"output\":\"\",\"cancelled\":false,\"truncated\":false,\"timestamp\":0" + extra + "}");
            Fails(SessionContextProjectionFailure.UnsupportedMessage, () => new SessionContextProjector().Project([unsupported], "unsupported"));
        }
        var invalidCheckpoint = Entry("compaction", "invalid", null, """{"summary":"summary","firstKeptEntryId":"none","tokensBefore":1,"systemMessage":{"role":42}}""");
        Fails(SessionContextProjectionFailure.UnsupportedMessage, () => new SessionContextProjector().Project([invalidCheckpoint], "invalid"));
        return Task.CompletedTask;
    }

    private static Task LimitsAndOwnership()
    {
        var compaction = Entry("compaction", "checkpoint", null, """{"summary":"owned summary","firstKeptEntryId":"none","tokensBefore":1,"systemMessage":{"role":"system","content":"checkpoint","timestamp":0}}""");
        ImmutableArray<SessionEntry> entries = [compaction]; var raw = compaction.WireBody.ToString();
        var snapshot = new SessionContextProjector().Project(entries, "checkpoint");
        var output = (int)Math.Max(snapshot.Messages.Sum(message => (long)message.WireBody.ToString().Length),
            snapshot.LlmMessages.Sum(message => (long)message.WireBody.ToString().Length));
        var exact = new SessionContextProjectionOptions(MaximumEntries: 1, MaximumAncestorSteps: 1,
            MaximumInputCharacters: raw.Length, MaximumOutputMessages: 2, MaximumOutputCharacters: output);
        Equal(2, new SessionContextProjector(exact).Project(entries, "checkpoint").LlmMessages.Length);
        Fails(SessionContextProjectionFailure.ResourceLimit, () => new SessionContextProjector(exact with { MaximumOutputCharacters = output - 1 }).Project(entries, "checkpoint"));
        Fails(SessionContextProjectionFailure.ResourceLimit, () => new SessionContextProjector(exact with { MaximumOutputMessages = 1 }).Project(entries, "checkpoint"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => new SessionContextProjector().Project(entries, "checkpoint", cancellation.Token));
        var later = entries.Add(Edit("later", "checkpoint", "checkpoint", "null"));
        Equal(0, new SessionContextProjector().Project(later, "later").Messages.Length);
        Equal(2, snapshot.Messages.Length); Equal(1, snapshot.ContextEntries.Length); Equal(raw, snapshot.ContextEntries[0].SourceEntry.WireBody.ToString());
        return Task.CompletedTask;
    }

    private static async Task GenuineWholeModule()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "fixtures/pi-v0.99.1/session-context/core.input.json")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Session context fixture root was not found.");
        // Check every immutable byte identity before parsing any fixture.
        var inputBytes = await ReadPinned("core.input.json", "f08048cf0be58181482aa5029f8c1cde340a62cbd18fa90760cfb5bc5010c0b9");
        var expectedBytes = await ReadPinned("core.expected.json", "27926944c7f0cf5cdf1f65188dcce0cf14132b932e8784af65796c723f516a09");
        var lockBytes = await ReadPinned("oracle.lock.json", "f5c886a1b9e171103eba930297333961cd9e5352b7b235f29eb40defdf16d2bb");
        using var input = JsonDocument.Parse(inputBytes); using var expected = JsonDocument.Parse(expectedBytes); using var oracleLock = JsonDocument.Parse(lockBytes);
        const string sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
        Equal(sourceSha, input.RootElement.GetProperty("sourceSha").GetString());
        Equal(sourceSha, expected.RootElement.GetProperty("sourceSha").GetString());
        Equal(sourceSha, oracleLock.RootElement.GetProperty("environmentPins").GetProperty("sourceSha").GetString());
        var observations = expected.RootElement.GetProperty("observations"); var checks = observations.GetProperty("checks");
        Equal(4, checks.GetProperty("caseCount").GetInt32()); Equal(28, checks.GetProperty("authoredInputEntries").GetInt32());
        foreach (var control in new[] { "wholeSessionModuleLoaded", "sourceEntriesUnchanged", "noClockOrRngOverride", "noSessionConstructorOrFilesystemOperation", "networkAndProcessesBlocked" })
            Check(checks.GetProperty(control).GetBoolean(), "Whole-module source capture provenance changed.");
        var entries = input.RootElement.GetProperty("entries").EnumerateArray().Select(Codec.Read).ToImmutableArray();
        var originals = entries.Select(entry => entry.WireBody.ToString()).ToArray();
        var capturedCases = observations.GetProperty("cases"); Equal(4, capturedCases.GetArrayLength());
        var index = 0;
        foreach (var item in input.RootElement.GetProperty("cases").EnumerateArray())
        {
            var captured = capturedCases[index++]; var caseId = item.GetProperty("caseId").GetString();
            Equal(caseId, captured.GetProperty("caseId").GetString());
            var leaf = item.GetProperty("leafId"); Check(Same(leaf, captured.GetProperty("leafId")), "Captured leaf differs.");
            var native = new SessionContextProjector().Project(entries, leaf.ValueKind == JsonValueKind.Null ? null : leaf.GetString());
            var model = native.Model is null ? "null" : JsonSerializer.Serialize(new { provider = native.Model.Provider, modelId = native.Model.ModelId });
            var fields = "\"messages\":" + Messages(native.Messages) + ",\"thinkingLevel\":" + JsonSerializer.Serialize(native.ThinkingLevel) + ",\"model\":" + model;
            var contributions = "[" + string.Join(',', native.ContextEntries.Select(entry =>
                "{\"sourceEntry\":" + entry.SourceEntry.WireBody.ToString() + ",\"messages\":" + Messages(entry.Messages) + "}")) + "]";
            using var projection = JsonDocument.Parse("{\"entries\":" + contributions + "," + fields + "}");
            using var context = JsonDocument.Parse("{" + fields + "}");
            using var llm = JsonDocument.Parse(Messages(native.LlmMessages));
            Check(Same(captured.GetProperty("projection"), projection.RootElement), "Complete native public projection differs: " + caseId);
            Check(Same(captured.GetProperty("context"), context.RootElement), "Complete native public context differs: " + caseId);
            Check(Same(captured.GetProperty("llmMessages"), llm.RootElement), "Complete native model conversion differs: " + caseId);
            var undefined = captured.GetProperty("ownUndefinedPaths");
            AssertAbsent(projection.RootElement, undefined.GetProperty("projection"));
            AssertAbsent(context.RootElement, undefined.GetProperty("context"));
            AssertAbsent(llm.RootElement, undefined.GetProperty("llmMessages"));
            for (var entry = 0; entry < entries.Length; entry++) Equal(originals[entry], entries[entry].WireBody.ToString());
        }
        Equal(4, index);

        async Task<byte[]> ReadPinned(string name, string sha)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "fixtures/pi-v0.99.1/session-context", name));
            Equal(sha, Convert.ToHexStringLower(SHA256.HashData(bytes))); return bytes;
        }
        static string Messages(ImmutableArray<TranscriptEntry> messages) => "[" + string.Join(',', messages.Select(message => message.WireBody.ToString())) + "]";
    }

    private static void AssertAbsent(JsonElement native, JsonElement paths)
    {
        foreach (var value in paths.EnumerateArray())
        {
            var segments = value.GetString()![1..].Split('/'); var current = native;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var segment = segments[index].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                current = current.ValueKind == JsonValueKind.Array ? current[int.Parse(segment, CultureInfo.InvariantCulture)] : current.GetProperty(segment);
            }
            var name = segments[^1].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            Check(current.ValueKind == JsonValueKind.Object && !current.TryGetProperty(name, out _), "Source own-undefined field must remain absent in native JSON.");
        }
    }

    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var leftProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var rightProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in left.EnumerateObject()) if (!leftProperties.TryAdd(property.Name, property.Value)) return false;
            foreach (var property in right.EnumerateObject()) if (!rightProperties.TryAdd(property.Name, property.Value)) return false;
            return leftProperties.Count == rightProperties.Count && leftProperties.All(property =>
                rightProperties.TryGetValue(property.Key, out var value) && Same(property.Value, value));
        }
        return left.ValueKind switch
        {
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            _ => left.GetRawText() == right.GetRawText()
        };
    }

    private static SessionEntry Edit(string id, string parent, string target, string replacement) => Entry("context_edit", id, parent,
        "{\"targetId\":" + JsonSerializer.Serialize(target) + ",\"replacement\":" + replacement + "}");
    private static SessionEntry Message(string id, string? parent, string message) => Entry("message", id, parent, "{\"message\":" + message + "}");
    private static SessionEntry Entry(string type, string id, string? parent, string fields, string timestamp = Time) => Codec.Parse(
        "{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) +
        ",\"timestamp\":" + JsonSerializer.Serialize(timestamp) + "," + fields[1..]);
    private static string User(string text) => "{\"role\":\"user\",\"content\":" + JsonSerializer.Serialize(text) + ",\"timestamp\":1}";
    private static string Assistant() => """{"role":"assistant","content":[{"type":"text","text":"stored answer"}],"api":"stored-api","provider":"stored-provider","model":"stored-model","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"error","timestamp":2,"thoughtSignature":"opaque\u002funchanged","opaque":{"wide":9007199254740993,"scaled":1.0,"nil":null}}""";
    private static string Text(TranscriptEntry message) => message.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Ordered source contributions differ.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Fails(SessionContextProjectionFailure failure, Action action) => Equal(failure, Throws<SessionContextProjectionException>(action).Failure);
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
