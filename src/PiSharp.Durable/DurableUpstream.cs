// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/index.ts, which only re-exports. The PiSharp.Durable namespace is
// the package root (the Testing namespace is the testing subpath; the SQLite and tools assemblies hold the storage/sqlite, env, tools and
// tool-search subpaths, and PiSharp.Durable.Store the storage/store subpath; search.ts, which the package does not export, is the internal
// Search). Its type-only exports map as the port map records: task inputs, checkpoints and results (GenerationInput, ToolTaskInput,
// ToolTaskCheckpoint, ToolTaskResult and the like) and document states (LiveState, LiveRun, InboxState, ProviderState, NestedCallState,
// and the slot types ToolSlot, NestedToolSlot, NestedToolSummary, CompactionStatus and CompactionFailure) are JSON values (Live.cs reads
// pi.live as JSON); the helper types AnyTask, HooksOf, HookResult, UserInput and InputSubmissionDraft become DurableTask, a task's hook
// class, the hook delegates' return types and SubmissionDraft; ToolHookCall is a hook's call JSON with its parent; the watch types upstream
// re-exports from Chord (WatchHandle, WatchEnd, WatchCall, NativeWatchCall) and DocumentWatch are Chord's IWatchHandle<T>, WatchEnd,
// WatchCall<T> and NativeWatchCall<T> in PiSharp.Durable.Chord, and DocumentState is gone with documentState(); the document definition and
// semantics types are the scope-typed tokens of Documents.cs (NestedCallDoc is HarnessTasks.NestedCallDoc); TableCommitChange is
// ICommitChange; SessionFailed and StorageRequestError are SessionFailedException and StorageRequestException.
// What Pi v1.5.0 adds, by area of the v1.5.0 sync:
// - errors (admission, harness-options): ConversationIdle and ReadOnlySession are ConversationIdleException and ReadOnlySessionException
//   (Errors.cs).
// - agent.ts (restarts): DEFAULT_RESTART_LIMIT is Agents.DefaultRestartLimit (Harness/Agent.cs).
// - define.ts and types.ts (run-end): runEndTask is Define.RunEndTask, and RunEndTask and RunSummary are records of Harness/Types.cs.
// - harness.ts (model-requests): DEFAULT_HARNESS_IDENTITY is Harness.DefaultIdentity, empty in PiSharp: no attribution (decision 0013).
// - live.ts (run-failure, live-turns): RunFailure and RunFailureReason are Live.cs's record and enum; LiveRun is pi.live.run's JSON.
// - registry-reader.ts (registry-reader, 6eb99ad65): registryReader is RegistryReader.Create in PiSharp.Durable.Store (RegistryReader.cs),
//   which needs Chord v2's registry value, so PiSharp.Durable keeps no reference to PiSharp.Chord (decision 0005 §4).
// - scheduler.ts (reports): BlockedReason is the enum of Harness/Types.cs.
// - telemetry.ts (telemetry): DURABLE_TELEMETRY_SCHEMA is DurableTelemetry.Schema (Harness/Telemetry.cs).
// - transcript.ts and view.ts (view, f3255de76): ConversationTranscript, ConversationTranscriptOptions and EntryLoader keep their names, and
//   TranscriptItem and TranscriptView are ConversationTranscriptItem and ConversationTranscriptView (Transcript.cs); ConversationWindow is
//   the record of View.cs.
// - harness/types.ts (reports, sleep, run-failure, harness-options): AfterFailedRun and CompactionSettings are Types.cs's enum and record,
//   and Wake is the record wake() returns.
// - types.ts: CommitObservation, CommitObserver, Observation and Observer (observers, vocabulary, 8b0caefc2 and 5f312dac4) are the classes
//   of TypesSession.cs, Observer<T> and Observation<T> generic; CommitOptions and CommitPersistence (deferred-commits) are its records;
//   CommitResult (storage-batches) is TypesStorage.cs's; HostCallback, Report, ReportFrom, ReportInfo and ReportTask (reports) are
//   TypesReports.cs's (ReportInfo one sealed record with Kind and optional fields); SetTimer (sleep) is the delegate of Timers.cs.
// - Removed: watchEvents and its types (AgentEvent, AgentEventStream, SnapshotEvent, MessageChange, ToolEventCall; area events, f3255de76),
//   as upstream removed them; PiSharp.Durable.Hosting keeps the projection, PiSharp-only, for the device wire (AgentEvents.cs). DocumentState
//   (area watches, 92ab53602), with Session.documentState().
namespace PiSharp.Durable;

/// <summary>The upstream source this port follows (decision 0005): Pi's <c>packages/durable</c> and the Chord subset it uses, at the
/// pinned Pi v1.5.0 commit. The port began at Pi v1.1.0 and was synced to v1.2.0 and v1.5.0:
/// <c>compatibility/durable-port-map.json</c> maps each upstream file to its C# counterpart and status, and marks the files whose port still
/// follows v1.2.0 (<c>needs-sync</c>: Chord v1 files phase 6 of the v1.5.0 sync re-syncs); <c>compatibility/durable-sync-1.5.0-worklist.json</c>
/// records how the v1.5.0 sync was split.</summary>
public static class DurableUpstream
{
    /// <summary>The pinned Pi commit.</summary>
    public const string Commit = "f9a26365568127461c47a07a37dcb42c140a3ac5";
    /// <summary>The upstream package and version.</summary>
    public const string Package = "@earendil-works/pi-durable@1.5.0";
}
