// PiSharp-only (decision 0008): the device's extensions for one session, put together for its registry.
namespace PiSharp.Durable.Hosting;

/// <summary>Builds the <c>durable-apps</c>, <c>system-prompts</c> and <c>command-guard</c> extensions of a session.</summary>
public static class DeviceApps
{
    /// <summary>The extension with the App State, history, subagent, session, handoff and job tools.</summary>
    public const string ExtensionName = "durable-apps";

    /// <summary>Extensions an agent turns on itself; off by default. <c>plan-mode</c> stays off too, as in <c>pisharp durable</c>.</summary>
    public static IReadOnlyList<string> Optional { get; } = [SystemPrompts.ExtensionName, CommandGuard.ExtensionName, "plan-mode"];

    internal static (IReadOnlyList<Extension> Extensions, IReadOnlyDictionary<string, DurableTask> JobTasks) Create(DurableDeviceHost host, HostedSession session)
    {
        var jobTasks = host.Options.Jobs.ToDictionary(job => job.Id, job => Jobs.CreateTask(job, host.AppState, session.Id), StringComparer.Ordinal);
        var apps = Define.Extension(new Extension
        {
            Name = ExtensionName,
            Tools =
            [
                AppTools.ReadState(host.AppState),
                AppTools.UpdateState(host.AppState, session.Id),
                AppTools.SearchHistory(session.ConversationForToolAsync),
                AppTools.Handoff(),
                AppTools.Subagent(),
                AppTools.StartSession(host, session.Id),
                Jobs.ListTool(host.Options.Jobs),
                Jobs.RunTool(host.Options.Jobs, jobTasks),
                Jobs.CheckTool(),
            ],
            Tasks = [.. jobTasks.Values, Jobs.ApprovalTask],
        });
        return ([apps, SystemPrompts.Create(host.PromptsDirectory, host.Options.DefaultPrompts),
            CommandGuard.Create(host.Options.CommandJudge, host.Options.CommandGuardFailOpen)], jobTasks);
    }
}
