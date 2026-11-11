// PiSharp-only authored fixture. Real published-loader entry for initializer/context lifetime controls.
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.CodingAgent.Tests;

// Only shared SDK contracts cross the load-context boundary. The test runner is not packaged as a plugin.
public sealed class NativeInitializerLoaderEntry : IPiSharpExtension
{
    private string? log; private IExtensionCommandContext? saved; private IExtensionContextReadHost? reads;
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
    {
        log = AppContext.GetData("PiSharp.NativeInitializer." + registry.OwnerId) as string ?? throw new IOException("Missing fixture log.");
        if (File.ReadAllText(log) != "binder\n") throw new IOException("Initializer binder did not precede original initialization exactly once.");
        File.AppendAllText(log, "initialize\n");
        registry.RegisterCommand(new("fixture-context", "fixture-context", "default host reads", async (_, context, _) =>
        {
            var host = (context as IExtensionFacadeHostContext)?.FacadeHost ?? throw new IOException("Default facade missing.");
            saved = context; reads = host;
            var model = host.GetModel(context)?.Value.GetProperty("id").GetString();
            await File.AppendAllTextAsync(log!, host.GetCwd(context) + "|" + model + "|" + host.IsIdle(context) + "\n");
        }));
        return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (saved is null || reads is null || log is null) throw new IOException("Default callback did not run.");
        try { _ = reads.GetCwd(saved); throw new IOException("Saved callback unexpectedly remained usable."); }
        catch (InvalidOperationException) { await File.AppendAllTextAsync(log, "stale-rejected\n"); }
        await File.AppendAllTextAsync(log, "dispose\n");
    }
}
