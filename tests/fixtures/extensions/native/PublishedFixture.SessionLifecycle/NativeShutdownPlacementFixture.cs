using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace NativeShutdownPlacementFixture
{
    public sealed class Entry : IPiSharpExtension
    {
        private JsonData? settings;
        private JsonData Settings => settings ?? throw new InvalidOperationException("Shutdown fixture settings were not bound.");
        private string Marker => Settings.Value.GetProperty("marker").GetString()!;
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // The actual loader uses LoadFromStream; Assembly.Location is not a resource directory.
            // Contracts is shared across contexts, so this unique test-owned immutable value can
            // cross the fixture load context without reopening production resource authority.
            settings = AppContext.GetData("PiSharp.ShutdownPlacementFixture." + registry.OwnerId) as JsonData
                ?? throw new InvalidOperationException("Shutdown fixture has no scoped settings bridge.");
            registry.Observe(new("shutdown", "session_shutdown", async (value, context, lifetime) =>
            {
                if (value.Value.GetProperty("reason").GetString() != "quit" || !lifetime.CanBeCanceled ||
                    lifetime != context.OperationCancellationToken || context.SessionCancellationToken.CanBeCanceled ||
                    context is IExtensionSessionActionsContext or IExtensionToolContext or IExtensionCommandContext or
                        IExtensionSessionCatalogContext or IExtensionSessionContextEditContext or
                        IExtensionSessionCompactionContext or IExtensionToolActivationContext)
                    throw new InvalidOperationException("Shutdown received ordinary interactive/action authority.");
                var snapshot = (context as IExtensionSessionContext)?.SessionSnapshot
                    ?? throw new InvalidOperationException("Retained shutdown snapshot is missing.");
                var ui = ((IExtensionUiContext)context).Ui;
                if (!ui.Capabilities.Features.IsEmpty || (await ui.PublishAsync(new ExtensionUiTitle("forbidden"))).Kind != ExtensionUiOutcomeKind.Unavailable)
                    throw new InvalidOperationException("Retired shutdown UI regained publication authority.");
                await File.WriteAllTextAsync(Settings.Value.GetProperty("view").GetString()!, JsonSerializer.Serialize(new
                {
                    snapshot.SessionId, snapshot.Generation, snapshot.SelectedLeafId,
                    persistence = snapshot.Persistence.ToString(),
                    entries = snapshot.BranchEntries.Select(entry => entry.Value).ToArray(),
                    noActions = true, noUi = true
                }));
                await File.AppendAllTextAsync(Marker, "shutdown\n");
                if (Settings.Value.GetProperty("throwHandler").GetBoolean()) throw new IOException("authored shutdown fixture handler failure");
            }));
            return ValueTask.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            if (settings is null) return; // Initialization failed before adopting marker resources.
            await File.AppendAllTextAsync(Marker, "dispose\n");
            if (Settings.Value.GetProperty("throwDispose").GetBoolean()) throw new IOException("authored shutdown fixture disposal failure");
        }
    }
}
