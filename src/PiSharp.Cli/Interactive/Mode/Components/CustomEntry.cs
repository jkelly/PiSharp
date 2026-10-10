// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/custom-entry.ts.
// The CustomEntry is a JsonObject in core/session-manager.ts's shape: { type: "custom", id, parentId, timestamp, customType, data? }.
// EntryRenderOptions and EntryRenderer are declared in core/extensions/types.ts; they live here until the extension runner port
// declares them.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal sealed record EntryRenderOptions(bool Expanded);

/// <summary>An extension's renderer for a custom session entry type; null renders nothing.</summary>
internal delegate IComponent? EntryRenderer(JsonObject entry, EntryRenderOptions options, Theme theme);

/// <summary>
/// Component that renders a custom session entry from extensions.
/// The host owns transcript spacing; renderer output should provide only its content.
/// </summary>
internal sealed class CustomEntryComponent : Container
{
    private readonly JsonObject entry;
    private readonly EntryRenderer renderer;
    private IComponent? customComponent;
    private bool expanded;
    private int outputPad;

    public CustomEntryComponent(JsonObject entry, EntryRenderer renderer, int outputPad = 1)
    {
        this.entry = entry;
        this.renderer = renderer;
        this.outputPad = outputPad;
        Rebuild();
    }

    public bool HasContent() => customComponent is not null;

    public void SetExpanded(bool expanded)
    {
        if (this.expanded != expanded)
        {
            this.expanded = expanded;
            Rebuild();
        }
    }

    public void SetOutputPad(int outputPad)
    {
        this.outputPad = outputPad;
        Rebuild();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        Rebuild();
    }

    private void Rebuild()
    {
        Clear();
        customComponent = null;

        IComponent? component;
        try
        {
            component = renderer(entry, new EntryRenderOptions(expanded), theme);
        }
        catch (Exception error)
        {
            var box = new Box(outputPad, 1, text => theme.Bg("customMessageBg", text));
            box.AddChild(new Text(theme.Fg("error", $"[{entry["customType"]?.ToString() ?? "undefined"}] renderer failed: {error.Message}"), 0, 0));
            component = box;
        }

        if (component is null) return;

        customComponent = component;
        AddChild(new Spacer(1));
        AddChild(component);
    }
}
