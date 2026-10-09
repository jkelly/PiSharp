// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/session-selector.ts.
// Also formatResumeCommand/quoteIfNeeded from coding-agent/src/modes/interactive/interactive-mode.ts (ResumeCommand below).
// Sessions are PiSharp's PiSessionInfo (the source SessionInfo). The loaders, rename and delete are delegates; delete defaults to
// the source deleteSessionFile (the `trash` command, then unlink).
using System.Diagnostics;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;
using TuiKeybindingsManager = PiSharp.Tui.Pi.KeybindingsManager;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source SessionListProgress: <paramref name="partialSessions"/> (sorted by activity) is present on periodic updates.</summary>
internal delegate void SessionListProgress(int loaded, int total, IReadOnlyList<PiSessionInfo>? partialSessions);

/// <summary>Source SessionsLoader: <c>(onProgress?, signal?) =&gt; Promise&lt;SessionInfo[]&gt;</c>.</summary>
internal delegate Task<IReadOnlyList<PiSessionInfo>> SessionsLoader(SessionListProgress? onProgress, CancellationToken signal);

/// <summary>Source deleteSessionFile result: method "trash" | "unlink".</summary>
internal sealed record DeleteSessionFileResult(bool Ok, string Method, string? Error = null);

/// <summary>Source SessionSelectorComponent options, plus the delete operation and the timer (both default to the source behaviour).</summary>
internal sealed class SessionSelectorOptions
{
    /// <summary>Source renameSession(sessionPath, newName).</summary>
    public Func<string, string?, Task>? RenameSession { get; init; }
    public bool? ShowRenameHint { get; init; }
    /// <summary>The manager used for <c>app.session.toggleNamedFilter</c> (source options.keybindings); defaults to the global one.</summary>
    public TuiKeybindingsManager? Keybindings { get; init; }
    /// <summary>Deletes a session file; defaults to <see cref="SessionSelectorComponent.DeleteSessionFile"/>.</summary>
    public Func<string, Task<DeleteSessionFileResult>>? DeleteSessionFile { get; init; }
    /// <summary>Node's setTimeout; defaults to <see cref="SelectorTimers.SetTimeout"/>.</summary>
    public Func<Action, double, IDisposable>? SetTimeout { get; init; }
}

/// <summary>Node's setTimeout for components: the current <see cref="UiLoop"/> when there is one, else a timer that posts to the
/// current synchronization context (or runs on the thread pool).</summary>
internal static class SelectorTimers
{
    public static IDisposable SetTimeout(Action action, double milliseconds)
    {
        if (SynchronizationContext.Current is UiLoop loop) return loop.SetTimeout(action, milliseconds);
        var context = SynchronizationContext.Current;
        var fired = 0;
        void Fire() { if (Interlocked.Exchange(ref fired, 1) == 0) action(); }
        var timer = new Timer(_ => { if (context is not null) context.Post(_ => Fire(), null); else Fire(); }, null,
            TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)), Timeout.InfiniteTimeSpan);
        return new Handle(timer, () => Interlocked.Exchange(ref fired, 1));
    }

    private sealed class Handle(Timer timer, Action cancel) : IDisposable
    {
        public void Dispose() { cancel(); timer.Dispose(); }
    }
}

/// <summary>Source formatResumeCommand and quoteIfNeeded (interactive-mode.ts).</summary>
internal static class ResumeCommand
{
    /// <summary>The members of SessionManager that formatResumeCommand reads.</summary>
    internal interface ISession
    {
        bool IsPersisted();
        string? GetSessionFile();
        string GetSessionId();
        string GetSessionDir();
        bool UsesDefaultSessionDir();
    }

    public static string QuoteIfNeeded(string value)
    {
        if (value.Length > 0 && value.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or '/' or '~' or ':' or '@'))
            return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Undefined (null) when stdout is not a TTY, the session is in memory, or its file is missing.</summary>
    public static string? FormatResumeCommand(ISession sessionManager, bool stdoutIsTty, string appName = PiConfig.AppName)
    {
        if (!stdoutIsTty) return null;
        if (!sessionManager.IsPersisted()) return null;

        var sessionFile = sessionManager.GetSessionFile();
        if (string.IsNullOrEmpty(sessionFile) || !(File.Exists(sessionFile) || Directory.Exists(sessionFile))) return null;

        var args = new List<string> { appName };
        if (!sessionManager.UsesDefaultSessionDir()) { args.Add("--session-dir"); args.Add(QuoteIfNeeded(sessionManager.GetSessionDir())); }
        args.Add("--session"); args.Add(sessionManager.GetSessionId());
        return string.Join(" ", args);
    }
}

internal enum SessionScope { Current, All }

/// <summary>Custom session list component with multi-line items and search.</summary>
internal sealed class SessionList : IComponent, IFocusable, IInputHandler
{
    /// <summary>A session tree node for hierarchical display.</summary>
    private sealed class SessionTreeNode(PiSessionInfo session, long latestActivity)
    {
        public PiSessionInfo Session { get; } = session;
        public List<SessionTreeNode> Children { get; } = [];
        public long LatestActivity { get; set; } = latestActivity;
    }

