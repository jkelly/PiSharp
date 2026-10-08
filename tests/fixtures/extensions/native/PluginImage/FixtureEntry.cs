using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PublishedImageFixture;

/// <summary>Authored native plugin fixture. All effects are confined to its explicit temporary marker file.</summary>
public sealed class Entry : IPiSharpExtension
{
    public Entry() => Mark("constructor");
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Mark("initialize");
        registry.RegisterTool(new("image", "fixture.image.render", "Authored native mixed image result",
            JsonData.Parse("{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\"}},\"required\":[\"mode\"],\"additionalProperties\":false}"),
            async (arguments, context, token) =>
            {
                token.ThrowIfCancellationRequested(); var mode = arguments.Value.GetProperty("mode").GetString();
                if (mode is not ("plain" or "patch")) throw new InvalidOperationException("Unknown authored image fixture mode.");
                Mark("execute:" + mode);
                try
                {
                    if (mode == "patch")
                    {
                        if (context is not IExtensionToolInvocationContext invocation)
                            throw new InvalidOperationException("Real invocation/progress context is required.");
                        await invocation.ReportUpdateAsync(JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"partial image\"},{\"type\":\"image\",\"data\":\"AQ==\",\"mimeType\":\"image/png\",\"source\":\"progress\"}],\"future\":null}"), token);
                        Mark("progress-acknowledged");
                    }
                    token.ThrowIfCancellationRequested();
                    return JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"before image\"},{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\",\"source\":\"native\"},{\"type\":\"text\",\"text\":\"after image\"}],\"details\":{\"mode\":\"" + mode + "\"},\"future\":null,\"opaque\":{\"ordered\":[2,1]}}");
                }
                finally { Mark("execute-closed"); }
            }));
        registry.RegisterToolResultHandler(new("image-patch", (result, _, token) =>
        {
            token.ThrowIfCancellationRequested(); Mark("result-hook");
            return ValueTask.FromResult<ExtensionToolResultPatch?>(result.ToolName == "fixture.image.render" &&
                result.Arguments.Value.GetProperty("mode").GetString() == "patch"
                ? new(JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"patched before\"},{\"type\":\"image\",\"data\":\"Ag==\",\"mimeType\":\"image/png\",\"source\":\"hook-one\"},{\"type\":\"image\",\"data\":\"Aw==\",\"mimeType\":\"image/jpeg\",\"source\":\"hook-two\"},{\"type\":\"text\",\"text\":\"patched after\"}],\"details\":{\"hook\":\"patched\"}}")) : null);
        }));
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() { Mark("dispose"); return ValueTask.CompletedTask; }
    [ModuleInitializer] internal static void Module() => Mark("module");
    private static void Mark(string stage)
    {
        var file = Environment.GetEnvironmentVariable("PISHARP_IMAGE_FIXTURE_MARKER");
        if (file is null) return;
        var full = Path.GetFullPath(file); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(file) || !full.StartsWith(temporary, StringComparison.Ordinal) ||
            Path.GetFileName(full) != "image-plugin.markers" || !Directory.Exists(Path.GetDirectoryName(full)) || file.Any(char.IsControl))
            throw new InvalidOperationException("Unowned authored image fixture marker.");
        File.AppendAllText(full, stage + "\n");
    }
}
