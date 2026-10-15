// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/entries.ts, plus the Entry interface of types.ts. Upstream's
// Entry<D> types an entry's data at compile time with D extends JsonValue; here Entry is a data-less kind (upstream Entry<never>) and
// Entry<TData> a kind whose data is the JsonValue subtype TData. The tool-result data stays the JSON object {diagnostics} the tool task (P9)
// writes in {severity, code?, message} order; a typed view of the compaction reason joins with the compaction task (P11).
using System.Diagnostics.CodeAnalysis;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>A typed entry kind with a narrowing guard (upstream <c>Entry</c>); this base is a data-less kind (upstream
/// <c>Entry&lt;never&gt;</c>).</summary>
public class Entry
{
    private protected Entry(string kind) { Kind = kind; }

    internal static Entry Create(string kind) => new(kind);

    /// <summary>The persisted <see cref="EntryRecord.Kind"/>.</summary>
    public string Kind { get; }

    /// <summary>Whether <paramref name="entry"/> is present and of this kind.</summary>
    public bool Is([NotNullWhen(true)] EntryRecord? entry) => entry is not null && string.Equals(entry.Kind, Kind, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Kind;
}

/// <summary>A typed entry kind whose data is <typeparamref name="TData"/> (upstream <c>Entry&lt;D&gt;</c>).</summary>
/// <typeparam name="TData">The JSON type of the entry's data.</typeparam>
public sealed class Entry<TData> : Entry where TData : JsonValue
{
    internal Entry(string kind) : base(kind) { }

    /// <summary>The typed view of <paramref name="entry"/> when it is present, of this kind and carries <typeparamref name="TData"/> data;
    /// otherwise null.</summary>
    public TypedEntry<TData>? As(EntryRecord? entry) => Is(entry) && entry.Data is TData ? new TypedEntry<TData>(entry) : null;
}

/// <summary>Entry kinds: <see cref="DefineEntry(string)"/> and the built-in kinds.</summary>
public static class Entries
{
    /// <summary>Defines a typed entry kind whose <see cref="Entry.Is"/> guard narrows by <see cref="EntryRecord.Kind"/>.</summary>
    public static Entry DefineEntry(string kind) => Entry.Create(CheckKind(kind));

    /// <summary>Defines a typed entry kind whose data is <typeparamref name="TData"/>.</summary>
    public static Entry<TData> DefineEntry<TData>(string kind) where TData : JsonValue => new(CheckKind(kind));

    private static string CheckKind(string kind) =>
        string.IsNullOrEmpty(kind) ? throw new ArgumentException("Entry kind must be a non-empty string") : kind;

    /// <summary>User input: <c>model</c> is <c>[UserMessage]</c>. Written by submissions.</summary>
    public static Entry UserEntry { get; } = DefineEntry("pi.user");

    /// <summary>Provider result with any stop reason: <c>model</c> is <c>[AssistantMessage]</c>. Written by generation.</summary>
    public static Entry AssistantEntry { get; } = DefineEntry("pi.assistant");

    /// <summary>Positional prompt and tool change: <c>model</c> is <c>[SystemMessage]</c> with empty <c>content</c>.</summary>
    public static Entry SystemEntry { get; } = DefineEntry("pi.system");

    /// <summary>Tool result: <c>model</c> is <c>[ToolResultMessage]</c>, whose content ends with the rendered diagnostics block; <c>data</c>
    /// is <c>{diagnostics: ToolDiagnostic[]}</c>. Written by tool tasks, and by generation for calls it did not offer.</summary>
    public static Entry<JsonObjectValue> ToolResultEntry { get; } = DefineEntry<JsonObjectValue>("pi.tool-result");

    /// <summary>Start of a new context: always <c>head: "self"</c>, with <c>model</c> absent for a plain reset or <c>[UserMessage]</c>
    /// carrying the handoff text. Written by <c>Conversation.reset()</c> and the <c>handoff</c> tool control.</summary>
    public static Entry ResetEntry { get; } = DefineEntry("pi.reset");

    /// <summary>Compaction summary: <c>model</c> is <c>[UserMessage]</c> with the wrapped summary, <c>head</c> the first kept entry;
    /// <c>data</c> is <c>{reason: CompactionReason}</c>. Written by compaction tasks, directly or through a write submission.</summary>
    public static Entry<JsonObjectValue> CompactionEntry { get; } = DefineEntry<JsonObjectValue>("pi.compaction");
}
