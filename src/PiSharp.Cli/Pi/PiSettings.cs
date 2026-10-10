// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/settings-manager.ts (SettingsManager.create,
// migrateSettings, deepMergeSettings, mergeDefaultTools and the getters the CLI reads) and core/settings-diagnostics.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

namespace PiSharp.Cli.Pi;

internal sealed record PiSettingsError(string Scope, string? Path, string Message);

/// <summary>Global <c>&lt;agentDir&gt;/settings.json</c> and, for a trusted project, <c>&lt;cwd&gt;/.pi/settings.json</c>: each layer
/// migrated as upstream does, merged project over global. Reading never writes; a layer that fails to load is empty and recorded.</summary>
internal sealed class PiSettings
{
    internal string GlobalPath { get; }
    internal string ProjectPath { get; }
    internal bool ProjectTrusted { get; }
    internal JsonObject Global { get; }
    internal JsonObject Project { get; }
    internal JsonObject Merged { get; private set; }
    private readonly List<PiSettingsError> errors = [];

    private PiSettings(string globalPath, string projectPath, bool projectTrusted, JsonObject global, JsonObject project)
    {
        GlobalPath = globalPath; ProjectPath = projectPath; ProjectTrusted = projectTrusted; Global = global; Project = project;
        Merged = Merge(global, project);
    }

    /// <summary>Source SettingsManager.create(cwd, agentDir, { projectTrusted }).</summary>
    internal static PiSettings Load(string cwd, string agentDir, bool projectTrusted)
    {
        var globalPath = Path.Join(agentDir, "settings.json");
        var projectPath = Path.Join(cwd, PiConfig.ConfigDirName, "settings.json");
        var loadErrors = new List<PiSettingsError>();
        var global = LoadLayer(globalPath, "global", loadErrors);
        var project = projectTrusted ? LoadLayer(projectPath, "project", loadErrors) : [];
        var settings = new PiSettings(globalPath, projectPath, projectTrusted, global, project);
        settings.errors.AddRange(loadErrors);
        settings.loadFailures.UnionWith(loadErrors);
        return settings;
    }

    /// <summary>Settings from explicit JSON objects (tests and in-memory composition).</summary>
    internal static PiSettings FromObjects(JsonObject global, JsonObject? project = null, bool projectTrusted = true) =>
        new("<memory>/settings.json", "<memory>/.pi/settings.json", projectTrusted, Migrate(global), projectTrusted && project is not null ? Migrate(project) : []) { inMemory = true };

    private bool inMemory;

    /// <summary>Source SettingsManager's single-field setters (setPackages, setProjectPackages, setExtensionPaths, …) and
    /// persistScopedSettings: the field changes in the layer and the merged view, then the file is re-read under its
    /// <c>settings.json.lock</c>, migrated, given the field (other keys kept, a null value removes it) and written as
    /// <c>JSON.stringify(settings, null, 2)</c>. A layer that failed to load is not written; write failures are recorded as errors.
    /// The project layer refuses writes when the project is untrusted.</summary>
    internal void SetField(string scope, string field, JsonNode? value)
    {
        var project = scope == "project";
        if (project && !ProjectTrusted) throw new InvalidOperationException("Project is not trusted; refusing to write project settings");
        var layer = project ? Project : Global;
        if (value is null) layer.Remove(field); else layer[field] = value.DeepClone();
        Merged = Merge(Global, Project);
        var path = project ? ProjectPath : GlobalPath;
        if (inMemory || loadFailures.Any(error => error.Scope == (project ? "project" : "global"))) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WithFileLock(path, () =>
            {
                var current = File.Exists(path) ? PiJson.Parse(PiPaths.ReadText(path)) as JsonObject
                    ?? throw new JsonException("Settings must be a JSON object") : [];
                Migrate(current);
                if (value is null) current.Remove(field); else current[field] = value.DeepClone();
                File.WriteAllText(path, PiJson.Stringify(current, indent: true), new System.Text.UTF8Encoding(false));
            });
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { errors.Add(new(project ? "project" : "global", path, error.Message)); }
    }

    private readonly HashSet<PiSettingsError> loadFailures = [];

