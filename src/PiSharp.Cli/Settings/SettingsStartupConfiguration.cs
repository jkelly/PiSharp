using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

namespace PiSharp.Cli.Settings;

internal static class SettingsStartupConfiguration
{
    internal const string Flags = "[--user-settings <absolute JSON>] [--project-settings <absolute JSON>] [--steering-mode all|one-at-a-time] [--follow-up-mode all|one-at-a-time]";
    internal static StartupSettingsRequest? FromOptions(IReadOnlyDictionary<string, string> options)
    {
        options.TryGetValue("--user-settings", out var user); options.TryGetValue("--project-settings", out var project);
        options.TryGetValue("--steering-mode", out var steering); options.TryGetValue("--follow-up-mode", out var followUp);
        if (user is null && project is null && steering is null && followUp is null) return null;
        if (steering is not (null or "all" or "one-at-a-time") || followUp is not (null or "all" or "one-at-a-time"))
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (steering is not null) overrides.Add("steeringMode", steering);
        if (followUp is not null) overrides.Add("followUpMode", followUp);
        return new(user is null ? null : SessionCommands.Absolute(user), project is null ? null : SessionCommands.Absolute(project),
            JsonData.Parse(JsonSerializer.Serialize(overrides)));
    }
    internal static async Task<StartupSettingsSnapshot?> LoadAsync(StartupSettingsRequest? request, TextWriter diagnostics,
        IStartupSettingsFileSystem? fileSystem, CancellationToken token)
    {
        var capture = await StartupSettings.LoadAsync(request ?? new StartupSettingsRequest(), fileSystem, token).ConfigureAwait(false);
        foreach (var diagnostic in capture.Diagnostics)
            await diagnostics.WriteLineAsync(JsonSerializer.Serialize(new { type = "settings_diagnostic", scope = diagnostic.Scope,
                path = diagnostic.Path, code = diagnostic.Code }).AsMemory(), token).ConfigureAwait(false);
        await diagnostics.FlushAsync(token).ConfigureAwait(false);
        return capture;
    }
}
