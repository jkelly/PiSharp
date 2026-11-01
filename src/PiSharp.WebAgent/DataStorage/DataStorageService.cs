// Decision 0011 (run W4, unit R9): Settings › Data & storage's operations. The page's facts are measured when it is asked for (a folder walk
// that never follows a link; runs.db's sizes from SQLite), so every number on the page is this machine's. The areas it reports on are each
// optional (resolved from the container when present), so the page renders in any host and says what is not there. Every change goes
// through the area that owns the data: run history's settings through ISettingsStore (and its retention is applied at once, through
// RunStore.Cleanup), workspaces through AgentCompareRunner.DiscardAsync when Compare knows the run (so its record says discarded) or
// WorkspaceManager otherwise, chats through ChatService.ArchiveAsync, credentials through IAccountStore.RemoveAsync, settings through
// ISettingsStore and the IDataResetPart of each area. Open folder never takes a path from the browser: it takes a target and resolves it here.
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.CustomProviders;
using PiSharp.WebAgent.Evals.Arena;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Settings;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>What Open folder can open.</summary>
public static class DataOpenTargets
{
    /// <summary>The data folder.</summary>
    public const string Data = "data";

    /// <summary>The durable device's folder (chats).</summary>
    public const string Chats = "chats";

    /// <summary>The archive of deleted chats.</summary>
    public const string Archive = "archive";

    /// <summary>The folder of runs.db.</summary>
    public const string Runs = "runs";

    /// <summary>The Data Protection key ring.</summary>
    public const string Keys = "keys";

    /// <summary>The folder of credentials.json.</summary>
    public const string Credentials = "credentials";

    /// <summary>The compare workspaces' folder.</summary>
    public const string Workspaces = "workspaces";

    /// <summary>One workspace (with a run and a slot).</summary>
    public const string Workspace = "workspace";

    /// <summary>Every target.</summary>
    public static IReadOnlyList<string> All { get; } = [Data, Chats, Archive, Runs, Keys, Credentials, Workspaces, Workspace];
}

/// <summary>What removing workspaces did.</summary>
/// <param name="Removed">The workspaces removed ("cmp_01JBQ7 · b").</param>
/// <param name="Freed">The bytes they used.</param>
/// <param name="Kept">The workspaces kept, with why ("cmp_01JBQ7 · a: still running").</param>
/// <param name="Failed">The workspaces that could not be removed, with why.</param>
public sealed record DataWorkspaceRemoval(IReadOnlyList<string> Removed, long Freed, IReadOnlyList<string> Kept, IReadOnlyList<string> Failed);

/// <summary>What a danger zone action did.</summary>
/// <param name="Action"><c>credentials</c>, <c>history</c>, <c>chats</c> or <c>settings</c>.</param>
/// <param name="Count">How many things it deleted, archived or reset.</param>
/// <param name="Message">What to tell the user.</param>
/// <param name="Failed">What it could not do, with why.</param>
public sealed record DataDangerResult(string Action, int Count, string Message, IReadOnlyList<string> Failed);

/// <summary>What saving run history's settings did.</summary>
/// <param name="Settings">The saved settings.</param>
/// <param name="RemovedRuns">Runs the retention deleted at once.</param>
/// <param name="ExpiredRaw">Runs whose raw payloads were removed at once.</param>
public sealed record DataHistorySaved(RunHistorySettings Settings, int RemovedRuns, int ExpiredRaw);

