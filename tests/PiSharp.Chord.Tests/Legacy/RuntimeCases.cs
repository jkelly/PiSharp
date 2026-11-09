// Authored native ports of v1.5.0 test/{contract,state-init,host-lifetime,principals,reload,generations}.test.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Legacy;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> LegacyRuntimeCases()
    {
        yield return Case("legacy.runtime.contract-description-normalization", () =>
        {
            var input = Obj(("session", Str()), ("conversation", Num(@default: 0)));
            var contract = LegacyContract.Remote("t.chat", 1, input, LegacyPlacement.Family("session:", "session"), new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>>
            { ["viewer"] = new Dictionary<string, LegacyMember> { ["view"] = LegacyMember.State(Obj(("text", Str()))) } });
            var normalized = LegacySchema.Normalize(input, JsonNode.Parse("{\"session\":\"s\",\"extra\":1}"), "input");
            Json("{\"session\":\"s\",\"conversation\":0}", normalized, "defaults + clean");
            Equal("{\"session\":\"s\"}", LegacySchema.Identity(input, normalized), "default omitted from identity");
            Equal("session:s", contract.Placement!.Resolve(contract.Name, normalized), "family placement");
            Equal(contract.Fingerprint, LegacyContract.FromDescription(contract.Description).Fingerprint, "roundtrip fingerprint");
            Equal(16, contract.Fingerprint.Length, "legacy fingerprint length");
            Equal("invalid", Throws<ChordException>(() => LegacySchema.Normalize(input, new JsonObject { ["session"] = 1 }, "input"), "input rejects").Code, "input error code");
        });
        yield return Case("legacy.runtime.contract-coverage-and-opaque", () =>
        {
            var a = LegacyContract.Local("method", 1, Obj(), new Dictionary<string, LegacyMember> { ["go"] = LegacyMember.Method(null, Num()), ["native"] = LegacyMember.Opaque("h@1") });
            var b = LegacyContract.Local("method", 1, Obj(), new Dictionary<string, LegacyMember> { ["go"] = LegacyMember.Method(), ["native"] = LegacyMember.Opaque("h@1") });
            Equal<string?>(null, LegacyContract.Coverage(a, b), "void consumer accepts result");
            Check(LegacyContract.Coverage(b, a)!.Contains("result"), "result consumer needs provider result");
            Throws<ArgumentException>(() => LegacyContract.Local("reserved", 1, Obj(), new Dictionary<string, LegacyMember> { ["ready"] = LegacyMember.Method() }), "reserved member");
            Throws<ArgumentException>(() => LegacyMember.Opaque("missing-version"), "opaque version");
        });
        yield return Case("legacy.runtime.local-call-state-native-disposal", async () =>
        {
            var events = new List<string>();
            await using var host = LegacyTestHost(); var type = LegacyCounter();
            await host.LoadAsync([LegacyCounterFacet(type, events: events)]);
            var reference = await host.GetAsync(type, new JsonObject());
            Equal("live", reference.Status, "live"); Equal(0, reference.Member("count").Value!.GetValue<int>(), "initial");
            var seen = new List<int>(); using var subscription = reference.Member("count").Subscribe(x => seen.Add(x!.GetValue<int>()));
            await reference.CallAsync("add", JsonValue.Create(3));
            Equal(3, reference.Member("count").Value!.GetValue<int>(), "changed");
            Sequence([0, 3], seen, "state stream");
            Equal("native", (string)reference.Native("native")!, "native exposed after open");
            reference.Release(); await host.CloseAsync();
            Sequence(["open:a", "open:b", "close:b", "close:a"], events, "resource reverse close");
            Equal("released", reference.Status, "release status");
        });
        yield return Case("legacy.runtime.invalid-method-values-and-context-expiry", async () =>
        {
            await using var host = LegacyTestHost(); var type = LegacyCounter(); await host.LoadAsync([LegacyCounterFacet(type)]);
            var reference = await host.GetAsync(type, new JsonObject());
            var argsError = await LegacyError(() => reference.CallAsync("add", JsonValue.Create("bad"))); Equal("args", argsError.Reason, "argument schema");
            var missing = await LegacyError(() => reference.CallAsync("add")); Equal("args", missing.Reason, "argument presence");
            LegacyContext? captured = null; await host.RootAsync(context => { captured = context; return Task.FromResult(0); });
            var expired = await LegacyError(() => reference.CallAsync("read", captured)); Equal("expired", expired.Reason, "expired context");
        });
        yield return Case("legacy.runtime.local-native-method-delegate", async () =>
        {
            var function = new JsonObject { ["type"] = "function", ["parameters"] = new JsonArray(), ["returnType"] = Int() };
            var type = LegacyContract.Local("native-method", 1, Obj(), new Dictionary<string, LegacyMember>
            { ["invoke"] = LegacyMember.Method(function, Int()), ["factory"] = LegacyMember.Method(null, function), ["invalidFactory"] = LegacyMember.Method(null, function) });
            var calls = 0; Func<int> callback = () => { calls++; return 42; };
            await using var host = LegacyTestHost(); host.Serve(type, (_, _, _) => new(new Dictionary<string, object?>
            {
                ["invoke"] = (LegacyNativeMethod)((value, _) => Task.FromResult<object?>(((Func<int>)value!)())),
                ["factory"] = (LegacyNativeMethod)((_, _) => Task.FromResult<object?>(callback)),
                ["invalidFactory"] = (LegacyNativeMethod)((_, _) => Task.FromResult<object?>("not-a-function")),
            }));
            var reference = await host.GetAsync(type, new JsonObject());
            Equal(42, (int)(await reference.CallNativeAsync("invoke", callback))!, "native delegate call");
            Equal(1, calls, "callable validation never invokes delegate");
            Check(ReferenceEquals(callback, await reference.CallNativeAsync("factory", null)), "native function result keeps identity");
            Equal(1, calls, "function result validation never invokes delegate");
            Equal("args", (await LegacyError(() => reference.CallNativeAsync("invoke", "not-a-function"))).Reason, "native args checked");
            Equal("result", (await LegacyError(() => reference.CallNativeAsync("invalidFactory", null))).Reason, "native function result checked");
            Equal("args", Throws<ChordException>(() => LegacySchema.AssertNative(Str(), callback, "args", "string"), "delegate is not a string").Reason, "non-callable schemas remain strict");
            var forbiddenFunction = (JsonObject)function.DeepClone(); forbiddenFunction["not"] = new JsonObject();
            Equal("args", Throws<ChordException>(() => LegacySchema.AssertNative(forbiddenFunction, callback, "args", "forbidden"), "remaining callable constraints").Reason, "callable keeps other schema constraints");
        });
        yield return Case("legacy.runtime.host-lifetime-registry-and-principals", async () =>
        {
            await using var host = LegacyTestHost(); var type = LegacyCounter(); var registry = LegacyRegistry<string>.Define("routes");
            var created = 0;
            await host.LoadAsync([new("plugin", api => api.Serve(type, (_, _, self) => { created++; var count = self.State("n", Int(), JsonValue.Create(0)); self.Register(registry, new Dictionary<string, string> { ["a"] = "route" }); return LegacyCounterImplementation(count, "native"); }, new() { Lifetime = "host" }))]);
            var a = await host.GetAsync(type, new JsonObject(), new() { Principal = "alice" });
            var b = await host.GetAsync(type, new JsonObject(), new() { Principal = "bob" });
            await a.CallAsync("add", JsonValue.Create(1)); Equal(1, b.Member("count").Value!.GetValue<int>(), "host lifetime shared");
            Equal(1, created, "host object created at load once"); Equal("route", host.Registry(registry).Get("a"), "registry contribution");
        });
        yield return Case("legacy.runtime.identity-normalized-defaults", async () =>
        {
            var type = LegacyContract.Local("identity", 1, Obj(("n", Int(@default: 0))), new Dictionary<string, LegacyMember>());
            await using var host = LegacyTestHost(); var created = 0; host.Serve(type, (_, _, _) => { created++; return new(new Dictionary<string, object?>()); });
            var a = await host.GetAsync(type, new JsonObject()); var b = await host.GetAsync(type, new JsonObject { ["n"] = 0, ["dropped"] = 1 });
            Equal(1, created, "normalized default address shares object"); a.Release(); b.Release();
        });
        yield return Case("legacy.runtime.dependency-order-and-cycle", async () =>
        {
            await using var host = LegacyTestHost();
            var a = LegacyContract.Local("a", 1, Obj()); var b = LegacyContract.Local("b", 1, Obj());
            await host.LoadAsync([new("a", api => api.Serve(a, (_, _, self) => { self.Get(b, new JsonObject()); return new(new Dictionary<string, object?>()); }, new() { Lifetime = "host" })), new("b", api => api.Serve(b, (_, _, _) => new(new Dictionary<string, object?>()), new() { Lifetime = "host" }))]);
            var reference = await host.GetAsync(a, new JsonObject()); Equal("live", reference.Status, "dependency declared later activates");
            var x = LegacyContract.Local("x", 1, Obj()); var y = LegacyContract.Local("y", 1, Obj());
            host.Serve(x, (_, _, self) => { self.Get(y, new JsonObject()); return new(new Dictionary<string, object?>()); });
            host.Serve(y, (_, _, self) => { self.Get(x, new JsonObject()); return new(new Dictionary<string, object?>()); });
            var error = await LegacyError(() => host.GetAsync(x, new JsonObject())); Equal("cycle", error.Reason, "cycle refused");
        });
        yield return Case("legacy.runtime.reload-retains-state-and-adopts-code", async () =>
        {
            await using var host = LegacyTestHost(); var type = LegacyCounter(); var opens = new List<string>();
            await host.LoadAsync([LegacyCounterFacet(type, 1, opens)]); var reference = await host.GetAsync(type, new JsonObject());
            await reference.CallAsync("add", JsonValue.Create(4));
            var report = await host.ReloadAsync([LegacyCounterFacet(type, 10, opens)]);
            Equal(2, report.CodeVersion, "generation"); Equal(4, reference.Member("count").Value!.GetValue<int>(), "cell retained");
            await reference.CallAsync("add", JsonValue.Create(1)); Equal(14, reference.Member("count").Value!.GetValue<int>(), "new handler");
            Equal(2, opens.Count, "resources kept");
        });
        yield return Case("legacy.runtime.reload-failure-preserves-generation", async () =>
        {
            await using var host = LegacyTestHost(); var type = LegacyCounter(); await host.LoadAsync([LegacyCounterFacet(type)]); var reference = await host.GetAsync(type, new JsonObject());
            var error = await LegacyError(() => host.ReloadAsync([new("counter", _ => throw ChordException.Invalid("declaration", "bad candidate"))]));
            Equal("declaration", error.Reason, "original failure preserved"); Equal(1, host.CodeVersion, "generation unchanged");
            await reference.CallAsync("add", JsonValue.Create(2)); Equal(2, reference.Member("count").Value!.GetValue<int>(), "old code live");
        });
        yield return Case("legacy.runtime.root-keeps-old-code-until-settlement", async () =>
        {
            await using var host = LegacyTestHost(); var type = LegacyCounter(); await host.LoadAsync([LegacyCounterFacet(type)]);
            var reference = await host.GetAsync(type, new JsonObject());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var root = host.RootAsync(async context => { entered.SetResult(); await proceed.Task; await reference.CallAsync("add", JsonValue.Create(1), context); return true; });
            await entered.Task; await host.ReloadAsync([LegacyCounterFacet(type, 10)]);
            Equal<int?>(1, host.PreviousCodeVersion, "root retains old generation");
            proceed.SetResult(); await root; Equal(1, reference.Member("count").Value!.GetValue<int>(), "old root uses old method");
            await reference.CallAsync("add", JsonValue.Create(1)); Equal(11, reference.Member("count").Value!.GetValue<int>(), "new root uses new method");
            Equal<int?>(null, host.PreviousCodeVersion, "settled root permits retirement");
        });
        yield return Case("legacy.runtime.new-host-object-prepared-before-commit", async () =>
        {
            await using var host = LegacyTestHost(); var type = LegacyContract.Local("added", 1, Obj(), new Dictionary<string, LegacyMember> { ["value"] = LegacyMember.State(Int()) });
            var resources = new List<string>();
            LegacyFacet Facet(bool fail) => new("added", api => api.Serve(type, (_, _, self) =>
            {
                self.Resource("native", new LegacyResourceDeclaration<string> { Open = _ => { resources.Add("open"); return Task.FromResult("resource"); }, Close = _ => { resources.Add("close"); return Task.CompletedTask; } });
                var state = self.State("value", Int(), _ => fail ? Task.FromException<JsonNode?>(ChordException.Invalid("init", "candidate failed")) : Task.FromResult<JsonNode?>(JsonValue.Create(7)));
                return new(new Dictionary<string, object?> { ["value"] = state });
            }, new() { Lifetime = "host" }));
            Equal("init", (await LegacyError(() => host.ReloadAsync([Facet(true)]))).Reason, "new eager object failure aborts reload");
            Equal(1, host.CodeVersion, "failed candidate does not publish");
            await host.ReloadAsync([Facet(false)]); var reference = await host.GetAsync(type, new JsonObject());
            Equal(7, reference.Member("value").Value!.GetValue<int>(), "prepared eager object adopted");
            await host.CloseAsync(); Sequence(["open", "close", "open", "close"], resources, "candidate and installed resource ownership");
        });
        yield return Case("legacy.runtime.derived-validation-rolls-back-write", async () =>
        {
            var type = LegacyContract.Local("derived", 1, Obj(), new Dictionary<string, LegacyMember> { ["n"] = LegacyMember.State(Int()), ["double"] = LegacyMember.State(Int(max: 5)), ["set"] = LegacyMember.Method(Int()) });
            await using var host = LegacyTestHost(); host.Serve(type, (_, _, self) =>
            {
                var n = self.State("n", Int(), JsonValue.Create(1)); var doubled = self.Derive([n], values => JsonValue.Create(values[0]!.GetValue<int>() * 2));
                return new(new Dictionary<string, object?> { ["n"] = n, ["double"] = doubled, ["set"] = (LegacyMethod)((args, context) => { n.Replace(context, args); return Task.FromResult<object?>(null); }) });
            });
            var reference = await host.GetAsync(type, new JsonObject());
            Equal("state", (await LegacyError(() => reference.CallAsync("set", JsonValue.Create(3)))).Reason, "derived validation fails write");
            Equal(1, reference.Member("n").Value!.GetValue<int>(), "source unchanged"); Equal(2, reference.Member("double").Value!.GetValue<int>(), "derived unchanged");
        });
        yield return Case("legacy.runtime.cancel-get-closes-late-resource", async () =>
        {
            await using var host = LegacyTestHost(); var type = LegacyCounter(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var opening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Serve(type, (_, _, self) => { var count = self.State("n", Int(), JsonValue.Create(0)); var resource = self.Resource("slow", new LegacyResourceDeclaration<string> { Open = _ => { started.TrySetResult(); return opening.Task; }, Close = _ => { closed.TrySetResult(); return Task.CompletedTask; } }); return LegacyCounterImplementation(count, self.Expose(resource)); });
            using var cancel = new CancellationTokenSource(); var get = host.GetAsync(type, new JsonObject(), cancellationToken: cancel.Token); await started.Task; cancel.Cancel();
            Equal("aborted", (await LegacyError(() => get)).Code, "get canceled"); opening.TrySetResult("late");
            await host.CloseAsync(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });
        yield return Case("legacy.runtime.remote-real-protocol4-call-state", async () =>
        {
            var pair = LegacyMemoryLink.Pair();
            await using var server = LegacyTestHost("server"); await using var client = LegacyTestHost("client");
            var type = LegacyCounter(remote: true); await server.LoadAsync([LegacyCounterFacet(type)]);
            server.Accept(pair.A, new() { Principal = JsonValue.Create("client"), Kind = "client" }); client.Connect(pair.B, JsonValue.Create("server"));
            var reference = await client.GetAsync(type, new JsonObject()); await reference.CallAsync("add", JsonValue.Create(5));
            for (var i = 0; i < 100 && reference.Member("count").Value!.GetValue<int>() != 5; i++) await Task.Delay(5);
            Equal(5, reference.Member("count").Value!.GetValue<int>(), "protocol4 state replicated");
        });
        yield return Case("legacy.runtime.retirement-waits-for-old-close-and-bounded-drain", async () =>
        {
            var errors = new List<Exception>();
            await using var host = new LegacyHost(new() { Self = "host", TaskStopDeadline = TimeSpan.FromMilliseconds(25), OnError = errors.Add });
            var type = LegacyContract.Local("retiring-resource", 1, Obj(), new Dictionary<string, LegacyMember> { ["read"] = LegacyMember.Method(null, Int()) });
            var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retired = new List<int>(); var allRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Retired += version => { retired.Add(version); if (version == 2) allRetired.TrySetResult(); };
            LegacyFacet Facet(int version) => new("retiring", api => api.Serve(type, (_, _, self) =>
            {
                if (version == 1) self.Resource("old", new LegacyResourceDeclaration<object>
                {
                    Open = _ => Task.FromResult(new object()),
                    Close = async _ => { closeStarted.TrySetResult(); await closeAllowed.Task; },
                });
                return new(new Dictionary<string, object?> { ["read"] = (LegacyMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(version))) });
            }));
            await host.LoadAsync([Facet(1)]); using var reference = await host.GetAsync(type, new JsonObject());
            await host.ReloadAsync([Facet(2)]); await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(2, ((JsonNode)(await reference.CallAsync("read"))!).GetValue<int>(), "new code remains live during old close");
            Equal(0, retired.Count, "native close owns old generation"); Equal(0, host.HighestRetiredCodeVersion, "watermark waits for close");
            var pending = await LegacyError(() => host.ReloadAsync([Facet(3)], new() { RetireDeadline = TimeSpan.FromMilliseconds(25) }));
            Equal("retirement_cleanup", pending.Reason, "next reload has a bounded cleanup wait"); Equal(2, host.CodeVersion, "blocked cleanup does not publish a third generation");
            await host.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Equal(0, retired.Count, "bounded close does not report unfinished cleanup as retired");
            Check(errors.OfType<ChordException>().Any(error => error.Reason == "drain_timeout"), "unfinished close reports its deadline");
            closeAllowed.TrySetResult(); await allRetired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Sequence([1, 2], retired, "retirement notifications remain ordered after late cleanup");
        });
        yield return Case("legacy.runtime.six-reloads-release-native-generation-captures", async () =>
        {
            var generations = new List<WeakReference>(); var nativeClosed = new List<int>();
            var registry = LegacyRegistry<Func<int>>.Define("generation-registry");
            var type = LegacyContract.Local("generation-owner", 1, Obj(), new Dictionary<string, LegacyMember>
            { ["count"] = LegacyMember.State(Int()), ["read"] = LegacyMember.Method(null, Int()) });
            await using var host = LegacyTestHost();
            await host.LoadAsync([LegacyGenerationFacet(type, registry, 0, generations, nativeClosed)]);
            using var reference = await host.GetAsync(type, new JsonObject());
            for (var generation = 1; generation <= 6; generation++)
            {
                await host.ReloadAsync([LegacyGenerationFacet(type, registry, generation, generations, nativeClosed)]);
                Equal(generation, ((JsonNode)(await reference.CallAsync("read"))!).GetValue<int>(), "held reference follows new code");
                await LegacyWaitUntil(() => host.HighestRetiredCodeVersion >= generation);
            }
            await LegacyCollect(generations.Take(6).ToArray());
            Check(generations[6].IsAlive, "current generation remains owned");
            Equal(0, nativeClosed.Count, "kept resource remains open through reloads");
            await host.CloseAsync(); Sequence([6], nativeClosed, "kept resource adopts only the current close delegate");
            await LegacyCollect([generations[6]]); GC.KeepAlive(reference);
        });
    }
    private static LegacyHost LegacyTestHost(string name = "host") => new(new() { Name = name, Kind = name == "client" ? "client" : "host", Self = JsonValue.Create(name), Dev = true, ReadyTimeout = TimeSpan.FromSeconds(2) });
    private static LegacyContract LegacyCounter(bool remote = false)
    {
        var members = new Dictionary<string, LegacyMember> { ["count"] = LegacyMember.State(Int()), ["add"] = LegacyMember.Method(Int()), ["read"] = LegacyMember.Method(null, Int()) };
        if (!remote) { members["native"] = LegacyMember.Opaque("test.native@1"); return LegacyContract.Local("counter", 1, Obj(), members); }
        return LegacyContract.Remote("counter", 1, Obj(), LegacyPlacement.Named("server"), new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>> { ["counter"] = members });
    }
    private static LegacyImplementation LegacyCounterImplementation(LegacyCell count, object native, int multiplier = 1, bool remote = false)
    {
        var members = new Dictionary<string, object?> { ["count"] = count,
            ["add"] = (LegacyMethod)((args, context) => { count.Replace(context, JsonValue.Create(count.Value!.GetValue<int>() + args!.GetValue<int>() * multiplier)); return Task.FromResult<object?>(null); }),
            ["read"] = (LegacyMethod)((_, _) => Task.FromResult<object?>(count.Value)) };
        if (!remote) members["native"] = native;
        return new(members, remote ? "counter" : null);
    }
    private static LegacyFacet LegacyCounterFacet(LegacyContract type, int multiplier = 1, List<string>? events = null) => new("counter", api => api.Serve(type, (_, _, self) =>
    {
        var count = self.State("n", Int(), JsonValue.Create(0));
        var a = self.Resource("a", new LegacyResourceDeclaration<string> { Open = _ => { events?.Add("open:a"); return Task.FromResult("native"); }, Close = _ => { events?.Add("close:a"); return Task.CompletedTask; } });
        self.Resource("b", new LegacyResourceDeclaration<string> { Dependencies = [a], Open = _ => { events?.Add("open:b"); return Task.FromResult("b"); }, Close = _ => { events?.Add("close:b"); return Task.CompletedTask; } });
        return LegacyCounterImplementation(count, self.Expose(a), multiplier, type.Scope == "remote");
    }));
    private static async Task<ChordException> LegacyError(Func<Task> work)
    { try { await work(); } catch (ChordException error) { return error; } throw new InvalidOperationException("Expected legacy ChordException."); }
    private sealed record LegacyGenerationSentinel(int Number);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static LegacyFacet LegacyGenerationFacet(LegacyContract type, LegacyRegistry<Func<int>> registry, int number, List<WeakReference> generations, List<int> nativeClosed)
    {
        var sentinel = new LegacyGenerationSentinel(number); generations.Add(new(sentinel));
        return new("generation", api => api.Serve(type, (_, _, self) =>
        {
            var count = self.State("count", Int(), JsonValue.Create(0));
            self.Resource("kept", new LegacyResourceDeclaration<object> { Open = _ => Task.FromResult(new object()), Close = _ => { nativeClosed.Add(sentinel.Number); return Task.CompletedTask; } });
            self.On("listener", count, (_, _) => GC.KeepAlive(sentinel));
            self.Task(async context =>
            {
                using var registration = context.CancellationToken.Register(() => GC.KeepAlive(sentinel));
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); } catch (OperationCanceledException) { }
                GC.KeepAlive(sentinel);
            });
            self.Register(registry, new Dictionary<string, Func<int>> { ["generation"] = () => sentinel.Number });
            return new(new Dictionary<string, object?>
            {
                ["count"] = count,
                ["read"] = (LegacyMethod)(async (_, context) =>
                {
                    using var registration = context.CancellationToken.Register(() => GC.KeepAlive(sentinel));
                    await Task.Yield(); return JsonValue.Create(sentinel.Number);
                }),
            });
        }, new() { Lifetime = "host" }));
    }
    private static async Task LegacyWaitUntil(Func<bool> ready)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(5);
        Check(ready(), "legacy cleanup did not settle within test deadline");
    }
    private static async Task LegacyCollect(IReadOnlyList<WeakReference> weak)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (weak.Any(reference => reference.IsAlive) && deadline.Elapsed < TimeSpan.FromSeconds(3))
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
        Check(weak.All(reference => !reference.IsAlive), "retired native generation remained strongly reachable");
    }
}
