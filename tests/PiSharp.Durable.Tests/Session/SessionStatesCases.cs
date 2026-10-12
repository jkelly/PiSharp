// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/session-states.test.ts. Verbatim apart from the adaptations in
// SessionSupport.cs: getReplicatedStateInternals(state).subscribe is ReplicatedStateInternals.Get<JsonObjectValue?>(state).SubscribeSource,
// "exposes trusted shared immutable values without freezing" checks reference sharing only (JsonValue trees are never frozen copies),
// and a reload after unloading is shown by the storage read, not a new object (an immutable stored value may be handed out again).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using static Program;

internal static partial class SessionTests
{
    private static IEnumerable<(string, Func<Task>)> States(string prefix, Func<IStorage> storageFactory)
    {
        var stateDoc = Documents.DefineSessionDoc("state.state", 1, () => O("{\"value\":0,\"retained\":{\"label\":\"stable\"}}"));
        var familyDoc = Documents.DefineSessionDocFamily("state.family", 1, seed => Obj(("value", S(seed).Length)));
        static double ValueOf(JsonObjectValue? value) => N(value!["value"]);
        static Task SetValue(Session session, SessionDocToken token, double value) =>
            session.CommitAsync(async tx => (await tx.DocAsync(token))["value"] = value, Context);

        async Task<TestSession> CreateState()
        {
            var harness = Open(storageFactory);
            await harness.Session.CommitAsync(tx => tx.DocAsync(stateDoc), Context);
            await Flush(harness.Session);
            return harness;
        }

        yield return Case(prefix + "never-creates-an-absent-document", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var commits = storage.CommitCount;
            Check(await session.DocumentStateAsync(stateDoc, Context) is null, "singleton");
            Check(await session.DocumentStateAsync(familyDoc, "missing", Context) is null, "family");
            Equal(commits, storage.CommitCount, "commits");
            Equal(0, storage.MintCount, "mints");
        });

        yield return Case(prefix + "returns-an-immediately-hydrated-read-only-state-with-contiguous-chord-deliveries", async () =>
        {
            var (_, session, _) = await CreateState();
            var baseline = (await session.SnapshotAsync(stateDoc, Context))!;
            var state = (await session.DocumentStateAsync(stateDoc, Context))!;
            var deliveries = new List<(double Value, long Sequence)>();
            state.Subscribe((value, _, delivery) => { lock (deliveries) deliveries.Add((ValueOf(value), delivery.Sequence)); });
            Same(baseline, state.Value, "hydrated value");

            await SetValue(session, stateDoc, 1);
            await SetValue(session, stateDoc, 2);
            await Flush(session);

            lock (deliveries)
            {
                Seq([0L, 1L, 2L], deliveries.Select(delivery => delivery.Sequence), "sequences");
                Seq([0.0, 1.0, 2.0], deliveries.Select(delivery => delivery.Value), "values");
            }
            Same(await session.SnapshotAsync(stateDoc, Context), state.Value, "current value");
            state.Dispose();
        });

        yield return Case(prefix + "creates-independent-disposable-states-for-one-incarnation", async () =>
        {
            var (_, session, _) = await CreateState();
            var first = (await session.DocumentStateAsync(stateDoc, Context))!;
            var second = (await session.DocumentStateAsync(stateDoc, Context))!;
            Check(!ReferenceEquals(first, second), "independent states");
            await SetValue(session, stateDoc, 1);
            await Flush(session);
            Equal(1.0, ValueOf(first.Value), "first");
            Equal(1.0, ValueOf(second.Value), "second");

            first.Dispose();
            await SetValue(session, stateDoc, 2);
            await Flush(session);
            Equal(1.0, ValueOf(first.Value), "disposed state keeps its value");
            Equal(2.0, ValueOf(second.Value), "second follows");
            second.Dispose();
        });

        yield return Case(prefix + "shares-exact-committed-value-and-operation-references-with-chord", async () =>
        {
            var (_, session, publications) = await CreateState();
            var state = (await session.DocumentStateAsync(stateDoc, Context))!;
            IReadOnlyList<Op>? receivedOps = null;
            ReplicatedStateInternals.Get<JsonObjectValue?>(state)!.SubscribeSource((ops, _, _) => receivedOps = ops);
            await SetValue(session, stateDoc, 4);
            await Flush(session);
            var published = DocumentChanges(Last(publications))[0];
            Same(published.Value, state.Value, "value");
            SameOps(published.Ops, receivedOps, "ops");
            Same((await session.SnapshotAsync(stateDoc, Context))!["retained"], state.Value!["retained"], "retained subtree");
            state.Dispose();
        });

