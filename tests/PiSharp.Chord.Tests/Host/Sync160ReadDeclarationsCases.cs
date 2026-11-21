using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/read-declarations.test.ts.
//
// Tests of OpenReadersAsync (slice 14 §3.2), the step a parked attempt or an admission opens its readers of named queries with: ready once
// each has a value; parked on the first that has none, saying why (the cause a reload's report waits for); broken for the reader's own
// code. The declarations' checks and builders are tested through live queries and lists (Sync160QueryReaderCases.cs,
// Sync160NamedQueryCases.cs, Sync160ListCases.cs). Upstream fakes a reader with an object literal; here a reader is an IQueryReader over a
// framed source nobody feeds.
internal static partial class Program
{
    /// <summary>A reader that opens as it is told, and its source (whose dependents the step follows).</summary>
    private sealed class DeclaredReader(Opened opened) : IQueryReader
    {
        public FramedSource Source { get; } = new IdleSource();

        public Task<Opened> OpenAsync(ChordContext opening) => Task.FromResult(opened);

        private sealed class IdleSource() : FramedSource(Sync160Batch(), new IdleOwner());

        private sealed class IdleOwner : IFramedOwner
        {
            public string Label => "app.doc";

            public void Report(Exception error)
            {
            }
        }
    }

    private sealed class DeclaredWake : IDependent
    {
        public void Invalidate()
        {
        }

        public void SourceRetired(ReferenceSet<IDependent> visited)
        {
        }
    }

    private sealed class DeclaredBlockers : IBlocked
    {
        internal readonly List<Blocker?> Named = [];

        public void BlockedBy(Blocker? blocker) => Named.Add(blocker);
    }

    /// <summary>Opens the readers with a wake and a record of the blockers named.</summary>
    private static async Task<(Parking Parking, DeclaredWake Wake, DeclaredBlockers Blockers)> DeclaredOpening(params DeclaredReader[] readers)
    {
        var wake = new DeclaredWake();
        var blockers = new DeclaredBlockers();
        return (await ReadDeclarations.OpenReadersAsync(readers, wake, blockers, "app.doc: a live query", ChordContext.Background), wake, blockers);
    }

    private static IEnumerable<(string, Func<Task>)> HostSync160ReadDeclarationsCases() =>
    [
        Case("host.read-declarations.opening-readers-is-ready-once-every-reader-has-a-value-and-follows-none", async () =>
        {
            var (a, b) = (new DeclaredReader(Opened.IsReady), new DeclaredReader(Opened.IsReady));
            var (parking, _, _) = await DeclaredOpening(a, b);
            Check(parking is Parking.Ready, "ready");
            Equal((0, 0), (a.Source.Dependents.Count, b.Source.Dependents.Count), "no reader is followed");
        }),
        Case("host.read-declarations.opening-readers-parks-waiting-on-a-read-that-failed-with-the-cause-a-reloads-report-names-and-follows-it", async () =>
        {
            var failed = new DeclaredReader(new Opened.Waiting(new InvalidOperationException("no such set")));
            var later = new DeclaredReader(Opened.IsReady);
            var (parking, wake, blockers) = await DeclaredOpening(failed, later);
            var parked = parking as Parking.Parked ?? throw new InvalidOperationException("expected parked, got " + parking);
            Check(parked.FirstPass, "its first pass is over");
            Equal("failed", parked.Shown.Kind, "it shows failed");
            Equal(FailureKind.Waiting, parked.Shown.Cause!.Kind, "waiting");
            Equal(FailureKind.Waiting, parked.Cause!.Kind, "the cause is a waiting one");
            Equal("waiting for app.doc: a live query: app.doc: a live query: no such set", parked.Cause.Error.Message, "the cause a reload's report names");
            Check(failed.Source.Dependents.Contains(wake), "the failed reader is followed");
            Equal(0, later.Source.Dependents.Count, "a reader behind it is not opened");
            Equal(new Blocker("app.doc: a live query", "failed"), blockers.Named[^1], "the blocker named last");
        }),
        Case("host.read-declarations.opening-readers-parks-loading-on-a-read-that-has-not-run-yet-with-a-cause-too-and-holds-up-the-first-pass", async () =>
        {
            var pending = new DeclaredReader(Opened.IsLoading);
            var (parking, wake, _) = await DeclaredOpening(new DeclaredReader(Opened.IsReady), pending);
            var parked = parking as Parking.Parked ?? throw new InvalidOperationException("expected parked, got " + parking);
            Check(!parked.FirstPass, "the first pass is held up");
            Equal("loading", parked.Shown.Kind, "it shows loading");
            Equal(FailureKind.Waiting, parked.Cause!.Kind, "a cause too");
            Equal("waiting for app.doc: a live query: app.doc: a live query is loading", parked.Cause.Error.Message, "the cause");
            Check(pending.Source.Dependents.Contains(wake), "the pending reader is followed");
        }),
        Case("host.read-declarations.opening-readers-breaks-on-a-readers-own-code", async () =>
        {
            var (parking, _, _) = await DeclaredOpening(new DeclaredReader(new Opened.Broken(new InvalidOperationException("bad arguments"))));
            var broken = parking as Parking.Broken ?? throw new InvalidOperationException("expected broken, got " + parking);
            Equal("app.doc: a live query: bad arguments", broken.Error.Message, "the reader's own code");
        }),
    ];
}
