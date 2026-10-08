using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Export;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Tools.Files;

internal static class RpcHtmlExportTests
{
    internal const string Prefix = "rpc.export-html.";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("html-fixture", "openai-responses", "fixture");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "actual-policy-write-full-captured-forest-selected-leaf-and-correlated-path", Success),
        (Prefix + "strict-grammar-explicit-path-host-and-denied-write-no-effects", Admission),
        (Prefix + "response-renderer-and-existing-writer-bounds-before-effects", Bounds),
        (Prefix + "caller-cancellation-joins-original-write-and-mutation-queue", Cancellation),
        (Prefix + "EOF-joins-original-write-and-keeps-borrowed-owner-usable", Eof),
        (Prefix + "replacement-cancels-captured-write-without-exporting-new-session", Replacement),
        (Prefix + "immutable-export-does-not-lock-or-follow-later-session-appends", Captured),
        (Prefix + "write-error-does-not-claim-rollback-and-owner-can-continue", Failure)
    ];
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Ok(JsonElement response) => Check(response.GetProperty("success").GetBoolean(), response.GetRawText());
    private static void Error(JsonElement response, string message) => Check(!response.GetProperty("success").GetBoolean() &&
        response.GetProperty("error").GetString() == message && !response.TryGetProperty("data", out _), response.GetRawText());
    private static SessionEntry Entry(string id, string? parent, object message) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = "2026-10-01T00:00:00.000Z", message }));
    private static async Task Success()
    {
        await using var f = await Fixture.Create(); var source = await SourceBytes(f); var snapshot = f.Session.Snapshot;
        var response = await f.Export("export"); Ok(response);
        Check(response.GetProperty("command").GetString() == "export_html" && response.GetProperty("data").EnumerateObject().Select(value => value.Name).SequenceEqual(new[] { "path" }) &&
            response.GetProperty("data").GetProperty("path").GetString() == f.Destination && f.Records.Length == 1, "Export response added archive payload, metadata or synthetic events.");
        var bytes = await OutputBytes(f.Destination); var html = Encoding.UTF8.GetString(bytes);
        var system = new SessionSystemReplay().Replay(snapshot.Context.Messages);
        // Pi's page: the upstream template with the captured log as base64 session data (exportSessionToHtml), system theme by default.
        var expected = SessionHtmlExport.GenerateHtml(SessionHtmlExport.BuildSessionData(
            SessionExportSource.FromLog(snapshot.Log.Header, snapshot.Log.Entries, snapshot.Context.LeafId, f.Session.Path),
            new(system.Prompt, system.Tools.Select(tool => SessionExportTool.FromJson(tool.ToString())).ToList()), null), new PiThemeHost(), null);
        Check(bytes.SequenceEqual(new UTF8Encoding(false).GetBytes(expected)), "Export bytes differ from Pi's page for the captured snapshot.");
        Check(!bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) && html.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal),
            "Export lost standalone UTF8 without BOM.");
        var data = SessionData(html);
        Check(data.GetProperty("header").GetProperty("id").GetString() == "source" && data.GetProperty("leafId").GetString() == "selected",
            "Export lost the captured header or selected leaf.");
        var entries = data.GetProperty("entries").EnumerateArray().ToArray();
        Check(entries.Any(entry => entry.GetProperty("message").GetProperty("content").ValueKind == JsonValueKind.String &&
            entry.GetProperty("message").GetProperty("content").GetString() == "selected </script> \U0001F9ED"),
            "Selected transcript lost hostile text or the compass Unicode scalar.");
        Check(!html.Contains("selected </script>", StringComparison.Ordinal), "Hostile text escaped the base64 session data.");
        Check(entries.Any(entry => entry.GetProperty("message").GetProperty("content").ValueKind == JsonValueKind.String &&
            entry.GetProperty("message").GetProperty("content").GetString() == "sibling text"), "Export lost the non-selected forest branch.");
        Check(data.GetProperty("systemPrompt").GetString() == "captured system" && data.GetProperty("tools")[0].GetProperty("name").GetString() == "lookup" &&
            data.GetProperty("tools")[0].GetProperty("description").GetString() == "captured tool" && !data.TryGetProperty("renderedTools", out _),
            "Export lost recorded system prompt or tool definitions.");
        var action = f.Policy.Actions.Single();
        Check(action.ToolName == "write" && action.Operation == "write" && action.Kind == PreparedToolActionKind.Path && action.Target == f.Destination &&
            action.Arguments.Value.GetProperty("content").GetString() == html && f.Operations.Writes == 1 && f.Transport.Calls == 0 && f.Session.Snapshot.Context.LeafId == "selected",
            "RPC bypassed existing final write policy, invoked a model or replaced selected branch.");
        var after = await SourceBytes(f); Check(source.SequenceEqual(after) && f.Session.Snapshot.Log.Sequence == snapshot.Log.Sequence &&
            f.Files.MutationSnapshot.RegisteredOperations == 0, "Read-only export mutated source or left original writer queue active.");
    }
    /// <summary>The page's base64 session data (template.html script#session-data) as JSON.</summary>
    private static JsonElement SessionData(string html)
    {
        const string opening = "<script id=\"session-data\" type=\"application/json\">"; var start = html.IndexOf(opening, StringComparison.Ordinal);
        Check(start >= 0, "Missing session data script."); start += opening.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        Check(end >= start, "Unclosed session data script.");
        return JsonData.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(html[start..end]))).Value;
    }
    private static async Task Admission()
    {
        await using var f = await Fixture.Create(); var before = await SourceBytes(f);
        foreach (var command in new object[] { new { id = "number", type = "export_html", outputPath = 42 },
            new { id = "same-source", type = "export_html", outputPath = f.Source }, new { id = "same-source-relative", type = "export_html", outputPath = "source.jsonl" } })
            Check(!(await f.Send(command)).GetProperty("success").GetBoolean(), "Malformed or source-overwriting export admitted.");
        Check(f.Policy.Actions.Count == 0 && f.Operations.Writes == 0 && f.Operations.Directories == 0 && !File.Exists(f.Destination), "Refused export acquired filesystem effects.");
        // Pi: no, null or empty outputPath means pi-session-<basename>.html and a relative path resolves against the working directory;
        // both still reach only the existing final write policy, which grants nothing outside the destination.
        foreach (var (command, target) in new (object, string)[] { (new { id = "null", type = "export_html", outputPath = (string?)null }, "pi-session-source.html"),
            (new { id = "missing", type = "export_html" }, "pi-session-source.html"), (new { id = "empty", type = "export_html", outputPath = "" }, "pi-session-source.html"),
            (new { id = "relative", type = "export_html", outputPath = "ambient.html" }, "ambient.html") })
        {
            var actions = f.Policy.Actions.Count;
            Error(await f.Send(command), "HTML export write was denied.");
            Check(f.Policy.Actions.Count == actions + 1 && f.Policy.Actions[^1].Target == Path.Combine(f.Root, target) && f.Operations.Writes == 0 &&
                !File.Exists(Path.Combine(f.Root, target)), "Default or relative export path was not resolved against the working directory.");
        }
        f.Policy.Destination = Path.Combine(f.Root, "pi-session-source.html");
        var defaulted = await f.Send(new { id = "default", type = "export_html" }); Ok(defaulted);
        Check(defaulted.GetProperty("data").GetProperty("path").GetString() == "pi-session-source.html" && File.Exists(f.Policy.Destination),
            "Default export did not return Pi's relative default name.");
        File.Delete(f.Policy.Destination); f.Policy.Destination = f.Destination; f.Policy.Actions.Clear();
        var writes = f.Operations.Writes; var directories = f.Operations.Directories;
        f.Policy.Allow = false; Error(await f.Export("denied"), "HTML export write was denied.");
        Check(f.Policy.Actions.Count == 1 && f.Operations.Writes == writes && f.Operations.Directories == directories, "Denied final write was executed.");
        f.Policy.Allow = true; Ok(await f.Export("retry"));
        var after = await SourceBytes(f); Check(before.SequenceEqual(after) && f.Transport.Calls == 0, "Admission retry changed source or inferred.");
        await using var unbound = await Fixture.Create(host: false);
        Error(await unbound.Export("unbound"), "HTML export requires an explicitly installed policy-mediated write invoker.");
        Check(!File.Exists(unbound.Destination) && unbound.Policy.Actions.Count == 0, "Default dispatcher installed an implicit write grant.");
        foreach (var mode in new[] { SessionStorageMode.InMemory, SessionStorageMode.LazyLocal })
        {
            await using var pending = await Fixture.Create(storageMode: mode);
            Error(await pending.Export("pending"), mode == SessionStorageMode.InMemory ? "Cannot export in-memory session to HTML" : "Nothing to export yet - start a conversation first");
            Check(pending.Policy.Actions.Count == 0 && pending.Operations.Writes == 0 && !File.Exists(pending.Source) && !File.Exists(pending.Destination),
                "Export materialized an in-memory/lazy source or acquired output authority.");
        }
    }
    private static async Task Bounds()
    {
        await using (var f = await Fixture.Create(options: new(MaximumOutputBytes: 256)))
        {
            var response = await f.Send(new { id = "budget", type = "export_html", outputPath = Path.Combine(f.Root, new string('x', 400) + ".html") });
            Check(!response.GetProperty("success").GetBoolean() && f.Policy.Actions.Count == 0 && f.Operations.Writes == 0 && f.Operations.Directories == 0,
                "Oversized complete path response reached write admission/effects.");
        }
        await using (var f = await Fixture.Create(writeLimit: 1))
        { var result = await f.Export("write-limit"); Check(!result.GetProperty("success").GetBoolean() && f.Policy.Actions.Count == 0 && f.Operations.Writes == 0 && !File.Exists(f.Destination), "Existing writer content bounds were bypassed."); }
    }
    private static async Task Cancellation()
    {
        await using var f = await Fixture.Create(); f.Operations.Hold = true; using var cancellation = new CancellationTokenSource();
        var original = f.Export("cancel", cancellation.Token);
        try { await f.Operations.Entered.Task.WaitAsync(Bound); cancellation.Cancel(); await f.Operations.Canceled.Task.WaitAsync(Bound);
            Check(!original.IsCompleted && f.Operations.Active == 1, "Caller cancellation abandoned original write/cleanup."); }
        finally { f.Operations.Release.TrySetResult(); await original; }
        Error(await original, "HTML export write canceled; output may have changed.");
        Check(f.Operations.Joined.Task.IsCompletedSuccessfully && f.Operations.Active == 0 && !File.Exists(f.Destination) && f.Files.MutationSnapshot.RegisteredOperations == 0,
            "Canceled original did not settle existing write queue.");
        f.Operations.Hold = false; Ok(await f.Export("after-cancel"));
    }
    private static async Task Eof()
    {
        await using var f = await Fixture.Create(); f.Operations.Hold = true;
        var input = JsonSerializer.Serialize(new { id = "eof", type = "export_html", outputPath = f.Destination }) + "\n";
        await using var reader = new JsonlReader(new MemoryStream(Encoding.UTF8.GetBytes(input)), ownership: JsonlStreamOwnership.Owned);
        var original = f.Dispatcher.RunAsync(reader);
        try { await f.Operations.Entered.Task.WaitAsync(Bound); await f.Operations.Canceled.Task.WaitAsync(Bound);
            Check(!original.IsCompleted && f.Operations.Active == 1, "EOF abandoned admitted original write."); }
        finally { f.Operations.Release.TrySetResult(); await original; }
        Check(f.Operations.Joined.Task.IsCompletedSuccessfully && !File.Exists(f.Destination) && !f.Session.Snapshot.IsDisposed && f.Files.MutationSnapshot.RegisteredOperations == 0,
            "EOF failed to join or disposed borrowed owner.");
        await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "after-export-eof", 1, JsonData.EmptyObject));
    }
    private static async Task Replacement()
    {
        await using var f = await Fixture.Create(); f.Operations.Hold = true; var old = f.Owner.Current;
        var original = f.Export("retired");
        try { await f.Operations.Entered.Task.WaitAsync(Bound); Ok(await f.Send(new { id = "switch", type = "switch_session", sessionPath = f.Other }));
            await f.Operations.Canceled.Task.WaitAsync(Bound); Check(!original.IsCompleted && old.Session.Snapshot.IsRetired && f.Session.Snapshot.Log.Header.Id == "other", "Replacement rebound or detached captured write."); }
        finally { f.Operations.Release.TrySetResult(); await original; }
        Error(await original, "HTML export write canceled; output may have changed.");
        Check(!File.Exists(f.Destination) && f.Operations.Active == 0 && f.Session.Snapshot.Context.LeafId == "selected", "Retired export claimed new session output or failed to join.");
        f.Operations.Hold = false; Ok(await f.Export("new")); var bytes = await OutputBytes(f.Destination);
        Check(SessionData(Encoding.UTF8.GetString(bytes)).GetProperty("header").GetProperty("id").GetString() == "other", "Subsequent export did not use current captured header.");
    }
    private static async Task Captured()
    {
        await using var f = await Fixture.Create(); f.Operations.Hold = true; var before = f.Session.Snapshot.Log; var original = f.Export("captured");
        try { await f.Operations.Entered.Task.WaitAsync(Bound); await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "after-capture-marker", 1, JsonData.EmptyObject));
            Check(!original.IsCompleted && f.Session.Snapshot.Log.Entries.Length == before.Entries.Length + 1, "Read-only export reserved live session mutation or abandoned write."); }
        finally { f.Operations.Release.TrySetResult(); await original; }
        Ok(await original); var bytes = await OutputBytes(f.Destination);
        Check(!SessionData(Encoding.UTF8.GetString(bytes)).GetRawText().Contains("after-capture-marker", StringComparison.Ordinal) && f.Policy.Actions.Count == 1 && f.Transport.Calls == 0,
            "Export followed later append rather than immutable capture.");
    }
    private static async Task Failure()
    {
        await using var f = await Fixture.Create(); f.Operations.FailAfterWrite = true; var before = await SourceBytes(f);
        Error(await f.Export("failed"), "HTML export write failed; output may have changed.");
        Check(File.Exists(f.Destination) && f.Operations.Joined.Task.IsCompletedSuccessfully && f.Files.MutationSnapshot.RegisteredOperations == 0 && f.Session.Snapshot.Fault is null,
            "Export error promised rollback, detached write or poisoned source owner.");
        f.Operations.FailAfterWrite = false; Ok(await f.Export("continuation")); var after = await SourceBytes(f);
        Check(before.SequenceEqual(after) && f.Operations.Writes == 2 && f.Transport.Calls == 0, "Failed export contaminated source or borrowed invoker continuation.");
    }
    private static async Task<byte[]> OutputBytes(string path)
    {
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(reader.Length is >= 0 and <= 1_048_576, "Unbounded authored output read."); var bytes = new byte[checked((int)reader.Length)];
        await reader.ReadExactlyAsync(bytes); Check(reader.Position == bytes.Length && reader.Length == bytes.Length, "Output read crossed mutation."); return bytes;
    }
    private static async Task<byte[]> SourceBytes(Fixture f)
    {
        var before = f.Session.Snapshot; Check(!before.Agent.IsRunning && !before.IsProcessingOperation && !before.IsCompacting && !before.IsAppendingExtensionEntry && before.Fault is null,
            "Source read lacks idle acknowledged state."); var bytes = await OutputBytes(f.Session.Path); var after = f.Session.Snapshot;
        Check(bytes.Length == before.Log.CommittedByteLength && after.Log.Sequence == before.Log.Sequence && after.Log.LeafId == before.Log.LeafId &&
            after.Log.CommittedByteLength == before.Log.CommittedByteLength && after.Fault is null, "Source read crossed acknowledged mutation."); return bytes;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PiSharp-rpc-html-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl"); internal string Destination => Path.Combine(Root, "export.html");
        internal readonly Operations Operations = new(); internal readonly Policy Policy = new(); internal readonly Transport Transport = new();
        private SessionStorageBackend? backend;
        internal ReadWriteTools Files = null!; internal ReplaceableAgentSession Owner = null!; internal RpcSessionDispatcher Dispatcher = null!;
        internal PersistentAgentSession Session => Owner.Current.Session; private readonly Capture output = new();
        internal JsonElement[] Records => Encoding.UTF8.GetString(output.Bytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => JsonData.Parse(value).Value).ToArray();
        private Task<PersistentAgentSession> Open(string path) => PersistentAgentSession.OpenAsync(path, new(Model, Transport, []), () => 123, () => "unused",
            new(UseLatestLeaf: false, SelectedLeafId: backend?.Mode == SessionStorageMode.LazyLocal ? null : "selected", SessionLogStoreOptions: new(StorageFactory: backend)));
        internal static async Task<Fixture> Create(bool host = true, RpcDispatchOptions? options = null, SessionHtmlExportHost? htmlExport = null, int writeLimit = 1_048_576, SessionStorageMode? storageMode = null)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root); f.Policy.Destination = f.Destination;
            if (storageMode is { } mode) f.backend = new(f.Root, mode);
            foreach (var path in new[] { f.Source, f.Other })
            {
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-01T00:00:00.000Z", cwd = f.Root }));
                await using var store = await SessionLogStore.CreateNewAsync(path, header, new(StorageFactory: f.backend));
                if (storageMode != SessionStorageMode.LazyLocal) await store.AppendAsync([Entry("system", null, new { role = "system", content = "captured system", timestamp = 123,
                    toolsAdded = new[] { new { name = "lookup", description = "captured tool", parameters = new { type = "object" } } } }),
                    Entry("selected", "system", new { role = "user", content = "selected </script> \U0001F9ED", timestamp = 123 }),
                    Entry("sibling", "system", new { role = "user", content = "sibling text", timestamp = 123 })]);
            }
            var session = await f.Open(f.Source); f.Owner = new(session, (request, _) => f.Open(request.Path));
            f.Files = new(f.Root, f.Root, f.Operations, new(MaximumWriteBytes: writeLimit, MaximumArgumentCharacters: 2_097_152));
            var invoker = f.Files.CreateInvoker(f.Policy);
            var wire = JsonData.Parse(JsonSerializer.Serialize(new { id = Model.Id, api = Model.Api, provider = Model.Provider, name = Model.Id, baseUrl = "https://offline.invalid", reasoning = false,
                input = new[] { "text" }, contextWindow = 32768, maxTokens = 1024, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } }));
            try { f.Dispatcher = new(session, new JsonlWriter(f.output, ownership: JsonlStreamOwnership.Borrowed), () => 123, [new(Model, wire)], options,
                RpcSessionOwnership.Borrowed, sessionOwner: f.Owner, exportHtmlWriter: host ? invoker : null,
                htmlExport: htmlExport ?? new(new PiThemeHost(), WorkingDirectory: f.Root)); return f; }
            catch { await f.Owner.DisposeAsync(); f.output.Dispose(); throw; }
        }
        internal Task<JsonElement> Export(string id, CancellationToken token = default) => Send(new { id, type = "export_html", outputPath = Destination }, token);
        internal async Task<JsonElement> Send(object command, CancellationToken token = default)
        { var raw = JsonData.Parse(JsonSerializer.Serialize(command)); await Dispatcher.SubmitAsync(raw, token); var id = raw.Value.GetProperty("id").GetString();
            return Records.Single(value => value.GetProperty("type").GetString() == "response" && value.TryGetProperty("id", out var identity) && identity.GetString() == id); }
        public async ValueTask DisposeAsync()
        { Operations.Release.TrySetResult(); try { await Dispatcher.DisposeAsync(); } finally { await Owner.DisposeAsync(); output.Dispose(); }
            Check(Operations.Active == 0 && Files.MutationSnapshot.RegisteredOperations == 0 && (backend is null || backend.ActiveWriterCount == 0), "Original export I/O/queue/backend did not settle."); }
    }
    private sealed class Capture : MemoryStream
    {
        private readonly object gate = new(); internal byte[] Bytes() { lock (gate) return ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); lock (gate) Write(bytes.Span); return ValueTask.CompletedTask; }
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal bool Allow = true; internal string Destination = ""; internal readonly List<PreparedToolAction> Actions = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Actions.Add(action); return ValueTask.FromResult(new ToolActionAuthorization(Allow && action.Target == Destination && action.ToolName == "write" && action.Operation == "write")); }
    }
    private sealed class Operations : IFileOperations
    {
        private readonly LocalFileOperations local = new(); internal bool Hold, FailAfterWrite; internal int Writes, Directories, Active;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => local.ExistsAsync(path, token);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => local.CanonicalizeAsync(path, token);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximumBytes, CancellationToken token) => local.ReadAsync(path, maximumBytes, token);
        public async ValueTask CreateDirectoryAsync(string path, CancellationToken token) { Directories++; await local.CreateDirectoryAsync(path, token); }
        public async ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            Writes++; Active++;
            try { if (Hold) { using var canceled = token.UnsafeRegister(_ => Canceled.TrySetResult(), null); Entered.TrySetResult(); await Release.Task; }
                token.ThrowIfCancellationRequested(); await local.WriteAsync(path, bytes, token); if (FailAfterWrite) throw new IOException("authored post-write failure"); }
            finally { Active--; Joined.TrySetResult(); }
        }
    }
    private sealed class Transport : IChatTransport
    {
        internal int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; Calls++; token.ThrowIfCancellationRequested(); var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [], TokenUsage.Zero, StopReason.Stop); yield return new StreamStarted(message with { StopReason = StopReason.Pending }); yield return new StreamDone(StopReason.Stop, message); }
    }
}
