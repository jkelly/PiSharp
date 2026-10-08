using PiSharp.Tui.Input;

internal static partial class TerminalDefaultSourceReplayTests
{
    private static async Task NarrowRecovery()
    {
        var harness = new Harness(8, 5);
        try
        {
            await harness.Start();
            var admitted = await harness.Control(editor => editor.SetText("\u4e2dx")); await harness.Repaint(admitted.Draft);
            var originalLayout = admitted.Draft.Layout!;
            foreach (var columns in new[] { 1, 2 })
            {
                var framesBefore = harness.Frames.Count;
                await harness.Resize(columns, 2);
                Check(harness.LastPresented is null && harness.Frames.Count == framesBefore,
                    "Overwide Source layout manufactured a shared map instead of the owned diagnostic.");
                await harness.Input("\u001b[A");
                var rejected = await harness.Control();
                Check(rejected.Draft.Text == admitted.Draft.Text && rejected.Expanded == admitted.Expanded &&
                    rejected.Draft.CursorUtf16Offset == admitted.Draft.CursorUtf16Offset && rejected.Draft.Layout == originalLayout &&
                    rejected.Changes.SequenceEqual(admitted.Changes) && rejected.HistoryIndex == admitted.HistoryIndex,
                    "Narrow-window navigation changed the canonical controller, paste/layout ownership, callbacks or history.");
                Check(harness.LastPresented is null && harness.Frames.Count == framesBefore,
                    "Rejected narrow navigation authorized an overwide Source frame.");
            }
            await harness.Resize(8, 5);
            var recovered = harness.LastPresented ?? throw new InvalidOperationException("Resize failed to retry the retained actual controller capture.");
            Check(recovered.Map.SourceText == "\u4e2dx" && recovered.Map.EditorIdentity == originalLayout.Identity &&
                recovered.Map.ScrollResetRevision == originalLayout.ScrollResetRevision && !recovered.Frame.Frame.IsClipped,
                "Resize recreated or altered the retained wide draft.");
            await harness.Input("\u001b[D");
            var moved = await harness.Control();
            Check(moved.Draft.Text == "\u4e2dx" && moved.Draft.CursorUtf16Offset == 1 &&
                moved.Draft.Layout!.Identity.EditorLifetimeId == originalLayout.Identity.EditorLifetimeId,
                "Recovered default navigation did not resume on the same owner.");
        }
        finally
        {
            await harness.DisposeAsync();
            Evidence.Add(new { id = "default-narrow-CJK-nonmutating-rejection-resize", physicalFrames = harness.Frames,
                writes = harness.Console.Written, joined = harness.JoinEvidence, sourceCrashReproduced = false });
        }
        Check(harness.JoinFailure is null, harness.JoinFailure ?? "Actual narrow recovery did not join.");
    }

