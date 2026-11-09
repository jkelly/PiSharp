using System.Reflection;
using PiSharp.Durable.Chord;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SettingsForwardingCases()
    {
        yield return Case("settings-forwarding.dynamic-matrix", () =>
        {
            var inner = new ForwardedSettings();
            var type = typeof(SessionCompaction).Assembly.GetType("PiSharp.Durable.Hosting.SessionHarnessSettings")!;
            var compaction = SessionCompaction.PiDurableDefaults;
            var wrapper = (HarnessSettings)Activator.CreateInstance(type, inner, (Func<SessionCompaction>)(() => compaction), null, null)!;
            foreach (var property in typeof(HarnessSettings).GetProperties())
                Equal(type, type.GetProperty(property.Name)!.GetMethod!.DeclaringType, property.Name + " has an explicit forwarding or override policy");
            foreach (var policy in new[] { AfterFailedRun.Withdraw, AfterFailedRun.Continue })
            foreach (var limit in new[] { 1d, double.PositiveInfinity })
            {
                inner.Current = new HarnessSettings { AfterFailedRun = policy, RestartLimit = limit, ContextRetentionMs = limit, DocumentRetentionMs = limit,
                    Stream = new(), Retry = new(), Progress = new(), ToolExecution = ToolExecutionMode.Parallel, SteeringMode = QueueMode.All, FollowUpMode = QueueMode.All };
                foreach (var property in typeof(HarnessSettings).GetProperties().Where(p => p.Name is not ("Extensions" or "Compaction")))
                    Equal(property.GetValue(inner), property.GetValue(wrapper), property.Name + " forwards dynamically");
                Equal(policy, Agents.ResolveSettings(wrapper).AfterFailedRun, "effective failure policy");
                Equal(limit, Agents.ResolveSettings(wrapper).RestartLimit, "effective restart limit");
            }
            compaction = new SessionCompaction(false, 12000, 3000, 7000);
            Equal(false, wrapper.Compaction!.Enabled, "session compaction stays live");
            Check(wrapper.Extensions is null, "default extensions unchanged");
            var installed = new Extension[] { new() { Name = "installed" } };
            var metadata = JsonObjectValue.Empty.SetItem("account", JsonValue.String("first"));
            var customized = (HarnessSettings)Activator.CreateInstance(type, inner, (Func<SessionCompaction>)(() => compaction),
                (Func<IReadOnlyList<Extension>>)(() => installed), (Func<JsonObjectValue>)(() => metadata))!;
            Check(ReferenceEquals(installed, customized.Extensions), "default extension callback");
            inner.Current = new HarnessSettings { Extensions = [], Compaction = new() { ModelOverrides = new Dictionary<string, CompactionOverride>() } };
            Check(customized.Extensions!.Count == 0, "explicit extension choice wins");
            Check(ReferenceEquals(inner.Compaction!.ModelOverrides, customized.Compaction!.ModelOverrides), "model overrides retained");
            Equal(metadata, customized.Stream!.Metadata![SessionStreamMetadata.Key], "metadata attached");
            metadata = JsonObjectValue.Empty.SetItem("account", JsonValue.String("second"));
            Equal(metadata, customized.Stream!.Metadata![SessionStreamMetadata.Key], "metadata remains live");
        });
        yield return Case("settings-forwarding.host-queue-outcome", async () =>
        {
            foreach (var policy in new[] { AfterFailedRun.Withdraw, AfterFailedRun.Continue })
            {
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var models = new FauxDurableModels { RespondAsync = async (chat, token) => {
                    if (chat.Text == "error: rejected") { reached.TrySetResult(); await release.Task.WaitAsync(token); return null; }
                    return "answered";
                }};
                await using var device = await TestDevice.OpenAsync("forwarding", new DeviceSetup { Models = models,
                    Configure = options => options with { Settings = new HarnessSettings { AfterFailedRun = policy, RestartLimit = 1, Retry = new RetrySettings { Enabled = false } } } });
                var session = await device.CreateAsync();
                var root = await session.GetHarness().RootAsync(DurableContext.Background);
                var first = await root.SubmitAsync(SubmissionDraft.Input("error: rejected"), DurableContext.Background);
                await reached.Task.WaitAsync(Answer);
                var second = await root.SubmitAsync(SubmissionDraft.Input("next", WhenBusy.FollowUp), DurableContext.Background);
                release.SetResult();
                var failed = await first.WaitAsync(DurableContext.Background);
                Equal(SubmissionStatus.Unanswered, failed.Status, "first failed: " + policy);
                Equal("model_error", failed.Reason, "provider failure classified: " + policy);
                var result = await second.WaitAsync(DurableContext.Background).WaitAsync(Answer);
                Equal(policy == AfterFailedRun.Withdraw ? SubmissionStatus.Unanswered : SubmissionStatus.Done, result.Status, "queued outcome follows policy: " + policy);
            }
        });
    }
    private sealed class ForwardedSettings : HarnessSettings
    {
        public HarnessSettings Current = new();
        public override IReadOnlyList<Extension>? Extensions => Current.Extensions;
        public override CompactionSettings? Compaction => Current.Compaction;
        public override ConversationStreamOptions? Stream => Current.Stream;
        public override RetrySettings? Retry => Current.Retry;
        public override ProgressSettings? Progress => Current.Progress;
        public override ToolExecutionMode? ToolExecution => Current.ToolExecution;
        public override QueueMode? SteeringMode => Current.SteeringMode;
        public override QueueMode? FollowUpMode => Current.FollowUpMode;
        public override double? ContextRetentionMs => Current.ContextRetentionMs;
        public override double? DocumentRetentionMs => Current.DocumentRetentionMs;
        public override AfterFailedRun? AfterFailedRun => Current.AfterFailedRun;
        public override double? RestartLimit => Current.RestartLimit;
    }
}