    /// <summary>Flattened node for display with tree structure info.</summary>
    private sealed record FlatSessionNode(PiSessionInfo Session, int Depth, bool IsLast, IReadOnlyList<bool> AncestorContinues);

    public string? GetSelectedSessionPath()
    {
        return Selected()?.Session.Path;
    }

    /// <summary>JavaScript <c>filteredSessions[selectedIndex]</c> (undefined outside the list).</summary>
    private FlatSessionNode? Selected() => selectedIndex >= 0 && selectedIndex < filteredSessions.Count ? filteredSessions[selectedIndex] : null;

    private IReadOnlyList<PiSessionInfo> allSessions;
    private List<FlatSessionNode> filteredSessions = [];
    private int selectedIndex;
    private bool selectionTouched;
    private readonly Input searchInput;
    private bool showCwd;
    private SortMode sortMode;
    private NameFilter nameFilter;
    private readonly TuiKeybindingsManager keybindings;
    private bool showPath;
    private string? confirmingDeletePath;
    private readonly string? currentSessionCanonicalPath;
    public Action<string>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }
    public Action OnExit { get; set; } = () => { };
    public Action? OnToggleScope { get; set; }
    public Action? OnToggleSort { get; set; }
    public Action? OnToggleNameFilter { get; set; }
    public Action<bool>? OnTogglePath { get; set; }
    public Action<string?>? OnDeleteConfirmationChange { get; set; }
    public Func<string, Task>? OnDeleteSession { get; set; }
    public Action<string>? OnRenameSession { get; set; }
    public Action<string>? OnError { get; set; }
    private readonly int maxVisible = 10; // Max sessions visible (one line each)

    // Focusable implementation - propagate to searchInput for IME cursor positioning
    private bool focused;
    public bool Focused
    {
        get => focused;
        set { focused = value; searchInput.Focused = value; }
    }

    public SessionList(IReadOnlyList<PiSessionInfo> sessions, bool showCwd, SortMode sortMode, NameFilter nameFilter, TuiKeybindingsManager keybindings,
        string? currentSessionFilePath = null)
    {
        allSessions = sessions;
        searchInput = new Input();
        this.showCwd = showCwd;
        this.sortMode = sortMode;
        this.nameFilter = nameFilter;
        this.keybindings = keybindings;
        currentSessionCanonicalPath = CanonicalizePath(currentSessionFilePath);
        FilterSessions("");

        // Handle Enter in search input - select current item
        searchInput.OnSubmit = _ =>
        {
            if (Selected() is { } selected) OnSelect?.Invoke(selected.Session.Path);
        };
    }

    private static string? CanonicalizePath(string? path) => string.IsNullOrEmpty(path) ? path : PiPaths.Canonicalize(path);

    public void SetSortMode(SortMode sortMode)
    {
        this.sortMode = sortMode;
        FilterSessions(searchInput.GetValue());
    }

    public void SetNameFilter(NameFilter nameFilter)
    {
        this.nameFilter = nameFilter;
        FilterSessions(searchInput.GetValue());
    }

    public void SetSessions(IReadOnlyList<PiSessionInfo> sessions, bool showCwd)
    {
        var selectedPath = selectionTouched ? GetSelectedSessionPath() : null;
        allSessions = sessions;
        this.showCwd = showCwd;
        FilterSessions(searchInput.GetValue());
        if (!selectionTouched) selectedIndex = 0;
        else if (!string.IsNullOrEmpty(selectedPath))
        {
            var index = filteredSessions.FindIndex(node => node.Session.Path == selectedPath);
            if (index >= 0) selectedIndex = index;
        }
    }

    /// <summary>Build a tree structure from sessions based on parentSessionPath. Returns root nodes sorted by latest activity (descending).</summary>
    private static List<SessionTreeNode> BuildSessionTree(IReadOnlyList<PiSessionInfo> sessions)
    {
        var byPath = new Dictionary<string, SessionTreeNode>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            var sessionPath = CanonicalizePath(session.Path) ?? session.Path;
            byPath[sessionPath] = new SessionTreeNode(session, session.Modified.ToUnixTimeMilliseconds());
        }

        var roots = new List<SessionTreeNode>();
        foreach (var session in sessions)
        {
            var sessionPath = CanonicalizePath(session.Path) ?? session.Path;
            var node = byPath[sessionPath];
            var parentPath = CanonicalizePath(session.ParentSessionPath);
            if (!string.IsNullOrEmpty(parentPath) && byPath.TryGetValue(parentPath, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }

        static long UpdateLatestActivity(SessionTreeNode node)
        {
            var latestActivity = node.Session.Modified.ToUnixTimeMilliseconds();
            foreach (var child in node.Children) latestActivity = Math.Max(latestActivity, UpdateLatestActivity(child));
            node.LatestActivity = latestActivity;
            return latestActivity;
        }

        foreach (var root in roots) UpdateLatestActivity(root);

        // Sort children and roots by latest activity in each subtree (descending)
        static void SortNodes(List<SessionTreeNode> nodes)
        {
            var sorted = nodes.OrderByDescending(node => node.LatestActivity).ToList();
            nodes.Clear(); nodes.AddRange(sorted);
            foreach (var node in nodes) SortNodes(node.Children);
        }
        SortNodes(roots);
        return roots;
    }

    /// <summary>Flatten tree into display list with tree structure metadata.</summary>
    private static List<FlatSessionNode> FlattenSessionTree(List<SessionTreeNode> roots)
    {
        var result = new List<FlatSessionNode>();

        void Walk(SessionTreeNode node, int depth, IReadOnlyList<bool> ancestorContinues, bool isLast)
        {
            result.Add(new FlatSessionNode(node.Session, depth, isLast, ancestorContinues));
            for (var i = 0; i < node.Children.Count; i++)
            {
                var childIsLast = i == node.Children.Count - 1;
                // Only show continuation line for non-root ancestors
                var continues = depth > 0 && !isLast;
                Walk(node.Children[i], depth + 1, [.. ancestorContinues, continues], childIsLast);
            }
        }

        for (var i = 0; i < roots.Count; i++) Walk(roots[i], 0, [], i == roots.Count - 1);
        return result;
    }

    private void FilterSessions(string query)
    {
        var trimmed = TextUtils.JsTrim(query);
        IReadOnlyList<PiSessionInfo> nameFiltered = nameFilter == NameFilter.All ? allSessions : [.. allSessions.Where(SessionSelectorSearch.HasSessionName)];

        if (sortMode == SortMode.Threaded && trimmed.Length == 0)
        {
            // Threaded mode without search: show tree structure
            filteredSessions = FlattenSessionTree(BuildSessionTree(nameFiltered));
        }
        else
        {
            // Other modes or with search: flat list
            var filtered = SessionSelectorSearch.FilterAndSortSessions(nameFiltered, query, sortMode, NameFilter.All);
            filteredSessions = [.. filtered.Select(session => new FlatSessionNode(session, 0, true, []))];
        }
        selectedIndex = Math.Min(selectedIndex, Math.Max(0, filteredSessions.Count - 1));
    }

    private void SetConfirmingDeletePath(string? path)
    {
        confirmingDeletePath = path;
        OnDeleteConfirmationChange?.Invoke(path);
    }

    private void StartDeleteConfirmationForSelectedSession()
    {
        if (Selected() is not { } selected) return;

        // Prevent deleting current session
        if (IsCurrentSessionPath(selected.Session.Path))
        {
            OnError?.Invoke("Cannot delete the currently active session");
            return;
        }

        SetConfirmingDeletePath(selected.Session.Path);
    }

    private bool IsCurrentSessionPath(string path)
    {
        if (string.IsNullOrEmpty(currentSessionCanonicalPath)) return false;
        return (CanonicalizePath(path) ?? path) == currentSessionCanonicalPath;
    }

    public void Invalidate() { }

    public List<string> Render(int width)
    {
        var lines = new List<string>();

        // Render search input
        lines.AddRange(searchInput.Render(width));
        lines.Add(""); // Blank line after search

        if (filteredSessions.Count == 0)
        {
            string emptyMessage;
            if (nameFilter == NameFilter.Named)
            {
                var toggleKey = KeybindingHints.KeyText("app.session.toggleNamedFilter");
                emptyMessage = showCwd
                    ? $"  No named sessions found. Press {toggleKey} to show all."
                    : $"  No named sessions in current folder. Press {toggleKey} to show all, or Tab to view all.";
            }
            else if (showCwd)
            {
                // "All" scope - no sessions anywhere that match filter
                emptyMessage = "  No sessions found";
            }
            else
            {
                // "Current folder" scope - hint to try "all"
                emptyMessage = "  No sessions in current folder. Press Tab to view all.";
            }
            lines.Add(theme.Fg("muted", TextUtils.TruncateToWidth(emptyMessage, width, "…")));
            return lines;
        }

        // Calculate visible range with scrolling
        var startIndex = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, filteredSessions.Count - maxVisible));
        var endIndex = Math.Min(startIndex + maxVisible, filteredSessions.Count);

        // Render visible sessions (one line each with tree structure)
        for (var i = startIndex; i < endIndex; i++)
        {
            var node = filteredSessions[i];
            var session = node.Session;
            var isSelected = i == selectedIndex;
            var isConfirmingDelete = session.Path == confirmingDeletePath;
            var isCurrent = IsCurrentSessionPath(session.Path);

            // Build tree prefix
            var prefix = BuildTreePrefix(node);

            // Session display text (name or first message)
            var hasName = !string.IsNullOrEmpty(session.Name);
            var displayText = session.Name ?? session.FirstMessage;
            var normalizedMessage = TextUtils.JsTrim(new string([.. displayText.Select(ch => ch < 0x20 || ch == 0x7f ? ' ' : ch)]));

            // Right side: message count and age
            var age = FormatSessionDate(session.Modified);
            var msgCount = session.MessageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rightPart = $"{msgCount} {age}";
            if (showCwd && !string.IsNullOrEmpty(session.Cwd)) rightPart = $"{ShortenPath(session.Cwd)} {rightPart}";
            if (showPath) rightPart = $"{ShortenPath(session.Path)} {rightPart}";

            // Cursor
            var cursor = isSelected ? theme.Fg("accent", "› ") : "  ";

            // Calculate available width for message
            var prefixWidth = TextUtils.VisibleWidth(prefix);
            var rightWidth = TextUtils.VisibleWidth(rightPart) + 2; // +2 for spacing
            var availableForMsg = width - 2 - prefixWidth - rightWidth; // -2 for cursor

            var truncatedMsg = TextUtils.TruncateToWidth(normalizedMessage, Math.Max(10, availableForMsg), "…");

            // Style message
            string? messageColor = null;
            if (isConfirmingDelete) messageColor = "error";
            else if (isCurrent) messageColor = "accent";
            else if (hasName) messageColor = "warning";
            var styledMsg = messageColor is not null ? theme.Fg(messageColor, truncatedMsg) : truncatedMsg;
            if (isSelected) styledMsg = theme.Bold(styledMsg);

            // Build line
            var leftPart = cursor + theme.Fg("dim", prefix) + styledMsg;
            var leftWidth = TextUtils.VisibleWidth(leftPart);
            var spacing = Math.Max(1, width - leftWidth - TextUtils.VisibleWidth(rightPart));
            var styledRight = theme.Fg(isConfirmingDelete ? "error" : "dim", rightPart);

            var line = leftPart + new string(' ', spacing) + styledRight;
            if (isSelected) line = theme.Bg("selectedBg", line);
            lines.Add(TextUtils.TruncateToWidth(line, width));
        }

        // Add scroll indicator if needed
        if (startIndex > 0 || endIndex < filteredSessions.Count)
        {
            var scrollText = $"  ({selectedIndex + 1}/{filteredSessions.Count})";
            lines.Add(theme.Fg("muted", TextUtils.TruncateToWidth(scrollText, width, "")));
        }

        return lines;
    }

    private static string BuildTreePrefix(FlatSessionNode node)
    {
        if (node.Depth == 0) return "";
        var parts = node.AncestorContinues.Select(continues => continues ? "│  " : "   ");
        var branch = node.IsLast ? "└─ " : "├─ ";
        return string.Concat(parts) + branch;
    }

    internal static string ShortenPath(string path)
    {
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") is { Length: > 0 } fromEnv
            ? fromEnv : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(home, StringComparison.Ordinal)) return "~" + path[home.Length..];
        return path;
    }

    internal static string FormatSessionDate(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var diffMs = (double)((now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() - date.ToUnixTimeMilliseconds());
        var diffMins = Math.Floor(diffMs / 60000);
        var diffHours = Math.Floor(diffMs / 3600000);
        var diffDays = Math.Floor(diffMs / 86400000);

        if (diffMins < 1) return "now";
        if (diffMins < 60) return $"{diffMins}m";
        if (diffHours < 24) return $"{diffHours}h";
        if (diffDays < 7) return $"{diffDays}d";
        if (diffDays < 30) return $"{Math.Floor(diffDays / 7)}w";
        if (diffDays < 365) return $"{Math.Floor(diffDays / 30)}mo";
        return $"{Math.Floor(diffDays / 365)}y";
    }

    public void HandleInput(string keyData)
    {
        var kb = TuiKeybindingsManager.Global;

        // Handle delete confirmation state first - intercept all keys
        if (confirmingDeletePath is not null)
        {
            if (kb.Matches(keyData, "tui.select.confirm"))
            {
                var pathToDelete = confirmingDeletePath;
                SetConfirmingDeletePath(null);
                _ = OnDeleteSession?.Invoke(pathToDelete);
                return;
            }
            if (kb.Matches(keyData, "tui.select.cancel"))
            {
                SetConfirmingDeletePath(null);
                return;
            }
            // Ignore all other keys while confirming
            return;
        }

        if (kb.Matches(keyData, "tui.input.tab"))
        {
            OnToggleScope?.Invoke();
            return;
        }

        if (kb.Matches(keyData, "app.session.toggleSort"))
        {
            OnToggleSort?.Invoke();
            return;
        }

        if (keybindings.Matches(keyData, "app.session.toggleNamedFilter"))
        {
            OnToggleNameFilter?.Invoke();
            return;
        }

        // Ctrl+P: toggle path display
        if (kb.Matches(keyData, "app.session.togglePath"))
        {
            showPath = !showPath;
            OnTogglePath?.Invoke(showPath);
            return;
        }

        // Ctrl+D: initiate delete confirmation (useful on terminals that don't distinguish Ctrl+Backspace from Backspace)
        if (kb.Matches(keyData, "app.session.delete"))
        {
            StartDeleteConfirmationForSelectedSession();
            return;
        }

        // Rename selected session
        if (kb.Matches(keyData, "app.session.rename"))
        {
            if (Selected() is { } selected) OnRenameSession?.Invoke(selected.Session.Path);
            return;
        }

        // Ctrl+Backspace: non-invasive convenience alias for delete
        // Only triggers deletion when the query is empty; otherwise it is forwarded to the input
        if (kb.Matches(keyData, "app.session.deleteNoninvasive"))
        {
            if (searchInput.GetValue().Length > 0)
            {
                searchInput.HandleInput(keyData);
                FilterSessions(searchInput.GetValue());
                return;
            }

            StartDeleteConfirmationForSelectedSession();
            return;
        }

        selectionTouched = true;
        if (kb.Matches(keyData, "tui.select.up")) selectedIndex = Math.Max(0, selectedIndex - 1);
        else if (kb.Matches(keyData, "tui.select.down")) selectedIndex = Math.Min(filteredSessions.Count - 1, selectedIndex + 1);
        // Page up - jump up by maxVisible items
        else if (kb.Matches(keyData, "tui.select.pageUp")) selectedIndex = Math.Max(0, selectedIndex - maxVisible);
        // Page down - jump down by maxVisible items
        else if (kb.Matches(keyData, "tui.select.pageDown")) selectedIndex = Math.Min(filteredSessions.Count - 1, selectedIndex + maxVisible);
        else if (kb.Matches(keyData, "tui.select.confirm"))
        {
            if (Selected() is { } selected && OnSelect is not null) OnSelect(selected.Session.Path);
        }
        else if (kb.Matches(keyData, "tui.select.cancel")) OnCancel?.Invoke();
        // Pass everything else to search input
        else
        {
            searchInput.HandleInput(keyData);
            FilterSessions(searchInput.GetValue());
        }
    }
}

