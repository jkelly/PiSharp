// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/tree-selector.ts.
// Session entries are Pi's JSON session entries (type message/compaction/branch_summary/custom_message/custom/label/model_change/
// thinking_level_change/session_info/context_edit/usage, id, parentId, timestamp, ...) as JsonObject; SessionTreeNode is the
// source core/session-manager.ts SessionTreeNode.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;
using TuiKeybindingsManager = PiSharp.Tui.Pi.KeybindingsManager;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source SessionTreeNode: an entry, its children, and the resolved label (with the time of its latest change).</summary>
internal sealed class SessionTreeNode(JsonObject entry, List<SessionTreeNode>? children = null, string? label = null, string? labelTimestamp = null)
{
    public JsonObject Entry { get; } = entry;
    public List<SessionTreeNode> Children { get; } = children ?? [];
    /// <summary>Resolved label for this entry, if any.</summary>
    public string? Label { get; set; } = label;
    /// <summary>Timestamp of the latest label change for this entry, if any.</summary>
    public string? LabelTimestamp { get; set; } = labelTimestamp;
}

/// <summary>Source FilterMode: "default" | "no-tools" | "user-only" | "labeled-only" | "all".</summary>
internal enum FilterMode { Default, NoTools, UserOnly, LabeledOnly, All }

/// <summary>Reads Pi session entry JSON with JavaScript value semantics.</summary>
internal static class SessionEntryJson
{
    public static string? Str(JsonObject? obj, string name) =>
        obj is not null && obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    public static string Type(JsonObject entry) => Str(entry, "type") ?? "";
    public static string Id(JsonObject entry) => Str(entry, "id") ?? "";
    public static string? ParentId(JsonObject entry) => Str(entry, "parentId");
    public static JsonObject? Message(JsonObject entry) => entry["message"] as JsonObject;
    public static string? Role(JsonObject entry) => Str(Message(entry), "role");

    /// <summary>JavaScript truthiness of a JSON value (a missing property is <c>undefined</c>).</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => NumberValue(value) is var number && number != 0 && !double.IsNaN(number),
            JsonValueKind.True => true,
            _ => false
        },
        _ => true
    };

    /// <summary>JavaScript <c>String(value)</c> for a JSON value.</summary>
    public static string JsString(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => JsNumber(NumberValue(value)),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "null"
        },
        JsonArray array => string.Join(",", array.Select(item => item is null ? "" : JsString(item))),
        _ => "[object Object]"
    };

    public static string JsNumber(double number) =>
        double.IsNaN(number) ? "NaN" : double.IsPositiveInfinity(number) ? "Infinity" : double.IsNegativeInfinity(number) ? "-Infinity" :
        number == Math.Floor(number) && Math.Abs(number) < 1e21 ? ((decimal)number).ToString(CultureInfo.InvariantCulture) : number.ToString("R", CultureInfo.InvariantCulture);

    public static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? NumberValue(value) : null;

    /// <summary>A JSON number as a double, whatever CLR type the node was built from.</summary>
    private static double NumberValue(JsonValue value) => value.TryGetValue<double>(out var number) ? number :
        double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
}

/// <summary>Tree list component with selection and ASCII art visualization.</summary>
internal sealed class TreeList : IComponent, IInputHandler
{
    /// <summary>Gutter info: position (displayIndent where connector was) and whether to show │.</summary>
    private readonly record struct GutterInfo(int Position, bool Show);

    /// <summary>Flattened tree node for navigation.</summary>
    private sealed class FlatNode(SessionTreeNode node, int indent, bool showConnector, bool isLast, IReadOnlyList<GutterInfo> gutters, bool isVirtualRootChild)
    {
        public SessionTreeNode Node { get; } = node;
        /// <summary>Indentation level (each level = 3 chars).</summary>
        public int Indent { get; set; } = indent;
        /// <summary>Whether to show connector (├─ or └─) - true if parent has multiple children.</summary>
        public bool ShowConnector { get; set; } = showConnector;
        /// <summary>If showConnector, true = last sibling (└─), false = not last (├─).</summary>
        public bool IsLast { get; set; } = isLast;
        /// <summary>Gutter info for each ancestor branch point.</summary>
        public IReadOnlyList<GutterInfo> Gutters { get; set; } = gutters;
        /// <summary>True if this node is a root under a virtual branching root (multiple roots).</summary>
        public bool IsVirtualRootChild { get; set; } = isVirtualRootChild;
        public string Id => SessionEntryJson.Id(Node.Entry);
    }

    private readonly record struct HorizontalViewportRow(string Gutter, string Body, int AnchorCol, int BodyWidth, bool IsSelected);

    /// <summary>Tool call info for lookup.</summary>
    private readonly record struct ToolCallInfo(string Name, JsonObject Arguments);

    private const int TreeGutterWidth = 2;
    private const int MinVisibleAnchorContentWidth = 4;
    private const int MaxVisibleAnchorContentWidth = 20;
    private const int MinAnchorContextWidth = 2;
    private const int MaxAnchorContextWidth = 12;

    /// <summary>Render tree rows into a horizontally clipped viewport. The tree gutter is always kept visible. The row bodies are shifted
    /// left only when the selected row's anchor (the start of its entry text after tree indentation/markers) would otherwise be too far
    /// right to see useful content.</summary>
    private static List<string> RenderHorizontalViewport(List<HorizontalViewportRow> rows, int width)
    {
        var viewportWidth = Math.Max(0, width - TreeGutterWidth);
        var maxBodyWidth = rows.Aggregate(0, (max, row) => Math.Max(max, row.BodyWidth));
        var maxHorizontalScroll = Math.Max(0, maxBodyWidth - viewportWidth);
        HorizontalViewportRow? selectedRow = rows.FindIndex(row => row.IsSelected) is var found and >= 0 ? rows[found] : null;

        // Only pan horizontally when needed to keep enough selected-row content visible after its anchor.
        var horizontalScroll = 0;
        if (selectedRow is { } selected && maxHorizontalScroll > 0)
        {
            var minVisibleAnchorContentWidth = Math.Min(MaxVisibleAnchorContentWidth, Math.Max(MinVisibleAnchorContentWidth, viewportWidth / 3));
            if (selected.AnchorCol > viewportWidth - minVisibleAnchorContentWidth)
            {
                var anchorContextWidth = Math.Min(MaxAnchorContextWidth, Math.Max(MinAnchorContextWidth, viewportWidth / 4));
                horizontalScroll = Math.Min(maxHorizontalScroll, selected.AnchorCol - anchorContextWidth);
            }
        }

        // Clip only the body; the fixed-width gutter remains visible as navigation context.
        return [.. rows.Select(row =>
        {
            var line = horizontalScroll > 0
                ? row.Gutter + TextUtils.SliceByColumn(row.Body, horizontalScroll, viewportWidth, true) + "\u001b[0m"
                : row.Gutter + row.Body;
            return TextUtils.TruncateToWidth(line, width, "");
        })];
    }

