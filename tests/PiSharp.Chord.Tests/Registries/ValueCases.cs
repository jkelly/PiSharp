using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/registries/value.test.ts. As record.test.ts, upstream builds
// its registries through createHost (phase 6); here they run on the hand-built host (RegistryHost.cs): a reader is an object whose derive
// reads the registry's value (self.registry), parked while the value has none (upstream's failed { waiting }, woken by its waiter) and
// live at the next check that finds one; an object's failure (a followed runtime that ended, a native that closed) is Fail, and its native
// is the resource it contributes (a Native handle's `.value` is the host's). What only the host has is left out, and each case says
// what: the parked attempt's state (upstream's waiting message prefixes the value's error with "waiting for registry <name>: " and its
// retryAt), failures(errors) (activation_failed), the publication host.registry waits for, and getEventListeners (a CancellationToken has
// none: an observer kept by its registration would stay alive while the token does, so its collection with the token alive shows the
// registration went).
internal static partial class Program
{
    /// <summary>The provider's version 2: contributions are numbers, the value their sum.</summary>
    private static readonly Registry<double, RegistriesTotal> RegistriesSumV2 = Registry.Define<double, RegistriesTotal>("app.sum", new() { Version = 2 });

    /// <summary>A reader built against version 1: its value was a plain number.</summary>
    private static readonly Registry<double, double> RegistriesSumV1 = Registry.Define<double, double>("app.sum", new() { Version = 1 });

    /// <summary>A reader built against a version the provider does not know yet.</summary>
    private static readonly Registry<double, RegistriesTotalCount> RegistriesSumV3 = Registry.Define<double, RegistriesTotalCount>("app.sum", new() { Version = 3 });

    private static RegistriesTotal RegistriesTotalOf(IReadOnlyList<RegistryEntry<double>> table) => new(table.Sum(entry => entry.Value));

    /// <summary>A reader of <paramref name="registry"/> (upstream's <c>Reader</c>): its derive is the value's total.</summary>
    private static (RegistryObject Reader, Derive Total) RegistriesReader(RegistryHost host, Registry<double, RegistriesTotal>? registry = null)
    {
        Derive? total = null;
        var reader = host.Activate("@app/sum", "app.reader", self => total = self.Derive([self.Registry(registry ?? RegistriesSumV2)], values => ((RegistriesTotal)values[0]!).Total));
        return (reader, total!);
    }

