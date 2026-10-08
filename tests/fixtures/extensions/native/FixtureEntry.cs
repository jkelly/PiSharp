using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PublishedFixture;

public sealed class Entry : IPiSharpExtension
{
    private string owner = "not-initialized";
    public Entry() => Signals.Mark("constructor");
    public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        owner = registry.OwnerId;
        Signals.Mark("initialize");
        registry.RegisterTool(new("value", owner + ".value", "Published fixture value/echo", JsonData.EmptyObject,
            (arguments, _, _) =>
            {
                if (arguments.Value.TryGetProperty("probePrivateDefault", out _)) Assembly.Load(new AssemblyName("PublishedConsumer"));
                if (arguments.Value.TryGetProperty("probeNative", out _)) NativeLibrary.Load("pisharp-fixture-missing-native", typeof(Entry).Assembly, null);
                if (arguments.Value.TryGetProperty("probeHostAction", out _))
                    (AppContext.GetData("PiSharp.PublishedFixture.HostAction") as Action ?? throw new InvalidOperationException("The authored host action is absent."))();
                return ValueTask.FromResult(arguments.Value.TryGetProperty("echo", out _) ? arguments :
                JsonData.Parse("{\"version\":\"" + Fixture.Private.Value.Text + "\",\"sharedContracts\":" +
                    (AssemblyLoadContext.GetLoadContext(typeof(JsonData).Assembly) == AssemblyLoadContext.Default ? "true" : "false") +
                    ",\"sharedAbstractions\":" + (AssemblyLoadContext.GetLoadContext(typeof(IPiSharpExtension).Assembly) == AssemblyLoadContext.Default ? "true" : "false") + "}"));
            }));
        registry.Observe(new("observer", owner + ".notice", async (_, _, token) =>
        {
            Signals.Signal(owner + ".observe-entered");
            try { await Signals.Wait(owner + ".observe-release", CancellationToken.None); token.ThrowIfCancellationRequested(); }
            finally { Signals.Mark("observe-closed"); }
        }));
        Signals.Signal(owner + ".initialize-entered");
        await Signals.Wait(owner + ".initialize-release", cancellationToken);
#if INIT_FAIL
        throw new InvalidOperationException("authored published fixture initialization failure");
#endif
    }
    public async ValueTask DisposeAsync()
    {
        Signals.Mark("dispose");
        Signals.Signal(owner + ".dispose-entered");
        await Signals.Wait(owner + ".dispose-release", CancellationToken.None);
    }
}

internal static class Signals
{
    [ModuleInitializer]
    internal static void Module() => Mark("module");
    internal static void Mark(string stage)
    {
        var root = Environment.GetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS");
        if (root is not null) File.AppendAllText(Path.Combine(root, typeof(Entry).Assembly.GetName().Name + ".markers"), stage + "\n");
    }
    private static Dictionary<string, TaskCompletionSource>? Gates =>
        AppContext.GetData("PiSharp.PublishedFixture.Gates") as Dictionary<string, TaskCompletionSource>;
    internal static void Signal(string key) { if (Gates?.TryGetValue(key, out var gate) == true) gate.TrySetResult(); }
    internal static Task Wait(string key, CancellationToken token) =>
        Gates?.TryGetValue(key, out var gate) == true ? gate.Task.WaitAsync(token) : Task.CompletedTask;
}