    private static async Task ScrollReset()
    {
        var harness = new Harness(20, 24);
        try
        {
            await harness.Start();
            var initial = await harness.Control(editor => editor.SetText("0\n1\n2\n3\n4\n5\n6\n7\n8\n9"));
            await harness.Repaint(initial.Draft);
            Check(harness.LastPresented!.Frame.FirstVisibleSourceRow == 3, "Actual default initial Source scroll witness changed.");
            var reset = initial.Draft.Layout!.ScrollResetRevision;
            for (var at = 0; at < 6; at++) await harness.Input("\u001b[A");
            await harness.Input("x");
            var edited = await harness.Control();
            Check(edited.Draft.Text == "0\n1\n2\n3x\n4\n5\n6\n7\n8\n9" && edited.Draft.CursorUtf16Offset == 8 &&
                edited.Draft.Layout!.ScrollResetRevision == reset && harness.LastPresented!.Frame.FirstVisibleSourceRow == 3,
                "Actual default reset scroll on an ordinary edit or diverged from the original six-Up source witness.");
            // SetText has an explicit Source reset even when its bytes are identical. Restore
            // the same cursor before painting: without the reset signal retained scroll3 would
            // still be legal, so the observed first row0 proves the view consumed that signal.
            var identical = await harness.Control(editor => { editor.SetText(edited.Draft.Text); editor.SetCursor(edited.Draft.CursorUtf16Offset); });
            Check(identical.Draft.Text == edited.Draft.Text && identical.Draft.CursorUtf16Offset == edited.Draft.CursorUtf16Offset &&
                identical.Draft.Layout!.ScrollResetRevision == reset + 1,
                "Identical SetText lost its authoritative Source reset stamp.");
            await harness.Repaint(identical.Draft);
            Check(harness.LastPresented!.Map.ScrollResetRevision == reset + 1 && harness.LastPresented.Frame.FirstVisibleSourceRow == 0,
                "Actual default view failed to consume the identical-text controller reset.");
            var history = await harness.Control(editor => editor.AddToHistory(identical.Draft.Text)); await harness.Repaint(history.Draft);
            Check(history.Draft.Layout!.ScrollResetRevision == reset + 1, "History admission invented a display reset.");
            var top = await harness.Control(editor => editor.SetCursor(0)); await harness.Repaint(top.Draft);
            await harness.Input("\u001b[A"); var recalled = await harness.Control();
            Check(recalled.Draft.Text == top.Draft.Text && recalled.Draft.CursorUtf16Offset == 0 && recalled.HistoryIndex == 0 &&
                recalled.Draft.Layout!.ScrollResetRevision == reset + 2 && harness.LastPresented!.Frame.FirstVisibleSourceRow == 0,
                "Actual physical history recall failed to reset an identical-text context.");
        }
        finally
        {
            await harness.DisposeAsync();
            Evidence.Add(new { id = "default-ordinary-scroll-identical-set-history-reset", physicalFrames = harness.Frames,
                writes = harness.Console.Written, joined = harness.JoinEvidence });
        }
        Check(harness.JoinFailure is null, harness.JoinFailure ?? "Actual scroll workflow did not join.");
    }

