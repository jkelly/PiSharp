// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/layout-node.ts, packages/tui/src/layout.ts,
// packages/tui/src/components/stack.ts, v-stack.ts, h-stack.ts, scroll-view.ts, mouse-region.ts, alt-screen-flash.ts,
// packages/tui/src/wheel-scroll.ts.
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

public readonly record struct LayoutViewport(int Width, int Height);
public enum StackAlign { Stretch, Start, Center, End }
/// <summary>A stack child and its flex options. <see cref="Basis"/> null means "auto".</summary>
public sealed record StackEntry(IComponent Component, int? Basis = null, int? Grow = null, int? Shrink = null, int? MinSize = null, int? MaxSize = null,
    Func<LayoutViewport, bool>? Visible = null);

public abstract record LayoutNode;
public sealed record StackLayoutNode(bool Vertical, IReadOnlyList<StackEntry> Entries, int Gap, StackAlign Align) : LayoutNode;
public sealed record ScrollLayoutNode(IComponent Component, ScrollView State) : LayoutNode;
/// <summary>A component the fullscreen layout engine lays out itself (stacks and scroll views).</summary>
public interface ILayoutComponent : IComponent { LayoutNode GetLayoutNode(); }

public sealed record LayoutRect(int X, int Y, int Width, int Height);
public sealed class LayoutBox
{
    public required IComponent Component;
    public required LayoutRect Rect;
    public required LayoutRect Clip;
    public List<LayoutBox> Children = [];
    public LayoutBox? Parent;
    public List<string>? Lines;
    public int LineOffset;
    public ScrollView? ScrollView;
    public List<string>? ScrollContentLines;
    public int Layer;
}
public sealed record LayoutFrame(LayoutBox Root, int Width, int Height, List<string> Lines, ScrollView? PrimaryScrollView);
public sealed record ScrollbarGeometry(int Column, int TrackTop, int TrackHeight, int ThumbTop, int ThumbHeight, int MaxScrollTop);

public static partial class LayoutEngine
{
    [GeneratedRegex(@"^(?:\x1b\]133;[ABC](?:\x07|\x1b\\))+")] internal static partial Regex Osc133ZonePrefix();

    private sealed class Context(LayoutViewport viewport, Action requestRender)
    {
        public LayoutViewport Viewport = viewport;
        public Dictionary<IComponent, Dictionary<int, List<string>>> Cache = new(ReferenceEqualityComparer.Instance);
        public Action RequestRender = requestRender;
        public ScrollView? Primary;
    }

    private static LayoutRect Intersect(LayoutRect a, LayoutRect b)
    {
        var x = Math.Max(a.X, b.X); var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width); var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static List<string> RenderCached(Context context, IComponent component, int width)
    {
        var safe = Math.Max(1, width);
        if (!context.Cache.TryGetValue(component, out var widths)) context.Cache[component] = widths = [];
        if (!widths.TryGetValue(safe, out var lines)) widths[safe] = lines = component.Render(safe);
        return lines;
    }

    private static void Translate(LayoutBox box, int deltaY)
    {
        box.Rect = box.Rect with { Y = box.Rect.Y + deltaY };
        foreach (var child in box.Children) Translate(child, deltaY);
    }
    private static void UpdateClips(LayoutBox box, LayoutRect parentClip)
    {
        box.Clip = Intersect(parentClip, box.Rect);
        foreach (var child in box.Children) UpdateClips(child, box.Clip);
    }

