// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/user-message-selector.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source UserMessageItem: the session entry id, the message text and an optional timestamp.</summary>
internal sealed record UserMessageItem(string Id, string Text, string? Timestamp = null);

/// <summary>Custom user message list component with selection.</summary>
internal sealed class UserMessageList : IComponent, IInputHandler
{
    private readonly List<UserMessageItem> messages;
    private int selectedIndex;
    public Action<string>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }
    private readonly int maxVisible = 10; // Max messages visible

    public UserMessageList(IReadOnlyList<UserMessageItem> messages, string? initialSelectedId = null)
    {
        // Store messages in chronological order (oldest to newest)
        this.messages = [.. messages];
        var initialIndex = !string.IsNullOrEmpty(initialSelectedId) ? this.messages.FindIndex(message => message.Id == initialSelectedId) : -1;
        // Start with selected message if provided, else default to the most recent
        selectedIndex = initialIndex >= 0 ? initialIndex : Math.Max(0, messages.Count - 1);
    }

    public void Invalidate()
    {
        // No cached state to invalidate currently
    }

    public List<string> Render(int width)
    {
        var lines = new List<string>();

        if (messages.Count == 0)
        {
            lines.Add(theme.Fg("muted", "  No user messages found"));
            return lines;
        }

        // Calculate visible range with scrolling
        var startIndex = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, messages.Count - maxVisible));
        var endIndex = Math.Min(startIndex + maxVisible, messages.Count);

        // Render visible messages (2 lines per message + blank line)
        for (var i = startIndex; i < endIndex; i++)
        {
            var message = messages[i];
            var isSelected = i == selectedIndex;

            // Normalize message to single line
            var normalizedMessage = TextUtils.JsTrim(message.Text.Replace("\n", " ", StringComparison.Ordinal));

            // First line: cursor + message
            var cursor = isSelected ? theme.Fg("accent", "› ") : "  ";
            var maxMsgWidth = width - 2; // Account for cursor (2 chars)
            var truncatedMsg = TextUtils.TruncateToWidth(normalizedMessage, maxMsgWidth);
            var messageLine = cursor + (isSelected ? theme.Bold(truncatedMsg) : truncatedMsg);

            lines.Add(messageLine);

            // Second line: metadata (position in history)
            var position = i + 1;
            var metadata = $"  Message {position} of {messages.Count}";
            lines.Add(theme.Fg("muted", metadata));
            lines.Add(""); // Blank line between messages
        }

        // Add scroll indicator if needed
        if (startIndex > 0 || endIndex < messages.Count)
            lines.Add(theme.Fg("muted", $"  ({selectedIndex + 1}/{messages.Count})"));

        return lines;
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        // Up arrow - go to previous (older) message, wrap to bottom when at top
        if (kb.Matches(keyData, "tui.select.up"))
            selectedIndex = selectedIndex == 0 ? messages.Count - 1 : selectedIndex - 1;
        // Down arrow - go to next (newer) message, wrap to top when at bottom
        else if (kb.Matches(keyData, "tui.select.down"))
            selectedIndex = selectedIndex == messages.Count - 1 ? 0 : selectedIndex + 1;
        // Enter - select message and branch
        else if (kb.Matches(keyData, "tui.select.confirm"))
        {
            if (selectedIndex >= 0 && selectedIndex < messages.Count) OnSelect?.Invoke(messages[selectedIndex].Id);
        }
        // Escape - cancel
        else if (kb.Matches(keyData, "tui.select.cancel"))
            OnCancel?.Invoke();
    }
}

/// <summary>Component that renders a user message selector for branching.</summary>
internal sealed class UserMessageSelectorComponent : Container
{
    private readonly UserMessageList messageList;

    /// <param name="setTimeout">Schedules the auto-cancel of an empty list (upstream <c>setTimeout(onCancel, 100)</c>); defaults to a
    /// timer that posts to the creating thread's synchronization context (the UI loop).</param>
    public UserMessageSelectorComponent(
        IReadOnlyList<UserMessageItem> messages,
        Action<string> onSelect,
        Action onCancel,
        string? initialSelectedId = null,
        Func<Action, double, IDisposable>? setTimeout = null)
    {
        // Add header
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Bold("Fork from Message"), 1, 0));
        AddChild(new Text(theme.Fg("muted", "Select a user message to copy the active path up to that point into a new session"), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        // Create message list
        messageList = new UserMessageList(messages, initialSelectedId) { OnSelect = onSelect, OnCancel = onCancel };

        AddChild(messageList);

        // Add bottom border
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());

        // Auto-cancel if no messages
        if (messages.Count == 0) (setTimeout ?? DefaultSetTimeout)(() => onCancel(), 100);
    }

    public UserMessageList GetMessageList() => messageList;

    private static IDisposable DefaultSetTimeout(Action action, double milliseconds)
    {
        if (SynchronizationContext.Current is UiLoop loop) return loop.SetTimeout(action, milliseconds);
        var context = SynchronizationContext.Current;
        var timer = new Timer(_ =>
        {
            if (context is null) action();
            else context.Post(_ => action(), null);
        }, null, TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
        return timer;
    }
}
