using PiSharp.Durable;
using PiSharp.Durable.Chord;

// Authored for P2 (no upstream test file of their own): definition validation and the address and version checks of
// packages/durable/src/documents.ts, and defineEntry() of entries.ts. Upstream exercises them through the Session in
// session-definitions.test.ts and session-checkpoints-migrations.test.ts, which port with the Session (P3).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> DocumentsCases() =>
    [
        Case("documents.validates-definitions", () =>
        {
            Equal("Document notes version must be a positive integer",
                Throws<ArgumentException>(() => Documents.DefineSessionDoc("notes", 0, () => JsonObjectValue.Empty), "version 0").Message, "message");
            Throws<ArgumentException>(() => Documents.DefineConversationDoc("notes", 1, DocumentFork.AsOf, () => JsonObjectValue.Empty), "latest asOf");
            var rewindable = Documents.DefineRewindableConversationDoc("notes", 2, DocumentFork.AsOf, () => JsonObjectValue.Empty);
            Check(rewindable is ConversationDocToken, "a rewindable token is a conversation token");
            Equal(DocumentHistory.Rewindable, rewindable.Definition.History, "history");
            var family = Documents.DefineTaskDocFamily("cache", 1, seed => JsonObjectValue.From([KeyValuePair.Create("seed", seed)]));
            Check(family.Definition.Family, "family");
            Same("{\"seed\":7}", family.Definition.Initial(7), "family initial(seed)");
        }),
        Case("documents.resolves-addresses-by-scope-owner-and-key", () =>
        {
            var conversationFamily = Documents.DefineConversationDocFamily("cache", 1, DocumentFork.Current, _ => JsonObjectValue.Empty);
            var resolved = Documents.ResolveAddress(conversationFamily.Definition, 3, "member");
            Equal(new DocumentAddress("cache", DocumentScope.Conversation(new(3)), "member"), resolved.Address, "address");
            Equal("[\"cache\",\"conversation\",3,\"member\"]", resolved.Id, "address ID");
            var session = Documents.ResolveAddress(Documents.DefineSessionDoc("settings", 1, () => JsonObjectValue.Empty).Definition, null, null);
            Equal("[\"settings\",\"session\",null,null]", session.Id, "session address ID");
            Equal("Document cache requires a conversation ID",
                Throws<ArgumentException>(() => Documents.ResolveAddress(conversationFamily.Definition, null, "member"), "missing owner").Message, "message");
            var create = Documents.CreateRecord(conversationFamily.Definition, resolved.Address, new DocumentId(9));
            Equal("{\"id\":9,\"kind\":\"cache\",\"key\":\"member\",\"scope\":{\"kind\":\"conversation\",\"conversationId\":3},\"history\":\"latest\",\"fork\":\"current\"}",
                create.ToJson().ToJson(), "create record");
        }),
        Case("documents.checks-scope-and-version-and-migrates-in-memory", () =>
        {
            var record = new DocumentRecord
            {
                Id = new(9), Kind = "notes", Scope = DocumentScope.Conversation(ConversationId.Root), History = DocumentHistory.Latest,
                Fork = DocumentFork.Current, CreatedAt = new(1),
            };
            var noMigration = Documents.DefineConversationDoc("notes", 2, DocumentFork.Current, () => JsonObjectValue.Empty);
            Equal("Document 9 (notes) requires migration from version 1",
                Throws<InvalidOperationException>(() => Documents.MaterializeDocumentValue(noMigration.Definition, record, 1, JsonObjectValue.Empty), "older").Message, "message");
            Equal("Document 9 (notes) has newer version 3 than 2",
                Throws<InvalidOperationException>(() => Documents.MaterializeDocumentValue(noMigration.Definition, record, 3, JsonObjectValue.Empty), "newer").Message, "message");
            var initialFork = Documents.DefineConversationDoc("notes", 1, DocumentFork.Initial, () => JsonObjectValue.Empty);
            Equal("Document 9 (notes) does not match the supplied definition semantics",
                Throws<ArgumentException>(() => Documents.MaterializeDocumentValue(initialFork.Definition, record, 1, JsonObjectValue.Empty), "fork").Message, "message");
            var migrating = Documents.DefineConversationDoc("notes", 2, DocumentFork.Current, () => JsonObjectValue.Empty,
                migrate: (value, fromVersion) => JsonObjectValue.From([KeyValuePair.Create("from", (JsonValue)fromVersion)]));
            Same("{\"from\":1}", Documents.MaterializeDocumentValue(migrating.Definition, record, 1, JsonObjectValue.Empty), "migrated");
            var current = (JsonObjectValue)J("{\"kept\":true}");
            Check(ReferenceEquals(current, Documents.MaterializeDocumentValue(migrating.Definition, record, 2, current)), "a current version is used as stored");
        }),
        Case("entries.defines-typed-entry-kinds", () =>
        {
            Equal("Entry kind must be a non-empty string", Throws<ArgumentException>(() => Entries.DefineEntry(""), "empty kind").Message, "message");
            var entry = new EntryRecord { Id = new(2), ConversationId = ConversationId.Root, Kind = "pi.user" };
            Check(Entries.UserEntry.Is(entry), "user entry");
            Check(!Entries.AssistantEntry.Is(entry), "not an assistant entry");
            Check(!Entries.UserEntry.Is(null), "absent entry");
            Equal("pi.tool-result,pi.compaction,pi.reset,pi.system", string.Join(",",
                Entries.ToolResultEntry.Kind, Entries.CompactionEntry.Kind, Entries.ResetEntry.Kind, Entries.SystemEntry.Kind), "built-in kinds");
        }),
    ];
}