    private static LayoutBox LayoutComponent(Context context, IComponent component, int x, int y, int width, int? height, LayoutRect clip)
    {
        var safeWidth = Math.Max(1, width);
        var node = component is ILayoutComponent layout ? layout.GetLayoutNode() : null;
        if (node is null)
        {
            var lines = RenderCached(context, component, safeWidth);
            var allocated = height is { } h ? Math.Max(0, h) : lines.Count;
            var offset = 0;
            if (lines.Count > allocated && allocated > 0)
            {
                var cursorLine = lines.FindIndex(line => line.Contains(TuiBase.CursorMarker, StringComparison.Ordinal));
                if (cursorLine >= allocated) offset = cursorLine - allocated + 1;
            }
            var rect = new LayoutRect(x, y, safeWidth, allocated);
            return new LayoutBox { Component = component, Rect = rect, Clip = Intersect(clip, rect), Lines = lines, LineOffset = offset };
        }
        if (node is ScrollLayoutNode scroll)
        {
            var state = scroll.State;
            var previousTop = state.ScrollTop;
            var contentWidth = state.GetContentWidth(safeWidth);
            var childBox = LayoutComponent(context, scroll.Component, x, y - previousTop, contentWidth, null, clip);
            var contentHeight = childBox.Rect.Height;
            var viewportHeight = height is { } h ? Math.Max(0, h) : contentHeight;
            state.UpdateLayout(contentHeight, viewportHeight, context.RequestRender);
            Translate(childBox, previousTop - state.ScrollTop);
            if (state.Primary || context.Primary is null) context.Primary = state;
            var rect = new LayoutRect(x, y, safeWidth, viewportHeight);
            var childClip = Intersect(clip, rect);
            var box = new LayoutBox { Component = component, Rect = rect, Clip = childClip, Children = [childBox], ScrollView = state,
                ScrollContentLines = RenderCached(context, scroll.Component, contentWidth) };
            childBox.Parent = box;
            UpdateClips(childBox, childClip);
            return box;
        }
        var stack = (StackLayoutNode)node;
        var entries = Stacks.VisibleEntries(stack.Entries, context.Viewport);
        var gapTotal = Math.Max(0, entries.Count - 1) * stack.Gap;
        if (stack.Vertical)
        {
            var intrinsic = entries.Select(entry => entry.Basis ?? RenderCached(context, entry.Component, safeWidth).Count).ToList();
            var sizes = Stacks.AllocateSizes(entries, intrinsic, height, stack.Gap);
            var natural = sizes.Sum() + gapTotal;
            var rect = new LayoutRect(x, y, safeWidth, height is { } h ? Math.Max(0, h) : natural);
            var box = new LayoutBox { Component = component, Rect = rect, Clip = Intersect(clip, rect) };
            var childY = y;
            for (var index = 0; index < entries.Count; index++)
            {
                var child = LayoutComponent(context, entries[index].Component, x, childY, safeWidth, sizes[index], box.Clip);
                child.Parent = box; box.Children.Add(child);
                childY += sizes[index] + stack.Gap;
            }
            return box;
        }
        var intrinsicWidths = entries.Select(entry => entry.Basis ?? RenderCached(context, entry.Component, safeWidth).Select(TextUtils.VisibleWidth).DefaultIfEmpty(0).Max()).ToList();
        var widths = Stacks.AllocateSizes(entries, intrinsicWidths, safeWidth, stack.Gap);
        var heights = entries.Select((entry, index) => RenderCached(context, entry.Component, Math.Max(1, widths[index])).Count).ToList();
        var allocatedHeight = height is { } ah ? Math.Max(0, ah) : heights.DefaultIfEmpty(0).Max();
        var hrect = new LayoutRect(x, y, safeWidth, allocatedHeight);
        var hbox = new LayoutBox { Component = component, Rect = hrect, Clip = Intersect(clip, hrect) };
        var childX = x;
        for (var index = 0; index < entries.Count; index++)
        {
            var childHeight = stack.Align == StackAlign.Stretch ? allocatedHeight : Math.Min(allocatedHeight, heights[index]);
            var childY = y;
            if (stack.Align == StackAlign.Center) childY += (allocatedHeight - childHeight) / 2;
            else if (stack.Align == StackAlign.End) childY += allocatedHeight - childHeight;
            var childWidth = widths[index];
            if (childWidth == 0)
                hbox.Children.Add(new LayoutBox { Component = entries[index].Component, Rect = new(childX, childY, 0, childHeight), Clip = new(childX, childY, 0, 0), Parent = hbox });
            else
            {
                var child = LayoutComponent(context, entries[index].Component, childX, childY, childWidth, childHeight, hbox.Clip);
                child.Parent = hbox; hbox.Children.Add(child);
            }
            childX += childWidth + stack.Gap;
        }
        return hbox;
    }

