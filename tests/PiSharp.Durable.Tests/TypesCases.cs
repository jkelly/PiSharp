using System.Reflection;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/types.test.ts (its runtime parts). Upstream's @ts-expect-error
// lines are compile-time rejections of invalid record shapes; here each record kind is its own struct or closed type, and the shapes the
// TypeScript union forbids are rejected where persisted JSON is decoded (FromJson) and by the records' Validate(). The third upstream test
// ("types submissions, task waits, compaction, and tasks erased into extensions") types the Harness surface and joins with P6-P8.
// types.record-json-key-order is authored: it pins the key orders this port writes, which are upstream's (see Types.cs; compat.* checks them
// against sessions upstream wrote).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> TypesCases() =>
    [
        Case("types.brands-numeric-ids-by-record-kind-and-carries-task-result-types", () =>
        {
            Check(Ids.FromNumber<ConversationId>(1).ToJson() is JsonNumberValue, "an ID is a JSON number");
            Equal("4", Ids.FromNumber<TaskId>(4).ToJson().ToJson(), "JSON.stringify(taskId)");
            Equal(ConversationId.Root, Ids.RootConversationId, "root conversation");
            Equal(1L, ConversationId.Root.Value, "root conversation ID");
            // The compile-time failures: distinct kinds do not convert into each other, and a sequence is not an ID.
            Type[] kinds = [typeof(ConversationId), typeof(EntryId), typeof(TaskId), typeof(SubmissionId), typeof(DocumentId), typeof(Seq)];
            foreach (var kind in kinds)
                Check(kind.GetMethods(BindingFlags.Public | BindingFlags.Static).All(method => method.Name is not ("op_Implicit" or "op_Explicit")),
                    kind.Name + " converts to another kind");
            Check(!typeof(IDurableId).IsAssignableFrom(typeof(Seq)), "a commit sequence is not an entity ID");
        }),
        Case("types.encodes-discriminator-dependent-fields", () =>
        {
            // Valid shapes round-trip.
            foreach (var (json, decode) in new (string, Func<JsonValue, object>)[]
            {
                ("{\"target\":2,\"action\":\"omit\"}", value => ContextEdit.FromJson(value)),
                ("{\"target\":2,\"action\":\"replace\",\"messages\":[]}", value => ContextEdit.FromJson(value)),
                ("{\"status\":\"pending\",\"checkpoint\":{\"phase\":\"ready\"}}", value => TaskState.FromJson(value)),
                ("{\"status\":\"terminal\",\"outcome\":{\"status\":\"completed\",\"result\":{\"value\":1}}}", value => TaskState.FromJson(value)),
                ("{\"id\":5,\"conversationId\":1,\"type\":\"input\",\"status\":\"done\",\"entry\":2,\"answer\":3}", value => SubmissionRecord.FromJson(value)),
                ("{\"id\":5,\"conversationId\":1,\"type\":\"write\",\"status\":\"done\",\"entry\":2}", value => SubmissionRecord.FromJson(value)),
                ("{\"conversationId\":1,\"type\":\"write\",\"status\":\"queued\"}", value => SubmissionCreate.FromJson(value)),
                ("{\"version\":1,\"kind\":\"base\",\"value\":{\"count\":1}}", value => DocumentContent.FromJson(value)),
                ("{\"version\":1,\"kind\":\"delta\",\"ops\":[[\"s\",[\"count\"],2]]}", value => DocumentContent.FromJson(value)),
                ("{\"id\":6,\"kind\":\"test\",\"scope\":{\"kind\":\"conversation\",\"conversationId\":1},\"history\":\"rewindable\",\"fork\":\"asOf\"}",
                    value => DocumentCreate.FromJson(value)),
            })
            {
                var decoded = decode(J(json));
                var encoded = decoded switch
                {
                    DurableValue durable => durable.ToJson(),
                    SubmissionRecord submission => submission.ToJson(),
                    SubmissionCreate create => create.ToJson(),
                    DocumentCreate document => document.ToJson(),
                    _ => throw new InvalidOperationException("Unexpected " + decoded.GetType().Name),
                };
                Equal(json, encoded.ToJson(), "round trip of " + json);
            }

            // Upstream's @ts-expect-error shapes are rejected.
            const string terminal = "\"state\":{\"status\":\"terminal\",\"outcome\":{\"status\":\"completed\",\"result\":1}}";
            foreach (var (what, json, decode) in new (string, string, Func<JsonValue, object>)[]
            {
                ("replacement edits require replacement messages", "{\"target\":2,\"action\":\"replace\"}", value => ContextEdit.FromJson(value)),
                ("omission edits cannot carry replacement messages", "{\"target\":2,\"action\":\"omit\",\"messages\":[]}", value => ContextEdit.FromJson(value)),
                ("live task state cannot carry a terminal outcome",
                    "{\"status\":\"pending\",\"checkpoint\":{\"phase\":\"ready\"},\"outcome\":{\"status\":\"completed\",\"result\":1}}", value => TaskState.FromJson(value)),
                ("terminal task state cannot retain a live checkpoint",
                    "{\"status\":\"terminal\",\"checkpoint\":{\"phase\":\"ready\"},\"outcome\":{\"status\":\"completed\",\"result\":1}}", value => TaskState.FromJson(value)),
                ("terminal task records cannot retain live memos",
                    "{\"id\":4,\"conversationId\":1,\"kind\":\"test\",\"version\":1,\"input\":null,\"background\":false,\"abortRequested\":false," + terminal +
                    ",\"memos\":{\"retained\":true}}", value => TaskRecord.FromJson(value)),
                ("session documents do not declare conversation history behavior",
                    "{\"id\":6,\"kind\":\"test\",\"scope\":{\"kind\":\"session\"},\"history\":\"latest\",\"fork\":\"current\",\"createdAt\":1}", value => DocumentRecord.FromJson(value)),
                ("conversation document creation requires history and fork policies",
                    "{\"id\":6,\"kind\":\"test\",\"scope\":{\"kind\":\"conversation\",\"conversationId\":1}}", value => DocumentCreate.FromJson(value)),
                ("task document creation cannot declare conversation policies",
                    "{\"id\":6,\"kind\":\"test\",\"scope\":{\"kind\":\"task\",\"taskId\":4},\"history\":\"latest\",\"fork\":\"initial\"}", value => DocumentCreate.FromJson(value)),
                ("storage, not the create command, supplies createdAt",
                    "{\"id\":6,\"kind\":\"test\",\"scope\":{\"kind\":\"session\"},\"createdAt\":1}", value => DocumentCreate.FromJson(value)),
                ("document bases cannot carry operation batches", "{\"kind\":\"base\",\"version\":1,\"value\":{},\"ops\":[]}", value => DocumentContent.FromJson(value)),
                ("document deltas cannot carry materialized values", "{\"kind\":\"delta\",\"version\":1,\"ops\":[],\"value\":{}}", value => DocumentContent.FromJson(value)),
                ("document creation always starts from a complete base",
                    "{\"type\":\"document.create\",\"record\":{\"id\":6,\"kind\":\"test\",\"scope\":{\"kind\":\"session\"}},\"content\":{\"kind\":\"delta\",\"version\":1,\"ops\":[]}}",
                    value => StorageWrite.FromJson(value)),
                ("completed outcomes cannot carry errors", "{\"status\":\"completed\",\"result\":1,\"error\":{\"message\":\"impossible\"}}", value => TaskOutcome.FromJson(value)),
                ("queued submissions cannot reference transcript entries",
                    "{\"id\":5,\"conversationId\":1,\"type\":\"input\",\"status\":\"queued\",\"entry\":2}", value => SubmissionRecord.FromJson(value)),
                ("successful input submissions require an answer entry",
                    "{\"id\":5,\"conversationId\":1,\"type\":\"input\",\"status\":\"done\",\"entry\":2}", value => SubmissionRecord.FromJson(value)),
                ("passive write submissions never carry an answer",
                    "{\"id\":5,\"conversationId\":1,\"type\":\"write\",\"status\":\"done\",\"entry\":2,\"answer\":3}", value => SubmissionRecord.FromJson(value)),
                ("Session, not the submission create value, assigns its ID",
                    "{\"id\":5,\"conversationId\":1,\"type\":\"write\",\"status\":\"queued\"}", value => SubmissionCreate.FromJson(value)),
            })
                Throws<FormatException>(() => decode(J(json)), what);

            // The same rules hold for values built in code.
            Throws<ArgumentNullException>(() => ContextEdit.Replace(new EntryId(2), null!), "replacement without messages");
            Throws<FormatException>(() => new SubmissionRecord
            {
                Id = new(5), ConversationId = ConversationId.Root, Type = SubmissionType.Write, Status = SubmissionStatus.Placed, Entry = new(2),
            }.Validate(), "placed write");
            Throws<FormatException>(() => new TaskRecord
            {
                Id = new(4), ConversationId = ConversationId.Root, Kind = "test", Version = 1, Input = JsonValue.Null, Background = false,
                AbortRequested = false, State = new TerminalTaskState(new CompletedTaskOutcome(1)), Memos = JsonObjectValue.Empty,
            }.Validate(), "terminal record with memos");
            Throws<FormatException>(() => new DocumentCreate
            {
                Id = new(6), Kind = "test", Scope = DocumentScope.Conversation(ConversationId.Root), History = DocumentHistory.Latest, Fork = DocumentFork.AsOf,
            }.Validate(), "latest document forking asOf");
        }),
        // Authored for Pi v1.2.0 (durable work list area c): a task record's optional abortReason and abandonOnRestart are written only when set,
        // in upstream's places (Transaction creates abandonOnRestart before state; a mark appends abortReason), and decode strictly.
        Case("types.task-record-abort-reason-and-abandon-on-restart", () =>
        {
            const string head = "{\"id\":4,\"conversationId\":1,\"kind\":\"test\",\"version\":1,\"input\":null,\"background\":false,\"abortRequested\":";
            const string pending = "\"state\":{\"status\":\"pending\",\"checkpoint\":{\"phase\":\"ready\"}}";
            foreach (var json in new[]
            {
                head + "false," + pending + "}",
                head + "false,\"abandonOnRestart\":true," + pending + "}",
                head + "true,\"abandonOnRestart\":true," + pending + ",\"startedAt\":5,\"abortReason\":\"restart\"}",
            })
                Equal(json, TaskRecord.FromJson(J(json)).ToJson().ToJson(), "round trip of " + json);
            var created = new TaskRecord
            {
                Id = new(4), ConversationId = ConversationId.Root, Kind = "test", Version = 1, Input = JsonValue.Null, Background = false,
                AbortRequested = false, AbandonOnRestart = true, State = new PendingTaskState(J("{\"phase\":\"ready\"}")),
            };
            Equal(head + "false,\"abandonOnRestart\":true," + pending + "}", created.ToJson().ToJson(), "created with abandonOnRestart");
            Equal(TaskAbortReason.Restart, TaskRecord.FromJson(J(head + "true," + pending + ",\"abortReason\":\"restart\"}")).AbortReason, "reason");
            Throws<FormatException>(() => TaskRecord.FromJson(J(head + "false,\"abandonOnRestart\":false," + pending + "}")), "abandonOnRestart false");
            Throws<FormatException>(() => TaskRecord.FromJson(J(head + "true," + pending + ",\"abortReason\":\"user\"}")), "unknown abort reason");
        }),
        Case("types.record-json-key-order", () =>
        {
            Equal("{\"id\":3,\"parent\":{\"conversationId\":1,\"at\":2},\"owner\":{\"conversationId\":1,\"taskId\":4}}",
                new ConversationRecord(new(3), new(ConversationId.Root, new(2)), new(ConversationId.Root, new(4))).ToJson().ToJson(), "conversation");
            Equal("{\"kind\":\"pi.user\",\"model\":[],\"data\":null,\"edits\":[{\"target\":1,\"action\":\"omit\"}],\"id\":2,\"conversationId\":1,\"head\":2,\"byTaskId\":4}",
                new EntryRecord
                {
                    Id = new(2), ConversationId = ConversationId.Root, Kind = "pi.user", Model = JsonArrayValue.Empty, Data = JsonValue.Null, Head = new(2),
                    Edits = [ContextEdit.Omit(new(1))], ByTaskId = new(4),
                }.ToJson().ToJson(), "entry");
            Equal("{\"conversationId\":1,\"requestId\":\"r\",\"type\":\"input\",\"status\":\"unanswered\",\"entry\":2,\"reason\":\"closed\",\"detail\":{},\"id\":5}",
                new SubmissionRecord
                {
                    Id = new(5), ConversationId = ConversationId.Root, RequestId = "r", Type = SubmissionType.Input, Status = SubmissionStatus.Unanswered,
                    Entry = new(2), Reason = "closed", Detail = JsonObjectValue.Empty,
                }.Validate().ToJson().ToJson(), "submission");
            Equal("{\"id\":4,\"conversationId\":1,\"kind\":\"k\",\"version\":2,\"input\":{},\"owner\":3,\"background\":false,\"abortRequested\":true," +
                "\"state\":{\"status\":\"waiting\",\"checkpoint\":{\"phase\":\"p\"},\"on\":[3],\"policy\":\"failFast\"},\"startedAt\":10,\"memos\":{},\"endedAt\":0.5}",
                new TaskRecord
                {
                    Id = new(4), ConversationId = ConversationId.Root, Kind = "k", Version = 2, Input = JsonObjectValue.Empty, Owner = new(3), Background = false,
                    AbortRequested = true, State = new WaitingTaskState(J("{\"phase\":\"p\"}"), [new TaskId(3)], JoinPolicy.FailFast),
                    Memos = JsonObjectValue.Empty, StartedAt = 10, EndedAt = 0.5,
                }.ToJson().ToJson(), "task");
            Equal("{\"status\":\"failed\",\"error\":{\"message\":\"m\",\"detail\":1},\"result\":2}",
                new FailedTaskOutcome(new TaskOutcomeError("m", 1), 2).ToJson().ToJson(), "failed outcome");
            // Decoded records keep their own key order; a spread-like change appends a new key, as upstream's { ...current, entry } does.
            foreach (var json in new[]
            {
                "{\"model\":[],\"data\":{},\"kind\":\"pi.tool-result\",\"id\":2,\"conversationId\":1,\"byTaskId\":4}",
                "{\"kind\":\"pi.compaction\",\"model\":[],\"data\":{},\"id\":2,\"conversationId\":1,\"head\":1,\"byTaskId\":4}",
            })
                Equal(json, EntryRecord.FromJson(J(json)).ToJson().ToJson(), "decoded entry order");
            var queued = new SubmissionCreate { ConversationId = ConversationId.Root, Type = SubmissionType.Input, Status = SubmissionStatus.Queued }.WithId(new(5));
            var placed = queued with { Status = SubmissionStatus.Placed, Entry = new(6) };
            Equal("{\"conversationId\":1,\"type\":\"input\",\"status\":\"placed\",\"id\":5,\"entry\":6}", placed.ToJson().ToJson(), "placed after queued");
            Equal("{\"conversationId\":1,\"type\":\"input\",\"status\":\"done\",\"id\":5,\"entry\":6,\"answer\":7}",
                (placed with { Status = SubmissionStatus.Done, Answer = new(7) }).ToJson().ToJson(), "answered after queued");
            Equal(placed.ToJson().ToJson(), SubmissionRecord.FromJson(placed.ToJson()).ToJson().ToJson(), "decoded submission order");
            Equal("{\"id\":6,\"kind\":\"k\",\"key\":\"f\",\"scope\":{\"kind\":\"conversation\",\"conversationId\":1},\"history\":\"latest\",\"fork\":\"initial\",\"createdAt\":7,\"retiredAt\":8}",
                new DocumentRecord
                {
                    Id = new(6), Kind = "k", Key = "f", Scope = DocumentScope.Conversation(ConversationId.Root), History = DocumentHistory.Latest,
                    Fork = DocumentFork.Initial, CreatedAt = new(7), RetiredAt = new(8),
                }.ToJson().ToJson(), "document record");
            Equal("{\"type\":\"document.copy\",\"record\":{\"id\":6,\"kind\":\"k\",\"scope\":{\"kind\":\"task\",\"taskId\":4}},\"source\":{\"id\":5,\"at\":\"current\"}}",
                new DocumentCopyWrite(new() { Id = new(6), Kind = "k", Scope = DocumentScope.Task(new(4)) }, new(new(5), DocumentPoint.Current)).ToJson().ToJson(), "copy write");
            foreach (var json in new[]
            {
                "{\"type\":\"task\",\"value\":{\"id\":4,\"conversationId\":1,\"kind\":\"k\",\"version\":1,\"input\":null,\"background\":true,\"abortRequested\":false,\"state\":{\"status\":\"completing\",\"outcome\":{\"status\":\"orphaned\",\"reason\":\"gone\"}}}}",
                "{\"type\":\"document.change\",\"id\":6,\"content\":{\"version\":3,\"kind\":\"delta\",\"ops\":[[\"a\",[\"text\"],\"x\"]]}}",
                "{\"type\":\"document.retire\",\"id\":6}",
            })
                Equal(json, StorageWrite.FromJson(J(json)).ToJson().ToJson(), "write round trip");
        }),
    ];
}
