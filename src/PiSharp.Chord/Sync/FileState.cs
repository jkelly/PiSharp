// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/state.ts.
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Sync;

/// <summary>Per-session sync state, replaced atomically. Losing this state does not lose session data.</summary>
public sealed class FileSyncStateStore(ISyncFileSystem fs, string directory) : ISyncStateStore
{
    public async Task<SyncState?> GetAsync(string sessionId, ChordContext context)
    {
        var path = await PathAsync(sessionId, context).ConfigureAwait(false);
        var result = await fs.ReadTextFileAsync(path, context).ConfigureAwait(false);
        if (result.IsNotFound) return null;
        var stored = JsonNode.Parse(result.Unwrap())!.AsObject();
        return new(stored["syncedHead"]?.GetValue<long>(), stored["pulling"]?.GetValue<long>());
    }
    public async Task SetAsync(string sessionId, SyncState state, ChordContext context)
    {
        var name = SyncPaths.CheckSessionId(sessionId) + ".json";
        var json = new JsonObject();
        if (state.SyncedHead is { } head) json["syncedHead"] = head;
        if (state.Pulling is { } pulling) json["pulling"] = pulling;
        await SyncFiles.WriteAtomicAsync(fs, directory, name,
            SyncFiles.Once(Encoding.UTF8.GetBytes(json.ToJsonString())), context).ConfigureAwait(false);
    }
    public async Task DeleteAsync(string sessionId, ChordContext context)
    {
        var path = await PathAsync(sessionId, context).ConfigureAwait(false);
        (await fs.RemoveAsync(path, new(Force: true), context).ConfigureAwait(false)).Unwrap();
    }
    private async Task<string> PathAsync(string sessionId, ChordContext context)
        => (await fs.JoinPathAsync([directory, SyncPaths.CheckSessionId(sessionId) + ".json"], context).ConfigureAwait(false)).Unwrap();
}
