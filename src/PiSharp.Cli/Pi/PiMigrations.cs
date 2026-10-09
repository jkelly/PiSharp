// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/migrations.ts (runMigrations: migrateAuthToAuthJson,
// migrateSessionsFromAgentRoot, migrateToolsToBin, migrateExtensionSystem with migrateCommandsToPrompts and checkDeprecatedExtensionDirs).
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Pi;

internal sealed record PiMigrationResult(ImmutableArray<string> MigratedAuthProviders, ImmutableArray<string> DeprecationWarnings,
    ImmutableArray<string> Messages);

/// <summary>One-time startup migrations of the agent directory and project <c>.pi</c> folder, as upstream runs them. Failures are
/// skipped silently, as upstream does. <see cref="PiMigrationResult.DeprecationWarnings"/> are shown by the interactive mode (IMPL-I).</summary>
internal static class PiMigrations
{
    internal static PiMigrationResult Run(string cwd, string agentDir)
    {
        var messages = ImmutableArray.CreateBuilder<string>();
        var providers = MigrateAuthToAuthJson(agentDir);
        MigrateSessionsFromAgentRoot(agentDir);
        MigrateToolsToBin(agentDir, messages);
        MigrateKeybindingsConfigFile(agentDir);
        var warnings = MigrateExtensionSystem(cwd, agentDir, messages);
        return new(providers, warnings, messages.ToImmutable());
    }

