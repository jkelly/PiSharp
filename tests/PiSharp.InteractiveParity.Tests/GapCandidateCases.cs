#pragma warning disable PISHARP_EXTUI001 // Cover actual custom component overlay and blocked lifetimes.
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Extensions;
using static Expect;

internal static class GapCandidateCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("gap.changelog-prerelease-build-and-native-revision", Sync(() =>
        {
            var entries = new[] { new ChangelogEntry(0, 1, 5, "new"), new ChangelogEntry(0, 1, 4, "old") };
            foreach (var seen in new[] { "0.1.5-20261020.7", "0.1.5+build.3", " v0.1.5-preview.1 " })
                Equal(0, Changelog.GetNewEntries(entries, seen).Count, "current release notes are not repeated for " + seen);
            Equal("new", Changelog.GetNewEntries(entries, "0.1.4-beta.1").Single().Content, "only newer release shown");
            foreach (var invalid in new[] { "latest", "0.1", "" })
                Equal(0, Changelog.GetNewEntries(entries, invalid).Count, "invalid last-seen version does not replay all notes");
            var native = new[] { new ChangelogEntry(1, 5, 0, "r3", 3), new ChangelogEntry(1, 5, 0, "r2", 2) };
            Equal("r3", Changelog.GetNewEntries(native, "1.5.0.2-preview.12+build.4").Single().Content, "native fourth part preserved");
        }));
        yield return ("gap.model-scope-cycle-remains-correct-after-reload", ModelCycleReload);
        yield return ("gap.overlay-keeps-inline-blocked-status-and-cancellation-disposes", BlockedOverlays);
    }

    private static async Task ModelCycleReload()
    {
        await using var pi = new InteractiveHarness("gap-cycle-reload");
        pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular",
            "--models", "claude-sonnet-4-5,claude-haiku-4-5");
        await pi.WaitFor("escape interrupt"); pi.Type("\u0010");
        await pi.WaitFor("claude-haiku-4-5 •");
        await pi.Submit("/reload"); await pi.WaitFor("Reloaded keybindings, extensions, skills, prompts, themes, and context files");
        pi.Type("\u0010"); await pi.WaitFor("claude-sonnet-4-5 •");
        await pi.Submit("after reload"); await pi.WaitFor("Hello from the fake model.");
        Check(pi.Requests[^1].Contains("\"model\":\"claude-sonnet-4-5\"", StringComparison.Ordinal), "request follows reloaded scope selection");
        Equal(0, await pi.Quit(), "exit code");
    }

    private static async Task BlockedOverlays()
    {
        await using var pi = new InteractiveHarness("gap-blocked-overlays");
        pi.Vars["PI_PROGRAM_STATUS"] = "1";
        pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular");
        await pi.WaitFor("escape interrupt");
        var disposed = new int[3];
        ExtensionCustomComponentCallbacks Panel(int index, string text, bool overlay) => new(
            new("gap-owner", 1, 1, "panel-" + index),
            (_, _, _) => Task.FromResult(new ExtensionCustomComponentRows([text], [text.Length])),
            (_, _, _) => Task.CompletedTask,
            _ => { Interlocked.Increment(ref disposed[index]); return Task.CompletedTask; })
            { Blocked = new("permission", "Approve action?"), Overlay = overlay ? new(null) : null };
        using var cancel = new CancellationTokenSource();
        var inline = Panel(0, "inline approval", false);
        var shownInline = pi.Mode!.ShowExtensionCustomComponentAsync(inline, null!, cancel.Token);
        await pi.WaitFor("inline approval");
        var status = "]7501;state=blocked:app=pi:kind=permission:msg=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Approve action?"));
        string LastStatus()
        {
            var raw = pi.Terminal.RawOutput; var at = raw.LastIndexOf("]7501;state=", StringComparison.Ordinal);
            if (at < 0) return ""; var end = raw.IndexOf("\u001b\\", at, StringComparison.Ordinal);
            return end < 0 ? raw[at..] : raw[at..end];
        }
        await pi.WaitUntil(_ => LastStatus() == status, "inline approval status");
        var first = Panel(1, "first overlay", true); var second = Panel(2, "second overlay", true);
        var shownFirst = pi.Mode.ShowExtensionCustomComponentAsync(first, null!, cancel.Token);
        await pi.WaitFor("first overlay");
        var shownSecond = pi.Mode.ShowExtensionCustomComponentAsync(second, null!, cancel.Token);
        await pi.WaitFor("second overlay");
        try
        {
            Equal(status, LastStatus(), "overlay does not replace inline blocked status");
            cancel.Cancel();
            foreach (var task in new[] { shownFirst, shownSecond, shownInline })
                try { await task; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            Check(disposed.All(count => count == 1), "cancellation disposes each inline/overlay once");
            await pi.WaitUntil(_ => !LastStatus().Contains("state=blocked", StringComparison.Ordinal), "cancel clears blocked state");
        }
        finally
        {
            cancel.Cancel();
            foreach (var task in new[] { shownFirst, shownSecond, shownInline })
                try { await task; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }
        Equal(0, await pi.Quit(), "exit code");
    }
}