    private List<FlatNode> flatNodes;
    private List<FlatNode> filteredNodes = [];
    private int selectedIndex;
    private readonly string? currentLeafId;
    private readonly int maxVisibleLines;
    private FilterMode filterMode;
    private string searchQuery = "";
    private readonly Dictionary<string, ToolCallInfo> toolCallMap = new(StringComparer.Ordinal);
    private bool multipleRoots;
    private bool showLabelTimestamps;
    private readonly HashSet<string> activePathIds = new(StringComparer.Ordinal);
    private Dictionary<string, string?> visibleParentMap = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> visibleChildrenMap = new(StringComparer.Ordinal);
    private List<string> visibleRootIds = [];
    private string? lastSelectedId;
    private readonly HashSet<string> foldedNodes = new(StringComparer.Ordinal);

    public Action<string>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }
    public Action<string?>? OnCopy { get; set; }
    public Action<string, string?>? OnLabelEdit { get; set; }

    public TreeList(IReadOnlyList<SessionTreeNode> tree, string? currentLeafId, int maxVisibleLines, string? initialSelectedId = null, FilterMode? initialFilterMode = null)
    {
        this.currentLeafId = currentLeafId;
        this.maxVisibleLines = maxVisibleLines;
        filterMode = initialFilterMode ?? FilterMode.Default;
        multipleRoots = tree.Count > 1;
        flatNodes = FlattenTree(tree);
        BuildActivePath();
        ApplyFilter();

        // Start with initialSelectedId if provided, otherwise current leaf
        var targetId = initialSelectedId ?? currentLeafId;
        selectedIndex = FindNearestVisibleIndex(targetId);
        lastSelectedId = Selected()?.Id;
    }

    private FlatNode? Selected() => selectedIndex >= 0 && selectedIndex < filteredNodes.Count ? filteredNodes[selectedIndex] : null;

    private Dictionary<string, FlatNode> EntryMap()
    {
        var entryMap = new Dictionary<string, FlatNode>(StringComparer.Ordinal);
        foreach (var flatNode in flatNodes) entryMap[flatNode.Id] = flatNode;
        return entryMap;
    }

    /// <summary>Find the index of the nearest visible entry, walking up the parent chain if needed. Returns the index in filteredNodes,
    /// or the last index as fallback.</summary>
    private int FindNearestVisibleIndex(string? entryId)
    {
        if (filteredNodes.Count == 0) return 0;

        // Build a map for parent lookup
        var entryMap = EntryMap();

        // Build a map of visible entry IDs to their indices in filteredNodes
        var visibleIdToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < filteredNodes.Count; i++) visibleIdToIndex[filteredNodes[i].Id] = i;

        // Walk from entryId up to root, looking for a visible entry
        var currentId = entryId;
        while (currentId is not null)
        {
            if (visibleIdToIndex.TryGetValue(currentId, out var index)) return index;
            if (!entryMap.TryGetValue(currentId, out var node)) break;
            currentId = SessionEntryJson.ParentId(node.Node.Entry);
        }

        // Fallback: last visible entry
        return filteredNodes.Count - 1;
    }

    /// <summary>Build the set of entry IDs on the path from root to current leaf.</summary>
    private void BuildActivePath()
    {
        activePathIds.Clear();
        if (string.IsNullOrEmpty(currentLeafId)) return;

        // Build a map of id -> entry for parent lookup
        var entryMap = EntryMap();

        // Walk from leaf to root
        var currentId = currentLeafId;
        while (!string.IsNullOrEmpty(currentId))
        {
            activePathIds.Add(currentId);
            if (!entryMap.TryGetValue(currentId, out var node)) break;
            currentId = SessionEntryJson.ParentId(node.Node.Entry);
        }
    }

    private List<FlatNode> FlattenTree(IReadOnlyList<SessionTreeNode> roots)
    {
        var result = new List<FlatNode>();
        toolCallMap.Clear();

        // Indentation rules:
        // - At indent 0: stay at 0 unless parent has >1 children (then +1)
        // - At indent 1: children always go to indent 2 (visual grouping of subtree)
        // - At indent 2+: stay flat for single-child chains, +1 only if parent branches

        // Stack items: [node, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild]
        var stack = new Stack<(SessionTreeNode Node, int Indent, bool JustBranched, bool ShowConnector, bool IsLast, IReadOnlyList<GutterInfo> Gutters, bool IsVirtualRootChild)>();

        // Determine which subtrees contain the active leaf (to sort current branch first)
        // Use iterative post-order traversal to avoid stack overflow
        var containsActive = new Dictionary<SessionTreeNode, bool>(ReferenceEqualityComparer.Instance);
        var leafId = currentLeafId;
        {
            // Build list in pre-order, then process in reverse for post-order effect
            var allNodes = new List<SessionTreeNode>();
            var preOrderStack = new List<SessionTreeNode>(roots);
            while (preOrderStack.Count > 0)
            {
                var node = preOrderStack[^1];
                preOrderStack.RemoveAt(preOrderStack.Count - 1);
                allNodes.Add(node);
                // Push children in reverse so they're processed left-to-right
                for (var i = node.Children.Count - 1; i >= 0; i--) preOrderStack.Add(node.Children[i]);
            }
            // Process in reverse (post-order): children before parents
            for (var i = allNodes.Count - 1; i >= 0; i--)
            {
                var node = allNodes[i];
                var has = leafId is not null && SessionEntryJson.Id(node.Entry) == leafId;
                foreach (var child in node.Children) if (containsActive.GetValueOrDefault(child)) has = true;
                containsActive[node] = has;
            }
        }

        // Add roots in reverse order, prioritizing the one containing the active leaf
        // If multiple roots, treat them as children of a virtual root that branches
        var hasMultipleRoots = roots.Count > 1;
        var orderedRoots = roots.OrderByDescending(root => containsActive.GetValueOrDefault(root) ? 1 : 0).ToList();
        for (var i = orderedRoots.Count - 1; i >= 0; i--)
        {
            var isLast = i == orderedRoots.Count - 1;
            stack.Push((orderedRoots[i], hasMultipleRoots ? 1 : 0, hasMultipleRoots, hasMultipleRoots, isLast, [], hasMultipleRoots));
        }

        while (stack.Count > 0)
        {
            var (node, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild) = stack.Pop();

            // Extract tool calls from assistant messages for later lookup
            var entry = node.Entry;
            if (SessionEntryJson.Type(entry) == "message" && SessionEntryJson.Role(entry) == "assistant" && SessionEntryJson.Message(entry)?["content"] is JsonArray content)
            {
                foreach (var block in content)
                {
                    if (block is JsonObject call && SessionEntryJson.Str(call, "type") == "toolCall")
                        toolCallMap[SessionEntryJson.Str(call, "id") ?? "undefined"] = new ToolCallInfo(SessionEntryJson.Str(call, "name") ?? "", call["arguments"] as JsonObject ?? new JsonObject());
                }
            }

            result.Add(new FlatNode(node, indent, showConnector, isLast, gutters, isVirtualRootChild));

            var children = node.Children;
            var multipleChildren = children.Count > 1;

            // Order children so the branch containing the active leaf comes first
            var orderedChildren = children.Where(child => containsActive.GetValueOrDefault(child)).Concat(children.Where(child => !containsActive.GetValueOrDefault(child))).ToList();

            // Calculate child indent
            int childIndent;
            if (multipleChildren) childIndent = indent + 1; // Parent branches: children get +1
            else if (justBranched && indent > 0) childIndent = indent + 1; // First generation after a branch: +1 for visual grouping
            else childIndent = indent; // Single-child chain: stay flat

            // Build gutters for children
            // If this node showed a connector, add a gutter entry for descendants
            // Only add gutter if connector is actually displayed (not suppressed for virtual root children)
            var connectorDisplayed = showConnector && !isVirtualRootChild;
            // When connector is displayed, add a gutter entry at the connector's position
            // Connector is at position (displayIndent - 1), so gutter should be there too
            var currentDisplayIndent = multipleRoots ? Math.Max(0, indent - 1) : indent;
            var connectorPosition = Math.Max(0, currentDisplayIndent - 1);
            IReadOnlyList<GutterInfo> childGutters = connectorDisplayed ? [.. gutters, new GutterInfo(connectorPosition, !isLast)] : gutters;

            // Add children in reverse order
            for (var i = orderedChildren.Count - 1; i >= 0; i--)
            {
                var childIsLast = i == orderedChildren.Count - 1;
                stack.Push((orderedChildren[i], childIndent, multipleChildren, multipleChildren, childIsLast, childGutters, false));
            }
        }

        return result;
    }

    private void ApplyFilter()
    {
        // Update lastSelectedId only when we have a valid selection (non-empty list)
        // This preserves the selection when switching through empty filter results
        if (filteredNodes.Count > 0) lastSelectedId = Selected()?.Id ?? lastSelectedId;

        var searchTokens = SplitWhitespace(searchQuery.ToLowerInvariant());

        filteredNodes = [.. flatNodes.Where(flatNode =>
        {
            var entry = flatNode.Node.Entry;
            var type = SessionEntryJson.Type(entry);
            if (type == "usage") return false;
            var isCurrentLeaf = SessionEntryJson.Id(entry) == currentLeafId;

            // Skip assistant messages with only tool calls (no text) unless error/aborted
            // Always show current leaf so active position is visible
            if (type == "message" && SessionEntryJson.Role(entry) == "assistant" && !isCurrentLeaf)
            {
                var msg = SessionEntryJson.Message(entry);
                var hasText = HasTextContent(msg?["content"]);
                var stopReason = SessionEntryJson.Str(msg, "stopReason");
                var isErrorOrAborted = !string.IsNullOrEmpty(stopReason) && stopReason != "stop" && stopReason != "toolUse";
                // Only hide if no text AND not an error/aborted message
                if (!hasText && !isErrorOrAborted) return false;
            }

            // Entry types hidden in default view (settings/bookkeeping)
            var isSettingsEntry = type is "label" or "context_edit" or "custom" or "model_change" or "thinking_level_change" or "session_info";

            var passesFilter = filterMode switch
            {
                // Just user messages
                FilterMode.UserOnly => type == "message" && SessionEntryJson.Role(entry) == "user",
                // Default minus tool results
                FilterMode.NoTools => !isSettingsEntry && !(type == "message" && SessionEntryJson.Role(entry) == "toolResult"),
                // Just labeled entries
                FilterMode.LabeledOnly => flatNode.Node.Label is not null,
                // Show everything
                FilterMode.All => true,
                // Default mode: hide settings/bookkeeping entries
                _ => !isSettingsEntry
            };

            if (!passesFilter) return false;

            // Apply search filter
            if (searchTokens.Count > 0)
            {
                var nodeText = GetSearchableText(flatNode.Node).ToLowerInvariant();
                return searchTokens.All(token => nodeText.Contains(token, StringComparison.Ordinal));
            }

            return true;
        })];

        // Filter out descendants of folded nodes.
        if (foldedNodes.Count > 0)
        {
            var skipSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var flatNode in flatNodes)
            {
                var id = flatNode.Id;
                var parentId = SessionEntryJson.ParentId(flatNode.Node.Entry);
                if (parentId is not null && (foldedNodes.Contains(parentId) || skipSet.Contains(parentId))) skipSet.Add(id);
            }
            filteredNodes = [.. filteredNodes.Where(flatNode => !skipSet.Contains(flatNode.Id))];
        }

        // Recalculate visual structure (indent, connectors, gutters) based on visible tree
        RecalculateVisualStructure();

        // Try to preserve cursor on the same node, or find nearest visible ancestor
        if (!string.IsNullOrEmpty(lastSelectedId)) selectedIndex = FindNearestVisibleIndex(lastSelectedId);
        else if (selectedIndex >= filteredNodes.Count) selectedIndex = Math.Max(0, filteredNodes.Count - 1); // Clamp index if out of bounds

        // Update lastSelectedId to the actual selection (may have changed due to parent walk)
        if (filteredNodes.Count > 0) lastSelectedId = Selected()?.Id ?? lastSelectedId;
    }

    /// <summary>JavaScript <c>text.split(/\s+/).filter(Boolean)</c>.</summary>
    private static List<string> SplitWhitespace(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (TextUtils.IsJsWhitespace(ch)) { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } }
            else current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Recompute indentation/connectors for the filtered view. Filtering can hide intermediate entries; descendants attach to
    /// the nearest visible ancestor. Keep indentation semantics aligned with flattenTree() so single-child chains don't drift right.</summary>
    private void RecalculateVisualStructure()
    {
        if (filteredNodes.Count == 0) return;

        var visibleIds = new HashSet<string>(filteredNodes.Select(n => n.Id), StringComparer.Ordinal);

        // Build entry map for efficient parent lookup (using full tree)
        var entryMap = EntryMap();

        // Find nearest visible ancestor for a node
        string? FindVisibleAncestor(string nodeId)
        {
            var currentId = entryMap.TryGetValue(nodeId, out var self) ? SessionEntryJson.ParentId(self.Node.Entry) : null;
            while (currentId is not null)
            {
                if (visibleIds.Contains(currentId)) return currentId;
                currentId = entryMap.TryGetValue(currentId, out var next) ? SessionEntryJson.ParentId(next.Node.Entry) : null;
            }
            return null;
        }

        // Build visible tree structure:
        // - visibleParent: nodeId → nearest visible ancestor (or null for roots)
        // - visibleChildren: parentId → list of visible children (in filteredNodes order)
        var visibleParent = new Dictionary<string, string?>(StringComparer.Ordinal);
        var visibleChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var rootIds = new List<string>(); // root-level nodes

        foreach (var flatNode in filteredNodes)
        {
            var nodeId = flatNode.Id;
            var ancestorId = FindVisibleAncestor(nodeId);
            visibleParent[nodeId] = ancestorId;
            if (ancestorId is null) rootIds.Add(nodeId);
            else
            {
                if (!visibleChildren.TryGetValue(ancestorId, out var list)) visibleChildren[ancestorId] = list = [];
                list.Add(nodeId);
            }
        }

        // Update multipleRoots based on visible roots
        multipleRoots = rootIds.Count > 1;

        // Build a map for quick lookup: nodeId → FlatNode
        var filteredNodeMap = new Dictionary<string, FlatNode>(StringComparer.Ordinal);
        foreach (var flatNode in filteredNodes) filteredNodeMap[flatNode.Id] = flatNode;

        // DFS over the visible tree using flattenTree() indentation semantics
        var stack = new Stack<(string NodeId, int Indent, bool JustBranched, bool ShowConnector, bool IsLast, IReadOnlyList<GutterInfo> Gutters, bool IsVirtualRootChild)>();

        // Add visible roots in reverse order (to process in forward order via stack)
        for (var i = rootIds.Count - 1; i >= 0; i--)
        {
            var isLast = i == rootIds.Count - 1;
            stack.Push((rootIds[i], multipleRoots ? 1 : 0, multipleRoots, multipleRoots, isLast, [], multipleRoots));
        }

        while (stack.Count > 0)
        {
            var (nodeId, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild) = stack.Pop();

            if (!filteredNodeMap.TryGetValue(nodeId, out var flatNode)) continue;

            // Update this node's visual properties
            flatNode.Indent = indent;
            flatNode.ShowConnector = showConnector;
            flatNode.IsLast = isLast;
            flatNode.Gutters = gutters;
            flatNode.IsVirtualRootChild = isVirtualRootChild;

            // Get visible children of this node
            var children = visibleChildren.GetValueOrDefault(nodeId) ?? [];
            var multipleChildren = children.Count > 1;

            // Child indent follows flattenTree(): branch points (and first generation after a branch) shift +1
            int childIndent;
            if (multipleChildren) childIndent = indent + 1;
            else if (justBranched && indent > 0) childIndent = indent + 1;
            else childIndent = indent;

            // Child gutters follow flattenTree() connector/gutter rules
            var connectorDisplayed = showConnector && !isVirtualRootChild;
            var currentDisplayIndent = multipleRoots ? Math.Max(0, indent - 1) : indent;
            var connectorPosition = Math.Max(0, currentDisplayIndent - 1);
            IReadOnlyList<GutterInfo> childGutters = connectorDisplayed ? [.. gutters, new GutterInfo(connectorPosition, !isLast)] : gutters;

            // Add children in reverse order (to process in forward order via stack)
            for (var i = children.Count - 1; i >= 0; i--)
            {
                var childIsLast = i == children.Count - 1;
                stack.Push((children[i], childIndent, multipleChildren, multipleChildren, childIsLast, childGutters, false));
            }
        }

        // Store visible tree maps for ancestor/descendant lookups in navigation
        visibleParentMap = visibleParent;
        visibleChildrenMap = visibleChildren;
        visibleRootIds = rootIds;
    }

    private List<string>? VisibleChildren(string? parentId) => parentId is null ? visibleRootIds : visibleChildrenMap.GetValueOrDefault(parentId);

    /// <summary>Get searchable text content from a node.</summary>
    private string GetSearchableText(SessionTreeNode node)
    {
        var entry = node.Entry;
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(node.Label)) parts.Add(node.Label);

        switch (SessionEntryJson.Type(entry))
        {
            case "message":
            {
                var msg = SessionEntryJson.Message(entry);
                var role = SessionEntryJson.Str(msg, "role");
                parts.Add(role ?? "");
                if (msg is not null && SessionEntryJson.Truthy(msg["content"])) parts.Add(ExtractContent(msg["content"]));
                if (role == "bashExecution" && SessionEntryJson.Str(msg, "command") is { Length: > 0 } command) parts.Add(command);
                break;
            }
            case "custom_message":
                parts.Add(SessionEntryJson.Str(entry, "customType") ?? "");
                parts.Add(SessionEntryJson.Str(entry, "content") ?? ExtractContent(entry["content"]));
                break;
            case "compaction":
                parts.Add("compaction");
                break;
            case "branch_summary":
                parts.Add("branch summary"); parts.Add(SessionEntryJson.Str(entry, "summary") ?? "");
                break;
            case "session_info":
                parts.Add("title");
                if (SessionEntryJson.Str(entry, "name") is { Length: > 0 } name) parts.Add(name);
                break;
            case "model_change":
                parts.Add("model"); parts.Add(SessionEntryJson.Str(entry, "modelId") ?? "");
                break;
            case "thinking_level_change":
                parts.Add("thinking"); parts.Add(SessionEntryJson.Str(entry, "thinkingLevel") ?? "");
                break;
            case "custom":
                parts.Add("custom"); parts.Add(SessionEntryJson.Str(entry, "customType") ?? "");
                break;
            case "context_edit":
                parts.Add("context edit"); parts.Add(IsOmit(entry) ? "omit" : "replace"); parts.Add(SessionEntryJson.Str(entry, "targetId") ?? "");
                break;
            case "label":
                parts.Add("label"); parts.Add(SessionEntryJson.Str(entry, "label") ?? "");
                break;
        }

        return string.Join(" ", parts);
    }

    /// <summary>Source <c>entry.replacement === null</c>.</summary>
    private static bool IsOmit(JsonObject entry) => entry.TryGetPropertyValue("replacement", out var replacement) && replacement is null;

    public void Invalidate() { }

    public string GetSearchQuery() => searchQuery;

    public SessionTreeNode? GetSelectedNode() => Selected()?.Node;

    public void CopySelected()
    {
        var node = GetSelectedNode();
        OnCopy?.Invoke(node is not null ? GetEntryCopyText(node) : null);
    }

    public void UpdateNodeLabel(string entryId, string? label, string? labelTimestamp = null)
    {
        foreach (var flatNode in flatNodes)
        {
            if (flatNode.Id != entryId) continue;
            flatNode.Node.Label = label;
            flatNode.Node.LabelTimestamp = !string.IsNullOrEmpty(label) ? labelTimestamp ?? PiSessions.IsoTimestamp(DateTimeOffset.UtcNow) : null;
            break;
        }
    }

    private string GetStatusLabels()
    {
        var labels = filterMode switch
        {
            FilterMode.NoTools => " [no-tools]",
            FilterMode.UserOnly => " [user]",
            FilterMode.LabeledOnly => " [labeled]",
            FilterMode.All => " [all]",
            _ => ""
        };
        if (showLabelTimestamps) labels += " [+label time]";
        return labels;
    }

    public List<string> Render(int width)
    {
        var lines = new List<string>();

        if (filteredNodes.Count == 0)
        {
            lines.Add(TextUtils.TruncateToWidth(theme.Fg("muted", "  No entries found"), width));
            lines.Add(TextUtils.TruncateToWidth(theme.Fg("muted", $"  (0/0){GetStatusLabels()}"), width));
            return lines;
        }

        var startIndex = Math.Max(0, Math.Min(selectedIndex - maxVisibleLines / 2, filteredNodes.Count - maxVisibleLines));
        var endIndex = Math.Min(startIndex + maxVisibleLines, filteredNodes.Count);

        var renderedRows = new List<HorizontalViewportRow>();
        for (var i = startIndex; i < endIndex; i++)
        {
            var flatNode = filteredNodes[i];
            var entryId = flatNode.Id;
            var isSelected = i == selectedIndex;

            // Build line: cursor + prefix + path marker + label + content
            var cursor = isSelected ? theme.Fg("accent", "› ") : "  ";

            // If multiple roots, shift display (roots at 0, not 1)
            var displayIndent = multipleRoots ? Math.Max(0, flatNode.Indent - 1) : flatNode.Indent;

            // Build prefix with gutters at their correct positions
            // Each gutter has a position (displayIndent where its connector was shown)
            var connector = flatNode.ShowConnector && !flatNode.IsVirtualRootChild ? flatNode.IsLast ? "└─ " : "├─ " : "";
            var connectorPosition = connector.Length > 0 ? displayIndent - 1 : -1;

            // Build prefix char by char, placing gutters and connector at their positions
            var totalChars = displayIndent * 3;
            var prefixChars = new StringBuilder();
            var isFolded = foldedNodes.Contains(entryId);
            for (var c = 0; c < totalChars; c++)
            {
                var level = c / 3;
                var posInLevel = c % 3;

                // Check if there's a gutter at this level
                var gutterIndex = -1;
                for (var g = 0; g < flatNode.Gutters.Count; g++) if (flatNode.Gutters[g].Position == level) { gutterIndex = g; break; }
                if (gutterIndex >= 0)
                {
                    if (posInLevel == 0) prefixChars.Append(flatNode.Gutters[gutterIndex].Show ? "│" : " ");
                    else prefixChars.Append(' ');
                }
                else if (connector.Length > 0 && level == connectorPosition)
                {
                    // Connector at this level, with fold indicator
                    if (posInLevel == 0) prefixChars.Append(flatNode.IsLast ? "└" : "├");
                    else if (posInLevel == 1)
                    {
                        var foldable = IsFoldable(entryId);
                        prefixChars.Append(isFolded ? "⊞" : foldable ? "⊟" : "─");
                    }
                    else prefixChars.Append(' ');
                }
                else prefixChars.Append(' ');
            }
            var prefix = prefixChars.ToString();

            // Fold marker for nodes without connectors (roots)
            var showsFoldInConnector = flatNode.ShowConnector && !flatNode.IsVirtualRootChild;
            var foldMarker = isFolded && !showsFoldInConnector ? theme.Fg("accent", "⊞ ") : "";

            // Active path marker - shown right before the entry text
            var isOnActivePath = activePathIds.Contains(entryId);
            var pathMarker = isOnActivePath ? theme.Fg("accent", "• ") : "";

            var label = !string.IsNullOrEmpty(flatNode.Node.Label) ? theme.Fg("warning", $"[{flatNode.Node.Label}] ") : "";
            var labelTimestamp = showLabelTimestamps && !string.IsNullOrEmpty(flatNode.Node.Label) && !string.IsNullOrEmpty(flatNode.Node.LabelTimestamp)
                ? theme.Fg("muted", $"{FormatLabelTimestamp(flatNode.Node.LabelTimestamp)} ")
                : "";
            var content = GetEntryDisplayText(flatNode.Node, isSelected);
            var prefixPart = theme.Fg("dim", prefix) + foldMarker + pathMarker;
            var anchorCol = TextUtils.VisibleWidth(prefixPart);
            var gutter = cursor;
            var body = prefixPart + label + labelTimestamp + content;
            if (isSelected)
            {
                gutter = theme.Bg("selectedBg", gutter);
                body = theme.Bg("selectedBg", body);
            }
            renderedRows.Add(new HorizontalViewportRow(gutter, body, anchorCol, TextUtils.VisibleWidth(body), isSelected));
        }

        lines.AddRange(RenderHorizontalViewport(renderedRows, width));
        lines.Add(TextUtils.TruncateToWidth(theme.Fg("muted", $"  ({selectedIndex + 1}/{filteredNodes.Count}){GetStatusLabels()}"), width));

        return lines;
    }

    private static string Normalize(string s) => TextUtils.JsTrim(s.Replace('\n', ' ').Replace('\t', ' '));

    private string GetEntryDisplayText(SessionTreeNode node, bool isSelected)
    {
        var entry = node.Entry;
        string result;

        switch (SessionEntryJson.Type(entry))
        {
            case "message":
            {
                var msg = SessionEntryJson.Message(entry);
                var role = SessionEntryJson.Str(msg, "role");
                if (role == "user")
                {
                    var content = Normalize(ExtractContent(msg?["content"]));
                    result = theme.Fg("accent", "user: ") + content;
                }
                else if (role == "assistant")
                {
                    var textContent = Normalize(ExtractContent(msg?["content"]));
                    if (textContent.Length > 0) result = theme.Fg("success", "assistant: ") + textContent;
                    else if (SessionEntryJson.Str(msg, "stopReason") == "aborted") result = theme.Fg("success", "assistant: ") + theme.Fg("muted", "(aborted)");
                    else if (SessionEntryJson.Str(msg, "errorMessage") is { Length: > 0 } errorMessage)
                    {
                        var errMsg = Normalize(errorMessage);
                        if (errMsg.Length > 80) errMsg = errMsg[..80];
                        result = theme.Fg("success", "assistant: ") + theme.Fg("error", errMsg);
                    }
                    else result = theme.Fg("success", "assistant: ") + theme.Fg("muted", "(no content)");
                }
                else if (role == "toolResult")
                {
                    var toolCallId = SessionEntryJson.Str(msg, "toolCallId");
                    if (!string.IsNullOrEmpty(toolCallId) && toolCallMap.TryGetValue(toolCallId, out var toolCall))
                        result = theme.Fg("muted", FormatToolCall(toolCall.Name, toolCall.Arguments));
                    else result = theme.Fg("muted", $"[{SessionEntryJson.Str(msg, "toolName") ?? "tool"}]");
                }
                else if (role == "bashExecution")
                    result = theme.Fg("dim", $"[bash]: {Normalize(SessionEntryJson.Str(msg, "command") ?? "")}");
                else result = theme.Fg("dim", $"[{role ?? "undefined"}]");
                break;
            }
            case "custom_message":
            {
                var content = SessionEntryJson.Str(entry, "content") ?? (entry["content"] is JsonArray blocks
                    ? string.Concat(blocks.OfType<JsonObject>().Where(c => SessionEntryJson.Str(c, "type") == "text").Select(c => SessionEntryJson.Str(c, "text") ?? ""))
                    : "");
                result = theme.Fg("customMessageLabel", $"[{SessionEntryJson.Str(entry, "customType") ?? "undefined"}]: ") + Normalize(content);
                break;
            }
            case "compaction":
            {
                var tokensBefore = SessionEntryJson.Number(entry["tokensBefore"]) ?? double.NaN;
                var tokens = Math.Floor(tokensBefore / 1000 + 0.5); // Math.round
                result = theme.Fg("borderAccent", $"[compaction: {SessionEntryJson.JsNumber(tokens)}k tokens]");
                break;
            }
            case "branch_summary":
                result = theme.Fg("warning", "[branch summary]: ") + Normalize(SessionEntryJson.Str(entry, "summary") ?? "");
                break;
            case "model_change":
                result = theme.Fg("dim", $"[model: {SessionEntryJson.Str(entry, "modelId") ?? "undefined"}]");
                break;
            case "thinking_level_change":
                result = theme.Fg("dim", $"[thinking: {SessionEntryJson.Str(entry, "thinkingLevel") ?? "undefined"}]");
                break;
            case "custom":
                result = theme.Fg("dim", $"[custom: {SessionEntryJson.Str(entry, "customType") ?? "undefined"}]");
                break;
            case "context_edit":
                result = theme.Fg("dim", $"[context {(IsOmit(entry) ? "omit" : "replace")}: {SessionEntryJson.Str(entry, "targetId") ?? "undefined"}]");
                break;
            case "label":
                result = theme.Fg("dim", $"[label: {SessionEntryJson.Str(entry, "label") ?? "(cleared)"}]");
                break;
            case "session_info":
                result = SessionEntryJson.Str(entry, "name") is { Length: > 0 } name
                    ? theme.Fg("dim", "[title: ") + theme.Fg("dim", name) + theme.Fg("dim", "]")
                    : theme.Fg("dim", "[title: ") + theme.Italic(theme.Fg("dim", "empty")) + theme.Fg("dim", "]");
                break;
            default:
                result = "";
                break;
        }

        return isSelected ? theme.Bold(result) : result;
    }

    private static string FormatLabelTimestamp(string timestamp)
    {
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) return "aN/NaN/NaN NaN:NaN";
        var date = parsed.ToLocalTime().DateTime;
        var now = DateTime.Now;
        var time = $"{date.Hour:00}:{date.Minute:00}";

        if (date.Year == now.Year && date.Month == now.Month && date.Day == now.Day) return time;

        var month = date.Month;
        var day = date.Day;
        if (date.Year == now.Year) return $"{month}/{day} {time}";

        var year = date.Year.ToString(CultureInfo.InvariantCulture);
        return $"{(year.Length > 2 ? year[^2..] : year)}/{month}/{day} {time}";
    }

    private static string ExtractContent(JsonNode? content)
    {
        var full = ExtractFullContent(content);
        return full.Length > 200 ? full[..200] : full;
    }

    private static string ExtractFullContent(JsonNode? content)
    {
        if (content is JsonValue value && value.GetValueKind() == JsonValueKind.String) return value.GetValue<string>();
        if (content is not JsonArray blocks) return "";

        var result = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block is JsonObject obj && SessionEntryJson.Str(obj, "type") == "text")
                result.Append(obj.TryGetPropertyValue("text", out var text) ? SessionEntryJson.JsString(text) : "undefined");
        }
        return result.ToString();
    }

    private static string? GetEntryCopyText(SessionTreeNode node)
    {
        var entry = node.Entry;
        string? text = null;

        switch (SessionEntryJson.Type(entry))
        {
            case "message":
            {
                var msg = SessionEntryJson.Message(entry);
                if (SessionEntryJson.Str(msg, "role") == "bashExecution") text = SessionEntryJson.Str(msg, "command");
                else if (msg is not null && msg.ContainsKey("content"))
                {
                    text = ExtractFullContent(msg["content"]);
                    if (text.Length == 0 && SessionEntryJson.Str(msg, "role") == "assistant") text = SessionEntryJson.Str(msg, "errorMessage");
                }
                break;
            }
            case "custom_message":
                text = ExtractFullContent(entry["content"]);
                break;
            case "compaction":
            case "branch_summary":
                text = SessionEntryJson.Str(entry, "summary");
                break;
        }

        return text is not null && TextUtils.JsTrim(text).Length > 0 ? text : null;
    }

    private static bool HasTextContent(JsonNode? content)
    {
        if (content is JsonValue value && value.GetValueKind() == JsonValueKind.String) return TextUtils.JsTrim(value.GetValue<string>()).Length > 0;
        if (content is JsonArray blocks)
        {
            foreach (var c in blocks)
            {
                if (c is JsonObject obj && SessionEntryJson.Str(obj, "type") == "text" && SessionEntryJson.Str(obj, "text") is { Length: > 0 } text && TextUtils.JsTrim(text).Length > 0)
                    return true;
            }
        }
        return false;
    }

    /// <summary>JavaScript <c>String(a || b || fallback)</c>.</summary>
    private static string Or(JsonObject args, string fallback, params string[] names)
    {
        foreach (var name in names) if (SessionEntryJson.Truthy(args[name])) return SessionEntryJson.JsString(args[name]);
        return fallback;
    }

    internal static string FormatToolCall(string name, JsonObject args)
    {
        static string ShortenPath(string p)
        {
            var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            if (home.Length > 0 && p.StartsWith(home, StringComparison.Ordinal)) return "~" + p[home.Length..];
            return p;
        }

        switch (name)
        {
            case "read":
            {
                var path = ShortenPath(Or(args, "", "path", "file_path"));
                var offset = SessionEntryJson.Number(args["offset"]);
                var limit = SessionEntryJson.Number(args["limit"]);
                var display = path;
                if (offset is not null || limit is not null)
                {
                    var start = offset ?? 1;
                    var end = limit is not null ? SessionEntryJson.JsNumber(start + limit.Value - 1) : "";
                    display += $":{SessionEntryJson.JsNumber(start)}{(end.Length > 0 && end != "0" && end != "NaN" ? $"-{end}" : "")}";
                }
                return $"[read: {display}]";
            }
            case "write":
                return $"[write: {ShortenPath(Or(args, "", "path", "file_path"))}]";
            case "edit":
                return $"[edit: {ShortenPath(Or(args, "", "path", "file_path"))}]";
            case "bash":
            {
                var rawCmd = Or(args, "", "command");
                var cmd = Normalize(rawCmd);
                if (cmd.Length > 50) cmd = cmd[..50];
                return $"[bash: {cmd}{(rawCmd.Length > 50 ? "..." : "")}]";
            }
            case "grep":
                return $"[grep: /{Or(args, "", "pattern")}/ in {ShortenPath(Or(args, ".", "path"))}]";
            case "find":
                return $"[find: {Or(args, "", "pattern")} in {ShortenPath(Or(args, ".", "path"))}]";
            case "ls":
                return $"[ls: {ShortenPath(Or(args, ".", "path"))}]";
            default:
            {
                // Custom tool - show name and truncated JSON args
                var json = PiJson.Stringify(args);
                var argsStr = json.Length > 40 ? json[..40] : json;
                return $"[{name}: {argsStr}{(json.Length > 40 ? "..." : "")}]";
            }
        }
    }

    public void HandleInput(string keyData)
    {
        var kb = TuiKeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.select.up"))
            selectedIndex = selectedIndex == 0 ? filteredNodes.Count - 1 : selectedIndex - 1;
        else if (kb.Matches(keyData, "tui.select.down"))
            selectedIndex = selectedIndex == filteredNodes.Count - 1 ? 0 : selectedIndex + 1;
        else if (kb.Matches(keyData, "app.tree.foldOrUp"))
        {
            var currentId = Selected()?.Id;
            if (!string.IsNullOrEmpty(currentId) && IsFoldable(currentId) && !foldedNodes.Contains(currentId))
            {
                foldedNodes.Add(currentId);
                ApplyFilter();
            }
            else selectedIndex = FindBranchSegmentStart("up");
        }
        else if (kb.Matches(keyData, "app.tree.unfoldOrDown"))
        {
            var currentId = Selected()?.Id;
            if (!string.IsNullOrEmpty(currentId) && foldedNodes.Contains(currentId))
            {
                foldedNodes.Remove(currentId);
                ApplyFilter();
            }
            else selectedIndex = FindBranchSegmentStart("down");
        }
        else if (kb.Matches(keyData, "tui.editor.cursorLeft") || kb.Matches(keyData, "tui.select.pageUp"))
            selectedIndex = Math.Max(0, selectedIndex - maxVisibleLines); // Page up
        else if (kb.Matches(keyData, "tui.editor.cursorRight") || kb.Matches(keyData, "tui.select.pageDown"))
            selectedIndex = Math.Min(filteredNodes.Count - 1, selectedIndex + maxVisibleLines); // Page down
        else if (kb.Matches(keyData, "tui.select.confirm"))
        {
            if (Selected() is { } selected && OnSelect is not null) OnSelect(selected.Id);
        }
        else if (kb.Matches(keyData, "app.message.copy")) CopySelected();
        else if (kb.Matches(keyData, "tui.select.cancel"))
        {
            if (searchQuery.Length > 0)
            {
                searchQuery = "";
                foldedNodes.Clear();
                ApplyFilter();
            }
            else OnCancel?.Invoke();
        }
        else if (kb.Matches(keyData, "app.tree.filter.default"))
        {
            // Direct filter: default
            filterMode = FilterMode.Default;
            foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, "app.tree.filter.noTools")) ToggleFilter(FilterMode.NoTools); // Toggle filter: no-tools ↔ default
        else if (kb.Matches(keyData, "app.tree.filter.userOnly")) ToggleFilter(FilterMode.UserOnly); // Toggle filter: user-only ↔ default
        else if (kb.Matches(keyData, "app.tree.filter.labeledOnly")) ToggleFilter(FilterMode.LabeledOnly); // Toggle filter: labeled-only ↔ default
        else if (kb.Matches(keyData, "app.tree.filter.all")) ToggleFilter(FilterMode.All); // Toggle filter: all ↔ default
        else if (kb.Matches(keyData, "app.tree.filter.cycleBackward"))
        {
            // Cycle filter backwards
            var modes = Enum.GetValues<FilterMode>();
            var currentIndex = Array.IndexOf(modes, filterMode);
            filterMode = modes[(currentIndex - 1 + modes.Length) % modes.Length];
            foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, "app.tree.filter.cycleForward"))
        {
            // Cycle filter forwards: default → no-tools → user-only → labeled-only → all → default
            var modes = Enum.GetValues<FilterMode>();
            var currentIndex = Array.IndexOf(modes, filterMode);
            filterMode = modes[(currentIndex + 1) % modes.Length];
            foldedNodes.Clear();
            ApplyFilter();
        }
        else if (kb.Matches(keyData, "tui.editor.deleteCharBackward"))
        {
            if (searchQuery.Length > 0)
            {
                searchQuery = searchQuery[..^1];
                foldedNodes.Clear();
                ApplyFilter();
            }
        }
        else if (kb.Matches(keyData, "app.tree.editLabel"))
        {
            if (Selected() is { } selected && OnLabelEdit is not null) OnLabelEdit(selected.Id, selected.Node.Label);
        }
        else if (kb.Matches(keyData, "app.tree.toggleLabelTimestamp")) showLabelTimestamps = !showLabelTimestamps;
        else
        {
            var hasControlChars = keyData.Any(ch => ch < 32 || ch == 0x7f || ch is >= '\x80' and <= '\x9f');
            if (!hasControlChars && keyData.Length > 0)
            {
                searchQuery += keyData;
                foldedNodes.Clear();
                ApplyFilter();
            }
        }
    }

    private void ToggleFilter(FilterMode mode)
    {
        filterMode = filterMode == mode ? FilterMode.Default : mode;
        foldedNodes.Clear();
        ApplyFilter();
    }

    /// <summary>Whether a node can be folded. A node is foldable if it has visible children and is either a root (no visible parent) or a
    /// segment start (visible parent has multiple visible children).</summary>
    private bool IsFoldable(string entryId)
    {
        var children = visibleChildrenMap.GetValueOrDefault(entryId);
        if (children is null || children.Count == 0) return false;
        if (!visibleParentMap.TryGetValue(entryId, out var parentId) || parentId is null) return true;
        var siblings = VisibleChildren(parentId);
        return siblings is not null && siblings.Count > 1;
    }

    /// <summary>Find the index of the next branch segment start in the given direction. A segment start is the first child of a branch
    /// point. "up" walks the visible parent chain; "down" walks visible children (always following the first child).</summary>
    private int FindBranchSegmentStart(string direction)
    {
        var selectedId = Selected()?.Id;
        if (string.IsNullOrEmpty(selectedId)) return selectedIndex;

        var indexByEntryId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < filteredNodes.Count; i++) indexByEntryId[filteredNodes[i].Id] = i;
        var currentId = selectedId;
        if (direction == "down")
        {
            while (true)
            {
                var children = visibleChildrenMap.GetValueOrDefault(currentId) ?? [];
                if (children.Count == 0) return indexByEntryId[currentId];
                if (children.Count > 1) return indexByEntryId[children[0]];
                currentId = children[0];
            }
        }

        // direction === "up"
        while (true)
        {
            var parentId = visibleParentMap.GetValueOrDefault(currentId);
            if (parentId is null) return indexByEntryId[currentId];
            var children = visibleChildrenMap.GetValueOrDefault(parentId) ?? [];
            if (children.Count > 1)
            {
                var segmentStart = indexByEntryId[currentId];
                if (segmentStart < selectedIndex) return segmentStart;
            }
            currentId = parentId;
        }
    }
}

