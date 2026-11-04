using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/state/native-source.test.ts. Upstream's native sources are a
// registry's value (`Count`, a sum that throws when negative: the registries area), a writable contributed to it and ref.state, all served
// by createHost (phase 6). Here they are the state layer's writables and two hand-built native sources: StateRegistry, a followed source
// composing its writables' values in a compute as a registry value does, and StateReach, a source shaped as ref.state (its value the
// reachability kind, changed in a commit). What only the host has waits for phase 6: the registry and its contributions, a reference's
// ref.state with failLive, the refusal of a native source as a state member (the host checks a type's members), and the `on` routine
// (here its follower, Subscribe, which Chord's own routines use). The type-level case (a native handle is no derive source) has no C#
// counterpart: a derive's sources are objects, and one that is no watchable of Chord's throws when it is read.
internal static partial class Program
{
    /// <summary>Upstream's <c>Count</c> compose: the sum, which throws when negative.</summary>
    private static double StateSum(IReadOnlyList<double> table)
    {
        var sum = table.Sum();
        if (sum < 0) throw new InvalidOperationException(FormattableString.Invariant($"negative: {sum}"));
        return sum;
    }

    private static IEnumerable<(string, Func<Task>)> StateNativeSourceCases() =>
    [
        Case("state.native-source.native-sources-as-derive-sources-a-derive-reads-json-and-native-sources-alike-marked-when-a-native-one-changes", () =>
        {
            var host = new StateHost();
            var setter = host.Writable(1.0, new StateOwner("app.setter"));
            var count = new StateRegistry<double>(host.Batch, StateSum, setter);
            var name = host.Writable("a", new StateOwner("app.mixed"));
            var label = host.Derive([name, count], values => FormattableString.Invariant($"{values[0]}:{values[1]}"));
            setter.Set(2);
            Json("\"a:2\"", label.Value, "the label");
            name.Set("b");
            Json("\"b:2\"", label.Value, "the label after a set");
        }),
        Case("state.native-source.native-sources-as-derive-sources-ref-state-is-a-derive-source-a-state-member-can-show-a-dependencys-reachability", async () =>
        {
            var host = new StateHost();
            var state = new StateReach(host.Batch);
            var flaky = host.Derive([state], values => (string)values[0]!);
            var kinds = new List<string>();
            (await flaky.WatchAsync(StateBg)).Start((call, _) =>
            {
                if (call is WatchValueCall<JsonNode?> value) kinds.Add(ChordJson.AsString(value.Value)!);
                return Task.CompletedTask;
            });
            Json("\"live\"", flaky.Value, "the derive");
            state.Fail(StateBg);
            Json("\"failed\"", flaky.Value, "the derive after the failure");
            host.Delivered();
            Sequence(["failed"], kinds, "the kinds");
        }),
        Case("state.native-source.native-sources-as-derive-sources-a-source-that-is-no-watchable-of-chords-is-refused-when-read-pisharp", () =>
        {
            // PiSharp: the type-level case's runtime counterpart; a native handle is no watchable of Chord's.
            var host = new StateHost();
            var derive = host.Derive([new object()], values => 1);
            Check(Throws<InvalidOperationException>(() => _ = derive.Value, "the read").Message.Contains("watchable", StringComparison.Ordinal),
                "the read names what a derive reads");
        }),
        Case("state.native-source.watching-a-native-source-that-fails-a-watch-taken-while-it-is-failed-opens-at-its-last-good-value-and-gets-the-failure-first", async () =>
        {
            var host = new StateHost();
            var setter = host.Writable(1.0, new StateOwner("app.setter"));
            var count = new StateRegistry<double>(host.Batch, StateSum, setter);
            // Composed once (upstream's registry has a value when the test sets -1).
            Equal(1.0, count.Value, "the first value");
            setter.Set(-1);
            var calls = new List<NativeWatchCall<double>>();
            var watch = await count.WatchAsync(StateBg);
            Equal(1.0, watch.Value, "the value the watch opens at");
            watch.Start((call, _) =>
            {
                calls.Add(call);
                return Task.CompletedTask;
            });
            host.Delivered();
            Equal(1, calls.Count, "one call");
            Check(calls[0] is NativeWatchFailedCall<double> { Error: InvalidOperationException { Message: "negative: -1" } }, $"the failure, actual {calls[0]}");
        }),
        Case("state.native-source.watching-a-native-source-that-fails-a-follower-started-while-its-source-is-failed-waits-for-its-next-good-value-never-calls-with-the-stale-one", () =>
        {
            var host = new StateHost();
            var setter = host.Writable(1.0, new StateOwner("app.setter"));
            var count = new StateRegistry<double>(host.Batch, StateSum, setter);
            Equal(1.0, count.Value, "the first value");
            setter.Set(-1);
            // Read: the failure is composed before the follower starts.
            Equal(1.0, count.Value, "the last good value");
            var seen = new List<double>();
            count.Subscribe(new StateNativeObserver<double>(call =>
            {
                if (call is NativeWatchValueCall<double> value) seen.Add(value.Value);
            }));
            host.Delivered();
            Equal(0, seen.Count, "nothing while it is failed");
            setter.Set(4);
            host.Delivered();
            Sequence([4.0], seen, "the next good value");
        }),
    ];
}

