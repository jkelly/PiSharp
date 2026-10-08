using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Resources;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Sessions.Serialization;

internal static class ContextDurableSessionMessageTests
{
    private static readonly ModelDescriptor Model = new("message-model", "openai-responses", "offline");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("custom-message.idle-durable-projection-without-provider", () => Exercise(0));
        yield return ("custom-message.next-turn-after-user-and-canceled-prompt-retains-queue", () => Exercise(1));
        yield return ("custom-message.held-tool-context-after-result-without-extra-request", () => Exercise(2));
        yield return ("custom-message.explicit-trigger-real-provider-turn", () => Exercise(3));
        yield return ("user-message.mixed-literal-and-explicit-template-policy", () => Exercise(4));
    }
    private static void Check(bool value) { if (!value) throw new IOException("Durable message control failed."); }
    private static async Task Exercise(int mode)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pisharp-durable-custom-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var tool = new HeldTool(); var transport = new Script(mode == 2); PersistentAgentSession? session = null;
        ReplaceableAgentSession? owner = null; IDisposable? subscription = null; CustomNotifications? notifications = null; Task<AgentLoopResult>? runOriginal = null; var faults = new List<Exception>(); var originals = new List<Original>();
        try
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "custom-session", timestamp = "2026-01-01T00:00:00Z", cwd = folder }));
            var sequence = 0; var configuration = new AgentConfiguration(Model, transport, mode == 2 ? [new("hold", tool)] : []);
            session = await Observe(PersistentAgentSession.CreateAsync(Path.Combine(folder, "session.jsonl"), header, configuration, () => 123, () => "entry-" + ++sequence), originals);
            var active = session;
            notifications = new CustomNotifications(active);
            if (mode == 0) subscription = active.Subscribe(notifications);
            var draft = new SessionCustomMessageDraft("fixture.custom", JsonData.Parse("\"custom text\""), false, JsonData.Parse("{\"opaque\":1}"));
            if (mode == 4)
            {
                owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new NotSupportedException()));
                var reads = new NativeExtensionContextFacadeHost(); reads.Attach(owner); var input = new CapturedInput();
                var catalog = new PromptTemplateCatalogSnapshot([new(new("review", "fixture", "expanded $1"), "caller://review", new("caller://review", "fixture", PromptTemplateSourceScope.Temporary, PromptTemplateSourceOrigin.TopLevel))], []);
                var effects = new NativeExistingSessionRegistrationActions(reads, () => input,
                    () => new PromptTemplateInputAdmission(catalog, PromptTemplateInputOperation.Prompt, expandTemplates: true));
                var literal = effects.SendUserMessageAsync(JsonData.Parse("[{\"type\":\"text\",\"text\":\"first\"},{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"},{\"type\":\"text\",\"text\":\"second\"}]"), null, default).AsTask();
                await Observe(literal, originals); Check(input.Last is { Text: "first\nsecond", Source: PromptInputSource.Extension } && input.Last.Images?.Value.GetArrayLength() == 1 && transport.Requests.Count == 0);
                var expanded = effects.SendUserMessageAsync(JsonData.Parse("\"/review argument\""), new(ExpandPromptTemplates: true), default).AsTask(); await Observe(expanded, originals);
                Check(transport.Requests.Count == 1 && transport.Requests[0].Messages[^1].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString() == "expanded argument");
                var literalDefault = new NativeExistingSessionRegistrationActions(reads, () => input);
                try { _ = literalDefault.SendUserMessageAsync(JsonData.Parse("\"/review argument\""), new(ExpandPromptTemplates: true), default); throw new IOException("An unadmitted catalog was invented."); }
                catch (NotSupportedException) { }
                Check(transport.Requests.Count == 1);
            }
            else if (mode == 2)
            {
                runOriginal = active.PromptAsync(User("run"));
                var entered = await Task.WhenAny(tool.Entered.Task, runOriginal);
                if (ReferenceEquals(entered, runOriginal)) { await Observe(runOriginal, originals); throw new IOException("Tool did not enter."); }
                await tool.Entered.Task;
                var deliveryOriginal = active.SendCustomMessageAsync("custom-session", draft, triggerTurn: false); var receipt = await Observe(deliveryOriginal, originals);
                Check(receipt.Disposition == SessionCustomMessageDisposition.Queued && active.HasPendingCustomMessages);
                Check(!active.Snapshot.Log.Entries.Any(entry => entry.Kind == SessionEntryKind.CustomMessage));
                tool.Release.TrySetResult(); await Observe(runOriginal, originals);
                var entries = active.Snapshot.Context.Messages;
                var resultIndex = Array.FindIndex(entries.ToArray(), entry => entry.Role == "toolResult");
                var customIndex = Array.FindIndex(entries.ToArray(), entry => entry.Role == "custom");
                Check(resultIndex >= 0 && customIndex > resultIndex && transport.Requests.Count == 2 && !active.HasPendingCustomMessages);
                Check(transport.Requests[1].Messages.Any(entry => entry.Role == "user" && entry.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString() == "custom text"));
            }
            else if (mode == 1)
            {
                var queued = active.SendCustomMessageAsync("custom-session", draft, SessionCustomMessageDelivery.NextTurn); Check((await Observe(queued, originals)).Disposition == SessionCustomMessageDisposition.Queued);
                var before = active.Snapshot.Log.Entries.Length;
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                try { _ = active.PromptAsync(User("canceled"), canceled.Token); throw new IOException("Canceled prompt admitted."); } catch (OperationCanceledException) { }
                Check(active.HasPendingCustomMessages && active.Snapshot.Log.Entries.Length == before && transport.Requests.Count == 0);
                runOriginal = active.PromptAsync(User("next")); await Observe(runOriginal, originals);
                Check(!active.HasPendingCustomMessages && active.Snapshot.Context.Messages[0].Role == "user" && active.Snapshot.Context.Messages[1].Role == "custom" && transport.Requests.Count == 1);
            }
            else
            {
                var original = active.SendCustomMessageAsync("custom-session", draft, triggerTurn: mode == 3); var receipt = await Observe(original, originals);
                Check(receipt.Disposition == (mode == 3 ? SessionCustomMessageDisposition.Started : SessionCustomMessageDisposition.Committed));
                Check(active.Snapshot.Log.Entries.Any(entry => entry.Kind == SessionEntryKind.CustomMessage));
                Check(active.Snapshot.Context.Messages[0].Role == "custom" && active.Snapshot.Context.Messages[0].WireBody.Value.GetProperty("display").ValueKind == JsonValueKind.False);
                Check(transport.Requests.Count == (mode == 3 ? 1 : 0));
                Check(new FileInfo(Path.Combine(folder, "session.jsonl")).Length == active.Snapshot.Log.CommittedByteLength);
                if (mode == 0)
                {
                    Check(notifications.Started == 1 && notifications.Ended == 1 && notifications.ReentryRefused == 2 && notifications.DurableBeforeNotification);
                    notifications.ReleaseDescendant.TrySetResult();
                    await Observe(notifications.DescendantOriginal!, originals);
                    Check(notifications.DescendantIdleJoined);
                }
            }
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            tool.Release.TrySetResult();
            if (notifications is not null)
            {
                notifications.ReleaseDescendant.TrySetResult();
                if (notifications.DescendantOriginal is { } descendant) await JoinPending(descendant, originals);
            }
            try { subscription?.Dispose(); } catch (Exception error) { faults.Add(error); }
            if (runOriginal is not null && !runOriginal.IsCompletedSuccessfully) await JoinPending(runOriginal, originals);
            if (owner is not null) await Cleanup(() => owner.DisposeAsync(), originals, faults);
            else if (session is not null) await Cleanup(() => session.DisposeAsync(), originals, faults);
            try { Directory.Delete(folder, true); } catch (Exception error) { faults.Add(error); }
        }
        foreach (var record in originals) { if (record.Aggregate is not null) faults.Add(record.Aggregate); if (record.Direct is not null) faults.Add(record.Direct); }
        if (faults.Count != 0) throw new AggregateException("Durable message control and cleanup originals.", faults);
    }
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = new[] { new { type = "text", text } }, timestamp = 123 })));
    private sealed class Original(Task task)
    { internal readonly Task Task = task; internal AggregateException? Aggregate; internal Exception? Direct; internal bool Joined; }
    private static async Task<T> Observe<T>(Task<T> original, List<Original> records)
    {
        var record = new Original(original); records.Add(record);
        try { return await original; }
        catch (Exception error) { record.Aggregate = original.Exception; record.Direct = error; throw; }
        finally { record.Joined = true; }
    }
    private static async Task Observe(Task original, List<Original> records)
    {
        var record = new Original(original); records.Add(record);
        try { await original; }
        catch (Exception error) { record.Aggregate = original.Exception; record.Direct = error; throw; }
        finally { record.Joined = true; }
    }
    private static async Task JoinPending(Task original, List<Original> records)
    {
        if (records.Any(record => ReferenceEquals(record.Task, original) && record.Joined)) return;
        try { await Observe(original, records); } catch (Exception) { }
    }
    private static async Task Cleanup(Func<ValueTask> factory, List<Original> records, List<Exception> faults)
    { try { var original = factory().AsTask(); await Observe(original, records); } catch (Exception error) { faults.Add(error); } }
    private sealed class CapturedInput : IPromptInputAdmission
    { internal PromptInput? Last; public ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token) { token.ThrowIfCancellationRequested(); Last = input; return ValueTask.FromResult(new PromptInputDecision(PromptInputAction.Handled)); } }
    private sealed class CustomNotifications(PersistentAgentSession session) : IAgentEventSink
    {
        internal int Started, Ended, ReentryRefused; internal bool DurableBeforeNotification = true;
        internal readonly TaskCompletionSource ReleaseDescendant = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? DescendantOriginal; internal bool DescendantIdleJoined;
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (observation is AgentLoopInputMessageStarted) { Started++; DescendantOriginal = CheckLaterAsync(); }
            if (observation is AgentLoopInputMessageEnded) Ended++;
            DurableBeforeNotification &= session.Snapshot.Log.Entries.Any(entry => entry.Kind == SessionEntryKind.CustomMessage);
            try { _ = session.DisposeAsync(); throw new IOException("Notification reentry was admitted."); }
            catch (InvalidOperationException) { ReentryRefused++; }
            return ValueTask.CompletedTask;
        }
        private async Task CheckLaterAsync()
        { await ReleaseDescendant.Task; await session.WaitForIdleAsync(); DescendantIdleJoined = true; }
    }
    private sealed class HeldTool : IToolExecutor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { Entered.TrySetResult(); await Release.Task; token.ThrowIfCancellationRequested(); return new ToolResult([new("result")], JsonData.EmptyObject); }
    }
    private sealed class Script(bool tools) : IChatTransport
    {
        internal readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request); var tool = tools && Requests.Count == 1;
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123,
                tool ? [new ToolCallContent("call", "hold", JsonData.EmptyObject)] : [new TextContent("done")], TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending }); await Task.CompletedTask;
            if (tool) { var call = (ToolCallContent)message.Content[0]; yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            yield return new StreamDone(message.StopReason, message);
        }
    }
}
