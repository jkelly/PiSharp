// PiSharp-only (decision 0008): the device side of durable-pi's dpi-backend (apps/dpi-backend/src/store.ts), without HTTP. One process hosts
// every session of a data directory: one Harness on one SQLite file per session, a catalog beside them, and change notifications for the
// device server's streams.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using PiSharp.Durable.Tools;

namespace PiSharp.Durable.Hosting;

/// <summary>
/// Hosts every durable session of one data directory in this process. Open it with <see cref="OpenAsync"/>; only one process may host a data
/// directory at a time. A session that fails to open is listed with its error and answers <see cref="SessionUnavailableException"/>; the
/// others are not affected.
/// </summary>
public sealed partial class DurableDeviceHost : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly List<HostedSession> sessions = [];
    private readonly List<Action<DeviceChange>> listeners = [];
    private readonly FileLock deviceLock;
    private int disposed;
    private Task? disposal;

    private DurableDeviceHost(DurableDeviceOptions options, FileLock deviceLock, SessionCatalog catalog, DeviceInfo info)
    {
        Options = options;
        this.deviceLock = deviceLock;
        Catalog = catalog;
        Info = info;
        AppState = new DeviceAppState(this);
        Blobs = new BlobStore(Path.Join(info.DataDirectory, "blobs"), catalog, options.TimeProvider);
        Budgets = new BudgetLedger(catalog, options.TimeProvider);
        Policies = new PolicyStore(catalog, options.Models, options.TimeProvider);
        Sources = new SourceStore(catalog, options.Retrieval, options.TimeProvider);
        Preferences = new PreferenceStore(catalog, options.TimeProvider);
        Retention = new DeviceRetention(this);
        DirectRetrieval = new DirectRetrievalProvider(options.Retrieval, options.TimeProvider);
        ApplicationJobs = ApplicationJobDefinitions.Create(this);
        AllJobs = [.. options.Jobs, .. ApplicationJobs];
    }

    /// <summary>What the device is.</summary>
    public DeviceInfo Info { get; }

    /// <summary>The device's shared App State.</summary>
    public DeviceAppState AppState { get; }

    /// <summary>The durable jobs of the device.</summary>
    public IReadOnlyList<JobDefinition> Jobs => Options.Jobs;

    /// <summary>The host's application jobs (decision 0010): judgment, source checks and research. They run and schedule like
    /// <see cref="Jobs"/>, but their IDs are reserved and agents do not list them.</summary>
    public IReadOnlyList<JobDefinition> ApplicationJobs { get; }

    /// <summary>The device's content-addressed blob store.</summary>
    public BlobStore Blobs { get; }

    /// <summary>The device's reservation and usage ledger.</summary>
    public BudgetLedger Budgets { get; }

    /// <summary>The device's judgment policies.</summary>
    public PolicyStore Policies { get; }

    /// <summary>The device's monitored sources.</summary>
    public SourceStore Sources { get; }

    /// <summary>The device's feedback events and preference profiles.</summary>
    public PreferenceStore Preferences { get; }

    /// <summary>Retention of the catalog's settled application records.</summary>
    public DeviceRetention Retention { get; }

    /// <summary>Direct HTTP retrieval under the device's address policy.</summary>
    internal DirectRetrievalProvider DirectRetrieval { get; }

    /// <summary><see cref="Jobs"/> and <see cref="ApplicationJobs"/>.</summary>
    internal IReadOnlyList<JobDefinition> AllJobs { get; }

    /// <summary>Where the <c>system-prompts</c> extension finds its prompt files.</summary>
    public string PromptsDirectory => Options.PromptsDirectory ?? Path.Join(Info.DataDirectory, "prompts");

    internal DurableDeviceOptions Options { get; }
    internal SessionCatalog Catalog { get; }
    internal string SessionsDirectory => Path.Join(Info.DataDirectory, "sessions");
    internal string ArchiveDirectory => Path.Join(Info.DataDirectory, "archive");

    /// <summary>Every session, oldest first, including unavailable ones.</summary>
    public IReadOnlyList<HostedSession> Sessions
    {
        get { lock (gate) return [.. sessions]; }
    }

    /// <summary>
    /// Locks the data directory, opens the catalog and every session in it, re-routes stored models the device does not know to
    /// <see cref="DurableDeviceOptions.DefaultModel"/>, and resumes the work each session left unfinished.
    /// </summary>
    /// <exception cref="DeviceLockedException">Another process hosts the data directory.</exception>
    public static async Task<DurableDeviceHost> OpenAsync(DurableDeviceOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Models);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
        if (options.StartDirectories is not { Count: > 0 }) throw new ArgumentException("At least one start directory is required.", nameof(options));
        if (options.DefaultModel is { } fallback && options.Models.GetModel(fallback.Provider, fallback.ModelId) is null)
            throw new ArgumentException($"The default model {fallback.Provider}/{fallback.ModelId} is not one of the device's models.", nameof(options));
        HostNames.Thinking(options.DefaultThinkingLevel);
        if (options.FeedCapacity < 1) throw new ArgumentOutOfRangeException(nameof(options), "FeedCapacity must be at least 1.");
        if (options.SchedulePollInterval <= TimeSpan.Zero || options.SchedulePollInterval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "SchedulePollInterval must be positive and no greater than 2147483647 milliseconds.");
        if (options.Jobs.Select(job => job.Id).Distinct(StringComparer.Ordinal).Count() != options.Jobs.Count ||
            options.Jobs.Any(job => string.IsNullOrWhiteSpace(job.Id) || job.Phases.Count == 0 ||
                job.Phases.Select(phase => phase.Id).Distinct(StringComparer.Ordinal).Count() != job.Phases.Count ||
                job.RequiredChecks.Any(check => string.IsNullOrWhiteSpace(check) || check.Length > 100) ||
                job.RequiredChecks.Distinct(StringComparer.Ordinal).Count() != job.RequiredChecks.Count))
            throw new ArgumentException("Jobs require unique IDs, nonempty unique phases and valid audit check names.", nameof(options));
        if (options.Retrieval.Limits is { MaxResponseBytes: < 1024 or > 64 * 1024 * 1024 } or { MaxTextCharacters: < 1024 or > 4 * 1024 * 1024 } or
            { MaxRedirects: < 0 or > 20 } or { MaxAttempts: < 1 or > 10 } or { MaxPages: < 1 or > 20 } ||
            options.Retrieval.Limits.Timeout <= TimeSpan.Zero || options.Retrieval.Limits.Timeout > TimeSpan.FromMinutes(10) ||
            options.Retrieval.Limits.MaxRetryWait < TimeSpan.Zero || options.Retrieval.Limits.MaxRetryWait > TimeSpan.FromDays(1) ||
            !Uri.TryCreate(options.Retrieval.GitHubApi, UriKind.Absolute, out var githubApi) || githubApi.Scheme is not ("http" or "https") ||
            options.Retrieval.Fallbacks.Any(provider => provider.Id is null or "" or DirectRetrievalProvider.ProviderId || provider.Id.Length > 100) ||
            options.Retrieval.Fallbacks.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count() != options.Retrieval.Fallbacks.Count)
            throw new ArgumentException("Retrieval limits must be within their hard ceilings, the GitHub API an http(s) URL, and fallback IDs unique, " +
                "nonempty and not \"direct\".", nameof(options));
        if (options.Jobs.FirstOrDefault(job => job.Id.StartsWith(ApplicationJobDefinitions.Prefix, StringComparison.Ordinal)) is { } reserved)
            throw new ArgumentException($"Job IDs starting with {ApplicationJobDefinitions.Prefix} are reserved: {reserved.Id}.", nameof(options));
        foreach (var job in options.Jobs.Where(job => job.InputSchema is not null))
        {
            var problems = InputSchema.Check(job.InputSchema!);
            if (problems.Count > 0 || job.MaxInputBytes is < 2 or > 256 * 1024)
                throw new ArgumentException($"Job {job.Id} has an invalid input schema: " + string.Join("; ", problems.Take(3).Select(problem => problem.Path + " " + problem.Message)) +
                    (job.MaxInputBytes is < 2 or > 256 * 1024 ? " MaxInputBytes must be 2 to 262144." : ""), nameof(options));
        }

        var data = Path.GetFullPath(ExpandHome(options.DataDirectory));
        Directory.CreateDirectory(data);
        var starts = options.StartDirectories.Select(start => Path.GetFullPath(ExpandHome(start))).Distinct(PathComparer).ToList();
        foreach (var start in starts) Directory.CreateDirectory(start);

        var deviceLock = FileLock.TryAcquire(Path.Join(data, "catalog.lock"), $"pid {Environment.ProcessId}")
            ?? throw new DeviceLockedException($"Another process hosts the durable device at {data}.");
        SessionCatalog catalog;
        try
        {
            catalog = SessionCatalog.Open(Path.Join(data, "catalog.sqlite"));
        }
        catch
        {
            deviceLock.Dispose();
            throw;
        }
        var info = new DeviceInfo(catalog.DeviceUid(), options.DeviceName is { Length: > 0 } name ? name : Environment.MachineName,
            Environment.MachineName, Platform(), starts, data, options.TimeProvider.GetUtcNow(), options.DefaultModel);
        var host = new DurableDeviceHost(options, deviceLock, catalog, info);
        try
        {
            Directory.CreateDirectory(host.SessionsDirectory);
            foreach (var row in catalog.Sessions())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = new HostedSession(host, row);
                lock (host.gate) host.sessions.Add(session);
                if (row.ArchivePath is { } archive)
                {
                    try { host.FinishArchive(session, archive); }
                    catch (Exception failure)
                    {
                        session.MarkUnavailable(failure);
                        options.OnReport?.Invoke(row.Id, failure);
                    }
                    continue;
                }
                await session.OpenAsync(row.InitialThinking is null ? null : new ModelRef(row.Provider!, row.Model!), row.InitialThinking)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        host.StartSchedules();
        return host;
    }

    /// <summary>Every session's summary, oldest first.</summary>
    public async Task<IReadOnlyList<HostedSessionSummary>> SessionSummariesAsync(CancellationToken cancellationToken = default)
    {
        var summaries = new List<HostedSessionSummary>();
        foreach (var session in Sessions) summaries.Add(await session.SummaryAsync(cancellationToken).ConfigureAwait(false));
        return summaries;
    }

    /// <summary>The session with this UUID, else the one with this case-insensitive name.</summary>
    /// <exception cref="HostNotFoundException">No such session.</exception>
    /// <exception cref="HostAmbiguousException">Several sessions have the name.</exception>
    public HostedSession ResolveSession(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var trimmed = address.Trim();
        var all = Sessions;
        if (all.FirstOrDefault(session => string.Equals(session.Id, trimmed, StringComparison.OrdinalIgnoreCase)) is { } byId) return byId;
        var byName = all.Where(session => string.Equals(session.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            0 => throw new HostNotFoundException($"No session {trimmed} on {Info.Name}."),
            1 => byName[0],
            _ => throw new HostAmbiguousException($"{byName.Count} sessions on {Info.Name} are called {trimmed}. Use the session's ID."),
        };
    }

    /// <summary>Creates a session: a catalog row, a directory with its database, and Main with the chosen model and thinking level. With a
    /// <see cref="SessionCreateRequest.SpawnKey"/> that already created a session, returns that session instead.</summary>
    /// <exception cref="HostInvalidException">An unknown model or thinking level, or a working directory that does not exist.</exception>
    public async Task<HostedSession> CreateSessionAsync(SessionCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (request.SpawnKey is { Length: 0 or > 256 }) throw new HostInvalidException("A spawn key must be 1 to 256 characters.");
        if (request.SpawnRequest is not null && request.SpawnKey is null)
            throw new HostInvalidException("A request identity requires a spawn key.");
        var model = request.Model ?? Options.DefaultModel ?? throw new HostInvalidException($"Choose a model: {Info.Name} has no default model.");
        RequireModel(model);
        var thinking = HostNames.Thinking(request.ThinkingLevel ?? Options.DefaultThinkingLevel);
        var cwd = ResolveDirectory(request.Cwd);
        var name = HostNames.Session(request.Name);
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (request.SpawnKey is { } key && Catalog.SpawnedSession(key, request.SpawnRequest) is { } spawned)
                return Sessions.FirstOrDefault(session => session.Id == spawned) ?? throw new HostNotFoundException($"The session spawned by {key} is gone.");
            var row = new CatalogSession(Guid.NewGuid().ToString("D"), name, cwd, Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                model.Provider, model.ModelId, null, thinking);
            Catalog.AddSession(row, request.SpawnKey, request.SpawnRequest);
            var session = new HostedSession(this, row);
            lock (gate) sessions.Add(session);
            await session.OpenAsync(model, thinking).ConfigureAwait(false);
            Notify(new DeviceChange(DeviceChangeKind.Catalog, session.Id));
            return session;
        }
        finally
        {
            mutations.Release();
        }
    }

    /// <summary>Renames a session; blank is "New session".</summary>
    public async Task<HostedSession> RenameSessionAsync(string address, string? name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var session = ResolveSession(address);
            var trimmed = HostNames.Session(name);
            Catalog.RenameSession(session.Id, trimmed);
            session.Name = trimmed;
            Notify(new DeviceChange(DeviceChangeKind.Catalog, session.Id));
            return session;
        }
        finally
        {
            mutations.Release();
        }
    }

    /// <summary>
    /// Archives a session: stops all of its work (background too), closes it, moves its directory to <c>archive/&lt;id&gt;-&lt;time&gt;</c>
    /// and removes its catalog rows. Nothing is deleted. Returns the archive directory.
    /// </summary>
    public async Task<string> ArchiveSessionAsync(string address, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var session = ResolveSession(address);
            await session.StopAndCloseAsync().ConfigureAwait(false);
            try
            {
                var target = Catalog.ArchivePath(session.Id) ??
                    Path.Join(ArchiveDirectory, $"{session.Id}-{Options.TimeProvider.GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}");
                Catalog.BeginArchive(session.Id, target);
                FinishArchive(session, target);
                return target;
            }
            catch (Exception failure)
            {
                session.MarkUnavailable(failure);
                throw;
            }
        }
        finally
        {
            mutations.Release();
        }
    }

    private void FinishArchive(HostedSession session, string target)
    {
        var sourceExists = Directory.Exists(session.DirectoryPath);
        var targetExists = Directory.Exists(target);
        if (sourceExists && targetExists)
            throw new IOException($"Archive destination already exists: {target}");
        if (!sourceExists && !targetExists)
            throw new IOException($"Neither the session directory nor its archive exists for {session.Id}.");
        if (sourceExists)
        {
            Directory.CreateDirectory(ArchiveDirectory);
            Directory.Move(session.DirectoryPath, target);
        }
        Catalog.RemoveSession(session.Id);
        lock (gate) sessions.Remove(session);
        Notify(new DeviceChange(DeviceChangeKind.Catalog, session.Id));
    }

    /// <summary>The chat models the device can run now: the models' available catalog (providers with complete auth).</summary>
    public async Task<IReadOnlyList<DeviceModel>> ModelsAsync(CancellationToken cancellationToken = default) =>
        [.. (await Options.Models.GetAvailableOfTypeAsync("chat", null, cancellationToken).ConfigureAwait(false))
            .Select(DeviceModel.FromJson).OfType<DeviceModel>()];

    /// <summary>
    /// The subdirectories of <paramref name="path"/> for choosing a working directory, or the start directories for a blank path (durable-pi
    /// <c>GET /v1/fs/dirs</c>). Directories whose names start with a dot, and <c>node_modules</c>, are left out; at most 500, by name.
    /// </summary>
    /// <exception cref="HostNotFoundException">No such directory.</exception>
    public DirectoryListing BrowseDirectories(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new DirectoryListing("", null, [.. Info.StartDirectories.Select(start => new DirectoryEntry(DisplayName(start), start))]);
        var full = Path.GetFullPath(ExpandHome(path.Trim()));
        if (!Directory.Exists(full)) throw new HostNotFoundException($"No directory {full} on {Info.Name}.");
        var directory = new DirectoryInfo(full);
        try
        {
            var children = directory.EnumerateDirectories()
                .Where(child => !child.Name.StartsWith('.') && child.Name != "node_modules")
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .Select(child => new DirectoryEntry(child.Name, child.FullName))
                .ToList();
            return new DirectoryListing(directory.FullName, directory.Parent?.FullName, children);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            throw new DurableHostException("unavailable", $"Cannot list {full}: {error.Message}", error);
        }
    }

    /// <summary>Calls <paramref name="listener"/> for every change, on the thread that made it; keep it short. Dispose to stop.</summary>
    public IDisposable SubscribeChanges(Action<DeviceChange> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate) listeners.Add(listener);
        return new Unsubscriber(this, listener);
    }

    /// <summary>
    /// Changes in batches: after the first change of a batch, waits <paramref name="coalesce"/> and yields every distinct change since
    /// (durable-pi coalesces device streams to 250 ms). Ends when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyCollection<DeviceChange>> WatchChangesAsync(TimeSpan coalesce,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<DeviceChange>(new UnboundedChannelOptions { SingleReader = true });
        using var subscription = SubscribeChanges(change => channel.Writer.TryWrite(change));
        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (coalesce > TimeSpan.Zero) await Task.Delay(coalesce, Options.TimeProvider, cancellationToken).ConfigureAwait(false);
            var batch = new HashSet<DeviceChange>();
            while (channel.Reader.TryRead(out var change)) batch.Add(change);
            if (batch.Count > 0) yield return batch;
        }
    }

    /// <summary>Closes every session and releases the data directory.</summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is null)
            {
                Volatile.Write(ref disposed, 1);
                disposal = DisposeCoreAsync();
            }
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        scheduleStop.Cancel();
        await scheduleLoop.ConfigureAwait(false);
        // Close admission first, then join the already admitted create/rename/archive operation. Keep the semaphore alive so
        // queued callers can acquire it, see the closed admission, and release it safely.
        await mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            HostedSession[] all;
            lock (gate) { all = [.. sessions]; sessions.Clear(); listeners.Clear(); }
            try { foreach (var session in all) await session.CloseAsync().ConfigureAwait(false); }
            finally
            {
                try { Catalog.Dispose(); DirectRetrieval.Dispose(); }
                finally { deviceLock.Dispose(); scheduleStop.Dispose(); }
            }
        }
        finally { mutations.Release(); }
    }

    internal void Notify(DeviceChange change)
    {
        Action<DeviceChange>[] targets;
        lock (gate) targets = [.. listeners];
        foreach (var target in targets)
        {
            try { target(change); }
            catch (Exception error) { Options.OnReport?.Invoke(change.SessionId, error); }
        }
    }

    internal void RequireModel(ModelRef model)
    {
        if (Options.Models.GetModel(model.Provider, model.ModelId) is null)
            throw new HostInvalidException($"{model.Provider}/{model.ModelId} is not one of {Info.Name}'s models.");
    }

    internal double ContextWindow(ModelRef? model) =>
        model is null ? 0 : Options.Models.GetModel(model.Provider, model.ModelId)?.ContextWindow ?? 0;

    /// <summary>An existing directory: absolute, <c>~</c>, or relative to the first start directory; blank is the first start
    /// directory.</summary>
    internal string ResolveDirectory(string? path)
    {
        var trimmed = path?.Trim() ?? "";
        if (trimmed.Length == 0) return Info.StartDirectories[0];
        var expanded = ExpandHome(trimmed);
        var full = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Join(Info.StartDirectories[0], expanded));
        return Directory.Exists(full) ? full : throw new HostInvalidException($"The working directory {full} does not exist on {Info.Name}.");
    }

    /// <summary>The registry of a session when the host passes none: the durable coding tools, <c>user-interaction</c>, <c>plan-mode</c>
    /// (installed, selected only while an agent plans) and the device's extensions (<see cref="HostedSessionContext.Apps"/>).</summary>
    public static IRegistry DefaultRegistry(HostedSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var registry = Registry.Create();
        registry.Install(CodingTools.CreateCodingTools());
        registry.Install(UserInteraction.CreateExtension());
        registry.Install(PlanMode.CreateExtension());
        foreach (var extension in context.Apps) registry.Install(extension);
        return registry;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Platform() =>
        OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription;

    private static string ExpandHome(string path)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith(@"~\", StringComparison.Ordinal)) return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Join(home, path[2..]);
    }

    private static string DisplayName(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    private sealed class Unsubscriber(DurableDeviceHost host, Action<DeviceChange> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (host.gate) host.listeners.Remove(listener);
        }
    }
}
