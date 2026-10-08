using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

if (args.Length == 6 && args[0] == "--trusted-source-frame" && args[2] == "--renderer-contract-supplemental" && args[4] == "--report")
    return await TerminalTrustedSourceFrameProbe.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]), Path.GetFullPath(args[5]));

if (args.Length == 6 && args[0] == "--renderer-contract-source" && args[2] == "--renderer-contract-supplemental" && args[4] == "--report")
    return await TerminalRendererContractProbe.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]), Path.GetFullPath(args[5]));

string? rgiWidthSourceObservations = null, rgiLayoutSourceObservations = null;
string? reportPath = null, sourceObservations = null, pasteSourceObservations = null, undoSourceObservations = null, controlsSourceObservations = null, historyLayoutSourceObservations = null, supplementarySourceObservations = null, characterJumpSourceObservations = null, wordControlsSourceObservations = null, wordDictionarySourceObservations = null, wordSeaSourceObservations = null, wordSeaMaximumSourceObservations = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--report" && reportPath is null && index + 1 < args.Length) reportPath = Path.GetFullPath(args[++index]);
    else if (args[index] == "--source-observations" && sourceObservations is null && index + 1 < args.Length) sourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--paste-source-observations" && pasteSourceObservations is null && index + 1 < args.Length) pasteSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--undo-source-observations" && undoSourceObservations is null && index + 1 < args.Length) undoSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--controls-source-observations" && controlsSourceObservations is null && index + 1 < args.Length) controlsSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--history-layout-source-observations" && historyLayoutSourceObservations is null && index + 1 < args.Length) historyLayoutSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--supplementary-source-observations" && supplementarySourceObservations is null && index + 1 < args.Length) supplementarySourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--character-jump-source-observations" && characterJumpSourceObservations is null && index + 1 < args.Length) characterJumpSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--word-controls-source-observations" && wordControlsSourceObservations is null && index + 1 < args.Length) wordControlsSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--word-dictionary-source-observations" && wordDictionarySourceObservations is null && index + 1 < args.Length) wordDictionarySourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--word-sea-source-observations" && wordSeaSourceObservations is null && index + 1 < args.Length) wordSeaSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--word-sea-maximum-source-observations" && wordSeaMaximumSourceObservations is null && index + 1 < args.Length) wordSeaMaximumSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--rgi-width-source-observations" && rgiWidthSourceObservations is null && index + 1 < args.Length) rgiWidthSourceObservations = Path.GetFullPath(args[++index]);
    else if (args[index] == "--rgi-layout-source-observations" && rgiLayoutSourceObservations is null && index + 1 < args.Length) rgiLayoutSourceObservations = Path.GetFullPath(args[++index]);
    else throw new ArgumentException("Usage: PiSharp.Tui.Editor.Tests --report <absolute-path> [--source-observations <absolute-path>]");
}
if (reportPath is null) throw new ArgumentException("An explicit report path is required.");
var results = new List<object>(); var failed = 0;
foreach (var test in EditorTests.Cases().Concat(TerminalHistoryNavigationV2Tests.Cases()).Concat(TerminalSourceProfileTests.Cases()).Concat(TerminalCharacterJumpTests.Cases()).Concat(TerminalWordControlsTests.Cases()).Concat(TerminalWordDictionaryTests.Cases()).Concat(TerminalWordSeaTests.Cases()).Concat(
    TerminalHistoryLayoutInvariantTests.Cases().Select(test => (test.Name, Run: (Action)(() => test.Run().GetAwaiter().GetResult())))))
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); results.Add(new { id = test.Name, status = "passed" }); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error}"); results.Add(new { id = test.Name, status = "failed", error = error.ToString() }); }
}
object? comparisons = null, pasteComparisons = null, legacyComparisons = null, undoComparisons = null, controlsComparisons = null, historyLayoutComparisons = null, sourceProfileComparisons = null, supplementaryEscapedComparisons = null, supplementarySourceProfileComparisons = null, characterJumpComparisons = null, wordControlsComparisons = null, wordDictionaryComparisons = null, wordSeaComparisons = null, wordSeaMaximumComparisons = null;
var sourceDifferentCases = 0; var pasteDifferentCases = 0; var legacyDifferentCases = 0; var undoDifferentCases = 0;
var controlsDifferentCases = 0;
var historyLayoutDifferentCases = 0;
var sourceProfileDifferentCases = 0; var supplementaryEscapedDifferentCases = 0; var supplementarySourceProfileDifferentCases = 0;
var characterJumpDifferentCases = 0; var wordControlsDifferentCases = 0; var wordDictionaryDifferentCases = 0;
if (sourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(sourceObservations));
    comparisons = EditorTests.CompareSource(source.RootElement, out sourceDifferentCases);
    legacyComparisons = EditorTests.CompareSource(source.RootElement, out legacyDifferentCases, pasteAware: false);
}
if (pasteSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(pasteSourceObservations));
    pasteComparisons = EditorTests.CompareSource(source.RootElement, out pasteDifferentCases, includeExpansion: true);
}
if (undoSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(undoSourceObservations));
    undoComparisons = EditorTests.CompareSource(source.RootElement, out undoDifferentCases, includeExpansion: true);
}
if (controlsSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(controlsSourceObservations));
    controlsComparisons = EditorTests.CompareSource(source.RootElement, out controlsDifferentCases, includeExpansion: true);
}
if (historyLayoutSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(historyLayoutSourceObservations));
    historyLayoutComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out historyLayoutDifferentCases);
    sourceProfileComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out sourceProfileDifferentCases, sourceProfile: true);
}
if (supplementarySourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(supplementarySourceObservations));
    supplementaryEscapedComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out supplementaryEscapedDifferentCases);
    supplementarySourceProfileComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out supplementarySourceProfileDifferentCases, sourceProfile: true);
}
if (characterJumpSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(characterJumpSourceObservations));
    characterJumpComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out characterJumpDifferentCases, captureEvents: true);
}
if (wordControlsSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(wordControlsSourceObservations));
    wordControlsComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out wordControlsDifferentCases, captureWordEvents: true);
}
if (wordDictionarySourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(wordDictionarySourceObservations));
    wordDictionaryComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out wordDictionaryDifferentCases);
}
var wordSeaDifferentCases = 0;
if (wordSeaSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(wordSeaSourceObservations));
    wordSeaComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out wordSeaDifferentCases);
}
var wordSeaMaximumDifferentCases = 0;
if (wordSeaMaximumSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(wordSeaMaximumSourceObservations));
    wordSeaMaximumComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out wordSeaMaximumDifferentCases);
}
object? rgiWidthComparisons = null, rgiSourceLayoutComparisons = null, rgiEscapedLayoutComparisons = null;
var rgiWidthDifferentObservations = 0; var rgiSourceLayoutDifferentCases = 0; var rgiEscapedLayoutDifferentCases = 0;
if (rgiWidthSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(rgiWidthSourceObservations));
    rgiWidthComparisons = TerminalRgiWidthSourceTests.Compare(source.RootElement, out rgiWidthDifferentObservations);
}
if (rgiLayoutSourceObservations is not null)
{
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(rgiLayoutSourceObservations));
    rgiSourceLayoutComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out rgiSourceLayoutDifferentCases, sourceProfile: true);
    rgiEscapedLayoutComparisons = TerminalHistoryLayoutSourceReplay.Compare(source.RootElement, out rgiEscapedLayoutDifferentCases);
}
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
{
    rgiWidthComparisons, rgiSourceLayoutComparisons, rgiEscapedLayoutComparisons,
    schemaVersion = 1, upstreamCommit = "d86654abb8862e201933517d6f1fce9f88dd117f",
    scope = "P5-08 original word controls; all predecessor corpora retained; ICU dictionary/locale and lead-owned host composition remain separately qualified",
    segmentationPolicy = TerminalTextEditor.SegmentationPolicyId,
    wordSegmentation = "Managed pinned ICU78.3 CJK/Thai engines and Unicode17 normalization; official Lao/Khmer/Burmese engines; no new optional profile",
    fixtureSubmissionContract = "Exact accepted trim6047151 helper after expansion; expected objects unchanged; actual hosts/receipts separately qualified",
    runtime = new { framework = RuntimeInformation.FrameworkDescription, Environment.Version, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString() },
    tests = results, passed = results.Count - failed, failed, comparisons, pasteComparisons, legacyComparisons, undoComparisons, controlsComparisons, historyLayoutComparisons, sourceProfileComparisons, supplementaryEscapedComparisons, supplementarySourceProfileComparisons, characterJumpComparisons, wordControlsComparisons, wordDictionaryComparisons,
    wordSeaComparisons, wordSeaMaximumComparisons,
    nativeWordSeaDataObservations = TerminalWordSeaTests.DataObservations,
    nativeWordSeaSegmentObservations = TerminalWordSeaTests.SegmentObservations,
    wordResourceObservations = TerminalWordSeaTests.ResourceObservations,
    nativeWordDictionarySegmentObservations = TerminalWordDictionaryTests.SegmentObservations,
    nativeWordDictionaryDataObservations = TerminalWordDictionaryTests.DataObservations,
    nativeDecodedWordEvents = TerminalHistoryLayoutSourceReplay.NativeDecodedWordEvents,
    nativeWordBoundaryObservations = TerminalWordControlsTests.BoundaryObservations,
    nativeWordDecoderObservations = TerminalWordControlsTests.DecoderObservations,
    nativeWordRejectionObservations = TerminalWordControlsTests.RejectionObservations,
    nativeDecodedJumpEvents = TerminalHistoryLayoutSourceReplay.NativeDecodedJumpEvents,
    nativeCharacterJumpDecoderObservations = TerminalCharacterJumpTests.DecoderObservations,
    nativeCharacterJumpRejectionObservations = TerminalCharacterJumpTests.RejectionObservations,
    nativeV2RejectionObservations = TerminalHistoryNavigationV2Tests.RejectionObservations,
    sourceComparisonDisposition = sourceObservations is null ? "not run" : sourceDifferentCases == 0 ? "selected complete observations match" : "HOLD: source/native observations differ",
    pasteComparisonDisposition = pasteSourceObservations is null ? "not run" : pasteDifferentCases == 0 ? "selected complete marker observations match" : "HOLD: marker source/native observations differ",
    legacyComparisonDisposition = sourceObservations is null ? "not run" : legacyDifferentCases == 0 ? "existing controller complete observations match" : "HOLD: existing controller observations differ",
    undoComparisonDisposition = undoSourceObservations is null ? "not run" : undoDifferentCases == 0 ? "selected complete undo observations match" : "HOLD: undo source/native observations differ",
    controlsComparisonDisposition = controlsSourceObservations is null ? "not run" : controlsDifferentCases == 0 ? "selected complete control observations match" : "HOLD: control source/native observations differ",
    historyLayoutComparisonDisposition = historyLayoutSourceObservations is null ? "not run" : historyLayoutDifferentCases == 0 ? "complete history/layout observations match" : "HOLD: complete source history/layout observations differ; raw Unicode/source wrapping/render parity remains OPEN",
    experimentalSourceProfileDisposition = sourceProfileDifferentCases + supplementarySourceProfileDifferentCases == 0 ? "Selected complete experimental source-profile observations match; full Unicode/platform/IME and lead-owned production wiring remain OPEN" : "HOLD: experimental source-profile complete observations differ",
    characterJumpDisposition = characterJumpSourceObservations is null ? "not run" : characterJumpDifferentCases == 0 ? "Complete original character-jump observations match; actual host contract and full package remain separately qualified" : "HOLD: original character-jump complete observations differ",
    wordControlsDisposition = wordControlsSourceObservations is null ? "not run" : wordControlsDifferentCases == 0 ? "Complete original word-control observations match; package criteria remain separately qualified" : "HOLD: complete word-control source/native observations differ",
    wordDictionaryDisposition = wordDictionarySourceObservations is null ? "not run" : wordDictionaryDifferentCases == 0 ? "Complete additional dictionary observations match; full package remains separately qualified" : "HOLD: complete additional dictionary observations differ; retain all structures",
    productionCliBootstrapQualified = false, completePackageAcceptanceClaimed = false, fullPhaseGatesPassed = 0
}, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Editor tests: {results.Count - failed} passed, {failed} failed. Source differences, when supplied, remain explicit in the report.");
return rgiWidthDifferentObservations == 0 && rgiSourceLayoutDifferentCases == 0 && failed == 0 && sourceDifferentCases == 0 && pasteDifferentCases == 0 && legacyDifferentCases == 0 && undoDifferentCases == 0 && controlsDifferentCases == 0 && historyLayoutDifferentCases == 0 && characterJumpDifferentCases == 0 && wordControlsDifferentCases == 0 && wordDictionaryDifferentCases == 0 && wordSeaDifferentCases == 0 && wordSeaMaximumDifferentCases == 0 ? 0 : 1;

