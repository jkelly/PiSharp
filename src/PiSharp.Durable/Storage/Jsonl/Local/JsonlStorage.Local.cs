// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/storage/jsonl/node.ts. The local runtime side of the JSONL
// boundary: only this folder of Storage/Jsonl reaches the local file system (as only upstream's storage/jsonl/node.ts imports env/node.ts).
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env.Local;

namespace PiSharp.Durable.Storage.Jsonl;

public sealed partial class JsonlStorage
{
    /// <summary>Opens or creates a JSONL storage directory using the local file system (upstream <c>openNodeJsonlStorage</c>); a relative
    /// <paramref name="directory"/> resolves against the process's current directory.</summary>
    public static Task<JsonlStorage> OpenAsync(string directory, DurableContext context, JsonlStorageOptions? options = null) =>
        OpenAsync(directory, new LocalFileSystem(Environment.CurrentDirectory), context, options);
}
