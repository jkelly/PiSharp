using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class ResponsesCheckpointProjectionTests
{
    private static readonly ModelDescriptor Model = new("rpc-reasoning", "openai-responses", "openai");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("rpc.responses-checkpoint-public-projection-and-final-state", Projection);
        yield return ("rpc.native-checkpoint-suppression-keeps-existing-event-policy", ExistingPolicy);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static PersistentAgentSessionSnapshot Snapshot(AssistantMessage message)
    {
        var messages = ImmutableArray.Create(new TranscriptEntry("assistant", PiWireJson.WriteMessage(message)));
        var header = new SessionEntryCodec().Parse("""{"type":"session","version":3,"id":"checkpoint-test","timestamp":"2026-10-03T00:00:00.000Z","cwd":"/synthetic"}""");
        var entries = ImmutableDictionary<string, SessionEntry>.Empty;
        return new(new(0, Model, [], messages, [], [], null, 0, 0, false, false, null, false),
            new(header, [], entries, null, 0, 0),
            new(null, [], entries, [], messages, messages, "off", null), null, false);
    }
    private static async Task Projection()
    {
        foreach (var outcome in new[] { "completed", "incomplete", "eof", "cancel" })
        {
            using var cancellation = new CancellationTokenSource();
            async IAsyncEnumerable<JsonData> Source([EnumeratorCancellation] CancellationToken token)
            {
                yield return JsonData.Parse("""{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}""");
                yield return JsonData.Parse("""{"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"partial"}""");
                yield return JsonData.Parse("""{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[{"text":"authoritative"}]}}""");
                if (outcome == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                if (outcome is "completed" or "incomplete")
                    yield return JsonData.Parse("{\"type\":\"response." + outcome + "\",\"response\":{\"status\":\"" + outcome +
                        "\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[{\"type\":\"reasoning\",\"id\":\"rs_1\",\"encrypted_content\":\"cipher\"}]}}");
                await Task.CompletedTask;
            }
            var client = new ChatClient(new ResponsesTextToolTransport((_, token) => Source(token)), capacity: 1);
            await using var run = await client.StartAsync(new(Model, [], 1), cancellation.Token);
            var projector = new RpcAgentEventProjector();
            var empty = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [], TokenUsage.Zero, StopReason.Pending);
            var initial = Snapshot(empty);
            var publicEvents = new List<JsonData>();
            var checkpoints = 0;
            await foreach (var frame in run.ReadEventsAsync())
            {
                var projected = projector.Project(new TurnStreamObserved(frame), initial, 0);
                if (frame is ThinkingCheckpoint) { checkpoints++; Check(projected.IsEmpty, "Checkpoint escaped onto RPC wire"); }
                publicEvents.AddRange(projected);
            }
            var result = await run.Completion;
            Check(checkpoints == 1, "Actual Responses mapper did not emit one checkpoint");
            var expected = outcome == "cancel" ? new[] { "thinking_start", "thinking_delta" } : new[] { "thinking_start", "thinking_delta", "thinking_end" };
            Check(publicEvents.Select(e => e.Value.GetProperty("assistantMessageEvent").GetProperty("type").GetString()).SequenceEqual(expected), "Public delta order changed");
            Check(publicEvents.All(e => e.Value.GetProperty("type").GetString() == "message_update"), "Invented RPC envelope");
            var thinking = (ThinkingContent)result.Message.Content.Single();
            Check(thinking.Thinking == "authoritative", "Checkpoint state lost at terminal");
            var signature = JsonData.Parse(thinking.ExtraProperties!.Values["thinkingSignature"].Value.GetString()!).Value;
            Check(signature.TryGetProperty("encrypted_content", out var cipher) == (outcome is "completed" or "incomplete"), "Wrong backfill state");
            if (outcome is "completed" or "incomplete") Check(cipher.GetString() == "cipher", "Backfill corrupted");
            Check(result.Message.StopReason == (outcome == "completed" ? StopReason.Stop : outcome == "incomplete" ? StopReason.Length : outcome == "cancel" ? StopReason.Aborted : StopReason.Error), "Terminal authority changed");
            var ended = projector.Project(new AssistantMessageEnded(result.Message), Snapshot(result.Message), 0).Single();
            Check(ended.Value.GetProperty("type").GetString() == "message_end", "Final message suppressed");
            Check(JsonElement.DeepEquals(ended.Value.GetProperty("message"), PiWireJson.WriteMessage(result.Message).Value), "Final message body changed");
        }
    }
    private static Task ExistingPolicy()
    {
        var projector = new RpcAgentEventProjector();
        var snapshot = Snapshot(new(Model.Api, Model.Provider, Model.Id, 1, [], TokenUsage.Zero, StopReason.Pending));
        foreach (var frame in new StreamEvent[] { new ThinkingCheckpoint(0, "internal"), new ToolCallCheckpoint(0, "{}"), new ToolCallHeaderUpdated(0, "call", "tool") })
            Check(projector.Project(new TurnStreamObserved(frame), snapshot, 0).IsEmpty, "Native bookkeeping became public");
        foreach (var frame in new StreamEvent[] { new TextEnded(0, "text"), new ThinkingEnded(0, "thinking"), new ToolCallEnded(0, new("call", "tool", JsonData.EmptyObject)) })
            Check(projector.Project(new TurnStreamObserved(frame), snapshot, 0).Length == 1, "Public end suppressed");
        try { projector.Project(new TurnStreamObserved(new Unknown()), snapshot, 0); }
        catch (RpcDispatchException) { return Task.CompletedTask; }
        throw new InvalidOperationException("Unknown event admission was broadened");
    }
    private sealed record Unknown : StreamEvent;
}