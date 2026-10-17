// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/index.ts, which only re-exports. The PiSharp.Durable namespace is
// the package root (the Testing namespace is the testing subpath; the SQLite and tools assemblies hold the storage/sqlite, env, tools and
// tool-search subpaths; search.ts, which the package does not export, is the internal Search). Its type-only exports map as the port map
// records: task inputs, checkpoints and results (GenerationInput, ToolTaskInput, ToolTaskCheckpoint, ToolTaskResult and the like) and
// document states (LiveState, InboxState, ProviderState, NestedCallState, and the slot types ToolSlot, NestedToolSlot, NestedToolSummary,
// CompactionStatus and CompactionFailure) are JSON values; the helper types AnyTask, HooksOf, HookResult, UserInput and
// InputSubmissionDraft become DurableTask, a task's hook class, the hook delegates' return types and SubmissionDraft; ToolHookCall is a
// hook's call JSON with its parent; the watch types are IWatchHandle<T>; the document definition and semantics types are the scope-typed
// tokens of Documents.cs (NestedCallDoc is HarnessTasks.NestedCallDoc); TableCommitChange is ICommitChange; SessionFailed and
// StorageRequestError are SessionFailedException and StorageRequestException.
namespace PiSharp.Durable;

/// <summary>The upstream source this port follows (decision 0005): Pi's <c>packages/durable</c> and the Chord subset it uses, at the
/// pinned Pi v1.2.0 commit. The port began at Pi v1.1.0 and was synced to v1.2.0 (<c>compatibility/durable-sync-1.2.0-worklist.json</c>,
/// areas a to m): <c>compatibility/durable-port-map.json</c> maps each upstream file to its C# counterpart and status, and no ported file
/// still follows v1.1.0 (no row is <c>needs-sync</c>).</summary>
public static class DurableUpstream
{
    /// <summary>The pinned Pi commit.</summary>
    public const string Commit = "06a466935536f8c9bcacb2d5efc0fc021be1bdff";
    /// <summary>The upstream package and version.</summary>
    public const string Package = "@earendil-works/pi-durable@1.2.0";
}
