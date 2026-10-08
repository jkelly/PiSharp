using PiSharp.Extensions;
using System.Runtime.CompilerServices;

namespace PublishedFixture;

public sealed class Entry : IPiSharpExtension
{
    public Entry() => Mark("constructor");
    [ModuleInitializer]
    internal static void Module() => Mark("module");
    private static void Mark(string stage)
    {
        var root = Environment.GetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS");
        if (root is not null) File.AppendAllText(Path.Combine(root, "PublishedFixture.Future.markers"), stage + "\n");
    }
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