    private static string ReplaceScrollbarCell(string line, int column, int totalWidth, string replacement, bool preserveBackground)
    {
        if (TerminalImage.IsImageLine(line)) return line;
        var range = TextUtils.GetGraphemeCellRange(line, column);
        var start = range?.Start ?? column; var end = range?.End ?? column + 1;
        var before = TextUtils.SliceByColumn(line, 0, start, true);
        var target = TextUtils.SliceByColumn(line, start, end - start, true);
        var after = TextUtils.SliceByColumn(line, end, Math.Max(0, totalWidth - end), true);
        var prefix = ""; var index = 0;
        while (index < target.Length) { var code = TextUtils.ExtractAnsiCode(target, index); if (code is null) break; prefix += code; index += code.Length; }
        var beforePadding = new string(' ', Math.Max(0, start - TextUtils.VisibleWidth(before)));
        var cellBefore = new string(' ', Math.Max(0, column - start)); var cellAfter = new string(' ', Math.Max(0, end - column - 1));
        var style = "\u001b[0m\u001b]8;;\u0007" + (preserveBackground ? TextUtils.GetActiveBackgroundAnsi(prefix) : "");
        return before + beforePadding + style + cellBefore + replacement + cellAfter + after;
    }

    public static ScrollbarGeometry? GetScrollbarGeometry(LayoutBox box, bool includeHiddenAuto = false)
    {
        if (box.ScrollView is not { } view || box.Rect.Width <= 0 || box.Rect.Height <= 0) return null;
        var contentHeight = box.Children.Count > 0 ? box.Children[0].Rect.Height : box.ScrollContentLines?.Count ?? 0;
        var track = box.Rect.Height;
        var reveal = includeHiddenAuto && view.Scrollbar == ScrollViewScrollbar.Auto && contentHeight > track;
        if (!view.IsScrollbarVisible && !reveal) return null;
        var minThumb = Math.Min(2, track);
        var thumb = Math.Max(minThumb, Math.Min(track, (int)Math.Round((double)track * track / Math.Max(1, contentHeight), MidpointRounding.AwayFromZero)));
        var maxScrollTop = Math.Max(0, contentHeight - track);
        var maxThumbTop = track - thumb;
        var thumbOffset = maxScrollTop == 0 ? 0 : (int)Math.Round((double)view.ScrollTop / maxScrollTop * maxThumbTop, MidpointRounding.AwayFromZero);
        var column = box.Rect.X + box.Rect.Width - 1;
        if (column < box.Clip.X || column >= box.Clip.X + box.Clip.Width) return null;
        return new(column, box.Rect.Y, track, box.Rect.Y + thumbOffset, thumb, maxScrollTop);
    }

    private static void PaintScrollbar(LayoutBox box, List<string> screen, int totalWidth)
    {
        if (GetScrollbarGeometry(box) is not { } geometry || box.ScrollView is not { } view) return;
        for (var offset = 0; offset < geometry.TrackHeight; offset++)
        {
            var row = geometry.TrackTop + offset;
            if (row < box.Clip.Y || row >= box.Clip.Y + box.Clip.Height || row < 0 || row >= screen.Count) continue;
            var isThumb = row >= geometry.ThumbTop && row < geometry.ThumbTop + geometry.ThumbHeight;
            var replacement = isThumb ? view.ScrollbarThumbStyle(view.IsScrollbarActive ? "█" : "┃") : view.ScrollbarTrackStyle("│");
            screen[row] = ReplaceScrollbarCell(screen[row], geometry.Column, totalWidth, replacement, view.Scrollbar != ScrollViewScrollbar.Always);
        }
    }

