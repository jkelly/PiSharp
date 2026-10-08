using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;

namespace PublishedSystemImportFixture;

/// <summary>Node-free compiled probe of the exact private supervisor import and an unapproved private caller.</summary>
public sealed class Entry : IPiSharpExtension
{
    public Entry() => Signals.Mark("constructor");

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Signals.Mark("initialize");
        registry.RegisterTool(new("import", "fixture.system.import", "Authored system import fixture",
            JsonData.Parse("{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"approved\",\"other\",\"unknown\",\"hold\"]}},\"required\":[\"action\"],\"additionalProperties\":false}"),
            async (arguments, context, token) =>
            {
                var action = arguments.Value.GetProperty("action").GetString();
                if (action is not ("approved" or "other" or "unknown" or "hold")) throw new InvalidOperationException("Unknown authored import action.");
                if (action == "unknown")
                {
                    _ = NativeLibrary.Load("pisharp-fixture-missing-native", typeof(NodeWorkerLaunch).Assembly, null);
                    throw new InvalidOperationException("An unknown native import was incorrectly admitted.");
                }
                var path = Signals.FreshChild(action);
                var created = action == "other" ? CreateDirectoryW(path, IntPtr.Zero) : SupervisorDirectory(path);
                if (!created) throw new IOException("Actual fixture native directory creation failed.");
                if (action == "hold")
                {
                    Signals.Signal("callback-entered");
                    try { await Signals.Wait("callback-release"); token.ThrowIfCancellationRequested(); }
                    finally { Signals.Mark("callback-closed"); }
                }
                var hostContext = AssemblyLoadContext.GetLoadContext(typeof(NodeWorkerLaunch).Assembly);
                var entryContext = AssemblyLoadContext.GetLoadContext(typeof(Entry).Assembly);
                return JsonData.Parse(JsonSerializer.Serialize(new
                {
                    created, privateHost = ReferenceEquals(hostContext, entryContext) && hostContext != AssemblyLoadContext.Default,
                    context = hostContext?.Name,
                    sharedContracts = AssemblyLoadContext.GetLoadContext(typeof(JsonData).Assembly) == AssemblyLoadContext.Default,
                    sharedAbstractions = AssemblyLoadContext.GetLoadContext(typeof(IPiSharpExtension).Assembly) == AssemblyLoadContext.Default
                }));
            }));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Signals.Mark("dispose-entered");
        // Actual plugin shutdown still uses its approved import before the loader may release the owned OS reference.
        if (!SupervisorDirectory(Signals.FreshChild("dispose-native"))) throw new IOException("Native import was unavailable during joined disposal.");
        Signals.Signal("dispose-entered");
        await Signals.Wait("dispose-release");
        Signals.Mark("dispose-closed");
    }

    private static bool SupervisorDirectory(string path)
    {
        var method = typeof(NodeWorkerLaunch).GetMethod("CreateDirectoryW", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The audited supervisor import is missing.");
        return (bool)(method.Invoke(null, [path, IntPtr.Zero]) ?? throw new InvalidOperationException("The native import returned no result."));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}

internal static class Signals
{
#pragma warning disable CA2255 // Authored module side-effect probe; admission must precede its execution.
    [ModuleInitializer]
    internal static void Module() => Mark("module");
#pragma warning restore CA2255
    private static string Root
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("PISHARP_SYSTEM_IMPORT_ROOT")
                ?? throw new InvalidOperationException("Explicit authored fixture root is required.");
            if (!Path.IsPathFullyQualified(value) || !Directory.Exists(value) || value != Path.GetFullPath(value))
                throw new IOException("An existing canonical fixture root is required.");
            for (var path = value; path is not null; path = Path.GetDirectoryName(path))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked fixture roots are rejected.");
            return value;
        }
    }
    internal static void Mark(string stage) => File.AppendAllText(Path.Combine(Root, "effects.markers"), stage + "\n");
    internal static string FreshChild(string name)
    {
        var path = Path.Combine(Root, name);
        if (Path.Exists(path)) throw new IOException("The authored native child must be fresh.");
        return path;
    }
    private static Dictionary<string, TaskCompletionSource>? Gates =>
        AppContext.GetData("PiSharp.SystemImport.Gates") as Dictionary<string, TaskCompletionSource>;
    internal static void Signal(string key) { if (Gates?.TryGetValue(key, out var value) == true) value.TrySetResult(); }
    internal static Task Wait(string key) => Gates?.TryGetValue(key, out var value) == true ? value.Task : Task.CompletedTask;
}
