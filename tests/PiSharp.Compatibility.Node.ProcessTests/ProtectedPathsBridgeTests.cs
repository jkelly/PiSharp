using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;
using PiSharp.Tools.Files;

internal static class ProtectedPathsBridgeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private sealed record Context(string Node, string Repo, string Oracle, string Jiti, string Reference, string Runs);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string nodePath,
        string repoRoot, string oracleRoot, string jitiRoot, string referenceRoot, string runParent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetHost); // Root supplies the gate host; this class never launches dotnet.
        var c = new Context(nodePath, repoRoot, oracleRoot, jitiRoot, referenceRoot, runParent);
        yield return ("node-hook.exact-launch-admission-no-input-writes", () => Run(c, Admission));
        yield return ("node-hook.whole-public-loader-real-function-and-atomic-native-publication", () => Run(c, Staging));
        yield return ("node-hook.all-eight-original-request-result-ledgers-and-notifications", () => Run(c, ReferenceRequests));
        yield return ("node-hook.real-native-write-block-and-final-policy-remains-authoritative", () => Run(c, NativeInvoker));
        yield return ("node-hook.nested-native-notification-awaited-before-hook-settlement", () => Run(c, HeldPublication));
        yield return ("node-hook.async-factory-failure-and-unsupported-registration-roll-back", () => Run(c, Rollback));
        yield return ("node-hook.cancellation-joins-source-finally-and-native-ui-context", () => Run(c, Cancellation));
        yield return ("node-hook.owner-replacement-revokes-old-snapshot-and-shares-disposal", () => Run(c, Generation));
        yield return ("node-hook.effect-then-publication-fault-retains-unknown-and-never-retries", () => Run(c, UncertainPublication));
        yield return ("node-hook.active-context-bounds-repeated-calls-and-natural-process-cleanup", () => Run(c, Bounds));
    }
    private static async Task Run(Context context, Func<Fixture, Task> action)
    {
        var fixture = new Fixture(context); Exception? primary = null;
        try { await action(fixture).WaitAsync(Deadline); }
        catch (Exception error) { primary = error; }
        try { await fixture.DisposeAsync().AsTask().WaitAsync(Deadline); }
        catch (Exception cleanup) { if (primary is not null) throw new AggregateException(primary, cleanup); throw; }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private static Task Admission(Fixture f)
    {
        var root = Path.Combine(f.Parent, "never-created");
        Throws<IOException>(() => NodeExtensionWorkerLaunch.ForProtectedPaths(Path.Combine(f.Parent, "missing.exe"), f.C.Repo,
            root, f.C.Oracle, f.C.Jiti, f.C.Reference, 3, 7)); Check(!Path.Exists(root), "Failed launch wrote scratch.");
        Throws<IOException>(() => NodeExtensionWorkerLaunch.ForProtectedPaths(f.C.Node, f.C.Repo,
            Path.Combine(f.C.Oracle, "forbidden-run"), f.C.Oracle, f.C.Jiti, f.C.Reference, 3, 7));
        var wrongReference = Path.Combine(f.Parent, "wrong-reference"); Directory.CreateDirectory(Path.Combine(wrongReference, "compatibility/node"));
        File.WriteAllText(Path.Combine(wrongReference, "compatibility/node/real-extension-reference.plan.json"), "{}\n");
        Throws<IOException>(() => NodeExtensionWorkerLaunch.ForProtectedPaths(f.C.Node, f.C.Repo, root, f.C.Oracle, f.C.Jiti, wrongReference, 3, 7));
        Check(!Path.Exists(root), "Unqualified reference started a worker.");
        var badRepo = Path.Combine(f.Parent, "bad-repo"); Directory.CreateDirectory(Path.Combine(badRepo, "compatibility/node"));
        File.WriteAllText(Path.Combine(badRepo, "compatibility/node/protected-paths-bridge.plan.json"), "{}\n");
        Throws<IOException>(() => NodeExtensionWorkerLaunch.ForProtectedPaths(f.C.Node, badRepo, root, f.C.Oracle, f.C.Jiti, f.C.Reference, 3, 7));
        Check(!Path.Exists(root), "Unpinned plan created scratch."); return Task.CompletedTask;
    }
    private static async Task Staging(Fixture f)
    {
        var activation = f.Activate(NodeHookStartupControl.HoldPublication); await Stage(f.Extension!.InitializationEntered, activation);
        Equal(0, f.Registry.CaptureSnapshot().Registrations.Length); var metadata = f.Extension.SourceLoadReport!.Value;
        Check(metadata.GetProperty("factoryAwaited").GetBoolean() && metadata.GetProperty("sourceFunctionRemainsInNode").GetBoolean(), "Actual public factory proof missing.");
        Equal("9ca66b1f3b1b9a61cc5ddc660f92cca2ea66c4264a1bdb6b00e0da5844399fe2", metadata.GetProperty("source").GetProperty("sha256").GetString());
        Check(metadata.GetProperty("loadedModules").EnumerateArray().Any(row => row.GetProperty("path").GetString() == "upstream/packages/coding-agent/src/core/extensions/loader.ts"), "Whole loader did not load.");
        Check(metadata.GetProperty("loadedModules").EnumerateArray().Any(row => row.GetProperty("path").GetString()!.StartsWith("jiti-root/node_modules/jiti/", StringComparison.Ordinal)), "Official Jiti did not load.");
        f.Extension.ReleaseInitialization(); f.Scope = await activation; Equal(1, f.Registry.CaptureSnapshot().ToolCallHandlers.Length);
        Equal(f.Scope.OwnerGeneration, f.Registry.CaptureSnapshot().ToolCallHandlers[0].OwnerGeneration);
    }
    private static async Task ReferenceRequests(Fixture f)
    {
        await f.Start(); using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.C.Reference, "fixtures/reference/node-real-extensions/input.json")));
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.C.Reference, "fixtures/reference/node-real-extensions/expected.json")));
        var count = 0; var cases = 0;
        foreach (var scenario in input.RootElement.GetProperty("cases").EnumerateArray().Where(row => row.GetProperty("family").GetString() == "tool_call"))
        {
            cases++; f.Ui.Available = scenario.GetProperty("ui").GetString() != "source-no-ui";
            var golden = expected.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("caseId").GetString() == scenario.GetProperty("caseId").GetString());
            foreach (var request in scenario.GetProperty("requests").EnumerateArray())
            {
                count++; var reference = golden.GetProperty("dispatches").EnumerateArray().Single(row => row.GetProperty("requestId").GetString() == request.GetProperty("id").GetString());
                var e = request.GetProperty("event"); var before = f.Ui.Records.Count;
                var result = await f.Dispatch(new(e.GetProperty("toolName").GetString()!, e.GetProperty("toolCallId").GetString()!, JsonData.FromElement(e.GetProperty("input"))));
                var actual = f.Extension!.LastSourceObservation!.Value;
                f.ReferenceObservations.Add(JsonData.Parse("{\"caseId\":" + scenario.GetProperty("caseId").GetRawText() +
                    ",\"request\":" + request.GetRawText() + ",\"qualifiedDispatch\":" + reference.GetRawText() +
                    ",\"actualSourceObservation\":" + actual.GetRawText() + ",\"actualSourceSettlement\":" + f.Extension.LastSourceSettlement + "}"));
                JsonEqual(reference.GetProperty("outcome").GetProperty("returned"), actual.GetProperty("returned"));
                using var supplied = JsonDocument.Parse(reference.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString()!);
                var eventBytes = supplied.RootElement.GetProperty("event").GetRawText();
                Equal(eventBytes, actual.GetProperty("inputBefore").GetProperty("serializedJson").GetString());
                Equal(eventBytes, actual.GetProperty("inputAfter").GetProperty("serializedJson").GetString());
                Check(!result.ArgumentsReplaced, "Unchanged source arguments became a native mutation.");
                var returned = reference.GetProperty("outcome").GetProperty("returned");
                if (returned.GetProperty("rootType").GetString() == "undefined") Check(result.Decision is null, "Undefined became JSON null or a decision.");
                else Equal(returned.GetProperty("serializedJson").GetString(), result.Decision!.ToString());
                var notices = reference.GetProperty("trace").EnumerateArray().Where(row => row.GetProperty("kind").GetString() == "ui.notify")
                    .Select(row => row.GetProperty("value").GetProperty("serializedJson").GetString()!).ToArray();
                Equal(notices.Length, f.Ui.Records.Count - before);
                for (var n = 0; n < notices.Length; n++) { using var notice = JsonDocument.Parse(notices[n]);
                    Equal(notice.RootElement[0].GetString(), f.Ui.Records[before + n].Message); Equal(notice.RootElement[1].GetString(), f.Ui.Records[before + n].Kind!.Value.ToString().ToLowerInvariant()); }
                Equal(notices.Length, actual.GetProperty("notifications").GetArrayLength());
                for (var n = 0; n < notices.Length; n++) Equal(notices[n], actual.GetProperty("notifications")[n].GetRawText());
            }
        }
        Equal(2, cases); Equal(8, count); Equal(0, f.Extension!.ActiveBrokerContexts);
    }
    private static async Task NativeInvoker(Fixture f)
    {
        await f.Start(); var policy = new Policy(); var tools = new ReadWriteTools(f.Workspace, f.Workspace);
        var binding = new ExtensionAgentBinding(f.Registry, policy, static (_, _, _) => throw new InvalidOperationException("No extension tools are declared in this profile."));
        var invoker = ToolInvoker.WithPreparedHooks(tools.Adapters, policy, binding.PreparedHooks!);
        var blocked = await invoker.ExecuteAsync(Invocation("write", "{\"path\":\".env\",\"content\":\"must-never-write\"}"), CancellationToken.None);
        Equal(ToolFailureKind.Blocked, blocked.Failure!.Kind); Equal(0, policy.Calls); Check(!File.Exists(Path.Combine(f.Workspace, ".env")), "Real native write executed after source block.");
        Equal(1, f.Ui.Records.Count); var allowed = await invoker.ExecuteAsync(Invocation("write", "{\"path\":\"ordinary.txt\",\"content\":\"one real write\"}"), CancellationToken.None);
        Check(!allowed.IsError, "Ordinary path did not reach the real native adapter."); Equal(1, policy.Calls); Equal("one real write", await File.ReadAllTextAsync(Path.Combine(f.Workspace, "ordinary.txt")));
        policy.Allow = false; var denied = await invoker.ExecuteAsync(Invocation("write", "{\"path\":\"denied.txt\",\"content\":\"denied\"}"), CancellationToken.None);
        Equal(ToolFailureKind.Blocked, denied.Failure!.Kind); Equal(2, policy.Calls); Check(!File.Exists(Path.Combine(f.Workspace, "denied.txt")), "Source continuation bypassed policy.");
        var invalid = await invoker.ExecuteAsync(Invocation("write", "{\"path\":\"ordinary.txt\"}"), CancellationToken.None);
        Equal(ToolFailureKind.InvalidArguments, invalid.Failure!.Kind); Equal(2, policy.Calls);
    }
    private static async Task HeldPublication(Fixture f)
    {
        await f.Start(); f.Ui.Hold = true; var callback = f.Dispatch(Protected()); await Stage(f.Ui.Entered.Task, callback);
        Check(!callback.IsCompleted && f.Extension!.ActiveBrokerContexts == 1, "Hook detached before actual native publication.");
        Equal(1, f.Extension!.WorkerSnapshot!.ActiveCallbacks); Equal(0, f.Ui.Records.Count);
        f.Ui.Release.TrySetResult(); var result = await callback; Check(result.Blocked, "Held callback lost the source decision.");
        Equal(1, f.Ui.Records.Count); Equal(0, f.Extension.ActiveBrokerContexts); Equal("fulfilled", f.Extension.LastSourceSettlement!.Value.GetProperty("status").GetString());
    }
    private static async Task Rollback(Fixture f)
    {
        var activation = f.Activate(NodeHookStartupControl.AuthoredAsyncFailure); await Stage(f.Extension!.InitializationEntered, activation);
        Equal(0, f.Registry.CaptureSnapshot().Registrations.Length); f.Extension.ReleaseInitialization(); await Fails<ExtensionRegistrationException>(activation);
        Equal("loadExtensionFromFactory", f.Extension.SourceLoadReport!.Value.GetProperty("sourceExport").GetString());
        Equal("authored async failure", f.Extension.LastSourceSettlement!.Value.GetProperty("thrown").GetProperty("serializedJson").GetString()!.Trim('"'));
        Equal(0, f.Registry.CaptureSnapshot().Registrations.Length); var ended = await f.Extension.Termination!; Natural(ended);
        var rejected = f.Activate(NodeHookStartupControl.AuthoredUnsupportedRegistration); await Fails<ExtensionRegistrationException>(rejected);
        Equal(0, f.Registry.CaptureSnapshot().Registrations.Length); Natural(await f.Extension!.Termination!);
        Equal("UnsupportedRegistration", f.Extension.SourceLoadReport!.Value.GetProperty("code").GetString());
        Equal("Unsupported registration: commands", f.Extension.SourceLoadReport.Value.GetProperty("message").GetString());
        await f.Start(); Equal(1, f.Registry.CaptureSnapshot().ToolCallHandlers.Length); Check((await f.Dispatch(Protected())).Blocked, "Failed admission damaged later healthy owner.");
    }
    private static async Task Cancellation(Fixture f)
    {
        await f.Start(); f.Ui.Hold = true; using var stop = new CancellationTokenSource();
        var callback = f.Dispatch(Protected(), stop.Token); await Stage(f.Ui.Entered.Task, callback); stop.Cancel();
        await Fails<OperationCanceledException>(callback); Equal(0, f.Extension!.ActiveBrokerContexts); Equal(0, f.Ui.Records.Count);
        Equal("failed", f.Extension.LastSourceSettlement!.Value.GetProperty("status").GetString()); Equal(0, f.Ui.OpenScopes);
    }
    private static async Task Generation(Fixture f)
    {
        await f.Start(); var old = f.Registry.CaptureSnapshot(); var generation = f.Scope!.OwnerGeneration;
        var first = f.Scope.DisposeAsync().AsTask(); var second = f.Scope.DisposeAsync().AsTask(); await Task.WhenAll(first, second);
        Equal(0, f.Registry.CaptureSnapshot().Registrations.Length); Natural(await f.Extension!.Termination!);
        await Fails<ExtensionRegistrationException>(f.Dispatch(Protected(), captured: old));
        await f.Start(); Check(f.Scope!.OwnerGeneration > generation, "Owner generation did not advance.");
        Check((await f.Dispatch(Protected())).Blocked, "Fresh owner did not dispatch."); Equal(2, f.Extensions.Count);
    }
    private static async Task UncertainPublication(Fixture f)
    {
        await f.Start(); f.Ui.ThrowAfterEffect = true;
        var error = await Failure(f.Dispatch(Protected())); Check(ContainsUnknown(error), "Effect-crossing callback was classified as not sent.");
        Equal(1, f.Ui.Records.Count); Equal(0, f.Extension!.ActiveBrokerContexts);
        Equal("failed", f.Extension.LastSourceSettlement!.Value.GetProperty("status").GetString());
        Check(f.Extension.LastSourceSettlement.Value.GetProperty("failure").GetProperty("notifications").GetArrayLength() == 1, "Failure dropped the native/source notification facts.");
        f.Ui.ThrowAfterEffect = false; Check((await f.Dispatch(new("read", "read-after-fault", JsonData.Parse("{\"path\":\".env\"}")))).Decision is null, "Recovery retried a previous callback.");
        Equal(1, f.Ui.Records.Count);
    }
    private static async Task Bounds(Fixture f)
    {
        await f.Start(); f.Ui.Hold = true; var calls = Enumerable.Range(0, 8).Select(n => f.Dispatch(new("write", "bounded-" + n, JsonData.Parse("{\"path\":\".env\"}")))).ToArray();
        await f.Ui.WaitForEntries(8); Equal(8, f.Extension!.ActiveBrokerContexts);
        await Fails<ExtensionEventDispatchException>(f.Dispatch(Protected())); Equal(8, f.Extension.ActiveBrokerContexts);
        f.Ui.Release.TrySetResult(); foreach (var result in await Task.WhenAll(calls)) Check(result.Blocked, "Admitted bounded call lost source result.");
        var priorNotices = f.Ui.Records.Count;
        await Fails<ExtensionEventDispatchException>(f.Dispatch(new("write", "oversized-input", JsonData.Parse("{\"path\":\".env\",\"content\":\"" + new string('x', 262_144) + "\"}"))));
        Equal(priorNotices, f.Ui.Records.Count); Equal(0, f.Extension.ActiveBrokerContexts);
        f.Ui.Hold = false;
        for (var round = 0; round < 8; round++)
        {
            var beforeMixed = f.Ui.Records.Count;
            var mixed = await Task.WhenAll(Enumerable.Range(0, 8).Select(n => f.Dispatch(new(n % 2 == 0 ? "read" : "write",
                "mixed-" + round + "-" + n, JsonData.Parse("{\"path\":\".env\"}")))));
            for (var n = 0; n < mixed.Length; n++)
            {
                Check(!mixed[n].ArgumentsReplaced, "Concurrent immutable source input became a mutation.");
                if (n % 2 == 0) Check(mixed[n].Decision is null && !mixed[n].Blocked, "Allowed callback borrowed another callback's source decision.");
                else { Check(mixed[n].Blocked, "Protected callback borrowed another callback's source continuation.");
                    Equal("Path \".env\" is protected", mixed[n].Decision!.Value.GetProperty("reason").GetString()); }
            }
            Equal(4, f.Ui.Records.Count - beforeMixed); Equal(0, f.Extension.ActiveBrokerContexts);
        }
        for (var n = 0; n < 140; n++) { var result = await f.Dispatch(new("read", "repeat-" + n, JsonData.Parse("{\"path\":\".env\"}"))); Check(result.Decision is null, "Repeated source continuation changed."); }
        Equal(0, f.Extension.ActiveBrokerContexts); Equal(1, f.Extension.WorkerSnapshot!.RegisteredHandles);
        await f.Scope!.DisposeAsync(); var termination = await f.Extension.Termination!; Natural(termination);
        Equal(0, termination.Protocol.PendingCalls); Equal(0, termination.Protocol.ActiveCallbacks); Equal(0, termination.Protocol.PendingWrites);
        Check(f.Extension.SourceFinalizationReport!.Value.GetProperty("immutableInputsVerified").GetBoolean(), "Post-source inventory qualification missing.");
        VerifyFinalInventoryReadAccounting(f);
    }
    private static void VerifyFinalInventoryReadAccounting(Fixture f)
    {
        var final = f.Extension!.SourceFinalizationReport!.Value; var loaded = f.Extension.SourceLoadReport!.Value;
        JsonEqual(loaded.GetProperty("loadedModules"), final.GetProperty("loadedModules"));
        JsonEqual(loaded.GetProperty("sourceReads"), final.GetProperty("sourceReads"));
        Equal(loaded.GetProperty("sourceReadScope").GetString(), final.GetProperty("sourceReadScope").GetString());
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.C.Reference, "fixtures/reference/node-real-extensions/manifest.json")));
        var inventories = final.GetProperty("qualifiedInventories");
        foreach (var phase in new[] { "before", "after" })
        {
            JsonEqual(manifest.RootElement.GetProperty("inputs").GetProperty("base"), inventories.GetProperty(phase).GetProperty("base"));
            JsonEqual(manifest.RootElement.GetProperty("inputs").GetProperty("jiti"), inventories.GetProperty(phase).GetProperty("jiti"));
        }
        var summary = final.GetProperty("inventoryVerificationReads");
        Equal("exact-manifest-bound-read-summary", summary.GetProperty("kind").GetString());
        Check(!summary.GetProperty("rawRowsIncluded").GetBoolean(), "Inventory summary claimed raw rows.");
        Equal(NodeExtensionWorkerLaunch.ReferenceManifestSha256, summary.GetProperty("authority").GetProperty("referenceManifestSha256").GetString());
        Equal(NodeExtensionWorkerLaunch.PlanSha256, summary.GetProperty("authority").GetProperty("bridgePlanSha256").GetString());
        JsonEqual(summary.GetProperty("authority"), inventories.GetProperty("authority"));
        var rows = new SortedDictionary<string, (long Bytes, string Sha)>(StringComparer.Ordinal);
        static bool Code(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ts" or ".js" or ".mjs" or ".cjs" or ".mts" or ".cts";
        void Add(string path, string label)
        {
            if (!Code(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked verifier input.");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!rows.TryAdd(label, (file.Length, Convert.ToHexStringLower(SHA256.HashData(file))))) throw new IOException("Duplicate verifier identity.");
        }
        void Tree(string root, string prefix)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                Add(file, prefix + Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
        Tree(Path.Combine(f.C.Oracle, "upstream"), "upstream/");
        foreach (var package in manifest.RootElement.GetProperty("inputs").GetProperty("base").GetProperty("packages").EnumerateArray())
        {
            var name = package.GetProperty("name").GetString()!; Tree(Path.Combine(f.C.Oracle, "node_modules", name), "node_modules/" + name + "/");
        }
        Tree(Path.Combine(f.C.Jiti, "node_modules/jiti"), "jiti-root/node_modules/jiti/");
        using var own = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.C.Repo, "compatibility/node/protected-paths-bridge.plan.json")));
        foreach (var helper in own.RootElement.GetProperty("helpers").EnumerateArray())
        {
            var relative = helper.GetProperty("path").GetString()!; Add(Path.Combine(f.C.Repo, relative), "bridge/" + relative);
        }
        using var reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.C.Reference, "compatibility/node/real-extension-reference.plan.json")));
        var inspector = reference.RootElement.GetProperty("archiveInspector").GetProperty("path").GetString()!;
        Add(Path.Combine(f.C.Reference, inspector), "reference/" + inspector);
        // verifyJiti also reads its immutable historical setup module to check receipt.setupSha256.
        var setupPin = manifest.RootElement.GetProperty("inputs").GetProperty("harness").EnumerateArray()
            .Single(row => row.GetProperty("path").GetString() == "tools/NodeExtensionReference/prepare-jiti.mjs");
        var setupRelative = setupPin.GetProperty("path").GetString()!;
        Add(Path.Combine(f.C.Reference, setupRelative), "reference/" + setupRelative);
        Equal(setupPin.GetProperty("bytes").GetInt64(), rows["reference/" + setupRelative].Bytes);
        Equal(setupPin.GetProperty("sha256").GetString(), rows["reference/" + setupRelative].Sha);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray(); foreach (var row in rows)
            { writer.WriteStartObject(); writer.WriteString("path", row.Key); writer.WriteNumber("bytes", row.Value.Bytes); writer.WriteString("sha256", row.Value.Sha); writer.WriteEndObject(); }
            writer.WriteEndArray();
        }
        Equal(rows.Count, summary.GetProperty("uniqueFiles").GetInt32());
        Equal(rows.Values.Sum(row => row.Bytes), summary.GetProperty("uniqueFileBytes").GetInt64());
        Equal(output.Length, summary.GetProperty("rowsEncodedBytes").GetInt64());
        Equal(Convert.ToHexStringLower(SHA256.HashData(output.ToArray())), summary.GetProperty("rowsSha256").GetString());
        Check(summary.GetProperty("readCalls").GetInt64() >= rows.Count && summary.GetProperty("observedReadBytes").GetInt64() >= rows.Values.Sum(row => row.Bytes), "Actual verifier read counters lost reads.");
        Check(Encoding.UTF8.GetByteCount(f.Extension.SourceFinalizationReport!.ToString()) <= 524_288, "Final report enlarged the retained cap.");
    }
    private static bool ContainsUnknown(Exception error) => error is WorkerProtocolException { Outcome: WorkerOutcome.Unknown } ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(ContainsUnknown) || error.InnerException is not null && ContainsUnknown(error.InnerException);
    private static ToolInvocation Invocation(string name, string arguments)
    { var call = new ToolCallContent("native-" + name, name, JsonData.Parse(arguments)); return new(new("openai-completions", "offline", "model", 1, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0); }
    private static ExtensionToolCallEvent Protected() => new("write", "protected", JsonData.Parse("{\"path\":\".env\",\"content\":\"not-written\"}"));
    private static void Natural(NodeWorkerTermination ended)
    { Equal(0, ended.ExitCode); Check(ended.HasExited && !ended.KillAttempted && ended.PinsRechecked && ended.Failures.IsEmpty, "Owned worker cleanup was not a joined natural success."); }
    private static async Task Stage(Task stage, Task operation)
    { var first = await Task.WhenAny(stage, operation).WaitAsync(Deadline); if (first == operation && !stage.IsCompletedSuccessfully) await operation; await stage.WaitAsync(Deadline); }
    private static async Task<Exception> Failure(Task task) { try { await task; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected failure."); }
    private static async Task Fails<T>(Task task) where T : Exception { var failure = await Failure(task); if (failure is not T) throw new InvalidOperationException("Unexpected failure type: " + failure.GetType().Name, failure); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected admission failure."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void JsonEqual(JsonElement expected, JsonElement actual)
    {
        Equal(expected.ValueKind, actual.ValueKind); if (expected.ValueKind == JsonValueKind.Object) { var left = expected.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(); var right = actual.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(); Equal(left.Length, right.Length);
            for (var n = 0; n < left.Length; n++) { Equal(left[n].Name, right[n].Name); JsonEqual(left[n].Value, right[n].Value); } }
        else if (expected.ValueKind == JsonValueKind.Array) { Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var n = 0; n < expected.GetArrayLength(); n++) JsonEqual(expected[n], actual[n]); }
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString()); else Equal(expected.GetRawText(), actual.GetRawText());
    }
    private sealed class Policy : IToolActionPolicy
    { internal int Calls; internal bool Allow = true; public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(new ToolActionAuthorization(Allow)); } }
    private sealed class Fixture : IAsyncDisposable
    {
        internal Context C { get; } internal string Parent { get; } internal string Workspace { get; }
        internal readonly NativeUi Ui = new(); internal ExtensionRegistry Registry { get; }
        internal NodeToolCallExtension? Extension; internal RegistrationScope? Scope;
        internal readonly List<NodeToolCallExtension> Extensions = [];
        internal readonly List<JsonData> ReferenceObservations = [];
        private int sequence;
        internal Fixture(Context context)
        {
            C = context; var root = Path.GetFullPath(context.Runs); if (!Path.IsPathFullyQualified(context.Runs) || !Directory.Exists(root)) throw new IOException("Existing explicit run parent required.");
            foreach (var protectedRoot in new[] { context.Repo, context.Oracle, context.Jiti, context.Reference, Path.GetDirectoryName(context.Node)! })
                if (Within(root, protectedRoot) || Within(protectedRoot, root)) throw new IOException("Test output overlaps immutable input.");
            NoLinks(root); Parent = Path.Combine(root, "pisharp-real-hook-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(Parent)) throw new IOException("Existing fixture output preserved."); Directory.CreateDirectory(Parent);
            Workspace = Path.Combine(Parent, "workspace"); Directory.CreateDirectory(Workspace); Registry = new(uiProvider: Ui);
        }
        internal Task<RegistrationScope> Activate(NodeHookStartupControl control = NodeHookStartupControl.None)
        {
            var launch = NodeExtensionWorkerLaunch.ForProtectedPaths(C.Node, C.Repo, Path.Combine(Parent, "worker-" + ++sequence), C.Oracle, C.Jiti, C.Reference, sequence, 7);
            Extension = new(launch, control); Extensions.Add(Extension); return Registry.ActivateAsync("real-protected-paths", Extension);
        }
        internal async Task Start() { Scope = await Activate(); }
        internal Task<ExtensionToolCallReduction> Dispatch(ExtensionToolCallEvent input, CancellationToken token = default, ExtensionRegistrySnapshot? captured = null) =>
            new RegisteredExtensionEventDispatcher(Registry, static _ => { }).DispatchToolCallAsync(captured ?? Registry.CaptureSnapshot(), input, token).AsTask();
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Registry.DisposeAsync(); foreach (var extension in Extensions) { await extension.DisposeAsync(); if (extension.Termination is { } termination) Natural(await termination); }
                Check(Ui.OpenScopes == 0, "Native UI scopes did not join.");
            }
            finally { PersistReceipts(); }
            // Receipts, exact input/output ledgers and task-local native effect files remain owned by the gate caller.
        }
        private void PersistReceipts()
        {
            NoLinks(Parent); var records = ReferenceObservations.Select(r => r.ToString()).ToArray();
            if (records.Length > 8 || records.Sum(r => Encoding.UTF8.GetByteCount(r)) > 4_194_304) throw new IOException("Test observation receipt limit.");
            WriteNew("source-request-replays.json", "[" + string.Join(',', records) + "]\n");
            var report = Extensions.Select(extension => new {
                sourceLoad = extension.SourceLoadReport?.Value, sourceLastObservation = extension.LastSourceObservation?.Value,
                sourceLastSettlement = extension.LastSourceSettlement?.Value, sourceFinalization = extension.SourceFinalizationReport?.Value,
                activeNativeContexts = extension.ActiveBrokerContexts,
                termination = extension.Termination is { IsCompletedSuccessfully: true } done ? done.Result : null
            }).ToArray();
            WriteNew("bridge.receipt.json", JsonSerializer.Serialize(new { sourceReplayCount = records.Length,
                nativeNotifications = Ui.Records, nativeUiScopes = Ui.OpenScopes, workers = report }) + "\n");
        }
        private void WriteNew(string name, string text)
        {
            var path = Path.Combine(Parent, name); NoLinks(path); var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 8_388_608) throw new IOException("Bridge receipt byte limit.");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); file.Write(bytes);
        }
        private static bool Within(string a, string b) { a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)); b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)); return StringComparer.OrdinalIgnoreCase.Equals(a, b) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        private static void NoLinks(string path) { for (var p = path; p is not null; p = Path.GetDirectoryName(p)) if (Path.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked fixture path."); }
    }
    // Actual native callback with a bounded owned output, controllable awaited publication, and leased cleanup.
    // This host is authored; it makes no claim of an RPC/TUI renderer or source UI client parity.
    private sealed class NativeUi : IExtensionUiProvider
    {
        internal bool Available = true, Hold, ThrowAfterEffect; internal int OpenScopes, Entries;
        internal readonly List<ExtensionUiNotify> Records = []; private readonly MemoryStream output = new(); private readonly object gate = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IExtensionUiScope OpenScope(IExtensionContext context) { Interlocked.Increment(ref OpenScopes); return new Scope(this, context, Available); }
        internal async Task WaitForEntries(int count) { for (;;) { Task signal; lock (gate) { if (Entries >= count) return; signal = changed.Task; } await signal.WaitAsync(Deadline); } }
        private sealed class Scope(NativeUi owner, IExtensionContext context, bool available) : IExtensionUiScope
        {
            private int closed;
            public ExtensionUiCapabilities Capabilities { get; } = new(ExtensionUiMode.Rpc, 3, 7, available ? [ExtensionUiFeature.Notify] : []);
            public async ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken token = default)
            {
                if (Volatile.Read(ref closed) != 0) return ExtensionUiOutcome<ExtensionUiPublication>.Unavailable(ExtensionUiUnavailableReason.StaleContext);
                if (!available || notification is not ExtensionUiNotify notice) return ExtensionUiOutcome<ExtensionUiPublication>.Unavailable(ExtensionUiUnavailableReason.UnsupportedCapability);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, context.OperationCancellationToken, context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
                lock (owner.gate) { owner.Entries++; owner.Entered.TrySetResult(); owner.changed.TrySetResult(); owner.changed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
                if (owner.Hold) await owner.Release.Task.WaitAsync(stop.Token); stop.Token.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { message = notice.Message, kind = notice.Kind?.ToString() }) + "\n");
                lock (owner.gate) { if (owner.output.Length + bytes.Length > 65_536) throw new IOException("Native UI output limit."); owner.output.Write(bytes); owner.output.Flush(); owner.Records.Add(notice); }
                if (owner.ThrowAfterEffect) throw new IOException("Authored publication failed after its real output effect."); return ExtensionUiOutcome<ExtensionUiPublication>.FromValue(ExtensionUiPublication.Published);
            }
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Text();
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(ExtensionUiOutcome<bool>.Unavailable(ExtensionUiUnavailableReason.UnsupportedCapability));
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Text();
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default) => Text();
            private static ValueTask<ExtensionUiOutcome<string>> Text() => ValueTask.FromResult(ExtensionUiOutcome<string>.Unavailable(ExtensionUiUnavailableReason.UnsupportedCapability));
            public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref closed, 1) == 0) Interlocked.Decrement(ref owner.OpenScopes); return ValueTask.CompletedTask; }
        }
    }
}
