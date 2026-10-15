// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/runtime.ts (ModelSummary, Notice,
// ConversationSummary, DurableView, DurableViewSource, DurableController, OpenDurableOptions, OpenDurableResult). The boundary between the
// `pisharp durable` runtime and its TUI (decision 0005, phase P12): the TUI renders a DurableView and asks the controller for changes; no
// Harness object crosses it. Upstream's unsubscribe function is an IDisposable and its close() is DisposeAsync.
// PiSharp-only since pi-durable 1.2.0 (durable work list area l): the view also carries the session's failure and the nested tool calls.
using System.Collections.Immutable;
using PiSharp.Cli.Pi;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.Cli.Durable;

/// <summary>A model the user can pick (upstream <c>ModelSummary</c>).</summary>
internal sealed record DurableModelSummary(string Provider, string ModelId, string Name, double ContextWindow)
{
    /// <summary>The model reference the agent document stores.</summary>
    public ModelRef Ref => new(Provider, ModelId);
}

/// <summary>How serious a notice is.</summary>
internal enum DurableNoticeLevel
{
    /// <summary><c>info</c>.</summary>
    Info,
    /// <summary><c>warning</c>.</summary>
    Warning,
    /// <summary><c>error</c>.</summary>
    Error,
}

/// <summary>A message for the user (upstream <c>Notice</c>).</summary>
internal sealed record DurableNotice(long Id, DurableNoticeLevel Level, string Message);

/// <summary>A conversation the user can switch to: the main one, or a subagent's (upstream <c>ConversationSummary</c>).</summary>
/// <param name="Title">The first user message; for a subagent its task. The main conversation has none.</param>
internal sealed record DurableConversationSummary(ConversationId Id, string Label, string? Title = null);

/// <summary>The open session's identity.</summary>
internal sealed record DurableSessionInfo(string Id, string Directory, string Cwd);

/// <summary>Everything the TUI renders (upstream <c>DurableView</c>). Plain values; no Harness objects cross this boundary.</summary>
/// <param name="Conversation">The conversation shown and talked to.</param>
/// <param name="Tasks">The live task graph while the task panel is open.</param>
/// <param name="Failure">Why the session failed (the error that failed it, usually a storage error), once it has: nothing runs or is saved any
/// more, and the session must be reopened (PiSharp-only; the v1.1.0 agent had no session failure).</param>
/// <param name="NestedCalls">The nested tool calls of the shown conversation by tool task ID, gathered from every revision of
/// <c>pi.live.nestedTools</c> (PiSharp-only, pi-durable 1.2.0): the TUI shows only the newest view, and a nested call can run and leave
/// <c>pi.live</c> between two of them. Null is none.</param>
internal sealed record DurableView(
    DurableSessionInfo Session,
    ConversationView Conversation,
    ImmutableArray<DurableConversationSummary> Conversations,
    ImmutableArray<DurableModelSummary> Models,
    ImmutableArray<DurableNotice> Notices,
    TaskGraph? Tasks = null,
    string? Failure = null,
    ImmutableSortedDictionary<long, DurableNestedCall>? NestedCalls = null);

/// <summary>A nested tool call as the runtime last saw it in <c>pi.live.nestedTools</c>.</summary>
/// <param name="Slot">Its slot: <c>{callId, parentCallId, parentTaskId, name, taskId, arguments, status, output?, ..., summary?: {isError,
/// durationMs?, usage?, error?}}</c>.</param>
/// <param name="Left">Whether the slot left <c>pi.live</c> before it was done: its run ended, or its caller settled first.</param>
internal sealed record DurableNestedCall(JsonObjectValue Slot, bool Left = false)
{
    /// <summary>At most this many calls are kept, the newest by task ID.</summary>
    internal const int MaxKept = 256;

