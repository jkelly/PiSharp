// Decision 0011 (run W4, R5): registers the agent compare runner. Program.cs calls AddAgentCompareRunner after AddRunsArea (so the models the
// device gets are the runs.db recorder over the Spend Guard) and MapAgentCompareRunner before the core's endpoints:
//   - webagent.db migrations 580 (agent_compare_runs, agent_compare_turns) and 585 (agent_compare_patches, W4 fix 2) and AgentCompareStore,
//     AgentCompareGate, AgentCompareRunner;
//   - the runner is a hosted lifecycle service (registered after DeviceHostService, so it is started after the device and stopped before
//     it; its StartingAsync attaches the approval override before the device opens) and the janitor's IWorkspaceActivity;
//   - DeviceHostService's registration is replaced by the same service built over AgentCompareModels (the gate over the app's
//     IDurableModels); the IDurableModels registration itself is unchanged, so the run engine and the rest keep the plain models.
// MapAgentCompareRunner maps /api/compare/agent (AgentCompareEndpoints).
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PiSharp.Durable;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>Registration and endpoints of the agent compare runner.</summary>
public static class AgentCompareModule
{
    /// <summary>Registers the runner (see the file comment). Call it after <c>AddChatArea</c>, <c>AddAgentCompareWorkspaces</c> and
    /// <c>AddRunsArea</c>.</summary>
    public static IServiceCollection AddAgentCompareRunner(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddWebAgentMigration(AgentCompareStore.Migration);
        services.AddWebAgentMigration(AgentCompareStore.PatchMigration);
        services.TryAddSingleton(provider => new AgentCompareStore(provider.GetRequiredService<WebAgentDb>(), provider.GetService<ILogger<AgentCompareStore>>()));
        services.TryAddSingleton(provider => new AgentCompareGate(provider.GetRequiredService<AgentCompareStore>(), provider,
            provider.GetService<ILogger<AgentCompareGate>>()));
        services.TryAddSingleton(provider => ActivatorUtilities.CreateInstance<AgentCompareRunner>(provider));
        services.RemoveAll<DeviceHostService>();
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<DeviceHostService>(provider,
            new AgentCompareModels(provider.GetRequiredService<IDurableModels>(), provider.GetRequiredService<AgentCompareGate>())));
        services.AddHostedService(provider => provider.GetRequiredService<AgentCompareRunner>());
        services.AddSingleton<IWorkspaceActivity>(provider => provider.GetRequiredService<AgentCompareRunner>());
        return services;
    }

    /// <summary>Maps <c>/api/compare/agent</c>.</summary>
    public static WebApplication MapAgentCompareRunner(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapAgentCompareEndpoints();
        return app;
    }
}