    private static void PaintBox(LayoutBox box, List<string> screen, int totalWidth)
    {
        if (box.Lines is { } lines)
        {
            var first = Math.Max(Math.Max(box.Rect.Y, box.Clip.Y), 0);
            var last = Math.Min(Math.Min(box.Rect.Y + box.Rect.Height, box.Clip.Y + box.Clip.Height), screen.Count);
            for (var row = first; row < last; row++)
            {
                var index = box.LineOffset + row - box.Rect.Y;
                if (index < 0 || index >= lines.Count) continue;
                var line = Osc133ZonePrefix().Replace(lines[index], "");
                if (TerminalImage.GetKittyImageMetadata(line) is { } metadata)
                {
                    var clipBottom = Math.Min(screen.Count, box.Clip.Y + box.Clip.Height);
                    var visibleRows = Math.Min(metadata.Rows, clipBottom - row);
                    if (visibleRows < metadata.Rows) line = TerminalImage.CropKittyImageLine(line, 0, visibleRows);
                }
                if (box.Rect.X == 0 && box.Rect.Width >= totalWidth && (TerminalImage.IsImageLine(line) || screen[row].Length == 0)) screen[row] = line;
                else screen[row] = TuiBase.CompositeLine(screen[row], line, box.Rect.X, box.Rect.Width, totalWidth);
            }
        }
        foreach (var child in box.Children) PaintBox(child, screen, totalWidth);
        if (box.ScrollView is { } view && box.ScrollContentLines is { } content && view.ScrollTop > 0 && box.Rect.Height > 0)
            for (var imageRow = view.ScrollTop - 1; imageRow >= 0; imageRow--)
            {
                var imageLine = imageRow < content.Count ? content[imageRow] : "";
                if (TerminalImage.GetKittyImageMetadata(imageLine) is { } metadata)
                {
                    var hidden = view.ScrollTop - imageRow;
                    if (hidden < metadata.Rows)
                    {
                        var cropped = TerminalImage.CropKittyImageLine(imageLine, hidden, Math.Min(box.Rect.Height, metadata.Rows - hidden));
                        if (box.Rect.X == 0 && box.Rect.Width >= totalWidth && box.Rect.Y >= 0 && box.Rect.Y < screen.Count) screen[box.Rect.Y] = cropped;
                    }
                    break;
                }
                if (imageLine.Length != 0) break;
            }
        PaintScrollbar(box, screen, totalWidth);
    }

    public static LayoutFrame RenderFrame(IComponent root, int width, int height, Action requestRender)
    {
        var safeWidth = Math.Max(1, width); var safeHeight = Math.Max(1, height);
        var context = new Context(new(safeWidth, safeHeight), requestRender);
        var rootBox = LayoutComponent(context, root, 0, 0, safeWidth, safeHeight, new(0, 0, safeWidth, safeHeight));
        var lines = Enumerable.Repeat("", safeHeight).ToList();
        PaintBox(rootBox, lines, safeWidth);
        return new(rootBox, safeWidth, safeHeight, lines, context.Primary);
    }

    private static bool ContainsPoint(LayoutRect rect, int x, int y) => x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;

    /// <summary>The visual hit path from the deepest component to the root.</summary>
    public static List<LayoutBox> GetBoxesAt(LayoutFrame frame, int x, int y)
    {
        var result = new List<(LayoutBox Box, int Depth)>();
        void Visit(LayoutBox box, int depth)
        {
            if (!ContainsPoint(box.Clip, x, y)) return;
            result.Add((box, depth));
            foreach (var child in box.Children) Visit(child, depth + 1);
        }
        Visit(frame.Root, 0);
        return result.OrderByDescending(entry => entry.Box.Layer).ThenByDescending(entry => entry.Depth).Select(entry => entry.Box).ToList();
    }

    public static LayoutBox? GetScrollViewBox(LayoutFrame frame, ScrollView view)
    {
        LayoutBox? Visit(LayoutBox box)
        {
            if (box.ScrollView == view) return box;
            foreach (var child in box.Children) if (Visit(child) is { } match) return match;
            return null;
        }
        return Visit(frame.Root);
    }

    public static List<ScrollView> GetScrollViewsAt(LayoutFrame frame, int x, int y)
    {
        var result = new List<(ScrollView View, int Depth)>();
        void Visit(LayoutBox box, int depth)
        {
            if (!ContainsPoint(box.Clip, x, y)) return;
            if (box.ScrollView is { } view && ContainsPoint(box.Rect, x, y)) result.Add((view, depth));
            foreach (var child in box.Children) Visit(child, depth + 1);
        }
        Visit(frame.Root, 0);
        return result.OrderByDescending(entry => entry.Depth).Select(entry => entry.View).ToList();
    }
}