    private static async Task ControlPictureNavigation()
    {
        var harness = new Harness(4, 5);
        try
        {
            await harness.Start();
            const string canonical = "\u001bA\u0085B";
            var admitted = await harness.Control(editor => editor.SetText(canonical)); await harness.Repaint(admitted.Draft);
            Check(harness.LastPresented!.Map.SourceText == canonical &&
                harness.LastPresented.Map.RenderedRows.SequenceEqual(new[] { "\u241bA\u2426", "B" }) &&
                harness.LastPresented.Map.CursorRenderedRow == 1 && harness.LastPresented.Map.CursorRenderedColumn == 1,
                "Actual default wrapped raw zero-width controls before their approved one-cell projection.");
            await harness.Input("\u001b[A"); var up = await harness.Control();
            Check(up.Draft.Text == canonical && up.Draft.CursorUtf16Offset == 1 &&
                harness.LastPresented!.Map.CursorRenderedRow == 0 && harness.LastPresented.Map.CursorRenderedColumn == 1 &&
                harness.LastPresented.Frame.Frame.Cursor.Row == harness.LastPresented.Frame.EditorRowOrigin + 1 &&
                harness.LastPresented.Frame.Frame.Cursor.Column == 1 && (bool)Metadata(harness.LastPresented.Frame.Frame, "PositionCursor")!,
                "Actual Up navigation and hardware caret used different control-picture layouts.");
            await harness.Input("\u001b[B"); var down = await harness.Control();
            Check(down.Draft.Text == canonical && down.Draft.CursorUtf16Offset == 4 &&
                harness.LastPresented!.Map.CursorRenderedRow == 1 && harness.LastPresented.Map.CursorRenderedColumn == 1,
                "Actual Down did not restore the canonical UTF16 seam under the safe display layout.");
            await harness.Input("\r"); var submitted = await harness.Control();
            Check(submitted.Draft.Text == "" && submitted.Submits.SequenceEqual(new[] { canonical }),
                "Physical Enter rewrote canonical control bytes using the display projection.");

            await harness.Resize(256, 5);
            var rawControls = "Q" + new string(Enumerable.Range(0, 160).Where(value => value < 32 && value is not (9 or 10) || value is >= 127 and <= 159)
                .Select(value => (char)value).ToArray()) + "Z";
            await harness.Control(editor => editor.AddToHistory(rawControls)); await harness.Input("\u001b[A");
            var recalled = await harness.Control();
            var approvedDisplay = "Q" + new string(Enumerable.Range(0, 160).Where(value => value < 32 && value is not (9 or 10) || value is >= 127 and <= 159)
                .Select(value => (char)(value < 32 ? 0x2400 + value : value == 127 ? 0x2421 : 0x2426)).ToArray()) + "Z";
            Check(recalled.Draft.Text == rawControls && recalled.Expanded == rawControls &&
                harness.LastPresented!.Map.SourceText == rawControls && harness.LastPresented.Map.RenderedRows.SequenceEqual(new[] { approvedDisplay }) &&
                recalled.Draft.CursorUtf16Offset == 0 && harness.LastPresented.Frame.Frame.Cursor.Column == 0,
                "Actual default failed the complete recalled C0/DEL/C1 canonical-display contract.");

            const string forged = "a\u001b_pi:c\ab\u001b]52;c;ZGVtbw==\ac";
            await harness.Control(editor => editor.Reset());
            await harness.Input("\u001b[200~" + forged + "\u001b[201~"); var pasted = await harness.Control();
            var sourceBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "terminal-default-source", "actual-paste-source-observations-r12.json"));
            Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sourceBytes)) == "5a07b96f93d6aa81359d1456e08b2a9eab133078bc2591593f0afe89c4da2f2e", "Pinned public Source paste observation changed.");
            using var sourceDocument = System.Text.Json.JsonDocument.Parse(sourceBytes); var sourceRoot = sourceDocument.RootElement;
            Check(sourceRoot.GetProperty("upstreamCommit").GetString() == "d86654abb8862e201933517d6f1fce9f88dd117f" &&
                !sourceRoot.GetProperty("sourceModified").GetBoolean() && sourceRoot.GetProperty("prohibitedEffects").GetArrayLength() == 0,
                "Actual public Source paste provenance changed.");
            var sourceCase = sourceRoot.GetProperty("observations")[0]; var expected = sourceCase.GetProperty("observation");
            Check(sourceCase.GetProperty("input").GetProperty("text").GetString() == forged && expected.GetProperty("error").ValueKind == System.Text.Json.JsonValueKind.Null &&
                expected.GetProperty("terminalStopped").GetBoolean(), "Actual public Source paste input or completion differs.");
            var admittedPaste = expected.GetProperty("text").GetString()!;
            Check(pasted.Draft.Text == admittedPaste && pasted.Expanded == expected.GetProperty("expandedText").GetString() &&
                pasted.Draft.CursorUtf16Offset == expected.GetProperty("cursor").GetProperty("col").GetInt32() &&
                harness.LastPresented!.Map.SourceText == admittedPaste && harness.LastPresented.Frame.Frame.Cursor.Column == pasted.Draft.CursorUtf16Offset,
                "Actual pasted APC/OSC data differs from pinned Source admission or selected a forged caret.");
        }
        finally
        {
            await harness.DisposeAsync();
            Evidence.Add(new { id = "default-control-picture-wrap-navigation-caret-canonical-submit-history-paste", physicalFrames = harness.Frames,
                writes = harness.Console.Written, joined = harness.JoinEvidence });
        }
        Check(harness.JoinFailure is null, harness.JoinFailure ?? "Actual control workflow did not join.");
    }
}