/// <summary>Component that renders a session tree selector for navigation.</summary>
internal sealed class TreeSelectorComponent : Container, IFocusable, IInputHandler
{
    /// <summary>Source DynamicBorder (dynamic-border.ts), kept local to this file.</summary>
    private sealed class DynamicBorder(Func<string, string>? color = null) : IComponent
    {
        private readonly Func<string, string> color = color ?? (s => theme.Fg("border", s));
        public void Invalidate() { }
        public List<string> Render(int width) => [color(TextUtils.Repeat("─", Math.Max(1, width)))];
    }

    /// <summary>Component that displays the current search query.</summary>
    private sealed class SearchLine(TreeList treeList) : IComponent
    {
        public void Invalidate() { }

        public List<string> Render(int width)
        {
            var query = treeList.GetSearchQuery();
            if (query.Length > 0) return [TextUtils.TruncateToWidth($"  {theme.Fg("muted", "Type to search:")} {theme.Fg("accent", query)}", width)];
            return [TextUtils.TruncateToWidth($"  {theme.Fg("muted", "Type to search:")}", width)];
        }
    }

    private readonly record struct TreeHelpItem(string[] Keys, string Label, bool LabelFirst = false);

    private static readonly TreeHelpItem[] TreeHelpItems =
    [
        new(["tui.select.up", "tui.select.down"], "move"),
        new(["tui.editor.cursorLeft", "tui.editor.cursorRight"], "page"),
        new(["app.tree.foldOrUp", "app.tree.unfoldOrDown"], "branch"),
        new(["app.message.copy"], "copy"),
        new(["app.tree.editLabel"], "label"),
        new(["app.tree.toggleLabelTimestamp"], "label time"),
        new(["app.tree.filter.default", "app.tree.filter.noTools", "app.tree.filter.userOnly", "app.tree.filter.labeledOnly", "app.tree.filter.all"], "filters", true),
        new(["app.tree.filter.cycleForward", "app.tree.filter.cycleBackward"], "cycle", true),
    ];

