using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/registries/record.test.ts. Upstream builds every registry
// through createHost (phase 6): plugins loaded with loadedPlugins, host-lifetime objects (local types with empty params, which go live at
// load) contributing writables, derives over documents and other registries' values, and storeRestartable's attempts. Here they run on the
// hand-built host (RegistryHost.cs), with the host's wiring done by hand: Load is load's publication, Activate an object going live (its
// contributions entering the tables in one commit), Fail and a second Activate an attempt failing and its retry, Reload a reload's commit,
// and Close host.close; an object's method that sets a writable or changes a document is that set or change, in its own commit, and a
// document is the state layer's StateDoc. A derive's JSON values are JsonNodes, so a derive contributing `d.n > 0` gives JSON booleans and
// its registry's contribution type is JsonNode. What only the host has is left out, and each case says what: the objects' reachability
// (`state.value` is `{ kind: "live" }`), and failures(errors) (activation_failed). Waits for phase 6: "a followed contribution whose
// runtime ended its observer stays with its last value" (self.follow's source, host/follow.ts, is the host's).
internal static partial class Program
{
    /// <summary>A registry whose composition is its table as <c>plugin:type=value</c> strings.</summary>
    private static readonly Registry<object?, string[]> RegistriesNames = Registry.Define<object?, string[]>("app.names", new() { Version = 1 });

    private static string[] RegistriesListed<C>(IReadOnlyList<RegistryEntry<C>> table) =>
        [.. table.Select(entry => $"{entry.Plugin}:{entry.Type ?? ""}={RegistriesText(entry.Value)}")];

    /// <summary>JavaScript's <c>${value}</c>.</summary>
    private static string RegistriesText(object? value) => value is JsonNode node ? ChordJson.ToJavaScriptString(node) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "undefined";

    private static RegistryProvider<C, string[]> RegistriesListing<C>() => new() { Compose = (table, _) => RegistriesListed(table) };

    /// <summary>Upstream's <c>setter</c>: a host-lifetime object of <paramref name="plugin"/> contributing a writable it sets with <c>set</c>.</summary>
    private static WritableSource<T> RegistriesSetter<C, V, T>(RegistryHost host, string plugin, Registry<C, V> registry, T initial)
    {
        WritableSource<T>? value = null;
        host.Activate(plugin, "app.setter", self =>
        {
            value = self.Writable(initial);
            self.Contribute(registry, value);
        });
        return value!;
    }

    private static List<RegistryErrorKind> RegistriesKinds(IEnumerable<RegistryError> errors) => [.. errors.Select(error => error.Kind)];