public static class Stacks
{
    internal static List<StackEntry> VisibleEntries(IReadOnlyList<StackEntry> entries, LayoutViewport viewport) =>
        entries.Where(entry => entry.Visible?.Invoke(viewport) ?? true).ToList();

    private static int Clamp(int size, StackEntry entry)
    {
        var min = Math.Max(0, entry.MinSize ?? 0);
        var max = Math.Max(min, entry.MaxSize ?? int.MaxValue);
        return Math.Max(min, Math.Min(max, Math.Max(0, size)));
    }

    private static void Distribute(int[] sizes, IReadOnlyList<StackEntry> entries, int amount, bool grow)
    {
        var remaining = amount;
        while (remaining > 0)
        {
            var candidates = entries.Select((entry, index) => (entry, index)).Where(c => grow
                ? (c.entry.Grow ?? 0) > 0 && sizes[c.index] < (c.entry.MaxSize ?? int.MaxValue)
                : (c.entry.Shrink ?? 1) > 0 && sizes[c.index] > (c.entry.MinSize ?? 0)).ToList();
            if (candidates.Count == 0) return;
            double Weight((StackEntry entry, int index) c) => grow ? c.entry.Grow ?? 0 : (double)(c.entry.Shrink ?? 1) * Math.Max(1, sizes[c.index]);
            var total = candidates.Sum(Weight);
            var distributed = 0;
            foreach (var candidate in candidates)
            {
                if (remaining <= 0) break;
                var proposed = Math.Max(1, (int)Math.Floor(remaining * Weight(candidate) / total));
                var capacity = grow ? (long)(candidate.entry.MaxSize ?? int.MaxValue) - sizes[candidate.index] : sizes[candidate.index] - (candidate.entry.MinSize ?? 0);
                var delta = (int)Math.Min(Math.Min(remaining, proposed), capacity);
                if (delta <= 0) continue;
                sizes[candidate.index] += grow ? delta : -delta;
                remaining -= delta; distributed += delta;
            }
            if (distributed == 0) return;
        }
    }

    public static int[] AllocateSizes(IReadOnlyList<StackEntry> entries, IReadOnlyList<int> intrinsic, int? available, int gap)
    {
        var sizes = entries.Select((entry, index) => Clamp(entry.Basis ?? (index < intrinsic.Count ? intrinsic[index] : 0), entry)).ToArray();
        if (available is not { } space) return sizes;
        var content = Math.Max(0, space - Math.Max(0, entries.Count - 1) * gap);
        var sum = sizes.Sum();
        if (sum < content) Distribute(sizes, entries, content - sum, true);
        else if (sum > content) Distribute(sizes, entries, sum - content, false);
        return sizes;
    }
}

/// <summary>A flex stack; the fullscreen layout engine lays it out, other renderers stack it naturally.</summary>
public abstract class Stack : Container, ILayoutComponent
{
    protected readonly List<StackEntry> Entries = [];
    protected readonly int Gap;
    protected readonly StackAlign Align;
    protected abstract bool Vertical { get; }
    protected Stack(IEnumerable<StackEntry> children, int gap = 0, StackAlign align = StackAlign.Stretch)
    {
        Gap = Math.Max(0, gap); Align = align;
        foreach (var child in children) AddChild(child);
    }
    public void AddChild(StackEntry entry)
    {
        base.AddChild(entry.Component);
        Entries.Add(entry with
        {
            Grow = entry.Grow is { } g ? Math.Max(0, g) : null, Shrink = entry.Shrink is { } s ? Math.Max(0, s) : null,
            MinSize = entry.MinSize is { } mn ? Math.Max(0, mn) : null, MaxSize = entry.MaxSize is { } mx ? Math.Max(0, mx) : null
        });
    }
    public override void AddChild(IComponent component) => AddChild(new StackEntry(component));
    public override void RemoveChild(IComponent component)
    {
        base.RemoveChild(component);
        var index = Entries.FindIndex(entry => entry.Component == component);
        if (index != -1) Entries.RemoveAt(index);
    }
    public override void Clear() { base.Clear(); Entries.Clear(); }
    public LayoutNode GetLayoutNode() => new StackLayoutNode(Vertical, Entries, Gap, Align);
}

