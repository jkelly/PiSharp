// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/agent-loop.ts.
using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;

// agent-loop.ts keeps whatever a tool returns: a JavaScript string may hold a lone surrogate, and the tool result message, its events and
// the session line carry it (JSON.stringify writes it as a lowercase escape). The Pi entry's invoker (KeepsLoneSurrogates) accepts such a
// result text, its details (values and object names) and arguments whose object names hold one; without the option they stay refused.
internal static class ToolLoneSurrogateTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("tool results and argument names keep lone surrogates when the invoker keeps them (Pi entry)", KeptInPiEntry);
        yield return ("tool results with lone surrogates stay refused without the Pi option", RefusedOtherwise);
    }

    private static readonly JsonData Details = JsonData.Parse("""{"note":"d\ud800","k\udc00":["\udfff"]}""");
    private static readonly JsonData Arguments = JsonData.Parse("""{"path":"a\ud800","k\udc00":1}""");

    private static async Task KeptInPiEntry()
    {
        var options = new ToolInvokerOptions { KeepsLoneSurrogates = true };
        var adapter = new Adapter { Result = new ToolResult([new TextContent("x\ud800y")], Details) };
        var invocation = Invocation(Arguments);
        var result = await new ToolInvoker([adapter], new Policy(), options: options).ExecuteAsync(invocation, default);
        Check(!result.IsError, "A result holding lone surrogates was refused: " + result.Failure?.Message);
        Equal("x\ud800y", ((TextContent)result.Content.Single()).Text);
        Equal(Details.ToString(), result.Details.ToString());
        Check(ReferenceEquals(Arguments, adapter.Executed.Single().Arguments), "Argument data with a lone-surrogate name was rewritten.");
        var limits = ToolResultValueOptions.ExecutionBoundary with { KeepsLoneSurrogates = true };
        var message = ToolResultMessageMaterializer.Create(new ToolOutcome(invocation, result), limits);
        var entry = ToolResultMessageMaterializer.ToTranscript(message, 5);
        var line = entry.WireBody.ToString();
        Check(line.Contains("\"content\":[{\"type\":\"text\",\"text\":\"x\\ud800y\"}]", StringComparison.Ordinal) &&
            line.Contains("\"details\":{\"note\":\"d\\ud800\",\"k\\udc00\":[\"\\udfff\"]}", StringComparison.Ordinal), "Tool result message: " + line);
    }

    private static async Task RefusedOtherwise()
    {
        var adapter = new Adapter { Result = ToolResult.Success("x\ud800y") };
        var result = await new ToolInvoker([adapter], new Policy()).ExecuteAsync(Invocation(JsonData.Parse("""{"path":"a"}""")), default);
        Check(result.IsError, "A native (non-Pi) invoker admitted a lone surrogate result.");
    }

    private static ToolInvocation Invocation(JsonData arguments)
    {
        var call = new ToolCallContent("call-1", "data", arguments);
        var assistant = new AssistantMessage("anthropic-messages", "anthropic", "m", 1, [call], TokenUsage.Zero, StopReason.ToolUse);
        return new(assistant, call, 0);
    }
    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "data";
        internal ToolResult Result = ToolResult.Success("ok");
        internal readonly List<PreparedToolAction> Executed = [];
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(
            Name, "invoke", PreparedToolActionKind.Extension, "owner/1/data", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) { Executed.Add(action); return ValueTask.FromResult(Result); }
    }
    private sealed class Policy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
