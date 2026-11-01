// Decision 0011 (run W4, unit R9): the 30-day purge of deleted chats. Deleting a chat moves its durable session to device\archive\ as
// <session>-<yyyyMMddHHmmss>-<guid> (DurableDeviceHost.ArchiveSessionAsync; the catalog forgets it then), and nothing in the device removes it
// later. This janitor does, when the app starts and once a day after: a folder whose name says it was archived more than 30 days ago is
// deleted (a link is removed, never followed). A folder the device did not name that way is never touched, and the folder name, not its
// file times, says when the chat was deleted.
using System.Globalization;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>Purges archived chats older than <see cref="DataStorageOptions.ArchiveRetention"/>.</summary>
public sealed partial class ChatArchiveJanitor(WebAgentOptions options, DataStorageOptions storage, TimeProvider? time = null, ILogger<ChatArchiveJanitor>? log = null)
    : BackgroundService
{
    /// <summary>How often the purge runs.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(24);

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>The archive folder.</summary>
    public string Directory => Path.Join(options.DeviceDirectory, "archive");

    /// <summary>When the archived session folder <paramref name="name"/> was archived, or null when the device did not name it.</summary>
    public static DateTimeOffset? ArchivedAt(string name) =>
        ArchiveName().Match(name) is { Success: true } match &&
        DateTime.TryParseExact(match.Groups["at"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? new DateTimeOffset(at, TimeSpan.Zero) : null;

    /// <summary>Deletes the archived chats past the retention now; how many it deleted.</summary>
    public int PurgeNow()
    {
        if (!System.IO.Directory.Exists(Directory) || DataFolders.IsLink(Directory)) return 0;
        var cutoff = clock.GetUtcNow() - storage.ArchiveRetention;
        var purged = 0;
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory).ToList())
        {
            if (ArchivedAt(Path.GetFileName(folder)) is not { } at || at >= cutoff) continue;
            try
            {
                DataFolders.Delete(folder);
                purged++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log?.LogWarning(error, "The archived chat {Folder} could not be purged; it is tried again tomorrow.", Path.GetFileName(folder));
            }
        }
        if (purged > 0) log?.LogInformation("Purged {Count} archived chats deleted more than {Days} days ago.", purged, storage.ArchiveRetention.TotalDays);
        return purged;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { PurgeNow(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log?.LogWarning(error, "The archive purge failed; it runs again in {Interval}.", Interval);
            }
            try { await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    [GeneratedRegex("^.+-(?<at>[0-9]{14})-[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveName();
}
