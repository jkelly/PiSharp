using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Tui.Components.SelectList;
using PiSharp.Tui.Components.Text;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalTextAdoptionCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("text-adoption-pinned-child-fixtures-through-real-ui-input-and-view", e => SourceFixtures(args[1], e)),
        ("text-adoption-title-is-separate-and-never-a-response-option", TitleAuthority),
        ("text-adoption-wrapped-canonical-option-reply-after-short-resize", ResizeCanonical),
        ("text-adoption-original-held-wrapped-paint-retains-focus-and-joins", HeldPaint),
        ("text-adoption-selected-option-window-and-empty-viewport-clearing", Window),
        ("text-adoption-inert-source-output-and-bounded-before-io", Bounds),
        ("text-adoption-public-native-startup-selector-and-downstream-effect", e => NativeCommand(args[1], e))
    ];

    private static async Task SourceFixtures(string reviewRoot, ConsumerEvidence e)
    {
        const string relative = "tests/PiSharp.Terminal.SelectList.Integration.Tests/fixtures/text-adoption-source.json";
        var source = Path.Combine(reviewRoot, relative);
        var output = Path.Combine(AppContext.BaseDirectory, "fixtures/text-adoption-source.json");
        var sourceBytes = await File.ReadAllBytesAsync(source); var outputBytes = await File.ReadAllBytesAsync(output);
        Check(sourceBytes.AsSpan().SequenceEqual(outputBytes), "Fixture output differs from admitted candidate source.");
        using var fixtures = JsonDocument.Parse(outputBytes);
        var root = fixtures.RootElement;
        Equal("d86654abb8862e201933517d6f1fce9f88dd117f", root.GetProperty("upstreamCommit").GetString());
        Equal("authored-from-static-pinned-source-not-captured", root.GetProperty("expectationKind").GetString());
        Equal(5, root.GetProperty("vectors").GetArrayLength());
        await using var f = await SelectorFixture.Create(); var sequence = 0;
        foreach (var vector in root.GetProperty("vectors").EnumerateArray())
        {
            var title = vector.GetProperty("title").GetString()!;
            var options = Strings(vector.GetProperty("options"));
            var columns = vector.GetProperty("columns").GetInt32(); var rows = vector.GetProperty("viewportRows").GetInt32();
            // Raw child comparison and connected inert VT rows have separately authored expectations.
            Same(Strings(vector.GetProperty("sourceHeadingRows")), new TerminalText(title, 1, 0).Render(columns));
            var expectedOptions = vector.GetProperty("sourceOptionRows");
            for (var index = 0; index < options.Length; index++)
                Same(Strings(expectedOptions[index]), new TerminalText((index == 0 ? "→ " : "  ") + options[index], 1, 0).Render(columns));
            f.Viewport.Value = new(columns, rows, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
            await using var scope = f.Ui.OpenScope(new SelectorContext(OwnerGeneration: sequence + 1));
            var answer = scope.SelectAsync(title, options.ToImmutableArray()).AsTask(); await f.Observer.WaitPublished(++sequence);
            var original = f.Frontend.CaptureSelectDialog()!;
            var nativeWrite = f.Terminal.Writes().Last(write => write.Contains("\u001b[2J", StringComparison.Ordinal));
            var actualRows = FullRows(nativeWrite, rows);
            e.Observe("authored-source-child-and-real-native-selector-rows", new { id = vector.GetProperty("id").GetString(),
                title, options, columns, rows, nativeWrite, actualRows, original.Identity, sourceFixtureHash = EvidenceAdmission.Hash(source),
                sourceObserved = false, approvedDeviation = vector.TryGetProperty("approvedDeviation", out var deviation) ? deviation.GetString() : null });
            Same(Strings(vector.GetProperty("nativeRows")), actualRows);
            Check(!nativeWrite.Contains("\u001b[31m", StringComparison.Ordinal), "Canonical ANSI escaped the actual view boundary.");
            await f.Key("\r"); Equal(options[0], (await answer.WaitAsync(Deadline)).Value);
            Equal(options[0], f.Replies()[^1].Value.GetProperty("value").GetString());
        }
        Equal(1, f.Terminal.MaximumReaders); Equal(0, f.Terminal.Disposals);
    }

    private static async Task TitleAuthority(ConsumerEvidence e)
    {
        await FirstPublicationPaint(e);
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("owned draft");
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        f.Viewport.Value = new(12, 6, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
        var answer = scope.SelectAsync("not an option\nsecond heading", ["only choice"]).AsTask(); await f.Observer.WaitPublished(1);
        var snapshot = f.Frontend.CaptureSelectDialog()!;
        await f.Key("k"); await f.Key("\r");
        Equal("only choice", (await answer.WaitAsync(Deadline)).Value);
        Equal(1, snapshot.Options.Length); Equal("only choice", snapshot.Options[0]);
        Equal("owned draft", f.LastDraft!.Text); Check(f.LastDraft.EditorOwnsFocus, "Selector retirement failed to restore its draft owner.");
        await using var emptyScope = f.Ui.OpenScope(new SelectorContext(OwnerGeneration: 2));
        var empty = emptyScope.SelectAsync("heading only", []).AsTask(); await f.Observer.WaitPublished(2);
        var emptyPaint = f.Terminal.Writes()[^1]; await f.Key("\r");
        Check(!empty.IsCompleted, "Display heading became a canonical selectable value."); Equal(1, f.Replies().Length);
        Check(!emptyPaint.Contains("\u001b[7m", StringComparison.Ordinal), "Empty selector styled its heading as selected.");
        await f.Key("\u001b"); Equal(ExtensionUiOutcomeKind.Cancelled, (await empty.WaitAsync(Deadline)).Kind);
        e.Observe("real-heading-child-without-response-authority", new { snapshot.Options, replies = f.Replies(), emptyPaint, f.LastDraft });
    }

    private static async Task FirstPublicationPaint(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("saved draft");
        f.Viewport.Value = new(48, 8, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        using var caller = new CancellationTokenSource();
        const string title = "first native heading";
        var before = f.Terminal.Writes().Length; var held = f.Terminal.HoldWrite(title);
        var original = scope.SelectAsync(title, ["first", "second"], cancellationToken: caller.Token).AsTask();
        try
        {
            await held.Entered.Task.WaitAsync(Deadline);
            // Hold the first physical title write, before publication acknowledgment can
            // hide a transient cooked frame behind the later native selector adoption.
            var first = f.Terminal.Writes().Skip(before).First(write => write.Contains(title, StringComparison.Ordinal));
            var rows = FullRows(first, 8);
            e.Observe("first-native-selector-publication-original-paint-held", new { rows,
                original.IsCompleted, f.Terminal.ActiveWrites, replies = f.Replies() });
            Equal(new TerminalText(title, 1, 0).Render(48)[0], rows[0]);
            Check(!first.Contains("[ui select]", StringComparison.Ordinal), "First native publication exposed the cooked selector description.");
            Check(!original.IsCompleted && f.Terminal.ActiveWrites == 1 && f.Replies().Length == 0,
                "First selector paint bypassed its original publication owner or supplied an answer.");
            held.Release.TrySetResult(); await f.Observer.WaitPublished(1);
            await f.Key("j"); await f.Key("\r"); Equal("second", (await original.WaitAsync(Deadline)).Value);
            Equal("saved draft", f.LastDraft!.Text); Check(f.LastDraft.EditorOwnsFocus, "First native paint changed draft ownership.");
        }
        finally { held.Release.TrySetResult(); caller.Cancel(); await original; }
        await f.StopAsync(); Equal(0, f.Terminal.ActiveWrites); Equal(0, f.Terminal.ActiveReads);
        Equal(1, f.Terminal.MaximumReaders); Equal(0, f.Terminal.Disposals);
    }

    private static async Task ResizeCanonical(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("kept");
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        const string exact = "first\r\nsecond\t中🙂\u001b[2J\0";
        var answer = scope.SelectAsync("long title\nwrapped heading", ["first option", exact]).AsTask(); await f.Observer.WaitPublished(1);
        var identity = f.Frontend.CaptureSelectDialog()!.Identity;
        await f.Key("j");
        f.Viewport.Value = new(1, 1, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
        var narrow = f.Terminal.Writes()[^1]; Same(["→"], FullRows(narrow, 1));
        Equal(identity, f.Frontend.CaptureSelectDialog()!.Identity); Equal("kept", f.LastDraft!.Text);
        f.Viewport.Value = new(20, 8, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
        var restored = f.Terminal.Writes()[^1];
        Check(restored.Contains("first", StringComparison.Ordinal) && restored.Contains("second", StringComparison.Ordinal), "Restored view collapsed canonical option line breaks.");
        Check(!restored.Contains("\u001b[2J\0", StringComparison.Ordinal) && !restored.Contains('\0'), "Canonical control bytes reached native row output.");
        await f.Key("\n"); Equal(exact, (await answer.WaitAsync(Deadline)).Value);
        Equal(exact, f.Replies().Single().Value.GetProperty("value").GetString());
        Equal("kept", f.LastDraft!.Text); Check(f.LastDraft.EditorOwnsFocus, "Resize changed draft focus authority.");
        e.Observe("actual-native-resize-keeps-canonical-choice-and-identity", new { narrow, restored, identity, replies = f.Replies(), f.LastDraft });
    }

    private static async Task HeldPaint(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("before");
        await using var scope = f.Ui.OpenScope(new SelectorContext()); using var caller = new CancellationTokenSource();
        var hold = f.Terminal.HoldWrite("wrapped choice");
        var answer = scope.SelectAsync("heading\nsecond heading", ["wrapped choice\nline two"], cancellationToken: caller.Token).AsTask();
        try
        {
            await hold.Entered.Task.WaitAsync(Deadline); caller.Cancel(); await hold.Canceled.Task.WaitAsync(Deadline);
            Check(!answer.IsCompleted && f.Terminal.ActiveWrites == 1, "Wrapped component paint detached its physical transport on cancellation.");
            Equal(0, f.Replies().Length); Equal(0, f.Terminal.Disposals);
            e.Observe("wrapped-child-original-physical-write-held", new { answer.IsCompleted, f.Terminal.ActiveWrites, f.Terminal.Disposals });
        }
        finally
        {
            hold.Release.TrySetResult(); await answer; // Join the coordinator's original publication/retirement chain.
        }
        Equal(ExtensionUiOutcomeKind.Cancelled, (await answer).Kind);
        await f.Key("!"); Equal("before!", f.LastDraft!.Text); Equal(1, f.Terminal.MaximumReaders);
        await f.StopAsync(); Equal(0, f.Terminal.ActiveReads); Equal(0, f.Terminal.ActiveWrites);
        e.Observe("wrapped-child-original-write-and-owner-joined", f.Snapshot());
    }

    private static async Task Window(ConsumerEvidence e)
    {
        var list = new TerminalSelectList(Enumerable.Range(0, 300).Select(i => new TerminalSelectListItem("option " + i, "")),
            256, new TerminalKeybindings((raw, key) => TerminalInputDecoder.MatchesRawKey(raw, key))); list.SetSelectedIndex(299);
        var frame = TerminalExtensionSelectorFrameFactory.Create("title", list, 3, 20);
        Equal(44, frame.OptionRange.StartIndex); Equal(300, frame.OptionRange.EndIndex);
        Check(frame.Frame.IsClipped && frame.SelectedFrameRow is not null && frame.Frame.Rows.Any(row => row.Text.Contains("→ option 299", StringComparison.Ordinal)),
            "Bounded option window lost canonical selected item.");
        var sink = new SelectorTerminal(null);
        await using (var renderer = new VtRenderer(sink))
        {
            await renderer.RenderAsync(frame.Frame);
            var empty = new TerminalSelectList([], 256, new TerminalKeybindings((raw, key) => TerminalInputDecoder.MatchesRawKey(raw, key)));
            var clear = TerminalExtensionSelectorFrameFactory.Create("", empty, 3, 20, focused: false);
            Check(clear.Frame.Rows.All(row => row.Text.Length == 0), "Empty child stack retained old visual rows.");
            Equal(TerminalRenderKind.Diff, (await renderer.RenderAsync(clear.Frame)).Kind);
            Check(sink.Writes()[^1].Contains("\u001b[3;1H\u001b[2K", StringComparison.Ordinal), "Empty selector failed to clear its former final row.");
            Equal(TerminalRenderKind.Unchanged, (await renderer.RenderAsync(clear.Frame)).Kind);
            e.Observe("actual-selector-child-window-and-physical-clear", new { frame.HeadingRows, frame.OptionRange,
                frame.BodyRowOffset, frame.SelectedFrameRow, writes = sink.Writes() });
        }
        Equal(0, sink.Disposals);
    }

    private static Task Bounds(ConsumerEvidence e)
    {
        var keys = new TerminalKeybindings((raw, key) => TerminalInputDecoder.MatchesRawKey(raw, key));
        var list = new TerminalSelectList([new("value", "ignored display label", "ignored description")], 256, keys);
        var safe = TerminalExtensionSelectorFrameFactory.Create("title\u001b]0;bad\u0007", list, 5, 30);
        Check(safe.Frame.Rows.All(row => !row.Text.Any(c => c < 32 || c is >= '\u007f' and <= '\u009f')), "Canonical control input gained frame authority.");
        Check(safe.Frame.Rows.Any(row => row.Text.Contains("value", StringComparison.Ordinal)) &&
            safe.Frame.Rows.All(row => !row.Text.Contains("ignored", StringComparison.Ordinal)), "Source canonical option was replaced by a SelectList label/description.");
        Failure(TerminalRenderFailure.ResourceLimit, () => TerminalExtensionSelectorFrameFactory.Create(new string('x', 65_537), list, 5, 30));
        Failure(TerminalRenderFailure.ResourceLimit, () => TerminalExtensionSelectorFrameFactory.Create("title", list, 5, 30,
            limits: new(MaximumInputCharacters: 2)));
        Failure(TerminalRenderFailure.ResourceLimit, () => TerminalExtensionSelectorFrameFactory.Create("title", list, 1, 30,
            limits: new(MaximumFrameCharacters: 32)));
        Failure(TerminalRenderFailure.InvalidUnicode, () => TerminalExtensionSelectorFrameFactory.Create("\ud800", list, 1, 30));
        e.Observe("bounded-source-child-admission-before-terminal-io", new { rows = safe.Frame.Rows, safe.Frame.IsClipped, canonical = list.FilteredItems[0].Value });
        return Task.CompletedTask;
    }

    private static async Task NativeCommand(string reviewRoot, ConsumerEvidence e)
    {
        var files = await StartupOwnedFiles.Create(reviewRoot, plugin: true, e);
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunConfiguredAsync(files.Args(), terminal, terminal, errors, files.Configuration, cancellation.Token);
        try
        {
            await terminal.WaitWrite("STARTUP choose"); await files.AssertWriterOwned();
            var observed = JsonSerializer.SerializeToElement(terminal.Evidence).GetProperty("writes").EnumerateArray().Select(row => row.GetString()!).ToArray();
            var selector = observed.Last(write => write.Contains("STARTUP choose", StringComparison.Ordinal));
            var nativeRows = FullRows(selector, 20);
            e.Observe("public-native-startup-selector-observed-title-paint", new { nativeRows,
                titleWrites = observed.Count(write => write.Contains("STARTUP choose", StringComparison.Ordinal)) });
            Check(nativeRows[0].StartsWith(" STARTUP choose \u6587", StringComparison.Ordinal) && !nativeRows[0].StartsWith("  ", StringComparison.Ordinal),
                "Public command still paints the title as a fake option row.");
            Check(!File.Exists(files.Target), "Startup presentation authorized an effect before selection.");
            await terminal.Feed("x\n"); await terminal.WaitWrite("[history]");
            await terminal.Feed(files.Prompt + "\r"); await terminal.WaitWrite("STARTUP_DONE");
            await terminal.Feed("/quit\r"); Equal(0, await original.WaitAsync(TimeSpan.FromSeconds(15)));
            Equal("", errors.ToString()); Equal(files.Saved, await File.ReadAllTextAsync(files.Target));
            terminal.AssertJoined(); await files.Complete(e);
            e.Observe("public-command-real-native-startup-uses-text-children", new { nativeRows, terminal = terminal.Evidence,
                effectHash = StartupOwnedFiles.Hash(files.Target), sourceObserved = false });
        }
        finally
        {
            cancellation.Cancel(); terminal.End(); terminal.Release(); await original;
            terminal.AssertJoined();
        }
    }

    // Independent full-frame observer for the renderer's declared commands. Literal backslash escapes
    // remain row data. This reads actual controlled-terminal output, not the factory's row objects.
    private static string[] FullRows(string output, int rows)
    {
        var result = Enumerable.Repeat("", rows).ToArray(); var row = 0;
        for (var at = 0; at < output.Length;)
        {
            if (output[at] != '\u001b')
            {
                var end = output.IndexOf('\u001b', at); if (end < 0) end = output.Length;
                Check(row >= 0 && row < rows, "Native cursor escaped the declared viewport.");
                result[row] += output[at..end]; at = end; continue;
            }
            Check(at + 1 < output.Length && output[at + 1] == '[', "Unexpected native control introducer.");
            var start = at + 2; var finish = start;
            while (finish < output.Length && output[finish] is not (>= '@' and <= '~')) finish++;
            Check(finish < output.Length, "Incomplete native control sequence.");
            var parameter = output[start..finish]; var final = output[finish]; at = finish + 1;
            if (final == 'H')
            { var values = parameter.Split(';'); Equal("1", values[1]); row = int.Parse(values[0], System.Globalization.CultureInfo.InvariantCulture) - 1; }
            else if (final == 'J' && parameter == "2") Array.Fill(result, "");
            else if (final == 'K' && parameter == "2") result[row] = "";
            else if (final == 'm' && parameter is "0" or "7") { }
            else if (final is 'h' or 'l' && parameter == "?25") { }
            else throw new InvalidOperationException("Unexpected native selector VT command.");
        }
        return result;
    }
    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();
    private static void Same(IEnumerable<string> expected, IEnumerable<string> actual)
    { Check(expected.SequenceEqual(actual), "Authored Source/native rows differ: " + string.Join("|", actual)); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => TerminalSelectDialogCases.Equal(expected, actual);
    private static void Failure(TerminalRenderFailure expected, Action action)
    { try { action(); } catch (TerminalRenderException error) { Equal(expected, error.Failure); return; } throw new InvalidOperationException("Expected render failure."); }
}