public sealed class VStack(IEnumerable<StackEntry> children, int gap = 0, StackAlign align = StackAlign.Stretch) : Stack(children, gap, align)
{
    protected override bool Vertical => true;
    public override List<string> Render(int width)
    {
        var viewport = new LayoutViewport(Math.Max(1, width), int.MaxValue);
        var entries = Stacks.VisibleEntries(Entries, viewport);
        var rendered = entries.Select(entry => entry.Component.Render(viewport.Width)).ToList();
        var sizes = Stacks.AllocateSizes(entries, rendered.Select(lines => lines.Count).ToList(), null, Gap);
        var lines = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0) for (var g = 0; g < Gap; g++) lines.Add("");
            var child = rendered[index].Take(sizes[index]).ToList();
            lines.AddRange(child);
            for (var padding = child.Count; padding < sizes[index]; padding++) lines.Add("");
        }
        return lines;
    }
}

public sealed class HStack(IEnumerable<StackEntry> children, int gap = 0, StackAlign align = StackAlign.Stretch) : Stack(children, gap, align)
{
    protected override bool Vertical => false;
    public override List<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        var entries = Stacks.VisibleEntries(Entries, new(safeWidth, int.MaxValue));
        if (entries.Count == 0) return [];
        var intrinsic = entries.Select(entry => entry.Component.Render(safeWidth).Select(TextUtils.VisibleWidth).DefaultIfEmpty(0).Max()).ToList();
        var widths = Stacks.AllocateSizes(entries, intrinsic, safeWidth, Gap);
        var rendered = entries.Select((entry, index) => widths[index] == 0 ? [] : entry.Component.Render(widths[index])).ToList();
        var height = rendered.Select(lines => lines.Count).DefaultIfEmpty(0).Max();
        var result = Enumerable.Repeat("", height).ToList();
        var x = 0;
        for (var index = 0; index < rendered.Count; index++)
        {
            var lines = rendered[index]; var childWidth = widths[index];
            var offset = Align == StackAlign.Center ? (height - lines.Count) / 2 : Align == StackAlign.End ? height - lines.Count : 0;
            for (var row = 0; row < lines.Count; row++)
            {
                var target = row + offset;
                if (target < 0 || target >= result.Count) continue;
                result[target] = TuiBase.CompositeLine(result[target], lines[row], x, childWidth, safeWidth);
            }
            x += childWidth + Gap;
        }
        return result;
    }
}

public enum ScrollViewScrollbar { Hidden, Auto, Always }
public enum ScrollViewFollow { None, End }
public enum ScrollOverscroll { Chain, Contain }
public sealed record ScrollViewOptions(ScrollViewFollow Follow = ScrollViewFollow.None, bool Primary = false, ScrollOverscroll Overscroll = ScrollOverscroll.Chain,
    ScrollViewScrollbar Scrollbar = ScrollViewScrollbar.Hidden, Func<string, string>? ScrollbarTrackStyle = null, Func<string, string>? ScrollbarThumbStyle = null,
    int ScrollbarHideDelayMs = 1000, UiLoop? Loop = null);

/// <summary>A vertical scroll viewport over exactly one child.</summary>
public sealed class ScrollView : Container, ILayoutComponent
{
    private readonly IComponent child;
    public bool FollowEnd { get; }
    public bool Primary { get; }
    public ScrollOverscroll Overscroll { get; }
    public Func<string, string> ScrollbarTrackStyle { get; }
    public Func<string, string> ScrollbarThumbStyle { get; }
    private ScrollViewScrollbar scrollbar;
    private readonly int hideDelayMs;
    private readonly UiLoop? loop;
    private int scrollTop, contentHeight, viewportHeight;
    private bool followingEnd, followSuppressedAtEnd, transientVisible, scrollbarActive;
    private Action? requestRender;
    private IDisposable? hideTimer;