    /// <summary>Component that renders tree help as semantic rows with chunk-aware wrapping.</summary>
    private sealed class TreeHelp : IComponent
    {
        public void Invalidate() { }

        public List<string> Render(int width)
        {
            var items = TreeHelpItems.Select(item =>
            {
                var text = FormatHelpKeys(item.Keys);
                if (text.Length == 0) return item.Label;
                return item.LabelFirst ? $"{item.Label} {text}" : $"{text} {item.Label}";
            });

            var availableWidth = Math.Max(1, width);
            const string indent = "  ";
            const string separator = " · ";
            var lines = new List<string>();
            var currentLine = "";

            foreach (var item in items)
            {
                var candidate = currentLine.Length > 0
                    ? currentLine + separator + item
                    : TextUtils.VisibleWidth(indent + item) <= availableWidth ? indent + item : item;
                if (currentLine.Length == 0 || TextUtils.VisibleWidth(candidate) <= availableWidth)
                {
                    currentLine = candidate;
                    continue;
                }

                lines.AddRange(TextUtils.WrapTextWithAnsi(TextUtils.JsTrimEnd(currentLine), availableWidth));
                currentLine = TextUtils.VisibleWidth(indent + item) <= availableWidth ? indent + item : item;
            }

            if (currentLine.Length > 0) lines.AddRange(TextUtils.WrapTextWithAnsi(TextUtils.JsTrimEnd(currentLine), availableWidth));

            return [.. lines.Select(line => theme.Fg("muted", line))];
        }
    }

