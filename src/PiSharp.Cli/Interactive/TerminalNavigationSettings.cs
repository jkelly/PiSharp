using System.Text;
using System.Text.Json;

namespace PiSharp.Cli.Interactive;

/// <summary>Immutable, read-only startup projection of only the double-Escape setting.</summary>
public sealed class TerminalNavigationSettings
{
    internal TerminalNavigationSettings(string globalPath, string projectPath, TerminalDoubleEscapeAction action,
        string source, string globalStatus, string projectStatus, Func<TerminalNavigationSettings> reload)
        => (GlobalPath, ProjectPath, Action, Source, GlobalStatus, ProjectStatus, this.reload) =
            (globalPath, projectPath, action, source, globalStatus, projectStatus, reload);
    private readonly Func<TerminalNavigationSettings> reload;
    public string GlobalPath { get; }
    public string ProjectPath { get; }
    public TerminalDoubleEscapeAction Action { get; }
    public string Source { get; }
    public string GlobalStatus { get; }
    public string ProjectStatus { get; }
    public TerminalNavigationSettings Reload() => reload();
}

public static class TerminalNavigationSettingsLoader
{
    public const int MaximumCharacters = 1_048_576, MaximumDepth = PiSharp.Contracts.JsonData.MaximumDepth;
    public static TerminalNavigationSettings Load(string agentDirectory, string workspace,
        Func<string, string?>? readText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectory); ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var global = Path.Combine(Path.GetFullPath(agentDirectory), "settings.json");
        var project = Path.Combine(Path.GetFullPath(workspace), ".pi", "settings.json");
        var reader = readText ?? ReadBounded;
        TerminalNavigationSettings Read()
        {
            var first = Scope(global); var second = Scope(project);
            var effective = second.Present ? second : first;
            return new(global, project, effective.Action, second.Present ? "project" : first.Present ? "global" : "default",
                first.Status, second.Status, Read);
        }
        (bool Present, TerminalDoubleEscapeAction Action, string Status) Scope(string path)
        {
            try
            {
                var text = reader(path);
                if (text is null) return (false, TerminalDoubleEscapeAction.Tree, "missing");
                if (text.Length == 0) return (false, TerminalDoubleEscapeAction.Tree, "absent");
                if (text.Length > MaximumCharacters) throw new InvalidDataException();
                if (text.StartsWith('\uFEFF')) text = text[1..];
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaximumDepth });
                if (document.RootElement.ValueKind != JsonValueKind.Object) return (false, TerminalDoubleEscapeAction.Tree, "invalid-root");
                if (!document.RootElement.TryGetProperty("doubleEscapeAction", out var value))
                    return (false, TerminalDoubleEscapeAction.Tree, "absent");
                if (value.ValueKind == JsonValueKind.Null) return (true, TerminalDoubleEscapeAction.Tree, "null-default");
                if (value.ValueKind == JsonValueKind.String)
                    switch (value.GetString())
                    {
                        case "tree": return (true, TerminalDoubleEscapeAction.Tree, "loaded");
                        case "fork": return (true, TerminalDoubleEscapeAction.Fork, "loaded");
                        case "none": return (true, TerminalDoubleEscapeAction.None, "loaded");
                    }
                // A present project value masks global, including a rejected enum/type.
                // Never coerce values or let an invalid override reveal a lower scope.
                return (true, TerminalDoubleEscapeAction.Tree, "invalid-value-default");
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { return (false, TerminalDoubleEscapeAction.Tree, "unreadable-invalid-or-bounded"); }
        }
        return Read();
    }
    private static string? ReadBounded(string path)
    {
        // File.Open distinguishes missing from denied/unreadable. No writes, lockfiles or other files.
        FileStream file;
        try { file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        using (file)
        {
            if (file.Length > MaximumCharacters * 4L) throw new InvalidDataException();
            using var reader = new StreamReader(file, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false);
            var result = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (count > MaximumCharacters - result.Length) throw new InvalidDataException();
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
    }
}

public static class TerminalStartupConfigurationLoader
{
    public static TerminalKeybindingConfiguration LoadDefault(string workspace)
    {
        var configuration = TerminalKeybindingConfigurationLoader.LoadDefault();
        return Load(configuration, workspace);
    }
    public static TerminalKeybindingConfiguration Load(TerminalKeybindingConfiguration keybindings, string workspace,
        Func<string, string?>? readSettingsText = null)
    {
        ArgumentNullException.ThrowIfNull(keybindings);
        return keybindings.WithNavigationSettings(TerminalNavigationSettingsLoader.Load(keybindings.AgentDirectory, workspace, readSettingsText));
    }
}