    public ScrollView(IComponent component, ScrollViewOptions? options = null)
    {
        options ??= new();
        child = component; Children.Add(component);
        FollowEnd = options.Follow == ScrollViewFollow.End; followingEnd = FollowEnd;
        Primary = options.Primary; Overscroll = options.Overscroll; scrollbar = options.Scrollbar;
        ScrollbarTrackStyle = options.ScrollbarTrackStyle ?? (text => "\u001b[90m" + text + "\u001b[39m");
        ScrollbarThumbStyle = options.ScrollbarThumbStyle ?? (text => "\u001b[37m" + text + "\u001b[39m");
        hideDelayMs = Math.Max(0, options.ScrollbarHideDelayMs); loop = options.Loop;
    }

    public int ScrollTop => scrollTop;
    public bool IsFollowingEnd => followingEnd;
    public int ViewportHeight => viewportHeight;
    public ScrollViewScrollbar Scrollbar => scrollbar;
    public bool IsScrollbarVisible => scrollbar == ScrollViewScrollbar.Always ? viewportHeight > 0 : scrollbar == ScrollViewScrollbar.Auto && contentHeight > viewportHeight && transientVisible;
    public bool IsScrollbarActive => scrollbarActive;

    public void SetScrollbar(ScrollViewScrollbar value)
    {
        if (value == scrollbar) return;
        scrollbar = value;
        if (value != ScrollViewScrollbar.Auto) HideTransient(); else if (scrollbarActive) MarkActivity();
        requestRender?.Invoke();
    }
    public int GetContentWidth(int width) => scrollbar == ScrollViewScrollbar.Always && width > 1 ? width - 1 : width;

    private void MarkActivity()
    {
        if (scrollbar != ScrollViewScrollbar.Auto || contentHeight <= viewportHeight) return;
        transientVisible = true;
        hideTimer?.Dispose(); hideTimer = null;
        if (scrollbarActive || loop is null) return;
        hideTimer = loop.SetTimeout(() => { hideTimer = null; transientVisible = false; requestRender?.Invoke(); }, hideDelayMs);
    }
    private void HideTransient() { transientVisible = false; hideTimer?.Dispose(); hideTimer = null; }
    public void SetScrollbarActive(bool active)
    {
        if (active == scrollbarActive) return;
        scrollbarActive = active; MarkActivity(); requestRender?.Invoke();
    }

    public void ScrollTo(int top, bool disableFollow = false)
    {
        var max = Math.Max(0, contentHeight - viewportHeight);
        var next = Math.Max(0, Math.Min(max, top));
        var nextSuppressed = disableFollow && next == max;
        var nextFollowing = !nextSuppressed && FollowEnd && next == max;
        if (next == scrollTop && nextFollowing == followingEnd && nextSuppressed == followSuppressedAtEnd) return;
        var moved = next != scrollTop;
        scrollTop = next; followingEnd = nextFollowing; followSuppressedAtEnd = nextSuppressed;
        if (moved) MarkActivity();
        requestRender?.Invoke();
    }

    /// <summary>Scrolls by lines and returns the part that could not be scrolled.</summary>
    public int ScrollBy(int lines)
    {
        if (lines == 0) return 0;
        var max = Math.Max(0, contentHeight - viewportHeight);
        var start = followingEnd ? max : scrollTop;
        var next = Math.Max(0, Math.Min(max, start + lines));
        var moved = next - start;
        var wasFollowing = followingEnd;
        scrollTop = next; followingEnd = FollowEnd && next == max; followSuppressedAtEnd = false;
        if (moved != 0) MarkActivity();
        if (moved != 0 || followingEnd != wasFollowing) requestRender?.Invoke();
        return lines - moved;
    }

    public void ScrollToStart()
    {
        var follow = FollowEnd && contentHeight <= viewportHeight;
        var changed = scrollTop != 0 || followingEnd != follow;
        scrollTop = 0; followingEnd = follow; followSuppressedAtEnd = false;
        if (changed) { MarkActivity(); requestRender?.Invoke(); }
    }

    public void ScrollToEnd()
    {
        var next = Math.Max(0, contentHeight - viewportHeight);
        var changed = scrollTop != next || followingEnd != FollowEnd;
        scrollTop = next; followingEnd = FollowEnd; followSuppressedAtEnd = false;
        if (changed) { MarkActivity(); requestRender?.Invoke(); }
    }

