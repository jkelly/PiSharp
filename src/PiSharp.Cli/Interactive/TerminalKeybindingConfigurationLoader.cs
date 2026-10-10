using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Tui.Input;

namespace PiSharp.Cli.Interactive;

/// <summary>Read-only keybindings.json loading. No model, credential or other settings file is opened.</summary>
public static class TerminalKeybindingConfigurationLoader
{
    public const int MaximumConfigurationCharacters = 1_048_576, MaximumEntries = 1024,
        MaximumKeysPerBinding = 256, MaximumKeyCharacters = 256;

    public static TerminalKeybindingConfiguration LoadDefault()
    {
        var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in new[] { "PI_CODING_AGENT_DIR", "WSL_DISTRO_NAME", "WSL_INTEROP" }) env[name] = Environment.GetEnvironmentVariable(name);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Load(ResolveAgentDirectory(home, platform, env), platform, env);
    }

    public static TerminalKeybindingConfiguration Load(string agentDirectory, string platform,
        IReadOnlyDictionary<string, string?>? environment = null, Func<string, string?>? readText = null)
    {
        ArgumentNullException.ThrowIfNull(agentDirectory); ArgumentNullException.ThrowIfNull(platform);
        var env = environment is null ? new Dictionary<string, string?>(StringComparer.Ordinal) :
            environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var definitions = TerminalAgentKeybindingDefinitions.Create(platform, env);
        var configPath = Join(platform, agentDirectory, "keybindings.json");
        var reader = readText ?? ReadBounded;
        TerminalKeybindingConfiguration ReadConfiguration()
        {
            var bindings = new List<KeyValuePair<string, TerminalKeybindingValue?>>(); var status = "missing"; var migrated = false;
            try
            {
                var content = reader(configPath);
                if (content is not null)
                {
                    if (content.Length > MaximumConfigurationCharacters) throw new InvalidDataException("Configuration exceeds native character bound.");
                    if (content.StartsWith('\uFEFF')) content = content[1..];
                    using var document = JsonDocument.Parse(content, PiSharp.Contracts.JsonData.DocumentOptions);
                    if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        var raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                        if (document.RootElement.ValueKind == JsonValueKind.Object)
                            foreach (var property in document.RootElement.EnumerateObject()) raw[property.Name] = property.Value;
                        else
                        {
                            var index = 0; foreach (var value in document.RootElement.EnumerateArray()) raw[(index++).ToString(CultureInfo.InvariantCulture)] = value;
                        }
                        if (raw.Count > MaximumEntries) throw new InvalidDataException("Configuration exceeds native entry bound.");
                        var renamed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                        foreach (var key in OwnOrder(raw.Keys))
                        {
                            var next = Migrations.GetValueOrDefault(key) ?? key;
                            if (next != key) { migrated = true; if (raw.ContainsKey(next)) continue; }
                            renamed[next] = raw[key];
                        }
                        var ordered = definitions.Select(pair => pair.Key).Where(renamed.ContainsKey).Concat(
                            renamed.Keys.Where(key => !definitions.Any(pair => pair.Key == key)).Order(StringComparer.Ordinal));
                        foreach (var key in OwnOrder(ordered))
                        {
                            var value = renamed[key]; TerminalKeybindingValue? binding = null;
                            if (value.ValueKind == JsonValueKind.String) binding = new(value.GetString()!);
                            else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
                                binding = new(value.EnumerateArray().Select(item => item.GetString()!));
                            if (binding is null) continue; // JSON null/invalid values are filtered; registry null is an internal adaptation only.
                            if (key.Length > MaximumKeyCharacters || binding.Keys.Count > MaximumKeysPerBinding || binding.Keys.Any(k => k.Length > MaximumKeyCharacters))
                                throw new InvalidDataException("Configuration exceeds native key bound.");
                            bindings.Add(new(key, binding));
                        }
                        status = "loaded";
                    }
                    else status = "invalid-root";
                }
            }
            catch (Exception) { bindings.Clear(); migrated = false; status = "unreadable-invalid-or-bounded"; }
            return new(agentDirectory, configPath, platform, status, migrated, definitions, bindings, ReadConfiguration);
        }
        return ReadConfiguration();
    }

    private static string? ReadBounded(string file)
    {
        if (!File.Exists(file)) return null;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumConfigurationCharacters * 4L) throw new InvalidDataException("Configuration exceeds native byte bound.");
        // Source readFileSync UTF-8 retains/replaces invalid bytes and strips exactly one leading BOM afterwards.
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false);
        var buffer = new char[4096]; var result = new StringBuilder(); int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (count > MaximumConfigurationCharacters - result.Length) throw new InvalidDataException("Configuration exceeds native character bound.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    /// <summary>Source migrateKeybindingsConfig (core/keybindings.ts): legacy names renamed (dropped when the new name is also
    /// present), then the known keybindings in definition order and the others sorted. migrations.ts writes the result back.</summary>
    internal static System.Text.Json.Nodes.JsonObject MigrateConfig(System.Text.Json.Nodes.JsonObject raw, string platform, out bool migrated)
    {
        migrated = false;
        var renamed = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(StringComparer.Ordinal);
        foreach (var key in OwnOrder(raw.Select(pair => pair.Key)))
        {
            var next = Migrations.GetValueOrDefault(key) ?? key;
            if (next != key) { migrated = true; if (raw.ContainsKey(next)) continue; }
            renamed[next] = raw[key]?.DeepClone();
        }
        var definitions = TerminalAgentKeybindingDefinitions.Create(platform, new Dictionary<string, string?>(StringComparer.Ordinal));
        var known = definitions.Select(pair => pair.Key).Where(renamed.ContainsKey).ToList();
        var result = new System.Text.Json.Nodes.JsonObject();
        foreach (var key in known.Concat(renamed.Keys.Where(key => !known.Contains(key)).Order(StringComparer.Ordinal))) result[key] = renamed[key];
        return result;
    }

    private static IEnumerable<string> OwnOrder(IEnumerable<string> keys)
    {
        var values = keys.ToArray();
        bool Index(string key, out uint index) => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index) &&
            index != uint.MaxValue && index.ToString(CultureInfo.InvariantCulture) == key;
        return values.Where(key => Index(key, out _)).OrderBy(key => uint.Parse(key, CultureInfo.InvariantCulture))
            .Concat(values.Where(key => !Index(key, out _)));
    }

    public static string ResolveAgentDirectory(string home, string platform, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(home); ArgumentNullException.ThrowIfNull(platform);
        var value = environment is not null && environment.TryGetValue("PI_CODING_AGENT_DIR", out var input) ? input : null;
        if (string.IsNullOrEmpty(value)) return Join(platform, home, ".pi", "agent");
        if (platform == "win32" && value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) && !value.Contains('\\'))
        {
            var shell = Regex.Match(value, @"^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (shell.Success) value = shell.Groups[1].Value.ToUpperInvariant() + ":\\" + shell.Groups[2].Value.Replace('/', '\\');
        }
        if (value == "~") return home;
        if (value.StartsWith("~/", StringComparison.Ordinal) || platform == "win32" && value.StartsWith("~\\", StringComparison.Ordinal)) return Join(platform, home, value[2..]);
        if (value.StartsWith("file://", StringComparison.Ordinal))
        {
            if (value.Contains("%2f", StringComparison.OrdinalIgnoreCase) || platform == "win32" && value.Contains("%5c", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Encoded separators are not file URL paths.");
            var uri = new Uri(value); if (!uri.IsFile) throw new ArgumentException("Expected file URL.");
            var decoded = Uri.UnescapeDataString(uri.AbsolutePath);
            if (platform == "win32") return uri.Host.Length != 0 && uri.Host != "localhost" ? "\\\\" + uri.Host + decoded.Replace('/', '\\') :
                decoded.Length >= 3 && decoded[0] == '/' && decoded[2] == ':' ? decoded[1..].Replace('/', '\\') : decoded.Replace('/', '\\');
            if (uri.Host.Length != 0 && uri.Host != "localhost") throw new ArgumentException("POSIX file URLs must be local.");
            return decoded;
        }
        return value;
    }

    private static string Join(string platform, params string[] parts)
    {
        var windows = platform == "win32"; var separator = windows ? '\\' : '/';
        var value = string.Join(separator, parts.Where(p => p.Length != 0)); if (windows) value = value.Replace('/', '\\');
        var prefix = ""; var protectedParts = 0;
        if (windows && value.StartsWith("\\\\", StringComparison.Ordinal)) { prefix = "\\\\"; value = value.TrimStart('\\'); protectedParts = 2; }
        else if (windows && value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        { prefix = value[..2]; value = value[2..]; if (value.StartsWith(separator)) { prefix += separator; value = value.TrimStart(separator); } }
        else if (value.StartsWith(separator)) { prefix = separator.ToString(); value = value.TrimStart(separator); }
        var rooted = prefix.EndsWith(separator); var segments = new List<string>();
        foreach (var segment in value.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == ".." && segments.Count > protectedParts && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
            else if (segment != ".." || !rooted) segments.Add(segment);
        }
        var joined = prefix + string.Join(separator, segments); return joined.Length == 0 ? "." : joined;
    }

    // Exact ordered migration inventory from pinned MIT-licensed coding-agent/core/keybindings.ts.
    private static readonly IReadOnlyDictionary<string, string> Migrations = MigrationInventory();
    private static IReadOnlyDictionary<string, string> MigrationInventory() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cursorUp"] = "tui.editor.cursorUp",
        ["cursorDown"] = "tui.editor.cursorDown",
        ["cursorLeft"] = "tui.editor.cursorLeft",
        ["cursorRight"] = "tui.editor.cursorRight",
        ["cursorWordLeft"] = "tui.editor.cursorWordLeft",
        ["cursorWordRight"] = "tui.editor.cursorWordRight",
        ["cursorLineStart"] = "tui.editor.cursorLineStart",
        ["cursorLineEnd"] = "tui.editor.cursorLineEnd",
        ["jumpForward"] = "tui.editor.jumpForward",
        ["jumpBackward"] = "tui.editor.jumpBackward",
        ["pageUp"] = "tui.editor.pageUp",
        ["pageDown"] = "tui.editor.pageDown",
        ["deleteCharBackward"] = "tui.editor.deleteCharBackward",
        ["deleteCharForward"] = "tui.editor.deleteCharForward",
        ["deleteWordBackward"] = "tui.editor.deleteWordBackward",
        ["deleteWordForward"] = "tui.editor.deleteWordForward",
        ["deleteToLineStart"] = "tui.editor.deleteToLineStart",
        ["deleteToLineEnd"] = "tui.editor.deleteToLineEnd",
        ["yank"] = "tui.editor.yank",
        ["yankPop"] = "tui.editor.yankPop",
        ["undo"] = "tui.editor.undo",
        ["newLine"] = "tui.input.newLine",
        ["submit"] = "tui.input.submit",
        ["tab"] = "tui.input.tab",
        ["copy"] = "tui.input.copy",
        ["selectUp"] = "tui.select.up",
        ["selectDown"] = "tui.select.down",
        ["selectPageUp"] = "tui.select.pageUp",
        ["selectPageDown"] = "tui.select.pageDown",
        ["selectConfirm"] = "tui.select.confirm",
        ["selectCancel"] = "tui.select.cancel",
        ["interrupt"] = "app.interrupt",
        ["clear"] = "app.clear",
        ["exit"] = "app.exit",
        ["suspend"] = "app.suspend",
        ["cycleThinkingLevel"] = "app.thinking.cycle",
        ["cycleModelForward"] = "app.model.cycleForward",
        ["cycleModelBackward"] = "app.model.cycleBackward",
        ["selectModel"] = "app.model.select",
        ["expandTools"] = "app.tools.expand",
        ["toggleThinking"] = "app.thinking.toggle",
        ["toggleSessionNamedFilter"] = "app.session.toggleNamedFilter",
        ["externalEditor"] = "app.editor.external",
        ["followUp"] = "app.message.followUp",
        ["dequeue"] = "app.message.dequeue",
        ["pasteImage"] = "app.clipboard.pasteImage",
        ["newSession"] = "app.session.new",
        ["tree"] = "app.session.tree",
        ["fork"] = "app.session.fork",
        ["resume"] = "app.session.resume",
        ["treeFoldOrUp"] = "app.tree.foldOrUp",
        ["treeUnfoldOrDown"] = "app.tree.unfoldOrDown",
        ["treeEditLabel"] = "app.tree.editLabel",
        ["treeToggleLabelTimestamp"] = "app.tree.toggleLabelTimestamp",
        ["toggleSessionPath"] = "app.session.togglePath",
        ["toggleSessionSort"] = "app.session.toggleSort",
        ["renameSession"] = "app.session.rename",
        ["deleteSession"] = "app.session.delete",
        ["deleteSessionNoninvasive"] = "app.session.deleteNoninvasive",
    };
}
