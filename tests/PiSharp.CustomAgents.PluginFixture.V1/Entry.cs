using System.Runtime.Loader;
using PiSharp.CustomAgents.Plugins;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Tools;
using PiSharp.Durable.Chord;

[assembly: AgentPlugin(typeof(AgentsPluginFixture.Entry))]

namespace AgentsPluginFixture;

public sealed record EchoArgs(string Text);

/// <summary>The fixture plugin: an echo tool, a tool that waits for the host's gate, a probe of where the contract types come from, and an
/// agent. Version 2 (FIXTURE_V2) answers with "v2" and adds a tool. Hosts pass a TaskCompletionSource (the gate), a
/// TaskCompletionSource&lt;string&gt; (set when the slow tool starts) and a List&lt;string&gt; (a log) as services.</summary>
public sealed class Entry : IAgentPlugin, IAsyncDisposable
{
#if FIXTURE_V2
    private const string Version = "v2";
#else
    private const string Version = "v1";
#endif

    private List<string>? log;

    public Task RegisterAsync(IAgentPluginContext context, CancellationToken cancellationToken)
    {
        if (context.Settings.TryGetValue("fail", out var fail) && fail is JsonBooleanValue { Value: true })
            throw new InvalidOperationException("the fixture was told to fail");
        log = context.GetService<List<string>>();
        var gate = context.GetService<TaskCompletionSource>();
        var started = context.GetService<TaskCompletionSource<string>>();
        context.AddTool(AgentTool.Create<EchoArgs>("plugin_echo", "Echoes text", (args, _) => Task.FromResult<AgentToolResult>(Version + ":" + args.Text)));
        context.AddTool(AgentTool.Create("plugin_slow", "Waits for the host's gate", async tool =>
        {
            started?.TrySetResult(Version);
            if (gate is not null) await gate.Task.WaitAsync(tool.CancellationToken);
            return Version + " slow done";
        }));
        context.AddTool(AgentTool.Create("plugin_identity", "Says which load contexts the contract types and the plugin come from", _ =>
            Task.FromResult<AgentToolResult>(AssemblyLoadContext.GetLoadContext(typeof(AgentTool).Assembly)!.Name + "|" +
                AssemblyLoadContext.GetLoadContext(typeof(Entry).Assembly)!.Name)));
#if FIXTURE_V2
        context.AddTool(AgentTool.Create("plugin_new", "Only in version 2", _ => Task.FromResult<AgentToolResult>("new in v2")));
#endif
        context.AddAgent(new AgentDefinition { Name = "plugin-helper", Description = "Comes from the plugin", Instructions = "PLUGIN " + Version });
        Log("registered:" + Version + "#" + context.Generation);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Log("disposed:" + Version);
        return ValueTask.CompletedTask;
    }

    private void Log(string line)
    {
        if (log is null) return;
        lock (log) log.Add(line);
    }
}
