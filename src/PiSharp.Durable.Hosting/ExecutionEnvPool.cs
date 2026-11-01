// PiSharp-only (decision 0008). The same pool as pisharp durable's DurableExecutionEnvs (src/PiSharp.Cli/Durable/DurableHarnessSetup.cs; upstream
// coding-agent ExecutionEnvs): one local environment per working directory, shared by every conversation that works there. The CLI keeps its
// copy until Phase 4 moves its host onto this library.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools.Env;

namespace PiSharp.Durable.Hosting;

/// <summary>One <see cref="LocalExecutionEnv"/> per working directory.</summary>
internal sealed class ExecutionEnvPool(string defaultCwd)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, LocalExecutionEnv> envs = new(StringComparer.Ordinal);

    /// <summary><see cref="HarnessOptions.Env"/>: the environment of the conversation's cwd, else the session's.</summary>
    public Task<IExecutionEnv?> EnvAsync(EnvTarget target, DurableContext context)
    {
        var cwd = target.Cwd ?? defaultCwd;
        lock (gate)
        {
            if (!envs.TryGetValue(cwd, out var env)) envs[cwd] = env = new LocalExecutionEnv(cwd);
            return Task.FromResult<IExecutionEnv?>(env);
        }
    }

    /// <summary>Cleans up every environment created so far.</summary>
    public async Task CleanupAsync(DurableContext context)
    {
        LocalExecutionEnv[] all;
        lock (gate) { all = [.. envs.Values]; envs.Clear(); }
        foreach (var env in all) await env.CleanupAsync(context).ConfigureAwait(false);
    }
}
