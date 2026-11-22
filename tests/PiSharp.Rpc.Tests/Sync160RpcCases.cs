// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/suite/regressions/12119-copy-after-abort-or-error.test.ts
// over packages/coding-agent/src/core/agent-session.ts (getLastAssistantText) and packages/coding-agent/src/modes/rpc/rpc-mode.ts
// (get_last_assistant_text).
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static partial class RpcSessionDispatcherTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Sync160Cases()
    {
        yield return ("rpc.sync160.last-assistant-text-returns-the-previous-answer-after-an-aborted-tool-call", LastAssistantTextAfterAbortedToolCall);
        yield return ("rpc.sync160.last-assistant-text-returns-the-previous-answer-after-a-provider-error", LastAssistantTextAfterProviderError);
        yield return ("rpc.sync160.last-assistant-text-keeps-a-normal-reply-without-text-as-the-last-reply", LastAssistantTextKeepsNormalReplyWithoutText);
    }

    private static AssistantMessage Empty(StopReason stop) => new(Model.Api, Model.Provider, Model.Id, 456, [], TokenUsage.Zero, stop,
        stop == StopReason.Error ? JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"400 invalid_request_error\"")) : JsonFields.Empty);

    private static async Task<string?> LastAssistantText(Fixture fixture, string id)
    {
        await fixture.Send("get_last_assistant_text", id);
        var data = fixture.Response(id).Value.GetProperty("data");
        return data.TryGetProperty("text", out var text) ? text.GetString() : null;
    }

    /// <summary>Issue #12119: after Esc during a tool call the last assistant messages have no text (a tool-call-only message, and an
    /// empty aborted one when the provider is asked again); the previous answer is the last reply.</summary>
    private static async Task LastAssistantTextAfterAbortedToolCall()
    {
        var toolStarted = Gate();
        var blocking = ProgressTool(async (_, _, token) =>
        {
            toolStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            return ToolResult.Success("tool aborted");
        });
        var transport = new Script([new(Message("Here is the answer.")), new(Message(tool: true)), new(Message("unreachable"))]);
        await using var fixture = await Fixture.Create(transport, tools: [blocking]);
        await fixture.Send("prompt", "first", "first question"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Equal("Here is the answer.", await LastAssistantText(fixture, "before"));
        await fixture.Send("prompt", "second", "run the blocking tool"); await toolStarted.Task.WaitAsync(Deadline);
        await fixture.Send("abort", "abort").WaitAsync(Deadline); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        var last = fixture.Session.Snapshot.Context.Messages.Last(message => message.Role == "assistant").WireBody.Value;
        Check(last.GetProperty("stopReason").GetString() != "stop" && Text(fixture.Session.Snapshot.Context.Messages.Last(message => message.Role == "assistant")).Length == 0,
            "The aborted turn did not end with a textless assistant message: " + last.GetRawText());
        Equal("Here is the answer.", await LastAssistantText(fixture, "after"));
    }

    private static async Task LastAssistantTextAfterProviderError()
    {
        var transport = new Script([new(Message("Here is the answer.")), new(Empty(StopReason.Error))]);
        await using var fixture = await Fixture.Create(transport);
        await fixture.Send("prompt", "first", "first question"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        await fixture.Send("prompt", "second", "second question"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Equal("error", fixture.Session.Snapshot.Context.Messages.Last(message => message.Role == "assistant").WireBody.Value.GetProperty("stopReason").GetString());
        Equal("Here is the answer.", await LastAssistantText(fixture, "text"));
    }

    private static async Task LastAssistantTextKeepsNormalReplyWithoutText()
    {
        var transport = new Script([new(Message("Here is the answer.")), new(Empty(StopReason.Stop))]);
        await using var fixture = await Fixture.Create(transport);
        await fixture.Send("prompt", "first", "first question"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        await fixture.Send("prompt", "second", "second question"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Equal("stop", fixture.Session.Snapshot.Context.Messages.Last(message => message.Role == "assistant").WireBody.Value.GetProperty("stopReason").GetString());
        Check(await LastAssistantText(fixture, "text") is null, "A reply that ended normally without text was skipped.");
    }
}
