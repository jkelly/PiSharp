// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/settings-manager.ts (SettingsManager: the
// getters with their defaults, the setters that persist the modified global fields, FileSettingsStorage locking).
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>Interactive settings: global <c>settings.json</c> and the trusted project layer merged; setters change the global layer
/// and persist only the fields modified in this session, merged into the current file contents under a lock.</summary>
internal sealed class InteractiveSettings
{
    private readonly string globalPath, projectPath;
    private JsonObject global, project, settings;
    private bool projectTrusted;
    private readonly HashSet<string> modified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> modifiedNested = new(StringComparer.Ordinal);
    private bool globalLoadFailed;
    private readonly object writeGate = new();
    private Task writeQueue = Task.CompletedTask;
    private readonly List<string> errors = [];
    private readonly Func<string, string?> env;

    public InteractiveSettings(string cwd, string agentDir, bool projectTrusted, Func<string, string?>? environment = null)
    {
        env = environment ?? Environment.GetEnvironmentVariable;
        globalPath = Path.Join(agentDir, "settings.json");
        projectPath = Path.Join(cwd, PiConfig.ConfigDirName, "settings.json");
        this.projectTrusted = projectTrusted;
        (global, globalLoadFailed) = Load(globalPath);
        project = projectTrusted ? Load(projectPath).Settings : [];
        settings = PiSettings.Merge(global, project);
    }

    /// <summary>In-memory settings (tests).</summary>
    public InteractiveSettings(JsonObject globalSettings, Func<string, string?>? environment = null)
    {
        env = environment ?? Environment.GetEnvironmentVariable;
        globalPath = ""; projectPath = ""; projectTrusted = true;
        global = PiSettings.Migrate(globalSettings); project = []; settings = PiSettings.Merge(global, project);
    }

    public string GlobalPath => globalPath;
    private bool InMemory => globalPath.Length == 0;

