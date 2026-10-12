// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/compaction.ts (the CompactionTask identity). TODO(P11): the
// compaction state machine (phases select, summarize and retry, its abort handler, range selection and summaries) is a placeholder whose
// handlers fault with "not ported yet (P11)".
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the compaction task (upstream <c>CompactionTask</c>, kind <c>pi.compaction</c>, version 1).</summary>
public static partial class HarnessTasks
{
    /// <summary>The task. TODO(P11): every phase and the abort handler fault until compaction is ported.</summary>
    public static DurableTask CompactionTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.compaction",
        Version = 1,
        Initial = _ => JsonValue.Parse("""{"phase":"select"}"""),
        Phases = BuiltinPlaceholders.Phases("pi.compaction", "P11", "select", "summarize", "retry"),
        Abort = BuiltinPlaceholders.Handler("pi.compaction", "P11"),
    });
}
