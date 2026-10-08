using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;

internal static class McpInitializerRegistrationTests
{
    internal const string Prefix = "mcp-initializer-registration.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "held-initializer-is-inert-then-one-atomic-batch-and-promoted-facade", HeldInitializer),
        (Prefix + "failed-initializer-discards-staging-and-joins-native-cleanup", FailedInitializer),
        (Prefix + "owned-cancellation-discards-staging-and-joins-native-cleanup", CanceledInitializer),
        (Prefix + "faulted-oce-original-preserves-exact-fault-task", FaultedOce),
        (Prefix + "foreign-conflict-after-native-activation-rolls-back-owner", CommitConflict),
        (Prefix + "configured-precedence-and-one-explicit-binder", Configured)
    ];
    private static void Require(bool value) { if (!value) throw new IOException("MCP initializer registration assertion failed."); }
    private static string PathFor(string owner) => Path.Combine(Path.GetTempPath(), "admitted-mcp-initializer-" + owner);
    private static JsonData Config(string command = "inert") => JsonData.Parse("{\"command\":\"" + command + "\",\"exposure\":\"direct\"}");
    private static McpExtensionRegistrationBridge Bridge(ExtensionRegistry registry) => new(registry, McpConfigurationReader.Load(null, null, true));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new IOException("Expected initializer failure."); }
    private static Exception Combine(Exception? primary, Exception next) => primary is null ? next : new AggregateException(primary, next);
    private static async Task<Exception?> Join(Task? original, bool expectedFailureObserved, Exception? primary)
    {
        if(original is not null)try { await original; }catch(Exception cleanup)
        { if(!expectedFailureObserved)primary=Combine(primary,original.Exception ?? cleanup); }
        return primary;
    }
    private sealed class Extension(Func<IExtensionRegistry,CancellationToken,ValueTask> initialize, Action? disposed = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry scope, CancellationToken token) => initialize(scope, token);
        public ValueTask DisposeAsync() { disposed?.Invoke(); return ValueTask.CompletedTask; }
    }
    private static async Task Retire(McpExtensionRegistrationBridge bridge, McpInitializedRegistrationOwner owner)
    { var disposal = owner.Scope.DisposeAsync().AsTask(); await bridge.RetireClosedOwnerAsync(owner.Scope, disposal); }
    private static bool ContainsOriginal(Exception error, Task original) =>
        error is McpInitializationOriginalException witness && ReferenceEquals(witness.Original, original) ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(child => ContainsOriginal(child, original)) ||
        error.InnerException is {} inner && ContainsOriginal(inner, original);
    private static async Task HeldInitializer()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry);
        var entered = Gate(); var release = Gate(); IMcpServerRegistrationFacade? facade = null;
        Task<McpInitializedRegistrationOwner>? activation = null; Exception? primary = null;
        try
        {
            activation = bridge.ActivateOwnerAsync("held", PathFor("held"), new Extension(async (_, _) =>
            { facade!.RegisterMcpServer("one", Config()); facade.RegisterMcpServer("two", Config()); entered.TrySetResult(); await release.Task; }), value => facade = value);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!activation.IsCompleted && bridge.CaptureSnapshot().Revision == 0 && bridge.CaptureSnapshot().RegisteredServers.IsEmpty && facade!.GetMcpServers().Length == 2);
            release.TrySetResult(); var owner = await activation;
            Require(owner.ActivationOriginal.IsCompletedSuccessfully && ReferenceEquals(owner.Scope, owner.ActivationOriginal.Result));
            Require(bridge.CaptureSnapshot().Revision == 1 && bridge.CaptureSnapshot().RegisteredServers.Length == 2);
            facade!.RegisterMcpServer("three", Config()); Require(bridge.CaptureSnapshot().Revision == 2);
            await Retire(bridge, owner); Require(bridge.CaptureSnapshot().RegisteredServers.IsEmpty);
        }
        catch(Exception error) { primary = error; }
        finally
        {
            release.TrySetResult();
            if(activation is not null)try { await activation; }catch(Exception cleanup) { primary = primary is null ? activation.Exception ?? cleanup : new AggregateException(primary, activation.Exception ?? cleanup); }
        }
        if(primary is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private static async Task FailedInitializer()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry);
        var disposed = 0; IMcpServerRegistrationFacade? facade = null; IExtensionRegistry? scope = null;
        var original = Task.FromException(new AggregateException(new IOException("first"), new IOException("second")));
        var activation = bridge.ActivateOwnerAsync("failed", PathFor("failed"), new Extension((native, _) =>
        { scope = native; facade!.RegisterMcpServer("hidden", Config()); return new(original); }, () => disposed++), value => facade = value);
        var error = await Failure(activation);
        Require(ContainsOriginal(error, original) && original.IsFaulted && disposed == 1 && scope!.ExtensionLifetimeCancellationToken.IsCancellationRequested);
        Require(bridge.CaptureSnapshot().Revision == 0 && bridge.CaptureSnapshot().RegisteredServers.IsEmpty);
        _ = await Failure(Task.Run(() => facade!.GetMcpServers()));
    }
    private static async Task CanceledInitializer()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry); using var stop = new CancellationTokenSource();
        var entered = Gate(); var disposed = 0; IMcpServerRegistrationFacade? facade = null; Task<McpInitializedRegistrationOwner>? activation = null;
        var observed = false; Exception? primary = null;
        try
        {
            activation = bridge.ActivateOwnerAsync("canceled", PathFor("canceled"), new Extension(async (_, token) =>
            { facade!.RegisterMcpServer("hidden", Config()); entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }, () => disposed++), value => facade = value, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); _ = await Failure(activation); observed = true;
            Require(activation.IsCanceled && disposed == 1 && bridge.CaptureSnapshot().Revision == 0);
        }
        catch(Exception error) { primary = error; }
        finally
        {
            try { stop.Cancel(); } catch(Exception cleanup) { primary = Combine(primary, cleanup); }
            primary = await Join(activation, observed, primary);
        }
        if(primary is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private static async Task FaultedOce()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry);
        var original = Task.FromException(new OperationCanceledException("faulted original, no owning cancellation"));
        var activation = bridge.ActivateOwnerAsync("faulted-oce", PathFor("faulted-oce"), new Extension((_, _) => new(original)), _ => { });
        var error = await Failure(activation);
        Require(original.IsFaulted && activation.IsFaulted && ContainsOriginal(error, original) && bridge.CaptureSnapshot().Revision == 0);
        await UnownedCancellationBoundaries();
    }
    private static async Task UnownedCancellationBoundaries()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry);
        static McpInitializationUnownedCancellationException? Find(Exception error) =>
            error is McpInitializationUnownedCancellationException witness ? witness :
            error is AggregateException aggregate ? aggregate.InnerExceptions.Select(Find).FirstOrDefault(x => x is not null) :
            error.InnerException is {} inner ? Find(inner) : null;
        foreach (var binderFailure in new[] { true, false })
        {
            var cause = new OperationCanceledException("synchronous unowned initializer cancellation");
            var activation = bridge.ActivateOwnerAsync("sync-" + binderFailure, PathFor("sync"),
                new Extension((_, _) => throw cause), _ => { if (binderFailure) throw cause; });
            var error = await Failure(activation); var witness = Find(error);
            Require(activation.IsFaulted && witness is not null && witness.Original is null && ReferenceEquals(witness.Cause, cause));
            Require(error is McpInitializationOriginalException native && native.Original.IsFaulted);
        }
        using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var clear = Gate(); clear.TrySetCanceled(); var index = 0;
        foreach (var original in new[] { Task.FromCanceled(foreign.Token), clear.Task })
        {
            var activation = bridge.ActivateOwnerAsync("foreign-" + index++, PathFor("foreign"),
                new Extension((_, _) => new(original)), _ => { });
            var error = await Failure(activation); var witness = Find(error);
            Require(original.IsCanceled && activation.IsFaulted && witness is not null && ReferenceEquals(witness.Original, original));
            Require(error is McpInitializationOriginalException native && native.Original.IsFaulted);
        }
        Require(bridge.CaptureSnapshot().Revision == 0 && bridge.CaptureSnapshot().RegisteredServers.IsEmpty);
    }
    private static async Task CommitConflict()
    {
        await using var registry = new ExtensionRegistry(); var bridge = Bridge(registry);
        var foreign = await registry.ActivateAsync("foreign", new Extension((_, _) => ValueTask.CompletedTask));
        var foreignFacade = bridge.BindOwner(foreign, PathFor("foreign"));
        var entered = Gate(); var release = Gate(); var disposed = 0; IMcpServerRegistrationFacade? staged = null;
        Task<McpInitializedRegistrationOwner>? activation = null; var observed = false; Exception? primary = null;
        try
        {
            activation = bridge.ActivateOwnerAsync("racing", PathFor("racing"), new Extension(async (_, _) =>
            { staged!.RegisterMcpServer("race", Config("staged")); entered.TrySetResult(); await release.Task; }, () => disposed++), value => staged = value);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); foreignFacade.RegisterMcpServer("race", Config("foreign"));
            release.TrySetResult(); _ = await Failure(activation); observed = true;
            Require(disposed == 1 && bridge.CaptureSnapshot().Revision == 1 && bridge.CaptureSnapshot().RegisteredServers.Single().ExtensionPath == PathFor("foreign"));
        }
        catch(Exception error) { primary = error; }
        finally
        {
            release.TrySetResult(); primary = await Join(activation, observed, primary);
            Task? close = null;
            try { close = foreign.DisposeAsync().AsTask(); await bridge.RetireClosedOwnerAsync(foreign, close); }
            catch(Exception cleanup) { primary = Combine(primary, close?.Exception ?? cleanup); }
        }
        if(primary is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private static async Task Configured()
    {
        await using var registry = new ExtensionRegistry();
        var configured = McpConfigurationReader.Load(new("global", "{\"mcpServers\":{\"shared\":{\"command\":\"configured\"}}}"), null, true);
        var bridge = new McpExtensionRegistrationBridge(registry, configured); IMcpServerRegistrationFacade? facade = null;
        var owner = await bridge.ActivateOwnerAsync("configured", PathFor("configured"), new Extension((_, _) =>
        { facade!.RegisterMcpServer("shared", Config("extension")); return ValueTask.CompletedTask; }), value => facade = value);
        Require(bridge.CaptureSnapshot().Revision == 1 && bridge.CaptureSnapshot().Catalog.Servers.Single().Config.Raw.Value.GetProperty("command").GetString() == "configured");
        await Retire(bridge, owner);
        var invocations = 0; Action<IMcpServerRegistrationFacade> binder = _ => invocations++; binder += _ => invocations++;
        _ = await Failure(bridge.ActivateOwnerAsync("multicast", PathFor("multicast"), new Extension((_, _) => { invocations++; return ValueTask.CompletedTask; }), binder));
        Require(invocations == 0);
    }
}
