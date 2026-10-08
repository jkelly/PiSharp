using PiSharp.Cli.Extensions;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Runtime;

internal static class McpApplicationInitializerAdmissionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-application-initializer.read-before-native-factory-and-second-factory-refused", ReadAndRepeat),
        ("mcp-application-initializer.wrong-actual-registry-refused-before-binder-effects", WrongRegistry),
        ("mcp-application-initializer.actual-binder-unowned-cancellation-retains-fault-and-cleanup", BinderFault)
    ];
    private static void Check(bool value) { if (!value) throw new IOException("Application initializer assertion failed."); }
    private static NativeExtensionInitializerInstallation Native() => new(registry =>
        new(registry, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration()), (_, _) => { });
    private static McpApplicationHostInstallation Host(ExtensionRegistry registry) => new(registry,
        McpConfigurationReader.Load(null, null, true), [], (_, _) => throw new IOException("No generation acquisition admitted."));
    private static bool Refused(Action action)
    { try { action(); return false; } catch (InvalidOperationException) { return true; } }
    private static async Task ReadAndRepeat()
    {
        var registry = new ExtensionRegistry(); var calls = 0; var failures = new List<Exception>();
        var capability = new McpApplicationInitializerAdmission(actual => { calls++; return Host(actual); }, _ => throw new IOException("No binder admitted before activation."));
        try
        {
            var native = capability.Bind(Native(), out var read);
            Check(Refused(() => _ = read()) && calls == 0);
            var bridge = native.CreateMcpBridge!(registry); Check(ReferenceEquals(read().Bridge, bridge) && calls == 1);
            Check(Refused(() => _ = native.CreateMcpBridge!(registry)) && calls == 1);
        }
        catch (Exception error) { failures.Add(error); }
        finally { await McpApplicationHostInstallationTests.Join(registry.DisposeAsync().AsTask(), failures); }
        McpApplicationHostInstallationTests.Throw(failures);
    }
    private static async Task WrongRegistry()
    {
        var registry = new ExtensionRegistry(); var foreign = new ExtensionRegistry(); var failures = new List<Exception>(); var binds = 0;
        var capability = new McpApplicationInitializerAdmission(_ => Host(foreign), _ => binds++);
        try
        {
            var native = capability.Bind(Native(), out var read);
            Check(Refused(() => _ = native.CreateMcpBridge!(registry)) && Refused(() => _ = read()) && binds == 0 &&
                registry.CaptureSnapshot().Tools.IsEmpty && foreign.CaptureSnapshot().Tools.IsEmpty);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        { await McpApplicationHostInstallationTests.Join(registry.DisposeAsync().AsTask(), failures); await McpApplicationHostInstallationTests.Join(foreign.DisposeAsync().AsTask(), failures); }
        McpApplicationHostInstallationTests.Throw(failures);
    }
    private static async Task BinderFault()
    {
        var registry = new ExtensionRegistry(); var failures = new List<Exception>(); var extension = new Extension();
        var cause = new OperationCanceledException("unowned application binder"); var binders = 0;
        var capability = new McpApplicationInitializerAdmission(actual => Host(actual), _ => { binders++; throw cause; });
        McpPreparedNativeInitialization? prepared = null;
        try
        {
            var native = capability.Bind(Native(), out var read);
            var mcp = native.CreateMcpBridge!(registry); var bridge = native.CreateRegistrationBridge(registry);
            prepared = mcp.PrepareNativeInitialization(extension, Path.GetTempPath(), native.BindMcp!);
            var actual = bridge.ActivateOwnerAsync("actual-application", prepared.DecoratedExtension, _ => { });
            try { _ = await McpApplicationHostOriginals.Observe(actual, "actual-application-binder-activation"); throw new IOException("Unowned binder cancellation accepted."); }
            catch (Exception)
            {
                var evidence = McpApplicationHostOriginals.Evidence(actual);
                Check(actual.IsFaulted && evidence.Aggregate is not null && Contains(evidence.Aggregate, cause) &&
                    binders == 1 && extension.Initializations == 0 && extension.Disposals == 1 &&
                    ReferenceEquals(read().Bridge, mcp) && mcp.CaptureSnapshot().Revision == 0);
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { prepared?.Abort(); } catch (Exception error) { failures.Add(error); }
            await McpApplicationHostInstallationTests.Join(registry.DisposeAsync().AsTask(), failures);
        }
        McpApplicationHostInstallationTests.Throw(failures);
    }
    private static bool Contains(Exception error, Exception expected) => ReferenceEquals(error, expected) ||
        (error is AggregateException aggregate ? aggregate.InnerExceptions.Any(child => Contains(child, expected)) :
            error.InnerException is { } inner && Contains(inner, expected));
    private sealed class Configuration : IExtensionProviderConfigurationAdapter
    { public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => throw new IOException("No provider admitted."); }
    private sealed class Extension : IPiSharpExtension
    {
        internal int Initializations, Disposals;
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { Initializations++; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
