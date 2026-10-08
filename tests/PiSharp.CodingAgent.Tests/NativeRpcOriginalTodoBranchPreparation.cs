using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;

// Source-only preparation for the coordinator's real profile factory. These are
// four literal offline Responses turns consumed by the profile's injected handler.
// Preparation invokes the actual Agent twice; it never writes transcript entries,
// navigates a tree, dispatches session_tree, launches a worker or contacts a provider.
internal static class NativeRpcOriginalTodoBranchPreparation
{
    internal const string Alpha = "Actual RPC original Todo A";
    internal const string Beta = "Actual RPC original Todo B";
    internal const string FirstPrompt = "Add actual RPC Todo A";
    internal const string SecondPrompt = "Add actual RPC Todo B";
    internal sealed record Evidence(string TargetEntryId, string CurrentLeafId,
        string FirstToolCallId, string SecondToolCallId, JsonData FirstState, JsonData CurrentState,
        int ProfileRequests, int GrantedActions, JsonData[] SourceToolReceipts);
    internal sealed record OriginalEvidence(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly ConcurrentQueue<OriginalEvidence> originals = new();
    internal static OriginalEvidence[] CapturedOriginals => originals.ToArray();

    internal static ImmutableArray<JsonData> Turns() =>
    [ Encode(Tool("rpc-todo-alpha", Alpha, FirstPrompt)), Encode(Text("Acknowledged actual A", "Added todo #1: " + Alpha)),
      Encode(Tool("rpc-todo-beta", Beta, SecondPrompt)), Encode(Text("Acknowledged actual B", "Added todo #2: " + Beta)) ];

    internal static async Task<Evidence> PrepareAsync(OfflineSessionProfile profile, NodeCommandInputExtension originalTodo,
        ExtensionRegistry registry, RegistrationScope scope, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(originalTodo);
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(scope);
        var owner = profile.Sessions ?? throw new IOException("Actual attached profile owner required for Todo preparation.");
        var attachment = owner.Current; var session = attachment.Session; owner.ValidateAttachment(attachment);
        Check(Equals(profile.SelectedModel, OfflineSessionProfile.Model) && profile.UsedTurns == 0 && profile.ScriptTurns == 4,
            "Preparation needs the fresh four-turn offline Responses profile.");
        Check(!session.Snapshot.Context.Ancestry.Any(entry => IsTodo(entry.WireBody)), "Fresh actual engine must have no prior Todo result.");
        Check(registry.CaptureSnapshot().Tools.Any(tool => tool.Name == "todo" && tool.OwnerId == scope.OwnerId && tool.OwnerGeneration == scope.OwnerGeneration),
            "Original Todo must be admitted on the actual native owner.");
        var sourceOffset = originalTodo.SourceOperations.Length;
        await Own(session.PromptAsync(Prompt(FirstPrompt), token), "actual-profile-first-todo-prompt");
        owner.ValidateAttachment(attachment);
        var first = session.Snapshot.Context.Ancestry.Single(entry => IsTodo(entry.WireBody));
        var firstMessage = first.WireBody.Value.GetProperty("message"); var firstState = State(firstMessage.GetProperty("details"));
        Check(firstState.Value.GetProperty("todos").GetArrayLength() == 1 &&
            firstState.Value.GetProperty("todos")[0].GetProperty("text").GetString() == Alpha && firstState.Value.GetProperty("nextId").GetInt32() == 2,
            "First acknowledged source Todo state absent.");
        await Own(session.PromptAsync(Prompt(SecondPrompt), token), "actual-profile-second-todo-prompt");
        owner.ValidateAttachment(attachment);
        var state = session.Snapshot; var results = state.Context.Ancestry.Where(entry => IsTodo(entry.WireBody)).ToArray();
        Check(results.Length == 2 && results[0].Id == first.Id && state.Context.LeafId is not null && state.Context.LeafId != first.Id &&
            !state.Agent.IsRunning && state.Fault is null && !state.IsDisposed && !state.IsRetired, "Distinct actual current ancestry was not acknowledged.");
        var second = results[1]; var secondMessage = second.WireBody.Value.GetProperty("message"); var currentState = State(secondMessage.GetProperty("details"));
        Check(currentState.Value.GetProperty("todos").GetArrayLength() == 2 &&
            currentState.Value.GetProperty("todos")[0].GetProperty("text").GetString() == Alpha &&
            currentState.Value.GetProperty("todos")[1].GetProperty("text").GetString() == Beta && currentState.Value.GetProperty("nextId").GetInt32() == 3,
            "Second acknowledged source Todo state absent.");
        Check(state.Log.Entries.Any(entry => entry.Id == first.Id) && state.Log.Entries.Any(entry => entry.Id == second.Id),
            "Both Todo snapshots must be actual acknowledged log entries.");
        var actions = Encode(profile.Actions).Value;
        Check(actions.GetArrayLength() == 2 && actions.EnumerateArray().All(action =>
            action.GetProperty("ToolName").GetString() == "todo" && action.GetProperty("allowed").GetBoolean()),
            "Preparation requires two actually granted native tool calls.");
        var source = originalTodo.SourceOperations.Skip(sourceOffset).Where(row =>
            row.Value.TryGetProperty("kind", out var kind) && kind.GetString() == "tool").ToArray();
        Check(profile.UsedTurns == 4 && source.Length == 2 && source.All(row => row.Value.GetProperty("status").GetString() == "fulfilled" &&
            row.Value.GetProperty("publicationJoined").GetBoolean() && row.Value.GetProperty("updateDeliveryJoined").GetBoolean()) &&
            originalTodo.ActiveContexts == 0 && originalTodo.WorkerSnapshot is { ActiveCallbacks: 0, PendingCalls: 0 },
            "Genuine source calls/profile acknowledgements did not settle.");
        var firstCall = firstMessage.GetProperty("toolCallId").GetString()!; var secondCall = secondMessage.GetProperty("toolCallId").GetString()!;
        Check(source[0].Value.GetProperty("context").GetProperty("actualNativeToolCallId").GetString() == firstCall &&
            source[1].Value.GetProperty("context").GetProperty("actualNativeToolCallId").GetString() == secondCall,
            "Source receipts must retain the actual acknowledged native invocation IDs.");
        return new(first.Id!, state.Context.LeafId!, firstCall, secondCall, firstState, currentState,
            profile.UsedTurns, actions.GetArrayLength(), source);

        async Task Own(Task original, string phase)
        {
            AggregateException? aggregate = null; Exception? direct = null;
            try { await original.ConfigureAwait(false); }
            catch (Exception error) { direct = error; aggregate = original.IsFaulted ? original.Exception : null; throw; }
            finally { originals.Enqueue(new(phase, original, aggregate, direct)); }
        }
    }
    private static TranscriptEntry Prompt(string text) => new("user", Encode(new
        { role = "user", content = new[] { new { type = "text", text } }, timestamp = 123 }));
    private static bool IsTodo(JsonData entry) => entry.Value.TryGetProperty("message", out var message) &&
        message.TryGetProperty("role", out var role) && role.GetString() == "toolResult" && message.TryGetProperty("toolName", out var name) &&
        name.GetString() == "todo" && message.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.False;
    private static JsonData State(JsonElement details) => Encode(new { todos = details.GetProperty("todos"), nextId = details.GetProperty("nextId") });
    private static JsonData Encode<T>(T value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    private static object Tool(string call, string text, string prompt) => new
    {
        requiredInputTexts = new[] { prompt }, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name = "todo", arguments = "" } },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name = "todo", arguments = JsonSerializer.Serialize(new { action = "add", text }) } }, Completed()
        }
    };
    private static object Text(string text, string requiredResult) => new
    {
        requiredInputTexts = new[] { requiredResult }, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg-final", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = "msg-final", delta = text },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg-final", content = new[] { new { type = "output_text", text } } } }, Completed()
        }
    };
    private static object Completed() => new { type = "response.completed", response = new
        { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
}