    private static string RegistriesCountOf(Func<IReadOnlyDictionary<string, double>> counts, string counter) =>
        counts().GetValueOrDefault(counter).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A sum over number contributions, throwing <c>negative: &lt;sum&gt;</c> below zero (upstream's <c>Count</c>).</summary>
    private static double RegistriesSum(IReadOnlyList<RegistryEntry<double>> table)
    {
        var sum = table.Sum(entry => entry.Value);
        if (sum < 0) throw new InvalidOperationException(FormattableString.Invariant($"negative: {sum}"));
        return sum;
    }

    private static IEnumerable<(string, Func<Task>)> RegistriesRecordCases() =>
    [
        Case("registries.record.the-table-is-sorted-by-plugin-name-then-type-a-facet-first-never-load-order", async () =>
        {
            var host = new RegistryHost();
            host.Load(
                ("@z/late", facet => facet.Contribute(RegistriesNames, "z")),
                ("@a/early", facet => facet.Contribute(RegistriesNames, "a")),
                ("@m/provider", facet => facet.Serve(RegistriesNames, RegistriesListing<object?>())));
            RegistriesSetter(host, "@a/early", RegistriesNames, "store");
            var value = await host.RegistryAsync(RegistriesNames);
            Sequence(["@a/early:=a", "@a/early:app.setter=store", "@z/late:=z"], value.Value, "the table");
            host.Close();
        }),
        Case("registries.record.the-table-adapts-an-older-contribution-once-per-value-a-newer-one-or-an-older-one-without-an-adapter-is-left-out-and-reported-once", async () =>
        {
            var v3 = Registry.Define<string, string[]>("app.versions", new() { Version = 3 });
            var v2 = Registry.Define<RegistriesOld, object?>("app.versions", new() { Version = 2 });
            var v1 = Registry.Define<string, object?>("app.versions", new() { Version = 1 });
            var v4 = Registry.Define<string, object?>("app.versions", new() { Version = 4 });
            var adapted = new List<string>();
            var host = new RegistryHost();
            host.Load(
                ("@app/provider", facet => facet.Serve(v3, new RegistryProvider<string, string[]>
                {
                    Compose = (table, _) => RegistriesListed(table),
                    Accepts = new()
                    {
                        [2] = new()
                        {
                            Contribution = old =>
                            {
                                var value = ((RegistriesOld)old!).Old;
                                adapted.Add(value);
                                return $"adapted {value}";
                            },
                        },
                    },
                })),
                ("@app/one", facet => facet.Contribute(v1, "one")),
                ("@app/two", _ => { }),
                ("@app/three", facet => facet.Contribute(v3, "three")),
                ("@app/four", facet => facet.Contribute(v4, "four")));
            var setter = RegistriesSetter(host, "@app/two", v2, new RegistriesOld("first"));
            var value = await host.RegistryAsync(v3);
            Sequence(["@app/three:=three", "@app/two:app.setter=adapted first"], value.Value, "the table");
            Sequence<RegistryError>(
                [new ContributionRefusedError("app.versions", "@app/four", null, 4), new ContributionRefusedError("app.versions", "@app/one", null, 1)],
                host.Errors, "the refusals");
            setter.Set(new RegistriesOld("5"));
            Sequence(["@app/three:=three", "@app/two:app.setter=adapted 5"], value.Value, "the table after a set");
            // Each value adapted once, though the table was built again; no refusal reported twice.
            Sequence(["first", "5"], adapted, "the adaptations");
            Sequence([RegistryErrorKind.ContributionRefused, RegistryErrorKind.ContributionRefused], RegistriesKinds(host.Errors), "the kinds");
            host.Close();
        }),
        Case("registries.record.the-table-takes-a-contribution-declared-before-its-providers-setup-into-the-first-composition", () =>
        {
            string[]? composed = null;
            var host = new RegistryHost();
            host.Load(
                ("@app/a", facet => facet.Contribute(RegistriesNames, "first")),
                ("@app/provider", facet => facet.Serve(RegistriesNames, new RegistryProvider<object?, string[]> { Compose = (table, _) => composed = RegistriesListed(table) })));
            Sequence(["@app/a:=first"], composed!, "the first composition");
            host.Close();
        }),
        Case("registries.record.compose-composes-every-served-registry-once-at-the-end-of-loads-setups-an-empty-table-too-before-any-object-activates", async () =>
        {
            var metered = await Metered();
            var other = Registry.Define<object?, double>("app.other", new() { Version = 1 });
            var host = new RegistryHost(metered.Context.Telemetry.Metrics);
            host.Load(("@app/provider", facet =>
            {
                facet.Serve(RegistriesNames, RegistriesListing<object?>());
                facet.Serve(other, new RegistryProvider<object?, double> { Compose = (table, _) => table.Count });
                facet.Contribute(RegistriesNames, "a");
            }));
            Equal("2", RegistriesCountOf(metered.Counts, "chord.registry.composes"), "the composes at load");
            string[]? seen = null;
            Derive? seenOther = null;
            var reader = host.Activate("@app/provider", "app.reader", self =>
            {
                var names = self.Registry(RegistriesNames);
                seenOther = self.Derive([self.Registry(other)], values => (double)values[0]!);
                // Its native's open reads the registry: here, right after the factory.
                seen = names.Value;
            });
            Check(reader.Live, "the reader is live");
            Sequence(["@app/provider:=a"], seen!, "what the reader saw");
            Json("0", seenOther!.Value, "the derive over the other registry");
            Equal("2", RegistriesCountOf(metered.Counts, "chord.registry.composes"), "no compose since");
            host.Close();
        }),
        Case("registries.record.compose-is-lazy-a-change-marks-the-table-a-read-composes-it-once-no-compose-for-an-unchanged-table", async () =>
        {
            var metered = await Metered();
            var flags = Registry.Define<JsonNode?, double>("app.flags", new() { Version = 1 });
            var host = new RegistryHost(metered.Context.Telemetry.Metrics);
            host.Load(("@app/flags", facet => facet.Serve(flags, new RegistryProvider<JsonNode?, double>
            {
                Compose = (table, _) => table.Count(entry => ChordJson.IsBoolean(entry.Value, true)),
            })));
            var doc = host.State.Doc("""{"n":1}""");
            host.Activate("@app/flags", "app.flagger", self => self.Contribute(flags, self.Derive([doc], values => ChordJson.AsNumber(((JsonNode)values[0]!)["n"]) > 0)));
            var value = await host.RegistryAsync(flags);
            double Composes() => metered.Counts().GetValueOrDefault("chord.registry.composes");
            Equal(1.0, value.Value, "the value");
            var start = Composes();
            Equal(1.0, value.Value, "the value read again");
            Equal(start, Composes(), "no compose for a read");
            // The derive changes, its value does not: the table stays, nothing composes.
            doc.Set("""{"n":2}""", StateBg);
            doc.Set("""{"n":3}""", StateBg);
            Equal(1.0, value.Value, "the value after unchanging changes");
            Equal(start, Composes(), "no compose for an unchanged table");
            // Nobody reads it: nothing composes until a read.
            doc.Set("""{"n":-1}""", StateBg);
            doc.Set("""{"n":-2}""", StateBg);
            Equal(start, Composes(), "no compose without a read");
            Equal(0.0, value.Value, "the value after a change");
            Equal(start + 1, Composes(), "one compose for the read");
            host.Close();
        }),
        Case("registries.record.compose-composes-a-watched-registry-in-the-batch-of-the-commit-that-changed-a-contribution-once-per-batch", async () =>
        {
            var metered = await Metered();
            var sum = Registry.Define<JsonNode?, double>("app.sum", new() { Version = 1 });
            var host = new RegistryHost(metered.Context.Telemetry.Metrics);
            host.Load(("@app/sum", facet => facet.Serve(sum, new RegistryProvider<JsonNode?, double>
            {
                Compose = (table, _) => table.Sum(entry => ChordJson.AsNumber(entry.Value!["a"])!.Value + ChordJson.AsNumber(entry.Value!["b"])!.Value),
            })));
            var doc = host.State.Doc("""{"a":0,"b":0}""");
            Derive? total = null;
            host.Activate("@app/sum", "app.two", self =>
            {
                self.Contribute(sum, doc);
                total = self.Derive([self.Registry(sum)], values => (double)values[0]!);
            });
            var sums = StateNumbers(await total!.WatchAsync(StateBg));
            var start = metered.Counts().GetValueOrDefault("chord.registry.composes");
            // `both(2)`: one transaction setting both fields, one commit.
            doc.Change(_ => JsonNode.Parse("""{"a":2,"b":2}"""), StateBg);
            host.Delivered();
            // One commit, both fields: one compose in its batch, one frame of the derive over it.
            Sequence([4.0], sums, "the sums");
            Equal(start + 1, metered.Counts().GetValueOrDefault("chord.registry.composes"), "one compose");
            host.Close();
        }),
        Case("registries.record.compose-reports-a-conflict-once-per-episode", async () =>
        {
            var routes = Registry.Define<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, TableItem<string>>>("app.routes", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(
                ("@app/a", facet => facet.Contribute(routes, new Dictionary<string, string> { ["/x"] = "a" })),
                ("@app/b", _ => { }),
                ("@app/http", facet => facet.Serve(routes, new RegistryProvider<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, TableItem<string>>>
                {
                    Compose = (table, context) => RegistryTable.KeyedEntries(table, contribution => contribution, context.Report),
                })));
            var setter = RegistriesSetter<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, TableItem<string>>, IReadOnlyDictionary<string, string>>(
                host, "@app/b", routes, new Dictionary<string, string> { ["/x"] = "b" });
            var value = await host.RegistryAsync(routes);
            Equal(new TableItem<string>("a", "@app/a"), value.Value["/x"], "the first by plugin name keeps the key");
            setter.Set(new Dictionary<string, string> { ["/x"] = "b", ["/1"] = "b" });
            _ = value.Value;
            setter.Set(new Dictionary<string, string> { ["/x"] = "b", ["/2"] = "b" });
            _ = value.Value;
            Equal(1, host.Errors.Count, "one report");
            var conflict = (RegistryConflictError)host.Errors[0];
            Equal("app.routes", conflict.Registry, "the registry");
            Equal("\"/x\" is contributed twice", conflict.Message, "the message");
            Sequence(["@app/a", "@app/b"], conflict.Plugins, "the plugins");
            host.Close();
        }),
        Case("registries.record.compose-a-throwing-compose-reported-once-a-failed-call-value-keeps-the-last-good-composition-the-next-good-one-heals", async () =>
        {
            var count = Registry.Define<double, double>("app.count", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/count", facet => facet.Serve(count, new RegistryProvider<double, double> { Compose = (table, _) => RegistriesSum(table) })));
            var setter = RegistriesSetter(host, "@app/count", count, 1.0);
            var value = await host.RegistryAsync(count);
            var calls = StateNativeCalls(await value.WatchAsync(StateBg));
            setter.Set(-1);
            setter.Set(-2);
            host.Delivered();
            Equal(1.0, value.Value, "the last good composition");
            Equal(1, host.Errors.Count, "one report");
            Check(host.Errors[0] is RegistryFailedError { Registry: "app.count", Error.Message: "negative: -1" }, $"the report: {host.Errors[0]}");
            setter.Set(1);
            host.Delivered();
            // The heal is a value call, also when its value is the last good one.
            Equal(2, calls.Count, "two calls");
            Check(calls[0] is NativeWatchFailedCall<double> { Error: Exception { Message: "negative: -1" } }, $"the failed call: {calls[0]}");
            Equal<NativeWatchCall<double>>(new NativeWatchValueCall<double>(1), calls[1], "the heal");
            setter.Set(3);
            host.Delivered();
            Equal<NativeWatchCall<double>>(new NativeWatchValueCall<double>(3), calls[^1], "the next value");
            host.Close();
        }),
        Case("registries.record.compose-before-any-good-composition-value-throws-the-error-and-host-registry-rejects-with-it-at-its-ready-timeout", async () =>
        {
            var broken = Registry.Define<double, double>("app.broken", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/broken", facet => facet.Serve(broken, new RegistryProvider<double, double> { Compose = (_, _) => throw new InvalidOperationException("bug") })));
            Equal("bug", Throws<InvalidOperationException>(() => _ = host.Record("app.broken").Value<double>(1).Value, "the value").Message, "the value's error");
            var rejected = await Rejection(host.RegistryAsync(broken, TimeSpan.FromMilliseconds(5)));
            Check(rejected is InvalidOperationException { Message: "bug" }, $"the rejection: {rejected}");
            Sequence([RegistryErrorKind.RegistryFailed], RegistriesKinds(host.Errors), "the kinds");
            host.Close();
        }),
        Case("registries.record.contributions-a-watchable-contribution-that-fails-is-left-out-and-reported-once-and-is-back-once-it-heals", async () =>
        {
            var host = new RegistryHost();
            host.Load(("@app/names", facet =>
            {
                facet.Serve(RegistriesNames, RegistriesListing<object?>());
                facet.Contribute(RegistriesNames, "facet");
            }));
            var doc = host.State.Doc("""{"n":1}""");
            host.Activate("@app/names", "app.failing", self => self.Contribute(RegistriesNames, self.Derive([doc], values =>
            {
                var n = ChordJson.AsNumber(((JsonNode)values[0]!)["n"])!.Value;
                if (n < 0) throw new InvalidOperationException("negative");
                return FormattableString.Invariant($"n{n}");
            })));
            var names = await host.RegistryAsync(RegistriesNames);
            Sequence(["@app/names:=facet", "@app/names:app.failing=n1"], names.Value, "the table");
            doc.Set("""{"n":-1}""", StateBg);
            Sequence(["@app/names:=facet"], names.Value, "the table while it fails");
            doc.Set("""{"n":-2}""", StateBg);
            Sequence(["@app/names:=facet"], names.Value, "the table while it still fails");
            doc.Set("""{"n":2}""", StateBg);
            Sequence(["@app/names:=facet", "@app/names:app.failing=n2"], names.Value, "the table once healed");
            var failed = host.Errors.Where(error => error.Kind == RegistryErrorKind.ContributionFailed).ToList();
            Equal(1, failed.Count, "one report");
            Check(failed[0] is ContributionFailedError { Registry: "app.names", Plugin: "@app/names", Type: "app.failing", Error.Message: "negative" }, $"the report: {failed[0]}");
            host.Close();
        }),
        Case("registries.record.contributions-a-contribution-adapter-that-throws-leaves-that-contribution-out-contribution-failed", async () =>
        {
            var v2 = Registry.Define<string, string[]>("app.adapted", new() { Version = 2 });
            var v1 = Registry.Define<string, object?>("app.adapted", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(
                ("@app/provider", facet =>
                {
                    facet.Serve(v2, new RegistryProvider<string, string[]>
                    {
                        Compose = (table, _) => RegistriesListed(table),
                        Accepts = new() { [1] = new() { Contribution = _ => throw new InvalidOperationException("cannot adapt") } },
                    });
                    facet.Contribute(v2, "current");
                }),
                ("@app/old", facet => facet.Contribute(v1, "old")));
            Sequence(["@app/provider:=current"], (await host.RegistryAsync(v2)).Value, "the table");
            Equal(1, host.Errors.Count, "one report");
            Check(host.Errors[0] is ContributionFailedError { Registry: "app.adapted", Plugin: "@app/old", Type: null, Error.Message: "cannot adapt" }, $"the report: {host.Errors[0]}");
            host.Close();
        }),
        Case("registries.record.contributions-catches-a-ring-of-registries-at-its-first-composition-the-contributors-go-live-the-rest-works-audit-a2", async () =>
        {
            var a = Registry.Define<object?, double>("app.ring-a", new() { Version = 1 });
            var b = Registry.Define<object?, double>("app.ring-b", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/ring", facet =>
            {
                facet.Serve(a, new RegistryProvider<object?, double> { Compose = (table, _) => table.Count });
                facet.Serve(b, new RegistryProvider<object?, double> { Compose = (table, _) => table.Count });
                facet.Contribute(a, "plain");
                facet.Contribute(b, "plain");
            }));
            var toA = host.Activate("@app/ring", "app.to-a", self => self.Contribute(a, self.Registry(b)));
            var toB = host.Activate("@app/ring", "app.to-b", self => self.Contribute(b, self.Registry(a)));
            Check(toA.Live && toB.Live, "the contributors are live");
            // A composes first: B composes for it and its contribution of A, read while A composes, is left out. A has its plain entry and B's
            // value, B its plain entry.
            Sequence([2.0, 1.0], [(await host.RegistryAsync(a)).Value, (await host.RegistryAsync(b)).Value], "the values");
            Equal(1, host.Errors.Count, "one report");
            Check(host.Errors[0] is ContributionFailedError { Registry: "app.ring-b", Plugin: "@app/ring", Type: "app.to-b", Error: ChordError { Code: "invalid", Reason: "cycle" } },
                $"the report: {host.Errors[0]}");
            host.Close();
        }),
        Case("registries.record.contributions-keeps-a-rings-later-recompositions-bounded-a-change-composes-each-registry-once-then-nothing-audit-a2", async () =>
        {
            var a = Registry.Define<object?, double>("app.later-a", new() { Version = 1 });
            var b = Registry.Define<object?, double>("app.later-b", new() { Version = 1 });
            var composes = 0;
            double Sum(IReadOnlyList<RegistryEntry<object?>> table, ComposeContext _)
            {
                composes++;
                return table.Sum(entry => entry.Value is double number ? number : 0);
            }
            var host = new RegistryHost();
            host.Load(("@app/later", facet =>
            {
                facet.Serve(a, new RegistryProvider<object?, double> { Compose = Sum });
                facet.Serve(b, new RegistryProvider<object?, double> { Compose = Sum });
            }));
            host.Activate("@app/later", "app.later-ring", self => self.Contribute(a, self.Registry(b)));
            host.Activate("@app/later", "app.later-back", self => self.Contribute(b, self.Registry(a)));
            WritableSource<double>? extra = null;
            host.Activate("@app/later", "app.later-extra", self =>
            {
                extra = self.Writable(1.0);
                self.Contribute(b, extra);
            });
            var valueA = await host.RegistryAsync(a);
            var valueB = await host.RegistryAsync(b);
            var watches = new[] { await valueA.WatchAsync(StateBg), await valueB.WatchAsync(StateBg) };
            host.Delivered();
            var before = composes;
            extra!.Set(5);
            host.Delivered();
            // B changed and composes first now: it reads A, whose contribution of B, read while B composes, is cut instead.
            Equal(2, composes - before, "one compose each");
            Sequence([0.0, 5.0], [valueA.Value, valueB.Value], "the values");
            Sequence(["app.later-b", "app.later-a"], host.Errors.Select(error => error is ContributionFailedError ? error.Registry : error.Kind.ToString()), "the reports");
            // Nothing composes on by itself.
            host.Delivered();
            Equal(2, composes - before, "nothing composes on");
            foreach (var watch in watches) await watch.StopAsync();
            host.Close();
        }),
        Case("registries.record.contributions-catches-a-ring-through-a-derive-and-one-declared-within-one-factory-the-same-way-audit-a2", async () =>
        {
            var a = Registry.Define<object?, double>("app.both-a", new() { Version = 1 });
            var b = Registry.Define<object?, double>("app.both-b", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/both", facet =>
            {
                facet.Serve(a, new RegistryProvider<object?, double> { Compose = (table, _) => table.Count });
                facet.Serve(b, new RegistryProvider<object?, double> { Compose = (table, _) => table.Count });
            }));
            var both = host.Activate("@app/both", "app.both", self =>
            {
                self.Contribute(a, self.Registry(b));
                self.Contribute(b, self.Derive([self.Registry(a)], values => (double)values[0]!));
            });
            Check(both.Live, "it is live");
            await host.RegistryAsync(a);
            await host.RegistryAsync(b);
            var cut = host.Errors.OfType<ContributionFailedError>().ToList();
            Check(cut.Count > 0, "a contribution is cut");
            foreach (var error in cut) Check(error.Error is ChordError { Code: "invalid", Reason: "cycle" }, $"a cut: {error.Error}");
            Check(host.Errors.All(error => error is ContributionFailedError), "nothing else is reported");
            host.Close();
        }),
        Case("registries.record.contributions-follows-only-chord-watchables-anything-else-is-a-plain-value-whatever-methods-it-has-sol", async () =>
        {
            var natives = Registry.Define<RegistriesHandle, string[]>("app.natives", new() { Version = 1 });
            var native = new RegistriesHandle();
            var host = new RegistryHost();
            host.Load(("@app/x", facet =>
            {
                facet.Serve(natives, new RegistryProvider<RegistriesHandle, string[]> { Compose = (table, _) => [.. table.Select(entry => entry.Value.Operation())] });
                facet.Contribute(natives, native);
            }));
            Sequence(["a native operation"], (await host.RegistryAsync(natives)).Value, "the composition");
            host.Close();
        }),
        Case("registries.record.contributions-keeps-an-undefined-contribution-in-the-table-sol-astra", async () =>
        {
            var nothing = Registry.Define<object?, double>("app.undefined", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/a", facet =>
            {
                facet.Serve(nothing, new RegistryProvider<object?, double> { Compose = (table, _) => table.Count });
                facet.Contribute(nothing, null);
            }));
            Equal(1.0, (await host.RegistryAsync(nothing)).Value, "the table's length");
            host.Close();
        }),
        Case("registries.record.contributions-leaves-out-a-registrys-value-contributed-to-another-while-that-registrys-compose-throws-sol-astra", async () =>
        {
            var source = Registry.Define<double, double>("app.source", new() { Version = 1 });
            var target = Registry.Define<double, double[]>("app.target", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@app/a", facet =>
            {
                facet.Serve(source, new RegistryProvider<double, double>
                {
                    Compose = (table, _) =>
                    {
                        var n = table.Sum(entry => entry.Value);
                        if (n < 0) throw new InvalidOperationException("negative");
                        return n;
                    },
                });
                facet.Serve(target, new RegistryProvider<double, double[]> { Compose = (table, _) => [.. table.Select(entry => entry.Value)] });
            }));
            var setter = RegistriesSetter(host, "@app/a", source, 1.0);
            host.Activate("@app/a", "app.bridge", self => self.Contribute(target, self.Registry(source)));
            var value = await host.RegistryAsync(target);
            Sequence([1.0], value.Value, "the target");
            setter.Set(-1);
            // The source's readers keep its last good value; as a contribution it is failed: left out, reported.
            Equal(1.0, (await host.RegistryAsync(source)).Value, "the source's last good value");
            Sequence([], value.Value, "the target while the source fails");
            Check(host.Errors.OfType<ContributionFailedError>().FirstOrDefault() is { Registry: "app.target", Plugin: "@app/a", Type: "app.bridge", Error.Message: "negative" },
                "the report: " + string.Join(", ", host.Errors));
            setter.Set(3);
            Sequence([3.0], value.Value, "the target once the source heals");
            host.Close();
        }),
        Case("registries.record.contributions-an-objects-contribution-is-in-the-table-from-live-until-it-fails-again-once-retried-until-it-ends", async () =>
        {
            var host = new RegistryHost();
            host.Load(("@test/app", facet => facet.Serve(RegistriesNames, RegistriesListing<object?>())));
            // storeRestartable's attempts: each contributes `attempt <n>`; fail() fails the live one, retry() starts the next.
            var attempt = host.Activate("@test/app", "app.store", self => self.Contribute(RegistriesNames, "attempt 1"));
            var names = await host.RegistryAsync(RegistriesNames);
            Sequence(["@test/app:app.store=attempt 1"], names.Value, "the first attempt");
            attempt.Fail();
            Sequence([], names.Value, "after its failure");
            host.Activate("@test/app", "app.store", self => self.Contribute(RegistriesNames, "attempt 2"));
            Sequence(["@test/app:app.store=attempt 2"], names.Value, "the retried attempt");
            host.Close();
            // Closed: nothing composes any more; a value a reader keeps reads the last composition.
            Sequence(["@test/app:app.store=attempt 2"], names.Value, "after the close");
        }),
        Case("registries.record.the-last-good-composition-decision-5-is-dropped-when-a-contribution-it-was-built-from-leaves-value-throws-until-compose-succeeds", async () =>
        {
            var count = Registry.Define<double, double>("app.count", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(("@test/app", facet => facet.Serve(count, new RegistryProvider<double, double>
            {
                Compose = (table, _) => table.Count == 0 ? throw new InvalidOperationException("no contributor") : table[0].Value,
            })));
            var attempt = host.Activate("@test/app", "app.store", self => self.Contribute(count, 1.0));
            var value = await host.RegistryAsync(count);
            Equal(1.0, value.Value, "the first attempt's");
            // Its contributor failed: the last good value was built from its contribution (a native may have closed with it).
            attempt.Fail();
            Equal("no contributor", Throws<InvalidOperationException>(() => _ = value.Value, "the value").Message, "the error");
            host.Activate("@test/app", "app.store", self => self.Contribute(count, 2.0));
            Equal(2.0, value.Value, "the retried attempt's");
            host.Close();
        }),
        Case("registries.record.the-tables-memory-keeps-nothing-of-a-closed-host-records-tables-and-followed-contributions-go-with-it", async () =>
        {
            await ExpectCollected(RegistriesClosedHost());
        }),
        Case("registries.record.a-reloads-new-contribution-adapter-fix-round-1-remaps-an-unchanged-contributor-with-the-new-providers-adapter", async () =>
        {
            var registry = Registry.Define<double, double>("astra.contrib", new() { Version = 2 });
            var old = Registry.Define<double, double>("astra.contrib", new() { Version = 1 });
            void Provider(RegistryFacet facet, double factor) => facet.Serve(registry, new RegistryProvider<double, double>
            {
                Compose = (entries, _) => entries.Sum(entry => entry.Value),
                Accepts = new() { [1] = new() { Contribution = n => (double)n! * factor, Value = n => n } },
            });
            var host = new RegistryHost();
            host.Load(("@astra/provider", facet => Provider(facet, 2)), ("@astra/contribution", facet => facet.Contribute(old, 7.0)));
            var value = await host.RegistryAsync(registry);
            Equal(14.0, value.Value, "the first provider's");
            host.Reload("@astra/provider", facet => Provider(facet, 3));
            var actual = value.Value;
            host.Close();
            Equal(21.0, actual, "the reloaded provider's");
        }),
        Case("registries.record.pisharp-a-contribution-that-is-no-value-of-the-providers-type-is-left-out-contribution-failed", async () =>
        {
            // PiSharp: JavaScript has no casts; a token of another type for the same version gives a value of another type.
            var strings = Registry.Define<string, string[]>("app.typed", new() { Version = 1 });
            var numbers = Registry.Define<double, object?>("app.typed", new() { Version = 1 });
            var host = new RegistryHost();
            host.Load(
                ("@app/a", facet =>
                {
                    facet.Serve(strings, new RegistryProvider<string, string[]> { Compose = (table, _) => RegistriesListed(table) });
                    facet.Contribute(strings, "text");
                }),
                ("@app/b", facet => facet.Contribute(numbers, 1.0)));
            Sequence(["@app/a:=text"], (await host.RegistryAsync(strings)).Value, "the table");
            Check(host.Errors.Single() is ContributionFailedError { Plugin: "@app/b", Error: InvalidCastException }, "the report: " + string.Join(", ", host.Errors));
            host.Close();
        }),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RegistriesClosedHost()
    {
        var host = new RegistryHost();
        host.Load(("@app/kept", facet => facet.Serve(RegistriesNames, RegistriesListing<object?>())));
        var doc = host.State.Doc("""{"on":true}""");
        host.Activate("@app/kept", "app.kept", self => self.Contribute(RegistriesNames, self.Derive([doc], values => ChordJson.ToJavaScriptString(((JsonNode)values[0]!)["on"]))));
        var names = host.RegistryAsync(RegistriesNames).GetAwaiter().GetResult();
        Sequence(["@app/kept:app.kept=true"], names.Value, "the table");
        host.Close();
        return [new WeakReference(names), new WeakReference(host), new WeakReference(doc)];
    }

    /// <summary>Upstream's <c>{ old: string }</c>, version 2's contribution.</summary>
    private sealed record RegistriesOld(string Old);

    /// <summary>A plain value with methods of its own, a native watchable's included: not Chord's, so not followed.</summary>
    private sealed class RegistriesHandle : INativeWatchable<string>
    {
        public string Value => "a handle";

        public string Operation() => "a native operation";

        public Task<INativeWatchHandle<string>> WatchAsync(ChordContext context) => throw new InvalidOperationException("not followed");
    }
}
