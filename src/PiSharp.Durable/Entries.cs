// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/entries.ts, plus the Entry interface of types.ts. Upstream's
// Entry<D> also types an entry's data at compile time (ToolResultEntry's diagnostics, CompactionEntry's reason); entry data here is
// JsonValue, and typed views of those payloads join with the tool (P9) and compaction (P11) tasks.
using System.Diagnostics.CodeAnalysis;

namespace PiSharp.Durable;

/// <summary>A typed entry kind with a narrowing guard (upstream <c>Entry</c>).</summary>
public sealed class Entry
{
    internal Entry(string kind) { Kind = kind; }

    /// <summary>The persisted <see cref="EntryRecord.Kind"/>.</summary>
    public string Kind { get; }

    /// <summary>Whether <paramref name="entry"/> is present and of this kind.</summary>
    public bool Is([NotNullWhen(true)] EntryRecord? entry) => entry is not null && string.Equals(entry.Kind, Kind, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Kind;
}

/// <summary>Entry kinds: <see cref="DefineEntry"/> and the built-in kinds.</summary>
public static class Entries
{
    /// <summary>Defines a typed entry kind whose <see cref="Entry.Is"/> guard narrows by <see cref="EntryRecord.Kind"/>.</summary>
    public static Entry DefineEntry(string kind) =>
        string.IsNullOrEmpty(kind) ? throw new ArgumentException("Entry kind must be a non-empty string") : new(kind);

    /// <summary>User input: <c>model</c> is <c>[UserMessage]</c>. Written by submissions.</summary>
    public static Entry UserEntry { get; } = DefineEntry("pi.user");

    /// <summary>Provider result with any stop reason: <c>model</c> is <c>[AssistantMessage]</c>. Written by generation.</summary>
    public static Entry AssistantEntry { get; } = DefineEntry("pi.assistant");

    /// <summary>Positional prompt and tool change: <c>model</c> is <c>[SystemMessage]</c> with empty <c>content</c>.</summary>
    public static Entry SystemEntry { get; } = DefineEntry("pi.system");

    /// <summary>Tool result: <c>model</c> is <c>[ToolResultMessage]</c>, whose content ends with the rendered diagnostics block; <c>data</c>
    /// is <c>{diagnostics: ToolDiagnostic[]}</c>. Written by tool tasks, and by generation for calls it did not offer.</summary>
    public static Entry ToolResultEntry { get; } = DefineEntry("pi.tool-result");

    /// <summary>Start of a new context: always <c>head: "self"</c>, with <c>model</c> absent for a plain reset or <c>[UserMessage]</c>
    /// carrying the handoff text. Written by <c>Conversation.reset()</c> and the <c>handoff</c> tool control.</summary>
    public static Entry ResetEntry { get; } = DefineEntry("pi.reset");

    /// <summary>Compaction summary: <c>model</c> is <c>[UserMessage]</c> with the wrapped summary, <c>head</c> the first kept entry;
    /// <c>data</c> is <c>{reason: CompactionReason}</c>. Written by compaction tasks, directly or through a write submission.</summary>
    public static Entry CompactionEntry { get; } = DefineEntry("pi.compaction");
}
