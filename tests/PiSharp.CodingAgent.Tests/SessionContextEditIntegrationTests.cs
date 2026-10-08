using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

internal static class SessionContextEditIntegrationTests
{
    private static readonly ModelDescriptor Model = new("fixture-model", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Time = "2024-04-01T00:00:00.000Z";
    private const string Family = "fixtures/pi-v0.99.1/session-context-edits";
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const string CaptureCandidate = "00054a0f61b9f48447115c1064b391d6d732a57b";
    private const string ManifestSha = "4ae0d820f0943aeba5e533cbfb530fff1cad5d91524847bdefdbead80ccc0948";
    private const string CaptureLockSha = "279b8b86f4cd256f56e642396a573981590b83499850c22ec866b5750be7f071";
    private static readonly string[] CaptureHashes =
    ["eebb9eb01fbcb9ad3d76be90dcde803eab146efd0f71315a89a559857064c96c", "f6477538a9d2e0191f961b0dc7c658d11705c65e16f1a276c7b85fed0c3059da"];
    private static readonly string[] ComparedSourceFields = ["header", "entries", "branch", "tree", "projection", "context", "llmMessages", "leafId", "sessionId", "cwd"];
    private static readonly string[] SourceCases =
    [
        "manager-repeated-latest-omission", "manager-exact-shape-all-roles", "manager-branch-before-after-sibling",
        "manager-compaction-before-after", "manager-admission-no-effects", "manager-memory-edit-history"
    ];
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("context-edit-integration active admission rejects before IDs clocks and file effects while imported replay stays permissive", Admission),
        ("context-edit-integration exact role normalization preserves original provider metadata raw billing and wrapper shape", ShapeAndHistory),
        ("context-edit-integration latest replacement and repeated null omissions refresh actual next requests and durable reopen", NextRequestAndReopen),
        ("context-edit-integration branch selection and both compaction boundaries retain raw forest and accounting", BranchAndCompaction),
        ("context-edit-integration direct abort cancellation disposal and actual held checkpoint join truthful durable outcomes", HeldOperations),
        ("context-edit-integration actual edited histories reach distinct Responses and Anthropic pairing boundaries", ProviderPairing)
    ];
    public static IEnumerable<(string Name, Func<Task> Run)> ReferenceCases() => SourceCases.Select(id =>
        ("context-edit-manager-reference exact whole source append records branches context and native author replay " + id,
            (Func<Task>)(() => CompareSource(id))));

    private static async Task Admission()
    {
        using var files = new Files(); await Seed(files.A, files.Root); var author = new Author(); var script = new Script();
        await using var session = await Open(files.A, author, script, "hidden"); var original = await Bytes(files.A);
        foreach (var (draft, failure) in new (SessionContextEditDraft, SessionContextEditFailure)[]
        {
            (new("absent", null), SessionContextEditFailure.TargetNotFound),
            (new("source", null), SessionContextEditFailure.TargetNotFound),
            (new("sibling", null), SessionContextEditFailure.TargetNotActive),
            (new("system", null), SessionContextEditFailure.TargetNotEditable),
            (new("state", null), SessionContextEditFailure.TargetNotEditable),
            (new("model", null), SessionContextEditFailure.TargetNotEditable),
            (new("user", JsonData.Parse("7")), SessionContextEditFailure.InvalidReplacement),
            (new("user", JsonData.EmptyObject), SessionContextEditFailure.InvalidReplacement),
            (new("user", JsonData.Parse("{\"content\":null}")), SessionContextEditFailure.InvalidReplacement),
            (new("user", JsonData.Parse("{\"content\":{}}")), SessionContextEditFailure.InvalidReplacement),
            (new("user", JsonData.Parse("{\"content\":[{\"type\":\"image\",\"data\":7,\"mimeType\":\"image/png\"}]}")), SessionContextEditFailure.UnsupportedReplacement)
        })
        {
            var failed = await Throws<SessionContextEditException>(() => session.AppendContextEditAsync("source", draft));
            Equal(failure, failed.Failure); Equal(0, author.Ids); Equal(0, author.Clocks);
            Equal("hidden", session.Snapshot.Context.LeafId); Check(!session.Snapshot.IsEditingContext && session.Snapshot.Fault is null, "Rejected edit poisoned or retained admission.");
            SameBytes(original, await Bytes(files.A));
        }
        ThrowsSync<JsonException>(() => JsonData.Parse("{\"content\":\"a\",\"opaque\":{\"x\":1,\"x\":2}}"));
        Equal(0, author.Ids); Equal(0, author.Clocks); SameBytes(original, await Bytes(files.A));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => session.AppendContextEditAsync("source", new("user", null), canceled.Token));
        await Throws<PersistentAgentSessionException>(() => session.AppendContextEditAsync("foreign", new("user", null)));
        Equal(0, author.Ids); Equal(0, author.Clocks); Equal(0, script.Requests.Count); SameBytes(original, await Bytes(files.A));
        var usable = await session.AppendContextEditAsync("source", new("user", Replacement("admitted")));
        Checkpoint(usable); Equal(1, author.Ids); Equal(1, author.Clocks);