    private static IEnumerable<(string, Func<Task>)> RegistriesValueCases() =>
    [
        Case("registries.value.older-readers-s2-get-the-composition-mapped-back-with-accepts-old-value-once-per-composition", async () =>
        {
            var metered = await Metered();
            var mapped = new List<double>();
            var host = new RegistryHost(metered.Context.Telemetry.Metrics);
            host.Load(("@app/sum", facet => facet.Serve(RegistriesSumV2, new RegistryProvider<double, RegistriesTotal>
            {
                Compose = (table, _) => RegistriesTotalOf(table),
                Accepts = new()
                {
                    [1] = new()
                    {
                        Value = current =>
                        {
                            mapped.Add(current.Total);
                            return current.Total;
                        },
                    },
                },
            })));
            var setter = RegistriesSetter(host, "@app/sum", RegistriesSumV2, 1.0);
            var old = await host.RegistryAsync(RegistriesSumV1);
            var current = await host.RegistryAsync(RegistriesSumV2);
            Equal(1.0, old.Value, "the old reader's value");
            Equal(1.0, old.Value, "read again");
            Equal(new RegistriesTotal(1), current.Value, "the current reader's value");
            setter.Set(4);
            Equal(4.0, old.Value, "the old reader's value after a set");
            Sequence([1.0, 4], mapped, "the mappings");
            Equal(2.0, metered.Counts()["chord.registry.adapts"], "the adapts");
            host.Close();
        }),
        Case("registries.value.older-readers-s2-without-a-value-mapping-and-for-a-reader-newer-than-the-provider-park-readers-failed-waiting-contract-mismatch", async () =>
        {
            var host = new RegistryHost();
            host.Load(("@app/sum", facet => facet.Serve(RegistriesSumV2, new RegistryProvider<double, RegistriesTotal> { Compose = (table, _) => RegistriesTotalOf(table) })));
            var newer = host.Activate("@app/sum", "app.newer", self => self.Derive([self.Registry(RegistriesSumV3)], values => ((RegistriesTotalCount)values[0]!).Total));
            var older = host.Activate("@app/sum", "app.older", self => self.Derive([self.Registry(RegistriesSumV1)], values => (double)values[0]!));
            var quick = TimeSpan.FromMilliseconds(5);
            Check(await Rejection(host.RegistryAsync(RegistriesSumV1, quick)) is JsonObject { } v1 && ChordJson.AsString(v1["code"]) == "contract_mismatch", "version 1's rejection");
            Check(await Rejection(host.RegistryAsync(RegistriesSumV3, quick)) is JsonObject { } v3 && ChordJson.AsString(v3["code"]) == "contract_mismatch", "version 3's rejection");
            // Not their fault: waiting (a reload serving the version wakes them), nothing failed.
            Equal(0, host.Errors.Count, "no report");
            Check(!newer.Live && !older.Live, "both readers are parked");
            Check(newer.Parked is ChordError { Code: "contract_mismatch", Message: "registry app.sum: version 3 is not served here (version 2 is)" }, $"the newer reader's cause: {newer.Parked}");
            Check(older.Parked is ChordError { Code: "contract_mismatch", Message: "registry app.sum: version 1 is not served here (version 2 is)" }, $"the older reader's cause: {older.Parked}");
            host.Close();
        }),
        Case("registries.value.older-readers-s2-drops-an-older-readers-last-good-mapping-after-a-contributor-it-was-built-from-failed-live-fix-round-1-astra-2", async () =>
        {
            var registry = Registry.Define<double, double>("app.mapped", new() { Version = 2 });
            var old = Registry.Define<double, double>("app.mapped", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/mapping", facet => facet.Serve(registry, new RegistryProvider<double, double>
            {
                Compose = (table, _) => table.Sum(entry => entry.Value),
                Accepts = new() { [1] = new() { Value = n => n == 0 ? throw new InvalidOperationException("old version cannot map the empty table") : n } },
            })));
            var contributor = host.Activate("@app/mapping", "app.contributor", self => self.Contribute(registry, 42.0));
            var value = await host.RegistryAsync(old);
            Equal(42.0, value.Value, "the mapping");
            // Its followed runtime ended failed: the object fails live.
            contributor.Fail();
            host.Delivered();
            Equal("old version cannot map the empty table", Throws<InvalidOperationException>(() => _ = value.Value, "the value").Message, "the error");
            host.Close();
        }),
        Case("registries.value.older-readers-s2-never-hands-out-a-mapped-native-whose-contributor-failed-and-closed-it-fix-round-1-sol-2", async () =>
        {
            var current = Registry.Define<RegistriesResource, RegistriesResource[]>("app.resources", new() { Version = 2 });
            var old = Registry.Define<RegistriesResource, RegistriesResource>("app.resources", new() { Version = 1 });
            var resource = new RegistriesResource();
            var host = new RegistryHost();
            host.Load(("@app/resources", facet => facet.Serve(current, new RegistryProvider<RegistriesResource, RegistriesResource[]>
            {
                Compose = (table, _) => [.. table.Select(entry => entry.Value)],
                Accepts = new() { [1] = new() { Value = resources => resources.Length == 0 ? throw new InvalidOperationException("no current resource") : resources[0] } },
            })));
            var owner = host.Activate("@app/resources", "app.owner", self => self.Contribute(current, resource));
            var value = await host.RegistryAsync(old);
            Check(ReferenceEquals(resource, value.Value), "the mapped native");
            // Its runtime closed: the object fails, and its native closes with it.
            owner.Fail();
            resource.Closed = true;
            host.Delivered();
            Check(resource.Closed, "the native is closed");
            Equal("no current resource", Throws<InvalidOperationException>(() => _ = value.Value, "the value").Message, "the error");
            host.Close();
        }),
        Case("registries.value.older-readers-s2-maps-an-undefined-composition-sol-astra", async () =>
        {
            var nothing = Registry.Define<double, object?>("app.nothing", new() { Version = 2 });
            var nothingV1 = Registry.Define<double, string>("app.nothing", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/a", facet => facet.Serve(nothing, new RegistryProvider<double, object?>
            {
                Compose = (_, _) => null,
                Accepts = new() { [1] = new() { Value = _ => "mapped" } },
            })));
            Equal("mapped", (await host.RegistryAsync(nothingV1)).Value, "the mapping");
            host.Close();
        }),
        Case("registries.value.older-readers-s2-a-failed-mapping-heals-when-the-composition-is-the-one-mapped-before-astra", async () =>
        {
            var count = Registry.Define<double, double>("app.count", new() { Version = 2 });
            var countV1 = Registry.Define<double, double>("app.count", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/count", facet => facet.Serve(count, new RegistryProvider<double, double>
            {
                Compose = (table, _) => table.Sum(entry => entry.Value),
                Accepts = new() { [1] = new() { Value = n => n < 0 ? throw new InvalidOperationException("negative") : n } },
            })));
            var counter = RegistriesSetter(host, "@app/count", count, 1.0);
            var old = await host.RegistryAsync(countV1);
            var calls = new List<NativeWatchCall<double>>();
            old.Observe(new StateNativeObserver<double>(calls.Add), StateBg);
            counter.Set(-1);
            // Back to the composition mapped before (the same number): the mapping heals.
            counter.Set(1);
            Equal(2, calls.Count, "two calls");
            Check(calls[0] is NativeWatchFailedCall<double> { Error: InvalidOperationException { Message: "negative" } }, $"the failure: {calls[0]}");
            Equal<NativeWatchCall<double>>(new NativeWatchValueCall<double>(1), calls[1], "the heal");
            host.Close();
        }),
        Case("registries.value.readers-s3-activate-once-the-registry-has-a-value-a-reader-of-a-registry-nobody-serves-parks-failed-waiting-load-waits-for-nothing", async () =>
        {
            var host = new RegistryHost();
            host.Load(("@app/reader", _ => { }));
            var (reader, _) = RegistriesReader(host);
            Equal(0, host.Errors.Count, "no report");
            Check(!reader.Live, "the reader is parked");
            Check(reader.Parked is ChordError { Code: "unavailable", Reason: "undeclared", Message: "registry app.sum: no plugin serves it here" }, $"its cause: {reader.Parked}");
            var rejected = await Rejection(host.RegistryAsync(RegistriesSumV2, TimeSpan.FromMilliseconds(5)));
            Check(rejected is JsonObject json && ChordJson.AsString(json["code"]) == "unavailable" && ChordJson.AsString(json["reason"]) == "undeclared", $"the rejection: {rejected}");
            host.Close();
        }),
        Case("registries.value.readers-s3-a-reader-parked-on-a-first-compose-that-threw-goes-on-at-the-first-good-composition", () =>
        {
            var host = new RegistryHost();
            host.Load(
                ("@app/sum", facet => facet.Serve(RegistriesSumV2, new RegistryProvider<double, RegistriesTotal>
                {
                    Compose = (table, _) =>
                    {
                        var sum = RegistriesTotalOf(table);
                        if (sum.Total == 0) throw new InvalidOperationException("empty");
                        return sum;
                    },
                })),
                ("@app/setter", _ => { }));
            var setter = RegistriesSetter(host, "@app/setter", RegistriesSumV2, 0.0);
            var (reader, total) = RegistriesReader(host);
            Check(!reader.Live && reader.Parked is InvalidOperationException { Message: "empty" }, $"the reader is parked: {reader.Parked}");
            setter.Set(3);
            // Woken by the composition: no timer of its own.
            host.Delivered();
            Check(reader.Live, "the reader is live");
            Json("3", total.Value, "its total");
            host.Close();
        }),
        Case("registries.value.readers-s3-host-registry-before-the-publication-waits-for-it-then-resolves-with-the-provider-it-served", async () =>
        {
            var host = new RegistryHost();
            var waiting = host.RegistryAsync(RegistriesSumV2);
            host.Load(("@app/sum", facet => facet.Serve(RegistriesSumV2, new RegistryProvider<double, RegistriesTotal> { Compose = (table, _) => RegistriesTotalOf(table) })));
            host.Delivered();
            Equal(new RegistriesTotal(0), (await waiting).Value, "the value");
            host.Close();
        }),
        Case("registries.value.readers-s3-host-registry-waits-through-no-value-until-a-first-good-composition-woken-by-it-its-wait-then-gone", async () =>
        {
            var host = new RegistryHost();
            host.Load(("@app/sum", facet => facet.Serve(RegistriesSumV2, new RegistryProvider<double, RegistriesTotal>
            {
                Compose = (table, _) =>
                {
                    var sum = RegistriesTotalOf(table);
                    if (sum.Total == 0) throw new InvalidOperationException("empty");
                    return sum;
                },
            })));
            var setter = RegistriesSetter(host, "@app/sum", RegistriesSumV2, 0.0);
            var waiting = host.RegistryAsync(RegistriesSumV2, TimeSpan.FromMilliseconds(500));
            host.Delivered();
            Check(!waiting.IsCompleted, "it waits");
            setter.Set(5);
            host.Delivered();
            var value = await waiting;
            Equal(new RegistriesTotal(5), value.Value, "the value");
            Check(!((RegistryValueSource<RegistriesTotal>)value).Watched, "its wait is gone");
            host.Close();
        }),
        Case("registries.value.readers-s3-once-live-a-later-throwing-compose-changes-nothing-for-the-reader-value-keeps-the-last-good-one", () =>
        {
            var host = new RegistryHost();
            host.Load(("@app/sum", facet => facet.Serve(RegistriesSumV2, new RegistryProvider<double, RegistriesTotal>
            {
                Compose = (table, _) =>
                {
                    var sum = RegistriesTotalOf(table);
                    if (sum.Total < 0) throw new InvalidOperationException("negative");
                    return sum;
                },
            })));
            var setter = RegistriesSetter(host, "@app/sum", RegistriesSumV2, 2.0);
            var (reader, total) = RegistriesReader(host);
            Json("2", total.Value, "the total");
            setter.Set(-1);
            Json("2", total.Value, "the total after a throwing compose");
            Check(reader.Live, "the reader stays live");
            host.Close();
        }),
        Case("registries.value.readers-memory-an-ended-reader-is-collected-while-the-registry-lives-its-derive-and-its-read-leave-the-value", async () =>
        {
            var host = new RegistryHost();
            host.Load(("@app/sum", facet => facet.Serve(RegistriesSumV2, new RegistryProvider<double, RegistriesTotal>
            {
                Compose = (table, _) => RegistriesTotalOf(table),
                Accepts = new() { [1] = new() { Value = current => current.Total } },
            })));
            var setter = RegistriesSetter(host, "@app/sum", RegistriesSumV2, 1.0);
            var references = RegistriesEndedReader(host);
            setter.Set(5);
            await ExpectCollected(references);
            host.Close();
        }),
        Case("registries.value.values-memory-drops-an-older-versions-mapping-once-its-last-reader-ended-sol", async () =>
        {
            var host = new RegistryHost();
            var mapped = RegistriesMappedThenEnded(host);
            host.Delivered();
            await ExpectCollected(mapped);
            host.Close();
        }),
        Case("registries.value.values-memory-keeps-no-old-composition-once-its-readers-stopped-waiting-and-nobody-follows-it-opus", async () =>
        {
            var big = Registry.Define<double, RegistriesBig>("app.big", new() { Version = 1 });
            var first = true;
            var made = new List<WeakReference>();
            var host = new RegistryHost();
            host.Load(("@app/a", facet => facet.Serve(big, new RegistryProvider<double, RegistriesBig>
            {
                Compose = (table, _) =>
                {
                    if (first) throw new InvalidOperationException("first");
                    var value = new RegistriesBig(Enumerable.Repeat((double)table.Count, 1000).ToArray());
                    made.Add(new WeakReference(value));
                    return value;
                },
            })));
            var counter = RegistriesSetter(host, "@app/a", big, 0.0);
            var waiting = host.Activate("@app/a", "app.waiting", self => _ = self.Registry(big));
            Check(!waiting.Live, "the reader waits");
            first = false;
            // The waiting reader's composition (pulled in the batch of this set) wakes it.
            counter.Set(1);
            host.Delivered();
            Check(waiting.Live, "the reader is live");
            counter.Set(2);
            _ = (await host.RegistryAsync(big)).Value;
            for (var round = 0; round < 5; round++)
            {
                await Task.Yield();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            // Only the current composition is alive.
            Sequence([false, true], made.Select(reference => reference.IsAlive), "the compositions alive");
            GC.KeepAlive(host);
            host.Close();
        }),
        Case("registries.value.values-memory-a-value-an-app-keeps-after-host-close-keeps-no-closed-host-and-reads-its-last-composition-sol-astra", async () =>
        {
            using var signal = new CancellationTokenSource();
            var (references, value) = RegistriesKeptAfterClose(signal.Token);
            // The token stays alive here: a registration it still held would keep the observer.
            await ExpectCollected(references);
            Equal(7.0, value.Value, "the last composition");
            GC.KeepAlive(signal);
        }),
        Case("registries.value.observe-calls-a-synchronous-observer-inside-the-batch-that-composed-it-counts-as-watching", async () =>
        {
            var labels = Registry.Define<string, string>("app.labels", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/labels", facet => facet.Serve(labels, new RegistryProvider<string, string> { Compose = (table, _) => string.Join(",", table.Select(entry => entry.Value)) })));
            var label = RegistriesSetter(host, "@app/labels", labels, "a");
            var value = await host.RegistryAsync(labels);
            var calls = new List<object>();
            var observer = new RegistriesObserver<string>(calls);
            var (cancellable, cancel) = ChordContext.WithCancel(StateBg);
            var observation = value.Observe(observer, cancellable);
            Equal("a", observation.Value, "the start value");
            label.Set("b");
            // Already called: synchronously, in the set's batch.
            Sequence<object>([new NativeWatchValueCall<string>("b")], calls, "the calls");
            cancel();
            label.Set("c");
            Sequence<object>([new NativeWatchValueCall<string>("b"), WatchEnd.Aborted], calls, "the calls after the cancel");
            var again = value.Observe(observer, StateBg);
            again.Detach();
            label.Set("d");
            Equal(2, calls.Count, "a detached observer gets nothing");
            value.Observe(new RegistriesObserver<string>(calls, advances: false), StateBg);
            host.Close();
            Equal<object>(WatchEnd.Ended, calls[^1], "the close ends it");
        }),
        Case("registries.value.a-reloads-new-value-adapter-fix-round-1-uses-a-new-value-adapter-even-when-compose-returns-the-same-primitive", async () =>
        {
            var registry = Registry.Define<double, double>("astra.registry", new() { Version = 2 });
            var old = Registry.Define<double, double>("astra.registry", new() { Version = 1 });
            void Facet(RegistryFacet facet, double factor) => facet.Serve(registry, new RegistryProvider<double, double>
            {
                Compose = (_, _) => 7,
                Accepts = new() { [1] = new() { Value = n => n * factor } },
            });
            var host = new RegistryHost();
            host.Load(("@astra/reg", facet => Facet(facet, 2)));
            var value = await host.RegistryAsync(old);
            Equal(14.0, value.Value, "the first adapter's");
            host.Reload("@astra/reg", facet => Facet(facet, 3));
            var actual = value.Value;
            host.Close();
            Equal(21.0, actual, "the reloaded adapter's");
        }),
        Case("registries.value.pisharp-a-version-is-read-as-one-type-a-composition-of-another-type-fails-it-contract-mismatch", async () =>
        {
            // PiSharp: JavaScript has no casts. Two tokens of one version whose value types differ cannot share its value, and a reader whose
            // value type is not the composition's gets contract_mismatch (a ChordError: its waiters are not woken again by the same one).
            var numbers = Registry.Define<double, double>("app.typed", new() { Version = 1 });
            var strings = Registry.Define<double, string>("app.typed", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/a", facet => facet.Serve(numbers, new RegistryProvider<double, double> { Compose = (table, _) => table.Count })));
            Equal(0.0, (await host.RegistryAsync(numbers)).Value, "the value");
            Refuses(() => host.Record("app.typed").Value<string>(1), "registry app.typed: version 1 is read as Double already, not as String");
            var other = new RegistryHost();
            other.Load(("@app/a", facet => facet.Serve(numbers, new RegistryProvider<double, double> { Compose = (table, _) => table.Count })));
            var rejected = await Rejection(other.RegistryAsync(strings, TimeSpan.FromMilliseconds(5)));
            Check(rejected is JsonObject json && ChordJson.AsString(json["code"]) == "contract_mismatch" &&
                ChordJson.AsString(json["message"]) == "registry app.typed: version 1 reads String, not Double", $"the rejection: {rejected}");
            host.Close();
            other.Close();
        }),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RegistriesEndedReader(RegistryHost host)
    {
        Derive? total = null;
        var reader = host.Activate("@app/sum", "app.reader", self =>
            total = self.Derive([self.Registry(RegistriesSumV2), self.Registry(RegistriesSumV1)], values => ((RegistriesTotal)values[0]!).Total + (double)values[1]!));
        Json("2", total!.Value, "the reader's total");
        reader.End();
        return [new WeakReference(reader), new WeakReference(total)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegistriesMappedThenEnded(RegistryHost host)
    {
        var @new = Registry.Define<double, double>("app.version-gc", new() { Version = 2 });
        var old = Registry.Define<double, RegistriesPayload>("app.version-gc", new() { Version = 1 });
        WeakReference? mapped = null;
        host.Load(("@app/a", facet => facet.Serve(@new, new RegistryProvider<double, double>
        {
            Compose = (_, _) => 1,
            Accepts = new()
            {
                [1] = new()
                {
                    Value = n =>
                    {
                        var value = new RegistriesPayload(n, new byte[1_000_000]);
                        mapped = new WeakReference(value);
                        return value;
                    },
                },
            },
        })));
        Derive? n = null;
        var reader = host.Activate("@app/a", "app.old-reader", self => n = self.Derive([self.Registry(old)], values => ((RegistriesPayload)values[0]!).N));
        Json("1", n!.Value, "the reader's n");
        reader.End();
        return mapped!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] References, IRegistryValue<double> Value) RegistriesKeptAfterClose(CancellationToken signal)
    {
        var kept = Registry.Define<double, double>("app.kept", new() { Version = 1 });
        var host = new RegistryHost();
        host.Load(("@app/a", facet => facet.Serve(kept, new RegistryProvider<double, double> { Compose = (_, _) => 7 })));
        var value = host.RegistryAsync(kept).GetAwaiter().GetResult();
        var ends = new List<object>();
        var observer = new RegistriesObserver<double>(ends, advances: false);
        value.Observe(observer, ChordContext.WithAbortSignal(signal, StateBg));
        host.Close();
        // The observation ended with the host: its abort registration went with it.
        Sequence<object>([WatchEnd.Ended], ends, "the observer's end");
        return ([new WeakReference(host), new WeakReference(observer)], value);
    }

    /// <summary>Version 2's value.</summary>
    private sealed record RegistriesTotal(double Total);

    /// <summary>Version 3's value.</summary>
    private sealed record RegistriesTotalCount(double Total, double Count);

    /// <summary>An older version's mapped value with a large payload.</summary>
    private sealed record RegistriesPayload(double N, byte[] Payload);

    /// <summary>A large composition.</summary>
    private sealed record RegistriesBig(double[] Payload);

    /// <summary>A native resource its owner closes.</summary>
    private sealed class RegistriesResource
    {
        public bool Closed { get; set; }
    }

    /// <summary>An observer recording its calls (or only its end) in one list.</summary>
    private sealed class RegistriesObserver<T>(List<object> calls, bool advances = true) : INativeObserver<T>
    {
        public void Advance(NativeWatchCall<T> call, ChordContext context)
        {
            if (advances) calls.Add(call);
        }

        public void End(WatchEnd end) => calls.Add(end);
    }
}
