// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/workers/base.ts.
// Native deployment snapshots replace JavaScript build artifacts.
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PiSharp.Chord.Hosting.Plugins;

namespace PiSharp.Chord.Hosting.Workers;

public sealed class WorkerBaseOptions
{
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>The complete deployment tree, including every privately loaded managed/native dependency.</summary>
    public string? ApplicationDirectory { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>Parent for a unique owned snapshot directory; existing contents are never removed.</summary>
    public string? BaseDirectory { get; init; }
    public int MaximumFiles { get; init; } = 10000;
    public long MaximumBytes { get; init; } = 512L * 1024 * 1024;
}

public sealed record WorkerRuntimeIdentity(string Executable, string Sha256, string? Version);
public sealed record WorkerLaunch(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);
public sealed record WorkersBuilt(string Kind, BaseInputs? Inputs = null, Exception? Error = null, WorkerRuntimeIdentity? Runtime = null);

/// <summary>An in-memory deployment snapshot. Capturing it writes nothing; only a first launch creates files.</summary>
/// <remarks>Framework-dependent workers use the explicitly pinned dotnet installation (recorded by hash/version),
/// which must remain installed. Application dependencies must be deployed under ApplicationDirectory. Runtime
/// probing outside that tree and dynamic code loads from arbitrary external paths are not a supported snapshot.
/// A self-contained deployment includes its runtime in the captured tree.</remarks>
public sealed class WorkerBase : IDisposable
{
    private sealed record FileImage(string RelativePath, byte[] Bytes, UnixFileMode? Mode);
    private readonly object sync = new();
    private readonly FileImage[] files;
    private readonly string sourceRoot, executable;
    private readonly string[] arguments;
    private readonly int managedArgument;
    private readonly string? workingDirectory, baseDirectory;
    private readonly string ownership = Guid.NewGuid().ToString("N");
    private readonly List<string> ownedDirectories = [];
    private const string OwnerFile = ".chord-worker-owner";
    private string? directory;
    private bool disposed;

    private WorkerBase(WorkerBaseOptions options, string sourceRoot, string executable, string[] arguments,
        int managedArgument, FileImage[] files, BaseInputs inputs, WorkerRuntimeIdentity? runtime)
    {
        this.sourceRoot = sourceRoot; this.executable = executable; this.arguments = arguments;
        this.managedArgument = managedArgument; this.files = files; Inputs = inputs; Runtime = runtime;
        workingDirectory = options.WorkingDirectory is null ? null : Path.GetFullPath(options.WorkingDirectory);
        baseDirectory = options.BaseDirectory is null ? null : Path.GetFullPath(options.BaseDirectory);
    }
    public BaseInputs Inputs { get; }
    public WorkerRuntimeIdentity? Runtime { get; }
    public string? DirectoryPath { get { lock (sync) return directory; } }

    public static Task<WorkerBase> BuildAsync(WorkerBaseOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Capture options now; callers cannot mutate the arguments while the capture is running.
        var captured = new WorkerBaseOptions { Executable = options.Executable, Arguments = options.Arguments.ToArray(),
            ApplicationDirectory = options.ApplicationDirectory, WorkingDirectory = options.WorkingDirectory,
            BaseDirectory = options.BaseDirectory, MaximumFiles = options.MaximumFiles, MaximumBytes = options.MaximumBytes };
        return Task.Run(() => Capture(captured, cancellationToken), cancellationToken);
    }

