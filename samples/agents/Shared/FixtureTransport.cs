using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Contracts;

namespace PiSharp.Samples;

/// <summary>Authored decisions only; tools and the native agent execute normally. No provider network.</summary>
public sealed class FixtureTransport(WorkflowSpec workflow) : IChatTransport
{
    private int nextCall;
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previousFailed = request.Messages.LastOrDefault()?.WireBody.Value.TryGetProperty("isError", out var failed) == true && failed.ValueKind == System.Text.Json.JsonValueKind.True;
        AssistantMessage message;
        if (!previousFailed && nextCall < workflow.DemoCalls.Count)
        {
            var step = workflow.DemoCalls[nextCall++];
            var call = new ToolCallContent($"fixture-{nextCall}", step.Tool, JsonData.Parse(step.Arguments.ToJsonString()));
            message = new(request.Model.Api, request.Model.Provider, request.Model.Id, 0, [call], TokenUsage.Zero, StopReason.ToolUse);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            yield return new ToolCallStarted(0, call);
            yield return new ToolCallEnded(0, call);
        }
        else
        {
            var text = previousFailed ? "The tool reported a failure; the workflow is incomplete. Review report.json before continuing."
                : $"Completed {nextCall} tool steps for {workflow.Title}. The actual results are recorded in report.json.";
            message = new(request.Model.Api, request.Model.Provider, request.Model.Id, 0, [new TextContent(text)], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new(""));
            yield return new TextDelta(0, text);
            yield return new TextEnded(0, text);
        }
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new StreamDone(message.StopReason, message);
    }
}