/// <summary>Component that renders a session selector.</summary>
internal sealed class SessionSelectorComponent : Container, IFocusable, IInputHandler
{
    /// <summary>Source DynamicBorder (dynamic-border.ts), kept local to this file.</summary>
    private sealed class DynamicBorder(Func<string, string>? color = null) : IComponent
    {
        private readonly Func<string, string> color = color ?? (s => theme.Fg("border", s));
        public void Invalidate() { }
        public List<string> Render(int width) => [color(TextUtils.Repeat("─", Math.Max(1, width)))];
    }

    private sealed class SessionSelectorHeader(SessionScope scope, SortMode sortMode, NameFilter nameFilter, Action requestRender,
        Func<Action, double, IDisposable> setTimeout) : IComponent
    {
        private SessionScope scope = scope;
        private SortMode sortMode = sortMode;
        private NameFilter nameFilter = nameFilter;
        private bool loading;
        private (int Loaded, int Total)? loadProgress;
        private bool showPath;
        private string? confirmingDeletePath;
        private (string Type, string Message)? statusMessage;
        private IDisposable? statusTimeout;
        private bool showRenameHint;

        public void SetScope(SessionScope value) => scope = value;
        public void SetSortMode(SortMode value) => sortMode = value;
        public void SetNameFilter(NameFilter value) => nameFilter = value;

        public void SetLoading(bool value)
        {
            loading = value;
            // Progress is scoped to the current load; clear whenever the loading state is set
            loadProgress = null;
        }

        public void SetProgress(int loaded, int total) => loadProgress = (loaded, total);
        public void SetShowPath(bool value) => showPath = value;
        public void SetShowRenameHint(bool show) => showRenameHint = show;
        public void SetConfirmingDeletePath(string? path) => confirmingDeletePath = path;

        private void ClearStatusTimeout()
        {
            if (statusTimeout is null) return;
            statusTimeout.Dispose();
            statusTimeout = null;
        }

        public void SetStatusMessage((string Type, string Message)? msg, int? autoHideMs = null)
        {
            ClearStatusTimeout();
            statusMessage = msg;
            if (msg is null || autoHideMs is null or 0) return;

            statusTimeout = setTimeout(() =>
            {
                statusMessage = null;
                statusTimeout = null;
                requestRender();
            }, autoHideMs.Value);
        }

        public void Invalidate() { }

        public List<string> Render(int width)
        {
            var title = scope == SessionScope.Current ? "Resume Session (Current Folder)" : "Resume Session (All)";
            var leftText = theme.Bold(title);

            var sortLabel = sortMode == SortMode.Threaded ? "Threaded" : sortMode == SortMode.Recent ? "Recent" : "Fuzzy";
            var sortText = theme.Fg("muted", "Sort: ") + theme.Fg("accent", sortLabel);

            var nameLabel = nameFilter == NameFilter.All ? "All" : "Named";
            var nameText = theme.Fg("muted", "Name: ") + theme.Fg("accent", nameLabel);

            string scopeText;
            if (loading)
            {
                var progressText = loadProgress is { } progress ? $"{progress.Loaded}/{progress.Total}" : "...";
                scopeText = theme.Fg("muted", "○ Current Folder | ") + theme.Fg("accent", $"Loading {progressText}");
            }
            else if (scope == SessionScope.Current) scopeText = theme.Fg("accent", "◉ Current Folder") + theme.Fg("muted", " | ○ All");
            else scopeText = theme.Fg("muted", "○ Current Folder | ") + theme.Fg("accent", "◉ All");

            var rightText = TextUtils.TruncateToWidth($"{scopeText}  {nameText}  {sortText}", width, "");
            var availableLeft = Math.Max(0, width - TextUtils.VisibleWidth(rightText) - 1);
            var left = TextUtils.TruncateToWidth(leftText, availableLeft, "");
            var spacing = Math.Max(0, width - TextUtils.VisibleWidth(left) - TextUtils.VisibleWidth(rightText));

            // Build hint lines - changes based on state (all branches truncate to width)
            string hintLine1, hintLine2;
            if (confirmingDeletePath is not null)
            {
                var confirmHint = $"Delete session? {KeybindingHints.KeyHint("tui.select.confirm", "confirm")} · {KeybindingHints.KeyHint("tui.select.cancel", "cancel")}";
                hintLine1 = theme.Fg("error", TextUtils.TruncateToWidth(confirmHint, width, "…"));
                hintLine2 = "";
            }
            else if (statusMessage is { } status)
            {
                var color = status.Type == "error" ? "error" : "accent";
                hintLine1 = theme.Fg(color, TextUtils.TruncateToWidth(status.Message, width, "…"));
                hintLine2 = "";
            }
            else
            {
                var pathState = showPath ? "(on)" : "(off)";
                var sep = theme.Fg("muted", " · ");
                var hint1 = KeybindingHints.KeyHint("tui.input.tab", "scope") + sep + theme.Fg("muted", "re:<pattern> regex · \"phrase\" exact");
                var hint2Parts = new List<string>
                {
                    KeybindingHints.KeyHint("app.session.toggleSort", "sort"),
                    KeybindingHints.KeyHint("app.session.toggleNamedFilter", "named"),
                    KeybindingHints.KeyHint("app.session.delete", "delete"),
                    KeybindingHints.KeyHint("app.session.togglePath", $"path {pathState}"),
                };
                if (showRenameHint) hint2Parts.Add(KeybindingHints.KeyHint("app.session.rename", "rename"));
                var hint2 = string.Join(sep, hint2Parts);
                hintLine1 = TextUtils.TruncateToWidth(hint1, width, "…");
                hintLine2 = TextUtils.TruncateToWidth(hint2, width, "…");
            }

            return [left + new string(' ', spacing) + rightText, hintLine1, hintLine2];
        }
    }