    internal static string FormatHelpKeys(IEnumerable<string> keybindings)
    {
        var keys = new List<string>();
        foreach (var keybinding in keybindings)
        {
            var bound = TuiKeybindingsManager.Global.GetKeys(keybinding);
            if (bound.Count > 0) keys.Add(bound[0]);
        }
        if (keys.Count == 0) return "";

        var text = KeybindingHints.FormatKeyText(CompactRawKeys(keys));
        text = Regex.Replace(text, @"\bpageUp\b", "pgup", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"\bpageDown\b", "pgdn", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"\bup\b", "↑", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"\bdown\b", "↓", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"\bleft\b", "←", RegexOptions.ECMAScript);
        text = Regex.Replace(text, @"\bright\b", "→", RegexOptions.ECMAScript);
        return text;
    }

    private static string CompactRawKeys(List<string> keys)
    {
        if (keys.Count == 1) return keys[0];

        var parts = keys.Select(key =>
        {
            var separatorIndex = key.LastIndexOf('+');
            return separatorIndex == -1 ? (Prefix: "", Suffix: key) : (Prefix: key[..(separatorIndex + 1)], Suffix: key[(separatorIndex + 1)..]);
        }).ToList();
        var prefix = parts[0].Prefix;
        return prefix.Length > 0 && parts.All(part => part.Prefix == prefix)
            ? prefix + string.Join("/", parts.Select(part => part.Suffix))
            : string.Join("/", keys);
    }

