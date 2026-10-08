using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// Caller supplies two separately admitted worker launches. The in-memory session and Agent
// are genuinely owned here; no prewritten transcript or substitute Todo executor is used.
internal static class NativeOriginalTodoOwnedSetupTests
{
    private sealed class Original(Task task, string phase)
    {
        internal readonly Task Task = task; internal readonly string Phase = phase;
        internal AggregateException? Aggregate; internal Exception? Direct; internal bool Joined;
    }
    private static readonly ConcurrentQueue<Original> captured = new();
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] RawCapturedOriginals =>
        captured.Select(record => (record.Phase, record.Task, record.Aggregate, record.Direct))
            .Concat(NativeOriginalTodoCustomUiTests.RawCapturedOriginals).ToArray();
    private static readonly ModelDescriptor Model = new("todo-offline", "openai-responses", "offline");
    private static void Check(bool value) { if (!value) throw new IOException("Owned original Todo setup control failed."); }

    internal static async Task<NativeOriginalTodoCustomUiTests.Evidence> RunAsync(
        NodeCommandInputWorkerLaunch admittedSetupLaunch, NodeCommandInputWorkerLaunch admittedUiLaunch,
        string workspace, string sessionNamespace)
    {
        ArgumentNullException.ThrowIfNull(admittedSetupLaunch); ArgumentNullException.ThrowIfNull(admittedUiLaunch);
        Check(Path.IsPathFullyQualified(workspace) && Path.IsPathFullyQualified(sessionNamespace));
        const string text = "Actual acknowledged original Todo";
        var arguments = JsonData.Parse(JsonSerializer.Serialize(new { action = "add", text }));
        var backend = new SessionStorageBackend(sessionNamespace, SessionStorageMode.InMemory);
        var sessionViews = new NativeSessionSnapshotProvider();
        var registry = new ExtensionRegistry(null, new UnavailableExtensionUiProvider(), sessionViews);
        var node = new NodeCommandInputExtension(admittedSetupLaunch, workspace, sourcePaths: [NodeTierAAdmission.TodoSource]);
        var ledger = new Dictionary<Task, Original>(ReferenceEqualityComparer.Instance); var errors = new List<Exception>();
        RegistrationScope? scope = null; PersistentAgentSession? session = null; ReplaceableAgentSession? owner = null;
        NativeOriginalTodoCustomUiTests.Evidence? evidence = null;
        try
        {
            var activation = registry.ActivateAsync("owned-original-todo-setup", node);
            await Own(activation, "setup-activation"); scope = activation.GetAwaiter().GetResult();
            var snapshot = registry.CaptureSnapshot(); var todo = snapshot.Tools.Single(tool => tool.Name == "todo");
            Check(snapshot.Tools.Length == 1 && node.SourceLoadReport!.Value.GetProperty("sourceFunctionsRemainInNode").GetBoolean());
            var policy = new ExactTodoPolicy(todo, arguments);
            var binding = new ExtensionAgentBinding(registry, policy, (tool, input, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(tool == todo && input.ToString() == arguments.ToString());
            });
            var transport = new Script(arguments);
            var configuration = new AgentConfiguration(Model, transport, binding.Tools, Hooks: binding.Hooks);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "owned-original-todo", timestamp = "2026-01-01T00:00:00Z", cwd = workspace }));
            var sequence = 0;
            var creation = PersistentAgentSession.CreateAsync(Path.Combine(sessionNamespace, "todo.jsonl"), header,
                configuration, () => 123, () => "todo-entry-" + (++sequence).ToString(CultureInfo.InvariantCulture),
                new() { SessionLogStoreOptions = new(StorageFactory: backend) });
            await Own(creation, "actual-session-creation"); session = creation.GetAwaiter().GetResult();
            owner = new ReplaceableAgentSession(session, (_, _) => throw new NotSupportedException("No replacement was admitted by this control."));
            sessionViews.Attach(owner); var attachment = owner.Current;
            await Own(registry.DispatchObservationsAsync(snapshot, "session_start",
                JsonData.Parse("{\"type\":\"session_start\",\"reason\":\"startup\"}")).AsTask(), "setup-session-start");
            var prompt = session.PromptAsync(new TranscriptEntry("user", JsonData.Parse(
                "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Add the admitted Todo\"}],\"timestamp\":123}")));
            await Own(prompt, "actual-agent-todo-prompt"); owner.ValidateAttachment(attachment);
            Check(policy.Allowed == 1 && transport.Requests == 2 && !session.Snapshot.Agent.IsRunning && session.Snapshot.Fault is null);
            var result = session.Snapshot.Context.Ancestry.Single(entry =>
                entry.WireBody.Value.TryGetProperty("message", out var message) &&
                message.TryGetProperty("role", out var role) && role.GetString() == "toolResult");
            var body = result.WireBody.Value.GetProperty("message");
            Check(body.GetProperty("toolName").GetString() == "todo" && !body.GetProperty("isError").GetBoolean() &&
                body.GetProperty("details").GetProperty("todos")[0].GetProperty("text").GetString() == text);
            Check(session.Snapshot.Log.Entries.Any(entry => entry.Id == result.Id) && backend.ActiveWriterCount == 1);
            Check(transport.AcknowledgedResult && node.ActiveContexts == 0 && node.WorkerSnapshot is { ActiveCallbacks: 0, PendingCalls: 0 });
            // The reviewed control obtains the live ancestry via the actual owner and genuine
            // source SessionManager facade, paints original custom rows, and sends real raw Escape.
            var ui = NativeOriginalTodoCustomUiTests.RunAsync(admittedUiLaunch, workspace, owner, text);
            await Own(ui, "actual-original-todo-custom-ui"); evidence = ui.GetAwaiter().GetResult();
            Check(evidence.SessionId == header.Id && evidence.Generation == attachment.Generation &&
                evidence.LeafId == session.Snapshot.Context.LeafId && evidence.EscapeConsumed);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            // Agent/session stops while its real registry-backed tool capability is still alive.
            if (owner is not null) Acquire(() => owner.DisposeAsync().AsTask(), "actual-owner-close");
            else if (session is not null) Acquire(() => session.DisposeAsync().AsTask(), "actual-session-close");
            await JoinAll();
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask(), "setup-scope-retirement");
            Acquire(() => registry.DisposeAsync().AsTask(), "setup-registry-retirement");
            await JoinAll();
            if (backend.ActiveWriterCount != 0) errors.Add(new IOException("Actual in-memory session writer was not retired."));
        }
        foreach (var record in ledger.Values)
        { if (record.Aggregate is not null) errors.Add(record.Aggregate); if (record.Direct is not null) errors.Add(record.Direct); }
        if (errors.Count != 0) throw new AggregateException("Original Todo setup, UI and owner cleanup originals.", errors);
        return evidence ?? throw new IOException("Original Todo UI evidence absent.");

        Original Track(Task task, string phase)
        {
            if (ledger.TryGetValue(task, out var known)) return known;
            var record = new Original(task, phase); ledger.Add(task, record); captured.Enqueue(record); return record;
        }
        void CaptureOnce(Original record, Exception? direct)
        {
            if (record.Joined) return;
            record.Aggregate = record.Task.IsFaulted ? record.Task.Exception : null;
            record.Direct = direct; record.Joined = true;
        }
        async Task Own(Task task, string phase)
        {
            var record = Track(task, phase);
            try { await task; }
            catch (Exception error) { CaptureOnce(record, error); throw; }
            finally { CaptureOnce(record, null); }
        }
        void Acquire(Func<Task> acquire, string phase)
        { try { Track(acquire(), phase); } catch (Exception error) { errors.Add(error); } }
        async Task JoinAll()
        {
            while (ledger.Values.FirstOrDefault(record => !record.Joined) is { } record)
            {
                try { await record.Task; } catch (Exception error) { CaptureOnce(record, error); }
                finally { CaptureOnce(record, null); }
            }
        }
    }

    private sealed class ExactTodoPolicy(ExtensionToolRegistrationInfo tool, JsonData arguments) : IToolActionPolicy
    {
        internal int Allowed;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var target = tool.OwnerId + "/" + tool.OwnerGeneration.ToString(CultureInfo.InvariantCulture) + "/" + tool.RegistrationId;
            var allow = invocation.Call.Name == "todo" && action.ToolName == "todo" && action.Operation == "invoke" &&
                action.Kind == PreparedToolActionKind.Extension && action.Target == target && action.Arguments.ToString() == arguments.ToString() &&
                action.CommandArguments.IsEmpty && action.Environment.IsEmpty && action.WorkingDirectory is null;
            if (allow) Allowed++; return ValueTask.FromResult(new ToolActionAuthorization(allow));
        }
    }
    private sealed class Script(JsonData arguments) : IChatTransport
    {
        internal int Requests; internal bool AcknowledgedResult;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var first = ++Requests == 1; Check(Requests <= 2);
            if (!first) AcknowledgedResult = request.Messages.Any(message => message.Role == "toolResult" &&
                message.WireBody.Value.GetProperty("toolName").GetString() == "todo" &&
                !message.WireBody.Value.GetProperty("isError").GetBoolean());
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123,
                first ? [new ToolCallContent("actual-todo-add", "todo", arguments)] : [new TextContent("Acknowledged")],
                TokenUsage.Zero, first ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending }); await Task.CompletedTask;
            if (first) { var call = (ToolCallContent)message.Content[0]; yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "Acknowledged"); }
            yield return new StreamDone(message.StopReason, message);
        }
    }
}