        // These stored records are deliberately replayable even though the active manager cannot author them.
        await Seed(files.B, files.Root, extras:
        [
            Entry("context_edit", "missing-imported", "hidden", "\"targetId\":\"absent\",\"replacement\":null"),
            Entry("context_edit", "system-imported", "missing-imported", "\"targetId\":\"system\",\"replacement\":{\"content\":\"ignored\"}")
        ]);
        var imported = await Bytes(files.B);
        await using var replay = await Open(files.B, new(), new());
        Check(replay.Snapshot.Context.Messages.Any(message => message.Role == "system" && Text(message) == "original system"), "Manager admission tightened imported replay.");
        Equal(2, replay.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.ContextEdit)); SameBytes(imported, await Bytes(files.B));
    }

    private static async Task ShapeAndHistory()
    {
        using var files = new Files(); await Seed(files.A, files.Root); var author = new Author();
        await using var session = await Open(files.A, author, new(), "hidden");
        var originals = session.Snapshot.Log.Entries.ToDictionary(entry => entry.Id, entry => entry.WireBody.ToString(), StringComparer.Ordinal);
        var accounting = Statistics(session);
        var supplied = JsonData.Parse("{\"content\":\"replacement\",\"wrapper\":{\"mustRemain\":true,\"nil\":null}}");
        foreach (var target in new[] { "assistant", "result", "hidden", "user" })
        {
            var receipt = await session.AppendContextEditAsync("source", new(target, supplied)); Checkpoint(receipt);
            var replacement = receipt.Entry.WireBody.Value.GetProperty("replacement");
            if (target is "assistant" or "result")
            {
                Equal(1, replacement.EnumerateObject().Count());
                Same(JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"replacement\"}]}").Value, replacement, target + " new wrapper");
            }
            else Same(supplied.Value, replacement, target + " exact wrapper");
        }
        var array = JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"array\",\"extra\":null}],\"wrapper\":{\"mustRemain\":true}}");
        var arrayReceipt = await session.AppendContextEditAsync("source", new("assistant", array)); Same(array.Value, arrayReceipt.Entry.WireBody.Value.GetProperty("replacement"), "array wrapper");
        var image = JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"image\"},{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\",\"extra\":null}],\"wrapper\":null}");
        var imageReceipt = await session.AppendContextEditAsync("source", new("user", image)); Same(image.Value, imageReceipt.Entry.WireBody.Value.GetProperty("replacement"), "image wrapper");
        foreach (var original in originals) Equal(original.Value, session.Snapshot.Log.ById[original.Key].WireBody.ToString());
        SameAccounting(accounting, Statistics(session));
        Equal("1.00e400", session.Snapshot.Log.ById["state"].WireBody.Value.GetProperty("data").GetProperty("opaque").GetRawText());
        Check(session.Snapshot.Agent.Messages.All(message => !message.WireBody.ToString().Contains("1.00e400", StringComparison.Ordinal)), "Inert state acquired model authority.");
        foreach (var message in session.Snapshot.Context.Messages.Where(message => message.Role == "assistant"))
        {
            var source = session.Snapshot.Log.Entries.First(entry => entry.Kind == SessionEntryKind.Message && entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "assistant" &&
                entry.WireBody.Value.GetProperty("message").GetProperty("opaque").GetString() == message.WireBody.Value.GetProperty("opaque").GetString());
            foreach (var field in source.WireBody.Value.GetProperty("message").EnumerateObject().Where(field => field.Name != "content"))
                Same(field.Value, message.WireBody.Value.GetProperty(field.Name), "retained assistant " + field.Name);
        }
        Equal("{\"content\":\"replacement\",\"wrapper\":{\"mustRemain\":true,\"nil\":null}}", supplied.ToString());
    }

    private static async Task NextRequestAndReopen()
    {
        using var files = new Files(); await Seed(files.A, files.Root); var original = await Bytes(files.A); var script = new Script(); var author = new Author();
        await using (var session = await Open(files.A, author, script, "hidden"))
        {
            var accounting = Statistics(session);
            await session.AppendContextEditAsync("source", new("user", Replacement("first")));
            await session.AppendContextEditAsync("source", new("user", Replacement("latest")));
            Check(session.Snapshot.Agent.Messages.Any(message => message.Role == "user" && Text(message) == "latest"), "Agent retained stale unedited request context.");
            Check(!session.Snapshot.Agent.Messages.Any(message => Text(message) is "first" or "original user"), "Latest edit did not win.");
            await session.AppendContextEditAsync("source", new("user", null));
            await session.AppendContextEditAsync("source", new("user", JsonData.Null));
            Check(!session.Snapshot.Agent.Messages.Any(message => Text(message) is "latest" or "first" or "original user"), "A second null restored a contribution.");
            SameAccounting(accounting, Statistics(session));
            var run = await session.PromptAsync(User("next actual user")); Equal(AgentLoopStopReason.Completed, run.Reason); Equal(1, script.Requests.Count);
            Check(script.Requests.Single().Messages.Any(message => Text(message) == "next actual user") &&
                script.Requests.Single().Messages.All(message => Text(message) is not ("original user" or "first" or "latest" or "physical sibling")), "Actual next provider request used raw history or sibling state.");
            Prefix(original, await Bytes(files.A));
        }
        var before = await Bytes(files.A);
        var exported = await new SessionCopyService().CopyAsync(new(files.A, files.Export, SessionCopyFormat.NativeExact));
        Check(exported.Published && exported.Retention is { ExcludedRecords: 0, ExcludedFields: 0 }, "Raw edit export excluded data.");
        SameBytes(before, await Bytes(files.Export)); SameBytes(before, await Bytes(files.A));
        await using var reopened = await Open(files.A, new(), new());
        Equal(4, reopened.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.ContextEdit));
        Check(reopened.Snapshot.Context.Messages.All(message => Text(message) != "original user"), "Reopen lost omission semantics.");
        Equal("original user", TextMessage(reopened.Snapshot.Log.ById["user"])); SameBytes(before, await Bytes(files.A));
        var latest = await reopened.AppendContextEditAsync("source", new("user", Replacement("explicit replacement restores content")));
        Checkpoint(latest); Check(reopened.Snapshot.Agent.Messages.Any(message => Text(message) == "explicit replacement restores content"), "A later explicit replacement was not applied.");
        Prefix(before, await Bytes(files.A));
    }

    private static async Task BranchAndCompaction()
    {
        using var files = new Files(); await Seed(files.A, files.Root); var original = await Bytes(files.A); var author = new Author(); string left;
        await using (var session = await Open(files.A, author, new(), "hidden"))
            left = (await session.AppendContextEditAsync("source", new("user", Replacement("left replacement")))).Entry.Id;
        var afterLeft = await Bytes(files.A); string right;
        await using (var before = await Open(files.A, author, new(), "user"))
        {
            Check(before.Snapshot.Context.Messages.Any(message => Text(message) == "original user"), "Branch before edit did not reveal original.");
            right = (await before.AppendContextEditAsync("source", new("user", null))).Entry.Id;
        }
        await using (var selected = await Open(files.A, author, new(), left))
        {
            Check(selected.Snapshot.Context.Messages.Any(message => Text(message) == "left replacement") && selected.Snapshot.Context.Ancestry.All(entry => entry.Id != right), "Sibling edit leaked across selected ancestry.");
            Check(selected.Snapshot.Log.ById.ContainsKey(right) && selected.Snapshot.Log.ById.ContainsKey("sibling"), "Branch selection rewrote abandoned physical records.");
        }
        await using (var root = await Open(files.A, author, new(), null, useLatest: false))
        {
            Check(root.Snapshot.Context.Ancestry.IsEmpty && root.Snapshot.Agent.Messages.IsEmpty, "Explicit root replayed branch edits.");
            var failed = await Throws<SessionContextEditException>(() => root.AppendContextEditAsync("source", new("user", null)));
            Equal(SessionContextEditFailure.TargetNotActive, failed.Failure);
        }
        Prefix(original, afterLeft); Prefix(afterLeft, await Bytes(files.A));

        await Seed(files.B, files.Root, extras:
        [
            Entry("context_edit", "pre-compaction-edit", "hidden", "\"targetId\":\"assistant\",\"replacement\":{\"content\":[{\"type\":\"text\",\"text\":\"pre-compaction replacement\"}]}"),
            Entry("compaction", "checkpoint", "pre-compaction-edit", "\"summary\":\"SUMMARY\",\"firstKeptEntryId\":\"assistant\",\"tokensBefore\":46,\"opaque\":null")
        ]);
        await using var compacted = await Open(files.B, new(), new()); var accounting = Statistics(compacted); var compactedBytes = await Bytes(files.B);
        Check(compacted.Snapshot.Context.Messages.Any(message => Text(message) == "pre-compaction replacement"), "Retained pre-compaction edit vanished.");
        await compacted.AppendContextEditAsync("source", new("user", Replacement("trimmed target remains outside context")));
        Check(compacted.Snapshot.Agent.Messages.All(message => Text(message) != "trimmed target remains outside context"), "Edit resurrected a trimmed target.");
        await compacted.AppendContextEditAsync("source", new("assistant", Replacement("post-compaction replacement")));
        Check(compacted.Snapshot.Agent.Messages.Any(message => Text(message) == "post-compaction replacement"), "Post-compaction edit did not win.");
        await compacted.AppendContextEditAsync("source", new("assistant", null));
        Check(compacted.Snapshot.Agent.Messages.All(message => Text(message) != "post-compaction replacement"), "Post-compaction omission retained the assistant contribution.");
        SameAccounting(accounting, Statistics(compacted)); Prefix(compactedBytes, await Bytes(files.B));
        Equal("assistant opaque", compacted.Snapshot.Log.ById["assistant"].WireBody.Value.GetProperty("message").GetProperty("opaque").GetString());
    }

    private static async Task HeldOperations()
    {
        foreach (var mode in new[] { "caller", "abort", "dispose" })
        {
            using var files = new Files(); await Seed(files.A, files.Root); var original = await Bytes(files.A);
            var session = await Open(files.A, new(), new(), "hidden"); using var cancellation = new CancellationTokenSource();
            var entered = Gate(); var canceled = Gate(); var release = Gate(); Task? disposal = null;
            var editing = session.AppendContextEditAsync("source", new("user", Replacement("held")), cancellation.Token, async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { canceled.TrySetResult(); await release.Task; }
            });
            try
            {
                await entered.Task.WaitAsync(Bound); Check(session.Snapshot.IsEditingContext && !session.WaitForIdleAsync().IsCompleted, "Held edit did not retain its actual admission.");
                if (mode == "caller") cancellation.Cancel();
                else if (mode == "abort") Check(session.Abort(), "Direct Abort did not cancel the held context edit.");
                else disposal = session.DisposeAsync().AsTask();
                await canceled.Task.WaitAsync(Bound);
                Check(!editing.IsCompleted && !session.WaitForIdleAsync().IsCompleted && (disposal is null || !disposal.IsCompleted), "Canceled edit abandoned held preflight cleanup.");
                SameBytes(original, await Bytes(files.A)); release.TrySetResult(); await Throws<OperationCanceledException>(() => editing);
                if (disposal is not null) await disposal.WaitAsync(Bound);
                else
                {
                    Check(!session.Snapshot.IsEditingContext && session.Snapshot.Fault is null, "Canceled preflight poisoned active context.");
                    Checkpoint(await session.AppendContextEditAsync("source", new("user", Replacement("usable after cancel"))));
                }
            }
            finally { cancellation.Cancel(); release.TrySetResult(); await Drain(editing); await session.DisposeAsync(); }
            await using var reopened = await Open(files.A, new(), new());
            Equal(mode == "dispose" ? 0 : 1, reopened.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.ContextEdit));
        }
        using (var files = new Files())
        {
            await Seed(files.A, files.Root); var factory = new HeldFactory(); var original = await Bytes(files.A);
            await using var session = await Open(files.A, new(), new(), "hidden", storage: factory); var held = factory.Storage!.Arm();
            var editing = session.AppendContextEditAsync("source", new("user", Replacement("checkpoint admitted")));
            try
            {
                await held.Entered.Task.WaitAsync(Bound); Check(session.Snapshot.IsEditingContext, "Held actual checkpoint released context admission.");
                Check(!session.Snapshot.Log.Entries.Any(entry => entry.Kind == SessionEntryKind.ContextEdit), "Unacknowledged edit was published to snapshots.");
                Check(session.Abort(), "Abort did not reach checkpoint-held edit."); held.Release.TrySetResult();
                var receipt = await editing.WaitAsync(Bound); Checkpoint(receipt);
                Check(session.Snapshot.Agent.Messages.Any(message => Text(message) == "checkpoint admitted"), "Late abort hid acknowledged edit.");
                Prefix(original, await Bytes(files.A)); Equal(1, session.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.ContextEdit));
            }
            finally { held.Release.TrySetResult(); await Drain(editing); }
        }
    }

    private static async Task ProviderPairing()
    {
        foreach (var schedule in new[] { "omit-call", "omit-result", "duplicate-call", "rename-call" })
        {
            using var files = new Files(); await Seed(files.A, files.Root);
            var script = new Script(); await using var session = await Open(files.A, new(), script, "hidden");
            var originals = session.Snapshot.Log.Entries.Select(entry => entry.WireBody.ToString()).ToArray(); var accounting = Statistics(session);
            if (schedule.StartsWith("omit-", StringComparison.Ordinal))
                await session.AppendContextEditAsync("source", new(schedule == "omit-call" ? "call" : "result", null));
            else
                await session.AppendContextEditAsync("source", new(schedule == "duplicate-call" ? "assistant" : "call",
                    JsonData.Parse("{\"content\":[{\"type\":\"toolCall\",\"id\":\"authored-call\",\"name\":\"" +
                        (schedule == "duplicate-call" ? "read" : "renamed-read") + "\",\"arguments\":{}}]}")));
            var actual = session.Snapshot.Agent.Messages;
            // Provider projection consumes the existing LLM view, which maps canonical custom messages to users.
            var llm = session.Snapshot.Context.LlmMessages;
            Check(actual.Any(message => message.Role == "custom") && !llm.Any(message => message.Role == "custom"),
                "Pairing fixture lost its canonical custom message or LLM role conversion.");
            var responses = ThrowsSync<ResponsesProjectionException>(() => new ResponsesTranscriptProjector(new(false)).Project(new(Model, llm)));
            Equal(schedule == "duplicate-call" ? ResponsesProjectionFailure.IdentityCollision : ResponsesProjectionFailure.UnmatchedToolResult, responses.Failure);
            var anthropicRequest = new ChatRequest(Model with { Api = "anthropic-messages" }, llm);
            var anthropic = new AnthropicMessagesRequestProjector(new(128, CacheRetention: AnthropicCacheRetention.None));
            if (schedule == "omit-call")
                Equal(AnthropicRequestFailure.UnmatchedToolResult, ThrowsSync<AnthropicRequestException>(() => anthropic.Project(anthropicRequest)).Failure);
            else if (schedule == "duplicate-call")
                Equal(AnthropicRequestFailure.IdentityCollision, ThrowsSync<AnthropicRequestException>(() => anthropic.Project(anthropicRequest)).Failure);
            else if (schedule == "omit-result")
            {
                var projected = anthropic.Project(anthropicRequest);
                var synthetic = projected.Value.GetProperty("messages").EnumerateArray()
                    .Where(message => message.GetProperty("content").ValueKind == JsonValueKind.Array)
                    .SelectMany(message => message.GetProperty("content").EnumerateArray())
                    .Single(block => block.GetProperty("type").GetString() == "tool_result");
                Equal("No result provided", synthetic.GetProperty("content").GetString()); Check(synthetic.GetProperty("is_error").GetBoolean(), "Missing result repair lacked explicit error status.");
                Equal("authored-call", synthetic.GetProperty("tool_use_id").GetString());
            }
            else
            {
                // Existing Anthropic policy pairs by ID; it does not impose the Responses name check.
                var projected = anthropic.Project(anthropicRequest);
                Check(projected.ToString().Contains("renamed-read", StringComparison.Ordinal) && projected.ToString().Contains("tool original", StringComparison.Ordinal),
                    "Anthropic identity-only pairing policy changed.");
            }
            for (var index = 0; index < originals.Length; index++) Equal(originals[index], session.Snapshot.Log.Entries[index].WireBody.ToString());
            SameAccounting(accounting, Statistics(session)); Equal(0, script.Requests.Count);
        }
    }

    private static async Task CompareSource(string caseId)
    {
        using var first = await ReadCapture(1); using var second = await ReadCapture(2);
        foreach (var (capture, repeat) in new[] { (first, 1), (second, 2) })
        {
            var cases = capture.RootElement.GetProperty("cases"); Equal(SourceCases.Length, cases.GetArrayLength());
            var test = cases.EnumerateArray().Single(test => test.GetProperty("caseId").GetString() == caseId);
            foreach (var relation in test.GetProperty("contract").GetProperty("relations").EnumerateObject()) Check(relation.Value.GetBoolean(), "Source relation failed: " + relation.Name);
            var raw = test.GetProperty("rawObservations"); var checkpoints = raw.GetProperty("checkpoints").EnumerateArray().ToArray();
            var operations = raw.GetProperty("operationReturns").EnumerateArray().ToArray(); Equal(operations.Length + 1, checkpoints.Length);
            using var files = new Files(); var memory = caseId == "manager-memory-edit-history";
            var backend = new SessionStorageBackend(files.Root, memory ? SessionStorageMode.InMemory : SessionStorageMode.LazyLocal);
            var options = new SessionLogStoreOptions(StorageFactory: backend);
            var initial = checkpoints[0].GetProperty("manager"); var header = Codec.Read(initial.GetProperty("header"));
            await using (var store = await SessionLogStore.CreateNewAsync(files.A, header, options)) { }
            string? selected = null;
            await CompareCheckpoint(checkpoints[0], files.A, options, backend, selected);
            for (var index = 0; index < operations.Length; index++)
            {
                var operation = operations[index]; var method = operation.GetProperty("method").GetString(); var args = operation.GetProperty("args");
                UndefinedAbsences(operation);
                var before = await BackendBytes(backend, files.A);
                if (method == "appendContextEdit")
                {
                    var author = new Author(); var script = new Script();
                    if (!operation.TryGetProperty("error", out _))
                    {
                        var expected = operation.GetProperty("returnedEntry");
                        author.PlannedId = expected.GetProperty("id").GetString();
                        author.PlannedTimestamp = DateTimeOffset.Parse(expected.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
                    }
                    await using var session = await Open(files.A, author, script, selected, useLatest: false, storage: backend);
                    var draft = new SessionContextEditDraft(args[0].GetString()!, JsonData.FromElement(args[1]));
                    if (operation.TryGetProperty("error", out _))
                    {
                        await Throws<SessionContextEditException>(() => session.AppendContextEditAsync(header.Id, draft));
                        Equal(0, author.Ids); Equal(0, author.Clocks); SameBytes(before, await BackendBytes(backend, files.A));
                        foreach (var relation in new[] { "failedOperationPreservedRecords", "failedOperationPreservedLeaf", "failedOperationPreservedBytes" })
                            Check(operation.GetProperty(relation).GetBoolean(), "Rejected source operation changed state.");
                    }
                    else
                    {
                        var receipt = await session.AppendContextEditAsync(header.Id, draft); Check(receipt.Append.CheckpointAcknowledged, "Native source replay lacked acknowledged checkpoint.");
                        Same(operation.GetProperty("returnedEntry"), receipt.Entry.WireBody.Value, caseId + "/actual native authored edit");
                        Equal(1, author.Ids); Equal(1, author.Clocks); selected = receipt.Entry.Id;
                    }
                    Equal(0, script.Requests.Count);
                }
                else if (method is "branch" or "resetLeaf") selected = method == "resetLeaf" ? null : args[0].GetString();
                else
                {
                    var entry = Codec.Read(operation.GetProperty("returnedEntry"));
                    await using var store = await SessionLogStore.OpenAsync(files.A, options); await store.AppendAsync([entry]); selected = entry.Id;
                }
                await CompareCheckpoint(checkpoints[index + 1], files.A, options, backend, selected);
            }
            Equal(0, backend.ActiveWriterCount);
            if (raw.TryGetProperty("reopened", out var reopened))
            {
                UndefinedAbsences(reopened);
                var bytes = await BackendBytes(backend, files.A);
                await using var session = await Open(files.A, new(), new(), storage: backend);
                using var frame = Frame(session.Snapshot.Log.Header, session.Snapshot.Log.Entries, session.Snapshot.Context);
                foreach (var field in new[] { "header", "entries", "projection", "context", "llmMessages" })
                    Same(reopened.GetProperty(field), frame.RootElement.GetProperty(field), caseId + "/reopen/" + field);
                SameBytes(bytes, await BackendBytes(backend, files.A));
            }
            var undefinedCount = checkpoints.Sum(checkpoint => checkpoint.GetProperty("ownUndefinedPaths").GetArrayLength()) +
                operations.Sum(operation => operation.GetProperty("ownUndefinedPaths").GetArrayLength());
            Console.WriteLine($"CONTEXT-EDIT-SOURCE {caseId}/repeat{repeat}: {checkpoints.Length} complete checkpoints, {operations.Length} operations, {undefinedCount} own-undefined paths retained; capture {CaptureHashes[repeat - 1]}; execution candidate {CaptureCandidate}");
        }
    }

    private static async Task CompareCheckpoint(JsonElement checkpoint, string path, SessionLogStoreOptions options,
        SessionStorageBackend backend, string? selected)
    {
        await using (var store = await SessionLogStore.OpenAsync(path, options))
        {
            var snapshot = store.Snapshot; var context = new SessionContextProjector().Project(snapshot.Entries, selected);
            using var frame = Frame(snapshot.Header, snapshot.Entries, context); var source = checkpoint.GetProperty("manager");
            UndefinedAbsences(checkpoint);
            foreach (var field in ComparedSourceFields)
                Same(source.GetProperty(field), frame.RootElement.GetProperty(field), checkpoint.GetProperty("name").GetString() + "/" + field);
            foreach (var pointer in checkpoint.GetProperty("ownUndefinedPaths").EnumerateArray().Select(path => path.GetString()!))
                if (pointer.StartsWith("/manager/", StringComparison.Ordinal) && ComparedSourceFields.Any(field => pointer.StartsWith("/manager/" + field + "/", StringComparison.Ordinal)))
                    Check(!HasJsonPath(frame.RootElement, pointer[8..]), "Native replay materialized a source own-undefined property: " + pointer);
            foreach (var original in snapshot.Entries) Equal(original.WireBody.ToString(), store.Snapshot.ById[original.Id].WireBody.ToString());
        }
        var physical = checkpoint.GetProperty("physicalFile"); Equal(physical.GetProperty("exists").GetBoolean(), File.Exists(path));
        if (physical.GetProperty("exists").GetBoolean())
        {
            var bytes = await File.ReadAllBytesAsync(path);
            SameBytes(Convert.FromBase64String(physical.GetProperty("base64").GetString()!), bytes);
            Equal(physical.GetProperty("sha256").GetString(), Hash(bytes)); Equal(physical.GetProperty("utf8").GetString(), Utf8.GetString(bytes));
            SameBytes(bytes, await BackendBytes(backend, path));
        }
    }

    private static async Task<JsonDocument> ReadCapture(int repeat)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, Family, "manifest.json"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Context-edit whole-manager fixture has not been captured and sealed.");
        Check(repeat is 1 or 2, "Only the two actual source runs are admitted.");
        using var manifest = JsonDocument.Parse(await Pinned("manifest.json", ManifestSha, 8149));
        using var capturedLock = JsonDocument.Parse(await Pinned("oracle.lock.json", CaptureLockSha, 155147));
        var declared = manifest.RootElement;
        Equal(SourceSha, declared.GetProperty("sourceSha").GetString()); Equal(CaptureCandidate, declared.GetProperty("captureExecutionCandidate").GetString());
        Equal(6, declared.GetProperty("caseCount").GetInt32()); Equal(2, declared.GetProperty("repeatRuns").GetInt32());
        Check(declared.GetProperty("caseIds").EnumerateArray().Select(id => id.GetString()).SequenceEqual(SourceCases), "Source schedule inventory changed.");
        ValidateCaptureProvenance(capturedLock.RootElement);
        ProvenanceMutationControls(capturedLock.RootElement);
        // The pinned inventory owns every original capture output, including full stdout and empty stderr.
        foreach (var file in declared.GetProperty("files").EnumerateArray())
            _ = await Pinned(file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString()!, file.GetProperty("bytes").GetInt32());
        var capturePin = declared.GetProperty("captures")[repeat - 1]; Equal(repeat, capturePin.GetProperty("repeat").GetInt32());
        var name = repeat == 1 ? "capture.json" : "repeat-2.json"; Equal(name, capturePin.GetProperty("path").GetString());
        Equal(2_815_925, capturePin.GetProperty("bytes").GetInt32()); Equal(CaptureHashes[repeat - 1], capturePin.GetProperty("sha256").GetString());
        var bytes = await Pinned(name, CaptureHashes[repeat - 1], 2_815_925);
        var capture = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        try
        {
            Equal(SourceSha, capture.RootElement.GetProperty("sourceSha").GetString());
            Same(capturedLock.RootElement.GetProperty("loadedModules"), capture.RootElement.GetProperty("loadedModules"), "whole original qualified module closure");
            var cases = capture.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            Equal(87, cases.Sum(test => test.GetProperty("rawObservations").GetProperty("checkpoints").GetArrayLength()));
            Equal(81, cases.Sum(test => test.GetProperty("rawObservations").GetProperty("operationReturns").GetArrayLength()));
            var operations = cases.SelectMany(test => test.GetProperty("rawObservations").GetProperty("operationReturns").EnumerateArray()).ToArray();
            Equal(22, operations.Count(operation => operation.GetProperty("method").GetString() == "appendContextEdit" && !operation.TryGetProperty("error", out _)));
            Equal(12, operations.Count(operation => operation.GetProperty("method").GetString() == "appendContextEdit" && operation.TryGetProperty("error", out _)));
            Equal(1, cases.Count(test => test.GetProperty("rawObservations").TryGetProperty("reopened", out _)));
            Equal(1654, cases.Sum(test => test.GetProperty("rawObservations").GetProperty("checkpoints").EnumerateArray().Sum(checkpoint => checkpoint.GetProperty("ownUndefinedPaths").GetArrayLength())) +
                operations.Sum(operation => operation.GetProperty("ownUndefinedPaths").GetArrayLength()));
            foreach (var check in new[] { "wholeSessionManagerLoaded", "clocksAndRngUnmodified", "scratchWriteGuard", "networkAndProcessesDenied" })
                Check(capture.RootElement.GetProperty("checks").GetProperty(check).GetBoolean(), "Whole-source capture guard changed.");
            Equal(0, capture.RootElement.GetProperty("checks").GetProperty("ownedOpenDescriptors").GetInt32()); return capture;
        }
        catch { capture.Dispose(); throw; }
        async Task<byte[]> Pinned(string name, string expectedHash, int expectedBytes)
        {
            Check(name == Path.GetFileName(name) && expectedBytes is >= 0 and <= 16_777_216, "Reference inventory escaped its separate immutable read bound.");
            var path = Path.Combine(root, Family, name); Equal((long)expectedBytes, new FileInfo(path).Length);
            var content = await File.ReadAllBytesAsync(path); Equal(expectedBytes, content.Length); Equal(expectedHash, Hash(content)); return content;
        }
    }

    private static void ValidateCaptureProvenance(JsonElement provenance)
    {
        Equal(SourceSha, provenance.GetProperty("sourceSha").GetString());
        Equal(CaptureCandidate, provenance.GetProperty("captureExecutionCandidate").GetString());
        foreach (var flag in new[] { "sourceUnchanged", "dependenciesUnchanged", "loadedModulesUnchanged" }) Check(provenance.GetProperty(flag).GetBoolean(), "Capture provenance changed: " + flag);
        Equal("b2fbfda80b8aee1bf3cbd1742cfe13c575d0032250d8f316b1503b8c400552f2", provenance.GetProperty("qualificationLockSha256").GetString());
        var pins = provenance.GetProperty("environmentPins"); Equal(SourceSha, pins.GetProperty("sourceSha").GetString());
        Equal(2093, pins.GetProperty("sourceFingerprint").GetProperty("canonicalGit").GetProperty("files").GetInt32());
        Equal("2d65bfaee0e2556cb82ae7e0425de68560ecc3be4451f69ceff3bcc9c6144aa3", pins.GetProperty("sourceFingerprint").GetProperty("canonicalGit").GetProperty("sha256").GetString());
        Equal("v24.19.0", pins.GetProperty("runtime").GetProperty("version").GetString());
        Equal("3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237", pins.GetProperty("runtime").GetProperty("sha256").GetString());
        Equal("3b8dcf94bbd7ad4b39570d762ba84db44adc38d817af1d35263f835ccc93c570", pins.GetProperty("contextEditHarness").GetProperty("sha256").GetString());
        var packages = new[] { "cross-spawn", "isexe", "partial-json", "path-key", "shebang-command", "shebang-regex", "typebox", "which" };
        Check(pins.GetProperty("dependencies").EnumerateArray().Select(package => package.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).SequenceEqual(packages), "Original eight dependency packages changed.");
        Equal(715, provenance.GetProperty("loadedModules").GetArrayLength());
        var children = provenance.GetProperty("children"); Equal(2, children.GetArrayLength());
        for (var index = 0; index < 2; index++)
        {
            var child = children[index]; Equal(index + 1, child.GetProperty("repeat").GetInt32()); Equal(0, child.GetProperty("status").GetInt32());
            Check(child.GetProperty("signal").ValueKind == JsonValueKind.Null && child.GetProperty("error").ValueKind == JsonValueKind.Null, "Exit code alone cannot qualify a timeout, maxBuffer error or signaled source child.");
            Equal(20_000, child.GetProperty("boundedTimeoutMilliseconds").GetInt32()); Equal(8_388_608, child.GetProperty("maxBufferBytes").GetInt32());
            Equal(1_474_860, child.GetProperty("stdoutBytes").GetInt32()); Equal(0, child.GetProperty("stderrBytes").GetInt32());
        }
        var runs = provenance.GetProperty("genuineSourceRuns"); Equal(2, runs.GetArrayLength());
        foreach (var run in runs.EnumerateArray())
        { Equal(87, run.GetProperty("checkpoints").GetInt32()); Equal(1, run.GetProperty("reopenViews").GetInt32()); Equal(81, run.GetProperty("operations").GetInt32()); Equal(22, run.GetProperty("successfulEdits").GetInt32()); Equal(12, run.GetProperty("rejectedEdits").GetInt32()); Equal(1654, run.GetProperty("ownUndefinedPaths").GetInt32()); }
    }
    private static void ProvenanceMutationControls(JsonElement provenance)
    {
        var relabeled = JsonNode.Parse(provenance.GetRawText())!.AsObject();
        relabeled["captureExecutionCandidate"] = "ffffffffffffffffffffffffffffffffffffffff";
        ThrowsSync<InvalidOperationException>(() => ValidateCaptureProvenance(JsonSerializer.SerializeToElement(relabeled)));
        var erroredZeroExit = JsonNode.Parse(provenance.GetRawText())!.AsObject();
        erroredZeroExit["children"]![0]!["error"] = "timeout or maxBuffer failure";
        ThrowsSync<InvalidOperationException>(() => ValidateCaptureProvenance(JsonSerializer.SerializeToElement(erroredZeroExit)));
        ValidateCaptureProvenance(provenance);
    }
    private static void UndefinedAbsences(JsonElement captured)
    {
        foreach (var path in captured.GetProperty("ownUndefinedPaths").EnumerateArray())
            Check(!HasJsonPath(captured, path.GetString()!), "Retained source own-undefined field no longer corresponds to JSON absence: " + path.GetString());
    }
    private static bool HasJsonPath(JsonElement value, string pointer)
    {
        Check(pointer.StartsWith("/", StringComparison.Ordinal), "Own-undefined path is not a JSON pointer.");
        foreach (var escaped in pointer[1..].Split('/'))
        {
            var name = escaped.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            { if (!value.TryGetProperty(name, out value)) return false; }
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
            else return false;
        }
        return true;
    }

    private static JsonDocument Frame(SessionEntry header, ImmutableArray<SessionEntry> entries, SessionContextProjection context)
    {
        var history = new SessionHistoryProjector().Project(entries, context.LeafId); var tree = history.Tree;
        return Write(writer =>
        {
            writer.WriteStartObject(); writer.WritePropertyName("header"); header.WireBody.Value.WriteTo(writer);
            writer.WritePropertyName("entries"); Entries(writer, entries); writer.WritePropertyName("branch"); Entries(writer, context.Ancestry);
            writer.WritePropertyName("tree"); writer.WriteStartArray(); foreach (var root in tree.RootIds) Node(root); writer.WriteEndArray();
            writer.WritePropertyName("projection"); Context(writer, context, true); writer.WritePropertyName("context"); Context(writer, context, false);
            writer.WritePropertyName("llmMessages"); Messages(writer, context.LlmMessages); writer.WriteString("leafId", context.LeafId);
            writer.WriteString("sessionId", header.Id); writer.WriteString("cwd", header.WireBody.Value.GetProperty("cwd").GetString()); writer.WriteEndObject();
            void Node(string id)
            {
                var item = tree.ById[id]; writer.WriteStartObject(); writer.WritePropertyName("entry"); item.Entry.WireBody.Value.WriteTo(writer);
                writer.WritePropertyName("children"); writer.WriteStartArray(); var ordered = tree.GetChronologicalChildren(id);
                Equal(SessionTreeOrderStatus.Completed, ordered.Status); foreach (var child in ordered.Entries) Node(child.Id); writer.WriteEndArray();
                if (item.ResolvedLabel is not null) { writer.WriteString("label", item.ResolvedLabel.Label); writer.WriteString("labelTimestamp", item.ResolvedLabel.Timestamp); }
                writer.WriteEndObject();
            }
        });
    }
    private static void Context(Utf8JsonWriter writer, SessionContextProjection context, bool projection)
    {
        writer.WriteStartObject();
        if (projection)
        {
            writer.WritePropertyName("entries"); writer.WriteStartArray();
            foreach (var contribution in context.ContextEntries)
            {
                writer.WriteStartObject(); writer.WritePropertyName("sourceEntry"); contribution.SourceEntry.WireBody.Value.WriteTo(writer);
                writer.WritePropertyName("messages"); Messages(writer, contribution.Messages); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WritePropertyName("messages"); Messages(writer, context.Messages); writer.WriteString("thinkingLevel", context.ThinkingLevel);
        writer.WritePropertyName("model");
        if (context.Model is null) writer.WriteNullValue();
        else { writer.WriteStartObject(); writer.WriteString("provider", context.Model.Provider); writer.WriteString("modelId", context.Model.ModelId); writer.WriteEndObject(); }
        writer.WriteEndObject();
    }
    private static void Entries(Utf8JsonWriter writer, IEnumerable<SessionEntry> entries)
    { writer.WriteStartArray(); foreach (var entry in entries) entry.WireBody.Value.WriteTo(writer); writer.WriteEndArray(); }
    private static void Messages(Utf8JsonWriter writer, IEnumerable<TranscriptEntry> messages)
    { writer.WriteStartArray(); foreach (var message in messages) message.WireBody.Value.WriteTo(writer); writer.WriteEndArray(); }
    private static JsonDocument Write(Action<Utf8JsonWriter> write)
    { using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output)) write(writer); return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 128 }); }

    private static SessionHistoryStatistics Statistics(PersistentAgentSession session) =>
        new SessionHistoryProjector().Project(session.Snapshot.Log.Entries, session.Snapshot.Context.LeafId).SessionStatistics;
    private static void SameAccounting(SessionHistoryStatistics expected, SessionHistoryStatistics actual)
    {
        Equal(expected.UserMessages, actual.UserMessages); Equal(expected.AssistantMessages, actual.AssistantMessages);
        Equal(expected.ToolCalls, actual.ToolCalls); Equal(expected.ToolResults, actual.ToolResults); Equal(expected.TotalMessages, actual.TotalMessages);
        Equal(expected.Status, actual.Status); Equal(expected.Totals, actual.Totals);
        Check(expected.UnsupportedRecordIndexes.SequenceEqual(actual.UnsupportedRecordIndexes), "Edits changed unsupported raw accounting indexes.");
        Equal(expected.Contributions.Length, actual.Contributions.Length);
        for (var index = 0; index < expected.Contributions.Length; index++)
        {
            Equal(expected.Contributions[index].SourceEntry.WireBody.ToString(), actual.Contributions[index].SourceEntry.WireBody.ToString());
            Equal(expected.Contributions[index].Usage.ToString(), actual.Contributions[index].Usage.ToString());
        }
    }
    private static JsonData Replacement(string text) => JsonData.Parse(JsonSerializer.Serialize(new { content = text, wrapper = new { retained = true } }));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 7 })));
    private static string Text(TranscriptEntry message)
    { var body = message.WireBody.Value; if (!body.TryGetProperty("content", out var content)) return ""; return content.ValueKind == JsonValueKind.String ? content.GetString()! : content.ValueKind == JsonValueKind.Array ? string.Concat(content.EnumerateArray().Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "text").Select(part => part.GetProperty("text").GetString())) : ""; }
    private static string TextMessage(SessionEntry entry) => Text(new(entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString()!, JsonData.FromElement(entry.WireBody.Value.GetProperty("message"))));
    private static SessionEntry Entry(string type, string id, string? parent, string fields) => Codec.Parse("{\"type\":" + JsonSerializer.Serialize(type) +
        ",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Time + "\"," + fields + "}");
    private static SessionEntry Header(string cwd) => Codec.Parse("{\"type\":\"session\",\"version\":3,\"id\":\"source\",\"timestamp\":\"" + Time + "\",\"cwd\":" + JsonSerializer.Serialize(cwd) + "}");
    private const string Usage = "\"usage\":{\"input\":11,\"output\":7,\"cacheRead\":3,\"cacheWrite\":2,\"totalTokens\":23,\"cost\":{\"input\":0.11,\"output\":0.07,\"cacheRead\":0.03,\"cacheWrite\":0.02,\"total\":0.23}}";
    private static async Task Seed(string path, string cwd, ImmutableArray<SessionEntry> extras = default)
    {
        await using var store = await SessionLogStore.CreateNewAsync(path, Header(cwd));
        await store.AppendAsync(
        [
            Entry("model_change", "model", null, "\"provider\":\"fixture\",\"modelId\":\"fixture-model\""),
            Entry("message", "system", "model", "\"message\":{\"role\":\"system\",\"content\":\"original system\",\"timestamp\":7}"),
            Entry("custom", "state", "system", "\"customType\":\"disabled.extension\",\"data\":{\"schemaVersion\":99,\"opaque\":1.00e400,\"nil\":null}"),
            Entry("message", "user", "state", "\"message\":{\"role\":\"user\",\"content\":\"original user\",\"timestamp\":7,\"opaque\":null}"),
            Entry("message", "assistant", "user", "\"message\":{\"role\":\"assistant\",\"api\":\"openai-responses\",\"provider\":\"fixture\",\"model\":\"fixture-model\",\"content\":[{\"type\":\"text\",\"text\":\"assistant original\"}],\"timestamp\":7,\"stopReason\":\"stop\",\"opaque\":\"assistant opaque\"," + Usage + "}"),
            Entry("message", "call", "assistant", "\"message\":{\"role\":\"assistant\",\"api\":\"openai-responses\",\"provider\":\"fixture\",\"model\":\"fixture-model\",\"content\":[{\"type\":\"toolCall\",\"id\":\"authored-call\",\"name\":\"read\",\"arguments\":{\"path\":\"unopened:/fixture\"}}],\"timestamp\":7,\"stopReason\":\"toolUse\",\"opaque\":\"call opaque\"," + Usage + "}"),
            Entry("message", "result", "call", "\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"authored-call\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"tool original\"}],\"isError\":false,\"timestamp\":7,\"opaque\":null}"),
            Entry("custom_message", "hidden", "result", "\"customType\":\"hidden.fixture\",\"content\":\"hidden original\",\"display\":false,\"details\":{\"opaque\":null}"),
            Entry("message", "sibling", "user", "\"message\":{\"role\":\"user\",\"content\":\"physical sibling\",\"timestamp\":7}")
        ]);
        if (!extras.IsDefaultOrEmpty) await store.AppendAsync(extras);
    }
    private static Task<PersistentAgentSession> Open(string path, Author author, Script script, string? leaf = null, bool useLatest = true, ISessionLogStorageFactory? storage = null) =>
        PersistentAgentSession.OpenAsync(path, new AgentConfiguration(Model, script, []), author.Clock, author.Next,
            new(UseLatestLeaf: leaf is null && useLatest, SelectedLeafId: leaf, SessionLogStoreOptions: new(StorageFactory: storage)));
    private static void Checkpoint(SessionContextEditReceipt receipt)
    { Check(receipt.Append.Accepted && receipt.Append.Flushed && receipt.Append.DurableCheckpointAcknowledged && receipt.Append.CheckpointAcknowledged, "Local edit did not acknowledge actual durable flush."); Equal(receipt.Entry.Id, receipt.Context.LeafId); Equal(receipt.Entry.Id, receipt.Append.Snapshot.LeafId); }
    private static async Task<byte[]> Bytes(string path)
    { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var output = new MemoryStream(); await stream.CopyToAsync(output); return output.ToArray(); }
    private static async Task<byte[]> BackendBytes(SessionStorageBackend backend, string path)
    { await using var stream = await backend.OpenReadAsync(path, CancellationToken.None); using var output = new MemoryStream(); await stream.CopyToAsync(output); return output.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Prefix(byte[] prefix, byte[] bytes) => Check(bytes.AsSpan().StartsWith(prefix), "Append or navigation rewrote original physical bytes.");
    private static void SameBytes(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Owned file bytes changed unexpectedly.");
    private static void Same(JsonElement expected, JsonElement actual, string where)
    {
        bool EqualJson(JsonElement left, JsonElement right)
        {
            if (left.ValueKind != right.ValueKind) return false;
            if (left.ValueKind == JsonValueKind.Object)
            {
                var a = left.EnumerateObject().ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
                var b = right.EnumerateObject().ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
                return a.Count == b.Count && a.All(field => b.TryGetValue(field.Key, out var value) && EqualJson(field.Value, value));
            }
            return left.ValueKind switch
            {
                JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => EqualJson(pair.First, pair.Second)),
                JsonValueKind.String => left.GetString() == right.GetString(),
                _ => left.GetRawText() == right.GetRawText()
            };
        }
        Check(EqualJson(expected, actual), "Exact JSON structure differs at " + where);
    }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ: " + expected + " / " + actual);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Throws<T>(Func<Task> work) where T : Exception { try { await work().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static T ThrowsSync<T>(Action work) where T : Exception { try { work(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Drain(Task work) { try { await work.WaitAsync(Bound); } catch (Exception) when (work.IsCompleted) { } }
    private sealed class Author
    {
        private readonly string prefix = "edit-" + Guid.NewGuid().ToString("N") + "-";
        public int Ids, Clocks; public string? PlannedId; public long? PlannedTimestamp;
        public string Next() { Ids++; return PlannedId ?? prefix + Ids; }
        public long Clock() { Clocks++; return PlannedTimestamp ?? 1711929600000; }
    }
    private sealed class Script : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request);
            var final = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 7, [new TextContent("actual final")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            await Task.CompletedTask; yield return new TextStarted(0, new("")); yield return new TextEnded(0, "actual final"); yield return new StreamDone(final.StopReason, final);
        }
    }
    private sealed class HeldFactory : ISessionLogStorageFactory
    {
        public HeldStorage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class HeldStorage(ISessionLogStorage inner) : ISessionLogStorage
    {
        private Barrier? held;
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes); public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
        public Barrier Arm() => held = new();
        public async ValueTask BeforeCheckpointAsync() { await inner.BeforeCheckpointAsync(); var active = held; held = null; if (active is not null) { active.Entered.TrySetResult(); await active.Release.Task; } }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Barrier { public readonly TaskCompletionSource Entered = Gate(), Release = Gate(); }
    private sealed class Files : IDisposable
    {
        private const string PrefixName = "PiSharp-context-edit-"; private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public string Root { get; } public string A => FilePath("a.jsonl"); public string B => FilePath("b.jsonl"); public string Export => FilePath("export.jsonl");
        public Files() { Root = Path.GetFullPath(Path.Combine(parent, PrefixName + Guid.NewGuid().ToString("N"))); ValidateRoot(); Directory.CreateDirectory(Root); }
        private string FilePath(string name) { var path = Path.Combine(Root, name); Validate(path); return path; }
        private void ValidateRoot() { var name = Path.GetFileName(Root); Check(Path.IsPathFullyQualified(Root) && string.Equals(Path.GetDirectoryName(Root), parent, Comparison) && name.StartsWith(PrefixName, StringComparison.Ordinal) && Guid.TryParseExact(name[PrefixName.Length..], "N", out _), "Unowned cleanup root rejected."); }
        private void Validate(string path) => Check(Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), Comparison) && string.Equals(Path.GetDirectoryName(path), Root, Comparison), "Fixture path escaped owned root.");
        public void Dispose()
        {
            ValidateRoot(); if (!Directory.Exists(Root)) return; Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "Linked cleanup root rejected.");
            foreach (var path in Directory.EnumerateFileSystemEntries(Root)) { Validate(path); Check((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Unowned directory/link rejected."); File.Delete(path); }
            Directory.Delete(Root, recursive: false);
        }
    }
}
