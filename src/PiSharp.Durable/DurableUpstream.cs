// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/index.ts, which only re-exports. The PiSharp.Durable namespace is
// the package root (PiSharp.Durable.Testing the testing subpath, PiSharp.Durable.Sqlite and PiSharp.Durable.Tools its storage/sqlite, env and
// tools subpaths). Its type-only exports map as the port map records: task inputs, checkpoints and results (GenerationInput, ToolTaskResult
// and the like) and document states (LiveState, InboxState, ProviderState) are JSON values; the helper types AnyTask, HooksOf, HookResult,
// UserInput and InputSubmissionDraft become DurableTask, a task's hook class, the hook delegates' return types and SubmissionDraft; the watch
// types are IWatchHandle<T>; the document definition and semantics types are the scope-typed tokens of Documents.cs; and TableCommitChange
// is ICommitChange.
namespace PiSharp.Durable;

/// <summary>The upstream source this port follows (decision 0005): Pi's <c>packages/durable</c> and the Chord subset it uses, at the
/// pinned Pi v1.1.0 commit. <c>compatibility/durable-port-map.json</c> maps each upstream file to its C# counterpart.</summary>
public static class DurableUpstream
{
    /// <summary>The pinned Pi commit.</summary>
    public const string Commit = "abe508e1b89912adde45528136c3221eb69acdd7";
    /// <summary>The upstream package and version.</summary>
    public const string Package = "@earendil-works/pi-durable@1.1.0";
}
