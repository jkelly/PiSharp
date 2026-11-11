// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/plugins/install.ts.
using System.Diagnostics;
using System.IO.Compression;
using PiSharp.Tools.Processes;

namespace PiSharp.Chord.Hosting.Plugins;

/// <summary>Explicit Git revision or local native ZIP. Name identifies the destination folder, not a plugin identity.</summary>
public sealed record PluginInstallSource(string Name, string Location, string? Revision = null, bool Archive = false);
public sealed class PluginInstallOptions
{
    public string GitPath { get; init; } = "git";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public long MaximumBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumFiles { get; init; } = 10000;
}

internal static class PluginInstaller
{
    internal static async Task<IReadOnlyList<string>> InstallAsync(PluginInstallSource source, string root, IReadOnlyList<PluginRoot> roots, PluginInstallOptions options, CancellationToken cancellation)
    {
        if (!PluginScanner.IsPluginName(source.Name) || source.Name.Contains('/') || source.Name.StartsWith('-')) throw new ArgumentException("install folder must be an unscoped plugin name");
        if (string.IsNullOrWhiteSpace(source.Location) || source.Location.StartsWith('-')) throw new ArgumentException("invalid plugin source");
        if (!source.Archive && (string.IsNullOrWhiteSpace(source.Revision) || source.Revision.StartsWith('-') || source.Revision.Any(char.IsWhiteSpace) || source.Revision.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '/' and not '-'))) throw new ArgumentException("an explicit safe Git revision is required");
        root = PluginScanner.RealPath(root);
        if (!roots.Any(item => BaseInputs.Paths.Equals(PluginScanner.RealPath(item.Path), root))) throw new ArgumentException("destination is not a configured plugin root");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var target = PluginScanner.ContainedPath(root, source.Name);
        var claim = Path.Combine(root, ".install-" + source.Name + ".lock");
        // CreateNew gives exclusive ownership. Never remove someone else's lock or stage.
        await using var ownership = new FileStream(claim, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("plugin destination already exists");
        var stage = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            if (source.Archive) await ExtractAsync(source.Location, stage, options, cancellation).ConfigureAwait(false);
            else
            {
                await GitAsync(options, root, ["clone", "--no-checkout", "--", source.Location, stage], cancellation).ConfigureAwait(false);
                await GitAsync(options, stage, ["checkout", "--detach", source.Revision!], cancellation).ConfigureAwait(false);
            }
            var discovery = await PluginScanner.DiscoverAsync(new[] { new PluginRoot(stage, true) }, cancellation).ConfigureAwait(false);
            if (discovery.Problems.Count != 0 || discovery.Plugins.Count == 0) throw new PluginError("installed package discovery failed: " + string.Join("; ", discovery.Problems.Select(problem => problem.Error)));
            var existing = await PluginScanner.DiscoverAsync(roots, cancellation).ConfigureAwait(false);
            var names = discovery.Plugins.Select(plugin => plugin.Manifest.Name).ToArray();
            if (names.Intersect(existing.Plugins.Select(plugin => plugin.Manifest.Name), StringComparer.Ordinal).Any()) throw new PluginError("installed plugin name conflicts with an existing root");
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return names;
        }
        finally
        {
            // stage was created by this invocation and cannot point outside its original parent.
            if (Directory.Exists(stage) && BaseInputs.Paths.Equals(Path.GetDirectoryName(stage), root) && new DirectoryInfo(stage).LinkTarget is null) Directory.Delete(stage, true);
        }
    }
    private static async Task ExtractAsync(string source, string stage, PluginInstallOptions options, CancellationToken cancellation)
    {
        using var archive = ZipFile.OpenRead(Path.GetFullPath(source));
        long total = 0; int count = 0;
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++count > options.MaximumFiles || entry.Length > options.MaximumBytes - total) throw new InvalidDataException("native archive exceeds install limits");
            total += entry.Length;
            var name = entry.FullName;
            if (name.Contains('\\') || name.Contains(':') || name.Contains('\0') || name.StartsWith('/') || name.Split('/').Any(part => part is "." or "..") || ((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000) throw new InvalidDataException("invalid native archive entry");
            var directoryEntry = name.EndsWith('/');
            var target = PluginScanner.ContainedPath(stage, directoryEntry ? name.TrimEnd('/') : name);
            if (directoryEntry) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var input = entry.Open(); await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
        }
    }
    private static async Task GitAsync(PluginInstallOptions options, string directory, string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(options.GitPath) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(options.Timeout);
        await using var process = await OwnedChildProcess.StartAsync(start, timeout.Token).ConfigureAwait(false);
        var stdout = PluginBuilder.ReadBoundedAsync(process.StandardOutput, 128 * 1024); var stderr = PluginBuilder.ReadBoundedAsync(process.StandardError, 128 * 1024);
        process.OwnReader(stdout); process.OwnReader(stderr);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Git installation failed: " + stderr.Result);
        }
        catch { await process.StopAsync().ConfigureAwait(false); throw; }
    }
}
