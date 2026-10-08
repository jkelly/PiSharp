using PiSharp.Tui;
using PiSharp.Tui.Components.SelectList;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal sealed record SelectListCase(string Id, string Description, Func<ConsumerEvidence, Task> Run);

internal static class SelectListCases
{
    internal static ConsumerEvidence? ActiveEvidence { get; set; }
    internal static IEnumerable<SelectListCase> Cases()
    {
        yield return Case("empty-navigation-and-cancel", "Empty Source indices, no selection callback, cancellation still emitted", Empty);
        yield return Case("singleton-wrap-notification", "Same-item up/down still notify; setters remain silent", Singleton);
        yield return Case("decoded-original-events", "Actual decoder arrows and Kitty confirm release preserve original matching", Decoded);
        yield return Case("configured-remap-disable-and-replace", "Owner remaps, empty-array disable and live registry replacement", Configured);
        yield return Case("action-priority-and-ignored-input", "Up/down before confirm/cancel; page, paste, protocol and text ignored", Priority);
        yield return Case("value-prefix-filter-and-label-fallback", "Filter by value, reset without notification, empty versus whitespace labels", Filter);
        yield return Case("unicode-filter-special-casing", "Dotted-I expansion and contextual Greek final sigma", UnicodeFilter);
        yield return Case("immutable-item-copy-and-index-clamp", "Native alias boundary plus negative/large/empty index clamps", CopyAndClamp);
        yield return Case("centered-scroll-and-key-wrap", "Centered range, last-window clamp, scroll counter and wrap", Scroll);
        yield return Case("mouse-press-anchor-through-recenter", "Press anchors original item across shifted click coordinates", MouseAnchor);
        yield return Case("mouse-wheel-hover-and-boundaries", "Wheel sign/clamp, unchanged render, hover/button/row guards, Source X behavior", MouseBounds);
        yield return Case("press-filter-stale-index", "Filter retains Source press anchor and can leave selection outside new list", StaleAnchor);
        yield return Case("reentrant-selection-callback-order", "Click rereads item and wheel render reads final state after callback", ReentrantSelection);
        yield return Case("callback-fault-retains-committed-state", "Throwing callbacks retain selection and cleared click anchor", CallbackFault);
        yield return Case("description-width-threshold-and-normalization", "Width >40 and remaining >10, JS whitespace and newline normalization", Descriptions);
        yield return Case("column-bounds-hidden-items-and-callback-context", "All filtered widths, reversed bounds and double fallback callback", Columns);
        yield return Case("theme-call-order-and-unused-prefix", "SelectedPrefix unused; selected row, description, scroll and no-match transforms", Themes);
        yield return Case("canonical-ascii-unicode-and-ansi-truncation", "Empty-ellipsis reset, whole graphemes and OSC8 closure", Truncation);
        yield return Case("reentrant-render-filter-shrink", "Source skips vanished rows after a render callback changes filter", ReentrantRender);
        yield return Case("bounded-input-and-callback-rejection", "Invalid UTF-16, counts, dimensions and callback outputs fail explicitly", Bounds);
        yield return Case("ansi-split-regional-indicator-regression", "Exact SELECTLIST-ANSI-GRAPHEME-TRUNCATION-1 minimal repro and budget/unsplit controls", AnsiRegionalRegression);
        yield return Case("ansi-grapheme-boundary-and-exhaustion-controls", "ANSI-split ZWJ, marks, Hangul, variation selectors, OSC8 and tabs versus unsplit/exhausted input", AnsiGraphemeControls);
    }

