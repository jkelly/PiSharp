// Decision 0011 (run W4, unit R4): registers agent compare's workspace manager. The root is <data>\workspaces, so --data and
// PISHARP_WEBAGENT_DATA move it with the rest of the app's data; the janitor cleans it at start and daily. No endpoints here: the agent
// compare runner (R5) answers /api/compare/agent/... and Settings › Data (R9) lists and removes workspaces, both through WorkspaceManager.
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Registers the workspace manager.</summary>
public static class WorkspacesModule
{
    /// <summary>The workspaces root for <paramref name="options"/>' data folder.</summary>
    public static string RootFor(WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Path.Join(options.DataDirectory, WorkspaceManagerOptions.FolderName);
    }

    /// <summary>Registers <see cref="WorkspaceManagerOptions"/> (root <c>&lt;data&gt;\workspaces</c>), <see cref="WorkspaceManager"/> and
    /// <see cref="WorkspaceJanitor"/> (as a hosted service and as itself). A test may register its own options first.</summary>
    public static IServiceCollection AddAgentCompareWorkspaces(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(new WorkspaceManagerOptions { Root = RootFor(options) });
        services.TryAddSingleton(provider => new WorkspaceManager(provider.GetRequiredService<WorkspaceManagerOptions>(), provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<WorkspaceManager>>()));
        services.TryAddSingleton(provider => new WorkspaceJanitor(provider.GetRequiredService<WorkspaceManager>(), provider.GetServices<IWorkspaceActivity>(),
            provider.GetService<TimeProvider>(), provider.GetService<ILogger<WorkspaceJanitor>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkspaceJanitor>(provider => provider.GetRequiredService<WorkspaceJanitor>()));
        return services;
    }
}
