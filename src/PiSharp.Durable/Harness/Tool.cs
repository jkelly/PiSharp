// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/tool.ts (the ToolTask identity). TODO(P9): the tool state
// machine (phases call and execute, its abort handler, intent, replay, running output and the tool execution API) is a placeholder whose
// handlers fault with "not ported yet (P9)".
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>Built-in tasks: the tool task (upstream <c>ToolTask</c>, kind <c>pi.tool</c>, version 1).</summary>
public static partial class HarnessTasks
{
    /// <summary>The task. TODO(P9): every phase and the abort handler fault until the tool task is ported.</summary>
    public static DurableTask ToolTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pi.tool",
        Version = 1,
        Initial = _ => JsonValue.Parse("""{"phase":"call"}"""),
        Phases = BuiltinPlaceholders.Phases("pi.tool", "P9", "call", "execute"),
        Abort = BuiltinPlaceholders.Handler("pi.tool", "P9"),
    });
}