    public void UpdateLayout(int content, int viewport, Action render)
    {
        contentHeight = Math.Max(0, content); viewportHeight = Math.Max(0, viewport); requestRender = render;
        var max = Math.Max(0, contentHeight - viewportHeight);
        scrollTop = followingEnd ? max : Math.Max(0, Math.Min(scrollTop, max));
        if (scrollTop < max) followSuppressedAtEnd = false;
        if (FollowEnd && scrollTop == max && !followSuppressedAtEnd) followingEnd = true;
        if (contentHeight <= viewportHeight) HideTransient();
    }

    public override void AddChild(IComponent component) => throw new InvalidOperationException("ScrollView has exactly one child");
    public override void RemoveChild(IComponent component) => throw new InvalidOperationException("ScrollView child cannot be removed");
    public override void Clear() => throw new InvalidOperationException("ScrollView child cannot be cleared");
    public override List<string> Render(int width)
    {
        var contentWidth = GetContentWidth(width);
        var lines = child.Render(contentWidth);
        return contentWidth == width ? lines : lines.Select(line => line + " ").ToList();
    }
    public LayoutNode GetLayoutNode() => new ScrollLayoutNode(child, this);
}

/// <summary>Adds mouse handling to a component without changing its rendering.</summary>
public sealed class MouseRegion(IComponent child, Func<TuiMouseEvent, TuiMouseEventResult?> onMouse) : IComponent, IMouseHandler
{
    public List<string> Render(int width) => child.Render(width);
    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent) => Mouse.Dispatch(child, mouseEvent) ?? onMouse(mouseEvent);
    public void Invalidate() => child.Invalidate();
}

/// <summary>Transient messages composited by the alternate-screen renderer.</summary>
public sealed class AltScreenFlashContainer(UiLoop loop, Action requestRender) : IComponent
{
    private readonly List<(int Id, string Message, IDisposable Timer)> entries = [];
    private int nextId;
    public void Flash(string message, int durationMs = 1000)
    {
        var id = nextId++;
        var timer = loop.SetTimeout(() =>
        {
            var index = entries.FindIndex(entry => entry.Id == id);
            if (index == -1) return;
            entries.RemoveAt(index); requestRender();
        }, Math.Max(0, durationMs));
        entries.Add((id, message, timer));
        requestRender();
    }
    public void Dispose() { foreach (var entry in entries) entry.Timer.Dispose(); entries.Clear(); }
    public void Invalidate() { }
    public List<string> Render(int width) => entries.Select(entry => "\u001b[7m" + TextUtils.TruncateToWidth(" " + entry.Message + " ", width, "") + "\u001b[27m").ToList();
}

/// <summary>Lines moved per wheel event: a fixed count, or null for "auto" acceleration of fast spins.</summary>
public sealed class WheelScrollAccelerator
{
    private int? lines;
    private readonly bool accelerate;
    private double lastTime = double.NegativeInfinity;
    private int lastDirection;
    private double? averageGap;
    private double carry;

    public static bool TerminalAcceleratesWheel(Func<string, string?> env) => OperatingSystem.IsMacOS() &&
        env("SSH_CONNECTION") is null && env("SSH_CLIENT") is null && env("SSH_TTY") is null;

    public WheelScrollAccelerator(int? lines = null, bool? accelerate = null)
    { this.lines = lines; this.accelerate = accelerate ?? !TerminalAcceleratesWheel(Environment.GetEnvironmentVariable); }
    public void SetLines(int? value) { lines = value; lastTime = double.NegativeInfinity; lastDirection = 0; averageGap = null; carry = 0; }

    public int Next(int direction, double now)
    {
        if (lines is { } fixedLines) return Math.Max(1, fixedLines);
        if (!accelerate) return 1;
        var gap = now - lastTime;
        var same = direction == lastDirection && gap <= 200;
        lastTime = now; lastDirection = direction;
        if (!same) { averageGap = null; carry = 0; return 1; }
        if (gap < 5) return 1;
        averageGap = averageGap is { } average ? (average + gap) / 2 : gap;
        var value = Math.Min(6, Math.Max(1, 100 / averageGap.Value)) + carry;
        var whole = (int)Math.Floor(value);
        carry = value - whole;
        return whole;
    }
}
