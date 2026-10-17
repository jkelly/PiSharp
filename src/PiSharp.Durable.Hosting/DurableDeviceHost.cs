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
public sealed class DurableDeviceHost : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly List<HostedSession> sessions = [];
    private readonly List<Action<DeviceChange>> listeners = [];
    private readonly FileLock deviceLock;
    private int disposed;

    private DurableDeviceHost(DurableDeviceOptions options, FileLock deviceLock, SessionCatalog catalog, DeviceInfo info)
    {
        Options = options;
        this.deviceLock = deviceLock;
        Catalog = catalog;
        Info = info;
    }

    /// <summary>What the device is.</summary>
    public DeviceInfo Info { get; }

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
                await session.OpenAsync(null, null).ConfigureAwait(false);
            }
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
        var model = request.Model ?? Options.DefaultModel ?? throw new HostInvalidException($"Choose a model: {Info.Name} has no default model.");
        RequireModel(model);
        var thinking = HostNames.Thinking(request.ThinkingLevel ?? Options.DefaultThinkingLevel);
        var cwd = ResolveDirectory(request.Cwd);
        var name = HostNames.Session(request.Name);
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.SpawnKey is { } key && Catalog.SpawnedSession(key) is { } spawned)
                return Sessions.FirstOrDefault(session => session.Id == spawned) ?? throw new HostNotFoundException($"The session spawned by {key} is gone.");
            var row = new CatalogSession(Guid.NewGuid().ToString("D"), name, cwd, Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                model.Provider, model.ModelId, null);
            Catalog.AddSession(row, request.SpawnKey);
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
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = ResolveSession(address);
            lock (gate) sessions.Remove(session);
            await session.StopAndCloseAsync().ConfigureAwait(false);
            Directory.CreateDirectory(ArchiveDirectory);
            var target = Path.Join(ArchiveDirectory, $"{session.Id}-{Options.TimeProvider.GetUtcNow():yyyyMMddHHmmss}");
            if (Directory.Exists(session.DirectoryPath))
            {
                // Pooled SQLite connections would keep the database open on Windows.
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Move(session.DirectoryPath, target);
            }
            Catalog.RemoveSession(session.Id);
            Notify(new DeviceChange(DeviceChangeKind.Catalog, session.Id));
            return target;
        }
        finally
        {
            mutations.Release();
        }
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
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        HostedSession[] all;
        lock (gate) { all = [.. sessions]; sessions.Clear(); listeners.Clear(); }
        foreach (var session in all) await session.CloseAsync().ConfigureAwait(false);
        Catalog.Dispose();
        deviceLock.Dispose();
        mutations.Dispose();
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

    /// <summary>The registry of a session when the host passes none.</summary>
    internal static IRegistry DefaultRegistry()
    {
        var registry = Registry.Create();
        registry.Install(CodingTools.CreateCodingTools());
        registry.Install(UserInteraction.CreateExtension());
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