    public void HandleInput(string data)
    {
        if (mode == "rename")
        {
            var kb = TuiKeybindingsManager.Global;
            if (kb.Matches(data, "tui.select.cancel"))
            {
                ExitRenameMode();
                return;
            }
            renameInput.HandleInput(data);
            return;
        }

        sessionList.HandleInput(data);
    }

    private readonly SessionList sessionList;
    private readonly SessionSelectorHeader header;
    private readonly TuiKeybindingsManager keybindings;
    private SessionScope scope = SessionScope.Current;
    private SortMode sortMode = SortMode.Threaded;
    private NameFilter nameFilter = NameFilter.All;
    private IReadOnlyList<PiSessionInfo>? currentSessions;
    private IReadOnlyList<PiSessionInfo>? allSessions;
    private readonly SessionsLoader currentSessionsLoader;
    private readonly SessionsLoader allSessionsLoader;
    private readonly Action requestRender;
    private readonly Func<string, string?, Task>? renameSession;
    private CancellationTokenSource? currentLoad;
    private CancellationTokenSource? allLoad;

    private string mode = "list"; // "list" | "rename"
    private readonly Input renameInput = new();
    private string? renameTargetPath;

    // Focusable implementation - propagate to sessionList for IME cursor positioning
    private bool focused;
    public bool Focused
    {
        get => focused;
        set
        {
            focused = value;
            sessionList.Focused = value;
            renameInput.Focused = value;
            if (value && mode == "rename") renameInput.Focused = true;
        }
    }

