using PiSharp.Contracts;
using PiSharp.Extensions;

namespace NativeProfileViewFixture;

public sealed class Entry : IPiSharpExtension
{
    private string? log;
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
    {
        var settings = AppContext.GetData("PiSharp.ProfileViewFixture." + registry.OwnerId) as JsonData
            ?? throw new InvalidOperationException("No profile-view fixture settings.");
        log = settings.Value.GetProperty("log").GetString()!;
        registry.Observe(new("start", "session_start", async (value, _, _) =>
            await File.AppendAllTextAsync(log!, "start:" + value.Value.GetProperty("reason").GetString() + "\n")));
        registry.Observe(new("shutdown", "session_shutdown", async (value, _, _) =>
            await File.AppendAllTextAsync(log!, "shutdown:" + value.Value.GetProperty("reason").GetString() + "\n")));
        return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    { if (log is not null) await File.AppendAllTextAsync(log, "dispose\n"); }
}
