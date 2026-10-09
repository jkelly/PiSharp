using System.Text.Json;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Lifecycle;

namespace PiSharp.Sessions.Storage;

public enum SessionStorageMode { InMemory, LazyLocal }
public sealed record SessionStorageBackendOptions(int MaximumFiles = 2048,
    int MaximumFileBytes = 16_777_216, int MaximumResidentBytes = 67_108_864);

/// <summary>An explicitly owned namespace for volatile or lazy local sessions. InMemory performs no
/// filesystem operations. LazyLocal publishes header/setup/state only in memory until a user or
/// assistant message is checkpointed, then uses the existing exclusive local writer and real disk flush.
/// Closing an unmaterialized session retains its checkpoint in this backend, never creates a file.</summary>
public sealed class SessionStorageBackend : ISessionLogStorageFactory, ISessionCatalogFileSystem,
    ISessionBranchFileSystem, ISessionCopyFileSystem
{
    private sealed record Stored(byte[] Bytes, long ModifiedUtcTicks);
    private readonly object gate = new();
    private readonly Dictionary<string, Stored> files;
    private readonly HashSet<string> writers;
    private readonly Dictionary<string, long> writerBuffers;
    private readonly SessionStorageBackendOptions options;
    private readonly StringComparer paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public string Directory { get; }
    public SessionStorageMode Mode { get; }
    public int ActiveWriterCount { get { lock (gate) return writers.Count; } }
    public SessionStorageBackend(string directory, SessionStorageMode mode,
        SessionStorageBackendOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.Length > 4096 ||
            !Enum.IsDefined(mode)) throw new ArgumentException("An absolute explicit session namespace is required.");
        this.options = options ?? new();
        if (this.options.MaximumFiles is < 1 or > 100_000 || this.options.MaximumFileBytes is < 1 or > 67_108_864 ||
            this.options.MaximumResidentBytes < this.options.MaximumFileBytes || this.options.MaximumResidentBytes > 268_435_456)
            throw new ArgumentOutOfRangeException(nameof(options));
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); Mode = mode;
        files = new(paths); writers = new(paths); writerBuffers = new(paths);
        if (mode == SessionStorageMode.LazyLocal && (!System.IO.Directory.Exists(Directory) ||
            (File.GetAttributes(Directory) & FileAttributes.ReparsePoint) != 0))
            throw new ArgumentException("Lazy local storage requires an existing explicit directory without a directory link.");
    }
    private string Admit(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 4096)
            throw new ArgumentException("Session backend path is invalid.");
        var canonical = Path.GetFullPath(path);
        // session-manager.ts SessionManager.open: a session file may live in any directory (its directory becomes the session
        // directory). A file outside this namespace is a local lazy file in either mode (memory storage, --no-session, keeps only
        // its own namespace in memory).
        if (string.IsNullOrEmpty(Path.GetFileName(canonical)))
            throw new ArgumentException("Session path is outside this explicit backend namespace.");
        return canonical;
    }
    /// <summary>Whether an admitted path is a local file (lazy local storage, or any path outside this namespace).</summary>
    private bool Local(string path) => Mode == SessionStorageMode.LazyLocal || !paths.Equals(Path.GetDirectoryName(path), Directory);
    public bool FileExists(string path)
    {
        path = Admit(path); lock (gate) return files.ContainsKey(path) || Local(path) && File.Exists(path);
    }
    public bool DirectoryExists(string path) => paths.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Directory) ||
        Mode == SessionStorageMode.LazyLocal && paths.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Directory) && System.IO.Directory.Exists(path);
    public SessionLogStorageDurability GetDurability(string path)
    {
        path = Admit(path);
        lock (gate) return files.ContainsKey(path) ? PendingDurability(path) : Local(path) && File.Exists(path)
            ? SessionLogStorageDurability.LocalFileFlush : PendingDurability(path);
    }
    private SessionLogStorageDurability PendingDurability(string path) => Local(path)
        ? SessionLogStorageDurability.DeferredLocalFile : SessionLogStorageDurability.VolatileMemory;
    // Count the entire direct-file namespace, including materialized and pre-existing files.
    // Stop at the admission bound rather than materializing an unbounded directory listing.
    private void AdmitNewFile()
    {
        var count = files.Count;
        if (count >= options.MaximumFiles) throw new IOException("Session backend file bound exceeded.");
        if (Mode == SessionStorageMode.InMemory) return;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory))
            if (!files.ContainsKey(path) && ++count >= options.MaximumFiles)
                throw new IOException("Session backend file bound exceeded.");
    }
    public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); path = Admit(path);
        byte[]? bytes;
        lock (gate)
        {
            if (writers.Contains(path)) throw new IOException("Session already has an owned writer.");
            var present = files.TryGetValue(path, out var stored);
            if (createNew && (present || Local(path) && (File.Exists(path) || System.IO.Directory.Exists(path))))
                throw new IOException("Session destination already exists.");
            if (!present && !createNew && !Local(path)) throw new FileNotFoundException();
            if (createNew)
            {
                AdmitNewFile();
                files.Add(path, new([], DateTime.UtcNow.Ticks)); stored = files[path]; present = true;
            }
            if (present && files.Values.Sum(value => (long)value.Bytes.Length) + writerBuffers.Values.Sum() + stored!.Bytes.Length > options.MaximumResidentBytes)
                throw new IOException("Session backend resident byte bound exceeded.");
            bytes = present ? stored!.Bytes.ToArray() : null; writers.Add(path); writerBuffers.Add(path, bytes?.Length ?? 0);
        }
        ISessionLogStorage? disk = null;
        try
        {
            if (bytes is null) disk = await SessionLogStore.DefaultStorageFactory.OpenAsync(path, false, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new Writer(this, path, bytes ?? [], disk);
        }
        catch
        {
            try { if (disk is not null) await disk.DisposeAsync().ConfigureAwait(false); }
            finally { lock (gate) { writers.Remove(path); writerBuffers.Remove(path); } }
            throw;
        }
    }
    private void CheckLength(long length)
    { if (length < 0 || length > options.MaximumFileBytes) throw new IOException("Session backend byte bound exceeded."); }
    private void Checkpoint(string path, byte[] bytes)
    {
        CheckLength(bytes.Length);
        lock (gate)
        {
            var resident = files.Values.Sum(value => (long)value.Bytes.Length) - files[path].Bytes.Length + bytes.Length + writerBuffers.Values.Sum();
            if (resident > options.MaximumResidentBytes) throw new IOException("Session backend resident byte bound exceeded.");
            files[path] = new(bytes, DateTime.UtcNow.Ticks);
        }
    }
    public bool IsDirectoryLink(string directory)
    {
        if (!DirectoryExists(directory)) throw new ArgumentException("Unconfigured session directory.");
        return Mode == SessionStorageMode.LazyLocal && (File.GetAttributes(Directory) & FileAttributes.ReparsePoint) != 0;
    }
    public IEnumerable<string> EnumerateFileNames(string directory)
    {
        if (!DirectoryExists(directory)) throw new ArgumentException("Unconfigured session directory.");
        string[] pending; lock (gate) pending = files.Keys.Select(path => Path.GetFileName(path)).ToArray();
        // The catalog enforces its own enumeration bounds; no eager unbounded directory materialization.
        return Mode == SessionStorageMode.InMemory ? pending : EnumerateLocal(pending);
    }
    private IEnumerable<string> EnumerateLocal(string[] pending)
    {
        var seen = new HashSet<string>(pending, paths);
        foreach (var name in pending) yield return name;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory))
        { var name = Path.GetFileName(path); if (seen.Add(name)) yield return name; }
    }
    public SessionCatalogFileMetadata GetMetadata(string path)
    {
        path = Admit(path);
        lock (gate) if (files.TryGetValue(path, out var stored)) return new(stored.Bytes.Length, stored.ModifiedUtcTicks, false);
        if (!Local(path)) throw new FileNotFoundException();
        var info = new FileInfo(path); return new(info.Length, info.LastWriteTimeUtc.Ticks, (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); path = Admit(path);
        lock (gate) if (files.TryGetValue(path, out var stored))
            return ValueTask.FromResult<Stream>(new MemoryStream(stored.Bytes.ToArray(), writable: false));
        if (!Local(path)) throw new FileNotFoundException();
        return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
    public async ValueTask<ISessionBranchOutput> CreateNewTemporaryAsync(string path) =>
        new BranchOutput((Writer)await OpenAsync(path, true, CancellationToken.None).ConfigureAwait(false));
    async ValueTask<Stream> ISessionCopyFileSystem.CreateNewTemporaryAsync(string path) =>
        new CopyOutput((Writer)await OpenAsync(path, true, CancellationToken.None).ConfigureAwait(false));
    public ValueTask PublishNewAsync(string temporaryPath, string destinationPath)
    {
        temporaryPath = Admit(temporaryPath); destinationPath = Admit(destinationPath);
        lock (gate)
        {
            if (writers.Contains(temporaryPath) || writers.Contains(destinationPath) || files.ContainsKey(destinationPath) ||
                Local(destinationPath) && (File.Exists(destinationPath) || System.IO.Directory.Exists(destinationPath)))
                throw new IOException("Session publication requires closed source and a fresh destination.");
            if (files.Remove(temporaryPath, out var stored)) files.Add(destinationPath, stored);
            else if (Local(temporaryPath)) File.Move(temporaryPath, destinationPath, overwrite: false);
            else throw new FileNotFoundException();
        }
        return ValueTask.CompletedTask;
    }
    public ValueTask DeleteOwnedAsync(string path)
    {
        path = Admit(path);
        lock (gate)
        {
            if (writers.Contains(path)) throw new IOException("Close session writer before owned deletion.");
            if (!files.Remove(path) && Local(path)) File.Delete(path);
        }
        return ValueTask.CompletedTask;
    }
    public ValueTask DeleteTemporaryAsync(string path) => DeleteOwnedAsync(path);
    private sealed class Writer : ISessionLogStorage
    {
        private readonly SessionStorageBackend owner;
        private readonly string path;
        private readonly MemoryStream buffer = new();
        private ISessionLogStorage? disk;
        private Task? close;
        public Writer(SessionStorageBackend owner, string path, byte[] bytes, ISessionLogStorage? disk)
        { this.owner = owner; this.path = path; this.disk = disk; buffer.Write(bytes); buffer.Position = 0; }
        public Stream ReadStream => disk?.ReadStream ?? buffer;
        public SessionLogStorageDurability Durability => disk?.Durability ?? owner.PendingDurability(path);
        public long Length => disk?.Length ?? buffer.Length;
        public void PositionForAppend(long expectedLength)
        {
            if (close is not null) throw new ObjectDisposedException(nameof(Writer));
            if (disk is not null) disk.PositionForAppend(expectedLength);
            else { if (buffer.Length != expectedLength) throw new IOException(); buffer.Position = expectedLength; }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            if (close is not null) throw new ObjectDisposedException(nameof(Writer));
            owner.CheckLength(Length + bytes.Length);
            if (disk is null) lock (owner.gate)
            {
                var nextLength = buffer.Length + bytes.Length;
                var resident = owner.files.Values.Sum(value => (long)value.Bytes.Length) + owner.writerBuffers.Values.Sum() - owner.writerBuffers[path] + nextLength;
                if (resident > owner.options.MaximumResidentBytes) throw new IOException("Session backend resident byte bound exceeded.");
                owner.writerBuffers[path] = nextLength;
            }
            return disk is null ? buffer.WriteAsync(bytes, CancellationToken.None) : disk.WriteAsync(bytes);
        }
        public async ValueTask FlushAsync()
        {
            if (disk is null && owner.Local(path) && HasConversation(buffer))
            {
                // CreateNew is the cross-process arbitration barrier. A racing external destination is
                // preserved. Once acquired, failure retains the uncertain file and closes the real lease.
                disk = await SessionLogStore.DefaultStorageFactory.OpenAsync(path, true, CancellationToken.None).ConfigureAwait(false);
                lock (owner.gate) owner.files.Remove(path);
                await disk.WriteAsync(buffer.ToArray()).ConfigureAwait(false);
            }
            if (disk is not null) await disk.FlushAsync().ConfigureAwait(false);
        }
        public void FlushToDisk() { if (disk is not null) disk.FlushToDisk(); }
        public async ValueTask BeforeCheckpointAsync()
        {
            if (disk is not null)
            {
                await disk.BeforeCheckpointAsync().ConfigureAwait(false);
                buffer.Dispose();
                lock (owner.gate) { owner.files.Remove(path); owner.writerBuffers[path] = 0; }
            }
            else owner.Checkpoint(path, buffer.ToArray());
        }
        private static bool HasConversation(MemoryStream stream)
        {
            var bytes = stream.GetBuffer().AsSpan(0, checked((int)stream.Length));
            while (!bytes.IsEmpty)
            {
                var end = bytes.IndexOf((byte)'\n'); var line = end < 0 ? bytes : bytes[..end];
                if (!IsBlank(line))
                {
                    using var record = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 256 }); var root = record.RootElement;
                    if (root.TryGetProperty("type", out var type) && type.GetString() == "message" &&
                        root.TryGetProperty("message", out var message) && message.TryGetProperty("role", out var role) &&
                        role.GetString() is "user" or "assistant") return true;
                }
                if (end < 0) break; bytes = bytes[(end + 1)..];
            }
            return false;
        }
        private static bool IsBlank(ReadOnlySpan<byte> line)
        {
            foreach (var value in line) if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
            return true;
        }
        public ValueTask DisposeAsync()
        { lock (owner.gate) { close ??= CloseAsync(); return new(close); } }
        private async Task CloseAsync()
        {
            await Task.Yield();
            try { if (disk is not null) await disk.DisposeAsync().ConfigureAwait(false); }
            finally { buffer.Dispose(); lock (owner.gate) { owner.writers.Remove(path); owner.writerBuffers.Remove(path); } }
        }
    }
    private sealed class BranchOutput(Writer writer) : ISessionBranchOutput
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); writer.PositionForAppend(writer.Length); return writer.WriteAsync(bytes); }
        public async ValueTask FlushAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); await writer.FlushAsync().ConfigureAwait(false); }
        public void FlushToDisk() { writer.FlushToDisk(); writer.BeforeCheckpointAsync().AsTask().GetAwaiter().GetResult(); }
        public ValueTask DisposeAsync() => writer.DisposeAsync();
    }
    private sealed class CopyOutput(Writer writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => writer.Length;
        public override long Position { get => writer.Length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); writer.PositionForAppend(writer.Length); return writer.WriteAsync(bytes); }
        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        public override async Task FlushAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); await writer.FlushAsync().ConfigureAwait(false); writer.FlushToDisk(); await writer.BeforeCheckpointAsync().ConfigureAwait(false); }
        protected override void Dispose(bool disposing) { if (disposing) writer.DisposeAsync().AsTask().GetAwaiter().GetResult(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => writer.DisposeAsync();
    }
}
