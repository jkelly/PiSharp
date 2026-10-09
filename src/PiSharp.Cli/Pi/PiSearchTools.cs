// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/grep.ts and core/tools/find.ts (ensureTool
// at execution: "ripgrep (rg) is not available and could not be downloaded", "fd is not available and could not be downloaded").
using System.Collections.Immutable;
using System.Security.Cryptography;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Pi;

/// <summary>The grep and find backends of the <c>pi</c> tool policy. Like upstream, the binary is resolved (and downloaded when missing)
/// on the first search, not at startup; the admitted executors of PiSharp.Tools then run it by absolute path.</summary>
internal sealed class PiSearchTools
{
    private readonly PiToolsManager tools;
    private readonly string workspace;
    private readonly Func<ISeparatedProcessRunner> runner;
    private readonly ImmutableDictionary<string, string> environment;
    private readonly Action<PiToolStatus>? status;
    private readonly TransientSpillRoot spills;

    internal PiSearchTools(PiToolsManager tools, string workspace, Func<ISeparatedProcessRunner> runner, IReadOnlyDictionary<string, string> environment,
        Action<PiToolStatus>? status = null)
    {
        this.tools = tools; this.workspace = workspace; this.runner = runner; this.status = status;
        this.environment = environment.ToImmutableDictionary(StringComparer.Ordinal);
        spills = new TransientSpillRoot(Path.Join(workspace, ".pisharp-search"));
    }

    internal IGrepExecutor Grep => new LazyGrep(this);
    internal IFindExecutor Find => new LazyFind(this);

    private async Task<(string Path, int Length, string Sha256)?> ResolveAsync(string tool, CancellationToken token)
    {
        var path = await tools.EnsureToolAsync(tool, status, token).ConfigureAwait(false);
        if (path is null) return null;
        var full = Path.GetFullPath(path);
        await using var image = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(image, token).ConfigureAwait(false));
        return (full, (int)Math.Min(image.Length, int.MaxValue), hash);
    }

    private static string RuntimeId => System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

    private sealed class LazyGrep(PiSearchTools owner) : IGrepExecutor
    {
        private RipgrepExecutor? executor;
        public async ValueTask<ImmutableArray<GrepMatch>> GrepAsync(GrepExecutionRequest request, CancellationToken cancellationToken)
        {
            if (executor is null)
            {
                var resolved = await owner.ResolveAsync("rg", cancellationToken).ConfigureAwait(false) ??
                    throw new IOException("ripgrep (rg) is not available and could not be downloaded");
                executor = new(new(resolved.Path, resolved.Length, resolved.Sha256, "pi-tools-manager", RuntimeId, "pi-policy:" + resolved.Path),
                    owner.workspace, owner.environment, owner.runner(), (_, _) => ValueTask.FromResult(true), owner.spills);
            }
            using (owner.spills.Enter()) return await executor.GrepAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class LazyFind(PiSearchTools owner) : IFindExecutor
    {
        private FdFindExecutor? executor;
        public FindExecutionProfile Profile => FindExecutionProfile.Fd;
        public bool SupportsPattern(string pattern) => pattern is { Length: > 0 and <= 1024 } && !pattern.Any(char.IsControl);
        public async ValueTask<ImmutableArray<string>> FindAsync(FindExecutionRequest request, CancellationToken cancellationToken)
        {
            if (executor is null)
            {
                var resolved = await owner.ResolveAsync("fd", cancellationToken).ConfigureAwait(false) ??
                    throw new IOException("fd is not available and could not be downloaded");
                executor = new(new(resolved.Path, resolved.Length, resolved.Sha256, "pi-tools-manager", RuntimeId, "pi-policy:" + resolved.Path),
                    owner.workspace, owner.environment, owner.runner(), (_, _) => ValueTask.FromResult(true), Repository, owner.spills);
            }
            using (owner.spills.Enter()) return await executor.FindAsync(request, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Whether the search path is inside a git repository (find.ts passes --no-require-git outside one).</summary>
        private static ValueTask<FdRepositoryPresence> Repository(string searchPath, CancellationToken token)
        {
            for (var directory = searchPath; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
                if (Path.Exists(Path.Join(directory, ".git"))) return ValueTask.FromResult(FdRepositoryPresence.InsideGitRepository);
            return ValueTask.FromResult(FdRepositoryPresence.OutsideGitRepository);
        }
    }

    /// <summary>Per-search spill directories beneath a hidden workspace folder that exists only while a search runs (the admitted
    /// executors keep their output inside the workspace).</summary>
    private sealed class TransientSpillRoot(string root) : IFdSpillLeaseFactory
    {
        private readonly object gate = new();
        private int active;
        public string Root { get; } = Path.GetFullPath(root);
        public IDisposable Enter()
        {
            lock (gate) { Directory.CreateDirectory(Root); active++; }
            return new Exit(this);
        }
        public ValueTask<IFdSpillLease> CreateAsync(CancellationToken cancellationToken) =>
            new LocalFdSpillLeaseFactory(Root).CreateAsync(cancellationToken);
        private void Release()
        {
            lock (gate)
            {
                if (--active > 0) return;
                try { if (Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any()) Directory.Delete(Root); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        private sealed class Exit(TransientSpillRoot owner) : IDisposable
        {
            private int done;
            public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) owner.Release(); }
        }
    }
}
