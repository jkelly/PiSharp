using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;

internal static class McpCombinedNativeInitializationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-combined-native.one-native-activation-before-mcp-publication", SingleActivation),
        ("mcp-combined-native.failed-activation-aborts-inert-stage", FailedActivation),
        ("mcp-combined-native.commit-conflict-joins-exact-native-scope", Conflict)
    ];
    private static void Require(bool value) { if (!value) throw new IOException("Combined MCP native initialization assertion failed."); }
    private static string OwnerPath => Path.Combine(Path.GetTempPath(), "admitted-combined-mcp");
    private static JsonData Config => JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"direct\"}");
    private sealed class Extension(Action initialize, Action dispose) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        { initialize(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }
    private static McpExtensionRegistrationBridge Bridge(ExtensionRegistry registry)
        => new(registry, McpConfigurationReader.Load(null, null, true));

    private static async Task SingleActivation()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry);
        IMcpServerRegistrationFacade? facade = null; var initialized = 0; var disposed = 0;
        var prepared = bridge.PrepareNativeInitialization(new Extension(() =>
        { initialized++; facade!.RegisterMcpServer("one", Config); }, () => disposed++), OwnerPath, x => facade = x);
        var activation = registry.ActivateAsync("one", prepared.DecoratedExtension);
        var scope = await activation;
        Require(initialized == 1 && bridge.CaptureSnapshot().RegisteredServers.IsEmpty);
        var commit = prepared.CommitAsync(scope, activation); var owner = await commit;
        Require(ReferenceEquals(owner.ActivationOriginal, activation) && bridge.CaptureSnapshot().RegisteredServers.Length == 1);
        try { _ = prepared.CommitAsync(scope, activation); throw new IOException("Repeated commit admitted."); }
        catch (InvalidOperationException) { }
        var disposal = scope.DisposeAsync().AsTask(); await disposal;
        await bridge.RetireClosedOwnerAsync(scope, disposal);
        Require(disposed == 1 && bridge.CaptureSnapshot().RegisteredServers.IsEmpty);
    }

    private static async Task FailedActivation()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry);
        IMcpServerRegistrationFacade? facade = null; var disposed = 0; var cause = new IOException("initializer failure");
        var prepared = bridge.PrepareNativeInitialization(new Extension(() =>
        { facade!.RegisterMcpServer("hidden", Config); throw cause; }, () => disposed++), OwnerPath, x => facade = x);
        var activation = registry.ActivateAsync("failed", prepared.DecoratedExtension);
        try { await activation; throw new IOException("Expected failed activation."); }
        catch (Exception) when (activation.IsFaulted) { prepared.Abort(); }
        Require(disposed == 1 && activation.Exception is not null && bridge.CaptureSnapshot().RegisteredServers.IsEmpty);
        try { facade!.GetMcpServers(); throw new IOException("Aborted facade admitted."); }
        catch (InvalidOperationException) { }
    }

    private static async Task Conflict()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry);
        var foreign = await registry.ActivateAsync("foreign", new Extension(() => { }, () => { }));
        bridge.BindOwner(foreign, OwnerPath).RegisterMcpServer("same", Config);
        IMcpServerRegistrationFacade? facade = null; var disposed = 0;
        var prepared = bridge.PrepareNativeInitialization(new Extension(() => facade!.RegisterMcpServer("same", Config),
            () => disposed++), OwnerPath, x => facade = x);
        var activation = registry.ActivateAsync("conflict", prepared.DecoratedExtension); var scope = await activation;
        var commit = prepared.CommitAsync(scope, activation);
        try { await commit; throw new IOException("Conflicting commit admitted."); }
        catch (Exception) when (commit.IsFaulted) { }
        Require(disposed == 1 && scope.ExtensionLifetimeCancellationToken.IsCancellationRequested &&
            bridge.CaptureSnapshot().RegisteredServers.Length == 1);
        var cleanup = foreign.DisposeAsync().AsTask(); await cleanup; await bridge.RetireClosedOwnerAsync(foreign, cleanup);
    }
}
