using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Sessions.Serialization;

// Authored actual-engine controls; root owns shared runner registration and execution.
internal static class ContextSessionFacadeHostTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [ ("context-facade.actual-native-engine-binding", ActualNativeEngineBinding) ];
    private static void Check(bool condition) { if (!condition) throw new IOException("Native facade binding control failed."); }
    private static async Task ActualNativeEngineBinding()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pisharp-context-facade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        PersistentAgentSession? session = null; ReplaceableAgentSession? owner = null;
        try
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "facade-session", timestamp = "2026-01-01T00:00:00Z", cwd = folder }));
            var transport = new NoTransport(); var ids = 0;
            var model = new ModelDescriptor("facade-model", "openai-responses", "synthetic-offline");
            session = await PersistentAgentSession.CreateAsync(Path.Combine(folder, "session.jsonl"), header,
                new AgentConfiguration(model, transport, []), () => 1, () => "facade-entry-" + ++ids);
            var activeSession = session;
            owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new NotSupportedException()));
            var appended = await owner.AppendExtensionEntryAsync(owner.Current,
                new("facade-test", "note", 1, JsonData.Parse("{\"opaque\":1e400}")));
            var id = appended.Entry.Id;
            var views = new NativeSessionSnapshotProvider(); views.Attach(owner);
            var host = new NativeExtensionContextFacadeHost(); host.Attach(owner);
            await using var registry = new ExtensionRegistry(null, null, views, host);
            IExtensionCommandFacade? retained = null; var calls = 0;
            var command = ExtensionCommandFacade.CreateCommand("native-facade", "native-facade", "native-facade", async (_, facade, _) =>
            {
                retained = facade; calls++;
                Check(facade.Cwd == folder && facade.IsIdle && !facade.HasPendingMessages && facade.SystemPrompt == "");
                Check(facade.Model!.Value.GetProperty("id").GetString() == model.Id && facade.SessionId == "facade-session");
                var graph = (IExtensionSessionGraphFacade)facade;
                Check(graph.GetEntry(id)!.ToString().Contains("1e400", StringComparison.Ordinal) && graph.LeafId == id);
                Check(graph.GetEntries().Length == activeSession.Snapshot.Log.Entries.Length && graph.GetTree().Value.ValueKind == JsonValueKind.Array);
                Check(graph.GetHeader().Value.GetProperty("cwd").GetString() == folder && graph.SessionFile == activeSession.Path);
                var idleOriginal = ((IExtensionHostActionFacade)facade).WaitForIdleAsync();
                await idleOriginal;
            });
            var nativeOwner = await registry.ActivateAsync("native-facade-owner", new Plugin(command));
            await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "native-facade", JsonData.EmptyObject);
            Check(calls == 1 && transport.Calls == 0);
            try { _ = retained!.Cwd; throw new IOException("Retired facade read succeeded."); }
            catch (InvalidOperationException) { }
            var forged = new Forged(new("facade-session", owner.Current.Generation + 1, null, []));
            try { _ = host.GetCwd(forged); throw new IOException("Wrong attachment generation was accepted."); }
            catch (InvalidOperationException) { }
            try { host.Attach(owner); throw new IOException("Owner attachment repeated."); }
            catch (InvalidOperationException) { }
            await nativeOwner.DisposeAsync();
        }
        finally
        {
            try { if (owner is not null) await owner.DisposeAsync(); else if (session is not null) await session.DisposeAsync(); }
            finally { Directory.Delete(folder, recursive: true); }
        }
    }
    private sealed class Plugin(ExtensionCommandDescriptor command) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
        { registry.RegisterCommand(command); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Forged(ExtensionSessionSnapshot snapshot) : IExtensionSessionContext
    {
        public string OwnerId => "forged"; public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => default;
        public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
        public ExtensionSessionSnapshot? SessionSnapshot => snapshot;
    }
    private sealed class NoTransport : IChatTransport
    {
        internal int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new IOException("Facade reads/idle must not invoke a provider.")); yield break; }
    }
}
