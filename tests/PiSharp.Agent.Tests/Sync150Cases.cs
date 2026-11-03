// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/validation.ts, packages/ai/src/utils/json-parse.ts and
// packages/agent/src/agent-loop.ts.
using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.Contracts;

// Authored expectations derived by reading the cited Pi 1.5.0 source; nothing here runs upstream code or is an upstream capture.
// The JSON.parse texts are V8's, as Node reports them (the way tools/V8JsonSyntax/gen-goldens.mjs produces CliParity's goldens).
// agent-loop.ts prepareToolCall finds the tool, runs its prepareArguments, then validation.ts validateToolArguments, which first
// throws `Invalid arguments for tool "<name>": <argumentsError>` when the finished call's argument text was malformed
// (json-parse.ts parseToolCallArguments set the error and left the arguments {}). The error becomes the tool result and the tool
// does not run.
internal static class Sync150Cases
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("sync150.tool-invoker-reports-malformed-final-arguments", MalformedArgumentsAreReported),
        ("sync150.tool-invoker-runs-well-formed-and-empty-final-arguments", WellFormedArgumentsRun),
    ];

    private static async Task MalformedArgumentsAreReported()
    {
        foreach (var (text, message) in new[]
        {
            ("{\"path\":\"./file", "Unterminated string in JSON at position 15 (line 1 column 16)"),
            ("[\"./file\"]", "Tool arguments must be a JSON object"),
        })
        {
            var finished = StreamingJson.ParseToolCallArguments(text);
            var error = message + "\n\nReceived arguments:\n" + text;
            Equal(error, finished.ArgumentsError);
            var adapter = new Adapter(); var policy = new Policy();
            var result = await new ToolInvoker([adapter], policy).ExecuteAsync(Invocation(finished), default);
            Check(result.IsError && result.Failure?.Kind == ToolFailureKind.InvalidArguments, "Malformed arguments did not fail as invalid arguments: " + text);
            Equal("Invalid arguments for tool \"edit\": " + error, result.Content.Single().Text);
            Equal(0, adapter.Prepares); Equal(0, adapter.Executions); Equal(0, policy.Decisions);
        }
    }

    private static async Task WellFormedArgumentsRun()
    {
        foreach (var (text, arguments) in new[] { ("{\"path\":\"./file\"}", "{\"path\":\"./file\"}"), ("  ", "{}") })
        {
            var finished = StreamingJson.ParseToolCallArguments(text);
            Check(finished.ArgumentsError is null, "Well-formed arguments reported an error: " + text);
            var adapter = new Adapter(); var policy = new Policy();
            var result = await new ToolInvoker([adapter], policy).ExecuteAsync(Invocation(finished), default);
            Check(!result.IsError, "Well-formed arguments did not run: " + text);
            Equal(1, adapter.Prepares); Equal(1, adapter.Executions); Equal(1, policy.Decisions);
            Equal(arguments, adapter.Prepared!.ToString());
        }
    }

    private static ToolInvocation Invocation(ToolCallArgumentsResult finished)
    {
        var call = finished.AssignTo(new ToolCallContent("call-edit", "edit", JsonData.EmptyObject));
        var message = new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse);
        return new(message, call, 0);
    }

    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "edit";
        public int Prepares, Executions;
        public JsonData? Prepared;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Prepares++; Prepared = invocation.Call.Arguments;
            return ValueTask.FromResult(new PreparedToolAction("edit", "write", PreparedToolActionKind.Path, "/allowed/file", invocation.Call.Arguments, [],
                null, ImmutableDictionary<string, string>.Empty));
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executions++; return ValueTask.FromResult(ToolResult.Success("effect")); }
    }

    private sealed class Policy : IToolActionPolicy
    {
        public int Decisions;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Decisions++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }

    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