    /// <summary>proper-lockfile's lockSync as SettingsManager retries it: an exclusive <c>mkdir</c> of <c>&lt;file&gt;.lock</c>, ten
    /// attempts 20 ms apart, stale after 10 s.</summary>
    private static void WithFileLock(string path, Action run)
    {
        var lockPath = path + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(lockPath))
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockPath) > TimeSpan.FromSeconds(10)) Directory.Delete(lockPath);
                    else throw new IOException("Lock file is already being held");
                }
                Directory.CreateDirectory(lockPath);
                break;
            }
            catch (IOException) when (attempt < 10) { Thread.Sleep(20); }
        }
        try { run(); }
        finally { try { Directory.Delete(lockPath); } catch (IOException) { } }
    }

    private static JsonObject LoadLayer(string path, string scope, List<PiSettingsError> errors)
    {
        try
        {
            if (!File.Exists(path)) return [];
            // Writers truncate and rewrite the file under <file>.lock (SetField, InteractiveSettings.WithLock), so the read takes the
            // same lock and never sees a file mid-write (which read as empty, and so as default settings). A lock held past the
            // retries, or a folder the reader cannot write, falls back to reading without it.
            string? text = null;
            try { WithFileLock(path, () => text = File.Exists(path) ? PiPaths.ReadText(path) : ""); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { text = null; }
            text ??= File.Exists(path) ? PiPaths.ReadText(path) : "";
            if (text.Length == 0) return [];
            var node = PiJson.Parse(text);
            if (node is not JsonObject settings) throw new JsonException("Settings must be a JSON object");
            return Migrate(settings);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            errors.Add(new(scope, path, error.Message));
            return [];
        }
    }

    /// <summary>Source migrateSettings: queueMode, websockets, the old skills object and retry.maxDelayMs.</summary>
    internal static JsonObject Migrate(JsonObject settings)
    {
        if (settings.ContainsKey("queueMode") && !settings.ContainsKey("steeringMode"))
        {
            settings["steeringMode"] = settings["queueMode"]?.DeepClone(); settings.Remove("queueMode");
        }
        if (!settings.ContainsKey("transport") && settings["websockets"] is JsonValue websockets &&
            websockets.TryGetValue<bool>(out var useWebsockets))
        {
            settings["transport"] = useWebsockets ? "websocket" : "sse"; settings.Remove("websockets");
        }
        if (settings["skills"] is JsonObject skills)
        {
            if (skills["enableSkillCommands"] is { } enable && (!settings.ContainsKey("enableSkillCommands") || settings["enableSkillCommands"] is null))
                settings["enableSkillCommands"] = enable.DeepClone();
            if (skills["customDirectories"] is JsonArray directories && directories.Count > 0) settings["skills"] = directories.DeepClone();
            else settings.Remove("skills");
        }
        if (settings["retry"] is JsonObject retry)
        {
            var provider = retry["provider"] as JsonObject;
            if (retry["maxDelayMs"] is JsonValue delay && delay.GetValueKind() == JsonValueKind.Number &&
                (provider?["maxRetryDelayMs"] is null))
            {
                var updated = provider is null ? new JsonObject() : (JsonObject)provider.DeepClone();
                updated["maxRetryDelayMs"] = delay.DeepClone();
                retry["provider"] = updated;
            }
            retry.Remove("maxDelayMs");
        }
        return settings;
    }

    /// <summary>Source deepMergeSettings: objects merge recursively, other values replace; a <c>defaultTools</c> list of only
    /// <c>+name</c>/<c>-name</c> entries is appended to the inherited list.</summary>
    internal static JsonObject Merge(JsonObject earlier, JsonObject later)
    {
        var result = MergeObjects(earlier, later);
        if (later["defaultTools"] is JsonArray overrides && earlier["defaultTools"] is JsonArray inherited &&
            overrides.All(node => node is JsonValue value && value.TryGetValue<string>(out var text) && PiArgs.IsToolModifier(text)))
        {
            var combined = (JsonArray)inherited.DeepClone();
            foreach (var item in overrides) combined.Add(item?.DeepClone());
            result["defaultTools"] = combined;
        }
        return result;
    }
    private static JsonObject MergeObjects(JsonObject earlier, JsonObject later)
    {
        var result = (JsonObject)earlier.DeepClone();
        foreach (var (key, value) in later)
            result[key] = result[key] is JsonObject previous && value is JsonObject next ? MergeObjects(previous, next) : value?.DeepClone();
        return result;
    }

    /// <summary>Source applyOverrides.</summary>
    internal void ApplyOverrides(JsonObject overrides) => Merged = Merge(Merged, overrides);

    /// <summary>Source collectSettingsDiagnostics (drains the recorded errors).</summary>
    internal ImmutableArray<PiDiagnostic> DrainDiagnostics()
    {
        var drained = errors.Select(error => new PiDiagnostic("warning", error.Path is not null
            ? $"Invalid settings file {error.Path}: {error.Message}" : $"Invalid {error.Scope} settings: {error.Message}")).ToImmutableArray();
        errors.Clear();
        return drained;
    }
    internal IReadOnlyList<PiSettingsError> Errors => errors;

    internal string? String(string name) => Merged[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal bool? Boolean(string name) => Merged[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    internal ImmutableArray<string> Strings(string name, JsonObject? layer = null) =>
        (layer ?? Merged)[name] is JsonArray array ? [.. array.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var text) ? text : null).OfType<string>()] : [];

    /// <summary>Source getSessionDir (normalized, tilde expanded).</summary>
    internal string? SessionDir(string home) => String("sessionDir") is { Length: > 0 } dir ? PiPaths.NormalizePath(dir, home) : null;
    /// <summary>Source getDefaultProjectTrust: global settings only; "always", "never", else "ask".</summary>
    internal string DefaultProjectTrust =>
        Global["defaultProjectTrust"] is JsonValue value && value.TryGetValue<string>(out var text) && text is "always" or "never" ? text : "ask";
    /// <summary>Source getQuietStartup: true, "header", else false.</summary>
    internal object QuietStartup => Merged["quietStartup"] is JsonValue value
        ? value.TryGetValue<bool>(out var flag) && flag ? true : value.TryGetValue<string>(out var text) && text == "header" ? "header" : false : false;
    /// <summary>Source getThemeSetting/getTheme: a theme name; values naming a file path are not themes.</summary>
    internal string? Theme => String("theme") is { } theme && !theme.Contains('/') ? theme : null;
    internal string? ThemeSetting => String("theme");
    /// <summary>Source getEnableSkillCommands (default true).</summary>
    internal bool EnableSkillCommands => Boolean("enableSkillCommands") ?? true;
    /// <summary>PiSharp <c>toolPolicy</c> (decision 0004): "pi" or "explicit", else null.</summary>
    internal string? ToolPolicy => String("toolPolicy") is "pi" or "explicit" ? String("toolPolicy") : null;
    internal ImmutableArray<string>? EnabledModels => Merged["enabledModels"] is JsonArray ? Strings("enabledModels") : null;

    /// <summary>The merged layers as the existing startup snapshot (queue modes, retry policy and tool settings readers).</summary>
    internal async Task<StartupSettingsSnapshot> ToStartupSnapshotAsync(CancellationToken cancellationToken)
    {
        var root = OperatingSystem.IsWindows() ? "C:" : "";
        string global = root + "/global/settings.json", project = root + "/project/settings.json";
        var files = new MemoryFiles(new(StringComparer.Ordinal) { [global] = Global.ToJsonString(), [project] = Project.ToJsonString() });
        var snapshot = await StartupSettings.LoadAsync(new(global, project), files, cancellationToken).ConfigureAwait(false);
        // Overrides applied after loading (source applyOverrides) are part of the effective values.
        if (JsonNode.DeepEquals(Merge(Global, Project), Merged)) return snapshot;
        return snapshot with { Values = JsonData.Parse(Merged.ToJsonString()) };
    }

    private sealed class MemoryFiles(Dictionary<string, string> files) : IStartupSettingsFileSystem
    {
        public ValueTask<string?> ReadTextAsync(string absolutePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(files.TryGetValue(absolutePath, out var text) ? text : null);
    }
}

internal sealed record PiDiagnostic(string Type, string Message);