    private static SelectListCase Case(string id, string description, Action<ConsumerEvidence> action) =>
        new(id, description, evidence => { action(evidence); return Task.CompletedTask; });
    internal static TerminalKeybindings Bindings(IEnumerable<KeyValuePair<string, TerminalKeybindingValue?>>? user = null) =>
        new((raw, key) => TerminalInputDecoder.MatchesRawKey(raw, key), user);
    internal static TerminalSelectList List(int count = 7, int visible = 3, TerminalKeybindings? bindings = null,
        TerminalSelectListTheme? theme = null, TerminalSelectListLayoutOptions? layout = null) =>
        new(Enumerable.Range(0, count).Select(at => new TerminalSelectListItem("item" + at, "Item " + at)), visible,
            bindings ?? Bindings(), theme, layout);
    internal static void Observe(ConsumerEvidence evidence, TerminalSelectList list, int columns = 24) =>
        evidence.Observe("actual-component-state-and-canonical-rows", new { list.SelectedIndex,
            selected = list.GetSelectedItem(), range = list.GetVisibleRange(), filtered = list.FilteredItems,
            rows = list.Render(columns), authoredExpectationsOnly = true });
    internal static void Equal<T>(T expected, T actual)
    {
        ActiveEvidence?.Observe("authored-equality-assertion", new { expected, actual });
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; actual {actual}");
    }
    internal static void True(bool value)
    {
        ActiveEvidence?.Observe("authored-boolean-assertion", new { actual = value });
        if (!value) throw new InvalidOperationException("Authored assertion failed");
    }
    internal static void Rows(string[] expected, IEnumerable<string> actual)
    {
        var observed = actual.ToArray(); ActiveEvidence?.Observe("authored-sequence-assertion", new { expected, actual = observed });
        True(expected.SequenceEqual(observed));
    }
    internal static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error)
        { ActiveEvidence?.Observe("expected-authored-exception", ConsumerException.From(error)); return error; }
        throw new InvalidOperationException("Expected " + typeof(T).FullName);
    }

    private static void Empty(ConsumerEvidence e)
    {
        var list = List(0); var selected = 0; var changed = 0; var cancelled = 0;
        list.OnSelect = _ => selected++; list.OnSelectionChange = _ => changed++; list.OnCancel = () => cancelled++;
        Equal(TerminalSelectListInputOutcome.Moved, list.HandleInput("\u001b[A")); Equal(-1, list.SelectedIndex);
        list.HandleInput("\u001b[A"); Equal(-2, list.SelectedIndex);
        list.HandleInput("\u001b[B"); Equal(-1, list.SelectedIndex);
        list.HandleInput("\u001b[B"); Equal(0, list.SelectedIndex);
        Equal(TerminalSelectListInputOutcome.Confirmed, list.HandleInput("\r")); Equal(0, selected);
        Equal(TerminalSelectListInputOutcome.Cancelled, list.HandleInput("\u001b")); Equal(1, cancelled); Equal(0, changed);
        Equal<TerminalSelectListItem?>(null, list.GetSelectedItem()); Equal(new(0, 0), list.GetVisibleRange());
        Rows(["  No matching commands"], list.Render(1));
        Equal<TerminalSelectListPointerResult?>(null, list.HandleMouse(new(TerminalSelectListPointerType.Wheel, 0, 0, WheelDelta: 1)));
        Observe(e, list, 1);
    }
    private static void Singleton(ConsumerEvidence e)
    {
        var list = List(1); var values = new List<string>(); list.OnSelectionChange = item => values.Add(item.Value);
        list.HandleInput("\u001b[A"); list.HandleInput("\u001b[B"); Equal(0, list.SelectedIndex);
        list.SetSelectedIndex(100); list.SetFilter("ITEM"); Rows(["item0", "item0"], values);
        Observe(e, list);
    }
    private static void Decoded(ConsumerEvidence e)
    {
        var list = List(); var decoder = new TerminalInputDecoder(kittyProtocolActive: true); var submitted = new List<string>();
        list.OnSelect = item => submitted.Add(item.Value);
        var down = decoder.Feed("\u001b[B").Single(); Equal(TerminalSelectListInputOutcome.Moved, list.HandleInput(down));
        var up = decoder.Feed("\u001b[A").Single(); list.HandleInput(up); Equal(0, list.SelectedIndex);
        var release = decoder.Feed("\u001b[13;1:3u").Single();
        True(release is TerminalKey { Action: TerminalKeyAction.Release });
        Equal(TerminalSelectListInputOutcome.Confirmed, list.HandleInput(release)); Rows(["item0"], submitted);
        True(TerminalInputDecoder.TryGetOriginalInput(release, out var raw, out var kitty) && raw == "\u001b[13;1:3u" && kitty);
        e.Observe("actual-decoder-events", new { down, up, release, raw, kitty, submitted }); Observe(e, list);
    }
    private static void Configured(ConsumerEvidence e)
    {
        var bindings = Bindings(new Dictionary<string, TerminalKeybindingValue?>
        { ["tui.select.up"] = "alt+j", ["tui.select.down"] = Array.Empty<string>(), ["tui.select.confirm"] = "alt+k" });
        var list = List(bindings: bindings); var decoder = new TerminalInputDecoder(); var selected = "";
        list.OnSelect = item => selected = item.Value;
        Equal(TerminalSelectListInputOutcome.Unhandled, list.HandleInput(decoder.Feed("\u001b[A").Single()));
        Equal(TerminalSelectListInputOutcome.Unhandled, list.HandleInput(decoder.Feed("\u001b[B").Single()));
        list.HandleInput(decoder.Feed("\u001bj").Single()); Equal(6, list.SelectedIndex);
        list.HandleInput(decoder.Feed("\u001bk").Single()); Equal("item6", selected);
        bindings.SetUserBindings([]); list.HandleInput(decoder.Feed("\u001b[B").Single()); Equal(0, list.SelectedIndex);
        e.Observe("actual-owner-configuration", new { resolved = bindings.GetResolvedBindings(), selected }); Observe(e, list);
    }
    private static void Priority(ConsumerEvidence e)
    {
        var bindings = Bindings(new Dictionary<string, TerminalKeybindingValue?>
        { ["tui.select.up"] = "ctrl+k", ["tui.select.down"] = "ctrl+k", ["tui.select.confirm"] = "ctrl+k", ["tui.select.cancel"] = "ctrl+k" });
        var list = List(bindings: bindings); var selected = 0; var cancelled = 0;
        list.OnSelect = _ => selected++; list.OnCancel = () => cancelled++;
        Equal(TerminalSelectListInputOutcome.Moved, list.HandleInput("\u000b")); Equal(6, list.SelectedIndex);
        Equal(0, selected); Equal(0, cancelled);
        bindings.SetUserBindings(new Dictionary<string, TerminalKeybindingValue?>
        { ["tui.select.up"] = Array.Empty<string>(), ["tui.select.down"] = "ctrl+k", ["tui.select.confirm"] = "ctrl+k" });
        list.HandleInput("\u000b"); Equal(0, list.SelectedIndex); Equal(0, selected);
        foreach (var input in new TerminalInputEvent[] { new TerminalText("x"), new TerminalPaste("\r"),
            new TerminalKey("pageUp"), new TerminalKey("pageDown"), new TerminalProtocol("\u001b[A"), new TerminalUnknownSequence("\u001b[A") })
            Equal(TerminalSelectListInputOutcome.Unhandled, list.HandleInput(input));
        Observe(e, list);
    }
    private static void Filter(ConsumerEvidence e)
    {
        var list = new TerminalSelectList([new("RunFoo", "Elsewhere"), new("RUNbar", ""), new("skip", "run label"), new("space", " ")], 5, Bindings());
        var changed = 0; list.OnSelectionChange = _ => changed++; list.SetSelectedIndex(3);
        list.SetFilter("run"); Equal(0, list.SelectedIndex); Rows(["RunFoo", "RUNbar"], list.FilteredItems.Select(item => item.Value));
        Rows(["\u2192 Elsewhere", "  RUNbar"], list.Render(24)); Equal(0, changed);
        list.SetFilter(" "); Equal(0, list.FilteredItems.Length);
        list.SetFilter("space"); Rows(["\u2192  "], list.Render(24));
        list.SetFilter(""); Equal(4, list.FilteredItems.Length); Observe(e, list);
    }
    private static void UnicodeFilter(ConsumerEvidence e)
    {
        var list = new TerminalSelectList([new("\u0130stanbul", "Dotted I"), new("\u039f\u03a3", "Final sigma"),
            new("\u039f\u03a3\u0391", "Medial sigma"), new("\u732b", "Cat")], 4, Bindings());
        list.SetFilter("i\u0307"); Rows(["\u0130stanbul"], list.FilteredItems.Select(item => item.Value));
        list.SetFilter("\u039f\u03a3"); Rows(["\u039f\u03a3"], list.FilteredItems.Select(item => item.Value));
        list.SetFilter("\u03bf\u03c3"); Rows(["\u039f\u03a3\u0391"], list.FilteredItems.Select(item => item.Value));
        list.SetFilter("\u732b"); Equal(1, list.FilteredItems.Length); Observe(e, list);
    }
    private static void CopyAndClamp(ConsumerEvidence e)
    {
        var items = new[] { new TerminalSelectListItem("same", "First"), new TerminalSelectListItem("same", "Second") };
        var list = new TerminalSelectList(items, 2, Bindings()); items[0] = new("replacement", "Mutated caller array");
        Equal("First", list.GetSelectedItem()!.Label); Equal("same", list.FilteredItems[0].Value);
        list.SetSelectedIndex(int.MaxValue); Equal(1, list.SelectedIndex); Equal("Second", list.GetSelectedItem()!.Label);
        list.SetSelectedIndex(int.MinValue); Equal(0, list.SelectedIndex);
        list.SetFilter("missing"); list.SetSelectedIndex(int.MaxValue); Equal(0, list.SelectedIndex);
        Observe(e, list);
    }
    private static void Scroll(ConsumerEvidence e)
    {
        var list = List(); list.SetSelectedIndex(4); Equal(new(3, 6), list.GetVisibleRange());
        Rows(["  Item 3", "\u2192 Item 4", "  Item 5", "  (5/7)"], list.Render(24));
        list.SetSelectedIndex(0); list.HandleInput("\u001b[A"); Equal(6, list.SelectedIndex); Equal(new(4, 7), list.GetVisibleRange());
        list.HandleInput("\u001b[B"); Equal(0, list.SelectedIndex); Equal(new(0, 3), list.GetVisibleRange()); Observe(e, list);
    }
    private static void MouseAnchor(ConsumerEvidence e)
    {
        var list = List(); var selected = new List<string>(); var changed = new List<string>();
        list.OnSelect = item => selected.Add(item.Value); list.OnSelectionChange = item => changed.Add(item.Value);
        var press = list.HandleMouse(new(TerminalSelectListPointerType.Press, 0, 2, TerminalSelectListPointerButton.Left));
        Equal(new(true, true, null), press); Equal(2, list.SelectedIndex); Equal(new(1, 4), list.GetVisibleRange());
        var click = list.HandleMouse(new(TerminalSelectListPointerType.Click, 0, 2, TerminalSelectListPointerButton.Left));
        Equal(new(true, null, null), click); Equal(2, list.SelectedIndex); Rows(["item2"], selected); Rows(["item2"], changed);
        e.Observe("actual-mouse-results", new { press, click, selected, changed }); Observe(e, list);
    }
    private static void MouseBounds(ConsumerEvidence e)
    {
        var list = List(); var changes = 0; list.OnSelectionChange = _ => changes++;
        Equal(new(true, null, false), list.HandleMouse(new(TerminalSelectListPointerType.Wheel, 0, 0, WheelDelta: -999)));
        Equal(new(true, null, true), list.HandleMouse(new(TerminalSelectListPointerType.Wheel, 0, 0, WheelDelta: int.MaxValue)));
        Equal(1, list.SelectedIndex); Equal(1, changes);
        foreach (var pointer in new TerminalSelectListPointer[] { new(TerminalSelectListPointerType.Move, 0, 2, TerminalSelectListPointerButton.Left),
            new(TerminalSelectListPointerType.Press, 0, -1, TerminalSelectListPointerButton.Left),
            new(TerminalSelectListPointerType.Click, 0, 3, TerminalSelectListPointerButton.Left),
            new(TerminalSelectListPointerType.Release, 0, 0, TerminalSelectListPointerButton.Left),
            new(TerminalSelectListPointerType.Press, 0, 0, TerminalSelectListPointerButton.Right),
            new(TerminalSelectListPointerType.Wheel, 0, 0, WheelDelta: 0) })
            Equal<TerminalSelectListPointerResult?>(null, list.HandleMouse(pointer));
        Equal(1, list.SelectedIndex);
        True(list.HandleMouse(new(TerminalSelectListPointerType.Press, int.MaxValue, 0, TerminalSelectListPointerButton.Left)) is { Focus: true });
        Equal(0, list.SelectedIndex); list.SetSelectedIndex(6);
        Equal(new(true, null, false), list.HandleMouse(new(TerminalSelectListPointerType.Wheel, 0, 0, WheelDelta: 1))); Observe(e, list);
    }
    private static void StaleAnchor(ConsumerEvidence e)
    {
        var list = List(); var selected = 0; list.OnSelect = _ => selected++;
        list.HandleMouse(new(TerminalSelectListPointerType.Press, 0, 2, TerminalSelectListPointerButton.Left));
        list.SetFilter("item0"); Equal(0, list.SelectedIndex);
        list.HandleMouse(new(TerminalSelectListPointerType.Click, 0, 0, TerminalSelectListPointerButton.Left));
        Equal(2, list.SelectedIndex); Equal<TerminalSelectListItem?>(null, list.GetSelectedItem()); Equal(0, selected);
        list.HandleInput("\u001b[A"); Equal(1, list.SelectedIndex); list.SetSelectedIndex(10); Equal(0, list.SelectedIndex); Observe(e, list);
    }
    private static void ReentrantSelection(ConsumerEvidence e)
    {
        var list = List(); var events = new List<string>();
        list.HandleMouse(new(TerminalSelectListPointerType.Press, 0, 2, TerminalSelectListPointerButton.Left)); list.SetSelectedIndex(0);
        list.OnSelectionChange = item => { events.Add("change:" + item.Value); list.SetFilter("item0"); };
        list.OnSelect = item => events.Add("select:" + item.Value);
        list.HandleMouse(new(TerminalSelectListPointerType.Click, 0, 0, TerminalSelectListPointerButton.Left));
        Rows(["change:item2", "select:item0"], events); Equal(0, list.SelectedIndex);
        list.SetFilter(""); list.OnSelectionChange = item => { events.Add("wheel:" + item.Value); list.SetSelectedIndex(0); };
        Equal(new(true, null, false), list.HandleMouse(new(TerminalSelectListPointerType.Wheel, 0, 0, WheelDelta: 1)));
        Equal("wheel:item1", events[^1]); e.Observe("actual-reentrant-callbacks", events); Observe(e, list);
    }
    private static void CallbackFault(ConsumerEvidence e)
    {
        var list = List(); list.OnSelectionChange = _ => throw new InvalidOperationException("selection callback marker");
        var error = Throws<InvalidOperationException>(() => list.HandleInput("\u001b[B")); Equal(1, list.SelectedIndex);
        list.OnSelectionChange = null; list.HandleMouse(new(TerminalSelectListPointerType.Press, 0, 2, TerminalSelectListPointerButton.Left));
        list.OnSelect = _ => throw new InvalidOperationException("submit callback marker");
        Throws<InvalidOperationException>(() => list.HandleMouse(new(TerminalSelectListPointerType.Click, 0, 0, TerminalSelectListPointerButton.Left)));
        Equal(2, list.SelectedIndex); list.OnSelect = null;
        list.HandleMouse(new(TerminalSelectListPointerType.Click, 0, 0, TerminalSelectListPointerButton.Left)); Equal(1, list.SelectedIndex);
        e.Observe("expected-callback-exception", ConsumerException.From(error)); Observe(e, list);
    }
    private static void Descriptions(ConsumerEvidence e)
    {
        var item = new TerminalSelectListItem("x", "X", "\ufeff  first\r\n\nsecond \ufeff");
        var list = new TerminalSelectList([item], 1, Bindings(), layout: new(8, 8));
        Rows(["\u2192 X"], list.Render(40)); Rows(["\u2192 X" + new string(' ', 7) + "first second"], list.Render(41));
        var normal = new TerminalSelectList([item], 1, Bindings());
        Rows(["\u2192 X"], normal.Render(46));
        Rows(["\u2192 X" + new string(' ', 31) + "first secon\u001b[0m"], normal.Render(47));
        var whitespace = new TerminalSelectList([item with { Description = "\r\n \ufeff" }], 1, Bindings()); Rows(["\u2192 X"], whitespace.Render(60));
        var nel = new TerminalSelectList([item with { Description = "\u0085text\u0085" }], 1, Bindings(), layout: new(8, 8));
        True(nel.Render(41)[0].EndsWith("\u0085text\u0085", StringComparison.Ordinal));
        Observe(e, normal, 47);
    }
    private static void Columns(ConsumerEvidence e)
    {
        var items = new[] { new TerminalSelectListItem("a", "A", "description"), new TerminalSelectListItem("b", new string('B', 20)) };
        var first = new TerminalSelectList(items, 1, Bindings(), layout: new(4, 30));
        var reversed = new TerminalSelectList(items, 1, Bindings(), layout: new(30, 4));
        Rows(first.Render(60).ToArray(), reversed.Render(60));
        Equal("\u2192 A" + new string(' ', 21) + "description", first.Render(60)[0]);
        var contexts = new List<TerminalSelectListTruncateContext>();
        var custom = new TerminalSelectList([items[0]], 1, Bindings(), layout: new(TruncatePrimary: context =>
        { contexts.Add(context); return new string('X', 100); }));
        var rows = custom.Render(46); Equal(2, contexts.Count);
        Equal(30, contexts[0].MaxWidth); Equal(32, contexts[0].ColumnWidth);
        Equal(42, contexts[1].MaxWidth); Equal(42, contexts[1].ColumnWidth);
        Equal("A", contexts[0].Text); True(contexts[0].IsSelected); Equal(items[0], contexts[0].Item);
        Equal("\u2192 " + new string('X', 42) + "\u001b[0m", rows[0]);
        contexts.Clear(); custom.Render(47); Equal(1, contexts.Count);
        e.Observe("actual-custom-layout-contexts", contexts); Observe(e, first, 60);
    }
    private static void Themes(ConsumerEvidence e)
    {
        var events = new List<string>(); var theme = new TerminalSelectListTheme
        {
            SelectedPrefix = _ => throw new InvalidOperationException("Source never invokes this transform"),
            SelectedText = text => { events.Add("selected"); return "S<" + text + ">"; },
            Description = text => { events.Add("description"); return "D<" + text + ">"; },
            ScrollInfo = text => { events.Add("scroll"); return "R<" + text + ">"; },
            NoMatch = text => { events.Add("none"); return "N<" + text + ">"; }
        };
        var list = new TerminalSelectList([new("a", "A", "one"), new("b", "B", "two")], 2, Bindings(), theme, new(8, 8));
        var rows = list.Render(60); Rows(["selected", "description"], events); True(rows[0].StartsWith("S<\u2192 A", StringComparison.Ordinal));
        Equal("  BD<" + new string(' ', 7) + "two>", rows[1]);
        var scroll = List(theme: theme); scroll.Render(24); Equal("scroll", events[^1]);
        list.SetFilter("missing"); Rows(["N<  No matching commands>"], list.Render(1)); Equal("none", events[^1]);
        e.Observe("actual-theme-callback-order", events); Observe(e, list, 1);
    }
    private static void Truncation(ConsumerEvidence e)
    {
        var ascii = new TerminalSelectList([new("a", "abcdef")], 1, Bindings()); Rows(["\u2192 abcd\u001b[0m"], ascii.Render(8));
        Rows(["\u2192 "], ascii.Render(1));
        var cjk = new TerminalSelectList([new("c", "\u732b\u72ac")], 1, Bindings()); Rows(["\u2192 \u732b\u001b[0m"], cjk.Render(6));
        var unicode = new TerminalSelectList([new("u", "e\u0301\U0001f469\u200d\U0001f4bbz")], 1, Bindings());
        Rows(["\u2192 e\u0301\U0001f469\u200d\U0001f4bb\u001b[0m"], unicode.Render(7));
        const string open = "\u001b]8;;https://local.invalid\u001b\\";
        var link = new TerminalSelectList([new("l", open + "abcdef")], 1, Bindings());
        Rows(["\u2192 " + open + "abcd\u001b]8;;\u001b\\\u001b[0m"], link.Render(8));
        var unknown = new TerminalSelectList([new("q", "\u001b[1uabcdef")], 1, Bindings());
        True(unknown.Render(8)[0].Contains("[1u", StringComparison.Ordinal));
        Observe(e, unicode, 7); e.Observe("actual-hyperlink-canonical-row", link.Render(8));
    }
    private static void ReentrantRender(ConsumerEvidence e)
    {
        TerminalSelectList? list = null;
        var theme = new TerminalSelectListTheme { SelectedText = text => { list!.SetFilter("missing"); return text; } };
        list = List(theme: theme); Rows(["\u2192 Item 0"], list.Render(24)); Equal(0, list.FilteredItems.Length);
        Observe(e, list);
    }
    private static void Bounds(ConsumerEvidence e)
    {
        Equal(TerminalRenderFailure.InvalidUnicode, Throws<TerminalRenderException>(() =>
            new TerminalSelectList([new("x", "\ud800")], 1, Bindings())).Failure);
        Throws<ArgumentOutOfRangeException>(() => List(1, 0));
        Equal(TerminalRenderFailure.ResourceLimit, Throws<TerminalRenderException>(() =>
            new TerminalSelectList([new("a", "A"), new("b", "B")], 1, Bindings(), limits: new(MaximumItems: 1))).Failure);
        var list = List(); list.SetSelectedIndex(3); Throws<TerminalRenderException>(() => list.SetFilter("\udfff")); Equal(3, list.SelectedIndex);
        Throws<ArgumentOutOfRangeException>(() => list.Render(0)); Throws<ArgumentOutOfRangeException>(() => list.Render(4097));
        var bad = new TerminalSelectList([new("a", "A")], 1, Bindings(), layout: new(TruncatePrimary: _ => "\ud800"));
        Equal(TerminalRenderFailure.InvalidUnicode, Throws<TerminalRenderException>(() => bad.Render(20)).Failure);
        var huge = new TerminalSelectList([new("a", "A")], 1, Bindings(), layout: new(TruncatePrimary: _ => new string('x', 65_537)));
        Equal(TerminalRenderFailure.ResourceLimit, Throws<TerminalRenderException>(() => huge.Render(20)).Failure);
        Observe(e, list);
    }

    private static void AnsiRegionalRegression(ConsumerEvidence e)
    {
        const string first = "\U0001f1fa"; const string second = "\U0001f1f8"; const string reset = "\u001b[0m";
        const string label = first + reset + second;
        var component = new TerminalSelectList([new("flag", label)], 1, Bindings());
        var actual = component.Render(6); // Exact reviewed public path; primary budget is two cells.
        e.Observe("exact-review-reproduction", new { finding = "SELECTLIST-ANSI-GRAPHEME-TRUNCATION-1", label,
            columns = 6, primaryWidth = 2, expectedCanonical = "\u2192 " + first + reset, actual,
            expectationProvenance = "Authored static Source derivation; no executed Source capture" });
        Rows(["\u2192 " + first + reset], actual); Equal(label, component.FilteredItems[0].Label);
        // Source span widths total four, so the larger budget retains both original indicators and ANSI.
        Rows(["\u2192 " + label], component.Render(8));
        var joined = new TerminalSelectList([new("flag", first + second)], 1, Bindings());
        Rows(["\u2192 " + first + second], joined.Render(6));
        // Below either standalone RI's width, canonical utility output consists only of its reset.
        Rows(["\u2192 " + reset], component.Render(5));
        Observe(e, component, 6);
    }

    private static void AnsiGraphemeControls(ConsumerEvidence e)
    {
        const string reset = "\u001b[0m"; const string first = "\U0001f1fa"; const string second = "\U0001f1f8";
        const string womanJoiner = "\U0001f469\u200d"; const string laptop = "\U0001f4bb";
        const string oscOpen = "\u001b]8;;https://local.invalid\u001b\\"; const string oscClose = "\u001b]8;;\u001b\\";
        (string Id, string Label, int Columns, string ExpectedPrimary)[] vectors =
        [
            ("zwj-split-at-reset", womanJoiner + reset + laptop, 6, womanJoiner + reset),
            ("zwj-without-ansi", womanJoiner + laptop, 6, womanJoiner + laptop),
            ("combining-split-with-overflow", "e" + reset + "\u0301x", 5, "e" + reset + "\u0301" + reset),
            ("combining-without-ansi", "e\u0301x", 5, "e\u0301" + reset),
            ("hangul-split-at-reset", "\u1100" + reset + "\u1161", 6, "\u1100" + reset),
            ("hangul-without-ansi", "\u1100\u1161", 6, "\u1100\u1161"),
            ("variation-split-exhausts-without-reset", "\u2764" + reset + "\ufe0f", 5, "\u2764" + reset + "\ufe0f"),
            ("variation-without-ansi-overflows", "\u2764\ufe0f", 5, reset),
            ("osc-open-pending-before-overflow", first + oscOpen + second, 6, first + reset),
            ("active-osc-prefix-closed-after-overflow", oscOpen + first + reset + second, 6, oscOpen + first + oscClose + reset),
            ("ascii-and-trailing-ansi-exhausted", "ab\u001b[31m", 6, "ab\u001b[31m"),
            ("zero-visible-ansi-exhausted", "\u001b[31m", 5, "\u001b[31m"),
            ("tab-followed-by-ansi-overflow", "\t" + reset + "a", 7, "\t" + reset),
            ("tab-without-ansi-overflow", "\tab", 7, "\t" + reset),
            ("tab-without-overflow-exhausted", "a\tb\u001b[31m", 9, "a\tb\u001b[31m")
        ];
        foreach (var vector in vectors)
        {
            var component = new TerminalSelectList([new(vector.Id, vector.Label)], 1, Bindings());
            var actual = component.Render(vector.Columns);
            e.Observe("authored-ansi-grapheme-control", new { vector.Id, vector.Label, vector.Columns,
                primaryWidth = vector.Columns - 4, vector.ExpectedPrimary, actual,
                expectationProvenance = "Pinned Source span/overflow/exhaustion rules, statically derived and unexecuted" });
            Rows(["\u2192 " + vector.ExpectedPrimary], actual); Equal(vector.Label, component.FilteredItems[0].Label);
        }
        Equal(15, vectors.Length);
    }
}
