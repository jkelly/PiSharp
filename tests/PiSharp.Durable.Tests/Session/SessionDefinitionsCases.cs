// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/session-definitions.test.ts. The two type-level cases
// (expectTypeOf and @ts-expect-error) are compile-time checks here: TypeChecks below must compile, and each @ts-expect-error line is kept
// as a comment naming the overload that does not exist. Their runtime parts (the snapshot calls of the first type case) run as upstream's
// do. "version 1.5" cannot be written: versions are int.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using static Program;

internal static partial class SessionTests
{
    private static readonly SessionDocToken DefinitionsSessionDoc = Documents.DefineSessionDoc("t.session", 1, () => O("{\"value\":0}"));
    private static readonly ConversationDocToken DefinitionsLatestDoc =
        Documents.DefineConversationDoc("t.latest", 1, DocumentFork.Current, () => O("{\"value\":0}"));
    private static readonly RewindableConversationDocToken DefinitionsRewindableDoc =
        Documents.DefineRewindableConversationDoc("t.rewindable", 1, DocumentFork.AsOf, () => O("{\"value\":0}"));
    private static readonly TaskDocToken DefinitionsTaskDoc = Documents.DefineTaskDoc("t.task", 1, () => O("{\"value\":0}"));
    private static readonly SessionDocFamilyToken DefinitionsSessionFamily =
        Documents.DefineSessionDocFamily("t.session-family", 1, seed => Obj(("value", seed)));
    private static readonly RewindableConversationDocFamilyToken DefinitionsConversationFamily =
        Documents.DefineRewindableConversationDocFamily("t.conversation-family", 1, DocumentFork.Initial, seed => Obj(("value", seed)));
    private static readonly TaskDocFamilyToken DefinitionsTaskFamily = Documents.DefineTaskDocFamily("t.task-family", 1, seed => Obj(("value", seed)));