        yield return Case(prefix + "captures-a-late-baseline-without-redelivering-an-already-covered-commit", async () =>
        {
            var (_, session, _) = await CreateState();
            await SetValue(session, stateDoc, 1);
            var state = (await session.DocumentStateAsync(stateDoc, Context))!;
            var deliveries = new List<long>();
            state.Subscribe((_, _, delivery) => { lock (deliveries) deliveries.Add(delivery.Sequence); });
            await Flush(session);
            Equal(1.0, ValueOf(state.Value), "value");
            lock (deliveries) Seq([0L], deliveries, "deliveries");
            state.Dispose();
        });

        yield return Case(prefix + "publishes-null-retirement-and-never-follows-a-replacement-incarnation", async () =>
        {
            var (_, session, _) = await CreateState();
            var oldState = (await session.DocumentStateAsync(stateDoc, Context))!;
            IReadOnlyList<Op>? retirementOps = null;
            ReplicatedStateInternals.Get<JsonObjectValue?>(oldState)!.SubscribeSource((ops, _, _) => retirementOps = ops);
            await session.CommitAsync(async tx =>
            {
                await tx.RetireDocAsync(stateDoc);
                (await tx.DocAsync(stateDoc))["value"] = 10;
            }, Context);
            await Flush(session);
            Check(oldState.Value is null, "retired to null");
            Equal("[[\"r\",null]]", OpsJson(retirementOps!), "retirement ops");

            var replacement = (await session.DocumentStateAsync(stateDoc, Context))!;
            Equal(10.0, ValueOf(replacement.Value), "replacement");
            await SetValue(session, stateDoc, 11);
            await Flush(session);
            Check(oldState.Value is null, "old state stays retired");
            Equal(11.0, ValueOf(replacement.Value), "replacement follows");
            oldState.Dispose();
            replacement.Dispose();
        });

        yield return Case(prefix + "cold-loads-a-definition-free-fork-copy", async () =>
        {
            var copied = Documents.DefineConversationDoc("state.copied", 1, DocumentFork.Current, () => O("{\"value\":0}"));
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            var entry = await session.CommitAsync(async tx =>
            {
                var created = await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" });
                (await tx.DocAsync(copied, parentId))["value"] = 7;
                return created;
            }, Context);
            var childId = await session.CommitAsync(async tx => (await tx.ForkConversationAsync(parentId, entry.Id, ConversationOwnership.Ownerless)).Id, Context);
            var reads = storage.DocumentReadCount;
            var state = (await session.DocumentStateAsync(copied, childId, Context))!;
            SameJson("{\"value\":7}", state.Value, "copied value");
            Check(storage.DocumentReadCount > reads, "cold-loaded");
            state.Dispose();
        });

        yield return Case(prefix + "hydrates-a-migrated-tracker-without-writing-and-skips-an-equal-version-base-update", async () =>
        {
            var old = Documents.DefineSessionDoc("state.migration", 1, () => O("{\"value\":3}"));
            var current = Documents.DefineSessionDoc("state.migration", 2, () => O("{\"value\":0,\"migrated\":false}"),
                migrate: (value, _) => Obj(("value", value["value"]), ("migrated", true)));
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;
            var state = (await session.DocumentStateAsync(current, Context))!;
            SameJson("{\"value\":3,\"migrated\":true}", state.Value, "migrated");
            Equal(commits, storage.CommitCount, "commits");
            var baseline = state.Value;

            await session.CommitAsync(tx => tx.DocAsync(current), Context);
            await Flush(session);
            Equal(commits + 1, storage.CommitCount, "version base written");
            Same(baseline, state.Value, "no update for an equal version base");
            await SetValue(session, current, 4);
            await Flush(session);
            SameJson("{\"value\":4,\"migrated\":true}", state.Value, "later edit");
            state.Dispose();
        });

        yield return Case(prefix + "continues-from-exact-committed-values-after-the-tracker-cache-unloads", async () =>
        {
            var (storage, session, _) = await CreateState();
            var state = (await session.DocumentStateAsync(stateDoc, Context))!;
            var baseline = state.Value;
            var reads = storage.DocumentReadCount;
            await session.UnloadDocumentsAsync();
            var reloaded = await session.SnapshotAsync(stateDoc, Context);
            Check(JsonValue.DeepEquals(reloaded, baseline), "equal");
            Check(storage.DocumentReadCount > reads, "read from storage");
            await SetValue(session, stateDoc, 6);
            await Flush(session);
            Equal(6.0, ValueOf(state.Value), "continues");
            state.Dispose();
        });

        yield return Case(prefix + "exposes-trusted-shared-immutable-values-without-freezing", async () =>
        {
            var (_, session, _) = await CreateState();
            var snapshot = (await session.SnapshotAsync(stateDoc, Context))!;
            var state = (await session.DocumentStateAsync(stateDoc, Context))!;
            Same(snapshot, state.Value, "shared value");
            // Upstream also checks Object.isFrozen is false; JsonValue trees are immutable by type, never frozen copies.
            state.Dispose();
        });
    }
}
