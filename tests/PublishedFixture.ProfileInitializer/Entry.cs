using PiSharp.Extensions;
using PiSharp.Contracts;

namespace PublishedFixture.ProfileInitializer;

public sealed class Entry : IPiSharpExtension
{
    private string? log;

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        log = AppContext.GetData("PiSharp.ProfileInitializer." + registry.OwnerId) as string
            ?? throw new IOException("Missing explicitly admitted profile log.");
        var observer = AppContext.GetData("PiSharp.ProfileInitializer.Shutdown." + registry.OwnerId) as Action<JsonData>
            ?? throw new IOException("Missing explicitly admitted shutdown observer.");
        registry.Observe(new("profile-shutdown", "session_shutdown", (observation, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            observer(observation);
            return ValueTask.CompletedTask;
        }));
        File.AppendAllText(log, "initialize\n");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        File.AppendAllText(log ?? throw new IOException("No initialized profile log."), "dispose\n");
        return ValueTask.CompletedTask;
    }
}
