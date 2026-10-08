using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent.Tests;

// Genuine session/registry engines with synthetic admitted input and offline transport; authored only.
internal static class ContextExistingSessionActionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("context-actions.existing-model-durable-and-unknown", () => Exercise(false));
        yield return ("context-actions.existing-input-original-close-join", () => Exercise(true));
    }
    private static void Check(bool condition) { if (!condition) throw new IOException("Existing session action control failed."); }
    private static async Task Exercise(bool heldInput)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pisharp-existing-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var transport = new NoTransport(); var admission = new HeldInput();
        var faults = new List<Exception>(); PersistentAgentSession? session = null; ReplaceableAgentSession? owner = null;
        ExtensionRegistry? registry = null; ExtensionProviderRegistrationHost? providers = null;
        RegistrationScope? scope = null; Task? commandOriginal = null, closeOriginal = null;
        try
        {
            var first = new ModelDescriptor("first", "openai-responses", "offline");
            var second = new ModelDescriptor("second", "openai-responses", "offline");
            var runtime = new SessionRuntimeRegistry([new(first, transport), new(second, transport)], [], new Deny());
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "existing-actions", timestamp = "2026-01-01T00:00:00Z", cwd = folder }));
            var sequence = 0;
            session = await PersistentAgentSession.CreateAsync(Path.Combine(folder, "session.jsonl"), header,
                runtime, first, () => 1, () => "entry-" + ++sequence);
            var active = session;
            owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new NotSupportedException()));
            var reads = new NativeExtensionContextFacadeHost(); reads.Attach(owner);
            var effects = new NativeExistingSessionRegistrationActions(reads, () => admission);
            var views = new NativeSessionSnapshotProvider(); views.Attach(owner);
            registry = new ExtensionRegistry(null, null, views, new NativeExtensionRegistrationFacadeHost(reads, effects));
            providers = new ExtensionProviderRegistrationHost(registry, [], new UnavailableConfiguration());
            var command = ExtensionRegistrationCommands.Create("existing-actions", "existing-actions", "existing engine", providers,
                async (_, actions, _, _) =>
                {
                    if (heldInput)
                    {
                        _ = actions.SendUserMessageAsync(JsonData.Parse("\"literal input\""),
                            new(ExtensionMessageDelivery.FollowUp, false));
                    }
                    else
                    {
                        Check(await actions.SetModelAsync(second));
                        Check(active.Snapshot.Agent.Model == second && active.Snapshot.Context.Model?.ModelId == second.Id);
                        var entries = active.Snapshot.Log.Entries.Length;
                        Check(!await actions.SetModelAsync(new("unknown", "openai-responses", "offline")));
                        Check(active.Snapshot.Agent.Model == second && active.Snapshot.Log.Entries.Length == entries);
                    }
                });
            scope = await registry.ActivateAsync("existing-actions-owner", new Plugin(command));
            commandOriginal = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "existing-actions", JsonData.EmptyObject).AsTask();
            if (heldInput)
            {
                var completed = await Task.WhenAny(admission.Entered.Task, commandOriginal);
                if (ReferenceEquals(completed, commandOriginal)) { await commandOriginal; throw new IOException("Original input did not enter."); }
                await admission.Entered.Task;
                Check(active.Snapshot.IsAdmittingInput && !commandOriginal.IsCompleted && admission.Calls == 1);
                Check(admission.Input is { Text: "literal input", Source: PromptInputSource.Extension, StreamingBehavior: null });
                closeOriginal = scope.DisposeAsync().AsTask(); Check(!closeOriginal.IsCompleted);
                admission.Release.SetResult();
                try { await commandOriginal; throw new IOException("Owner-close command unexpectedly succeeded."); }
                catch (OperationCanceledException error) when (commandOriginal.IsCanceled && scope.ExtensionLifetimeCancellationToken.IsCancellationRequested)
                { Check(error.CancellationToken.IsCancellationRequested); }
                await closeOriginal;
                Check(!active.Snapshot.IsAdmittingInput && transport.Calls == 0 && closeOriginal.IsCompletedSuccessfully);
            }
            else
            {
                await commandOriginal; Check(commandOriginal.IsCompletedSuccessfully && transport.Calls == 0 && admission.Calls == 0);
                try { _ = effects.SendUserMessageAsync(JsonData.Parse("\"requires template policy\""), new(ExpandPromptTemplates: true), default); throw new IOException("Unadmitted template policy was invented."); }
                catch (NotSupportedException) { }
                Check(admission.Calls == 0);
            }
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            admission.Release.TrySetResult();
            if (commandOriginal is not null) await Join(commandOriginal, faults, closeOriginal is not null);
            if (scope is not null) { closeOriginal ??= scope.DisposeAsync().AsTask(); await Join(closeOriginal, faults); }
            providers?.Dispose();
            if (registry is not null) await Join(registry.DisposeAsync().AsTask(), faults);
            if (owner is not null) await Join(owner.DisposeAsync().AsTask(), faults);
            else if (session is not null) await Join(session.DisposeAsync().AsTask(), faults);
            try { Directory.Delete(folder, recursive: true); } catch (Exception error) { faults.Add(error); }
        }
        if (faults.Count > 0) throw new AggregateException("Actual engine/control/cleanup originals.", faults);
    }
    private static async Task Join(Task original, List<Exception> faults, bool allowOwnedCancellation = false)
    {
        try { await original; }
        catch (OperationCanceledException error) when (allowOwnedCancellation && original.IsCanceled && error.CancellationToken.IsCancellationRequested) { }
        catch (Exception error) { faults.Add(original.Exception ?? error); }
    }
    private sealed class HeldInput : IPromptInputAdmission
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls; internal PromptInput? Input;
        public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        { Calls++; Input = input; Entered.SetResult(); await Release.Task; return new(PromptInputAction.Handled); }
    }
    private sealed class Plugin(ExtensionCommandDescriptor command) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { registry.RegisterCommand(command); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class UnavailableConfiguration : IExtensionProviderConfigurationAdapter
    { public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => throw new NotSupportedException(); }
    private sealed class Deny : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class NoTransport : IChatTransport
    {
        internal int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new IOException("Existing model/input admission controls must not invoke a provider.")); yield break; }
    }
}