internal static class EditorTests
{
    private static readonly string[] Clusters = ["e\u0301", "中", "\U0001f642", "\U0001f469\u200d\U0001f469\u200d\U0001f467\u200d\U0001f466", "\U0001f1fa\U0001f1f8", "\U0001f44d\U0001f3fd", "\r\n", "x"];

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("editor.authored-whole-cluster-cursors-and-immutable-snapshots", Boundaries);
        yield return ("editor.backward-and-forward-deletion-at-every-authored-boundary", Deletion);
        yield return ("editor.insertion-and-deletion-resegment-joined-clusters", Seams);
        yield return ("editor.multiline-cr-lf-crlf-navigation-and-line-joins", Lines);
        yield return ("editor.exact-limits-invalid-unicode-and-cursor-rejections-are-atomic", Rejections);
        yield return ("editor.actual-decoder-every-split-unicode-paste-and-key-schedules", DecoderSchedules);
        yield return ("editor.complete-bounded-draft-movement-and-clear", FullDraft);
        yield return ("editor.strict-comparator-detects-nested-extra-missing-and-value-mutations", ComparatorControls);
        yield return ("editor-controller.programmatic-normalization-and-atomic-bound-rejection", ControllerNormalization);
        yield return ("editor-controller.actual-no-op-notifications-and-committed-callback-errors", ControllerNotifications);
        yield return ("editor-controller.paste-filter-csi-controls-path-prefix-and-explicit-large-paste", ControllerPaste);
        yield return ("editor-controller.source-edit-seams-followed-by-both-deletion-directions", ControllerSeams);
        yield return ("editor-controller.escaped-projection-preserves-source-seams-and-bounded-caret", ControllerProjection);
        yield return ("paste-controller.prepared-size-thresholds-and-source-marker-labels", PasteThresholds);
        yield return ("paste-controller.atomic-markers-forward-retention-backspace-renumber-and-reset", PasteLifecycle);
        yield return ("paste-controller.logical-expanded-retained-and-entry-limits-are-atomic", PasteLimits);
        yield return ("paste-controller.actual-marker-notifications-and-consistent-observer-errors", PasteNotifications);
        yield return ("paste-controller.ordered-literal-expansion-and-source-alias-quirks", PasteExpansion);
        yield return ("editor-undo.source-word-whitespace-and-no-op-action-grouping", UndoGrouping);
        yield return ("editor-undo.restores-paste-registry-counter-expansion-and-scalar-seams", UndoState);
        yield return ("editor-undo.rejected-operations-preserve-history-and-typing-group", UndoRejections);
        yield return ("editor-undo.exact-entry-and-retention-eviction-and-host-reset", UndoBounds);
        yield return ("editor-undo.actual-raw-and-coded-bindings-and-committed-observer-errors", UndoBindingsAndEvents);
        yield return ("editor-controls.default-decoded-aliases-and-newline-identities", ControlsAliases);
        yield return ("editor-controls.line-kill-accumulation-and-retained-marker-yank", ControlsKills);
        yield return ("editor-controls.source-two-phase-yank-pop-and-independent-ring-undo", ControlsYank);
        yield return ("editor-controls.exact-ring-bounds-and-atomic-yank-admission", ControlsBounds);
        yield return ("editor-controls.actual-phase-observers-and-committed-callback-errors", ControlsObservers);
    }

    private static void Boundaries()
    {
        var text = string.Concat(Clusters); var editor = new TerminalTextEditor();
        True(editor.SetText(text, 0)); var original = editor.Snapshot;
        var expected = 0;
        foreach (var cluster in Clusters) { True(editor.MoveRight()); expected += cluster.Length; Equal(expected, editor.Snapshot.CursorUtf16Offset); }
        True(!editor.MoveRight());
        foreach (var cluster in Clusters.Reverse()) { True(editor.MoveLeft()); expected -= cluster.Length; Equal(expected, editor.Snapshot.CursorUtf16Offset); }
        True(!editor.MoveLeft()); Equal(new TerminalTextEditorSnapshot(text, 0), original);
        True(!editor.SetText(text, 0));
        var valid = new HashSet<int> { 0 }; var offset = 0;
        foreach (var cluster in Clusters) { offset += cluster.Length; valid.Add(offset); }
        for (var index = 0; index <= text.Length; index++)
        {
            if (valid.Contains(index)) { editor.SetCursor(index); Equal(index, editor.Snapshot.CursorUtf16Offset); }
            else { var before = editor.Snapshot; Failure(TerminalTextEditorFailure.InvalidCursor, () => editor.SetCursor(index)); Same(before, editor.Snapshot); }
        }
        editor.Insert("q"); Equal(text, original.Text); Equal(0, original.CursorUtf16Offset);
    }

    private static void Deletion()
    {
        var text = string.Concat(Clusters); var cursor = 0;
        for (var index = 0; index < Clusters.Length; index++)
        {
            var editor = new TerminalTextEditor(); editor.SetText(text, cursor);
            True(editor.Delete()); Equal(text.Remove(cursor, Clusters[index].Length), editor.Snapshot.Text); Equal(cursor, editor.Snapshot.CursorUtf16Offset);
            cursor += Clusters[index].Length; editor.SetText(text, cursor);
            True(editor.Backspace()); Equal(text.Remove(cursor - Clusters[index].Length, Clusters[index].Length), editor.Snapshot.Text);
            Equal(cursor - Clusters[index].Length, editor.Snapshot.CursorUtf16Offset);
        }
        var empty = new TerminalTextEditor(); True(!empty.Backspace() && !empty.Delete());
        var reverse = new TerminalTextEditor(); reverse.SetText(text);
        for (var count = Clusters.Length - 1; count >= 0; count--) { True(reverse.Backspace()); Equal(string.Concat(Clusters.Take(count)), reverse.Snapshot.Text); }
        True(!reverse.Backspace());
    }

    private static void Seams()
    {
        AssertEdit("eA", 1, edit => edit.Insert("\u0301"), "e\u0301A", 2);
        AssertEdit("\U0001f469\U0001f469", 2, edit => edit.Insert("\u200d"), "\U0001f469\u200d\U0001f469", 5);
        AssertEdit("\U0001f1faX\U0001f1f8", 2, edit => edit.Delete(), "\U0001f1fa\U0001f1f8", 4);
        AssertEdit("\U0001f1faX\U0001f1f8", 3, edit => edit.Backspace(), "\U0001f1fa\U0001f1f8", 4);
        AssertEdit("a\n\u0301b", 1, edit => edit.Delete(), "a\u0301b", 2);
        AssertEdit("a\r\n\u0301b", 3, edit => edit.Backspace(), "a\u0301b", 2);
        AssertEdit("\U0001f44dX", 2, edit => edit.Insert("\U0001f3fd"), "\U0001f44d\U0001f3fdX", 4);
        AssertEdit("\U0001f1fa\U0001f1f8", 0, edit => edit.Insert("\U0001f1e8"), "\U0001f1e8\U0001f1fa\U0001f1f8", 4);
    }

    private static void Lines()
    {
        const string text = "a\nb\r\nc\rd";
        (int Cursor, int Start, int End)[] cases = [(0, 0, 1), (1, 0, 1), (2, 2, 3), (3, 2, 3), (5, 5, 6), (6, 5, 6), (7, 7, 8), (8, 7, 8)];
        foreach (var item in cases)
        {
            var editor = new TerminalTextEditor(); editor.SetText(text, item.Cursor); editor.MoveLineStart(); Equal(item.Start, editor.Snapshot.CursorUtf16Offset);
            editor.SetCursor(item.Cursor); editor.MoveLineEnd(); Equal(item.End, editor.Snapshot.CursorUtf16Offset);
        }
        var emptyLines = new TerminalTextEditor(); emptyLines.SetText("\n\r\n\r");
        foreach (var cursor in new[] { 0, 1, 3, 4 }) { emptyLines.SetCursor(cursor); True(!emptyLines.MoveLineStart() && !emptyLines.MoveLineEnd()); }
        AssertEdit("a\nb", 1, edit => edit.Delete(), "ab", 1);
        AssertEdit("a\nb", 2, edit => edit.Backspace(), "ab", 1);
        var middle = new TerminalTextEditor(); middle.SetText("abc", 1); middle.Insert("x\r\ny"); Equal("ax\r\nybc", middle.Snapshot.Text); Equal(5, middle.Snapshot.CursorUtf16Offset);
    }

    private static void Rejections()
    {
        foreach (var limit in new[] { 0, -1, 65_537 }) Failure(TerminalTextEditorFailure.InvalidOptions, () => new TerminalTextEditor(limit));
        var editor = new TerminalTextEditor(4); editor.SetText("\U0001f642ab", 2); var original = editor.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => editor.Insert("x")); Same(original, editor.Snapshot);
        Failure(TerminalTextEditorFailure.ResourceLimit, () => editor.SetText("abcde")); Same(original, editor.Snapshot);
        foreach (var cursor in new[] { -1, 1, 5 }) { Failure(TerminalTextEditorFailure.InvalidCursor, () => editor.SetCursor(cursor)); Same(original, editor.Snapshot); }
        foreach (var malformed in new[] { "\ud800", "\udc00", "\ud800x", "x\udc00" })
        {
            Failure(TerminalTextEditorFailure.InvalidUnicode, () => editor.SetText(malformed)); Same(original, editor.Snapshot);
            var roomy = new TerminalTextEditor(); roomy.SetText("ab", 1); var before = roomy.Snapshot;
            Failure(TerminalTextEditorFailure.InvalidUnicode, () => roomy.Insert(malformed)); Same(before, roomy.Snapshot);
        }
        Failure(TerminalTextEditorFailure.InvalidCursor, () => editor.SetText("e\u0301", 1)); Same(original, editor.Snapshot);
        Throws<ArgumentNullException>(() => editor.Insert(null!)); Same(original, editor.Snapshot);
        Throws<ArgumentNullException>(() => editor.SetText(null!)); Same(original, editor.Snapshot);
        True(!editor.Insert("")); Same(original, editor.Snapshot);
        var one = new TerminalTextEditor(1); one.Insert("a"); Failure(TerminalTextEditorFailure.ResourceLimit, () => one.Insert("\u0301")); Equal("a", one.Snapshot.Text);
        editor.Clear(); True(!editor.Clear()); Equal(new TerminalTextEditorSnapshot("", 0), editor.Snapshot);
    }

    private static void DecoderSchedules()
    {
        const string raw = "e\u0301\U0001f642\u001b[DX\u001b[3~\u001b[200~\r\n中\U0001f469\u200d\U0001f467\u001b[201~\u001b[D\u007f";
        var expected = new TerminalTextEditorSnapshot("e\u0301X\r\n\U0001f469\u200d\U0001f467", 5);
        for (var split = 0; split <= raw.Length; split++)
        {
            var editor = new TerminalTextEditor(); var decoder = new TerminalInputDecoder(timeProvider: new FixedClock());
            Apply(editor, decoder.Feed(raw.AsSpan(0, split))); Apply(editor, decoder.Feed(raw.AsSpan(split))); Apply(editor, decoder.Complete());
            Equal(expected, editor.Snapshot);
        }
        var tiny = new TerminalTextEditor(); var fragmented = new TerminalInputDecoder(timeProvider: new FixedClock());
        foreach (var value in raw) Apply(tiny, fragmented.Feed(value.ToString())); Apply(tiny, fragmented.Complete()); Equal(expected, tiny.Snapshot);
        var literal = new TerminalTextEditor(); var paste = new TerminalInputDecoder(timeProvider: new FixedClock());
        Apply(literal, paste.Feed("\u001b[200~\u0003\u001b[31m\u001b[201~")); Equal("\u0003\u001b[31m", literal.Snapshot.Text);
    }

    private static void FullDraft()
    {
        var text = new string('a', 65_536); var editor = new TerminalTextEditor(); editor.SetText(text, 0);
        for (var index = 0; index < text.Length; index++) True(editor.MoveRight());
        True(!editor.MoveRight()); Equal(text.Length, editor.Snapshot.CursorUtf16Offset);
        for (var index = text.Length; index > 0; index--) True(editor.MoveLeft());
        True(!editor.MoveLeft()); editor.MoveDocumentEnd(); Equal(text.Length, editor.Snapshot.CursorUtf16Offset); editor.MoveDocumentStart();
        Equal(0, editor.Snapshot.CursorUtf16Offset); editor.Clear(); Equal(new TerminalTextEditorSnapshot("", 0), editor.Snapshot);
    }

    public static object CompareSource(JsonElement source, out int differentCases, bool includeExpansion = false, bool pasteAware = true)
    {
        var cases = new List<object>(); differentCases = 0;
        foreach (var sourceCase in source.GetProperty("cases").EnumerateArray())
        {
            var editor = pasteAware ? new TerminalTextEditorPasteController() : null;
            var legacy = pasteAware ? null : new TerminalTextEditorController();
            var changes = new List<string>(); var steps = new List<object>();
            if (editor is not null) editor.TextChanged += changes.Add; else legacy!.TextChanged += changes.Add;
            foreach (var step in sourceCase.GetProperty("input").GetProperty("steps").EnumerateArray())
            {
                if (step.GetProperty("kind").GetString() == "set")
                { if (editor is not null) editor.SetText(step.GetProperty("data").GetString()!); else legacy!.SetText(step.GetProperty("data").GetString()!); }
                else if (step.GetProperty("kind").GetString() == "insert")
                { if (editor is not null) editor.InsertText(step.GetProperty("data").GetString()!); else legacy!.InsertText(step.GetProperty("data").GetString()!); }
                else
                {
                    var decoder = new TerminalInputDecoder(timeProvider: new FixedClock());
                    foreach (var input in decoder.Feed(step.GetProperty("data").GetString()!).AddRange(decoder.Complete()))
                    { if (editor is not null) editor.HandleInput(input); else legacy!.HandleInput(input); }
                }
                var snapshot = editor?.Snapshot ?? legacy!.Snapshot;
                var text = snapshot.Text; var cursor = snapshot.CursorUtf16Offset; var line = 0; var col = 0;
                for (var index = 0; index < cursor; index++)
                    if (text[index] is '\r' or '\n') { if (text[index] == '\r' && index + 1 < cursor && text[index + 1] == '\n') index++; line++; col = 0; }
                    else col++;
                if (includeExpansion) steps.Add(new { text, expandedText = editor!.GetExpandedText(), cursor = new { line, col }, cursorUtf16Offset = cursor, changes = changes.ToArray(), submits = Array.Empty<string>(), renderRequests = 0 });
                else steps.Add(new { text, cursor = new { line, col }, cursorUtf16Offset = cursor, changes = changes.ToArray(), submits = Array.Empty<string>(), renderRequests = 0 });
            }
            var actual = JsonSerializer.SerializeToElement(new { steps });
            var expected = sourceCase.GetProperty("observation").Clone(); var differences = new List<object>();
            Difference(expected, actual, "$", differences);
            if (differences.Count != 0) differentCases++;
            cases.Add(new { id = sourceCase.GetProperty("id").GetString(), input = sourceCase.GetProperty("input").Clone(), expected, actual, matches = differences.Count == 0, differences });
        }
        return new { sourceBoundary = "Unchanged upstream Editor public setText/handleInput/getText/getCursor plus onChange/onSubmit/render-request observers; actual native controller TextChanged callbacks and draft snapshots", normalization = "none", matchedCases = cases.Count - differentCases, differentCases, cases };
    }

    private static void ControllerNormalization()
    {
        var controller = new TerminalTextEditorController(); var changes = new List<string>(); controller.TextChanged += changes.Add;
        controller.SetText("a\r\nb\rc\td"); Equal(new TerminalTextEditorSnapshot("a\nb\nc    d", 10), controller.Snapshot);
        controller.HandleInput(new TerminalKey("Home")); Equal(4, controller.Snapshot.CursorUtf16Offset);
        controller.InsertText("x\r\n\t"); Equal("a\nb\nx\n    c    d", controller.Snapshot.Text); Equal(10, controller.Snapshot.CursorUtf16Offset);
        Equal(2, changes.Count);
        var small = new TerminalTextEditorController(4); var notifications = 0; small.TextChanged += _ => notifications++;
        small.SetText("ab"); var before = small.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => small.SetText("\tX")); Same(before, small.Snapshot);
        Failure(TerminalTextEditorFailure.ResourceLimit, () => small.InsertText("\t")); Same(before, small.Snapshot);
        Failure(TerminalTextEditorFailure.InvalidUnicode, () => small.SetText("\ud800")); Same(before, small.Snapshot);
        Equal(1, notifications);
        small.SetText("\t"); Equal(new TerminalTextEditorSnapshot("    ", 4), small.Snapshot); Equal(2, notifications);
        small.SetCursor(0); var full = small.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => small.InsertText("\r\n")); Same(full, small.Snapshot); Equal(2, notifications);
        True(!small.InsertText("")); Same(full, small.Snapshot); Equal(2, notifications);
        var maximum = new TerminalTextEditorController(); maximum.SetText(new string('\t', 16_384));
        Equal(new string(' ', 65_536), maximum.Snapshot.Text); Equal(65_536, maximum.Snapshot.CursorUtf16Offset);
        var atLimit = maximum.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => maximum.SetText(new string('\t', 16_384) + "x")); Same(atLimit, maximum.Snapshot);
    }

    private static void ControllerNotifications()
    {
        var controller = new TerminalTextEditorController(); var changes = new List<string>(); controller.TextChanged += changes.Add;
        True(!controller.SetText("")); controller.HandleInput(new TerminalKey("Backspace")); controller.HandleInput(new TerminalKey("Delete"));
        controller.HandleInput(new TerminalKey("Left")); controller.HandleInput(new TerminalProtocol("\u001b[I"));
        controller.HandleInput(new TerminalUnknownSequence("\u001b[999~")); controller.HandleInput(new TerminalKey("Backspace", Action: TerminalKeyAction.Release));
        controller.HandleInput(new TerminalText("")); controller.HandleInput(new TerminalPaste("\u0003"));
        Equal(3, changes.Count); True(changes.All(value => value == ""));
        controller.HandleInput(new TerminalText("e\u0301")); Equal(4, changes.Count); Equal("e\u0301", changes[^1]);
        controller.SetText("e\u0301"); Equal(5, changes.Count);
        var throwing = new TerminalTextEditorController(); throwing.TextChanged += _ => throw new InvalidOperationException("observer");
        Throws<InvalidOperationException>(() => throwing.SetText("x")); Equal(new TerminalTextEditorSnapshot("x", 1), throwing.Snapshot);
    }

    private static void ControllerPaste()
    {
        var controller = new TerminalTextEditorController(); controller.SetText("x");
        controller.HandleInput(new TerminalPaste("\u0003\u001b[31m")); Equal("x[31m", controller.Snapshot.Text);
        controller.Clear(); controller.HandleInput(new TerminalPaste("\u001b[106;5u\u001b[109;5u\u001b[105;5u\u001b[99;5u\u001b[74;5u\u001b[1;5u\u007f\u0085"));
        Equal("\n\n    \n[1;5u\u007f\u0085", controller.Snapshot.Text);
        foreach (var prefix in new[] { "/file", "~file", ".file" })
        { controller.SetText("a"); controller.HandleInput(new TerminalPaste(prefix)); Equal("a " + prefix, controller.Snapshot.Text); }
        controller.SetText("\u4e2d"); controller.HandleInput(new TerminalPaste("/file")); Equal("\u4e2d/file", controller.Snapshot.Text);
        controller.SetText("a\nb"); controller.SetCursor(2); controller.HandleInput(new TerminalPaste("/file")); Equal("a\n/fileb", controller.Snapshot.Text);
        controller.Clear(); var large = new string('a', 1001);
        Equal(TerminalEditorInputDisposition.LiteralLargePaste, controller.HandleInput(new TerminalPaste(large))); Equal(large, controller.Snapshot.Text);
        True(!TerminalTextEditorController.PasteMarkerExpansionSupported);
        controller.Clear(); Equal(TerminalEditorInputDisposition.Handled, controller.HandleInput(new TerminalPaste(new string('a', 1000))));
        controller.Clear(); Equal(TerminalEditorInputDisposition.Handled, controller.HandleInput(new TerminalPaste(new string('\n', 9))));
        controller.Clear(); Equal(TerminalEditorInputDisposition.LiteralLargePaste, controller.HandleInput(new TerminalPaste(new string('\n', 10))));
        var bounded = new TerminalTextEditorController(4); bounded.SetText("a"); var before = bounded.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => bounded.HandleInput(new TerminalPaste("\t"))); Same(before, bounded.Snapshot);
    }

    private static void ControllerSeams()
    {
        var zwj = new TerminalTextEditorController(); zwj.SetText("\U0001f469\U0001f469"); zwj.HandleInput(new TerminalKey("Left")); zwj.HandleInput(new TerminalText("\u200d"));
        Equal(new TerminalTextEditorSnapshot("\U0001f469\u200d\U0001f469", 3), zwj.Snapshot);
        var original = zwj.Snapshot; zwj.HandleInput(new TerminalKey("Backspace")); Equal(new TerminalTextEditorSnapshot("\U0001f469", 0), zwj.Snapshot);
        Equal(new TerminalTextEditorSnapshot("\U0001f469\u200d\U0001f469", 3), original);
        zwj.SetText("\U0001f469\u200d\U0001f469"); zwj.SetCursor(3); zwj.HandleInput(new TerminalKey("Delete")); Equal(new TerminalTextEditorSnapshot("\U0001f469\u200d", 3), zwj.Snapshot);
        var flag = new TerminalTextEditorController(); flag.SetText("\U0001f1faX\U0001f1f8"); flag.SetCursor(2); flag.HandleInput(new TerminalKey("Delete"));
        Equal(new TerminalTextEditorSnapshot("\U0001f1fa\U0001f1f8", 2), flag.Snapshot); flag.HandleInput(new TerminalKey("Backspace")); Equal(new TerminalTextEditorSnapshot("\U0001f1f8", 0), flag.Snapshot);
        flag.SetText("\U0001f1fa\U0001f1f8"); flag.SetCursor(2); flag.HandleInput(new TerminalKey("Delete")); Equal(new TerminalTextEditorSnapshot("\U0001f1fa", 2), flag.Snapshot);
        var combining = new TerminalTextEditorController(); combining.SetText("a\n\u0301b"); combining.SetCursor(2); combining.HandleInput(new TerminalKey("Backspace"));
        Equal(new TerminalTextEditorSnapshot("a\u0301b", 1), combining.Snapshot); combining.HandleInput(new TerminalKey("Delete")); Equal(new TerminalTextEditorSnapshot("ab", 1), combining.Snapshot);
        combining.SetText("a\u0301b"); combining.SetCursor(1); combining.HandleInput(new TerminalKey("Backspace")); Equal(new TerminalTextEditorSnapshot("\u0301b", 0), combining.Snapshot);
        var cursorBefore = zwj.Snapshot; Failure(TerminalTextEditorFailure.InvalidCursor, () => zwj.SetCursor(1)); Same(cursorBefore, zwj.Snapshot);
    }

    private static void ControllerProjection()
    {
        var woman = new TerminalTextEditorSnapshot("\U0001f469\u200d\U0001f469", 3);
        var projected = TerminalTextEditorProjection.Create(woman, 40, 3);
        Equal(@"> \U0001f469\u200d\U0001f469", projected.Rows.Single()); Equal(18, projected.CursorColumn); Equal(0, projected.CursorRow); Equal(3, projected.SourceCursorUtf16Offset);
        var flag = TerminalTextEditorProjection.Create(new("\U0001f1fa\U0001f1f8", 2), 40, 3); Equal(12, flag.CursorColumn);
        var mark = TerminalTextEditorProjection.Create(new("a\u0301b", 1), 40, 3); Equal(@"> a\u0301b", mark.Rows.Single()); Equal(3, mark.CursorColumn);
        var controls = TerminalTextEditorProjection.Create(new("\u001b[31m\\\t\u0000", 0), 40, 3);
        Equal(@"> \u001b[31m\\\t\u0000", controls.Rows.Single()); True(controls.Rows.All(row => row.All(character => character is >= ' ' and <= '~')));
        foreach (var columns in new[] { 1, 2, 3, 8, 40 })
            foreach (var rows in new[] { 1, 2, 3 })
                foreach (var offset in new[] { 0, 2, 3, 5 })
                {
                    var view = TerminalTextEditorProjection.Create(woman with { CursorUtf16Offset = offset }, columns, rows);
                    True(view.Rows.Length <= rows && view.Rows.All(row => row.Length <= columns));
                    True(view.CursorRow >= 0 && view.CursorRow < view.Rows.Length && view.CursorColumn >= 0 && view.CursorColumn < columns);
                    Equal(offset, view.SourceCursorUtf16Offset); Equal(3, woman.CursorUtf16Offset);
                }
        foreach (var cursor in new[] { 0, 1, 2, 3, 4 })
        {
            var lineView = TerminalTextEditorProjection.Create(new("a\r\nb", cursor), 1, 1);
            True(lineView.CursorRow == 0 && lineView.CursorColumn == 0 && lineView.Rows.Length == 1);
        }
        Failure(TerminalTextEditorFailure.InvalidCursor, () => TerminalTextEditorProjection.Create(woman with { CursorUtf16Offset = 1 }, 40, 3));
        Failure(TerminalTextEditorFailure.InvalidUnicode, () => TerminalTextEditorProjection.Create(new("\ud800", 0), 40, 3));
        Failure(TerminalTextEditorFailure.ResourceLimit, () => TerminalTextEditorProjection.Create(new(new string('a', 65_537), 0), 40, 3));
        Failure(TerminalTextEditorFailure.InvalidOptions, () => TerminalTextEditorProjection.Create(woman, 257, 3));
        Failure(TerminalTextEditorFailure.InvalidOptions, () => TerminalTextEditorProjection.Create(woman, 40, 4));
    }

    private static void PasteThresholds()
    {
        var editor = new TerminalTextEditorPasteController();
        editor.HandleInput(new TerminalPaste(new string('a', 1000))); Equal(new string('a', 1000), editor.Snapshot.Text); Equal(0, editor.RegisteredPasteCount);
        editor.Clear(); editor.HandleInput(new TerminalPaste(new string('a', 1001))); Equal("[paste #1 1001 chars]", editor.Snapshot.Text); Equal(new string('a', 1001), editor.GetExpandedText());
        editor.Clear(); editor.HandleInput(new TerminalPaste(new string('\n', 9))); Equal(new string('\n', 9), editor.Snapshot.Text); Equal(0, editor.RegisteredPasteCount);
        editor.Clear(); editor.HandleInput(new TerminalPaste(new string('\n', 10))); Equal("[paste #1 +11 lines]", editor.Snapshot.Text); Equal(new string('\n', 10), editor.GetExpandedText());
        editor.SetText("x"); editor.HandleInput(new TerminalPaste("/" + new string('a', 999))); Equal("x[paste #1 1001 chars]", editor.Snapshot.Text); Equal("x /" + new string('a', 999), editor.GetExpandedText());
        editor.Clear(); editor.HandleInput(new TerminalPaste(new string('\t', 251))); Equal("[paste #1 1004 chars]", editor.Snapshot.Text); Equal(new string(' ', 1004), editor.GetExpandedText());
        editor.Clear(); editor.HandleInput(new TerminalPaste(string.Concat(Enumerable.Repeat("\u001b[106;5u", 10)))); Equal("[paste #1 +11 lines]", editor.Snapshot.Text);
        editor.Clear(); editor.HandleInput(new TerminalPaste(string.Concat(Enumerable.Repeat("\U0001f642", 513)))); Equal("[paste #1 1026 chars]", editor.Snapshot.Text);
    }

    private static void PasteLifecycle()
    {
        var editor = new TerminalTextEditorPasteController(); var a = new string('a', 1001); var b = new string('b', 1002); var c = new string('c', 1003);
        editor.HandleInput(new TerminalPaste(a)); var original = editor.Snapshot;
        editor.HandleInput(new TerminalKey("Left")); Equal(0, editor.Snapshot.CursorUtf16Offset);
        editor.HandleInput(new TerminalKey("Right")); Equal(original.Text.Length, editor.Snapshot.CursorUtf16Offset);
        editor.HandleInput(new TerminalKey("Home")); editor.HandleInput(new TerminalKey("Delete")); Equal("", editor.Snapshot.Text); Equal(1, editor.RegisteredPasteCount); Equal(1001, editor.RetainedPasteCharacters);
        editor.InsertText("[paste #1]"); Equal(a, editor.GetExpandedText()); editor.HandleInput(new TerminalKey("Left")); Equal(0, editor.Snapshot.CursorUtf16Offset);
        editor.HandleInput(new TerminalKey("Right")); editor.HandleInput(new TerminalKey("Backspace")); Equal("", editor.Snapshot.Text); Equal(0, editor.RegisteredPasteCount);
        editor.HandleInput(new TerminalPaste(b)); editor.HandleInput(new TerminalPaste(c)); Equal("[paste #1 1002 chars][paste #2 1003 chars]", editor.Snapshot.Text);
        editor.HandleInput(new TerminalKey("Home")); editor.HandleInput(new TerminalKey("Right")); editor.HandleInput(new TerminalKey("Backspace"));
        Equal(new TerminalTextEditorSnapshot("[paste #1 1003 chars]", 0), editor.Snapshot); Equal(c, editor.GetExpandedText()); Equal(1, editor.RegisteredPasteCount); Equal(1003, editor.RetainedPasteCharacters);
        editor.SetCursor(5); editor.HandleInput(new TerminalKey("Left")); Equal(4, editor.Snapshot.CursorUtf16Offset);
        editor.SetText(editor.Snapshot.Text); Equal(editor.Snapshot.Text, editor.GetExpandedText()); Equal(0, editor.RegisteredPasteCount);
        editor.Clear(); editor.HandleInput(new TerminalPaste(a)); Equal("[paste #1 1001 chars]", editor.Snapshot.Text);
        Equal(new TerminalTextEditorSnapshot("[paste #1 1001 chars]", 21), original);
    }

    private static void PasteLimits()
    {
        var editor = new TerminalTextEditorPasteController(1001); var changes = 0; editor.TextChanged += _ => changes++;
        editor.HandleInput(new TerminalPaste(new string('a', 1001))); var before = editor.Snapshot; var expanded = editor.GetExpandedText();
        foreach (var (failure, operation) in new (TerminalTextEditorFailure, Action)[] {
            (TerminalTextEditorFailure.ResourceLimit, () => editor.InsertText("x")),
            (TerminalTextEditorFailure.ResourceLimit, () => editor.InsertText("[paste #1]")),
            (TerminalTextEditorFailure.ResourceLimit, () => editor.HandleInput(new TerminalPaste(new string('\t', 251)))),
            (TerminalTextEditorFailure.InvalidUnicode, () => editor.SetText("\ud800")) })
        {
            Failure(failure, operation);
            Same(before, editor.Snapshot); Same(expanded, editor.GetExpandedText()); Equal(1, editor.RegisteredPasteCount); Equal(1001, editor.RetainedPasteCharacters); Equal(1, changes);
        }
        editor.HandleInput(new TerminalKey("Home")); editor.HandleInput(new TerminalKey("Delete")); var empty = editor.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => editor.HandleInput(new TerminalPaste(new string('b', 1001)))); Same(empty, editor.Snapshot); Equal(1, editor.RegisteredPasteCount); Equal(2, changes);
        var entries = new TerminalTextEditorPasteController(5000, 1); entries.HandleInput(new TerminalPaste(new string('a', 1001))); var registered = entries.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => entries.HandleInput(new TerminalPaste(new string('b', 1001)))); Same(registered, entries.Snapshot); Equal(1, entries.RegisteredPasteCount);
        entries.Clear(); entries.HandleInput(new TerminalPaste(new string('b', 1001))); Equal("[paste #1 1001 chars]", entries.Snapshot.Text);
        var tiny = new TerminalTextEditorPasteController(11); var initial = tiny.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => tiny.HandleInput(new TerminalPaste(new string('\n', 10)))); Same(initial, tiny.Snapshot); Equal(0, tiny.RegisteredPasteCount);
        var maximum = new TerminalTextEditorPasteController(); var payload = new string('a', 65_536);
        maximum.HandleInput(new TerminalPaste(payload)); Equal(payload, maximum.GetExpandedText()); Equal(65_536, maximum.RetainedPasteCharacters); var full = maximum.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => maximum.InsertText("[paste #1]")); Same(full, maximum.Snapshot); maximum.Clear(); Equal(0, maximum.RetainedPasteCharacters);
        foreach (var count in new[] { 0, 65 }) Failure(TerminalTextEditorFailure.InvalidOptions, () => _ = new TerminalTextEditorPasteController(1001, count));
        var registryLimit = new TerminalTextEditorPasteController(); var linePaste = "x" + new string('\n', 10);
        for (var index = 0; index < 64; index++) registryLimit.HandleInput(new TerminalPaste(linePaste));
        Equal(64, registryLimit.RegisteredPasteCount); var allEntries = registryLimit.Snapshot;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => registryLimit.HandleInput(new TerminalPaste(linePaste))); Same(allEntries, registryLimit.Snapshot);
        registryLimit.HandleInput(new TerminalKey("Backspace")); registryLimit.HandleInput(new TerminalPaste(linePaste));
        Equal(64, registryLimit.RegisteredPasteCount); True(registryLimit.Snapshot.Text.EndsWith("[paste #64 +11 lines]", StringComparison.Ordinal));
    }

    private static void PasteNotifications()
    {
        var editor = new TerminalTextEditorPasteController(); var changes = new List<string>(); editor.TextChanged += changes.Add;
        editor.SetText(""); editor.HandleInput(new TerminalKey("Backspace")); editor.HandleInput(new TerminalKey("Delete"));
        editor.HandleInput(new TerminalPaste(new string('a', 1001))); editor.HandleInput(new TerminalKey("Left")); editor.HandleInput(new TerminalKey("Right"));
        editor.HandleInput(new TerminalKey("Delete")); editor.HandleInput(new TerminalKey("Backspace"));
        editor.HandleInput(new TerminalKey("Backspace", Action: TerminalKeyAction.Release)); editor.HandleInput(new TerminalProtocol("\u001b[I"));
        editor.HandleInput(new TerminalText("")); editor.HandleInput(new TerminalPaste("\u0003"));
        True(changes.SequenceEqual(new[] { "", "", "", "[paste #1 1001 chars]", "[paste #1 1001 chars]", "" }));
        var throwing = new TerminalTextEditorPasteController(); throwing.TextChanged += _ => throw new InvalidOperationException("observer");
        Throws<InvalidOperationException>(() => throwing.HandleInput(new TerminalPaste(new string('x', 1001))));
        Equal("[paste #1 1001 chars]", throwing.Snapshot.Text); Equal(new string('x', 1001), throwing.GetExpandedText()); Equal(1, throwing.RegisteredPasteCount);
        Throws<InvalidOperationException>(() => throwing.Clear()); Equal("", throwing.GetExpandedText()); Equal(0, throwing.RegisteredPasteCount); Equal(0, throwing.RetainedPasteCharacters);
    }

    private static void PasteExpansion()
    {
        var editor = new TerminalTextEditorPasteController(); var a = new string('a', 991); var b = new string('b', 991);
        editor.HandleInput(new TerminalPaste("[paste #2]" + a)); editor.HandleInput(new TerminalPaste(new string('b', 1001)));
        Equal(new string('b', 1001) + a + new string('b', 1001), editor.GetExpandedText());
        editor.Clear(); editor.HandleInput(new TerminalPaste("[paste #1]" + a)); editor.HandleInput(new TerminalPaste("[paste #1]" + b));
        Equal("[paste #1]" + a + "[paste #1]" + b, editor.GetExpandedText());
        editor.Clear(); var literal = string.Concat(Enumerable.Repeat("$&$1$$", 170)); editor.HandleInput(new TerminalPaste(literal)); editor.InsertText("[paste #1 +123 lines]"); Equal(literal + literal, editor.GetExpandedText());
        editor.Clear(); editor.HandleInput(new TerminalPaste(new string('a', 1001))); editor.HandleInput(new TerminalPaste(new string('b', 1002))); editor.InsertText("[paste #99][paste #2]");
        editor.HandleInput(new TerminalKey("Home")); editor.HandleInput(new TerminalKey("Right")); editor.HandleInput(new TerminalKey("Backspace"));
        Equal("[paste #1 1002 chars][paste #98undefined][paste #1undefined]", editor.Snapshot.Text); Equal(new string('b', 1002) + "[paste #98undefined][paste #1undefined]", editor.GetExpandedText());
    }

    private static void UndoGrouping()
    {
        var editor = new TerminalTextEditorPasteController();
        foreach (var c in "hello world!") editor.HandleInput(new TerminalText(c.ToString()));
        Equal(2, editor.UndoDepth); UndoKey(editor); Equal("hello", editor.Snapshot.Text);
        UndoKey(editor); Equal("", editor.Snapshot.Text); UndoKey(editor); Equal(0, editor.UndoDepth);
        foreach (var whitespace in new[] { '\u0009', '\u000b', '\u000c', '\u0020', '\u00a0', '\u1680', '\u2000', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff' })
        {
            editor.Reset(); editor.HandleInput(new TerminalText("A")); editor.HandleInput(new TerminalText(whitespace.ToString())); editor.HandleInput(new TerminalText("B"));
            Equal(2, editor.UndoDepth); UndoKey(editor); Equal("A", editor.Snapshot.Text);
        }
        editor.Reset(); editor.HandleInput(new TerminalText("A")); editor.HandleInput(new TerminalText("\u0085")); editor.HandleInput(new TerminalText("B"));
        Equal(1, editor.UndoDepth); UndoKey(editor); Equal("", editor.Snapshot.Text);
        foreach (var noOp in new[] { "Delete", "End" })
        {
            editor.Reset(); editor.HandleInput(new TerminalText("a")); editor.HandleInput(new TerminalKey(noOp)); editor.HandleInput(new TerminalText("b"));
            Equal(2, editor.UndoDepth); UndoKey(editor); Equal("a", editor.Snapshot.Text); UndoKey(editor); Equal("", editor.Snapshot.Text);
        }
        editor.Reset(); editor.HandleInput(new TerminalText("a")); editor.HandleInput(new TerminalPaste("")); editor.InsertText("");
        editor.HandleInput(new TerminalText("b")); Equal(1, editor.UndoDepth); UndoKey(editor); Equal("", editor.Snapshot.Text);
        editor.HandleInput(new TerminalText("ab")); editor.HandleInput(new TerminalKey("Enter", TerminalModifiers.Shift)); editor.HandleInput(new TerminalText("c"));
        UndoKey(editor); Equal("ab\n", editor.Snapshot.Text); UndoKey(editor); Equal("ab", editor.Snapshot.Text); UndoKey(editor); Equal("", editor.Snapshot.Text);
    }

    private static void UndoState()
    {
        var editor = new TerminalTextEditorPasteController(); var a = new string('a', 1001); var b = new string('b', 1002);
        editor.HandleInput(new TerminalPaste(a)); editor.HandleInput(new TerminalPaste(b));
        var full = editor.Snapshot; var expanded = editor.GetExpandedText();
        editor.HandleInput(new TerminalKey("Home")); editor.HandleInput(new TerminalKey("Right")); var cursor = editor.Snapshot;
        editor.HandleInput(new TerminalKey("Backspace")); Equal(1, editor.RegisteredPasteCount);
        UndoKey(editor); Equal(cursor, editor.Snapshot); Equal(expanded, editor.GetExpandedText()); Equal(2, editor.RegisteredPasteCount); Equal(2003, editor.RetainedPasteCharacters);
        editor.SetCursor(editor.Snapshot.Text.Length); editor.HandleInput(new TerminalPaste(new string('c', 1003)));
        True(editor.Snapshot.Text.EndsWith("[paste #3 1003 chars]", StringComparison.Ordinal)); UndoKey(editor); Equal(full, editor.Snapshot);
        editor.SetText("replacement"); UndoKey(editor); Equal(full, editor.Snapshot); Equal(expanded, editor.GetExpandedText());
        editor.HandleInput(new TerminalKey("Home")); editor.HandleInput(new TerminalKey("Delete")); Equal(2, editor.RegisteredPasteCount);
        UndoKey(editor); Equal(new TerminalTextEditorSnapshot(full.Text, 0), editor.Snapshot); Equal(expanded, editor.GetExpandedText());
        editor.Reset(); editor.SetText("\U0001f469\U0001f469"); editor.SetCursor(2); editor.HandleInput(new TerminalText("\u200d"));
        var seam = editor.Snapshot; Equal(3, seam.CursorUtf16Offset); editor.HandleInput(new TerminalKey("Backspace"));
        UndoKey(editor); Equal(seam, editor.Snapshot); UndoKey(editor); Equal(new TerminalTextEditorSnapshot("\U0001f469\U0001f469", 2), editor.Snapshot);
        UndoKey(editor); Equal(new TerminalTextEditorSnapshot("", 0), editor.Snapshot);
    }

    private static void UndoRejections()
    {
        foreach (var operation in new Action<TerminalTextEditorPasteController>[] {
            editor => editor.InsertText(new string('x', 10)), editor => editor.HandleInput(new TerminalPaste(new string('x', 10))),
            editor => editor.SetText("\ud800"), editor => editor.HandleInput(new TerminalText("\udc00")), editor => editor.SetCursor(2) })
        {
            var editor = new TerminalTextEditorPasteController(3); var changes = 0; editor.TextChanged += _ => changes++;
            editor.HandleInput(new TerminalText("a")); var before = editor.Snapshot; var depth = editor.UndoDepth; var retained = editor.RetainedUndoCharacters;
            Throws<TerminalTextEditorException>(() => operation(editor)); Same(before, editor.Snapshot); Equal(depth, editor.UndoDepth); Equal(retained, editor.RetainedUndoCharacters); Equal(1, changes);
            editor.HandleInput(new TerminalText("b")); Equal(depth, editor.UndoDepth); UndoKey(editor); Equal("", editor.Snapshot.Text);
        }
        var full = new TerminalTextEditorPasteController(1001, 1); full.HandleInput(new TerminalPaste(new string('a', 1001)));
        var snapshot = full.Snapshot; var expanded = full.GetExpandedText(); var beforeDepth = full.UndoDepth; var beforeRetained = full.RetainedUndoCharacters;
        foreach (var operation in new Action[] { () => full.InsertText("[paste #1]"), () => full.HandleInput(new TerminalPaste(new string('b', 1001))), () => full.SetCursor(-1) })
        {
            Throws<TerminalTextEditorException>(operation); Same(snapshot, full.Snapshot); Same(expanded, full.GetExpandedText()); Equal(1, full.RegisteredPasteCount);
            Equal(beforeDepth, full.UndoDepth); Equal(beforeRetained, full.RetainedUndoCharacters);
        }
        UndoKey(full); Equal("", full.Snapshot.Text); Equal(0, full.RegisteredPasteCount);
    }

    private static void UndoBounds()
    {
        var editor = new TerminalTextEditorPasteController(); var before = new List<TerminalTextEditorSnapshot>();
        for (var index = 0; index < 70; index++) { before.Add(editor.Snapshot); editor.SetText(index.ToString(CultureInfo.InvariantCulture)); }
        Equal(64, editor.UndoDepth);
        foreach (var snapshot in before.TakeLast(64).Reverse()) { UndoKey(editor); Equal(snapshot, editor.Snapshot); }
        Equal(0, editor.UndoDepth); Equal(0, editor.RetainedUndoCharacters); var last = editor.Snapshot; UndoKey(editor); Same(last, editor.Snapshot);
        editor.Reset(); var payload = new string('x', 65_536);
        for (var index = 0; index < 12; index++)
        {
            editor.HandleInput(new TerminalPaste(payload)); editor.Clear();
            True(editor.RetainedUndoCharacters <= TerminalTextEditorPasteController.MaximumRetainedUndoCharacters); True(editor.UndoDepth <= 64);
        }
        True(editor.UndoDepth < 24 && editor.UndoDepth > 0);
        while (editor.UndoDepth != 0) { UndoKey(editor); True(editor.RetainedUndoCharacters <= TerminalTextEditorPasteController.MaximumRetainedUndoCharacters); }
        Equal(0, editor.RetainedUndoCharacters); editor.Reset(); editor.HandleInput(new TerminalPaste(payload)); editor.Clear();
        editor.Reset(); Equal(0, editor.UndoDepth); Equal(0, editor.RetainedUndoCharacters); Equal(0, editor.RegisteredPasteCount); Equal("", editor.GetExpandedText());
        editor.HandleInput(new TerminalText("new prompt")); UndoKey(editor); Equal("", editor.Snapshot.Text); UndoKey(editor); Equal("", editor.Snapshot.Text);
    }

    private static void UndoBindingsAndEvents()
    {
        var raw = new TerminalInputDecoder().Feed("\u001f"); Equal(new TerminalKey("-", TerminalModifiers.Control), raw.Single());
        var coded = new TerminalInputDecoder().Feed("\u001b[45;5u"); Equal(raw.Single(), coded.Single());
        var underscore = new TerminalInputDecoder().Feed("\u001b[95;5u"); Equal(new TerminalKey("_", TerminalModifiers.Control), underscore.Single());
        var editor = new TerminalTextEditorPasteController(); var changes = new List<string>(); editor.TextChanged += changes.Add;
        UndoKey(editor); Equal(0, changes.Count); editor.HandleInput(new TerminalPaste("")); Equal(0, editor.UndoDepth);
        editor.HandleInput(new TerminalPaste("\u0003")); Equal(1, editor.UndoDepth); Equal(0, changes.Count); UndoKey(editor); True(changes.SequenceEqual(new[] { "" }));
        editor.HandleInput(new TerminalText("a"));
        foreach (var input in new TerminalInputEvent[] { underscore.Single(), new TerminalKey("z", TerminalModifiers.Control), new TerminalKey("-", TerminalModifiers.Control, TerminalKeyAction.Release) })
            Equal(TerminalEditorInputDisposition.Ignored, editor.HandleInput(input));
        editor.HandleInput(raw.Single()); Equal("", editor.Snapshot.Text); Equal(0, editor.UndoDepth);
        var throwing = new TerminalTextEditorPasteController(); throwing.TextChanged += _ => throw new InvalidOperationException("observer");
        Throws<InvalidOperationException>(() => throwing.HandleInput(new TerminalPaste(new string('x', 1001)))); Equal(1, throwing.UndoDepth);
        Throws<InvalidOperationException>(() => UndoKey(throwing)); Equal(0, throwing.UndoDepth); Equal(0, throwing.RegisteredPasteCount); Equal("", throwing.GetExpandedText());
        Throws<InvalidOperationException>(() => throwing.SetText("next")); True(throwing.UndoDepth > 0);
        Throws<InvalidOperationException>(() => throwing.Reset()); Equal(0, throwing.UndoDepth); Equal(0, throwing.RetainedUndoCharacters); Equal("", throwing.Snapshot.Text);
    }

    private static void UndoKey(TerminalTextEditorPasteController editor) => editor.HandleInput(new TerminalKey("-", TerminalModifiers.Control));

    private static void ControlsAliases()
    {
        Equal(new TerminalKey("\ue005", TerminalModifiers.Shift), new TerminalInputDecoder().Feed("\u001b[57349;2u").Single());
        Equal(new TerminalKey("Delete", TerminalModifiers.Shift), new TerminalInputDecoder().Feed("\u001b[57426;2u").Single());
        var printable = new TerminalTextEditorPasteController();
        foreach (var input in new TerminalInputDecoder().Feed("\u001b[65u\u001b[128578;2u\u001b[57349;2u")) printable.HandleInput(input);
        Equal("A\U0001f642\ue005", printable.Snapshot.Text); UndoKey(printable); Equal("", printable.Snapshot.Text);
        Equal(new TerminalKey("j", TerminalModifiers.Control), new TerminalInputDecoder().Feed("\n").Single());
        Equal(new TerminalKey("Enter"), new TerminalInputDecoder().Feed("\r").Single());
        Equal(new TerminalKey("Enter", TerminalModifiers.Alt), new TerminalInputDecoder().Feed("\u001b\r").Single());
        var editor = new TerminalTextEditorPasteController(); editor.SetText("abc\ndef");
        foreach (var key in new[] { new TerminalKey("a", TerminalModifiers.Control), new TerminalKey("Home", TerminalModifiers.Control) })
        { editor.SetCursor(7); editor.HandleInput(key); Equal(4, editor.Snapshot.CursorUtf16Offset); }
        editor.HandleInput(new TerminalKey("f", TerminalModifiers.Control)); Equal(5, editor.Snapshot.CursorUtf16Offset);
        editor.HandleInput(new TerminalKey("b", TerminalModifiers.Control)); Equal(4, editor.Snapshot.CursorUtf16Offset);
        editor.HandleInput(new TerminalKey("e", TerminalModifiers.Control)); Equal(7, editor.Snapshot.CursorUtf16Offset);
        editor.HandleInput(new TerminalKey("d", TerminalModifiers.Control)); Equal("abc\ndef", editor.Snapshot.Text);
        editor.HandleInput(new TerminalKey("Backspace", TerminalModifiers.Shift)); Equal("abc\nde", editor.Snapshot.Text);
        editor.HandleInput(new TerminalKey("a", TerminalModifiers.Control)); editor.HandleInput(new TerminalKey("Delete", TerminalModifiers.Shift)); Equal("abc\ne", editor.Snapshot.Text);
        foreach (var key in new[] { new TerminalKey("j", TerminalModifiers.Control), new TerminalKey("Enter", TerminalModifiers.Alt), new TerminalKey("Enter", TerminalModifiers.Shift) })
        { editor.Reset(); editor.HandleInput(new TerminalText("a")); editor.HandleInput(key); Equal("a\n", editor.Snapshot.Text); UndoKey(editor); Equal("a", editor.Snapshot.Text); }
        foreach (var key in new[] { new TerminalKey("u", TerminalModifiers.Control), new TerminalKey("k", TerminalModifiers.Control), new TerminalKey("y", TerminalModifiers.Control), new TerminalKey("y", TerminalModifiers.Alt) })
        { var before = editor.Snapshot; Equal(TerminalEditorInputDisposition.Ignored, editor.HandleInput(key with { Action = TerminalKeyAction.Release })); Same(before, editor.Snapshot); }
    }

    private static void ControlsKills()
    {
        var editor = new TerminalTextEditorPasteController(); editor.SetText("ab\ncd\nef"); editor.SetCursor(0);
        ControlKey(editor, "k"); ControlKey(editor, "k"); ControlKey(editor, "k"); Equal("\nef", editor.Snapshot.Text);
        Equal(1, editor.KillRingCount); Equal(5, editor.RetainedKillCharacters); ControlKey(editor, "y"); Equal("ab\ncd\nef", editor.Snapshot.Text);
        UndoKey(editor); Equal("\nef", editor.Snapshot.Text); UndoKey(editor); Equal("cd\nef", editor.Snapshot.Text); Equal(5, editor.RetainedKillCharacters);
        editor.Reset(); ControlKey(editor, "y"); Equal("ab\ncd", editor.Snapshot.Text); editor.SetText("ab\ncd\nef");
        ControlKey(editor, "u"); ControlKey(editor, "u"); ControlKey(editor, "u"); Equal("ab\n", editor.Snapshot.Text);
        ControlKey(editor, "y"); Equal("ab\ncd\nef", editor.Snapshot.Text); Equal(2, editor.KillRingCount);
        editor.Reset(); var payload = new string('x', 1001); editor.HandleInput(new TerminalPaste(payload));
        ControlKey(editor, "u"); Equal(1, editor.RegisteredPasteCount); Equal("", editor.Snapshot.Text); ControlKey(editor, "y"); Equal(payload, editor.GetExpandedText());
        editor.SetText(""); ControlKey(editor, "y"); Equal("[paste #1 1001 chars]", editor.GetExpandedText()); Equal(0, editor.RegisteredPasteCount);
    }

    private static void ControlsYank()
    {
        var editor = TwoKills("A", "BBB"); var changes = new List<string>(); editor.TextChanged += changes.Add;
        ControlKey(editor, "y"); var depth = editor.UndoDepth; editor.HandleInput(new TerminalKey("y", TerminalModifiers.Alt));
        True(changes.SequenceEqual(new[] { "BBB", "", "A" })); Equal(depth + 1, editor.UndoDepth); Equal("A", editor.Snapshot.Text);
        UndoKey(editor); Equal("BBB", editor.Snapshot.Text); ControlKey(editor, "y"); Equal("BBBA", editor.Snapshot.Text);
        editor.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)); Equal("BBBBBB", editor.Snapshot.Text);
        editor.SetText(""); ControlKey(editor, "y"); var before = editor.Snapshot;
        ControlKey(editor, "k"); editor.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)); Equal("A", editor.Snapshot.Text);
        UndoKey(editor); Equal(before, editor.Snapshot); editor.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)); Equal(before, editor.Snapshot);
        editor.Reset(); ControlKey(editor, "y"); Equal("A", editor.Snapshot.Text);
    }

    private static void ControlsBounds()
    {
        var editor = new TerminalTextEditorPasteController(); var entries = Enumerable.Range(0, 70).Select(index => "entry-" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        foreach (var entry in entries) { editor.SetText(entry); ControlKey(editor, "u"); }
        Equal(64, editor.KillRingCount); Equal(entries.TakeLast(64).Sum(value => value.Length), editor.RetainedKillCharacters);
        editor.Reset(); ControlKey(editor, "y"); Equal(entries[^1], editor.Snapshot.Text);
        foreach (var entry in entries.TakeLast(64).Reverse().Skip(1)) { editor.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)); Equal(entry, editor.Snapshot.Text); }
        editor.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)); Equal(entries[^1], editor.Snapshot.Text);
        var maximum = new TerminalTextEditorPasteController();
        for (var index = 0; index < 17; index++) { maximum.SetText(new string((char)('A' + index), 65_536)); ControlKey(maximum, "u"); }
        Equal(16, maximum.KillRingCount); Equal(1_048_576, maximum.RetainedKillCharacters); maximum.Reset(); ControlKey(maximum, "y"); Equal(new string('Q', 65_536), maximum.Snapshot.Text);
        var small = TwoKills("aaaa", "b", 4); small.SetText("xxx"); ControlKey(small, "y"); var original = small.Snapshot;
        var depth = small.UndoDepth; var retained = small.RetainedUndoCharacters; var notifications = 0; small.TextChanged += _ => notifications++;
        for (var count = 0; count < 2; count++)
        {
            Failure(TerminalTextEditorFailure.ResourceLimit, () => small.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)));
            Same(original, small.Snapshot); Equal(depth, small.UndoDepth); Equal(retained, small.RetainedUndoCharacters); Equal(5, small.RetainedKillCharacters); Equal(0, notifications);
        }
        small.Reset(); ControlKey(small, "y"); Equal("b", small.Snapshot.Text);
        var grouped = new TerminalTextEditorPasteController(3); grouped.SetText("abc"); ControlKey(grouped, "u"); grouped.HandleInput(new TerminalText("a"));
        var typed = grouped.Snapshot; var groupedDepth = grouped.UndoDepth;
        Failure(TerminalTextEditorFailure.ResourceLimit, () => ControlKey(grouped, "y")); Same(typed, grouped.Snapshot); Equal(groupedDepth, grouped.UndoDepth);
        grouped.HandleInput(new TerminalText("b")); Equal(groupedDepth, grouped.UndoDepth); UndoKey(grouped); Equal("", grouped.Snapshot.Text);
        var marker = new TerminalTextEditorPasteController(1001); marker.HandleInput(new TerminalPaste(new string('x', 1001))); ControlKey(marker, "u");
        marker.InsertText("A"); ControlKey(marker, "u"); marker.InsertText("X"); ControlKey(marker, "y"); var draft = marker.Snapshot; var expanded = marker.GetExpandedText();
        Failure(TerminalTextEditorFailure.ResourceLimit, () => marker.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)));
        Same(draft, marker.Snapshot); Same(expanded, marker.GetExpandedText()); Equal(1, marker.RegisteredPasteCount); Equal(1001, marker.RetainedPasteCharacters);
    }

    private static void ControlsObservers()
    {
        var killed = new TerminalTextEditorPasteController(); killed.SetText("abc"); killed.TextChanged += _ => throw new InvalidOperationException("observer");
        Throws<InvalidOperationException>(() => ControlKey(killed, "u")); Equal("", killed.Snapshot.Text); Equal(1, killed.KillRingCount); Equal(3, killed.RetainedKillCharacters);
        var firstPhase = TwoKills("A", "BB"); ControlKey(firstPhase, "y"); var oldDepth = firstPhase.UndoDepth;
        Action<string> firstThrow = value => { Equal(value, firstPhase.Snapshot.Text); throw new InvalidOperationException("first phase"); };
        firstPhase.TextChanged += firstThrow; Throws<InvalidOperationException>(() => firstPhase.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)));
        Equal("", firstPhase.Snapshot.Text); Equal(oldDepth + 1, firstPhase.UndoDepth); Equal(2, firstPhase.KillRingCount);
        firstPhase.TextChanged -= firstThrow; firstPhase.Reset(); ControlKey(firstPhase, "y"); Equal("BB", firstPhase.Snapshot.Text);
        var secondPhase = TwoKills("A", "BB"); ControlKey(secondPhase, "y"); var observed = new List<string>();
        Action<string> secondThrow = value => { Equal(value, secondPhase.Snapshot.Text); observed.Add(value); if (value == "A") throw new InvalidOperationException("second phase"); };
        secondPhase.TextChanged += secondThrow; Throws<InvalidOperationException>(() => secondPhase.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)));
        True(observed.SequenceEqual(new[] { "", "A" })); Equal("A", secondPhase.Snapshot.Text);
        secondPhase.TextChanged -= secondThrow; secondPhase.Reset(); ControlKey(secondPhase, "y"); Equal("A", secondPhase.Snapshot.Text);
    }

    private static TerminalTextEditorPasteController TwoKills(string first, string second, int maximum = 65_536)
    { var editor = new TerminalTextEditorPasteController(maximum); editor.SetText(first); ControlKey(editor, "u"); editor.SetText(second); ControlKey(editor, "u"); return editor; }
    private static void ControlKey(TerminalTextEditorPasteController editor, string key) => editor.HandleInput(new TerminalKey(key, TerminalModifiers.Control));

    private static void ComparatorControls()
    {
        using var expected = JsonDocument.Parse("{\"steps\":[{\"cursor\":{\"line\":0,\"col\":2},\"changes\":[\"x\"]}]}");
        foreach (var text in new[] { "{\"steps\":[{\"cursor\":{\"line\":0,\"col\":1},\"changes\":[\"x\"]}]}", "{\"steps\":[{\"cursor\":{\"line\":0,\"col\":2}}]}", "{\"steps\":[{\"cursor\":{\"line\":0,\"col\":2},\"changes\":[\"x\"],\"extra\":null}]}" })
        { using var actual = JsonDocument.Parse(text); var differences = new List<object>(); Difference(expected.RootElement, actual.RootElement, "$", differences); True(differences.Count > 0); }
        var equal = new List<object>(); Difference(expected.RootElement, expected.RootElement, "$", equal); Equal(0, equal.Count);
    }

    internal static void Difference(JsonElement expected, JsonElement actual, string path, List<object> differences)
    {
        if (expected.ValueKind != actual.ValueKind) { differences.Add(new { path, expected, actual }); return; }
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var names = expected.EnumerateObject().Select(value => value.Name).Concat(actual.EnumerateObject().Select(value => value.Name)).Distinct(StringComparer.Ordinal);
            foreach (var name in names)
                if (!expected.TryGetProperty(name, out var left) || !actual.TryGetProperty(name, out var right)) differences.Add(new { path = path + "." + name, reason = "missing/extra property" });
                else Difference(left, right, path + "." + name, differences);
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength()) differences.Add(new { path, expected, actual, reason = "ordered array length" });
            for (var index = 0; index < Math.Min(expected.GetArrayLength(), actual.GetArrayLength()); index++) Difference(expected[index], actual[index], path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", differences);
        }
        else if (!JsonElement.DeepEquals(expected, actual)) differences.Add(new { path, expected, actual });
    }

    private static void Apply(TerminalTextEditor editor, IEnumerable<TerminalInputEvent> events)
    {
        foreach (var input in events)
        {
            if (input is TerminalText text) editor.Insert(text.Text);
            else if (input is TerminalPaste paste) editor.Insert(paste.Text);
            else if (input is TerminalKey { Action: not TerminalKeyAction.Release, Modifiers: TerminalModifiers.None } key)
                switch (key.Key)
                {
                    case "Left": editor.MoveLeft(); break; case "Right": editor.MoveRight(); break;
                    case "Home": editor.MoveLineStart(); break; case "End": editor.MoveLineEnd(); break;
                    case "Backspace": editor.Backspace(); break; case "Delete": editor.Delete(); break;
                }
            else if (input is TerminalKey { Key: "Enter", Modifiers: TerminalModifiers.Shift, Action: not TerminalKeyAction.Release }) editor.Insert("\n");
        }
    }

    private static void AssertEdit(string before, int cursor, Func<TerminalTextEditor, bool> action, string text, int offset)
    { var editor = new TerminalTextEditor(); editor.SetText(before, cursor); True(action(editor)); Equal(new TerminalTextEditorSnapshot(text, offset), editor.Snapshot); }
    private static void Failure(TerminalTextEditorFailure failure, Action action)
    { try { action(); } catch (TerminalTextEditorException error) { Equal(failure, error.Failure); return; } throw new Exception("Expected " + failure); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Same(object expected, object actual) => True(ReferenceEquals(expected, actual));
    private static void True(bool value) { if (!value) throw new Exception("Editor invariant differs."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
    private sealed class FixedClock : TimeProvider { public override long TimestampFrequency => 1_000; public override long GetTimestamp() => 0; }
}
