// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/index.ts, which only re-exports. The PiSharp.Durable namespace is
// the package root (the Testing namespace is the testing subpath; the SQLite and tools assemblies hold the storage/sqlite, env, tools and
// tool-search subpaths; search.ts, which the package does not export, is the internal Search). Its type-only exports map as the port map
// records: task inputs, checkpoints and results (GenerationInput, ToolTaskInput, ToolTaskCheckpoint, ToolTaskResult and the like) and
// document states (LiveState, InboxState, ProviderState, NestedCallState, and the slot types ToolSlot, NestedToolSlot, NestedToolSummary,
// CompactionStatus and CompactionFailure) are JSON values; the helper types AnyTask, HooksOf, HookResult, UserInput and
// InputSubmissionDraft become DurableTask, a task's hook class, the hook delegates' return types and SubmissionDraft; ToolHookCall is a
// hook's call JSON with its parent; the watch types upstream re-exports from Chord (WatchHandle, WatchEnd, WatchCall, NativeWatchCall)
// and DocumentWatch are Chord's IWatchHandle<T>, WatchEnd, WatchCall<T> and NativeWatchCall<T> in PiSharp.Durable.Chord, and
// DocumentState is gone with documentState(); the document definition and semantics types are the scope-typed
// tokens of Documents.cs (NestedCallDoc is HarnessTasks.NestedCallDoc); TableCommitChange is ICommitChange; SessionFailed and
// StorageRequestError are SessionFailedException and StorageRequestException. Pi v1.5.0 sync (area observers, 8b0caefc2): the Observer
// and Observation exports are the classes Observer<T> and Observation<T> (TypesSession.cs).
// Area view (f3255de76): ConversationWindow is the record of View.cs; ConversationTranscript, ConversationTranscriptOptions and EntryLoader
// keep their names, and TranscriptItem and TranscriptView are ConversationTranscriptItem and ConversationTranscriptView (Transcript.cs).
namespace PiSharp.Durable;

/// <summary>The upstream source this port follows (decision 0005): Pi's <c>packages/durable</c> and the Chord subset it uses, at the
/// pinned Pi v1.5.0 commit. The port began at Pi v1.1.0, was synced to v1.2.0 and is being synced to v1.5.0:
/// <c>compatibility/durable-port-map.json</c> maps each upstream file to its C# counterpart and status and marks the files whose port still
/// follows v1.2.0 (<c>needs-sync</c>), and <c>compatibility/durable-sync-1.5.0-worklist.json</c> groups the remaining work.</summary>
public static class DurableUpstream
{
    /// <summary>The pinned Pi commit.</summary>
    public const string Commit = "f9a26365568127461c47a07a37dcb42c140a3ac5";
    /// <summary>The upstream package and version.</summary>
    public const string Package = "@earendil-works/pi-durable@1.5.0";
}
