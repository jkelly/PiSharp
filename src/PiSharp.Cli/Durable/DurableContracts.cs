// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/runtime.ts (ModelSummary, Notice,
// ConversationSummary, DurableView, DurableViewSource, DurableController, OpenDurableOptions, OpenDurableResult). The boundary between the
// `pisharp durable` runtime and its TUI (decision 0005, phase P12): the TUI renders a DurableView and asks the controller for changes; no
// Harness object crosses it. Upstream's unsubscribe function is an IDisposable and its close() is DisposeAsync.
using System.Collections.Immutable;
using PiSharp.Cli.Pi;
using PiSharp.Durable;

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
internal sealed record DurableView(
    DurableSessionInfo Session,
    ConversationView Conversation,
    ImmutableArray<DurableConversationSummary> Conversations,
    ImmutableArray<DurableModelSummary> Models,
    ImmutableArray<DurableNotice> Notices,
    TaskGraph? Tasks = null);

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
