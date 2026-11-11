#pragma warning disable PISHARP_EXTUI001
using System.Text.Json;
using static Expect;

internal static class GapOverlayCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("gap.overlay-node-stacked-handles-visibility-resize-dispose", StackedNodeOverlays);
    }

    private static async Task StackedNodeOverlays()
    {
        await using var pi = new InteractiveHarness("gap-overlay-node");
        var log = Path.Combine(pi.Cwd, "overlay.log");
        var extension = pi.Write("overlay.mjs", """
            import { appendFileSync } from 'node:fs';
            export default function (pi) {
              const log = (value) => appendFileSync(process.cwd() + '/overlay.log', JSON.stringify(value) + '\n');
              pi.registerCommand('overlay', { handler: async (_, ctx) => {
                let lower, upper, closeLower, closeUpper;
                let ready;
                const mounted = new Promise(resolve => ready = resolve);
                const first = ctx.ui.custom((tui, theme, keys, done) => {
                  closeLower = done;
                  return { render: () => [`lower:${lower?.isFocused() === true}`], handleInput: data => {
                    if (data === 'u') { lower.unfocus(); upper.focus(); }
                  }, dispose: () => log('lower-disposed') };
                }, { overlay: true, overlayOptions: () => ({ width: 20, row: 1, col: 0,
                  visible: (columns, rows) => { lower?.getBounds(); return columns >= 80 && rows >= 10; } }),
                  onHandle: handle => { lower = handle; ready(); } });
                await mounted;
                const second = ctx.ui.custom((tui, theme, keys, done) => {
                  closeUpper = done;
                  return { render: () => [`upper:${upper?.isFocused() === true}`], handleInput: data => {
                    if (data === 'h') { lower.setHidden(true); log(['hidden', lower.isHidden()]); }
                    if (data === 's') lower.setHidden(false);
                    if (data === 'f') lower.focus();
                    if (data === 'b') log(['bounds', lower.getBounds(), upper.getBounds()]);
                    if (data === 'c') { closeLower('closed'); closeLower('duplicate'); }
                    if (data === 'x') closeUpper('closed');
                  }, dispose: () => log('upper-disposed') };
                }, { overlay: true, overlayOptions: { width: '50%', row: 4, col: 0 }, onHandle: handle => upper = handle });
                await Promise.all([first, second]);
                lower.focus(); lower.setHidden(false); upper.hide();
                log(['stale', lower.isHidden(), lower.isFocused(), lower.getBounds() ?? null]);
                try { await ctx.ui.custom(() => ({ render: () => ['throw-overlay'], dispose: () => log('throw-disposed') }),
                  { overlay: true, onHandle: () => { throw new Error('handle-failed'); } }); }
                catch (error) { log(['error', error.message]); }
                ctx.ui.notify('overlays complete', 'info');
              } });
            }
            """);
        pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular", "-e", extension);
        await pi.WaitFor("escape interrupt");
        await pi.Submit("/overlay");
        await pi.WaitUntil(text => text.Contains("lower:", StringComparison.Ordinal) && text.Contains("upper", StringComparison.Ordinal), "both Node overlays");
        pi.Type("h");
        await pi.WaitUntil(text => !text.Contains("lower:", StringComparison.Ordinal) && text.Contains("upper", StringComparison.Ordinal), "only lower overlay hidden");
        pi.Type("s");
        await pi.WaitFor("lower:true");
        pi.Type("u");
        // unfocus and focus are separate host notifications. A lower:false frame can precede upper.focus;
        // wait for the intended input recipient before driving resize and the next key.
        await pi.WaitUntil(text => text.Contains("lower:false", StringComparison.Ordinal) && text.Contains("upper:true", StringComparison.Ordinal), "upper focused after transfer");
        pi.Terminal.Resize(70, 30);
        await pi.WaitUntil(text => !text.Contains("lower:", StringComparison.Ordinal) && text.Contains("upper", StringComparison.Ordinal), "dynamic predicate after shrinking");
        pi.Terminal.Resize(120, 30);
        await pi.WaitUntil(text => text.Contains("lower:false", StringComparison.Ordinal) && text.Contains("upper:true", StringComparison.Ordinal), "upper focused after resize");
        pi.Type("b");
        await pi.WaitUntil(_ => File.Exists(log) && File.ReadAllText(log).Contains("bounds", StringComparison.Ordinal), "bounds recorded after resize");
        pi.Type("f");
        await pi.WaitFor("lower:true");
        pi.Type("u");
        await pi.WaitUntil(text => text.Contains("lower:false", StringComparison.Ordinal) && text.Contains("upper:true", StringComparison.Ordinal), "upper focused before closing lower");
        pi.Type("c");
        await pi.WaitUntil(text => !text.Contains("lower:", StringComparison.Ordinal) && text.Contains("upper", StringComparison.Ordinal), "done closes its own overlay");
        pi.Type("x");
        await pi.WaitFor("overlays complete");
        var records = File.ReadAllLines(log).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Equal(1, records.Count(row => row.ValueKind == JsonValueKind.String && row.GetString() == "lower-disposed"), "lower disposed once");
        Equal(1, records.Count(row => row.ValueKind == JsonValueKind.String && row.GetString() == "upper-disposed"), "upper disposed once");
        Equal(1, records.Count(row => row.ValueKind == JsonValueKind.String && row.GetString() == "throw-disposed"), "throwing onHandle disposed once");
        var bounds = records.Single(row => row.ValueKind == JsonValueKind.Array && row[0].GetString() == "bounds");
        Equal(20, bounds[1].GetProperty("width").GetInt32(), "lower width");
        Equal(60, bounds[2].GetProperty("width").GetInt32(), "upper width after resize");
        var stale = records.Single(row => row.ValueKind == JsonValueKind.Array && row[0].GetString() == "stale");
        Check(stale[1].GetBoolean() && !stale[2].GetBoolean() && stale[3].ValueKind == JsonValueKind.Null, "stale handle is inert");
        Check(records.Any(row => row.ValueKind == JsonValueKind.Array && row[0].GetString() == "error" && row[1].GetString()!.Contains("handle-failed", StringComparison.Ordinal)), "onHandle error rejects");
        Equal(0, await pi.Quit(), "exit code");
    }
}