    /// <summary>Source migrateAuthToAuthJson: legacy oauth.json and settings.json apiKeys into a new auth.json.</summary>
    internal static ImmutableArray<string> MigrateAuthToAuthJson(string agentDir)
    {
        var authPath = Path.Join(agentDir, "auth.json"); var oauthPath = Path.Join(agentDir, "oauth.json"); var settingsPath = Path.Join(agentDir, "settings.json");
        if (File.Exists(authPath)) return [];
        var migrated = new JsonObject(); var providers = ImmutableArray.CreateBuilder<string>();
        if (File.Exists(oauthPath))
        {
            try
            {
                if (JsonNode.Parse(PiPaths.ReadText(oauthPath)) is JsonObject oauth)
                    foreach (var (provider, credential) in oauth)
                    {
                        var entry = new JsonObject { ["type"] = "oauth" };
                        if (credential is JsonObject fields) foreach (var (key, value) in fields) entry[key] = value?.DeepClone();
                        migrated[provider] = entry; providers.Add(provider);
                    }
                File.Move(oauthPath, oauthPath + ".migrated");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(PiPaths.ReadText(settingsPath)) is JsonObject settings && settings["apiKeys"] is JsonObject apiKeys)
                {
                    foreach (var (provider, key) in apiKeys)
                        if (!migrated.ContainsKey(provider) && key is JsonValue value && value.TryGetValue<string>(out var text))
                        { migrated[provider] = new JsonObject { ["type"] = "api_key", ["key"] = text }; providers.Add(provider); }
                    settings.Remove("apiKeys");
                    File.WriteAllText(settingsPath, PiJson.Stringify(settings, indent: true));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        if (migrated.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
            File.WriteAllText(authPath, PiJson.Stringify(migrated, indent: true));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(authPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return providers.ToImmutable();
    }

    /// <summary>Source migrateSessionsFromAgentRoot: session files saved directly in the agent directory move to their cwd's folder.</summary>
    internal static void MigrateSessionsFromAgentRoot(string agentDir)
    {
        string[] files;
        try { files = Directory.Exists(agentDir) ? Directory.GetFiles(agentDir, "*.jsonl") : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        foreach (var file in files)
        {
            try
            {
                var first = File.ReadLines(file).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(first) || JsonNode.Parse(first) is not JsonObject header || PiSessions.Text(header, "type") != "session" ||
                    PiSessions.Text(header, "cwd") is not { Length: > 0 } headerCwd) continue;
                var trimmed = headerCwd.Length > 0 && (headerCwd[0] == '/' || headerCwd[0] == '\\') ? headerCwd[1..] : headerCwd;
                var correctDir = Path.Join(agentDir, "sessions", "--" + trimmed.Replace('/', '-').Replace('\\', '-').Replace(':', '-') + "--");
                Directory.CreateDirectory(correctDir);
                var target = Path.Join(correctDir, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                File.Move(file, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
    }

    /// <summary>Source migrateKeybindingsConfigFile: renamed legacy keybinding names are written back to keybindings.json
    /// (two-space JSON, trailing newline); a malformed file is left alone.</summary>
    internal static void MigrateKeybindingsConfigFile(string agentDir)
    {
        var path = Path.Join(agentDir, "keybindings.json");
        if (!File.Exists(path)) return;
        try
        {
            if (JsonNode.Parse(PiPaths.ReadText(path)) is not JsonObject raw) return;
            var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
            var config = PiSharp.Cli.Interactive.TerminalKeybindingConfigurationLoader.MigrateConfig(raw, platform, out var migrated);
            if (!migrated) return;
            File.WriteAllText(path, PiJson.Stringify(config, indent: true) + "\n", new System.Text.UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    /// <summary>Source migrateToolsToBin: managed fd/rg binaries move from tools/ to bin/.</summary>
    internal static void MigrateToolsToBin(string agentDir, ImmutableArray<string>.Builder messages)
    {
        var tools = Path.Join(agentDir, "tools"); var bin = Path.Join(agentDir, "bin");
        if (!Directory.Exists(tools)) return;
        var moved = false;
        foreach (var name in new[] { "fd", "rg", "fd.exe", "rg.exe" })
        {
            var old = Path.Join(tools, name); var target = Path.Join(bin, name);
            if (!File.Exists(old)) continue;
            Directory.CreateDirectory(bin);
            try
            {
                if (!File.Exists(target)) { File.Move(old, target); moved = true; }
                else File.Delete(old);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (moved) messages.Add("Migrated managed binaries tools/ → bin/");
    }

    /// <summary>Source migrateExtensionSystem: commands/ becomes prompts/, and deprecated hooks/ and custom tools/ are reported.</summary>
    internal static ImmutableArray<string> MigrateExtensionSystem(string cwd, string agentDir, ImmutableArray<string>.Builder messages)
    {
        var project = Path.Join(cwd, PiConfig.ConfigDirName);
        MigrateCommandsToPrompts(agentDir, "Global", messages);
        MigrateCommandsToPrompts(project, "Project", messages);
        return [.. CheckDeprecatedExtensionDirs(agentDir, "Global"), .. CheckDeprecatedExtensionDirs(project, "Project")];
    }

    private static void MigrateCommandsToPrompts(string baseDir, string label, ImmutableArray<string>.Builder messages)
    {
        var commands = Path.Join(baseDir, "commands"); var prompts = Path.Join(baseDir, "prompts");
        if (!Path.Exists(commands) || Path.Exists(prompts)) return;
        try { Directory.Move(commands, prompts); messages.Add($"Migrated {label} commands/ → prompts/"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { messages.Add($"Warning: Could not migrate {label} commands/ to prompts/: {error.Message}"); }
    }

    private static IEnumerable<string> CheckDeprecatedExtensionDirs(string baseDir, string label)
    {
        if (Path.Exists(Path.Join(baseDir, "hooks"))) yield return $"{label} hooks/ directory found. Hooks have been renamed to extensions.";
        var tools = Path.Join(baseDir, "tools");
        if (!Directory.Exists(tools)) yield break;
        string[] entries;
        try { entries = [.. Directory.EnumerateFileSystemEntries(tools).Select(path => Path.GetFileName(path)!)]; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        if (entries.Any(entry => entry.ToLowerInvariant() is not ("fd" or "rg" or "fd.exe" or "rg.exe") && !entry.StartsWith('.')))
            yield return $"{label} tools/ directory contains custom tools. Custom tools have been merged into extensions.";
    }
}
