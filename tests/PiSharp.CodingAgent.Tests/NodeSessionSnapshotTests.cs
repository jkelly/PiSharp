using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

// Actual registry-owned callback contexts; no worker launch, full-tree provider or session effects.
internal static class NodeSessionSnapshotTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [ ("node-session.actual-current-branch-leaf-and-null-snapshot", () => Run(false)),
      ("node-session.actual-callback-held-retirement-and-own-close-refusal", () => Run(true)) ];
    private static void Check(bool condition) { if (!condition) throw new IOException("Node session snapshot control failed."); }
    private sealed class View : IExtensionSessionViewProvider
    {
        internal ExtensionSessionSnapshot? Current;
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => Current;
    }
    private sealed class Extension(Action<IExtensionRegistry> register) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { register(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Record(Task original)
    { internal Task Original = original; internal AggregateException? Aggregate; internal Exception? Direct; internal bool Joined; }
    private static async Task Run(bool held)
    {
        var view = new View(); var registry = new ExtensionRegistry(null, null, view); RegistrationScope? scope = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originals = new List<Record>(); var failures = new List<Exception>(); var captured = new List<Dictionary<string, object?>>();
        var first = JsonData.Parse("{\"type\":\"message\",\"id\":\"a\",\"parentId\":null,\"unknown\":{\"kept\":true},\"message\":{\"role\":\"toolResult\",\"toolName\":\"todo\",\"details\":{\"todos\":[]}}}");
        view.Current = new("actual-session", 1, "a", [first]);
        Task? dispatch = null, closing = null;
        try
        {
            var activation = registry.ActivateAsync("node-session", new Extension(entries =>
            {
                foreach (var topic in new[] { "session_start", "session_tree" })
                    entries.Observe(new(topic, topic, async (observation, context, callbackToken) =>
                    {
                        var payload = new Dictionary<string, object?>(); NodeSessionSnapshotMetadata.AddTo(payload, context); captured.Add(payload);
                        if (held)
                        {
                            var revision = registry.CaptureSnapshot().Revision; var refused = false;
                            try { _ = scope!.DisposeAsync(); }
                            catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { refused = true; }
                            Check(refused && registry.CaptureSnapshot().Revision == revision);
                            entered.TrySetResult(); await release.Task;
                        }
                    }));
            }));
            Track(activation); scope = await activation;
            dispatch = registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "session_start", JsonData.Parse("{\"type\":\"session_start\",\"reason\":\"new\"}")).AsTask(); Track(dispatch);
            if (held)
            {
                var timer = Task.Delay(TimeSpan.FromSeconds(5)); Track(timer);
                Check(ReferenceEquals(await Task.WhenAny(entered.Task, dispatch, timer), entered.Task));
                closing = scope.DisposeAsync().AsTask(); Track(closing); Check(!closing.IsCompleted && !dispatch.IsCompleted);
                release.TrySetResult(); await Observe(dispatch); await Observe(closing);
                Check(captured.Count == 1);
            }
            else
            {
                await Observe(dispatch);
                var payload = captured.Single(); Check((string?)payload["sessionSnapshotPresence"] == "json");
                var wire = JsonData.Parse((string)payload["sessionSnapshotJson"]!).Value;
                Check(wire.GetProperty("sessionId").GetString() == "actual-session" && wire.GetProperty("selectedLeafId").GetString() == "a" &&
                    wire.GetProperty("branchEntries")[0].GetRawText() == first.ToString() && !wire.TryGetProperty("sessionFile", out _));
                view.Current = new("actual-session", 2, null, []);
                var tree = registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "session_tree", JsonData.Parse("{\"type\":\"session_tree\"}")).AsTask(); Track(tree); await Observe(tree);
                wire = JsonData.Parse((string)captured[1]["sessionSnapshotJson"]!).Value;
                Check(wire.GetProperty("generation").GetInt64() == 2 && wire.GetProperty("selectedLeafId").ValueKind == System.Text.Json.JsonValueKind.Null && wire.GetProperty("branchEntries").GetArrayLength() == 0);
                view.Current = null;
                var empty = registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "session_start", JsonData.Parse("{\"type\":\"session_start\"}")).AsTask(); Track(empty); await Observe(empty);
                Check((string?)captured[2]["sessionSnapshotPresence"] == "none" && !captured[2].ContainsKey("sessionSnapshotJson"));
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            release.TrySetResult();
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask());
            Acquire(() => registry.DisposeAsync().AsTask());
            foreach (var record in originals) await Observe(record.Original);
        }
        if (failures.Count != 0) throw new AggregateException("Actual Node session callback and cleanup originals.", failures);
        void Track(Task original) { if (!originals.Any(item => ReferenceEquals(item.Original, original))) originals.Add(new(original)); }
        void Acquire(Func<Task> acquire) { try { Track(acquire()); } catch (Exception error) { failures.Add(error); } }
        async Task Observe(Task original)
        {
            var record = originals.Single(item => ReferenceEquals(item.Original, original)); if (record.Joined) return;
            try { await original; }
            catch (Exception error) { record.Direct = error; record.Aggregate = original.IsFaulted ? original.Exception : null; if (record.Aggregate is not null) failures.Add(record.Aggregate); failures.Add(error); }
            finally { record.Joined = true; }
        }
    }
}