    /// <summary>Label input component shown when editing a label.</summary>
    private sealed class LabelInput : IComponent, IFocusable, IInputHandler
    {
        private readonly Input input;
        private readonly string entryId;
        public Action<string, string?>? OnSubmit { get; set; }
        public Action? OnCancel { get; set; }

        // Focusable implementation - propagate to input for IME cursor positioning
        private bool focused;
        public bool Focused
        {
            get => focused;
            set { focused = value; input.Focused = value; }
        }

        public LabelInput(string entryId, string? currentLabel)
        {
            this.entryId = entryId;
            input = new Input();
            if (!string.IsNullOrEmpty(currentLabel)) input.SetValue(currentLabel);
        }

        public void Invalidate() { }

        public List<string> Render(int width)
        {
            var lines = new List<string>();
            const string indent = "  ";
            var availableWidth = width - indent.Length;
            lines.Add(TextUtils.TruncateToWidth($"{indent}{theme.Fg("muted", "Label (empty to remove):")}", width));
            lines.AddRange(input.Render(availableWidth).Select(line => TextUtils.TruncateToWidth(indent + line, width)));
            lines.Add(TextUtils.TruncateToWidth($"{indent}{KeybindingHints.KeyHint("tui.select.confirm", "save")}  {KeybindingHints.KeyHint("tui.select.cancel", "cancel")}", width));
            return lines;
        }

