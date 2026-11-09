using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>The transcript owns the lifetime of its tool views; ordinary TUI containers do not own their children.</summary>
internal sealed class ToolExecutionContainer : Container
{
    public void DisposeToolViews()
    {
        List<Exception>? errors = null;
        foreach (var component in Children.OfType<ToolExecutionComponent>().Distinct().ToArray())
            try { component.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Transcript tool disposal failed.", errors);
    }

    public override void Clear()
    {
        try { DisposeToolViews(); }
        finally { base.Clear(); }
    }

    public override void RemoveChild(IComponent component)
    {
        var owned = Children.Contains(component);
        base.RemoveChild(component);
        if (owned && !Children.Contains(component) && component is ToolExecutionComponent tool) tool.Dispose();
    }
}
