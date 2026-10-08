using PiSharp.Contracts;
using PiSharp.Extensions;

namespace NativeLoadoutFailureFixture;

public sealed class Entry : IPiSharpExtension
{
    public const string Tool = "fixture.loadout.failure";
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        registry.RegisterTool(new("probe", Tool, "canonical preparation fallback", JsonData.Parse(
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
            (_, _, _) => throw new InvalidOperationException("Preparation fixture must not execute."))
        { PrepareLoadout = _ => throw new InvalidOperationException("private authored preparation cause") });
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