        public void HandleInput(string keyData)
        {
            var kb = TuiKeybindingsManager.Global;
            if (kb.Matches(keyData, "tui.select.confirm"))
            {
                var value = TextUtils.JsTrim(input.GetValue());
                OnSubmit?.Invoke(entryId, value.Length > 0 ? value : null);
            }
            else if (kb.Matches(keyData, "tui.select.cancel")) OnCancel?.Invoke();
            else input.HandleInput(keyData);
        }
    }

    private readonly TreeList treeList;
    private LabelInput? labelInput;
    private readonly Container labelInputContainer;
    private readonly Container treeContainer;
    private readonly Action<string, string?>? onLabelChangeCallback;
    public Action<string?>? OnCopy { get; set; }

    // Focusable implementation - propagate to labelInput when active for IME cursor positioning
    private bool focused;
    public bool Focused
    {
        get => focused;
        set
        {
            focused = value;
            // Propagate to labelInput when it's active
            if (labelInput is not null) labelInput.Focused = value;
        }
    }

    public TreeSelectorComponent(IReadOnlyList<SessionTreeNode> tree, string? currentLeafId, int terminalHeight, Action<string> onSelect, Action onCancel,
        Action<string, string?>? onLabelChange = null, string? initialSelectedId = null, FilterMode? initialFilterMode = null,
        Func<Action, double, IDisposable>? setTimeout = null)
    {
        onLabelChangeCallback = onLabelChange;
        var maxVisibleLines = Math.Max(5, terminalHeight / 2);

        treeList = new TreeList(tree, currentLeafId, maxVisibleLines, initialSelectedId, initialFilterMode)
        {
            OnSelect = onSelect,
            OnCancel = onCancel,
        };
        treeList.OnCopy = text => OnCopy?.Invoke(text);
        treeList.OnLabelEdit = ShowLabelInput;

        treeContainer = new Container();
        treeContainer.AddChild(treeList);

        labelInputContainer = new Container();

        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new Text(theme.Bold("  Session Tree"), 1, 0));
        AddChild(new TreeHelp());
        AddChild(new SearchLine(treeList));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(treeContainer);
        AddChild(labelInputContainer);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());

        if (tree.Count == 0) (setTimeout ?? SelectorTimers.SetTimeout)(onCancel, 100);
    }

    private void ShowLabelInput(string entryId, string? currentLabel)
    {
        labelInput = new LabelInput(entryId, currentLabel);
        labelInput.OnSubmit = (id, label) =>
        {
            treeList.UpdateNodeLabel(id, label);
            onLabelChangeCallback?.Invoke(id, label);
            HideLabelInput();
        };
        labelInput.OnCancel = HideLabelInput;

        // Propagate current focused state to the new labelInput
        labelInput.Focused = focused;

        treeContainer.Clear();
        labelInputContainer.Clear();
        labelInputContainer.AddChild(labelInput);
    }

    private void HideLabelInput()
    {
        labelInput = null;
        labelInputContainer.Clear();
        treeContainer.Clear();
        treeContainer.AddChild(treeList);
    }

    public void HandleInput(string keyData)
    {
        if (labelInput is not null) labelInput.HandleInput(keyData);
        else treeList.HandleInput(keyData);
    }

    public TreeList GetTreeList() => treeList;
}