    private static WorkerBase Capture(WorkerBaseOptions options, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(options.Executable)) throw new ArgumentException("Worker executable must be an absolute path");
        if (options.MaximumFiles <= 0 || options.MaximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        var executable = Path.GetFullPath(options.Executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Worker executable is missing", executable);
        // Resolve the launcher itself (e.g. /usr/bin/dotnet); the captured application tree cannot contain links.
        executable = new FileInfo(executable).ResolveLinkTarget(true)?.FullName ?? executable;
        var arguments = options.Arguments.ToArray();
        var dotnet = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
        var managedArgument = dotnet && arguments.FirstOrDefault() == "exec" ? 1 : dotnet ? 0 : -1;
        if (dotnet && (managedArgument >= arguments.Length || !arguments[managedArgument].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use dotnet app.dll or dotnet exec app.dll; external runtime/probing flags are not supported");
        var entry = managedArgument < 0 ? executable : Path.GetFullPath(arguments[managedArgument], options.WorkingDirectory ?? Environment.CurrentDirectory);
        if (!File.Exists(entry)) throw new FileNotFoundException("Worker entry is missing", entry);
        if (managedArgument >= 0) arguments[managedArgument] = entry;
        var root = Path.GetFullPath(options.ApplicationDirectory ?? Path.GetDirectoryName(entry)!);
        if (!Within(entry, root)) throw new ArgumentException("Worker entry must be inside ApplicationDirectory");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Worker application directory cannot be a link");
        var images = new List<FileImage>(); var hashes = new Dictionary<string, string?>(PathComparer);
        var pending = new Stack<string>(); pending.Push(root); long length = 0;
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Worker snapshot cannot follow link: {path}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(path) is ".git" or ".svn" or "obj" or "node_modules") continue;
                    // A previous snapshot parent is not part of the application deployment.
                    if (options.BaseDirectory is not null && PathComparer.Equals(Path.GetFullPath(path), Path.GetFullPath(options.BaseDirectory))) continue;
                    pending.Push(path); continue;
                }
                if (images.Count >= options.MaximumFiles) throw new IOException("Worker snapshot exceeds file limit");
                if (PathComparer.Equals(path, Path.Combine(root, OwnerFile))) throw new IOException("Worker deployment uses the reserved ownership file name");
                var bytes = File.ReadAllBytes(path);
                length += bytes.LongLength;
                if (length > options.MaximumBytes) throw new IOException("Worker snapshot exceeds byte limit");
                if (path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.dev.json", StringComparison.OrdinalIgnoreCase))
                    RejectExternalProbing(path, bytes);
                hashes.Add(Path.GetFullPath(path), BaseInputs.ContentHash(bytes));
                images.Add(new(Path.GetRelativePath(root, path), bytes, OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path)));
            }
        }
        WorkerRuntimeIdentity? runtime = null;
        if (dotnet)
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
            hashes[executable] = hash;
            runtime = new(executable, hash, FileVersionInfo.GetVersionInfo(executable).FileVersion);
        }
        return new(options, root, executable, arguments, managedArgument, images.ToArray(), BaseInputs.FromHashes(hashes, [root], options.MaximumFiles), runtime);
    }

    private static void RejectExternalProbing(string path, byte[] bytes)
    {
        var options = JsonNode.Parse(bytes)?["runtimeOptions"];
        if (options?["additionalProbingPaths"] is JsonArray { Count: > 0 })
            throw new IOException($"Worker snapshot requires all private dependencies deployed locally; external probing in {path}");
    }

    /// <summary>Materializes the captured bytes once, then returns a launch that reads only that deployment.</summary>
    public WorkerLaunch PrepareLaunch()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (directory is null || !IsOwned(directory) || files.Any(file => !File.Exists(Path.Combine(directory, file.RelativePath))))
            {
                var parent = baseDirectory ?? Path.GetTempPath();
                Directory.CreateDirectory(parent);
                var target = Path.Combine(parent, "chord-worker-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(target);
                // A missing temporary snapshot is recreated from captured bytes at a fresh name. Never rewrite
                // files a currently running worker may still load, or a directory another component now owns.
                directory = target;
                try
                {
                    using (var owner = new StreamWriter(new FileStream(Path.Combine(target, OwnerFile), FileMode.CreateNew, FileAccess.Write, FileShare.None))) owner.Write(ownership);
                    ownedDirectories.Add(target);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    foreach (var image in files)
                    {
                        var destination = Path.Combine(target, image.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.WriteAllBytes(destination, image.Bytes);
                        if (!OperatingSystem.IsWindows() && image.Mode is { } mode) File.SetUnixFileMode(destination, mode);
                    }
                }
                catch { if (IsOwned(target)) Directory.Delete(target, true); directory = null; throw; }
            }
            var launchArguments = arguments.ToArray();
            if (managedArgument >= 0) launchArguments[managedArgument] = Path.Combine(directory, Path.GetRelativePath(sourceRoot, arguments[managedArgument]));
            var launchExecutable = managedArgument >= 0 ? executable : Path.Combine(directory, Path.GetRelativePath(sourceRoot, executable));
            var cwd = workingDirectory is null ? directory : Within(workingDirectory, sourceRoot) ? Path.Combine(directory, Path.GetRelativePath(sourceRoot, workingDirectory)) : workingDirectory;
            return new(launchExecutable, new ReadOnlyCollection<string>(launchArguments), cwd);
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var owned in ownedDirectories) if (IsOwned(owned)) Directory.Delete(owned, true);
            ownedDirectories.Clear(); directory = null;
        }
    }
    private bool IsOwned(string path)
    {
        try { return File.ReadAllText(Path.Combine(path, OwnerFile)) == ownership; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool Within(string path, string root) => PathComparer.Equals(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
