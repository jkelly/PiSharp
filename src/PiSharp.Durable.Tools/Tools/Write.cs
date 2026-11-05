// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/tools/write.ts (v1.2.0 renamed the result's content to output)
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Tools;

/// <summary>Upstream <c>createWriteTool</c>.</summary>
internal static class WriteTool
{
    /// <summary><c>obj({ path, content }, { open: true })</c> as pi-schema builds it.</summary>
    internal static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to write (relative or absolute)"},"content":{"type":"string","description":"Content to write to the file"}},"required":["path","content"]}""");

    public static ToolRegistration Create() => Define.Tool(new ToolRegistration
    {
        Name = "write",
        Description = "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.",
        Parameters = Parameters,
        Execute = async (arguments, api, context) =>
        {
            var args = (JsonObjectValue)arguments;
            var path = ((JsonStringValue)args["path"]).Value;
            var content = ((JsonStringValue)args["content"]).Value;
            var env = ToolEnv.Require(api);
            var absolutePath = await PathUtils.ResolveToolPathAsync(env, path, context).ConfigureAwait(false);
            return await FileMutationQueue.RunAsync(env, absolutePath, async () =>
            {
                if (context.IsCancellationRequested) throw new OperationCanceledException("Operation aborted");
                (await env.WriteFileAsync(absolutePath, content, context).ConfigureAwait(false)).GetOrThrow();
                if (context.IsCancellationRequested) throw new OperationCanceledException("Operation aborted");
                return new ToolExecutionResult { Output = ToolContent.Text($"Successfully wrote to {path}") };
            }, context).ConfigureAwait(false);
        },
    });
}
