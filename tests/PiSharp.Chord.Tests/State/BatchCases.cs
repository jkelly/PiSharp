using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/state/batch.test.ts. Upstream's probe is a local type served
// by createHost (phase 6) whose `probe` derive runs the attempts; here the derive is the state layer's over the hand-built host's document
// (StateHost.cs), with a writable of the same host. The attempts are the state layer's own operations, each through its own check: a
// watch of a JSON source and of a native one, an observe, and a writable's set (refused as a change inside a compute). What only the host
// has waits for phase 6: release, get, load and close, a constant or ref.state watched through a reference, and a set through a method and
// a transaction. They all refuse through the batch's AssertOutside, so the cases also call that check under the name of one of them (get).
// The delivery case: a JsonNode is always JSON, so a derive's diff cannot throw as upstream's `{ a: undefined }` does; the delivery that
// throws here is a subscriber's (a hold's, which a document's delivery feeds), which reaches the batch the same way. The flushes case is
// upstream's own, on a Batch.
internal static partial class Program
{
    /// <summary>The state layer's operations a derive compute may not make, each with what it threw or returned (a task, awaited later).</summary>
    private static Dictionary<string, object?> StateAttempts(StateHost host, Derive self, WritableSource<double> own)
    {
        static object? Outcome(Func<object?> run)
        {
            try
            {
                return run();
            }
            catch (Exception error)
            {
                return error;
            }
        }
        return new()
        {
            ["watch"] = Outcome(() => self.WatchAsync(StateBg)),
            ["watch a writable"] = Outcome(() => own.WatchAsync(StateBg)),
            ["observe a writable"] = Outcome(() => own.Observe(new StateNativeObserver<double>(_ => { }), StateBg)),
            ["set a writable"] = Outcome(() =>
            {
                own.Set(5);
                return null;
            }),
            ["get (the host's check, phase 6)"] = Outcome(() =>
            {
                host.Batch.AssertOutside("get");
                return null;
            }),
        };
    }

    /// <summary>A host whose <c>probe</c> derive runs <paramref name="attempt"/> on every compute with n &gt; 0 and throws for n &gt; 1 (upstream
    /// <c>probe</c>); what each operation threw is recorded.</summary>
    private static (StateHost Host, StateDoc Num, Derive Probe, WritableSource<double> Own, List<Dictionary<string, object?>> Seen) StateProbe(
        Func<StateHost, Derive, WritableSource<double>, Dictionary<string, object?>> attempt)
    {
        var host = new StateHost();
        var num = host.Doc("""{"n":0}""");
        var own = host.Writable(0.0, new StateOwner("app.probe"));
        var seen = new List<Dictionary<string, object?>>();
        Derive? probe = null;
        probe = host.Derive([num], values =>
        {
            var n = StateN(values[0]);
            if (n > 0) seen.Add(attempt(host, probe!, own));
            if (n > 1) throw new InvalidOperationException("compute threw");
            return n;
        });
        return (host, num, probe, own, seen);
    }

    /// <summary>Expects every attempt refused as <c>invalid/reentrant</c> (a task by its rejection).</summary>
    private static async Task StateRefusedAll(Dictionary<string, object?> attempts)
    {
        Check(attempts.Count == 5, "every attempt ran");
        foreach (var (operation, result) in attempts)
        {
            var error = result is Task task ? await StateRejected(task) : result;
            Check(error is ChordError { Code: "invalid", Reason: "reentrant" }, $"{operation}: expected invalid/reentrant, actual {error}");
        }
    }

    private static async Task<object?> StateRejected(Task task)
    {
        try
        {
            await task;
            return task;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static IEnumerable<(string, Func<Task>)> StateBatchCases() =>
    [
        Case("state.batch.reentrancy-refuses-watch-observe-a-set-and-the-hosts-operations-in-a-commits-derive-computes", async () =>
        {
            var (_, num, probe, _, seen) = StateProbe(StateAttempts);
            await probe.WatchAsync(StateBg);
            StateSetN(num, 1);
            Equal(1, seen.Count, "one compute with n > 0");
            await StateRefusedAll(seen[0]);
        }),
        Case("state.batch.reentrancy-refuses-them-in-a-lazy-compute-outside-a-commit", async () =>
        {
            var (_, num, probe, _, seen) = StateProbe(StateAttempts);
            StateSetN(num, 1);
            Equal(0, seen.Count, "nothing computed in the commit");
            Json("1", probe.Value, "the read");
            await StateRefusedAll(seen[0]);
        }),
        Case("state.batch.reentrancy-clears-the-flag-after-a-throwing-compute", async () =>
        {
            var (host, num, probe, own, _) = StateProbe((_, _, _) => []);
            StateSetN(num, 2);
            Equal("compute threw", Throws<InvalidOperationException>(() => _ = probe.Value, "the read").Message, "the read's error");
            Check(!host.Batch.Running, "the flag is cleared");
            Accepts(() => host.Batch.AssertOutside("get"), "a host operation after it");
            await num.WatchAsync(StateBg);
            Accepts(() => own.Set(3), "a set after it");
            Accepts(() => StateSetN(num, 3), "a commit after it");
        }),
        Case("state.batch.delivery-reports-a-delivery-that-throws-and-still-delivers-the-later-frames-of-the-commit", async () =>
        {
            var host = new StateHost();
            var num = host.Doc("""{"n":0}""");
            var other = host.Doc("""{"n":0}""");
            var threw = new InvalidOperationException("delivery threw");
            num.Subscribe(new StateSubscriber(_ => throw threw));
            var frames = FramesOf(await other.WatchAsync(StateBg));
            // `num` is adopted first, so its delivery comes before `other`'s.
            host.Batch.Commit(() =>
            {
                num.Adopt(_ => Parse("""{"n":1}"""));
                other.Adopt(_ => Parse("""{"n":1}"""));
            }, StateBg);
            host.Delivered();
            Sequence([BatchErrorKind.DeliveryFailed], host.Errors.Select(error => error.Kind), "the reports");
            Check(ReferenceEquals(threw, host.Errors[0].Error), "the report's error");
            Json("""[{"n":1}]""", new JsonArray([.. frames.Select(frame => frame.Value?.DeepClone())]), "other's values");
        }),
        Case("state.batch.flushes-run-once-per-listing-at-the-end-of-the-commit-after-its-deliveries-a-throwing-one-is-reported", () =>
        {
            var reported = new List<BatchError>();
            var batch = new Batch(reported.Add, null);
            var order = new List<string>();
            batch.Commit(() =>
            {
                batch.Flushing(_ => order.Add("first"));
                batch.Flushing(_ => throw new InvalidOperationException("flush threw"));
                batch.Flushing(_ => order.Add("third"));
                order.Add("adopted");
            }, StateBg);
            Sequence(["adopted", "first", "third"], order, "the order");
            Sequence([BatchErrorKind.DeliveryFailed], reported.Select(error => error.Kind), "the reports");
            Equal("flush threw", reported[0].Error.Message, "the report's error");
            batch.Commit(() => { }, StateBg);
            Sequence(["adopted", "first", "third"], order, "the order after another commit");
        }),
    ];
}

/// <summary>A native observer that records the calls it is fed and its end.</summary>
internal sealed class StateNativeObserver<T>(Action<NativeWatchCall<T>> advance) : INativeObserver<T>
{
    public List<WatchEnd> Ends { get; } = [];

    public void Advance(NativeWatchCall<T> call, ChordContext context) => advance(call);

    public void End(WatchEnd end) => Ends.Add(end);
}