    private void BuildBaseLayout(IComponent content, bool showHeader = true)
    {
        Clear();
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(s => theme.Fg("accent", s)));
        AddChild(new Spacer(1));
        if (showHeader)
        {
            AddChild(header);
            AddChild(new Spacer(1));
        }
        AddChild(content);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(s => theme.Fg("accent", s)));
    }

    public SessionSelectorComponent(SessionsLoader currentSessionsLoader, SessionsLoader allSessionsLoader, Action<string> onSelect, Action onCancel,
        Action onExit, Action requestRender, SessionSelectorOptions? options = null, string? currentSessionFilePath = null)
    {
        keybindings = options?.Keybindings ?? TuiKeybindingsManager.Global;
        this.currentSessionsLoader = currentSessionsLoader;
        this.allSessionsLoader = allSessionsLoader;
        this.requestRender = requestRender;
        header = new SessionSelectorHeader(scope, sortMode, nameFilter, this.requestRender, options?.SetTimeout ?? SelectorTimers.SetTimeout);
        var rename = options?.RenameSession;
        renameSession = rename;
        var canRename = rename is not null;
        header.SetShowRenameHint(options?.ShowRenameHint ?? canRename);
        var deleteSessionFile = options?.DeleteSessionFile ?? DeleteSessionFile;

        // Create session list (starts empty, will be populated after load)
        sessionList = new SessionList([], false, sortMode, nameFilter, keybindings, currentSessionFilePath);

        BuildBaseLayout(sessionList);

        renameInput.OnSubmit = value => { _ = ConfirmRename(value); };

        // Ensure header status timeouts are cleared when leaving the selector
        void ClearStatusMessage() => header.SetStatusMessage(null);
        sessionList.OnSelect = sessionPath =>
        {
            ClearStatusMessage();
            CancelLoads();
            onSelect(sessionPath);
        };
        sessionList.OnCancel = () =>
        {
            ClearStatusMessage();
            CancelLoads();
            onCancel();
        };
        sessionList.OnExit = () =>
        {
            ClearStatusMessage();
            CancelLoads();
            onExit();
        };
        sessionList.OnToggleScope = ToggleScope;
        sessionList.OnToggleSort = ToggleSortMode;
        sessionList.OnToggleNameFilter = ToggleNameFilter;
        sessionList.OnRenameSession = sessionPath =>
        {
            if (rename is null) return;
            if ((scope == SessionScope.Current ? currentLoad : allLoad) is not null) return;

            var sessions = scope == SessionScope.All ? allSessions ?? [] : currentSessions ?? [];
            var session = sessions.FirstOrDefault(s => s.Path == sessionPath);
            EnterRenameMode(sessionPath, session?.Name);
        };

        // Sync list events to header
        sessionList.OnTogglePath = showPath =>
        {
            header.SetShowPath(showPath);
            this.requestRender();
        };
        sessionList.OnDeleteConfirmationChange = path =>
        {
            header.SetConfirmingDeletePath(path);
            this.requestRender();
        };
        sessionList.OnError = msg =>
        {
            header.SetStatusMessage(("error", msg), 3000);
            this.requestRender();
        };

        // Handle session deletion
        sessionList.OnDeleteSession = async sessionPath =>
        {
            var result = await deleteSessionFile(sessionPath);

            if (result.Ok)
            {
                if (currentSessions is not null) currentSessions = [.. currentSessions.Where(s => s.Path != sessionPath)];
                if (allSessions is not null) allSessions = [.. allSessions.Where(s => s.Path != sessionPath)];

                var sessions = scope == SessionScope.All ? allSessions ?? [] : currentSessions ?? [];
                var showCwd = scope == SessionScope.All;
                sessionList.SetSessions(sessions, showCwd);

                var msg = result.Method == "trash" ? "Session moved to trash" : "Session deleted";
                header.SetStatusMessage(("info", msg), 2000);
                await RefreshSessionsAfterMutation();
            }
            else
            {
                var errorMessage = result.Error ?? "Unknown error";
                header.SetStatusMessage(("error", $"Failed to delete: {errorMessage}"), 3000);
            }

            this.requestRender();
        };

        // Start loading current sessions immediately
        _ = LoadScope(SessionScope.Current);
    }

    /// <summary>Source deleteSessionFile: the <c>trash</c> CLI first (success when it exits 0 or the file is gone afterwards),
    /// then permanent deletion.</summary>
    public static Task<DeleteSessionFileResult> DeleteSessionFile(string sessionPath)
    {
        // Try `trash` first (if installed)
        string[] trashArgs = sessionPath.StartsWith('-') ? ["--", sessionPath] : [sessionPath];
        var (status, spawnError, stderr) = SpawnTrash(trashArgs);

        string? GetTrashErrorHint()
        {
            var parts = new List<string>();
            if (spawnError is not null) parts.Add(spawnError);
            var trimmed = stderr is null ? "" : TextUtils.JsTrim(stderr);
            if (trimmed.Length > 0) parts.Add(trimmed.Split('\n')[0]);
            if (parts.Count == 0) return null;
            var joined = string.Join(" · ", parts);
            return "trash: " + (joined.Length > 200 ? joined[..200] : joined);
        }

        // If trash reports success, or the file is gone afterwards, treat it as successful
        if (status == 0 || !(File.Exists(sessionPath) || Directory.Exists(sessionPath)))
            return Task.FromResult(new DeleteSessionFileResult(true, "trash"));

        // Fallback to permanent deletion
        try
        {
            if (Directory.Exists(sessionPath)) throw new IOException($"EISDIR: illegal operation on a directory, unlink '{sessionPath}'");
            File.Delete(sessionPath);
            return Task.FromResult(new DeleteSessionFileResult(true, "unlink"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var unlinkError = error.Message;
            var trashErrorHint = GetTrashErrorHint();
            var message = trashErrorHint is not null ? $"{unlinkError} ({trashErrorHint})" : unlinkError;
            return Task.FromResult(new DeleteSessionFileResult(false, "unlink", message));
        }
    }

    /// <summary>Node's spawnSync("trash", args): exit status (null when it could not run), spawn error message and stderr.</summary>
    private static (int? Status, string? Error, string? Stderr) SpawnTrash(string[] args)
    {
        var start = new ProcessStartInfo("trash")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return (null, "spawnSync trash ENOENT", null);
            process.StandardInput.Close();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            _ = stdoutTask.GetAwaiter().GetResult();
            return (process.ExitCode, null, stderrTask.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            return (null, error.NativeErrorCode == 2 ? "spawnSync trash ENOENT" : $"spawnSync trash {error.Message}", null);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            return (null, $"spawnSync trash {error.Message}", null);
        }
    }

    private void CancelLoads()
    {
        if (currentLoad is not null)
        {
            currentLoad.Cancel();
            currentLoad = null;
            currentSessions = null;
        }
        if (allLoad is not null)
        {
            allLoad.Cancel();
            allLoad = null;
            allSessions = null;
        }
    }

    private void EnterRenameMode(string sessionPath, string? currentName)
    {
        mode = "rename";
        renameTargetPath = sessionPath;
        renameInput.SetValue(currentName ?? "");
        renameInput.Focused = true;

        var panel = new Container();
        panel.AddChild(new Text(theme.Bold("Rename Session"), 1, 0));
        panel.AddChild(new Spacer(1));
        panel.AddChild(renameInput);
        panel.AddChild(new Spacer(1));
        panel.AddChild(new Text(theme.Fg("muted", $"{KeybindingHints.KeyText("tui.select.confirm")} to save · {KeybindingHints.KeyText("tui.select.cancel")} to cancel"), 1, 0));

        BuildBaseLayout(panel, showHeader: false);
        requestRender();
    }

    private void ExitRenameMode()
    {
        mode = "list";
        renameTargetPath = null;

        BuildBaseLayout(sessionList);

        requestRender();
    }

    private async Task ConfirmRename(string value)
    {
        var next = TextUtils.JsTrim(value);
        if (next.Length == 0) return;
        var target = renameTargetPath;
        if (target is null)
        {
            ExitRenameMode();
            return;
        }

        // Find current name for callback
        var rename = renameSession;
        if (rename is null)
        {
            ExitRenameMode();
            return;
        }

        try
        {
            await rename(target, next);
            await RefreshSessionsAfterMutation();
        }
        finally
        {
            ExitRenameMode();
        }
    }

    private async Task LoadScope(SessionScope loadScope)
    {
        if ((loadScope == SessionScope.Current ? currentLoad : allLoad) is not null) return;

        var showCwd = loadScope == SessionScope.All;
        var controller = new CancellationTokenSource();
        if (loadScope == SessionScope.Current) currentLoad = controller;
        else allLoad = controller;
        header.SetScope(loadScope);
        header.SetLoading(true);
        requestRender();

        bool IsActive() => (loadScope == SessionScope.Current ? currentLoad : allLoad) == controller;
        void OnProgress(int loaded, int total, IReadOnlyList<PiSessionInfo>? partialSessions)
        {
            if (!IsActive()) return;
            if (partialSessions is not null)
            {
                IReadOnlyList<PiSessionInfo> sessions = [.. partialSessions];
                if (loadScope == SessionScope.Current) currentSessions = sessions;
                else allSessions = sessions;
                if (loadScope == scope) sessionList.SetSessions(sessions, showCwd);
            }
            if (loadScope != scope) return;
            header.SetProgress(loaded, total);
            requestRender();
        }

        try
        {
            var sessions = await (loadScope == SessionScope.Current
                ? currentSessionsLoader(OnProgress, controller.Token)
                : allSessionsLoader(OnProgress, controller.Token));
            if (!IsActive()) return;

            if (loadScope == SessionScope.Current)
            {
                currentSessions = sessions;
                currentLoad = null;
            }
            else
            {
                allSessions = sessions;
                allLoad = null;
            }

            if (loadScope != scope) return;
            header.SetLoading(false);
            sessionList.SetSessions(sessions, showCwd);
            requestRender();
        }
        catch (Exception error)
        {
            if (!IsActive()) return;
            if (loadScope == SessionScope.Current)
            {
                currentLoad = null;
                currentSessions = null;
            }
            else
            {
                allLoad = null;
                allSessions = null;
            }
            if (loadScope != scope) return;

            header.SetLoading(false);
            header.SetStatusMessage(("error", $"Failed to load sessions: {error.Message}"), 4000);
            sessionList.SetSessions([], showCwd);
            requestRender();
        }
    }

    private void ToggleSortMode()
    {
        // Cycle: threaded -> recent -> relevance -> threaded
        sortMode = sortMode == SortMode.Threaded ? SortMode.Recent : sortMode == SortMode.Recent ? SortMode.Relevance : SortMode.Threaded;
        header.SetSortMode(sortMode);
        sessionList.SetSortMode(sortMode);
        requestRender();
    }

    private void ToggleNameFilter()
    {
        nameFilter = nameFilter == NameFilter.All ? NameFilter.Named : NameFilter.All;
        header.SetNameFilter(nameFilter);
        sessionList.SetNameFilter(nameFilter);
        requestRender();
    }

    private async Task RefreshSessionsAfterMutation()
    {
        CancelLoads();
        currentSessions = null;
        allSessions = null;
        await LoadScope(scope);
    }

    private void ToggleScope()
    {
        scope = scope == SessionScope.Current ? SessionScope.All : SessionScope.Current;
        var sessions = scope == SessionScope.Current ? currentSessions : allSessions;
        var loading = (scope == SessionScope.Current ? currentLoad : allLoad) is not null;
        header.SetScope(scope);
        header.SetLoading(loading);
        sessionList.SetSessions(sessions ?? [], scope == SessionScope.All);
        requestRender();
        if (sessions is null && !loading) _ = LoadScope(scope);
    }

    public SessionList GetSessionList() => sessionList;
}