    /// <summary><paramref name="calls"/> updated from one revision of <paramref name="conversation"/>'s <c>pi.live</c>: listed slots are
    /// replaced, and unfinished calls no longer listed have left. The same instance when nothing changed.</summary>
    internal static ImmutableSortedDictionary<long, DurableNestedCall> Merge(ImmutableSortedDictionary<long, DurableNestedCall> calls,
        ConversationView conversation)
    {
        var slots = conversation.Doc("pi.live") is { } live && live.TryGetValue("nestedTools", out var listed) &&
            listed is JsonArrayValue array ? array : JsonArrayValue.Empty;
        if (slots.Count == 0 && calls.IsEmpty) return calls;
        var builder = calls.ToBuilder();
        var seen = new HashSet<long>();
        var changed = false;
        foreach (var slot in slots.OfType<JsonObjectValue>())
        {
            if (!slot.TryGetValue("taskId", out var id) || id is not JsonNumberValue number) continue;
            var taskId = (long)number.Value;
            seen.Add(taskId);
            if (calls.TryGetValue(taskId, out var known) && !known.Left && ReferenceEquals(known.Slot, slot)) continue;
            builder[taskId] = new DurableNestedCall(slot);
            changed = true;
        }
        foreach (var (taskId, call) in calls)
        {
            if (call.Left || seen.Contains(taskId) || Status(call.Slot) == "done") continue;
            builder[taskId] = call with { Left = true };
            changed = true;
        }
        while (builder.Count > MaxKept)
        {
            builder.Remove(builder.Keys.First());
            changed = true;
        }
        return changed ? builder.ToImmutable() : calls;
    }

    private static string? Status(JsonObjectValue slot) =>
        slot.TryGetValue("status", out var status) && status is JsonStringValue text ? text.Value : null;
}

/// <summary>The current view and its changes (upstream <c>DurableViewSource</c>).</summary>
internal interface IDurableViewSource
{
    /// <summary>The view now.</summary>
    DurableView Current { get; }

    /// <summary>Calls <paramref name="listener"/> after each change of <see cref="Current"/>; disposing unsubscribes.</summary>
    IDisposable Subscribe(Action listener);
}

/// <summary>What the TUI may ask for (upstream <c>DurableController</c>).</summary>
internal interface IDurableController
{
    /// <summary>Prompt when idle; otherwise steer or queue a follow-up.</summary>
    Task SubmitAsync(string text, WhenBusy whenBusy);

    /// <summary>Compact the shown conversation, with optional instructions.</summary>
    Task CompactAsync(string? instructions);

    /// <summary>Abort the shown conversation's work.</summary>
    Task AbortAsync();

    /// <summary>Move to the next thinking level the model supports.</summary>
    Task CycleThinkingAsync();

    /// <summary>Use <paramref name="model"/> for the shown conversation.</summary>
    Task SetModelAsync(ModelRef model);

    /// <summary>Open or close the task panel (the view's <see cref="DurableView.Tasks"/>).</summary>
    Task ToggleTasksAsync();

    /// <summary>Show and talk to another conversation.</summary>
    Task SwitchConversationAsync(ConversationId id);
}

/// <summary>How to open a durable session (upstream <c>OpenDurableOptions</c>).</summary>
internal sealed record OpenDurableOptions
{
    /// <summary>The working directory; the process's when null.</summary>
    public string? Cwd { get; init; }

    /// <summary>Reopen the newest session for the working directory instead of starting one.</summary>
    public bool ContinueSession { get; init; }
}

/// <summary>An open durable session (upstream <c>OpenDurableResult</c>); disposing closes it and releases its lock.</summary>
internal interface IOpenDurable : IAsyncDisposable
{
    /// <summary>The view source.</summary>
    IDurableViewSource View { get; }

    /// <summary>The controller.</summary>
    IDurableController Controller { get; }

    /// <summary>pi's settings, for the TUI's theme and terminal capabilities.</summary>
    PiSettings Settings { get; }
}

/// <summary>What <c>pisharp durable</c> tells the user when the session failed.</summary>
internal static class DurableFailure
{
    /// <summary>The one line the TUI shows and the CLI prints after the TUI exits.</summary>
    internal static string Message(string failure) =>
        $"Session failed: {failure}. Nothing more is saved; exit and run `pisharp durable --continue` to reopen it.";
}
