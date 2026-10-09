// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/extension-editor.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source ExtensionEditorOptions (EditorOptions plus a description).</summary>
internal sealed record ExtensionEditorOptions(int PaddingX = 0, int AutocompleteMaxVisible = 5, string? Description = null);

/// <summary>Multi-line editor component for extensions. Supports Ctrl+G for external editor.</summary>
internal sealed class ExtensionEditorComponent : Container, IFocusable, IInputHandler
{
    private readonly Editor editor;
    private readonly Action<string> onSubmitCallback;
    private readonly Action onCancelCallback;
    private readonly ITui tui;
    private readonly KeybindingsManager keybindings;
    private readonly string externalEditorCommand;
    private readonly ExternalEditorProcessRunner? processRunner;
    private readonly TextWriter? externalEditorOutput;

    private bool focused;
    public bool Focused
    {
        get => focused;
        set { focused = value; editor.Focused = value; }
    }

    /// <param name="keybindings">The app keybindings manager (app.* definitions).</param>
    /// <param name="environment">Reads VISUAL and EDITOR (default: the process environment).</param>
    /// <param name="processRunner">Runs the external editor (default: <see cref="ExternalEditor.RunProcess"/>).</param>
    /// <param name="externalEditorOutput">Receives the launch notice (default: standard output).</param>
    public ExtensionEditorComponent(
        ITui tui,
        KeybindingsManager keybindings,
        string title,
        string? prefill,
        Action<string> onSubmit,
        Action onCancel,
        ExtensionEditorOptions? options = null,
        string? externalEditorCommand = null,
        Func<string, string?>? environment = null,
        ExternalEditorProcessRunner? processRunner = null,
        TextWriter? externalEditorOutput = null)
    {
        this.tui = tui;
        this.keybindings = keybindings;
        environment ??= Environment.GetEnvironmentVariable;
        this.externalEditorCommand = FirstTruthy(externalEditorCommand, environment("VISUAL"), environment("EDITOR"))
            ?? (OperatingSystem.IsWindows() ? "notepad" : "nano");
        this.processRunner = processRunner;
        this.externalEditorOutput = externalEditorOutput;
        onSubmitCallback = onSubmit;
        onCancelCallback = onCancel;
        var description = options?.Description;
        var editorOptions = new EditorOptions(options?.PaddingX ?? 0, options?.AutocompleteMaxVisible ?? 5);

        // Add top border
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        // Add title and optional description
        AddChild(new Text(theme.Fg("accent", title), 1, 0));
        if (!string.IsNullOrEmpty(description))
        {
            AddChild(new Spacer(1));
            AddChild(new Text(theme.Fg("text", description), 1, 0));
        }
        AddChild(new Spacer(1));

        // Create editor
        editor = new Editor(tui, Themes.GetEditorTheme(), editorOptions);
        if (!string.IsNullOrEmpty(prefill)) editor.SetText(prefill);
        // Wire up Enter to submit (Shift+Enter for newlines, like the main editor)
        editor.OnSubmit = text => onSubmitCallback(text);
        AddChild(editor);

        AddChild(new Spacer(1));

        // Add hint
        var hint =
            KeybindingHints.KeyHint("tui.select.confirm", "submit") + "  " +
            KeybindingHints.KeyHint("tui.input.newLine", "newline") + "  " +
            KeybindingHints.KeyHint("tui.select.cancel", "cancel") +
            $"  {KeybindingHints.KeyHint("app.editor.external", "external editor")}";
        AddChild(new Text(hint, 1, 0));

        AddChild(new Spacer(1));

        // Add bottom border
        AddChild(new DynamicBorder());
    }

    private static string? FirstTruthy(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrEmpty(value));

    public string ExternalEditorCommand => externalEditorCommand;

    public Editor GetEditor() => editor;

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        // Escape or Ctrl+C to cancel
        if (kb.Matches(keyData, "tui.select.cancel"))
        {
            onCancelCallback();
            return;
        }

        // External editor (app keybinding)
        if (keybindings.Matches(keyData, "app.editor.external"))
        {
            _ = HandleOpenExternalEditor();
            return;
        }

        // Forward to editor
        editor.HandleInput(keyData);
    }

    /// <summary>Hands the terminal to the external editor: the TUI stops, the editor runs, the TUI restarts with a full redraw.
    /// Continuations resume on the caller's synchronization context (the UI loop when called from input handling).</summary>
    private async Task HandleOpenExternalEditor()
    {
        var content = editor.GetText();
        tui.Stop();
        try
        {
            var result = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions(externalEditorCommand, content), processRunner, externalEditorOutput);
            if (result.Status == "complete") editor.SetText(result.Content ?? "");
        }
        finally
        {
            tui.Start();
            tui.RequestRender(true);
        }
    }
}