    private static IEnumerable<(string, Func<Task>)> Definitions(string prefix, Func<IStorage> storageFactory)
    {
        yield return Case(prefix + "validates-persisted-version-semantics", () =>
        {
            Rejects(Program.Throws<ArgumentException>(() => Documents.DefineSessionDoc("k", 0, () => O("{\"value\":0}")), "version 0"), "positive integer", "version 0");
            // Upstream: version 1.5 also rejects; a C# version is an int.
            Equal("", Documents.DefineSessionDoc("", 1, () => O("{\"value\":0}")).Definition.Kind, "empty kind");
            Equal("t.session", DefinitionsSessionDoc.Definition.Kind, "kind");
        });

        yield return Case(prefix + "types-every-owner-key-and-seed-overload", async () =>
        {
            ISession session = Session.Create(storageFactory());
            Func<ITx, ConversationId, TaskId, Task> check = TypeChecks;
            Check(check is not null, "the type checks compile");

            Check(await session.SnapshotAsync(DefinitionsSessionDoc, Context) is null, "session");
            var taskId = Ids.FromNumber<TaskId>(1);
            Check(await session.SnapshotAsync(DefinitionsLatestDoc, ConversationId.Root, Context) is null, "conversation");
            Check(await session.SnapshotAsync(DefinitionsTaskDoc, taskId, Context) is null, "task");
            Check(await session.SnapshotAsync(DefinitionsSessionFamily, "k", Context) is null, "session family");
            Check(await session.SnapshotAsync(DefinitionsConversationFamily, ConversationId.Root, "k", Context) is null, "conversation family");
            Check(await session.SnapshotAsync(DefinitionsTaskFamily, taskId, "k", Context) is null, "task family");
            // @ts-expect-error snapshots never take a creation seed: there is no SnapshotAsync(SessionDocFamilyToken, string, JsonValue, ...).
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "types-historical-reads-states-watches-and-typed-entries", () =>
        {
            Func<ISession, ITx, ConversationId, TaskId, EntryId, Task> check = ObservationTypeChecks;
            Check(check is not null, "the type checks compile");
        });

        // Adapted from the typed-entry half of the case above: the typed overloads also run.
        yield return Case(prefix + "appends-and-reads-typed-entries", async () =>
        {
            var note = Entries.DefineEntry<JsonObjectValue>("t.note");
            var marker = Entries.DefineEntry("t.marker");
            var (_, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var (typed, plain) = await session.CommitAsync(async tx =>
            {
                var typedNote = await tx.AppendEntryAsync(note, conversationId, new TypedEntryDraft<JsonObjectValue> { Data = O("{\"text\":\"x\"}") });
                var markerEntry = await tx.AppendEntryAsync(marker, conversationId, new TypedEntryDraft());
                return (typedNote, markerEntry);
            }, Context);
            Equal("x", S(typed.Data["text"]), "typed data");
            Equal("t.marker", plain.Kind, "kind from the token");
            Check(plain.Data is null, "a data-less kind carries no data");
            await session.CommitAsync(async tx =>
            {
                Equal("x", S((await tx.GetEntryAsync(note, typed.Id))!.Data["text"]), "typed read");
                Check(await tx.GetEntryAsync(note, plain.Id) is null, "another kind reads as absent");
                Check(await tx.GetEntryAsync(marker, plain.Id) is not null, "a data-less kind reads as a record");
            }, Context);
        });
    }

    // expectTypeOf(...).toEqualTypeOf<Draft<State>>(): every overload returns Task<DraftObject>.
    private static async Task TypeChecks(ITx tx, ConversationId conversationId, TaskId taskId)
    {
        DraftObject draft = await tx.DocAsync(DefinitionsSessionDoc);
        draft = await tx.DocAsync(DefinitionsLatestDoc, conversationId);
        draft = await tx.DocAsync(DefinitionsRewindableDoc, conversationId);
        draft = await tx.DocAsync(DefinitionsTaskDoc, taskId);
        draft = await tx.DocAsync(DefinitionsSessionFamily, "k", 1);
        draft = await tx.DocAsync(DefinitionsConversationFamily, conversationId, "k", 1);
        draft = await tx.DocAsync(DefinitionsTaskFamily, taskId, "k", 1);
        _ = draft;
        await tx.RetireDocAsync(DefinitionsSessionDoc);
        await tx.RetireDocAsync(DefinitionsLatestDoc, conversationId);
        await tx.RetireDocAsync(DefinitionsTaskDoc, taskId);
        await tx.RetireDocAsync(DefinitionsSessionFamily, "k");
        await tx.RetireDocAsync(DefinitionsConversationFamily, conversationId, "k");
        await tx.RetireDocAsync(DefinitionsTaskFamily, taskId, "k");
        // @ts-expect-error Session documents take no owner:            tx.DocAsync(DefinitionsSessionDoc, conversationId)
        // @ts-expect-error conversation documents require an ID:       tx.DocAsync(DefinitionsLatestDoc)
        // @ts-expect-error family access requires a creation seed:     tx.DocAsync(DefinitionsSessionFamily, "k")
        // @ts-expect-error family seeds are typed:                     (seeds are JsonValue; a non-JSON seed does not convert)
        // @ts-expect-error family retirement takes no seed:            tx.RetireDocAsync(DefinitionsTaskFamily, taskId, "k", 1)
    }

    private static async Task ObservationTypeChecks(ISession session, ITx tx, ConversationId conversationId, TaskId taskId, EntryId at)
    {
        Task<JsonObjectValue?> rewound = session.SnapshotAsOfAsync(DefinitionsRewindableDoc, conversationId, at, Context);
        rewound = session.SnapshotAsOfAsync(DefinitionsConversationFamily, conversationId, "k", at, Context);
        _ = rewound;
        // @ts-expect-error latest conversation, Session and task documents keep no history: SnapshotAsOfAsync takes only rewindable tokens.

        Task<AttachedReplicatedState<JsonObjectValue?>?> state = session.DocumentStateAsync(DefinitionsSessionDoc, Context);
        state = session.DocumentStateAsync(DefinitionsLatestDoc, conversationId, Context);
        state = session.DocumentStateAsync(DefinitionsTaskDoc, taskId, Context);
        state = session.DocumentStateAsync(DefinitionsSessionFamily, "k", Context);
        state = session.DocumentStateAsync(DefinitionsConversationFamily, conversationId, "k", Context);
        state = session.DocumentStateAsync(DefinitionsTaskFamily, taskId, "k", Context);
        _ = state;
        Task<IWatchHandle<JsonObjectValue?>?> watch = session.WatchDocAsync(DefinitionsSessionDoc, Context);
        watch = session.WatchDocAsync(DefinitionsLatestDoc, conversationId, Context);
        watch = session.WatchDocAsync(DefinitionsTaskDoc, taskId, Context);
        watch = session.WatchDocAsync(DefinitionsSessionFamily, "k", Context);
        watch = session.WatchDocAsync(DefinitionsConversationFamily, conversationId, "k", Context);
        watch = session.WatchDocAsync(DefinitionsTaskFamily, taskId, "k", Context);
        _ = watch;
        // @ts-expect-error a watch of a Session document takes no owner: there is no WatchDocAsync(SessionDocToken, ConversationId, ...).

        var note = Entries.DefineEntry<JsonObjectValue>("t.note");
        var marker = Entries.DefineEntry("t.marker");
        EntryRecord? raw = await tx.GetEntryAsync(at);
        TypedEntry<JsonObjectValue>? typed = await tx.GetEntryAsync(note, at);
        EntryRecord appended = await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "t.raw" });
        TypedEntry<JsonObjectValue> written = await tx.AppendEntryAsync(note, conversationId, new TypedEntryDraft<JsonObjectValue> { Data = O("{\"text\":\"x\"}") });
        JsonObjectValue data = written.Data;
        EntryRecord markerEntry = await tx.AppendEntryAsync(marker, conversationId, new TypedEntryDraft());
        _ = (raw, typed, appended, data, markerEntry);
        // @ts-expect-error a data entry requires its data:             TypedEntryDraft<JsonObjectValue>.Data is required
        // @ts-expect-error data is typed by the token:                 Data must be a JsonObjectValue for Entry<JsonObjectValue>
        // @ts-expect-error the token supplies the kind:                typed drafts have no Kind
        // @ts-expect-error an entry kind without data takes none:      TypedEntryDraft has no Data
    }
}
