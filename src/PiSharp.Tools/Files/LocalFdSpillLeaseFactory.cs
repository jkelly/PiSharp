namespace PiSharp.Tools.Files;

/// <summary>Exclusive per-invocation directory beneath a host-owned canonical workspace spill root.</summary>
public sealed class LocalFdSpillLeaseFactory : IFdSpillLeaseFactory
{
    private readonly string _root;
    private readonly IFileOperations _files;
    public string Root => _root;
    public LocalFdSpillLeaseFactory(string canonicalRoot, IFileOperations? files = null)
    {
        if (string.IsNullOrEmpty(canonicalRoot) || !Path.IsPathFullyQualified(canonicalRoot) || Path.GetFullPath(canonicalRoot) != canonicalRoot)
            throw new ArgumentException("An explicit canonical spill root is required.");
        _root = Path.TrimEndingDirectorySeparator(canonicalRoot); _files = files ?? new LocalFileOperations();
    }
    public async ValueTask<IFdSpillLease> CreateAsync(CancellationToken token)
    {
        if (_root != await _files.CanonicalizeAsync(_root, token).ConfigureAwait(false) || !Directory.Exists(_root))
            throw new IOException("Fd spill root identity changed.");
        var directory = Path.Combine(_root, "pisharp-fd-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Fd spill lease name is already occupied.");
        var lease = new Lease(_root, directory, _files);
        try
        {
            await _files.CreateDirectoryAsync(directory, token).ConfigureAwait(false);
            if (directory != await _files.CanonicalizeAsync(directory, token).ConfigureAwait(false)) throw new IOException("Fd spill lease identity changed.");
            token.ThrowIfCancellationRequested(); return lease;
        }
        catch (Exception original)
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Fd spill acquisition and cleanup failed.", original, cleanup); }
            throw;
        }
    }
    private sealed class Lease(string root, string directory, IFileOperations files) : IFdSpillLease
    {
        private readonly object _gate = new();
        private Task? _disposal;
        public string SpillPath => Path.Combine(directory, "output.log");
        public ValueTask DisposeAsync()
        {
            lock (_gate) return new(_disposal ??= CloseAsync());
        }
        private async Task CloseAsync()
        {
            if (Path.GetDirectoryName(directory) != root || !Path.GetFileName(directory).StartsWith("pisharp-fd-", StringComparison.Ordinal))
                throw new IOException("Invalid owned fd spill directory.");
            if (Directory.Exists(directory))
            {
                if (root != await files.CanonicalizeAsync(root, CancellationToken.None).ConfigureAwait(false) ||
                    directory != await files.CanonicalizeAsync(directory, CancellationToken.None).ConfigureAwait(false))
                    throw new IOException("Fd spill cleanup identity changed.");
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fd spill directory was replaced by a link.");
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
