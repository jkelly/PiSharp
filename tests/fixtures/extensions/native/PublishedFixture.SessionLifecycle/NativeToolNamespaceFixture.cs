using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace NativeToolNamespaceFixture
{
    // Authored native consumer loaded through the existing explicit manifest/approval contract.
    public sealed class Entry : IPiSharpExtension
    {
        public const string Tool = "fixture.namespace.probe";
        public const string PreparedDescription = "Namespace metadata reached actual CLI session preparation";
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            registry.RegisterTool(new("probe", Tool, "canonical namespace probe", JsonData.Parse(
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
                (_, _, _) => throw new InvalidOperationException("This namespace fixture must not execute a tool."))
            {
                Namespace = new("mcp__native_fixture", "Authored namespace description"),
                PrepareLoadout = loadout =>
                {
                    if (loadout.GetNamespace(Tool) != new ToolNamespace("mcp__native_fixture", "Authored namespace description") ||
                        loadout.GetNamespace("read") is not null || loadout.GetNamespace("unknown") is not null)
                        throw new InvalidOperationException("Namespace missing or synthesized in actual host preparation.");
                    return new() { Descriptions = ImmutableDictionary<string, string>.Empty.Add(Tool, PreparedDescription) };
                }
            });
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
