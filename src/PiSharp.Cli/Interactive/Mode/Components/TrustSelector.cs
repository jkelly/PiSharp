// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/trust-selector.ts.
using System.Collections.Immutable;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source TrustSelection (<c>Pick&lt;ProjectTrustOption, "trusted" | "updates"&gt;</c>); the host saves the updates with
/// <see cref="ProjectTrustStore.SetMany"/>.</summary>
internal sealed record TrustSelection(bool Trusted, ImmutableArray<ProjectTrustUpdate> Updates);

/// <summary>Source TrustSelectorOptions. <see cref="GetProjectTrustOptions"/> is trust-manager's getProjectTrustOptions(cwd)
/// (default: <see cref="ProjectTrustStore.Options"/> over the user's home directory).</summary>
internal sealed record TrustSelectorOptions(
    string Cwd,
    ProjectTrustStoreEntry? SavedDecision,
    bool ProjectTrusted,
    Action<TrustSelection> OnSelect,
    Action OnCancel,
    Func<string, IReadOnlyList<ProjectTrustOption>>? GetProjectTrustOptions = null);

/// <summary>The /trust dialog: the saved decision, the session's trust state and the trust choices for the cwd.</summary>
internal sealed class TrustSelectorComponent : Container, IInputHandler
{
    private int selectedIndex;
    private readonly Container listContainer;
    private readonly IReadOnlyList<ProjectTrustOption> trustOptions;
    private readonly ProjectTrustStoreEntry? savedDecision;
    private readonly Action<TrustSelection> onSelectCallback;
    private readonly Action onCancelCallback;

    private static string FormatDecision(string? trustPath, ProjectTrustStoreEntry? decision)
    {
        if (decision is null) return "none";
        var label = decision.Decision ? "trusted" : "untrusted";
        if (trustPath is not null && decision.Path != trustPath) return $"{label} (inherited from {decision.Path})";
        return $"{label} ({decision.Path})";
    }

    /// <summary>getProjectTrustOptions(cwd) without session-only choices.</summary>
    public static IReadOnlyList<ProjectTrustOption> DefaultProjectTrustOptions(string cwd)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new ProjectTrustStore(home, home).Options(cwd);
    }

    public TrustSelectorComponent(TrustSelectorOptions options)
    {
        savedDecision = options.SavedDecision;
        trustOptions = (options.GetProjectTrustOptions ?? DefaultProjectTrustOptions)(options.Cwd);
        selectedIndex = Math.Max(0, trustOptions.ToList().FindIndex(IsSavedOption));
        onSelectCallback = options.OnSelect;
        onCancelCallback = options.OnCancel;

        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold("Project trust")), 1, 0));
        AddChild(new Text(theme.Fg("muted", options.Cwd), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("muted",
            $"Saved decision: {FormatDecision(trustOptions.Count > 0 ? trustOptions[0].SavedPath : null, options.SavedDecision)}"), 1, 0));
        AddChild(new Text(theme.Fg("muted", $"Current session: {(options.ProjectTrusted ? "trusted" : "untrusted")}"), 1, 0));
        AddChild(new Spacer(1));

        listContainer = new Container();
        AddChild(listContainer);
        AddChild(new Spacer(1));
        AddChild(new Text(
            KeybindingHints.RawKeyHint("↑↓", "navigate") + "  " +
            KeybindingHints.KeyHint("tui.select.confirm", "save") + "  " +
            KeybindingHints.KeyHint("tui.select.cancel", "cancel"), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());

        UpdateList();
    }

    private bool IsSavedOption(ProjectTrustOption option) =>
        option.SavedPath is not null && savedDecision is not null && savedDecision.Decision == option.Trusted && savedDecision.Path == option.SavedPath;

    private void UpdateList()
    {
        listContainer.Clear();
        for (var i = 0; i < trustOptions.Count; i++)
        {
            var option = trustOptions[i];
            var isSelected = i == selectedIndex;
            var isCurrent = IsSavedOption(option);
            var currentMarker = isCurrent ? theme.Fg("accent", "✓ ") : "  ";
            var prefix = isSelected ? theme.Fg("accent", "→ ") : "  ";
            var label = isSelected ? theme.Fg("accent", option.Label) : theme.Fg("text", option.Label);
            listContainer.AddChild(new Text($"{prefix}{currentMarker}{label}", 1, 0));
        }
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.select.up") || keyData == "k")
        {
            selectedIndex = Math.Max(0, selectedIndex - 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.down") || keyData == "j")
        {
            selectedIndex = Math.Min(trustOptions.Count - 1, selectedIndex + 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.confirm") || keyData == "\n")
        {
            if (selectedIndex >= 0 && selectedIndex < trustOptions.Count)
            {
                var selected = trustOptions[selectedIndex];
                onSelectCallback(new TrustSelection(selected.Trusted, selected.Updates));
            }
        }
        else if (kb.Matches(keyData, "tui.select.cancel"))
        {
            onCancelCallback();
        }
    }
}
