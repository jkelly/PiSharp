using System.Collections.Immutable;
using PiSharp.Rpc.Ui;
using PiSharp.Tui;
using PiSharp.Tui.Components.SelectList;
using PiSharp.Tui.Input;

namespace PiSharp.Cli.Interactive;

// Keyboard rules adapted from Mario Zechner's MIT-licensed Pi v0.99.1 at d86654a.
// Copyright (c) 2025 Mario Zechner; full notice retained in
// tests/PiSharp.Terminal.SelectList.Integration.Tests/UPSTREAM-LICENSE.

// Local presentation identity never enters the baseline extension_ui_response packet.
internal sealed record TerminalSelectListDialogSnapshot(Guid LifetimeId,
    RpcExtensionUiPresentationIdentity? Identity, long SessionGeneration, string Title,
    ImmutableArray<string> Options, bool ResponsePending, bool HostNavigation = false, int InitialIndex = 0);

/// <summary>
/// The existing input consumer alone dispatches keys. The view alone mutates/renders the
/// list under its render turn. The frontend remains the authority for replies and retirement.
/// This controller borrows all three; command cleanup joins their originals before closing it.
/// </summary>
internal sealed class TerminalSelectListDialogController(InteractiveSessionFrontend frontend,
    TerminalSessionView view, TerminalKeybindings keybindings) : IAsyncDisposable
{
    private readonly TerminalKeybindings bindings = keybindings.CreateSnapshot();
    private TerminalSelectListDialogSnapshot? active;
    private TerminalSelectList? component;
    private int selectedIndex;
    private bool closed;

    internal async ValueTask ReconcileAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        var next = frontend.CaptureSelectDialog();
        if (SameLifetime(active, next) && active?.ResponsePending == next?.ResponsePending) return;
        if (!SameLifetime(active, next)) selectedIndex = next?.InitialIndex ?? 0;
        active = next;
        component = next is null ? null : CreateComponent(next);
        await view.SetSelectListAsync(component, token, focused: next is { Options.Length: > 0 },
            title: next?.Title + (next?.ResponsePending == true ? " [awaiting retirement]" : "")).ConfigureAwait(false);
    }

    internal async ValueTask HandleInputAsync(TerminalInputEvent input,
        TerminalSelectListDialogSnapshot admitted, CancellationToken token)
    {
        // A key captured for a retired/queued/replaced dialog is consumed without becoming
        // a draft edit or an answer to its successor. A pending answer keeps its owner.
        if (closed || !SameLifetime(active, admitted) || component is not { } list) return;
        var current = frontend.CaptureSelectDialog();
        if (current is null || !SameLifetime(current, admitted) || current.ResponsePending) return;
        bool Matches(string action) => bindings.GetKeys(action).Any(key => TerminalInputDecoder.MatchesKey(input, key));
        TerminalInputDecoder.TryGetOriginalInput(input, out var raw, out _);
        // ExtensionSelectorComponent's precedence and clamp semantics differ from SelectList.
        // Its optional tools-expansion callback is absent in this bounded native UI profile.
        if (Matches("app.tools.expand")) return;
        if (Matches("tui.select.up") || raw == "k")
            selectedIndex = Math.Max(0, selectedIndex - 1);
        else if (Matches("tui.select.down") || raw == "j")
            selectedIndex = Math.Min(current.Options.Length - 1, selectedIndex + 1);
        else if (Matches("tui.select.confirm") || raw == "\n")
        {
            // Source deliberately leaves an empty string unresolved.
            if ((uint)selectedIndex < (uint)current.Options.Length && current.Options[selectedIndex].Length != 0)
                await frontend.RespondSelectAsync(admitted, selectedIndex, canceled: false, token).ConfigureAwait(false);
            await ReconcileAsync(token).ConfigureAwait(false); return;
        }
        else if (Matches("tui.select.cancel"))
        {
            await frontend.RespondSelectAsync(admitted, null, canceled: true, token).ConfigureAwait(false);
            await ReconcileAsync(token).ConfigureAwait(false); return;
        }
        else return;
        await view.UpdateSelectListAsync(list, selected => selected.SetSelectedIndex(selectedIndex), token).ConfigureAwait(false);
    }

    private TerminalSelectList CreateComponent(TerminalSelectListDialogSnapshot snapshot)
    {
        // The title is a separate Text component, never a selectable option or response value.
        var rows = snapshot.Options.Select(option => new TerminalSelectListItem(option, ""));
        var list = new TerminalSelectList(rows, maxVisible: 256, bindings,
            layout: new(MaxPrimaryColumnWidth: 256),
            limits: new(MaximumItems: 65_536, MaximumTextCharacters: 1_048_576));
        list.SetSelectedIndex(selectedIndex);
        return list;
    }

    internal static bool SameLifetime(TerminalSelectListDialogSnapshot? first, TerminalSelectListDialogSnapshot? second) =>
        first is null ? second is null : second is not null && first.LifetimeId == second.LifetimeId &&
        first.Identity == second.Identity && first.HostNavigation == second.HostNavigation && first.SessionGeneration == second.SessionGeneration;

    public async ValueTask DisposeAsync()
    {
        if (closed) return;
        closed = true; active = null; component = null;
        await view.SetSelectListAsync(null, CancellationToken.None).ConfigureAwait(false);
    }
}
