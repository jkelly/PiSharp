using System.Reflection;
using PiSharp.Durable;
using PiSharp.CustomAgents.Teams;


internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SettingsForwardingCases()
    {
        yield return Case("settings-forwarding.dynamic-matrix", () =>
        {
            var inner = new ForwardedSettings();
            var type = typeof(AgentTeam).GetNestedType("TeamSettings", BindingFlags.NonPublic)!;

            var wrapper = (HarnessSettings)Activator.CreateInstance(type, inner)!;
            foreach (var property in typeof(HarnessSettings).GetProperties())
                Equal(type, type.GetProperty(property.Name)!.GetMethod!.DeclaringType, property.Name + " has an explicit forwarding or override policy");
            foreach (var policy in new[] { AfterFailedRun.Withdraw, AfterFailedRun.Continue })
            foreach (var limit in new[] { 1d, double.PositiveInfinity })
            {
                inner.Current = new HarnessSettings { Extensions = [new Extension { Name = "suppressed" }], AfterFailedRun = policy, RestartLimit = limit, ContextRetentionMs = limit, DocumentRetentionMs = limit,
                    Compaction = new(), Stream = new(), Retry = new(), Progress = new(), ToolExecution = ToolExecutionMode.Parallel, SteeringMode = QueueMode.All, FollowUpMode = QueueMode.All };
                foreach (var property in typeof(HarnessSettings).GetProperties().Where(p => p.Name != "Extensions"))
                    Equal(property.GetValue(inner), property.GetValue(wrapper), property.Name + " forwards dynamically");
                Equal(policy, Agents.ResolveSettings(wrapper).AfterFailedRun, "effective failure policy");
                Equal(limit, Agents.ResolveSettings(wrapper).RestartLimit, "effective restart limit");
            }
            Check(wrapper.Extensions!.Count == 0, "team suppresses default extensions");
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
