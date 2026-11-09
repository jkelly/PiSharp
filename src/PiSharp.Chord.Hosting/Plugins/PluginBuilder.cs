using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PiSharp.Tools.Processes;

namespace PiSharp.Chord.Hosting.Plugins;

public sealed class PluginBuildOptions
{
    public required string DotnetPath { get; init; }
    public required string ArtifactDirectory { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public string? PackageDirectory { get; init; }
    public bool Restore { get; init; }
}

/// <summary>Plain data suitable for sending to a native worker on the same machine.</summary>
public sealed record BuiltPlugin(NativePlugin Plugin, string? AssemblyPath, string? Digest, string? Error);
public sealed record PluginBuildSet(IReadOnlyList<BuiltPlugin> Plugins, IReadOnlyList<DiscoveryProblem> Problems);

public sealed class PluginBuilder(PluginBuildOptions options)
{
    public async Task<BuiltPlugin> BuildAsync(NativePlugin plugin, CancellationToken cancellationToken = default)
    {
        string? output = null;
        try
        {
            var source = PluginScanner.ContainedPath(plugin.Directory, plugin.Manifest.Assembly);
            if (plugin.Manifest.Project is { } project)
            {
                output = Path.Combine(Path.GetFullPath(options.ArtifactDirectory), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(output);
                var start = new ProcessStartInfo(options.DotnetPath) { WorkingDirectory = plugin.Directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "build", PluginScanner.ContainedPath(plugin.Directory, project), "--disable-build-servers", "--nologo", "-v:q", "-m:1", "-o", output, "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "-p:ArtifactsPath=" + Path.Combine(output, ".build") }) start.ArgumentList.Add(argument);
                start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                start.ArgumentList.Add("-p:Deterministic=true");
                start.ArgumentList.Add("-p:PathMap=" + output + "=/_/build%2C" + plugin.Directory + "=/_/plugin");
                if (!options.Restore) start.ArgumentList.Add("--no-restore");
                if (options.PackageDirectory is { } packages) start.Environment["NUGET_PACKAGES"] = Path.GetFullPath(packages);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);
                await using var process = await OwnedChildProcess.StartAsync(start, timeout.Token).ConfigureAwait(false);
                var stdout = ReadBoundedAsync(process.StandardOutput, 128 * 1024);
                var stderr = ReadBoundedAsync(process.StandardError, 128 * 1024);
                process.OwnReader(stdout); process.OwnReader(stderr);
                string log;
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
                    log = stdout.Result + stderr.Result;
                }
                catch { await process.StopAsync().ConfigureAwait(false); throw; }
                if (process.ExitCode != 0) throw new InvalidDataException($"build exited {process.ExitCode}: {log}");
                source = Path.Combine(output, Path.GetFileName(plugin.Manifest.Assembly));
            }
            else
            {
                output = Path.Combine(Path.GetFullPath(options.ArtifactDirectory), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(output);
                var sourceDirectory = Path.GetDirectoryName(source)!;
                var inputs = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                    .Where(file => !Path.GetFullPath(file).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .Where(file => Path.GetExtension(file) is ".dll" or ".pdb" or ".json" or ".so" or ".dylib" || Path.GetRelativePath(sourceDirectory, file).StartsWith("runtimes" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var file in inputs)
                {
                    var target = Path.Combine(output, Path.GetRelativePath(sourceDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
                }
                source = Path.Combine(output, Path.GetFileName(source));
            }
            if (!File.Exists(source)) throw new FileNotFoundException("plugin assembly was not produced", source);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(plugin.Manifest.Name + "\n" + plugin.Manifest.Type));
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(output, file).StartsWith(".build" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Path.GetExtension(file) != ".pdb").Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(output, file).Replace(Path.DirectorySeparatorChar, '/')));
                hash.AppendData(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
            }
            return new(plugin, source, Convert.ToHexString(hash.GetHashAndReset()), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AggregateException error) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Plugin build cancelled during owned cleanup", error, cancellationToken); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or AggregateException or ArgumentException or System.ComponentModel.Win32Exception)
        { return new(plugin, null, null, error is OperationCanceledException ? "plugin build timed out" : error.Message); }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        { if (result.Length < limit) result.Append(buffer, 0, Math.Min(count, limit - result.Length)); }
        return result.ToString();
    }
}
