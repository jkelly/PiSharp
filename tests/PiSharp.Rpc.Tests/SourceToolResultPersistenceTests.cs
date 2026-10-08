using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SourceToolResultPersistenceTests
{
    private static readonly ModelDescriptor Model = new("owned-result-model", "openai-responses", "offline-authored");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("source result complete RPC objects differ from durable projection and survive older-leaf reopen", RpcAndDurableReopen);
    }
    private static async Task RpcAndDurableReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PiSharp-source-result-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl"); var ids = 0;
        string NextId() => "result-entry-" + Interlocked.Increment(ref ids);
        var missing = ToolResult.FromJson(JsonData.EmptyObject);
        var nullResult = ToolResultValueCodec.Read("{\"content\":null,\"details\":null,\"usage\":{\"future\":1.0,\"fraction\":0.005,\"wide\":9007199254740993,\"nil\":null},\"structuredContent\":{\"eventOnly\":true},\"isError\":false,\"opaqueResult\":[7,null]}");
        var invoker = new ToolInvoker([new Adapter("missing", missing), new Adapter("null", nullResult)], new Policy());
        var provider = new Source([Message(true), Message(false)]);
        var config = new AgentConfiguration(Model, provider, [new("missing", invoker), new("null", invoker)], new Hooks(), ToolExecutionMode.Sequential);
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "owned-result-session", timestamp = "2026-10-01T00:00:00.000Z", cwd = directory }));
        ImmutableArray<TranscriptEntry> stored; string leaf;
        try
        {
            await using (var session = await PersistentAgentSession.CreateAsync(path, header, config, () => 123, NextId))
            {
                var acknowledgementChecks = 0;
                using var acknowledgement = session.Subscribe(new Sink(async (observation, token) =>
                {
                    if (observation is not ToolResultMessageEnded) return;
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var read = await new SessionLogReader().ReadAsync(stream, leaveOpen: true, token);
                    Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete, "Live acknowledged source-result log was not complete.");
                    Equal(session.Snapshot.Log.CommittedByteLength, stream.Length);
                    Equal(session.Snapshot.Log.CommittedByteLength, (long)read.ValidatedPrefixByteLength); acknowledgementChecks++;
                }));
                using var output = new MemoryStream();
                await using var dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(output), () => 123,
                    [new(Model, JsonData.Parse("{\"id\":\"owned-result-model\",\"api\":\"openai-responses\",\"provider\":\"offline-authored\",\"name\":\"Owned result fixture\",\"baseUrl\":\"https://offline.invalid\",\"reasoning\":false,\"input\":[\"text\"],\"contextWindow\":4096,\"maxTokens\":128,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0}}"))]);
                await dispatcher.SubmitAsync(JsonData.Parse("{\"type\":\"prompt\",\"id\":\"owned-result\",\"message\":\"run\"}"));
                await dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
                var records = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JsonData.Parse).ToArray();
                var ends = records.Where(record => record.Value.GetProperty("type").GetString() == "tool_execution_end").ToArray();
                Equal(2, ends.Length);
                Check(ends.All(value => value.Value.GetProperty("isError").GetBoolean()), "Source hook outcome error was conflated with result.isError.");
                Equal("{}", ends[0].Value.GetProperty("result").GetRawText());
                var raw = ends[1].Value.GetProperty("result");
                Check(raw.GetProperty("content").ValueKind == JsonValueKind.Null && !raw.GetProperty("isError").GetBoolean() &&
                    raw.TryGetProperty("structuredContent", out _) && raw.TryGetProperty("opaqueResult", out _), "RPC lost complete owned result or changed its own error flag.");
                Equal("1.0", raw.GetProperty("usage").GetProperty("future").GetRawText());
                stored = session.Snapshot.Context.Messages; var messages = stored.Where(value => value.Role == "toolResult").ToArray();
                Equal(2, messages.Length); Equal(2, acknowledgementChecks); Equal(2, provider.Requests.Count);
                Check(!messages[0].WireBody.Value.TryGetProperty("details", out _) && !messages[0].WireBody.Value.TryGetProperty("usage", out _), "Absent durable metadata was materialized.");
                Check(messages[1].WireBody.Value.GetProperty("details").ValueKind == JsonValueKind.Null && messages[1].WireBody.Value.GetProperty("content").GetArrayLength() == 0,
                    "Null durable details/content normalization changed.");
                foreach (var message in messages)
                {
                    Check(message.WireBody.Value.GetProperty("isError").GetBoolean() && !message.WireBody.Value.TryGetProperty("structuredContent", out _) &&
                        !message.WireBody.Value.TryGetProperty("opaqueResult", out _), "Canonical message copied event-only fields or wrong outcome.");
                    Equal(message.WireBody.ToString(), provider.Requests[1].Messages.Single(value => value.Role == "toolResult" &&
                        value.WireBody.Value.GetProperty("toolCallId").GetString() == message.WireBody.Value.GetProperty("toolCallId").GetString()).WireBody.ToString());
                }
                var acknowledgedResult = session.Snapshot.Log.Entries.Last(entry => entry.Kind == SessionEntryKind.Message &&
                    entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult"); leaf = acknowledgedResult.Id;
                var wireMessageEnds = records.Where(record => record.Value.GetProperty("type").GetString() == "message_end" &&
                    record.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult").ToArray();
                Equal(2, wireMessageEnds.Length);
                for (var index = 0; index < messages.Length; index++) Equal(messages[index].WireBody.ToString(), wireMessageEnds[index].Value.GetProperty("message").GetRawText());
                Equal(new FileInfo(path).Length, session.Snapshot.Log.CommittedByteLength);
            }
            var resumeProvider = new Source([Message(false)]);
            await using (var resumed = await PersistentAgentSession.OpenAsync(path, new(Model, resumeProvider, []), () => 123, NextId,
                new(UseLatestLeaf: false, SelectedLeafId: leaf)))
            {
                var selected = stored.Take(stored.Length - 1).ToImmutableArray();
                Equal(selected.Length, resumed.Snapshot.Context.Messages.Length);
                for (var index = 0; index < selected.Length; index++) Equal(selected[index].WireBody.ToString(), resumed.Snapshot.Context.Messages[index].WireBody.ToString());
                await resumed.ContinueAsync();
                Equal(1, resumeProvider.Requests.Count);
                for (var index = 0; index < selected.Length; index++) Equal(selected[index].WireBody.ToString(), resumeProvider.Requests[0].Messages[index].WireBody.ToString());
                Check(resumed.Snapshot.Fault is null, "Raw usage/absent details poisoned older-leaf continuation.");
            }
            var finalRead = await new SessionLogReader().ReadFileAsync(path);
            Check(finalRead.SourceComplete && finalRead.Status == SessionLogReadStatus.Complete, "Closed reopened result history did not remain complete.");
            Equal(new FileInfo(path).Length, (long)finalRead.ValidatedPrefixByteLength);
        }
        finally
        {
            var target = Path.GetFullPath(directory); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(target) != parent || !Path.GetFileName(target).StartsWith("PiSharp-source-result-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned result fixture.");
            Directory.Delete(target, recursive: true);
        }
    }
    private static AssistantMessage Message(bool tool) => new(Model.Api, Model.Provider, Model.Id, 123,
        tool ? [new ToolCallContent("missing-call", "missing", JsonData.EmptyObject), new ToolCallContent("null-call", "null", JsonData.EmptyObject)] : [new TextContent("done")], TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
    private sealed class Adapter(string name, ToolResult result) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name, "read", PreparedToolActionKind.Path, "/authored", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(result);
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class Hooks : ISourceToolHooks
    {
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolPreflightDecision.Allow);
        public ValueTask<JsonData?> AfterToolCallAsync(ToolInvocation invocation, ToolResult result, bool isError, CancellationToken token) => ValueTask.FromResult<JsonData?>(JsonData.Parse("{\"isError\":true,\"details\":null,\"usage\":null,\"structuredContent\":null}"));
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback(observation, token); }
    private sealed class Source(AssistantMessage[] messages) : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = messages[Requests.Count]; Requests.Add(request); token.ThrowIfCancellationRequested();
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            for (var index = 0; index < final.Content.Length; index++)
                if (final.Content[index] is ToolCallContent call) { yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(index, call); }
                else if (final.Content[index] is TextContent text) { yield return new TextStarted(index, new("")); yield return new TextEnded(index, text.Text); }
            yield return new StreamDone(final.StopReason, final); await Task.CompletedTask;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