    private (JsonObject Settings, bool Failed) Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return ([], false);
            var text = File.ReadAllText(path);
            if (text.StartsWith('﻿')) text = text[1..];
            if (text.Length == 0) return ([], false);
            return (JsonNode.Parse(text) as JsonObject is { } obj ? PiSettings.Migrate(obj) : throw new JsonException("Settings must be a JSON object"), false);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            lock (errors) errors.Add($"Invalid settings file {path}: {error.Message}");
            return ([], true);
        }
    }

    public bool IsProjectTrusted => projectTrusted;
    public JsonObject Snapshot => (JsonObject)settings.DeepClone();
    public IReadOnlyList<string> DrainErrors() { lock (errors) { var drained = errors.ToList(); errors.Clear(); return drained; } }

    public Task ReloadAsync()
    {
        return Task.Run(async () =>
        {
            Task pending; lock (writeGate) pending = writeQueue;
            await pending.ConfigureAwait(false);
            var (loaded, failed) = Load(globalPath);
            if (!failed) { global = loaded; globalLoadFailed = false; }
            modified.Clear(); modifiedNested.Clear();
            if (projectTrusted) project = Load(projectPath).Settings;
            settings = PiSettings.Merge(global, project);
        });
    }

    public Task FlushAsync() { lock (writeGate) return writeQueue; }

    // ---------------------------------------------------------------- access helpers
    private JsonNode? Get(string name) => settings[name];
    private JsonNode? Get(string name, string nested) => settings[name] is JsonObject obj ? obj[nested] : null;
    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    private static double? Num(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;

    private void Set(string name, JsonNode? value)
    {
        if (value is null) global.Remove(name); else global[name] = value;
        modified.Add(name);
        Save();
    }
    private void SetNested(string name, string nested, JsonNode? value)
    {
        if (global[name] is not JsonObject obj) global[name] = obj = [];
        if (value is null) obj.Remove(nested); else obj[nested] = value;
        modified.Add(name);
        if (!modifiedNested.TryGetValue(name, out var set)) modifiedNested[name] = set = new(StringComparer.Ordinal);
        set.Add(nested);
        Save();
    }

    private void Save()
    {
        settings = PiSettings.Merge(global, project);
        if (globalLoadFailed || InMemory) { modified.Clear(); modifiedNested.Clear(); return; }
        var snapshot = (JsonObject)global.DeepClone();
        var fields = modified.ToList();
        var nested = modifiedNested.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
        modified.Clear(); modifiedNested.Clear();
        lock (writeGate)
            writeQueue = writeQueue.ContinueWith(_ =>
            {
                try { Persist(snapshot, fields, nested); }
                catch (Exception error) { lock (errors) errors.Add($"Invalid settings file {globalPath}: {error.Message}"); }
            }, TaskScheduler.Default);
    }

    private void Persist(JsonObject snapshot, List<string> fields, Dictionary<string, List<string>> nested)
    {
        WithLock(globalPath, current =>
        {
            var file = current is null || current.Length == 0 ? [] : PiSettings.Migrate((JsonNode.Parse(current.TrimStart('﻿')) as JsonObject) ?? []);
            foreach (var field in fields)
            {
                var value = snapshot[field];
                if (nested.TryGetValue(field, out var keys) && value is JsonObject inMemory)
                {
                    var merged = file[field] is JsonObject baseNested ? (JsonObject)baseNested.DeepClone() : [];
                    foreach (var key in keys)
                    {
                        if (inMemory[key] is { } nestedValue) merged[key] = nestedValue.DeepClone(); else merged.Remove(key);
                    }
                    file[field] = merged;
                }
                else if (value is null) file.Remove(field);
                else file[field] = value.DeepClone();
            }
            return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        });
    }

    /// <summary>proper-lockfile's protocol: a <c>&lt;file&gt;.lock</c> directory, retried ten times 20 ms apart.</summary>
    internal static void WithLock(string path, Func<string?, string?> update)
    {
        var lockPath = path + ".lock";
        var exists = File.Exists(path);
        var locked = false;
        void Acquire()
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(lockPath))
                    {
                        // A lock older than 10 s is stale (proper-lockfile's default stale time).
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockPath) > TimeSpan.FromSeconds(10)) Directory.Delete(lockPath, true);
                        else throw new IOException("ELOCKED");
                    }
                    Directory.CreateDirectory(lockPath); locked = true; return;
                }
                catch (IOException) when (attempt < 10) { Thread.Sleep(20); }
            }
        }
        try
        {
            if (exists) Acquire();
            var current = exists ? File.ReadAllText(path) : null;
            var next = update(current);
            if (next is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!locked) Acquire();
            File.WriteAllText(path, next);
        }
        finally { if (locked) try { Directory.Delete(lockPath, true); } catch { } }
    }

    // ---------------------------------------------------------------- settings
    public string? LastChangelogVersion => Str(Get("lastChangelogVersion"));
    public void SetLastChangelogVersion(string version) => Set("lastChangelogVersion", version);
    public string? DefaultProvider => Str(Get("defaultProvider"));
    public string? DefaultModel => Str(Get("defaultModel"));
    public void SetDefaultModelAndProvider(string provider, string modelId)
    {
        global["defaultProvider"] = provider; global["defaultModel"] = modelId;
        modified.Add("defaultProvider"); modified.Add("defaultModel"); Save();
    }
    public string SteeringMode => Str(Get("steeringMode")) is { Length: > 0 } mode ? mode : "one-at-a-time";
    public void SetSteeringMode(string mode) => Set("steeringMode", mode);
    public string FollowUpMode => Str(Get("followUpMode")) is { Length: > 0 } mode ? mode : "one-at-a-time";
    public void SetFollowUpMode(string mode) => Set("followUpMode", mode);
    public string? ThemeSetting => Str(Get("theme"));
    public string? Theme => ThemeSetting is { } theme && !theme.Contains('/') ? theme : null;
    public void SetTheme(string theme) => Set("theme", theme);
    public string? DefaultThinkingLevel => Str(Get("defaultThinkingLevel"));
    public void SetDefaultThinkingLevel(string level) => Set("defaultThinkingLevel", level);
    public IReadOnlyDictionary<string, string> AllModelThinkingLevels =>
        Get("modelThinkingLevels") is JsonObject levels ? levels.Where(pair => Str(pair.Value) is not null).ToDictionary(pair => pair.Key, pair => Str(pair.Value)!, StringComparer.Ordinal) : new Dictionary<string, string>();
    public string? ModelThinkingLevel(string provider, string modelId) => Get("modelThinkingLevels") is JsonObject levels ? Str(levels[provider + "/" + modelId]) : null;
    public void SetModelThinkingLevel(string provider, string modelId, string level)
    {
        if (global["modelThinkingLevels"] is not JsonObject levels) global["modelThinkingLevels"] = levels = [];
        levels[provider + "/" + modelId] = level; modified.Add("modelThinkingLevels"); Save();
    }
    public void RemoveModelThinkingLevel(string provider, string modelId)
    {
        if (global["modelThinkingLevels"] is not JsonObject levels) return;
        levels.Remove(provider + "/" + modelId);
        if (levels.Count == 0) global.Remove("modelThinkingLevels");
        modified.Add("modelThinkingLevels"); Save();
    }
    public string Transport => Str(Get("transport")) ?? "auto";
    public void SetTransport(string transport) => Set("transport", transport);
    public bool CompactionEnabled => Bool(Get("compaction", "enabled")) ?? true;
    public void SetCompactionEnabled(bool enabled) => SetNested("compaction", "enabled", enabled);
    public bool BranchSummarySkipPrompt => Bool(Get("branchSummary", "skipPrompt")) ?? false;
    public bool RetryEnabled => Bool(Get("retry", "enabled")) ?? true;
    public int HttpIdleTimeoutMs => Num(Get("httpIdleTimeoutMs")) is { } value && value >= 0 ? (int)Math.Floor(value) : 300_000;
    public void SetHttpIdleTimeoutMs(int timeoutMs)
    {
        if (timeoutMs < 0) throw new ArgumentException($"Invalid httpIdleTimeoutMs setting: {timeoutMs}");
        Set("httpIdleTimeoutMs", timeoutMs);
    }
    /// <summary>Read from global settings only because warming costs money.</summary>
    public string CacheWarmingMode => Str(global["cacheWarming"]) is "off" or "streaming" or "idle" ? Str(global["cacheWarming"])! : "streaming";
    public void SetCacheWarmingMode(string mode) => Set("cacheWarming", mode);
    public bool HideThinkingBlock => Bool(Get("hideThinkingBlock")) ?? false;
    public void SetHideThinkingBlock(bool hide) => Set("hideThinkingBlock", hide);
    public bool ShowCacheMissNotices => Bool(Get("showCacheMissNotices")) ?? false;
    public void SetShowCacheMissNotices(bool show) => Set("showCacheMissNotices", show);
    public string ExternalEditorCommand
    {
        get
        {
            if (Str(Get("externalEditor")) is { } configured && configured.Trim().Length > 0) return configured;
            if (env("VISUAL") is { Length: > 0 } visual) return visual;
            if (env("EDITOR") is { Length: > 0 } editor) return editor;
            return OperatingSystem.IsWindows() ? "notepad" : "nano";
        }
    }
    /// <summary>true, "header" or false.</summary>
    public object QuietStartup => Get("quietStartup") is JsonValue value ? value.TryGetValue<bool>(out var flag) && flag ? true : Str(value) == "header" ? "header" : false : false;
    public void SetQuietStartup(object quiet) => Set("quietStartup", quiet is bool flag ? flag : quiet.ToString());
    public string DefaultProjectTrust => Str(global["defaultProjectTrust"]) is "always" or "never" ? Str(global["defaultProjectTrust"])! : "ask";
    public void SetDefaultProjectTrust(string value) => Set("defaultProjectTrust", value);
    public bool CollapseChangelog => Bool(Get("collapseChangelog")) ?? false;
    public void SetCollapseChangelog(bool collapse) => Set("collapseChangelog", collapse);
    public bool EnableInstallTelemetry => Bool(Get("enableInstallTelemetry")) ?? true;
    public void SetEnableInstallTelemetry(bool enabled) => Set("enableInstallTelemetry", enabled);
    public string GetOrCreateDeviceId()
    {
        if (Str(global["deviceId"]) is { Length: > 0 } id) return id;
        var created = Guid.NewGuid().ToString(); Set("deviceId", created);
        return created;
    }
    public bool EnableSkillCommands => Bool(Get("enableSkillCommands")) ?? true;
    public void SetEnableSkillCommands(bool enabled) => Set("enableSkillCommands", enabled);
    public PiSharp.Tui.Pi.TerminalCapabilityOverrides TerminalCapabilityOverrides
    {
        get
        {
            var terminal = Get("terminal") as JsonObject;
            var images = terminal?["images"];
            PiSharp.Tui.Pi.ImageProtocol? protocol = Str(images) switch { "kitty" => PiSharp.Tui.Pi.ImageProtocol.Kitty, "iterm2" => PiSharp.Tui.Pi.ImageProtocol.ITerm2, _ => Bool(images) == false ? PiSharp.Tui.Pi.ImageProtocol.None : null };
            return new(protocol, Bool(terminal?["trueColor"]), Bool(terminal?["hyperlinks"]));
        }
    }
    public bool ShowImages => Bool(Get("terminal", "showImages")) ?? true;
    public void SetShowImages(bool show) => SetNested("terminal", "showImages", show);
    public int ImageWidthCells => Num(Get("terminal", "imageWidthCells")) is { } width && double.IsFinite(width) ? Math.Max(1, (int)Math.Floor(width)) : 60;
    public void SetImageWidthCells(int width) => SetNested("terminal", "imageWidthCells", Math.Max(1, width));
    public bool ClearOnShrink => Bool(Get("terminal", "clearOnShrink")) ?? env("PI_CLEAR_ON_SHRINK") == "1";
    public void SetClearOnShrink(bool enabled) => SetNested("terminal", "clearOnShrink", enabled);
    public bool ShowTerminalProgress => Bool(Get("terminal", "showTerminalProgress")) ?? false;
    public void SetShowTerminalProgress(bool enabled) => SetNested("terminal", "showTerminalProgress", enabled);
    public string TuiMode => Str(Get("tuiMode")) == "regular" ? "regular" : "fullscreen";
    public void SetTuiMode(string mode) => Set("tuiMode", mode);
    public string FullscreenExitOutput => Str(Get("fullscreenExitOutput")) == "resume-hint" ? "resume-hint" : "transcript";
    public void SetFullscreenExitOutput(string output) => Set("fullscreenExitOutput", output);
    public string FullscreenScrollbar => Str(Get("fullscreenScrollbar")) is "always" or "hidden" ? Str(Get("fullscreenScrollbar"))! : "auto";
    public void SetFullscreenScrollbar(string mode) => Set("fullscreenScrollbar", mode);
    public bool FullscreenCopyOnSelect => Bool(Get("fullscreenCopyOnSelect")) ?? true;
    public void SetFullscreenCopyOnSelect(bool enabled) => Set("fullscreenCopyOnSelect", enabled);
    /// <summary>Lines per wheel event 1-100, or null for "auto".</summary>
    public int? FullscreenWheelScrollLines => Num(Get("fullscreenWheelScrollLines")) is { } lines && double.IsFinite(lines) ? Math.Max(1, Math.Min(100, (int)Math.Floor(lines))) : null;
    public void SetFullscreenWheelScrollLines(int? lines) => Set("fullscreenWheelScrollLines", lines is { } value ? Math.Max(1, Math.Min(100, value)) : "auto");
    public bool ImageAutoResize => Bool(Get("images", "autoResize")) ?? true;
    public void SetImageAutoResize(bool enabled) => SetNested("images", "autoResize", enabled);
    public bool BlockImages => Bool(Get("images", "blockImages")) ?? false;
    public void SetBlockImages(bool blocked) => SetNested("images", "blockImages", blocked);
    public IReadOnlyList<string>? EnabledModels => Get("enabledModels") is JsonArray array ? array.Select(Str).OfType<string>().ToList() : null;
    public void SetEnabledModels(IReadOnlyList<string>? patterns) => Set("enabledModels", patterns is null ? null : new JsonArray(patterns.Select(p => (JsonNode)p).ToArray()));
    public string DoubleEscapeAction => Str(Get("doubleEscapeAction")) is "fork" or "tree" or "none" ? Str(Get("doubleEscapeAction"))! : "tree";
    public void SetDoubleEscapeAction(string action) => Set("doubleEscapeAction", action);
    public string TreeFilterMode => Str(Get("treeFilterMode")) is "default" or "no-tools" or "user-only" or "labeled-only" or "all" ? Str(Get("treeFilterMode"))! : "default";
    public void SetTreeFilterMode(string mode) => Set("treeFilterMode", mode);
    public bool ShowHardwareCursor => Bool(Get("showHardwareCursor")) ?? env("PI_HARDWARE_CURSOR") == "1";
    public void SetShowHardwareCursor(bool enabled) => Set("showHardwareCursor", enabled);
    public int EditorPaddingX => Num(Get("editorPaddingX")) is { } value ? (int)value : 0;
    public void SetEditorPaddingX(int padding) => Set("editorPaddingX", Math.Max(0, Math.Min(3, padding)));
    public int OutputPad => Num(Get("outputPad")) == 0 ? 0 : 1;
    public void SetOutputPad(int padding) => Set("outputPad", padding == 0 ? 0 : 1);
    public int AutocompleteMaxVisible => Num(Get("autocompleteMaxVisible")) is { } value ? (int)value : 5;
    public void SetAutocompleteMaxVisible(int maxVisible) => Set("autocompleteMaxVisible", Math.Max(3, Math.Min(20, maxVisible)));
    public string CodeBlockIndent => Str(Get("markdown", "codeBlockIndent")) ?? "  ";
    public string MermaidRenderingMode => Str(Get("markdown", "mermaid")) is "off" or "final" ? Str(Get("markdown", "mermaid"))! : "streaming";
    public void SetMermaidRenderingMode(string mode) => SetNested("markdown", "mermaid", mode);
    /// <summary>The <c>warnings</c> object (anthropicExtraUsage defaults to true).</summary>
    public bool WarningAnthropicExtraUsage => Bool(Get("warnings", "anthropicExtraUsage")) ?? true;
    public void SetWarningAnthropicExtraUsage(bool enabled) { global["warnings"] = new JsonObject { ["anthropicExtraUsage"] = enabled }; modified.Add("warnings"); Save(); }
    public string? SessionDir(string home) => Str(Get("sessionDir")) is { Length: > 0 } dir ? PiPaths.NormalizePath(dir, home) : null;
}