/// <summary>
/// A registry value standing in for the registries area's (phase 6's host serves them): it composes its parts' values (writables, its
/// contributions) lazily, in a compute as a registry's compose runs (Batch.Compute), and a change of a part marks it. A compose that throws
/// fails it: <see cref="Value"/> keeps the last good value for readers, a new watch opens there and gets the failure first, and
/// <see cref="Current"/> throws it.
/// </summary>
internal sealed class StateRegistry<T> : FollowedSource<T>, IDependent
{
    private readonly WritableSource<double>[] parts;
    private readonly Func<IReadOnlyList<double>, T> compose;
    private bool dirty = true;
    private T value = default!;
    private bool hasGood;
    private Exception? error;

    public StateRegistry(Batch batch, Func<IReadOnlyList<double>, T> compose, params WritableSource<double>[] parts)
        : base(batch)
    {
        this.parts = parts;
        this.compose = compose;
        foreach (var part in parts) part.Dependents.Add(this);
    }

    public override T Value
    {
        get
        {
            Compose();
            if (!hasGood) ExceptionDispatchInfo.Throw(error!);
            return value;
        }
    }

    public override NativeStart<T> Start()
    {
        Compose();
        return error is null ? NativeStart<T>.Of(value) : NativeStart<T>.Failure(error, hasGood, value);
    }

    public override T Current()
    {
        Compose();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
        return value;
    }

    public void Invalidate()
    {
        dirty = true;
        Changed();
    }

    public void SourceRetired(ReferenceSet<IDependent> visited)
    {
    }

    private void Compose()
    {
        if (!dirty) return;
        dirty = false;
        try
        {
            value = Batch!.Compute(() => compose([.. parts.Select(part => part.Value)]));
            hasGood = true;
            error = null;
        }
        catch (Exception thrown)
        {
            error = thrown;
        }
    }
}

/// <summary>A native source shaped as ref.state (the host's, phase 6): the reachability kind, "live" until <see cref="Fail"/> commits
/// "failed". Its watches are the host's.</summary>
internal sealed class StateReach(Batch batch) : NativeSource<string>(batch)
{
    private string kind = "live";

    public override string Value => kind;

    public override Task<INativeWatchHandle<string>> WatchAsync(ChordContext context) =>
        Task.FromException<INativeWatchHandle<string>>(new NotSupportedException("ref.state's watches are the host's (phase 6)"));

    /// <summary>The dependency fails (upstream <c>failLive</c>): "failed", in a commit of its own.</summary>
    public void Fail(ChordContext context) => Batch!.Within(() =>
    {
        kind = "failed";
        Changed();
    }, context);
}