/// <summary>Settings › Data &amp; storage's facts and operations. A singleton.</summary>
public sealed class DataStorageService(WebAgentOptions options, ISettingsStore settings, DataStorageOptions storage, IFolderOpener opener, DataExport export,
    IServiceProvider services, TimeProvider? time = null, ILogger<DataStorageService>? log = null)
{
    /// <summary>What a danger zone action needs typed: <c>DELETE</c>.</summary>
    public const string ConfirmWord = "DELETE";

    /// <summary>The prefix of the settings copy written before "Reset all settings".</summary>
    public const string BackupPrefix = "settings-backup-";

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>The app's options.</summary>
    public WebAgentOptions Options => options;

    /// <summary>The device's archive of deleted chats.</summary>
    public string ArchiveDirectory => Path.Join(options.DeviceDirectory, "archive");

    /// <summary>The export.</summary>
    public DataExport Export => export;

    private RunStore? Runs => services.GetService<RunStore>();

    private WorkspaceManager? Workspaces => services.GetService<WorkspaceManager>();

    private AgentCompareRunner? Runner => services.GetService<AgentCompareRunner>();

    private ChatBindings? Bindings => services.GetService<ChatBindings>();

    // ─── The page ───────────────────────────────────────────────────────────

    /// <summary>Measures everything the page shows.</summary>
    public async Task<DataPage> PageAsync(CancellationToken cancellationToken = default)
    {
        var workspaces = await WorkspaceFactsAsync(cancellationToken).ConfigureAwait(false);
        var (folder, usage, chats) = await Task.Run(() => Measure(workspaces, cancellationToken), cancellationToken).ConfigureAwait(false);
        var runs = RunFacts();
        var file = ExportFile();
        return new DataPage
        {
            Folder = folder,
            Usage = usage,
            Chats = chats,
            Runs = runs,
            Keys = KeyRing(),
            Credentials = CredentialFacts(),
            Workspaces = workspaces,
            Export = new DataExportFacts(file.FileName, Encoding.UTF8.GetByteCount(file.Text), file.Parts, file.Notes, chats.Count, ChatsFileName(clock.GetUtcNow())),
            Reset = ResetFacts(runs),
            OpenFolders = storage.OpenFolders,
            Opener = opener.Name,
            Existing = ExistingTargets(),
        };
    }

    private (DataFolderFacts Folder, DataUsage Usage, DataChatFacts Chats) Measure(DataWorkspaceFacts workspaces, CancellationToken cancellationToken)
    {
        var data = options.DataDirectory;
        var device = DataFolders.Measure(options.DeviceDirectory, cancellationToken);
        var archive = DataFolders.Measure(ArchiveDirectory, cancellationToken);
        var workspaceRoot = Workspaces?.Root;
        var inside = workspaceRoot is not null && DataFolders.IsWithin(data, workspaceRoot) && !DataFolders.IsWithin(workspaceRoot, data);
        var workspaceBytes = workspaces.Bytes;
        var rest = DataFolders.Measure(data, inside ? [options.DeviceDirectory, workspaceRoot!] : [options.DeviceDirectory], cancellationToken);
        var total = rest + device + (inside ? workspaceBytes : 0);
        var runs = DataFolders.DatabaseBytes(RunsPath);
        var keys = DataFolders.Measure(options.KeysDirectory, cancellationToken) + DataFolders.FileBytes(options.CredentialsPath) +
            DataFolders.DatabaseBytes(options.DatabasePath) + DataFolders.FileBytes(Path.Join(data, CustomProviderStore.FileName)) +
            DataFolders.FileBytes(Path.Join(data, CustomProviderStore.FileName + ".bak")) + Backups().Sum(DataFolders.FileBytes);
        var other = Math.Max(0, total - runs - device - (inside ? workspaceBytes : 0) - keys);
        var segments = new List<DataUsageSegment>();
        if (runs > 0) segments.Add(new DataUsageSegment("run history", runs, "seg-accent", "text-ps-accent"));
        if (inside && workspaceBytes > 0) segments.Add(new DataUsageSegment("compare workspaces", workspaceBytes, "seg-faint", "text-ps-faint", "#compare-workspaces"));
        if (device > 0) segments.Add(new DataUsageSegment("chats", device, "seg-blue", "text-ps-blue"));
        if (keys > 0) segments.Add(new DataUsageSegment("keys, credentials, settings", keys, "seg-ok", "text-ps-ok"));
        if (other > 0) segments.Add(new DataUsageSegment("other files", other, "", "text-ps-faint"));
        var (drive, free) = DataFolders.Drive(data);
        var folder = new DataFolderFacts(data, DataFolders.Display(data, folder: true), Source(), total, drive, free);
        var bindings = Bindings;
        var device_ = services.GetService<DeviceHostService>();
        var live = bindings?.List() ?? [];
        var chats = new DataChatFacts(bindings is not null, live.Count, device_ is null ? 0 : live.Count(binding => device_.IsBusy(binding.SessionId)),
            DataFolders.Display(options.DeviceDirectory, folder: true), device, Relative(ArchiveDirectory) + Path.DirectorySeparatorChar,
            ArchivedCount(), archive);
        return (folder, new DataUsage(total, segments), chats);
    }

    private DataFolderSource Source()
    {
        var environment = System.Environment.GetEnvironmentVariable(WebAgentOptions.DataEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment) && SamePath(Path.GetFullPath(environment.Trim()), options.DataDirectory)) return DataFolderSource.Environment;
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        return local.Length > 0 && SamePath(Path.Join(local, WebAgentOptions.DefaultFolderName), options.DataDirectory) ? DataFolderSource.Default : DataFolderSource.CommandLine;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), DataFolders.PathComparison);

    private string Relative(string path) => Path.GetRelativePath(options.DataDirectory, path);

    private string RunsPath => Runs?.Db.Path ?? Path.Join(options.DataDirectory, RunsDb.FileName);

    /// <summary>The settings copies written before a reset, oldest first.</summary>
    public IReadOnlyList<string> Backups()
    {
        try
        {
            return Directory.Exists(options.DataDirectory)
                ? [.. Directory.EnumerateFiles(options.DataDirectory, BackupPrefix + "*.json").Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The deleted chats in the archive (its folders).</summary>
    public int ArchivedCount()
    {
        try { return Directory.Exists(ArchiveDirectory) ? Directory.EnumerateDirectories(ArchiveDirectory).Count() : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>runs.db's facts, with what each retention the page offers would delete now. Runs are counted as History › Runs counts them
    /// (top-level runs: an agent compare run with its models and their calls is one), so both pages show the same number; the rows runs.db
    /// holds are <see cref="DataRunFacts.Records"/>.</summary>
    public DataRunFacts RunFacts()
    {
        var history = settings.Get<RunHistorySettings>();
        var display = DataFolders.Display(RunsPath, folder: false);
        if (Runs is not { } store) return new DataRunFacts(false, display, 0, 0, 0, 0, 0, 0, history, new Dictionary<int, long>());
        var count = store.CountRoots();
        var records = store.Count();
        var db = store.Db.Db;
        var pinned = store.CountRoots(pinned: true);
        var raw = db.Query("SELECT COALESCE(SUM(LENGTH(CAST(data AS BLOB))), 0), COUNT(DISTINCT run_id) FROM run_raw;",
            reader => (reader.GetInt64(0), reader.GetInt64(1)))[0];
        var events = Long(db.Scalar("SELECT COALESCE(SUM(LENGTH(CAST(event AS BLOB))), 0) FROM run_events;"));
        var now = clock.GetUtcNow();
        var older = DataRunFacts.Choices.OfType<int>().ToDictionary(days => days, days => PastRetention(store, now, days));
        return new DataRunFacts(true, display, count, pinned, store.SizeBytes(), raw.Item1, raw.Item2, events, history, older) { Records = records };
    }

    // Runs a retention of `days` would delete now: RunStore.Cleanup's own rule (unpinned, not running, not in a pinned run's tree).
    private static long PastRetention(RunStore store, DateTimeOffset now, int days) => store.CountPastRetention(TimeSpan.FromDays(days), now);

    private static long Long(object? value) => value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    /// <summary>The key ring's facts.</summary>
    public DataKeyRingFacts KeyRing()
    {
        var display = DataFolders.Display(options.KeysDirectory, folder: true);
        var dpapi = OperatingSystem.IsWindows();
        if (services.GetService<IKeyManager>() is { } manager)
        {
            try
            {
                var keys = manager.GetAllKeys();
                var now = clock.GetUtcNow();
                var active = keys.Where(key => !key.IsRevoked && key.ActivationDate <= now && key.ExpirationDate > now)
                    .OrderByDescending(key => key.ActivationDate).FirstOrDefault();
                return new DataKeyRingFacts(display, keys.Count, active?.ExpirationDate, dpapi);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
            {
                log?.LogWarning(error, "The key ring could not be read; the page counts its files instead.");
            }
        }
        int files;
        try { files = Directory.Exists(options.KeysDirectory) ? Directory.EnumerateFiles(options.KeysDirectory, "key-*.xml").Count() : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { files = 0; }
        return new DataKeyRingFacts(display, files, null, dpapi);
    }

    /// <summary>credentials.json's facts: what is stored, by provider name, and which environment variables provide keys.</summary>
    public DataCredentialFacts CredentialFacts()
    {
        var display = DataFolders.Display(options.CredentialsPath, folder: false);
        var exists = File.Exists(options.CredentialsPath);
        if (services.GetService<IAccountStore>() is not { } accounts) return new DataCredentialFacts(false, display, exists, 0, [], [], []);
        var all = accounts.List();
        var names = ProviderNames();
        string Name(string id) => names.GetValueOrDefault(id) ?? id;
        var keys = all.Where(account => account.Kind == AccountKind.ApiKey).Select(account => Name(account.Provider)).Distinct().ToList();
        var signIns = all.Where(account => account.Kind == AccountKind.OAuth).Select(account => Name(account.Provider)).Distinct().ToList();
        var environment = all.Where(account => account.Kind == AccountKind.Environment && account.Status != AccountStatus.Unavailable && account.Source is { Length: > 0 })
            .Select(account => account.Source!).Distinct(StringComparer.Ordinal).ToList();
        return new DataCredentialFacts(true, display, exists, all.Count(account => !account.ReadOnly), keys, signIns, environment);
    }

    private Dictionary<string, string> ProviderNames()
    {
        try
        {
            return services.GetService<ProviderDirectory>()?.List().ToDictionary(entry => entry.Id, entry => entry.Name, StringComparer.Ordinal) ?? [];
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>The compare workspaces, measured.</summary>
    public async Task<DataWorkspaceFacts> WorkspaceFactsAsync(CancellationToken cancellationToken = default)
    {
        if (Workspaces is not { } manager)
            return new DataWorkspaceFacts(false, DataFolders.Display(Path.Join(options.DataDirectory, WorkspaceManagerOptions.FolderName), folder: true), 0, false, [], TimeSpan.Zero);
        var runs = await manager.ListAsync(measure: true, cancellationToken).ConfigureAwait(false);
        var rows = new List<DataWorkspaceRow>();
        var blind = ArenaBlindSet.Of(services);
        foreach (var run in runs)
        {
            foreach (var slot in run.Slots.Where(slot => slot.State == WorkspaceSlotState.Ready))
                rows.Add(Row(run, slot, blind));
        }
        var bytes = await manager.MeasureRootAsync(cancellationToken).ConfigureAwait(false);
        return new DataWorkspaceFacts(true, DataFolders.Display(manager.Root, folder: true), bytes, Directory.Exists(manager.Root), rows, manager.Options.Retention);
    }

    private DataWorkspaceRow Row(WorkspaceRun run, WorkspaceSlot slot, ArenaBlindSet blind)
    {
        var compare = Runner?.Store.Get(run.Run);
        var spec = compare?.ModelSpec(slot.Slot);
        // W5 (fix 1, arena): an agent battle not voted yet names no model (slot a is its column A, so a slot's model names a contestant).
        var model = blind.Hides(run.Run) || blind.Hides(AgentCompareRun.ModelKey(run.Run, slot.Slot)) ? "blind round · workspace " + slot.Slot
            : spec is not null ? spec.Provider + "/" + spec.Model : slot.Label ?? "model " + slot.Slot.ToUpperInvariant();
        return new DataWorkspaceRow(run.Run, slot.Slot, model, run.Kind == WorkspaceKind.Worktree ? "git worktree" : "temporary copy", run.Kind == WorkspaceKind.Worktree,
            slot.Bytes, slot.AppliedAt, slot.Branch, run.ExpiresAt, run.ProjectPath, compare is not null, Busy(run.Run, slot.Slot));
    }

    // Why a workspace can't be removed now: the reasons AgentCompareRunner.DiscardAsync refuses with, or another user of the run.
    private string? Busy(string run, string slot)
    {
        if (Runner?.Store.Get(run) is { } compare)
        {
            if (compare.Model(slot) is not { } model) return null;
            if (model.Phase != AgentModelPhase.Done && compare.State != AgentRunState.Failed) return "still running";
            if (model.ChatId is { } chat && Bindings?.Get(chat) is { IsArchived: false }) return "continued as a chat";
            return null;
        }
        return services.GetServices<IWorkspaceActivity>().Any(activity => activity.IsActive(run)) ? "in use" : null;
    }

    /// <summary>The settings file as an export made now would be.</summary>
    public DataExportFile ExportFile() => export.Build();

    /// <summary>The file name of an "Export all chats" made at <paramref name="at"/>.</summary>
    public static string ChatsFileName(DateTimeOffset at) => $"pisharp-chats-{at.UtcDateTime:yyyy-MM-dd}.zip";

    private DataResetFacts ResetFacts(DataRunFacts runs)
    {
        var changes = services.GetServices<IDataResetPart>().Select(part => part.ResetText()).Where(text => text.Length > 0).ToList();
        var now = runs.Settings.RetentionDays;
        var after = RunHistorySettings.CreateDefault().RetentionDays;
        var changes_ = now != after;
        long past = 0;
        if (changes_ && after is { } days && Runs is { } store) past = runs.OlderThan.TryGetValue(days, out var known) ? known : PastRetention(store, clock.GetUtcNow(), days);
        return new DataResetFacts(changes, after, past) { RetentionChanges = changes_ };
    }

    // ─── Open folder ────────────────────────────────────────────────────────

    /// <summary>Opens <paramref name="target"/> in the file manager; the folder as the page shows it.</summary>
    /// <exception cref="ArgumentException">An unknown target, or a workspace without a valid run and slot.</exception>
    /// <exception cref="KeyNotFoundException">The folder doesn't exist (yet).</exception>
    /// <exception cref="InvalidOperationException">Opening folders is turned off, or the file manager could not start.</exception>
    public async Task<string> OpenAsync(string? target, string? run, string? slot, CancellationToken cancellationToken = default)
    {
        if (!storage.OpenFolders) throw new InvalidOperationException($"Opening folders is turned off for this app ({DataStorageOptions.OpenFoldersKey}).");
        var folder = await ResolveAsync(target, run, slot, cancellationToken).ConfigureAwait(false);
        opener.Open(folder);
        return DataFolders.Display(folder, folder: true);
    }

    /// <summary>The folder <paramref name="target"/> names (an existing folder inside the data folder or the workspaces folder).</summary>
    public async Task<string> ResolveAsync(string? target, string? run, string? slot, CancellationToken cancellationToken = default)
    {
        string folder;
        switch (target)
        {
            case DataOpenTargets.Data or DataOpenTargets.Runs or DataOpenTargets.Credentials:
                folder = options.DataDirectory;
                break;
            case DataOpenTargets.Chats:
                folder = options.DeviceDirectory;
                break;
            case DataOpenTargets.Archive:
                folder = ArchiveDirectory;
                break;
            case DataOpenTargets.Keys:
                folder = options.KeysDirectory;
                break;
            case DataOpenTargets.Workspaces:
                folder = (Workspaces ?? throw new KeyNotFoundException("Agent compare's workspaces are not part of this app.")).Root;
                break;
            case DataOpenTargets.Workspace:
                var manager = Workspaces ?? throw new KeyNotFoundException("Agent compare's workspaces are not part of this app.");
                if (!WorkspaceManager.IsValidRun(run) || !WorkspaceManager.IsValidSlot(slot)) throw new ArgumentException("Name a workspace's run and model.");
                var found = await manager.GetAsync(run!, measure: false, cancellationToken).ConfigureAwait(false);
                var entry = found?.Find(slot!) is { State: WorkspaceSlotState.Ready } ready ? ready : throw new KeyNotFoundException($"There is no workspace {run} · {slot}.");
                folder = entry.Path;
                if (!DataFolders.IsWithin(manager.Root, folder)) throw new KeyNotFoundException($"There is no workspace {run} · {slot}.");
                break;
            default:
                throw new ArgumentException("Choose a folder to open: " + string.Join(", ", DataOpenTargets.All) + ".");
        }
        if (!Directory.Exists(folder) || DataFolders.IsLink(folder)) throw new KeyNotFoundException("That folder doesn't exist yet.");
        return Path.GetFullPath(folder);
    }

    /// <summary>The Open folder targets (all but a single workspace) whose folders exist now.</summary>
    public IReadOnlySet<string> ExistingTargets()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (target, folder) in new[]
        {
            (DataOpenTargets.Data, options.DataDirectory), (DataOpenTargets.Runs, options.DataDirectory), (DataOpenTargets.Credentials, options.DataDirectory),
            (DataOpenTargets.Chats, options.DeviceDirectory), (DataOpenTargets.Archive, ArchiveDirectory), (DataOpenTargets.Keys, options.KeysDirectory),
            (DataOpenTargets.Workspaces, Workspaces?.Root),
        })
        {
            if (folder is not null && Directory.Exists(folder) && !DataFolders.IsLink(folder)) found.Add(target);
        }
        return found;
    }

    // ─── Run history ────────────────────────────────────────────────────────

    /// <summary>Saves run history's settings and applies the retention now (runs past it are deleted, as the hourly cleanup would).</summary>
    /// <exception cref="ArgumentException">A retention outside 1 to 3,650 days.</exception>
    public async Task<DataHistorySaved> SaveHistoryAsync(RunHistorySettings value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var saved = await settings.SaveAsync(value.Normalize(), cancellationToken).ConfigureAwait(false);
        var cleanup = Runs?.Cleanup(saved, clock.GetUtcNow()) ?? new RunCleanup(0, 0);
        log?.LogInformation("Run history settings saved (retention {Days}, raw {Raw}, events {Events}); {Runs} runs removed.", saved.RetentionDays, saved.StoreRawPayloads,
            saved.StoreSseEvents, cleanup.Runs);
        return new DataHistorySaved(saved, cleanup.Runs, cleanup.RawRuns);
    }

    // ─── Workspaces ─────────────────────────────────────────────────────────

    /// <summary>Removes one workspace, or (no slot) every workspace of a run.</summary>
    /// <exception cref="ArgumentException">Not a run or slot name.</exception>
    /// <exception cref="KeyNotFoundException">No such workspace.</exception>
    /// <exception cref="AgentCompareException">Compare refuses (still running, continued as a chat): 409.</exception>
    /// <exception cref="InvalidOperationException">Another part of the app uses the run.</exception>
    public async Task<DataWorkspaceRemoval> RemoveWorkspaceAsync(string? run, string? slot, CancellationToken cancellationToken = default)
    {
        var manager = Workspaces ?? throw new KeyNotFoundException("Agent compare's workspaces are not part of this app.");
        if (!WorkspaceManager.IsValidRun(run)) throw new ArgumentException("Name the run of the workspace to remove.");
        if (slot is not null && !WorkspaceManager.IsValidSlot(slot)) throw new ArgumentException("Name the model of the workspace to remove.");
        var found = await manager.GetAsync(run!, measure: true, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"There are no workspaces of {run}.");
        var slots = found.Slots.Where(entry => entry.State == WorkspaceSlotState.Ready && (slot is null || string.Equals(entry.Slot, slot, StringComparison.OrdinalIgnoreCase))).ToList();
        if (slots.Count == 0) throw new KeyNotFoundException($"There is no workspace {run} · {slot}.");
        var freed = slots.Sum(entry => entry.Bytes ?? 0) + (slot is null || found.Slots.Count(entry => entry.State == WorkspaceSlotState.Ready) == 1 ? found.BaseBytes ?? 0 : 0);
        if (Runner is { } runner && runner.Store.Get(run!) is not null)
        {
            await runner.DiscardAsync(run!, slot is null ? null : slots[0].Slot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (services.GetServices<IWorkspaceActivity>().Any(activity => activity.IsActive(run!)))
                throw new InvalidOperationException($"The run {run} is still in use, so its workspaces are kept.");
            if (slot is null) await manager.DiscardRunAsync(run!, cancellationToken).ConfigureAwait(false);
            else await manager.DiscardAsync(run!, slots[0].Slot, cancellationToken).ConfigureAwait(false);
        }
        log?.LogInformation("Compare workspaces removed from Settings › Data: {Run} {Slot}.", run, slot ?? "(all)");
        return new DataWorkspaceRemoval([.. slots.Select(entry => run + " · " + entry.Slot)], freed, [], []);
    }

    /// <summary>Removes every workspace that is not in use (Clean up); those in use are kept and listed.</summary>
    public async Task<DataWorkspaceRemoval> CleanUpWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        var manager = Workspaces ?? throw new KeyNotFoundException("Agent compare's workspaces are not part of this app.");
        var removed = new List<string>();
        var kept = new List<string>();
        var failed = new List<string>();
        long freed = 0;
        foreach (var run in await manager.ListAsync(measure: false, cancellationToken).ConfigureAwait(false))
        {
            var ready = run.Slots.Where(entry => entry.State == WorkspaceSlotState.Ready).ToList();
            var busy = ready.Select(entry => (entry.Slot, Reason: Busy(run.Run, entry.Slot))).Where(pair => pair.Reason is not null).ToList();
            kept.AddRange(busy.Select(pair => $"{run.Run} · {pair.Slot}: {pair.Reason}"));
            var free = ready.Where(entry => busy.All(pair => pair.Slot != entry.Slot)).ToList();
            if (free.Count == 0) continue;
            // A run with nothing in use goes whole (its base copy too); otherwise each free workspace on its own.
            var whole = busy.Count == 0;
            foreach (var target in whole ? [null] : free.Select(entry => (string?)entry.Slot))
            {
                try
                {
                    var result = await RemoveWorkspaceAsync(run.Run, target, cancellationToken).ConfigureAwait(false);
                    removed.AddRange(result.Removed);
                    freed += result.Freed;
                }
                catch (Exception error) when (error is AgentCompareException or InvalidOperationException or KeyNotFoundException or WorkspaceException or IOException
                    or UnauthorizedAccessException)
                {
                    failed.Add($"{run.Run}{(target is null ? "" : " · " + target)}: {error.Message}");
                }
            }
        }
        return new DataWorkspaceRemoval(removed, freed, kept, failed);
    }

    // ─── Danger zone ────────────────────────────────────────────────────────

    /// <summary>Checks the typed confirmation.</summary>
    /// <exception cref="ArgumentException">It is not exactly <see cref="ConfirmWord"/>.</exception>
    public static void RequireConfirm(string? typed)
    {
        if (!string.Equals(typed, ConfirmWord, StringComparison.Ordinal)) throw new ArgumentException($"Type {ConfirmWord} to confirm.");
    }

    /// <summary>Deletes every stored API key and sign-in (environment variables and ambient credentials stay; so does the key ring).</summary>
    public async Task<DataDangerResult> DeleteCredentialsAsync(string? confirm, CancellationToken cancellationToken = default)
    {
        RequireConfirm(confirm);
        var accounts = services.GetService<IAccountStore>() ?? throw new InvalidOperationException("Credentials are not part of this app.");
        var removed = 0;
        var failed = new List<string>();
        foreach (var account in accounts.List().Where(account => !account.ReadOnly))
        {
            try
            {
                await accounts.RemoveAsync(account.Id, cancellationToken).ConfigureAwait(false);
                removed++;
            }
            catch (KeyNotFoundException) { /* Removed meanwhile. */ }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            {
                failed.Add($"{account.Label}: {SecretText.Scrub(error.Message)}");
            }
        }
        log?.LogInformation("Settings › Data: {Count} stored credentials deleted.", removed);
        var left = accounts.List().Count(account => account.Kind == AccountKind.Environment && account.Status != AccountStatus.Unavailable);
        return new DataDangerResult("credentials", removed,
            $"Deleted {Plural(removed, "stored credential")}." + (left > 0 ? $" {Plural(left, "environment variable")} still provide{(left == 1 ? "s" : "")} keys." : ""), failed);
    }

    /// <summary>Deletes every run (pinned ones too) with its events and raw payloads, and compacts runs.db. The result counts runs as the
    /// page and History count them (top-level runs), so it says the number the confirmation named. Agent compare runs are runs too (W4 fix
    /// 2): each is deleted with its sessions and workspaces (AgentCompareRunner.DeleteAllAsync; a model continued as a chat keeps both), and
    /// the answer waits for those deletes.</summary>
    public async Task<DataDangerResult> ClearHistoryAsync(string? confirm, CancellationToken cancellationToken = default)
    {
        RequireConfirm(confirm);
        var store = Runs ?? throw new InvalidOperationException("Run history is not part of this app.");
        var before = store.SizeBytes();
        var runs = store.CountRoots();
        var rows = store.Clear(keepPinned: false);
        var deleted = (int)Math.Min(runs, rows); // Never more than were deleted (a retention cleanup may have run in between).
        var failed = new List<string>();
        if (Runner is { } runner)
        {
            foreach (var run in await runner.DeleteAllAsync(cancellationToken).ConfigureAwait(false))
                if (!run.Deleted && run.Failed.Count > 0)
                    failed.Add($"Agent compare run {run.Id} was only partly deleted ({string.Join(", ", run.Failed)}); the rest goes when the app restarts.");
        }
        try
        {
            store.Db.Db.Execute("VACUUM;");
            store.Db.Db.Scalar("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            log?.LogWarning(error, "runs.db could not be compacted after Clear history.");
            failed.Add("runs.db could not be compacted now (it is in use); it shrinks after the app restarts.");
        }
        var freed = Math.Max(0, before - store.SizeBytes());
        log?.LogInformation("Settings › Data: run history cleared ({Count} runs, {Rows} records, {Bytes} bytes freed).", deleted, rows, freed);
        var records = rows > deleted ? $" ({Plural(rows, "record")})" : "";
        return new DataDangerResult("history", deleted, $"Deleted {Plural(deleted, "run")}{records} and freed {DataFolders.Size(freed)}.", failed);
    }

    /// <summary>Stops and archives every chat (they stay in the archive for 30 days).</summary>
    public async Task<DataDangerResult> DeleteChatsAsync(string? confirm, CancellationToken cancellationToken = default)
    {
        RequireConfirm(confirm);
        var chats = services.GetService<ChatService>() ?? throw new InvalidOperationException("Chats are not part of this app.");
        var bindings = Bindings ?? throw new InvalidOperationException("Chats are not part of this app.");
        var archived = 0;
        var failed = new List<string>();
        foreach (var binding in bindings.List())
        {
            try
            {
                await chats.ArchiveAsync(binding.SessionId, cancellationToken).ConfigureAwait(false);
                archived++;
            }
            catch (Exception error) when (error is ChatException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failed.Add($"{binding.Title}: {error.Message}");
            }
        }
        log?.LogInformation("Settings › Data: {Count} chats archived.", archived);
        return new DataDangerResult("chats", archived,
            $"Moved {Plural(archived, "chat")} to the archive. They are purged after {DataStorageOptions.ArchiveRetentionDays} days.", failed);
    }

    /// <summary>Writes a copy of the settings (an export file) into the data folder, then sets every settings part back to its defaults.
    /// Credentials, chats and history are kept.</summary>
    public async Task<DataDangerResult> ResetSettingsAsync(string? confirm, CancellationToken cancellationToken = default)
    {
        RequireConfirm(confirm);
        var backup = Path.Join(options.DataDirectory, $"{BackupPrefix}{clock.GetUtcNow().UtcDateTime:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(backup, export.Build().Text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var parts = services.GetServices<IDataResetPart>().ToList();
        var reset = 0;
        var failed = new List<string>();
        foreach (var part in parts)
        {
            try
            {
                await part.ResetAsync(cancellationToken).ConfigureAwait(false);
                reset++;
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
            {
                failed.Add(error.Message);
            }
        }
        log?.LogInformation("Settings › Data: settings reset; a copy is in {Backup}.", Path.GetFileName(backup));
        return new DataDangerResult("settings", reset,
            $"Every setting is back to its default. A copy of the old ones is in {Path.GetFileName(backup)} in the data folder; import it to undo.", failed);
    }

    // ─── Export all chats ───────────────────────────────────────────────────

    /// <summary>A zip with a Markdown transcript of each chat (deleted chats are not included); a chat whose session can't be read is
    /// listed in <c>not-exported.txt</c>.</summary>
    public async Task<(byte[] Zip, string FileName, int Chats)> ExportChatsAsync(CancellationToken cancellationToken = default)
    {
        var chats = services.GetService<ChatService>() ?? throw new InvalidOperationException("Chats are not part of this app.");
        var bindings = Bindings ?? throw new InvalidOperationException("Chats are not part of this app.");
        var list = bindings.List();
        if (list.Count == 0) throw new KeyNotFoundException("There are no chats to export.");
        using var buffer = new MemoryStream();
        var exported = 0;
        var missing = new List<string>();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in list)
            {
                string markdown;
                try { markdown = await chats.ExportMarkdownAsync(binding.SessionId, null, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is ChatException or InvalidOperationException or IOException or PiSharp.Durable.Hosting.DurableHostException)
                {
                    missing.Add($"{binding.Title} ({binding.SessionId}): {error.Message}");
                    continue;
                }
                var name = EntryName(binding.Title, binding.SessionId, names);
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = binding.Updated;
                await using (var stream = entry.Open())
                {
                    var bytes = Encoding.UTF8.GetBytes(markdown);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                exported++;
            }
            if (missing.Count > 0)
            {
                var entry = zip.CreateEntry("not-exported.txt");
                await using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes("These chats could not be read, so they are not in this zip:\n" + string.Join("\n", missing) + "\n");
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        return (buffer.ToArray(), ChatsFileName(clock.GetUtcNow()), exported);
    }

    private static string EntryName(string title, string id, HashSet<string> taken)
    {
        var safe = new StringBuilder();
        foreach (var character in title.Trim())
        {
            if (safe.Length >= 60) break;
            safe.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ' ' ? character : '-');
        }
        var stem = safe.ToString().Trim(' ', '.', '-');
        if (stem.Length == 0) stem = "chat";
        var name = $"{stem} ({(id.Length > 8 ? id[..8] : id)}).md";
        for (var n = 2; !taken.Add(name); n++) name = $"{stem} ({(id.Length > 8 ? id[..8] : id)}-{n}).md";
        return name;
    }

    /// <summary>"1 run", "3 runs".</summary>
    public static string Plural(long count, string one, string? many = null) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many ?? one + "s");
}
